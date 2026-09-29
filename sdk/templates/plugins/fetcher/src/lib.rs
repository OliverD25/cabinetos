//! A test fixture for `net`. `fetcher.get {"url", "method", "body",
//! "secret", "header", "timeout_ms"}` makes one request through the core's
//! `http-request` and answers `{"status", "body", "headers"}`, the body as
//! text; an error comes back as the command's error. Its manifest names
//! one host, `localhost:8090`, and one secret, `fetcher-test`, whose value
//! the core puts into the header and this plugin never sees.

wit_bindgen::generate!({
    path: "../../../wit",
    world: "core-plugin",
});

use cabinetos::plugin::host::{self, WebRequest};

struct Fetcher;

impl Guest for Fetcher {
    fn info() -> PluginInfo {
        PluginInfo {
            id: "fetcher".to_owned(),
            name: "Fetcher".to_owned(),
            version: env!("CARGO_PKG_VERSION").to_owned(),
            api_version: "0.2.0".to_owned(),
        }
    }

    fn activate(_ctx: Activation) -> Result<(), String> {
        host::register_command("fetcher.get", "Fetch", "Fetcher", &[]);
        Ok(())
    }

    fn deactivate() {}

    fn on_command(id: String, args: String) -> Result<String, String> {
        if id != "fetcher.get" {
            return Err(format!("Fetcher has no command {id}"));
        }
        let args: serde_json::Value =
            serde_json::from_str(&args).map_err(|error| format!("args are not JSON: {error}"))?;
        let text = |key: &str| args[key].as_str().map(str::to_owned);
        let request = WebRequest {
            method: text("method").unwrap_or_else(|| "GET".to_owned()),
            url: text("url").ok_or("args need a \"url\"")?,
            headers: vec![("accept".to_owned(), "application/json".to_owned())],
            body: text("body").map(String::into_bytes),
            secret: text("secret"),
            secret_header: text("header"),
            timeout_ms: args["timeout_ms"]
                .as_u64()
                .and_then(|ms| u32::try_from(ms).ok())
                .unwrap_or(0),
        };
        let answer = host::http_request(&request)?;
        let headers: serde_json::Map<String, serde_json::Value> = answer
            .headers
            .into_iter()
            .map(|(name, value)| (name, serde_json::Value::String(value)))
            .collect();
        Ok(serde_json::json!({
            "status": answer.status,
            "body": String::from_utf8_lossy(&answer.body),
            "bytes": answer.body.len(),
            "headers": headers,
        })
        .to_string())
    }

    fn before_job(_job: JobSummary) -> JobVerdict {
        JobVerdict::Allow
    }

    fn on_listing_opened(_path: String, _entry_count: u32) {}
}

export!(Fetcher);
