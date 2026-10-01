//! `cabinetos-cli term`: a shell run by the core, in this console window;
//! and `term list`, `term close`, `term type`, `term mode`
//! (`docs/terminal.md`).

use std::io::{Read, Write};
use std::time::Duration;

use anyhow::Context;
use cabinetos_cli_args::{ModeArg, PaneArg};
use cabinetos_ipc::PipeClient;
use cabinetos_protocol::{
    Envelope, Event, Pane, Request, Response, TerminalMode, TerminalSession, TerminalState,
};
use cabinetos_terminal::console::{self, ConsoleInput, RawMode};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::mpsc::{self, UnboundedReceiver, UnboundedSender};

use crate::{expect_welcome, failure, say, send};

/// `Ctrl+]` in raw mode: detaches from the session.
const DETACH: u8 = 0x1d;
/// How often the console's size is compared with the session's.
const RESIZE_POLL: Duration = Duration::from_millis(250);
/// How long to wait for `terminal_exited` once the output has ended.
const EXIT_WAIT: Duration = Duration::from_secs(2);
/// The session's size when standard output is not a console.
const DEFAULT_SIZE: (u16, u16) = (80, 25);
/// How long another client may keep the session's pipe busy.
const ATTACH_TIMEOUT: Duration = Duration::from_secs(5);
/// Undoes what a shell may have left in this console: colors, a hidden
/// cursor.
const RESET: &[u8] = b"\x1b[0m\x1b[?25h";

/// What the thread that reads standard input reports.
enum Input {
    Keys(Vec<u8>),
    /// `Ctrl+]` was pressed.
    Detach,
    /// Standard input ended (a pipe or a file).
    End,
}

/// How attaching ended.
enum Outcome {
    Detached,
    /// The output ended: the shell exited, or the session was closed. The
    /// exit code, when `terminal_exited` arrived.
    Ended(Option<u32>),
}

/// Writes a status line to standard error: standard output carries the
/// shell's bytes.
fn note(line: std::fmt::Arguments<'_>) {
    let _ = writeln!(std::io::stderr(), "cabinetos-cli: {line}");
}

/// `term`: opens a session sized to this console and attaches to it until
/// `Ctrl+]` or until the shell exits. With standard input from a pipe or a
/// file, forwards it unchanged and waits for the shell to exit.
pub(crate) async fn run(
    client: &mut PipeClient,
    profile: Option<String>,
    cwd: String,
    pane: Pane,
) -> anyhow::Result<()> {
    expect_welcome(client).await?;
    let mut events = client.events().context("the core's events were taken")?;
    let interactive = console::stdin_is_console();
    let on_console = console::stdout_is_console();
    let (cols, rows) = console::size().unwrap_or(DEFAULT_SIZE);
    let reply = send(
        client,
        Request::TerminalOpen {
            profile,
            cwd: Some(cwd),
            cols,
            rows,
            pane,
            mode: None,
        },
    )
    .await?;
    let Response::TerminalOpened {
        session_id,
        pipe,
        pid,
        ..
    } = reply.body
    else {
        return Err(failure("terminal_open", &reply.body));
    };
    note(format_args!(
        "session {session_id}, pid {pid}{}",
        if interactive { "; Ctrl+] detaches" } else { "" }
    ));
    let output = attach(&pipe).await?;
    let raw = if interactive {
        Some(RawMode::enable().context("cannot switch the console to raw mode")?)
    } else {
        None
    };
    let outcome = pump(
        client,
        &mut events,
        session_id,
        output,
        interactive,
        on_console,
    )
    .await;
    drop(raw);
    if on_console {
        let mut stdout = std::io::stdout();
        let _ = stdout.write_all(RESET).and_then(|()| stdout.flush());
    }
    match outcome? {
        Outcome::Detached => note(format_args!(
            "detached; session {session_id} keeps running (`term close {session_id}` ends it)"
        )),
        Outcome::Ended(Some(code)) => {
            note(format_args!("session {session_id} ended; exit code {code}"));
        }
        Outcome::Ended(None) => note(format_args!(
            "session {session_id} ended; its exit code did not arrive"
        )),
    }
    Ok(())
}

