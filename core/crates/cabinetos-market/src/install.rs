//! Installing and removing extensions.
//!
//! An install never runs anything and never leaves half an extension
//! behind: the download goes to a temporary file whose SHA-256 must match
//! the index; it is unpacked into a staging folder and checked there; only
//! then are its files copied into place, each through a temporary file and
//! a rename. `installed.json` records exactly which files an install put in
//! place, and an uninstall removes those and nothing else.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Mutex, MutexGuard, PoisonError};

use cabinetos_protocol::{Catalogue, ErrorCode, ExtensionKind, MarketItem, ToolInfo};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::index::{self, Index, Source};
use crate::tools::{self, TOOL_MANIFEST_FILE};
use crate::transfer::{self, Client, DownloadError, Http, ZipLimits};
use crate::{MarketError, now_ms, parse_version};

/// The most bytes a download may have, whatever the index says.
const MAX_DOWNLOAD: u64 = 256 * 1024 * 1024;

/// The most entries a zip may have.
const MAX_ZIP_ENTRIES: usize = 1000;

/// The most bytes a zip may unpack to.
const MAX_UNPACKED: u64 = 256 * 1024 * 1024;

const PLUGIN_MANIFEST: &str = "plugin.json";
const PLUGIN_COMPONENT: &str = "plugin.wasm";
const RECORD_FILE: &str = "installed.json";

/// Where extensions go, and the marketplace's own folder.
#[derive(Clone, Debug)]
pub struct Dirs {
    /// `<id>\plugin.json` and `<id>\plugin.wasm` for each Core Plugin.
    pub plugins: PathBuf,
    /// `<id>.json` for each theme.
    pub themes: PathBuf,
    /// `<id>\` for each Tool Extension.
    pub tools: PathBuf,
    /// The index cache, downloads while they run, and `installed.json`.
    pub market: PathBuf,
}

/// The one log line for theme items of `index.json` that nobody reads
/// because `themes.json` exists.
fn note_ignored_themes(count: usize) {
    tracing::info!(
        count,
        "themes.json is there; the theme items of index.json are ignored"
    );
}

/// What one install put in place, as `installed.json` keeps it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Installed {
    /// What it is.
    pub kind: ExtensionKind,
    /// The version installed.
    pub version: String,
    /// The SHA-256 of the download, lower case.
    pub sha256: String,
    /// The index it came from.
    pub source: String,
    /// When, in milliseconds since 1970-01-01 UTC.
    pub installed_at_ms: u64,
    /// The files, relative to the kind's folder, with `/` between names,
    /// such as `hello/plugin.json` or `nord.json`.
    pub files: Vec<String>,
}

/// The marketplace client: its folders, its HTTP client and the record of
/// installs.
#[derive(Debug)]
pub struct Market {
    dirs: Dirs,
    core_version: String,
    http: Http,
    /// One install or uninstall at a time: they share `installed.json`.
    busy: Mutex<()>,
    /// How many theme items the last `index.json` read listed (they are
    /// not the extensions' business), and whether `themes.json` was there
    /// the last time it was asked for: together they say when the log
    /// should note that those items are ignored.
    index_theme_items: AtomicUsize,
    themes_file_seen: AtomicBool,
}

impl Market {
    /// A client for these folders; `core_version` is checked against each
    /// item's `minCoreVersion`.
    #[must_use]
    pub fn new(dirs: Dirs, core_version: &str) -> Self {
        Self {
            dirs,
            core_version: core_version.to_owned(),
            http: Http::default(),
            busy: Mutex::new(()),
            index_theme_items: AtomicUsize::new(0),
            themes_file_seen: AtomicBool::new(false),
        }
    }

    /// The folders.
    #[must_use]
    pub fn dirs(&self) -> &Dirs {
        &self.dirs
    }

