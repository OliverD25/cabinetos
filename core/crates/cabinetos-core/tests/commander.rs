//! Total Commander's small requests (sub-phase 11a, protocol version 12),
//! end to end: the real `cabinetos-core.exe` on a random pipe with a real
//! client. Every file and folder lives under `%TEMP%\cabinetos-core-test\`,
//! in a folder removed at the end.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, Event, MeasureResult, Request, Response};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(60);

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
        .prefix("commander")
        .tempdir_in(root)
        .unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("config").join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
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

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn error_code(response: &Response) -> Option<ErrorCode> {
    match response {
        Response::Error { code, .. } => Some(*code),
        _ => None,
    }
}

fn text(path: &Path) -> String {
    path.to_str().unwrap().to_owned()
}

/// `wide` folders under `root`, each with `wide` more folders and one file
/// of 1,000 bytes.
fn tree(root: &Path, wide: usize) {
    for outer in 0..wide {
        let folder = root.join(format!("folder {outer}"));
        for inner in 0..wide {
            std::fs::create_dir_all(folder.join(format!("inner {inner}"))).unwrap();
        }
        std::fs::write(folder.join("data.bin"), [7u8; 1000]).unwrap();
    }
}

async fn measure(client: &mut PipeClient, paths: &[&Path]) -> u64 {
    let paths = paths.iter().map(|path| text(path)).collect();
    match ask(client, Request::MeasurePaths { paths }).await {
        Response::MeasureStarted { measure_id } => measure_id,
        other => panic!("expected measure_started, got {other:?}"),
    }
}

/// The next event, within the deadline.
async fn next(events: &mut UnboundedReceiver<Envelope<Event>>) -> Event {
    tokio::time::timeout(EVENT_DEADLINE, events.recv())
        .await
        .expect("no event in time")
        .expect("the event stream ended")
        .body
}

/// What a measure sent: its progress, each with when it came, then its
/// results and whether it was cancelled.
struct Followed {
    progress: Vec<(Instant, String, u64)>,
    results: Vec<MeasureResult>,
    cancelled: bool,
}

async fn follow(events: &mut UnboundedReceiver<Envelope<Event>>, measure_id: u64) -> Followed {
    let mut progress = Vec::new();
    loop {
        match next(events).await {
            Event::MeasureProgress {
                measure_id: id,
                path,
                folders,
                ..
            } if id == measure_id => progress.push((Instant::now(), path, folders)),
            Event::MeasureFinished {
                measure_id: id,
                results,
                cancelled,
            } if id == measure_id => {
                return Followed {
                    progress,
                    results,
                    cancelled,
                };
            }
            _ => {}
        }
    }
}

fn result(path: &Path, files: u64, folders: u64, bytes: u64) -> MeasureResult {
    MeasureResult {
        path: text(path),
        files,
        folders,
        bytes,
        unreadable: 0,
    }
}

#[tokio::test]
async fn a_measure_counts_each_path_and_sends_progress_at_most_30_a_second() {
    let core = start_core();
    let root = core.files().join("tree");
    tree(&root, 60);
    let file = root.join("folder 7").join("data.bin");
    let mut client = connect(&core.pipe).await;
    let mut events = client.events().unwrap();

    let started = Instant::now();
    let measure_id = measure(&mut client, &[&root, &file]).await;
    let followed = follow(&mut events, measure_id).await;
    let took = started.elapsed();
    assert!(!followed.cancelled);
    assert_eq!(
        followed.results,
        [
            result(&root, 60, 60 * 61, 60_000),
            result(&file, 1, 0, 1000)
        ]
    );
    // The walk of 3,660 folders takes longer than one thirtieth of a second.
    let sent = followed.progress.len();
    assert!(sent >= 1, "no progress in {took:?}");
    let most = took.as_secs_f64() * 30.0 + 1.0;
    assert!(
        f64::from(u32::try_from(sent).unwrap()) <= most,
        "{sent} progress events in {took:?}"
    );
    assert!(
        followed
            .progress
            .iter()
            .all(|(_, path, _)| *path == text(&root))
    );
    assert!(
        followed
            .progress
            .windows(2)
            .all(|pair| pair[0].2 <= pair[1].2)
    );
}

