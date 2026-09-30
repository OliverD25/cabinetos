//! In-app updates end to end: the real `cabinetos-core.exe`, once as the
//! development build it is (which never updates itself), and once copied
//! into a fake install folder with `release.json`, where its daily check
//! finds a newer version in a feed folder, downloads it, and the swap moves
//! the running core's own program aside. The Apps entry is a test key;
//! nothing here reaches the network or the real install.

use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_fs::registry::{self, RegValue};
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, Request, Response, UpdatePhase, UpdateStatus,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const VERSION: &str = env!("CARGO_PKG_VERSION");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// The daily check looks 10 seconds after the start; a debug build is slow.
const DAILY_DEADLINE: Duration = Duration::from_secs(60);
/// A version newer than any this workspace will have.
const NEWER: &str = "99.0.0";

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

fn release_json(version: &str) -> String {
    json!({"product": "CabinetOS", "version": version}).to_string()
}

fn release_zip(version: &str) -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let release = release_json(version);
    for (name, bytes) in [
        ("release.json", release.as_bytes()),
        ("CabinetOS.exe", b"the new window".as_slice()),
        ("cabinetos-core.exe", b"the new core".as_slice()),
        ("uninstall.ps1", b"# the uninstaller".as_slice()),
    ] {
        writer
            .start_file(name, zip::write::SimpleFileOptions::default())
            .unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

struct Core {
    child: Child,
    pipe: PipeName,
    dir: TempDir,
    apps_key: String,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = registry::delete_user_key(&self.apps_key);
    }
}

impl Core {
    fn path(&self, name: &str) -> PathBuf {
        self.dir.path().join(name)
    }
}

fn scratch() -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("update")
        .tempdir_in(root)
        .unwrap()
}

