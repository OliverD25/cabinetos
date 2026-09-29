//! The changes a user makes to a folder by hand, without a job: a new
//! folder, a new empty file and a rename in place. Copies, moves and
//! deletes are jobs (`cabinetos-jobs`); these touch one name and finish at
//! once.
//!
//! All take the verbatim (`\\?\`) path, so length is no limit. That form
//! takes a name literally, so a name Windows could not open again later
//! (one ending in a dot or a space, or a device name such as `CON`) is
//! refused before the call, with the reason.

use std::ffi::OsString;
use std::os::windows::ffi::OsStringExt;
use std::path::Path;

use windows::Win32::Storage::FileSystem::{CreateDirectoryW, MOVE_FILE_FLAGS, MoveFileExW};
use windows::core::PCWSTR;

use crate::{FsError, path};

/// Creates the folder `path`. Its parent must exist; nothing may have its
/// name yet.
pub fn create_directory(path: &str) -> Result<(), FsError> {
    let name = Path::new(path)
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid(path, "the path names no folder to create"))?;
    check_name(name).map_err(|reason| invalid(path, &reason))?;
    let wide = path::verbatim_wide(path)?;
    // SAFETY: `wide` is NUL-terminated and outlives the call; no security
    // attributes.
    unsafe { CreateDirectoryW(PCWSTR(wide.as_ptr()), None) }
        .map_err(|error| FsError::from_windows(path, &error))
}

/// Creates the empty file `path`. Its folder must exist; nothing may have
/// its name yet, and nothing is ever opened or replaced.
pub fn create_file(path: &str) -> Result<(), FsError> {
    let name = Path::new(path)
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid(path, "the path names no file to create"))?;
    check_name(name).map_err(|reason| invalid(path, &reason))?;
    let wide = path::verbatim_wide(path)?;
    let verbatim = OsString::from_wide(&wide[..wide.len() - 1]);
    // `create_new` asks Windows for CREATE_NEW, which fails on any name
    // that is taken.
    std::fs::File::create_new(&verbatim)
        .map(drop)
        .map_err(|error| match error.raw_os_error() {
            // A folder's name is refused as access denied; a folder that
            // may not be written leaves nothing at the name.
            Some(code) => match FsError::from_win32(path, code.cast_unsigned()) {
                FsError::AccessDenied { .. } if std::fs::symlink_metadata(&verbatim).is_ok() => {
                    FsError::AlreadyExists {
                        path: path.to_owned(),
                    }
                }
                other => other,
            },
            None => FsError::Io {
                path: path.to_owned(),
                source: error,
            },
        })
}

/// Renames the file or folder `path` to `new_name` in the same folder.
/// Nothing is replaced: when the new name is taken, the rename fails with
/// [`FsError::AlreadyExists`]. Changing only the case of letters works.
pub fn rename(path: &str, new_name: &str) -> Result<(), FsError> {
    check_name(new_name).map_err(|reason| invalid(new_name, &reason))?;
    let source = Path::new(path);
    let (Some(parent), Some(_)) = (source.parent(), source.file_name()) else {
        return Err(invalid(path, "the path names nothing to rename"));
    };
    let target = parent.join(new_name);
    let target = target
        .to_str()
        .ok_or_else(|| invalid(new_name, "the new path is not valid Unicode"))?;
    let from = path::verbatim_wide(path)?;
    let to = path::verbatim_wide(target)?;
    // SAFETY: both strings are NUL-terminated and outlive the call. Without
    // MOVEFILE_REPLACE_EXISTING an existing target is never replaced, and
    // without MOVEFILE_COPY_ALLOWED nothing is copied.
    unsafe {
        MoveFileExW(
            PCWSTR(from.as_ptr()),
            PCWSTR(to.as_ptr()),
            MOVE_FILE_FLAGS(0),
        )
    }
    .map_err(|error| match FsError::from_windows(path, &error) {
        // The name that is taken is the new one.
        FsError::AlreadyExists { .. } => FsError::AlreadyExists {
            path: target.to_owned(),
        },
        other => other,
    })
}

