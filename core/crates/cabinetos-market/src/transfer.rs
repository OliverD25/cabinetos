//! Getting files over the network or from disk, checking them and
//! unpacking them: what the marketplace and the updater share
//! (`cabinetos-update`, docs/marketplace.md and ADR 0014).
//!
//! - [`parse_location`] and [`locate`] read an address: an `https:` URL
//!   (plain `http:` only when a setting allows it), a `file:` URL, or a
//!   path; a relative reference is resolved next to the file that named it.
//! - [`Http`] is the HTTPS client: rustls with the Windows certificate
//!   store, made when first needed.
//! - [`get`] asks for a small file, with `If-None-Match` when a tag is
//!   known; [`download`] streams a large one into a file, counting its
//!   bytes and computing its SHA-256 on the way.
//! - [`extract_zip`] unpacks a zip into a folder, refusing entries that
//!   point outside it, links, and more entries or bytes than allowed.
//!
//! Nothing here reaches the network on its own: every function is called
//! for one request a client made (or the updater's daily check).

use std::collections::BTreeSet;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::OnceLock;
use std::time::{Duration, Instant};

use sha2::{Digest, Sha256};
use ureq::Agent;
use ureq::tls::{RootCerts, TlsConfig};
use url::Url;

/// How long connecting to a server may take.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// How long one request may take, a download included.
const REQUEST_TIMEOUT: Duration = Duration::from_mins(10);

/// Who fetches: the words of a refusal, and the heavy log's target.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Client {
    /// The marketplace (`marketplace.allowInsecure`).
    Market,
    /// The updater (`update.allowInsecure`).
    Update,
}

impl Client {
    /// The setting that allows plain `http:`.
    const fn insecure_setting(self) -> &'static str {
        match self {
            Self::Market => "marketplace.allowInsecure",
            Self::Update => "update.allowInsecure",
        }
    }

    /// Who reads, for a refused scheme.
    const fn reader(self) -> &'static str {
        match self {
            Self::Market => "the marketplace",
            Self::Update => "the updater",
        }
    }
}

/// Heavy mode's line for one network request: host, method, status (0 when
/// no answer came), bytes and time; never its headers or its body.
pub fn http_line(
    client: Client,
    url: &Url,
    method: &'static str,
    status: u16,
    bytes: u64,
    started: Instant,
) {
    let host = url.host_str().unwrap_or_default();
    let ms = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);
    // A tracing target must be a constant, so each client has its own line.
    match client {
        Client::Market => tracing::debug!(
            target: "heavy::market",
            host, method, status, bytes, ms,
            "http request"
        ),
        Client::Update => tracing::debug!(
            target: "heavy::update",
            host, method, status, bytes, ms,
            "http request"
        ),
    }
}

/// Where a file is.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Location {
    /// On this machine.
    File(PathBuf),
    /// On the web: `https:`, or `http:` when allowed.
    Web(Url),
}

/// What a relative reference is relative to: the folder of a file on this
/// machine, or the URL of a file on the web.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Base {
    /// The folder that holds the file that names the reference.
    Folder(PathBuf),
    /// The URL of the file that names the reference.
    Web(Url),
}

/// `C:\…`, `C:/…` or `\\server\share`: a Windows path, which a URL parser
/// would read as the scheme `c:`.
#[must_use]
pub fn looks_like_path(text: &str) -> bool {
    let bytes = text.as_bytes();
    (bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':')
        || text.starts_with(r"\\")
}

/// `url` if it may be fetched: `https:`, or `http:` when allowed.
pub fn web_url(url: Url, allow_insecure: bool, client: Client) -> Result<Url, String> {
    match url.scheme() {
        "https" => Ok(url),
        "http" if allow_insecure => Ok(url),
        "http" => Err(format!(
            "`{url}` uses plain http, which anyone on the network can change on the way; use https, or set {} to true (for testing only)",
            client.insecure_setting()
        )),
        scheme => Err(format!(
            "`{url}`: {} reads https: URLs, file: URLs and paths, not {scheme}:",
            client.reader()
        )),
    }
}

