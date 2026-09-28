//! Jobs end to end: the real `cabinetos-core.exe` on a random pipe, a real
//! client. Everything the tests write (the core's log and configuration
//! included) lives under `%TEMP%\cabinetos-jobs-test\` and is removed.

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Conflict, ConflictKind, Envelope, ErrorCode, Event, JobAction, JobKind, JobOptions,
    JobProgress, JobRequest, JobState, Rate, Request, RequestId, Resolution, Response,
};
use serde_json::Value;
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const JOB_DEADLINE: Duration = Duration::from_secs(180);
/// The brief's limit (§3): at most 30 progress events per second per job.
const MAX_PER_SECOND: usize = 30;

fn scratch(name: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(name)
        .tempdir_in(root)
        .unwrap()
}

/// A running core, killed at the end of the test. It owns its folder (log
/// and configuration), removed after the core is gone.
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

fn start_core() -> Core {
    let dir = scratch("core");
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("cabinetos.json"))
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

impl Core {
    /// Asks the core to exit and waits, so its log is complete.
    async fn stop(&mut self, client: &mut PipeClient) {
        assert_eq!(ask(client, Request::Shutdown).await, Response::Ok);
        let until = Instant::now() + STARTUP_DEADLINE;
        while self.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < until, "the core did not exit");
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

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

/// A client that said hello, with its event stream.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("jobs-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn copy(sources: &[&Path], destination: &Path, options: JobOptions) -> Request {
    Request::StartJob(JobRequest {
        kind: JobKind::Copy,
        sources: sources
            .iter()
            .map(|path| path.display().to_string())
            .collect(),
        destination: Some(destination.display().to_string()),
        options,
    })
}

async fn start(client: &mut PipeClient, request: Request) -> u64 {
    match ask(client, request).await {
        Response::JobStarted { job_id } => job_id,
        other => panic!("expected job_started, got {other:?}"),
    }
}

/// The events of `job` until it ends, with the time each arrived.
async fn until_done(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    job: u64,
) -> Vec<(Instant, Event)> {
    let deadline = Instant::now() + JOB_DEADLINE;
    let mut seen = Vec::new();
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("the job did not end in time")
            .expect("the event stream ended")
            .body;
        let (of_job, terminal) = match &event {
            Event::JobProgress(progress) => (progress.job_id == job, false),
            Event::JobConflict(conflict) => (conflict.job_id == job, false),
            Event::JobStateChanged { job_id, state } => (*job_id == job, state.is_terminal()),
            _ => (false, false),
        };
        if of_job {
            seen.push((Instant::now(), event));
            if terminal {
                return seen;
            }
        }
    }
}

async fn next_conflict(events: &mut UnboundedReceiver<Envelope<Event>>, job: u64) -> Conflict {
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("no conflict in time")
            .expect("the event stream ended")
            .body;
        if let Event::JobConflict(conflict) = event
            && conflict.job_id == job
        {
            return conflict;
        }
    }
}

fn progress_of(events: &[(Instant, Event)]) -> Vec<(Instant, JobProgress)> {
    events
        .iter()
        .filter_map(|(at, event)| match event {
            Event::JobProgress(progress) => Some((*at, progress.clone())),
            _ => None,
        })
        .collect()
}

/// The most progress events in any one-second window, by the core's own
/// clock (`elapsed_ms`) and by arrival at the client.
fn busiest_second(progress: &[(Instant, JobProgress)]) -> (usize, usize) {
    let mut by_core: BTreeMap<u64, usize> = BTreeMap::new();
    for (_, record) in progress {
        *by_core.entry(record.elapsed_ms / 1000).or_default() += 1;
    }
    let first = progress.first().map(|(at, _)| *at);
    let mut by_arrival: BTreeMap<u128, usize> = BTreeMap::new();
    for (at, _) in progress {
        let second = at.duration_since(first.unwrap()).as_millis() / 1000;
        *by_arrival.entry(second).or_default() += 1;
    }
    (
        by_core.values().copied().max().unwrap_or(0),
        by_arrival.values().copied().max().unwrap_or(0),
    )
}

