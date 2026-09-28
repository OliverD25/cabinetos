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
    unsafe { ShellExecuteExW(&raw mut info) }.map_err(|error| FsError::from_windows(path, &error))
}

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
    fn a_malformed_path_is_refused() {
        let error = open_path("").unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
    }
}
