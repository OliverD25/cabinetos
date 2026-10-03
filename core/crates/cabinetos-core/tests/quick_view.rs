//! Quick View's requests (protocol version 21, ADR 0023), end to end: the
//! real `cabinetos-core.exe` on a random pipe with a real client. Every
//! file and folder lives under `%TEMP%\cabinetos-core-test\`, in a folder
//! removed at the end.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{ErrorCode, Request, Response, ThumbnailReason};
use tempfile::TempDir;

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
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("quickview")
        .tempdir_in(root)
        .unwrap();
    std::fs::create_dir_all(dir.path().join("files")).unwrap();
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
