//! Drive letters that come and go: a USB stick, a card put into a reader,
//! a network share mapped to a letter. Windows announces them with
//! `WM_DEVICECHANGE` broadcasts, which reach top-level windows only (a
//! message-only window gets none), so the watcher owns a top-level window
//! that is never shown, on a thread of its own that runs its message loop.

use std::cell::RefCell;
use std::sync::mpsc;
use std::thread::JoinHandle;

use windows::Win32::Foundation::{ERROR_CLASS_ALREADY_EXISTS, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DBT_DEVICEARRIVAL, DBT_DEVICEREMOVECOMPLETE, DBT_DEVTYP_VOLUME,
    DEV_BROADCAST_HDR, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, MSG,
    PostMessageW, PostQuitMessage, RegisterClassW, TranslateMessage, WINDOW_EX_STYLE, WINDOW_STYLE,
    WM_CLOSE, WM_DESTROY, WM_DEVICECHANGE, WNDCLASSW,
};
use windows::core::{PCWSTR, w};

use crate::FsError;

/// The window class of every watcher's window.
const CLASS_NAME: PCWSTR = w!("CabinetOS.DriveWatcher");

thread_local! {
    /// What the window on this thread calls when a drive letter changes.
    static ON_CHANGE: RefCell<Option<Box<dyn Fn()>>> = const { RefCell::new(None) };
}

/// Calls back when a drive letter appears or goes away, until dropped.
#[derive(Debug)]
pub struct DriveWatcher {
    /// The window's handle, as an address: a handle is not `Send`, and
    /// the window is only ever posted to from here.
    window: usize,
    thread: Option<JoinHandle<()>>,
}

impl DriveWatcher {
    /// Starts watching. `on_change` runs on the watcher's thread for every
    /// announcement: a volume arrived or was removed, media went into or
    /// out of a drive, or a network share was mapped or unmapped. One
    /// change may be announced more than once; `on_change` should be quick
    /// and leave the rest to another thread.
    pub fn start(on_change: impl Fn() + Send + 'static) -> Result<Self, FsError> {
        let (ready_tx, ready_rx) = mpsc::channel();
        let thread = std::thread::Builder::new()
            .name("drive-watch".to_owned())
            .spawn(move || run(Box::new(on_change), &ready_tx))
            .map_err(failure)?;
        match ready_rx.recv() {
            Ok(Ok(window)) => Ok(Self {
                window,
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                let _ = thread.join();
                Err(error)
            }
            Err(_) => {
                let _ = thread.join();
                Err(failure(std::io::Error::other(
                    "the drive watcher's thread ended before its window existed",
                )))
            }
        }
    }

    #[cfg(test)]
    fn window(&self) -> HWND {
        HWND(std::ptr::with_exposed_provenance_mut(self.window))
    }
}

impl Drop for DriveWatcher {
    fn drop(&mut self) {
        let window = HWND(std::ptr::with_exposed_provenance_mut(self.window));
        // SAFETY: posting needs no valid window: a handle that is gone only
        // makes the call fail. WM_CLOSE destroys the window, which ends the
        // thread's message loop.
        if unsafe { PostMessageW(Some(window), WM_CLOSE, WPARAM(0), LPARAM(0)) }.is_ok()
            && let Some(thread) = self.thread.take()
        {
            let _ = thread.join();
        }
    }
}

/// The watcher's thread: the window, then its message loop until the window
/// is destroyed.
fn run(on_change: Box<dyn Fn() + Send>, ready: &mpsc::Sender<Result<usize, FsError>>) {
    ON_CHANGE.with(|slot| *slot.borrow_mut() = Some(on_change));
    match create_window() {
        Ok(window) => {
            let _ = ready.send(Ok(window.0.expose_provenance()));
        }
        Err(error) => {
            let _ = ready.send(Err(error));
            return;
        }
    }
    let mut message = MSG::default();
    // SAFETY: `message` is a valid local for every call. GetMessageW
    // returns 0 for WM_QUIT (posted when the window is destroyed) and -1
    // on an error; both end the loop.
    while unsafe { GetMessageW(&raw mut message, None, 0, 0) }.0 > 0 {
        // SAFETY: `message` was filled by GetMessageW just before.
        unsafe {
            let _ = TranslateMessage(&raw const message);
            DispatchMessageW(&raw const message);
        }
    }
    ON_CHANGE.with(|slot| slot.borrow_mut().take());
}

/// A top-level window that is never shown: broadcasts reach it, and the
/// user never sees it (no taskbar button, no Alt+Tab entry).
fn create_window() -> Result<HWND, FsError> {
    // SAFETY: a plain call; `None` names this process's executable.
    let instance = unsafe { GetModuleHandleW(None) }.map_err(|error| windows_failure(&error))?;
    let class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance.into(),
        lpszClassName: CLASS_NAME,
        ..WNDCLASSW::default()
    };
    // SAFETY: `class` is a valid structure; its strings are static. A second
    // watcher finds the class registered already, which is fine: it is the
    // same class.
    if unsafe { RegisterClassW(&raw const class) } == 0 {
        let error = windows::core::Error::from_thread();
        if error.code() != ERROR_CLASS_ALREADY_EXISTS.to_hresult() {
            return Err(windows_failure(&error));
        }
    }
    // SAFETY: the class is registered and its name and the title are
    // static; no parent, menu or creation data. Without WS_VISIBLE the
    // window is never shown.
    unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(0),
            CLASS_NAME,
            w!("CabinetOS drive watcher"),
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            None,
            None,
            Some(instance.into()),
            None,
        )
    }
    .map_err(|error| windows_failure(&error))
}

