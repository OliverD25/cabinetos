//! The catalogues: where they are, reading them, and searching them. The
//! marketplace has two, `index.json` for extensions and `themes.json` for
//! themes, in one format (ADR 0022).

use std::path::{Path, PathBuf};

use cabinetos_commands::rank;
use cabinetos_protocol::{
    Catalogue, ExtensionKind, INDEX_SCHEMA_VERSION, MarketItem, extension_id_problem,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use url::Url;

use crate::transfer::{self, Base, Client, Fetched, Http, Location};
use crate::{MarketError, now_ms, parse_version};

/// The file of a catalogue in a folder, on the web, and in the cache.
const fn file_of(catalogue: Catalogue) -> &'static str {
    match catalogue {
        Catalogue::Extensions => "index.json",
        Catalogue::Themes => "themes.json",
    }
}

/// The file that keeps a cached catalogue's `ETag`.
const fn meta_of(catalogue: Catalogue) -> &'static str {
    match catalogue {
        Catalogue::Extensions => "index.meta.json",
        Catalogue::Themes => "themes.meta.json",
    }
}

/// What messages call a catalogue.
const fn what_of(catalogue: Catalogue) -> &'static str {
    match catalogue {
        Catalogue::Extensions => "the index",
        Catalogue::Themes => "the themes catalogue",
    }
}

/// The most bytes an index may have.
const MAX_INDEX_BYTES: u64 = 16 * 1024 * 1024;

/// Where a catalogue is (`marketplace.index` or `marketplace.themes`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Source {
    /// A catalogue file on this machine, or the folder that holds it.
    Local(PathBuf),
    /// An `https:` URL, or `http:` when the configuration allows it.
    Remote(Url),
}

impl Source {
    /// Reads `marketplace.index`: an `https:` URL; a plain `http:` URL only
    /// when `allow_insecure`; a `file:` URL; or an absolute path to an
    /// `index.json` or to its folder.
    pub fn parse(location: &str, allow_insecure: bool) -> Result<Self, MarketError> {
        Self::parse_setting("marketplace.index", location, allow_insecure)
    }

    /// Reads `marketplace.themes` the same way: an `https:` URL, a `file:`
    /// URL, or an absolute path to a `themes.json` or to its folder.
    pub fn parse_themes(location: &str, allow_insecure: bool) -> Result<Self, MarketError> {
        Self::parse_setting("marketplace.themes", location, allow_insecure)
    }

    fn parse_setting(
        setting: &str,
        location: &str,
        allow_insecure: bool,
    ) -> Result<Self, MarketError> {
        let location = location.trim();
        if location.is_empty() {
            return Err(MarketError::market(format!("{setting} is empty")));
        }
        match transfer::parse_location(location, allow_insecure, Client::Market)
            .map_err(MarketError::market)?
        {
            Location::File(path) => local(setting, path),
            Location::Web(url) => Ok(Self::Remote(url)),
        }
    }
}

