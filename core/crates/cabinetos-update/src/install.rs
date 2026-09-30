//! The install folder: the folder of the running core, and whether the
//! updater may change it. Only a release with `release.json` next to the
//! core is ever touched, so a development build in `target\release` never
//! overwrites itself; and only a folder this user may change without
//! administrator rights, so an all-users install keeps the installer or
//! winget.

use std::fs;
use std::path::{Path, PathBuf};

use serde::Deserialize;

/// The file a release carries next to its programs (`build/release.ps1`).
pub const RELEASE_FILE: &str = "release.json";

/// The installer's record of what it put in place (`build/install.ps1`).
pub const RECORD_FILE: &str = ".cabinetos-install.json";

/// Where a user gets a release by hand: the newest GitHub Release.
pub const RELEASE_PAGE: &str = "https://github.com/OliverD25/cabinetos/releases/latest";

/// What the updater reads of `release.json`.
#[derive(Clone, Debug, Deserialize)]
pub struct ReleaseFacts {
    /// Always `CabinetOS`.
    pub product: String,
    /// The version the folder holds.
    pub version: String,
}

/// Reads `release.json` in `dir`.
pub fn read_release(dir: &Path) -> Result<ReleaseFacts, String> {
    let path = dir.join(RELEASE_FILE);
    let bytes =
        fs::read(&path).map_err(|error| format!("cannot read {}: {error}", path.display()))?;
    let text = String::from_utf8_lossy(&bytes);
    let facts: ReleaseFacts = serde_json::from_str(text.trim_start_matches('\u{feff}'))
        .map_err(|error| format!("{} is not a release file: {error}", path.display()))?;
    if facts.product != "CabinetOS" {
        return Err(format!("{} is not CabinetOS's", path.display()));
    }
    Ok(facts)
}

/// Why the install in `dir` cannot update itself, with what to do instead;
/// `None` when it can.
#[must_use]
pub fn not_updatable(dir: &Path) -> Option<String> {
    if !dir.join(RELEASE_FILE).is_file() {
        return Some(format!(
            "{} has no {RELEASE_FILE} next to cabinetos-core.exe: a development build, which never updates itself",
            dir.display()
        ));
    }
    if let Err(problem) = read_release(dir) {
        return Some(problem);
    }
    let instead = format!("update it with the installer or winget: {RELEASE_PAGE}");
    if for_all_users(dir) {
        return Some(format!(
            "CabinetOS is installed for all users in {}; {instead}",
            dir.display()
        ));
    }
    if let Err(error) = probe_write(dir) {
        return Some(format!(
            "the install folder {} cannot be changed without administrator rights ({error}); {instead}",
            dir.display()
        ));
    }
    None
}

/// An install in Program Files, or one whose record says it is for every
/// user: running elevated would let the probe pass, but such an install
/// belongs to the installer.
fn for_all_users(dir: &Path) -> bool {
    let program_files = ["ProgramFiles", "ProgramFiles(x86)", "ProgramW6432"]
        .into_iter()
        .filter_map(std::env::var_os)
        .filter(|folder| !folder.is_empty())
        .map(PathBuf::from);
    if program_files
        .into_iter()
        .any(|folder| is_inside(dir, &folder))
    {
        return true;
    }
    fs::read(dir.join(RECORD_FILE))
        .ok()
        .and_then(|bytes| serde_json::from_slice::<serde_json::Value>(strip_bom(&bytes)).ok())
        .is_some_and(|record| record["scope"] == "machine")
}

/// `bytes` without a UTF-8 byte-order mark, which Windows PowerShell writes.
pub fn strip_bom(bytes: &[u8]) -> &[u8] {
    bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(bytes)
}

/// Whether `path` is `folder` or inside it, ignoring case.
#[must_use]
pub fn is_inside(path: &Path, folder: &Path) -> bool {
    let normal = |path: &Path| {
        path.display()
            .to_string()
            .trim_end_matches('\\')
            .to_lowercase()
    };
    let (path, folder) = (normal(path), normal(folder));
    path == folder || path.starts_with(&format!("{folder}\\"))
}

/// Creates and removes a file in `dir`: the one sure test of whether this
/// user may change the folder.
fn probe_write(dir: &Path) -> std::io::Result<()> {
    let probe = dir.join(format!(".cabinetos-update-probe-{}", std::process::id()));
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&probe)?;
    fs::remove_file(&probe)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inside_ignores_case_and_trailing_separators() {
        let folder = Path::new(r"C:\Program Files\");
        assert!(is_inside(Path::new(r"c:\program files\CabinetOS"), folder));
        assert!(is_inside(Path::new(r"C:\Program Files"), folder));
        assert!(!is_inside(
            Path::new(r"C:\Program Files (x86)\x"),
            Path::new(r"C:\Program Files")
        ));
        assert!(!is_inside(Path::new(r"D:\apps"), folder));
    }

    #[test]
    fn a_folder_without_release_json_is_a_development_build() {
        let dir = tempfile::tempdir().unwrap();
        let reason = not_updatable(dir.path()).unwrap();
        assert!(reason.contains("development build"), "{reason}");
        fs::write(
            dir.path().join(RELEASE_FILE),
            r#"{"product":"Other","version":"1.0.0"}"#,
        )
        .unwrap();
        let reason = not_updatable(dir.path()).unwrap();
        assert!(reason.contains("is not CabinetOS's"), "{reason}");
        fs::write(
            dir.path().join(RELEASE_FILE),
            "\u{feff}{\"product\":\"CabinetOS\",\"version\":\"0.1.0\"}",
        )
        .unwrap();
        assert_eq!(not_updatable(dir.path()), None);
        fs::write(
            dir.path().join(RECORD_FILE),
            r#"{"product":"CabinetOS","scope":"machine"}"#,
        )
        .unwrap();
        let reason = not_updatable(dir.path()).unwrap();
        assert!(
            reason.contains("for all users") && reason.contains(RELEASE_PAGE),
            "{reason}"
        );
    }
}
