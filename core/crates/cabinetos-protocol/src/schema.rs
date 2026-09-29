//! JSON Schema export of the control-channel messages (ADR 0006), and of the
//! file formats the messages carry: theme files and marketplace indexes.
//!
//! The message schemas are checked into `sdk/protocol/` so the C# side can be
//! validated against the Rust types; the theme schema into `sdk/themes/`,
//! for editors; the index schema into `sdk/marketplace/`, for whoever
//! publishes an index. A test keeps the checked-in files equal to what the
//! types produce. After changing a message or a file format, regenerate
//! them from `core/`:
//!
//! ```text
//! CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-protocol
//! ```

use schemars::{Schema, schema_for};

use crate::{
    Envelope, Event, IndexerRequest, IndexerResponse, MarketIndex, Request, Response, THEME_FORMAT,
    Theme,
};

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

/// The JSON Schema of an event: an [`Envelope`] around an [`Event`]. A
/// client parses replies and events from one stream; their `type` tags never
/// overlap.
#[must_use]
pub fn event_schema() -> Schema {
    titled(schema_for!(Envelope<Event>), "EventEnvelope")
}

/// The JSON Schema of a request on the indexer's pipe.
#[must_use]
pub fn indexer_request_schema() -> Schema {
    titled(
        schema_for!(Envelope<IndexerRequest>),
        "IndexerRequestEnvelope",
    )
}

/// The JSON Schema of a reply on the indexer's pipe.
#[must_use]
pub fn indexer_response_schema() -> Schema {
    titled(
        schema_for!(Envelope<IndexerResponse>),
        "IndexerResponseEnvelope",
    )
}

/// The JSON Schema of a theme file, `<id>.json` in the themes folder. Its
/// `$id` names the format's version, [`THEME_FORMAT`].
#[must_use]
pub fn theme_schema() -> Schema {
    let mut schema = titled(schema_for!(Theme), "CabinetOS theme");
    schema.insert(
        "$id".to_owned(),
        format!("urn:cabinetos:theme:{THEME_FORMAT}").into(),
    );
    schema
}

/// The JSON Schema of a marketplace index, `index.json`.
#[must_use]
pub fn market_index_schema() -> Schema {
    titled(schema_for!(MarketIndex), "CabinetOS marketplace index")
}

/// The envelopes would otherwise be titled "Envelope". Code generators on
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

    fn sdk_dir(folder: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../sdk")
            .join(folder)
    }

    /// Compares the checked-in schema in `sdk/protocol/` with the generated
    /// one, or rewrites the file when `CABINETOS_UPDATE_SCHEMA=1`.
    fn check_snapshot(file_name: &str, schema: &Schema) {
        check_snapshot_in("protocol", file_name, schema);
    }

    fn check_snapshot_in(folder: &str, file_name: &str, schema: &Schema) {
        let path = sdk_dir(folder).join(file_name);
        let mut generated = serde_json::to_string_pretty(schema).unwrap();
        generated.push('\n');

        if std::env::var_os("CABINETOS_UPDATE_SCHEMA").is_some_and(|value| value == "1") {
            fs::create_dir_all(sdk_dir(folder)).unwrap();
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

    #[test]
    fn event_schema_matches_sdk() {
        check_snapshot("event.schema.json", &event_schema());
    }

    #[test]
    fn theme_schema_matches_sdk() {
        check_snapshot_in("themes", "theme.schema.json", &theme_schema());
    }

    #[test]
    fn the_theme_schema_rejects_unknown_keys() {
        let schema = serde_json::to_value(theme_schema()).unwrap();
        assert_eq!(schema["additionalProperties"], false);
        assert_eq!(schema["$defs"]["Palette"]["additionalProperties"], false);
        let required: Vec<&str> = schema["required"]
            .as_array()
            .unwrap()
            .iter()
            .filter_map(serde_json::Value::as_str)
            .collect();
        assert!(required.contains(&"palette") && !required.contains(&"accent"));
        let ansi = &schema["$defs"]["TerminalColors"]["properties"]["ansi"];
        assert_eq!(
            (ansi["minItems"].as_u64(), ansi["maxItems"].as_u64()),
            (Some(16), Some(16))
        );
    }

    #[test]
    fn the_theme_schema_carries_metrics_and_chrome_with_their_bounds() {
        let schema = serde_json::to_value(theme_schema()).unwrap();
        assert_eq!(schema["$id"], "urn:cabinetos:theme:2");
        let required = schema["required"].as_array().unwrap();
        for optional in ["metrics", "chrome"] {
            assert!(schema["properties"][optional].is_object(), "{optional}");
            assert!(!required.contains(&optional.into()), "{optional}");
        }
        let metrics = &schema["$defs"]["Metrics"];
        assert_eq!(metrics["additionalProperties"], false);
        assert_eq!(
            metrics["properties"].as_object().unwrap().len(),
            crate::METRICS.len()
        );
        let row = &metrics["properties"]["rowHeight"];
        assert_eq!(
            (&row["type"], &row["minimum"], &row["maximum"]),
            (&"integer".into(), &14.into(), &80.into())
        );
        let line = &metrics["properties"]["lineHeight"];
        assert_eq!(
            (&line["type"], &line["minimum"], &line["maximum"]),
            (&"number".into(), &1.into(), &2.5.into())
        );
        let chrome = &schema["$defs"]["Chrome"];
        assert_eq!(chrome["additionalProperties"], false);
        let mut keys: Vec<&String> = chrome["properties"].as_object().unwrap().keys().collect();
        keys.sort();
        assert_eq!(keys, ["fkeyBar", "hairlines", "rowStripes"]);
    }

    #[test]
    fn market_index_schema_matches_sdk() {
        check_snapshot_in("marketplace", "index.schema.json", &market_index_schema());
    }

    #[test]
    fn indexer_schemas_match_sdk() {
        check_snapshot("indexer-request.schema.json", &indexer_request_schema());
        check_snapshot("indexer-response.schema.json", &indexer_response_schema());
    }
}
