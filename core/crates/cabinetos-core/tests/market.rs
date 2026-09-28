//! The marketplace end to end: the real `cabinetos-core.exe` with its own
//! folders in `%TEMP%\cabinetos-core-test\`, a local index built from the
//! committed `hello` fixture plugin, and a real client. Nothing here
//! reaches the network.

use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    CapabilityLevel, Envelope, ErrorCode, Event, ExtensionKind, PluginInfo, PluginState, Request,
    Response,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// Long enough to compile a component in a debug build on a slow runner.
const SETTLE_DEADLINE: Duration = Duration::from_secs(60);

fn fixture(file: &str) -> Vec<u8> {
    fs::read(
        Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../../sdk/fixtures/plugins/hello")
            .join(file),
    )
    .unwrap()
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

fn zip(files: &[(&str, &[u8])]) -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    for (name, bytes) in files {
        writer
            .start_file(*name, zip::write::SimpleFileOptions::default())
            .unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

/// An index item offering `bytes`, written next to the index in `dir`.
fn offer(dir: &Path, kind: &str, id: &str, bytes: &[u8], extra: &Value) -> Value {
    let file = format!("files/{id}.bin");
    fs::create_dir_all(dir.join("files")).unwrap();
    fs::write(dir.join(&file), bytes).unwrap();
    let mut item = json!({
        "id": id,
        "kind": kind,
        "name": id,
        "author": {"name": "Tester", "verified": false},
        "version": "1.0.0",
        "description": "A test item.",
        "size": bytes.len(),
        "download": {"url": file, "sha256": sha256(bytes)},
        "manifest": {"id": id},
        "minCoreVersion": "0.1.0",
        "license": "MIT"
    });
    for (key, value) in extra.as_object().unwrap() {
        item[key] = value.clone();
    }
    item
}

fn hello_item(dir: &Path) -> Value {
    let manifest: Value = serde_json::from_slice(&fixture("plugin.json")).unwrap();
    let package = zip(&[
        ("plugin.json", &fixture("plugin.json")),
        ("plugin.wasm", &fixture("plugin.wasm")),
    ]);
    offer(
        dir,
        "plugin",
        "hello",
        &package,
        &json!({
            "version": manifest["version"],
            "capabilities": manifest["capabilities"],
            "manifest": manifest,
        }),
    )
}

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
    fn path(&self, name: &str) -> PathBuf {
        self.dir.path().join(name)
    }
}

/// Starts a core whose index is `index/index.json` in its folder, holding
/// what `items` makes, with `config` (the index location is added).
fn start_core(items: impl FnOnce(&Path) -> Vec<Value>, config: &Value) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("market")
        .tempdir_in(root)
        .unwrap();
    let index_dir = dir.path().join("index");
    fs::create_dir_all(&index_dir).unwrap();
    let index = json!({"schemaVersion": 1, "generatedAt": "2026-09-28T00:00:00Z", "items": items(&index_dir)});
    fs::write(index_dir.join("index.json"), index.to_string()).unwrap();
    let mut config = config.clone();
    if config.pointer("/marketplace/index").is_none() {
        config["marketplace"] = json!({"index": index_dir.display().to_string()});
    }
    let config_path = dir.path().join("cabinetos.json");
    fs::write(&config_path, serde_json::to_string_pretty(&config).unwrap()).unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .arg("--plugins-dir")
        .arg(dir.path().join("plugins"))
        .arg("--plugins-data-dir")
        .arg(dir.path().join("plugins-data"))
        .arg("--themes-dir")
        .arg(dir.path().join("themes"))
        .arg("--tools-dir")
        .arg(dir.path().join("tools"))
        .arg("--marketplace-dir")
        .arg(dir.path().join("marketplace"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
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
    let welcome = client.hello("market-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn install(id: &str) -> Request {
    Request::InstallExtension {
        extension_id: id.to_owned(),
        version: None,
    }
}

fn uninstall(id: &str) -> Request {
    Request::UninstallExtension {
        extension_id: id.to_owned(),
    }
}

fn error_of(reply: Response) -> (ErrorCode, String) {
    match reply {
        Response::Error { code, message } => (code, message),
        other => panic!("expected an error, got {other:?}"),
    }
}

async fn plugins(client: &mut PipeClient) -> Vec<PluginInfo> {
    match ask(client, Request::ListPlugins).await {
        Response::Plugins { plugins } => plugins,
        other => panic!("expected plugins, got {other:?}"),
    }
}

/// The plugin's state once it has left `loading`.
async fn settled(client: &mut PipeClient, id: &str) -> PluginState {
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let state = plugins(client)
            .await
            .into_iter()
            .find(|plugin| plugin.id == id)
            .map(|plugin| plugin.state);
        match state {
            Some(PluginState::Loading) | None if Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Some(state) => return state,
            None => panic!("{id} never appeared"),
        }
    }
}

/// Every event that arrives within `wait`.
async fn events_within(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    wait: Duration,
) -> Vec<Event> {
    let until = Instant::now() + wait;
    let mut seen = Vec::new();
    while let Ok(Some(event)) = tokio::time::timeout(
        until.saturating_duration_since(Instant::now()),
        events.recv(),
    )
    .await
    {
        seen.push(event.body);
    }
    seen
}

fn files_under(dir: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                found.extend(files_under(&path));
            } else {
                found.push(path);
            }
        }
    }
    found
}

