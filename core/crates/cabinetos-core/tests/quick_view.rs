//! Quick View's requests (protocol version 21, ADR 0023), end to end: the
//! real `cabinetos-core.exe` on a random pipe with a real client. Every
//! file and folder lives under `%TEMP%\cabinetos-core-test\`, in a folder
//! removed at the end.

use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, QuickViewKind, Request, Response, ThumbnailReason,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

/// A running core, killed at the end of the test, with its own folder for
/// the configuration, the logs, the caches and the test's files.
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

fn start_core(extra: &[&str]) -> Core {
    start_core_with(extra, |_| None)
}

/// Like [`start_core`], with a local index in `<dir>/index` whose items
/// `items` makes (none: no index), and the marketplace pointed at it.
fn start_core_with(extra: &[&str], items: impl FnOnce(&Path) -> Option<Vec<Value>>) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("quickview")
        .tempdir_in(root)
        .unwrap();
    std::fs::create_dir_all(dir.path().join("files")).unwrap();
    std::fs::create_dir_all(dir.path().join("config")).unwrap();
    let index_dir = dir.path().join("index");
    std::fs::create_dir_all(&index_dir).unwrap();
    let mut config = json!({});
    if let Some(items) = items(&index_dir) {
        let index =
            json!({"schemaVersion": 1, "generatedAt": "2026-10-03T00:00:00Z", "items": items});
        std::fs::write(index_dir.join("index.json"), index.to_string()).unwrap();
        let folder = index_dir.display().to_string();
        config["marketplace"] = json!({"index": folder, "themes": folder});
    }
    std::fs::write(
        dir.path().join("config").join("cabinetos.json"),
        config.to_string(),
    )
    .unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("config").join("cabinetos.json"))
        .arg("--tools-dir")
        .arg(dir.path().join("tools"))
        .arg("--marketplace-dir")
        .arg(dir.path().join("marketplace"))
        .args(extra)
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_CACHE_DIR", dir.path().join("cache"))
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

/// A client that said hello, so it receives the events.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("quickview-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
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

/// The committed fixture viewer's folder.
fn fixture_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/fixtures/tools")
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

/// The fixture viewer as the marketplace ships a tool: a zip of its
/// folder, offered in the index at `dir` with its `tool.json` as manifest.
fn fixture_offer(dir: &Path, min_core: &str) -> Value {
    let folder = fixture_dir().join("quickview-fixture");
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    for name in ["tool.json", "index.html", "quickview.html", "quickview.js"] {
        writer
            .start_file(name, zip::write::SimpleFileOptions::default())
            .unwrap();
        writer
            .write_all(&std::fs::read(folder.join(name)).unwrap())
            .unwrap();
    }
    let bytes = writer.finish().unwrap().into_inner();
    std::fs::create_dir_all(dir.join("files")).unwrap();
    std::fs::write(dir.join("files/quickview-fixture.zip"), &bytes).unwrap();
    let manifest: Value =
        serde_json::from_slice(&std::fs::read(folder.join("tool.json")).unwrap()).unwrap();
    json!({
        "id": "quickview-fixture",
        "kind": "tool",
        "name": "Quick View Fixture",
        "author": {"name": "CabinetOS", "verified": false},
        "version": manifest["version"],
        "description": "The test viewer.",
        "size": bytes.len(),
        "download": {"url": "files/quickview-fixture.zip", "sha256": sha256(&bytes)},
        "manifest": manifest,
        "minCoreVersion": min_core,
        "license": "MIT"
    })
}

fn table_of(event: &Event) -> Option<(Vec<String>, Vec<QuickViewKind>)> {
    match event {
        Event::QuickViewTableChanged { viewers, kinds } => Some((
            viewers.iter().map(|viewer| viewer.id.clone()).collect(),
            kinds.clone(),
        )),
        _ => None,
    }
}

