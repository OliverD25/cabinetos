//! The real indexer process on a real volume, and the Windows service. Both
//! need Administrator rights, so they are `#[ignore]`d and skip with a
//! message when not elevated. CI runs them (its runners are Administrators):
//!
//! ```text
//! cargo test --release -p cabinetos-indexer -- --ignored --nocapture
//! ```
//!
//! The service test installs and removes a Windows service, a change to the
//! machine, so it also needs `CABINETOS_TEST_SERVICE=1`, which only CI sets.
//! Everything the tests write lives under `%TEMP%\cabinetos-index-test\`.

use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_index::is_elevated;
use cabinetos_indexer::PIPE_NAME;
use cabinetos_ipc::{PipeName, exchange};
use cabinetos_protocol::{
    Envelope, HitKind, IndexState, IndexerErrorCode, IndexerRequest, IndexerResponse, RequestId,
};

const EXE: &str = env!("CARGO_BIN_EXE_cabinetos-indexer");

/// Building a system drive with millions of files.
const READY_DEADLINE: Duration = Duration::from_mins(15);

/// The tests index the same volume; one at a time keeps memory and disk
/// reads moderate.
static ONE_AT_A_TIME: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

fn elevated() -> bool {
    let elevated = is_elevated();
    if !elevated {
        eprintln!(
            "skipped: the indexer reads the MFT and the change journal, which needs Administrator rights; run it from an elevated terminal (CI does)"
        );
    }
    elevated
}

