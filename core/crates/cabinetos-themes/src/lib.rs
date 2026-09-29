//! Colour themes (`docs/themes.md`): the JSON theme format, the themes
//! folder `%LOCALAPPDATA%\CabinetOS\themes`, and the five themes that ship
//! with the core.
//!
//! - [`parse`] reads a theme file strictly: an unknown key, a missing key, a
//!   colour that is not `#RRGGBB` or `#RRGGBBAA`, a metric outside its
//!   bounds, or an ID that is not the file's name refuses the whole theme
//!   with a message that names the problem. A theme is never applied
//!   half-way.
//! - [`ThemeFolder`] owns the folder: opening it writes each shipped theme
//!   whose file is missing, and the schema for editors; it lists and loads
//!   themes by ID.
//!
//! Serves Constitution Article 6 (Universal Configuration: a theme is a
//! file the user may edit, and the edit applies at once) and Article 8
//! (Sandboxed Extensibility: the JSON-based theme engine).
#![forbid(unsafe_code)]

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fmt;
use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;

use cabinetos_protocol::{Metrics, Theme, ThemeInfo, extension_id_problem};

/// Environment variable naming the themes folder, when no folder is given
/// on the command line.
pub const THEMES_DIR_ENV: &str = "CABINETOS_THEMES_DIR";

/// The theme `ui.theme` names by default, and the one in effect while the
/// configured one cannot be used.
pub const DEFAULT_THEME: &str = "default";

/// The density preset of the default theme (`docs/design/compact/`): the
/// same colours, Total Commander's sizes.
pub const COMMANDER_COMPACT: &str = "commander-compact";

/// The file name of the schema kept next to the themes.
pub const SCHEMA_FILE_NAME: &str = "theme.schema.json";

/// The record, in the themes folder, of every shipped theme file the core
/// wrote there: each ID with the SHA-256 of each version.
pub const SHIPPED_RECORD: &str = ".shipped.json";

/// The JSON Schema of a theme file, as `sdk/themes/theme.schema.json` has it.
pub const SCHEMA_JSON: &str = include_str!("../../../../sdk/themes/theme.schema.json");

/// The themes that ship with the core: each ID with its file, as
/// `sdk/themes/` has them. The default is first.
pub const SHIPPED: [(&str, &str); 5] = [
    (
        DEFAULT_THEME,
        include_str!("../../../../sdk/themes/default.json"),
    ),
    ("nord", include_str!("../../../../sdk/themes/nord.json")),
    (
        "catppuccin-mocha",
        include_str!("../../../../sdk/themes/catppuccin-mocha.json"),
    ),
    (
        "rose-pine-moon",
        include_str!("../../../../sdk/themes/rose-pine-moon.json"),
    ),
    (
        COMMANDER_COMPACT,
        include_str!("../../../../sdk/themes/commander-compact.json"),
    ),
];

/// Versions of the shipped themes from before the record existed, by
/// SHA-256 of the file as the core wrote it: a copy that matches one is
/// unedited, and follows the shipped version.
const EARLIER_SHIPPED: [(&str, &str); 1] = [(
    // default 1.0.0, before it followed Windows' light or dark mode.
    DEFAULT_THEME,
    "789d478e245cb5c8004bfcb38221c29c2c0133185c8989ca4ce8a63eac0a7e69",
)];

/// How often, and how far apart, a read is retried while an editor holds
/// the file open without sharing it.
const SHARING_RETRIES: u32 = 10;
const SHARING_RETRY_DELAY: Duration = Duration::from_millis(20);

/// `ERROR_SHARING_VIOLATION`.
const SHARING_VIOLATION: i32 = 32;

/// The themes folder: `explicit` wins, then [`THEMES_DIR_ENV`], then
/// `%LOCALAPPDATA%\CabinetOS\themes`.
#[must_use]
pub fn themes_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(
        explicit,
        std::env::var_os(THEMES_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
    )
}

