//! Terminal sessions end to end with the real `cabinetos-core.exe`: the
//! requests, the byte pipe, `terminal_exited`, a session that outlives its
//! connection, and the shells ending with the core. The shells run only
//! `echo`, `cd` and `exit`, in folders under `%TEMP%\cabinetos-term-test\`,
//! which the tests remove.

use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, Event, Request, Response, TerminalState};
use tempfile::TempDir;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const DEADLINE: Duration = Duration::from_secs(30);

/// Two profiles: an interactive cmd (the default) and one that prints a
/// line and exits with code 3.
const CONFIG: &str = r#"{
  "terminal": {
    "defaultProfile": "cmd",
    "profiles": [
      { "name": "cmd", "command": "cmd.exe" },
      { "name": "once", "command": "cmd.exe", "args": ["/c", "echo", "hello-from-conpty", "&&", "exit", "3"] }
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
        Request::TerminalSyncCwd {
            session_id: 999,
            path: shown(dir.path()),
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
    } = reply
    else {
        panic!("expected terminal_opened, got {reply:?}");
    };
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
    let target = dir.path().join("later");
    std::fs::create_dir(&target).unwrap();
    let mut core = start_core(dir.path());

    // The first client opens the default profile, then goes away.
    let (session_id, pipe, pid) = {
        let mut client = connect(&core.pipe).await;
        let reply = ask(&mut client, open(None, dir.path(), 200)).await;
        let Response::TerminalOpened {
            session_id,
            pipe,
            pid,
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

    let synced = ask(
        &mut client,
        Request::TerminalSyncCwd {
            session_id,
            path: shown(&target),
        },
    )
    .await;
    assert_eq!(synced, Response::Ok);
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
    read_until(&mut output, &mut seen, &format!("{}>", shown(&target))).await;
    output.write_all(b"echo fr^esh\r").await.unwrap();
    read_until(&mut output, &mut seen, "\nfresh").await;
    let Response::TerminalSessions { sessions } = ask(&mut client, Request::TerminalList).await
    else {
        panic!("expected terminal_sessions");
    };
    assert_eq!((sessions[0].cols, sessions[0].rows), (180, 30));
    assert_eq!(sessions[0].cwd, shown(&target));
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
