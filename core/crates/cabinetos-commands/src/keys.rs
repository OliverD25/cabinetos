//! The key grammar (`docs/keybindings.md`).
//!
//! A binding is one combination (`ctrl+shift+p`) or a chord of two
//! (`ctrl+k ctrl+c`: the second within [`CHORD_WINDOW_MS`] of the first).
//! Text is parsed case-insensitively and normalized: lower case, modifiers in
//! the order `ctrl+shift+alt+win`, punctuation by name, one space between
//! combinations.
//!
//! [`CHORD_WINDOW_MS`]: crate::CHORD_WINDOW_MS

use std::fmt;
use std::str::FromStr;

/// The most combinations one binding may have. Two is enough to multiply the
/// shortcut space (Article 7) and keeps the UI's chord state machine simple.
pub const MAX_CHORDS: usize = 2;

/// Modifier keys, in the normalized order.
const MODIFIERS: [(u8, &str); 4] = [(1, "ctrl"), (2, "shift"), (4, "alt"), (8, "win")];

/// Other spellings accepted for modifiers.
const MODIFIER_ALIASES: [(&str, u8); 2] = [("control", 1), ("meta", 8)];

/// Named keys, besides letters, digits and F1–F24.
const NAMED_KEYS: [&str; 26] = [
    "escape",
    "enter",
    "tab",
    "space",
    "backspace",
    "delete",
    "insert",
    "home",
    "end",
    "pageup",
    "pagedown",
    "up",
    "down",
    "left",
    "right",
    "backquote",
    "comma",
    "period",
    "slash",
    "minus",
    "equal",
    "bracketleft",
    "bracketright",
    "backslash",
    "semicolon",
    "quote",
];

/// Other spellings accepted for keys: short names and the punctuation
/// characters themselves (`ctrl+`` ` is `ctrl+backquote`). `+` cannot be
/// written this way, because it separates modifiers; use `shift+equal`.
const KEY_ALIASES: [(&str, &str); 17] = [
    ("esc", "escape"),
    ("return", "enter"),
    ("del", "delete"),
    ("ins", "insert"),
    ("pgup", "pageup"),
    ("pgdn", "pagedown"),
    ("`", "backquote"),
    (",", "comma"),
    (".", "period"),
    ("/", "slash"),
    ("-", "minus"),
    ("=", "equal"),
    ("[", "bracketleft"),
    ("]", "bracketright"),
    ("\\", "backslash"),
    (";", "semicolon"),
    ("'", "quote"),
];

/// A key that is not a modifier.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Key {
    /// A letter `a`–`z` or a digit `0`–`9`.
    Char(char),
    /// F1–F24.
    Function(u8),
    /// A named key, such as `escape` or `bracketleft`.
    Named(&'static str),
}

impl Key {
    fn parse(name: &str) -> Option<Self> {
        let name = KEY_ALIASES
            .iter()
            .find(|(alias, _)| *alias == name)
            .map_or(name, |(_, canonical)| canonical);
        let mut chars = name.chars();
        if let (Some(c), None) = (chars.next(), chars.next())
            && (c.is_ascii_lowercase() || c.is_ascii_digit())
        {
            return Some(Self::Char(c));
        }
        if let Some(digits) = name.strip_prefix('f')
            && !digits.starts_with('0')
            && let Ok(number) = digits.parse::<u8>()
            && (1..=24).contains(&number)
        {
            return Some(Self::Function(number));
        }
        NAMED_KEYS
            .iter()
            .find(|named| **named == name)
            .map(|named| Self::Named(named))
    }
}

impl fmt::Display for Key {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Char(c) => write!(f, "{c}"),
            Self::Function(n) => write!(f, "f{n}"),
            Self::Named(name) => f.write_str(name),
        }
    }
}

/// One combination: modifiers held while one key is pressed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct KeyChord {
    /// Bit set: 1 ctrl, 2 shift, 4 alt, 8 win.
    modifiers: u8,
    key: Key,
}

impl KeyChord {
    /// The key pressed.
    #[must_use]
    pub fn key(&self) -> Key {
        self.key
    }
}

impl fmt::Display for KeyChord {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (bit, name) in MODIFIERS {
            if self.modifiers & bit != 0 {
                write!(f, "{name}+")?;
            }
        }
        write!(f, "{}", self.key)
    }
}

impl FromStr for KeyChord {
    type Err = KeyParseError;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        let error = |reason: String| KeyParseError {
            input: text.to_owned(),
            reason,
        };
        let lower = text.to_lowercase();
        let mut modifiers = 0u8;
        let mut key = None;
        for part in lower.split('+') {
            if part.is_empty() {
                return Err(error(
                    "a key name is missing between two `+` (to bind the + key, use `shift+equal`)"
                        .to_owned(),
                ));
            }
            let modifier = MODIFIERS
                .iter()
                .find(|(_, name)| *name == part)
                .map(|(bit, _)| *bit)
                .or_else(|| {
                    MODIFIER_ALIASES
                        .iter()
                        .find(|(alias, _)| *alias == part)
                        .map(|(_, bit)| *bit)
                });
            if let Some(bit) = modifier {
                if modifiers & bit != 0 {
                    return Err(error(format!("`{part}` appears twice")));
                }
                modifiers |= bit;
            } else {
                let parsed =
                    Key::parse(part).ok_or_else(|| error(format!("`{part}` is not a key name")))?;
                if key.replace(parsed).is_some() {
                    return Err(error(
                        "a combination has exactly one key besides modifiers".to_owned(),
                    ));
                }
            }
        }
        let key =
            key.ok_or_else(|| error("a combination needs a key besides modifiers".to_owned()))?;
        Ok(Self { modifiers, key })
    }
}