/// Without `LOCALAPPDATA` (not a normal Windows session) the default is
/// under the temp folder, as for the logs.
fn resolve_dir(
    explicit: Option<PathBuf>,
    env_dir: Option<OsString>,
    local_app_data: Option<OsString>,
) -> PathBuf {
    if let Some(dir) = explicit {
        return dir;
    }
    if let Some(dir) = env_dir.filter(|dir| !dir.is_empty()) {
        return PathBuf::from(dir);
    }
    local_app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join("themes")
}

/// Why a theme cannot be used.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ThemeError {
    /// There is no such theme: no `<id>.json`, or an ID that cannot name
    /// a file.
    NotFound(String),
    /// The file is there, but it cannot be read or is not a valid theme.
    Invalid(String),
}

impl fmt::Display for ThemeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotFound(message) | Self::Invalid(message) => f.write_str(message),
        }
    }
}

impl std::error::Error for ThemeError {}

/// Reads the text of a theme file strictly. `expected_id` is the ID the
/// file's name gives, when there is a file. A byte-order mark at the start,
/// which Notepad and others may write, is ignored; so is `$schema`, which
/// is for editors.
pub fn parse(text: &str, expected_id: Option<&str>) -> Result<Theme, String> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let mut theme: Theme = serde_json::from_str(text).map_err(|error| error.to_string())?;
    check(&theme, expected_id)?;
    theme.schema = None;
    Ok(theme)
}

/// The checks beyond the shape of the file.
fn check(theme: &Theme, expected_id: Option<&str>) -> Result<(), String> {
    if let Some(problem) = extension_id_problem(&theme.id) {
        return Err(format!("id: {problem}"));
    }
    if let Some(expected) = expected_id
        && expected != theme.id
    {
        return Err(format!(
            "the id `{}` must be the file's name, `{expected}`",
            theme.id
        ));
    }
    for (field, value) in [("name", &theme.name), ("author", &theme.author)] {
        if value.trim().is_empty() {
            return Err(format!("`{field}` is empty"));
        }
    }
    if !is_version(&theme.version) {
        return Err(format!(
            "version `{}` is not major.minor.patch",
            theme.version
        ));
    }
    if let Some(problem) = theme.metrics.as_ref().and_then(Metrics::problem) {
        return Err(problem);
    }
    Ok(())
}

/// `major.minor.patch`, numbers only.
fn is_version(text: &str) -> bool {
    let parts: Vec<&str> = text.split('.').collect();
    parts.len() == 3 && parts.iter().all(|part| part.parse::<u32>().is_ok())
}

/// The default theme as it ships. It is always valid (a test says so), so
/// the core always has a theme to fall back on.
#[must_use]
pub fn default_theme() -> Theme {
    parse(SHIPPED[0].1, Some(DEFAULT_THEME)).expect("the shipped default theme is valid")
}

/// The themes folder: `<id>.json` for each theme.
#[derive(Clone, Debug)]
pub struct ThemeFolder {
    dir: PathBuf,
}

impl ThemeFolder {
    /// Opens the folder, creating it when needed, and keeps the shipped
    /// themes current: a missing one is written; a copy the core wrote and
    /// the user never changed follows the version this core ships; a copy
    /// the user edited stays as it is. [`SHIPPED_RECORD`] remembers the
    /// SHA-256 of every shipped file the core wrote, which is how an
    /// unedited copy is told from an edited one. Also keeps
    /// `theme.schema.json` current for editors. Best effort: what cannot be
    /// written is logged, and the core still has the default theme in
    /// memory.
    pub fn open(dir: PathBuf) -> Self {
        if let Err(error) = std::fs::create_dir_all(&dir) {
            tracing::warn!(dir = %dir.display(), %error, "cannot create the themes folder");
            return Self { dir };
        }
        let mut record = read_record(&dir);
        let before = record.clone();
        for (id, text) in SHIPPED {
            if keep_shipped(&dir, id, text, &record) {
                record
                    .entry(id.to_owned())
                    .or_default()
                    .insert(sha256_hex(text.as_bytes()));
            }
        }
        if record != before
            && let Err(error) = write_record(&dir, &record)
        {
            tracing::warn!(dir = %dir.display(), %error, "cannot write the record of shipped themes");
        }
        let schema = dir.join(SCHEMA_FILE_NAME);
        if std::fs::read(&schema).ok().as_deref() != Some(SCHEMA_JSON.as_bytes())
            && let Err(error) = replace_file(&schema, SCHEMA_JSON.as_bytes())
        {
            tracing::warn!(path = %schema.display(), %error, "cannot write the theme schema");
        }
        Self { dir }
    }