    /// Reads the index at `source` (`index.json`): from disk, or from the
    /// web (with the cache in the marketplace folder), whole. Use
    /// [`Index::only`] for the extensions alone. Blocking.
    pub fn fetch(&self, source: &Source, allow_insecure: bool) -> Result<Index, MarketError> {
        let index = index::fetch(
            source,
            Catalogue::Extensions,
            &self.dirs.market,
            &self.http,
            allow_insecure,
        )?
        .ok_or_else(|| MarketError::market("the index is missing"))?;
        let themes = index
            .items
            .iter()
            .filter(|item| item.kind == ExtensionKind::Theme)
            .count();
        self.index_theme_items.store(themes, Ordering::Relaxed);
        if themes > 0 && self.themes_file_seen.load(Ordering::Relaxed) {
            note_ignored_themes(themes);
        }
        Ok(index)
    }

    /// Reads the themes catalogue (ADR 0022, the transition rule): the
    /// theme items of `themes.json` at `themes`. While that file is not
    /// there (a 404 on the web, no such file on this machine), the theme
    /// items of the `index.json` at `index` take its place, with one log
    /// line. When `themes.json` is there, the theme items of `index.json`
    /// are ignored. Items of another kind in `themes.json` are left out.
    /// Any other failure (no network, a damaged file) is an error. `index`
    /// gives the index's address, and is asked only when the fallback needs
    /// it. Blocking.
    pub fn fetch_themes(
        &self,
        themes: &Source,
        index: impl FnOnce() -> Result<Source, MarketError>,
        allow_insecure: bool,
    ) -> Result<Index, MarketError> {
        let own = index::fetch(
            themes,
            Catalogue::Themes,
            &self.dirs.market,
            &self.http,
            allow_insecure,
        )?;
        self.themes_file_seen
            .store(own.is_some(), Ordering::Relaxed);
        if let Some(own) = own {
            let kept = own.only(Catalogue::Themes);
            if kept.items.len() < own.items.len() {
                tracing::warn!(
                    left_out = own.items.len() - kept.items.len(),
                    source = %own.source,
                    "the themes catalogue lists items that are not themes; they were left out"
                );
            }
            let ignored = self.index_theme_items.load(Ordering::Relaxed);
            if ignored > 0 {
                note_ignored_themes(ignored);
            }
            return Ok(kept);
        }
        let legacy = self
            .fetch(&index()?, allow_insecure)?
            .only(Catalogue::Themes);
        tracing::info!(
            themes = legacy.items.len(),
            source = %legacy.source,
            "themes.json is not there; the theme items of index.json are used instead"
        );
        Ok(legacy)
    }

