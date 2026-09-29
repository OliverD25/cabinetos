//! `http-request`: the web requests the core makes for a plugin that holds
//! `net` (`docs/plugins.md`, "The network"). The plugin's sandbox has no
//! network of its own; the core reaches only the hosts the manifest names,
//! over `https:` (plain `http:` only to `localhost` and `127.0.0.1`), with
//! the marketplace's stack: `ureq` over rustls, certificates checked against
//! the Windows certificate store. A secret the plugin names goes into a
//! header here, so the plugin never holds it. One log line per request:
//! host, status, size and time, never a header or a body.

use std::io::Read;
use std::sync::OnceLock;
use std::time::{Duration, Instant};

use ureq::Agent;
use ureq::tls::{RootCerts, TlsConfig};
use url::Url;

use crate::manifest::HostRule;

/// The largest answer a plugin gets; a longer body is an error.
pub(crate) const MAX_BODY: u64 = 8 * 1024 * 1024;

/// The longest a request may take, whatever the plugin asks.
pub(crate) const MAX_TIMEOUT: Duration = Duration::from_secs(120);

/// How long connecting may take.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// One request as the plugin asked for it.
#[derive(Debug)]
pub(crate) struct Ask {
    pub(crate) method: String,
    pub(crate) url: String,
    pub(crate) headers: Vec<(String, String)>,
    pub(crate) body: Option<Vec<u8>>,
    pub(crate) secret: Option<String>,
    pub(crate) secret_header: Option<String>,
    pub(crate) timeout_ms: u32,
}

/// The server's answer.
#[derive(Debug, PartialEq, Eq)]
pub(crate) struct Answer {
    pub(crate) status: u16,
    pub(crate) headers: Vec<(String, String)>,
    pub(crate) body: Vec<u8>,
}

/// What one plugin may reach.
#[derive(Clone, Debug, Default)]
pub(crate) struct NetRules {
    pub(crate) hosts: Vec<HostRule>,
    pub(crate) secrets: Vec<String>,
}

/// The HTTP client every plugin shares, made at the first request.
#[derive(Debug, Default)]
pub(crate) struct Net {
    agent: OnceLock<Agent>,
}

impl Net {
    fn agent(&self) -> &Agent {
        self.agent.get_or_init(|| {
            Agent::config_builder()
                .tls_config(
                    TlsConfig::builder()
                        .root_certs(RootCerts::PlatformVerifier)
                        .build(),
                )
                // The URL is checked here; plain http to localhost passes.
                .https_only(false)
                .http_status_as_error(false)
                // A redirect could lead to a host the manifest does not
                // name: the plugin gets the 3xx and decides.
                .max_redirects(0)
                .timeout_connect(Some(CONNECT_TIMEOUT))
                .user_agent(format!("CabinetOS/{}", env!("CARGO_PKG_VERSION")))
                .build()
                .new_agent()
        })
    }

    /// Checks `ask` against `rules`, puts the secret into its header, and
    /// makes the request. `secret` reads a stored secret by name. Blocking.
    pub(crate) fn request(
        &self,
        rules: &NetRules,
        ask: Ask,
        secret: &dyn Fn(&str) -> Option<String>,
    ) -> Result<Answer, String> {
        let url = check_url(&ask.url, &rules.hosts)?;
        let method = ask.method.to_ascii_uppercase();
        if !matches!(
            method.as_str(),
            "GET" | "POST" | "PUT" | "PATCH" | "DELETE" | "HEAD"
        ) {
            return Err(format!("method `{}` is not allowed", ask.method));
        }
        let mut builder = ureq::http::Request::builder()
            .method(method.as_str())
            .uri(url.as_str());
        let secret_header = secret_header(rules, &ask, secret)?;
        for (name, value) in &ask.headers {
            let lower = name.to_ascii_lowercase();
            // The core sets these itself.
            if lower == "host"
                || lower == "content-length"
                || secret_header
                    .as_ref()
                    .is_some_and(|(header, _)| *header == lower)
            {
                continue;
            }
            builder = builder.header(name.as_str(), value.as_str());
        }
        if let Some((header, value)) = &secret_header {
            builder = builder.header(header.as_str(), value.as_str());
        }
        let timeout = match ask.timeout_ms {
            0 => MAX_TIMEOUT,
            ms => Duration::from_millis(u64::from(ms)).min(MAX_TIMEOUT),
        };
        let host = url.host_str().unwrap_or_default().to_owned();
        let started = Instant::now();
        let request = builder
            .body(ask.body.unwrap_or_default())
            .map_err(|error| format!("the request is not valid: {error}"))?;
        let request = self
            .agent()
            .configure_request(request)
            .timeout_global(Some(timeout))
            .build();
        let result = self.agent().run(request);
        let elapsed_ms = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);
        match result {
            Ok(response) => read_answer(response, &host, &method, started),
            Err(error) => {
                tracing::info!(host = %host, method = %method, elapsed_ms, error = %error, "web request failed");
                Err(format!("the request to {host} failed: {error}"))
            }
        }
    }
}

/// The header that carries the secret `ask` names, with its value: only a
/// secret the manifest lists, and only when one is stored.
fn secret_header(
    rules: &NetRules,
    ask: &Ask,
    secret: &dyn Fn(&str) -> Option<String>,
) -> Result<Option<(String, String)>, String> {
    match (&ask.secret, &ask.secret_header) {
        (Some(name), Some(header)) => {
            if !rules.secrets.iter().any(|allowed| allowed == name) {
                return Err(format!(
                    "the secret `{name}` is not among the secrets plugin.json names for net"
                ));
            }
            let value = secret(name).ok_or_else(|| {
                format!(
                    "no secret `{name}` is stored; store it with `cabinetos-cli secret set {name}`"
                )
            })?;
            let value = if header.eq_ignore_ascii_case("authorization") {
                format!("Bearer {value}")
            } else {
                value
            };
            Ok(Some((header.to_ascii_lowercase(), value)))
        }
        (Some(_), None) => Err("a request with `secret` needs `secret-header`".to_owned()),
        (None, _) => Ok(None),
    }
}

