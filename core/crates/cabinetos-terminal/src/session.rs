//! One terminal session: a shell in a pseudo-console, the three threads
//! that move its bytes and wait for it, and the task that serves its byte
//! pipe.
//!
//! - `term-<id>-out` reads the pseudo-console's output into [`Output`], a
//!   buffer of at most [`OUTPUT_LIMIT`] bytes. When it is full the thread
//!   stops reading, so the pseudo-console waits, until a client takes
//!   bytes out (backpressure).
//! - `term-<id>-in` writes what clients type (and the paths of
//!   `type_paths`) into the pseudo-console.
//! - `term-<id>-exit` waits for the shell to exit, closes the
//!   pseudo-console once its last output has arrived, and reports the exit.
//! - The pipe task serves one client at a time on the byte pipe: output from
//!   the buffer to the client, keys from the client to the input thread.

use std::collections::VecDeque;
use std::fs::File;
use std::io::{Read, Write};
use std::path::Path;
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use cabinetos_protocol::{ErrorCode, Event, Pane, TerminalMode, TerminalSession, TerminalState};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeServer};
use tokio::runtime::Handle;
use tokio::sync::{Notify, mpsc};
use tokio_util::sync::CancellationToken;
use tracing::Instrument;

use crate::conpty::{self, Child, PseudoConsole};
use crate::shell::{self, ShellKind};
use crate::{Binding, EventSink, OUTPUT_LIMIT, PIPE_PREFIX, Profile, TerminalError};

/// Bytes read from the pseudo-console at a time.
const READ_CHUNK: usize = 16 * 1024;
/// Bytes read from a client at a time.
const INPUT_CHUNK: usize = 4 * 1024;
/// Bytes written to a client at a time.
const WRITE_CHUNK: usize = 64 * 1024;
/// Input chunks that may wait for the input thread.
const INPUT_QUEUE: usize = 256;
/// After the shell exits, the output must be quiet this long before the
/// pseudo-console closes: the pseudo-console paints the shell's last
/// output a few milliseconds after the shell wrote it.
const QUIET: Duration = Duration::from_millis(100);
/// The longest wait for that quiet: a child process of the shell may still
/// write.
const QUIET_LIMIT: Duration = Duration::from_secs(1);
/// How long the output may take to end once the pseudo-console is closed.
const END_LIMIT: Duration = Duration::from_secs(5);
/// Pause before trying again to create the byte pipe's next instance.
const INSTANCE_RETRY: Duration = Duration::from_millis(50);
/// How long a closing session waits for a connect in flight to finish.
const LET_GO_LIMIT: Duration = Duration::from_secs(1);
/// How long a shell ended by force may take to be gone.
const TERMINATE_WAIT: Duration = Duration::from_secs(1);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The shell's output on its way to a client.
pub(crate) struct Output {
    state: Mutex<OutputState>,
    /// Wakes the reader thread (room in the buffer) and the exit thread (the
    /// output ended).
    changed: Condvar,
    /// Wakes the pipe task: bytes arrived, or the output ended.
    ready: Notify,
    limit: usize,
    /// For the log: span fields do not reach the log lines.
    session_id: u64,
}

/// What happens to output that does not fit.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Overflow {
    /// It waits for room (backpressure): the shell runs.
    Wait,
    /// It is dropped: the shell has exited, and the pseudo-console must be
    /// able to finish closing even when no client reads.
    DropNew,
    /// Everything is dropped and nobody reads: the session is closing.
    DropAll,
}

struct OutputState {
    bytes: VecDeque<u8>,
    overflow: Overflow,
    /// The pseudo-console closed its end: nothing more will come.
    ended: bool,
    /// Backpressure was logged for this session.
    waited: bool,
    /// Bytes dropped after the shell exited.
    dropped: usize,
    /// When output last arrived.
    last: Instant,
}

impl Output {
    pub(crate) fn new(session_id: u64, limit: usize) -> Self {
        Self {
            state: Mutex::new(OutputState {
                bytes: VecDeque::new(),
                overflow: Overflow::Wait,
                ended: false,
                waited: false,
                dropped: 0,
                last: Instant::now(),
            }),
            changed: Condvar::new(),
            ready: Notify::new(),
            limit,
            session_id,
        }
    }

