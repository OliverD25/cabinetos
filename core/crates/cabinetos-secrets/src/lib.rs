//! Named secrets, such as an API key, kept in the Windows Credential
//! Manager as generic credentials named `CabinetOS/<name>`, persisted for
//! the local machine and readable only by the user who wrote them.
//!
//! The core reads a secret only to put it into a request it makes for a
//! plugin (`http-request` with `secret`), so no plugin ever sees a value;
//! only a window or the command line may set, read, delete or list them
//! (`docs/ipc.md`, "Secrets"). A value never reaches a log: [`SecretValue`]
//! prints as `<hidden>`.
//!
//! Serves Constitution Article 8 (Sandboxed Extensibility: a plugin works
//! with a key it never holds). Unsafe code lives only in `win`, each block
//! with a `SAFETY:` comment.

#[allow(unsafe_code)]
mod win;

use std::fmt;

/// The prefix of every CabinetOS credential's target name.
pub const TARGET_PREFIX: &str = "CabinetOS/";

/// The longest value Windows keeps in a generic credential, in bytes
/// (`CRED_MAX_CREDENTIAL_BLOB_SIZE`).
pub const MAX_VALUE_BYTES: usize = 5 * 512;

/// The longest secret name, in characters.
pub const MAX_NAME_CHARS: usize = 128;

/// A secret's value. Its `Debug` and `Display` never show it, so a value
/// that ends up in a log line by mistake stays hidden.
#[derive(Clone, PartialEq, Eq)]
pub struct SecretValue(String);

impl SecretValue {
    /// Wraps `value`.
    #[must_use]
    pub fn new(value: impl Into<String>) -> Self {
        Self(value.into())
    }

    /// The value itself, for the one place that needs it.
    #[must_use]
    pub fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for SecretValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("<hidden>")
    }
}

impl fmt::Display for SecretValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("<hidden>")
    }
}

/// Why a secret could not be read or written.
#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum SecretError {
    /// The name is empty, too long, or has a character other than letters,
    /// digits, `-`, `_` and `.`.
    #[error("`{0}` is not a secret name: use 1 to 128 letters, digits, `-`, `_` and `.`")]
    InvalidName(String),
    /// The value is empty or longer than Windows keeps.
    #[error("the value of `{name}` is {len} bytes; a secret holds 1 to {MAX_VALUE_BYTES}")]
    InvalidValue {
        /// The secret.
        name: String,
        /// The value's length in bytes (UTF-8).
        len: usize,
    },
    /// No secret has that name.
    #[error("no secret `{0}` is stored")]
    NotFound(String),
    /// The Credential Manager refused.
    #[error("the Credential Manager refused `{name}`: {message}")]
    Windows {
        /// The secret.
        name: String,
        /// Windows' message.
        message: String,
    },
}

/// The secrets of this user, under one target prefix.
#[derive(Clone, Debug)]
pub struct Secrets {
    prefix: String,
}

impl Default for Secrets {
    fn default() -> Self {
        Self::new(TARGET_PREFIX)
    }
}

impl Secrets {
    /// Secrets whose target names start with `prefix`; the core uses
    /// [`TARGET_PREFIX`], tests a prefix of their own.
    #[must_use]
    pub fn new(prefix: impl Into<String>) -> Self {
        Self {
            prefix: prefix.into(),
        }
    }

    /// Stores `value` under `name`, replacing what was there.
    pub fn set(&self, name: &str, value: &SecretValue) -> Result<(), SecretError> {
        check_name(name)?;
        let len = value.expose().len();
        if len == 0 || len > MAX_VALUE_BYTES {
            return Err(SecretError::InvalidValue {
                name: name.to_owned(),
                len,
            });
        }
        win::write(&self.target(name), value.expose().as_bytes()).map_err(|message| {
            SecretError::Windows {
                name: name.to_owned(),
                message,
            }
        })
    }

