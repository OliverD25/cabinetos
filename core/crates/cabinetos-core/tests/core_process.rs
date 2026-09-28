//! End-to-end tests against the real `cabinetos-core.exe`.
//!
//! Every test starts its own core on a random pipe with its own temporary log
//! directory, so the tests can run in parallel and never touch the real
//! `%LOCALAPPDATA%\CabinetOS\logs`.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::frame::{read_frame, write_frame};
use cabinetos_ipc::{IpcError, MAX_FRAME, PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, PROTOCOL_VERSION, Request, RequestId, Response};
use serde_json::Value;
use tempfile::TempDir;
use tokio::net::windows::named_pipe::ClientOptions;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EXIT_DEADLINE: Duration = Duration::from_secs(10);

/// A running core; killed if a test fails before it exits on its own.
struct Core {
    child: Child,
    pipe: PipeName,
    log_dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn core_command(pipe: &PipeName, log_dir: &Path) -> Command {
    let mut command = Command::new(CORE_EXE);
    command
        .args(["--pipe", pipe.token()])
        .env("CABINETOS_LOG_DIR", log_dir)
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    command
}

fn start_core(extra_args: &[&str]) -> Core {
    let log_dir = tempfile::tempdir().unwrap();
    let pipe = PipeName::random();
    let child = core_command(&pipe, log_dir.path())
        .args(extra_args)
        .spawn()
        .unwrap();
    Core {
        child,
        pipe,
        log_dir,
    }
}

/// Connects once the core has created its pipe.
async fn connect(pipe: &PipeName) -> PipeClient {
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        match PipeClient::connect(pipe, Duration::from_secs(1)).await {
            Ok(client) => return client,
            Err(error) if Instant::now() < deadline => {
                let _ = error;
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) => {
                panic!("the core's pipe did not appear within {STARTUP_DEADLINE:?}: {error}")
            }
        }
    }
}

