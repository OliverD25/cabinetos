//! Watching one directory for changes with `ReadDirectoryChangesW`.
//!
//! Each watched directory gets a dedicated thread that waits on the change
//! notification and on a stop event. `start` reports only that something
//! changed; the caller reads the directory again rather than patching the
//! listing (reading 100,000 entries takes tens of milliseconds, and patching
//! can wait until a measurement says it is needed). `start_detailed` also
//! reads Windows' records: which entry was created, modified, removed or
//! renamed (a plugin's `watch-folder`).
//!
//! The directory is opened with `FILE_SHARE_DELETE`, so watching never stops
//! the user from deleting or renaming it.

use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::sync::{Arc, mpsc};
use std::thread::JoinHandle;

use windows::Win32::Foundation::{HANDLE, WAIT_OBJECT_0};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OVERLAPPED, FILE_LIST_DIRECTORY,
    FILE_NOTIFY_CHANGE_ATTRIBUTES, FILE_NOTIFY_CHANGE_DIR_NAME, FILE_NOTIFY_CHANGE_FILE_NAME,
    FILE_NOTIFY_CHANGE_LAST_WRITE, FILE_NOTIFY_CHANGE_SIZE, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, OPEN_EXISTING, ReadDirectoryChangesW,
};
use windows::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows::Win32::System::Threading::{
    CreateEventW, INFINITE, ResetEvent, SetEvent, WaitForMultipleObjects,
};
use windows::core::PCWSTR;

use crate::{FsError, path};

/// Notification buffer: 64 KiB, the most `ReadDirectoryChangesW` accepts for
/// directories on network shares. Its contents are not read; only the
/// completion matters.
const BUFFER_SIZE: usize = 64 * 1024;

/// `ERROR_OPERATION_ABORTED`: the stop event cancelled the wait.
const ERROR_OPERATION_ABORTED: u32 = 995;
/// `ERROR_NOTIFY_ENUM_DIR`: too many changes to report one by one.
const ERROR_NOTIFY_ENUM_DIR: u32 = 1022;

/// What a watched directory reports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum DirectoryChanged {
    /// Entries were added, removed, renamed or modified.
    Changed,
    /// Too many changes at once; Windows lost track of which. Read the
    /// directory again from scratch.
    Overflow,
    /// Watching stopped because of an error, for example because the
    /// directory was deleted. Nothing more follows.
    Failed(String),
}

/// What kind of change one entry went through.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EntryChangeKind {
    /// It appeared: created, or moved or renamed in from elsewhere.
    Created,
    /// Its contents, size, attributes or write time changed.
    Modified,
    /// It went: deleted, or moved or renamed away.
    Removed,
    /// It got a new name inside the directory.
    Renamed,
}

/// One change inside a directory watched with `start_detailed`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EntryChange {
    /// What happened to it.
    pub kind: EntryChangeKind,
    /// The entry's name inside the directory (after a rename, the new one).
    pub name: String,
    /// For `Renamed`: the name before, when Windows reported both halves in
    /// the same batch.
    pub old_name: Option<String>,
}

/// What a directory watched with `start_detailed` reports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum DetailedChange {
    /// These entries changed, in the order Windows reported them.
    Entries(Vec<EntryChange>),
    /// Too many changes at once; Windows lost track of which.
    Overflow,
    /// Watching stopped because of an error, for example because the
    /// directory was deleted. Nothing more follows.
    Failed(String),
}

/// One completed notification request, as the watching thread sees it.
enum Completion<'a> {
    /// The records Windows wrote.
    Changed(&'a [u8]),
    Overflow,
    Failed(String),
}

/// Watches one directory until dropped or stopped.
#[derive(Debug)]
pub struct DirectoryWatcher {
    /// Signalled to end the watching thread.
    stop: Arc<OwnedHandle>,
    thread: Option<JoinHandle<()>>,
}

impl DirectoryWatcher {
    /// Starts watching the directory at `path` (not its subdirectories) on a
    /// new thread named `thread_name`. `on_change` runs on that thread, once
    /// per batch of changes, and must return quickly.
    ///
    /// When this returns, the first request for notifications is already
    /// with Windows, so no change made afterwards can be missed.
    pub fn start<F>(path: &str, thread_name: String, mut on_change: F) -> Result<Self, FsError>
    where
        F: FnMut(DirectoryChanged) + Send + 'static,
    {
        Self::start_with(path, thread_name, move |completion| {
            on_change(match completion {
                Completion::Changed(_) => DirectoryChanged::Changed,
                Completion::Overflow => DirectoryChanged::Overflow,
                Completion::Failed(message) => DirectoryChanged::Failed(message),
            });
        })
    }

