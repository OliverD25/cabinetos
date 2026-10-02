//! Terminal sessions end to end with the real `cabinetos-core.exe`: the
//! requests, the byte pipe, `terminal_exited`, the panes and modes with
//! `terminal_mode_changed`, the pane's folder a prompt hook asks for
//! (`terminal_pane_folder`), a session that outlives its connection, and the
//! shells ending with the core. The shells run only `echo`, `cd` and
//! `exit`, in folders under `%TEMP%\cabinetos-term-test\`, which the tests
//! remove.

use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, Pane, PaneState, Request, Response, TerminalMode, TerminalState,
    WindowPanes, WindowState, WindowTab,
};
use tempfile::TempDir;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const DEADLINE: Duration = Duration::from_secs(30);

/// Three profiles: an interactive cmd (the default, not linkable), one that
/// prints a line and exits with code 3, and a cmd that says it is linkable.
const CONFIG: &str = r#"{
  "terminal": {
    "defaultProfile": "cmd",
    "profiles": [
      { "name": "cmd", "command": "cmd.exe" },
      { "name": "once", "command": "cmd.exe", "args": ["/c", "echo", "hello-from-conpty", "&&", "exit", "3"] },
      { "name": "hooked", "command": "cmd.exe", "linkable": true }
    ]
  }
}"#;

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

fn start_core(dir: &Path) -> Core {
    let config = dir.join("cabinetos.json");
    std::fs::write(&config, CONFIG).unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config)
        .env("CABINETOS_LOG_DIR", dir.join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.join("plugins"))
        .env("CABINETOS_PLUGINS_DATA_DIR", dir.join("plugins-data"))
        .env("CABINETOS_THEMES_DIR", dir.join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.join("undo"))
        .env("CABINETOS_INDEXER_PIPE", r"\\.\pipe\cabinetos-indexer-none")
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe }
}

async fn connect(pipe: &PipeName) -> PipeClient {
    let deadline = Instant::now() + DEADLINE;
    loop {
        match PipeClient::connect(pipe, Duration::from_secs(1)).await {
            Ok(client) => return client,
            Err(_) if Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) => panic!("the core's pipe did not appear: {error}"),
        }
    }
}

/// Connects and says `hello`, so the events arrive.
async fn connect_with_events(pipe: &PipeName) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(pipe).await;
    let welcome = client.hello("terminal-test").await.unwrap().body;
    assert!(matches!(welcome, Response::Welcome { .. }), "{welcome:?}");
    let events = client.events().unwrap();
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn open(profile: Option<&str>, cwd: &Path, cols: u16) -> Request {
    Request::TerminalOpen {
        profile: profile.map(str::to_owned),
        cwd: Some(shown(cwd)),
        cols,
        rows: 25,
        pane: Pane::Left,
        mode: None,
    }
}

/// The next `terminal_mode_changed`, skipping other events.
async fn mode_change(events: &mut UnboundedReceiver<Envelope<Event>>) -> (u64, TerminalMode) {
    let deadline = tokio::time::Instant::now() + DEADLINE;
    loop {
        match tokio::time::timeout_at(deadline, events.recv()).await {
            Ok(Some(Envelope {
                body: Event::TerminalModeChanged { session_id, mode },
                ..
            })) => return (session_id, mode),
            Ok(Some(_)) => {}
            Ok(None) => panic!("the events ended"),
            Err(elapsed) => panic!("no terminal_mode_changed: {elapsed}"),
        }
    }
}

fn error_code(reply: &Response) -> ErrorCode {
    match reply {
        Response::Error { code, .. } => *code,
        other => panic!("expected an error, got {other:?}"),
    }
}

async fn exit_code(events: &mut UnboundedReceiver<Envelope<Event>>, session_id: u64) -> u32 {
    let deadline = tokio::time::Instant::now() + DEADLINE;
    loop {
        match tokio::time::timeout_at(deadline, events.recv()).await {
            Ok(Some(Envelope {
                body:
                    Event::TerminalExited {
                        session_id: exited,
                        exit_code,
                    },
                ..
            })) if exited == session_id => return exit_code,
            Ok(Some(_)) => {}
            Ok(None) => panic!("the events ended"),
            Err(elapsed) => panic!("no terminal_exited for session {session_id}: {elapsed}"),
        }
    }
}

async fn attach(pipe: &str) -> NamedPipeClient {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match ClientOptions::new().open(pipe) {
            Ok(client) => return client,
            // ERROR_PIPE_BUSY: another client is still attached.
            Err(error) if error.raw_os_error() == Some(231) && Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            Err(error) => panic!("cannot attach to {pipe}: {error}"),
        }
    }
}

