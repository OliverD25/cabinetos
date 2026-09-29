//! Heavy mode end to end: `set_value logging.heavy` turns the core's heavy
//! file on and off while it runs; the file holds each request's and reply's
//! JSON with secrets masked, and one line per file a job touches, all with
//! the action's trace (docs/diagnostics.md, "Heavy mode"). Everything the
//! test writes lives under `%TEMP%\cabinetos-jobs-test\` and is removed.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, Event, JobKind, JobOptions, JobRequest, JobState, Request, RequestId, Response,
};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
/// Generous, so a busy machine cannot fail the tests.
const STARTUP_DEADLINE: Duration = Duration::from_secs(60);
const JOB_DEADLINE: Duration = Duration::from_secs(120);

fn scratch(name: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(name)
        .tempdir_in(root)
        .unwrap()
}

/// A running core, killed at the end of the test. It owns its folder.
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
    let dir = scratch("heavy");
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("cabinetos.json"))
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
        .env_remove("CABINETOS_LOG_HEAVY")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe, dir }
}

impl Core {
    fn logs(&self) -> PathBuf {
        self.dir.path().join("logs")
    }

    fn files(&self, prefix: &str) -> Vec<PathBuf> {
        let Ok(entries) = fs::read_dir(self.logs()) else {
            return Vec::new();
        };
        let mut files: Vec<PathBuf> = entries
            .map(|entry| entry.unwrap().path())
            .filter(|path| {
                let name = path.file_name().unwrap().to_string_lossy();
                name.starts_with(prefix) && name.ends_with(".jsonl")
            })
            .collect();
        files.sort();
        files
    }

    async fn stop(&mut self, client: &mut PipeClient) {
        let reply = client.request(Request::Shutdown).await.unwrap();
        assert_eq!(reply.body, Response::Ok);
        let until = Instant::now() + STARTUP_DEADLINE;
        while self.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < until, "the core did not exit");
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }
}

fn is_jsonl(name: &str) -> bool {
    Path::new(name)
        .extension()
        .is_some_and(|extension| extension == "jsonl")
}

