//! Handing a file or folder to the shell: opening it with its default
//! application, as a double-click in Explorer does (the last step of
//! navigation), opening a file for editing (Total Commander's F4), and
//! showing Windows' own property sheet. What opens it (a viewer, an editor,
//! Explorer for a folder, the program itself for an `.exe`) is the user's
//! choice in Windows, not the core's.

use std::ffi::OsString;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};

use windows::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_DIRECTORY, GetFileAttributesW, INVALID_FILE_ATTRIBUTES,
};
use windows::Win32::System::Com::IDataObject;
use windows::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows::Win32::UI::Shell::Common::ITEMIDLIST;
use windows::Win32::UI::Shell::{
    ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_COMMAND, AssocQueryStringW, BHID_DataObject, ILFree,
    SEE_MASK_FLAG_NO_UI, SEE_MASK_INVOKEIDLIST, SEE_MASK_NO_CONSOLE, SEE_MASK_NOASYNC,
    SHCreateShellItemArrayFromIDLists, SHELLEXECUTEINFOW, SHMultiFileProperties,
    SHParseDisplayName, ShellExecuteExW,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows::core::{PCWSTR, w};

use crate::com::Apartment;
use crate::{FsError, path};

/// Opens `path`, a file or a folder, with its default application: the
/// `open` verb of `ShellExecuteExW`, without error dialogs, returning once
/// the shell has handed it over (not when the application ends). A console
/// program (a batch file, a script, a console `.exe`) gets a console window
/// of its own, as from Explorer.
///
/// The path must exist as given. The shell would otherwise look further: a
/// name without an extension may run a program with that name.
pub fn open_path(path: &str) -> Result<(), FsError> {
    existing(path)?;
    let plain = plain_wide(path)?;
    // The shell may hand the file to a COM server; COM wants to be ready.
    let _apartment = Apartment::enter();
    shell_execute(&open_launch(&plain)).map_err(|error| refused(path, &plain, &error))
}

/// The launch of `open_path` for the NUL-terminated plain path `plain`.
/// Without `SEE_MASK_NO_CONSOLE` a console program would share the core's
/// console, which the window starts without a window: it would run where
/// nobody sees it.
fn open_launch(plain: &[u16]) -> Launch<'_> {
    Launch {
        verb: w!("open"),
        file: plain,
        parameters: None,
        mask: SEE_MASK_NO_CONSOLE,
        directory: None,
    }
}

/// Shows Windows' own property sheet for `paths`, each of which must
/// exist: for one, the `properties` verb of `ShellExecuteExW`; for several,
/// the shell's combined sheet (`SHMultiFileProperties`), with what they
/// have in common. The shell runs the sheet on a thread of its own in this
/// process, so it stays open after this returns, for as long as the
/// process lives.
pub fn show_properties(paths: &[String]) -> Result<(), FsError> {
    let Some(first) = paths.first() else {
        return Err(FsError::InvalidPath {
            path: String::new(),
            reason: "no file or folder to show".to_owned(),
        });
    };
    for path in paths {
        existing(path)?;
    }
    let _apartment = Apartment::enter();
    if let [path] = paths {
        let plain = plain_wide(path)?;
        return shell_execute(&Launch {
            verb: w!("properties"),
            file: &plain,
            parameters: None,
            mask: SEE_MASK_INVOKEIDLIST,
            directory: None,
        })
        .map_err(|error| refused(path, &plain, &error));
    }
    let mut items = Items(Vec::with_capacity(paths.len()));
    for path in paths {
        let plain = plain_wide(path)?;
        let mut item = std::ptr::null_mut();
        // SAFETY: `plain` is NUL-terminated and outlives the call; `item`
        // is a valid output, and `Items` frees what the shell allocates.
        unsafe { SHParseDisplayName(PCWSTR(plain.as_ptr()), None, &raw mut item, 0, None) }
            .map_err(|error| shell_error(path, &error))?;
        items.0.push(item);
    }
    let list: Vec<*const ITEMIDLIST> = items.0.iter().map(|item| item.cast_const()).collect();
    // SAFETY: every entry is an absolute item ID list from
    // SHParseDisplayName, alive until `items` drops, after these calls.
    let sheet = unsafe {
        SHCreateShellItemArrayFromIDLists(&list)
            .and_then(|array| array.BindToHandler::<_, IDataObject>(None, &BHID_DataObject))
            .and_then(|data| SHMultiFileProperties(&data, 0))
    };
    sheet.map_err(|error| shell_error(first, &error))
}

