//! Windows' own context menu of files (Phase 18, ADR 0015): the menu
//! Explorer shows, with "Open with", "Send to" and what other programs add.
//!
//! The shell builds the menu from COM objects (`IContextMenu`) and a menu
//! handle (`HMENU`), and both must stay on the thread that made them. So
//! each menu gets a thread of its own in a single-threaded apartment: it
//! builds the menu, hands back the items, and waits, pumping messages, until
//! an item is chosen or [`MENU_LIFETIME`] passes. Only then are the menu and
//! the COM objects released. The window never waits for any of it: the core
//! answers its requests from here on a blocking thread.

use std::collections::{HashMap, HashSet};
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, TryRecvError};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::Shell::Common::ITEMIDLIST;
use windows::Win32::UI::Shell::{
    CMF_NORMAL, CMINVOKECOMMANDINFO, CMINVOKECOMMANDINFOEX, GCS_VERBW, IContextMenu, IContextMenu2,
    IContextMenu3, ILFree, IShellFolder, SHBindToParent, SHParseDisplayName,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreatePopupMenu, DestroyMenu, DispatchMessageW, GetMenuItemCount, GetMenuItemInfoW, HMENU,
    MENUITEMINFOW, MFT_OWNERDRAW, MFT_SEPARATOR, MIIM_FTYPE, MIIM_ID, MIIM_STATE, MIIM_STRING,
    MIIM_SUBMENU, MSG, MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_NOREMOVE, PM_REMOVE,
    PeekMessageW, PostThreadMessageW, QS_ALLINPUT, SW_SHOWNORMAL, TranslateMessage,
    WM_INITMENUPOPUP, WM_NULL,
};
use windows::core::{Interface, PCSTR, PCWSTR, PSTR};

use crate::com::Apartment;

/// How long the shell may take to build a menu before the request fails.
pub const BUILD_TIMEOUT: Duration = Duration::from_secs(3);

/// How long a menu waits for its item to be chosen; then it is released.
pub const MENU_LIFETIME: Duration = Duration::from_secs(30);

/// How long [`ShellMenus::invoke`] waits for the item to finish before it
/// answers that it started: an item may open a dialog ("Open with") that
/// stays until the user closes it.
pub const INVOKE_WAIT: Duration = Duration::from_secs(5);

/// The first command ID the shell's handlers may use; 0 means "none".
const FIRST_ID: u32 = 1;
/// The last command ID the shell's handlers may use.
const LAST_ID: u32 = 0x7FFF;

// shobjidl_core.h; the `windows` crate does not name them.
const CMIC_MASK_NOASYNC: u32 = 0x0000_0100;
const CMIC_MASK_UNICODE: u32 = 0x0000_4000;
const CMIC_MASK_NO_CONSOLE: u32 = 0x0000_8000;

/// Menu IDs are unique across all connections.
static NEXT_MENU: AtomicU64 = AtomicU64::new(1);

/// One item of a shell menu.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShellItem {
    /// What [`ShellMenus::invoke`] takes; 0 for a separator and for an item
    /// that only opens a submenu.
    pub id: u32,
    /// The text, without its `&` mnemonic and its shortcut.
    pub text: String,
    /// A divider line.
    pub separator: bool,
    /// A submenu's items, one level deep: a submenu inside a submenu is
    /// left out.
    pub items: Vec<ShellItem>,
    /// The item's language-neutral verb, when its handler names one (such
    /// as `open` or `properties`): for the log and the tests.
    pub verb: Option<String>,
}

/// A menu the shell built, alive until an item is chosen or
/// [`MENU_LIFETIME`] passes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BuiltMenu {
    /// Its ID for [`ShellMenus::invoke`].
    pub menu_id: u64,
    /// Its items, top to bottom.
    pub items: Vec<ShellItem>,
}

/// Why a shell menu could not be built or used.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
pub enum ShellMenuError {
    /// The shell could not build the menu or run the item; the message
    /// says why.
    #[error("{0}")]
    Failed(String),
    /// No menu with this ID is open for this owner: an item was chosen
    /// already, or it expired.
    #[error("no Windows menu {0} is open; it was used, or it waited longer than 30 s")]
    NoSuchMenu(u64),
}

/// A request to the menu's thread: the item, and where the result goes.
type Invoke = (u32, Sender<Result<(), String>>);

