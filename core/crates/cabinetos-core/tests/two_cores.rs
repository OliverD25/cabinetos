//! Two windows are two cores on one configuration: the same
//! `cabinetos.json`, themes folder, marketplace folder, log folder and
//! indexer. Each test starts two real `cabinetos-core.exe` processes on
//! folders they share, under `%TEMP%\cabinetos-core-test\`.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::{Duration, Instant};

use cabinetos_indexer::{IndexSource, PIPE_SDDL, SearchReply, serve};
use cabinetos_ipc::{PipeClient, PipeName, PipeServer};
use cabinetos_protocol::{
    Envelope, Event, FileHit, HitKind, IndexState, IndexerErrorCode, Request, Response,
    SearchSource, VolumeStatus,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;
use tokio_util::sync::CancellationToken;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(10);

/// The folders two cores share.
struct Shared {
    dir: TempDir,
}

impl Shared {
    fn new(prefix: &str) -> Self {
        let root = std::env::temp_dir().join("cabinetos-core-test");
        fs::create_dir_all(&root).unwrap();
        Self {
            dir: tempfile::Builder::new()
                .prefix(prefix)
                .tempdir_in(root)
                .unwrap(),
        }
    }

    fn path(&self, name: &str) -> PathBuf {
        self.dir.path().join(name)
    }

    fn config(&self) -> PathBuf {
        self.path("cabinetos.json")
    }
}

struct Core {
    child: Child,
    pipe: PipeName,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

/// A core on `shared`'s folders, its indexer at `indexer` (or nowhere).
fn start(shared: &Shared, indexer: Option<&PipeName>) -> Core {
    let pipe = PipeName::random();
    let indexer = indexer.map_or_else(
        || {
            format!(
                r"\\.\pipe\cabinetos-indexer-none-{}",
                PipeName::random().token()
            )
        },
        |pipe| pipe.as_str().to_owned(),
    );
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(shared.config())
        .arg("--plugins-dir")
        .arg(shared.path("plugins"))
        .arg("--plugins-data-dir")
        .arg(shared.path("plugins-data"))
        .arg("--themes-dir")
        .arg(shared.path("themes"))
        .arg("--tools-dir")
        .arg(shared.path("tools"))
        .arg("--marketplace-dir")
        .arg(shared.path("marketplace"))
        .env("CABINETOS_LOG_DIR", shared.path("logs"))
        .env("CABINETOS_INDEXER_PIPE", indexer)
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe }
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
    let welcome = client.hello("two-cores-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn set_value(path: &str, value: Value) -> Request {
    Request::SetValue {
        path: path.to_owned(),
        value,
    }
}

fn read_json(path: &Path) -> Value {
    let text = fs::read_to_string(path).unwrap();
    serde_json::from_str(&text)
        .unwrap_or_else(|error| panic!("{}: {error}\n{text}", path.display()))
}

/// Waits until `pick` accepts an event.
async fn wait_for(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    what: &str,
    mut pick: impl FnMut(&Event) -> bool,
) {
    let deadline = Instant::now() + EVENT_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        match tokio::time::timeout(left, events.recv()).await {
            Ok(Some(envelope)) if pick(&envelope.body) => return,
            Ok(Some(_)) => {}
            Ok(None) => panic!("the events ended before {what}"),
            Err(tokio::time::error::Elapsed { .. }) => {
                panic!("no {what} within {EVENT_DEADLINE:?}")
            }
        }
    }
}

#[tokio::test]
async fn set_value_from_both_cores_at_once_keeps_every_change() {
    let shared = Shared::new("two-config");
    let (a, b) = (start(&shared, None), start(&shared, None));
    let (mut client_a, mut events_a) = greeted(&a).await;
    let (mut client_b, mut events_b) = greeted(&b).await;
    for round in 0..25 {
        let pinned = json!([format!(r"A:\round {round}")]);
        let index = json!(format!(r"B:\round {round}\index.json"));
        let (from_a, from_b) = tokio::join!(
            ask(&mut client_a, set_value("ui.pinned", pinned.clone())),
            ask(&mut client_b, set_value("marketplace.index", index.clone())),
        );
        assert_eq!(from_a, Response::Ok, "round {round}");
        assert_eq!(from_b, Response::Ok, "round {round}");
        // The file is whole JSON and holds both changes: neither core
        // wrote over the other's.
        let file = read_json(&shared.config());
        assert_eq!(file["ui"]["pinned"], pinned, "round {round}: {file}");
        assert_eq!(file["marketplace"]["index"], index, "round {round}: {file}");
    }
    // Each core heard the other's changes through the file.
    wait_for(&mut events_a, "B's change at A", |event| {
        matches!(event, Event::ConfigChanged { changed } if changed.iter().any(|path| path == "marketplace.index"))
    })
    .await;
    wait_for(&mut events_b, "A's change at B", |event| {
        matches!(event, Event::ConfigChanged { changed } if changed.iter().any(|path| path == "ui.pinned"))
    })
    .await;
}

#[tokio::test]
async fn both_cores_writing_the_shipped_themes_at_start_leave_whole_files() {
    for round in 0..5 {
        let shared = Shared::new("two-themes");
        let (a, b) = (start(&shared, None), start(&shared, None));
        let _clients = (greeted(&a).await, greeted(&b).await);
        let themes = shared.path("themes");
        let mut found = Vec::new();
        for entry in fs::read_dir(&themes).unwrap() {
            let path = entry.unwrap().path();
            let name = path.file_name().unwrap().to_string_lossy().into_owned();
            assert!(
                path.extension().is_none_or(|extension| extension != "tmp"),
                "round {round}: a temporary file stayed: {name}"
            );
            // The record of shipped themes and the schema: whole JSON.
            if name.starts_with('.') || name == "theme.schema.json" {
                read_json(&path);
                continue;
            }
            let (_, shipped) = cabinetos_themes::SHIPPED
                .iter()
                .find(|(id, _)| format!("{id}.json") == name)
                .unwrap_or_else(|| panic!("round {round}: {name} is not a shipped theme"));
            assert_eq!(
                fs::read_to_string(&path).unwrap(),
                *shipped,
                "round {round}: {name} is whole"
            );
            found.push(name);
        }
        assert_eq!(
            found.len(),
            cabinetos_themes::SHIPPED.len(),
            "round {round}: {found:?}"
        );
    }
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

/// A local marketplace index offering two themes, `alpha` and `beta`.
fn two_themes_index(dir: &Path) {
    fs::create_dir_all(dir.join("files")).unwrap();
    let items: Vec<Value> = ["alpha", "beta"]
        .into_iter()
        .map(|id| {
            let mut theme: Value = serde_json::from_str(cabinetos_themes::SHIPPED[1].1).unwrap();
            theme["id"] = json!(id);
            theme["name"] = json!(id);
            theme["version"] = json!("1.0.0");
            let bytes = serde_json::to_vec(&theme).unwrap();
            let file = format!("files/{id}.bin");
            fs::write(dir.join(&file), &bytes).unwrap();
            json!({
                "id": id,
                "kind": "theme",
                "name": id,
                "author": {"name": "Tester", "verified": false},
                "version": "1.0.0",
                "description": "A test theme.",
                "size": bytes.len(),
                "download": {"url": file, "sha256": sha256(&bytes)},
                "manifest": {"id": id},
                "minCoreVersion": "0.1.0",
                "license": "MIT"
            })
        })
        .collect();
    let index = json!({"schemaVersion": 1, "generatedAt": "2026-09-29T00:00:00Z", "items": items});
    fs::write(dir.join("index.json"), index.to_string()).unwrap();
}

async fn installed(
    client: &mut PipeClient,
    events: &mut UnboundedReceiver<Envelope<Event>>,
    id: &str,
) {
    wait_for(events, &format!("{id}'s install_finished"), |event| {
        if let Event::InstallFinished {
            extension_id,
            ok,
            message,
            ..
        } = event
            && extension_id == id
        {
            assert!(ok, "{id}: {message:?}");
            return true;
        }
        false
    })
    .await;
    let _ = client;
}

#[tokio::test]
async fn two_installs_at_once_keep_both_in_the_record() {
    let shared = Shared::new("two-market");
    let index = shared.path("index");
    two_themes_index(&index);
    fs::write(
        shared.config(),
        // The same folder for the themes: it has no themes.json, so the
        // index's theme items stand in (ADR 0022) and nothing is fetched
        // from the public site.
        json!({"marketplace": {"index": index.display().to_string(), "themes": index.display().to_string()}}).to_string(),
    )
    .unwrap();
    let (a, b) = (start(&shared, None), start(&shared, None));
    let (mut client_a, mut events_a) = greeted(&a).await;
    let (mut client_b, mut events_b) = greeted(&b).await;
    let record = shared.path("marketplace").join("installed.json");
    for round in 0..3 {
        let install = |id: &str| Request::InstallExtension {
            extension_id: id.to_owned(),
            version: None,
        };
        let (from_a, from_b) = tokio::join!(
            ask(&mut client_a, install("alpha")),
            ask(&mut client_b, install("beta")),
        );
        assert_eq!(
            (from_a, from_b),
            (Response::Ok, Response::Ok),
            "round {round}"
        );
        installed(&mut client_a, &mut events_a, "alpha").await;
        installed(&mut client_b, &mut events_b, "beta").await;
        let kept = read_json(&record);
        assert!(
            kept.get("alpha").is_some() && kept.get("beta").is_some(),
            "round {round}: both installs are on record: {kept}"
        );
        assert!(shared.path("themes").join("alpha.json").is_file());
        assert!(shared.path("themes").join("beta.json").is_file());

        let uninstall = |id: &str| Request::UninstallExtension {
            extension_id: id.to_owned(),
        };
        let (from_a, from_b) = tokio::join!(
            ask(&mut client_a, uninstall("alpha")),
            ask(&mut client_b, uninstall("beta")),
        );
        assert_eq!(
            (from_a, from_b),
            (Response::Ok, Response::Ok),
            "round {round}"
        );
        let kept = read_json(&record);
        assert!(
            kept.get("alpha").is_none() && kept.get("beta").is_none(),
            "round {round}: both uninstalls are on record: {kept}"
        );
    }
}

#[tokio::test]
async fn a_core_that_crashes_leaves_the_other_unharmed() {
    let shared = Shared::new("two-crash");
    let (mut a, b) = (start(&shared, None), start(&shared, None));
    let (mut client_a, _events_a) = greeted(&a).await;
    let (mut client_b, _events_b) = greeted(&b).await;
    // A dies in the middle of writing the settings, as a crash would.
    let writing = tokio::spawn(async move {
        for round in 0..1000 {
            let request = set_value("ui.pinned", json!([format!(r"A:\{round}")]));
            if client_a.request(request).await.is_err() {
                break;
            }
        }
    });
    tokio::time::sleep(Duration::from_millis(200)).await;
    a.child.kill().unwrap();
    a.child.wait().unwrap();
    let _ = writing.await;

    assert!(matches!(
        ask(&mut client_b, Request::Ping).await,
        Response::Pong { .. }
    ));
    for round in 0..5 {
        let index = json!(format!(r"B:\after the crash {round}"));
        assert_eq!(
            ask(&mut client_b, set_value("marketplace.index", index.clone())).await,
            Response::Ok,
            "round {round}"
        );
        assert_eq!(read_json(&shared.config())["marketplace"]["index"], index);
    }
    let Response::Config { config, .. } = ask(&mut client_b, Request::GetConfig).await else {
        panic!("expected config");
    };
    assert_eq!(
        config["marketplace"]["index"],
        json!(r"B:\after the crash 4")
    );
}

/// Answers like the indexer, from a fixed list.
struct Stand;

impl IndexSource for Stand {
    fn status(&self) -> Vec<VolumeStatus> {
        vec![VolumeStatus {
            letter: 'C',
            state: IndexState::Ready,
            entries: 1_000,
            built_in_ms: Some(10),
            journal_lag: Some(0),
        }]
    }

    fn search(
        &self,
        query: &str,
        _limit: usize,
        _root: Option<&str>,
    ) -> Result<SearchReply, (IndexerErrorCode, String)> {
        Ok(SearchReply {
            hits: vec![FileHit {
                path: format!(r"C:\indexed\{query}.txt"),
                kind: HitKind::File,
                frn: Some(0x0001_0000_0000_1234),
            }],
            complete: true,
        })
    }
}

#[tokio::test]
async fn the_indexer_serves_both_cores_at_once() {
    let indexer = PipeName::from_full(format!(
        r"\\.\pipe\cabinetos-indexer-test-{:016x}",
        rand::random::<u64>()
    ));
    let server = PipeServer::bind_with_sddl(&indexer, PIPE_SDDL).unwrap();
    let stop = CancellationToken::new();
    let serving = tokio::spawn(serve(server, Arc::new(Stand), stop.clone()));
    let shared = Shared::new("two-indexer");
    let (a, b) = (
        start(&shared, Some(&indexer)),
        start(&shared, Some(&indexer)),
    );
    let (mut client_a, _) = greeted(&a).await;
    let (mut client_b, _) = greeted(&b).await;
    let search = |query: &str| Request::Search {
        query: query.to_owned(),
        limit: 10,
        root: None,
    };
    for round in 0..20 {
        let (from_a, from_b) = tokio::join!(
            ask(&mut client_a, search(&format!("a{round}"))),
            ask(&mut client_b, search(&format!("b{round}"))),
        );
        for (reply, query) in [(from_a, format!("a{round}")), (from_b, format!("b{round}"))] {
            let Response::FileSearchResults { hits, source, .. } = reply else {
                panic!("expected hits, got {reply:?}");
            };
            assert_eq!(source, SearchSource::Index, "{query}");
            assert_eq!(hits[0].path, format!(r"C:\indexed\{query}.txt"));
        }
    }
    stop.cancel();
    let _ = serving.await;
}

#[tokio::test]
async fn two_cores_log_into_one_file_without_losing_a_line() {
    let shared = Shared::new("two-logs");
    let (mut a, mut b) = (start(&shared, None), start(&shared, None));
    let (client_a, _) = greeted(&a).await;
    let (client_b, _) = greeted(&b).await;
    let pings = |mut client: PipeClient| async move {
        let mut ids = Vec::new();
        for _ in 0..300 {
            let reply = client.request(Request::Ping).await.unwrap();
            assert!(matches!(reply.body, Response::Pong { .. }), "{reply:?}");
            ids.push(reply.id.to_string());
        }
        let _ = client.request(Request::Shutdown).await;
        ids
    };
    let (ids_a, ids_b) = tokio::join!(pings(client_a), pings(client_b));
    for core in [&mut a, &mut b] {
        let deadline = Instant::now() + STARTUP_DEADLINE;
        while core.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < deadline, "a core did not stop");
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    let mut handled = std::collections::HashMap::<String, usize>::new();
    let mut lines = 0;
    for entry in fs::read_dir(shared.path("logs")).unwrap() {
        let path = entry.unwrap().path();
        if path
            .extension()
            .is_none_or(|extension| extension != "jsonl")
        {
            continue;
        }
        for line in fs::read_to_string(&path).unwrap().lines() {
            let parsed: Value = serde_json::from_str(line)
                .unwrap_or_else(|error| panic!("a torn line ({error}): {line}"));
            lines += 1;
            if parsed["message"] == "request handled"
                && let Some(id) = parsed["request_id"].as_str()
            {
                *handled.entry(id.to_owned()).or_default() += 1;
            }
        }
    }
    for id in ids_a.iter().chain(&ids_b) {
        assert_eq!(
            handled.get(id),
            Some(&1),
            "request {id} has one line; {lines} lines in all"
        );
    }
}
