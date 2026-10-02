//! Real shells in real pseudo-consoles: cmd everywhere; pwsh, Windows
//! PowerShell and WSL when they are installed (skipped with a message
//! otherwise). The shells run only `echo`, `cd`, `mode con` (which prints
//! the console's size), `Get-Location`, `pwd` and `exit`, in folders under
//! `%TEMP%\cabinetos-term-test\`, which the tests remove. A `cd` the test
//! types itself, with the folder typed by `type_paths`, shows that each
//! shell reads a typed path literally. The prompt hook's tests give the
//! shells hook code of their own (the core's own hook asks the core, which
//! `cabinetos-cli`'s tests run).

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, mpsc};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeName, PipeServer};
use cabinetos_protocol::{ErrorCode, Event, Pane, TerminalMode, TerminalSession, TerminalState};
use cabinetos_terminal::{
    Binding, EventSink, Hook, HookHost, MAX_SESSIONS, OUTPUT_LIMIT, Opened, PIPE_PREFIX, Profile,
    Terminals, linkable_by_default,
};
use tempfile::TempDir;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::runtime::Runtime;

/// How long a shell may take to answer. WSL may have to start first.
const DEADLINE: Duration = Duration::from_secs(60);

/// `ERROR_PIPE_BUSY`: another client is attached.
const PIPE_BUSY: i32 = 231;

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-term-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

/// A path as the shells print it.
fn shown(path: &Path) -> String {
    path.display().to_string()
}

