//! The foundations of Phase 14 (protocol version 13), end to end: the real
//! `cabinetos-core.exe` on a random pipe with real clients. Every file and
//! folder lives under `%TEMP%\cabinetos-core-test\`, in a folder removed at
//! the end.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_fs::ListingReader;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    ChangeKind, Envelope, ErrorCode, Event, JobState, Pane, PaneState, PreviewRow, Request,
    Response, WindowPanes, WindowState, WindowTab,
};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(20);

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
    start_core_with(&[])
}

/// A core with extra environment variables, such as a short preview life.
fn start_core_with(env: &[(&str, &str)]) -> Core {
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
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .envs(env.iter().copied())
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

fn row(path: &std::path::Path, kind: ChangeKind, to: Option<String>) -> PreviewRow {
    PreviewRow {
        path: path.display().to_string(),
        kind,
        to,
    }
}

/// The next event that `pick` accepts, skipping others.
async fn next_event<T>(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    mut pick: impl FnMut(&Event) -> Option<T>,
) -> T {
    let deadline = Instant::now() + EVENT_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("the event did not come in time")
            .expect("the event stream ended")
            .body;
        if let Some(found) = pick(&event) {
            return found;
        }
    }
}

/// The final state of each of `jobs`, from `job_state_changed`.
async fn final_states(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    jobs: &[u64],
) -> Vec<JobState> {
    let mut states = vec![None; jobs.len()];
    while states.iter().any(Option::is_none) {
        let (job, state) = next_event(events, |event| match event {
            Event::JobStateChanged { job_id, state } if state.is_terminal() => {
                Some((*job_id, state.clone()))
            }
            _ => None,
        })
        .await;
        if let Some(index) = jobs.iter().position(|id| *id == job) {
            states[index] = Some(state);
        }
    }
    states.into_iter().map(Option::unwrap).collect()
}