    /// `start`, reporting which entries changed and how.
    pub fn start_detailed<F>(
        path: &str,
        thread_name: String,
        mut on_change: F,
    ) -> Result<Self, FsError>
    where
        F: FnMut(DetailedChange) + Send + 'static,
    {
        Self::start_with(path, thread_name, move |completion| {
            on_change(match completion {
                Completion::Changed(records) => {
                    DetailedChange::Entries(parse_notifications(records))
                }
                Completion::Overflow => DetailedChange::Overflow,
                Completion::Failed(message) => DetailedChange::Failed(message),
            });
        })
    }

    fn start_with<F>(path: &str, thread_name: String, on_change: F) -> Result<Self, FsError>
    where
        F: FnMut(Completion<'_>) + Send + 'static,
    {
        let wide = path::verbatim_wide(path)?;
        let directory = open_for_watching(path, &wide)?;
        let io_error = |error: io::Error| FsError::Io {
            path: path.to_owned(),
            source: error,
        };
        let stop = Arc::new(new_event().map_err(io_error)?);
        let io_event = new_event().map_err(io_error)?;
        let (armed_tx, armed_rx) = mpsc::channel();
        let thread_stop = Arc::clone(&stop);
        let thread = std::thread::Builder::new()
            .name(thread_name)
            .spawn(move || watch(&directory, &io_event, &thread_stop, on_change, armed_tx))
            .map_err(io_error)?;
        match armed_rx.recv() {
            Ok(Ok(())) => Ok(Self {
                stop,
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                let _ = thread.join();
                Err(FsError::from_windows(path, &error))
            }
            Err(_) => {
                let _ = thread.join();
                Err(io_error(io::Error::other(
                    "the watching thread ended before it started",
                )))
            }
        }
    }

    /// Stops watching and waits for the thread to end (it ends at once).
    pub fn stop(mut self) {
        signal(&self.stop);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for DirectoryWatcher {
    /// Tells the thread to end, without waiting: dropping must never block
    /// an async task. The thread owns everything it still uses.
    fn drop(&mut self) {
        signal(&self.stop);
    }
}

/// What one completed notification request means.
#[derive(Debug, PartialEq, Eq)]
enum Outcome {
    Changed,
    Overflow,
    Stopped,
    Failed(String),
}

/// Classifies a completion: the bytes returned, or the Win32 error code.
fn classify(result: Result<u32, u32>) -> Outcome {
    match result {
        // Zero bytes on success: the buffer overflowed and was discarded.
        Ok(0) | Err(ERROR_NOTIFY_ENUM_DIR) => Outcome::Overflow,
        Ok(_) => Outcome::Changed,
        Err(ERROR_OPERATION_ABORTED) => Outcome::Stopped,
        Err(code) => Outcome::Failed(io::Error::from_raw_os_error(code.cast_signed()).to_string()),
    }
}

/// The watching thread: request notifications, wait for them or for the stop
/// event, report, repeat. Every request is complete (or cancelled and
/// complete) before `overlapped` and `buffer` go out of scope.
fn watch<F>(
    directory: &OwnedHandle,
    io_event: &OwnedHandle,
    stop: &OwnedHandle,
    mut on_change: F,
    armed: mpsc::Sender<windows::core::Result<()>>,
) where
    F: FnMut(Completion<'_>),
{
    let directory = HANDLE(directory.as_raw_handle());
    let io_event = HANDLE(io_event.as_raw_handle());
    let stop = HANDLE(stop.as_raw_handle());
    let mut buffer = vec![0u64; BUFFER_SIZE / 8];
    let buffer_len = u32::try_from(BUFFER_SIZE).expect("64 KiB fits in u32");
    let filter = FILE_NOTIFY_CHANGE_FILE_NAME
        | FILE_NOTIFY_CHANGE_DIR_NAME
        | FILE_NOTIFY_CHANGE_ATTRIBUTES
        | FILE_NOTIFY_CHANGE_SIZE
        | FILE_NOTIFY_CHANGE_LAST_WRITE;
    let mut armed = Some(armed);

    loop {
        let mut overlapped = OVERLAPPED {
            hEvent: io_event,
            ..OVERLAPPED::default()
        };
        // SAFETY: `io_event` is a valid manual-reset event owned by the caller
        // for the whole thread.
        let issued = unsafe { ResetEvent(io_event) }.and_then(|()| {
            // SAFETY: `directory` was opened with FILE_FLAG_OVERLAPPED and
            // FILE_LIST_DIRECTORY; `buffer` (DWORD-aligned, `buffer_len` bytes)
            // and `overlapped` stay in place until the request completes: every
            // path below waits for completion before they go out of scope.
            unsafe {
                ReadDirectoryChangesW(
                    directory,
                    buffer.as_mut_ptr().cast(),
                    buffer_len,
                    false,
                    filter,
                    None,
                    Some(&raw mut overlapped),
                    None,
                )
            }
        });
        if let Some(armed) = armed.take() {
            let failed = issued.is_err();
            let _ = armed.send(issued);
            if failed {
                return;
            }
        } else if let Err(error) = issued {
            on_change(Completion::Failed(error.message()));
            return;
        }

        // SAFETY: both handles are valid events for the whole thread.
        let woke = unsafe { WaitForMultipleObjects(&[stop, io_event], false, INFINITE) };
        if woke == WAIT_OBJECT_0 {
            cancel_and_wait(directory, &overlapped);
            return;
        }
        if woke.0 != WAIT_OBJECT_0.0 + 1 {
            let error = io::Error::last_os_error();
            cancel_and_wait(directory, &overlapped);
            on_change(Completion::Failed(error.to_string()));
            return;
        }

        let mut bytes = 0u32;
        // SAFETY: the request has completed (its event is signalled), so the
        // call only reads the result out of `overlapped`.
        let result =
            unsafe { GetOverlappedResult(directory, &raw const overlapped, &raw mut bytes, false) };
        match classify(result.map(|()| bytes).map_err(|error| win32_code(&error))) {
            Outcome::Changed => {
                let written = usize::try_from(bytes)
                    .unwrap_or(BUFFER_SIZE)
                    .min(BUFFER_SIZE);
                // SAFETY: the request is complete, so Windows wrote `written`
                // bytes (at most the buffer's size) and no longer touches
                // the buffer; any byte of a `u64` may be read as a `u8`. The
                // slice ends before the next request borrows the buffer.
                let records =
                    unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>(), written) };
                on_change(Completion::Changed(records));
            }
            Outcome::Overflow => on_change(Completion::Overflow),
            Outcome::Stopped => return,
            Outcome::Failed(message) => {
                on_change(Completion::Failed(message));
                return;
            }
        }
    }
}

/// Reads the `FILE_NOTIFY_INFORMATION` records of one completion: each is
/// the offset of the next (0 for the last), the action, the name's length
/// in bytes, and the name in UTF-16, not NUL-terminated. A rename is two
/// records, the old name and then the new one; an old name with no new one
/// in the batch means the entry left the directory.
fn parse_notifications(records: &[u8]) -> Vec<EntryChange> {
    const ADDED: u32 = 1;
    const REMOVED: u32 = 2;
    const MODIFIED: u32 = 3;
    const RENAMED_OLD_NAME: u32 = 4;
    const RENAMED_NEW_NAME: u32 = 5;
    let word = |at: usize| {
        records
            .get(at..at + 4)
            .map(|bytes| u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    };
    let mut changes = Vec::new();
    let mut old_name: Option<String> = None;
    let mut at = 0;
    while let (Some(next), Some(action), Some(length)) = (word(at), word(at + 4), word(at + 8)) {
        let start = at + 12;
        let Some(name) = usize::try_from(length)
            .ok()
            .and_then(|length| records.get(start..start + length))
        else {
            break;
        };
        let units: Vec<u16> = name
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect();
        let name = String::from_utf16_lossy(&units);
        let change = |kind, name, old_name| EntryChange {
            kind,
            name,
            old_name,
        };
        if action != RENAMED_NEW_NAME
            && let Some(left) = old_name.take()
        {
            changes.push(change(EntryChangeKind::Removed, left, None));
        }
        match action {
            ADDED => changes.push(change(EntryChangeKind::Created, name, None)),
            REMOVED => changes.push(change(EntryChangeKind::Removed, name, None)),
            MODIFIED => changes.push(change(EntryChangeKind::Modified, name, None)),
            RENAMED_OLD_NAME => old_name = Some(name),
            RENAMED_NEW_NAME => changes.push(match old_name.take() {
                Some(old) => change(EntryChangeKind::Renamed, name, Some(old)),
                None => change(EntryChangeKind::Created, name, None),
            }),
            _ => {}
        }
        let Ok(next) = usize::try_from(next) else {
            break;
        };
        if next == 0 {
            break;
        }
        at += next;
    }
    if let Some(left) = old_name {
        changes.push(EntryChange {
            kind: EntryChangeKind::Removed,
            name: left,
            old_name: None,
        });
    }
    changes
}

/// Cancels the pending request and waits until Windows is done with its
/// buffer and `OVERLAPPED`.
fn cancel_and_wait(directory: HANDLE, overlapped: &OVERLAPPED) {
    // SAFETY: `overlapped` belongs to the request pending on `directory`.
    // Cancelling a request that already completed is harmless.
    let _ = unsafe { CancelIoEx(directory, Some(std::ptr::from_ref(overlapped))) };
    let mut bytes = 0u32;
    // SAFETY: waits (bWait = TRUE) for that request to finish, successfully or
    // cancelled; afterwards Windows no longer touches the buffer.
    let _ = unsafe {
        GetOverlappedResult(
            directory,
            std::ptr::from_ref(overlapped),
            &raw mut bytes,
            true,
        )
    };
}

/// Opens `wide` (verbatim, NUL-terminated) for overlapped change requests.
fn open_for_watching(path: &str, wide: &[u16]) -> Result<OwnedHandle, FsError> {
    // SAFETY: `wide` is a NUL-terminated UTF-16 string that outlives the call;
    // no security attributes or template handle.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            FILE_LIST_DIRECTORY.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED,
            None,
        )
    }
    .map_err(|error| FsError::from_windows(path, &error))?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else owns.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw.0) })
}

