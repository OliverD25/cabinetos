//! The Settings > Apps entry: `install.ps1` writes it for a per-user
//! install, under the user's own hive; after a swap the updater brings its
//! version and size up to date. The updater never creates the entry, and
//! changes it only when it names this install folder: an install by hand
//! or by winget has its own entry, or none.

use std::path::Path;

use cabinetos_fs::registry::{self, RegValue};

use crate::install::is_inside;

/// The entry under `HKEY_CURRENT_USER`.
pub const APPS_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS";

/// Environment variable naming another key under `HKEY_CURRENT_USER`, for
/// tests: a test never touches the real entry.
pub const APPS_KEY_ENV: &str = "CABINETOS_UPDATE_APPS_KEY";

/// The key to keep current: [`APPS_KEY_ENV`], else [`APPS_KEY`].
#[must_use]
pub fn apps_key() -> String {
    std::env::var(APPS_KEY_ENV)
        .ok()
        .filter(|key| !key.trim().is_empty())
        .unwrap_or_else(|| APPS_KEY.to_owned())
}

/// Writes `version` and the size (`bytes`, shown in KB) into the entry at
/// `key`, when it exists and its `InstallLocation` is `install`. Returns
/// whether it did.
pub fn refresh(key: &str, install: &Path, version: &str, bytes: u64) -> Result<bool, String> {
    let Some(location) = registry::read_user_string(key, "InstallLocation") else {
        return Ok(false);
    };
    let same = is_inside(Path::new(&location), install) && is_inside(install, Path::new(&location));
    if !same {
        tracing::debug!(
            key,
            location,
            "the Apps entry names another folder; left alone"
        );
        return Ok(false);
    }
    let kilobytes = u32::try_from(bytes.div_ceil(1024)).unwrap_or(u32::MAX);
    registry::write_user_values(
        key,
        &[
            ("DisplayVersion", RegValue::Text(version)),
            ("EstimatedSize", RegValue::Number(kilobytes)),
        ],
    )
    .map_err(|error| format!("cannot update the Apps entry HKCU\\{key}: {error}"))?;
    tracing::info!(
        key,
        version,
        kilobytes,
        "the Apps entry names the new version"
    );
    Ok(true)
}
