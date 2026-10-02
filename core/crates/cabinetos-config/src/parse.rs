//! Reading `cabinetos.json`: strict JSON, then the checks serde cannot make.

use std::borrow::Cow;
use std::fmt;

use cabinetos_commands::KeymapError;

use crate::locate::{Segment, locate, position};
use crate::menu::check_extension;
use crate::{
    Config, FORMAT_VERSION, MAX_COLUMN_WIDTH, MAX_COMPACT_SIZE, MIN_COLUMN_WIDTH, MIN_COMPACT_SIZE,
    MenuItem, is_program_name, parse_arg,
};

/// A configuration file that cannot be used. The settings in effect stay as
/// they were.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ConfigError {
    /// The line of the problem, from 1, when known.
    pub line: Option<u32>,
    /// The column of the problem, from 1 and in characters, when known.
    pub column: Option<u32>,
    /// What is wrong.
    pub message: String,
}

impl ConfigError {
    /// An error without a position, such as a file that cannot be read.
    #[must_use]
    pub fn general(message: impl Into<String>) -> Self {
        Self {
            line: None,
            column: None,
            message: message.into(),
        }
    }

    pub(crate) fn at(text: &str, path: &[Segment<'_>], message: impl Into<String>) -> Self {
        let (line, column) = locate(text, path).unzip();
        Self {
            line,
            column,
            message: message.into(),
        }
    }
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match (self.line, self.column) {
            (Some(line), Some(column)) => {
                write!(f, "line {line}, column {column}: {}", self.message)
            }
            (Some(line), None) => write!(f, "line {line}: {}", self.message),
            _ => f.write_str(&self.message),
        }
    }
}

impl std::error::Error for ConfigError {}

/// A caller's objection to a configuration that parsed, such as a keybinding
/// conflict. `keybinding` is the index of the `keybindings` entry at fault.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Rejection {
    /// The `keybindings` entry at fault, if one is.
    pub keybinding: Option<usize>,
    /// What is wrong.
    pub message: String,
}

impl From<KeymapError> for Rejection {
    fn from(error: KeymapError) -> Self {
        Self {
            keybinding: error.entry(),
            message: error.to_string(),
        }
    }
}

impl Rejection {
    pub(crate) fn into_error(self, text: &str) -> ConfigError {
        match self.keybinding {
            Some(index) => ConfigError::at(
                text,
                &[Segment::Key("keybindings"), Segment::Index(index)],
                self.message,
            ),
            None => ConfigError::general(self.message),
        }
    }
}

/// The text of a configuration file's bytes: UTF-8, with or without a
/// byte-order mark ([`parse`] drops it), or UTF-16 with its byte-order
/// mark, which Windows PowerShell 5.1's `>` and `Out-File` write. The core
/// writes UTF-8 back.
///
/// # Errors
///
/// When the bytes are neither.
#[must_use = "the text or the reason there is none"]
pub fn file_text(bytes: &[u8]) -> Result<Cow<'_, str>, ConfigError> {
    let utf16 = |rest: &[u8], unit: fn([u8; 2]) -> u16| {
        let (pairs, odd) = rest.as_chunks::<2>();
        if !odd.is_empty() {
            return Err(ConfigError::general(
                "the file starts with the byte-order mark of UTF-16 text, \
                 but its length is an odd number of bytes",
            ));
        }
        String::from_utf16(&pairs.iter().map(|pair| unit(*pair)).collect::<Vec<u16>>())
            .map(Cow::Owned)
            .map_err(|error| ConfigError::general(format!("the file is not UTF-16 text: {error}")))
    };
    match bytes {
        [0xFF, 0xFE, rest @ ..] => utf16(rest, u16::from_le_bytes),
        [0xFE, 0xFF, rest @ ..] => utf16(rest, u16::from_be_bytes),
        _ => std::str::from_utf8(bytes)
            .map(Cow::Borrowed)
            .map_err(|error| ConfigError::general(format!("the file is not UTF-8 text: {error}"))),
    }
}

/// Parses and checks the text of a configuration file. A byte-order mark at
/// the start, which Notepad and others may write, is ignored.
pub fn parse(text: &str) -> Result<Config, ConfigError> {
    let text = without_byte_order_mark(text);
    let config: Config = serde_json::from_str(text).map_err(|error| serde_error(text, &error))?;
    check(text, &config)?;
    Ok(config)
}