/// A complete binding: one combination, or a chord of two.
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct KeySequence {
    chords: Vec<KeyChord>,
}

impl KeySequence {
    /// The combinations, first to last (one or two).
    #[must_use]
    pub fn chords(&self) -> &[KeyChord] {
        &self.chords
    }

    /// The first combination.
    #[must_use]
    pub fn first(&self) -> KeyChord {
        self.chords[0]
    }

    /// Whether this is a chord of two combinations.
    #[must_use]
    pub fn is_chord(&self) -> bool {
        self.chords.len() > 1
    }
}

impl fmt::Display for KeySequence {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (index, chord) in self.chords.iter().enumerate() {
            if index > 0 {
                f.write_str(" ")?;
            }
            write!(f, "{chord}")?;
        }
        Ok(())
    }
}

impl FromStr for KeySequence {
    type Err = KeyParseError;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        let parts: Vec<&str> = text.split_whitespace().collect();
        if parts.is_empty() {
            return Err(KeyParseError {
                input: text.to_owned(),
                reason: "no keys".to_owned(),
            });
        }
        if parts.len() > MAX_CHORDS {
            return Err(KeyParseError {
                input: text.to_owned(),
                reason: format!("a binding has at most {MAX_CHORDS} combinations"),
            });
        }
        let chords = parts
            .into_iter()
            .map(|part| {
                part.parse::<KeyChord>().map_err(|error| KeyParseError {
                    input: text.to_owned(),
                    reason: error.reason,
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self { chords })
    }
}

/// Text that does not follow the key grammar.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
#[error("`{input}` is not a valid key binding: {reason}")]
pub struct KeyParseError {
    input: String,
    reason: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn normalized(text: &str) -> String {
        text.parse::<KeySequence>().unwrap().to_string()
    }

    fn rejects(text: &str) -> String {
        text.parse::<KeySequence>().unwrap_err().to_string()
    }

    #[test]
    fn normalizes_case_order_and_spacing() {
        assert_eq!(normalized("Ctrl+Shift+P"), "ctrl+shift+p");
        assert_eq!(normalized("shift+ctrl+P"), "ctrl+shift+p");
        assert_eq!(normalized("win+alt+shift+ctrl+x"), "ctrl+shift+alt+win+x");
        assert_eq!(normalized("  CTRL+K    ctrl+C "), "ctrl+k ctrl+c");
        assert_eq!(normalized("Control+Meta+1"), "ctrl+win+1");
    }

    #[test]
    fn accepts_every_key_name() {
        for name in NAMED_KEYS {
            assert_eq!(normalized(&format!("ctrl+{name}")), format!("ctrl+{name}"));
        }
        for c in ('a'..='z').chain('0'..='9') {
            assert_eq!(normalized(&c.to_string()), c.to_string());
        }
        for n in 1..=24 {
            assert_eq!(normalized(&format!("F{n}")), format!("f{n}"));
        }
    }

    #[test]
    fn accepts_aliases_and_punctuation_characters() {
        assert_eq!(normalized("ctrl+`"), "ctrl+backquote");
        assert_eq!(normalized("Esc"), "escape");
        assert_eq!(normalized("ctrl+,"), "ctrl+comma");
        assert_eq!(normalized("ctrl+\\"), "ctrl+backslash");
        assert_eq!(normalized("ctrl+]"), "ctrl+bracketright");
        assert_eq!(normalized("pgdn"), "pagedown");
    }

    #[test]
    fn rejects_malformed_bindings() {
        assert!(rejects("").contains("no keys"));
        assert!(rejects("ctrl").contains("needs a key"));
        assert!(rejects("ctrl+shift").contains("needs a key"));
        assert!(rejects("ctrl+a+b").contains("exactly one key"));
        assert!(rejects("ctrl+ctrl+a").contains("twice"));
        assert!(rejects("ctrl++").contains("missing"));
        assert!(rejects("ctrl+hyper").contains("`hyper` is not a key name"));
        assert!(rejects("f25").contains("not a key name"));
        assert!(rejects("f0").contains("not a key name"));
        assert!(rejects("f01").contains("not a key name"));
        assert!(rejects("ctrl+k ctrl+c ctrl+d").contains("at most 2"));
        assert!(rejects("ctrl+é").contains("not a key name"));
    }

    #[test]
    fn round_trips_through_display() {
        for text in [
            "ctrl+shift+p",
            "escape",
            "ctrl+k ctrl+s",
            "tab",
            "f5",
            "ctrl+backquote",
            "ctrl+shift+alt+win+f24",
        ] {
            let parsed: KeySequence = text.parse().unwrap();
            assert_eq!(parsed.to_string(), text);
            assert_eq!(parsed.to_string().parse::<KeySequence>().unwrap(), parsed);
        }
    }

    #[test]
    fn tells_chords_apart() {
        let chord: KeySequence = "ctrl+k ctrl+c".parse().unwrap();
        assert!(chord.is_chord());
        assert_eq!(chord.first().to_string(), "ctrl+k");
        assert_eq!(chord.chords()[1].key(), Key::Char('c'));
        assert!(!"ctrl+k".parse::<KeySequence>().unwrap().is_chord());
    }
}
