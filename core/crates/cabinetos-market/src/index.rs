//! The index: where it is, reading it, and searching it.

use std::path::{Path, PathBuf};

use cabinetos_commands::rank;
use cabinetos_protocol::{ExtensionKind, INDEX_SCHEMA_VERSION, MarketItem, extension_id_problem};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use url::Url;

use crate::transfer::{self, Base, Client, Fetched, Http, Location};
use crate::{MarketError, now_ms, parse_version};

/// The file an index folder holds.
pub(crate) const INDEX_FILE: &str = "index.json";

/// The most bytes an index may have.
const MAX_INDEX_BYTES: u64 = 16 * 1024 * 1024;

/// Where an index is (`marketplace.index`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Source {
    /// An `index.json` on this machine, or the folder that holds it.
    Local(PathBuf),
    /// An `https:` URL, or `http:` when the configuration allows it.
    Remote(Url),
}

impl Source {
    /// Reads `marketplace.index`: an `https:` URL; a plain `http:` URL only
    /// when `allow_insecure`; a `file:` URL; or an absolute path to an
    /// `index.json` or to its folder.
    pub fn parse(location: &str, allow_insecure: bool) -> Result<Self, MarketError> {
        let location = location.trim();
        if location.is_empty() {
            return Err(MarketError::market("marketplace.index is empty"));
        }
        match transfer::parse_location(location, allow_insecure, Client::Market)
            .map_err(MarketError::market)?
        {
            Location::File(path) => local(path),
            Location::Web(url) => Ok(Self::Remote(url)),
        }
    }
}

fn local(path: PathBuf) -> Result<Source, MarketError> {
    if path.is_absolute() {
        Ok(Source::Local(path))
    } else {
        Err(MarketError::market(format!(
            "marketplace.index `{}` must be an absolute path or a URL",
            path.display()
        )))
    }
}

/// An index as the core read it.
#[derive(Clone, Debug)]
pub struct Index {
    /// The items that follow the format, in index order.
    pub items: Vec<MarketItem>,
    /// Where it came from: the index file, or the URL.
    pub source: String,
    /// When it was read or confirmed unchanged, in milliseconds since
    /// 1970-01-01 UTC.
    pub fetched_at_ms: u64,
    /// What a relative download URL is relative to.
    base: Base,
}

impl Index {
    /// Where a download named in this index is: an absolute URL, or a path
    /// relative to the index. An index on the web may only name downloads
    /// on the web.
    pub(crate) fn locate(
        &self,
        reference: &str,
        allow_insecure: bool,
    ) -> Result<Location, MarketError> {
        transfer::locate(&self.base, reference, allow_insecure, Client::Market)
            .map_err(MarketError::market)
    }
}

/// What the cache knows about the index it keeps.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CacheMeta {
    source: String,
    etag: Option<String>,
    fetched_at_ms: u64,
}

/// Reads the index at `source`: from disk, or from the web with the cache
/// in `cache_dir`.
pub(crate) fn fetch(
    source: &Source,
    cache_dir: &Path,
    http: &Http,
    allow_insecure: bool,
) -> Result<Index, MarketError> {
    match source {
        Source::Local(path) => {
            let file = if path.is_dir() {
                path.join(INDEX_FILE)
            } else {
                path.clone()
            };
            let bytes = read_limited(&file)?;
            let items = parse_index(&text_of(&bytes, &file.display().to_string())?)?;
            let folder = file.parent().map(Path::to_path_buf).unwrap_or_default();
            Ok(Index {
                items,
                source: file.display().to_string(),
                fetched_at_ms: now_ms(),
                base: Base::Folder(folder),
            })
        }
        Source::Remote(url) => fetch_remote(url, cache_dir, http, allow_insecure),
    }
}

fn read_limited(file: &Path) -> Result<Vec<u8>, MarketError> {
    transfer::read_limited(file, MAX_INDEX_BYTES, "the index").map_err(MarketError::market)
}

fn text_of(bytes: &[u8], what: &str) -> Result<String, MarketError> {
    String::from_utf8(bytes.to_vec())
        .map_err(|_| MarketError::market(format!("the index {what} is not UTF-8 text")))
}