#[tokio::test(flavor = "multi_thread")]
#[expect(clippy::too_many_lines, reason = "one preview from proposal to disk")]
async fn a_preview_is_a_listing_and_applies_its_rows_in_order() {
    let core = start_core();
    let files = core.files();
    std::fs::write(files.join("a.txt"), b"alpha").unwrap();
    std::fs::write(files.join("b.txt"), b"bravo").unwrap();
    let sorted = files.join("Sorted");
    let (mut client, mut events) = greeted(&core, "preview-test").await;

    // The move needs what the rename makes, and the rename's folder what
    // the first row creates: only an ordered chain gets this right.
    let rows = vec![
        row(&files.join("Sorted\\"), ChangeKind::Create, None),
        row(
            &files.join("a.txt"),
            ChangeKind::Rename,
            Some("a2.txt".to_owned()),
        ),
        row(
            &files.join("a2.txt"),
            ChangeKind::Move,
            Some(sorted.display().to_string()),
        ),
        row(
            &files.join("b.txt"),
            ChangeKind::Copy,
            Some(sorted.display().to_string()),
        ),
        row(&sorted.join("note.txt"), ChangeKind::Create, None),
    ];
    let reply = ask(
        &mut client,
        Request::PreviewListing {
            title: "Sort two files".to_owned(),
            rows,
        },
    )
    .await;
    let Response::PreviewOpened {
        preview,
        title,
        listing,
    } = reply
    else {
        panic!("{reply:?}")
    };
    assert_eq!(title, "Sort two files");
    assert_eq!((listing.entry_count, listing.generation), (5, 1));
    // Nothing on disk changed.
    assert!(!sorted.exists() && files.join("a.txt").exists());

    let section = client.take_section(listing.section_handle).unwrap();
    let view = section.map_readonly().unwrap();
    let reader = ListingReader::new(view.as_slice()).unwrap();
    assert!(reader.is_preview());
    let names: Vec<String> = reader.entries().map(|entry| entry.unwrap().name).collect();
    assert_eq!(names[0], sorted.display().to_string());
    assert_eq!(names[1], files.join("a.txt").display().to_string());
    assert_eq!(
        reader.entry(1).unwrap().meta.size,
        5,
        "the file as it is now"
    );
    let rename = reader.preview_row(1).unwrap();
    assert_eq!(rename.change, Some(ChangeKind::Rename));
    assert_eq!(
        rename.to.as_deref(),
        Some(files.join("a2.txt").display().to_string().as_str())
    );
    assert_eq!(
        reader.preview_row(3).unwrap().change,
        Some(ChangeKind::Copy)
    );
    // A pane asks for type names as for any listing.
    let reply = ask(
        &mut client,
        Request::DescribeEntries {
            listing_id: listing.listing_id,
            from: 0,
            count: 5,
        },
    )
    .await;
    assert!(
        matches!(reply, Response::EntryDetails { ref details, .. } if details.len() == 5),
        "{reply:?}"
    );

    let reply = ask(
        &mut client,
        Request::PreviewApply {
            preview: preview.clone(),
        },
    )
    .await;
    let Response::JobsStarted { jobs } = reply else {
        panic!("{reply:?}")
    };
    // Create and rename share a steps job; the move, the copy and the last
    // create follow, each after the one before.
    assert_eq!(jobs.len(), 4, "{jobs:?}");
    let applied = next_event(&mut events, |event| match event {
        Event::PreviewApplied { preview: id, jobs } => Some((id.clone(), jobs.clone())),
        _ => None,
    })
    .await;
    assert_eq!(applied, (preview.clone(), jobs.clone()));
    let states = final_states(&mut events, &jobs).await;
    assert!(
        states.iter().all(|state| *state == JobState::Completed),
        "{states:?}"
    );
    assert_eq!(std::fs::read(sorted.join("a2.txt")).unwrap(), b"alpha");
    assert_eq!(std::fs::read(sorted.join("b.txt")).unwrap(), b"bravo");
    assert!(files.join("b.txt").exists() && !files.join("a.txt").exists());
    assert_eq!(std::fs::read(sorted.join("note.txt")).unwrap(), b"");

    // Applied once: it is gone.
    let reply = ask(&mut client, Request::PreviewApply { preview }).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::NoSuchPreview),
        "{reply:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_row_that_fails_cancels_the_rows_after_it() {
    let core = start_core();
    let files = core.files();
    std::fs::write(files.join("b.txt"), b"bravo").unwrap();
    let (mut client, mut events) = greeted(&core, "preview-test").await;
    let reply = ask(
        &mut client,
        Request::PreviewListing {
            title: "Broken".to_owned(),
            rows: vec![
                row(
                    &files.join("missing.txt"),
                    ChangeKind::Rename,
                    Some("x.txt".to_owned()),
                ),
                row(
                    &files.join("b.txt"),
                    ChangeKind::Move,
                    Some(files.join("elsewhere").display().to_string()),
                ),
            ],
        },
    )
    .await;
    let Response::PreviewOpened { preview, .. } = reply else {
        panic!("{reply:?}")
    };
    let Response::JobsStarted { jobs } = ask(&mut client, Request::PreviewApply { preview }).await
    else {
        panic!("not applied")
    };
    let states = final_states(&mut events, &jobs).await;
    assert_eq!(states, [JobState::CompletedWithErrors, JobState::Cancelled]);
    assert!(files.join("b.txt").exists(), "the move never ran");
}