/// `path` spelled with long names. `%TEMP%` may contain a short name, such
/// as `RUNNER~1` on CI; cmd prints a folder as it was given, PowerShell
/// with its long names.
fn long_name(path: &Path) -> PathBuf {
    let full = std::fs::canonicalize(path).unwrap();
    let full = full.to_string_lossy();
    PathBuf::from(full.strip_prefix(r"\\?\").unwrap_or(&full))
}

fn profile(name: &str, command: &str, args: &[&str]) -> Profile {
    Profile {
        name: name.to_owned(),
        command: command.to_owned(),
        args: args.iter().map(|arg| (*arg).to_owned()).collect(),
        linkable: linkable_by_default(command),
        hook: Hook::Off,
    }
}

fn cmd() -> Profile {
    profile("cmd", "cmd.exe", &[])
}

/// Whether `program` is in one of the `PATH`'s folders.
fn on_path(program: &str) -> bool {
    std::env::var_os("PATH")
        .is_some_and(|path| std::env::split_paths(&path).any(|dir| dir.join(program).is_file()))
}

/// The output as text: VT sequences, carriage returns and other control
/// characters removed; a cursor move to a row becomes a line break, a move
/// to the right a space.
fn plain(bytes: &[u8]) -> String {
    let raw = String::from_utf8_lossy(bytes);
    let mut plain = String::with_capacity(raw.len());
    let mut chars = raw.chars();
    while let Some(c) = chars.next() {
        match c {
            '\u{1b}' => match chars.next() {
                Some('[') => {
                    for c in chars.by_ref() {
                        if ('@'..='~').contains(&c) {
                            match c {
                                'H' | 'f' => plain.push('\n'),
                                'C' => plain.push(' '),
                                _ => {}
                            }
                            break;
                        }
                    }
                }
                Some(']') => {
                    while let Some(c) = chars.next() {
                        if c == '\u{7}' {
                            break;
                        }
                        if c == '\u{1b}' {
                            chars.next();
                            break;
                        }
                    }
                }
                _ => {}
            },
            '\n' => plain.push('\n'),
            c if c.is_control() => {}
            c => plain.push(c),
        }
    }
    plain
}

/// Every number `mode con` printed after `Columns:`.
fn columns(output: &str) -> Vec<u32> {
    output
        .match_indices("Columns:")
        .filter_map(|(at, label)| {
            let rest = output[at + label.len()..].trim_start();
            let digits: String = rest.chars().take_while(char::is_ascii_digit).collect();
            digits.parse().ok()
        })
        .collect()
}

/// Whether the output ends at a prompt of cmd or PowerShell: `…>`, maybe
/// followed by a space.
fn at_prompt(output: &str) -> bool {
    output.trim_end().ends_with('>')
}

/// Whether one line of the output is exactly `wanted`.
fn has_line(output: &str, wanted: &str) -> bool {
    output.lines().any(|line| line.trim_end() == wanted)
}

/// A client of a session's byte pipe.
struct Client {
    pipe: NamedPipeClient,
    output: Vec<u8>,
}

impl Client {
    async fn attach(pipe: &str) -> Self {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            match ClientOptions::new().open(pipe) {
                Ok(pipe) => {
                    return Self {
                        pipe,
                        output: Vec::new(),
                    };
                }
                Err(error) if error.raw_os_error() == Some(PIPE_BUSY) => {
                    assert!(Instant::now() < deadline, "{pipe} stayed busy");
                    tokio::time::sleep(Duration::from_millis(20)).await;
                }
                Err(error) => panic!("cannot attach to {pipe}: {error}"),
            }
        }
    }

    async fn send(&mut self, keys: &str) {
        self.pipe.write_all(keys.as_bytes()).await.unwrap();
    }

    /// Reads until `done` holds for the plain text read so far (since the
    /// last [`forget`](Self::forget)); returns that text.
    async fn read_until(&mut self, done: impl Fn(&str) -> bool) -> String {
        let deadline = tokio::time::Instant::now() + DEADLINE;
        let mut buffer = vec![0; 64 * 1024];
        loop {
            let output = plain(&self.output);
            if done(&output) {
                return output;
            }
            match tokio::time::timeout_at(deadline, self.pipe.read(&mut buffer)).await {
                Ok(Ok(0)) => panic!("the output ended first; it was:\n{output}"),
                Ok(Ok(read)) => self.output.extend_from_slice(&buffer[..read]),
                Ok(Err(error)) => panic!("reading failed: {error}; the output was:\n{output}"),
                Err(elapsed) => panic!("{elapsed}; the output so far:\n{output}"),
            }
        }
    }

    /// Reads to the end of the pipe; returns the raw bytes since the last
    /// [`forget`](Self::forget).
    async fn read_to_end(&mut self) -> Vec<u8> {
        let mut rest = Vec::new();
        match tokio::time::timeout(DEADLINE, self.pipe.read_to_end(&mut rest)).await {
            Ok(Ok(_)) => {}
            Ok(Err(error)) => panic!("reading failed: {error}"),
            Err(elapsed) => panic!("the output did not end: {elapsed}"),
        }
        self.output.extend_from_slice(&rest);
        std::mem::take(&mut self.output)
    }

    /// Later reads look only at output from now on.
    fn forget(&mut self) {
        self.output.clear();
    }
}

/// A [`Terminals`] with its runtime and the events it sent. Create it after
/// the test's folders: it closes its sessions when dropped, and a folder
/// that is a shell's current folder cannot be removed.
struct Harness {
    // Declared first, so it closes its sessions before the runtime stops.
    terminals: Terminals,
    events: mpsc::Receiver<Event>,
    runtime: Runtime,
}

impl Harness {
    fn new() -> Self {
        Self::with_limit(MAX_SESSIONS)
    }

    fn with_limit(limit: usize) -> Self {
        Self::build(limit, None)
    }

    /// Shells that take a prompt hook get one, and every shell the pipe
    /// token `test-pipe`; no command line: only a profile's own hook code
    /// follows anything.
    fn with_hooks() -> Self {
        Self::build(
            MAX_SESSIONS,
            Some(HookHost {
                pipe_token: "test-pipe".to_owned(),
                cli: None,
            }),
        )
    }

    fn build(limit: usize, host: Option<HookHost>) -> Self {
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .unwrap();
        let (sender, events) = mpsc::channel();
        let sink: EventSink = Arc::new(move |event| {
            let _ = sender.send(event);
        });
        let mut terminals = Terminals::with_limit(runtime.handle().clone(), sink, limit);
        if let Some(host) = host {
            terminals = terminals.with_host(host);
        }
        Self {
            terminals,
            events,
            runtime,
        }
    }

    fn open(&self, profile: &Profile, cwd: &Path, cols: u16, rows: u16) -> Opened {
        self.terminals
            .open(profile, Some(&shown(cwd)), cols, rows, Binding::default())
            .unwrap_or_else(|error| panic!("cannot open {}: {error}", profile.name))
    }

    fn attach(&self, opened: &Opened) -> Client {
        self.runtime.block_on(Client::attach(&opened.pipe))
    }

    fn send(&self, client: &mut Client, keys: &str) {
        self.runtime.block_on(client.send(keys));
    }

    fn read_until(&self, client: &mut Client, done: impl Fn(&str) -> bool) -> String {
        self.runtime.block_on(client.read_until(done))
    }

    fn read_to_end(&self, client: &mut Client) -> Vec<u8> {
        self.runtime.block_on(client.read_to_end())
    }

    /// Changes the shell's folder to `path` with a `cd` the test types
    /// itself around the folder `type_paths` typed: the folder first, at
    /// the empty prompt (as Ctrl+Alt+P types it), then Home, the shell's own
    /// `cd` (`command`, with the space after it) and Enter. The keys wait
    /// for the folder's echo: they and the typed paths take two ways into
    /// the shell. (Typed after the command instead, the folder lost its
    /// typographic quotes in Windows PowerShell 5.1 on 2026-10-01, while a
    /// suggestion from its history was on the line.)
    fn cd_typed(&self, client: &mut Client, opened: &Opened, command: &str, path: &Path) {
        client.forget();
        self.terminals
            .type_paths(opened.session_id, &[shown(path)])
            .unwrap();
        let parent = path
            .parent()
            .and_then(Path::file_name)
            .unwrap()
            .to_string_lossy()
            .into_owned();
        self.read_until(client, |output| output.contains(&parent));
        self.send(client, &format!("\x1b[H{command}\r"));
    }

    /// The next `terminal_mode_changed`; fails on any other event.
    fn mode_change(&self) -> (u64, TerminalMode) {
        match self.events.recv_timeout(Duration::from_secs(5)) {
            Ok(Event::TerminalModeChanged { session_id, mode }) => (session_id, mode),
            other => panic!("expected terminal_mode_changed, got {other:?}"),
        }
    }

    /// Waits for the session's `terminal_exited`; returns its exit code.
    fn exit_code(&self, session_id: u64) -> u32 {
        let deadline = Instant::now() + DEADLINE;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            match self.events.recv_timeout(left) {
                Ok(Event::TerminalExited {
                    session_id: exited,
                    exit_code,
                }) if exited == session_id => return exit_code,
                Ok(_) => {}
                Err(error) => panic!("no terminal_exited for session {session_id}: {error}"),
            }
        }
    }

    /// Waits until `check` holds for the session's entry in the list.
    fn wait_for(&self, session_id: u64, check: impl Fn(&TerminalSession) -> bool) {
        let deadline = Instant::now() + DEADLINE;
        loop {
            let listed = self.terminals.list();
            let session = listed
                .iter()
                .find(|session| session.session_id == session_id)
                .unwrap_or_else(|| panic!("session {session_id} is not listed"));
            if check(session) {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "timed out; the session is {session:?}"
            );
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

#[test]
fn cmd_output_arrives_and_its_exit_code_is_reported() {
    let dir = scratch("exit");
    let harness = Harness::new();
    let once = profile(
        "once",
        "cmd.exe",
        &["/c", "echo", "hello-from-conpty", "&&", "exit", "3"],
    );
    let opened = harness.open(&once, dir.path(), 80, 25);
    assert!(opened.pipe.starts_with(PIPE_PREFIX), "{}", opened.pipe);
    assert!(opened.pid > 0);

    // The shell may be done before a client attaches: its output waits.
    let mut client = harness.attach(&opened);
    let output = plain(&harness.read_to_end(&mut client));
    assert!(output.contains("hello-from-conpty"), "{output}");
    assert_eq!(harness.exit_code(opened.session_id), 3);

    let listed = harness.terminals.list();
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].session_id, opened.session_id);
    assert_eq!(listed[0].profile, "once");
    assert_eq!(listed[0].pid, opened.pid);
    assert_eq!(listed[0].state, TerminalState::Exited { code: 3 });
    assert_eq!(listed[0].cwd, shown(dir.path()));

    // An exited session stays until it is closed.
    harness.terminals.close(opened.session_id).unwrap();
    assert!(harness.terminals.list().is_empty());
}

#[test]
fn typed_input_reaches_the_shell() {
    let dir = scratch("input");
    let harness = Harness::new();
    let opened = harness.open(&cmd(), dir.path(), 80, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);

    // The caret keeps the typed line from containing the answer: cmd
    // prints `abc`, the screen also shows `echo a^bc`.
    harness.send(&mut client, "echo a^bc\r\n");
    harness.read_until(&mut client, |output| output.contains("\nabc"));
    harness.send(&mut client, "exit\r\n");
    harness.read_to_end(&mut client);
    assert_eq!(harness.exit_code(opened.session_id), 0);
    harness.wait_for(opened.session_id, |session| {
        session.state == TerminalState::Exited { code: 0 }
    });
}

/// Whether `entry` (one folder of a `PATH`) names `folder`.
fn is_folder(entry: &str, folder: &str) -> bool {
    entry
        .trim()
        .trim_end_matches('\\')
        .eq_ignore_ascii_case(folder.trim_end_matches('\\'))
}

/// The core's own folder holds `cabinetos-cli`; here the core is this test
/// binary, so its folder must be one of the shell's `PATH` entries.
///
/// `cargo test` puts this folder on `PATH` itself, which would make the
/// check pass without the core adding anything. So the test runs itself
/// once more (`CABINETOS_PATH_CHILD`) with that folder taken out of `PATH`.
#[test]
fn the_cores_folder_is_on_the_shells_path() {
    let exe = std::env::current_exe().unwrap();
    let core_folder = exe.parent().unwrap().to_path_buf();
    let wanted = shown(&core_folder);
    if std::env::var_os("CABINETOS_PATH_CHILD").is_none() {
        let inherited = std::env::var_os("PATH").unwrap_or_default();
        let kept: Vec<PathBuf> = std::env::split_paths(&inherited)
            .filter(|entry| !is_folder(&shown(entry), &wanted))
            .collect();
        let output = Command::new(&exe)
            .args(["--exact", "the_cores_folder_is_on_the_shells_path"])
            .args(["--test-threads=1", "--nocapture"])
            .env("PATH", std::env::join_paths(kept).unwrap())
            .env("CABINETOS_PATH_CHILD", "1")
            .output()
            .unwrap();
        let text = String::from_utf8_lossy(&output.stdout);
        assert!(
            output.status.success() && text.contains("1 passed"),
            "{text}\n{}",
            String::from_utf8_lossy(&output.stderr)
        );
        return;
    }
    // Here the core's folder is not in the core's own `PATH`.
    let own = std::env::var_os("PATH").unwrap_or_default();
    assert!(
        !std::env::split_paths(&own).any(|entry| is_folder(&shown(&entry), &wanted)),
        "the child must start without the folder"
    );

    let dir = scratch("path");
    let harness = Harness::new();
    // A wide console keeps a long `PATH` on one line.
    let opened = harness.open(&cmd(), dir.path(), 4000, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);

    client.forget();
    harness.send(&mut client, "echo %PATH%\r");
    harness.read_until(&mut client, |output| {
        output
            .lines()
            .flat_map(|line| line.split(';'))
            .any(|entry| is_folder(entry, &wanted))
    });
}

#[test]
fn resizing_changes_the_width_the_shell_sees() {
    let dir = scratch("resize");
    let harness = Harness::new();
    let opened = harness.open(&cmd(), dir.path(), 80, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);
    harness.send(&mut client, "mode con\r");
    harness.read_until(&mut client, |output| columns(output).contains(&80));

    client.forget();
    harness.terminals.resize(opened.session_id, 40, 25).unwrap();
    harness.send(&mut client, "mode con\r");
    harness.read_until(&mut client, |output| columns(output).contains(&40));
    harness.wait_for(opened.session_id, |session| {
        (session.cols, session.rows) == (40, 25)
    });
}

#[test]
fn cmd_reads_a_typed_folder_literally() {
    let dir = scratch("typed-cd");
    let harness = Harness::new();
    // cmd would expand %CABINETOS_SESSION% (the shell has that variable)
    // if the typed path left it inside the quotes.
    let target = dir.path().join("to 100%CABINETOS_SESSION% & ^x (y)");
    std::fs::create_dir(&target).unwrap();
    let opened = harness.open(&cmd(), dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);

    harness.cd_typed(&mut client, &opened, "cd /d ", &target);
    let prompt = format!("{}>", shown(&target));
    harness.read_until(&mut client, |output| output.contains(&prompt));
    client.forget();
    harness.send(&mut client, "cd\r");
    harness.read_until(&mut client, |output| has_line(output, &shown(&target)));
    // The core does not follow the shell: the list keeps the folder it
    // started in.
    harness.wait_for(opened.session_id, |session| {
        session.cwd == shown(dir.path())
    });
}

/// Paths reach cmd's prompt quoted as its `cd` is, and no Enter follows:
/// typed twice, they stay on one line, after the prompt.
#[test]
fn paths_are_typed_at_the_prompt_without_enter() {
    let dir = scratch("type");
    let harness = Harness::new();
    let opened = harness.open(&cmd(), dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);

    let first = [shown(&dir.path().join("a b.txt"))];
    let second = [shown(&dir.path().join("100%CABINETOS_SESSION% & x"))];
    harness
        .terminals
        .type_paths(opened.session_id, &first)
        .unwrap();
    harness
        .terminals
        .type_paths(opened.session_id, &second)
        .unwrap();
    let folder = shown(dir.path());
    let line = format!(r#"{folder}>"{folder}\a b.txt""{folder}\100"%^C"ABINETOS_SESSION"%^ "& x""#);
    harness.read_until(&mut client, |output| has_line(output, &line));

    let refused = |session_id: u64, path: &str| {
        harness
            .terminals
            .type_paths(session_id, &[path.to_owned()])
            .unwrap_err()
            .code
    };
    assert_eq!(
        refused(opened.session_id, "C:\\a\rcalc"),
        ErrorCode::InvalidPath
    );
    assert_eq!(
        refused(opened.session_id, "C:\\a\u{1b}[2J"),
        ErrorCode::InvalidPath
    );
    assert_eq!(
        refused(opened.session_id + 1000, &folder),
        ErrorCode::NoSuchSession
    );
}

/// A folder name beyond ASCII with the characters each shell's quoting
/// must keep literal: `'` and `’` (PowerShell and bash quotes), `$`
/// (PowerShell and bash variables), `%` (cmd variables).
const BEYOND_ASCII: &str = "Звіт 'проєкт' $HOME ’q’ 100%PATH% Ґанок";

#[test]
fn cmd_reads_a_typed_folder_beyond_ascii_literally() {
    let dir = scratch("typed-names");
    let harness = Harness::new();
    let target = long_name(dir.path()).join(BEYOND_ASCII);
    std::fs::create_dir(&target).unwrap();
    let opened = harness.open(&cmd(), dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, at_prompt);

    harness.cd_typed(&mut client, &opened, "cd /d ", &target);
    let prompt = format!("{}>", shown(&target));
    harness.read_until(&mut client, |output| output.contains(&prompt));
    client.forget();
    harness.send(&mut client, "cd\r");
    harness.read_until(&mut client, |output| has_line(output, &shown(&target)));
    harness.send(&mut client, "exit\r");
    harness.read_to_end(&mut client);
    assert_eq!(harness.exit_code(opened.session_id), 0);
}

#[test]
fn powershell_reads_a_typed_folder_beyond_ascii_literally_when_installed() {
    let mut tested = 0;
    for program in ["pwsh.exe", "powershell.exe"] {
        if !on_path(program) {
            println!("skipped {program}: it is not on the PATH");
            continue;
        }
        let dir = scratch("powershell-names");
        let harness = Harness::new();
        let target = long_name(dir.path()).join(BEYOND_ASCII);
        std::fs::create_dir(&target).unwrap();
        let shell = profile("ps", program, &["-NoLogo", "-NoProfile"]);
        let opened = harness.open(&shell, dir.path(), 300, 25);
        let mut client = harness.attach(&opened);
        harness.read_until(&mut client, |output| {
            output.contains("PS ") && at_prompt(output)
        });

        harness.cd_typed(&mut client, &opened, "Set-Location -LiteralPath ", &target);
        let prompt = format!("PS {}>", shown(&target));
        harness.read_until(&mut client, |output| output.contains(&prompt));
        client.forget();
        harness.send(&mut client, "(Get-Location).Path\r");
        harness.read_until(&mut client, |output| has_line(output, &shown(&target)));
        harness.send(&mut client, "exit\r");
        harness.read_to_end(&mut client);
        assert_eq!(harness.exit_code(opened.session_id), 0, "{program}");
        tested += 1;
    }
    println!("{tested} PowerShell version(s) tested");
}

#[test]
fn two_sessions_are_independent() {
    let dir = scratch("two");
    let harness = Harness::new();
    let first = harness.open(&cmd(), dir.path(), 80, 25);
    let second = harness.open(&cmd(), dir.path(), 80, 25);
    assert_ne!(first.session_id, second.session_id);
    assert_ne!(first.pipe, second.pipe);
    let mut first_client = harness.attach(&first);
    let mut second_client = harness.attach(&second);
    harness.read_until(&mut first_client, at_prompt);
    harness.read_until(&mut second_client, at_prompt);

    harness.terminals.close(first.session_id).unwrap();
    // The first shell got its hang-up; its pipe ends.
    harness.read_to_end(&mut first_client);
    harness.exit_code(first.session_id);
    let listed = harness.terminals.list();
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].session_id, second.session_id);
    assert_eq!(listed[0].state, TerminalState::Running);

    harness.send(&mut second_client, "echo still^ here\r");
    harness.read_until(&mut second_client, |output| output.contains("\nstill here"));
    assert_eq!(
        harness.terminals.close(first.session_id).unwrap_err().code,
        ErrorCode::NoSuchSession
    );
}