    fn state(&self) -> MutexGuard<'_, OutputState> {
        lock(&self.state)
    }

    /// Adds output; waits while the buffer is full (backpressure).
    pub(crate) fn push(&self, mut bytes: &[u8]) {
        let mut state = self.state();
        state.last = Instant::now();
        while !bytes.is_empty() {
            if state.overflow == Overflow::DropAll {
                return;
            }
            let room = self.limit.saturating_sub(state.bytes.len());
            if room == 0 {
                if state.overflow == Overflow::DropNew {
                    state.dropped += bytes.len();
                    return;
                }
                if !state.waited {
                    state.waited = true;
                    tracing::warn!(
                        session_id = self.session_id,
                        limit = self.limit,
                        "terminal output backpressure: the buffer is full, so the shell's \
                         output waits until a client reads"
                    );
                }
                state = self
                    .changed
                    .wait(state)
                    .unwrap_or_else(PoisonError::into_inner);
                continue;
            }
            let taken = room.min(bytes.len());
            state.bytes.extend(&bytes[..taken]);
            bytes = &bytes[taken..];
            self.ready.notify_one();
        }
    }

    /// The pseudo-console closed its end of the pipe.
    pub(crate) fn end(&self) {
        let mut state = self.state();
        state.ended = true;
        if state.dropped > 0 {
            tracing::debug!(
                session_id = self.session_id,
                dropped = state.dropped,
                "output that arrived after the shell exited did not fit the buffer"
            );
        }
        drop(state);
        self.changed.notify_all();
        self.ready.notify_one();
    }

    /// Up to `max` bytes from the front of the buffer, left in it until
    /// [`consume`](Self::consume); `None` once the output has ended and all
    /// of it was taken, or the session is closing.
    pub(crate) async fn next_chunk(&self, max: usize) -> Option<Vec<u8>> {
        loop {
            // Created before the check: a permit stored by `notify_one` in
            // between wakes it at once.
            let ready = self.ready.notified();
            {
                let state = self.state();
                if state.overflow == Overflow::DropAll {
                    return None;
                }
                if !state.bytes.is_empty() {
                    let count = max.min(state.bytes.len());
                    return Some(state.bytes.range(..count).copied().collect());
                }
                if state.ended {
                    return None;
                }
            }
            ready.await;
        }
    }

    /// Removes `count` bytes a client received from the front.
    pub(crate) fn consume(&self, count: usize) {
        let mut state = self.state();
        let count = count.min(state.bytes.len());
        state.bytes.drain(..count);
        drop(state);
        self.changed.notify_all();
    }

    /// The shell has exited: stop waiting for room.
    fn shell_exited(&self) {
        let mut state = self.state();
        if state.overflow == Overflow::Wait {
            state.overflow = Overflow::DropNew;
        }
        drop(state);
        self.changed.notify_all();
    }

    /// The session is closing: drop everything, now and later.
    fn discard(&self) {
        let mut state = self.state();
        state.overflow = Overflow::DropAll;
        state.bytes = VecDeque::new();
        drop(state);
        self.changed.notify_all();
        self.ready.notify_one();
    }

    fn is_discarding(&self) -> bool {
        self.state().overflow == Overflow::DropAll
    }

    /// Waits until no output has arrived for `quiet`, counted from the call
    /// at the earliest, but no longer than `limit`.
    fn wait_quiet(&self, quiet: Duration, limit: Duration) {
        let started = Instant::now();
        loop {
            let last = self.state().last.max(started);
            let now = Instant::now();
            let silent = now.duration_since(last);
            if silent >= quiet || now.duration_since(started) >= limit {
                return;
            }
            std::thread::sleep(quiet.saturating_sub(silent).min(Duration::from_millis(20)));
        }
    }

    /// Waits up to `limit` for the output to end; whether it did.
    fn wait_ended(&self, limit: Duration) -> bool {
        let state = self.state();
        let (state, _) = self
            .changed
            .wait_timeout_while(state, limit, |state| !state.ended)
            .unwrap_or_else(PoisonError::into_inner);
        state.ended
    }

    #[cfg(test)]
    fn buffered(&self) -> usize {
        self.state().bytes.len()
    }
}

/// What a session reports and changes.
struct Info {
    cwd: String,
    cols: u16,
    rows: u16,
    state: TerminalState,
    attached: bool,
    mode: TerminalMode,
    /// The shell's folder, as its prompt hook last reported it.
    folder: Option<String>,
}

