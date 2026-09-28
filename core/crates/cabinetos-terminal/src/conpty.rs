//! The Windows calls behind a session: anonymous pipes, the pseudo-console
//! (`ConPTY`), finding a program on the `PATH`, starting it attached to the
//! pseudo-console, and waiting for it. Every unsafe block states its
//! invariant in a `SAFETY:` comment.

use std::ffi::{OsStr, OsString, c_void};
use std::fs::File;
use std::io;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::{Path, PathBuf};
use std::time::Duration;

use windows::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0};
use windows::Win32::Storage::FileSystem::SearchPathW;
use windows::Win32::System::Console::{
    COORD, ClosePseudoConsole, CreatePseudoConsole, HPCON, ResizePseudoConsole,
};
use windows::Win32::System::Pipes::CreatePipe;
use windows::Win32::System::Threading::{
    CREATE_UNICODE_ENVIRONMENT, CreateProcessW, DeleteProcThreadAttributeList,
    EXTENDED_STARTUPINFO_PRESENT, GetExitCodeProcess, INFINITE, InitializeProcThreadAttributeList,
    LPPROC_THREAD_ATTRIBUTE_LIST, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, PROCESS_INFORMATION,
    STARTF_USESTDHANDLES, STARTUPINFOEXW, TerminateProcess, UpdateProcThreadAttribute,
    WaitForSingleObject,
};
use windows::core::{PCWSTR, PWSTR, w};

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain([0]).collect()
}

/// A new anonymous pipe: its read end and its write end.
pub(crate) fn pipe() -> io::Result<(File, File)> {
    let mut read = HANDLE::default();
    let mut write = HANDLE::default();
    // SAFETY: both outputs are valid locals; no security attributes, so the
    // handles are not inheritable; the default buffer size.
    unsafe { CreatePipe(&raw mut read, &raw mut write, None, 0) }?;
    // SAFETY: CreatePipe succeeded and returned two new handles that nothing
    // else owns.
    let read = unsafe { File::from_raw_handle(read.0) };
    // SAFETY: as above.
    let write = unsafe { File::from_raw_handle(write.0) };
    Ok((read, write))
}

fn size(cols: u16, rows: u16) -> COORD {
    let cells = |value: u16| i16::try_from(value.clamp(1, 32_767)).expect("clamped to i16");
    COORD {
        X: cells(cols),
        Y: cells(rows),
    }
}

/// A pseudo-console: a console without a window, whose screen goes to a pipe
/// as text with VT sequences, and whose keyboard comes from a pipe. Closing
/// it (drop) ends the programs attached to it.
pub(crate) struct PseudoConsole(HPCON);

impl PseudoConsole {
    /// Creates one of `cols` × `rows` cells that reads its input from `input`
    /// and writes its output to `output` (it keeps copies of both).
    pub(crate) fn create(cols: u16, rows: u16, input: &File, output: &File) -> io::Result<Self> {
        // SAFETY: both handles are open pipe ends for the whole call; the
        // pseudo-console duplicates them, so the caller may close its own.
        let console = unsafe {
            CreatePseudoConsole(
                size(cols, rows),
                HANDLE(input.as_raw_handle()),
                HANDLE(output.as_raw_handle()),
                0,
            )
        }?;
        Ok(Self(console))
    }

    /// Changes its size.
    pub(crate) fn resize(&self, cols: u16, rows: u16) -> io::Result<()> {
        // SAFETY: the pseudo-console is open: only Drop closes it.
        unsafe { ResizePseudoConsole(self.0, size(cols, rows)) }?;
        Ok(())
    }
}

impl Drop for PseudoConsole {
    fn drop(&mut self) {
        // SAFETY: the pseudo-console is open, and this is the only place
        // that closes it, exactly once.
        unsafe { ClosePseudoConsole(self.0) };
    }
}

/// A process attribute list that attaches a new process to a pseudo-console.
struct AttributeList {
    /// 8-byte aligned storage for the list.
    storage: Vec<u64>,
}