#[test]
fn a_session_outlives_its_client_and_takes_one_client_at_a_time() {
    let dir = scratch("reattach");
    let harness = Harness::new();
    let opened = harness.open(&cmd(), dir.path(), 200, 25);
    let mut first = harness.attach(&opened);
    harness.read_until(&mut first, at_prompt);
    harness.wait_for(opened.session_id, |session| session.attached);

    // A second client is turned away: no instance listens while one is
    // attached.
    let busy = harness
        .runtime
        .block_on(async { ClientOptions::new().open(&opened.pipe) })
        .unwrap_err();
    assert_eq!(busy.raw_os_error(), Some(PIPE_BUSY), "{busy}");

    drop(first);
    harness.wait_for(opened.session_id, |session| !session.attached);

    // Output made while nobody is attached waits for the next client: a
    // resize makes the pseudo-console paint the screen, prompt and all.
    harness
        .terminals
        .resize(opened.session_id, 150, 25)
        .unwrap();
    std::thread::sleep(Duration::from_millis(300));
    let mut second = harness.attach(&opened);
    let prompt = format!("{}>", shown(dir.path()));
    harness.read_until(&mut second, |output| output.contains(&prompt));
    harness.send(&mut second, "echo fr^esh\r");
    harness.read_until(&mut second, |output| output.contains("\nfresh"));
    harness.send(&mut second, "exit\r");
    harness.read_to_end(&mut second);
    assert_eq!(harness.exit_code(opened.session_id), 0);
    drop(second);

    // After the end, a client gets the end at once.
    let mut late = harness.attach(&opened);
    assert!(harness.read_to_end(&mut late).is_empty());
}