/// What a new session needs.
pub(crate) struct Start<'a> {
    pub(crate) id: u64,
    pub(crate) profile: &'a Profile,
    /// The program, found on the `PATH`.
    pub(crate) program: &'a Path,
    /// A folder that exists.
    pub(crate) cwd: &'a Path,
    pub(crate) cols: u16,
    pub(crate) rows: u16,
    pub(crate) binding: Binding,
}

/// One running (or exited) shell.
pub(crate) struct Session {
    pub(crate) id: u64,
    profile: String,
    kind: ShellKind,
    pane: Pane,
    /// Whether the mode may be `linked`, from the profile it started with.
    pub(crate) linkable: bool,
    pipe: PipeName,
    pub(crate) pid: u32,
    process: std::os::windows::io::OwnedHandle,
    /// `None` once closed.
    console: Mutex<Option<PseudoConsole>>,
    /// To the input thread; `None` once the shell exited or the session
    /// closed.
    input: Mutex<Option<mpsc::Sender<Vec<u8>>>>,
    output: Output,
    info: Mutex<Info>,
    /// Ends the pipe task.
    stop: CancellationToken,
    span: tracing::Span,
}

fn no_longer_running(id: u64) -> TerminalError {
    TerminalError::new(
        ErrorCode::NoSuchSession,
        format!("the shell of terminal session {id} has exited"),
    )
}

/// Starts a shell: its byte pipe first (nothing to undo if that fails), then
/// the pseudo-console and the process, then the threads and the pipe task.
pub(crate) fn start(
    start: &Start<'_>,
    runtime: &Handle,
    sink: EventSink,
) -> Result<Arc<Session>, String> {
    let Start {
        id,
        profile,
        program,
        cwd,
        cols,
        rows,
        binding,
    } = *start;
    let pipe = PipeName::from_full(format!("{PIPE_PREFIX}{:016x}", rand::random::<u64>()));
    let server = {
        let _entered = runtime.enter();
        cabinetos_ipc::byte_pipe(&pipe, true)
    }
    .map_err(|error| format!("cannot create the session's pipe {pipe}: {error}"))?;

    let pty_error = |error: std::io::Error| format!("cannot create a pseudo-console: {error}");
    let (input_read, input_write) = conpty::pipe().map_err(pty_error)?;
    let (output_read, output_write) = conpty::pipe().map_err(pty_error)?;
    let console =
        PseudoConsole::create(cols, rows, &input_read, &output_write).map_err(pty_error)?;
    // The pseudo-console keeps its own copies. Without ours, the output pipe
    // ends when the pseudo-console closes.
    drop((input_read, output_write));

    let session_text = id.to_string();
    let mut inherited: Vec<_> = std::env::vars_os().collect();
    // The core's own folder holds `cabinetos-cli` (and `cab` in a release),
    // so the shell can run the command line of the window it sits in.
    if let Some(folder) = std::env::current_exe()
        .ok()
        .as_deref()
        .and_then(Path::parent)
    {
        shell::append_to_path(&mut inherited, folder);
    }
    let environment = shell::environment_block(
        inherited,
        &[
            ("TERM", "xterm-256color"),
            ("CABINETOS_SESSION", &session_text),
        ],
    );
    let line = shell::command_line(program, &profile.args);
    let Child { process, pid } = match conpty::spawn(&console, program, &line, &environment, cwd) {
        Ok(child) => child,
        Err(error) => {
            // Our end of the output first: a pseudo-console whose output
            // nobody reads may wait forever to close.
            drop(output_read);
            drop(console);
            return Err(format!("cannot start {}: {error}", program.display()));
        }
    };

    let span = tracing::info_span!("terminal", session_id = id);
    let (input_tx, input_rx) = mpsc::channel(INPUT_QUEUE);
    let session = Arc::new(Session {
        id,
        profile: profile.name.clone(),
        kind: ShellKind::of(&profile.command),
        pane: binding.pane,
        linkable: profile.linkable,
        pipe,
        pid,
        process,
        console: Mutex::new(Some(console)),
        input: Mutex::new(Some(input_tx)),
        output: Output::new(id, OUTPUT_LIMIT),
        info: Mutex::new(Info {
            cwd: cwd.display().to_string(),
            cols,
            rows,
            state: TerminalState::Running,
            attached: false,
            mode: binding.mode,
            folder: None,
        }),
        stop: CancellationToken::new(),
        span: span.clone(),
    });

    let reader = Arc::clone(&session);
    let watcher = Arc::clone(&session);
    let started = spawn_thread(format!("term-{id}-out"), &span, move || {
        reader.read_output(output_read);
    })
    .and_then(|()| {
        spawn_thread(format!("term-{id}-in"), &span, move || {
            write_input(input_write, input_rx);
        })
    })
    .and_then(|()| {
        spawn_thread(format!("term-{id}-exit"), &span, move || {
            watcher.watch_exit(&sink);
        })
    });
    if let Err(error) = started {
        session.begin_close();
        session.finish_close(crate::CLOSE_GRACE);
        return Err(format!("cannot start the session's threads: {error}"));
    }
    runtime.spawn(Arc::clone(&session).serve_pipe(server).instrument(span));
    Ok(session)
}

