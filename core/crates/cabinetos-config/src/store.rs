//! The configuration file on disk: reading it, noticing changes, and
//! rewriting it safely.

use std::ffi::OsString;
use std::hash::{DefaultHasher, Hasher};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;

use cabinetos_commands::KeySequence;
use serde_json::Value;

use crate::diff::changed_paths;
use crate::parse::{ConfigError, Rejection, check_values, parse, parse_checked};
use crate::{Config, KeybindingEntry, Keys, SCHEMA_JSON, SCHEMA_REFERENCE};

/// Environment variable with the full path of the configuration file.
pub const CONFIG_ENV: &str = "CABINETOS_CONFIG";

/// The file name of the configuration.
pub const FILE_NAME: &str = "cabinetos.json";

/// The file name of the schema written next to it.
pub const SCHEMA_FILE_NAME: &str = "cabinetos.schema.json";

/// How often, and how far apart, a read is retried while an editor holds the
/// file open without sharing it.
const SHARING_RETRIES: u32 = 10;
const SHARING_RETRY_DELAY: Duration = Duration::from_millis(20);

/// `ERROR_SHARING_VIOLATION`.
const SHARING_VIOLATION: i32 = 32;

/// Picks the configuration file: an explicit path (the core's `--config`)
/// wins, then `CABINETOS_CONFIG`, then `%APPDATA%\CabinetOS\cabinetos.json`.
#[must_use]
pub fn resolve_path(
    explicit: Option<PathBuf>,
    env_path: Option<OsString>,
    app_data: Option<OsString>,
) -> PathBuf {
    if let Some(path) = explicit {
        return path;
    }
    if let Some(path) = env_path.filter(|path| !path.is_empty()) {
        return PathBuf::from(path);
    }
    app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join(FILE_NAME)
}

/// The path this process would use, from its environment.
#[must_use]
pub fn default_path(explicit: Option<PathBuf>) -> PathBuf {
    resolve_path(
        explicit,
        std::env::var_os(CONFIG_ENV),
        std::env::var_os("APPDATA"),
    )
}

/// What opening the file found.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Opened {
    /// The file was read and is in effect.
    Loaded,
    /// There was no file; one with the defaults was written.
    Created,
    /// There was no file and none could be written; the defaults are in
    /// effect.
    NotCreated(String),
    /// The file has an error; the defaults are in effect until it is fixed.
    Invalid(ConfigError),
}

/// What reading the file again found.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Reload {
    /// Same content as the configuration in effect (or an error already
    /// reported): nothing to tell anyone. The core's own writes land here.
    Unchanged,
    /// A new configuration is in effect; these settings changed (possibly
    /// none, when only the formatting did).
    Changed(Vec<String>),
    /// The file cannot be used; the configuration in effect stays.
    Invalid(ConfigError),
}

/// Why an update was not written.
#[derive(Debug)]
pub enum UpdateError<E> {
    /// The file on disk has an error. Rewriting it would throw away the
    /// user's unfinished edit, so the user must fix it first.
    FileHasError(ConfigError),
    /// The change itself was refused.
    Rejected(E),
    /// The file could not be read or written.
    Io(io::Error),
}

impl<E: std::fmt::Display> std::fmt::Display for UpdateError<E> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::FileHasError(error) => write!(
                f,
                "the configuration file has an error to fix first ({error})"
            ),
            Self::Rejected(error) => write!(f, "{error}"),
            Self::Io(error) => write!(f, "cannot write the configuration file: {error}"),
        }
    }
}

/// The configuration file and the settings in effect.
#[derive(Debug)]
pub struct ConfigStore {
    path: PathBuf,
    /// The settings in effect: the last good file, or the defaults.
    config: Config,
    /// Hash of the file content the settings came from, or that the core
    /// wrote itself. The same content read again is not a change.
    applied: Option<u64>,
    /// The file as it was when it last failed, while that error is not
    /// resolved. One bad save is reported once, however many change
    /// notifications it causes.
    failed: Option<Seen>,
}