    /// The folder.
    #[must_use]
    pub fn dir(&self) -> &Path {
        &self.dir
    }

    /// The file of the theme `id`.
    #[must_use]
    pub fn path_of(&self, id: &str) -> PathBuf {
        self.dir.join(format!("{id}.json"))
    }

    /// Reads and checks the theme `id`.
    pub fn load(&self, id: &str) -> Result<Theme, ThemeError> {
        if let Some(problem) = extension_id_problem(id) {
            return Err(ThemeError::NotFound(format!(
                "there is no theme with that ID: {problem}"
            )));
        }
        let path = self.path_of(id);
        let text = match read_text(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                return Err(ThemeError::NotFound(format!(
                    "there is no theme `{id}`: {} does not exist",
                    path.display()
                )));
            }
            Err(error) => {
                return Err(ThemeError::Invalid(format!(
                    "cannot read {}: {error}",
                    path.display()
                )));
            }
        };
        parse(&text, Some(id))
            .map_err(|problem| ThemeError::Invalid(format!("{}: {problem}", path.display())))
    }

    /// Every valid theme in the folder, by ID. A file that is not a valid
    /// theme is logged and left out.
    #[must_use]
    pub fn list(&self) -> Vec<ThemeInfo> {
        let entries = match std::fs::read_dir(&self.dir) {
            Ok(entries) => entries,
            Err(error) => {
                tracing::warn!(dir = %self.dir.display(), %error, "cannot list the themes folder");
                return Vec::new();
            }
        };
        let mut themes = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            let Some(id) = path
                .file_name()
                .and_then(|name| name.to_str())
                .and_then(|name| name.strip_suffix(".json"))
            else {
                continue;
            };
            // `.shipped.json` and other dotfiles are the core's own.
            if id.ends_with(".schema") || id.starts_with('.') || !path.is_file() {
                continue;
            }
            match self.load(id) {
                Ok(theme) => themes.push(ThemeInfo::from(&theme)),
                Err(error) => tracing::warn!(%error, "not a valid theme; left out of the list"),
            }
        }
        themes.sort_by(|a, b| a.id.cmp(&b.id));
        themes
    }
}

/// Brings the shipped theme `id` up to `text` unless the user edited it.
/// Returns whether the folder now holds `text`, so it is recorded.
fn keep_shipped(
    dir: &Path,
    id: &str,
    text: &str,
    record: &BTreeMap<String, BTreeSet<String>>,
) -> bool {
    let path = dir.join(format!("{id}.json"));
    let shipped = sha256_hex(text.as_bytes());
    let found = match std::fs::read(&path) {
        Ok(bytes) => sha256_hex(&bytes),
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            return match replace_file(&path, text.as_bytes()) {
                Ok(()) => {
                    tracing::info!(path = %path.display(), "wrote a shipped theme");
                    true
                }
                Err(error) => {
                    tracing::warn!(path = %path.display(), %error, "cannot write a shipped theme");
                    false
                }
            };
        }
        Err(error) => {
            tracing::warn!(path = %path.display(), %error, "cannot read a shipped theme");
            return false;
        }
    };
    if found == shipped {
        return true;
    }
    let unedited = record.get(id).is_some_and(|hashes| hashes.contains(&found))
        || EARLIER_SHIPPED
            .iter()
            .any(|(earlier, hash)| *earlier == id && *hash == found);
    if !unedited {
        tracing::info!(
            path = %path.display(),
            "keeping the edited copy of a shipped theme; this core's version of it is not written"
        );
        return false;
    }
    match replace_file(&path, text.as_bytes()) {
        Ok(()) => {
            tracing::info!(path = %path.display(), "updated an unedited shipped theme to the version this core ships");
            true
        }
        Err(error) => {
            tracing::warn!(path = %path.display(), %error, "cannot update a shipped theme");
            false
        }
    }
}