/// Starts `exe` with its own folders under `dir`, `config` as its
/// configuration, and the Apps entry at a test key.
fn start(dir: TempDir, exe: &Path, config: &Value, name: &str) -> Core {
    let config_path = dir.path().join("cabinetos.json");
    fs::write(&config_path, serde_json::to_string_pretty(config).unwrap()).unwrap();
    let apps_key = format!(
        r"Software\CabinetOS-test-core-update-{}-{name}",
        std::process::id()
    );
    let pipe = PipeName::random();
    let child = Command::new(exe)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .arg("--plugins-dir")
        .arg(dir.path().join("plugins"))
        .arg("--plugins-data-dir")
        .arg(dir.path().join("plugins-data"))
        .arg("--themes-dir")
        .arg(dir.path().join("themes"))
        .arg("--tools-dir")
        .arg(dir.path().join("tools"))
        .arg("--marketplace-dir")
        .arg(dir.path().join("marketplace"))
        .arg("--update-dir")
        .arg(dir.path().join("update"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
        .env("CABINETOS_UPDATE_APPS_KEY", &apps_key)
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core {
        child,
        pipe,
        dir,
        apps_key,
    }
}

async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let deadline = Instant::now() + STARTUP_DEADLINE;
    let mut client = loop {
        match PipeClient::connect(&core.pipe, Duration::from_secs(1)).await {
            Ok(client) => break client,
            Err(_) if Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) => panic!("the core's pipe did not appear: {error}"),
        }
    };
    let events = client.events().unwrap();
    let welcome = client.hello("update-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn state_of(reply: Response) -> UpdateStatus {
    match reply {
        Response::UpdateState(status) => *status,
        other => panic!("expected update_state, got {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_development_build_never_updates_itself() {
    let dir = scratch();
    let core = start(dir, Path::new(CORE_EXE), &json!({}), "development");
    let (mut client, _events) = greeted(&core).await;
    let status = state_of(ask(&mut client, Request::UpdateStatus).await);
    assert_eq!(status.state, UpdatePhase::NotUpdatable);
    assert!(status.reason.unwrap().contains("development build"));
    assert_eq!(status.current, VERSION);
    assert_eq!(status.install_dir, None);
    for request in [
        Request::UpdateCheck,
        Request::UpdateApply,
        Request::UpdateSnooze,
    ] {
        match ask(&mut client, request).await {
            Response::Error { code, message } => {
                assert_eq!(code, ErrorCode::UpdateError);
                assert!(message.contains("development build"), "{message}");
            }
            other => panic!("expected an error, got {other:?}"),
        }
    }
    assert!(!core.path("update").exists(), "nothing was written");
}

/// The next `update_state_changed` whose state is `wanted`, and the
/// progress seen on the way.
async fn wait_for(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    wanted: UpdatePhase,
    progress: &mut Vec<(u64, u64)>,
) -> UpdateStatus {
    let deadline = tokio::time::Instant::now() + DAILY_DEADLINE;
    loop {
        let event = tokio::time::timeout_at(deadline, events.recv())
            .await
            .unwrap_or_else(|_| panic!("no update_state_changed to {wanted:?} in time"))
            .expect("the event stream ended");
        match event.body {
            Event::UpdateStateChanged(status) if status.state == wanted => return *status,
            Event::UpdateStateChanged(status) if status.state == UpdatePhase::Failed => {
                panic!("the update failed: {:?}", status.message)
            }
            Event::UpdateProgress { bytes, total, .. } => progress.push((bytes, total)),
            _ => {}
        }
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn the_daily_check_downloads_and_the_core_swaps_itself_and_rolls_back() {
    let dir = scratch();
    let install = dir.path().join("install");
    fs::create_dir_all(&install).unwrap();
    let core_bytes = fs::read(CORE_EXE).unwrap();
    fs::write(install.join("cabinetos-core.exe"), &core_bytes).unwrap();
    fs::write(install.join("CabinetOS.exe"), b"the old window").unwrap();
    fs::write(install.join("release.json"), release_json(VERSION)).unwrap();

    let feed = dir.path().join("feed").join("stable");
    fs::create_dir_all(feed.join("files")).unwrap();
    let zip = release_zip(NEWER);
    fs::write(feed.join("files").join("release.zip"), &zip).unwrap();
    fs::write(
        feed.join(format!("notes-{NEWER}.md")),
        format!("## [{NEWER}] - 2026-10-01\n\n- **Updates** from inside the app.\n"),
    )
    .unwrap();
    let latest = json!({
        "schemaVersion": 1,
        "channel": "stable",
        "version": NEWER,
        "published": "2026-10-01",
        "zip": {"url": "files/release.zip", "sha256": sha256(&zip), "size": zip.len()},
        "notes": {"url": format!("notes-{NEWER}.md")}
    });
    fs::write(feed.join("latest.json"), latest.to_string()).unwrap();
    let config = json!({"update": {"source": dir.path().join("feed").display().to_string()}});
    let apps_install = format!("{}\\", install.display());

    let core = start(dir, &install.join("cabinetos-core.exe"), &config, "swap");
    registry::write_user_values(
        &core.apps_key,
        &[
            ("DisplayVersion", RegValue::Text(VERSION)),
            ("InstallLocation", RegValue::Text(&apps_install)),
        ],
    )
    .unwrap();
    let (mut client, mut events) = greeted(&core).await;
    let first = state_of(ask(&mut client, Request::UpdateStatus).await);
    assert_eq!(
        first.install_dir.as_deref(),
        Some(install.display().to_string().as_str())
    );

    // The daily check finds the newer version and downloads it.
    let mut progress = Vec::new();
    let downloaded = wait_for(&mut events, UpdatePhase::Downloaded, &mut progress).await;
    assert_eq!(downloaded.latest.unwrap().version, NEWER);
    assert!(downloaded.notes.unwrap().contains("**Updates**"));
    assert_eq!(progress.last(), Some(&(zip.len() as u64, zip.len() as u64)));
    let status = state_of(ask(&mut client, Request::UpdateStatus).await);
    assert_eq!(status.state, UpdatePhase::Downloaded);
    assert!(status.checked_at_ms.is_some());

    // The swap: the running core's own program goes into previous\.
    let applied = state_of(ask(&mut client, Request::UpdateApply).await);
    assert_eq!(applied.state, UpdatePhase::Ready);
    assert_eq!(applied.installed.as_deref(), Some(NEWER));
    assert_eq!(applied.previous.as_deref(), Some(VERSION));
    assert_eq!(
        fs::read(install.join("cabinetos-core.exe")).unwrap(),
        b"the new core"
    );
    assert_eq!(
        fs::read(install.join("previous").join("cabinetos-core.exe")).unwrap(),
        core_bytes
    );
    assert!(
        matches!(ask(&mut client, Request::Ping).await, Response::Pong { .. }),
        "the core still runs"
    );
    assert_eq!(
        registry::read_user_string(&core.apps_key, "DisplayVersion").as_deref(),
        Some(NEWER)
    );

    let rolled = state_of(ask(&mut client, Request::UpdateRollback).await);
    assert_eq!(rolled.previous, None);
    assert_eq!(
        fs::read(install.join("cabinetos-core.exe")).unwrap(),
        core_bytes
    );
    assert_eq!(
        fs::read(install.join("CabinetOS.exe")).unwrap(),
        b"the old window"
    );
    assert!(!install.join("previous").exists());
    assert_eq!(
        registry::read_user_string(&core.apps_key, "DisplayVersion").as_deref(),
        Some(VERSION)
    );

    // Later: no dialog for a day.
    let snoozed = state_of(ask(&mut client, Request::UpdateSnooze).await);
    assert!(snoozed.snoozed_until_ms.is_some());
}
