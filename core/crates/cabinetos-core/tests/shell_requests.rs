//! The requests the shell needs beyond listing (protocol version 8), end to
//! end: the real `cabinetos-core.exe` on a random pipe with a real client.
//! Every file and folder lives under `%TEMP%\cabinetos-core-test\`, in a
//! folder removed at the end.

use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Request, Response};
use tempfile::TempDir;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

/// A running core, killed at the end of the test, with its own folder for
/// the configuration, the logs and the test's files.
struct Core {
    child: Child,
    pipe: PipeName,
    _dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
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
    Core {
        child,
        pipe,
        _dir: dir,
    }
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

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
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
