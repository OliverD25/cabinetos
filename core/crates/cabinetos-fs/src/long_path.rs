//! The long form of a path, so two spellings of one folder compare equal.
//!
//! NTFS gives most names a second, short one (`CABINE~1` for `cabinetos`),
//! and a user's `%TEMP%` is often written that way. Comparing paths as text
//! then calls one folder two different things. [`long_path`] spells every
//! short name out.

use std::ffi::OsString;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};

use windows::Win32::Storage::FileSystem::GetLongPathNameW;
use windows::core::PCWSTR;

/// The size the first call offers; a longer path makes the call say how much
/// it needs.
const FIRST_TRY: usize = 512;

/// `path` with its short (8.3) names spelled out: `C:\Users\CABINE~1\Temp` is
/// `C:\Users\cabinetos\Temp`. Callers compare paths without regard to case,
/// so the case of the result is not promised.
///
/// The path may name something that does not exist yet. Then the longest
/// part that does exist is resolved and the rest follows as it was written,
/// so a file a program is about to create is judged by the folder it will be
/// in. A path of which no part exists (a drive that is not there) comes back
/// as it was. Nothing is resolved through links: a junction or a symbolic link
/// keeps its own name.
#[must_use]
pub fn long_path(path: &Path) -> PathBuf {
    let mut head = path.to_path_buf();
    // The names taken off the end, last name first.
    let mut tail = Vec::new();
    loop {
        if let Some(mut long) = long_name(&head) {
            for name in tail.iter().rev() {
                long.push(name);
            }
            return long;
        }
        let Some(name) = head.file_name().map(OsString::from) else {
            return path.to_path_buf();
        };
        if !head.pop() {
            return path.to_path_buf();
        }
        tail.push(name);
    }
}

/// `GetLongPathNameW` of `path`; `None` when `path` does not exist.
fn long_name(path: &Path) -> Option<PathBuf> {
    let mut wide: Vec<u16> = path.as_os_str().encode_wide().collect();
    if wide.contains(&0) {
        return None;
    }
    wide.push(0);
    let mut buffer = vec![0u16; FIRST_TRY.max(wide.len())];
    loop {
        // SAFETY: `wide` is NUL-terminated and outlives the call; the binding
        // passes the output buffer's length.
        let written = unsafe { GetLongPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) };
        let written = usize::try_from(written).ok()?;
        if written == 0 {
            return None;
        }
        if written < buffer.len() {
            return Some(PathBuf::from(OsString::from_wide(&buffer[..written])));
        }
        // Too small: `written` is the size needed, with the terminator.
        buffer.resize(written, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    use windows::Win32::Storage::FileSystem::GetShortPathNameW;

    /// The 8.3 form of `path`, which must exist. `None` when the volume
    /// keeps no short names (then the path comes back unchanged).
    fn short_form(path: &Path) -> Option<PathBuf> {
        let mut wide: Vec<u16> = path.as_os_str().encode_wide().collect();
        wide.push(0);
        let mut buffer = vec![0u16; 1024];
        // SAFETY: `wide` is NUL-terminated and outlives the call; the binding
        // passes the output buffer's length.
        let written =
            unsafe { GetShortPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) } as usize;
        assert!(
            written > 0 && written < buffer.len(),
            "no short form of {path:?}"
        );
        let short = PathBuf::from(OsString::from_wide(&buffer[..written]));
        (short != path).then_some(short)
    }

    /// A folder with a long name in a temporary folder, with its short form.
    fn folder_with_a_short_name() -> Option<(tempfile::TempDir, PathBuf, PathBuf)> {
        let temp = tempfile::tempdir().unwrap();
        let long = std::fs::canonicalize(temp.path())
            .unwrap()
            .join("a folder with a long name");
        std::fs::create_dir(&long).unwrap();
        // `canonicalize` gives the `\\?\` form, which `GetShortPathNameW`
        // takes too, but the paths compared here are the plain ones.
        let long = PathBuf::from(long.to_string_lossy().trim_start_matches(r"\\?\"));
        let Some(short) = short_form(&long) else {
            eprintln!("skipped: this volume keeps no 8.3 names");
            return None;
        };
        Some((temp, long, short))
    }

    #[test]
    fn a_short_name_becomes_the_long_one() {
        let Some((_temp, long, short)) = folder_with_a_short_name() else {
            return;
        };
        assert!(short.to_string_lossy().contains('~'), "{short:?}");
        assert_eq!(long_path(&short), long);
        assert_eq!(long_path(&long), long);
    }

    #[test]
    fn the_case_of_a_short_name_does_not_matter() {
        let Some((_temp, long, short)) = folder_with_a_short_name() else {
            return;
        };
        let lower = |path: &Path| path.to_string_lossy().to_lowercase();
        let quiet = PathBuf::from(lower(&short));
        assert_eq!(lower(&long_path(&quiet)), lower(&long));
        let loud = PathBuf::from(short.to_string_lossy().to_uppercase());
        assert_eq!(lower(&long_path(&loud)), lower(&long));
    }

    #[test]
    fn a_name_that_does_not_exist_yet_follows_its_folder() {
        let Some((_temp, long, short)) = folder_with_a_short_name() else {
            return;
        };
        assert_eq!(
            long_path(&short.join("new").join("file.txt")),
            long.join("new").join("file.txt")
        );
        // The part that does not exist keeps its spelling, tilde and all.
        assert_eq!(long_path(&long.join("NOTHER~1")), long.join("NOTHER~1"));
    }

    #[test]
    fn a_path_of_which_nothing_exists_comes_back_as_it_was() {
        let missing = Path::new(r"Q:\no\such\place");
        // Only when the drive really is not there.
        if !Path::new(r"Q:\").exists() {
            assert_eq!(long_path(missing), missing);
        }
        assert_eq!(long_path(Path::new("")), Path::new(""));
    }

    #[test]
    fn a_drive_root_stays_a_drive_root() {
        assert_eq!(long_path(Path::new(r"C:\")), Path::new(r"C:\"));
    }

    #[test]
    fn a_path_longer_than_the_first_buffer_is_resolved() {
        let temp = tempfile::tempdir().unwrap();
        let mut deep = std::fs::canonicalize(temp.path()).unwrap();
        for _ in 0..10 {
            deep.push("a folder name of some sixty characters, no more, no less");
        }
        std::fs::create_dir_all(&deep).unwrap();
        let plain = PathBuf::from(deep.to_string_lossy().trim_start_matches(r"\\?\"));
        assert!(plain.as_os_str().len() > FIRST_TRY);
        // The verbatim form takes the length; the plain call does not.
        assert_eq!(long_path(&deep), deep);
    }
}
