//! `cabinetos-cli term` against a real core: a shell fed from a pipe;
//! `term list`, `term mode`, `term type` and `term close` from a second CLI
//! while the first is attached; `term cwd`, which a prompt hook runs, with
//! this test as the window that says what its panes show; real PowerShell
//! and WSL shells whose prompt hook follows that window's pane; and `term`
//! in a console window, which here is a pseudo-console of this test's own,
//! so the test types the keys. The
//! shells run only `echo`, `cd`, `mode con` (which prints the console's
//! size) and `exit`, in folders under `%TEMP%\cabinetos-term-test\`, which
//! the tests remove; typed paths are never run.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::sync::{Arc, Mutex, mpsc};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, Event, Pane, PaneState, Request, Response, TerminalMode, WindowPanes, WindowState,
    WindowTab,
};
use cabinetos_terminal::{Binding, EventSink, Hook, Opened, Profile, Terminals};
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
        .env("CABINETOS_UNDO_DIR", dir.join("undo"))
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
fn list_mode_and_close_act_on_a_running_session() {
    let dir = scratch("cli");
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
    // Without --pane the session belongs to the left pane, locked.
    assert!(line.contains(" cmd left locked pid "), "{line}");

    // cmd is not linkable: no prompt hook can be added to it.
    let refused = cli(&core, &["term", "mode", &id, "linked"]);
    assert!(!refused.status.success());
    assert!(
        text(&refused.stderr).contains("not_linkable"),
        "{}",
        text(&refused.stderr)
    );
    let locked = cli(&core, &["term", "mode", &id, "locked"]);
    assert!(locked.status.success(), "{}", text(&locked.stderr));
    assert_eq!(
        text(&locked.stdout).trim_end(),
        format!("session {id}: locked")
    );

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

#[test]
fn a_session_opened_for_the_right_pane_can_be_linked_when_its_profile_is_linkable() {
    let dir = scratch("cli-mode");
    std::fs::write(
        dir.path().join("cabinetos.json"),
        r#"{"terminal": {"defaultProfile": "hooked", "profiles": [
            {"name": "hooked", "command": "cmd.exe", "linkable": true}
        ]}}"#,
    )
    .unwrap();
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut term = Attached::start(&core, &["term", "--pane", "right", "--cwd", &cwd]);
    term.wait_for_output(&format!("{cwd}>"));
    let listed = cli(&core, &["term", "list"]);
    let line = text(&listed.stdout);
    let id = line.split(' ').next().unwrap().to_owned();
    assert!(line.contains(" hooked right locked pid "), "{line}");

    let linked = cli(&core, &["term", "mode", &id, "linked"]);
    assert!(linked.status.success(), "{}", text(&linked.stderr));
    assert_eq!(
        text(&linked.stdout).trim_end(),
        format!("session {id}: linked")
    );
    let line = text(&cli(&core, &["term", "list"]).stdout);
    assert!(line.contains(" hooked right linked pid "), "{line}");
    let closed = cli(&core, &["term", "close", &id]);
    assert!(closed.status.success(), "{}", text(&closed.stderr));
    term.finish();
}