    /// The item to install: `version` of `id`, or without a version the
    /// newest one this core can run.
    pub fn choose<'a>(
        &self,
        index: &'a Index,
        id: &str,
        version: Option<&str>,
    ) -> Result<&'a MarketItem, MarketError> {
        let versions: Vec<&MarketItem> = index.items.iter().filter(|item| item.id == id).collect();
        if versions.is_empty() {
            return Err(MarketError::new(
                ErrorCode::NoSuchExtension,
                format!("the index has no extension `{id}`"),
            ));
        }
        let core = parse_version(&self.core_version);
        let runs_here = |item: &MarketItem| parse_version(&item.min_core_version) <= core;
        let too_new = |item: &MarketItem| {
            MarketError::new(
                ErrorCode::Incompatible,
                format!(
                    "{id} {} needs CabinetOS {} or newer; this is {}",
                    item.version, item.min_core_version, self.core_version
                ),
            )
        };
        if let Some(version) = version {
            let Some(item) = versions.iter().find(|item| item.version == version) else {
                let known: Vec<&str> = versions.iter().map(|item| item.version.as_str()).collect();
                return Err(MarketError::new(
                    ErrorCode::NoSuchExtension,
                    format!(
                        "the index has no version {version} of `{id}`; it has {}",
                        known.join(", ")
                    ),
                ));
            };
            return if runs_here(item) {
                Ok(item)
            } else {
                Err(too_new(item))
            };
        }
        if let Some(newest) = versions
            .iter()
            .filter(|item| runs_here(item))
            .max_by_key(|item| parse_version(&item.version))
        {
            return Ok(newest);
        }
        let easiest = versions
            .iter()
            .min_by_key(|item| parse_version(&item.min_core_version))
            .expect("there is at least one version");
        Err(too_new(easiest))
    }

    /// Everything installed from the marketplace, by ID.
    #[must_use]
    pub fn installed(&self) -> BTreeMap<String, Installed> {
        read_record(&self.dirs.market)
    }

    /// What a client is offered of `items`: one item per extension, the
    /// newest version this core can run (an extension none of whose
    /// versions it can run is left out), in the order the index first
    /// lists each, with the version installed from the marketplace, if
    /// any. Reads the record of installs.
    #[must_use]
    pub fn offers(&self, items: &[MarketItem]) -> Vec<MarketItem> {
        let core = parse_version(&self.core_version);
        let installed = self.installed();
        let mut offered: Vec<MarketItem> = Vec::new();
        for item in items {
            if parse_version(&item.min_core_version) > core {
                continue;
            }
            match offered.iter_mut().find(|known| known.id == item.id) {
                Some(known) if parse_version(&item.version) > parse_version(&known.version) => {
                    known.clone_from(item);
                }
                Some(_) => {}
                None => offered.push(item.clone()),
            }
        }
        for item in &mut offered {
            item.installed_version = installed.get(&item.id).map(|record| record.version.clone());
        }
        offered
    }

    /// Every Tool Extension in the tools folder.
    #[must_use]
    pub fn tools(&self) -> Vec<ToolInfo> {
        tools::list_tools(&self.dirs.tools)
    }

    /// Installs `item` of `index`: downloads it (or copies it from disk)
    /// into a temporary file, checks its SHA-256, unpacks it into a staging
    /// folder, checks it as its kind needs (`check_plugin` checks a plugin's
    /// folder the way the plugin host will), and puts its files in place.
    /// `progress` hears the bytes downloaded, the total, and whether the
    /// download is complete. Nothing is run. Blocking.
    pub fn install(
        &self,
        index: &Index,
        item: &MarketItem,
        allow_insecure: bool,
        check_plugin: &dyn Fn(&Path) -> Result<(), String>,
        progress: &mut dyn FnMut(u64, u64, bool),
    ) -> Result<Installed, MarketError> {
        let _busy = self.lock();
        let mut record = read_record(&self.dirs.market);
        let previous = record.get(&item.id).cloned();
        self.check_target(item, previous.as_ref())?;
        let work = Work::new(&self.dirs.market, item)?;
        let placed = (|| {
            self.download(index, item, allow_insecure, &work.download, progress)?;
            let staged = unpack(item, &work.download, &work.staging)?;
            check_staged(item, &work.staging, &staged, check_plugin)?;
            self.place(item, &work.staging, &staged, previous.as_ref())
        })();
        work.clean();
        let installed = Installed {
            kind: item.kind,
            version: item.version.clone(),
            sha256: item.download.sha256.to_ascii_lowercase(),
            source: index.source.clone(),
            installed_at_ms: now_ms(),
            files: placed?,
        };
        record.insert(item.id.clone(), installed.clone());
        write_record(&self.dirs.market, &record).map_err(|error| {
            MarketError::market(format!(
                "{} {} is in place, but the record of installs cannot be written ({error}), so uninstalling it will not find it",
                item.id, item.version
            ))
        })?;
        tracing::info!(
            id = %item.id,
            version = %item.version,
            files = installed.files.len(),
            "extension installed"
        );
        Ok(installed)
    }

    /// Removes exactly the files the install of `id` put in place, and the
    /// folders that leaves empty; nothing else. `before` runs first, with
    /// what was installed: it may refuse (the theme in effect) or prepare
    /// (stop the plugin). Blocking.
    pub fn uninstall(
        &self,
        id: &str,
        before: impl FnOnce(&Installed) -> Result<(), MarketError>,
    ) -> Result<Installed, MarketError> {
        let _busy = self.lock();
        let mut record = read_record(&self.dirs.market);
        let Some(installed) = record.get(id).cloned() else {
            return Err(MarketError::new(
                ErrorCode::NoSuchExtension,
                format!(
                    "`{id}` was not installed from the marketplace; the marketplace removes only what it installed"
                ),
            ));
        };
        before(&installed)?;
        let failed = remove_files(self.root(installed.kind), &installed.files);
        match record.get_mut(id) {
            Some(entry) if !failed.is_empty() => {
                entry.files = failed.iter().map(|(file, _)| file.clone()).collect();
            }
            _ => {
                record.remove(id);
            }
        }
        write_record(&self.dirs.market, &record).map_err(|error| {
            MarketError::market(format!(
                "`{id}` was removed, but the record of installs cannot be written: {error}"
            ))
        })?;
        if let Some((file, error)) = failed.first() {
            return Err(MarketError::market(format!(
                "cannot remove {file} of `{id}` ({error}); {} file(s) stay, uninstall again to retry",
                failed.len()
            )));
        }
        tracing::info!(id, files = installed.files.len(), "extension uninstalled");
        Ok(installed)
    }

    /// One install or uninstall at a time: in this process, and among the
    /// cores that share the marketplace folder (two windows), which read
    /// and write the same `installed.json`.
    fn lock(&self) -> Busy<'_> {
        let process = self.busy.lock().unwrap_or_else(PoisonError::into_inner);
        let cores = lock_beside(&self.dirs.market.join(RECORD_FILE))
            .map_err(|error| {
                tracing::warn!(
                    %error,
                    "cannot lock the record of installs; another core may write it at the same time"
                );
            })
            .ok();
        Busy {
            _process: process,
            _cores: cores,
        }
    }

    /// The folder of a kind.
    fn root(&self, kind: ExtensionKind) -> &Path {
        match kind {
            ExtensionKind::Plugin => &self.dirs.plugins,
            ExtensionKind::Theme => &self.dirs.themes,
            ExtensionKind::Tool => &self.dirs.tools,
        }
    }

    /// Refuses to put files where something the marketplace did not install
    /// is, or where it installed an extension of another kind.
    fn check_target(
        &self,
        item: &MarketItem,
        previous: Option<&Installed>,
    ) -> Result<(), MarketError> {
        if let Some(previous) = previous {
            if previous.kind != item.kind {
                return Err(MarketError::market(format!(
                    "`{}` is installed as a {}; uninstall it first",
                    item.id,
                    kind_name(previous.kind)
                )));
            }
            return Ok(());
        }
        let target = match item.kind {
            ExtensionKind::Theme => self.dirs.themes.join(format!("{}.json", item.id)),
            kind => self.root(kind).join(&item.id),
        };
        if target.try_exists().unwrap_or(true) {
            return Err(MarketError::new(
                ErrorCode::AlreadyExists,
                format!(
                    "{} is there already and was not installed from the marketplace; the marketplace does not replace it",
                    target.display()
                ),
            ));
        }
        Ok(())
    }

    /// Downloads (or copies) the item into `to` and checks its size and
    /// SHA-256.
    fn download(
        &self,
        index: &Index,
        item: &MarketItem,
        allow_insecure: bool,
        to: &Path,
        progress: &mut dyn FnMut(u64, u64, bool),
    ) -> Result<(), MarketError> {
        let failed = |problem: String| {
            MarketError::market(format!(
                "cannot download {} {}: {problem}",
                item.id, item.version
            ))
        };
        let location = index.locate(&item.download.url, allow_insecure)?;
        let limit = item.size.min(MAX_DOWNLOAD);
        progress(0, item.size, false);
        let downloaded = transfer::download(
            &self.http,
            &location,
            to,
            limit,
            allow_insecure,
            Client::Market,
            &mut |done| {
                progress(done, item.size, false);
                true
            },
        )
        .map_err(|error| match error {
            DownloadError::TooLarge => MarketError::market(format!(
                "the download of {} {} is larger than the {} bytes the index gives; it was deleted and nothing was installed",
                item.id, item.version, item.size
            )),
            DownloadError::Failed(problem) => failed(problem),
        })?;
        progress(downloaded.bytes, item.size, true);
        let hash = downloaded.sha256;
        if !hash.eq_ignore_ascii_case(&item.download.sha256) {
            return Err(MarketError::new(
                ErrorCode::HashMismatch,
                format!(
                    "the download of {} {} has the SHA-256 {hash}, but the index gives {}; it was deleted and nothing was installed",
                    item.id,
                    item.version,
                    item.download.sha256.to_ascii_lowercase()
                ),
            ));
        }
        Ok(())
    }

    /// Copies the staged files into place. A fresh install that fails half
    /// way removes what it copied; an update also removes the files of the
    /// old version that the new one does not have.
    fn place(
        &self,
        item: &MarketItem,
        staging: &Path,
        staged: &[String],
        previous: Option<&Installed>,
    ) -> Result<Vec<String>, MarketError> {
        let root = self.root(item.kind);
        let prefix = match item.kind {
            ExtensionKind::Theme => String::new(),
            _ => format!("{}/", item.id),
        };
        let mut placed: Vec<String> = Vec::new();
        for relative in staged {
            let recorded = format!("{prefix}{relative}");
            let copied = match (relative_path(relative), relative_path(&recorded)) {
                (Some(from), Some(to)) => copy_replacing(&staging.join(from), &root.join(to))
                    .map_err(|error| format!("cannot put {recorded} in place: {error}")),
                _ => Err(format!("cannot record the file name `{relative}`")),
            };
            if let Err(problem) = copied {
                if previous.is_none() {
                    remove_files(root, &placed);
                }
                return Err(MarketError::market(format!(
                    "{} {}: {problem}",
                    item.id, item.version
                )));
            }
            placed.push(recorded);
        }
        if let Some(previous) = previous {
            let stale: Vec<String> = previous
                .files
                .iter()
                .filter(|file| !placed.contains(file))
                .cloned()
                .collect();
            for (file, error) in remove_files(root, &stale) {
                tracing::warn!(%file, %error, "cannot remove a file of the previous version");
            }
        }
        Ok(placed)
    }
}

