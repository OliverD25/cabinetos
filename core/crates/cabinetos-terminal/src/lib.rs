//! Terminal sessions: shells that the core runs in Windows pseudo-consoles
//! (`ConPTY`), for the terminal pane (`docs/terminal.md`).
//!
//! - [`Terminals::open`] finds the profile's program on the `PATH`, creates
//!   a pseudo-console of the asked size with two pipes (input to the shell,
//!   output from it), and starts the program attached to it in the asked
//!   folder, with the core's environment plus `TERM=xterm-256color` and
//!   `CABINETOS_SESSION=<id>`, and the core's own folder added at the end
//!   of `PATH`, so `cabinetos-cli` runs from any of its shells.
//! - Each session's bytes travel on a byte pipe of its own,
//!   `\\.\pipe\cabinetos-term-<random>`: raw bytes, no framing, both ways,
//!   one client at a time, the current user only and no remote clients (the
//!   control pipe's rules). Terminal output is frequent and binary; the
//!   JSON control pipe carries only the requests about it.
//! - Sessions belong to the core: a client may leave and a new one attach
//!   to the same pipe. Output waits in a buffer of at most 1 MiB meanwhile;
//!   when it is full, the session stops reading the pseudo-console until a
//!   client catches up.
//! - When the shell exits, the output is delivered to its end, the pipe
//!   ends, and the event sink gets `terminal_exited`. The session stays
//!   listed, as exited, until it is closed. Closing it closes the
//!   pseudo-console, which the shell sees as a hang-up.
//! - Each session belongs to a file pane and has a mode, `locked` or
//!   `linked` ([`Terminals::set_mode`]); a change of mode goes to the event
//!   sink as `terminal_mode_changed`. Nothing here types into a shell on
//!   its own: a shell gets only what a client sends, and the paths of
//!   [`Terminals::type_paths`].
//!
//! [`console`] is the other end, for a client in a console window: raw
//! mode, the window size and keys as VT text.
//!
//! Serves Constitution Article 9 (Workspace & Terminal Integration: a shell
//! scoped to a pane), Article 4 (Progressive Disclosure: nothing runs until
//! a client asks) and Article 1 (a slow or absent client never blocks the
//! core: every session has its own threads, and its output is bounded).
//! Unsafe code is limited to the modules that call Windows APIs (`conpty`
//! and `console`); every unsafe block states its invariant in a `SAFETY:`
//! comment.

#[allow(unsafe_code)]
mod conpty;
#[allow(unsafe_code)]
pub mod console;
mod session;
mod shell;

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_protocol::{ErrorCode, Event, Pane, TerminalMode, TerminalSession};
use tokio::runtime::Handle;

use crate::session::{Session, Start};
use crate::shell::ShellKind;

pub use conpty::find_program;

/// The most sessions, running or exited, the core keeps at once.
pub const MAX_SESSIONS: usize = 32;

/// The most output a session holds for a client: 1 MiB.
pub const OUTPUT_LIMIT: usize = 1024 * 1024;

/// The name of every session's byte pipe starts with this; 16 random hex
/// digits follow.
pub const PIPE_PREFIX: &str = r"\\.\pipe\cabinetos-term-";

/// How long a shell may take to end after its pseudo-console closed before
/// it is ended by force.
pub(crate) const CLOSE_GRACE: Duration = Duration::from_secs(2);

/// The most character cells in either direction (a console's sizes are
/// 16-bit signed numbers).
const MAX_CELLS: u16 = 32_767;

/// Receives the events of every session: `terminal_exited` and
/// `terminal_mode_changed`.
pub type EventSink = Arc<dyn Fn(Event) + Send + Sync>;

/// One shell a session can run: a profile from `terminal.profiles`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Profile {
    /// Its name, such as `pwsh`.
    pub name: String,
    /// The program, such as `pwsh.exe`: a full path, or a name looked up in
    /// the `PATH`'s folders (with `.exe` added when it has no extension).
    pub command: String,
    /// Its arguments.
    pub args: Vec<String>,
    /// Whether its sessions may be `linked`.
    pub linkable: bool,
}

