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

use cabinetos_protocol::{ErrorCode, Response, WindowState};

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

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, Stored>> {
        self.states
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
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
    use cabinetos_protocol::Pane;

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
}
