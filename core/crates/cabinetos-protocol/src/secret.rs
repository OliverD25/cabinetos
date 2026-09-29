//! Secret values on the wire (`secret_set`, `secret`; protocol version 13).

use std::fmt;

use serde::{Deserialize, Serialize};

/// A secret's value in a message: plain text on the wire, `<hidden>` when
/// printed, so a message logged with `{:?}` never shows it.
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(transparent)]
pub struct SecretText(pub String);

impl fmt::Debug for SecretText {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("<hidden>")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_text_travels_but_never_prints() {
        let text = SecretText("sk-ant-123".to_owned());
        assert_eq!(serde_json::to_string(&text).unwrap(), r#""sk-ant-123""#);
        assert_eq!(format!("{text:?}"), "<hidden>");
        let back: SecretText = serde_json::from_str(r#""sk-ant-123""#).unwrap();
        assert_eq!(back, text);
    }
}
