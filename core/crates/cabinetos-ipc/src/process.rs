//! Watching another process's lifetime.
//!
//! The core must not outlive the UI that started it (PLAN §2, "Process
//! layout"). The UI passes its PID; the core waits on the process handle on a
//! dedicated thread and shuts down when the handle is signalled.

use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use windows::Win32::Foundation::{HANDLE, WAIT_OBJECT_0};
use windows::Win32::System::Pipes::GetNamedPipeClientProcessId;
use windows::Win32::System::Threading::{
    INFINITE, OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
};

use crate::IpcError;

/// Calls `on_exit` once process `pid` has exited.
///
/// The process is opened before this function returns, so a PID that does
/// not exist (or has already exited and been cleaned up) is an error here
/// rather than a silent watcher. The wait blocks a dedicated thread, never an
/// async worker. If the wait itself fails, `on_exit` is called as well: the
/// safe reaction to losing sight of the parent is to shut down.
pub fn watch_process_exit<F>(pid: u32, on_exit: F) -> Result<(), IpcError>
where
    F: FnOnce() + Send + 'static,
{
    // SAFETY: OpenProcess has no memory-safety preconditions; failure is
    // reported through the Result.
    let process = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, pid) }?;
    // SAFETY: OpenProcess succeeded, so this is a new handle we own.
    let process = unsafe { OwnedHandle::from_raw_handle(process.0) };

    std::thread::Builder::new()
        .name(format!("watch-process-{pid}"))
        .spawn(move || {
            // SAFETY: `process` is a valid handle with SYNCHRONIZE access and
            // stays open for the whole wait, because the closure owns it.
            let result = unsafe { WaitForSingleObject(HANDLE(process.as_raw_handle()), INFINITE) };
            if result != WAIT_OBJECT_0 {
                tracing::warn!(
                    pid,
                    result = result.0,
                    "waiting for the process failed; treating it as exited"
                );
            }
            drop(process);
            on_exit();
        })?;
    Ok(())
}

/// The ID of the process connected to the server end of a pipe, as Windows
/// knows it. The core compares it with the ID a client claims in `hello`.
pub(crate) fn pipe_client_process_id(pipe: &impl AsRawHandle) -> Result<u32, IpcError> {
    let mut pid = 0u32;
    // SAFETY: the handle is the server end of a pipe instance and stays open
    // for the call; `pid` is a valid output location.
    unsafe { GetNamedPipeClientProcessId(HANDLE(pipe.as_raw_handle()), &raw mut pid) }?;
    Ok(pid)
}

#[cfg(test)]
mod tests {
    use std::process::{Command, Stdio};
    use std::sync::mpsc;
    use std::time::Duration;

    use super::*;

    #[test]
    fn reports_when_the_process_exits() {
        let mut child = Command::new("cmd.exe")
            .args(["/c", "pause"])
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .spawn()
            .unwrap();
        let (sender, receiver) = mpsc::channel();
        watch_process_exit(child.id(), move || sender.send(()).unwrap()).unwrap();

        assert!(
            receiver.recv_timeout(Duration::from_millis(200)).is_err(),
            "fired while the process was still running"
        );
        child.kill().unwrap();
        child.wait().unwrap();
        receiver
            .recv_timeout(Duration::from_secs(5))
            .expect("no exit notice within 5 s");
    }

    #[test]
    fn a_missing_process_is_an_error() {
        // PID 0 is the System Idle Process, which cannot be opened.
        assert!(watch_process_exit(0, || {}).is_err());
    }
}