/// Reads an address a setting gives: an `https:` URL; a plain `http:` URL
/// only when `allow_insecure`; a `file:` URL; or a path. The caller checks
/// that a path is absolute.
pub fn parse_location(
    text: &str,
    allow_insecure: bool,
    client: Client,
) -> Result<Location, String> {
    if looks_like_path(text) {
        return Ok(Location::File(PathBuf::from(text)));
    }
    match Url::parse(text) {
        Ok(url) if url.scheme() == "file" => url
            .to_file_path()
            .map(Location::File)
            .map_err(|()| format!("`{text}` does not name a file")),
        Ok(url) => web_url(url, allow_insecure, client).map(Location::Web),
        Err(_) => Ok(Location::File(PathBuf::from(text))),
    }
}

/// Where `reference` is, named in a file at `base`: an absolute URL, or a
/// path relative to that file. A file on the web may only name files on
/// the web.
pub fn locate(
    base: &Base,
    reference: &str,
    allow_insecure: bool,
    client: Client,
) -> Result<Location, String> {
    match base {
        Base::Web(base) => {
            let url = base
                .join(reference)
                .map_err(|error| format!("the download `{reference}` is not a URL: {error}"))?;
            web_url(url, allow_insecure, client).map(Location::Web)
        }
        Base::Folder(folder) => {
            if !looks_like_path(reference)
                && let Ok(url) = Url::parse(reference)
            {
                if url.scheme() == "file" {
                    return url
                        .to_file_path()
                        .map(Location::File)
                        .map_err(|()| format!("`{reference}` does not name a file"));
                }
                return web_url(url, allow_insecure, client).map(Location::Web);
            }
            Ok(Location::File(folder.join(reference)))
        }
    }
}

/// The HTTP clients, made when first needed: a process that never reaches
/// the network never sets up TLS. Certificates are checked against the
/// Windows certificate store.
#[derive(Debug, Default)]
pub struct Http {
    strict: OnceLock<Agent>,
    lenient: OnceLock<Agent>,
}

impl Http {
    /// The client; `allow_insecure` lets it follow plain `http:` too.
    pub fn agent(&self, allow_insecure: bool) -> &Agent {
        let cell = if allow_insecure {
            &self.lenient
        } else {
            &self.strict
        };
        cell.get_or_init(|| {
            Agent::config_builder()
                .tls_config(
                    TlsConfig::builder()
                        .root_certs(RootCerts::PlatformVerifier)
                        .build(),
                )
                // A redirect from https to plain http is refused too.
                .https_only(!allow_insecure)
                .http_status_as_error(false)
                .timeout_connect(Some(CONNECT_TIMEOUT))
                .timeout_global(Some(REQUEST_TIMEOUT))
                .user_agent(format!("CabinetOS/{}", env!("CARGO_PKG_VERSION")))
                .build()
                .new_agent()
        })
    }
}

/// What one request for a small file brought.
#[derive(Debug)]
pub enum Fetched {
    /// The server confirmed the tag sent: the caller's copy is current.
    NotModified,
    /// The file itself, with its tag.
    Whole {
        /// The file's bytes.
        bytes: Vec<u8>,
        /// The server's `ETag`, when it sent one.
        etag: Option<String>,
    },
}

/// Asks for a small file, `what` in messages (`the index`), with
/// `If-None-Match: tag` when there is a tag. A 304 counts only as the
/// answer to a tag. More than `limit` bytes is an error.
pub fn get(
    http: &Http,
    url: &Url,
    tag: Option<&str>,
    allow_insecure: bool,
    limit: u64,
    what: &str,
    client: Client,
) -> Result<Fetched, String> {
    let started = Instant::now();
    let mut request = http.agent(allow_insecure).get(url.as_str());
    if let Some(tag) = tag {
        request = request.header("If-None-Match", tag);
    }
    let response = request.call().map_err(|error| {
        http_line(client, url, "GET", 0, 0, started);
        format!("cannot fetch {what} {url}: {error}")
    })?;
    let status = response.status().as_u16();
    if status != 200 {
        http_line(client, url, "GET", status, 0, started);
    }
    match status {
        304 if tag.is_some() => Ok(Fetched::NotModified),
        200 => {
            let etag = response
                .headers()
                .get("etag")
                .and_then(|value| value.to_str().ok())
                .map(str::to_owned);
            let bytes = response
                .into_body()
                .into_with_config()
                .limit(limit)
                .read_to_vec()
                .map_err(|error| format!("cannot read {what} {url}: {error}"))?;
            http_line(client, url, "GET", status, bytes.len() as u64, started);
            Ok(Fetched::Whole { bytes, etag })
        }
        status => Err(format!("the server answered {status} for {what} {url}")),
    }
}