/// Parses the text like [`parse`], then runs the caller's checks on the
/// result, such as keybinding conflicts. A refusal that names a
/// `keybindings` entry points at that entry's line.
pub fn parse_checked(
    text: &str,
    validate: impl FnOnce(&Config) -> Result<(), Rejection>,
) -> Result<Config, ConfigError> {
    let text = without_byte_order_mark(text);
    let config = parse(text)?;
    validate(&config).map_err(|rejection| rejection.into_error(text))?;
    Ok(config)
}

fn without_byte_order_mark(text: &str) -> &str {
    text.strip_prefix('\u{feff}').unwrap_or(text)
}

/// The checks of [`parse`] that go beyond the shape of the file, for
/// settings that come from elsewhere (`set_value`): the message alone, as
/// there is no text to point into.
pub(crate) fn check_values(config: &Config) -> Result<(), String> {
    check("", config).map_err(|error| error.message)
}

/// The checks that go beyond the shape of the file.
fn check(text: &str, config: &Config) -> Result<(), ConfigError> {
    if config.version != FORMAT_VERSION {
        return Err(ConfigError::at(
            text,
            &[Segment::Key("version")],
            format!(
                "version {} is not supported; this build reads version {FORMAT_VERSION}",
                config.version
            ),
        ));
    }
    if let Some(editor) = &config.files.editor
        && editor.command.trim().is_empty()
    {
        return Err(ConfigError::at(
            text,
            &[
                Segment::Key("files"),
                Segment::Key("editor"),
                Segment::Key("command"),
            ],
            "files.editor.command is empty; name the editor's program, or set files.editor to null"
                .to_owned(),
        ));
    }
    for (pane, tabs) in [
        ("left", &config.ui.tabs.left),
        ("right", &config.ui.tabs.right),
    ] {
        let count = tabs.items.len();
        if usize::try_from(tabs.active).map_or(true, |active| active >= count.max(1)) {
            return Err(ConfigError::at(
                text,
                &[
                    Segment::Key("ui"),
                    Segment::Key("tabs"),
                    Segment::Key(pane),
                    Segment::Key("active"),
                ],
                format!(
                    "ui.tabs.{pane}.active is {}, but the {pane} pane has {count} tab{}; it counts from 0",
                    tabs.active,
                    if count == 1 { "" } else { "s" }
                ),
            ));
        }
    }
    check_columns(text, config)?;
    check_compact_overlay(text, config)?;
    check_programs(text, config)?;
    check_menu_extensions(text, config)?;
    let profiles = &config.terminal.profiles;
    for (index, profile) in profiles.iter().enumerate() {
        if profiles[..index]
            .iter()
            .any(|earlier| earlier.name == profile.name)
        {
            return Err(ConfigError::at(
                text,
                &[
                    Segment::Key("terminal"),
                    Segment::Key("profiles"),
                    Segment::Index(index),
                    Segment::Key("name"),
                ],
                format!("two terminal profiles are named `{}`", profile.name),
            ));
        }
    }
    if !profiles
        .iter()
        .any(|profile| profile.name == config.terminal.default_profile)
    {
        return Err(ConfigError::at(
            text,
            &[Segment::Key("terminal"), Segment::Key("defaultProfile")],
            format!(
                "terminal.defaultProfile is `{}`, but no profile has that name",
                config.terminal.default_profile
            ),
        ));
    }
    Ok(())
}