/// The output as text, without VT sequences and control characters; a
/// cursor move to a row becomes a line break.
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
                            if c == 'H' {
                                plain.push('\n');
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

async fn read_until(pipe: &mut NamedPipeClient, seen: &mut Vec<u8>, wanted: &str) {
    let deadline = tokio::time::Instant::now() + DEADLINE;
    let mut buffer = vec![0; 64 * 1024];
    while !plain(seen).contains(wanted) {
        match tokio::time::timeout_at(deadline, pipe.read(&mut buffer)).await {
            Ok(Ok(0)) => panic!("the output ended before {wanted:?}:\n{}", plain(seen)),
            Ok(Ok(read)) => seen.extend_from_slice(&buffer[..read]),
            Ok(Err(error)) => panic!("reading failed: {error}"),
            Err(elapsed) => panic!("{elapsed} waiting for {wanted:?}:\n{}", plain(seen)),
        }
    }
}

async fn read_to_end(pipe: &mut NamedPipeClient) -> Vec<u8> {
    let mut all = Vec::new();
    tokio::time::timeout(DEADLINE, pipe.read_to_end(&mut all))
        .await
        .expect("the output did not end")
        .expect("reading failed");
    all
}

/// Whether a process with this ID runs (read-only `tasklist`).
fn running(pid: u32) -> bool {
    let output = Command::new("tasklist")
        .args(["/FI", &format!("PID eq {pid}"), "/FO", "CSV", "/NH"])
        .output()
        .unwrap();
    String::from_utf8_lossy(&output.stdout).contains(&format!("\"{pid}\""))
}

#[tokio::test]
async fn terminal_requests_work_over_the_pipe() {
    let dir = scratch("core");
    let core = start_core(dir.path());
    let (mut client, mut events) = connect_with_events(&core.pipe).await;

    let unknown = ask(&mut client, open(Some("fish"), dir.path(), 80)).await;
    assert_eq!(error_code(&unknown), ErrorCode::UnknownProfile);
    let missing = ask(&mut client, open(None, &dir.path().join("missing"), 80)).await;
    assert_eq!(error_code(&missing), ErrorCode::SpawnFailed);
    for request in [
        Request::TerminalResize {
            session_id: 999,
            cols: 80,
            rows: 25,
        },
        Request::TerminalClose { session_id: 999 },
        Request::TerminalSetMode {
            session_id: 999,
            mode: TerminalMode::Locked,
        },
    ] {
        let reply = ask(&mut client, request).await;
        assert_eq!(error_code(&reply), ErrorCode::NoSuchSession);
    }

    let reply = ask(&mut client, open(Some("once"), dir.path(), 80)).await;
    let Response::TerminalOpened {
        session_id,
        pipe,
        pid,
        mode,
        linkable,
    } = reply
    else {
        panic!("expected terminal_opened, got {reply:?}");
    };
    assert_eq!((mode, linkable), (TerminalMode::Locked, false));
    assert!(pipe.starts_with(r"\\.\pipe\cabinetos-term-"), "{pipe}");
    assert!(pid > 0);
    let mut output = attach(&pipe).await;
    let text = plain(&read_to_end(&mut output).await);
    assert!(text.contains("hello-from-conpty"), "{text}");
    assert_eq!(exit_code(&mut events, session_id).await, 3);

    let Response::TerminalSessions { sessions } = ask(&mut client, Request::TerminalList).await
    else {
        panic!("expected terminal_sessions");
    };
    assert_eq!(sessions.len(), 1);
    assert_eq!(sessions[0].session_id, session_id);
    assert_eq!(sessions[0].profile, "once");
    assert_eq!(sessions[0].state, TerminalState::Exited { code: 3 });
    assert_eq!(sessions[0].pipe, pipe);
    assert_eq!(sessions[0].pane, Pane::Left);
    assert_eq!(sessions[0].mode, TerminalMode::Locked);
    assert!(!sessions[0].linkable);
    assert_eq!(
        ask(&mut client, Request::TerminalClose { session_id }).await,
        Response::Ok
    );
    let Response::TerminalSessions { sessions } = ask(&mut client, Request::TerminalList).await
    else {
        panic!("expected terminal_sessions");
    };
    assert!(sessions.is_empty(), "{sessions:?}");
}

#[tokio::test]
async fn a_session_outlives_its_connection_and_ends_with_the_core() {
    let dir = scratch("core");
    let mut core = start_core(dir.path());

    // The first client opens the default profile, then goes away.
    let (session_id, pipe, pid) = {
        let mut client = connect(&core.pipe).await;
        let reply = ask(&mut client, open(None, dir.path(), 200)).await;
        let Response::TerminalOpened {
            session_id,
            pipe,
            pid,
            ..
        } = reply
        else {
            panic!("expected terminal_opened, got {reply:?}");
        };
        let mut output = attach(&pipe).await;
        let mut seen = Vec::new();
        read_until(&mut output, &mut seen, &format!("{}>", shown(dir.path()))).await;
        (session_id, pipe, pid)
    };

    // A new connection finds it running, with nobody attached.
    let (mut client, _events) = connect_with_events(&core.pipe).await;
    let deadline = Instant::now() + DEADLINE;
    loop {
        let Response::TerminalSessions { sessions } = ask(&mut client, Request::TerminalList).await
        else {
            panic!("expected terminal_sessions");
        };
        let session = sessions
            .iter()
            .find(|session| session.session_id == session_id)
            .expect("the session is listed");
        assert_eq!(session.state, TerminalState::Running);
        assert_eq!(session.profile, "cmd");
        if !session.attached {
            break;
        }
        assert!(Instant::now() < deadline, "still attached: {session:?}");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    // The resize makes the pseudo-console paint its screen again while
    // nobody is attached: that output waits for the next client.
    let resized = ask(
        &mut client,
        Request::TerminalResize {
            session_id,
            cols: 180,
            rows: 30,
        },
    )
    .await;
    assert_eq!(resized, Response::Ok);

    // Reattached: what the shell printed meanwhile, then fresh output.
    let mut output = attach(&pipe).await;
    let mut seen = Vec::new();
    read_until(&mut output, &mut seen, &format!("{}>", shown(dir.path()))).await;
    output.write_all(b"echo fr^esh\r").await.unwrap();
    read_until(&mut output, &mut seen, "\nfresh").await;
    let Response::TerminalSessions { sessions } = ask(&mut client, Request::TerminalList).await
    else {
        panic!("expected terminal_sessions");
    };
    assert_eq!((sessions[0].cols, sessions[0].rows), (180, 30));
    assert_eq!(sessions[0].cwd, shown(dir.path()));
    assert!(sessions[0].attached);

    // The core closes its sessions when it stops.
    assert_eq!(ask(&mut client, Request::Shutdown).await, Response::Ok);
    drop(client);
    let deadline = Instant::now() + DEADLINE;
    while core.child.try_wait().unwrap().is_none() {
        assert!(Instant::now() < deadline, "the core did not exit");
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
    read_to_end(&mut output).await;
    let deadline = Instant::now() + Duration::from_secs(5);
    while running(pid) {
        assert!(
            Instant::now() < deadline,
            "the shell {pid} outlived the core"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

/// Each listed session's ID, pane, mode and whether it is linkable.
async fn bindings(client: &mut PipeClient) -> Vec<(u64, Pane, TerminalMode, bool)> {
    let Response::TerminalSessions { sessions } = ask(client, Request::TerminalList).await else {
        panic!("expected terminal_sessions");
    };
    sessions
        .iter()
        .map(|session| {
            (
                session.session_id,
                session.pane,
                session.mode,
                session.linkable,
            )
        })
        .collect()
}

/// A session opened for a pane keeps that pane; its mode changes by
/// `terminal_set_mode` from any connection, and every connection that said
/// `hello` hears of it. A profile that is not linkable is refused `linked`,
/// at the open and later.
#[tokio::test]
async fn sessions_have_a_pane_and_a_mode_and_every_client_hears_a_change() {
    let dir = scratch("core-modes");
    let core = start_core(dir.path());
    let (mut first, mut first_events) = connect_with_events(&core.pipe).await;
    let (mut second, mut second_events) = connect_with_events(&core.pipe).await;

    let open_for = |profile: &str, pane: Pane, mode: Option<TerminalMode>| Request::TerminalOpen {
        profile: Some(profile.to_owned()),
        cwd: Some(shown(dir.path())),
        cols: 80,
        rows: 25,
        pane,
        mode,
    };
    let refused = ask(
        &mut first,
        open_for("cmd", Pane::Right, Some(TerminalMode::Linked)),
    )
    .await;
    assert_eq!(error_code(&refused), ErrorCode::NotLinkable);

    let reply = ask(
        &mut first,
        open_for("hooked", Pane::Right, Some(TerminalMode::Linked)),
    )
    .await;
    let Response::TerminalOpened {
        session_id: hooked,
        mode,
        linkable,
        ..
    } = reply
    else {
        panic!("expected terminal_opened, got {reply:?}");
    };
    assert_eq!((mode, linkable), (TerminalMode::Linked, true));
    let reply = ask(&mut first, open_for("cmd", Pane::Left, None)).await;
    let Response::TerminalOpened {
        session_id: plain, ..
    } = reply
    else {
        panic!("expected terminal_opened, got {reply:?}");
    };

    assert_eq!(
        bindings(&mut second).await,
        [
            (hooked, Pane::Right, TerminalMode::Linked, true),
            (plain, Pane::Left, TerminalMode::Locked, false),
        ]
    );

    // The second connection locks the first one's session; both hear it.
    let set_mode =
        |session_id: u64, mode: TerminalMode| Request::TerminalSetMode { session_id, mode };
    let locked = ask(&mut second, set_mode(hooked, TerminalMode::Locked)).await;
    assert_eq!(locked, Response::Ok);
    for events in [&mut first_events, &mut second_events] {
        assert_eq!(mode_change(events).await, (hooked, TerminalMode::Locked));
    }
    let not_linkable = ask(&mut first, set_mode(plain, TerminalMode::Linked)).await;
    assert_eq!(error_code(&not_linkable), ErrorCode::NotLinkable);
    assert_eq!(
        bindings(&mut first).await,
        [
            (hooked, Pane::Right, TerminalMode::Locked, true),
            (plain, Pane::Left, TerminalMode::Locked, false),
        ]
    );
    for session_id in [hooked, plain] {
        assert_eq!(
            ask(&mut first, Request::TerminalClose { session_id }).await,
            Response::Ok
        );
    }
}

/// A `window_state` whose panes show `left` and `right` (each the tab in
/// front of two).
fn window_showing(left: &str, right: &str) -> Request {
    let pane = |front: &str| PaneState {
        tabs: vec![
            WindowTab {
                path: r"C:\Windows".to_owned(),
                ..WindowTab::default()
            },
            WindowTab {
                path: front.to_owned(),
                ..WindowTab::default()
            },
        ],
        active: 1,
        ..PaneState::default()
    };
    Request::WindowState(WindowState {
        active_pane: Pane::Left,
        panes: WindowPanes {
            left: pane(left),
            right: pane(right),
        },
        dual: true,
    })
}

/// `terminal_pane_folder`, as a prompt hook's `cab term cwd` asks it: on a
/// connection without `hello`, the session's pane and mode and the folder
/// its pane shows in the newest `window_state`. No window yet, or the
/// window gone: no folder. A locked session gets the folder too, with its
/// mode; the command line prints it only for a linked one.
#[tokio::test]
async fn the_pane_folder_comes_from_the_newest_window_state() {
    let dir = scratch("core-pane-folder");
    let core = start_core(dir.path());
    let (mut window, _events) = connect_with_events(&core.pipe).await;
    let reply = ask(
        &mut window,
        Request::TerminalOpen {
            profile: Some("hooked".to_owned()),
            cwd: Some(shown(dir.path())),
            cols: 80,
            rows: 25,
            pane: Pane::Right,
            mode: Some(TerminalMode::Linked),
        },
    )
    .await;
    let Response::TerminalOpened { session_id, .. } = reply else {
        panic!("expected terminal_opened, got {reply:?}");
    };
    let mut hook = connect(&core.pipe).await;
    let folder_of = |mode: TerminalMode, folder: Option<&str>| Response::TerminalPaneFolder {
        session_id,
        pane: Pane::Right,
        mode,
        folder: folder.map(str::to_owned),
    };
    let ask_folder = Request::TerminalPaneFolder { session_id };
    assert_eq!(
        ask(&mut hook, ask_folder.clone()).await,
        folder_of(TerminalMode::Linked, None),
        "no window has said what it shows"
    );

    let right = r"D:\Звіт 'проєкт' $HOME";
    assert_eq!(
        ask(&mut window, window_showing(r"E:\left", right)).await,
        Response::Ok
    );
    assert_eq!(
        ask(&mut hook, ask_folder.clone()).await,
        folder_of(TerminalMode::Linked, Some(right))
    );
    let locked = Request::TerminalSetMode {
        session_id,
        mode: TerminalMode::Locked,
    };
    assert_eq!(ask(&mut window, locked).await, Response::Ok);
    assert_eq!(
        ask(&mut hook, ask_folder.clone()).await,
        folder_of(TerminalMode::Locked, Some(right))
    );
    let unknown = ask(
        &mut hook,
        Request::TerminalPaneFolder {
            session_id: session_id + 100,
        },
    )
    .await;
    assert_eq!(error_code(&unknown), ErrorCode::NoSuchSession);

    // The window leaves: the core forgets what it showed.
    drop(window);
    let deadline = Instant::now() + DEADLINE;
    while ask(&mut hook, ask_folder.clone()).await != folder_of(TerminalMode::Locked, None) {
        assert!(Instant::now() < deadline, "the window's state stayed");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    assert_eq!(
        ask(&mut hook, Request::TerminalClose { session_id }).await,
        Response::Ok
    );
}
