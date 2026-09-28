//! Turning user paths into the verbatim (`\\?\`) form that Windows file APIs
//! accept at any length.

use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use crate::FsError;

/// The Windows limit for paths without the verbatim prefix.
const MAX_PATH: usize = 260;

/// The absolute verbatim form of `path` as a NUL-terminated UTF-16 string:
/// `C:\x` becomes `\\?\C:\x` and `\\server\share\x` becomes
/// `\\?\UNC\server\share\x`. Relative paths are resolved against the current
/// directory first, which also normalizes `.`, `..` and `/`; the verbatim
/// form would take those literally.
pub fn verbatim_wide(path: &str) -> Result<Vec<u16>, FsError> {
    let wide = absolute_units(path)?;
    let mut result = Vec::with_capacity(wide.len() + 9);
    if starts_with(&wide, r"\\?\") || starts_with(&wide, r"\\.\") {
        result.extend_from_slice(&wide);
    } else if let Some(rest) = strip_prefix(&wide, r"\\") {
        result.extend(r"\\?\UNC\".encode_utf16());
        result.extend_from_slice(rest);
    } else {
        result.extend(r"\\?\".encode_utf16());
        result.extend_from_slice(&wide);
    }
    result.push(0);
    Ok(result)
}

/// The absolute form of `path` as a NUL-terminated UTF-16 string, verbatim
/// only when it is too long for the plain form. For functions such as
/// `GetVolumePathNameW` that handle a path to a missing drive better in plain
/// form.
pub(crate) fn absolute_wide(path: &str) -> Result<Vec<u16>, FsError> {
    let mut wide = absolute_units(path)?;
    if wide.len() >= MAX_PATH {
        return verbatim_wide(path);
    }
    wide.push(0);
    Ok(wide)
}

/// `path` made absolute, as UTF-16 units without a terminator.
fn absolute_units(path: &str) -> Result<Vec<u16>, FsError> {
    if path.is_empty() || path.contains('\0') {
        return Err(FsError::InvalidPath {
            path: path.to_owned(),
            reason: "the path is empty or contains a NUL character".to_owned(),
        });
    }
    let absolute = std::path::absolute(Path::new(path)).map_err(|error| FsError::InvalidPath {
        path: path.to_owned(),
        reason: error.to_string(),
    })?;
    Ok(absolute.as_os_str().encode_wide().collect())
}

fn starts_with(wide: &[u16], prefix: &str) -> bool {
    strip_prefix(wide, prefix).is_some()
}

fn strip_prefix<'a>(wide: &'a [u16], prefix: &str) -> Option<&'a [u16]> {
    let prefix: Vec<u16> = prefix.encode_utf16().collect();
    wide.strip_prefix(prefix.as_slice())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn verbatim(path: &str) -> String {
        let wide = verbatim_wide(path).unwrap();
        assert_eq!(wide.last(), Some(&0));
        String::from_utf16(&wide[..wide.len() - 1]).unwrap()
    }

    #[test]
    fn adds_the_verbatim_prefix() {
        assert_eq!(verbatim(r"C:\Windows"), r"\\?\C:\Windows");
        assert_eq!(verbatim(r"C:\"), r"\\?\C:\");
        assert_eq!(verbatim(r"\\server\share\dir"), r"\\?\UNC\server\share\dir");
        assert_eq!(verbatim(r"\\?\C:\already"), r"\\?\C:\already");
    }

    #[test]
    fn normalizes_before_prefixing() {
        assert_eq!(
            verbatim(r"C:/Windows/./System32/../Temp"),
            r"\\?\C:\Windows\Temp"
        );
    }

    #[test]
    fn resolves_relative_paths() {
        let current = std::env::current_dir().unwrap();
        assert_eq!(verbatim("."), format!(r"\\?\{}", current.display()));
    }

    #[test]
    fn rejects_empty_paths_and_nul() {
        assert!(matches!(
            verbatim_wide(""),
            Err(FsError::InvalidPath { .. })
        ));
        assert!(matches!(
            verbatim_wide("C:\\a\0b"),
            Err(FsError::InvalidPath { .. })
        ));
    }
}