/// Item ID lists the shell allocated, freed when dropped.
struct Items(Vec<*mut ITEMIDLIST>);

impl Drop for Items {
    fn drop(&mut self) {
        for &item in &self.0 {
            // SAFETY: each came from SHParseDisplayName and is freed once;
            // nothing uses it after.
            unsafe { ILFree(Some(item.cast_const())) };
        }
    }
}

/// A program to edit files with: `files.editor`, its program found.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Editor {
    /// The program's full path.
    pub program: PathBuf,
    /// Its arguments before the file's path.
    pub args: Vec<String>,
}

/// Opens the file `path` for editing, and never runs it: with `editor`
/// when there is one; else with the `edit` verb of the file's type, when
/// the type has one (Notepad for a batch file, whose `open` would run it);
/// else with Notepad, Total Commander's default editor. Returns once the
/// program is started. A console program gets a console of its own, as
/// from Explorer: the core's own console has no window.
pub fn edit_path(path: &str, editor: Option<&Editor>) -> Result<(), FsError> {
    check_editable(path)?;
    let plain = plain_wide(path)?;
    let _apartment = Apartment::enter();
    let program = match editor {
        Some(editor) => editor.clone(),
        None if has_edit_verb(path) => {
            return shell_execute(&Launch {
                verb: w!("edit"),
                file: &plain,
                parameters: None,
                mask: SEE_MASK_NO_CONSOLE,
                directory: None,
            })
            .map_err(|error| refused(path, &plain, &error));
        }
        None => Editor {
            program: notepad(path)?,
            args: Vec::new(),
        },
    };
    let file: Vec<u16> = program
        .program
        .as_os_str()
        .encode_wide()
        .chain([0])
        .collect();
    let file_path = String::from_utf16_lossy(&plain[..plain.len() - 1]);
    let parameters: Vec<u16> = parameters(&program.args, &file_path)
        .encode_utf16()
        .chain([0])
        .collect();
    shell_execute(&Launch {
        verb: w!("open"),
        file: &file,
        parameters: Some(&parameters),
        mask: SEE_MASK_NO_CONSOLE,
        directory: None,
    })
    .map_err(|error| shell_error(&program.program.display().to_string(), &error))
}

/// Starts `program` (a full path, found by the caller) with `arguments`,
/// each quoted as the Microsoft C runtime splits a command line back
/// ([`command_line`]), through the same `ShellExecuteExW` call as
/// [`edit_path`]: no error dialogs, a console program with a console of
/// its own. `directory` is its working folder when it is an existing
/// folder whose plain path the shell takes (shorter than 260 characters);
/// otherwise the shell's default. Returns once the program is started.
/// The `programs` of the configuration start this way (Phase 18).
pub fn start_program(
    program: &Path,
    arguments: &[String],
    directory: Option<&str>,
) -> Result<(), FsError> {
    let file: Vec<u16> = program.as_os_str().encode_wide().chain([0]).collect();
    let parameters: Vec<u16> = command_line(arguments).encode_utf16().chain([0]).collect();
    let folder = directory
        .filter(|folder| Path::new(folder).is_dir())
        .map(|folder| folder.encode_utf16().chain([0]).collect::<Vec<u16>>())
        .filter(|wide| wide.len() <= MAX_PATH);
    let _apartment = Apartment::enter();
    shell_execute(&Launch {
        verb: w!("open"),
        file: &file,
        parameters: Some(&parameters),
        mask: SEE_MASK_NO_CONSOLE,
        directory: folder.as_deref(),
    })
    .map_err(|error| shell_error(&program.display().to_string(), &error))
}

/// `arguments` as one command line, each quoted the way the Microsoft C
/// runtime splits a command line back into arguments (the rules
/// `std::process::Command` follows): quotes only where needed.
#[must_use]
pub fn command_line(arguments: &[String]) -> String {
    let mut line = String::new();
    for arg in arguments {
        if !line.is_empty() {
            line.push(' ');
        }
        push_argument(&mut line, arg);
    }
    line
}