/// The file that keeps the cached index's `ETag`.
const META_FILE: &str = "index.meta.json";

/// Fetches an index from the web. The cache in `cache_dir` holds the last
/// copy and its `ETag`; an unchanged index costs one `304 Not Modified`.
/// A cached copy that cannot be read (half written, damaged) is fetched
/// again whole: a 304 alone would send every later refresh back to it.
fn fetch_remote(
    url: &Url,
    cache_dir: &Path,
    http: &Http,
    allow_insecure: bool,
) -> Result<Index, MarketError> {
    let cached = cache_dir.join(INDEX_FILE);
    let meta = std::fs::read(cache_dir.join(META_FILE))
        .ok()
        .and_then(|bytes| serde_json::from_slice::<CacheMeta>(&bytes).ok())
        .filter(|meta| meta.source == url.as_str() && cached.is_file());
    let tag = meta.as_ref().and_then(|meta| meta.etag.clone());
    let (items, fresh, etag) = match download(url, tag.as_deref(), http, allow_insecure)? {
        Downloaded::Whole { text, etag } => (parse_index(&text)?, Some(text), etag),
        Downloaded::NotModified => match read_cached(&cached, url) {
            Ok(items) => (items, None, tag),
            Err(error) => {
                tracing::warn!(%url, error = %error.message, "the cached copy of the index cannot be read; fetching the whole index again");
                match download(url, None, http, allow_insecure)? {
                    Downloaded::Whole { text, etag } => (parse_index(&text)?, Some(text), etag),
                    Downloaded::NotModified => {
                        return Err(MarketError::market(format!(
                            "the server answered 304 for the index {url} without being asked"
                        )));
                    }
                }
            }
        },
    };
    let fetched_at_ms = now_ms();
    keep_in_cache(
        cache_dir,
        fresh.as_deref(),
        &CacheMeta {
            source: url.to_string(),
            etag,
            fetched_at_ms,
        },
    );
    Ok(Index {
        items,
        source: url.to_string(),
        fetched_at_ms,
        base: Base::Web(url.clone()),
    })
}

/// What one request for the index brought.
enum Downloaded {
    /// The server confirmed the tag sent: the cached copy is current.
    NotModified,
    /// The index itself, with its tag.
    Whole { text: String, etag: Option<String> },
}

/// Asks for the index, with `If-None-Match: tag` when there is a tag. A 304
/// counts only as the answer to a tag.
fn download(
    url: &Url,
    tag: Option<&str>,
    http: &Http,
    allow_insecure: bool,
) -> Result<Downloaded, MarketError> {
    let fetched = transfer::get(
        http,
        url,
        tag,
        allow_insecure,
        MAX_INDEX_BYTES,
        "the index",
        Client::Market,
    )
    .map_err(MarketError::market)?;
    Ok(match fetched {
        Fetched::NotModified => Downloaded::NotModified,
        Fetched::Whole { bytes, etag } => Downloaded::Whole {
            text: text_of(&bytes, url.as_str())?,
            etag,
        },
    })
}

/// The items of the cached copy of the index at `url`.
fn read_cached(cached: &Path, url: &Url) -> Result<Vec<MarketItem>, MarketError> {
    let bytes = read_limited(cached)?;
    parse_index(&text_of(&bytes, url.as_str())?)
}

