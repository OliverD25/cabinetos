use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use ulid::Ulid;

/// Identifies one request on the control channel, and every log line that
/// request causes in every process (brief §8).
///
/// The ID is a ULID: a 128-bit value that sorts by creation time, written as 26
/// Crockford base32 characters. On the wire it is a plain JSON string.
///
/// The text is kept exactly as it was received. A reply therefore echoes the
/// ID byte for byte, even when a client writes it in lower case.
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct RequestId(String);

impl RequestId {
    /// Creates a fresh ID for the current time.
    #[expect(
        clippy::new_without_default,
        reason = "a request ID has no meaningful default; every call must create a new one"
    )]
    #[must_use]
    pub fn new() -> Self {
        Self(Ulid::generate().to_string())
    }

    /// The ID as text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for RequestId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl AsRef<str> for RequestId {
    fn as_ref(&self) -> &str {
        &self.0
    }
}

impl FromStr for RequestId {
    type Err = InvalidRequestId;

    /// Accepts a ULID in upper or lower case. Like the ULID specification, it
    /// rejects values above 128 bits (a first character above `7`), which the
    /// `ulid` crate would silently truncate.
    fn from_str(text: &str) -> Result<Self, Self::Err> {
        Ulid::from_string(text).map_err(|_| InvalidRequestId)?;
        if !matches!(text.as_bytes().first(), Some(b'0'..=b'7')) {
            return Err(InvalidRequestId);
        }
        Ok(Self(text.to_owned()))
    }
}

/// The text is not a ULID.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InvalidRequestId;

impl fmt::Display for InvalidRequestId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("request ID must be a ULID: 26 Crockford base32 characters, the first one 0-7")
    }
}

impl std::error::Error for InvalidRequestId {}

impl Serialize for RequestId {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for RequestId {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        text.parse().map_err(serde::de::Error::custom)
    }
}

#[cfg(feature = "schema")]
impl schemars::JsonSchema for RequestId {
    fn schema_name() -> std::borrow::Cow<'static, str> {
        "RequestId".into()
    }

    fn json_schema(_generator: &mut schemars::SchemaGenerator) -> schemars::Schema {
        schemars::json_schema!({
            "description": "A ULID created by the sender of a request and echoed unchanged in the reply.",
            "type": "string",
            "pattern": "^[0-7][0-9A-HJKMNP-TV-Za-hjkmnp-tv-z]{25}$"
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_ids_are_valid_ulids() {
        let id = RequestId::new();
        assert_eq!(id.as_str().len(), 26);
        assert_eq!(id.as_str().parse::<RequestId>(), Ok(id.clone()));
        assert_ne!(id, RequestId::new());
    }

    #[test]
    fn keeps_the_text_exactly_as_received() {
        let lower = "01j9zq4x7k3m5n8p2r6s0t1v4w";
        let id: RequestId = lower.parse().unwrap();
        assert_eq!(id.to_string(), lower);
    }

    #[test]
    fn rejects_text_that_is_not_a_ulid() {
        for bad in [
            "",
            "not-a-ulid",
            "01J9ZQ4X7K3M5N8P2R6S0T1V4",   // 25 characters
            "01J9ZQ4X7K3M5N8P2R6S0T1V4WX", // 27 characters
            "01J9ZQ4X7K3M5N8P2R6S0T1V4U",  // U is not in the alphabet
            "81J9ZQ4X7K3M5N8P2R6S0T1V4W",  // above 128 bits
        ] {
            assert_eq!(bad.parse::<RequestId>(), Err(InvalidRequestId), "{bad:?}");
        }
    }

    #[test]
    fn serializes_as_a_plain_string() {
        let id: RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4W".parse().unwrap();
        let json = serde_json::to_string(&id).unwrap();
        assert_eq!(json, r#""01J9ZQ4X7K3M5N8P2R6S0T1V4W""#);
        assert_eq!(serde_json::from_str::<RequestId>(&json).unwrap(), id);
        assert!(serde_json::from_str::<RequestId>(r#""nope""#).is_err());
    }
}