fn spawn_thread(
    name: String,
    span: &tracing::Span,
    work: impl FnOnce() + Send + 'static,
) -> std::io::Result<()> {
    let span = span.clone();
    std::thread::Builder::new().name(name).spawn(move || {
        let _entered = span.enter();
        work();
    })?;
    Ok(())
}

/// Writes what clients type into the pseudo-console, until the session
/// lets go of the queue or the pseudo-console stops reading.
fn write_input(mut pipe: File, mut input: mpsc::Receiver<Vec<u8>>) {
    while let Some(bytes) = input.blocking_recv() {
        if pipe.write_all(&bytes).is_err() {
            break;
        }
    }
}

impl Session {
    pub(crate) fn pipe_name(&self) -> String {
        self.pipe.as_str().to_owned()
    }

    fn info(&self) -> MutexGuard<'_, Info> {
        lock(&self.info)
    }

    /// Reads the output until the pseudo-console closes.
    fn read_output(&self, mut pipe: File) {
        let mut chunk = vec![0; READ_CHUNK];
        loop {
            match pipe.read(&mut chunk) {
                Ok(0) | Err(_) => break,
                Ok(read) => self.output.push(&chunk[..read]),
            }
        }
        self.output.end();
    }

    /// Waits for the shell, then lets the pseudo-console paint the last
    /// output, closes it, waits for the output to end, and reports.
    fn watch_exit(&self, sink: &EventSink) {
        let code = conpty::wait_for_exit(&self.process);
        self.info().state = TerminalState::Exited { code };
        tracing::info!(
            session_id = self.id,
            exit_code = code,
            "terminal shell exited"
        );
        if !self.output.is_discarding() {
            self.output.wait_quiet(QUIET, QUIET_LIMIT);
        }
        self.output.shell_exited();
        self.close_console();
        drop(lock(&self.input).take());
        if !self.output.wait_ended(END_LIMIT) {
            tracing::warn!(
                session_id = self.id,
                "the pseudo-console did not end its output after the shell exited"
            );
        }
        sink(Event::TerminalExited {
            session_id: self.id,
            exit_code: code,
        });
    }

    fn close_console(&self) {
        let console = lock(&self.console).take();
        // Outside the lock: closing may wait for the last output to drain.
        drop(console);
    }

    /// Starts closing: the pipe task stops, the output is dropped, the
    /// pseudo-console closes (the shell gets a hang-up), and typing ends.
    pub(crate) fn begin_close(&self) {
        self.stop.cancel();
        self.output.discard();
        self.close_console();
        drop(lock(&self.input).take());
    }

    /// Waits up to `wait` for the shell to end after
    /// [`begin_close`](Self::begin_close); ends it if it does not.
    pub(crate) fn finish_close(&self, wait: Duration) {
        if !conpty::ends_within(&self.process, wait) {
            // Seen with a shell whose pseudo-console closed in its first
            // milliseconds: it never ends on its own.
            let _entered = self.span.enter();
            tracing::warn!(
                session_id = self.id,
                pid = self.pid,
                "the shell kept running after its pseudo-console closed; ending it"
            );
            conpty::terminate(&self.process);
            // Ending is asynchronous; until it is done, the shell still
            // holds its current folder, for one.
            if !conpty::ends_within(&self.process, TERMINATE_WAIT) {
                tracing::warn!(
                    session_id = self.id,
                    pid = self.pid,
                    "the shell did not end when told to"
                );
            }
        }
    }

    pub(crate) fn resize(&self, cols: u16, rows: u16) -> Result<(), TerminalError> {
        let console = lock(&self.console);
        let Some(console) = console.as_ref() else {
            return Err(no_longer_running(self.id));
        };
        console.resize(cols, rows).map_err(|error| {
            TerminalError::new(
                ErrorCode::Internal,
                format!("cannot resize terminal session {}: {error}", self.id),
            )
        })?;
        let mut info = self.info();
        info.cols = cols;
        info.rows = rows;
        Ok(())
    }

    /// The profile it was opened with.
    pub(crate) fn profile(&self) -> &str {
        &self.profile
    }

    /// Its pane and its mode now.
    pub(crate) fn binding(&self) -> (Pane, TerminalMode) {
        (self.pane, self.info().mode)
    }

    /// Sets the mode; whether it changed.
    pub(crate) fn set_mode(&self, mode: TerminalMode) -> bool {
        let mut info = self.info();
        let changed = info.mode != mode;
        info.mode = mode;
        changed
    }

    /// Types `paths` at the prompt, quoted for the shell, without Enter.
    pub(crate) fn type_paths(&self, paths: &[String]) -> Result<(), TerminalError> {
        self.type_text(self.kind.typed_paths(paths), "the paths")
    }

    /// Types `text` as one chunk of input, so a client's keys cannot land
    /// inside it. `what` names it in the error.
    fn type_text(&self, text: String, what: &str) -> Result<(), TerminalError> {
        let input = lock(&self.input)
            .clone()
            .ok_or_else(|| no_longer_running(self.id))?;
        match input.try_send(text.into_bytes()) {
            Ok(()) => Ok(()),
            Err(mpsc::error::TrySendError::Closed(_)) => Err(no_longer_running(self.id)),
            Err(mpsc::error::TrySendError::Full(_)) => Err(TerminalError::new(
                ErrorCode::Internal,
                format!(
                    "the shell of terminal session {} does not read its input; \
                     {what} were not typed",
                    self.id
                ),
            )),
        }
    }

    pub(crate) fn describe(&self) -> TerminalSession {
        let info = self.info();
        TerminalSession {
            session_id: self.id,
            profile: self.profile.clone(),
            cwd: info.cwd.clone(),
            cols: info.cols,
            rows: info.rows,
            pid: self.pid,
            state: info.state,
            pipe: self.pipe_name(),
            attached: info.attached,
            pane: self.pane,
            mode: info.mode,
            linkable: self.linkable,
            folder: info.folder.clone(),
        }
    }

    /// Serves the byte pipe until the session closes: one client at a time,
    /// each on a fresh instance. While a client is attached no instance
    /// listens, so a second client is turned away as busy.
    async fn serve_pipe(self: Arc<Self>, mut server: NamedPipeServer) {
        loop {
            let connected = tokio::select! {
                () = self.stop.cancelled() => {
                    self.let_go(server).await;
                    return;
                }
                connected = server.connect() => connected,
            };
            match connected {
                Ok(()) => {
                    self.info().attached = true;
                    tracing::debug!(session_id = self.id, "terminal client attached");
                    self.pump(&mut server).await;
                    tracing::debug!(session_id = self.id, "terminal client detached");
                }
                Err(error) => tracing::debug!(
                    session_id = self.id,
                    %error,
                    "a terminal client failed to connect"
                ),
            }
            if self.stop.is_cancelled() {
                return;
            }
            // The next instance first, so the pipe's name never disappears
            // while clients come and go.
            let next = self.next_instance().await;
            // Dropping the instance lets a write in flight finish, so the
            // client reads the output to its end, then the end of the pipe.
            drop(server);
            let Some(next) = next else {
                return;
            };
            server = next;
            self.info().attached = false;
        }
    }

    /// Output to the client and keys from it, until the client leaves, the
    /// output has ended and was delivered, or the session closes.
    async fn pump(&self, server: &mut NamedPipeServer) {
        let input = lock(&self.input).clone();
        let (mut from_client, mut to_client) = tokio::io::split(server);
        let keys = async {
            let mut buffer = vec![0; INPUT_CHUNK];
            loop {
                match from_client.read(&mut buffer).await {
                    Ok(0) | Err(_) => return,
                    Ok(read) => {
                        if let Some(input) = &input {
                            // Refused only once the shell has exited.
                            let _ = input.send(buffer[..read].to_vec()).await;
                        }
                    }
                }
            }
        };
        let output = async {
            while let Some(chunk) = self.output.next_chunk(WRITE_CHUNK).await {
                if to_client.write_all(&chunk).await.is_err() {
                    // Not consumed: the next client gets it.
                    return;
                }
                self.output.consume(chunk.len());
            }
        };
        tokio::select! {
            () = self.stop.cancelled() => {}
            () = keys => {}
            () = output => {}
        }
    }

    /// Drops an instance whose connect may be in flight. When that connect
    /// completes after the instance is dropped, the runtime still starts a
    /// read for the new client, and that read keeps the instance open: the
    /// client would never see the end of the pipe. So the connect finishes
    /// first, with a client of our own when no real one came; the instance
    /// then closes like a connected one, and a real client gets its end.
    async fn let_go(&self, server: NamedPipeServer) {
        let own = ClientOptions::new().open(self.pipe.as_str());
        let _ = tokio::time::timeout(LET_GO_LIMIT, server.connect()).await;
        drop(server);
        drop(own);
    }

    /// The pipe's next instance. The pipe takes two, so Windows refuses a
    /// third while an earlier client still reads the end of its output.
    async fn next_instance(&self) -> Option<NamedPipeServer> {
        let mut waiting = false;
        loop {
            match cabinetos_ipc::byte_pipe(&self.pipe, false) {
                Ok(server) => return Some(server),
                Err(error) => {
                    if !waiting {
                        waiting = true;
                        tracing::debug!(
                            session_id = self.id,
                            %error,
                            "waiting for the last client to let go of the pipe"
                        );
                    }
                    tokio::select! {
                        () = self.stop.cancelled() => return None,
                        () = tokio::time::sleep(INSTANCE_RETRY) => {}
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicBool, Ordering};

    use super::*;

    #[test]
    fn a_full_buffer_holds_the_reader_until_a_client_reads() {
        let output = Arc::new(Output::new(1, 8));
        output.push(b"12345678");
        assert_eq!(output.buffered(), 8);

        let pushed = Arc::new(AtomicBool::new(false));
        let reader = {
            let output = Arc::clone(&output);
            let pushed = Arc::clone(&pushed);
            std::thread::spawn(move || {
                output.push(b"abc");
                pushed.store(true, Ordering::SeqCst);
            })
        };
        std::thread::sleep(Duration::from_millis(100));
        assert!(
            !pushed.load(Ordering::SeqCst),
            "waits while the buffer is full"
        );
        assert_eq!(output.buffered(), 8, "never more than the limit");

        let runtime = tokio::runtime::Builder::new_current_thread()
            .build()
            .unwrap();
        let chunk = runtime.block_on(output.next_chunk(5)).unwrap();
        assert_eq!(chunk, b"12345");
        output.consume(chunk.len());
        reader.join().unwrap();
        assert!(pushed.load(Ordering::SeqCst));
        assert_eq!(output.buffered(), 6);

        output.end();
        let rest = runtime.block_on(output.next_chunk(64)).unwrap();
        assert_eq!(rest, b"678abc");
        output.consume(rest.len());
        assert_eq!(runtime.block_on(output.next_chunk(64)), None, "ended");
    }

    #[test]
    fn after_the_shell_exits_what_does_not_fit_is_dropped() {
        let output = Output::new(1, 4);
        output.push(b"1234");
        output.shell_exited();
        output.push(b"56");
        assert_eq!(output.buffered(), 4);
        output.end();
        assert!(output.wait_ended(Duration::ZERO));
    }

    #[test]
    fn closing_frees_a_waiting_reader_and_drops_the_output() {
        let output = Arc::new(Output::new(1, 2));
        output.push(b"12");
        let reader = {
            let output = Arc::clone(&output);
            std::thread::spawn(move || output.push(b"345"))
        };
        std::thread::sleep(Duration::from_millis(50));
        output.discard();
        reader.join().unwrap();
        assert_eq!(output.buffered(), 0);
        let runtime = tokio::runtime::Builder::new_current_thread()
            .build()
            .unwrap();
        assert_eq!(runtime.block_on(output.next_chunk(64)), None);
    }

    #[test]
    fn quiet_is_measured_from_the_last_output() {
        let output = Output::new(1, 64);
        let started = Instant::now();
        output.wait_quiet(Duration::from_millis(60), Duration::from_secs(1));
        let waited = started.elapsed();
        assert!(waited >= Duration::from_millis(60), "{waited:?}");
        assert!(waited < Duration::from_millis(500), "{waited:?}");

        let started = Instant::now();
        output.wait_quiet(Duration::from_secs(10), Duration::from_millis(80));
        assert!(started.elapsed() < Duration::from_secs(2), "the limit wins");
    }
}