async fn wait_for_exit(child: &mut Child, deadline: Duration) -> ExitStatus {
    let until = Instant::now() + deadline;
    loop {
        if let Some(status) = child.try_wait().unwrap() {
            return status;
        }
        assert!(
            Instant::now() < until,
            "the core did not exit within {deadline:?}"
        );
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

/// Every line of every `<process>.<date>.jsonl` file in `dir`, parsed.
fn read_log_lines(dir: &Path, process: &str) -> Vec<Value> {
    let mut lines = Vec::new();
    for path in files_in(dir) {
        let name = path.file_name().unwrap().to_string_lossy().into_owned();
        let is_log = name.starts_with(&format!("{process}."))
            && path
                .extension()
                .is_some_and(|extension| extension == "jsonl");
        if is_log {
            let text = fs::read_to_string(&path).unwrap();
            lines.extend(text.lines().map(|line| {
                serde_json::from_str::<Value>(line)
                    .unwrap_or_else(|error| panic!("not a JSON line ({error}): {line}"))
            }));
        }
    }
    lines
}

fn files_in(dir: &Path) -> Vec<PathBuf> {
    fs::read_dir(dir)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .collect()
}

#[tokio::test]
async fn ping_round_trip_is_logged_with_its_request_id() {
    let mut core = start_core(&[]);
    let mut client = connect(&core.pipe).await;

    let id = RequestId::new();
    let reply = client
        .request_with_id(id.clone(), Request::Ping)
        .await
        .unwrap();
    assert_eq!(reply.id, id);
    match reply.body {
        Response::Pong {
            protocol_version,
            core_version,
        } => {
            assert_eq!(protocol_version, PROTOCOL_VERSION);
            assert_eq!(protocol_version, 3);
            assert_eq!(core_version, env!("CARGO_PKG_VERSION"));
        }
        other => panic!("expected pong, got {other:?}"),
    }

    let shutdown = client.request(Request::Shutdown).await.unwrap();
    assert_eq!(shutdown.body, Response::Ok);
    drop(client);
    let status = wait_for_exit(&mut core.child, EXIT_DEADLINE).await;
    assert_eq!(status.code(), Some(0));

    let lines = read_log_lines(core.log_dir.path(), "core");
    assert!(
        lines
            .iter()
            .any(|line| line["request_id"] == id.as_str() && line["boundary"] == "engine"),
        "no engine log line with request_id {id}: {lines:#?}"
    );
    assert!(lines.iter().any(|line| line["message"] == "core started"));
    assert!(lines.iter().any(|line| line["message"] == "core stopped"));
}

#[tokio::test]
async fn a_panic_writes_a_crash_trace_and_flushes_the_log() {
    let log_dir = tempfile::tempdir().unwrap();
    let mut child = core_command(&PipeName::random(), log_dir.path())
        .arg("--self-test-panic")
        .spawn()
        .unwrap();
    let status = wait_for_exit(&mut child, Duration::from_secs(10)).await;
    assert!(!status.success(), "a panicking core must not exit with 0");

    let crash_files: Vec<PathBuf> = files_in(log_dir.path())
        .into_iter()
        .filter(|path| {
            let name = path.file_name().unwrap().to_string_lossy();
            name.starts_with("crash-")
                && path
                    .extension()
                    .is_some_and(|extension| extension == "json")
        })
        .collect();
    assert_eq!(crash_files.len(), 1, "{crash_files:?}");
    let crash: Value = serde_json::from_str(&fs::read_to_string(&crash_files[0]).unwrap()).unwrap();

    assert!(
        crash["message"]
            .as_str()
            .unwrap()
            .contains("self-test panic")
    );
    assert_eq!(crash["boundary"], "engine");
    assert_eq!(crash["process"], "core");
    assert_eq!(crash["version"], env!("CARGO_PKG_VERSION"));
    assert!(
        crash["location"]["file"]
            .as_str()
            .unwrap()
            .ends_with("main.rs")
    );
    assert!(crash["location"]["line"].as_u64().unwrap() > 0);
    assert_eq!(crash["thread"], "main");
    assert!(!crash["backtrace"].as_str().unwrap().is_empty());
    let recent = crash["recent_events"].as_array().unwrap();
    assert!(
        recent
            .iter()
            .any(|event| event["message"] == "about to panic (self-test)"),
        "{recent:#?}"
    );

    // The INFO line reached the log file although the process died right
    // after it: the panic hook flushed the background writer.
    let lines = read_log_lines(log_dir.path(), "core");
    assert!(
        lines.iter().any(|line| {
            line["message"] == "about to panic (self-test)" && line["level"] == "INFO"
        }),
        "{lines:#?}"
    );
}

#[tokio::test]
async fn the_core_exits_when_its_parent_exits() {
    // A stand-in parent that waits for a key press that never comes.
    let mut parent = Command::new("cmd.exe")
        .args(["/c", "pause"])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .spawn()
        .unwrap();
    let mut core = start_core(&["--parent-pid", &parent.id().to_string()]);

    // Once the pipe answers, the watch is in place (it starts before the
    // core accepts clients).
    let mut client = connect(&core.pipe).await;
    client.request(Request::Ping).await.unwrap();
    drop(client);

    parent.kill().unwrap();
    parent.wait().unwrap();
    let status = wait_for_exit(&mut core.child, EXIT_DEADLINE).await;
    assert_eq!(status.code(), Some(0));

    let lines = read_log_lines(core.log_dir.path(), "core");
    assert!(
        lines
            .iter()
            .any(|line| line["message"] == "parent process exited; shutting down"),
        "{lines:#?}"
    );
}

#[tokio::test]
async fn a_missing_parent_is_an_error() {
    let log_dir = tempfile::tempdir().unwrap();
    // PID 0 is the System Idle Process, which cannot be opened.
    let mut child = core_command(&PipeName::random(), log_dir.path())
        .args(["--parent-pid", "0"])
        .spawn()
        .unwrap();
    let status = wait_for_exit(&mut child, EXIT_DEADLINE).await;
    assert_eq!(status.code(), Some(1));
}

/// Opens a plain pipe client, retrying while the core has not yet created
/// the next pipe instance (`ERROR_PIPE_BUSY`), as `PipeClient::connect` does.
async fn open_raw(pipe: &PipeName) -> tokio::net::windows::named_pipe::NamedPipeClient {
    const ERROR_PIPE_BUSY: i32 = 231;
    let deadline = Instant::now() + STARTUP_DEADLINE;
    loop {
        match ClientOptions::new().open(pipe.as_str()) {
            Ok(client) => return client,
            Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY) => {
                assert!(Instant::now() < deadline, "the pipe stayed busy: {error}");
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            Err(error) => panic!("cannot open the pipe: {error}"),
        }
    }
}

/// Sends one raw frame and reads the reply envelope.
async fn exchange(
    pipe: &mut tokio::net::windows::named_pipe::NamedPipeClient,
    payload: &[u8],
) -> Envelope<Response> {
    write_frame(pipe, payload).await.unwrap();
    serde_json::from_slice(&read_frame(pipe).await.unwrap()).unwrap()
}

#[tokio::test]
async fn bad_frames_get_error_replies() {
    let mut core = start_core(&[]);
    let mut client = connect(&core.pipe).await;
    let mut raw = open_raw(&core.pipe).await;

    let id = RequestId::new();
    let unknown = format!(r#"{{"id":"{id}","type":"format_disk"}}"#);
    let reply = exchange(&mut raw, unknown.as_bytes()).await;
    assert_eq!(reply.id, id);
    assert!(matches!(
        reply.body,
        Response::Error {
            code: ErrorCode::UnknownRequest,
            ..
        }
    ));

    let reply = exchange(&mut raw, b"this is not json").await;
    assert!(matches!(
        reply.body,
        Response::Error {
            code: ErrorCode::ProtocolError,
            ..
        }
    ));

    // The connection survives bad JSON: a valid request still works.
    let id = RequestId::new();
    let ping = format!(r#"{{"id":"{id}","type":"ping"}}"#);
    let reply = exchange(&mut raw, ping.as_bytes()).await;
    assert_eq!(reply.id, id);
    assert!(matches!(reply.body, Response::Pong { .. }));

    // An oversized frame gets an error reply, then the connection closes.
    let too_long = u32::try_from(MAX_FRAME + 1).unwrap().to_le_bytes();
    tokio::io::AsyncWriteExt::write_all(&mut raw, &too_long)
        .await
        .unwrap();
    let reply: Envelope<Response> =
        serde_json::from_slice(&read_frame(&mut raw).await.unwrap()).unwrap();
    assert!(matches!(
        reply.body,
        Response::Error {
            code: ErrorCode::FrameTooLarge,
            ..
        }
    ));
    assert!(matches!(
        read_frame(&mut raw).await,
        Err(IpcError::Closed | IpcError::Io(_))
    ));

    // Other connections are not affected.
    client.request(Request::Ping).await.unwrap();
    client.request(Request::Shutdown).await.unwrap();
    drop(client);
    drop(raw);
    let status = wait_for_exit(&mut core.child, EXIT_DEADLINE).await;
    assert_eq!(status.code(), Some(0));
}