/// Reads a small file on this machine, `what` in messages; more than
/// `limit` bytes is an error.
pub fn read_limited(file: &Path, limit: u64, what: &str) -> Result<Vec<u8>, String> {
    let cannot = |error: io::Error| format!("cannot read {what} {}: {error}", file.display());
    let mut bytes = Vec::new();
    File::open(file)
        .map_err(cannot)?
        .take(limit + 1)
        .read_to_end(&mut bytes)
        .map_err(cannot)?;
    if bytes.len() as u64 > limit {
        return Err(format!(
            "{what} {} is larger than {}",
            file.display(),
            size_text(limit)
        ));
    }
    Ok(bytes)
}

/// Why a download failed.
#[derive(Debug)]
pub enum DownloadError {
    /// More bytes came than allowed; the partial file may remain, and the
    /// caller deletes it.
    TooLarge,
    /// Anything else, for the user.
    Failed(String),
}

/// A finished download.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Downloaded {
    /// Its size in bytes.
    pub bytes: u64,
    /// Its SHA-256, lower case hex.
    pub sha256: String,
}

/// Copies (from disk) or downloads (from the web) `location` into `to`,
/// with at most `limit` bytes, and computes its SHA-256 on the way.
/// `progress` hears the bytes done after each piece, and answers whether to
/// go on: `false` stops the download (the caller is stopping). Blocking.
pub fn download(
    http: &Http,
    location: &Location,
    to: &Path,
    limit: u64,
    allow_insecure: bool,
    client: Client,
    progress: &mut dyn FnMut(u64) -> bool,
) -> Result<Downloaded, DownloadError> {
    let failed = DownloadError::Failed;
    let started = Instant::now();
    // Set for a download over the network, which heavy mode logs.
    let mut web = None;
    let mut source: Box<dyn Read> = match location {
        Location::File(path) => Box::new(
            File::open(path).map_err(|error| failed(format!("{}: {error}", path.display())))?,
        ),
        Location::Web(url) => {
            let response = http
                .agent(allow_insecure)
                .get(url.as_str())
                .call()
                .map_err(|error| {
                    http_line(client, url, "GET", 0, 0, started);
                    failed(error.to_string())
                })?;
            let status = response.status().as_u16();
            if status != 200 {
                http_line(client, url, "GET", status, 0, started);
                return Err(failed(format!("the server answered {status} for {url}")));
            }
            web = Some(url);
            Box::new(response.into_body().into_reader())
        }
    };
    let mut file =
        File::create(to).map_err(|error| failed(format!("{}: {error}", to.display())))?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0; 64 * 1024];
    let mut done = 0_u64;
    loop {
        let read = source
            .read(&mut buffer)
            .map_err(|error| failed(error.to_string()))?;
        if read == 0 {
            break;
        }
        done += read as u64;
        if done > limit {
            return Err(DownloadError::TooLarge);
        }
        hasher.update(&buffer[..read]);
        file.write_all(&buffer[..read])
            .map_err(|error| failed(format!("{}: {error}", to.display())))?;
        if !progress(done) {
            return Err(failed("the download was stopped".to_owned()));
        }
    }
    file.flush()
        .map_err(|error| failed(format!("{}: {error}", to.display())))?;
    if let Some(url) = web {
        http_line(client, url, "GET", 200, done, started);
    }
    Ok(Downloaded {
        bytes: done,
        sha256: hex(&hasher.finalize()),
    })
}

/// How much a zip may unpack to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ZipLimits {
    /// The most entries.
    pub entries: usize,
    /// The most bytes, all files together.
    pub bytes: u64,
}