/// What a read found, to recognize the same failure again.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Seen {
    Missing,
    Content(u64),
}

impl ConfigStore {
    /// Opens the configuration at `path`, creating it with the defaults (and
    /// a `$schema` reference) when it does not exist yet: the file is how
    /// users discover that everything is configurable (Article 6). Also
    /// writes the schema next to it, for editor completion. `validate` is
    /// as for [`reload`](Self::reload).
    pub fn open(
        path: PathBuf,
        validate: impl FnOnce(&Config) -> Result<(), Rejection>,
    ) -> (Self, Opened) {
        let mut store = Self {
            path,
            config: Config::default(),
            applied: None,
            failed: None,
        };
        let opened = match read(&store.path) {
            Ok(Some(bytes)) => match store.adopt(&bytes, validate) {
                Ok(_) => Opened::Loaded,
                Err(error) => Opened::Invalid(error),
            },
            Ok(None) => {
                let config = Config {
                    schema: Some(SCHEMA_REFERENCE.to_owned()),
                    ..Config::default()
                };
                match store.write(&config) {
                    Ok(()) => {
                        store.config = config;
                        Opened::Created
                    }
                    Err(error) => Opened::NotCreated(error.to_string()),
                }
            }
            Err(error) => Opened::Invalid(ConfigError::general(format!(
                "cannot read {}: {error}",
                store.path.display()
            ))),
        };
        store.write_schema();
        (store, opened)
    }

    /// The configuration file.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// The settings in effect.
    #[must_use]
    pub fn config(&self) -> &Config {
        &self.config
    }

    /// Reads the file again after it changed on disk. `validate` gets the
    /// candidate settings (for the checks this crate cannot make, such as
    /// keybinding conflicts) and may refuse them.
    pub fn reload(&mut self, validate: impl FnOnce(&Config) -> Result<(), Rejection>) -> Reload {
        let bytes = match read(&self.path) {
            Ok(Some(bytes)) => bytes,
            Ok(None) => {
                if self.failed == Some(Seen::Missing) {
                    return Reload::Unchanged;
                }
                self.failed = Some(Seen::Missing);
                return Reload::Invalid(ConfigError::general(format!(
                    "{} was deleted; the settings in use stay until it is back",
                    self.path.display()
                )));
            }
            Err(error) => {
                return Reload::Invalid(ConfigError::general(format!(
                    "cannot read {}: {error}",
                    self.path.display()
                )));
            }
        };
        let hash = content_hash(&bytes);
        if Some(hash) == self.applied {
            // Back to the settings in effect: if an error was reported, it
            // is resolved now, and the UI must hear so.
            return if self.failed.take().is_some() {
                Reload::Changed(Vec::new())
            } else {
                Reload::Unchanged
            };
        }
        if self.failed == Some(Seen::Content(hash)) {
            return Reload::Unchanged;
        }
        match self.adopt(&bytes, validate) {
            Ok(changed) => Reload::Changed(changed),
            Err(error) => Reload::Invalid(error),
        }
    }

    /// Parses and validates `bytes`, the file's content, and makes it the
    /// settings in effect. Returns the settings that changed.
    fn adopt(
        &mut self,
        bytes: &[u8],
        validate: impl FnOnce(&Config) -> Result<(), Rejection>,
    ) -> Result<Vec<String>, ConfigError> {
        let hash = content_hash(bytes);
        let candidate = text_of(bytes).and_then(|text| parse_checked(text, validate));
        match candidate {
            Ok(candidate) => {
                let changed = changed_paths(&self.config, &candidate);
                self.config = candidate;
                self.applied = Some(hash);
                self.failed = None;
                Ok(changed)
            }
            Err(error) => {
                self.failed = Some(Seen::Content(hash));
                Err(error)
            }
        }
    }