/// Phase 9's goal for the marketplace: a plugin from a local index installs
/// with its hash checked, waits for review, and uninstalls exactly.
#[tokio::test]
async fn a_plugin_from_a_local_index_installs_for_review_and_uninstalls_exactly() {
    // Grants left over from an earlier install must not carry over.
    let core = start_core(
        |dir| vec![hello_item(dir)],
        &json!({"plugins": {"hello": {"granted": ["cmd:register", "events:emit"]}}}),
    );
    let (mut client, mut events) = greeted(&core).await;

    let Response::MarketplaceIndex { items, source, .. } =
        ask(&mut client, Request::MarketplaceRefresh).await
    else {
        panic!("expected the index");
    };
    assert_eq!(items.len(), 1);
    assert!(source.ends_with("index.json"), "{source}");
    assert_eq!(items[0].capabilities[0].level, Some(CapabilityLevel::Low));
    let Response::MarketplaceIndex { items, .. } = ask(
        &mut client,
        Request::MarketplaceSearch {
            query: "hel".to_owned(),
            kind: Some(ExtensionKind::Plugin),
        },
    )
    .await
    else {
        panic!("expected search results");
    };
    assert_eq!(items[0].id, "hello");

    assert_eq!(ask(&mut client, install("hello")).await, Response::Ok);
    let seen = events_within(&mut events, Duration::from_millis(500)).await;
    let size = fs::metadata(core.path("index").join("files").join("hello.bin"))
        .unwrap()
        .len();
    assert!(seen.iter().any(|event| matches!(
        event,
        Event::InstallProgress { extension_id, bytes, total } if extension_id == "hello" && *bytes == size && *total == size
    )), "{seen:?}");
    assert!(seen.iter().any(|event| matches!(
        event,
        Event::InstallFinished { extension_id, ok: true, message } if extension_id == "hello" && message.contains("installed hello 0.1.0")
    )), "{seen:?}");

    let state = settled(&mut client, "hello").await;
    assert_eq!(
        state,
        PluginState::NeedsReview {
            missing: vec!["cmd:register".to_owned(), "events:emit".to_owned()]
        }
    );
    let config: Value =
        serde_json::from_str(&fs::read_to_string(core.path("cabinetos.json")).unwrap()).unwrap();
    assert_eq!(config["plugins"]["hello"]["granted"], json!([]));
    let installed = core.path("plugins").join("hello");
    assert!(installed.join("plugin.json").is_file() && installed.join("plugin.wasm").is_file());

    // The plugin's own data folder and a file the user added stay.
    let data = core.path("plugins-data").join("hello");
    fs::create_dir_all(&data).unwrap();
    fs::write(data.join("state.json"), "{}").unwrap();
    fs::write(installed.join("notes.txt"), "mine").unwrap();
    assert_eq!(ask(&mut client, uninstall("hello")).await, Response::Ok);
    assert!(plugins(&mut client).await.is_empty());
    assert_eq!(
        files_under(&installed),
        [installed.join("notes.txt")],
        "only the installed files go"
    );
    assert!(data.join("state.json").is_file());
    let (error_code, _) = error_of(ask(&mut client, uninstall("hello")).await);
    assert_eq!(error_code, ErrorCode::NoSuchExtension);
}