fn local(setting: &str, path: PathBuf) -> Result<Source, MarketError> {
    if path.is_absolute() {
        Ok(Source::Local(path))
    } else {
        Err(MarketError::market(format!(
            "{setting} `{}` must be an absolute path or a URL",
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
    /// The same index with only the items of `catalogue`: a theme belongs
    /// to the themes catalogue, a plugin or a tool to the extensions.
    #[must_use]
    pub fn only(&self, catalogue: Catalogue) -> Self {
        Self {
            items: self
                .items
                .iter()
                .filter(|item| catalogue.holds(item.kind))
                .cloned()
                .collect(),
            ..self.clone()
        }
    }

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

/// Reads a catalogue at `source`: from disk, or from the web with the cache
/// in `cache_dir`. `Ok(None)` only for the themes catalogue, when the file
/// is not there: a 404 on the web, or no such file on this machine. The
/// extensions' `index.json` must exist.
pub(crate) fn fetch(
    source: &Source,
    catalogue: Catalogue,
    cache_dir: &Path,
    http: &Http,
    allow_insecure: bool,
) -> Result<Option<Index>, MarketError> {
    match source {
        Source::Local(path) => {
            let file = if path.is_dir() {
                path.join(file_of(catalogue))
            } else {
                path.clone()
            };
            if catalogue == Catalogue::Themes && !file.exists() {
                return Ok(None);
            }
            let bytes = read_limited(&file, catalogue)?;
            let items = parse_catalogue(
                &text_of(&bytes, &file.display().to_string(), catalogue)?,
                catalogue,
            )?;
            let folder = file.parent().map(Path::to_path_buf).unwrap_or_default();
            Ok(Some(Index {
                items,
                source: file.display().to_string(),
                fetched_at_ms: now_ms(),
                base: Base::Folder(folder),
            }))
        }
        Source::Remote(url) => fetch_remote(url, catalogue, cache_dir, http, allow_insecure),
    }
}

fn read_limited(file: &Path, catalogue: Catalogue) -> Result<Vec<u8>, MarketError> {
    transfer::read_limited(file, MAX_INDEX_BYTES, what_of(catalogue)).map_err(MarketError::market)
}

fn text_of(bytes: &[u8], at: &str, catalogue: Catalogue) -> Result<String, MarketError> {
    String::from_utf8(bytes.to_vec())
        .map_err(|_| MarketError::market(format!("{} {at} is not UTF-8 text", what_of(catalogue))))
}

/// Fetches a catalogue from the web. The cache in `cache_dir` holds the
/// last copy and its `ETag`; an unchanged catalogue costs one `304 Not
/// Modified`. A cached copy that cannot be read (half written, damaged) is
/// fetched again whole: a 304 alone would send every later refresh back to
/// it. `Ok(None)` for a themes catalogue the server does not have (404).
fn fetch_remote(
    url: &Url,
    catalogue: Catalogue,
    cache_dir: &Path,
    http: &Http,
    allow_insecure: bool,
) -> Result<Option<Index>, MarketError> {
    let what = what_of(catalogue);
    let cached = cache_dir.join(file_of(catalogue));
    let meta = std::fs::read(cache_dir.join(meta_of(catalogue)))
        .ok()
        .and_then(|bytes| serde_json::from_slice::<CacheMeta>(&bytes).ok())
        .filter(|meta| meta.source == url.as_str() && cached.is_file());
    let tag = meta.as_ref().and_then(|meta| meta.etag.clone());
    let first = download(url, catalogue, tag.as_deref(), http, allow_insecure)?;
    let (items, fresh, etag) = match first {
        Downloaded::Missing => return Ok(None),
        Downloaded::Whole { text, etag } => (parse_catalogue(&text, catalogue)?, Some(text), etag),
        Downloaded::NotModified => match read_cached(&cached, url, catalogue) {
            Ok(items) => (items, None, tag),
            Err(error) => {
                tracing::warn!(%url, error = %error.message, "the cached copy of {what} cannot be read; fetching the whole file again");
                match download(url, catalogue, None, http, allow_insecure)? {
                    Downloaded::Whole { text, etag } => {
                        (parse_catalogue(&text, catalogue)?, Some(text), etag)
                    }
                    Downloaded::Missing => return Ok(None),
                    Downloaded::NotModified => {
                        return Err(MarketError::market(format!(
                            "the server answered 304 for {what} {url} without being asked"
                        )));
                    }
                }
            }
        },
    };
    let fetched_at_ms = now_ms();
    keep_in_cache(
        cache_dir,
        catalogue,
        fresh.as_deref(),
        &CacheMeta {
            source: url.to_string(),
            etag,
            fetched_at_ms,
        },
    );
    Ok(Some(Index {
        items,
        source: url.to_string(),
        fetched_at_ms,
        base: Base::Web(url.clone()),
    }))
}

/// The cached copy of the catalogue at `url` when it was read or confirmed
/// unchanged within `max_age_ms`, read from the cache folder alone: the web
/// is not asked. `None` when there is no such copy, it is older, or it
/// cannot be read.
pub(crate) fn cached(
    url: &Url,
    catalogue: Catalogue,
    cache_dir: &Path,
    max_age_ms: u64,
) -> Option<Index> {
    let meta: CacheMeta = std::fs::read(cache_dir.join(meta_of(catalogue)))
        .ok()
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())?;
    if meta.source != url.as_str() || now_ms().saturating_sub(meta.fetched_at_ms) > max_age_ms {
        return None;
    }
    let items = read_cached(&cache_dir.join(file_of(catalogue)), url, catalogue).ok()?;
    Some(Index {
        items,
        source: url.to_string(),
        fetched_at_ms: meta.fetched_at_ms,
        base: Base::Web(url.clone()),
    })
}

/// What one request for a catalogue brought.
enum Downloaded {
    /// The server confirmed the tag sent: the cached copy is current.
    NotModified,
    /// The server does not have the file (404), and the catalogue may be
    /// missing.
    Missing,
    /// The catalogue itself, with its tag.
    Whole { text: String, etag: Option<String> },
}

/// Asks for a catalogue, with `If-None-Match: tag` when there is a tag. A
/// 304 counts only as the answer to a tag. A 404 is `Missing` for the
/// themes catalogue and an error for the extensions' index.
fn download(
    url: &Url,
    catalogue: Catalogue,
    tag: Option<&str>,
    http: &Http,
    allow_insecure: bool,
) -> Result<Downloaded, MarketError> {
    let what = what_of(catalogue);
    let fetched = transfer::get_optional(
        http,
        url,
        tag,
        allow_insecure,
        MAX_INDEX_BYTES,
        what,
        Client::Market,
    )
    .map_err(MarketError::market)?;
    Ok(match fetched {
        None if catalogue == Catalogue::Themes => Downloaded::Missing,
        None => {
            return Err(MarketError::market(format!(
                "the server answered 404 for {what} {url}"
            )));
        }
        Some(Fetched::NotModified) => Downloaded::NotModified,
        Some(Fetched::Whole { bytes, etag }) => Downloaded::Whole {
            text: text_of(&bytes, url.as_str(), catalogue)?,
            etag,
        },
    })
}

/// The items of the cached copy of the catalogue at `url`.
fn read_cached(
    cached: &Path,
    url: &Url,
    catalogue: Catalogue,
) -> Result<Vec<MarketItem>, MarketError> {
    let bytes = read_limited(cached, catalogue)?;
    parse_catalogue(&text_of(&bytes, url.as_str(), catalogue)?, catalogue)
}

/// Best effort: without a cache the next fetch downloads the whole file.
/// Each file is replaced through a temporary file and a rename, so an
/// interrupted write leaves the old file whole. `text` is `None` when the
/// server confirmed the cached copy: only its time changes.
fn keep_in_cache(cache_dir: &Path, catalogue: Catalogue, text: Option<&str>, meta: &CacheMeta) {
    let written = std::fs::create_dir_all(cache_dir)
        .and_then(|()| match text {
            Some(text) => {
                transfer::replace_file(&cache_dir.join(file_of(catalogue)), text.as_bytes())
            }
            None => Ok(()),
        })
        .and_then(|()| {
            let meta = serde_json::to_vec_pretty(meta).map_err(std::io::Error::other)?;
            transfer::replace_file(&cache_dir.join(meta_of(catalogue)), &meta)
        });
    if let Err(error) = written {
        tracing::warn!(dir = %cache_dir.display(), %error, "cannot keep {} in the cache", what_of(catalogue));
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawIndex {
    schema_version: u32,
    #[serde(default)]
    items: Vec<Value>,
}

/// Reads the text of an `index.json`; see [`parse_catalogue`].
pub fn parse_index(text: &str) -> Result<Vec<MarketItem>, MarketError> {
    parse_catalogue(text, Catalogue::Extensions)
}

/// Reads a catalogue's text (`catalogue` only changes the words of an
/// error). An item that does not follow the format (an unknown `kind` from
/// a newer index, a missing key, a bad ID, version, hash or tile colour, a
/// second copy of one ID and version) is logged and left out, so one bad
/// item does not hide the others. Which items belong in the catalogue is
/// not decided here: [`Index::only`] does that.
pub fn parse_catalogue(text: &str, catalogue: Catalogue) -> Result<Vec<MarketItem>, MarketError> {
    let what = what_of(catalogue);
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let raw: RawIndex = serde_json::from_str(text)
        .map_err(|error| MarketError::market(format!("{what} is not valid: {error}")))?;
    if raw.schema_version != INDEX_SCHEMA_VERSION {
        return Err(MarketError::market(format!(
            "{what} has schemaVersion {}; this core reads version {INDEX_SCHEMA_VERSION}",
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
    if item
        .tile
        .as_ref()
        .is_some_and(|tile| !tile.is_well_formed())
    {
        return Err("`tile` has a colour that is not #RRGGBB".to_owned());
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

    #[test]
    fn a_cached_catalogue_is_used_only_while_it_is_young_enough() {
        const DAY_MS: u64 = 24 * 60 * 60 * 1000;
        let root = std::env::temp_dir().join("cabinetos-core-test");
        std::fs::create_dir_all(&root).unwrap();
        let scratch = tempfile::Builder::new()
            .prefix("index-cache")
            .tempdir_in(root)
            .unwrap();
        let cache = scratch.path();
        let url = Url::parse("https://example.invalid/market/index.json").unwrap();
        std::fs::write(
            cache.join("index.json"),
            index(&[item("viewer", "tool", "1.0.0")]),
        )
        .unwrap();
        let write_meta = |source: &str, age_ms: u64| {
            let meta = CacheMeta {
                source: source.to_owned(),
                etag: Some("\"v1\"".to_owned()),
                fetched_at_ms: now_ms() - age_ms,
            };
            std::fs::write(
                cache.join("index.meta.json"),
                serde_json::to_vec(&meta).unwrap(),
            )
            .unwrap();
        };
        let seven_days = 7 * DAY_MS;
        write_meta(url.as_str(), 6 * DAY_MS);
        let young = cached(&url, Catalogue::Extensions, cache, seven_days).unwrap();
        assert_eq!(young.items[0].id, "viewer");
        assert_eq!(young.source, url.as_str());
        write_meta(url.as_str(), 8 * DAY_MS);
        assert!(cached(&url, Catalogue::Extensions, cache, seven_days).is_none());
        write_meta("https://example.invalid/other/index.json", 0);
        assert!(cached(&url, Catalogue::Extensions, cache, seven_days).is_none());
    }
}