/// Opens the session's byte pipe; waits while another client has it.
async fn attach(pipe: &str) -> anyhow::Result<NamedPipeClient> {
    let deadline = tokio::time::Instant::now() + ATTACH_TIMEOUT;
    loop {
        match ClientOptions::new().open(pipe) {
            Ok(client) => return Ok(client),
            Err(error)
                if error.raw_os_error() == Some(231) // ERROR_PIPE_BUSY
                    && tokio::time::Instant::now() < deadline =>
            {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            Err(error) => {
                return Err(error).with_context(|| format!("cannot attach to {pipe}"));
            }
        }
    }
}

/// Reads standard input on its own thread: a console key by key, anything
/// else as it comes.
fn read_input(interactive: bool, keys: &UnboundedSender<Input>) {
    if interactive {
        let mut input = ConsoleInput::new();
        while let Ok(bytes) = input.read() {
            if let Some(at) = bytes.iter().position(|&byte| byte == DETACH) {
                if at > 0 {
                    let _ = keys.send(Input::Keys(bytes[..at].to_vec()));
                }
                let _ = keys.send(Input::Detach);
                return;
            }
            if keys.send(Input::Keys(bytes)).is_err() {
                return;
            }
        }
    } else {
        let mut stdin = std::io::stdin().lock();
        let mut buffer = vec![0; 4096];
        loop {
            match stdin.read(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(read) => {
                    if keys.send(Input::Keys(buffer[..read].to_vec())).is_err() {
                        return;
                    }
                }
            }
        }
    }
    let _ = keys.send(Input::End);
}

/// Moves bytes both ways until `Ctrl+]` or the end of the output, and keeps
/// the session's size in step with the console's.
async fn pump(
    client: &mut PipeClient,
    events: &mut UnboundedReceiver<Envelope<Event>>,
    session_id: u64,
    output: NamedPipeClient,
    interactive: bool,
    on_console: bool,
) -> anyhow::Result<Outcome> {
    let (keys_tx, mut keys) = mpsc::unbounded_channel();
    std::thread::Builder::new()
        .name("term-input".to_owned())
        .spawn(move || read_input(interactive, &keys_tx))
        .context("cannot start the input thread")?;
    let (mut from_shell, mut to_shell) = tokio::io::split(output);
    let mut stdout = tokio::io::stdout();
    let mut filter = ModeFilter::default();
    let mut exit_code = None;
    let mut size = console::size();
    let mut resize_tick = tokio::time::interval(RESIZE_POLL);
    let mut buffer = vec![0; 64 * 1024];
    let mut typing = true;
    let mut listening = true;
    loop {
        tokio::select! {
            read = from_shell.read(&mut buffer) => match read {
                Ok(0) | Err(_) => break,
                Ok(read) => {
                    let bytes = if on_console {
                        filter.pass(&buffer[..read])
                    } else {
                        buffer[..read].to_vec()
                    };
                    stdout.write_all(&bytes).await?;
                    stdout.flush().await?;
                }
            },
            input = keys.recv(), if typing => match input {
                Some(Input::Keys(bytes)) => {
                    if to_shell.write_all(&bytes).await.is_err() {
                        // The session is gone; its output ends next.
                        typing = false;
                    }
                }
                Some(Input::Detach) => return Ok(Outcome::Detached),
                Some(Input::End) | None => typing = false,
            },
            event = events.recv(), if listening => match event {
                Some(Envelope {
                    body: Event::TerminalExited { session_id: exited, exit_code: code },
                    ..
                }) if exited == session_id => exit_code = Some(code),
                Some(_) => {}
                None => listening = false,
            },
            _ = resize_tick.tick(), if on_console => {
                let now = console::size();
                if now != size {
                    size = now;
                    if let Some((cols, rows)) = now {
                        let reply = send(client, Request::TerminalResize { session_id, cols, rows }).await?;
                        if reply.body != Response::Ok {
                            tracing::debug!(reply = ?reply.body, "resize refused");
                        }
                    }
                }
            }
        }
    }
    let tail = filter.finish();
    if !tail.is_empty() {
        stdout.write_all(&tail).await?;
        stdout.flush().await?;
    }
    if exit_code.is_none() && listening {
        exit_code = wait_for_exit(events, session_id).await;
    }
    Ok(Outcome::Ended(exit_code))
}

/// The session's `terminal_exited`, if it arrives within [`EXIT_WAIT`].
async fn wait_for_exit(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    session_id: u64,
) -> Option<u32> {
    let deadline = tokio::time::Instant::now() + EXIT_WAIT;
    loop {
        match tokio::time::timeout_at(deadline, events.recv()).await {
            Ok(Some(Envelope {
                body:
                    Event::TerminalExited {
                        session_id: exited,
                        exit_code,
                    },
                ..
            })) if exited == session_id => return Some(exit_code),
            Ok(Some(_)) => {}
            Ok(None) | Err(_) => return None,
        }
    }
}

/// Drops two requests the pseudo-console makes of the terminal in front of
/// it: win32-input-mode (`ESC [ ? 9001 h`), which would make this console
/// send keys as sequences in which `Ctrl+]` no longer shows, and focus
/// reports (`ESC [ ? 1004 h`). Keys then arrive as plain VT, which the
/// pseudo-console also reads. A sequence split between two reads is held
/// back until the next.
#[derive(Debug, Default)]
struct ModeFilter {
    held: Vec<u8>,
}

impl ModeFilter {
    const DROPPED: [&[u8]; 2] = [b"\x1b[?9001h", b"\x1b[?1004h"];

    fn pass(&mut self, bytes: &[u8]) -> Vec<u8> {
        let mut data = std::mem::take(&mut self.held);
        data.extend_from_slice(bytes);
        let mut passed = Vec::with_capacity(data.len());
        let mut at = 0;
        while at < data.len() {
            if data[at] == 0x1b {
                let rest = &data[at..];
                if let Some(dropped) = Self::DROPPED.iter().find(|seq| rest.starts_with(seq)) {
                    at += dropped.len();
                    continue;
                }
                if Self::DROPPED.iter().any(|seq| seq.starts_with(rest)) {
                    self.held = rest.to_vec();
                    break;
                }
            }
            passed.push(data[at]);
            at += 1;
        }
        passed
    }

    /// What was held back when the output ends.
    fn finish(&mut self) -> Vec<u8> {
        std::mem::take(&mut self.held)
    }
}

/// `term list`: one line per session.
pub(crate) async fn list(client: &mut PipeClient) -> anyhow::Result<()> {
    let reply = send(client, Request::TerminalList).await?;
    let Response::TerminalSessions { sessions } = reply.body else {
        return Err(failure("terminal_list", &reply.body));
    };
    if sessions.is_empty() {
        say(format_args!("no terminal sessions"));
    }
    for session in &sessions {
        if !say(format_args!("{}", session_line(session))) {
            break;
        }
    }
    Ok(())
}

/// `3 pwsh left locked pid 4242 120x30 running attached E:\work`.
fn session_line(session: &TerminalSession) -> String {
    let state = match session.state {
        TerminalState::Running => "running".to_owned(),
        TerminalState::Exited { code } => format!("exited({code})"),
    };
    let pane = match session.pane {
        Pane::Left => "left",
        Pane::Right => "right",
    };
    format!(
        "{} {} {pane} {} pid {} {}x{} {state} {} {}",
        session.session_id,
        session.profile,
        mode_word(session.mode),
        session.pid,
        session.cols,
        session.rows,
        if session.attached {
            "attached"
        } else {
            "detached"
        },
        session.cwd
    )
}

/// The pane of `--pane`.
pub(crate) fn pane(pane: PaneArg) -> Pane {
    match pane {
        PaneArg::Left => Pane::Left,
        PaneArg::Right => Pane::Right,
    }
}

/// The mode of `term mode`.
pub(crate) fn mode(mode: ModeArg) -> TerminalMode {
    match mode {
        ModeArg::Locked => TerminalMode::Locked,
        ModeArg::Linked => TerminalMode::Linked,
    }
}

/// `locked` or `linked`, as the wire says it.
pub(crate) fn mode_word(mode: TerminalMode) -> &'static str {
    match mode {
        TerminalMode::Locked => "locked",
        TerminalMode::Linked => "linked",
    }
}

