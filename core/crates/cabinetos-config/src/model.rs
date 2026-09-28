//! The shape of `cabinetos.json`. Every section has defaults, so a file may
//! leave anything out; an unknown key is an error, so a typo is caught
//! instead of silently ignored.

use std::collections::BTreeMap;

use cabinetos_commands::{KeySequence, Override};
use cabinetos_protocol::{SortKey, SortSpec};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// The `$schema` value in files the core creates: the schema file it writes
/// next to the configuration, so editors can complete and check keys.
pub const SCHEMA_REFERENCE: &str = "./cabinetos.schema.json";

/// The version of the file format this build reads.
pub const FORMAT_VERSION: u32 = 1;

/// The default `marketplace.index`: a placeholder until a public index
/// exists. `.invalid` is a name reserved never to resolve, so nothing is
/// ever fetched from it.
pub const DEFAULT_MARKETPLACE_INDEX: &str = "https://marketplace.cabinetos.invalid/index.json";

/// The whole configuration.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct Config {
    /// Where editors find the schema of this file. The core writes
    /// `./cabinetos.schema.json` next to it.
    #[serde(rename = "$schema", skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    /// The file format version; always 1 for now.
    pub version: u32,
    /// The window.
    pub ui: UiConfig,
    /// The file panes.
    pub panes: PanesConfig,
    /// The integrated terminal.
    pub terminal: TerminalConfig,
    /// The user's changes to key bindings. The defaults live in the command
    /// registry; list only what differs.
    pub keybindings: Vec<KeybindingEntry>,
    /// Diagnostics.
    pub logging: LoggingConfig,
    /// Core Plugins, by ID: which run, and what they may do. A plugin that
    /// is not listed is on, with nothing granted.
    pub plugins: BTreeMap<String, PluginSettings>,
    /// Where extensions and themes come from.
    pub marketplace: MarketplaceConfig,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            schema: None,
            version: FORMAT_VERSION,
            ui: UiConfig::default(),
            panes: PanesConfig::default(),
            terminal: TerminalConfig::default(),
            keybindings: Vec::new(),
            logging: LoggingConfig::default(),
            plugins: BTreeMap::new(),
            marketplace: MarketplaceConfig::default(),
        }
    }
}

impl Config {
    /// The `keybindings` entries as keymap overrides, in file order, so a
    /// keymap error's entry index is the index in the file.
    #[must_use]
    pub fn overrides(&self) -> Vec<Override> {
        self.keybindings
            .iter()
            .map(|entry| Override {
                command: entry.command.clone(),
                keys: entry.keys.0.clone(),
                when: entry.when.clone(),
            })
            .collect()
    }
}

/// The window.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct UiConfig {
    /// Where the sidebar, panes and terminal go.
    pub layout: Layout,
    /// Two file panes side by side, or one.
    pub dual_pane: bool,
    /// Show the sidebar.
    pub sidebar: bool,
    /// The color theme's ID.
    pub theme: String,
    /// The folders the panes showed last, left pane first, so the next
    /// start opens them again. Empty: the UI picks.
    pub last_paths: Vec<String>,
    /// Folders the user pinned to the sidebar, in the sidebar's order.
    pub pinned: Vec<String>,
}

impl Default for UiConfig {
    fn default() -> Self {
        Self {
            layout: Layout::Classic,
            dual_pane: true,
            sidebar: true,
            theme: "default".to_owned(),
            last_paths: Vec::new(),
            pinned: Vec::new(),
        }
    }
}

/// Window layouts from the design.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "lowercase")]
pub enum Layout {
    /// Sidebar on the left, terminal at the bottom.
    #[default]
    Classic,
    /// Terminal on the right.
    Right,
    /// An activity rail instead of the full sidebar.
    Rail,
}

/// The file panes. These are the defaults for `list_directory` requests that
/// do not say otherwise.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct PanesConfig {
    /// Also list hidden and system entries.
    pub show_hidden: bool,
    /// The order of entries; directories always come first.
    pub sort: SortConfig,
}

/// The order of a listing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct SortConfig {
    /// `name` (natural order), `size`, `modified` or `kind`.
    pub key: SortKey,
    /// Largest, newest or last first.
    pub descending: bool,
}

impl From<SortConfig> for SortSpec {
    fn from(sort: SortConfig) -> Self {
        Self {
            key: sort.key,
            descending: sort.descending,
        }
    }
}

/// The integrated terminal (Phase 8).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct TerminalConfig {
    /// The `name` of the profile a new terminal starts with.
    pub default_profile: String,
    /// The shells a terminal can run.
    pub profiles: Vec<TerminalProfile>,
}

impl Default for TerminalConfig {
    fn default() -> Self {
        let profile = |name: &str, command: &str, args: &[&str]| TerminalProfile {
            name: name.to_owned(),
            command: command.to_owned(),
            args: args.iter().map(|arg| (*arg).to_owned()).collect(),
        };
        Self {
            default_profile: "pwsh".to_owned(),
            profiles: vec![
                profile("pwsh", "pwsh.exe", &["-NoLogo"]),
                profile("cmd", "cmd.exe", &[]),
                profile("wsl", "wsl.exe", &[]),
            ],
        }
    }
}

/// One shell a terminal can run.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct TerminalProfile {
    /// A unique name, for example `pwsh`.
    pub name: String,
    /// The program, for example `pwsh.exe`.
    pub command: String,
    /// Its arguments.
    #[serde(default)]
    pub args: Vec<String>,
}

/// One change to a key binding.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct KeybindingEntry {
    /// The command's ID, for example `view.toggleSidebar`.
    pub command: String,
    /// The new keys, for example `ctrl+alt+b` or `ctrl+k ctrl+b`; an empty
    /// string removes the command's binding. Grammar: docs/keybindings.md.
    pub keys: Keys,
    /// The context in which the binding applies, for example `filesView`;
    /// without it the command's own context applies.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub when: Option<String>,
}

