//! Terminal sessions: shells the core runs in pseudo-consoles
//! (`docs/terminal.md`). The control messages travel on the JSON pipe; each
//! session's bytes travel on a raw byte pipe of their own.

use serde::{Deserialize, Serialize};

use crate::window::Pane;

/// Whether a session's shell still runs.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum TerminalState {
    /// The shell runs.
    Running,
    /// The shell has exited.
    Exited {
        /// Its exit code (a Windows exit code, such as 3221225786 for a
        /// console closed under it).
        code: u32,
    },
}

/// How a session is bound to its pane (`docs/terminal.md`, "Panes and
/// modes").
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum TerminalMode {
    /// The session stays where the user takes it: nothing the pane does
    /// reaches it.
    #[default]
    Locked,
    /// The session follows its pane: each time the shell draws its prompt,
    /// its prompt hook asks the core for the pane's folder
    /// (`terminal_pane_folder`) and changes to it. A running command or a
    /// half-typed line is never touched.
    Linked,
}

/// One terminal session, as `terminal_list` reports it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TerminalSession {
    /// The session's ID, unique while the core runs.
    pub session_id: u64,
    /// The profile it was opened with, such as `pwsh`.
    pub profile: String,
    /// The folder it started in. The shell may have moved since.
    pub cwd: String,
    /// Its width in character cells.
    pub cols: u16,
    /// Its height in character cells.
    pub rows: u16,
    /// The shell's process ID.
    pub pid: u32,
    /// Whether the shell still runs.
    pub state: TerminalState,
    /// The session's byte pipe, such as `\\.\pipe\cabinetos-term-<token>`.
    pub pipe: String,
    /// Whether a client is attached to the byte pipe now.
    pub attached: bool,
    /// The file pane the session belongs to.
    pub pane: Pane,
    /// How it is bound to that pane.
    pub mode: TerminalMode,
    /// Whether it may be `linked`: `false` for a profile that says
    /// `"linkable": false` (no prompt hook can be added to its program).
    pub linkable: bool,
    /// The shell's current folder, as its prompt hook last reported it
    /// (`terminal_folder_changed`); left out before the first report and
    /// for a shell without a hook.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub folder: Option<String>,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn sessions_are_plain_json() {
        let session = TerminalSession {
            session_id: 3,
            profile: "pwsh".to_owned(),
            cwd: r"E:\work".to_owned(),
            cols: 120,
            rows: 30,
            pid: 4242,
            state: TerminalState::Exited { code: 3 },
            pipe: r"\\.\pipe\cabinetos-term-0123456789abcdef".to_owned(),
            attached: false,
            pane: Pane::Right,
            mode: TerminalMode::Linked,
            linkable: true,
            folder: None,
        };
        assert_eq!(
            serde_json::to_value(&session).unwrap(),
            json!({
                "session_id": 3, "profile": "pwsh", "cwd": r"E:\work", "cols": 120, "rows": 30,
                "pid": 4242, "state": {"type": "exited", "code": 3},
                "pipe": r"\\.\pipe\cabinetos-term-0123456789abcdef", "attached": false,
                "pane": "right", "mode": "linked", "linkable": true
            })
        );
        assert_eq!(
            serde_json::to_value(TerminalState::Running).unwrap(),
            json!({"type": "running"})
        );
    }

    #[test]
    fn modes_are_two_words_and_locked_is_the_default() {
        assert_eq!(TerminalMode::default(), TerminalMode::Locked);
        assert_eq!(
            serde_json::to_value(TerminalMode::Locked).unwrap(),
            json!("locked")
        );
        assert_eq!(
            serde_json::from_value::<TerminalMode>(json!("linked")).unwrap(),
            TerminalMode::Linked
        );
        assert!(serde_json::from_value::<TerminalMode>(json!("following")).is_err());
        assert!(serde_json::from_value::<TerminalMode>(json!("Locked")).is_err());
    }
}