fn lines_of(files: &[PathBuf]) -> Vec<Value> {
    files
        .iter()
        .flat_map(|path| {
            fs::read_to_string(path)
                .unwrap()
                .lines()
                .map(|line| serde_json::from_str(line).unwrap())
                .collect::<Vec<Value>>()
        })
        .collect()
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
    let welcome = client.hello("heavy-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn set_heavy(client: &mut PipeClient, on: bool) {
    let reply = client
        .request(Request::SetValue {
            path: "logging.heavy".to_owned(),
            value: json!(on),
        })
        .await
        .unwrap();
    assert_eq!(reply.body, Response::Ok);
}

/// Waits until `check` holds. The switch applies within a second; a busy
/// machine gets a minute.
async fn eventually(check: impl Fn() -> bool) {
    let deadline = Instant::now() + Duration::from_mins(1);
    while !check() {
        assert!(Instant::now() < deadline, "the switch did not apply");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

#[tokio::test]
async fn heavy_mode_records_payloads_and_job_entries_while_it_is_on() {
    let mut core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    assert!(core.files("heavy-core.").is_empty());

    set_heavy(&mut client, true).await;
    eventually(|| !core.files("heavy-core.").is_empty()).await;

    let trace = RequestId::new();
    client.set_trace(Some(trace.clone()));
    let refused = client
        .request(Request::ExecuteCommand {
            command: "no.such.command".to_owned(),
            args: json!({"path": "C:\\x", "token": "hunter2"}),
        })
        .await
        .unwrap();
    assert!(matches!(refused.body, Response::Error { .. }));

    let files = scratch("heavy-job");
    for name in ["a.txt", "b.txt"] {
        fs::write(files.path().join(name), name).unwrap();
    }
    let destination = files.path().join("to");
    fs::create_dir(&destination).unwrap();
    let started = client
        .request(Request::StartJob(JobRequest {
            kind: JobKind::Copy,
            sources: vec![
                files.path().join("a.txt").display().to_string(),
                files.path().join("b.txt").display().to_string(),
            ],
            destination: Some(destination.display().to_string()),
            options: JobOptions::default(),
        }))
        .await
        .unwrap();
    let Response::JobStarted { job_id } = started.body else {
        panic!("{:?}", started.body);
    };
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("the job did not end")
            .expect("the event stream ended");
        if matches!(event.body, Event::JobStateChanged { job_id: id, state: JobState::Completed } if id == job_id)
        {
            break;
        }
    }

    client.set_trace(None);
    set_heavy(&mut client, false).await;
    // Switched off, the file is closed: nothing holds it any more.
    let heavy_files = core.files("heavy-core.");
    eventually(|| {
        heavy_files.iter().all(|path| {
            fs::OpenOptions::new()
                .write(true)
                .open(path)
                .is_ok_and(|file| {
                    drop(file);
                    fs::rename(path, path.with_extension("closed")).is_ok()
                })
        })
    })
    .await;
    let closed: Vec<PathBuf> = heavy_files
        .iter()
        .map(|path| path.with_extension("closed"))
        .collect();
    let pong = client.request(Request::Ping).await.unwrap();
    core.stop(&mut client).await;
    assert!(
        core.files("heavy-core.").is_empty(),
        "nothing after the switch"
    );

    let heavy = lines_of(&closed);
    check_payloads(&heavy, &trace, &refused.id);
    check_entries(&heavy, job_id, &trace, &destination);
    assert!(
        heavy
            .iter()
            .all(|line| line["request_id"] != pong.id.as_str()),
        "nothing of the ping after the switch"
    );

    let normal = lines_of(&core.files("core."));
    assert!(
        normal
            .iter()
            .all(|line| !line["target"].as_str().unwrap().starts_with("heavy::")),
        "heavy-only lines stay out of the normal file"
    );
    assert!(
        normal
            .iter()
            .any(|line| line["message"] == "heavy logging is on")
    );
}

#[tokio::test]
async fn a_log_bundle_is_saved_on_request() {
    let mut core = start_core();
    let (mut client, _events) = greeted(&core).await;
    let trace = RequestId::new();
    client.set_trace(Some(trace.clone()));
    let saved = client
        .request(Request::SaveLogBundle { minutes: 5 })
        .await
        .unwrap();
    let Response::LogBundle { path } = saved.body else {
        panic!("{:?}", saved.body);
    };
    let path = PathBuf::from(path);
    assert_eq!(path.parent(), Some(core.logs().as_path()));
    let name = path.file_name().unwrap().to_string_lossy().into_owned();
    assert!(name.starts_with("bundle-"), "{name}");
    assert!(
        Path::new(&name)
            .extension()
            .is_some_and(|extension| extension == "zip"),
        "{name}"
    );

    let bytes = fs::read(&path).unwrap();
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
    let names: Vec<String> = archive.file_names().map(str::to_owned).collect();
    let log = names
        .iter()
        .find(|name| name.starts_with("core.") && is_jsonl(name))
        .unwrap_or_else(|| panic!("the core's log is in the bundle: {names:?}"))
        .clone();
    let mut text = String::new();
    std::io::Read::read_to_string(&mut archive.by_name(&log).unwrap(), &mut text).unwrap();
    assert!(text.contains("core started"), "{text}");
    let mut manifest = String::new();
    std::io::Read::read_to_string(&mut archive.by_name("bundle.json").unwrap(), &mut manifest)
        .unwrap();
    let manifest: Value = serde_json::from_str(&manifest).unwrap();
    assert_eq!(manifest["reason"], "asked");
    assert_eq!(manifest["minutes"], 5);
    assert_eq!(manifest["config"]["logging"]["heavy"], false);
    assert!(
        manifest["windows_build"]
            .as_str()
            .is_some_and(|build| build.starts_with("10.0.")),
        "{manifest}"
    );

    let refused = client
        .request(Request::SaveLogBundle { minutes: 0 })
        .await
        .unwrap();
    assert!(
        matches!(refused.body, Response::Error { .. }),
        "{:?}",
        refused.body
    );
    core.stop(&mut client).await;
}

/// The refused command's request and reply are in the heavy file, the
/// request with its trace and its token masked.
fn check_payloads(heavy: &[Value], trace: &RequestId, refused: &RequestId) {
    let request = heavy
        .iter()
        .find(|line| {
            line["message"] == "request payload"
                && line["fields"]["payload"]
                    .as_str()
                    .is_some_and(|payload| payload.contains("no.such.command"))
        })
        .expect("the request's JSON is in the heavy file");
    assert_eq!(request["target"], "heavy::core");
    assert_eq!(request["trace_id"], trace.as_str());
    let payload: Value =
        serde_json::from_str(request["fields"]["payload"].as_str().unwrap()).unwrap();
    assert_eq!(payload["args"]["token"], "***", "secrets are masked");
    assert_eq!(payload["args"]["path"], "C:\\x");
    let reply = heavy
        .iter()
        .find(|line| line["message"] == "reply payload" && line["request_id"] == refused.as_str())
        .expect("the reply's JSON is in the heavy file");
    assert!(
        reply["fields"]["payload"]
            .as_str()
            .unwrap()
            .contains("unknown_command")
    );
}

/// One `entry done` line per copied file, with the job's trace.
fn check_entries(heavy: &[Value], job_id: u64, trace: &RequestId, destination: &Path) {
    let entries: Vec<&Value> = heavy
        .iter()
        .filter(|line| line["message"] == "entry done" && line["fields"]["job_id"] == json!(job_id))
        .collect();
    assert_eq!(entries.len(), 2, "one line per file: {entries:#?}");
    for entry in &entries {
        assert_eq!(entry["target"], "heavy::jobs");
        assert_eq!(entry["trace_id"], trace.as_str());
        assert_eq!(entry["fields"]["kind"], "file");
        assert_eq!(entry["fields"]["outcome"], "done");
        assert_eq!(entry["fields"]["bytes"], json!(5));
        let to = entry["fields"]["to"].as_str().unwrap();
        assert!(Path::new(to).starts_with(destination), "{to}");
        assert!(entry["fields"]["ms"].is_u64());
    }
}
