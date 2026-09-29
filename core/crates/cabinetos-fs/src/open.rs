//! Handing a file or folder to the shell: opening it with its default
//! application, as a double-click in Explorer does (the last step of
//! navigation), and opening a file for editing (Total Commander's F4). What
//! opens it (a viewer, an editor, Explorer for a folder, the program itself
//! for an `.exe`) is the user's choice in Windows, not the core's.

use std::ffi::OsString;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::PathBuf;

use windows::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_DIRECTORY, GetFileAttributesW, INVALID_FILE_ATTRIBUTES,
};
use windows::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows::Win32::UI::Shell::{
    ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_COMMAND, AssocQueryStringW, SEE_MASK_FLAG_NO_UI,
    SEE_MASK_NO_CONSOLE, SEE_MASK_NOASYNC, SHELLEXECUTEINFOW, ShellExecuteExW,
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
    existing(path)?;
    let plain = plain_wide(path)?;
    // The shell may hand the file to a COM server; COM wants to be ready.
    let _apartment = Apartment::enter();
    shell_execute(&Launch {
        verb: w!("open"),
        file: &plain,
        parameters: None,
        own_console: false,
    })
    .map_err(|error| refused(path, &plain, &error))
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
    let attributes = existing(path)?;
    if attributes & FILE_ATTRIBUTE_DIRECTORY.0 != 0 {
        return Err(FsError::InvalidPath {
            path: path.to_owned(),
            reason: "it is a folder; only a file can be edited".to_owned(),
        });
    }
    let plain = plain_wide(path)?;
    let _apartment = Apartment::enter();
    let program = match editor {
        Some(editor) => editor.clone(),
        None if has_edit_verb(path) => {
            return shell_execute(&Launch {
                verb: w!("edit"),
                file: &plain,
                parameters: None,
                own_console: true,
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
        own_console: true,
    })
    .map_err(|error| shell_error(&program.program.display().to_string(), &error))
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
    /// A console program gets a console of its own instead of sharing the
    /// core's, which has no window.
    own_console: bool,
}

/// Runs `launch` without error dialogs, returning once the shell is done
/// with it. The caller is in a COM apartment.
fn shell_execute(launch: &Launch<'_>) -> windows::core::Result<()> {
    let mut mask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    if launch.own_console {
        mask |= SEE_MASK_NO_CONSOLE;
    }
    let mut info = SHELLEXECUTEINFOW {
        cbSize: u32::try_from(size_of::<SHELLEXECUTEINFOW>()).unwrap_or(u32::MAX),
        fMask: mask,
        lpVerb: launch.verb,
        lpFile: PCWSTR(launch.file.as_ptr()),
        lpParameters: launch
            .parameters
            .map_or_else(PCWSTR::null, |parameters| PCWSTR(parameters.as_ptr())),
        nShow: SW_SHOWNORMAL.0,
        ..SHELLEXECUTEINFOW::default()
    };
    // SAFETY: `info` is a valid structure with its size in `cbSize`; its
    // strings are NUL-terminated and outlive the call, which returns once
    // the shell is done with them (SEE_MASK_NOASYNC).
    unsafe { ShellExecuteExW(&raw mut info) }
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