fn kind(pattern: &str, viewers: &[&str], off: bool) -> QuickViewKind {
    QuickViewKind {
        pattern: pattern.to_owned(),
        viewers: viewers.iter().map(|id| (*id).to_owned()).collect(),
        off,
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

/// A PNG of `width` by `height` pixels.
fn write_png(path: &Path, width: u32, height: u32) {
    let mut bytes = Vec::new();
    let mut encoder = png::Encoder::new(&mut bytes, width, height);
    encoder.set_color(png::ColorType::Rgb);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().unwrap();
    let pixels: Vec<u8> = (0..width * height)
        .flat_map(|index| [u8::try_from(index % 251).unwrap(), 40, 200])
        .collect();
    writer.write_image_data(&pixels).unwrap();
    writer.finish().unwrap();
    std::fs::write(path, bytes).unwrap();
}

fn png_size(base64: &str) -> (u32, u32) {
    let bytes = BASE64.decode(base64).unwrap();
    let reader = png::Decoder::new(std::io::Cursor::new(bytes))
        .read_info()
        .unwrap();
    (reader.info().width, reader.info().height)
}

#[tokio::test]
async fn thumbnails_come_from_the_core() {
    let core = start_core(&[]);
    let mut client = connect(&core.pipe).await;
    let photo = core.path("files").join("tall.png");
    write_png(&photo, 300, 600);
    let path = photo.display().to_string();

    let reply = ask(
        &mut client,
        Request::GetThumbnail {
            path: path.clone(),
            size: 256,
            ahead: false,
        },
    )
    .await;
    let Response::Thumbnail {
        width: Some(128),
        height: Some(256),
        png_base64: Some(png),
        reason: None,
        ..
    } = &reply
    else {
        panic!("{reply:?}");
    };
    assert_eq!(png_size(png), (128, 256));

    let unknown = core.path("files").join("notes.xyz");
    std::fs::write(&unknown, b"nobody draws this").unwrap();
    let none = ask(
        &mut client,
        Request::GetThumbnail {
            path: unknown.display().to_string(),
            size: 96,
            ahead: true,
        },
    )
    .await;
    assert!(
        matches!(
            none,
            Response::Thumbnail {
                png_base64: None,
                reason: Some(ThumbnailReason::None),
                ..
            }
        ),
        "{none:?}"
    );

    let wrong_size = ask(
        &mut client,
        Request::GetThumbnail {
            path: path.clone(),
            size: 200,
            ahead: false,
        },
    )
    .await;
    assert_eq!(error_code(&wrong_size), Some(ErrorCode::ProtocolError));
    let relative = ask(
        &mut client,
        Request::GetThumbnail {
            path: "tall.png".to_owned(),
            size: 256,
            ahead: false,
        },
    )
    .await;
    assert_eq!(error_code(&relative), Some(ErrorCode::InvalidPath));
    let gone = ask(
        &mut client,
        Request::GetThumbnail {
            path: core.path("files").join("gone.png").display().to_string(),
            size: 256,
            ahead: false,
        },
    )
    .await;
    assert_eq!(error_code(&gone), Some(ErrorCode::NotFound));
}

#[tokio::test]
async fn drawings_for_a_page_land_in_the_render_cache() {
    let core = start_core(&[]);
    let mut client = connect(&core.pipe).await;
    let photo = core.path("files").join("tall.png");
    write_png(&photo, 300, 600);
    let path = photo.display().to_string();

    let rendered = ask(
        &mut client,
        Request::RenderImage {
            path: path.clone(),
            max_size: 400,
        },
    )
    .await;
    let Response::RenderedImage {
        folder,
        width: 200,
        height: 400,
    } = &rendered
    else {
        panic!("{rendered:?}");
    };
    assert!(Path::new(folder).starts_with(core.path("cache").join("render")));
    assert!(Path::new(folder).join("image.png").is_file());
    let too_big = ask(
        &mut client,
        Request::RenderImage {
            path,
            max_size: 2561,
        },
    )
    .await;
    assert_eq!(error_code(&too_big), Some(ErrorCode::ProtocolError));
}

/// Decision 1.3 and 5.3 of ADR 0023: installing a viewer from a local index
/// sends `tools_changed` and then `quick_view_table_changed`, and so do a
/// choice in `quickView.viewers` and the uninstall.
#[tokio::test]
async fn a_viewer_installed_from_a_local_index_changes_the_table() {
    let core = start_core_with(&[], |dir| Some(vec![fixture_offer(dir, "0.1.0")]));
    let (mut client, mut events) = greeted(&core).await;
    assert_eq!(
        ask(&mut client, Request::QuickViewTable).await,
        Response::QuickViewTable {
            viewers: Vec::new(),
            kinds: Vec::new(),
        }
    );

    let install = Request::InstallExtension {
        extension_id: "quickview-fixture".to_owned(),
        version: None,
    };
    assert_eq!(ask(&mut client, install).await, Response::Ok);
    let seen = events_within(&mut events, Duration::from_millis(500)).await;
    let tools_at = seen
        .iter()
        .position(|event| matches!(event, Event::ToolsChanged { tools } if tools.len() == 1))
        .unwrap_or_else(|| panic!("{seen:?}"));
    let table_at = seen
        .iter()
        .position(|event| table_of(event).is_some())
        .unwrap_or_else(|| panic!("{seen:?}"));
    assert!(tools_at < table_at, "tools_changed comes first: {seen:?}");
    let (viewers, kinds) = table_of(&seen[table_at]).unwrap();
    assert_eq!(viewers, ["quickview-fixture"]);
    assert_eq!(
        kinds,
        [
            kind("*.qvtest", &["quickview-fixture"], false),
            kind("*.png", &["quickview-fixture"], false)
        ]
    );
    let Response::QuickViewTable { viewers, .. } = ask(&mut client, Request::QuickViewTable).await
    else {
        panic!("expected the table");
    };
    assert_eq!(viewers[0].entry, "quickview.html");
    assert!(Path::new(&viewers[0].dir).starts_with(core.path("tools")));

    let choose = Request::SetValue {
        path: "quickView.viewers".to_owned(),
        value: json!({"*.png": "none"}),
    };
    let chosen = ask(&mut client, choose).await;
    assert!(error_code(&chosen).is_none(), "{chosen:?}");
    let seen = events_within(&mut events, Duration::from_millis(500)).await;
    let (_, kinds) = seen
        .iter()
        .find_map(table_of)
        .unwrap_or_else(|| panic!("{seen:?}"));
    assert_eq!(
        kinds,
        [
            kind("*.png", &[], true),
            kind("*.qvtest", &["quickview-fixture"], false)
        ]
    );

    let uninstall = Request::UninstallExtension {
        extension_id: "quickview-fixture".to_owned(),
    };
    assert_eq!(ask(&mut client, uninstall).await, Response::Ok);
    let seen = events_within(&mut events, Duration::from_millis(500)).await;
    let (viewers, kinds) = seen
        .iter()
        .find_map(table_of)
        .unwrap_or_else(|| panic!("{seen:?}"));
    assert!(viewers.is_empty());
    assert_eq!(kinds, [kind("*.png", &[], true)]);
}

/// `--dev-tools-dir` (decision 1.3): the window's folder of tools in
/// development is listed for the table, here the committed fixture.
#[tokio::test]
async fn the_development_folder_brings_its_viewers() {
    let dev = fixture_dir().display().to_string();
    let core = start_core(&["--dev-tools-dir", &dev]);
    let mut client = connect(&core.pipe).await;
    let Response::QuickViewTable { viewers, kinds } =
        ask(&mut client, Request::QuickViewTable).await
    else {
        panic!("expected the table");
    };
    assert_eq!(viewers.len(), 1);
    assert_eq!(viewers[0].id, "quickview-fixture");
    assert_eq!(viewers[0].name, "Quick View Fixture");
    assert!(Path::new(&viewers[0].dir).ends_with("quickview-fixture"));
    assert_eq!(kinds.len(), 2);
}