/// The record of shipped theme files the core wrote. A record that cannot
/// be read counts as empty: every copy that differs from this core's
/// version is then kept, as an edit.
fn read_record(dir: &Path) -> BTreeMap<String, BTreeSet<String>> {
    let path = dir.join(SHIPPED_RECORD);
    match std::fs::read(&path) {
        Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_else(|error| {
            tracing::warn!(path = %path.display(), %error, "the record of shipped themes cannot be read");
            BTreeMap::new()
        }),
        Err(_) => BTreeMap::new(),
    }
}

fn write_record(dir: &Path, record: &BTreeMap<String, BTreeSet<String>>) -> io::Result<()> {
    let mut text = serde_json::to_string_pretty(record).map_err(io::Error::other)?;
    text.push('\n');
    replace_file(&dir.join(SHIPPED_RECORD), text.as_bytes())
}

/// Replaces `path` through a temporary file and a rename, so the themes
/// watcher and a second core never read half a file.
fn replace_file(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let name = path
        .file_name()
        .map_or_else(String::new, |name| name.to_string_lossy().into_owned());
    let temporary = path.with_file_name(format!(".{name}.{}.tmp", std::process::id()));
    let result = std::fs::write(&temporary, bytes).and_then(|()| std::fs::rename(&temporary, path));
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

fn sha256_hex(bytes: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    Sha256::digest(bytes)
        .iter()
        .fold(String::with_capacity(64), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

/// Reads a whole text file. Retries for a short while when an editor holds
/// it open without sharing.
fn read_text(path: &Path) -> io::Result<String> {
    let mut attempts = 0;
    loop {
        match std::fs::read_to_string(path) {
            Err(error)
                if error.raw_os_error() == Some(SHARING_VIOLATION)
                    && attempts < SHARING_RETRIES =>
            {
                attempts += 1;
                std::thread::sleep(SHARING_RETRY_DELAY);
            }
            result => return result,
        }
    }
}

#[cfg(test)]
mod tests {
    use std::fs;

    use cabinetos_protocol::{METRICS, Opacity, ThemeKind};
    use serde_json::{Value, json};

    use super::*;

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-core-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("themes")
            .tempdir_in(root)
            .unwrap()
    }

    fn shipped(id: &str) -> Theme {
        let (_, text) = SHIPPED.iter().find(|(shipped, _)| *shipped == id).unwrap();
        parse(text, Some(id)).unwrap()
    }

    #[test]
    fn folders_come_from_the_flag_then_the_variable_then_local_app_data() {
        let local = Some(OsString::from(r"C:\Users\me\AppData\Local"));
        assert_eq!(
            resolve_dir(Some(PathBuf::from(r"C:\flag")), None, local.clone()),
            PathBuf::from(r"C:\flag")
        );
        assert_eq!(
            resolve_dir(None, Some(OsString::from(r"C:\env")), local.clone()),
            PathBuf::from(r"C:\env")
        );
        assert_eq!(
            resolve_dir(None, Some(OsString::new()), local),
            PathBuf::from(r"C:\Users\me\AppData\Local\CabinetOS\themes")
        );
    }

    #[test]
    fn every_shipped_theme_is_valid_and_follows_the_design() {
        for (id, _) in SHIPPED {
            let theme = shipped(id);
            assert_eq!(theme.id, id);
            assert_eq!(theme.schema, None, "{id}: $schema is for editors only");
            // The default and its compact preset follow Windows' light or
            // dark mode; the named palettes are dark ones.
            let kind = if [DEFAULT_THEME, COMMANDER_COMPACT].contains(&id) {
                ThemeKind::System
            } else {
                ThemeKind::Dark
            };
            assert_eq!(theme.kind, kind, "{id}");
            if id != COMMANDER_COMPACT {
                assert_eq!((theme.metrics, theme.chrome), (None, None), "{id}");
            }
        }
        let default = default_theme();
        assert_eq!((default.accent, default.mica), (None, None));
        assert_eq!(default.palette.text_secondary.as_str(), "#FFFFFFC8");
        assert_eq!(default.palette.folder_icon.as_str(), "#F2C063");
        assert_eq!(default.palette.permission_high.as_str(), "#F27A6C");
        for (id, accent, tint, opacity) in [
            ("nord", "#88C0D0", "#2E3440", 0.88),
            ("catppuccin-mocha", "#CBA6F7", "#1E1E2E", 0.9),
            ("rose-pine-moon", "#EBBCBA", "#232136", 0.9),
        ] {
            let theme = shipped(id);
            assert_eq!(theme.accent.unwrap().as_str(), accent, "{id}");
            let mica = theme.mica.unwrap();
            assert_eq!(mica.tint.as_str(), tint, "{id}");
            assert_eq!(mica.opacity, Opacity::new(opacity).unwrap(), "{id}");
            let attribution = theme.attribution.unwrap();
            assert!(attribution.contains("MIT License"), "{id}: {attribution}");
        }
    }

    /// The handout (`docs/design/compact/COMPACT_THEME.md`): the default
    /// theme's colours and terminal, the acrylic tint of its Overlays line,
    /// every metric at its compact value, and all three chrome elements.
    #[test]
    fn commander_compact_is_the_default_look_made_dense() {
        let compact = shipped(COMMANDER_COMPACT);
        let default = default_theme();
        assert_eq!(compact.name, "Commander Compact");
        assert_eq!((&compact.accent, &compact.mica), (&None, &None));
        assert_eq!(compact.terminal, default.terminal);
        assert_eq!(compact.palette.acrylic_tint.as_str(), "#262626E6");
        let mut palette = compact.palette.clone();
        palette.acrylic_tint = default.palette.acrylic_tint.clone();
        assert_eq!(
            palette, default.palette,
            "every other colour is the default's"
        );

        let metrics = compact.metrics.as_ref().unwrap();
        let values = metrics.values();
        assert_eq!(values.len(), METRICS.len(), "every metric is set");
        for (spec, value) in values {
            assert!(
                (value - spec.compact).abs() < 1e-9,
                "{}: {value}, the handout says {}",
                spec.name,
                spec.compact
            );
        }
        assert_eq!(metrics.row_height, Some(20));
        assert_eq!(metrics.font_size, Some(12));
        let chrome = compact.chrome.unwrap();
        assert_eq!(
            (chrome.fkey_bar, chrome.row_stripes, chrome.hairlines),
            (Some(true), Some(true), Some(true))
        );
        assert!(ThemeInfo::from(&compact).has_metrics);
    }

    #[test]
    fn the_embedded_schema_is_the_generated_one() {
        let mut generated =
            serde_json::to_string_pretty(&cabinetos_protocol::schema::theme_schema()).unwrap();
        generated.push('\n');
        assert_eq!(SCHEMA_JSON.replace("\r\n", "\n"), generated);
    }

    /// The default theme's JSON with one change.
    fn changed(change: impl FnOnce(&mut Value)) -> String {
        let mut value: Value = serde_json::from_str(SHIPPED[0].1).unwrap();
        change(&mut value);
        value.to_string()
    }

    #[test]
    fn a_bad_theme_is_refused_with_its_problem() {
        let cases: [(&str, String); 13] = [
            (
                "unknown field `textPrimry`",
                changed(|theme| theme["palette"]["textPrimry"] = json!("#FFFFFF")),
            ),
            (
                "missing field `textDisabled`",
                changed(|theme| {
                    theme["palette"]
                        .as_object_mut()
                        .unwrap()
                        .remove("textDisabled");
                }),
            ),
            (
                "is not a colour",
                changed(|theme| theme["palette"]["layerFill"] = json!("white")),
            ),
            (
                "use #RRGGBB",
                changed(|theme| theme["accent"] = json!("#60CDFF80")),
            ),
            (
                "invalid length 15",
                changed(|theme| {
                    theme["terminal"]["ansi"].as_array_mut().unwrap().pop();
                }),
            ),
            (
                "opacity 1.5 is not from 0 to 1",
                changed(|theme| theme["mica"] = json!({"tint": "#202020", "opacity": 1.5})),
            ),
            (
                "must be the file's name",
                changed(|theme| theme["id"] = json!("other")),
            ),
            (
                "not major.minor.patch",
                changed(|theme| theme["version"] = json!("1.0")),
            ),
            (
                "unknown field `rowHight`",
                changed(|theme| theme["metrics"] = json!({"rowHight": 20})),
            ),
            (
                "metrics.rowHeight: 10 is not from 14 to 80",
                changed(|theme| theme["metrics"] = json!({"rowHeight": 10})),
            ),
            (
                "invalid type: floating point",
                changed(|theme| theme["metrics"] = json!({"rowHeight": 20.5})),
            ),
            (
                "sidebarMaxWidth",
                changed(|theme| theme["metrics"] = json!({"sidebarMinWidth": 300})),
            ),
            (
                "unknown field `stripes`",
                changed(|theme| theme["chrome"] = json!({"stripes": true})),
            ),
        ];
        for (expected, text) in cases {
            let problem = parse(&text, Some(DEFAULT_THEME)).unwrap_err();
            assert!(problem.contains(expected), "{expected}: {problem}");
        }
        let problem = parse(&changed(|theme| theme["name"] = json!(" ")), None).unwrap_err();
        assert!(problem.contains("`name` is empty"), "{problem}");
        let with_bom = format!("\u{feff}{}", SHIPPED[1].1);
        assert_eq!(parse(&with_bom, Some("nord")).unwrap(), shipped("nord"));
    }

    #[test]
    fn opening_writes_what_is_missing_and_keeps_edits() {
        let scratch = scratch();
        let dir = scratch.path().join("themes");
        fs::create_dir_all(&dir).unwrap();
        let edited = changed(|theme| theme["name"] = json!("My Default"));
        fs::write(dir.join("default.json"), &edited).unwrap();

        let folder = ThemeFolder::open(dir.clone());
        assert_eq!(
            fs::read_to_string(dir.join("default.json")).unwrap(),
            edited
        );
        assert_eq!(
            fs::read_to_string(dir.join("nord.json")).unwrap(),
            SHIPPED[1].1
        );
        assert_eq!(
            fs::read_to_string(dir.join(SCHEMA_FILE_NAME)).unwrap(),
            SCHEMA_JSON
        );
        assert_eq!(folder.load("default").unwrap().name, "My Default");

        fs::write(dir.join("broken.json"), "{ not json").unwrap();
        fs::write(dir.join("notes.txt"), "not a theme").unwrap();
        let ids: Vec<String> = folder.list().into_iter().map(|theme| theme.id).collect();
        assert_eq!(
            ids,
            [
                "catppuccin-mocha",
                "commander-compact",
                "default",
                "nord",
                "rose-pine-moon"
            ]
        );
        assert!(matches!(
            folder.load("broken"),
            Err(ThemeError::Invalid(message)) if message.contains("broken.json")
        ));
        assert!(matches!(
            folder.load("nothing"),
            Err(ThemeError::NotFound(_))
        ));
        assert!(matches!(
            folder.load(r"..\themes\nord"),
            Err(ThemeError::NotFound(_))
        ));

        // A deleted shipped theme comes back at the next start.
        fs::remove_file(dir.join("nord.json")).unwrap();
        ThemeFolder::open(dir.clone());
        assert!(dir.join("nord.json").is_file());
    }

    /// `default.json` as the shipped version 1.0.0 was: an older core wrote
    /// this file on every PC it started on.
    const DEFAULT_1_0_0: &str = include_str!("../testdata/default-1.0.0.json");

    /// A theme of the first format, colours only, stays valid: `metrics`
    /// and `chrome` are optional, and absent means the default look.
    #[test]
    fn a_theme_without_metrics_and_chrome_is_still_valid() {
        let old = parse(DEFAULT_1_0_0, Some(DEFAULT_THEME)).unwrap();
        assert_eq!((old.metrics, old.chrome), (None, None));
        assert!(!ThemeInfo::from(&old).has_metrics);
        let explicit_nothing = changed(|theme| {
            theme["metrics"] = json!({});
            theme["chrome"] = json!({});
        });
        let theme = parse(&explicit_nothing, Some(DEFAULT_THEME)).unwrap();
        assert!(theme.metrics.unwrap().is_empty());
        assert!(!ThemeInfo::from(&theme).has_metrics);
    }

    #[test]
    fn the_earlier_versions_are_the_files_that_shipped() {
        assert_eq!(
            EARLIER_SHIPPED,
            [(DEFAULT_THEME, sha256_hex(DEFAULT_1_0_0.as_bytes()).as_str())]
        );
    }

    /// The nord theme's JSON with one change.
    fn changed_nord(change: impl FnOnce(&mut Value)) -> String {
        let mut value: Value = serde_json::from_str(SHIPPED[1].1).unwrap();
        change(&mut value);
        value.to_string()
    }

    /// A PC that ran the core before `default` became 1.1.0 has the 1.0.0
    /// file: untouched, it follows the shipped version; a theme the user
    /// edited stays theirs.
    #[test]
    fn an_unedited_older_shipped_theme_is_updated_and_an_edited_one_kept() {
        let scratch = scratch();
        let dir = scratch.path().join("themes");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("default.json"), DEFAULT_1_0_0).unwrap();
        let edited = changed_nord(|theme| theme["name"] = json!("My Nord"));
        fs::write(dir.join("nord.json"), &edited).unwrap();

        let folder = ThemeFolder::open(dir.clone());
        assert_eq!(
            fs::read_to_string(dir.join("default.json")).unwrap(),
            SHIPPED[0].1,
            "the unedited 1.0.0 became the shipped 1.1.0"
        );
        assert_eq!(folder.load("default").unwrap().version, "1.1.0");
        assert_eq!(
            fs::read_to_string(dir.join("nord.json")).unwrap(),
            edited,
            "the edited theme stays as the user left it"
        );
    }

    /// The record of what the core wrote (`.shipped.json`) recognizes an
    /// unedited copy of any shipped version, also one this core never
    /// shipped itself.
    #[test]
    fn a_copy_the_record_knows_follows_the_shipped_version() {
        let scratch = scratch();
        let dir = scratch.path().join("themes");
        fs::create_dir_all(&dir).unwrap();
        let older = changed_nord(|theme| theme["version"] = json!("0.9.0"));
        fs::write(dir.join("nord.json"), &older).unwrap();
        fs::write(
            dir.join(SHIPPED_RECORD),
            json!({"nord": [sha256_hex(older.as_bytes())]}).to_string(),
        )
        .unwrap();

        let folder = ThemeFolder::open(dir.clone());
        assert_eq!(
            fs::read_to_string(dir.join("nord.json")).unwrap(),
            SHIPPED[1].1
        );
        let record: Value =
            serde_json::from_str(&fs::read_to_string(dir.join(SHIPPED_RECORD)).unwrap()).unwrap();
        for (id, text) in SHIPPED {
            let hashes: Vec<&str> = record[id]
                .as_array()
                .unwrap_or_else(|| panic!("{id} is recorded"))
                .iter()
                .map(|hash| hash.as_str().unwrap())
                .collect();
            assert!(
                hashes.contains(&sha256_hex(text.as_bytes()).as_str()),
                "{id}: {hashes:?}"
            );
        }
        assert_eq!(
            record["nord"].as_array().unwrap().len(),
            2,
            "the old hash stays known"
        );
        let ids: Vec<String> = folder.list().into_iter().map(|theme| theme.id).collect();
        assert_eq!(
            ids,
            [
                "catppuccin-mocha",
                "commander-compact",
                "default",
                "nord",
                "rose-pine-moon"
            ],
            "the record is not a theme"
        );
    }
}