#[test]
fn a_full_buffer_holds_the_shell_until_a_client_reads() {
    let dir = scratch("flood");
    let harness = Harness::new();
    let lines: u32 = 40_000;
    let count = format!("(1,1,{lines})");
    // About 2.8 MB of output, well over the buffer's 1 MiB.
    let flood = profile(
        "flood",
        "cmd.exe",
        &[
            "/c",
            "for",
            "/L",
            "%i",
            "in",
            &count,
            "do",
            "@echo",
            "%i",
            "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghij",
        ],
    );
    let opened = harness.open(&flood, dir.path(), 120, 30);
    // Nobody reads for a while: the buffer fills and the shell waits.
    std::thread::sleep(Duration::from_secs(2));
    let mut client = harness.attach(&opened);
    let output = harness.read_to_end(&mut client);
    assert!(output.len() > OUTPUT_LIMIT, "{} bytes", output.len());
    let seen: BTreeSet<u32> = plain(&output)
        .lines()
        .filter(|line| line.ends_with("ghij"))
        .filter_map(|line| line.split(' ').next()?.parse().ok())
        .collect();
    assert_eq!(seen.len(), 40_000, "every line arrived");
    assert_eq!(seen.first(), Some(&1));
    assert_eq!(seen.last(), Some(&lines));
    assert_eq!(harness.exit_code(opened.session_id), 0);
}

