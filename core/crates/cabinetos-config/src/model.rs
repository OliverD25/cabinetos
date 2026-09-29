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

/// The default `marketplace.index`: the public index, GitHub Pages of the
/// repository `cabinetos-marketplace` (ADR 0012).
pub const DEFAULT_MARKETPLACE_INDEX: &str =
    "https://oliverd25.github.io/cabinetos-marketplace/index.json";

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
    /// What happens to files: the editor that opens them.
    pub files: FilesConfig,
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
            files: FilesConfig::default(),
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
    /// The Tool Dock's size as the user last dragged it, so it survives a
    /// restart.
    pub dock_size: DockSize,
    /// The tabs of each pane as the window last saved them, so the next
    /// start opens them again. The window owns them; the core only checks
    /// and stores them.
    pub tabs: TabsConfig,
    /// The buttons of the activity rail (`layout: rail`) in the order the
    /// user put them, by the ID each button has: `explorer`, `search`,
    /// `marketplace`, `terminal`, or the ID of a tool with a sidebar view.
    /// Empty: the default order. The window owns it; the core only stores
    /// it.
    pub rail: Vec<String>,
    /// The sidebar's width in pixels as the user last dragged it. `null`:
    /// the design's width.
    pub sidebar_width: Option<u32>,
    /// The view the sidebar showed last: `explorer`, `search`, or the ID of
    /// a tool with a sidebar view. The window falls back to `explorer` for
    /// one it does not know.
    pub sidebar_view: String,
    /// The Explorer view follows the active pane's folder.
    pub sidebar_auto_reveal: bool,
}

/// The tabs of both panes.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default)]
pub struct TabsConfig {
    /// The left pane's tabs.
    pub left: PaneTabs,
    /// The right pane's tabs.
    pub right: PaneTabs,
}

/// One pane's tabs, left to right, and which one is in front.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default)]
pub struct PaneTabs {
    /// The tabs, left to right. Empty: the pane opens one tab, as
    /// `ui.lastPaths` or the window decides.
    pub items: Vec<TabEntry>,
    /// The index of the tab in front, from 0; must name one of `items`
    /// (0 when there are none).
    pub active: u32,
}

/// One tab of a pane.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields)]
pub struct TabEntry {
    /// The folder the tab shows, as an absolute path.
    pub path: String,
    /// A locked tab stays on its folder: opening another folder in it opens
    /// a new tab instead.
    #[serde(default)]
    pub locked: bool,
}

/// The Tool Dock's size, in pixels, for each place it can sit. `null`: the
/// design's size, within the design's limits.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default)]
pub struct DockSize {
    /// Its height when it sits under the panes.
    pub bottom: Option<u32>,
    /// Its width when it sits beside the panes.
    pub right: Option<u32>,
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
            dock_size: DockSize::default(),
            tabs: TabsConfig::default(),
            rail: Vec::new(),
            sidebar_width: None,
            sidebar_view: "explorer".to_owned(),
            sidebar_auto_reveal: true,
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
    /// How the keyboard marks rows: as Windows does, or as Total Commander
    /// does.
    pub selection: SelectionMode,
}

/// How the keyboard marks rows in a file pane (`docs/config.md`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "lowercase")]
pub enum SelectionMode {
    /// As in Explorer: a key that moves the cursor selects the row it moves
    /// to, and Shift extends the selection.
    #[default]
    Windows,
    /// As in Total Commander: keys that move the cursor keep the marks,
    /// Shift with them marks the rows passed over, and commands act on the
    /// marked rows, or on the cursor row when none is marked.
    Commander,
}

/// What happens to files.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct FilesConfig {
    /// The program that edits a file (`file.edit`, F4). `null`: Windows'
    /// own edit verb for the file's type, else Notepad.
    pub editor: Option<EditorProgram>,
}

/// A program that edits a file: the file's path is added as its last
/// argument. The program is a full path, or a name found on the PATH, as
/// for a terminal profile; never the current folder.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct EditorProgram {
    /// The program, for example `notepad++.exe` or
    /// `C:\Program Files\Microsoft VS Code\Code.exe`.
    pub command: String,
    /// Its arguments, before the file's path, for example `["--wait"]`.
    #[serde(default)]
    pub args: Vec<String>,
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
    /// The plugin's own settings: any keys and values, since the core does
    /// not know what the plugin means by them. The plugin reads them with
    /// `config-get` (`plugins.<id>.settings`), and `config set
    /// plugins.<id>.settings.<key> <value>` writes one, creating what is
    /// missing on the way. A change is told to the running plugin as the
    /// event `settings-changed`; it does not restart it.
    #[serde(skip_serializing_if = "serde_json::Map::is_empty")]
    pub settings: serde_json::Map<String, serde_json::Value>,
}

