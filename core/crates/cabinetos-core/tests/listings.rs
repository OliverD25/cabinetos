//! Directory listings, watching and volume information, end to end: the real
//! `cabinetos-core.exe` on a random pipe, a real client, real shared memory.

use std::fs::{self, File};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_fs::ListingReader;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::shm::EntryKind;
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, PROTOCOL_VERSION, RefreshReason, Request, Response,
};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(2);

/// A running core, killed at the end of the test.
struct Core {
    child: Child,
    pipe: PipeName,
    _log_dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn start_core() -> Core {
    let log_dir = tempfile::tempdir().unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .env("CABINETOS_LOG_DIR", log_dir.path())
        // Never the real plugins folder, whatever is installed there.
        .env("CABINETOS_PLUGINS_DIR", log_dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            log_dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", log_dir.path().join("themes"))
        .env("CABINETOS_CONFIG", log_dir.path().join("cabinetos.json"))
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
        _log_dir: log_dir,
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

/// A connected client that has said hello.
async fn greeted(core: &Core) -> PipeClient {
    let mut client = connect(&core.pipe).await;
    let welcome = client.hello("listings-test").await.unwrap();
    assert_eq!(
        welcome.body,
        Response::Welcome {
            protocol_version: PROTOCOL_VERSION,
            core_version: env!("CARGO_PKG_VERSION").to_owned(),
        }
    );
    client
}

fn list(path: &Path, watch: bool) -> Request {
    Request::ListDirectory {
        path: path.to_str().unwrap().to_owned(),
        include_hidden: None,
        sort: None,
        watch,
    }
}

/// The names in a section, read the way the CLI reads them.
fn names_in(client: &PipeClient, section_handle: u64) -> Vec<String> {
    let section = client.take_section(section_handle).unwrap();
    let view = section.map_readonly().unwrap();
    let reader = ListingReader::new(view.as_slice()).unwrap();
    reader.entries().map(|entry| entry.unwrap().name).collect()
}

async fn next_event(events: &mut UnboundedReceiver<Envelope<Event>>) -> Event {
    tokio::time::timeout(EVENT_DEADLINE, events.recv())
        .await
        .expect("no event within 2 s")
        .expect("the event stream ended")
        .body
}

fn error_code(response: &Response) -> Option<ErrorCode> {
    match response {
        Response::Error { code, .. } => Some(*code),
        _ => None,
    }
}

#[tokio::test]
async fn lists_a_thousand_files_into_shared_memory() {
    let dir = tempfile::tempdir().unwrap();
    for n in 0..1000 {
        File::create(dir.path().join(format!("file{n}.txt"))).unwrap();
    }
    fs::create_dir(dir.path().join("subfolder")).unwrap();
    let core = start_core();
    let mut client = greeted(&core).await;

    let reply = client.request(list(dir.path(), false)).await.unwrap();
    let Response::ListingOpened {
        listing_id,
        section_handle,
        entry_count,
        generation,
        ..
    } = reply.body
    else {
        panic!("expected listing_opened, got {:?}", reply.body);
    };
    assert_eq!(entry_count, 1001);
    assert_eq!(generation, 1);

    let section = client.take_section(section_handle).unwrap();
    let view = section.map_readonly().unwrap();
    let reader = ListingReader::new(view.as_slice()).unwrap();
    assert_eq!(reader.len(), 1001);
    let first = reader.entry(0).unwrap();
    assert_eq!(
        (first.name.as_str(), first.kind),
        ("subfolder", EntryKind::Directory)
    );
    // Natural order: file2 before file10.
    let names: Vec<String> = reader.entries().map(|entry| entry.unwrap().name).collect();
    assert_eq!(&names[1..4], ["file0.txt", "file1.txt", "file2.txt"]);
    assert_eq!(names.last().unwrap(), "file999.txt");
    let position = |name: &str| names.iter().position(|n| n == name).unwrap();
    assert!(position("file2.txt") < position("file10.txt"));

    let closed = client
        .request(Request::CloseListing { listing_id })
        .await
        .unwrap();
    assert_eq!(closed.body, Response::Ok);
    // The client's own handle keeps the section readable after the close.
    assert_eq!(reader.len(), 1001);
    let again = client
        .request(Request::CloseListing { listing_id })
        .await
        .unwrap();
    assert_eq!(error_code(&again.body), Some(ErrorCode::NoSuchListing));

    client.request(Request::Shutdown).await.unwrap();
}

#[tokio::test]
async fn listing_requires_hello() {
    let dir = tempfile::tempdir().unwrap();
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let reply = client.request(list(dir.path(), false)).await.unwrap();
    match reply.body {
        Response::Error { code, message } => {
            assert_eq!(code, ErrorCode::ProtocolError);
            assert_eq!(message, "hello required");
        }
        other => panic!("expected an error, got {other:?}"),
    }
}

#[tokio::test]
async fn hello_with_another_process_id_is_refused() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let reply = client
        .request(Request::Hello {
            client_pid: std::process::id() + 4,
            client_name: "impostor".to_owned(),
        })
        .await
        .unwrap();
    assert_eq!(error_code(&reply.body), Some(ErrorCode::ProtocolError));
}

#[tokio::test]
async fn a_watched_listing_is_refreshed_when_a_file_appears() {
    let dir = tempfile::tempdir().unwrap();
    File::create(dir.path().join("before.txt")).unwrap();
    let core = start_core();
    let mut client = greeted(&core).await;
    let mut events = client.events().unwrap();

    let reply = client.request(list(dir.path(), true)).await.unwrap();
    let Response::ListingOpened {
        listing_id,
        section_handle,
        ..
    } = reply.body
    else {
        panic!("expected listing_opened, got {:?}", reply.body);
    };
    assert_eq!(names_in(&client, section_handle), ["before.txt"]);

    let created = Instant::now();
    File::create(dir.path().join("after.txt")).unwrap();
    match next_event(&mut events).await {
        Event::ListingRefreshed {
            listing_id: refreshed,
            section_handle,
            entry_count,
            generation,
            reason,
            ..
        } => {
            assert_eq!(refreshed, listing_id);
            assert_eq!(entry_count, 2);
            assert_eq!(generation, 2);
            assert_eq!(reason, RefreshReason::Changed);
            assert_eq!(
                names_in(&client, section_handle),
                ["after.txt", "before.txt"]
            );
        }
        other => panic!("expected listing_refreshed, got {other:?}"),
    }
    assert!(created.elapsed() < EVENT_DEADLINE);

    // After close_listing no more events come. Windows may report one new
    // file in two batches, so a second refresh can be queued before the
    // close; the core's replies and events share one ordered queue, so
    // anything queued before the close is already here once the reply is.
    client
        .request(Request::CloseListing { listing_id })
        .await
        .unwrap();
    while events.try_recv().is_ok() {}
    File::create(dir.path().join("ignored.txt")).unwrap();
    assert!(
        tokio::time::timeout(Duration::from_millis(300), events.recv())
            .await
            .is_err()
    );
}

#[tokio::test]
async fn a_watched_listing_is_lost_when_its_directory_goes() {
    let parent = tempfile::tempdir().unwrap();
    let dir = parent.path().join("doomed");
    fs::create_dir(&dir).unwrap();
    let core = start_core();
    let mut client = greeted(&core).await;
    let mut events = client.events().unwrap();

    let reply = client.request(list(&dir, true)).await.unwrap();
    let Response::ListingOpened { listing_id, .. } = reply.body else {
        panic!("expected listing_opened, got {:?}", reply.body);
    };
    fs::remove_dir(&dir).unwrap();
    match next_event(&mut events).await {
        Event::ListingLost {
            listing_id: lost, ..
        } => assert_eq!(lost, listing_id),
        other => panic!("expected listing_lost, got {other:?}"),
    }
    let closed = client
        .request(Request::CloseListing { listing_id })
        .await
        .unwrap();
    assert_eq!(closed.body, Response::Ok);
}

#[tokio::test]
async fn missing_paths_and_files_are_errors() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("plain.txt");
    File::create(&file).unwrap();
    let core = start_core();
    let mut client = greeted(&core).await;

    let missing = client
        .request(list(&dir.path().join("nope"), false))
        .await
        .unwrap();
    assert_eq!(error_code(&missing.body), Some(ErrorCode::NotFound));
    let not_a_directory = client.request(list(&file, false)).await.unwrap();
    assert_eq!(
        error_code(&not_a_directory.body),
        Some(ErrorCode::InvalidPath)
    );
    let unknown = client
        .request(Request::CloseListing {
            listing_id: 424_242,
        })
        .await
        .unwrap();
    assert_eq!(error_code(&unknown.body), Some(ErrorCode::NoSuchListing));
}

#[tokio::test]
async fn volume_info_describes_the_system_drive() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let windows = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_owned());
    let reply = client
        .request(Request::VolumeInfo { path: windows })
        .await
        .unwrap();
    let Response::VolumeInfo(details) = reply.body else {
        panic!("expected volume_info, got {:?}", reply.body);
    };
    assert!(details.drive_letter.is_some());
    assert_eq!(details.filesystem, "NTFS");
    assert!(details.total_bytes > 0);
}