/// The download and the staging folder of one install.
struct Work {
    download: PathBuf,
    staging: PathBuf,
}

impl Work {
    fn new(market: &Path, item: &MarketItem) -> Result<Self, MarketError> {
        let downloads = market.join("downloads");
        let staging = market.join("staging").join(&item.id);
        // What an install cut short (the core was stopped) left behind.
        let _ = fs::remove_dir_all(&staging);
        fs::create_dir_all(&downloads)
            .and_then(|()| fs::create_dir_all(&staging))
            .map_err(|error| {
                MarketError::market(format!("cannot prepare {}: {error}", market.display()))
            })?;
        Ok(Self {
            download: downloads.join(format!("{}-{}.download", item.id, item.version)),
            staging,
        })
    }

    fn clean(&self) {
        let _ = fs::remove_file(&self.download);
        let _ = fs::remove_dir_all(&self.staging);
    }
}

/// Puts the download's files into `staging`: a theme as `<id>.json`; a
/// plugin from its zip, or its component alone with `plugin.json` from the
/// item's manifest; a tool from its zip. Returns the files, relative to
/// `staging`, with `/` between names.
fn unpack(item: &MarketItem, download: &Path, staging: &Path) -> Result<Vec<String>, MarketError> {
    let not_valid = |problem: String| {
        MarketError::market(format!(
            "the download of {} {} {problem}",
            item.id, item.version
        ))
    };
    let io_failed = |error: io::Error| not_valid(format!("cannot be unpacked: {error}"));
    let magic = first_bytes(download).map_err(io_failed)?;
    match item.kind {
        ExtensionKind::Theme => {
            let text = fs::read_to_string(download)
                .map_err(|_| not_valid("is not UTF-8 text".to_owned()))?;
            let theme = cabinetos_themes::parse(&text, Some(&item.id))
                .map_err(|problem| not_valid(format!("is not a valid theme: {problem}")))?;
            if theme.version != item.version {
                return Err(not_valid(format!(
                    "is version {} of the theme",
                    theme.version
                )));
            }
            let name = format!("{}.json", item.id);
            fs::copy(download, staging.join(&name)).map_err(io_failed)?;
            Ok(vec![name])
        }
        ExtensionKind::Plugin if magic == *b"\0asm" => {
            fs::copy(download, staging.join(PLUGIN_COMPONENT)).map_err(io_failed)?;
            let manifest = serde_json::to_string_pretty(&item.manifest)
                .map_err(|error| not_valid(format!("has no usable manifest: {error}")))?;
            fs::write(staging.join(PLUGIN_MANIFEST), manifest).map_err(io_failed)?;
            Ok(vec![
                PLUGIN_MANIFEST.to_owned(),
                PLUGIN_COMPONENT.to_owned(),
            ])
        }
        ExtensionKind::Plugin | ExtensionKind::Tool if magic == *b"PK\x03\x04" => {
            transfer::extract_zip(
                download,
                staging,
                ZipLimits {
                    entries: MAX_ZIP_ENTRIES,
                    bytes: MAX_UNPACKED,
                },
            )
            .map_err(not_valid)
        }
        ExtensionKind::Plugin => Err(not_valid(
            "is neither a zip nor a WebAssembly component".to_owned(),
        )),
        ExtensionKind::Tool => Err(not_valid("is not a zip".to_owned())),
    }
}