    /// The value stored under `name`.
    pub fn get(&self, name: &str) -> Result<SecretValue, SecretError> {
        check_name(name)?;
        match win::read(&self.target(name)) {
            Ok(Some(bytes)) => Ok(SecretValue(String::from_utf8_lossy(&bytes).into_owned())),
            Ok(None) => Err(SecretError::NotFound(name.to_owned())),
            Err(message) => Err(SecretError::Windows {
                name: name.to_owned(),
                message,
            }),
        }
    }

    /// Removes the secret `name`.
    pub fn delete(&self, name: &str) -> Result<(), SecretError> {
        check_name(name)?;
        match win::delete(&self.target(name)) {
            Ok(true) => Ok(()),
            Ok(false) => Err(SecretError::NotFound(name.to_owned())),
            Err(message) => Err(SecretError::Windows {
                name: name.to_owned(),
                message,
            }),
        }
    }

    /// The names of every stored secret, sorted.
    pub fn list(&self) -> Result<Vec<String>, SecretError> {
        let targets = win::enumerate(&format!("{}*", self.prefix)).map_err(|message| {
            SecretError::Windows {
                name: "*".to_owned(),
                message,
            }
        })?;
        let mut names: Vec<String> = targets
            .into_iter()
            .filter_map(|target| target.strip_prefix(&self.prefix).map(str::to_owned))
            .filter(|name| check_name(name).is_ok())
            .collect();
        names.sort();
        Ok(names)
    }

    fn target(&self, name: &str) -> String {
        format!("{}{name}", self.prefix)
    }
}

/// A secret name: 1 to 128 letters, digits, `-`, `_` and `.`, so it is a
/// plain part of a target name and never a wildcard.
pub fn check_name(name: &str) -> Result<(), SecretError> {
    let good = !name.is_empty()
        && name.chars().count() <= MAX_NAME_CHARS
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'));
    if good {
        Ok(())
    } else {
        Err(SecretError::InvalidName(name.to_owned()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_value_never_prints() {
        let value = SecretValue::new("sk-ant-very-secret");
        assert_eq!(format!("{value:?}"), "<hidden>");
        assert_eq!(format!("{value}"), "<hidden>");
        assert_eq!(value.expose(), "sk-ant-very-secret");
    }

    #[test]
    fn names_are_plain() {
        for good in ["anthropic", "openai.key", "my_token-2"] {
            assert!(check_name(good).is_ok(), "{good}");
        }
        for bad in ["", "a/b", "a*", "a b", "ключ", &"x".repeat(129)] {
            assert!(check_name(bad).is_err(), "{bad}");
        }
    }

    /// Writes real credentials under a prefix of its own and removes them.
    #[test]
    fn secrets_round_trip_through_the_credential_manager() {
        let prefix = format!("CabinetOS-test-{}/", std::process::id());
        let secrets = Secrets::new(&prefix);
        let value = SecretValue::new("sk-test-Звіт-0123456789");
        secrets.set("unit.one", &value).unwrap();
        secrets.set("unit.two", &SecretValue::new("x")).unwrap();
        assert_eq!(secrets.get("unit.one").unwrap(), value);
        assert_eq!(secrets.list().unwrap(), ["unit.one", "unit.two"]);
        // Replaced in place.
        secrets.set("unit.two", &SecretValue::new("y")).unwrap();
        assert_eq!(secrets.get("unit.two").unwrap().expose(), "y");
        secrets.delete("unit.one").unwrap();
        secrets.delete("unit.two").unwrap();
        assert_eq!(
            secrets.get("unit.one"),
            Err(SecretError::NotFound("unit.one".to_owned()))
        );
        assert_eq!(
            secrets.delete("unit.one"),
            Err(SecretError::NotFound("unit.one".to_owned()))
        );
        assert!(secrets.list().unwrap().is_empty());
        assert!(matches!(
            secrets.set("big", &SecretValue::new("x".repeat(MAX_VALUE_BYTES + 1))),
            Err(SecretError::InvalidValue { .. })
        ));
        assert!(matches!(
            secrets.set("empty", &SecretValue::new("")),
            Err(SecretError::InvalidValue { .. })
        ));
    }
}
