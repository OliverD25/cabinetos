//! Reading `cabinetos.json`: strict JSON, then the checks serde cannot make.

use std::borrow::Cow;
use std::fmt;

use cabinetos_commands::KeymapError;

use crate::locate::{Segment, locate, position};
use crate::{Config, FORMAT_VERSION};

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
        assert_eq!(config.terminal.profiles.len(), 3);
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