/// The first four bytes of a file (fewer when it is shorter).
fn first_bytes(path: &Path) -> io::Result<[u8; 4]> {
    let mut magic = [0; 4];
    let mut file = File::open(path)?;
    let mut filled = 0;
    while filled < magic.len() {
        let read = file.read(&mut magic[filled..])?;
        if read == 0 {
            break;
        }
        filled += read;
    }
    Ok(magic)
}

/// Checks the staged files as the kind needs.
fn check_staged(
    item: &MarketItem,
    staging: &Path,
    staged: &[String],
    check_plugin: &dyn Fn(&Path) -> Result<(), String>,
) -> Result<(), MarketError> {
    let not_valid = |problem: String| {
        MarketError::market(format!(
            "{} {} cannot be installed: {problem}",
            item.id, item.version
        ))
    };
    match item.kind {
        // Parsed and checked while unpacking.
        ExtensionKind::Theme => Ok(()),
        ExtensionKind::Plugin => {
            for needed in [PLUGIN_MANIFEST, PLUGIN_COMPONENT] {
                if !staged.iter().any(|file| file == needed) {
                    return Err(not_valid(format!("it has no {needed}")));
                }
            }
            check_plugin(staging).map_err(&not_valid)?;
            let manifest: Value = fs::read(staging.join(PLUGIN_MANIFEST))
                .ok()
                .and_then(|bytes| serde_json::from_slice(&bytes).ok())
                .ok_or_else(|| not_valid("its plugin.json cannot be read".to_owned()))?;
            if manifest["version"].as_str() != Some(item.version.as_str()) {
                return Err(not_valid(format!(
                    "its plugin.json says version {}",
                    manifest["version"]
                )));
            }
            let asked: BTreeSet<&str> = manifest["capabilities"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|capability| capability["name"].as_str())
                .collect();
            let listed: BTreeSet<&str> = item
                .capabilities
                .iter()
                .map(|capability| capability.name.as_str())
                .collect();
            if asked != listed {
                return Err(not_valid(format!(
                    "it asks for {}, but the index lists {}",
                    names(&asked),
                    names(&listed)
                )));
            }
            Ok(())
        }
        ExtensionKind::Tool => {
            if !staged.iter().any(|file| file == TOOL_MANIFEST_FILE) {
                return Err(not_valid(format!("it has no {TOOL_MANIFEST_FILE}")));
            }
            let manifest = tools::read_manifest(staging).map_err(&not_valid)?;
            if manifest.id != item.id || manifest.version != item.version {
                return Err(not_valid(format!(
                    "its tool.json names {} {}",
                    manifest.id, manifest.version
                )));
            }
            Ok(())
        }
    }
}