impl AttributeList {
    fn with_pseudo_console(console: &PseudoConsole) -> io::Result<Self> {
        let mut bytes = 0usize;
        // SAFETY: asks only for the size of a one-attribute list; this call
        // fails with ERROR_INSUFFICIENT_BUFFER by design.
        let _ = unsafe { InitializeProcThreadAttributeList(None, 1, None, &raw mut bytes) };
        let mut storage = vec![0u64; bytes.div_ceil(8)];
        let list = LPPROC_THREAD_ATTRIBUTE_LIST(storage.as_mut_ptr().cast());
        // SAFETY: `storage` is writable for at least `bytes` bytes and 8-byte
        // aligned; `bytes` is a valid in-out location.
        unsafe { InitializeProcThreadAttributeList(Some(list), 1, None, &raw mut bytes) }?;
        // From here on Drop deletes the initialized list.
        let this = Self { storage };
        // SAFETY: the list was initialized for one attribute. For this
        // attribute the value itself is the pseudo-console handle (not a
        // pointer to it), with the size of a handle; the pseudo-console
        // outlives every use of the list.
        unsafe {
            UpdateProcThreadAttribute(
                this.list(),
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                Some(std::ptr::without_provenance::<c_void>(
                    console.0.0.cast_unsigned(),
                )),
                size_of::<HPCON>(),
                None,
                None,
            )
        }?;
        Ok(this)
    }

    fn list(&self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        LPPROC_THREAD_ATTRIBUTE_LIST(self.storage.as_ptr().cast_mut().cast())
    }
}

impl Drop for AttributeList {
    fn drop(&mut self) {
        // SAFETY: the list was initialized (a value exists only after that)
        // and is deleted exactly once.
        unsafe { DeleteProcThreadAttributeList(self.list()) };
    }
}

/// A started shell.
pub(crate) struct Child {
    /// The process, to wait for it.
    pub(crate) process: OwnedHandle,
    /// Its ID.
    pub(crate) pid: u32,
}

/// Starts `program` with `command_line` (whose first word is the program)
/// in `cwd`, attached to `console`, with the environment block
/// `environment` (UTF-16, `name=value` entries, double NUL at the end).
pub(crate) fn spawn(
    console: &PseudoConsole,
    program: &Path,
    command_line: &str,
    environment: &[u16],
    cwd: &Path,
) -> io::Result<Child> {
    let attributes = AttributeList::with_pseudo_console(console)?;
    let mut startup = STARTUPINFOEXW::default();
    startup.StartupInfo.cb =
        u32::try_from(size_of::<STARTUPINFOEXW>()).expect("the structure is small");
    // Invalid standard handles: the shell must use the pseudo-console, and
    // must not inherit this process's own (a test runner's pipes, say).
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = INVALID_HANDLE_VALUE;
    startup.StartupInfo.hStdOutput = INVALID_HANDLE_VALUE;
    startup.StartupInfo.hStdError = INVALID_HANDLE_VALUE;
    startup.lpAttributeList = attributes.list();
    let program = wide(program.as_os_str());
    let mut line: Vec<u16> = command_line.encode_utf16().chain([0]).collect();
    let cwd = wide(cwd.as_os_str());
    let mut info = PROCESS_INFORMATION::default();
    // SAFETY: every string is NUL-terminated and outlives the call; the
    // command line buffer is writable, as CreateProcessW requires; the
    // environment block is UTF-16 with a double NUL at the end, as
    // CREATE_UNICODE_ENVIRONMENT says; `startup` begins with its
    // STARTUPINFOW, holds a valid attribute list (EXTENDED_STARTUPINFO_PRESENT)
    // and lives through the call; `info` is a valid output. No handle is
    // inherited.
    unsafe {
        CreateProcessW(
            PCWSTR(program.as_ptr()),
            Some(PWSTR(line.as_mut_ptr())),
            None,
            None,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            Some(environment.as_ptr().cast()),
            PCWSTR(cwd.as_ptr()),
            &raw const startup.StartupInfo,
            &raw mut info,
        )
    }?;
    // SAFETY: CreateProcessW succeeded and returned two new handles that this
    // function owns: the process (kept) and its first thread (closed now).
    let process = unsafe { OwnedHandle::from_raw_handle(info.hProcess.0) };
    // SAFETY: as above.
    drop(unsafe { OwnedHandle::from_raw_handle(info.hThread.0) });
    Ok(Child {
        process,
        pid: info.dwProcessId,
    })
}