/// `Ok` when `path` can be opened for editing: it exists as given (a link
/// whose target is gone does not), and it is a file. [`edit_path`] checks
/// it first; a caller that must find the editor on its own asks it before
/// that, so a bad path is the answer whatever the editor.
pub fn check_editable(path: &str) -> Result<(), FsError> {
    let attributes = existing(path)?;
    if attributes & FILE_ATTRIBUTE_DIRECTORY.0 != 0 {
        return Err(FsError::InvalidPath {
            path: path.to_owned(),
            reason: "it is a folder; only a file can be edited".to_owned(),
        });
    }
    Ok(())
}

/// The attributes of `path`, which must exist as given. A link whose
/// target is gone exists itself, but the shell can open nothing through it
/// and says only "unspecified error": that is `NotFound`, naming the
/// target.
fn existing(path: &str) -> Result<u32, FsError> {
    let verbatim = path::verbatim_wide(path)?;
    // SAFETY: `verbatim` is NUL-terminated and outlives the call.
    let attributes = unsafe { GetFileAttributesW(PCWSTR(verbatim.as_ptr())) };
    if attributes == INVALID_FILE_ATTRIBUTES {
        return Err(FsError::from_windows(
            path,
            &windows::core::Error::from_thread(),
        ));
    }
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
    Ok(attributes)
}

/// `path` made absolute in its plain form, NUL-terminated: the shell parses
/// plain paths, not the `\\?\` form.
fn plain_wide(path: &str) -> Result<Vec<u16>, FsError> {
    let plain = std::path::absolute(path).map_err(|error| FsError::InvalidPath {
        path: path.to_owned(),
        reason: error.to_string(),
    })?;
    Ok(plain.as_os_str().encode_wide().chain([0]).collect())
}

/// One call of `ShellExecuteExW`.
struct Launch<'a> {
    verb: PCWSTR,
    /// The file to open, or the program to start; NUL-terminated.
    file: &'a [u16],
    /// The program's arguments, NUL-terminated.
    parameters: Option<&'a [u16]>,
    /// More `SEE_MASK_` flags. `SEE_MASK_NO_CONSOLE`: a console program
    /// gets a console of its own instead of sharing the core's, which has
    /// no window. `SEE_MASK_INVOKEIDLIST`: the verb comes from the item's
    /// own context menu, which is where `properties` is.
    mask: u32,
    /// The program's working folder, NUL-terminated; `None` leaves it to
    /// the shell.
    directory: Option<&'a [u16]>,
}

/// Runs `launch` without error dialogs, returning once the shell is done
/// with it. The caller is in a COM apartment.
fn shell_execute(launch: &Launch<'_>) -> windows::core::Result<()> {
    let mut info = execute_info(launch);
    // SAFETY: `info` is a valid structure with its size in `cbSize`; its
    // strings are `launch`'s, NUL-terminated, and outlive the call, which
    // returns once the shell is done with them (SEE_MASK_NOASYNC).
    unsafe { ShellExecuteExW(&raw mut info) }
}

/// What `ShellExecuteExW` gets for `launch`: always without error dialogs
/// and returning only once the shell is done, plus the launch's own flags.
fn execute_info(launch: &Launch<'_>) -> SHELLEXECUTEINFOW {
    SHELLEXECUTEINFOW {
        cbSize: u32::try_from(size_of::<SHELLEXECUTEINFOW>()).unwrap_or(u32::MAX),
        fMask: SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | launch.mask,
        lpVerb: launch.verb,
        lpFile: PCWSTR(launch.file.as_ptr()),
        lpParameters: launch
            .parameters
            .map_or_else(PCWSTR::null, |parameters| PCWSTR(parameters.as_ptr())),
        lpDirectory: launch
            .directory
            .map_or_else(PCWSTR::null, |directory| PCWSTR(directory.as_ptr())),
        nShow: SW_SHOWNORMAL.0,
        ..SHELLEXECUTEINFOW::default()
    }
}

