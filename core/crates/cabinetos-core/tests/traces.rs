//! Trace IDs end to end: the real `cabinetos-core.exe`, a client that sends
//! one trace with its requests, and the core's log. A job and a plugin call
//! started by a traced request carry that trace in their events and in
//! every log line (docs/diagnostics.md, "How an action's trace id travels").
//! Everything the tests write lives under `%TEMP%\cabinetos-jobs-test\`
//! and is removed.

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
/// Long enough to compile a component in a debug build on a slow runner.
const SETTLE_DEADLINE: Duration = Duration::from_secs(120);

fn scratch(name: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(name)
        .tempdir_in(root)
        .unwrap()
}

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/fixtures/plugins")
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

impl Core {
    /// Asks the core to exit and waits, so its log is complete.
    async fn stop(&mut self, client: &mut PipeClient) {
        let reply = client.request(Request::Shutdown).await.unwrap();
        assert_eq!(reply.body, Response::Ok);
        let until = Instant::now() + STARTUP_DEADLINE;
        while self.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < until, "the core did not exit");
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    /// Every line of the core's log files, parsed.
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

/// A core with copies of the fixture `plugins` and `config` as its
/// configuration file.
fn start_core(plugins: &[&str], config: &Value) -> Core {
    let dir = scratch("traces");
    for id in plugins {
        let to = dir.path().join("plugins").join(id);
        fs::create_dir_all(&to).unwrap();
        for file in ["plugin.json", "plugin.wasm"] {
            fs::copy(fixtures().join(id).join(file), to.join(file)).unwrap();
        }
    }
    let config_path = dir.path().join("cabinetos.json");
    fs::write(&config_path, serde_json::to_string_pretty(config).unwrap()).unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .arg("--plugins-dir")
        .arg(dir.path().join("plugins"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
        .env_remove("CABINETOS_PLUGINS_DIR")
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

/// A client that said hello, with its event stream.
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
    let welcome = client.hello("traces-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

/// The next event that `wanted` accepts, with its envelope.
async fn next_event(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    wanted: impl Fn(&Event) -> bool,
) -> Envelope<Event> {
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        match tokio::time::timeout(left, events.recv()).await {
            Ok(Some(envelope)) if wanted(&envelope.body) => return envelope,
            Ok(Some(_)) => {}
            Ok(None) => panic!("the event stream ended"),
            Err(elapsed) => panic!("the event did not come: {elapsed}"),
        }
    }
}

#[tokio::test]
async fn a_job_carries_the_trace_of_the_request_that_started_it() {
    let mut core = start_core(&[], &json!({}));
    let (mut client, mut events) = greeted(&core).await;
    let files = scratch("trace-job");
    let source = files.path().join("a.txt");
    fs::write(&source, "traced").unwrap();
    let destination = files.path().join("to");
    fs::create_dir(&destination).unwrap();

    let trace = RequestId::new();
    client.set_trace(Some(trace.clone()));
    let started = client
        .request(Request::StartJob(JobRequest {
            kind: JobKind::Copy,
            sources: vec![source.display().to_string()],
            destination: Some(destination.display().to_string()),
            options: JobOptions::default(),
        }))
        .await
        .unwrap();
    assert_eq!(started.trace.as_ref(), Some(&trace), "the reply echoes it");
    let Response::JobStarted { job_id } = started.body else {
        panic!("{:?}", started.body);
    };
    let ended = next_event(&mut events, |event| {
        matches!(event, Event::JobStateChanged { job_id: id, state: JobState::Completed } if *id == job_id)
    })
    .await;
    assert_eq!(ended.trace.as_ref(), Some(&trace));
    assert!(destination.join("a.txt").exists());

    // A request of another action, with no trace: its own ID is its trace.
    client.set_trace(None);
    let pong = client.request(Request::Ping).await.unwrap();
    assert_eq!(pong.trace.as_ref(), Some(&pong.id));
    core.stop(&mut client).await;

    let lines = core.log_lines();
    let job_lines: Vec<&Value> = lines
        .iter()
        .filter(|line| line["fields"]["job_id"] == json!(job_id))
        .collect();
    for message in ["job queued", "job ended"] {
        let line = job_lines
            .iter()
            .find(|line| line["message"] == message)
            .unwrap_or_else(|| panic!("no `{message}` line: {job_lines:#?}"));
        assert_eq!(line["trace_id"], trace.as_str(), "{line}");
    }
    let on_job_thread = job_lines
        .iter()
        .find(|line| line["message"] == "job ended")
        .unwrap();
    assert!(
        on_job_thread["thread"]
            .as_str()
            .is_some_and(|thread| thread.starts_with("job-")),
        "{on_job_thread}"
    );
    assert!(
        job_lines
            .iter()
            .all(|line| line["trace_id"] == trace.as_str()),
        "{job_lines:#?}"
    );
}

#[tokio::test]
async fn a_plugin_call_and_the_event_it_emits_carry_the_trace() {
    let mut core = start_core(
        &["hello"],
        &json!({"plugins": {"hello": {"granted": ["cmd:register", "events:emit"]}}}),
    );
    let (mut client, mut events) = greeted(&core).await;
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let reply = client.request(Request::ListPlugins).await.unwrap();
        let Response::Plugins { plugins } = reply.body else {
            panic!("{:?}", reply.body);
        };
        if plugins.iter().any(|plugin| {
            plugin.id == "hello" && plugin.state == cabinetos_protocol::PluginState::Active
        }) {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "hello did not start: {plugins:?}"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    let trace = RequestId::new();
    client.set_trace(Some(trace.clone()));
    let said = client
        .request(Request::ExecuteCommand {
            command: "hello.say".to_owned(),
            args: Value::Null,
        })
        .await
        .unwrap();
    assert_eq!(said.trace.as_ref(), Some(&trace));
    assert!(
        matches!(said.body, Response::CommandResult { .. }),
        "{:?}",
        said.body
    );
    let emitted = next_event(&mut events, |event| {
        matches!(event, Event::PluginEvent { .. })
    })
    .await;
    assert_eq!(
        emitted.trace.as_ref(),
        Some(&trace),
        "the event carries the call's trace"
    );
    core.stop(&mut client).await;

    let lines = core.log_lines();
    let printed = lines
        .iter()
        .find(|line| line["message"] == "Hello says hello")
        .expect("the plugin's output during the call is in the log");
    assert_eq!(printed["plugin_id"], "hello");
    assert_eq!(printed["trace_id"], trace.as_str());
    assert_eq!(printed["request_id"], said.id.as_str());
    let activated = lines
        .iter()
        .find(|line| {
            line["message"]
                .as_str()
                .is_some_and(|message| message.starts_with("Hello is active"))
        })
        .expect("the plugin's start is in the log");
    assert!(
        activated.get("trace_id").is_none(),
        "the plugin's start is nobody's action: {activated}"
    );
}
