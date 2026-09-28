//! Terminal sessions: shells the core runs in pseudo-consoles
//! (`docs/terminal.md`). The control messages travel on the JSON pipe; each
//! session's bytes travel on a raw byte pipe of their own.

use serde::{Deserialize, Serialize};

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

/// One terminal session, as `terminal_list` reports it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TerminalSession {
    /// The session's ID, unique while the core runs.
    pub session_id: u64,
    /// The profile it was opened with, such as `pwsh`.
    pub profile: String,
    /// The folder it started in, or was last synced to. The shell may have
    /// moved since.
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
        };
        assert_eq!(
            serde_json::to_value(&session).unwrap(),
            json!({
                "session_id": 3, "profile": "pwsh", "cwd": r"E:\work", "cols": 120, "rows": 30,
                "pid": 4242, "state": {"type": "exited", "code": 3},
                "pipe": r"\\.\pipe\cabinetos-term-0123456789abcdef", "attached": false
            })
        );
        assert_eq!(
            serde_json::to_value(TerminalState::Running).unwrap(),
            json!({"type": "running"})
        );
    }
}