/// Whether a profile that does not say `linkable` may be linked: the
/// programs a prompt hook can be added to, PowerShell and WSL's shell. cmd
/// and any other program cannot follow a pane.
#[must_use]
pub fn linkable_by_default(command: &str) -> bool {
    matches!(
        ShellKind::of(command),
        ShellKind::PowerShell | ShellKind::Wsl
    )
}

/// Where a new session belongs: its pane, and how it is bound to it.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Binding {
    /// The file pane.
    pub pane: Pane,
    /// Locked or linked.
    pub mode: TerminalMode,
}

/// A session that just started.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Opened {
    /// Its ID, unique while the process runs.
    pub session_id: u64,
    /// Its byte pipe.
    pub pipe: String,
    /// The shell's process ID.
    pub pid: u32,
}

/// Why a request about a session failed.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
#[error("{message}")]
pub struct TerminalError {
    /// The protocol error code.
    pub code: ErrorCode,
    /// What is wrong.
    pub message: String,
}

impl TerminalError {
    fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    fn spawn_failed(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::SpawnFailed, message)
    }

    fn not_linkable(profile: &str) -> Self {
        Self::new(
            ErrorCode::NotLinkable,
            format!(
                "profile `{profile}` cannot be linked to a pane: no prompt hook can be added to its \
                 program (`\"linkable\": false`)"
            ),
        )
    }
}

/// The sessions, and the slots taken by sessions still starting.
struct Registry {
    sessions: BTreeMap<u64, Arc<Session>>,
    starting: usize,
    shut_down: bool,
}

/// Every terminal session of the process.
///
/// The methods block (starting a shell takes tens of milliseconds, closing
/// one up to [`CLOSE_GRACE`]), so an async caller runs them on a blocking
/// thread. Dropping it closes every session.
pub struct Terminals {
    runtime: Handle,
    sink: EventSink,
    limit: usize,
    next_id: AtomicU64,
    registry: Mutex<Registry>,
}

impl Terminals {
    /// No sessions yet. Each session's pipe is served on `runtime`; `sink`
    /// gets their events, from the sessions' own threads.
    #[must_use]
    pub fn new(runtime: Handle, sink: EventSink) -> Self {
        Self::with_limit(runtime, sink, MAX_SESSIONS)
    }

    /// Like [`new`](Self::new), with at most `limit` sessions instead of
    /// [`MAX_SESSIONS`].
    #[must_use]
    pub fn with_limit(runtime: Handle, sink: EventSink, limit: usize) -> Self {
        Self {
            runtime,
            sink,
            limit,
            next_id: AtomicU64::new(1),
            registry: Mutex::new(Registry {
                sessions: BTreeMap::new(),
                starting: 0,
                shut_down: false,
            }),
        }
    }

    fn registry(&self) -> MutexGuard<'_, Registry> {
        self.registry.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn session(&self, session_id: u64) -> Result<Arc<Session>, TerminalError> {
        self.registry()
            .sessions
            .get(&session_id)
            .cloned()
            .ok_or_else(|| {
                TerminalError::new(
                    ErrorCode::NoSuchSession,
                    format!("no terminal session {session_id}"),
                )
            })
    }

