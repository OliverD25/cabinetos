//! What each window shows (`window_state`), kept so that a program without
//! a window of its own (the command line, a plugin) can ask what the user
//! looks at (`get_window_state`).
//!
//! The window owns the state (Phase 12) and may send it at every change; the
//! core only stores the last one per connection and forgets it when the
//! connection ends.

use std::collections::HashMap;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use cabinetos_protocol::{ErrorCode, Pane, PaneState, Response, WindowState};

/// The last state each client sent.
#[derive(Default)]
pub(crate) struct WindowStates {
    states: Mutex<HashMap<String, Stored>>,
    /// Orders the states by arrival, so "the newest" does not depend on the
    /// clock's resolution.
    sequence: AtomicU64,
}

struct Stored {
    state: WindowState,
    sent_at_ms: u64,
    sequence: u64,
}

impl WindowStates {
    /// Keeps `state` as the last state of `client`.
    pub(crate) fn store(&self, client: &str, state: WindowState) {
        let stored = Stored {
            state,
            sent_at_ms: now_ms(),
            sequence: self.sequence.fetch_add(1, Ordering::Relaxed),
        };
        self.lock().insert(client.to_owned(), stored);
    }

    /// Forgets `client`'s state: its connection ended.
    pub(crate) fn remove(&self, client: &str) {
        self.lock().remove(client);
    }

    /// The newest state, or `client`'s, as the reply to `get_window_state`.
    pub(crate) fn get(&self, client: Option<&str>) -> Response {
        let states = self.lock();
        let found = match client {
            Some(client) => states.get_key_value(client),
            None => states.iter().max_by_key(|(_, stored)| stored.sequence),
        };
        match found {
            Some((client, stored)) => Response::WindowState {
                client: client.clone(),
                sent_at_ms: stored.sent_at_ms,
                state: stored.state.clone(),
            },
            None => Response::Error {
                code: ErrorCode::NoWindow,
                message: match client {
                    Some(client) => {
                        format!("client `{client}` has not said what its window shows")
                    }
                    None => "no window has said what it shows".to_owned(),
                },
            },
        }
    }

    /// The folder `pane` shows in the newest state: its tab in front, when
    /// that tab shows a folder (not a tool) by an absolute path. What a
    /// linked terminal session follows (`terminal_pane_folder`). From the
    /// state in memory only: no file is read.
    pub(crate) fn pane_folder(&self, pane: Pane) -> Option<String> {
        let states = self.lock();
        let newest = states.values().max_by_key(|stored| stored.sequence)?;
        let shown = match pane {
            Pane::Left => &newest.state.panes.left,
            Pane::Right => &newest.state.panes.right,
        };
        front_folder(shown).map(str::to_owned)
    }

