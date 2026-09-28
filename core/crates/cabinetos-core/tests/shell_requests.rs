//! The requests the shell needs beyond listing (protocol version 8), end to
//! end: the real `cabinetos-core.exe` on a random pipe with a real client.
//! Every file and folder lives under `%TEMP%\cabinetos-core-test\`, in a
//! folder removed at the end.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, Event, Request, Response};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(5);

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
    fn config_path(&self) -> PathBuf {
        self.dir.path().join("config").join("cabinetos.json")
    }
}

fn start_core() -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("shell")
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

/// A client that said hello, so it receives the configuration events.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("shell-test").await.unwrap();
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

async fn get_value(client: &mut PipeClient, path: &str) -> Response {
    ask(
        client,
        Request::GetValue {
            path: path.to_owned(),
        },
    )
    .await
}

async fn set_value(client: &mut PipeClient, path: &str, value: Value) -> Response {
    ask(
        client,
        Request::SetValue {
            path: path.to_owned(),
            value,
        },
    )
    .await
}

/// The next `config_changed`; any other event fails the test.
async fn config_changed(events: &mut UnboundedReceiver<Envelope<Event>>) -> Vec<String> {
    let event = tokio::time::timeout(EVENT_DEADLINE, events.recv())
        .await
        .expect("no event in time")
        .expect("the event stream ended")
        .body;
    match event {
        Event::ConfigChanged { changed } => changed,
        other => panic!("expected config_changed, got {other:?}"),
    }
}

fn read_config(core: &Core) -> Value {
    serde_json::from_str(&std::fs::read_to_string(core.config_path()).unwrap()).unwrap()
}

#[tokio::test]
async fn every_drive_letter_is_listed_once_in_order() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let started = Instant::now();
    let reply = ask(&mut client, Request::ListVolumes).await;
    let elapsed = started.elapsed();
    let Response::Volumes { volumes } = reply else {
        panic!("expected volumes, got {reply:?}")
    };
    let letters: Vec<char> = volumes.iter().filter_map(|v| v.drive_letter).collect();
    assert_eq!(letters.len(), volumes.len(), "every volume has its letter");
    assert!(
        letters.windows(2).all(|pair| pair[0] < pair[1]),
        "{letters:?}"
    );
    let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
    let system = system.chars().next().unwrap().to_ascii_uppercase();
    let volume = volumes
        .iter()
        .find(|v| v.drive_letter == Some(system))
        .unwrap_or_else(|| panic!("no {system}: in {letters:?}"));
    assert_eq!(volume.filesystem, "NTFS");
    assert!(volume.total_bytes > 0 && volume.free_bytes <= volume.total_bytes);
    // The same fields as `volume_info` for the same drive (space may move).
    let Response::VolumeInfo(single) = ask(
        &mut client,
        Request::VolumeInfo {
            path: format!("{system}:\\"),
        },
    )
    .await
    else {
        panic!("expected volume_info")
    };
    assert_eq!(single.volume_guid_path, volume.volume_guid_path);
    assert_eq!(single.disk, volume.disk);
    // Network drives have 200 ms, local ones 2 s; they are asked at once.
    assert!(elapsed < Duration::from_secs(3), "{elapsed:?}");
    // Works before `hello` too, and again.
    assert!(matches!(
        ask(&mut client, Request::ListVolumes).await,
        Response::Volumes { .. }
    ));
}

#[tokio::test]
async fn open_path_refuses_what_it_cannot_open_without_starting_anything() {
    // A real open starts an application and leaves its window on the
    // desktop, so only the refusals are tested here.
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let open = |path: &str| Request::OpenPath {
        path: path.to_owned(),
    };
    let missing = core.dir.path().join("no-such-file.txt");
    let reply = ask(&mut client, open(missing.to_str().unwrap())).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotFound), "{reply:?}");
    // A name the shell might look up elsewhere (`notepad` on the PATH) is
    // never tried: only the path as given.
    let bare = core.dir.path().join("notepad");
    let reply = ask(&mut client, open(bare.to_str().unwrap())).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotFound), "{reply:?}");
    for relative in ["notes.txt", r"sub\notes.txt", ""] {
        let reply = ask(&mut client, open(relative)).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::InvalidPath),
            "{relative:?}: {reply:?}"
        );
    }
}

#[tokio::test]
async fn set_value_writes_one_setting_and_tells_every_client() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let (_other, mut other_events) = greeted(&core).await;
    assert_eq!(
        get_value(&mut client, "ui.dualPane").await,
        Response::Value { value: json!(true) }
    );
    assert_eq!(
        get_value(&mut client, "panes.sort").await,
        Response::Value {
            value: json!({"key": "name", "descending": false})
        }
    );

    assert_eq!(
        set_value(&mut client, "ui.dualPane", json!(false)).await,
        Response::Ok
    );
    assert_eq!(read_config(&core)["ui"]["dualPane"], json!(false));
    for events in [&mut events, &mut other_events] {
        assert_eq!(config_changed(events).await, ["ui.dualPane"]);
    }
    assert_eq!(
        get_value(&mut client, "ui.dualPane").await,
        Response::Value {
            value: json!(false)
        }
    );

    let folders = json!([r"C:\Users", r"D:\work"]);
    assert_eq!(
        set_value(&mut client, "ui.lastPaths", folders.clone()).await,
        Response::Ok
    );
    assert_eq!(config_changed(&mut events).await, ["ui.lastPaths"]);
    assert_eq!(read_config(&core)["ui"]["lastPaths"], folders);
    assert_eq!(
        get_value(&mut client, "ui.lastPaths").await,
        Response::Value { value: folders }
    );
    // The same value again changes nothing and says nothing.
    assert_eq!(
        set_value(&mut client, "ui.dualPane", json!(false)).await,
        Response::Ok
    );
    // The watcher sees the core's own writes and must not report them.
    tokio::time::sleep(Duration::from_millis(600)).await;
    assert!(
        events.try_recv().is_err(),
        "the core's own write came back as an event"
    );
}

#[tokio::test]
async fn a_value_the_file_could_not_hold_is_refused_and_the_file_kept() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    // The core creates the file at start; wait until it is there.
    assert!(matches!(
        get_value(&mut client, "ui").await,
        Response::Value { .. }
    ));
    let before = std::fs::read(core.config_path()).unwrap();
    for (path, value, says) in [
        ("ui.dualPane", json!("yes"), "expected a boolean"),
        ("ui.nope", json!(1), "no setting `ui.nope`"),
        ("ui.pinned", json!(r"D:\work"), "expected a sequence"),
        ("terminal.defaultProfile", json!("fish"), "fish"),
        ("version", json!(2), "version 2"),
    ] {
        let reply = set_value(&mut client, path, value).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::ConfigError),
            "{path}: {reply:?}"
        );
        let Response::Error { message, .. } = &reply else {
            unreachable!()
        };
        assert!(message.contains(says), "{path}: {message}");
    }
    assert_eq!(std::fs::read(core.config_path()).unwrap(), before);
    for path in ["ui.nope", "ui.dualPane.deeper", ""] {
        let reply = get_value(&mut client, path).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::ConfigError),
            "{path}: {reply:?}"
        );
    }
}
