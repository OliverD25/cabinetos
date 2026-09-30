//! The right-click menu (`contextMenu`) and the user's own programs
//! (`programs`): what the window shows in the menu, and what a menu entry
//! may start (Phase 18, ADR 0015).
//!
//! The window builds the menu from these settings at every opening; the
//! core checks them when the file is read and turns each program into the
//! command `program.<name>`.

pub use cabinetos_commands::PROGRAM_PREFIX;
use serde::{Deserialize, Serialize};

/// The tokens a program's arguments may hold.
pub const PROGRAM_TOKENS: [&str; 3] = ["{path}", "{selection}", "{cwd}"];

/// The right-click menu of a file pane: one list for each kind of target,
/// and whether Shift+right-click shows Windows' own menu.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, default, rename_all = "camelCase")]
pub struct ContextMenuConfig {
    /// Shift+right-click and `menu.showShell` (Ctrl+Shift+F10) show
    /// Windows' own menu of the file (Open with, Send to, the menus other
    /// programs add). The core builds it on a background thread. Off: both
    /// open this menu.
    pub shell_menu: bool,
    /// The menu of a pane's empty space.
    pub background: BackgroundMenu,
    /// The menu of one file.
    pub file: FileMenu,
    /// The menu of one folder.
    pub folder: FolderMenu,
    /// The menu of a row inside a selection of several rows.
    pub multi_select: MultiSelectMenu,
}

impl ContextMenuConfig {
    /// Each target by its key in the file, with its icon row and its rows.
    #[must_use]
    pub fn targets(&self) -> [(&'static str, &[String], &[MenuItem]); 4] {
        [
            (
                "background",
                &self.background.quick_actions,
                &self.background.items,
            ),
            ("file", &self.file.quick_actions, &self.file.items),
            ("folder", &self.folder.quick_actions, &self.folder.items),
            (
                "multiSelect",
                &self.multi_select.quick_actions,
                &self.multi_select.items,
            ),
        ]
    }
}

/// Today's icon row of a row's menu (Phase 5): Cut, Copy, Paste, Rename,
/// Delete.
const ROW_QUICK_ACTIONS: [&str; 5] = [
    "edit.cut",
    "edit.copy",
    "edit.paste",
    "file.rename",
    "file.delete",
];

/// Today's rows of a row's menu (Phase 5). Properties and the plugins'
/// group are not listed: the window adds them.
const ROW_ITEMS: [&str; 4] = [
    "pane.openSelected",
    "file.openInOtherPane",
    "file.copyToOtherPane",
    "terminal.new",
];

/// Today's rows of the empty space's menu. Properties is added by the window.
const BACKGROUND_ITEMS: [&str; 3] = ["edit.paste", "file.newFolder", "sidebar.pin"];

fn commands(ids: &[&str]) -> Vec<String> {
    ids.iter().map(|id| (*id).to_owned()).collect()
}

fn items(ids: &[&str]) -> Vec<MenuItem> {
    ids.iter().map(|id| MenuItem::command(id)).collect()
}

/// One target's menu, with its own defaults: a key left out keeps them, so
/// adding a row to `items` keeps the icon row.
macro_rules! menu_target {
    ($(#[$doc:meta])* $name:ident, $quick:expr, $items:expr) => {
        $(#[$doc])*
        #[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
        #[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
        #[serde(deny_unknown_fields, default, rename_all = "camelCase")]
        pub struct $name {
            /// The icon row at the top of the menu: command IDs, left to
            /// right. Each shows its icon, with its title and keys as a
            /// tooltip.
            pub quick_actions: Vec<String>,
            /// The rows, top to bottom: `{"command": "<id>"}`, optionally
            /// with `"extensions": [".md", ".txt"]` to show it only for
            /// such files, or `{"separator": true}`. The window adds the
            /// plugins' commands, Properties and "Edit Menu…" after them.
            pub items: Vec<MenuItem>,
        }

        impl Default for $name {
            fn default() -> Self {
                Self {
                    quick_actions: commands(&$quick),
                    items: items(&$items),
                }
            }
        }
    };
}

menu_target!(
    /// The menu of a pane's empty space: Paste, New folder, Pin this folder
    /// to the sidebar (while it is not pinned), then Properties of the folder.
    BackgroundMenu,
    [],
    BACKGROUND_ITEMS
);
menu_target!(
    /// The menu of one file.
    FileMenu,
    ROW_QUICK_ACTIONS,
    ROW_ITEMS
);
menu_target!(
    /// The menu of one folder.
    FolderMenu,
    ROW_QUICK_ACTIONS,
    ROW_ITEMS
);
menu_target!(
    /// The menu of a row inside a selection of several rows.
    MultiSelectMenu,
    ROW_QUICK_ACTIONS,
    ROW_ITEMS
);

/// One row of a menu: a command, or a divider.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(try_from = "RawMenuItem", into = "RawMenuItem")]
pub enum MenuItem {
    /// A command by its ID. With `extensions`, only for a file with one of
    /// them, or a selection of such files only.
    Command {
        /// The command's ID, such as `pane.openSelected` or `program.code`.
        command: String,
        /// Extensions with their dot, such as `.md`; case does not matter.
        extensions: Option<Vec<String>>,
    },
    /// A divider line.
    Separator,
}

impl MenuItem {
    /// A command for every file.
    #[must_use]
    pub fn command(id: &str) -> Self {
        Self::Command {
            command: id.to_owned(),
            extensions: None,
        }
    }
}

#[cfg(feature = "schema")]
impl schemars::JsonSchema for MenuItem {
    fn schema_name() -> std::borrow::Cow<'static, str> {
        "MenuItem".into()
    }

    fn json_schema(generator: &mut schemars::SchemaGenerator) -> schemars::Schema {
        RawMenuItem::json_schema(generator)
    }
}

/// A menu row as the file writes it.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct RawMenuItem {
    /// The command's ID, such as `pane.openSelected` or `program.code`. An
    /// ID no command has (a plugin that is off) is left out of the menu.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    command: Option<String>,
    /// Show the command only for files with one of these extensions, each
    /// with its dot (`.md`); case does not matter. With several rows
    /// selected, every selected file must have one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    extensions: Option<Vec<String>>,
    /// `true`: a divider line instead of a command.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    separator: bool,
}

impl TryFrom<RawMenuItem> for MenuItem {
    type Error = String;