/// The status, headers and body of `response`, the body up to 8 MiB.
fn read_answer(
    response: ureq::http::Response<ureq::Body>,
    host: &str,
    method: &str,
    started: Instant,
) -> Result<Answer, String> {
    let status = response.status().as_u16();
    let headers = response
        .headers()
        .iter()
        .filter_map(|(name, value)| {
            value
                .to_str()
                .ok()
                .map(|value| (name.as_str().to_owned(), value.to_owned()))
        })
        .collect();
    let mut body = Vec::new();
    let read = response
        .into_body()
        .into_reader()
        .take(MAX_BODY + 1)
        .read_to_end(&mut body);
    let elapsed_ms = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);
    if let Err(error) = read {
        tracing::info!(host = %host, method = %method, status, elapsed_ms, error = %error, "web request failed while reading");
        return Err(format!("reading the answer of {host} failed: {error}"));
    }
    if body.len() as u64 > MAX_BODY {
        tracing::info!(host = %host, method = %method, status, elapsed_ms, "web answer larger than 8 MiB; refused");
        return Err(format!("the answer of {host} is larger than 8 MiB"));
    }
    tracing::info!(
        host = %host,
        method = %method,
        status,
        bytes = body.len(),
        elapsed_ms,
        "web request"
    );
    Ok(Answer {
        status,
        headers,
        body,
    })
}

/// The URL, when the plugin may reach it: `https:` to a named host, or
/// plain `http:` to `localhost` or `127.0.0.1` when named; no user name or
/// password in it.
pub(crate) fn check_url(text: &str, hosts: &[HostRule]) -> Result<Url, String> {
    let url = Url::parse(text).map_err(|error| format!("`{text}` is not a URL: {error}"))?;
    let host = url
        .host_str()
        .ok_or_else(|| format!("`{text}` names no host"))?
        .to_ascii_lowercase();
    let local = host == "localhost" || host == "127.0.0.1";
    let default_port = match url.scheme() {
        "https" => 443,
        "http" if local => 80,
        "http" => {
            return Err(format!(
                "`{text}`: plain http is allowed only to localhost and 127.0.0.1; use https"
            ));
        }
        scheme => return Err(format!("`{text}`: the scheme `{scheme}` is not allowed")),
    };
    if !url.username().is_empty() || url.password().is_some() {
        return Err(format!(
            "`{text}`: a URL may not carry a user name or password"
        ));
    }
    let port = url.port().unwrap_or(default_port);
    if !hosts
        .iter()
        .any(|rule| rule.matches(&host, port, default_port))
    {
        return Err(format!(
            "{host}:{port} is not among the hosts plugin.json names for net"
        ));
    }
    Ok(url)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rules() -> Vec<HostRule> {
        ["api.anthropic.com", "localhost:11434", "127.0.0.1:8090"]
            .into_iter()
            .map(|host| HostRule::parse(host).unwrap())
            .collect()
    }

    #[test]
    fn only_named_hosts_and_https_pass() {
        let rules = rules();
        for good in [
            "https://api.anthropic.com/v1/messages",
            "https://API.anthropic.com:443/v1/messages",
            "http://localhost:11434/v1/chat/completions",
            "http://127.0.0.1:8090/echo?x=1",
        ] {
            assert!(check_url(good, &rules).is_ok(), "{good}");
        }
        for (bad, words) in [
            ("http://api.anthropic.com/v1", "plain http"),
            ("https://evil.example.com/", "not among the hosts"),
            ("https://api.anthropic.com:8443/", "not among the hosts"),
            ("http://localhost:8080/", "not among the hosts"),
            ("ftp://api.anthropic.com/", "scheme"),
            ("https://user:pass@api.anthropic.com/", "user name"),
            ("not a url", "not a URL"),
        ] {
            let error = check_url(bad, &rules).unwrap_err();
            assert!(error.contains(words), "{bad}: {error}");
        }
    }

    #[test]
    fn a_secret_must_be_named_and_stored() {
        let net = Net::default();
        let rules = NetRules {
            hosts: rules(),
            secrets: vec!["anthropic".to_owned()],
        };
        let ask = |secret: Option<&str>, header: Option<&str>| Ask {
            method: "GET".to_owned(),
            url: "https://api.anthropic.com/v1/models".to_owned(),
            headers: Vec::new(),
            body: None,
            secret: secret.map(str::to_owned),
            secret_header: header.map(str::to_owned),
            timeout_ms: 1,
        };
        let none = |_: &str| None;
        let error = net
            .request(&rules, ask(Some("openai"), Some("x-api-key")), &none)
            .unwrap_err();
        assert!(error.contains("not among the secrets"), "{error}");
        let error = net
            .request(&rules, ask(Some("anthropic"), Some("x-api-key")), &none)
            .unwrap_err();
        assert!(
            error.contains("cabinetos-cli secret set anthropic"),
            "{error}"
        );
        let error = net
            .request(&rules, ask(Some("anthropic"), None), &none)
            .unwrap_err();
        assert!(error.contains("secret-header"), "{error}");
        let mut bad_method = ask(None, None);
        bad_method.method = "CONNECT".to_owned();
        assert!(
            net.request(&rules, bad_method, &none)
                .unwrap_err()
                .contains("not allowed")
        );
    }
}