/// Unpacks a zip into `staging`. Refuses entries that point outside it,
/// links, and more entries or bytes than `limits` allow. Returns the files,
/// relative to `staging`, with `/` between names, sorted.
pub fn extract_zip(
    archive: &Path,
    staging: &Path,
    limits: ZipLimits,
) -> Result<Vec<String>, String> {
    let file = File::open(archive).map_err(|error| format!("cannot be opened: {error}"))?;
    let mut zip =
        zip::ZipArchive::new(file).map_err(|error| format!("is not a valid zip: {error}"))?;
    if zip.len() > limits.entries {
        return Err(format!("has more than {} files", limits.entries));
    }
    let mut unpacked = 0_u64;
    let mut files = BTreeSet::new();
    for position in 0..zip.len() {
        let mut entry = zip
            .by_index(position)
            .map_err(|error| format!("is not a valid zip: {error}"))?;
        let name = entry.name().to_owned();
        let relative = entry
            .enclosed_name()
            .filter(|path| {
                path.components()
                    .all(|part| matches!(part, Component::Normal(_)))
            })
            .ok_or_else(|| format!("has the entry `{name}`, which points outside its folder"))?;
        if entry.is_symlink() {
            return Err(format!("has the entry `{name}`, which is a link"));
        }
        let out = staging.join(&relative);
        if entry.is_dir() {
            fs::create_dir_all(&out).map_err(|error| format!("cannot be unpacked: {error}"))?;
            continue;
        }
        if let Some(parent) = out.parent() {
            fs::create_dir_all(parent).map_err(|error| format!("cannot be unpacked: {error}"))?;
        }
        let mut written =
            File::create(&out).map_err(|error| format!("cannot unpack `{name}`: {error}"))?;
        let budget = limits.bytes - unpacked + 1;
        unpacked += io::copy(&mut (&mut entry).take(budget), &mut written)
            .map_err(|error| format!("cannot unpack `{name}`: {error}"))?;
        if unpacked > limits.bytes {
            return Err(format!("unpacks to more than {}", size_text(limits.bytes)));
        }
        files.insert(slash_path(&relative));
    }
    Ok(files.into_iter().collect())
}

/// A relative path with `/` between its names.
#[must_use]
pub fn slash_path(path: &Path) -> String {
    path.components()
        .map(|part| part.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/")
}

/// Writes `bytes` to `path` through a temporary file and a rename, so an
/// interrupted write leaves the old file whole.
pub fn replace_file(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let name = path
        .file_name()
        .map_or_else(String::new, |name| name.to_string_lossy().into_owned());
    let temporary = path.with_file_name(format!(".{name}.{}.tmp", std::process::id()));
    let result = fs::write(&temporary, bytes).and_then(|()| fs::rename(&temporary, path));
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

/// Lower case hex.
#[must_use]
pub fn hex(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(text, "{byte:02x}");
    }
    text
}

/// Whether `text` is 64 hex digits, as a SHA-256 is written.
#[must_use]
pub fn is_sha256(text: &str) -> bool {
    text.len() == 64 && text.chars().all(|c| c.is_ascii_hexdigit())
}

/// `256 MiB`, `16 MiB`: a limit in whole mebibytes when it is one.
fn size_text(bytes: u64) -> String {
    const MIB: u64 = 1024 * 1024;
    if bytes.is_multiple_of(MIB) {
        format!("{} MiB", bytes / MIB)
    } else {
        format!("{bytes} bytes")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locations_are_urls_or_paths() {
        assert_eq!(
            parse_location(r"C:\market\index.json", false, Client::Market).unwrap(),
            Location::File(PathBuf::from(r"C:\market\index.json"))
        );
        assert_eq!(
            parse_location("file:///C:/update", false, Client::Update).unwrap(),
            Location::File(PathBuf::from(r"C:\update"))
        );
        let refused = parse_location("http://example.org/u", false, Client::Update).unwrap_err();
        assert!(refused.contains("update.allowInsecure"), "{refused}");
        let refused = parse_location("ftp://example.org/u", true, Client::Update).unwrap_err();
        assert!(refused.contains("the updater reads"), "{refused}");
        assert!(matches!(
            parse_location("http://example.org/u", true, Client::Update),
            Ok(Location::Web(_))
        ));
    }

    #[test]
    fn sizes_and_hashes_read_well() {
        assert_eq!(size_text(256 * 1024 * 1024), "256 MiB");
        assert_eq!(size_text(1000), "1000 bytes");
        assert_eq!(hex(&[0, 0xab, 0x10]), "00ab10");
        assert!(is_sha256(&"aB".repeat(32)));
        assert!(!is_sha256(&"g".repeat(64)));
        assert!(!is_sha256("abc"));
    }
}
