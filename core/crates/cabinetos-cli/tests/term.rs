//! `cabinetos-cli term` against a real core: a shell fed from a pipe;
//! `term list`, `term cd` and `term close` from a second CLI while the
//! first is attached; and `term` in a console window, which here is a
//! pseudo-console of this test's own, so the test types the keys. The
//! shells run only `echo`, `cd`, `mode con` (which prints the console's
//! size) and `exit`, in folders under `%TEMP%\cabinetos-term-test\`, which
//! the tests remove.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::sync::{Arc, Mutex, mpsc};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use cabinetos_protocol::Event;
use cabinetos_terminal::{EventSink, Opened, Profile, Terminals};
use tempfile::TempDir;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const DEADLINE: Duration = Duration::from_secs(30);

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-term-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

fn shown(path: &Path) -> String {
    path.display().to_string()
}

struct Core {
    child: Child,
    pipe: PipeName,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn core_exe() -> PathBuf {
    let path = Path::new(CLI_EXE).with_file_name("cabinetos-core.exe");
    assert!(
        path.exists(),
        "{} is missing; build cabinetos-core first",
        path.display()
    );
    path
}

fn start_core(dir: &Path) -> Core {
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.join("plugins"))
        .env("CABINETOS_PLUGINS_DATA_DIR", dir.join("plugins-data"))
        .env("CABINETOS_THEMES_DIR", dir.join("themes"))
        .env(
            "CABINETOS_INDEXER_PIPE",
            format!(
                r"\\.\pipe\cabinetos-indexer-none-{}",
                PipeName::random().token()
            ),
        )
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let core = Core { child, pipe };
    let deadline = Instant::now() + DEADLINE;
    while !cli(&core, &["ping"]).status.success() {
        assert!(Instant::now() < deadline, "the core did not answer");
        std::thread::sleep(Duration::from_millis(50));
    }
    core
}

fn cli_command(core: &Core, args: &[&str]) -> Command {
    let mut command = Command::new(CLI_EXE);
    command
        .args(["--pipe", core.pipe.token()])
        .args(args)
        .env_remove("CABINETOS_LOG");
    command
}

fn cli(core: &Core, args: &[&str]) -> Output {
    cli_command(core, args).output().unwrap()
}

fn text(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).into_owned()
}

/// A `term` CLI whose standard input stays open, with its output collected
/// as it arrives.
struct Attached {
    child: Child,
    stdin: Option<std::process::ChildStdin>,
    stdout: Arc<Mutex<Vec<u8>>>,
    stderr: Arc<Mutex<Vec<u8>>>,
}

impl Drop for Attached {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn collect(mut from: impl Read + Send + 'static) -> Arc<Mutex<Vec<u8>>> {
    let collected = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&collected);
    std::thread::spawn(move || {
        let mut buffer = [0u8; 4096];
        while let Ok(read) = from.read(&mut buffer) {
            if read == 0 {
                break;
            }
            sink.lock().unwrap().extend_from_slice(&buffer[..read]);
        }
    });
    collected
}