/// The error for a file the shell refused to open, `plain` its
/// NUL-terminated plain path.
fn refused(path: &str, plain: &[u16], error: &windows::core::Error) -> FsError {
    // The terminating NUL is not part of the length.
    let length = plain.len() - 1;
    if length >= MAX_PATH {
        // Whether the shell takes such a path depends on the program
        // (Notepad took 339 characters, cmd refused 311). It gives no
        // reason, and the short (8.3) form does not help: the shell turns
        // it back into the long one.
        return FsError::InvalidPath {
            path: path.to_owned(),
            reason: format!(
                "the shell refused to open it; its path has {length} characters, and \
                 many programs take only paths shorter than {MAX_PATH}; move the file \
                 or shorten a folder name on the way"
            ),
        };
    }
    shell_error(path, error)
}

/// The error for a shell call about `subject`, with the shell's code when
/// Windows has no text for it.
fn shell_error(subject: &str, error: &windows::core::Error) -> FsError {
    match FsError::from_windows(subject, error) {
        FsError::Io { path, source } if source.to_string().is_empty() => FsError::Io {
            path,
            source: std::io::Error::other(format!(
                "the shell cannot open it (error {:#010x})",
                error.code().0
            )),
        },
        other => other,
    }
}

/// Whether the type of the file `path` names has an `edit` verb with a
/// command. A type Windows does not know has none.
fn has_edit_verb(path: &str) -> bool {
    let Some(extension) = extension(path) else {
        return false;
    };
    let extension: Vec<u16> = extension.encode_utf16().chain([0]).collect();
    let mut length = 0u32;
    // SAFETY: both strings are NUL-terminated and outlive the call; without
    // an output buffer the call only writes the command's length.
    let found = unsafe {
        AssocQueryStringW(
            ASSOCF_INIT_IGNOREUNKNOWN,
            ASSOCSTR_COMMAND,
            PCWSTR(extension.as_ptr()),
            w!("edit"),
            None,
            &raw mut length,
        )
    };
    found.is_ok()
}

/// The extension of the file `path` names, with its dot: from the last dot
/// of the name on, as the Type column reads it (`.gitignore` has one).
fn extension(path: &str) -> Option<&str> {
    let name = path.rsplit(['\\', '/']).next().unwrap_or(path);
    let dot = name.rfind('.')?;
    (dot + 1 < name.len()).then(|| &name[dot..])
}

/// Notepad, in the system folder: never a `notepad.exe` found elsewhere.
fn notepad(path: &str) -> Result<PathBuf, FsError> {
    let mut folder = [0u16; 512];
    // SAFETY: the binding passes the buffer's length; Windows writes at
    // most that many units and returns how many it wrote.
    let length = unsafe { GetSystemDirectoryW(Some(&mut folder)) } as usize;
    if length == 0 || length >= folder.len() {
        return Err(FsError::Io {
            path: path.to_owned(),
            source: std::io::Error::other("cannot find the system folder, where Notepad is"),
        });
    }
    Ok(PathBuf::from(OsString::from_wide(&folder[..length])).join("notepad.exe"))
}

/// A program's arguments with `path` last, each quoted the way the
/// Microsoft C runtime splits a command line back into arguments (the same
/// rules `std::process::Command` follows).
fn parameters(args: &[String], path: &str) -> String {
    let mut line = String::new();
    for arg in args.iter().map(String::as_str).chain([path]) {
        if !line.is_empty() {
            line.push(' ');
        }
        push_argument(&mut line, arg);
    }
    line
}

fn push_argument(line: &mut String, arg: &str) {
    if !arg.is_empty() && !arg.contains([' ', '\t', '\n', '\u{b}', '"']) {
        line.push_str(arg);
        return;
    }
    line.push('"');
    let mut backslashes = 0;
    for c in arg.chars() {
        match c {
            '\\' => backslashes += 1,
            '"' => {
                // Backslashes before a quote are doubled, and the quote
                // itself gets one.
                line.extend(std::iter::repeat_n('\\', backslashes * 2 + 1));
                line.push('"');
                backslashes = 0;
            }
            _ => {
                line.extend(std::iter::repeat_n('\\', backslashes));
                line.push(c);
                backslashes = 0;
            }
        }
    }
    // Backslashes before the closing quote are doubled too.
    line.extend(std::iter::repeat_n('\\', backslashes * 2));
    line.push('"');
}

/// The shell's path limit: a path must be shorter, in UTF-16 units.
const MAX_PATH: usize = 260;