    fn try_from(raw: RawMenuItem) -> Result<Self, String> {
        match (raw.command, raw.separator) {
            (Some(_), true) => Err("a menu item is a command or a separator, not both".to_owned()),
            (None, true) if raw.extensions.is_some() => {
                Err("a separator has no extensions".to_owned())
            }
            (None, true) => Ok(Self::Separator),
            (None, false) => Err(
                "a menu item needs `command` (a command ID) or `\"separator\": true`".to_owned(),
            ),
            (Some(command), false) if command.trim().is_empty() => {
                Err("a menu item's command is empty".to_owned())
            }
            // The extensions are checked with the rest of the file (parse.rs),
            // so an error points at the extension, not at the item's end.
            (Some(command), false) => Ok(Self::Command {
                command,
                extensions: raw.extensions,
            }),
        }
    }
}

impl From<MenuItem> for RawMenuItem {
    fn from(item: MenuItem) -> Self {
        match item {
            MenuItem::Command {
                command,
                extensions,
            } => Self {
                command: Some(command),
                extensions,
                separator: false,
            },
            MenuItem::Separator => Self {
                separator: true,
                ..Self::default()
            },
        }
    }
}

/// An extension as a menu filter takes it: a dot, then a name with no
/// further dot and no folder separator, since a file's extension starts at
/// the last dot of its name.
pub(crate) fn check_extension(extension: &str) -> Result<(), String> {
    let rest = extension.strip_prefix('.').unwrap_or("");
    if rest.is_empty() || rest.contains(['.', '\\', '/']) || rest.trim() != rest {
        return Err(format!(
            "`{extension}` is not an extension; write it with its dot and nothing else, such as `.md`"
        ));
    }
    Ok(())
}

/// A program a menu entry, a key or the palette may start: the command
/// `program.<name>`. Nothing else can be started from the menu.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct ProgramEntry {
    /// The name of its command, `program.<name>`: lowercase letters, digits
    /// and `-`, starting with a letter; unique.
    pub name: String,
    /// What the menu and the palette show; without it, the name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    /// The program: a full path, or a name found on the PATH, as for
    /// `files.editor`; never the current folder.
    pub command: String,
    /// Its arguments. `{path}` is the focused file or folder, `{cwd}` the
    /// active pane's folder, and `{selection}`, which must be an argument
    /// of its own, becomes one argument per selected path (the focused one
    /// when nothing is marked). No other `{token}` is allowed.
    #[serde(default)]
    pub args: Vec<String>,
}

impl ProgramEntry {
    /// The ID of the command this program becomes.
    #[must_use]
    pub fn command_id(&self) -> String {
        format!("{PROGRAM_PREFIX}{}", self.name)
    }

