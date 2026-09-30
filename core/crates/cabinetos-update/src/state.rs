//! `state.json` in the update folder: what the updater must remember
//! between starts. The core alone writes it, through a temporary file and a
//! rename; a file that cannot be read counts as empty (the next check
//! fetches `latest.json` whole again).

use std::fs;
use std::io;
use std::path::Path;

use cabinetos_market::transfer;
use cabinetos_protocol::UpdateRelease;
use serde::{Deserialize, Serialize};

/// The file's name in the update folder.
pub const STATE_FILE: &str = "state.json";

/// What the updater remembers.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Saved {
    /// When the last check that worked ended, in milliseconds since
    /// 1970-01-01 UTC: the daily rule counts from it. A check that failed
    /// (no network) is tried again at the next hourly look.
    pub last_check_ms: Option<u64>,
    /// The address of the `latest.json` that `etag` and `latest` belong to.
    pub latest_source: Option<String>,
    /// The server's `ETag` of that `latest.json`.
    pub etag: Option<String>,
    /// That `latest.json`, as the last check read it.
    pub latest: Option<UpdateRelease>,
    /// No dialog before this time: the user said Later.
    pub snoozed_until_ms: Option<u64>,
    /// The version downloaded, checked and unpacked under
    /// `staging\<version>\files`, waiting for `apply`.
    pub staged: Option<String>,
    /// The last swap or rollback, and when a start confirmed it.
    pub swap: Option<Swap>,
}

/// One swap of the install folder's files.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Swap {
    /// `apply` or `rollback`.
    pub kind: SwapKind,
    /// The version the folder held before.
    pub from: String,
    /// The version it holds after.
    pub to: String,
    /// When, in milliseconds since 1970-01-01 UTC.
    pub at_ms: u64,
    /// When a core of version `to` started from the folder: the swap is
    /// confirmed. Absent until then.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub confirmed_at_ms: Option<u64>,
}

/// What a swap did.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SwapKind {
    /// A newer version was put in place.
    Apply,
    /// The version in `previous\` came back.
    Rollback,
}

/// Reads `state.json` in `dir`; empty when it is missing or cannot be read.
pub fn load(dir: &Path) -> Saved {
    let path = dir.join(STATE_FILE);
    match fs::read(&path) {
        Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_else(|error| {
            tracing::warn!(path = %path.display(), %error, "the updater's state cannot be read; starting from nothing");
            Saved::default()
        }),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Saved::default(),
        Err(error) => {
            tracing::warn!(path = %path.display(), %error, "cannot read the updater's state");
            Saved::default()
        }
    }
}

/// Writes `state.json` into `dir`, creating the folder.
pub fn save(dir: &Path, saved: &Saved) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    let mut text = serde_json::to_string_pretty(saved).map_err(io::Error::other)?;
    text.push('\n');
    transfer::replace_file(&dir.join(STATE_FILE), text.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_state_survives_a_round_trip_and_a_damaged_file_reads_as_empty() {
        let dir = tempfile::tempdir().unwrap();
        assert_eq!(load(dir.path()), Saved::default());
        let saved = Saved {
            last_check_ms: Some(1_790_000_000_000),
            staged: Some("0.2.0".to_owned()),
            swap: Some(Swap {
                kind: SwapKind::Apply,
                from: "0.1.0".to_owned(),
                to: "0.2.0".to_owned(),
                at_ms: 1_790_000_000_500,
                confirmed_at_ms: None,
            }),
            ..Saved::default()
        };
        save(dir.path(), &saved).unwrap();
        assert_eq!(load(dir.path()), saved);
        let text = fs::read_to_string(dir.path().join(STATE_FILE)).unwrap();
        assert!(text.contains("\"lastCheckMs\": 1790000000000"), "{text}");
        assert!(text.contains("\"kind\": \"apply\""), "{text}");
        fs::write(dir.path().join(STATE_FILE), "{ half").unwrap();
        assert_eq!(load(dir.path()), Saved::default());
    }
}
