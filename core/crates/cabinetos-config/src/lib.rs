//! Configuration: `%APPDATA%\CabinetOS\cabinetos.json`.
//!
//! - [`parse`] reads the file: strict JSON, every setting optional with a
//!   default, an unknown key an error that names the key and its line.
//! - [`ConfigStore`] owns the file: it creates it with the defaults on first
//!   run, re-reads it after a change ([`ConfigStore::reload`]), keeps the last
//!   good settings while the file has an error, and rewrites it atomically
//!   for changes made from the UI ([`ConfigStore::set_keybinding`],
//!   [`ConfigStore::set_value`]). The core's own writes are recognized by a
//!   content hash, so they are not reported back as changes.
//! - [`ConfigWatcher`] notices edits: it watches the file's directory (so a
//!   save that renames a temporary file into place is seen too) and reports
//!   once the directory has been quiet for 100 ms.
//! - [`changed_paths`] names the settings that differ, as dotted paths.
//!
//! The file format is in `docs/config.md`; its JSON Schema is checked in at
//! `sdk/config/cabinetos.schema.json`.
//!
//! Serves Constitution Article 6 (Universal Configuration: the raw file and
//! the settings UI stay in sync in real time). Brief §7.
#![forbid(unsafe_code)]

mod diff;
mod locate;
mod model;
mod parse;
#[cfg(feature = "schema")]
pub mod schema;
mod store;
mod watch;

pub use diff::changed_paths;
pub use model::{
    Config, DEFAULT_MARKETPLACE_INDEX, DockSize, EditorProgram, FORMAT_VERSION, FilesConfig,
    KeybindingEntry, Keys, Layout, LogLevel, LoggingConfig, MarketplaceConfig, PaneTabs,
    PanesConfig, PluginSettings, SCHEMA_REFERENCE, SelectionMode, SortConfig, TabEntry, TabsConfig,
    TerminalConfig, TerminalProfile, UiConfig, UpdateConfig,
};
pub use parse::{ConfigError, Rejection, file_text, parse, parse_checked};
pub use store::{
    CONFIG_ENV, ConfigStore, FILE_NAME, Opened, Reload, SCHEMA_FILE_NAME, UpdateError,
    default_path, resolve_path,
};
pub use watch::{ConfigWatcher, DEBOUNCE, WatchEvent};

/// The JSON Schema of `cabinetos.json`. The core writes it next to the file,
/// where the `$schema` key points, so editors complete and check the keys.
pub const SCHEMA_JSON: &str = include_str!("../../../../sdk/config/cabinetos.schema.json");
