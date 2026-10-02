//! The GUI context for `cab` (unit 4 of the terminal sprint), end to end:
//! the real `cabinetos-core.exe` on a random pipe, a client that plays the
//! window by sending `window_state`, and a second client that asks
//! `gui_context` as `cab` does, with no `hello`. A job started from the
//! answer runs as the window's own does. Everything the tests write lives
//! under `%TEMP%\cabinetos-core-test\` in folders removed at the end.

use std::fs;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    ConflictPolicy, Envelope, ErrorCode, Event, JobKind, JobOptions, JobRequest, JobState, Pane,
    PaneState, Request, Response, WindowPanes, WindowState, WindowTab,
};
use serde_json::Value;
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const JOB_DEADLINE: Duration = Duration::from_secs(60);

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

fn start_core(level: Option<&str>) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("gui-context")
        .tempdir_in(root)
        .unwrap();
    let pipe = PipeName::random();
    let mut command = Command::new(CORE_EXE);
    command
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
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    match level {
        Some(level) => command.env("CABINETOS_LOG", level),
        None => command.env_remove("CABINETOS_LOG"),
    };
    let child = command.spawn().unwrap();
    Core { child, pipe, dir }
}

impl Core {
    /// Every line of the core's log, parsed.
    fn log_lines(&self) -> Vec<Value> {
        let mut lines = Vec::new();
        for entry in fs::read_dir(self.dir.path().join("logs")).unwrap() {
            let path = entry.unwrap().path();
            if path
                .extension()
                .is_some_and(|extension| extension == "jsonl")
            {
                for line in fs::read_to_string(&path).unwrap().lines() {
                    lines.push(serde_json::from_str(line).unwrap());
                }
            }
        }
        lines
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

/// A client that said hello, as a window does.
async fn window(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("gui-context-test").await.unwrap();
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

fn shown(path: &Path) -> String {
    path.display().to_string()
}

fn pane(folder: &str, cursor: Option<&str>, marked: &[&str]) -> PaneState {
    PaneState {
        tabs: vec![WindowTab {
            path: folder.to_owned(),
            ..WindowTab::default()
        }],
        cursor: cursor.map(str::to_owned),
        marked: marked.iter().map(|path| (*path).to_owned()).collect(),
        ..PaneState::default()
    }
}

fn state(active_pane: Pane, left: PaneState, right: PaneState) -> Request {
    Request::WindowState(WindowState {
        active_pane,
        panes: WindowPanes { left, right },
    })
}

/// The fields of a `gui_context` reply, or a panic.
struct Context {
    active: Pane,
    left: Option<String>,
    right: Option<String>,
    selection: Vec<String>,
    selection_total: u32,
    cursor: Option<String>,
}

async fn context(client: &mut PipeClient) -> Context {
    match ask(client, Request::GuiContext).await {
        Response::GuiContext {
            active,
            left,
            right,
            selection,
            selection_total,
            cursor,
        } => Context {
            active,
            left,
            right,
            selection,
            selection_total,
            cursor,
        },
        other => panic!("expected gui_context, got {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn without_a_window_there_is_no_context_and_a_window_that_leaves_takes_it_along() {
    let core = start_core(None);
    // `cab` sends no hello.
    let mut cab = connect(&core.pipe).await;
    let reply = ask(&mut cab, Request::GuiContext).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NoWindow), "{reply:?}");

    let (mut shell_window, _events) = window(&core).await;
    let reply = ask(
        &mut shell_window,
        state(Pane::Left, pane(r"E:\a", None, &[]), PaneState::default()),
    )
    .await;
    assert_eq!(reply, Response::Ok);
    assert_eq!(context(&mut cab).await.left.as_deref(), Some(r"E:\a"));

    drop(shell_window);
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        let reply = ask(&mut cab, Request::GuiContext).await;
        if error_code(&reply) == Some(ErrorCode::NoWindow) {
            break;
        }
        assert!(Instant::now() < deadline, "the context stayed: {reply:?}");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn the_context_is_the_newest_state_with_marks_a_cursor_a_tool_an_empty_folder_and_a_cut() {
    let core = start_core(Some("debug"));
    let (mut shell_window, _events) = window(&core).await;
    let mut cab = connect(&core.pipe).await;

    // Marks win over the cursor; the right pane is not the active one.
    let marked = [r"E:\left\a.txt", r"E:\left\b.txt"];
    let sent = state(
        Pane::Left,
        pane(r"E:\left", Some(r"E:\left\b.txt"), &marked),
        pane(r"D:\right", Some(r"D:\right\z.txt"), &[]),
    );
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let got = context(&mut cab).await;
    assert_eq!(got.active, Pane::Left);
    assert_eq!(got.left.as_deref(), Some(r"E:\left"));
    assert_eq!(got.right.as_deref(), Some(r"D:\right"));
    assert_eq!(got.selection, marked);
    assert_eq!(got.selection_total, 2);
    assert_eq!(got.cursor.as_deref(), Some(r"E:\left\b.txt"));

    // Only a cursor: the row the window's commands would act on.
    let sent = state(
        Pane::Right,
        pane(r"E:\left", None, &[]),
        pane(r"D:\right", Some(r"D:\right\z.txt"), &[]),
    );
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let got = context(&mut cab).await;
    assert_eq!(got.active, Pane::Right);
    assert_eq!(got.selection, [r"D:\right\z.txt"]);
    assert_eq!(got.selection_total, 1);

    // An empty folder: nothing to act on.
    let sent = state(
        Pane::Left,
        pane(r"E:\empty", None, &[]),
        pane(r"D:\right", None, &[]),
    );
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let got = context(&mut cab).await;
    assert_eq!(got.selection, Vec::<String>::new());
    assert_eq!((got.selection_total, got.cursor), (0, None));

    // A tool in front of the active pane: no folder, no selection.
    let mut tool = pane(r"E:\notes\x.md", Some(r"E:\notes\x.md"), &[]);
    tool.tabs[0].tool = Some("markdown-preview".to_owned());
    let sent = state(Pane::Left, tool, pane(r"D:\right", None, &[]));
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let got = context(&mut cab).await;
    assert_eq!(got.left, None);
    assert_eq!(got.right.as_deref(), Some(r"D:\right"));
    assert_eq!((got.selection, got.selection_total), (Vec::new(), 0));

    // A selection the window cut at 1,000 rows says how many there are.
    let mut cut = pane(r"E:\big", Some(r"E:\big\f1"), &[r"E:\big\f1", r"E:\big\f2"]);
    cut.marked_total = Some(5000);
    let sent = state(Pane::Left, cut, PaneState::default());
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let got = context(&mut cab).await;
    assert_eq!((got.selection.len(), got.selection_total), (2, 5000));

    // The request is a debug line, with the answer's numbers; it is not an
    // info line, since a script may ask as often as it likes.
    let lines = core.log_lines();
    let answered = lines
        .iter()
        .filter(|line| line["message"] == "gui context answered")
        .collect::<Vec<_>>();
    assert_eq!(answered.len(), 5, "one line for each answer");
    assert_eq!(answered[0]["level"], "DEBUG", "{}", answered[0]);
    assert_eq!(answered[0]["fields"]["selection_total"], 2);
    assert_eq!(answered[4]["fields"]["selection_total"], 5000);
    assert!(
        !lines
            .iter()
            .any(|line| { line["fields"]["request"] == "gui_context" && line["level"] == "INFO" }),
        "gui_context is logged at debug level only"
    );
}

/// The files of one job, started from the context's answer as `cab copy
/// --selection --dest opposite_pane` starts it.
async fn start_from(
    client: &mut PipeClient,
    got: &Context,
    kind: JobKind,
    destination: &str,
    on_conflict: ConflictPolicy,
) -> u64 {
    let reply = ask(
        client,
        Request::StartJob(JobRequest {
            kind,
            sources: got.selection.clone(),
            destination: Some(destination.to_owned()),
            options: JobOptions {
                on_conflict,
                ..JobOptions::default()
            },
        }),
    )
    .await;
    match reply {
        Response::JobStarted { job_id } => job_id,
        other => panic!("expected job_started, got {other:?}"),
    }
}

async fn ended(events: &mut UnboundedReceiver<Envelope<Event>>, job: u64) -> JobState {
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("the job did not end in time")
            .expect("the event stream ended")
            .body;
        if let Event::JobStateChanged { job_id, state } = event
            && job_id == job
            && state.is_terminal()
        {
            return state;
        }
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_job_started_from_the_context_runs_as_the_window_s_own_does() {
    let core = start_core(None);
    let files = core.dir.path().join("files");
    let (left, right) = (files.join("left"), files.join("Правий 'pane'"));
    fs::create_dir_all(&left).unwrap();
    fs::create_dir_all(&right).unwrap();
    for name in ["a.txt", "b.txt", "c.txt"] {
        fs::write(left.join(name), format!("left {name}")).unwrap();
    }
    fs::write(right.join("b.txt"), "right b.txt, already there").unwrap();

    let (mut shell_window, mut events) = window(&core).await;
    let (left_text, right_text) = (shown(&left), shown(&right));
    let [a, b, c] = ["a.txt", "b.txt", "c.txt"].map(|name| shown(&left.join(name)));
    let sent = state(
        Pane::Left,
        pane(&left_text, Some(b.as_str()), &[a.as_str(), b.as_str()]),
        pane(&right_text, None, &[]),
    );
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    let mut cab = connect(&core.pipe).await;

    // The marks, copied into the opposite pane's folder, with the default
    // of the command line: what exists there is skipped.
    let got = context(&mut cab).await;
    let opposite = got.right.clone().expect("the right pane shows a folder");
    let job = start_from(
        &mut cab,
        &got,
        JobKind::Copy,
        &opposite,
        ConflictPolicy::Skip,
    )
    .await;
    assert_eq!(ended(&mut events, job).await, JobState::Completed);
    assert_eq!(
        fs::read_to_string(right.join("a.txt")).unwrap(),
        "left a.txt"
    );
    assert_eq!(
        fs::read_to_string(right.join("b.txt")).unwrap(),
        "right b.txt, already there",
        "skip leaves the file that was there"
    );
    assert!(left.join("a.txt").exists(), "a copy leaves its source");

    // The cursor row alone, moved, with rename: the name is new, the source
    // is gone.
    let sent = state(
        Pane::Left,
        pane(&left_text, Some(c.as_str()), &[]),
        pane(&right_text, None, &[]),
    );
    assert_eq!(ask(&mut shell_window, sent).await, Response::Ok);
    fs::write(right.join("c.txt"), "right c.txt, already there").unwrap();
    let got = context(&mut cab).await;
    assert_eq!(got.selection, [c]);
    let job = start_from(
        &mut cab,
        &got,
        JobKind::Move,
        &opposite,
        ConflictPolicy::Rename,
    )
    .await;
    assert_eq!(ended(&mut events, job).await, JobState::Completed);
    assert!(!left.join("c.txt").exists(), "a move takes its source");
    assert_eq!(
        fs::read_to_string(right.join("c.txt")).unwrap(),
        "right c.txt, already there"
    );
    assert_eq!(
        fs::read_to_string(right.join("c (2).txt")).unwrap(),
        "left c.txt"
    );
}