#[tokio::test]
async fn a_cancelled_measure_ends_with_the_paths_it_counted() {
    let core = start_core();
    let root = core.files().join("tree");
    tree(&root, 60);
    let file = root.join("folder 3").join("data.bin");
    let mut client = connect(&core.pipe).await;
    let mut events = client.events().unwrap();

    // The file, then the tree, cancelled at the tree's first progress, in
    // the middle of its walk.
    let measure_id = measure(&mut client, &[&file, &root]).await;
    let mut cancel_reply = None;
    loop {
        match next(&mut events).await {
            Event::MeasureProgress { measure_id: id, .. }
                if id == measure_id && cancel_reply.is_none() =>
            {
                cancel_reply = Some(ask(&mut client, Request::CancelMeasure { measure_id }).await);
            }
            Event::MeasureFinished {
                measure_id: id,
                results,
                cancelled,
            } if id == measure_id => {
                assert_eq!(
                    cancel_reply,
                    Some(Response::Ok),
                    "no progress came before the end"
                );
                assert!(cancelled, "the walk ended before the cancel came");
                assert_eq!(results, [result(&file, 1, 0, 1000)]);
                break;
            }
            _ => {}
        }
    }
    // A measure that has ended, or never ran: nothing to stop.
    assert_eq!(
        ask(&mut client, Request::CancelMeasure { measure_id }).await,
        Response::Ok
    );
    assert_eq!(
        ask(
            &mut client,
            Request::CancelMeasure {
                measure_id: u64::MAX
            }
        )
        .await,
        Response::Ok
    );
}

#[tokio::test]
async fn measures_run_at_once_and_refuse_what_is_not_there() {
    let core = start_core();
    let first = core.files().join("first");
    let second = core.files().join("second");
    tree(&first, 4);
    tree(&second, 3);
    let mut client = connect(&core.pipe).await;
    let mut events = client.events().unwrap();

    let one = measure(&mut client, &[&first]).await;
    let two = measure(&mut client, &[&second]).await;
    assert_ne!(one, two);
    let mut finished = Vec::new();
    while finished.len() < 2 {
        if let Event::MeasureFinished {
            measure_id,
            results,
            ..
        } = next(&mut events).await
        {
            finished.push((measure_id, results));
        }
    }
    finished.sort_by_key(|(measure_id, _)| *measure_id);
    assert_eq!(
        finished,
        [
            (one, vec![result(&first, 4, 20, 4000)]),
            (two, vec![result(&second, 3, 12, 3000)])
        ]
    );

    let gone = core.files().join("gone");
    let refused = ask(
        &mut client,
        Request::MeasurePaths {
            paths: vec![text(&first), text(&gone)],
        },
    )
    .await;
    assert_eq!(
        error_code(&refused),
        Some(ErrorCode::NotFound),
        "{refused:?}"
    );
    let relative = ask(
        &mut client,
        Request::MeasurePaths {
            paths: vec!["first".to_owned()],
        },
    )
    .await;
    assert_eq!(error_code(&relative), Some(ErrorCode::InvalidPath));
    // Nothing to count: it starts and ends at once.
    let nothing = measure(&mut client, &[]).await;
    let followed = follow(&mut events, nothing).await;
    assert!(followed.results.is_empty() && !followed.cancelled);
    // A refused measure sends nothing.
    assert!(
        tokio::time::timeout(Duration::from_millis(300), events.recv())
            .await
            .is_err()
    );
}

/// A client that said hello, as listing needs.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("commander-test").await.unwrap();
    assert!(
        matches!(welcome.body, Response::Welcome { .. }),
        "{welcome:?}"
    );
    (client, events)
}

async fn matches(
    client: &mut PipeClient,
    listing_id: u64,
    patterns: &str,
    files_only: bool,
    first_from: Option<u32>,
) -> (u32, Vec<[u32; 2]>) {
    let reply = ask(
        client,
        Request::MatchEntries {
            listing_id,
            patterns: patterns.to_owned(),
            files_only,
            first_from,
        },
    )
    .await;
    match reply {
        Response::EntryMatches {
            listing_id: id,
            generation,
            ranges,
        } if id == listing_id => (generation, ranges),
        other => panic!("expected entry_matches, got {other:?}"),
    }
}

