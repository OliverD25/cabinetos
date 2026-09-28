//! Finding where a value sits in the JSON text, so an error found after
//! parsing (a keybinding conflict, a missing terminal profile) can still name
//! its line and column.

/// One step of a path into a JSON document.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Segment<'a> {
    /// A member of an object.
    Key(&'a str),
    /// An element of an array.
    Index(usize),
}

/// The line and column (both from 1, the column in characters) where the
/// value at `path` starts in `text`, which must be valid JSON. `None` if the
/// path is not in the document.
pub(crate) fn locate(text: &str, path: &[Segment<'_>]) -> Option<(u32, u32)> {
    let mut scanner = Scanner {
        bytes: text.as_bytes(),
        at: 0,
    };
    let at = scanner.find(path)?;
    Some(position(text, at))
}

/// The line and column (from 1, in characters) of byte offset `at`.
pub(crate) fn position(text: &str, at: usize) -> (u32, u32) {
    let before = &text[..at.min(text.len())];
    let line = before.matches('\n').count() + 1;
    let line_start = before.rfind('\n').map_or(0, |newline| newline + 1);
    let column = before[line_start..].chars().count() + 1;
    (
        u32::try_from(line).unwrap_or(u32::MAX),
        u32::try_from(column).unwrap_or(u32::MAX),
    )
}

struct Scanner<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl Scanner<'_> {
    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.at).copied()
    }

    fn skip_whitespace(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.at += 1;
        }
    }

    /// The byte offset of the value at `path`, starting from the value here.
    fn find(&mut self, path: &[Segment<'_>]) -> Option<usize> {
        self.skip_whitespace();
        let Some((segment, rest)) = path.split_first() else {
            return Some(self.at);
        };
        match (self.peek()?, segment) {
            (b'{', Segment::Key(wanted)) => {
                self.at += 1;
                loop {
                    self.skip_whitespace();
                    if self.peek()? == b'}' {
                        return None;
                    }
                    let key = self.string()?;
                    self.skip_whitespace();
                    if self.peek()? != b':' {
                        return None;
                    }
                    self.at += 1;
                    if key == *wanted {
                        return self.find(rest);
                    }
                    self.skip_value()?;
                    self.skip_whitespace();
                    if self.peek()? == b',' {
                        self.at += 1;
                    }
                }
            }
            (b'[', Segment::Index(wanted)) => {
                self.at += 1;
                let mut index = 0;
                loop {
                    self.skip_whitespace();
                    if self.peek()? == b']' {
                        return None;
                    }
                    if index == *wanted {
                        return self.find(rest);
                    }
                    self.skip_value()?;
                    self.skip_whitespace();
                    if self.peek()? == b',' {
                        self.at += 1;
                    }
                    index += 1;
                }
            }
            _ => None,
        }
    }

    /// Reads a string (the scanner is at its opening quote) and returns its
    /// value; escapes other than `\"` and `\\` are kept as written, which is
    /// enough to compare the ASCII keys of the configuration.
    fn string(&mut self) -> Option<String> {
        if self.peek()? != b'"' {
            return None;
        }
        self.at += 1;
        let mut value = Vec::new();
        loop {
            match self.peek()? {
                b'"' => {
                    self.at += 1;
                    return String::from_utf8(value).ok();
                }
                b'\\' => {
                    let escaped = *self.bytes.get(self.at + 1)?;
                    match escaped {
                        b'"' | b'\\' => value.push(escaped),
                        other => value.extend_from_slice(&[b'\\', other]),
                    }
                    self.at += 2;
                }
                byte => {
                    value.push(byte);
                    self.at += 1;
                }
            }
        }
    }

    fn skip_value(&mut self) -> Option<()> {
        self.skip_whitespace();
        match self.peek()? {
            b'"' => self.string().map(|_| ()),
            open @ (b'{' | b'[') => {
                let close = if open == b'{' { b'}' } else { b']' };
                self.at += 1;
                loop {
                    self.skip_whitespace();
                    match self.peek()? {
                        byte if byte == close => {
                            self.at += 1;
                            return Some(());
                        }
                        b',' | b':' => self.at += 1,
                        _ => self.skip_value()?,
                    }
                }
            }
            _ => {
                while !matches!(
                    self.peek(),
                    None | Some(b',' | b'}' | b']' | b' ' | b'\t' | b'\n' | b'\r')
                ) {
                    self.at += 1;
                }
                Some(())
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Segment::{Index, Key};
    use super::*;

    const TEXT: &str = r#"{
  "ui": { "layout": "rail", "note": "a \"quoted\" {brace}" },
  "keybindings": [
    { "command": "view.toggleSidebar", "keys": "ctrl+b" },
    { "command": "help.about", "keys": "ctrl+shift+p" }
  ],
  "названня": [1, [2, 3], {"x": null}]
}"#;

    #[test]
    fn finds_members_and_elements() {
        assert_eq!(locate(TEXT, &[]), Some((1, 1)));
        assert_eq!(locate(TEXT, &[Key("ui"), Key("layout")]), Some((2, 21)));
        assert_eq!(locate(TEXT, &[Key("keybindings"), Index(0)]), Some((4, 5)));
        assert_eq!(
            locate(TEXT, &[Key("keybindings"), Index(1), Key("keys")]),
            Some((5, 40))
        );
    }

    #[test]
    fn skips_strings_with_braces_and_nested_values() {
        assert_eq!(locate(TEXT, &[Key("keybindings")]), Some((3, 18)));
        assert_eq!(
            locate(TEXT, &[Key("названня"), Index(2), Key("x")]),
            Some((7, 33))
        );
    }

    #[test]
    fn missing_paths_are_none() {
        assert_eq!(locate(TEXT, &[Key("nope")]), None);
        assert_eq!(locate(TEXT, &[Key("keybindings"), Index(9)]), None);
        assert_eq!(locate(TEXT, &[Key("ui"), Index(0)]), None);
    }

    #[test]
    fn columns_count_characters() {
        assert_eq!(position("ab\nпр", 5), (2, 2));
    }
}