#[test]
fn the_byte_pipe_admits_only_the_current_user() {
    let dir = scratch("dacl");
    let harness = Harness::new();
    let opened = harness.open(&cmd(), dir.path(), 80, 25);
    let client = harness.attach(&opened);
    let stored = cabinetos_ipc::stored_security(&client.pipe).unwrap();
    println!("stored: {stored}");
    // One ACE: allow the current user full access; nothing inherited.
    assert!(stored.starts_with("D:P(A;;FA;;;"), "{stored}");
    assert_eq!(stored.matches('(').count(), 1, "{stored}");
    // The same descriptor as the control pipe's.
    let control = {
        let _entered = harness.runtime.enter();
        PipeServer::bind(&PipeName::random()).unwrap()
    };
    assert_eq!(stored, control.stored_security().unwrap());
}

#[test]
fn refusals_name_the_problem() {
    let dir = scratch("refusals");
    let harness = Harness::with_limit(2);
    let cwd = shown(dir.path());
    let open = |profile: &Profile, cwd: &str| {
        harness
            .terminals
            .open(profile, Some(cwd), 80, 25, Binding::default())
    };

    let missing = open(&profile("nope", "cabinetos-no-such-shell.exe", &[]), &cwd).unwrap_err();
    assert_eq!(missing.code, ErrorCode::SpawnFailed);
    assert!(
        missing.message.contains("not on the PATH"),
        "{}",
        missing.message
    );
    let no_folder = open(&cmd(), &shown(&dir.path().join("missing"))).unwrap_err();
    assert_eq!(no_folder.code, ErrorCode::SpawnFailed);
    assert!(
        no_folder.message.contains("no such folder"),
        "{}",
        no_folder.message
    );
    assert_eq!(
        open(&cmd(), "relative").unwrap_err().code,
        ErrorCode::SpawnFailed
    );

    let first = open(&cmd(), &cwd).unwrap();
    let _second = open(&cmd(), &cwd).unwrap();
    let full = open(&cmd(), &cwd).unwrap_err();
    assert_eq!(full.code, ErrorCode::SpawnFailed);
    assert!(
        full.message.contains("2 terminal sessions"),
        "{}",
        full.message
    );
    harness.terminals.close(first.session_id).unwrap();
    let third = open(&cmd(), &cwd).unwrap();
    assert!(third.session_id > first.session_id);

    let unknown = 9_999;
    for code in [
        harness.terminals.resize(unknown, 80, 25).unwrap_err().code,
        harness.terminals.close(unknown).unwrap_err().code,
        harness
            .terminals
            .set_mode(unknown, TerminalMode::Locked)
            .unwrap_err()
            .code,
    ] {
        assert_eq!(code, ErrorCode::NoSuchSession);
    }

    harness.terminals.shutdown();
    assert!(harness.terminals.list().is_empty());
    let late = open(&cmd(), &cwd).unwrap_err();
    assert_eq!(late.code, ErrorCode::SpawnFailed);
}