    /// Starts `profile`'s program in a pseudo-console of `cols` × `rows`
    /// cells (each at least 1 and at most 32,767), in `cwd` (an absolute
    /// path to a folder; the user's profile folder when `None`), bound to
    /// `binding`'s pane in its mode.
    ///
    /// Fails with `not_linkable` when the mode is `linked` and the profile
    /// is not linkable; with `spawn_failed` when the program is not on the
    /// `PATH`, the folder does not exist, [`MAX_SESSIONS`] sessions exist,
    /// or Windows refuses.
    pub fn open(
        &self,
        profile: &Profile,
        cwd: Option<&str>,
        cols: u16,
        rows: u16,
        binding: Binding,
    ) -> Result<Opened, TerminalError> {
        if binding.mode == TerminalMode::Linked && !profile.linkable {
            return Err(TerminalError::not_linkable(&profile.name));
        }
        let program = conpty::find_program(&profile.command).ok_or_else(|| {
            TerminalError::spawn_failed(format!(
                "`{}` of profile `{}` is not on the PATH",
                profile.command, profile.name
            ))
        })?;
        let cwd = match cwd {
            Some(cwd) => {
                folder(cwd).map_err(|(_, message)| TerminalError::spawn_failed(message))?
            }
            None => home_folder(),
        };
        let (cols, rows) = (cells(cols), cells(rows));
        {
            let mut registry = self.registry();
            if registry.shut_down {
                return Err(TerminalError::spawn_failed("the core is shutting down"));
            }
            if registry.sessions.len() + registry.starting >= self.limit {
                return Err(TerminalError::spawn_failed(format!(
                    "{} terminal sessions are open, the most the core keeps; close one first",
                    self.limit
                )));
            }
            registry.starting += 1;
        }
        let session_id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let started = session::start(
            &Start {
                id: session_id,
                profile,
                program: &program,
                cwd: &cwd,
                cols,
                rows,
                binding,
            },
            &self.runtime,
            Arc::clone(&self.sink),
        );
        let mut registry = self.registry();
        registry.starting -= 1;
        let session = started.map_err(TerminalError::spawn_failed)?;
        if registry.shut_down {
            drop(registry);
            session.begin_close();
            session.finish_close(CLOSE_GRACE);
            return Err(TerminalError::spawn_failed("the core is shutting down"));
        }
        registry.sessions.insert(session_id, Arc::clone(&session));
        drop(registry);
        tracing::info!(
            session_id,
            profile = %profile.name,
            program = %program.display(),
            pid = session.pid,
            cwd = %cwd.display(),
            cols,
            rows,
            pane = ?binding.pane,
            mode = ?binding.mode,
            linkable = profile.linkable,
            "terminal session opened"
        );
        Ok(Opened {
            session_id,
            pipe: session.pipe_name(),
            pid: session.pid,
        })
    }

    /// Changes how a session is bound to its pane. When the mode changes,
    /// the event sink gets `terminal_mode_changed`; the same mode again
    /// changes nothing and sends nothing. An exited session takes the
    /// change too: it stays listed until it is closed.
    ///
    /// Fails with `no_such_session` for an unknown session, and with
    /// `not_linkable` for `linked` when its profile is not linkable.
    pub fn set_mode(&self, session_id: u64, mode: TerminalMode) -> Result<(), TerminalError> {
        let session = self.session(session_id)?;
        if mode == TerminalMode::Linked && !session.linkable {
            return Err(TerminalError::not_linkable(session.profile()));
        }
        if session.set_mode(mode) {
            tracing::info!(session_id, mode = ?mode, "terminal mode changed");
            (self.sink)(Event::TerminalModeChanged { session_id, mode });
        }
        Ok(())
    }

    /// A session's pane and its mode now: what its prompt hook needs to
    /// know whether to follow the pane. Fails with `no_such_session` for an
    /// unknown session; an exited one still answers.
    pub fn binding(&self, session_id: u64) -> Result<(Pane, TerminalMode), TerminalError> {
        Ok(self.session(session_id)?.binding())
    }

    /// Changes a running session's size.
    pub fn resize(&self, session_id: u64, cols: u16, rows: u16) -> Result<(), TerminalError> {
        self.session(session_id)?.resize(cells(cols), cells(rows))
    }

    /// Closes a session and forgets it: the pseudo-console closes, so the
    /// shell gets a hang-up; a shell still running [`CLOSE_GRACE`] later is
    /// ended. Returns once the shell has ended.
    pub fn close(&self, session_id: u64) -> Result<(), TerminalError> {
        let session = self
            .registry()
            .sessions
            .remove(&session_id)
            .ok_or_else(|| {
                TerminalError::new(
                    ErrorCode::NoSuchSession,
                    format!("no terminal session {session_id}"),
                )
            })?;
        session.begin_close();
        session.finish_close(CLOSE_GRACE);
        tracing::info!(session_id, "terminal session closed");
        Ok(())
    }

