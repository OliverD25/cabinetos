//! JSON Schema export of `cabinetos.json`.
//!
//! The schema is checked in at `sdk/config/cabinetos.schema.json`, and the
//! core writes a copy next to the configuration file for editors. A test keeps
//! the checked-in file equal to what the types produce. After changing a
//! setting, regenerate it from `core/`:
//!
//! ```text
//! CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config
//! ```

use schemars::{Schema, schema_for};

use crate::Config;

/// The JSON Schema of the configuration file.
#[must_use]
pub fn config_schema() -> Schema {
    let mut schema = schema_for!(Config);
    schema.insert("title".to_owned(), "CabinetOS configuration".into());
    schema
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::PathBuf;

    use super::*;
    use crate::SCHEMA_JSON;

    fn schema_path() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/config/cabinetos.schema.json")
    }

    fn generated() -> String {
        let mut text = serde_json::to_string_pretty(&config_schema()).unwrap();
        text.push('\n');
        text
    }

    /// Compares the checked-in schema with the generated one, or rewrites the
    /// file when `CABINETOS_UPDATE_SCHEMA=1`.
    #[test]
    fn config_schema_matches_sdk() {
        let path = schema_path();
        let generated = generated();
        if std::env::var_os("CABINETOS_UPDATE_SCHEMA").is_some_and(|value| value == "1") {
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, generated).unwrap();
            return;
        }
        let checked_in = fs::read_to_string(&path).unwrap_or_else(|error| {
            panic!(
                "cannot read {}: {error}. Create it with CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config",
                path.display()
            )
        });
        assert!(
            checked_in.replace("\r\n", "\n") == generated,
            "{} is out of date. Regenerate it with CABINETOS_UPDATE_SCHEMA=1 cargo test -p cabinetos-config and commit it.",
            path.display()
        );
        // The copy compiled into the core is the same file.
        assert_eq!(SCHEMA_JSON.replace("\r\n", "\n"), generated);
    }

    #[test]
    fn the_schema_rejects_unknown_keys_and_knows_the_defaults() {
        let schema = serde_json::to_value(config_schema()).unwrap();
        assert_eq!(schema["additionalProperties"], false);
        let ui = &schema["$defs"]["UiConfig"];
        assert_eq!(ui["additionalProperties"], false);
        assert_eq!(schema["properties"]["ui"]["default"]["layout"], "classic");
        assert!(schema["properties"]["$schema"].is_object());
    }
}