/// A menu's thread, as the registry keeps it.
struct MenuThread {
    owner: u64,
    thread_id: u32,
    invoke: Sender<Invoke>,
}

/// The open shell menus of this process, each on its own thread.
#[derive(Clone, Default)]
pub struct ShellMenus {
    open: Arc<Mutex<HashMap<u64, MenuThread>>>,
}

impl ShellMenus {
    /// No menus open.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Builds Windows' menu of `paths`, which must be absolute and in one
    /// folder, for `owner` (a connection): its earlier menus close first,
    /// since a window shows one at a time. Blocking, for at most
    /// [`BUILD_TIMEOUT`].
    pub fn open(&self, owner: u64, paths: &[String]) -> Result<BuiltMenu, ShellMenuError> {
        self.open_for(owner, paths, BUILD_TIMEOUT, MENU_LIFETIME)
    }

    /// [`Self::open`] with its own limits, for the tests.
    pub fn open_for(
        &self,
        owner: u64,
        paths: &[String],
        build_timeout: Duration,
        lifetime: Duration,
    ) -> Result<BuiltMenu, ShellMenuError> {
        check_paths(paths).map_err(ShellMenuError::Failed)?;
        self.close_owner(owner);
        let menu_id = NEXT_MENU.fetch_add(1, Ordering::Relaxed);
        let (ready_tx, ready_rx) = mpsc::channel();
        let (invoke_tx, invoke_rx) = mpsc::channel::<Invoke>();
        let registry = Arc::clone(&self.open);
        let paths = paths.to_vec();
        std::thread::Builder::new()
            .name(format!("shell-menu-{menu_id}"))
            .spawn(move || {
                menu_thread(&paths, lifetime, &ready_tx, &invoke_rx);
                lock(&registry).remove(&menu_id);
            })
            .map_err(|error| ShellMenuError::Failed(format!("cannot start a thread: {error}")))?;
        match ready_rx.recv_timeout(build_timeout) {
            Ok(Ok((items, thread_id))) => {
                lock(&self.open).insert(
                    menu_id,
                    MenuThread {
                        owner,
                        thread_id,
                        invoke: invoke_tx,
                    },
                );
                Ok(BuiltMenu { menu_id, items })
            }
            Ok(Err(message)) => Err(ShellMenuError::Failed(message)),
            Err(RecvTimeoutError::Timeout) => Err(ShellMenuError::Failed(format!(
                "Windows took longer than {} s to build the menu",
                build_timeout.as_secs_f32()
            ))),
            Err(RecvTimeoutError::Disconnected) => Err(ShellMenuError::Failed(
                "the menu's thread ended before the menu was built".to_owned(),
            )),
        }
    }

    /// Runs item `item_id` of `owner`'s menu `menu_id` on the menu's
    /// thread; the menu is released afterwards. Answers once the item is
    /// done, or after [`INVOKE_WAIT`] while it still runs (a dialog it
    /// opened stays open).
    pub fn invoke(&self, owner: u64, menu_id: u64, item_id: u32) -> Result<(), ShellMenuError> {
        let (invoke, thread_id) = {
            let mut open = lock(&self.open);
            match open.get(&menu_id) {
                Some(menu) if menu.owner == owner => {}
                _ => return Err(ShellMenuError::NoSuchMenu(menu_id)),
            }
            let menu = open.remove(&menu_id).expect("checked above");
            (menu.invoke, menu.thread_id)
        };
        let (reply_tx, reply_rx) = mpsc::channel();
        if invoke.send((item_id, reply_tx)).is_err() {
            return Err(ShellMenuError::NoSuchMenu(menu_id));
        }
        wake(thread_id);
        match reply_rx.recv_timeout(INVOKE_WAIT) {
            Ok(Ok(())) | Err(RecvTimeoutError::Timeout) => Ok(()),
            Ok(Err(message)) => Err(ShellMenuError::Failed(message)),
            // The thread ended with the request unread: the menu expired.
            Err(RecvTimeoutError::Disconnected) => Err(ShellMenuError::NoSuchMenu(menu_id)),
        }
    }

