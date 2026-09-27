//! JSON Schema export of the control-channel messages (ADR 0006).
//!
//! The schemas are checked into `sdk/protocol/` so the C# side can be validated
//! against the Rust types. A test keeps the checked-in files equal to what the
//! types produce. After changing a message, regenerate them from `core/`:
//!
//! ```text
//! CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol
//! ```

use schemars::{Schema, schema_for};

use crate::{Envelope, Request, Response};

/// The JSON Schema of a request: an [`Envelope`] around a [`Request`].
#[must_use]
pub fn request_schema() -> Schema {
    titled(schema_for!(Envelope<Request>), "RequestEnvelope")
}

/// The JSON Schema of a reply: an [`Envelope`] around a [`Response`].
#[must_use]
pub fn response_schema() -> Schema {
    titled(schema_for!(Envelope<Response>), "ResponseEnvelope")
}

/// Both envelopes would otherwise be titled "Envelope". Code generators on
/// the C# side turn the title into a class name, so each gets its own.
fn titled(mut schema: Schema, title: &str) -> Schema {
    schema.insert("title".to_owned(), title.into());
    schema
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::PathBuf;

    use super::*;

    fn sdk_protocol_dir() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/protocol")
    }

    /// Compares the checked-in schema with the generated one, or rewrites the
    /// file when `CABINETOS_UPDATE_SCHEMA=1`.
    fn check_snapshot(file_name: &str, schema: &Schema) {
        let path = sdk_protocol_dir().join(file_name);
        let mut generated = serde_json::to_string_pretty(schema).unwrap();
        generated.push('\n');

        if std::env::var_os("CABINETOS_UPDATE_SCHEMA").is_some_and(|value| value == "1") {
            fs::create_dir_all(sdk_protocol_dir()).unwrap();
            fs::write(&path, generated).unwrap();
            return;
        }

        let checked_in = fs::read_to_string(&path).unwrap_or_else(|error| {
            panic!(
                "cannot read {}: {error}. Create it with CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol",
                path.display()
            )
        });
        assert!(
            checked_in.replace("\r\n", "\n") == generated,
            "{} is out of date. Regenerate it with CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol and commit it.",
            path.display()
        );
    }

    #[test]
    fn request_schema_matches_sdk() {
        check_snapshot("request.schema.json", &request_schema());
    }

    #[test]
    fn response_schema_matches_sdk() {
        check_snapshot("response.schema.json", &response_schema());
    }
}