#[cfg(test)]
mod tests {
    use super::*;

    // Opening or editing a file for real starts an application and leaves
    // its window open on the desktop, so the tests stop before the shell is
    // called. The CLI's tests start an editor that records its arguments.

    fn scratch(prefix: &str) -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix(prefix)
            .tempdir_in(&root)
            .unwrap()
    }

    /// An editor that is never started: every test that names it fails
    /// before the shell is asked.
    fn never_started() -> Editor {
        Editor {
            program: PathBuf::from(r"C:\cabinetos-no-such-folder\editor.exe"),
            args: Vec::new(),
        }
    }

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
        for editor in [None, Some(&never_started())] {
            let error = edit_path(missing.to_str().unwrap(), editor).unwrap_err();
            assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
        }
    }

    #[test]
    fn a_folder_cannot_be_edited() {
        let dir = scratch("edit-folder");
        for editor in [None, Some(&never_started())] {
            let error = edit_path(dir.path().to_str().unwrap(), editor).unwrap_err();
            let FsError::InvalidPath { reason, .. } = &error else {
                panic!("{error:?}")
            };
            assert!(reason.contains("folder"), "{reason}");
        }
    }

    #[test]
    fn a_path_too_long_for_the_shell_is_refused_with_the_reason() {
        let dir = scratch("open-long");
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
        let dir = scratch("open-link");
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
        let error = edit_path(link.to_str().unwrap(), Some(&never_started())).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");

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
        let error = edit_path("", None).unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
    }

    /// A batch file's `edit` opens Notepad where `open` would run it; a
    /// type Windows does not know, and a name without an extension, have
    /// no `edit`, so Notepad opens them.
    #[test]
    fn the_edit_verb_is_asked_of_the_file_s_type() {
        assert!(has_edit_verb(r"C:\work\build.bat"));
        assert!(has_edit_verb(r"C:\work\BUILD.CMD"));
        assert!(!has_edit_verb(r"C:\work\notes.cabinetos-no-such-type"));
        assert!(!has_edit_verb(r"C:\work\README"));
        assert!(!has_edit_verb(r"C:\a.bat\README"));
        assert!(!has_edit_verb(r"C:\work\trailing."));
        assert_eq!(extension(r"C:\a.b\.gitignore"), Some(".gitignore"));
        assert_eq!(extension(r"C:\a.b\c.tar.gz"), Some(".gz"));
    }

    /// The dialogs (window class `#32770`, as a property sheet is) this
    /// process shows now.
    fn sheets() -> Vec<isize> {
        use windows::Win32::Foundation::{HWND, LPARAM};
        use windows::Win32::System::Threading::GetCurrentProcessId;
        use windows::Win32::UI::WindowsAndMessaging::{
            EnumWindows, GetClassNameW, GetWindowThreadProcessId, IsWindowVisible,
        };
        use windows::core::BOOL;

        unsafe extern "system" fn collect(window: HWND, found: LPARAM) -> BOOL {
            let mut process = 0;
            let mut class = [0u16; 16];
            // SAFETY: `window` came from EnumWindows; the outputs are valid
            // and the binding passes the buffer's length.
            let (length, visible) = unsafe {
                GetWindowThreadProcessId(window, Some(&raw mut process));
                (GetClassNameW(window, &mut class), IsWindowVisible(window))
            };
            let dialog = usize::try_from(length)
                .is_ok_and(|length| String::from_utf16_lossy(&class[..length]) == "#32770");
            // SAFETY: a plain call.
            if dialog && visible.as_bool() && process == unsafe { GetCurrentProcessId() } {
                // SAFETY: `found` is the vector `sheets` lent for this
                // enumeration, which runs on this thread.
                let found = unsafe { &mut *(found.0 as *mut Vec<isize>) };
                found.push(window.0 as isize);
            }
            BOOL::from(true)
        }

        let mut found: Vec<isize> = Vec::new();
        // SAFETY: `collect` only reads windows and writes into `found`,
        // which outlives the enumeration.
        let _ = unsafe { EnumWindows(Some(collect), LPARAM(&raw mut found as isize)) };
        found
    }

    /// What `probe` finds within 10 s.
    fn within_ten_seconds<T>(what: &str, mut probe: impl FnMut() -> Option<T>) -> T {
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        loop {
            if let Some(found) = probe() {
                return found;
            }
            assert!(std::time::Instant::now() < deadline, "{what}");
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
    }

    /// The sheet comes up for one file and for several, stays open after
    /// the thread that asked for it ended (as a pool thread of the core
    /// may), and the test closes it.
    #[test]
    fn the_property_sheet_shows_one_file_or_several_and_outlives_its_caller() {
        use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
        use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};

        let dir = scratch("properties");
        let a = dir.path().join("a.txt");
        let b = dir.path().join("Звіт b.txt");
        std::fs::write(&a, "a").unwrap();
        std::fs::write(&b, "b").unwrap();
        let text = |path: &std::path::Path| path.to_str().unwrap().to_owned();
        let missing = [text(&a), text(&dir.path().join("gone.txt"))];
        let error = show_properties(&missing).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
        let error = show_properties(&[]).unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
        assert!(sheets().is_empty(), "a refusal showed a sheet");

        for paths in [vec![text(&a)], vec![text(&a), text(&b), text(dir.path())]] {
            let before = sheets();
            std::thread::spawn(move || show_properties(&paths))
                .join()
                .unwrap()
                .unwrap();
            let sheet = within_ten_seconds("no sheet came up", || {
                sheets().into_iter().find(|sheet| !before.contains(sheet))
            });
            std::thread::sleep(std::time::Duration::from_secs(1));
            assert!(sheets().contains(&sheet), "the sheet closed by itself");
            // SAFETY: a plain call; the window may be gone already, which
            // makes it fail and nothing else.
            unsafe { PostMessageW(Some(HWND(sheet as _)), WM_CLOSE, WPARAM(0), LPARAM(0)) }
                .unwrap();
            within_ten_seconds("the sheet did not close", || {
                (!sheets().contains(&sheet)).then_some(())
            });
        }
    }

    /// What `open_path` hands the shell for a batch file: a console of its
    /// own, no error dialogs, the call returning once the shell is done.
    /// The launch itself is not made: it would run the file.
    #[test]
    fn open_path_gives_a_console_program_a_console_of_its_own() {
        let file: Vec<u16> = r"C:\workuild 2026.cmd".encode_utf16().chain([0]).collect();
        let info = execute_info(&open_launch(&file));
        let wanted = SEE_MASK_NO_CONSOLE | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        assert_eq!(info.fMask & wanted, wanted, "{:#x}", info.fMask);
        assert_eq!(info.fMask & SEE_MASK_INVOKEIDLIST, 0);
        // SAFETY: the verb is a NUL-terminated string literal from `w!`.
        assert_eq!(unsafe { info.lpVerb.to_string() }.unwrap(), "open");
        assert_eq!(info.lpFile.0, file.as_ptr());
        assert!(info.lpParameters.is_null());
        assert_eq!(info.nShow, SW_SHOWNORMAL.0);
    }

    #[test]
    fn notepad_is_the_system_folder_s() {
        let notepad = notepad("x").unwrap();
        assert!(notepad.is_absolute() && notepad.is_file(), "{notepad:?}");
        // Windows spells it `system32`; names compare without case.
        assert!(
            notepad
                .display()
                .to_string()
                .to_lowercase()
                .ends_with(r"\system32\notepad.exe"),
            "{notepad:?}"
        );
    }

    #[test]
    fn the_file_s_path_is_the_last_argument_quoted_for_the_c_runtime() {
        let args = |args: &[&str]| args.iter().map(|arg| (*arg).to_owned()).collect::<Vec<_>>();
        assert_eq!(parameters(&[], r"C:\notes.txt"), r"C:\notes.txt");
        assert_eq!(
            parameters(&args(&["-n", "--wait"]), r"C:\my notes\a b.txt"),
            r#"-n --wait "C:\my notes\a b.txt""#
        );
        assert_eq!(
            parameters(&args(&["", r#"say "hi""#, r"C:\dir\"]), r"C:\dir x\"),
            r#""" "say \"hi\"" C:\dir\ "C:\dir x\\""#
        );
        assert_eq!(
            parameters(&[], "E:\\Звіт 📁 cafe\u{301}.txt"),
            "\"E:\\Звіт 📁 cafe\u{301}.txt\""
        );
    }
}