/// The window's messages, on the watcher's thread.
unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_DEVICECHANGE => {
            if is_volume_change(wparam, lparam) {
                ON_CHANGE.with(|slot| {
                    if let Some(on_change) = slot.borrow().as_ref() {
                        on_change();
                    }
                });
            }
            LRESULT(1)
        }
        WM_CLOSE => {
            // SAFETY: the window belongs to this thread.
            let _ = unsafe { DestroyWindow(window) };
            LRESULT(0)
        }
        WM_DESTROY => {
            // SAFETY: a plain call on the window's own thread.
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        // SAFETY: the arguments are the ones Windows passed in.
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

/// Whether `WM_DEVICECHANGE` with these arguments is about a volume: one
/// that arrived, or one that was removed.
fn is_volume_change(wparam: WPARAM, lparam: LPARAM) -> bool {
    let event = u32::try_from(wparam.0).unwrap_or(0);
    if (event != DBT_DEVICEARRIVAL && event != DBT_DEVICEREMOVECOMPLETE) || lparam.0 == 0 {
        return false;
    }
    // SAFETY: for these two events `lparam` points at a structure that
    // starts with a DEV_BROADCAST_HDR and is valid while the message is
    // handled.
    let header = unsafe {
        &*std::ptr::with_exposed_provenance::<DEV_BROADCAST_HDR>(lparam.0.cast_unsigned())
    };
    header.dbch_devicetype == DBT_DEVTYP_VOLUME
}

fn failure(error: std::io::Error) -> FsError {
    FsError::Io {
        path: "drive letters".to_owned(),
        source: error,
    }
}

fn windows_failure(error: &windows::core::Error) -> FsError {
    failure(std::io::Error::other(error.message()))
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use windows::Win32::UI::WindowsAndMessaging::{
        DBT_DEVTYP_PORT, DEV_BROADCAST_VOLUME, SendMessageW,
    };

    use super::*;

    /// Sends `WM_DEVICECHANGE` to the watcher's window, as Windows would.
    fn announce<T>(watcher: &DriveWatcher, event: u32, data: &T) {
        // SAFETY: the window lives until the watcher drops, and SendMessageW
        // returns only once the window has handled the message, so `data`
        // outlives its use.
        unsafe {
            SendMessageW(
                watcher.window(),
                WM_DEVICECHANGE,
                Some(WPARAM(event as usize)),
                Some(LPARAM(
                    std::ptr::from_ref(data).expose_provenance().cast_signed(),
                )),
            );
        }
    }

    fn volume(letter: char) -> DEV_BROADCAST_VOLUME {
        DEV_BROADCAST_VOLUME {
            dbcv_size: u32::try_from(size_of::<DEV_BROADCAST_VOLUME>()).unwrap(),
            dbcv_devicetype: DBT_DEVTYP_VOLUME.0,
            dbcv_unitmask: 1 << (u32::from(letter) - u32::from('A')),
            ..DEV_BROADCAST_VOLUME::default()
        }
    }

    #[test]
    fn volumes_coming_and_going_call_back() {
        let (changed_tx, changed_rx) = mpsc::channel();
        let watcher = DriveWatcher::start(move || {
            let _ = changed_tx.send(());
        })
        .unwrap();
        let wait = Duration::from_secs(2);

        announce(&watcher, DBT_DEVICEARRIVAL, &volume('Z'));
        changed_rx.recv_timeout(wait).unwrap();
        announce(&watcher, DBT_DEVICEREMOVECOMPLETE, &volume('Z'));
        changed_rx.recv_timeout(wait).unwrap();
    }

    /// A serial port is not a drive, and a query is not a change. The
    /// filter is asked directly: the watcher's window also hears Windows'
    /// real announcements of any letter that comes or goes meanwhile, such
    /// as the `subst` letter of `volume`'s test, about half a second after
    /// it.
    #[test]
    fn only_a_volume_that_arrived_or_left_is_a_change() {
        let port = DEV_BROADCAST_HDR {
            dbch_size: u32::try_from(size_of::<DEV_BROADCAST_HDR>()).unwrap(),
            dbch_devicetype: DBT_DEVTYP_PORT,
            dbch_reserved: 0,
        };
        let z = volume('Z');
        let data = |data: *const ()| LPARAM(data.expose_provenance().cast_signed());
        let event = |event: u32| WPARAM(event as usize);
        let volume = data(std::ptr::from_ref(&z).cast());
        assert!(is_volume_change(event(DBT_DEVICEARRIVAL), volume));
        assert!(is_volume_change(event(DBT_DEVICEREMOVECOMPLETE), volume));
        assert!(!is_volume_change(
            event(DBT_DEVICEARRIVAL),
            data(std::ptr::from_ref(&port).cast())
        ));
        assert!(!is_volume_change(event(0x8001), volume));
        assert!(!is_volume_change(event(DBT_DEVICEARRIVAL), LPARAM(0)));
    }

    #[test]
    fn two_watchers_run_at_once_and_stop_when_dropped() {
        let first = DriveWatcher::start(|| {}).unwrap();
        let second = DriveWatcher::start(|| {}).unwrap();
        drop(first);
        drop(second);
    }
}