    /// Releases every menu of `owner`: a new one replaces them, or the
    /// connection ended.
    pub fn close_owner(&self, owner: u64) {
        let closed: Vec<MenuThread> = {
            let mut open = lock(&self.open);
            let ids: Vec<u64> = open
                .iter()
                .filter(|(_, menu)| menu.owner == owner)
                .map(|(id, _)| *id)
                .collect();
            ids.iter().filter_map(|id| open.remove(id)).collect()
        };
        for menu in closed {
            let thread_id = menu.thread_id;
            // The thread sees the request channel closed once it wakes.
            drop(menu);
            wake(thread_id);
        }
    }

    /// How many menus are open.
    #[must_use]
    pub fn open_count(&self) -> usize {
        lock(&self.open).len()
    }
}

fn lock(
    open: &Mutex<HashMap<u64, MenuThread>>,
) -> std::sync::MutexGuard<'_, HashMap<u64, MenuThread>> {
    open.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Wakes a menu's thread from its wait for messages.
fn wake(thread_id: u32) {
    // SAFETY: a plain call; a thread that ended makes it fail, which is
    // fine: it has nothing left to wake.
    let _ = unsafe { PostThreadMessageW(thread_id, WM_NULL, WPARAM(0), LPARAM(0)) };
}

/// The shell takes the files of one folder: every path absolute, all with
/// the same parent.
fn check_paths(paths: &[String]) -> Result<(), String> {
    let Some(first) = paths.first() else {
        return Err("no file or folder to show a menu for".to_owned());
    };
    for path in paths {
        if !Path::new(path).is_absolute() {
            return Err(format!("{path}: not an absolute path"));
        }
    }
    let parent = |path: &str| {
        Path::new(path)
            .parent()
            .map(|parent| parent.to_string_lossy().to_lowercase())
    };
    let folder = parent(first);
    if paths.len() > 1 && (folder.is_none() || paths.iter().any(|path| parent(path) != folder)) {
        return Err(
            "the paths are not in one folder; Windows' menu takes the files of one folder"
                .to_owned(),
        );
    }
    Ok(())
}

/// What the menu's thread hands back once the menu is built: its items and
/// the thread's ID, or why it failed.
type Ready = Result<(Vec<ShellItem>, u32), String>;

/// The life of one menu: build it, hand back its items, wait for an item or
/// the end of `lifetime`, run the item, release everything.
fn menu_thread(
    paths: &[String],
    lifetime: Duration,
    ready: &Sender<Ready>,
    invoke: &Receiver<Invoke>,
) {
    let _apartment = Apartment::enter();
    // A message queue before the thread's ID is handed out, so a wake-up
    // posted to it is never lost.
    let mut message = MSG::default();
    // SAFETY: `message` is a valid output; PM_NOREMOVE leaves the queue as
    // it is.
    let _ = unsafe { PeekMessageW(&raw mut message, None, 0, 0, PM_NOREMOVE) };
    let menu = match Menu::build(paths) {
        Ok(menu) => menu,
        Err(error) => {
            let _ = ready.send(Err(error));
            return;
        }
    };
    // SAFETY: a plain call.
    let thread_id = unsafe { GetCurrentThreadId() };
    if ready.send(Ok((menu.items.clone(), thread_id))).is_err() {
        // The request gave up waiting: nobody will choose an item.
        return;
    }
    let deadline = Instant::now() + lifetime;
    loop {
        pump_messages();
        match invoke.try_recv() {
            Ok((item, reply)) => {
                let _ = reply.send(menu.invoke(item));
                return;
            }
            Err(TryRecvError::Disconnected) => return,
            Err(TryRecvError::Empty) => {}
        }
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return;
        }
        let millis = u32::try_from(left.as_millis()).unwrap_or(u32::MAX);
        // SAFETY: no handles to wait on; it returns on a message or when
        // the time is up.
        unsafe { MsgWaitForMultipleObjectsEx(None, millis, QS_ALLINPUT, MWMO_INPUTAVAILABLE) };
    }
}