#[tokio::test(flavor = "multi_thread")]
async fn previews_are_cancelled_and_expire() {
    let core = start_core_with(&[("CABINETOS_PREVIEW_TTL_MS", "400")]);
    let files = core.files();
    let (mut client, mut events) = greeted(&core, "preview-test").await;
    let (mut other, _other_events) = greeted(&core, "other-window").await;
    let one = || Request::PreviewListing {
        title: "One file".to_owned(),
        rows: vec![row(&files.join("x.txt"), ChangeKind::Create, None)],
    };

    let Response::PreviewOpened { preview, .. } = ask(&mut client, one()).await else {
        panic!("no preview")
    };
    // Another window opens it by its ID, as it does one a plugin proposed.
    let reply = ask(
        &mut other,
        Request::OpenPreview {
            preview: preview.clone(),
        },
    )
    .await;
    assert!(matches!(reply, Response::PreviewOpened { .. }), "{reply:?}");
    assert_eq!(
        ask(
            &mut client,
            Request::PreviewCancel {
                preview: preview.clone()
            }
        )
        .await,
        Response::Ok
    );
    let cancelled = next_event(&mut events, |event| match event {
        Event::PreviewCancelled { preview } => Some(preview.clone()),
        _ => None,
    })
    .await;
    assert_eq!(cancelled, preview);
    let reply = ask(&mut client, Request::OpenPreview { preview }).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NoSuchPreview));

    // Not applied in time: dropped with the same event.
    let Response::PreviewOpened { preview, .. } = ask(&mut client, one()).await else {
        panic!("no preview")
    };
    let expired = next_event(&mut events, |event| match event {
        Event::PreviewCancelled { preview } => Some(preview.clone()),
        _ => None,
    })
    .await;
    assert_eq!(expired, preview);
    assert!(!files.join("x.txt").exists());

    // Rows are checked before anything is kept.
    let reply = ask(
        &mut client,
        Request::PreviewListing {
            title: "Bad".to_owned(),
            rows: vec![PreviewRow {
                path: "relative.txt".to_owned(),
                kind: ChangeKind::Delete,
                to: None,
            }],
        },
    )
    .await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::InvalidPath),
        "{reply:?}"
    );
    // A preview needs hello: it becomes a listing in the client's process.
    let mut stranger = connect(&core.pipe).await;
    let reply = ask(&mut stranger, one()).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ProtocolError),
        "{reply:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn the_twenty_first_preview_of_a_client_is_refused() {
    let core = start_core();
    let files = core.files();
    let (mut client, _events) = greeted(&core, "preview-test").await;
    let (mut other, _other_events) = greeted(&core, "other-window").await;
    let one = || Request::PreviewListing {
        title: "One file".to_owned(),
        rows: vec![row(&files.join("x.txt"), ChangeKind::Create, None)],
    };
    let mut first = None;
    for _ in 0..20 {
        let Response::PreviewOpened { preview, .. } = ask(&mut client, one()).await else {
            panic!("refused too early")
        };
        first.get_or_insert(preview);
    }
    let reply = ask(&mut client, one()).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::TooManyPreviews),
        "{reply:?}"
    );
    // Another client has twenty of its own.
    assert!(matches!(
        ask(&mut other, one()).await,
        Response::PreviewOpened { .. }
    ));
    // Cancelling one makes room.
    let cancel = Request::PreviewCancel {
        preview: first.unwrap(),
    };
    assert_eq!(ask(&mut client, cancel).await, Response::Ok);
    assert!(matches!(
        ask(&mut client, one()).await,
        Response::PreviewOpened { .. }
    ));
}

/// Every line of every log file the core wrote so far.
fn log_text(core: &Core) -> String {
    let mut text = String::new();
    let dir = core.dir.path().join("logs");
    for entry in std::fs::read_dir(&dir).into_iter().flatten().flatten() {
        if let Ok(content) = std::fs::read_to_string(entry.path()) {
            text.push_str(&content);
        }
    }
    text
}