/// Best effort: without a cache the next fetch downloads the whole index.
/// Each file is replaced through a temporary file and a rename, so an
/// interrupted write leaves the old file whole. `text` is `None` when the
/// server confirmed the cached copy: only its time changes.
fn keep_in_cache(cache_dir: &Path, text: Option<&str>, meta: &CacheMeta) {
    let written = std::fs::create_dir_all(cache_dir)
        .and_then(|()| match text {
            Some(text) => transfer::replace_file(&cache_dir.join(INDEX_FILE), text.as_bytes()),
            None => Ok(()),
        })
        .and_then(|()| {
            let meta = serde_json::to_vec_pretty(meta).map_err(std::io::Error::other)?;
            transfer::replace_file(&cache_dir.join(META_FILE), &meta)
        });
    if let Err(error) = written {
        tracing::warn!(dir = %cache_dir.display(), %error, "cannot keep the index in the cache");
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawIndex {
    schema_version: u32,
    #[serde(default)]
    items: Vec<Value>,
}

/// Reads an index's text. An item that does not follow the format (an
/// unknown `kind` from a newer index, a missing key, a bad ID, version or
/// hash, a second copy of one ID and version) is logged and left out, so
/// one bad item does not hide the others.
pub fn parse_index(text: &str) -> Result<Vec<MarketItem>, MarketError> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let raw: RawIndex = serde_json::from_str(text)
        .map_err(|error| MarketError::market(format!("the index is not valid: {error}")))?;
    if raw.schema_version != INDEX_SCHEMA_VERSION {
        return Err(MarketError::market(format!(
            "the index has schemaVersion {}; this core reads version {INDEX_SCHEMA_VERSION}",
            raw.schema_version
        )));
    }
    let mut items: Vec<MarketItem> = Vec::new();
    for (position, value) in raw.items.into_iter().enumerate() {
        let item = match serde_json::from_value::<MarketItem>(value) {
            Ok(item) => item,
            Err(error) => {
                tracing::warn!(position, %error, "an index item was left out");
                continue;
            }
        };
        let problem = check_item(&item).err().or_else(|| {
            items
                .iter()
                .any(|known| known.id == item.id && known.version == item.version)
                .then(|| "the index lists this version twice".to_owned())
        });
        if let Some(problem) = problem {
            tracing::warn!(position, id = %item.id, %problem, "an index item was left out");
            continue;
        }
        items.push(item);
    }
    Ok(items)
}

/// The checks beyond the shape of an item.
fn check_item(item: &MarketItem) -> Result<(), String> {
    if let Some(problem) = extension_id_problem(&item.id) {
        return Err(problem);
    }
    if parse_version(&item.version).is_none() {
        return Err(format!(
            "version `{}` is not major.minor.patch",
            item.version
        ));
    }
    if parse_version(&item.min_core_version).is_none() {
        return Err(format!(
            "minCoreVersion `{}` is not major.minor.patch",
            item.min_core_version
        ));
    }
    if item.size == 0 {
        return Err("`size` is 0".to_owned());
    }
    let hash = &item.download.sha256;
    if hash.len() != 64 || !hash.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(format!("sha256 `{hash}` is not 64 hex digits"));
    }
    for (field, value) in [
        ("name", &item.name),
        ("author.name", &item.author.name),
        ("download.url", &item.download.url),
    ] {
        if value.trim().is_empty() {
            return Err(format!("`{field}` is empty"));
        }
    }
    Ok(())
}