#[test]
fn type_puts_quoted_paths_at_the_prompt_without_enter() {
    let dir = scratch("cli-type");
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut term = Attached::start(&core, &["term", "--profile", "cmd", "--cwd", &cwd]);
    term.wait_for_output(&format!("{cwd}>"));
    let listed = cli(&core, &["term", "list"]);
    assert!(listed.status.success(), "{}", text(&listed.stderr));
    let id = text(&listed.stdout).split(' ').next().unwrap().to_owned();

    let first = shown(&dir.path().join("a b.txt"));
    let second = shown(&dir.path().join("100%PATH% & x"));
    let typed = cli(&core, &["term", "type", &id, &first, &second]);
    assert!(typed.status.success(), "{}", text(&typed.stderr));
    assert_eq!(
        text(&typed.stdout).trim_end(),
        format!("session {id}: typed 2 paths")
    );
    // What cmd shows is what it received: the paths, quoted. (cmd draws a
    // VT sequence between its prompt and them; the terminal crate's test
    // finds them on the prompt line.)
    term.wait_for_output(&format!(r#""{first}" "{cwd}\100"%^P"ATH"%^ "& x""#));

    let refused = cli(&core, &["term", "type", "999999", &first]);
    assert!(!refused.status.success());
    assert!(
        text(&refused.stderr).contains("no_such_session"),
        "{}",
        text(&refused.stderr)
    );
    let closed = cli(&core, &["term", "close", &id]);
    assert!(closed.status.success(), "{}", text(&closed.stderr));
    term.finish();
}

/// The window this test plays: it says `hello` and what its panes show,
/// and hears the core's events.
struct Window {
    client: PipeClient,
    events: tokio::sync::mpsc::UnboundedReceiver<Envelope<Event>>,
    /// Folder reports heard and not looked at yet: session and folder.
    reports: Vec<(u64, String)>,
    runtime: tokio::runtime::Runtime,
}

impl Window {
    fn connect(core: &Core) -> Self {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let (client, events) = runtime.block_on(async {
            let mut client = PipeClient::connect(&core.pipe, DEADLINE).await.unwrap();
            client.hello("term-cwd-test").await.unwrap();
            let events = client.events().unwrap();
            (client, events)
        });
        Self {
            client,
            events,
            reports: Vec::new(),
            runtime,
        }
    }

    fn ask(&mut self, request: Request) -> Response {
        let Self {
            client, runtime, ..
        } = self;
        runtime.block_on(client.request(request)).unwrap().body
    }

    /// Waits for `terminal_folder_changed` of `session` with `folder`;
    /// returns its reports until then, oldest first. Other sessions'
    /// reports wait for their own turn.
    fn folder_reported(&mut self, session: u64, folder: &str) -> Vec<String> {
        let mut reported = Vec::new();
        let deadline = Instant::now() + DEADLINE;
        loop {
            if let Some(at) = self.reports.iter().position(|(id, _)| *id == session) {
                let (_, got) = self.reports.remove(at);
                reported.push(got.clone());
                if got == folder {
                    return reported;
                }
                continue;
            }
            let Self {
                events, runtime, ..
            } = self;
            let left = deadline.saturating_duration_since(Instant::now());
            match runtime.block_on(async { tokio::time::timeout(left, events.recv()).await }) {
                Ok(Some(Envelope {
                    body: Event::TerminalFolderChanged { session_id, folder },
                    ..
                })) => self.reports.push((session_id, folder)),
                Ok(Some(_)) => {}
                Ok(None) => panic!("the events ended"),
                Err(timeout) => panic!("no report of {folder} ({timeout}); reported: {reported:?}"),
            }
        }
    }

    /// The left pane shows `left`, the right pane `right`.
    fn show(&mut self, left: &str, right: &str) {
        let pane = |path: &str| PaneState {
            tabs: vec![WindowTab {
                path: path.to_owned(),
                ..WindowTab::default()
            }],
            ..PaneState::default()
        };
        let state = WindowState {
            active_pane: Pane::Left,
            panes: WindowPanes {
                left: pane(left),
                right: pane(right),
            },
        };
        assert_eq!(self.ask(Request::WindowState(state)), Response::Ok);
    }

    fn open(&mut self, cwd: &str, pane: Pane, mode: TerminalMode) -> u64 {
        self.open_shell("hooked", cwd, pane, mode).0
    }

    /// Opens a session of `profile`; its ID and its byte pipe.
    fn open_shell(
        &mut self,
        profile: &str,
        cwd: &str,
        pane: Pane,
        mode: TerminalMode,
    ) -> (u64, String) {
        let reply = self.ask(Request::TerminalOpen {
            profile: Some(profile.to_owned()),
            cwd: Some(cwd.to_owned()),
            cols: 300,
            rows: 25,
            pane,
            mode: Some(mode),
        });
        let Response::TerminalOpened {
            session_id, pipe, ..
        } = reply
        else {
            panic!("expected terminal_opened, got {reply:?}");
        };
        (session_id, pipe)
    }

    fn attach(&mut self, pipe: &str) -> Shell {
        let pipe = self
            .runtime
            .block_on(async { ClientOptions::new().open(pipe) })
            .unwrap();
        Shell {
            pipe,
            seen: Vec::new(),
        }
    }

    fn send(&mut self, shell: &mut Shell, keys: &str) {
        self.runtime
            .block_on(shell.pipe.write_all(keys.as_bytes()))
            .unwrap();
    }

    /// Reads the shell until its prompt hook reported a folder (OSC 9;9):
    /// the shell has drawn a prompt.
    fn wait_for_report(&mut self, shell: &mut Shell) {
        self.read_while(shell, |_, seen| {
            !seen.windows(5).any(|bytes| bytes == b"]9;9;")
        });
    }

    /// Reads the shell until `done` holds for its plain text since the last
    /// [`Shell::forget`]; returns that text.
    fn read_until(&mut self, shell: &mut Shell, done: impl Fn(&str) -> bool) -> String {
        self.read_while(shell, |output, _| !done(output))
    }

    /// Reads while `waiting` holds for the plain text and the bytes since
    /// the last [`Shell::forget`]; returns the text.
    fn read_while(&mut self, shell: &mut Shell, waiting: impl Fn(&str, &[u8]) -> bool) -> String {
        let Shell { pipe, seen } = shell;
        self.runtime.block_on(async {
            let deadline = tokio::time::Instant::now() + DEADLINE;
            let mut buffer = vec![0; 64 * 1024];
            loop {
                let output = plain(seen);
                if !waiting(&output, seen) {
                    return output;
                }
                match tokio::time::timeout_at(deadline, pipe.read(&mut buffer)).await {
                    Ok(Ok(0)) => panic!("the output ended; it was:\n{output}"),
                    Ok(Ok(read)) => seen.extend_from_slice(&buffer[..read]),
                    Ok(Err(error)) => panic!("reading failed: {error}; the output:\n{output}"),
                    Err(elapsed) => panic!(
                        "{elapsed}; the output so far:\n{output}\nas bytes: {}",
                        String::from_utf8_lossy(seen).escape_debug()
                    ),
                }
            }
        })
    }
}

/// A session's byte pipe as the window reads it.
struct Shell {
    pipe: NamedPipeClient,
    seen: Vec<u8>,
}

impl Shell {
    /// Later reads look only at output from now on.
    fn forget(&mut self) {
        self.seen.clear();
    }
}

/// Whether `program` is in one of the `PATH`'s folders.
fn on_path(program: &str) -> bool {
    std::env::var_os("PATH")
        .is_some_and(|path| std::env::split_paths(&path).any(|dir| dir.join(program).is_file()))
}

/// Whether one line of the output is exactly `wanted`.
fn has_line(output: &str, wanted: &str) -> bool {
    output.lines().any(|line| line.trim_end() == wanted)
}

/// `path` spelled with long names, as PowerShell prints it (`%TEMP%` may
/// contain a short name).
fn long_name(path: &Path) -> PathBuf {
    let full = std::fs::canonicalize(path).unwrap();
    let full = full.to_string_lossy();
    PathBuf::from(full.strip_prefix(r"\\?\").unwrap_or(&full))
}

/// A core whose profile `shell` runs `program` with `args`, and a folder
/// of the test's own.
fn core_with_shell(prefix: &str, program: &str, args: &[&str]) -> (TempDir, Core) {
    let dir = scratch(prefix);
    let config = serde_json::json!({"terminal": {"defaultProfile": "shell", "profiles": [
        {"name": "shell", "command": program, "args": args}
    ]}});
    std::fs::write(dir.path().join("cabinetos.json"), config.to_string()).unwrap();
    let core = start_core(dir.path());
    (dir, core)
}

/// A linked PowerShell follows its pane at its next prompt: the prompt hook
/// asks `term cwd` and changes folder before the prompt is drawn. A
/// half-typed line is left alone: nothing is typed into it, and it runs
/// where its prompt was drawn; the next prompt follows. A `cd` of the
/// user's own stays until the pane moves again. A locked session stays
/// where it is. Both PowerShell versions, when installed.
#[test]
fn a_linked_powershell_follows_its_pane_at_the_next_prompt_when_installed() {
    let mut tested = 0;
    for program in ["pwsh.exe", "powershell.exe"] {
        if !on_path(program) {
            println!("skipped {program}: it is not on the PATH");
            continue;
        }
        let (dir, core) = core_with_shell("cli-follow", program, &["-NoLogo", "-NoProfile"]);
        let root = long_name(dir.path());
        let folder = |name: &str| {
            let path = root.join(name);
            std::fs::create_dir(&path).unwrap();
            shown(&path)
        };
        let start = folder("start");
        let first = folder("Звіт 'проєкт' $HOME ’q’");
        let second = folder("b [1]");
        let own = folder("own");
        let third = folder("c");
        let prompt = |folder: &str| format!("PS {folder}>");

        let mut window = Window::connect(&core);
        window.show(&first, &third);
        let (linked, linked_pipe) =
            window.open_shell("shell", &start, Pane::Left, TerminalMode::Linked);
        let (locked, locked_pipe) =
            window.open_shell("shell", &start, Pane::Right, TerminalMode::Locked);
        let mut left = window.attach(&linked_pipe);
        let mut right = window.attach(&locked_pipe);
        // The first prompt: the linked shell is in its pane's folder already,
        // and each shell reported its folder.
        window.read_until(&mut left, |output| output.contains(&prompt(&first)));
        window.read_until(&mut right, |output| output.contains(&prompt(&start)));
        assert_eq!(
            window.folder_reported(linked, &first),
            std::slice::from_ref(&first)
        );
        assert_eq!(
            window.folder_reported(locked, &start),
            std::slice::from_ref(&start)
        );

        // A half-typed line; the pane moves; then Enter.
        left.forget();
        window.send(&mut left, "echo half-typed; (Get-Location).Path");
        window.read_until(&mut left, |output| output.contains("(Get-Location).Path"));
        window.show(&second, &third);
        window.send(&mut left, "\r");
        let ran = window.read_until(&mut left, |output| output.contains(&prompt(&second)));
        assert!(
            has_line(&ran, "half-typed") && has_line(&ran, &first),
            "{program}: the line ran as typed, where its prompt was drawn:\n{ran}"
        );
        assert_eq!(
            window.folder_reported(linked, &second),
            std::slice::from_ref(&second)
        );

        // The user's own cd stays while the pane stays.
        left.forget();
        window.send(&mut left, &format!("Set-Location -LiteralPath '{own}'\r"));
        window.read_until(&mut left, |output| output.contains(&prompt(&own)));
        assert_eq!(
            window.folder_reported(linked, &own),
            std::slice::from_ref(&own)
        );
        left.forget();
        window.send(&mut left, "\r");
        let stayed = window.read_until(&mut left, |output| output.contains(&prompt(&own)));
        assert!(!stayed.contains(&prompt(&second)), "{program}:\n{stayed}");
        // The pane moves; `(Get-Location).Path` is typed while the hook
        // runs for the empty line's prompt, and arrives whole after it.
        window.show(&third, &third);
        left.forget();
        window.send(&mut left, "\r(Get-Location).Path\r");
        window.read_until(&mut left, |output| has_line(output, &third));
        assert_eq!(
            window.folder_reported(linked, &third),
            std::slice::from_ref(&third)
        );

        // The locked session's pane moves; it stays.
        right.forget();
        window.send(&mut right, "\r");
        let stayed = window.read_until(&mut right, |output| output.contains(&prompt(&start)));
        assert!(!stayed.contains(&prompt(&third)), "{program}:\n{stayed}");

        for session_id in [linked, locked] {
            assert_eq!(
                window.ask(Request::TerminalClose { session_id }),
                Response::Ok
            );
        }
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
    let probe = Command::new("wsl.exe")
        .args(["-e", "sh", "-c", "exit 0"])
        .current_dir(folder)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status();
    match probe {
        Ok(status) if status.success() => true,
        other => {
            println!("skipped: WSL has no Linux to run ({other:?})");
            false
        }
    }
}

/// A linked WSL shell (bash) follows its pane at its next prompt through
/// `PROMPT_COMMAND`, which `WSLENV` passes into Linux; `pwd` shows the
/// pane's folder seen from Linux.
#[test]
fn a_linked_wsl_shell_follows_its_pane_at_the_next_prompt_when_installed() {
    let (dir, core) = core_with_shell("cli-follow-wsl", "wsl.exe", &[]);
    if !wsl_runs(dir.path()) {
        return;
    }
    let start = dir.path().join("start");
    let target = dir.path().join("Звіт 'проєкт' $HOME");
    for folder in [&start, &target] {
        std::fs::create_dir(folder).unwrap();
    }
    let mut window = Window::connect(&core);
    window.show(&shown(&start), &shown(&start));
    let (session_id, pipe) =
        window.open_shell("shell", &shown(&start), Pane::Left, TerminalMode::Linked);
    let mut shell = window.attach(&pipe);
    let in_linux = |output: &str, tail: &str| {
        output
            .lines()
            .any(|line| line.starts_with("/mnt/") && line.trim_end().ends_with(tail))
    };
    window.wait_for_report(&mut shell);
    window.send(&mut shell, "pwd\r");
    window.read_until(&mut shell, |output| in_linux(output, "/start"));
    window.show(&shown(&target), &shown(&start));
    shell.forget();
    // `pwd` is typed while the hook runs for the empty line's prompt.
    window.send(&mut shell, "\rpwd\r");
    window.read_until(&mut shell, |output| {
        in_linux(output, "/Звіт 'проєкт' $HOME")
    });
    // WSL reports its folder as Windows names it.
    let reported = window.folder_reported(session_id, &shown(&target));
    assert_eq!(reported.last(), Some(&shown(&target)));
    window.send(&mut shell, "exit\r");
    let closed = window.ask(Request::TerminalClose { session_id });
    assert!(
        matches!(closed, Response::Ok | Response::Error { .. }),
        "{closed:?}"
    );
}

/// `term cwd` prints the folder of a linked session's pane, one line, and
/// nothing for a locked session or before a window said what it shows. It
/// takes the session and the pipe from `CABINETOS_SESSION` and
/// `CABINETOS_PIPE`, as in a CabinetOS terminal, or from `--session` and
/// `--pipe`.
#[test]
fn term_cwd_prints_the_folder_a_linked_session_follows() {
    let dir = scratch("cli-cwd");
    std::fs::write(
        dir.path().join("cabinetos.json"),
        r#"{"terminal": {"defaultProfile": "hooked", "profiles": [
            {"name": "hooked", "command": "cmd.exe", "linkable": true}
        ]}}"#,
    )
    .unwrap();
    let core = start_core(dir.path());
    let cwd = shown(dir.path());
    let mut window = Window::connect(&core);
    let linked = window.open(&cwd, Pane::Right, TerminalMode::Linked);
    let locked = window.open(&cwd, Pane::Left, TerminalMode::Locked);
    let ask = |session: u64| cli(&core, &["term", "cwd", "--session", &session.to_string()]);
    let printed = |output: &Output| {
        assert!(output.status.success(), "{}", text(&output.stderr));
        text(&output.stdout)
    };
    assert_eq!(printed(&ask(linked)), "", "no window state yet");

    let right = shown(&dir.path().join("Звіт 'проєкт' $HOME"));
    window.show(r"E:\left", &right);
    assert_eq!(printed(&ask(linked)), format!("{right}\n"));
    assert_eq!(
        printed(&ask(locked)),
        "",
        "a locked session follows nothing"
    );

    // In a CabinetOS terminal: no options, the variables the core set.
    let from_env = Command::new(CLI_EXE)
        .args(["term", "cwd"])
        .env("CABINETOS_SESSION", linked.to_string())
        .env("CABINETOS_PIPE", core.pipe.token())
        .env_remove("CABINETOS_LOG")
        .output()
        .unwrap();
    assert_eq!(printed(&from_env), format!("{right}\n"));

    let unknown = ask(linked + locked + 100);
    assert!(!unknown.status.success());
    assert!(
        text(&unknown.stderr).contains("no_such_session"),
        "{}",
        text(&unknown.stderr)
    );
    let nowhere = Command::new(CLI_EXE)
        .args(["--pipe", core.pipe.token(), "term", "cwd"])
        .env_remove("CABINETOS_SESSION")
        .env_remove("CABINETOS_LOG")
        .output()
        .unwrap();
    assert!(!nowhere.status.success());
    assert!(
        text(&nowhere.stderr).contains("CABINETOS_SESSION is not set"),
        "{}",
        text(&nowhere.stderr)
    );
    for session_id in [linked, locked] {
        assert_eq!(
            window.ask(Request::TerminalClose { session_id }),
            Response::Ok
        );
    }
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
            linkable: false,
            hook: Hook::Off,
        };
        let opened = terminals
            .open(&cli, Some(cwd), 120, 30, Binding::default())
            .unwrap();
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
    let wanted = format!("{id} cmd left locked pid ");
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
    assert!(
        listed.starts_with(&format!("{id} cmd left locked pid ")),
        "{listed}"
    );
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