#[tokio::test(flavor = "multi_thread")]
async fn secrets_are_kept_by_windows_and_never_logged() {
    let prefix = format!("CabinetOS-test-{}-{}/", std::process::id(), line!());
    let core = start_core_with(&[
        ("CABINETOS_SECRETS_PREFIX", prefix.as_str()),
        // The most detailed log: a value must not reach even a trace line.
        ("CABINETOS_LOG", "trace"),
    ]);
    let mut client = connect(&core.pipe).await;
    let value = "sk-test-DO-NOT-LOG-7f3a9c";
    let set = |name: &str, value: &str| Request::SecretSet {
        name: name.to_owned(),
        value: cabinetos_protocol::SecretText(value.to_owned()),
    };
    assert_eq!(
        ask(&mut client, set("phase14.one", value)).await,
        Response::Ok
    );
    assert_eq!(
        ask(&mut client, set("phase14.two", "other")).await,
        Response::Ok
    );
    let reply = ask(
        &mut client,
        Request::SecretGet {
            name: "phase14.one".to_owned(),
        },
    )
    .await;
    let Response::Secret { value: got } = reply else {
        panic!("{reply:?}")
    };
    assert_eq!(got.0, value);
    assert_eq!(
        ask(&mut client, Request::SecretList).await,
        Response::SecretNames {
            names: vec!["phase14.one".to_owned(), "phase14.two".to_owned()]
        }
    );
    for name in ["phase14.one", "phase14.two"] {
        let delete = Request::SecretDelete {
            name: name.to_owned(),
        };
        assert_eq!(ask(&mut client, delete).await, Response::Ok);
    }
    let reply = ask(
        &mut client,
        Request::SecretGet {
            name: "phase14.one".to_owned(),
        },
    )
    .await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::NoSuchSecret),
        "{reply:?}"
    );
    let reply = ask(&mut client, set("a/b", value)).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::SecretError),
        "{reply:?}"
    );
    assert_eq!(
        ask(&mut client, Request::SecretList).await,
        Response::SecretNames { names: Vec::new() }
    );

    // The log names the secrets it handled, never a value.
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        let text = log_text(&core);
        if text.contains("secret removed") && text.contains("phase14.two") {
            assert!(!text.contains(value), "a secret's value reached the log");
            assert!(
                !text.contains("other\""),
                "a secret's value reached the log"
            );
            break;
        }
        assert!(Instant::now() < deadline, "the log lines did not come");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_job_is_undone_over_the_pipe_and_its_journal_line_stays() {
    let core = start_core();
    let files = core.files();
    let (mut client, mut events) = greeted(&core, "undo-test").await;
    let from = files.join("from");
    let to = files.join("to");
    std::fs::create_dir_all(&from).unwrap();
    std::fs::create_dir_all(&to).unwrap();
    std::fs::write(from.join("m.txt"), "m").unwrap();

    let reply = ask(
        &mut client,
        Request::StartJob(cabinetos_protocol::JobRequest {
            kind: cabinetos_protocol::JobKind::Move,
            sources: vec![from.join("m.txt").display().to_string()],
            destination: Some(to.display().to_string()),
            options: cabinetos_protocol::JobOptions::default(),
        }),
    )
    .await;
    let Response::JobStarted { job_id: moved } = reply else {
        panic!("{reply:?}")
    };
    let ended = |job: u64| {
        move |event: &Event| {
            matches!(event, Event::JobStateChanged { job_id, state } if *job_id == job && state.is_terminal())
                .then_some(())
        }
    };
    next_event(&mut events, ended(moved)).await;
    assert!(to.join("m.txt").exists());

    let reply = ask(&mut client, Request::UndoJob { job: None }).await;
    let Response::UndoStarted {
        job_id: undo,
        undoes,
        left,
    } = reply
    else {
        panic!("{reply:?}")
    };
    assert_eq!((undoes, left), (moved, Vec::new()));
    next_event(&mut events, ended(undo)).await;
    assert!(from.join("m.txt").exists(), "moved back");

    let reply = ask(&mut client, Request::UndoJob { job: Some(moved) }).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::NotUndoable),
        "{reply:?}"
    );
    let journal =
        std::fs::read_to_string(core.dir.path().join("undo").join("journal.jsonl")).unwrap();
    let lines: Vec<serde_json::Value> = journal
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(lines.len(), 2, "{journal}");
    assert_eq!(lines[0]["job"], moved);
    assert_eq!(lines[1]["undoes"], moved);
}