/// The full path of `command` (with `.exe` added when it has no
/// extension): itself when absolute, else the first match in the `PATH`'s
/// folders. The current folder is not searched, so a stray `pwsh.exe` there
/// is never run.
pub(crate) fn find_program(command: &str) -> Option<PathBuf> {
    let path = Path::new(command);
    if path.is_absolute() {
        if path.is_file() {
            return Some(path.to_path_buf());
        }
        if path.extension().is_none() {
            let with_exe = path.with_extension("exe");
            return with_exe.is_file().then_some(with_exe);
        }
        return None;
    }
    let search = wide(&std::env::var_os("PATH")?);
    let name = wide(OsStr::new(command));
    let mut buffer = vec![0u16; 1024];
    loop {
        // SAFETY: both strings are NUL-terminated and outlive the call; the
        // binding passes the buffer's length; no file-part pointer is wanted.
        let len = unsafe {
            SearchPathW(
                PCWSTR(search.as_ptr()),
                PCWSTR(name.as_ptr()),
                w!(".exe"),
                Some(&mut buffer),
                None,
            )
        } as usize;
        if len == 0 {
            return None;
        }
        if len < buffer.len() {
            return Some(PathBuf::from(OsString::from_wide(&buffer[..len])));
        }
        buffer.resize(len + 1, 0);
    }
}

/// Waits until the process ends; returns its exit code.
pub(crate) fn wait_for_exit(process: &OwnedHandle) -> u32 {
    // SAFETY: the handle is open while borrowed; waiting changes nothing.
    unsafe { WaitForSingleObject(HANDLE(process.as_raw_handle()), INFINITE) };
    let mut code = 0u32;
    // SAFETY: the handle is open; `code` is a valid output.
    let _ = unsafe { GetExitCodeProcess(HANDLE(process.as_raw_handle()), &raw mut code) };
    code
}

/// Whether the process ends within `timeout`.
pub(crate) fn ends_within(process: &OwnedHandle, timeout: Duration) -> bool {
    let millis = u32::try_from(timeout.as_millis()).unwrap_or(INFINITE - 1);
    // SAFETY: the handle is open while borrowed; waiting changes nothing.
    let waited = unsafe { WaitForSingleObject(HANDLE(process.as_raw_handle()), millis) };
    waited == WAIT_OBJECT_0
}

/// Ends the process at once.
pub(crate) fn terminate(process: &OwnedHandle) {
    // SAFETY: the handle is open while borrowed and has terminate rights (it
    // came from CreateProcessW). Failing (already ended) is fine.
    let _ = unsafe { TerminateProcess(HANDLE(process.as_raw_handle()), 1) };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn programs_are_found_on_the_path_only() {
        let cmd = find_program("cmd").unwrap();
        assert!(cmd.ends_with("cmd.exe"), "{}", cmd.display());
        assert!(cmd.is_absolute());
        assert_eq!(find_program("cmd.exe"), Some(cmd.clone()));
        assert_eq!(find_program(&cmd.display().to_string()), Some(cmd.clone()));
        let without_extension = cmd.with_extension("");
        assert_eq!(
            find_program(&without_extension.display().to_string()),
            Some(cmd)
        );
        assert_eq!(find_program("cabinetos-no-such-shell"), None);
        assert_eq!(find_program(r"C:\cabinetos-no-such-folder\shell.exe"), None);
    }

    #[test]
    fn a_pipe_carries_bytes_and_ends() {
        use std::io::{Read, Write};
        let (mut read, mut write) = pipe().unwrap();
        write.write_all(b"abc").unwrap();
        drop(write);
        let mut text = String::new();
        read.read_to_string(&mut text).unwrap();
        assert_eq!(text, "abc");
    }
}