fn names(set: &BTreeSet<&str>) -> String {
    if set.is_empty() {
        "nothing".to_owned()
    } else {
        set.iter().copied().collect::<Vec<_>>().join(", ")
    }
}

fn kind_name(kind: ExtensionKind) -> &'static str {
    match kind {
        ExtensionKind::Plugin => "plugin",
        ExtensionKind::Theme => "theme",
        ExtensionKind::Tool => "tool",
    }
}

/// A recorded path (`/` between names) as a path, if every part is a plain
/// name: a record can never make the core touch a file outside the kind's
/// folder.
fn relative_path(text: &str) -> Option<PathBuf> {
    let parts: Vec<&str> = text.split('/').collect();
    let plain = parts.iter().all(|part| {
        !part.is_empty() && *part != "." && *part != ".." && !part.contains(['\\', ':'])
    });
    plain.then(|| parts.iter().collect())
}

/// Copies `from` over `to` through a temporary file and a rename, so
/// whoever reads `to` (the themes watcher) never sees half a file.
fn copy_replacing(from: &Path, to: &Path) -> io::Result<()> {
    if let Some(parent) = to.parent() {
        fs::create_dir_all(parent)?;
    }
    let name = to
        .file_name()
        .map_or_else(String::new, |name| name.to_string_lossy().into_owned());
    let temporary = to.with_file_name(format!(".{name}.{}.tmp", std::process::id()));
    let result = fs::copy(from, &temporary).and_then(|_| fs::rename(&temporary, to));
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

/// Removes `files` (relative to `root`, with `/`) and then the folders they
/// leave empty. A file that is already gone counts as removed. Returns the
/// files that could not be removed.
fn remove_files(root: &Path, files: &[String]) -> Vec<(String, io::Error)> {
    let mut failed = Vec::new();
    let mut folders = BTreeSet::new();
    for file in files {
        let Some(relative) = relative_path(file) else {
            tracing::warn!(%file, "a recorded file that is not a plain relative path is left alone");
            continue;
        };
        match fs::remove_file(root.join(&relative)) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => {
                failed.push((file.clone(), error));
                continue;
            }
        }
        let mut parent = relative.parent();
        while let Some(folder) = parent.filter(|folder| !folder.as_os_str().is_empty()) {
            folders.insert(folder.to_path_buf());
            parent = folder.parent();
        }
    }
    let mut folders: Vec<PathBuf> = folders.into_iter().collect();
    folders.sort_by_key(|folder| std::cmp::Reverse(folder.components().count()));
    for folder in folders {
        // Fails while the folder holds something the install did not put
        // there; that stays.
        let _ = fs::remove_dir(root.join(folder));
    }
    failed
}