    /// Changes the settings and rewrites the file. The change applies to what
    /// is on disk now, so an edit the user saved a moment ago is kept; if
    /// that edit has an error, nothing is written. Returns the settings that
    /// changed.
    pub fn update<E>(
        &mut self,
        change: impl FnOnce(&mut Config) -> Result<(), E>,
        validate: impl FnOnce(&Config) -> Result<(), E>,
    ) -> Result<Vec<String>, UpdateError<E>> {
        let (base, base_hash) = match read(&self.path).map_err(UpdateError::Io)? {
            Some(bytes) => {
                let hash = content_hash(&bytes);
                let base = if Some(hash) == self.applied {
                    self.config.clone()
                } else {
                    text_of(&bytes)
                        .and_then(parse)
                        .map_err(UpdateError::FileHasError)?
                };
                (base, Some(hash))
            }
            // Deleted: the change brings the file back.
            None => (self.config.clone(), None),
        };
        let mut updated = base.clone();
        change(&mut updated).map_err(UpdateError::Rejected)?;
        validate(&updated).map_err(UpdateError::Rejected)?;
        if updated == base && base_hash.is_some() {
            // The file already says this; leave the user's formatting alone.
            self.applied = base_hash;
        } else {
            self.write(&updated).map_err(UpdateError::Io)?;
        }
        let changed = changed_paths(&self.config, &updated);
        self.config = updated;
        self.failed = None;
        Ok(changed)
    }

    /// Binds `command` to `keys` (`None`: no binding), replacing the user's
    /// earlier entries for it.
    pub fn set_keybinding<E>(
        &mut self,
        command: &str,
        keys: Option<KeySequence>,
        validate: impl FnOnce(&Config) -> Result<(), E>,
    ) -> Result<Vec<String>, UpdateError<E>> {
        self.update(
            |config| {
                config.keybindings.retain(|entry| entry.command != command);
                config.keybindings.push(KeybindingEntry {
                    command: command.to_owned(),
                    keys: Keys(keys),
                    when: None,
                });
                Ok(())
            },
            validate,
        )
    }

    /// Removes the user's entries for `command`: it gets its default
    /// bindings back.
    pub fn unset_keybinding<E>(
        &mut self,
        command: &str,
        validate: impl FnOnce(&Config) -> Result<(), E>,
    ) -> Result<Vec<String>, UpdateError<E>> {
        self.update(
            |config| {
                config.keybindings.retain(|entry| entry.command != command);
                Ok(())
            },
            validate,
        )
    }

    /// Sets one setting by its dotted path, for example `ui.layout` to
    /// `"rail"`. The path must exist and the value must fit it; the result
    /// must pass the checks a file must pass (the version, the terminal
    /// profiles) and `validate`.
    pub fn set_value(
        &mut self,
        path: &str,
        value: Value,
        validate: impl FnOnce(&Config) -> Result<(), Rejection>,
    ) -> Result<Vec<String>, UpdateError<Rejection>> {
        let refuse = |message: String| Rejection {
            keybinding: None,
            message,
        };
        self.update(
            |config| {
                let mut document =
                    serde_json::to_value(&*config).map_err(|error| refuse(error.to_string()))?;
                let mut slot = &mut document;
                for key in path.split('.') {
                    slot = slot
                        .get_mut(key)
                        .ok_or_else(|| refuse(format!("there is no setting `{path}`")))?;
                }
                *slot = value;
                *config = serde_json::from_value(document)
                    .map_err(|error| refuse(format!("{path}: {error}")))?;
                check_values(config).map_err(refuse)
            },
            validate,
        )
    }

    /// Writes `config` atomically: a temporary file in the same directory,
    /// flushed to disk, then renamed over the old file, which Windows does
    /// with `MOVEFILE_REPLACE_EXISTING`. A reader never sees half a file.
    /// The written content counts as applied, so the watcher does not echo
    /// the core's own write as a change.
    fn write(&mut self, config: &Config) -> io::Result<()> {
        let mut text = serde_json::to_string_pretty(config).map_err(io::Error::other)?;
        text.push('\n');
        write_atomically(&self.path, text.as_bytes())?;
        self.applied = Some(content_hash(text.as_bytes()));
        Ok(())
    }