/// The keys of a binding, checked against the key grammar while the file is
/// read (so an error points at its line). `None` is the empty string: no
/// binding.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Keys(pub Option<KeySequence>);

impl Serialize for Keys {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match &self.0 {
            Some(keys) => serializer.collect_str(keys),
            None => serializer.serialize_str(""),
        }
    }
}

impl<'de> Deserialize<'de> for Keys {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        if text.trim().is_empty() {
            return Ok(Self(None));
        }
        text.parse()
            .map(|keys| Self(Some(keys)))
            .map_err(serde::de::Error::custom)
    }
}

#[cfg(feature = "schema")]
impl schemars::JsonSchema for Keys {
    fn schema_name() -> std::borrow::Cow<'static, str> {
        "Keys".into()
    }

    fn json_schema(_generator: &mut schemars::SchemaGenerator) -> schemars::Schema {
        schemars::json_schema!({
            "description": "One combination (`ctrl+shift+p`) or a chord of two separated by a space (`ctrl+k ctrl+c`); an empty string for none. Modifiers: ctrl, shift, alt, win.",
            "type": "string"
        })
    }
}

/// One plugin's settings.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct PluginSettings {
    /// Run the plugin.
    pub enabled: bool,
    /// The capabilities the user granted it, for example `fs:read`. It runs
    /// only when it has every capability it asks for (docs/plugins.md).
    pub granted: Vec<String>,
}

impl Default for PluginSettings {
    fn default() -> Self {
        Self {
            enabled: true,
            granted: Vec::new(),
        }
    }
}

/// The marketplace (docs/marketplace.md).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct MarketplaceConfig {
    /// Where the index is: an `https:` URL, a `file:` URL, or the path of an
    /// `index.json` or of its folder. The core reads it only when a client
    /// asks.
    pub index: String,
    /// Also accept a plain `http:` index and downloads, which anyone on the
    /// network could change on the way. For testing only.
    pub allow_insecure: bool,
}

impl Default for MarketplaceConfig {
    fn default() -> Self {
        Self {
            index: DEFAULT_MARKETPLACE_INDEX.to_owned(),
            allow_insecure: false,
        }
    }
}

/// Diagnostics.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct LoggingConfig {
    /// The least important level written to the log. The environment
    /// variable `CABINETOS_LOG` overrides it.
    pub level: LogLevel,
}

/// Log levels, most detailed first.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "lowercase")]
pub enum LogLevel {
    /// Everything.
    Trace,
    /// Details for developers.
    Debug,
    /// Normal operation.
    #[default]
    Info,
    /// Problems the core recovered from.
    Warn,
    /// Failures.
    Error,
}

impl LogLevel {
    /// The level as a `tracing` level.
    #[must_use]
    pub fn to_tracing(self) -> tracing::Level {
        match self {
            Self::Trace => tracing::Level::TRACE,
            Self::Debug => tracing::Level::DEBUG,
            Self::Info => tracing::Level::INFO,
            Self::Warn => tracing::Level::WARN,
            Self::Error => tracing::Level::ERROR,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_follow_the_design() {
        let config = Config::default();
        assert_eq!(config.version, 1);
        assert_eq!(config.ui.layout, Layout::Classic);
        assert!(config.ui.dual_pane && config.ui.sidebar);
        assert_eq!(config.ui.theme, "default");
        assert!(config.ui.last_paths.is_empty() && config.ui.pinned.is_empty());
        assert!(!config.panes.show_hidden);
        assert_eq!(SortSpec::from(config.panes.sort), SortSpec::default());
        assert_eq!(config.terminal.default_profile, "pwsh");
        let names: Vec<&str> = config
            .terminal
            .profiles
            .iter()
            .map(|p| p.name.as_str())
            .collect();
        assert_eq!(names, ["pwsh", "cmd", "wsl"]);
        assert!(config.keybindings.is_empty());
        assert_eq!(config.logging.level, LogLevel::Info);
        assert_eq!(config.marketplace.index, DEFAULT_MARKETPLACE_INDEX);
        assert!(!config.marketplace.allow_insecure);
    }

    #[test]
    fn keys_are_normalized_and_empty_means_none() {
        let entry: KeybindingEntry =
            serde_json::from_str(r#"{"command":"view.toggleSidebar","keys":"Ctrl+Alt+B"}"#)
                .unwrap();
        assert_eq!(
            serde_json::to_string(&entry.keys).unwrap(),
            r#""ctrl+alt+b""#
        );
        let none: Keys = serde_json::from_str(r#""""#).unwrap();
        assert_eq!(none, Keys(None));
        assert_eq!(serde_json::to_string(&none).unwrap(), r#""""#);
        assert!(serde_json::from_str::<Keys>(r#""ctrl+nope""#).is_err());
    }

    #[test]
    fn serializes_in_camel_case_and_field_order() {
        let text = serde_json::to_string(&Config::default()).unwrap();
        let order = [
            "\"version\"",
            "\"ui\"",
            "\"panes\"",
            "\"terminal\"",
            "\"keybindings\"",
            "\"logging\"",
            "\"marketplace\"",
        ];
        let positions: Vec<usize> = order.iter().map(|key| text.find(key).unwrap()).collect();
        assert!(positions.is_sorted(), "{text}");
        assert!(text.contains("\"dualPane\":true"));
        assert!(text.contains("\"lastPaths\":[],\"pinned\":[]"));
        assert!(text.contains("\"showHidden\":false"));
        assert!(text.contains("\"defaultProfile\":\"pwsh\""));
        assert!(text.contains("\"allowInsecure\":false"));
    }
}