/// Hands this thread's messages to their windows: a shell handler may have
/// made a hidden window here and posted work to it.
fn pump_messages() {
    let mut message = MSG::default();
    // SAFETY: `message` is a valid output for each call, and each message
    // is dispatched as Windows gave it.
    while unsafe { PeekMessageW(&raw mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
        // SAFETY: as above.
        unsafe {
            let _ = TranslateMessage(&raw const message);
            DispatchMessageW(&raw const message);
        }
    }
}

/// A built menu: the COM object, the menu handle, and the IDs of the items
/// that can be chosen. Released on this thread when dropped.
struct Menu {
    context: IContextMenu,
    handle: HMENU,
    items: Vec<ShellItem>,
    ids: HashSet<u32>,
    folder: Vec<u16>,
}

impl Drop for Menu {
    fn drop(&mut self) {
        // SAFETY: the handle came from CreatePopupMenu on this thread and
        // is destroyed once; the COM object is released after it.
        let _ = unsafe { DestroyMenu(self.handle) };
    }
}

/// Item ID lists the shell allocated, freed when dropped.
struct Pidls(Vec<*mut ITEMIDLIST>);

impl Drop for Pidls {
    fn drop(&mut self) {
        for &pidl in &self.0 {
            // SAFETY: each came from SHParseDisplayName and is freed once;
            // nothing uses it after.
            unsafe { ILFree(Some(pidl.cast_const())) };
        }
    }
}

impl Menu {
    /// Asks the shell for the menu of `paths` (checked by `check_paths`)
    /// and reads its items. On the menu's thread, in its apartment.
    fn build(paths: &[String]) -> Result<Self, String> {
        let mut pidls = Pidls(Vec::with_capacity(paths.len()));
        for path in paths {
            let plain = plain_wide(path);
            let mut pidl = std::ptr::null_mut();
            // SAFETY: `plain` is NUL-terminated and outlives the call;
            // `pidl` is a valid output, freed by `pidls`.
            unsafe { SHParseDisplayName(PCWSTR(plain.as_ptr()), None, &raw mut pidl, 0, None) }
                .map_err(|error| format!("{path}: {}", error.message()))?;
            pidls.0.push(pidl);
        }
        let mut children: Vec<*const ITEMIDLIST> = Vec::with_capacity(pidls.0.len());
        let mut folder: Option<IShellFolder> = None;
        for &pidl in &pidls.0 {
            let mut last = std::ptr::null_mut();
            // SAFETY: `pidl` is an absolute ID list alive until `pidls`
            // drops; `last` points into it and is used only before then.
            let parent: IShellFolder =
                unsafe { SHBindToParent(pidl.cast_const(), Some(&raw mut last)) }
                    .map_err(|error| format!("cannot reach the folder: {}", error.message()))?;
            children.push(last.cast_const());
            // Every path has the same parent (`check_paths`): one folder
            // object serves them all.
            folder.get_or_insert(parent);
        }
        let folder = folder.ok_or_else(|| "no file or folder to show a menu for".to_owned())?;
        // SAFETY: every child is a relative ID list of an item of `folder`,
        // alive until `pidls` drops, after this call.
        let context: IContextMenu =
            unsafe { folder.GetUIObjectOf(HWND::default(), &children, None) }
                .map_err(|error| format!("Windows has no menu for it: {}", error.message()))?;
        // SAFETY: a plain call; the handle is destroyed by `Menu`'s drop.
        let handle = unsafe { CreatePopupMenu() }
            .map_err(|error| format!("cannot make a menu: {}", error.message()))?;
        let mut menu = Self {
            context,
            handle,
            items: Vec::new(),
            ids: HashSet::new(),
            folder: Path::new(&paths[0])
                .parent()
                .map(|parent| parent.as_os_str().encode_wide().chain([0]).collect())
                .unwrap_or_default(),
        };
        // SAFETY: the handle is a valid, empty popup menu of this thread.
        unsafe {
            menu.context
                .QueryContextMenu(menu.handle, 0, FIRST_ID, LAST_ID, CMF_NORMAL)
        }
        .ok()
        .map_err(|error| format!("Windows could not fill the menu: {}", error.message()))?;
        menu.items = menu.read(menu.handle, 1);
        Ok(menu)
    }

    /// The items of `handle`, `level` 1 for the menu itself and 2 for a
    /// submenu, whose own submenus are left out.
    fn read(&mut self, handle: HMENU, level: u8) -> Vec<ShellItem> {
        // SAFETY: a plain call on a menu of this thread.
        let count = unsafe { GetMenuItemCount(Some(handle)) };
        let mut items = Vec::new();
        for position in 0..u32::try_from(count).unwrap_or(0) {
            let Some(item) = self.item(handle, position, level) else {
                continue;
            };
            items.push(item);
        }
        tidy_separators(items)
    }

    /// The item at `position` of `handle`; `None` for an item the window
    /// cannot show: disabled, drawn by its handler, without text, or a
    /// submenu too deep or left empty.
    fn item(&mut self, handle: HMENU, position: u32, level: u8) -> Option<ShellItem> {
        let mut info = MENUITEMINFOW {
            cbSize: u32::try_from(size_of::<MENUITEMINFOW>()).unwrap_or(u32::MAX),
            fMask: MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING,
            ..MENUITEMINFOW::default()
        };
        // SAFETY: `info` is valid with its size set; without a buffer the
        // call only writes the text's length into `cch`.
        unsafe { GetMenuItemInfoW(handle, position, true, &raw mut info) }.ok()?;
        if info.fType.0 & MFT_SEPARATOR.0 != 0 {
            return Some(ShellItem {
                id: 0,
                text: String::new(),
                separator: true,
                items: Vec::new(),
                verb: None,
            });
        }
        // MFS_DISABLED and MFS_GRAYED share their bits.
        if info.fState.0 & 3 != 0 || info.fType.0 & MFT_OWNERDRAW.0 != 0 || info.cch == 0 {
            return None;
        }
        let mut text = vec![0u16; info.cch as usize + 1];
        info.dwTypeData = windows::core::PWSTR(text.as_mut_ptr());
        info.cch = u32::try_from(text.len()).unwrap_or(u32::MAX);
        // SAFETY: `dwTypeData` points at `text`, whose length is in `cch`;
        // Windows writes at most that many units, NUL included.
        unsafe { GetMenuItemInfoW(handle, position, true, &raw mut info) }.ok()?;
        let length = text
            .iter()
            .position(|&unit| unit == 0)
            .unwrap_or(text.len());
        let text = shown_text(&String::from_utf16_lossy(&text[..length]));
        if text.is_empty() {
            return None;
        }
        if !info.hSubMenu.is_invalid() {
            if level > 1 {
                return None;
            }
            self.init_submenu(info.hSubMenu, position);
            let items = self.read(info.hSubMenu, level + 1);
            return (!items.is_empty()).then_some(ShellItem {
                id: 0,
                text,
                separator: false,
                items,
                verb: None,
            });
        }
        if !(FIRST_ID..=LAST_ID).contains(&info.wID) {
            return None;
        }
        self.ids.insert(info.wID);
        let verb = (level == 1).then(|| self.verb(info.wID)).flatten();
        Some(ShellItem {
            id: info.wID,
            text,
            separator: false,
            items: Vec::new(),
            verb,
        })
    }

    /// Lets the handlers fill a submenu they fill only when it opens, as
    /// "Send to" and "Open with" are: the message Explorer passes on when
    /// a submenu is about to show.
    fn init_submenu(&self, submenu: HMENU, position: u32) {
        let wparam = WPARAM(submenu.0 as usize);
        let lparam = LPARAM(isize::try_from(position).unwrap_or(0));
        if let Ok(menu3) = self.context.cast::<IContextMenu3>() {
            // SAFETY: the submenu belongs to this menu, on this thread; no
            // result is asked for.
            let _ = unsafe { menu3.HandleMenuMsg2(WM_INITMENUPOPUP, wparam, lparam, None) };
        } else if let Ok(menu2) = self.context.cast::<IContextMenu2>() {
            // SAFETY: as above.
            let _ = unsafe { menu2.HandleMenuMsg(WM_INITMENUPOPUP, wparam, lparam) };
        }
    }

    /// The language-neutral verb of item `id`, when its handler names one.
    fn verb(&self, id: u32) -> Option<String> {
        let mut buffer = [0u16; 128];
        // SAFETY: GCS_VERBW writes a NUL-terminated UTF-16 string of at
        // most `cchmax` units into the buffer, which the PSTR only
        // carries.
        unsafe {
            self.context.GetCommandString(
                (id - FIRST_ID) as usize,
                GCS_VERBW,
                None,
                PSTR(buffer.as_mut_ptr().cast()),
                u32::try_from(buffer.len() - 1).unwrap_or(0),
            )
        }
        .ok()?;
        let length = buffer.iter().position(|&unit| unit == 0)?;
        (length > 0).then(|| String::from_utf16_lossy(&buffer[..length]))
    }

    /// Runs item `id`, as Explorer does when it is clicked: by its offset
    /// from the first ID, waiting until the handler is done.
    fn invoke(&self, id: u32) -> Result<(), String> {
        if !self.ids.contains(&id) {
            return Err(format!("the menu has no item {id}"));
        }
        let offset = (id - FIRST_ID) as usize;
        let info = CMINVOKECOMMANDINFOEX {
            cbSize: u32::try_from(size_of::<CMINVOKECOMMANDINFOEX>()).unwrap_or(u32::MAX),
            fMask: CMIC_MASK_UNICODE | CMIC_MASK_NOASYNC | CMIC_MASK_NO_CONSOLE,
            // The offset in place of a verb's text, as MAKEINTRESOURCE
            // makes it.
            lpVerb: PCSTR(offset as *const u8),
            lpVerbW: PCWSTR(offset as *const u16),
            lpDirectoryW: if self.folder.is_empty() {
                PCWSTR::null()
            } else {
                PCWSTR(self.folder.as_ptr())
            },
            nShow: SW_SHOWNORMAL.0,
            ..CMINVOKECOMMANDINFOEX::default()
        };
        // SAFETY: `info` is a valid CMINVOKECOMMANDINFOEX with its size
        // set, which the shell reads through the base type's pointer; its
        // strings outlive the call, which returns once the handler is done
        // (CMIC_MASK_NOASYNC).
        unsafe {
            self.context
                .InvokeCommand((&raw const info).cast::<CMINVOKECOMMANDINFO>())
        }
        .map_err(|error| format!("Windows could not run the item: {}", error.message()))
    }
}

/// `path` in its plain form, NUL-terminated: the shell parses plain paths,
/// not the `\\?\` form.
fn plain_wide(path: &str) -> Vec<u16> {
    path.strip_prefix(r"\\?\")
        .unwrap_or(path)
        .encode_utf16()
        .chain([0])
        .collect()
}

/// A menu text as the window shows it: the `&` of the mnemonic removed
/// (`&&` is one `&`), and the shortcut after a tab left out.
fn shown_text(text: &str) -> String {
    let text = text.split('\t').next().unwrap_or("");
    let mut shown = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '&' {
            if chars.peek() == Some(&'&') {
                shown.push('&');
                chars.next();
            }
            continue;
        }
        shown.push(c);
    }
    shown.trim().to_owned()
}

/// No separator first, last, or next to another.
fn tidy_separators(items: Vec<ShellItem>) -> Vec<ShellItem> {
    let mut tidy: Vec<ShellItem> = Vec::with_capacity(items.len());
    for item in items {
        if item.separator && tidy.last().is_none_or(|last| last.separator) {
            continue;
        }
        tidy.push(item);
    }
    if tidy.last().is_some_and(|last| last.separator) {
        tidy.pop();
    }
    tidy
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(prefix: &str) -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix(prefix)
            .tempdir_in(&root)
            .unwrap()
    }

    fn text(path: &Path) -> String {
        path.to_str().unwrap().to_owned()
    }

    fn all_ids(items: &[ShellItem]) -> Vec<u32> {
        items
            .iter()
            .flat_map(|item| std::iter::once(item.id).chain(all_ids(&item.items)))
            .filter(|&id| id != 0)
            .collect()
    }

    #[test]
    fn menu_texts_lose_their_mnemonic_and_shortcut() {
        assert_eq!(shown_text("&Open"), "Open");
        assert_eq!(shown_text("Cu&t\tCtrl+X"), "Cut");
        assert_eq!(shown_text("Save && close"), "Save & close");
        assert_eq!(shown_text("  "), "");
    }

    #[test]
    fn separators_are_tidied() {
        let item = |text: &str| ShellItem {
            id: 1,
            text: text.to_owned(),
            separator: false,
            items: Vec::new(),
            verb: None,
        };
        let line = ShellItem {
            id: 0,
            text: String::new(),
            separator: true,
            items: Vec::new(),
            verb: None,
        };
        let tidy = tidy_separators(vec![
            line.clone(),
            item("a"),
            line.clone(),
            line.clone(),
            item("b"),
            line.clone(),
        ]);
        let shape: Vec<bool> = tidy.iter().map(|item| item.separator).collect();
        assert_eq!(shape, [false, true, false]);
    }

    #[test]
    fn the_paths_must_be_absolute_and_in_one_folder() {
        assert!(check_paths(&[]).is_err());
        assert!(check_paths(&["relative.txt".to_owned()]).is_err());
        assert!(check_paths(&[r"C:\a\x.txt".to_owned(), r"C:\A\y.txt".to_owned()]).is_ok());
        let error = check_paths(&[r"C:\a\x.txt".to_owned(), r"C:\b\y.txt".to_owned()]).unwrap_err();
        assert!(error.contains("one folder"), "{error}");
        // A drive alone has no folder around it, and takes no company.
        assert!(check_paths(&[r"C:\".to_owned()]).is_ok());
        assert!(check_paths(&[r"C:\".to_owned(), r"D:\".to_owned()]).is_err());
    }

    /// The menu of real files: items with IDs, the Properties verb among
    /// them; an ID the menu does not have is refused, and a menu is gone
    /// once used or expired. The one item run is Properties, whose sheet
    /// the test closes, as open.rs's test does: every other item would
    /// change something on the desktop.
    #[test]
    fn a_menu_is_built_used_once_and_released() {
        let dir = scratch("shell-menu");
        let a = dir.path().join("a.txt");
        let b = dir.path().join("Звіт b.txt");
        std::fs::write(&a, "a").unwrap();
        std::fs::write(&b, "b").unwrap();
        let menus = ShellMenus::new();

        let error = menus
            .open(1, &[text(&dir.path().join("gone.txt"))])
            .unwrap_err();
        assert!(matches!(error, ShellMenuError::Failed(_)), "{error:?}");

        let menu = menus.open(1, &[text(&a), text(&b)]).unwrap();
        assert!(menu.items.len() > 3, "{:?}", menu.items);
        assert!(!menu.items[0].separator);
        assert!(all_ids(&menu.items).iter().all(|&id| id >= FIRST_ID));
        assert_eq!(menus.open_count(), 1);
        // Another owner cannot use it, and a wrong item is refused.
        assert_eq!(
            menus.invoke(2, menu.menu_id, 1),
            Err(ShellMenuError::NoSuchMenu(menu.menu_id))
        );
        let error = menus.invoke(1, menu.menu_id, LAST_ID).unwrap_err();
        assert!(
            matches!(&error, ShellMenuError::Failed(message) if message.contains("no item")),
            "{error:?}"
        );
        // Used: gone.
        assert_eq!(
            menus.invoke(1, menu.menu_id, 1),
            Err(ShellMenuError::NoSuchMenu(menu.menu_id))
        );

        // A second menu of the same owner replaces the first.
        let first = menus.open(1, &[text(&a)]).unwrap();
        let second = menus.open(1, &[text(&a)]).unwrap();
        assert_eq!(
            menus.invoke(1, first.menu_id, 1),
            Err(ShellMenuError::NoSuchMenu(first.menu_id))
        );
        let properties = second
            .items
            .iter()
            .find(|item| item.verb.as_deref() == Some("properties"))
            .unwrap_or_else(|| panic!("no properties verb: {:?}", second.items));
        let _sheets = crate::open::tests::SHEET_TESTS
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let before = crate::open::tests::sheets();
        menus.invoke(1, second.menu_id, properties.id).unwrap();
        let sheet = crate::open::tests::within_ten_seconds("no sheet came up", || {
            crate::open::tests::sheets()
                .into_iter()
                .find(|sheet| !before.contains(sheet))
        });
        crate::open::tests::close_sheet(sheet);

        // Unused, it expires.
        let short = menus
            .open_for(3, &[text(&a)], BUILD_TIMEOUT, Duration::from_millis(300))
            .unwrap();
        std::thread::sleep(Duration::from_millis(1500));
        assert_eq!(
            menus.invoke(3, short.menu_id, 1),
            Err(ShellMenuError::NoSuchMenu(short.menu_id))
        );
        menus.close_owner(1);
        let deadline = Instant::now() + Duration::from_secs(5);
        while menus.open_count() > 0 {
            assert!(Instant::now() < deadline, "menus left open");
            std::thread::sleep(Duration::from_millis(50));
        }
    }
}