impl Default for PluginSettings {
    fn default() -> Self {
        Self {
            enabled: true,
            granted: Vec::new(),
            settings: serde_json::Map::new(),
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
    /// Heavy logging: every operation is also written, at every level, into
    /// `heavy-<process>.<date>.jsonl` files (at most 2 GB in all), even at
    /// the cost of speed. On until turned off. The environment variable
    /// `CABINETOS_LOG_HEAVY` overrides it.
    pub heavy: bool,
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
        assert_eq!(config.ui.dock_size, DockSize::default());
        assert_eq!(
            (config.ui.dock_size.bottom, config.ui.dock_size.right),
            (None, None)
        );
        assert_eq!(config.ui.tabs, TabsConfig::default());
        assert!(config.ui.tabs.left.items.is_empty() && config.ui.tabs.right.items.is_empty());
        assert_eq!(
            (config.ui.tabs.left.active, config.ui.tabs.right.active),
            (0, 0)
        );
        assert!(!config.panes.show_hidden);
        assert_eq!(SortSpec::from(config.panes.sort), SortSpec::default());
        // Article 4: the first run marks files as Windows does.
        assert_eq!(config.panes.selection, SelectionMode::Windows);
        assert_eq!(config.files.editor, None);
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
    fn the_selection_mode_and_the_editor_are_read() {
        let config: Config = serde_json::from_str(
            r#"{"panes": {"selection": "commander"},
                "files": {"editor": {"command": "code.cmd", "args": ["--wait"]}}}"#,
        )
        .unwrap();
        assert_eq!(config.panes.selection, SelectionMode::Commander);
        assert_eq!(
            config.files.editor,
            Some(EditorProgram {
                command: "code.cmd".to_owned(),
                args: vec!["--wait".to_owned()],
            })
        );
        // Arguments are optional, as for a terminal profile.
        let bare: FilesConfig =
            serde_json::from_str(r#"{"editor": {"command": "notepad++.exe"}}"#).unwrap();
        assert_eq!(bare.editor.unwrap().args, Vec::<String>::new());
        let none: FilesConfig = serde_json::from_str(r#"{"editor": null}"#).unwrap();
        assert_eq!(none.editor, None);
        for (bad, expected) in [
            (r#"{"panes": {"selection": "tc"}}"#, "unknown variant `tc`"),
            (
                r#"{"files": {"editor": {"command": "x", "arg": []}}}"#,
                "unknown field `arg`",
            ),
            (
                r#"{"files": {"editor": "notepad.exe"}}"#,
                "invalid type: string",
            ),
            (r#"{"files": {"viewer": null}}"#, "unknown field `viewer`"),
        ] {
            let error = serde_json::from_str::<Config>(bad).unwrap_err().to_string();
            assert!(error.contains(expected), "{bad}: {error}");
        }
    }

    #[test]
    fn tabs_are_read_with_their_defaults_and_unknown_keys_refused() {
        let ui: UiConfig = serde_json::from_str(
            r#"{"tabs": {"left": {"items": [{"path": "C:\\x", "locked": true}, {"path": "D:\\y"}], "active": 1}}}"#,
        )
        .unwrap();
        assert_eq!(
            ui.tabs.left.items,
            [
                TabEntry {
                    path: r"C:\x".to_owned(),
                    locked: true
                },
                TabEntry {
                    path: r"D:\y".to_owned(),
                    locked: false
                }
            ]
        );
        assert_eq!(ui.tabs.left.active, 1);
        assert_eq!(ui.tabs.right, PaneTabs::default());
        for (bad, expected) in [
            (r#"{"tabs": {"middle": {}}}"#, "unknown field `middle`"),
            (
                r#"{"tabs": {"left": {"items": [{"path": "C:\\", "pinned": true}]}}}"#,
                "unknown field `pinned`",
            ),
            (
                r#"{"tabs": {"left": {"items": [{}]}}}"#,
                "missing field `path`",
            ),
            (r#"{"tabs": {"left": {"active": -1}}}"#, "invalid value"),
        ] {
            let error = serde_json::from_str::<UiConfig>(bad)
                .unwrap_err()
                .to_string();
            assert!(error.contains(expected), "{bad}: {error}");
        }
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
            "\"files\"",
            "\"terminal\"",
            "\"keybindings\"",
            "\"logging\"",
            "\"marketplace\"",
        ];
        let positions: Vec<usize> = order.iter().map(|key| text.find(key).unwrap()).collect();
        assert!(positions.is_sorted(), "{text}");
        assert!(text.contains("\"dualPane\":true"));
        assert!(text.contains("\"lastPaths\":[],\"pinned\":[]"));
        assert!(text.contains("\"dockSize\":{\"bottom\":null,\"right\":null}"));
        assert!(
            text.contains(
                "\"tabs\":{\"left\":{\"items\":[],\"active\":0},\"right\":{\"items\":[],\"active\":0}}"
            ),
            "{text}"
        );
        let dragged: UiConfig = serde_json::from_str(r#"{"dockSize": {"bottom": 320}}"#).unwrap();
        assert_eq!(
            dragged.dock_size,
            DockSize {
                bottom: Some(320),
                right: None
            }
        );
        assert!(serde_json::from_str::<UiConfig>(r#"{"dockSize": {"left": 1}}"#).is_err());
        assert!(text.contains("\"showHidden\":false"));
        assert!(text.contains("\"selection\":\"windows\""), "{text}");
        assert!(text.contains("\"files\":{\"editor\":null}"), "{text}");
        assert!(text.contains("\"defaultProfile\":\"pwsh\""));
        assert!(text.contains("\"allowInsecure\":false"));
    }
}