#[test]
fn powershell_reads_a_typed_folder_with_quotes_and_brackets_when_installed() {
    let mut tested = 0;
    for program in ["pwsh.exe", "powershell.exe"] {
        if !on_path(program) {
            println!("skipped {program}: it is not on the PATH");
            continue;
        }
        let dir = scratch("powershell");
        let harness = Harness::new();
        // A quote, a typographic quote and brackets: literal for
        // Set-Location -LiteralPath, a wildcard for plain Set-Location.
        let target = long_name(dir.path()).join("it's [1] ’q’");
        std::fs::create_dir(&target).unwrap();
        let shell = profile("ps", program, &["-NoLogo", "-NoProfile"]);
        let opened = harness.open(&shell, dir.path(), 300, 25);
        let mut client = harness.attach(&opened);
        harness.read_until(&mut client, |output| {
            output.contains("PS ") && at_prompt(output)
        });

        harness.cd_typed(&mut client, &opened, "Set-Location -LiteralPath ", &target);
        let prompt = format!("PS {}>", shown(&target));
        harness.read_until(&mut client, |output| output.contains(&prompt));
        client.forget();
        harness.send(&mut client, "Get-Location\r");
        harness.read_until(&mut client, |output| has_line(output, &shown(&target)));
        harness.send(&mut client, "exit\r");
        harness.read_to_end(&mut client);
        assert_eq!(harness.exit_code(opened.session_id), 0, "{program}");
        tested += 1;
    }
    println!("{tested} PowerShell version(s) tested");
}

