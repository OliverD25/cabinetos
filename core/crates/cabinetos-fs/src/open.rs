//! Opening a file or folder with its default application, as a
//! double-click in Explorer does: the last step of navigation. What opens
//! it (a viewer, an editor, Explorer for a folder, the program itself for
//! an `.exe`) is the user's choice in Windows, not the core's.

use std::os::windows::ffi::OsStrExt;

use windows::Win32::Storage::FileSystem::{GetFileAttributesW, INVALID_FILE_ATTRIBUTES};
use windows::Win32::UI::Shell::{
    SEE_MASK_FLAG_NO_UI, SEE_MASK_NOASYNC, SHELLEXECUTEINFOW, ShellExecuteExW,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows::core::{PCWSTR, w};

use crate::com::Apartment;
use crate::{FsError, path};

/// Opens `path`, a file or a folder, with its default application: the
/// `open` verb of `ShellExecuteExW`, without error dialogs, returning once
/// the shell has handed it over (not when the application ends).
///
/// The path must exist as given. The shell would otherwise look further: a
/// name without an extension may run a program with that name.
pub fn open_path(path: &str) -> Result<(), FsError> {
    let verbatim = path::verbatim_wide(path)?;
    // SAFETY: `verbatim` is NUL-terminated and outlives the call.
    if unsafe { GetFileAttributesW(PCWSTR(verbatim.as_ptr())) } == INVALID_FILE_ATTRIBUTES {
        return Err(FsError::from_windows(
            path,
            &windows::core::Error::from_thread(),
        ));
    }
    // A link whose target is gone exists itself, but the shell can open
    // nothing through it and says only "unspecified error": name the target.
    if std::fs::symlink_metadata(path).is_ok_and(|link| link.file_type().is_symlink())
        && std::fs::metadata(path).is_err_and(|error| error.kind() == std::io::ErrorKind::NotFound)
    {
        let target = std::fs::read_link(path).map_or_else(
            |_| path.to_owned(),
            |target| {
                let target = target.display().to_string();
                target
                    .strip_prefix(r"\\?\")
                    .map_or_else(|| target.clone(), str::to_owned)
            },
        );
        return Err(FsError::NotFound { path: target });
    }
    // The shell parses plain paths, not the `\\?\` form.
    let plain = std::path::absolute(path).map_err(|error| FsError::InvalidPath {
        path: path.to_owned(),
        reason: error.to_string(),
    })?;
    let plain: Vec<u16> = plain.as_os_str().encode_wide().chain([0]).collect();
    // The shell may hand the file to a COM server; COM wants to be ready.
    let _apartment = Apartment::enter();
    let mut info = SHELLEXECUTEINFOW {
        cbSize: u32::try_from(size_of::<SHELLEXECUTEINFOW>()).unwrap_or(u32::MAX),
        fMask: SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
        lpVerb: w!("open"),
        lpFile: PCWSTR(plain.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..SHELLEXECUTEINFOW::default()
    };
    // SAFETY: `info` is a valid structure with its size in `cbSize`; its
    // strings are NUL-terminated and outlive the call, which returns once
    // the shell is done with them (SEE_MASK_NOASYNC).
    unsafe { ShellExecuteExW(&raw mut info) }.map_err(|error| {
        // The terminating NUL is not part of the length.
        let length = plain.len() - 1;
        if length >= MAX_PATH {
            // Whether the shell takes such a path depends on the program
            // (Notepad took 339 characters, cmd refused 311). It gives no
            // reason, and the short (8.3) form does not help: the shell
            // turns it back into the long one.
            return FsError::InvalidPath {
                path: path.to_owned(),
                reason: format!(
                    "the shell refused to open it; its path has {length} characters, and \
                     many programs take only paths shorter than {MAX_PATH}; move the file \
                     or shorten a folder name on the way"
                ),
            };
        }
        match FsError::from_windows(path, &error) {
            FsError::Io { path, source } if source.to_string().is_empty() => FsError::Io {
                path,
                source: std::io::Error::other(format!(
                    "the shell cannot open it (error {:#010x})",
                    error.code().0
                )),
            },
            other => other,
        }
    })
}

/// The shell's path limit: a path must be shorter, in UTF-16 units.
const MAX_PATH: usize = 260;

#[cfg(test)]
mod tests {
    use super::*;

    // Opening a file for real starts an application and leaves its window
    // open on the desktop, so the tests stop before the shell is called.

    #[test]
    fn a_missing_path_is_not_found_and_nothing_runs() {
        let dir = std::env::temp_dir().join("cabinetos-fs-test");
        let missing = dir.join("no-such-file.txt");
        let error = open_path(missing.to_str().unwrap()).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
        // No extension probing: `notepad` next to no such file runs nothing.
        let bare = dir.join("notepad");
        let error = open_path(bare.to_str().unwrap()).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    }

    #[test]
    fn a_path_too_long_for_the_shell_is_refused_with_the_reason() {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("open-long")
            .tempdir_in(&root)
            .unwrap();
        let mut deep = dir.path().to_path_buf();
        while deep.as_os_str().len() < 300 {
            deep.push("segment-of-a-long-path-0123456789");
        }
        std::fs::create_dir_all(format!(r"\\?\{}", deep.display())).unwrap();
        // If the shell ever ran it, it would only leave this marker.
        let marker = dir.path().join("opened.txt");
        let script = deep.join("opened.cmd");
        std::fs::write(
            format!(r"\\?\{}", script.display()),
            format!("@echo opened> \"{}\"\r\n@exit\r\n", marker.display()),
        )
        .unwrap();
        let error = open_path(script.to_str().unwrap()).unwrap_err();
        let FsError::InvalidPath { reason, .. } = &error else {
            panic!("{error:?}")
        };
        assert!(reason.contains("260"), "{reason}");
        std::thread::sleep(std::time::Duration::from_millis(500));
        assert!(!marker.exists(), "the shell ran the script");

        let missing = deep.join("missing.txt");
        let error = open_path(missing.to_str().unwrap()).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    }

    #[test]
    fn a_link_whose_target_is_gone_is_not_found_before_the_shell_is_asked() {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("open-link")
            .tempdir_in(&root)
            .unwrap();
        // An extension nothing is associated with: if the shell were asked,
        // it would refuse without opening anything.
        let target = dir.path().join("gone.cabinetos-link-test");
        std::fs::write(&target, "x").unwrap();
        let link = dir.path().join("link.cabinetos-link-test");
        if let Err(error) = std::os::windows::fs::symlink_file(&target, &link) {
            println!("skipped: this user may not make a symbolic link: {error}");
            return;
        }
        std::fs::remove_file(&target).unwrap();
        let error = open_path(link.to_str().unwrap()).unwrap_err();
        let FsError::NotFound { path } = &error else {
            panic!("{error:?}")
        };
        assert!(path.contains("gone.cabinetos-link-test"), "{path}");

        let folder = dir.path().join("gone folder");
        std::fs::create_dir(&folder).unwrap();
        let junction = dir.path().join("junction");
        let output = std::process::Command::new("cmd")
            .args(["/c", "mklink", "/J"])
            .arg(&junction)
            .arg(&folder)
            .output()
            .unwrap();
        assert!(output.status.success(), "{output:?}");
        std::fs::remove_dir(&folder).unwrap();
        let error = open_path(junction.to_str().unwrap()).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    }

    #[test]
    fn a_malformed_path_is_refused() {
        let error = open_path("").unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
    }
}