/// A new manual-reset event, not signalled.
fn new_event() -> io::Result<OwnedHandle> {
    // SAFETY: no security attributes and no name: a private event.
    let raw =
        unsafe { CreateEventW(None, true, false, PCWSTR::null()) }.map_err(io::Error::from)?;
    // SAFETY: CreateEventW succeeded and returned a new handle nothing else owns.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw.0) })
}

fn signal(event: &OwnedHandle) {
    // SAFETY: the event is valid while the Arc holding it lives.
    let _ = unsafe { SetEvent(HANDLE(event.as_raw_handle())) };
}

/// The Win32 error code inside a `windows` crate error.
fn win32_code(error: &windows::core::Error) -> u32 {
    let hresult = error.code().0.cast_unsigned();
    if hresult & 0xFFFF_0000 == 0x8007_0000 {
        hresult & 0xFFFF
    } else {
        hresult
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One `FILE_NOTIFY_INFORMATION` record; `last` leaves its next offset 0.
    fn record(action: u32, name: &str, last: bool) -> Vec<u8> {
        let name: Vec<u8> = name.encode_utf16().flat_map(u16::to_le_bytes).collect();
        let size = (12 + name.len()).next_multiple_of(4);
        let next = if last {
            0
        } else {
            u32::try_from(size).unwrap()
        };
        let mut bytes = Vec::new();
        bytes.extend(next.to_le_bytes());
        bytes.extend(action.to_le_bytes());
        bytes.extend(u32::try_from(name.len()).unwrap().to_le_bytes());
        bytes.extend(name);
        bytes.resize(size, 0);
        bytes
    }

    #[test]
    fn records_become_changes_and_renames_pair_up() {
        let records = [
            record(1, "new.txt", false),
            record(3, "new.txt", false),
            record(4, "old name.txt", false),
            record(5, "new name.txt", false),
            record(4, "gone.txt", false),
            record(2, "ünïcode.txt", true),
        ]
        .concat();
        let change = |kind, name: &str, old: Option<&str>| EntryChange {
            kind,
            name: name.to_owned(),
            old_name: old.map(str::to_owned),
        };
        assert_eq!(
            parse_notifications(&records),
            [
                change(EntryChangeKind::Created, "new.txt", None),
                change(EntryChangeKind::Modified, "new.txt", None),
                change(
                    EntryChangeKind::Renamed,
                    "new name.txt",
                    Some("old name.txt")
                ),
                change(EntryChangeKind::Removed, "gone.txt", None),
                change(EntryChangeKind::Removed, "ünïcode.txt", None),
            ]
        );
        assert_eq!(
            parse_notifications(&record(5, "arrived.txt", true)),
            [change(EntryChangeKind::Created, "arrived.txt", None)]
        );
        // A record cut short ends the reading; what came before stays.
        let mut cut = [record(1, "a", false), record(1, "bbbbbbbb", true)].concat();
        cut.truncate(cut.len() - 6);
        assert_eq!(
            parse_notifications(&cut),
            [change(EntryChangeKind::Created, "a", None)]
        );
        assert!(parse_notifications(&[]).is_empty());
    }

    #[test]
    fn classifies_completions() {
        assert_eq!(classify(Ok(120)), Outcome::Changed);
        assert_eq!(classify(Ok(0)), Outcome::Overflow);
        assert_eq!(classify(Err(ERROR_NOTIFY_ENUM_DIR)), Outcome::Overflow);
        assert_eq!(classify(Err(ERROR_OPERATION_ABORTED)), Outcome::Stopped);
        assert!(matches!(classify(Err(5)), Outcome::Failed(message) if !message.is_empty()));
    }
}
