//! The foundations of Phase 14 (protocol version 13), end to end: the real
//! `cabinetos-core.exe` on a random pipe with real clients. Every file and
//! folder lives under `%TEMP%\cabinetos-core-test\`, in a folder removed at
//! the end.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, Pane, PaneState, Request, Response, WindowPanes, WindowState,
    WindowTab,
};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

/// A running core, killed at the end of the test, with its own folder for
/// the configuration, the logs and the test's files.
struct Core {
    child: Child,
    pipe: PipeName,
    dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

impl Core {
    /// A folder for the test's files and folders.
    #[allow(dead_code)]
    fn files(&self) -> PathBuf {
        let files = self.dir.path().join("files");
        std::fs::create_dir_all(&files).unwrap();
        files
    }
}

fn start_core() -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("phase14")
        .tempdir_in(root)
        .unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("config").join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        // Never the real plugins folder, whatever is installed there.
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe, dir }
}

async fn connect(pipe: &PipeName) -> PipeClient {
    let deadline = Instant::now() + STARTUP_DEADLINE;
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

/// A client that said hello, so it receives events.
async fn greeted(core: &Core, name: &str) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello(name).await.unwrap();
    assert!(
        matches!(welcome.body, Response::Welcome { .. }),
        "{welcome:?}"
    );
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn error_code(response: &Response) -> Option<ErrorCode> {
    match response {
        Response::Error { code, .. } => Some(*code),
        _ => None,
    }
}

fn window(active_pane: Pane, folder: &str) -> WindowState {
    WindowState {
        active_pane,
        panes: WindowPanes {
            left: PaneState {
                tabs: vec![WindowTab {
                    path: folder.to_owned(),
                    locked: false,
                    tool: None,
                }],
                active: 0,
                cursor: Some(format!(r"{folder}\a.txt")),
                marked: vec![format!(r"{folder}\a.txt"), format!(r"{folder}\b.txt")],
            },
            right: PaneState::default(),
        },
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_window_state_is_kept_per_client_until_it_leaves() {
    let core = start_core();
    let mut cli = connect(&core.pipe).await;
    // Nobody said anything yet; a named client that does not exist neither.
    let reply = ask(&mut cli, Request::GetWindowState { client: None }).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NoWindow), "{reply:?}");

    // A state needs hello: the core names the client by it.
    let reply = ask(&mut cli, Request::WindowState(window(Pane::Left, r"C:\x"))).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ProtocolError),
        "{reply:?}"
    );

    let (mut first, _first_events) = greeted(&core, "window-test").await;
    let (mut second, _second_events) = greeted(&core, "window-test").await;
    let sent = window(Pane::Right, r"C:\first");
    assert_eq!(
        ask(&mut first, Request::WindowState(sent.clone())).await,
        Response::Ok
    );
    let Response::WindowState {
        client: first_id,
        sent_at_ms,
        state,
    } = ask(&mut cli, Request::GetWindowState { client: None }).await
    else {
        panic!("no state")
    };
    assert_eq!(state, sent, "kept exactly as sent");
    assert!(first_id.starts_with("window-test#"), "{first_id}");
    assert!(sent_at_ms > 1_700_000_000_000, "{sent_at_ms}");

    // The newest state wins; the first stays reachable by its name.
    assert_eq!(
        ask(
            &mut second,
            Request::WindowState(window(Pane::Left, r"C:\second"))
        )
        .await,
        Response::Ok
    );
    let Response::WindowState {
        client: second_id,
        state,
        ..
    } = ask(&mut cli, Request::GetWindowState { client: None }).await
    else {
        panic!("no state")
    };
    assert_ne!(second_id, first_id);
    assert_eq!(state.panes.left.tabs[0].path, r"C:\second");
    let Response::WindowState { state, .. } = ask(
        &mut cli,
        Request::GetWindowState {
            client: Some(first_id.clone()),
        },
    )
    .await
    else {
        panic!("no state")
    };
    assert_eq!(state.panes.left.tabs[0].path, r"C:\first");

    // A client that leaves takes its state along.
    drop(second);
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        let Response::WindowState { client, .. } =
            ask(&mut cli, Request::GetWindowState { client: None }).await
        else {
            panic!("the first window's state is gone too")
        };
        if client == first_id {
            break;
        }
        assert!(Instant::now() < deadline, "the second state stayed");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    let reply = ask(
        &mut cli,
        Request::GetWindowState {
            client: Some(second_id),
        },
    )
    .await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NoWindow), "{reply:?}");
    drop(first);
}