    /// Keeps `cabinetos.schema.json` next to the file current, for editors.
    /// Best effort: the configuration works without it.
    fn write_schema(&self) {
        let Some(dir) = self.path.parent() else {
            return;
        };
        let schema_path = dir.join(SCHEMA_FILE_NAME);
        let current = std::fs::read(&schema_path).ok();
        if current.as_deref() != Some(SCHEMA_JSON.as_bytes())
            && let Err(error) = write_atomically(&schema_path, SCHEMA_JSON.as_bytes())
        {
            tracing::warn!(path = %schema_path.display(), %error, "cannot write the configuration schema");
        }
    }
}

/// Reads the whole file; `None` if it does not exist. Retries for a short
/// while when an editor holds it open without sharing.
fn read(path: &Path) -> io::Result<Option<Vec<u8>>> {
    let mut attempts = 0;
    loop {
        match std::fs::read(path) {
            Ok(bytes) => return Ok(Some(bytes)),
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error)
                if error.raw_os_error() == Some(SHARING_VIOLATION)
                    && attempts < SHARING_RETRIES =>
            {
                attempts += 1;
                std::thread::sleep(SHARING_RETRY_DELAY);
            }
            Err(error) => return Err(error),
        }
    }
}

fn text_of(bytes: &[u8]) -> Result<&str, ConfigError> {
    std::str::from_utf8(bytes)
        .map_err(|error| ConfigError::general(format!("the file is not UTF-8 text: {error}")))
}