/// Why `name` cannot be one file or folder name that Windows can open again
/// later, or `Ok` when it can.
pub(crate) fn check_name(name: &str) -> Result<(), String> {
    if name.is_empty() {
        return Err("the name is empty".to_owned());
    }
    if name == "." || name == ".." {
        return Err(format!("`{name}` is not a name"));
    }
    if name.contains(['\\', '/']) {
        return Err("a name cannot contain `\\` or `/`".to_owned());
    }
    if let Some(bad) = name
        .chars()
        .find(|&c| matches!(c, '<' | '>' | ':' | '"' | '|' | '?' | '*') || c < ' ')
    {
        return Err(if bad < ' ' {
            "a name cannot contain control characters".to_owned()
        } else {
            format!("a name cannot contain `{bad}`")
        });
    }
    if name.ends_with(['.', ' ']) {
        return Err("a name cannot end with a dot or a space".to_owned());
    }
    // `CON`, `nul.txt` and the like name devices, not files.
    let stem = name.split('.').next().unwrap_or(name).trim_end();
    if is_device_name(stem) {
        return Err(format!("`{stem}` is the name of a device"));
    }
    Ok(())
}

fn is_device_name(stem: &str) -> bool {
    let upper = stem.to_ascii_uppercase();
    if matches!(upper.as_str(), "CON" | "PRN" | "AUX" | "NUL") {
        return true;
    }
    let mut chars = upper.chars();
    let prefix: String = chars.by_ref().take(3).collect();
    let digit: String = chars.collect();
    (prefix == "COM" || prefix == "LPT")
        && matches!(
            digit.as_str(),
            "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
        )
}

fn invalid(path: &str, reason: &str) -> FsError {
    FsError::InvalidPath {
        path: path.to_owned(),
        reason: reason.to_owned(),
    }
}

#[cfg(test)]
mod tests {
    use std::fs;