    /// What the newest window shows, as `cab` asks it (`gui_context`): both
    /// panes' folders, and what a command would act on in the active pane.
    /// From the state in memory only: no file is read. `no_window` when no
    /// window has sent a state.
    pub(crate) fn gui_context(&self) -> Response {
        let states = self.lock();
        let Some(newest) = states.values().max_by_key(|stored| stored.sequence) else {
            return Response::Error {
                code: ErrorCode::NoWindow,
                message: "no window has said what it shows".to_owned(),
            };
        };
        let state = &newest.state;
        let active = state.active();
        // A tool in front has no listing: the window sends no cursor and no
        // marks then, and none that arrived is taken for one.
        let listing = usize::try_from(active.active)
            .ok()
            .and_then(|front| active.tabs.get(front))
            .is_some_and(|tab| tab.tool.is_none());
        let known = u32::try_from(active.marked.len()).unwrap_or(u32::MAX);
        let marked_total = active.marked_total.unwrap_or(0).max(known);
        // What the window's file commands act on: the marked rows, or the
        // row the cursor is on when none is marked.
        let (selection, selection_total) = if !listing {
            (Vec::new(), 0)
        } else if marked_total > 0 {
            (active.marked.clone(), marked_total)
        } else if let Some(cursor) = &active.cursor {
            (vec![cursor.clone()], 1)
        } else {
            (Vec::new(), 0)
        };
        let left = front_folder(&state.panes.left).map(str::to_owned);
        let right = front_folder(&state.panes.right).map(str::to_owned);
        tracing::debug!(
            active = ?state.active_pane,
            left = left.as_deref().unwrap_or(""),
            right = right.as_deref().unwrap_or(""),
            selection = selection.len(),
            selection_total,
            "gui context answered"
        );
        Response::GuiContext {
            active: state.active_pane,
            left,
            right,
            selection,
            selection_total,
            cursor: active.cursor.clone().filter(|_| listing),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, Stored>> {
        self.states
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

/// The folder a pane shows: its tab in front, when that tab shows a folder
/// (not a tool) by an absolute path.
fn front_folder(shown: &PaneState) -> Option<&str> {
    let tab = shown.tabs.get(usize::try_from(shown.active).ok()?)?;
    (tab.tool.is_none() && std::path::Path::new(&tab.path).is_absolute())
        .then_some(tab.path.as_str())
}

/// Milliseconds since 1970-01-01 UTC.
pub(crate) fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::{PaneState, WindowTab};

    use super::*;

    fn state(pane: Pane) -> WindowState {
        WindowState {
            active_pane: pane,
            ..WindowState::default()
        }
    }

    #[test]
    fn the_newest_state_wins_and_a_named_client_is_found() {
        let states = WindowStates::default();
        assert!(matches!(
            states.get(None),
            Response::Error {
                code: ErrorCode::NoWindow,
                ..
            }
        ));
        states.store("CabinetOS#1", state(Pane::Left));
        states.store("CabinetOS#2", state(Pane::Right));
        let Response::WindowState {
            client, state: got, ..
        } = states.get(None)
        else {
            panic!("no state")
        };
        assert_eq!(
            (client.as_str(), got.active_pane),
            ("CabinetOS#2", Pane::Right)
        );
        // A new state from the first client makes it the newest.
        states.store("CabinetOS#1", state(Pane::Right));
        let Response::WindowState { client, .. } = states.get(None) else {
            panic!("no state")
        };
        assert_eq!(client, "CabinetOS#1");
        let Response::WindowState { client, .. } = states.get(Some("CabinetOS#2")) else {
            panic!("no state")
        };
        assert_eq!(client, "CabinetOS#2");
        states.remove("CabinetOS#2");
        let Response::Error { code, message } = states.get(Some("CabinetOS#2")) else {
            panic!("a state that was removed")
        };
        assert_eq!(code, ErrorCode::NoWindow);
        assert!(message.contains("CabinetOS#2"), "{message}");
    }

    fn tab(path: &str, tool: Option<&str>) -> WindowTab {
        WindowTab {
            path: path.to_owned(),
            locked: false,
            tool: tool.map(str::to_owned),
        }
    }

    #[test]
    fn a_pane_s_folder_is_its_tab_in_front_in_the_newest_state() {
        let states = WindowStates::default();
        assert_eq!(states.pane_folder(Pane::Left), None, "no window yet");
        let mut first = state(Pane::Left);
        first.panes.left = PaneState {
            tabs: vec![tab(r"C:\a", None), tab(r"D:\Звіт 'b c'", None)],
            active: 1,
            ..PaneState::default()
        };
        first.panes.right = PaneState {
            tabs: vec![tab(r"E:\notes\x.md", Some("markdown-preview"))],
            ..PaneState::default()
        };
        states.store("CabinetOS#1", first);
        assert_eq!(
            states.pane_folder(Pane::Left).as_deref(),
            Some(r"D:\Звіт 'b c'")
        );
        assert_eq!(
            states.pane_folder(Pane::Right),
            None,
            "a tool, not a folder"
        );

        let mut second = state(Pane::Right);
        second.panes.left = PaneState {
            tabs: vec![tab(r"\\server\share\x", None)],
            ..PaneState::default()
        };
        second.panes.right = PaneState {
            tabs: vec![tab("relative", None)],
            ..PaneState::default()
        };
        states.store("CabinetOS#2", second);
        assert_eq!(
            states.pane_folder(Pane::Left).as_deref(),
            Some(r"\\server\share\x"),
            "the newest window wins"
        );
        assert_eq!(states.pane_folder(Pane::Right), None, "not absolute");
        states.remove("CabinetOS#2");
        let mut past_end = state(Pane::Left);
        past_end.panes.left = PaneState {
            tabs: vec![tab(r"C:\a", None)],
            active: 3,
            ..PaneState::default()
        };
        states.store("CabinetOS#1", past_end);
        assert_eq!(states.pane_folder(Pane::Left), None, "no tab at that index");
    }

    /// `gui_context`'s answer as plain values: active pane, folders,
    /// selection, total and cursor.
    type Context = (
        Pane,
        Option<String>,
        Option<String>,
        Vec<String>,
        u32,
        Option<String>,
    );

    fn context(states: &WindowStates) -> Result<Context, ErrorCode> {
        match states.gui_context() {
            Response::GuiContext {
                active,
                left,
                right,
                selection,
                selection_total,
                cursor,
            } => Ok((active, left, right, selection, selection_total, cursor)),
            Response::Error { code, .. } => Err(code),
            other => panic!("not a gui_context: {other:?}"),
        }
    }

    fn strings(paths: &[&str]) -> Vec<String> {
        paths.iter().map(|path| (*path).to_owned()).collect()
    }

    fn shows(path: &str, cursor: Option<&str>, marked: &[&str]) -> PaneState {
        PaneState {
            tabs: vec![tab(path, None)],
            cursor: cursor.map(str::to_owned),
            marked: strings(marked),
            ..PaneState::default()
        }
    }

    #[test]
    fn without_a_window_the_context_says_no_window() {
        let states = WindowStates::default();
        assert_eq!(context(&states), Err(ErrorCode::NoWindow));
        states.store("CabinetOS#1", state(Pane::Left));
        states.remove("CabinetOS#1");
        assert_eq!(context(&states), Err(ErrorCode::NoWindow), "it left");
    }

    #[test]
    fn the_selection_is_the_marked_rows_of_the_active_pane() {
        let states = WindowStates::default();
        let mut shown = state(Pane::Right);
        shown.panes.left = shows(r"E:\left", Some(r"E:\left\x.txt"), &[]);
        shown.panes.right = shows(
            r"D:\right",
            Some(r"D:\right\b.txt"),
            &[r"D:\right\a.txt", r"D:\right\b.txt"],
        );
        states.store("CabinetOS#1", shown);
        assert_eq!(
            context(&states),
            Ok((
                Pane::Right,
                Some(r"E:\left".to_owned()),
                Some(r"D:\right".to_owned()),
                strings(&[r"D:\right\a.txt", r"D:\right\b.txt"]),
                2,
                Some(r"D:\right\b.txt".to_owned()),
            )),
            "the marks win over the cursor, and the other pane's cursor is not asked"
        );
    }

    #[test]
    fn with_no_marks_the_selection_is_the_cursor_row_as_the_window_s_commands_act() {
        let states = WindowStates::default();
        let mut shown = state(Pane::Left);
        shown.panes.left = shows(r"E:\left", Some(r"E:\left\only.txt"), &[]);
        shown.panes.right = shows(r"D:\right", None, &[]);
        states.store("CabinetOS#1", shown);
        let (_, left, right, selection, total, cursor) = context(&states).unwrap();
        assert_eq!(left.as_deref(), Some(r"E:\left"));
        assert_eq!(right.as_deref(), Some(r"D:\right"));
        assert_eq!(selection, strings(&[r"E:\left\only.txt"]));
        assert_eq!((total, cursor.as_deref()), (1, Some(r"E:\left\only.txt")));
    }

    #[test]
    fn an_empty_folder_has_no_cursor_and_no_selection() {
        let states = WindowStates::default();
        let mut shown = state(Pane::Left);
        shown.panes.left = shows(r"E:\empty", None, &[]);
        states.store("CabinetOS#1", shown);
        let (_, left, right, selection, total, cursor) = context(&states).unwrap();
        assert_eq!(left.as_deref(), Some(r"E:\empty"));
        assert_eq!(right, None, "the right pane has no tab");
        assert_eq!((selection, total, cursor), (Vec::new(), 0, None));
    }

    #[test]
    fn a_tool_in_front_shows_no_folder_and_has_no_selection() {
        let states = WindowStates::default();
        let mut shown = state(Pane::Left);
        // Whatever a window sent with the tool, a tool has no rows.
        shown.panes.left = PaneState {
            tabs: vec![tab(r"E:\notes\x.md", Some("markdown-preview"))],
            cursor: Some(r"E:\notes\x.md".to_owned()),
            marked: strings(&[r"E:\notes\x.md"]),
            ..PaneState::default()
        };
        shown.panes.right = shows(r"D:\right", Some(r"D:\right\a.txt"), &[]);
        states.store("CabinetOS#1", shown);
        let (active, left, right, selection, total, cursor) = context(&states).unwrap();
        assert_eq!(active, Pane::Left);
        assert_eq!(left, None, "a tool is not a folder");
        assert_eq!(right.as_deref(), Some(r"D:\right"));
        assert_eq!((selection, total, cursor), (Vec::new(), 0, None));
    }

    #[test]
    fn a_selection_the_window_cut_says_how_many_there_are() {
        let states = WindowStates::default();
        let mut shown = state(Pane::Left);
        shown.panes.left = shows(r"E:\big", Some(r"E:\big\f1.txt"), &[r"E:\big\f1.txt"]);
        shown.panes.left.marked_total = Some(5000);
        states.store("CabinetOS#1", shown);
        let (_, _, _, selection, total, _) = context(&states).unwrap();
        assert_eq!(selection.len(), 1);
        assert_eq!(total, 5000, "more than the paths listed: a client refuses");
        // A total that is not more than the list changes nothing.
        let mut whole = state(Pane::Left);
        whole.panes.left = shows(r"E:\big", None, &[r"E:\big\a", r"E:\big\b"]);
        whole.panes.left.marked_total = Some(1);
        states.store("CabinetOS#1", whole);
        assert_eq!(context(&states).unwrap().4, 2);
    }

    #[test]
    fn the_newest_window_decides_the_context() {
        let states = WindowStates::default();
        let mut first = state(Pane::Left);
        first.panes.left = shows(r"E:\first", Some(r"E:\first\a"), &[]);
        let mut second = state(Pane::Right);
        second.panes.right = shows(r"D:\second", Some(r"D:\second\b"), &[]);
        states.store("CabinetOS#1", first);
        states.store("CabinetOS#2", second);
        let (active, _, right, selection, ..) = context(&states).unwrap();
        assert_eq!(active, Pane::Right);
        assert_eq!(right.as_deref(), Some(r"D:\second"));
        assert_eq!(selection, strings(&[r"D:\second\b"]));
    }
}