fn small_files(folder: &Path, count: usize) {
    for group in 0..count.div_ceil(500) {
        let sub = folder.join(format!("group {group}"));
        fs::create_dir_all(&sub).unwrap();
        for number in 0..500.min(count - group * 500) {
            fs::write(sub.join(format!("{number}.txt")), vec![b'x'; 1024 + number]).unwrap();
        }
    }
}

#[tokio::test]
async fn ten_thousand_files_report_at_most_thirty_times_a_second() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let data = scratch("throttle");
    let source = data.path().join("src");
    small_files(&source, 10_000);
    let destination = data.path().join("dst");

    let job = start(
        &mut client,
        copy(&[&source], &destination, JobOptions::default()),
    )
    .await;
    let seen = until_done(&mut events, job).await;
    let progress = progress_of(&seen);
    let last = &progress.last().unwrap().1;
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert_eq!(last.files_done, 10_000 + 21, "10,000 files and 21 folders");
    let (by_core, by_arrival) = busiest_second(&progress);
    assert!(
        by_core <= MAX_PER_SECOND,
        "{by_core} progress events in one second of the core's clock ({} in all, {} ms)",
        progress.len(),
        last.elapsed_ms
    );
    // Arrival times can bunch up a little in the pipe; they still stay
    // near the limit.
    assert!(
        by_arrival <= MAX_PER_SECOND + 2,
        "{by_arrival} arrived in one second"
    );
    assert!(progress.len() >= 2, "progress also comes before the end");
}

#[tokio::test]
async fn jobs_outlive_the_client_that_started_them() {
    let core = start_core();
    let data = scratch("outlive");
    let source = data.path().join("src");
    small_files(&source, 200);
    let destination = data.path().join("dst");
    let job = {
        let (mut client, _events) = greeted(&core).await;
        let job = start(
            &mut client,
            copy(&[&source], &destination, JobOptions::default()),
        )
        .await;
        // Paused, so it is surely still there when the client comes back.
        assert_eq!(
            ask(
                &mut client,
                Request::JobControl {
                    job_id: job,
                    action: JobAction::Pause
                }
            )
            .await,
            Response::Ok
        );
        job
        // The client goes away here.
    };
    let (mut client, mut events) = greeted(&core).await;
    let Response::Jobs { jobs } = ask(&mut client, Request::ListJobs).await else {
        panic!("expected jobs")
    };
    let listed = jobs
        .iter()
        .find(|info| info.progress.job_id == job)
        .expect("the job is still there");
    assert_eq!(listed.kind, JobKind::Copy);
    assert_eq!(listed.progress.state, JobState::Paused);
    assert_eq!(
        listed.destination.as_deref(),
        Some(destination.to_str().unwrap())
    );

    assert_eq!(
        ask(
            &mut client,
            Request::JobControl {
                job_id: job,
                action: JobAction::Resume
            }
        )
        .await,
        Response::Ok
    );
    let seen = until_done(&mut events, job).await;
    assert!(matches!(
        seen.last().unwrap().1,
        Event::JobStateChanged {
            state: JobState::Completed,
            ..
        }
    ));
    assert_eq!(fs::read_dir(destination.join("src")).unwrap().count(), 1);
}

#[tokio::test]
async fn a_conflict_waits_for_a_client_that_connects_later() {
    let core = start_core();
    let data = scratch("later");
    let source = data.path().join("a.txt");
    fs::write(&source, "new").unwrap();
    let destination = data.path().join("dst");
    fs::create_dir_all(&destination).unwrap();
    fs::write(destination.join("a.txt"), "old").unwrap();
    let job = {
        let (mut client, mut events) = greeted(&core).await;
        let job = start(
            &mut client,
            copy(&[&source], &destination, JobOptions::default()),
        )
        .await;
        let conflict = next_conflict(&mut events, job).await;
        assert!(matches!(conflict.kind, ConflictKind::FileExists { .. }));
        job
    };
    // A new client hears about the waiting conflict right after hello.
    let (mut client, mut events) = greeted(&core).await;
    let conflict = next_conflict(&mut events, job).await;
    assert_eq!(
        ask(
            &mut client,
            Request::ResolveConflict {
                job_id: job,
                conflict_id: conflict.conflict_id,
                resolution: Resolution::Overwrite,
                apply_to_same_kind: false,
            },
        )
        .await,
        Response::Ok
    );
    let seen = until_done(&mut events, job).await;
    assert!(matches!(
        seen.last().unwrap().1,
        Event::JobStateChanged {
            state: JobState::Completed,
            ..
        }
    ));
    assert_eq!(
        fs::read_to_string(destination.join("a.txt")).unwrap(),
        "new"
    );
}