    use super::*;

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("ops")
            .tempdir_in(root)
            .unwrap()
    }

    fn text(path: &Path) -> String {
        path.to_str().unwrap().to_owned()
    }

    /// The names in `dir`, as the file system spells them.
    fn names(dir: &Path) -> Vec<String> {
        let mut names: Vec<String> = fs::read_dir(dir)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().into_string().unwrap())
            .collect();
        names.sort();
        names
    }

    #[test]
    fn names_windows_could_not_open_again_are_refused() {
        for good in [
            "a",
            "New folder",
            "report.2026.txt",
            "Звіт",
            ".hidden",
            "COM10",
            "CONSOLE",
        ] {
            assert_eq!(check_name(good), Ok(()), "{good}");
        }
        for bad in [
            "",
            ".",
            "..",
            "a\\b",
            "a/b",
            "a:b",
            "what?",
            "star*",
            "pipe|",
            "q\"",
            "<x>",
            "tab\there",
            "dot.",
            "space ",
            "CON",
            "con.txt",
            "Nul",
            "com1",
            "LPT9.log",
            "COM²",
        ] {
            assert!(check_name(bad).is_err(), "{bad:?} was accepted");
        }
    }

    #[test]
    fn a_folder_is_created_once() {
        let dir = scratch();
        let new = dir.path().join("New folder");
        create_directory(&text(&new)).unwrap();
        assert!(new.is_dir());
        let again = create_directory(&text(&new)).unwrap_err();
        assert!(matches!(again, FsError::AlreadyExists { .. }), "{again:?}");
        // A file of that name is in the way too.
        fs::write(dir.path().join("taken"), "x").unwrap();
        let file = create_directory(&text(&dir.path().join("taken"))).unwrap_err();
        assert!(matches!(file, FsError::AlreadyExists { .. }), "{file:?}");
    }

    #[test]
    fn a_folder_needs_its_parent_and_a_good_name() {
        let dir = scratch();
        let orphan =
            create_directory(&text(&dir.path().join("missing").join("child"))).unwrap_err();
        assert!(matches!(orphan, FsError::NotFound { .. }), "{orphan:?}");
        for bad in ["trailing.", "CON", "a?b"] {
            let error = create_directory(&text(&dir.path().join(bad))).unwrap_err();
            assert!(
                matches!(error, FsError::InvalidPath { .. }),
                "{bad}: {error:?}"
            );
        }
        assert!(names(dir.path()).is_empty());
    }

    #[test]
    fn a_file_is_created_empty_and_nothing_is_replaced() {
        let dir = scratch();
        let new = dir.path().join("New Text Document.txt");
        create_file(&text(&new)).unwrap();
        assert!(new.is_file());
        assert_eq!(fs::metadata(&new).unwrap().len(), 0);
        let again = create_file(&text(&new)).unwrap_err();
        assert!(matches!(again, FsError::AlreadyExists { .. }), "{again:?}");
        // A file with content keeps it.
        fs::write(dir.path().join("kept.txt"), "kept").unwrap();
        let kept = create_file(&text(&dir.path().join("kept.txt"))).unwrap_err();
        assert!(matches!(kept, FsError::AlreadyExists { .. }), "{kept:?}");
        assert_eq!(
            fs::read_to_string(dir.path().join("kept.txt")).unwrap(),
            "kept"
        );
        // A folder of that name is in the way too.
        fs::create_dir(dir.path().join("folder")).unwrap();
        let folder = create_file(&text(&dir.path().join("folder"))).unwrap_err();
        assert!(
            matches!(folder, FsError::AlreadyExists { .. }),
            "{folder:?}"
        );
        assert!(dir.path().join("folder").is_dir());
    }

    #[test]
    fn a_file_needs_its_folder_and_a_good_name() {
        let dir = scratch();
        let orphan = create_file(&text(&dir.path().join("missing").join("a.txt"))).unwrap_err();
        assert!(matches!(orphan, FsError::NotFound { .. }), "{orphan:?}");
        for bad in ["trailing.", "space ", "CON", "nul.txt", "a?b.txt", "x|y"] {
            let error = create_file(&text(&dir.path().join(bad))).unwrap_err();
            assert!(
                matches!(error, FsError::InvalidPath { .. }),
                "{bad}: {error:?}"
            );
        }
        let root = create_file(r"C:\").unwrap_err();
        assert!(matches!(root, FsError::InvalidPath { .. }), "{root:?}");
        for name in ["Звіт.txt", "📁 notes.md", "cafe\u{301}.txt", ".gitignore"] {
            create_file(&text(&dir.path().join(name))).unwrap();
        }
        assert_eq!(
            names(dir.path()),
            [".gitignore", "cafe\u{301}.txt", "Звіт.txt", "📁 notes.md"]
        );
    }

    #[test]
    fn a_long_path_is_no_limit() {
        let dir = scratch();
        let mut path = dir.path().to_path_buf();
        while path.as_os_str().len() < 400 {
            path.push("a-folder-name-of-forty-characters-long-x");
            create_directory(&text(&path)).unwrap();
        }
        // The standard library adds the verbatim prefix to long paths itself.
        let file = path.join("old.txt");
        fs::write(&file, "x").unwrap();
        rename(&text(&file), "new.txt").unwrap();
        assert!(path.join("new.txt").is_file());
        create_file(&text(&path.join("empty.txt"))).unwrap();
        assert_eq!(fs::metadata(path.join("empty.txt")).unwrap().len(), 0);
    }

    #[test]
    fn a_rename_never_replaces() {
        let dir = scratch();
        fs::write(dir.path().join("a.txt"), "a").unwrap();
        fs::write(dir.path().join("b.txt"), "b").unwrap();
        let error = rename(&text(&dir.path().join("a.txt")), "b.txt").unwrap_err();
        let FsError::AlreadyExists { path } = &error else {
            panic!("{error:?}")
        };
        assert!(
            path.ends_with("b.txt"),
            "the error names the taken name: {path}"
        );
        assert_eq!(fs::read_to_string(dir.path().join("b.txt")).unwrap(), "b");

        rename(&text(&dir.path().join("a.txt")), "c.txt").unwrap();
        assert_eq!(names(dir.path()), ["b.txt", "c.txt"]);
        // Only the case changes: the same file, a new spelling.
        rename(&text(&dir.path().join("c.txt")), "C.txt").unwrap();
        assert_eq!(names(dir.path()), ["C.txt", "b.txt"]);
        // The same name again changes nothing.
        rename(&text(&dir.path().join("C.txt")), "C.txt").unwrap();
        assert_eq!(names(dir.path()), ["C.txt", "b.txt"]);
        // A folder, with what is inside it.
        fs::create_dir(dir.path().join("sub")).unwrap();
        fs::write(dir.path().join("sub").join("inner.txt"), "i").unwrap();
        rename(&text(&dir.path().join("sub")), "renamed").unwrap();
        assert!(dir.path().join("renamed").join("inner.txt").is_file());
    }

    #[test]
    fn names_beyond_ascii_are_created_and_renamed() {
        let dir = scratch();
        for name in ["Ґанок", "中文文件夹", "📁 photos", "𝔘𝔫𝔦𝔠𝔬𝔡𝔢", "مستند"]
        {
            create_directory(&text(&dir.path().join(name))).unwrap();
            assert!(dir.path().join(name).is_dir(), "{name}");
        }
        rename(&text(&dir.path().join("Ґанок")), "Їжак і Єнот").unwrap();
        assert!(dir.path().join("Їжак і Єнот").is_dir());

        // Composed and decomposed café are two names side by side.
        fs::write(dir.path().join("caf\u{e9}.txt"), "NFC").unwrap();
        fs::write(dir.path().join("x.txt"), "NFD").unwrap();
        rename(&text(&dir.path().join("x.txt")), "cafe\u{301}.txt").unwrap();
        let read = |name: &str| fs::read_to_string(dir.path().join(name)).unwrap();
        assert_eq!(read("caf\u{e9}.txt"), "NFC");
        assert_eq!(read("cafe\u{301}.txt"), "NFD");
        // Back to the other form is a name that is taken, not a case change.
        let taken =
            rename(&text(&dir.path().join("cafe\u{301}.txt")), "caf\u{e9}.txt").unwrap_err();
        assert!(matches!(taken, FsError::AlreadyExists { .. }), "{taken:?}");

        // Only the case changes, beyond ASCII too.
        fs::write(dir.path().join("звіт.txt"), "з").unwrap();
        rename(&text(&dir.path().join("звіт.txt")), "ЗВІТ.txt").unwrap();
        assert!(names(dir.path()).contains(&"ЗВІТ.txt".to_owned()));
        assert!(!names(dir.path()).contains(&"звіт.txt".to_owned()));
    }

    #[test]
    fn a_name_holds_255_utf16_units_and_a_surrogate_pair_counts_twice() {
        let dir = scratch();
        let most = "𝔘".repeat(127) + "a";
        assert_eq!(most.encode_utf16().count(), 255);
        create_directory(&text(&dir.path().join(&most))).unwrap();
        let over = "𝔘".repeat(128);
        assert_eq!(over.chars().count(), 128);
        let error = create_directory(&text(&dir.path().join(&over))).unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
        fs::write(dir.path().join("short.txt"), "s").unwrap();
        let error = rename(&text(&dir.path().join("short.txt")), &over).unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
        assert_eq!(names(dir.path()), ["short.txt".to_owned(), most]);
    }

    #[test]
    fn a_rename_needs_a_source_and_a_plain_name() {
        let dir = scratch();
        let missing = rename(&text(&dir.path().join("nothing.txt")), "x.txt").unwrap_err();
        assert!(matches!(missing, FsError::NotFound { .. }), "{missing:?}");
        fs::write(dir.path().join("a.txt"), "a").unwrap();
        for bad in ["", "sub\\b.txt", "../up.txt", "x/y", "trailing ", "aux"] {
            let error = rename(&text(&dir.path().join("a.txt")), bad).unwrap_err();
            assert!(
                matches!(error, FsError::InvalidPath { .. }),
                "{bad:?}: {error:?}"
            );
        }
        let root = rename(r"C:\", "D").unwrap_err();
        assert!(matches!(root, FsError::InvalidPath { .. }), "{root:?}");
        assert_eq!(names(dir.path()), ["a.txt"]);
    }
}
