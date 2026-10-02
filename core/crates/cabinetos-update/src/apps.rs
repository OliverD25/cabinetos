//! The Settings > Apps entry: `install.ps1` writes one for a per-user
//! install, and the setup file (`build/setup.iss`, Inno Setup) writes its
//! own, both under the user's own hive; after a swap the updater brings
//! their version and size up to date. The updater never creates an entry,
//! and changes one only when it names this install folder: an install by
//! hand or by winget has its own entry, or none.

use std::path::Path;

use cabinetos_fs::registry::{self, RegValue};

use crate::install::is_inside;
use crate::version::Version;

/// The entry `install.ps1` writes, under `HKEY_CURRENT_USER`.
pub const APPS_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS";

/// The entry the setup file writes: Inno Setup names it after the setup's
/// `AppId` (`CabinetOS`) with `_is1`.
pub const SETUP_APPS_KEY: &str =
    r"Software\Microsoft\Windows\CurrentVersion\Uninstall\CabinetOS_is1";

/// Environment variable naming other keys under `HKEY_CURRENT_USER`, for
/// tests: one, or several separated by `;`. A test never touches the real
/// entries.
pub const APPS_KEY_ENV: &str = "CABINETOS_UPDATE_APPS_KEY";

/// The keys to keep current: those [`APPS_KEY_ENV`] names, else
/// [`APPS_KEY`] and [`SETUP_APPS_KEY`].
#[must_use]
pub fn apps_keys() -> Vec<String> {
    keys_from(std::env::var(APPS_KEY_ENV).ok().as_deref())
}

fn keys_from(variable: Option<&str>) -> Vec<String> {
    let named: Vec<String> = variable
        .unwrap_or_default()
        .split(';')
        .map(str::trim)
        .filter(|key| !key.is_empty())
        .map(str::to_owned)
        .collect();
    if named.is_empty() {
        vec![APPS_KEY.to_owned(), SETUP_APPS_KEY.to_owned()]
    } else {
        named
    }
}

/// Writes `version` and the size (`bytes`, shown in KB) into the entry at
/// `key`, when it exists and its `InstallLocation` is `install`; also the
/// `MajorVersion` and `MinorVersion` numbers Inno Setup keeps, when the
/// entry has them. Returns whether it did.
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
    let mut values = vec![
        ("DisplayVersion", RegValue::Text(version)),
        ("EstimatedSize", RegValue::Number(kilobytes)),
    ];
    if let Some(parsed) = Version::parse(version) {
        for (name, number) in [
            ("MajorVersion", parsed.major()),
            ("MinorVersion", parsed.minor()),
        ] {
            if registry::read_user_number(key, name).is_some() {
                values.push((
                    name,
                    RegValue::Number(u32::try_from(number).unwrap_or(u32::MAX)),
                ));
            }
        }
    }
    registry::write_user_values(key, &values)
        .map_err(|error| format!("cannot update the Apps entry HKCU\\{key}: {error}"))?;
    tracing::info!(
        key,
        version,
        kilobytes,
        "the Apps entry names the new version"
    );
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn both_entries_unless_the_variable_names_others() {
        assert_eq!(keys_from(None), [APPS_KEY, SETUP_APPS_KEY]);
        assert_eq!(keys_from(Some(" ; ")), [APPS_KEY, SETUP_APPS_KEY]);
        assert_eq!(keys_from(Some(r"Software\A")), [r"Software\A"]);
        assert_eq!(
            keys_from(Some(r"Software\A; Software\B_is1;")),
            [r"Software\A", r"Software\B_is1"]
        );
    }
}