/// Whether WSL has a Linux to run: `wsl.exe` may exist without one.
fn wsl_runs(folder: &Path) -> bool {
    if !on_path("wsl.exe") {
        println!("skipped: wsl.exe is not on the PATH");
        return false;
    }
    let Ok(mut probe) = Command::new("wsl.exe")
        .args(["-e", "sh", "-c", "exit 0"])
        .current_dir(folder)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    else {
        println!("skipped: wsl.exe did not start");
        return false;
    };
    let deadline = Instant::now() + DEADLINE;
    loop {
        match probe.try_wait() {
            Ok(Some(status)) if status.success() => return true,
            Ok(Some(status)) => {
                println!("skipped: WSL has no Linux to run ({status})");
                return false;
            }
            Ok(None) if Instant::now() < deadline => {
                std::thread::sleep(Duration::from_millis(100));
            }
            _ => {
                let _ = probe.kill();
                println!("skipped: WSL did not answer");
                return false;
            }
        }
    }
}

#[test]
fn wsl_reads_a_typed_folder_when_installed() {
    let dir = scratch("wsl");
    if !wsl_runs(dir.path()) {
        return;
    }
    let harness = Harness::new();
    let target = dir.path().join("it's here");
    std::fs::create_dir(&target).unwrap();
    let opened = harness.open(&profile("wsl", "wsl.exe", &[]), dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    // WSL starts in the same folder, seen from Linux.
    let folder = dir
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    let in_linux = |output: &str, tail: &str| {
        output
            .lines()
            .any(|line| line.starts_with("/mnt/") && line.trim_end().ends_with(tail))
    };
    harness.send(&mut client, "pwd\r");
    harness.read_until(&mut client, |output| in_linux(output, &folder));

    harness.cd_typed(&mut client, &opened, "cd ", &target);
    client.forget();
    harness.send(&mut client, "pwd\r");
    let tail = format!("{folder}/it's here");
    harness.read_until(&mut client, |output| in_linux(output, &tail));
    harness.send(&mut client, "exit\r");
    harness.read_to_end(&mut client);
    assert_eq!(harness.exit_code(opened.session_id), 0);
}

#[test]
fn wsl_translates_a_folder_beyond_ascii_when_installed() {
    let dir = scratch("wsl-names");
    if !wsl_runs(dir.path()) {
        return;
    }
    let harness = Harness::new();
    let target = dir.path().join(BEYOND_ASCII);
    std::fs::create_dir(&target).unwrap();
    let opened = harness.open(&profile("wsl", "wsl.exe", &[]), dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    let folder = dir
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    let in_linux = |output: &str, tail: &str| {
        output
            .lines()
            .any(|line| line.starts_with("/mnt/") && line.trim_end().ends_with(tail))
    };
    harness.send(&mut client, "pwd\r");
    harness.read_until(&mut client, |output| in_linux(output, &folder));

    harness.cd_typed(&mut client, &opened, "cd ", &target);
    client.forget();
    harness.send(&mut client, "pwd\r");
    let tail = format!("{folder}/{BEYOND_ASCII}");
    harness.read_until(&mut client, |output| in_linux(output, &tail));
    harness.send(&mut client, "exit\r");
    harness.read_to_end(&mut client);
    assert_eq!(harness.exit_code(opened.session_id), 0);
}

/// A session belongs to the pane it was opened for and keeps its mode until
/// a client changes it; a profile that is not linkable stays locked.
#[test]
fn a_session_has_a_pane_and_a_mode_and_only_a_linkable_one_may_be_linked() {
    let dir = scratch("modes");
    let harness = Harness::new();
    let cwd = shown(dir.path());
    let locked_cmd = cmd();
    assert!(!locked_cmd.linkable, "cmd is not linkable by default");
    let linkable_cmd = Profile {
        name: "cmd-hooked".to_owned(),
        linkable: true,
        ..cmd()
    };

    let open = |profile: &Profile, pane: Pane, mode: TerminalMode| {
        harness
            .terminals
            .open(profile, Some(&cwd), 80, 25, Binding { pane, mode })
    };

    let refused = open(&locked_cmd, Pane::Left, TerminalMode::Linked).unwrap_err();
    assert_eq!(refused.code, ErrorCode::NotLinkable);
    assert!(refused.message.contains("`cmd`"), "{}", refused.message);
    assert!(harness.terminals.list().is_empty(), "nothing started");

    let right = open(&locked_cmd, Pane::Right, TerminalMode::Locked).unwrap();
    let left = open(&linkable_cmd, Pane::Left, TerminalMode::Linked).unwrap();
    let described = |session_id: u64| {
        let listed = harness.terminals.list();
        let session = listed
            .iter()
            .find(|session| session.session_id == session_id)
            .unwrap();
        (session.pane, session.mode, session.linkable)
    };
    assert_eq!(
        described(right.session_id),
        (Pane::Right, TerminalMode::Locked, false)
    );
    assert_eq!(
        described(left.session_id),
        (Pane::Left, TerminalMode::Linked, true)
    );

    let not_linkable = harness
        .terminals
        .set_mode(right.session_id, TerminalMode::Linked)
        .unwrap_err();
    assert_eq!(not_linkable.code, ErrorCode::NotLinkable);
    // The same mode again is no change: nothing is sent.
    harness
        .terminals
        .set_mode(right.session_id, TerminalMode::Locked)
        .unwrap();
    assert!(harness.events.try_recv().is_err(), "no event for no change");

    harness
        .terminals
        .set_mode(left.session_id, TerminalMode::Locked)
        .unwrap();
    assert_eq!(
        harness.mode_change(),
        (left.session_id, TerminalMode::Locked)
    );
    assert_eq!(
        described(left.session_id),
        (Pane::Left, TerminalMode::Locked, true)
    );
    harness
        .terminals
        .set_mode(left.session_id, TerminalMode::Linked)
        .unwrap();
    assert_eq!(
        harness.mode_change(),
        (left.session_id, TerminalMode::Linked)
    );
    assert_eq!(
        harness
            .terminals
            .set_mode(left.session_id + 1000, TerminalMode::Linked)
            .unwrap_err()
            .code,
        ErrorCode::NoSuchSession
    );
}

/// A PowerShell profile's hook code runs before each prompt is drawn (here
/// it moves the shell, as the core's own hook does for a linked session),
/// the user's own prompt still draws, `$LASTEXITCODE` stays the last
/// command's, and every shell gets the core's pipe token. A profile whose
/// arguments already run a command gets no hook and still runs.
#[test]
fn powershell_runs_its_hook_at_each_prompt_when_installed() {
    let mut tested = 0;
    for program in ["pwsh.exe", "powershell.exe"] {
        if !on_path(program) {
            println!("skipped {program}: it is not on the PATH");
            continue;
        }
        let dir = scratch("powershell-hook");
        let harness = Harness::with_hooks();
        let target = long_name(dir.path()).join("Звіт 'проєкт' ’q’");
        std::fs::create_dir(&target).unwrap();
        let quoted = shown(&target).replace('\'', "''").replace('’', "’’");
        let shell = Profile {
            hook: Hook::Custom(format!("Set-Location -LiteralPath '{quoted}'")),
            ..profile("ps", program, &["-NoLogo", "-NoProfile"])
        };
        let opened = harness.open(&shell, dir.path(), 300, 25);
        let mut client = harness.attach(&opened);
        let prompt = format!("PS {}>", shown(&target));
        harness.read_until(&mut client, |output| output.contains(&prompt));

        client.forget();
        harness.send(&mut client, "cmd /c exit 3\r");
        harness.read_until(&mut client, |output| output.contains(&prompt));
        client.forget();
        harness.send(
            &mut client,
            "\"code $LASTEXITCODE pipe $env:CABINETOS_PIPE\"\r",
        );
        harness.read_until(&mut client, |output| {
            has_line(output, "code 3 pipe test-pipe")
        });
        harness.send(&mut client, "exit\r");
        harness.read_to_end(&mut client);
        assert_eq!(harness.exit_code(opened.session_id), 0, "{program}");

        // A command of the profile's own: no hook, the command runs.
        let once = profile(
            "once",
            program,
            &[
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "Write-Output hook-free",
            ],
        );
        let opened = harness.open(&once, dir.path(), 300, 25);
        let mut client = harness.attach(&opened);
        let output = plain(&harness.read_to_end(&mut client));
        assert!(has_line(&output, "hook-free"), "{program}: {output}");
        assert_eq!(harness.exit_code(opened.session_id), 0, "{program}");
        tested += 1;
    }
    println!("{tested} PowerShell version(s) tested");
}

/// WSL's bash gets its hook as `PROMPT_COMMAND` through `WSLENV`, with the
/// session's ID and the core's pipe token.
#[test]
fn wsl_runs_its_hook_at_each_prompt_when_installed() {
    let dir = scratch("wsl-hook");
    if !wsl_runs(dir.path()) {
        return;
    }
    let harness = Harness::with_hooks();
    let shell = Profile {
        hook: Hook::Custom("builtin cd /tmp".to_owned()),
        ..profile("wsl", "wsl.exe", &[])
    };
    let opened = harness.open(&shell, dir.path(), 300, 25);
    let mut client = harness.attach(&opened);
    harness.read_until(&mut client, |output| output.contains("/tmp"));
    client.forget();
    harness.send(
        &mut client,
        "echo \"in $PWD session $CABINETOS_SESSION pipe $CABINETOS_PIPE\"\r",
    );
    let wanted = format!("in /tmp session {} pipe test-pipe", opened.session_id);
    harness.read_until(&mut client, |output| has_line(output, &wanted));
    harness.send(&mut client, "exit\r");
    harness.read_to_end(&mut client);
    assert_eq!(harness.exit_code(opened.session_id), 0);
}