/// `ui.columns`: each width in whole pixels from 24 to 2000, so a hand edit
/// cannot make a column vanish or push the others out of the pane.
fn check_columns(text: &str, config: &Config) -> Result<(), ConfigError> {
    let Some(columns) = &config.ui.columns else {
        return Ok(());
    };
    for (column, width) in [
        ("modified", columns.modified),
        ("type", columns.r#type),
        ("size", columns.size),
    ] {
        if !(MIN_COLUMN_WIDTH..=MAX_COLUMN_WIDTH).contains(&width) {
            return Err(ConfigError::at(
                text,
                &[
                    Segment::Key("ui"),
                    Segment::Key("columns"),
                    Segment::Key(column),
                ],
                format!(
                    "ui.columns.{column} is {width}; a column is from {MIN_COLUMN_WIDTH} to {MAX_COLUMN_WIDTH} pixels wide"
                ),
            ));
        }
    }
    Ok(())
}

/// `ui.compactOverlay`: each side in whole pixels from 240 to 4000, so a
/// hand edit cannot make a drawer too small to use or larger than a screen.
fn check_compact_overlay(text: &str, config: &Config) -> Result<(), ConfigError> {
    let Some(overlay) = &config.ui.compact_overlay else {
        return Ok(());
    };
    for (side, size) in [("width", overlay.width), ("height", overlay.height)] {
        if !(MIN_COMPACT_SIZE..=MAX_COMPACT_SIZE).contains(&size) {
            return Err(ConfigError::at(
                text,
                &[
                    Segment::Key("ui"),
                    Segment::Key("compactOverlay"),
                    Segment::Key(side),
                ],
                format!(
                    "ui.compactOverlay.{side} is {size}; the compact overlay is from {MIN_COMPACT_SIZE} to {MAX_COMPACT_SIZE} pixels"
                ),
            ));
        }
    }
    Ok(())
}

/// The `programs` list: each name follows the pattern and is unique, each
/// program is named, and the arguments hold only the three tokens. Whether
/// the program exists is checked when it runs, as for `files.editor`: a
/// program on a drive that is not there yet must not make the file invalid.
fn check_programs(text: &str, config: &Config) -> Result<(), ConfigError> {
    let programs = &config.programs;
    for (index, program) in programs.iter().enumerate() {
        let at = |key: &'static str| {
            [
                Segment::Key("programs"),
                Segment::Index(index),
                Segment::Key(key),
            ]
        };
        if !is_program_name(&program.name) {
            return Err(ConfigError::at(
                text,
                &at("name"),
                format!(
                    "programs[{index}].name is `{}`; a program's name is lowercase letters, digits and `-`, starting with a letter",
                    program.name
                ),
            ));
        }
        if programs[..index]
            .iter()
            .any(|earlier| earlier.name == program.name)
        {
            return Err(ConfigError::at(
                text,
                &at("name"),
                format!("two programs are named `{}`", program.name),
            ));
        }
        if program.command.trim().is_empty() {
            return Err(ConfigError::at(
                text,
                &at("command"),
                format!(
                    "programs[{index}].command is empty; name the program `{}` starts",
                    program.name
                ),
            ));
        }
        for (arg_index, arg) in program.args.iter().enumerate() {
            if let Err(message) = parse_arg(arg) {
                return Err(ConfigError::at(
                    text,
                    &[
                        Segment::Key("programs"),
                        Segment::Index(index),
                        Segment::Key("args"),
                        Segment::Index(arg_index),
                    ],
                    format!("programs[{index}].args[{arg_index}]: {message}"),
                ));
            }
        }
    }
    Ok(())
}

/// Every `extensions` filter of the menu: a dot and a name, as a file's
/// extension starts at its last dot.
fn check_menu_extensions(text: &str, config: &Config) -> Result<(), ConfigError> {
    for (target, _, items) in config.context_menu.targets() {
        for (index, item) in items.iter().enumerate() {
            let MenuItem::Command {
                extensions: Some(extensions),
                ..
            } = item
            else {
                continue;
            };
            for (at, extension) in extensions.iter().enumerate() {
                if let Err(message) = check_extension(extension) {
                    return Err(ConfigError::at(
                        text,
                        &[
                            Segment::Key("contextMenu"),
                            Segment::Key(target),
                            Segment::Key("items"),
                            Segment::Index(index),
                            Segment::Key("extensions"),
                            Segment::Index(at),
                        ],
                        format!("contextMenu.{target}.items[{index}]: {message}"),
                    ));
                }
            }
        }
    }
    Ok(())
}