/// The locks an install or uninstall holds until it is dropped.
struct Busy<'a> {
    _process: MutexGuard<'a, ()>,
    /// The lock file next to `installed.json`; `None` when it could not be
    /// taken (the install goes on, as before cores could share a folder).
    _cores: Option<fs::File>,
}

/// Locks `.<name>.lock` next to `path` for this process until the returned
/// file is dropped, waiting while another process holds it. Windows
/// releases the lock when a process ends, however it ends. The lock file
/// stays, empty: removing it could race a process that is about to lock it.
fn lock_beside(path: &Path) -> io::Result<fs::File> {
    let name = path.file_name().map_or_else(
        || RECORD_FILE.into(),
        |name| name.to_string_lossy().into_owned(),
    );
    let lock = path.with_file_name(format!(".{name}.lock"));
    if let Some(dir) = lock.parent() {
        fs::create_dir_all(dir)?;
    }
    let file = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(lock)?;
    file.lock()?;
    Ok(file)
}

fn read_record(market: &Path) -> BTreeMap<String, Installed> {
    let path = market.join(RECORD_FILE);
    match fs::read(&path) {
        Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_else(|error| {
            tracing::warn!(
                path = %path.display(),
                %error,
                "the record of installs cannot be read; the marketplace treats nothing as installed by it"
            );
            BTreeMap::new()
        }),
        Err(error) if error.kind() == io::ErrorKind::NotFound => BTreeMap::new(),
        Err(error) => {
            tracing::warn!(path = %path.display(), %error, "cannot read the record of installs");
            BTreeMap::new()
        }
    }
}

fn write_record(market: &Path, record: &BTreeMap<String, Installed>) -> io::Result<()> {
    fs::create_dir_all(market)?;
    let mut text = serde_json::to_string_pretty(record).map_err(io::Error::other)?;
    text.push('\n');
    let path = market.join(RECORD_FILE);
    let temporary = market.join(format!(".{RECORD_FILE}.{}.tmp", std::process::id()));
    let result = fs::write(&temporary, text).and_then(|()| fs::rename(&temporary, &path));
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}