fn write_atomically(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let dir = path
        .parent()
        .filter(|dir| !dir.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    std::fs::create_dir_all(dir)?;
    let name = path.file_name().map_or_else(
        || "config".into(),
        |name| name.to_string_lossy().into_owned(),
    );
    let temporary = dir.join(format!(".{name}.{}.tmp", std::process::id()));
    let result = (|| {
        let mut file = std::fs::File::create(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        std::fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

fn content_hash(bytes: &[u8]) -> u64 {
    let mut hasher = DefaultHasher::new();
    hasher.write(bytes);
    hasher.finish()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Layout;

    fn temp_config() -> (tempfile::TempDir, PathBuf) {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("sub").join(FILE_NAME);
        (dir, path)
    }

    #[expect(clippy::unnecessary_wraps, reason = "it stands in for a validator")]
    fn accept(_: &Config) -> Result<(), Rejection> {
        Ok(())
    }

    #[test]
    fn explicit_path_wins_over_env_and_default() {
        let explicit = resolve_path(
            Some(PathBuf::from(r"D:\c.json")),
            Some(r"E:\e.json".into()),
            Some(r"C:\A".into()),
        );
        assert_eq!(explicit, PathBuf::from(r"D:\c.json"));
        let env = resolve_path(None, Some(r"E:\e.json".into()), Some(r"C:\A".into()));
        assert_eq!(env, PathBuf::from(r"E:\e.json"));
        let default = resolve_path(None, None, Some(r"C:\A".into()));
        assert_eq!(default, PathBuf::from(r"C:\A\CabinetOS\cabinetos.json"));
    }

    #[test]
    fn a_missing_file_is_created_with_defaults_and_a_schema() {
        let (_dir, path) = temp_config();
        let (store, opened) = ConfigStore::open(path.clone(), accept);
        assert_eq!(opened, Opened::Created);
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(
            text.starts_with("{\n  \"$schema\": \"./cabinetos.schema.json\",\n  \"version\": 1,"),
            "{text}"
        );
        assert_eq!(parse(&text).unwrap(), *store.config());
        let schema = std::fs::read_to_string(path.with_file_name(SCHEMA_FILE_NAME)).unwrap();
        assert_eq!(schema, SCHEMA_JSON);
    }

    #[test]
    fn a_broken_file_keeps_the_defaults_and_reports_the_error() {
        let (_dir, path) = temp_config();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, "{ \"ui\": { \"layout\": \"rail\", } }").unwrap();
        let (store, opened) = ConfigStore::open(path.clone(), accept);
        assert!(
            matches!(opened, Opened::Invalid(ConfigError { line: Some(1), .. })),
            "{opened:?}"
        );
        assert_eq!(*store.config(), Config::default());
        // The user's file is left alone.
        assert!(std::fs::read_to_string(&path).unwrap().contains("rail"));
    }

    #[test]
    fn reload_reports_changes_once_and_ignores_own_writes() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        // The core's own write (creating the file) is not a change.
        assert_eq!(store.reload(accept), Reload::Unchanged);

        std::fs::write(
            &path,
            r#"{"ui": {"layout": "rail"}, "panes": {"showHidden": true}}"#,
        )
        .unwrap();
        let Reload::Changed(mut changed) = store.reload(accept) else {
            panic!()
        };
        changed.sort();
        assert_eq!(changed, ["panes.showHidden", "ui.layout"]);
        assert_eq!(store.config().ui.layout, Layout::Rail);
        assert_eq!(store.reload(accept), Reload::Unchanged);

        std::fs::write(&path, r#"{"ui": {"layout": 5}}"#).unwrap();
        assert!(matches!(store.reload(accept), Reload::Invalid(_)));
        // The same bad content again: already reported.
        assert_eq!(store.reload(accept), Reload::Unchanged);
        assert_eq!(
            store.config().ui.layout,
            Layout::Rail,
            "the good settings stay"
        );
    }

    #[test]
    fn a_rejection_keeps_the_settings_and_points_at_the_entry() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        std::fs::write(
            &path,
            "{\n  \"keybindings\": [\n    {\"command\": \"help.about\", \"keys\": \"ctrl+b\"}\n  ]\n}",
        )
        .unwrap();
        let outcome = store.reload(|_| {
            Err(Rejection {
                keybinding: Some(0),
                message: "ctrl+b is taken".to_owned(),
            })
        });
        assert_eq!(
            outcome,
            Reload::Invalid(ConfigError {
                line: Some(3),
                column: Some(5),
                message: "ctrl+b is taken".to_owned()
            })
        );
        assert!(store.config().keybindings.is_empty());
    }

    #[test]
    fn updates_write_atomically_in_order_and_are_not_echoed() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        let changed = store
            .set_keybinding(
                "view.toggleSidebar",
                Some("Ctrl+Alt+B".parse().unwrap()),
                accept,
            )
            .unwrap();
        assert_eq!(changed, ["keybindings"]);
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("\"keys\": \"ctrl+alt+b\""), "{text}");
        assert!(
            text.contains("\n  \"ui\": {\n    \"layout\": \"classic\""),
            "2-space JSON: {text}"
        );
        assert_eq!(
            store.reload(accept),
            Reload::Unchanged,
            "no echo of the own write"
        );
        // No temporary file is left behind.
        let leftovers: Vec<_> = std::fs::read_dir(path.parent().unwrap())
            .unwrap()
            .map(|entry| entry.unwrap().file_name())
            .filter(|name| name.to_string_lossy().ends_with(".tmp"))
            .collect();
        assert!(leftovers.is_empty(), "{leftovers:?}");

        store
            .set_keybinding("view.toggleSidebar", None, accept)
            .unwrap();
        assert_eq!(store.config().keybindings.len(), 1, "one entry per command");
        store
            .unset_keybinding("view.toggleSidebar", accept)
            .unwrap();
        assert!(store.config().keybindings.is_empty());
    }

    #[test]
    fn an_update_keeps_a_fresh_edit_and_refuses_a_broken_one() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        // The user saved an edit the watcher has not processed yet.
        std::fs::write(&path, r#"{"ui": {"theme": "nord"}}"#).unwrap();
        let mut changed = store
            .set_keybinding("help.about", Some("f1".parse().unwrap()), accept)
            .unwrap();
        changed.sort();
        assert_eq!(changed, ["keybindings", "ui.theme"]);
        assert!(std::fs::read_to_string(&path).unwrap().contains("nord"));

        std::fs::write(&path, r#"{"ui": {"theme": "#).unwrap();
        let result = store.set_keybinding("help.about", None, accept);
        assert!(
            matches!(result, Err(UpdateError::FileHasError(_))),
            "{result:?}"
        );
        assert_eq!(
            std::fs::read_to_string(&path).unwrap(),
            r#"{"ui": {"theme": "#,
            "untouched"
        );
    }

    #[test]
    fn set_value_checks_the_path_and_the_type() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path, accept);
        let changed = store
            .set_value("ui.layout", Value::from("rail"), accept)
            .unwrap();
        assert_eq!(changed, ["ui.layout"]);
        assert_eq!(store.config().ui.layout, Layout::Rail);
        assert!(matches!(
            store.set_value("ui.nope", Value::from(1), accept),
            Err(UpdateError::Rejected(_))
        ));
        assert!(matches!(
            store.set_value("ui.dualPane", Value::from("yes"), accept),
            Err(UpdateError::Rejected(_))
        ));
        let changed = store
            .set_value("ui.pinned", serde_json::json!([r"D:\work"]), accept)
            .unwrap();
        assert_eq!(changed, ["ui.pinned"]);
        assert_eq!(store.config().ui.pinned, [r"D:\work"]);
        assert!(matches!(
            store.set_value("ui.lastPaths", Value::from(r"C:\"), accept),
            Err(UpdateError::Rejected(_))
        ));
    }

    #[test]
    fn set_value_keeps_the_file_readable() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        let before = std::fs::read_to_string(&path).unwrap();
        // Each of these parses, but a file that says so would not load.
        for (setting, value) in [
            ("terminal.defaultProfile", Value::from("fish")),
            ("version", Value::from(2)),
        ] {
            let result = store.set_value(setting, value, accept);
            let Err(UpdateError::Rejected(rejection)) = result else {
                panic!("{setting}: {result:?}")
            };
            assert!(!rejection.message.contains("line"), "{}", rejection.message);
        }
        assert_eq!(std::fs::read_to_string(&path).unwrap(), before);
        assert_eq!(*store.config(), parse(&before).unwrap());
    }

    #[test]
    fn a_deleted_file_is_an_error_until_it_returns() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        let original = std::fs::read(&path).unwrap();
        std::fs::remove_file(&path).unwrap();
        assert!(matches!(store.reload(accept), Reload::Invalid(_)));
        assert_eq!(store.reload(accept), Reload::Unchanged, "reported once");
        std::fs::write(&path, &original).unwrap();
        // Nothing changed, but the error is resolved.
        assert_eq!(store.reload(accept), Reload::Changed(Vec::new()));
        assert_eq!(store.reload(accept), Reload::Unchanged);
    }

    #[test]
    fn undoing_a_bad_edit_resolves_the_error() {
        let (_dir, path) = temp_config();
        let (mut store, _) = ConfigStore::open(path.clone(), accept);
        let original = std::fs::read(&path).unwrap();
        std::fs::write(&path, "{ oops").unwrap();
        assert!(matches!(store.reload(accept), Reload::Invalid(_)));
        std::fs::write(&path, &original).unwrap();
        assert_eq!(store.reload(accept), Reload::Changed(Vec::new()));
    }

    #[test]
    fn open_validates_too() {
        let (_dir, path) = temp_config();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, r#"{"ui": {"theme": "nord"}}"#).unwrap();
        let (store, opened) = ConfigStore::open(path, |_| {
            Err(Rejection {
                keybinding: None,
                message: "no".to_owned(),
            })
        });
        assert_eq!(opened, Opened::Invalid(ConfigError::general("no")));
        assert_eq!(*store.config(), Config::default());
    }

    #[test]
    fn a_byte_order_mark_is_accepted() {
        let (_dir, path) = temp_config();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, "\u{feff}{\"ui\": {\"sidebar\": false}}").unwrap();
        let (store, opened) = ConfigStore::open(path, accept);
        assert_eq!(opened, Opened::Loaded);
        assert!(!store.config().ui.sidebar);
    }
}