/// `term close <id>`, `term type <id> <paths>` and `term mode <id> <mode>`:
/// one request, `ok` back.
pub(crate) async fn change(
    client: &mut PipeClient,
    request: Request,
    done: std::fmt::Arguments<'_>,
) -> anyhow::Result<()> {
    let kind = request.type_tag();
    let reply = send(client, request).await?;
    if reply.body != Response::Ok {
        return Err(failure(kind, &reply.body));
    }
    say(done);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_filter_drops_the_two_mode_requests_even_when_split() {
        let mut filter = ModeFilter::default();
        assert_eq!(
            filter.pass(b"\x1b[?9001h\x1b[?1004h\x1b[?25lhello"),
            b"\x1b[?25lhello"
        );
        assert_eq!(filter.pass(b"a\x1b[?90"), b"a");
        assert_eq!(filter.pass(b"01hb\x1b[?1"), b"b");
        assert_eq!(filter.pass(b"004lc"), b"\x1b[?1004lc");
        assert_eq!(filter.pass(b"\x1b[?10"), b"");
        assert_eq!(filter.finish(), b"\x1b[?10");
    }

    #[test]
    fn a_session_reads_as_one_line() {
        let mut session = TerminalSession {
            session_id: 3,
            profile: "pwsh".to_owned(),
            cwd: r"E:\work".to_owned(),
            cols: 120,
            rows: 30,
            pid: 4242,
            state: TerminalState::Running,
            pipe: r"\\.\pipe\cabinetos-term-0123456789abcdef".to_owned(),
            attached: true,
            pane: Pane::Left,
            mode: TerminalMode::Locked,
            linkable: true,
        };
        assert_eq!(
            session_line(&session),
            r"3 pwsh left locked pid 4242 120x30 running attached E:\work"
        );
        session.state = TerminalState::Exited { code: 3 };
        session.attached = false;
        session.pane = Pane::Right;
        session.mode = TerminalMode::Linked;
        assert_eq!(
            session_line(&session),
            r"3 pwsh right linked pid 4242 120x30 exited(3) detached E:\work"
        );
    }
}