/// A serde error with its position in characters and without the position
/// text serde appends to the message.
fn serde_error(text: &str, error: &serde_json::Error) -> ConfigError {
    let message = error.to_string();
    let message = message
        .rfind(" at line ")
        .map_or(message.as_str(), |cut| &message[..cut])
        .to_owned();
    if error.line() == 0 {
        return ConfigError::general(message);
    }
    // serde counts columns in bytes; editors count characters.
    let line_start = text
        .split_inclusive('\n')
        .take(error.line() - 1)
        .map(str::len)
        .sum::<usize>();
    let byte_column = error.column().saturating_sub(1);
    let mut at = (line_start + byte_column).min(text.len());
    while !text.is_char_boundary(at) {
        at -= 1;
    }
    let (line, column) = position(text, at);
    ConfigError {
        line: Some(line),
        column: Some(column),
        message,
    }
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::TerminalMode;

    use super::*;
    use crate::Layout;

    #[test]
    fn an_empty_object_is_all_defaults() {
        assert_eq!(parse("{}").unwrap(), Config::default());
    }

    #[test]
    fn a_partial_file_keeps_the_other_defaults() {
        let config = parse(r#"{ "ui": { "layout": "rail" } }"#).unwrap();
        assert_eq!(config.ui.layout, Layout::Rail);
        assert!(config.ui.dual_pane);
        assert_eq!(config.terminal.profiles.len(), 4);
    }

    #[test]
    fn an_unknown_key_is_an_error_naming_the_key_and_its_place() {
        let error = parse("{\n  \"ui\": {\n    \"dualPan\": false\n  }\n}").unwrap_err();
        assert!(error.message.contains("unknown field `dualPan`"), "{error}");
        assert!(
            error.message.contains("dualPane"),
            "suggests the known keys: {error}"
        );
        assert_eq!(error.line, Some(3));
        assert!(!error.message.contains(" at line "), "{error}");
    }

    #[test]
    fn bad_values_and_bad_json_have_positions() {
        let error = parse(r#"{ "ui": { "layout": "sideways" } }"#).unwrap_err();
        assert!(error.message.contains("sideways"), "{error}");
        assert_eq!(error.line, Some(1));

        let error = parse("{\n  \"version\": 1,\n}").unwrap_err();
        assert_eq!(error.line, Some(3), "{error}");

        let error =
            parse(r#"{"keybindings":[{"command":"view.toggleSidebar","keys":"ctrl+nope"}]}"#)
                .unwrap_err();
        assert!(
            error.message.contains("`nope` is not a key name"),
            "{error}"
        );
    }

    #[test]
    fn columns_count_characters_not_bytes() {
        let error = parse("{\"ui\": {\"theme\": \"Привіт\", \"x\": 1}}").unwrap_err();
        assert_eq!(error.line, Some(1));
        // `"x"` starts at character 28; in bytes it would be 34.
        assert!(error.column.unwrap() <= 32, "{error}");
    }

    #[test]
    fn an_editor_needs_a_program() {
        let error = parse(r#"{"files": {"editor": {"command": "  ", "args": []}}}"#).unwrap_err();
        assert!(
            error.message.contains("files.editor.command is empty"),
            "{error}"
        );
        assert_eq!(error.line, Some(1), "{error}");
        assert!(parse(r#"{"files": {"editor": {"command": "notepad++.exe"}}}"#).is_ok());
    }

    #[test]
    fn programs_are_checked_and_errors_point_at_their_line() {
        let config = parse(
            r#"{"programs": [{"name": "code", "title": "Open in Code", "command": "code", "args": ["--goto", "{path}", "{selection}", "{cwd}"]}]}"#,
        )
        .unwrap();
        assert_eq!(config.programs[0].command_id(), "program.code");
        for (bad, line, expected) in [
            (
                "{\"programs\": [\n{\"name\": \"Code\", \"command\": \"code\"}]}",
                2,
                "programs[0].name is `Code`",
            ),
            (
                "{\"programs\": [{\"name\": \"a\", \"command\": \"a\"},\n{\"name\": \"a\", \"command\": \"b\"}]}",
                2,
                "two programs are named `a`",
            ),
            (
                "{\"programs\": [\n{\"name\": \"a\", \"command\": \" \"}]}",
                2,
                "programs[0].command is empty",
            ),
            (
                "{\"programs\": [{\"name\": \"a\", \"command\": \"a\",\n\"args\": [\"x\",\n\"{file}\"]}]}",
                3,
                "programs[0].args[1]: `{file}`",
            ),
            (
                "{\"programs\": [{\"name\": \"a\", \"command\": \"a\", \"args\": [\"-f={selection}\"]}]}",
                1,
                "an argument of its own",
            ),
        ] {
            let error = parse(bad).unwrap_err();
            assert!(error.message.contains(expected), "{bad}: {error}");
            assert_eq!(error.line, Some(line), "{bad}: {error}");
        }
    }

    #[test]
    fn a_bad_menu_item_names_its_line() {
        let text = "{\"contextMenu\": {\"file\": {\"items\": [\n{\"command\": \"pane.openSelected\"},\n{\"command\": \"program.x\", \"extensions\": [\"md\"]}\n]}}}";
        let error = parse(text).unwrap_err();
        assert!(
            error
                .message
                .contains("contextMenu.file.items[1]: `md` is not an extension"),
            "{error}"
        );
        assert_eq!(error.line, Some(3), "{error}");
        let error = parse("{\"contextMenu\": {\"folder\": {\"items\": [\n{\"separator\": true, \"command\": \"x\"}\n]}}}").unwrap_err();
        assert!(error.message.contains("not both"), "{error}");
        // An ID no command has is not an error: a plugin may be off.
        let config = parse(
            r#"{"contextMenu": {"shellMenu": true, "background": {"items": [{"command": "no.such"}, {"separator": true}]}}}"#,
        )
        .unwrap();
        assert!(config.context_menu.shell_menu);
        assert_eq!(config.context_menu.background.items.len(), 2);
    }

    #[test]
    fn version_and_terminal_profiles_are_checked() {
        let error = parse("{\n\"version\": 2\n}").unwrap_err();
        assert_eq!((error.line, error.column), (Some(2), Some(12)));
        assert!(error.message.contains("version 2"));

        let error = parse(r#"{"terminal": {"defaultProfile": "fish"}}"#).unwrap_err();
        assert!(error.message.contains("fish"), "{error}");

        let error = parse(
            r#"{"terminal": {"profiles": [{"name": "a", "command": "a.exe"}, {"name": "a", "command": "b.exe"}], "defaultProfile": "a"}}"#,
        )
        .unwrap_err();
        assert!(error.message.contains("two terminal profiles"), "{error}");

        let error = parse(
            r#"{"terminal": {"profiles": [{"name": "a", "command": "a.exe", "linkable": "yes"}], "defaultProfile": "a"}}"#,
        )
        .unwrap_err();
        assert!(error.message.contains("invalid type: string"), "{error}");
        let config = parse(
            r#"{"terminal": {"profiles": [{"name": "a", "command": "a.exe"}], "defaultProfile": "a"}}"#,
        )
        .unwrap();
        assert_eq!(config.terminal.profiles[0].linkable, None);
        // A file written before unit 1 of the terminal sprint still loads.
        let config = parse(
            r#"{"terminal": {"profiles": [{"name": "a", "command": "a.exe", "followsPane": false}], "defaultProfile": "a"}}"#,
        )
        .unwrap();
        assert_eq!(config.terminal.profiles[0].follows_pane, Some(false));
    }

    #[test]
    fn the_terminal_restores_and_starts_in_the_mode_the_file_says() {
        let config = parse(r#"{"terminal": {"restore": false, "defaultMode": "linked"}}"#).unwrap();
        assert!(!config.terminal.restore);
        assert_eq!(config.terminal.default_mode, TerminalMode::Linked);
        let config = parse("{}").unwrap();
        assert!(config.terminal.restore);
        assert_eq!(config.terminal.default_mode, TerminalMode::Locked);
        // A word that is not a mode names the line and the words that are.
        let error = parse("{\n\"terminal\": {\"defaultMode\": \"following\"}\n}").unwrap_err();
        assert!(
            error.message.contains("following") && error.message.contains("linked"),
            "{error}"
        );
        assert_eq!(error.line, Some(2), "{error}");
        let error = parse(r#"{"terminal": {"restore": "yes"}}"#).unwrap_err();
        assert!(error.message.contains("invalid type: string"), "{error}");
    }

    #[test]
    fn a_tab_index_past_the_end_names_the_pane() {
        let text = "{\n  \"ui\": {\n    \"tabs\": {\n      \"right\": {\"items\": [{\"path\": \"C:\\\\\"}], \"active\": 1}\n    }\n  }\n}";
        let error = parse(text).unwrap_err();
        assert!(
            error.message.contains("ui.tabs.right.active is 1")
                && error.message.contains("the right pane has 1 tab;"),
            "{error}"
        );
        assert_eq!(error.line, Some(4), "{error}");
        let error = parse(r#"{"ui": {"tabs": {"left": {"active": 2}}}}"#).unwrap_err();
        assert!(
            error.message.contains("ui.tabs.left.active is 2") && error.message.contains("0 tabs"),
            "{error}"
        );
        // No tabs and 0 in front is the default; the last tab in front is fine.
        assert!(parse(r#"{"ui": {"tabs": {"left": {"items": [], "active": 0}}}}"#).is_ok());
        assert!(
            parse(r#"{"ui": {"tabs": {"left": {"items": [{"path": "C:\\"}, {"path": "D:\\"}], "active": 1}}}}"#)
                .is_ok()
        );
    }

    #[test]
    fn column_widths_are_whole_pixels_from_24_to_2000() {
        let config =
            parse(r#"{"ui": {"columns": {"modified": 24, "type": 2000, "size": 64}}}"#).unwrap();
        let columns = config.ui.columns.unwrap();
        assert_eq!(
            (columns.modified, columns.r#type, columns.size),
            (24, 2000, 64)
        );
        assert_eq!(
            parse(r#"{"ui": {"columns": null}}"#).unwrap().ui.columns,
            None
        );
        let text = "{\n  \"ui\": {\n    \"columns\": {\n      \"modified\": 120,\n      \"type\": 23,\n      \"size\": 64\n    }\n  }\n}";
        let error = parse(text).unwrap_err();
        assert!(
            error.message.contains("ui.columns.type is 23") && error.message.contains("24 to 2000"),
            "{error}"
        );
        assert_eq!(error.line, Some(5), "{error}");
        for (bad, expected) in [
            (
                r#"{"ui": {"columns": {"modified": 120, "type": 90, "size": 2001}}}"#,
                "ui.columns.size is 2001",
            ),
            (
                r#"{"ui": {"columns": {"modified": 120.5, "type": 90, "size": 64}}}"#,
                "invalid type: floating point",
            ),
            (
                r#"{"ui": {"columns": {"modified": 120, "type": 90}}}"#,
                "missing field `size`",
            ),
            (
                r#"{"ui": {"columns": {"modified": 120, "type": 90, "size": 64, "name": 300}}}"#,
                "unknown field `name`",
            ),
        ] {
            let error = parse(bad).unwrap_err();
            assert!(error.message.contains(expected), "{bad}: {error}");
        }
    }

    #[test]
    fn the_compact_overlay_is_whole_pixels_from_240_to_4000() {
        let config =
            parse(r#"{"ui": {"compactOverlay": {"width": 240, "height": 4000}}}"#).unwrap();
        let overlay = config.ui.compact_overlay.unwrap();
        assert_eq!((overlay.width, overlay.height), (240, 4000));
        assert_eq!(
            parse(r#"{"ui": {"compactOverlay": null}}"#)
                .unwrap()
                .ui
                .compact_overlay,
            None
        );
        let text = "{\n  \"ui\": {\n    \"compactOverlay\": {\n      \"width\": 480,\n      \"height\": 239\n    }\n  }\n}";
        let error = parse(text).unwrap_err();
        assert!(
            error.message.contains("ui.compactOverlay.height is 239")
                && error.message.contains("240 to 4000"),
            "{error}"
        );
        assert_eq!(error.line, Some(5), "{error}");
        for (bad, expected) in [
            (
                r#"{"ui": {"compactOverlay": {"width": 4001, "height": 640}}}"#,
                "ui.compactOverlay.width is 4001",
            ),
            (
                r#"{"ui": {"compactOverlay": {"width": 480.5, "height": 640}}}"#,
                "invalid type: floating point",
            ),
            (
                r#"{"ui": {"compactOverlay": {"width": 480}}}"#,
                "missing field `height`",
            ),
            (
                r#"{"ui": {"compactOverlay": {"width": 480, "height": 640, "top": 0}}}"#,
                "unknown field `top`",
            ),
        ] {
            let error = parse(bad).unwrap_err();
            assert!(error.message.contains(expected), "{bad}: {error}");
        }
    }

    #[test]
    fn rejections_point_at_their_keybinding() {
        let text = "{\n  \"keybindings\": [\n    {\"command\": \"a\", \"keys\": \"f1\"},\n    {\"command\": \"b\", \"keys\": \"f1\"}\n  ]\n}";
        let error = Rejection {
            keybinding: Some(1),
            message: "conflict".to_owned(),
        }
        .into_error(text);
        assert_eq!((error.line, error.column), (Some(4), Some(5)));
        assert_eq!(error.to_string(), "line 4, column 5: conflict");
    }

    #[test]
    fn parse_checked_runs_the_callers_check_and_ignores_a_byte_order_mark() {
        let text =
            "\u{feff}{\n  \"keybindings\": [\n    {\"command\": \"a\", \"keys\": \"f1\"}\n  ]\n}";
        let error = parse_checked(text, |config| {
            assert_eq!(config.keybindings.len(), 1);
            Err(Rejection {
                keybinding: Some(0),
                message: "no".to_owned(),
            })
        })
        .unwrap_err();
        assert_eq!((error.line, error.column), (Some(3), Some(5)));
        assert!(parse_checked(text, |_| Ok(())).is_ok());
    }
}