fn scratch() -> tempfile::TempDir {
    let root = std::env::temp_dir().join("cabinetos-index-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("indexer")
        .tempdir_in(root)
        .unwrap()
}

fn letter_of(path: &Path) -> char {
    path.to_str()
        .unwrap()
        .chars()
        .next()
        .unwrap()
        .to_ascii_uppercase()
}

/// A drive letter no volume uses.
fn unused_letter() -> char {
    ('D'..='Z')
        .rev()
        .find(|&letter| !Path::new(&format!("{letter}:\\")).exists())
        .expect("some drive letter is free")
}

/// Kills the process when the test ends, however it ends.
struct Running(std::process::Child);

impl Drop for Running {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

async fn ask(pipe: &PipeName, request: IndexerRequest) -> Option<IndexerResponse> {
    let envelope = Envelope::new(RequestId::new(), request);
    exchange::<_, Envelope<IndexerResponse>>(pipe, &envelope, Duration::from_secs(10))
        .await
        .ok()
        .map(|reply| reply.body)
}

/// Waits until every volume is ready.
async fn wait_ready(pipe: &PipeName) {
    let deadline = Instant::now() + READY_DEADLINE;
    loop {
        if let Some(IndexerResponse::IndexStatus { volumes }) =
            ask(pipe, IndexerRequest::IndexStatus).await
        {
            for volume in &volumes {
                if let IndexState::Failed { message } = &volume.state {
                    panic!("{}: failed: {message}", volume.letter);
                }
            }
            if !volumes.is_empty()
                && volumes
                    .iter()
                    .all(|volume| volume.state == IndexState::Ready)
            {
                for volume in &volumes {
                    println!(
                        "{}: ready, {} entries, built in {} ms",
                        volume.letter,
                        volume.entries,
                        volume.built_in_ms.unwrap_or_default()
                    );
                }
                return;
            }
        }
        assert!(
            Instant::now() < deadline,
            "the indexer was not ready in time"
        );
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

async fn search(pipe: &PipeName, query: &str, root: Option<&Path>) -> IndexerResponse {
    ask(
        pipe,
        IndexerRequest::Search {
            query: query.to_owned(),
            limit: 50,
            root: root.map(|root| root.display().to_string()),
        },
    )
    .await
    .expect("the indexer answers")
}

#[tokio::test]
#[ignore = "needs Administrator rights; CI runs it"]
async fn the_console_indexer_answers_status_and_search() {
    if !elevated() {
        return;
    }
    let _serial = ONE_AT_A_TIME.lock().await;
    let dir = scratch();
    let token = format!("cbi{:012x}", rand::random::<u64>() & 0xFFFF_FFFF_FFFF);
    std::fs::write(dir.path().join(format!("{token}.txt")), "x").unwrap();
    let letter = letter_of(dir.path());
    let pipe = PipeName::from_full(format!(r"\\.\pipe\cabinetos-indexer-test-{token}"));
    let logs = dir.path().join("logs");
    let indexer = Running(
        Command::new(EXE)
            .args([
                "--console",
                "--volumes",
                &letter.to_string(),
                "--pipe",
                pipe.as_str(),
            ])
            .arg("--log-dir")
            .arg(&logs)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap(),
    );

    let started = Instant::now();
    loop {
        if matches!(
            ask(&pipe, IndexerRequest::Ping).await,
            Some(IndexerResponse::Pong { .. })
        ) {
            break;
        }
        assert!(
            started.elapsed() < Duration::from_secs(30),
            "the pipe never appeared"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    wait_ready(&pipe).await;

    let IndexerResponse::FileSearchResults {
        hits,
        took_us,
        complete,
    } = search(&pipe, &token, None).await
    else {
        panic!("expected hits");
    };
    println!(
        "found {} hit(s) for the test file in {took_us} µs over the whole volume",
        hits.len()
    );
    assert!(complete);
    assert!(
        hits.iter()
            .any(|hit| hit.kind == HitKind::File && hit.path.ends_with(&format!("\\{token}.txt"))),
        "{hits:?}"
    );

    let IndexerResponse::FileSearchResults { hits, .. } =
        search(&pipe, &token, Some(dir.path())).await
    else {
        panic!("expected hits");
    };
    assert_eq!(hits.len(), 1, "{hits:?}");

    let elsewhere = PathBuf::from(format!("{}:\\x", unused_letter()));
    assert!(matches!(
        search(&pipe, &token, Some(&elsewhere)).await,
        IndexerResponse::Error {
            code: IndexerErrorCode::NotIndexed,
            ..
        }
    ));

    drop(indexer);
    let log = std::fs::read_dir(&logs)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .find(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .starts_with("indexer.")
        })
        .expect("the indexer writes indexer.<date>.jsonl");
    let text = std::fs::read_to_string(log).unwrap();
    assert!(text.contains(r#""boundary":"indexer""#), "{text}");
    assert!(text.contains("index built"), "{text}");
}

/// Removes the service when the test ends, however it ends.
struct Installed;

impl Drop for Installed {
    fn drop(&mut self) {
        let _ = Command::new(EXE).arg("--uninstall").status();
    }
}

#[tokio::test]
#[ignore = "installs a Windows service: needs Administrator rights and CABINETOS_TEST_SERVICE=1 (CI)"]
async fn the_service_installs_starts_answers_and_uninstalls() {
    if !elevated() {
        return;
    }
    if std::env::var("CABINETOS_TEST_SERVICE").as_deref() != Ok("1") {
        eprintln!(
            "skipped: installing a Windows service changes the machine; set CABINETOS_TEST_SERVICE=1 to run it (CI does)"
        );
        return;
    }
    let _serial = ONE_AT_A_TIME.lock().await;
    let dir = scratch();
    let letter = letter_of(dir.path());
    let logs = dir.path().join("logs");
    let installed = Command::new(EXE)
        .args(["--install", "--volumes", &letter.to_string()])
        .arg("--log-dir")
        .arg(&logs)
        .output()
        .unwrap();
    assert!(
        installed.status.success(),
        "{}",
        String::from_utf8_lossy(&installed.stderr)
    );
    let _installed = Installed;
    // `--install` starts the service itself, and registers it to start by
    // itself after every restart of Windows (automatic, delayed).
    let config = Command::new("sc.exe")
        .args(["qc", "cabinetos-indexer"])
        .output()
        .unwrap();
    let config = String::from_utf8_lossy(&config.stdout);
    assert!(config.contains("AUTO_START  (DELAYED)"), "{config}");
    let status = Command::new("sc.exe")
        .args(["query", "cabinetos-indexer"])
        .output()
        .unwrap();
    let status = String::from_utf8_lossy(&status.stdout);
    assert!(status.contains("RUNNING"), "{status}");

    let pipe = PipeName::from_full(PIPE_NAME);
    wait_ready(&pipe).await;
    let IndexerResponse::FileSearchResults { complete, .. } = search(&pipe, "windows", None).await
    else {
        panic!("expected hits");
    };
    assert!(complete);

    let removed = Command::new(EXE).arg("--uninstall").output().unwrap();
    assert!(
        removed.status.success(),
        "{}",
        String::from_utf8_lossy(&removed.stderr)
    );
    let query = Command::new("sc.exe")
        .args(["query", "cabinetos-indexer"])
        .output()
        .unwrap();
    assert!(!query.status.success(), "the service is gone");
    assert!(
        ask(&pipe, IndexerRequest::Ping).await.is_none(),
        "the service's pipe is gone"
    );
}