impl Attached {
    fn start(core: &Core, args: &[&str]) -> Self {
        let mut child = cli_command(core, args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let stdin = child.stdin.take();
        let stdout = collect(child.stdout.take().unwrap());
        let stderr = collect(child.stderr.take().unwrap());
        Self {
            child,
            stdin,
            stdout,
            stderr,
        }
    }

    fn wait_for_output(&self, wanted: &str) {
        let deadline = Instant::now() + DEADLINE;
        loop {
            let seen = text(&self.stdout.lock().unwrap());
            if seen.contains(wanted) {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "{wanted:?} did not appear; stdout:\n{seen}\nstderr:\n{}",
                text(&self.stderr.lock().unwrap())
            );
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    /// Waits for the CLI to exit; returns its standard error.
    fn finish(&mut self) -> String {
        drop(self.stdin.take());
        let deadline = Instant::now() + DEADLINE;
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                // The collecting threads may still hold the last bytes.
                std::thread::sleep(Duration::from_millis(100));
                let stderr = text(&self.stderr.lock().unwrap());
                assert!(status.success(), "the CLI failed ({status}):\n{stderr}");
                return stderr;
            }
            assert!(Instant::now() < deadline, "the CLI did not exit");
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

#[test]
fn piped_input_runs_in_the_shell_and_the_exit_code_is_printed() {
    let dir = scratch("cli");
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut term = Attached::start(&core, &["term", "--profile", "cmd", "--cwd", &cwd]);
    // The caret keeps the typed line from containing the answer.
    term.stdin
        .as_mut()
        .unwrap()
        .write_all(b"echo a^bc\r\nexit 5\r\n")
        .unwrap();
    let stderr = term.finish();
    let stdout = text(&term.stdout.lock().unwrap());
    assert!(stdout.contains(&format!("{cwd}>")), "{stdout}");
    // Only the echo's output contains `abc`; the typed line shows `a^bc`.
    assert!(stdout.contains("abc"), "{stdout}");
    assert!(
        !stderr.contains("Ctrl+]"),
        "no keys to detach with: {stderr}"
    );
    assert!(stderr.contains("ended; exit code 5"), "{stderr}");
}

#[test]
fn list_cd_and_close_act_on_a_running_session() {
    let dir = scratch("cli");
    let target = dir.path().join("later");
    std::fs::create_dir(&target).unwrap();
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut term = Attached::start(&core, &["term", "--profile", "cmd", "--cwd", &cwd]);
    term.wait_for_output(&format!("{cwd}>"));

    let listed = cli(&core, &["term", "list"]);
    assert!(listed.status.success(), "{}", text(&listed.stderr));
    let line = text(&listed.stdout);
    let id = line.split(' ').next().unwrap().to_owned();
    assert!(
        line.trim_end()
            .ends_with(&format!(" running attached {cwd}")),
        "{line}"
    );
    assert!(line.contains(" cmd pid "), "{line}");

    let moved = cli(&core, &["term", "cd", &id, &shown(&target)]);
    assert!(moved.status.success(), "{}", text(&moved.stderr));
    assert_eq!(
        text(&moved.stdout).trim_end(),
        format!("session {id}: cd {}", shown(&target))
    );
    term.wait_for_output(&format!("{}>", shown(&target)));

    let closed = cli(&core, &["term", "close", &id]);
    assert!(closed.status.success(), "{}", text(&closed.stderr));
    assert_eq!(
        text(&closed.stdout).trim_end(),
        format!("session {id} closed")
    );
    let stderr = term.finish();
    assert!(
        stderr.contains(&format!("session {id} ended; exit code")),
        "{stderr}"
    );

    let listed = cli(&core, &["term", "list"]);
    assert_eq!(text(&listed.stdout), "no terminal sessions\n");
    let refused = cli(&core, &["term", "close", &id]);
    assert!(!refused.status.success());
    assert!(
        text(&refused.stderr).contains("no_such_session"),
        "{}",
        text(&refused.stderr)
    );
}

/// The output as text, without VT sequences and control characters; a
/// cursor move to a row becomes a line break, a move to the right a space.
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
                    for c in chars.by_ref() {
                        if c == '\u{7}' {
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

/// `cabinetos-cli term` as the program of a session of this test's own:
/// its console is that session's pseudo-console, which the test types into
/// and reads, as a person at a console window would.
struct InConsole {
    // Declared first: it closes its session before the runtime stops.
    terminals: Terminals,
    events: mpsc::Receiver<Event>,
    opened: Opened,
    pipe: NamedPipeClient,
    seen: Vec<u8>,
    runtime: tokio::runtime::Runtime,
}

impl InConsole {
    fn start(core: &Core, cwd: &str) -> Self {
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .unwrap();
        let (sender, events) = mpsc::channel();
        let sink: EventSink = Arc::new(move |event| {
            let _ = sender.send(event);
        });
        let terminals = Terminals::new(runtime.handle().clone(), sink);
        let cli = Profile {
            name: "cli".to_owned(),
            command: CLI_EXE.to_owned(),
            args: [
                "--pipe",
                core.pipe.token(),
                "term",
                "--profile",
                "cmd",
                "--cwd",
                cwd,
            ]
            .iter()
            .map(|arg| (*arg).to_owned())
            .collect(),
        };
        let opened = terminals.open(&cli, Some(cwd), 120, 30).unwrap();
        let pipe = runtime
            .block_on(async { ClientOptions::new().open(&opened.pipe) })
            .unwrap();
        Self {
            terminals,
            events,
            opened,
            pipe,
            seen: Vec::new(),
            runtime,
        }
    }

    fn type_keys(&mut self, keys: &[u8]) {
        self.runtime.block_on(self.pipe.write_all(keys)).unwrap();
    }

    /// Reads the console until `done` holds for what it shows since the
    /// last [`forget`](Self::forget).
    fn read_until(&mut self, done: impl Fn(&str) -> bool) -> String {
        let Self {
            runtime,
            pipe,
            seen,
            ..
        } = self;
        runtime.block_on(async {
            let deadline = tokio::time::Instant::now() + DEADLINE;
            let mut buffer = vec![0; 64 * 1024];
            loop {
                let shown = plain(seen);
                if done(&shown) {
                    return shown;
                }
                match tokio::time::timeout_at(deadline, pipe.read(&mut buffer)).await {
                    Ok(Ok(0)) => panic!("the console's output ended first:\n{shown}"),
                    Ok(Ok(read)) => seen.extend_from_slice(&buffer[..read]),
                    Ok(Err(error)) => panic!("reading the console failed: {error}\n{shown}"),
                    Err(elapsed) => panic!("{elapsed}; the console so far:\n{shown}"),
                }
            }
        })
    }

    fn forget(&mut self) {
        self.seen.clear();
    }

    /// The CLI's exit code, once it exits.
    fn exit_code(&self) -> u32 {
        let deadline = Instant::now() + DEADLINE;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            match self.events.recv_timeout(left) {
                Ok(Event::TerminalExited {
                    session_id,
                    exit_code,
                }) if session_id == self.opened.session_id => return exit_code,
                Ok(_) => {}
                Err(error) => panic!("the CLI did not exit: {error}"),
            }
        }
    }
}

/// The session ID from the CLI's first line, `cabinetos-cli: session 3, …`.
fn announced_session(console: &str) -> String {
    let (_, rest) = console
        .split_once("cabinetos-cli: session ")
        .unwrap_or_else(|| panic!("no session line in:\n{console}"));
    rest.chars().take_while(char::is_ascii_digit).collect()
}

/// Waits until the core lists the session with this size.
fn wait_for_size(core: &Core, id: &str, cols: u16, rows: u16) {
    let wanted = format!("{id} cmd pid ");
    let size = format!(" {cols}x{rows} ");
    let deadline = Instant::now() + DEADLINE;
    loop {
        let listed = text(&cli(core, &["term", "list"]).stdout);
        if listed
            .lines()
            .any(|line| line.starts_with(&wanted) && line.contains(&size))
        {
            return;
        }
        assert!(Instant::now() < deadline, "never {size}:\n{listed}");
        std::thread::sleep(Duration::from_millis(50));
    }
}

#[test]
fn in_a_console_keys_resizes_and_ctrl_bracket_reach_the_session() {
    let dir = scratch("cli");
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut console = InConsole::start(&core, &cwd);
    let screen = console.read_until(|shown| {
        shown.contains("Ctrl+] detaches") && shown.contains(&format!("{cwd}>"))
    });
    let id = announced_session(&screen);

    // Only the echo's output contains `abc`; the typed line shows `a^bc`.
    console.type_keys(b"echo a^bc\r");
    console.read_until(|shown| shown.contains("abc"));
    console.type_keys(b"mode con\r");
    console.read_until(|shown| columns(shown).contains(&120));

    // The CLI follows its console's size.
    console.forget();
    console
        .terminals
        .resize(console.opened.session_id, 100, 30)
        .unwrap();
    wait_for_size(&core, &id, 100, 30);
    console.type_keys(b"mode con\r");
    console.read_until(|shown| columns(shown).contains(&100));

    console.type_keys(&[0x1d]);
    console.read_until(|shown| shown.contains(&format!("detached; session {id} keeps running")));
    assert_eq!(console.exit_code(), 0);
    let listed = text(&cli(&core, &["term", "list"]).stdout);
    assert!(listed.starts_with(&format!("{id} cmd pid ")), "{listed}");
    assert!(listed.contains(" running detached "), "{listed}");
    assert!(cli(&core, &["term", "close", &id]).status.success());
}

#[test]
fn in_a_console_the_shell_exit_ends_the_cli() {
    let dir = scratch("cli");
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut console = InConsole::start(&core, &cwd);
    console.read_until(|shown| shown.contains(&format!("{cwd}>")));
    console.type_keys(b"exit 4\r");
    console.read_until(|shown| shown.contains("ended; exit code 4"));
    assert_eq!(console.exit_code(), 0);
    let listed = text(&cli(&core, &["term", "list"]).stdout);
    assert!(listed.contains(" exited(4) "), "{listed}");
}