/// The names are read from the listing's current section: after a
/// refresh, from the new one.
#[tokio::test]
async fn entries_match_patterns_in_the_current_section() {
    let core = start_core();
    let folder = core.files().join("folder");
    std::fs::create_dir_all(folder.join("folder.txt")).unwrap();
    for name in ["a.txt", "b.md", "c.txt", "d.jpg", "e.txt", "README"] {
        std::fs::write(folder.join(name), name).unwrap();
    }
    let (mut client, mut events) = greeted(&core).await;
    let reply = ask(
        &mut client,
        Request::ListDirectory {
            path: text(&folder),
            include_hidden: None,
            sort: None,
            watch: true,
        },
    )
    .await;
    let Response::ListingOpened { listing_id, .. } = reply else {
        panic!("expected listing_opened, got {reply:?}");
    };
    // 0 folder.txt, 1 a.txt, 2 b.md, 3 c.txt, 4 d.jpg, 5 e.txt, 6 README
    assert_eq!(
        matches(&mut client, listing_id, "*.txt", false, None).await,
        (1, vec![[0, 2], [3, 1], [5, 1]])
    );
    assert_eq!(
        matches(&mut client, listing_id, "*.txt;*.md|c*", true, None).await,
        (1, vec![[1, 2], [5, 1]])
    );
    assert_eq!(
        matches(&mut client, listing_id, "*.txt", true, Some(6)).await,
        (1, vec![[1, 1]])
    );
    let unknown = ask(
        &mut client,
        Request::MatchEntries {
            listing_id: listing_id + 1000,
            patterns: "*".to_owned(),
            files_only: false,
            first_from: None,
        },
    )
    .await;
    assert_eq!(error_code(&unknown), Some(ErrorCode::NoSuchListing));

    std::fs::write(folder.join("f.txt"), "f").unwrap();
    loop {
        if let Event::ListingRefreshed { entry_count: 8, .. } = next(&mut events).await {
            break;
        }
    }
    let (generation, ranges) = matches(&mut client, listing_id, "*.txt", true, None).await;
    assert!(generation >= 2, "{generation}");
    assert_eq!(ranges, [[1, 1], [3, 1], [5, 2]]);
}

/// The answer to `edit_path` of `path` from a core whose `files.editor`
/// names a program found nowhere. Nothing starts: every answer comes
/// before a launch.
async fn edit_with_an_editor_found_nowhere(core: &Core, path: &Path) -> Response {
    let mut client = connect(&core.pipe).await;
    let editor = serde_json::json!({"command": "cabinetos-no-such-editor"});
    let set = ask(
        &mut client,
        Request::SetValue {
            path: "files.editor".to_owned(),
            value: editor,
        },
    )
    .await;
    assert_eq!(set, Response::Ok);
    ask(&mut client, Request::EditPath { path: text(path) }).await
}

/// The path is checked before the editor: a folder is a folder, whatever
/// `files.editor` names.
#[tokio::test]
async fn editing_a_folder_is_invalid_path_before_the_editor_is_looked_for() {
    let core = start_core();
    let folder = core.files().join("a folder");
    std::fs::create_dir_all(&folder).unwrap();
    let reply = edit_with_an_editor_found_nowhere(&core, &folder).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::InvalidPath),
        "{reply:?}"
    );
}

/// A missing file is `not_found`, whatever `files.editor` names.
#[tokio::test]
async fn editing_a_missing_file_is_not_found_before_the_editor_is_looked_for() {
    let core = start_core();
    let missing = core.files().join("gone.txt");
    let reply = edit_with_an_editor_found_nowhere(&core, &missing).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotFound), "{reply:?}");
}

/// A file that is there, with an editor found nowhere: `spawn_failed`,
/// naming the setting, and nothing else is tried.
#[tokio::test]
async fn editing_a_file_with_an_editor_found_nowhere_is_spawn_failed() {
    let core = start_core();
    let file = core.files().join("notes.txt");
    std::fs::write(&file, "notes").unwrap();
    let reply = edit_with_an_editor_found_nowhere(&core, &file).await;
    let Response::Error {
        code: ErrorCode::SpawnFailed,
        message,
    } = &reply
    else {
        panic!("expected spawn_failed, got {reply:?}");
    };
    assert!(
        message.contains("files.editor") && message.contains("cabinetos-no-such-editor"),
        "{message}"
    );
}