/// The items that match `query` (in the name, the ID or the publisher's
/// name, ranked as the palette ranks commands), best first; only those of
/// `kind` when given. An empty query keeps every item in index order.
#[must_use]
pub fn search(items: &[MarketItem], query: &str, kind: Option<ExtensionKind>) -> Vec<MarketItem> {
    let pool: Vec<&MarketItem> = items
        .iter()
        .filter(|item| kind.is_none_or(|kind| item.kind == kind))
        .collect();
    rank(&pool, query, |item| {
        vec![item.name.clone(), item.id.clone(), item.author.name.clone()]
    })
    .into_iter()
    .map(|(index, _)| pool[index].clone())
    .collect()
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    fn item(id: &str, kind: &str, version: &str) -> Value {
        json!({
            "id": id,
            "kind": kind,
            "name": id.to_uppercase(),
            "author": {"name": "Tester"},
            "version": version,
            "description": "Test.",
            "size": 10,
            "download": {"url": format!("files/{id}-{version}.zip"), "sha256": "0".repeat(64)},
            "manifest": {"id": id},
            "minCoreVersion": "0.1.0",
            "license": "MIT"
        })
    }

    fn index(items: &[Value]) -> String {
        json!({"schemaVersion": 1, "generatedAt": "2026-09-28T00:00:00Z", "items": items})
            .to_string()
    }

    #[test]
    fn sources_are_urls_or_absolute_paths() {
        assert_eq!(
            Source::parse(r"C:\market\index.json", false).unwrap(),
            Source::Local(PathBuf::from(r"C:\market\index.json"))
        );
        assert_eq!(
            Source::parse("file:///C:/market/index.json", false).unwrap(),
            Source::Local(PathBuf::from(r"C:\market\index.json"))
        );
        assert!(matches!(
            Source::parse("https://example.org/index.json", false).unwrap(),
            Source::Remote(_)
        ));
        let refused = Source::parse("http://example.org/index.json", false).unwrap_err();
        assert!(refused.message.contains("allowInsecure"), "{refused}");
        assert!(Source::parse("http://example.org/index.json", true).is_ok());
        for bad in ["", "market/index.json", "ftp://example.org/index.json"] {
            assert!(Source::parse(bad, true).is_err(), "{bad}");
        }
    }

    #[test]
    fn downloads_are_found_next_to_the_index() {
        let local = Index {
            items: Vec::new(),
            source: String::new(),
            fetched_at_ms: 0,
            base: Base::Folder(PathBuf::from(r"C:\market")),
        };
        assert_eq!(
            local.locate("files/a.zip", false).unwrap(),
            Location::File(PathBuf::from(r"C:\market\files/a.zip"))
        );
        assert_eq!(
            local.locate(r"D:\elsewhere\a.zip", false).unwrap(),
            Location::File(PathBuf::from(r"D:\elsewhere\a.zip"))
        );
        assert!(matches!(
            local.locate("https://example.org/a.zip", false),
            Ok(Location::Web(_))
        ));
        let web = Index {
            base: Base::Web(Url::parse("https://example.org/market/index.json").unwrap()),
            ..local
        };
        assert_eq!(
            web.locate("files/a.zip", false).unwrap(),
            Location::Web(Url::parse("https://example.org/market/files/a.zip").unwrap())
        );
        assert!(web.locate("file:///C:/secret.txt", false).is_err());
        assert!(web.locate("http://example.org/a.zip", false).is_err());
    }

    #[test]
    fn a_bad_item_is_left_out_and_the_rest_stay() {
        let mut bad_hash = item("bad-hash", "plugin", "1.0.0");
        bad_hash["download"]["sha256"] = json!("xyz");
        let mut no_name = item("no-name", "theme", "1.0.0");
        no_name.as_object_mut().unwrap().remove("name");
        let items = parse_index(&index(&[
            item("hello", "plugin", "0.1.0"),
            item("future", "language-pack", "1.0.0"),
            item("Bad_ID", "theme", "1.0.0"),
            bad_hash,
            no_name,
            item("hello", "plugin", "0.1.0"),
            item("nord", "theme", "1.0.0"),
        ]))
        .unwrap();
        let kept: Vec<&str> = items.iter().map(|item| item.id.as_str()).collect();
        assert_eq!(kept, ["hello", "nord"]);

        let old = parse_index(r#"{"schemaVersion": 2, "items": []}"#).unwrap_err();
        assert!(old.message.contains("schemaVersion 2"), "{old}");
        assert!(parse_index("not json").is_err());
        let with_bom = format!("\u{feff}{}", index(&[item("nord", "theme", "1.0.0")]));
        assert_eq!(parse_index(&with_bom).unwrap().len(), 1);
    }

    #[test]
    fn search_ranks_like_the_palette_and_filters_by_kind() {
        let items = parse_index(&index(&[
            item("rose-pine-moon", "theme", "1.0.0"),
            item("nord", "theme", "1.0.0"),
            item("hello", "plugin", "0.1.0"),
        ]))
        .unwrap();
        let ids = |found: Vec<MarketItem>| -> Vec<String> {
            found.into_iter().map(|item| item.id).collect()
        };
        assert_eq!(ids(search(&items, "nord", None)), ["nord"]);
        assert_eq!(
            ids(search(&items, "", Some(ExtensionKind::Plugin))),
            ["hello"]
        );
        assert_eq!(
            ids(search(&items, "", None)),
            ["rose-pine-moon", "nord", "hello"]
        );
        assert_eq!(
            ids(search(&items, "tester", Some(ExtensionKind::Theme))).len(),
            2
        );
        assert!(search(&items, "zzz", None).is_empty());
    }
}