#[tokio::test]
async fn a_wrong_hash_fails_and_leaves_no_files() {
    let core = start_core(
        |dir| {
            let mut item = hello_item(dir);
            item["download"]["sha256"] = json!("f".repeat(64));
            vec![item]
        },
        &json!({}),
    );
    let (mut client, mut events) = greeted(&core).await;
    let (error_code, message) = error_of(ask(&mut client, install("hello")).await);
    assert_eq!(error_code, ErrorCode::HashMismatch);
    assert!(message.contains("nothing was installed"), "{message}");
    let seen = events_within(&mut events, Duration::from_millis(300)).await;
    assert!(
        seen.iter()
            .any(|event| matches!(event, Event::InstallFinished { ok: false, .. })),
        "{seen:?}"
    );
    assert!(plugins(&mut client).await.is_empty());
    assert!(files_under(&core.path("plugins")).is_empty());
    assert!(files_under(&core.path("marketplace").join("downloads")).is_empty());
    assert!(!core.path("marketplace").join("installed.json").exists());
}

#[tokio::test]
async fn a_plain_http_index_is_refused_without_reaching_it() {
    let core = start_core(
        |_| Vec::new(),
        &json!({"marketplace": {"index": "http://127.0.0.1:9/index.json"}}),
    );
    let (mut client, _events) = greeted(&core).await;
    for request in [Request::MarketplaceRefresh, install("hello")] {
        let (error_code, message) = error_of(ask(&mut client, request).await);
        assert_eq!(error_code, ErrorCode::MarketplaceError);
        assert!(message.contains("plain http"), "{message}");
    }
}

/// A theme that `ui.theme` names applies as soon as it is installed; the
/// theme in effect cannot be uninstalled; a tool install tells clients.
#[tokio::test]
async fn a_theme_and_a_tool_install_with_their_events() {
    let core = start_core(
        |dir| {
            let mut theme: Value = serde_json::from_str(cabinetos_themes::SHIPPED[1].1).unwrap();
            theme["id"] = json!("solarized");
            theme["version"] = json!("1.0.0");
            let tool = zip(&[
                (
                    "tool.json",
                    br#"{"id":"md-preview","name":"Markdown Preview","version":"1.0.0"}"#,
                ),
                ("index.html", b"<!doctype html>"),
            ]);
            vec![
                offer(
                    dir,
                    "theme",
                    "solarized",
                    &serde_json::to_vec(&theme).unwrap(),
                    &json!({}),
                ),
                offer(dir, "tool", "md-preview", &tool, &json!({})),
            ]
        },
        &json!({"ui": {"theme": "solarized"}}),
    );
    let (mut client, mut events) = greeted(&core).await;

    assert_eq!(ask(&mut client, install("solarized")).await, Response::Ok);
    let seen = events_within(&mut events, Duration::from_millis(500)).await;
    assert!(
        seen.iter().any(|event| matches!(
            event,
            Event::ThemeChanged { theme } if theme.id == "solarized"
        )),
        "{seen:?}"
    );
    let (error_code, message) = error_of(ask(&mut client, uninstall("solarized")).await);
    assert_eq!(error_code, ErrorCode::MarketplaceError);
    assert!(message.contains("theme in effect"), "{message}");
    assert!(core.path("themes").join("solarized.json").is_file());

    assert_eq!(ask(&mut client, install("md-preview")).await, Response::Ok);
    let seen = events_within(&mut events, Duration::from_millis(300)).await;
    assert!(
        seen.iter().any(|event| matches!(
            event,
            Event::ToolsChanged { tools } if tools.len() == 1 && tools[0].id == "md-preview"
        )),
        "{seen:?}"
    );
    let Response::Tools { tools } = ask(&mut client, Request::ListTools).await else {
        panic!("expected tools");
    };
    assert_eq!(tools[0].name, "Markdown Preview");
    assert_eq!(
        ask(&mut client, uninstall("md-preview")).await,
        Response::Ok
    );
    let seen = events_within(&mut events, Duration::from_millis(300)).await;
    assert!(
        seen.iter()
            .any(|event| matches!(event, Event::ToolsChanged { tools } if tools.is_empty())),
        "{seen:?}"
    );
    assert!(!core.path("tools").join("md-preview").exists());
}
