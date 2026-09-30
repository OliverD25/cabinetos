//! What the plugin keeps between calls and across restarts, in files of its
//! own folder: the tier the user chose with `agent.tier`, and the jobs of
//! the last preview that was applied, so `agent.undo` can reverse them.

use serde_json::{Value, json};

use crate::host::Host;
use crate::tier::Tier;

/// The file that holds it.
pub const STATE_FILE: &str = "state.json";

/// The jobs of a preview that was applied.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Applied {
    pub preview: String,
    /// In the order they ran.
    pub jobs: Vec<u64>,
}

/// The saved state.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Saved {
    /// The tier set with `agent.tier`, which wins over `settings.tier`
    /// until the settings change it.
    pub tier: Option<Tier>,
    pub last: Option<Applied>,
}

impl Saved {
    #[must_use]
    pub fn to_json(&self) -> Value {
        json!({
            "tier": self.tier.map(Tier::number),
            "last": self.last.as_ref().map(|applied| json!({
                "preview": applied.preview,
                "jobs": applied.jobs,
            })),
        })
    }

    #[must_use]
    pub fn from_json(value: &Value) -> Self {
        Self {
            tier: value["tier"].as_u64().and_then(Tier::from_number),
            last: value["last"].as_object().and_then(|last| {
                Some(Applied {
                    preview: last.get("preview")?.as_str()?.to_owned(),
                    jobs: last
                        .get("jobs")?
                        .as_array()?
                        .iter()
                        .filter_map(Value::as_u64)
                        .collect(),
                })
            }),
        }
    }
}

/// Reads the saved state; a missing or damaged file is an empty state.
#[must_use]
pub fn load(host: &dyn Host) -> Saved {
    host.read_data(STATE_FILE)
        .ok()
        .flatten()
        .and_then(|bytes| serde_json::from_slice::<Value>(&bytes).ok())
        .map(|value| Saved::from_json(&value))
        .unwrap_or_default()
}

/// Saves the state.
pub fn save(host: &dyn Host, saved: &Saved) -> Result<(), String> {
    host.write_data(STATE_FILE, saved.to_json().to_string().as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::FakeHost;

    #[test]
    fn the_state_survives_a_save_and_a_load() {
        let host = FakeHost::new();
        assert_eq!(load(&host), Saved::default());
        let saved = Saved {
            tier: Some(Tier::Autonomous),
            last: Some(Applied {
                preview: "preview-3".to_owned(),
                jobs: vec![4, 5, 6],
            }),
        };
        save(&host, &saved).unwrap();
        assert_eq!(load(&host), saved);
        save(&host, &Saved::default()).unwrap();
        assert_eq!(load(&host), Saved::default());
    }

    #[test]
    fn a_damaged_file_is_an_empty_state() {
        let host = FakeHost::new();
        host.write_data(STATE_FILE, b"{ not json").unwrap();
        assert_eq!(load(&host), Saved::default());
        host.write_data(STATE_FILE, br#"{"tier": 9, "last": {"preview": 5}}"#)
            .unwrap();
        assert_eq!(load(&host), Saved::default());
    }
}