    /// What the menu and the palette show.
    #[must_use]
    pub fn shown_title(&self) -> &str {
        self.title
            .as_deref()
            .filter(|title| !title.trim().is_empty())
            .unwrap_or(&self.name)
    }
}

/// Whether `name` can name a program: `^[a-z][a-z0-9-]*$`.
#[must_use]
pub fn is_program_name(name: &str) -> bool {
    let mut chars = name.chars();
    chars.next().is_some_and(|first| first.is_ascii_lowercase())
        && chars.all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
}

/// One piece of a program's argument.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ArgPart<'a> {
    /// Text as written.
    Text(&'a str),
    /// `{path}`: the focused entry.
    Path,
    /// `{selection}`: every selected path, one argument each.
    Selection,
    /// `{cwd}`: the active pane's folder.
    Cwd,
}

/// Splits one argument into text and tokens. A `{word}` that is not one of
/// [`PROGRAM_TOKENS`] is an error, and so is `{selection}` inside other
/// text: it stands for several arguments. Other braces are text.
pub fn parse_arg(arg: &str) -> Result<Vec<ArgPart<'_>>, String> {
    let mut parts = Vec::new();
    let mut text_start = 0;
    let mut search = 0;
    while let Some(found) = arg[search..].find('{') {
        let open = search + found;
        let Some(length) = arg[open + 1..].find('}') else {
            break;
        };
        let close = open + 1 + length;
        let word = &arg[open + 1..close];
        let is_word = !word.is_empty()
            && word
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-');
        if !is_word {
            search = open + 1;
            continue;
        }
        let token = match word {
            "path" => ArgPart::Path,
            "selection" => ArgPart::Selection,
            "cwd" => ArgPart::Cwd,
            _ => {
                return Err(format!(
                    "`{{{word}}}` is not a token a program may use; only {}",
                    PROGRAM_TOKENS.join(", ")
                ));
            }
        };
        if open > text_start {
            parts.push(ArgPart::Text(&arg[text_start..open]));
        }
        parts.push(token);
        text_start = close + 1;
        search = close + 1;
    }
    if text_start < arg.len() {
        parts.push(ArgPart::Text(&arg[text_start..]));
    }
    if parts.len() > 1 && parts.contains(&ArgPart::Selection) {
        return Err(
            "`{selection}` must be an argument of its own: it becomes one argument per selected path"
                .to_owned(),
        );
    }
    Ok(parts)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arguments_split_into_text_and_tokens() {
        assert_eq!(parse_arg("--wait").unwrap(), [ArgPart::Text("--wait")]);
        assert_eq!(parse_arg("{path}").unwrap(), [ArgPart::Path]);
        assert_eq!(
            parse_arg("--goto={path}:1").unwrap(),
            [ArgPart::Text("--goto="), ArgPart::Path, ArgPart::Text(":1")]
        );
        assert_eq!(
            parse_arg(r"{cwd}\{path}").unwrap(),
            [ArgPart::Cwd, ArgPart::Text(r"\"), ArgPart::Path]
        );
        assert_eq!(parse_arg("{selection}").unwrap(), [ArgPart::Selection]);
        // Braces around anything that is not a word stay text, in one piece.
        assert_eq!(
            parse_arg(r#"{"a": 1} {} {x y}"#).unwrap(),
            [ArgPart::Text(r#"{"a": 1} {} {x y}"#)]
        );
        assert_eq!(parse_arg("{path").unwrap(), [ArgPart::Text("{path")]);
        assert!(parse_arg("").unwrap().is_empty());
        let error = parse_arg("{file}").unwrap_err();
        assert!(
            error.contains("`{file}`") && error.contains("{path}, {selection}, {cwd}"),
            "{error}"
        );
        assert!(parse_arg("{PATH}").is_err());
        let error = parse_arg("--files={selection}").unwrap_err();
        assert!(error.contains("an argument of its own"), "{error}");
    }

    #[test]
    fn menu_items_are_a_command_or_a_separator() {
        let item: MenuItem =
            serde_json::from_str(r#"{"command": "program.code", "extensions": [".md", ".RS"]}"#)
                .unwrap();
        assert_eq!(
            item,
            MenuItem::Command {
                command: "program.code".to_owned(),
                extensions: Some(vec![".md".to_owned(), ".RS".to_owned()])
            }
        );
        let item: MenuItem = serde_json::from_str(r#"{"separator": true}"#).unwrap();
        assert_eq!(item, MenuItem::Separator);
        assert_eq!(
            serde_json::to_string(&MenuItem::Separator).unwrap(),
            r#"{"separator":true}"#
        );
        assert_eq!(
            serde_json::to_string(&MenuItem::command("edit.cut")).unwrap(),
            r#"{"command":"edit.cut"}"#
        );
        for (bad, expected) in [
            (r"{}", "needs `command`"),
            (r#"{"separator": false}"#, "needs `command`"),
            (r#"{"command": "a.b", "separator": true}"#, "not both"),
            (
                r#"{"separator": true, "extensions": [".md"]}"#,
                "no extensions",
            ),
            (r#"{"command": " "}"#, "is empty"),
            (r#"{"command": "a.b", "icon": "x"}"#, "unknown field `icon`"),
        ] {
            let error = serde_json::from_str::<MenuItem>(bad)
                .unwrap_err()
                .to_string();
            assert!(error.contains(expected), "{bad}: {error}");
        }
    }

    #[test]
    fn an_extension_is_a_dot_and_a_name() {
        for good in [".md", ".RS", ".gitignore", ".7z"] {
            assert!(check_extension(good).is_ok(), "{good}");
        }
        for bad in ["md", ".", "", ".tar.gz", r".a\b", ".a/b", ". md", ".md "] {
            let error = check_extension(bad).unwrap_err();
            assert!(
                error.contains(&format!("`{bad}` is not an extension")),
                "{error}"
            );
        }
    }

    #[test]
    fn the_defaults_are_the_menus_of_phase_5() {
        let menu = ContextMenuConfig::default();
        assert!(!menu.shell_menu);
        let ids = |items: &[MenuItem]| -> Vec<String> {
            items
                .iter()
                .map(|item| match item {
                    MenuItem::Command { command, .. } => command.clone(),
                    MenuItem::Separator => "-".to_owned(),
                })
                .collect()
        };
        for (name, quick, items) in menu.targets() {
            if name == "background" {
                assert!(quick.is_empty());
                assert_eq!(ids(items), ["edit.paste", "file.newFolder", "sidebar.pin"]);
            } else {
                assert_eq!(
                    quick,
                    [
                        "edit.cut",
                        "edit.copy",
                        "edit.paste",
                        "file.rename",
                        "file.delete"
                    ],
                    "{name}"
                );
                assert_eq!(
                    ids(items),
                    [
                        "pane.openSelected",
                        "file.openInOtherPane",
                        "file.copyToOtherPane",
                        "terminal.new"
                    ],
                    "{name}"
                );
            }
        }
        // A target that names only its rows keeps its icon row.
        let menu: ContextMenuConfig =
            serde_json::from_str(r#"{"file": {"items": [{"command": "program.code"}]}}"#).unwrap();
        assert_eq!(menu.file.quick_actions.len(), 5);
        assert_eq!(menu.file.items, [MenuItem::command("program.code")]);
        assert_eq!(menu.folder, FolderMenu::default());
        assert!(serde_json::from_str::<ContextMenuConfig>(r#"{"row": {}}"#).is_err());
        assert!(serde_json::from_str::<ContextMenuConfig>(r#"{"file": {"quick": []}}"#).is_err());
    }

    #[test]
    fn a_program_has_a_command_id_and_a_title() {
        let program: ProgramEntry =
            serde_json::from_str(r#"{"name": "code", "command": "code", "args": ["{path}"]}"#)
                .unwrap();
        assert_eq!(program.command_id(), "program.code");
        assert_eq!(program.shown_title(), "code");
        let titled = ProgramEntry {
            title: Some("Open in Code".to_owned()),
            ..program.clone()
        };
        assert_eq!(titled.shown_title(), "Open in Code");
        assert_eq!(
            serde_json::to_string(&program).unwrap(),
            r#"{"name":"code","command":"code","args":["{path}"]}"#
        );
        assert!(serde_json::from_str::<ProgramEntry>(r#"{"name": "a"}"#).is_err());
        assert!(
            serde_json::from_str::<ProgramEntry>(r#"{"name": "a", "command": "a", "cwd": "x"}"#)
                .is_err()
        );
    }

    #[test]
    fn program_names_follow_the_pattern() {
        for good in ["code", "a", "vs-code", "x1", "open-2"] {
            assert!(is_program_name(good), "{good}");
        }
        for bad in ["", "Code", "1x", "-a", "a_b", "a.b", "a b", "é"] {
            assert!(!is_program_name(bad), "{bad}");
        }
    }
}