    /// Types `paths` at the prompt of a running session, each quoted as
    /// the shell reads it literally, separated by spaces, without Enter. A
    /// path with a control character is refused: typed, it would act as a
    /// key.
    pub fn type_paths(&self, session_id: u64, paths: &[String]) -> Result<(), TerminalError> {
        let session = self.session(session_id)?;
        if let Some(path) = paths.iter().find(|path| path.chars().any(char::is_control)) {
            return Err(TerminalError::new(
                ErrorCode::InvalidPath,
                format!(
                    "{}: a path cannot contain control characters; typed, they would act as keys",
                    path.escape_debug()
                ),
            ));
        }
        session.type_paths(paths)?;
        tracing::debug!(
            session_id,
            paths = paths.len(),
            "paths typed into the terminal"
        );
        Ok(())
    }

    /// Every session, oldest first.
    #[must_use]
    pub fn list(&self) -> Vec<TerminalSession> {
        let sessions: Vec<Arc<Session>> = self.registry().sessions.values().cloned().collect();
        sessions.iter().map(|session| session.describe()).collect()
    }

    /// Closes every session, together within [`CLOSE_GRACE`], and refuses
    /// new ones.
    pub fn shutdown(&self) {
        let sessions: Vec<Arc<Session>> = {
            let mut registry = self.registry();
            registry.shut_down = true;
            std::mem::take(&mut registry.sessions)
                .into_values()
                .collect()
        };
        if sessions.is_empty() {
            return;
        }
        for session in &sessions {
            session.begin_close();
        }
        let deadline = Instant::now() + CLOSE_GRACE;
        for session in &sessions {
            session.finish_close(deadline.saturating_duration_since(Instant::now()));
        }
        tracing::info!(count = sessions.len(), "terminal sessions closed");
    }
}

impl Drop for Terminals {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn cells(count: u16) -> u16 {
    count.clamp(1, MAX_CELLS)
}

/// `path` when it is an absolute path to an existing folder; otherwise the
/// error code and message for it.
fn folder(path: &str) -> Result<PathBuf, (ErrorCode, String)> {
    let folder = Path::new(path);
    if !folder.is_absolute() {
        return Err((
            ErrorCode::InvalidPath,
            format!("{path}: not an absolute path"),
        ));
    }
    match std::fs::metadata(folder) {
        Ok(metadata) if metadata.is_dir() => Ok(folder.to_path_buf()),
        Ok(_) => Err((ErrorCode::InvalidPath, format!("{path}: not a folder"))),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            Err((ErrorCode::NotFound, format!("{path}: no such folder")))
        }
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
            Err((ErrorCode::AccessDenied, format!("{path}: {error}")))
        }
        Err(error) => Err((ErrorCode::Io, format!("{path}: {error}"))),
    }
}

/// Where a shell starts when the client names no folder: the user's profile
/// folder, else the process's own.
fn home_folder() -> PathBuf {
    std::env::var_os("USERPROFILE")
        .map(PathBuf::from)
        .filter(|home| home.is_dir())
        .or_else(|| std::env::current_dir().ok())
        .unwrap_or_else(|| PathBuf::from(r"C:\"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folders_must_be_absolute_and_exist() {
        let temp = std::env::temp_dir();
        assert_eq!(folder(&temp.display().to_string()), Ok(temp.clone()));
        assert_eq!(folder("relative").unwrap_err().0, ErrorCode::InvalidPath);
        assert_eq!(
            folder(r"C:\cabinetos-no-such-folder").unwrap_err().0,
            ErrorCode::NotFound
        );
        let file = std::env::current_exe().unwrap();
        assert_eq!(
            folder(&file.display().to_string()).unwrap_err().0,
            ErrorCode::InvalidPath
        );
        assert!(home_folder().is_dir());
    }

    #[test]
    fn only_powershell_and_wsl_are_linkable_unless_the_profile_says_so() {
        for command in [
            "pwsh.exe",
            r"C:\Program Files\PowerShell\7\pwsh.exe",
            "powershell",
            "wsl.exe",
        ] {
            assert!(linkable_by_default(command), "{command}");
        }
        for command in ["cmd.exe", "claude.exe", "nu.exe", ""] {
            assert!(!linkable_by_default(command), "{command}");
        }
    }

    #[test]
    fn sizes_stay_within_what_a_console_takes() {
        assert_eq!(cells(0), 1);
        assert_eq!(cells(80), 80);
        assert_eq!(cells(u16::MAX), 32_767);
    }
}