#[tokio::test]
async fn bad_job_requests_get_error_codes() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let data = scratch("codes");
    let error = |response: Response| match response {
        Response::Error { code, .. } => code,
        other => panic!("expected an error, got {other:?}"),
    };
    let relative = copy(
        &[Path::new("relative.txt")],
        data.path(),
        JobOptions::default(),
    );
    assert_eq!(
        error(ask(&mut client, relative).await),
        ErrorCode::InvalidPath
    );
    let missing = copy(
        &[&data.path().join("missing.txt")],
        data.path(),
        JobOptions::default(),
    );
    assert_eq!(error(ask(&mut client, missing).await), ErrorCode::NotFound);
    let control = Request::JobControl {
        job_id: 999_999,
        action: JobAction::Cancel,
    };
    assert_eq!(error(ask(&mut client, control).await), ErrorCode::NoSuchJob);
    let source = data.path().join("a.txt");
    fs::write(&source, "a").unwrap();
    let job = start(
        &mut client,
        copy(&[&source], &data.path().join("dst"), JobOptions::default()),
    )
    .await;
    let resolve = Request::ResolveConflict {
        job_id: job,
        conflict_id: 999_999,
        resolution: Resolution::Skip,
        apply_to_same_kind: false,
    };
    assert_eq!(
        error(ask(&mut client, resolve).await),
        ErrorCode::NoSuchConflict
    );
    // The copy ends before its folder is removed.
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        let Response::Jobs { jobs } = ask(&mut client, Request::ListJobs).await else {
            panic!("expected jobs")
        };
        if jobs.iter().all(|info| info.progress.state.is_terminal()) {
            break;
        }
        assert!(Instant::now() < deadline, "the copy did not end");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

/// The log line "job queued" carries the ID of the `start_job` request
/// that queued the job, and a delete, which moves no bytes, reports its
/// pace in items per second.
#[tokio::test]
async fn a_job_is_logged_under_its_request_and_reports_its_pace() {
    let mut core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let dir = scratch("pace");
    let doomed = dir.path().join("doomed");
    fs::create_dir(&doomed).unwrap();
    for index in 0..2000 {
        fs::write(doomed.join(format!("{index}.txt")), "x").unwrap();
    }
    let id = RequestId::new();
    let reply = client
        .request_with_id(
            id.clone(),
            Request::StartJob(JobRequest {
                kind: JobKind::Delete { permanent: true },
                sources: vec![doomed.display().to_string()],
                destination: None,
                options: JobOptions::default(),
            }),
        )
        .await
        .unwrap();
    let Response::JobStarted { job_id } = reply.body else {
        panic!("expected job_started, got {:?}", reply.body);
    };
    let paces: Vec<Option<Rate>> = until_done(&mut events, job_id)
        .await
        .into_iter()
        .filter_map(|(_, event)| match event {
            Event::JobProgress(progress) => Some(progress.items_per_second),
            _ => None,
        })
        .collect();
    assert!(!doomed.exists());
    assert_eq!(paces.last(), Some(&Rate::new(0.0)), "{paces:?}");
    // A job quicker than one tick of the publisher sends only its final
    // record; otherwise the first has nothing to average yet, and the
    // records between show the pace.
    if paces.len() > 1 {
        assert_eq!(paces[0], None, "{paces:?}");
    }
    if paces.len() > 2 {
        assert!(
            paces[1..paces.len() - 1]
                .iter()
                .any(|pace| pace.is_some_and(|pace| pace.get() > 0.0)),
            "{paces:?}"
        );
    }

    // Stopped, the core has written its whole log.
    core.stop(&mut client).await;
    let lines = core.log_lines();
    let queued = lines
        .iter()
        .find(|line| line["message"] == "job queued")
        .expect("a job queued line");
    assert_eq!(queued["request_id"], id.as_str(), "{queued}");
    assert_eq!(queued["fields"]["job_id"], job_id, "{queued}");
}
