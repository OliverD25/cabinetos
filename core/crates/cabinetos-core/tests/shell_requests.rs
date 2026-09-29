//! The requests the shell needs beyond listing (protocol version 8), end to
//! end: the real `cabinetos-core.exe` on a random pipe with a real client.
//! Every file and folder lives under `%TEMP%\cabinetos-core-test\`, in a
//! folder removed at the end.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_fs::ListingReader;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, Event, Request, Response};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const EVENT_DEADLINE: Duration = Duration::from_secs(5);

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
    fn config_path(&self) -> PathBuf {
        self.dir.path().join("config").join("cabinetos.json")
    }

    /// A folder for the test's files and folders.
    fn files(&self) -> PathBuf {
        let files = self.dir.path().join("files");
        std::fs::create_dir_all(&files).unwrap();
        files
    }
}

fn start_core() -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("shell")
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

/// A client that said hello, so it receives the configuration events.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("shell-test").await.unwrap();
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

async fn get_value(client: &mut PipeClient, path: &str) -> Response {
    ask(
        client,
        Request::GetValue {
            path: path.to_owned(),
        },
    )
    .await
}

async fn set_value(client: &mut PipeClient, path: &str, value: Value) -> Response {
    ask(
        client,
        Request::SetValue {
            path: path.to_owned(),
            value,
        },
    )
    .await
}

/// The next `config_changed`; any other event fails the test.
async fn config_changed(events: &mut UnboundedReceiver<Envelope<Event>>) -> Vec<String> {
    let event = tokio::time::timeout(EVENT_DEADLINE, events.recv())
        .await
        .expect("no event in time")
        .expect("the event stream ended")
        .body;
    match event {
        Event::ConfigChanged { changed } => changed,
        other => panic!("expected config_changed, got {other:?}"),
    }
}

/// The names in a section, read the way the CLI reads them.
fn names_in(client: &PipeClient, section_handle: u64) -> Vec<String> {
    let section = client.take_section(section_handle).unwrap();
    let view = section.map_readonly().unwrap();
    let reader = ListingReader::new(view.as_slice()).unwrap();
    reader.entries().map(|entry| entry.unwrap().name).collect()
}

/// Waits for a `listing_refreshed` with these names. One change may come
/// as more than one refresh.
async fn refreshed_to(
    client: &PipeClient,
    events: &mut UnboundedReceiver<Envelope<Event>>,
    expected: &[&str],
) {
    let deadline = Instant::now() + EVENT_DEADLINE;
    let mut last = Vec::new();
    while Instant::now() < deadline {
        let left = deadline.saturating_duration_since(Instant::now());
        let Ok(Some(event)) = tokio::time::timeout(left, events.recv()).await else {
            break;
        };
        if let Event::ListingRefreshed { section_handle, .. } = event.body {
            last = names_in(client, section_handle);
            if last == expected {
                return;
            }
        }
    }
    panic!("no refresh showed {expected:?}; the last showed {last:?}");
}

fn text(path: &std::path::Path) -> String {
    path.to_str().unwrap().to_owned()
}

fn read_config(core: &Core) -> Value {
    serde_json::from_str(&std::fs::read_to_string(core.config_path()).unwrap()).unwrap()
}

#[tokio::test]
async fn every_drive_letter_is_listed_once_in_order() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let started = Instant::now();
    let reply = ask(&mut client, Request::ListVolumes).await;
    let elapsed = started.elapsed();
    let Response::Volumes { volumes } = reply else {
        panic!("expected volumes, got {reply:?}")
    };
    let letters: Vec<char> = volumes.iter().filter_map(|v| v.drive_letter).collect();
    assert_eq!(letters.len(), volumes.len(), "every volume has its letter");
    assert!(
        letters.windows(2).all(|pair| pair[0] < pair[1]),
        "{letters:?}"
    );
    let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
    let system = system.chars().next().unwrap().to_ascii_uppercase();
    let volume = volumes
        .iter()
        .find(|v| v.drive_letter == Some(system))
        .unwrap_or_else(|| panic!("no {system}: in {letters:?}"));
    assert_eq!(volume.filesystem, "NTFS");
    assert!(volume.total_bytes > 0 && volume.free_bytes <= volume.total_bytes);
    // The same fields as `volume_info` for the same drive (space may move).
    let Response::VolumeInfo(single) = ask(
        &mut client,
        Request::VolumeInfo {
            path: format!("{system}:\\"),
        },
    )
    .await
    else {
        panic!("expected volume_info")
    };
    assert_eq!(single.volume_guid_path, volume.volume_guid_path);
    assert_eq!(single.disk, volume.disk);
    // Network drives have 200 ms, local ones 2 s; they are asked at once.
    assert!(elapsed < Duration::from_secs(3), "{elapsed:?}");
    // Works before `hello` too, and again.
    assert!(matches!(
        ask(&mut client, Request::ListVolumes).await,
        Response::Volumes { .. }
    ));
}

#[tokio::test]
async fn open_path_refuses_what_it_cannot_open_without_starting_anything() {
    // A real open starts an application and leaves its window on the
    // desktop, so only the refusals are tested here.
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let open = |path: &str| Request::OpenPath {
        path: path.to_owned(),
    };
    let missing = core.dir.path().join("no-such-file.txt");
    let reply = ask(&mut client, open(missing.to_str().unwrap())).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotFound), "{reply:?}");
    // A name the shell might look up elsewhere (`notepad` on the PATH) is
    // never tried: only the path as given.
    let bare = core.dir.path().join("notepad");
    let reply = ask(&mut client, open(bare.to_str().unwrap())).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotFound), "{reply:?}");
    for relative in ["notes.txt", r"sub\notes.txt", ""] {
        let reply = ask(&mut client, open(relative)).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::InvalidPath),
            "{relative:?}: {reply:?}"
        );
    }
}

#[tokio::test]
async fn set_value_writes_one_setting_and_tells_every_client() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let (_other, mut other_events) = greeted(&core).await;
    assert_eq!(
        get_value(&mut client, "ui.dualPane").await,
        Response::Value { value: json!(true) }
    );
    assert_eq!(
        get_value(&mut client, "panes.sort").await,
        Response::Value {
            value: json!({"key": "name", "descending": false})
        }
    );

    assert_eq!(
        set_value(&mut client, "ui.dualPane", json!(false)).await,
        Response::Ok
    );
    assert_eq!(read_config(&core)["ui"]["dualPane"], json!(false));
    for events in [&mut events, &mut other_events] {
        assert_eq!(config_changed(events).await, ["ui.dualPane"]);
    }
    assert_eq!(
        get_value(&mut client, "ui.dualPane").await,
        Response::Value {
            value: json!(false)
        }
    );

    let folders = json!([r"C:\Users", r"D:\work"]);
    assert_eq!(
        set_value(&mut client, "ui.lastPaths", folders.clone()).await,
        Response::Ok
    );
    assert_eq!(config_changed(&mut events).await, ["ui.lastPaths"]);
    assert_eq!(read_config(&core)["ui"]["lastPaths"], folders);
    assert_eq!(
        get_value(&mut client, "ui.lastPaths").await,
        Response::Value { value: folders }
    );

    // A dragged dock size, one level deeper, survives in the file.
    assert_eq!(
        get_value(&mut client, "ui.dockSize").await,
        Response::Value {
            value: json!({"bottom": null, "right": null})
        }
    );
    assert_eq!(
        set_value(&mut client, "ui.dockSize.bottom", json!(320)).await,
        Response::Ok
    );
    assert_eq!(config_changed(&mut events).await, ["ui.dockSize.bottom"]);
    assert_eq!(
        read_config(&core)["ui"]["dockSize"],
        json!({"bottom": 320, "right": null})
    );
    // The same value again changes nothing and says nothing.
    assert_eq!(
        set_value(&mut client, "ui.dualPane", json!(false)).await,
        Response::Ok
    );
    // The watcher sees the core's own writes and must not report them.
    tokio::time::sleep(Duration::from_millis(600)).await;
    assert!(
        events.try_recv().is_err(),
        "the core's own write came back as an event"
    );
}

#[tokio::test]
async fn a_value_the_file_could_not_hold_is_refused_and_the_file_kept() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    // The core creates the file at start; wait until it is there.
    assert!(matches!(
        get_value(&mut client, "ui").await,
        Response::Value { .. }
    ));
    let before = std::fs::read(core.config_path()).unwrap();
    for (path, value, says) in [
        ("ui.dualPane", json!("yes"), "expected a boolean"),
        ("ui.nope", json!(1), "no setting `ui.nope`"),
        ("ui.pinned", json!(r"D:\work"), "expected a sequence"),
        ("terminal.defaultProfile", json!("fish"), "fish"),
        ("version", json!(2), "version 2"),
    ] {
        let reply = set_value(&mut client, path, value).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::ConfigError),
            "{path}: {reply:?}"
        );
        let Response::Error { message, .. } = &reply else {
            unreachable!()
        };
        assert!(message.contains(says), "{path}: {message}");
    }
    assert_eq!(std::fs::read(core.config_path()).unwrap(), before);
    for path in ["ui.nope", "ui.dualPane.deeper", ""] {
        let reply = get_value(&mut client, path).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::ConfigError),
            "{path}: {reply:?}"
        );
    }
}

#[tokio::test]
async fn a_new_folder_and_a_rename_reach_a_watched_listing() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let files = core.files();
    std::fs::write(files.join("a.txt"), "a").unwrap();
    let reply = ask(
        &mut client,
        Request::ListDirectory {
            path: text(&files),
            include_hidden: None,
            sort: None,
            watch: true,
        },
    )
    .await;
    let Response::ListingOpened { section_handle, .. } = reply else {
        panic!("expected listing_opened, got {reply:?}")
    };
    assert_eq!(names_in(&client, section_handle), ["a.txt"]);

    let folder = files.join("New folder");
    let reply = ask(
        &mut client,
        Request::CreateDirectory {
            path: text(&folder),
        },
    )
    .await;
    assert_eq!(reply, Response::Ok);
    assert!(folder.is_dir());
    refreshed_to(&client, &mut events, &["New folder", "a.txt"]).await;

    let reply = ask(
        &mut client,
        Request::Rename {
            path: text(&files.join("a.txt")),
            new_name: "b.txt".to_owned(),
        },
    )
    .await;
    assert_eq!(reply, Response::Ok);
    refreshed_to(&client, &mut events, &["New folder", "b.txt"]).await;
}

#[tokio::test]
async fn new_folders_and_renames_are_refused_with_the_reason() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    let files = core.files();
    std::fs::write(files.join("a.txt"), "a").unwrap();
    std::fs::write(files.join("b.txt"), "b").unwrap();
    let mkdir = |path: String| Request::CreateDirectory { path };
    let rename = |path: String, new_name: &str| Request::Rename {
        path,
        new_name: new_name.to_owned(),
    };

    for (request, expected) in [
        (mkdir(text(&files.join("b.txt"))), ErrorCode::AlreadyExists),
        (
            mkdir(text(&files.join("gone").join("child"))),
            ErrorCode::NotFound,
        ),
        (mkdir(text(&files.join("dot."))), ErrorCode::InvalidPath),
        (mkdir("relative".to_owned()), ErrorCode::InvalidPath),
        (
            rename(text(&files.join("a.txt")), "b.txt"),
            ErrorCode::AlreadyExists,
        ),
        (
            rename(text(&files.join("a.txt")), ""),
            ErrorCode::InvalidPath,
        ),
        (
            rename(text(&files.join("a.txt")), r"sub\c.txt"),
            ErrorCode::InvalidPath,
        ),
        (
            rename(text(&files.join("a.txt")), "../c.txt"),
            ErrorCode::InvalidPath,
        ),
        (
            rename(text(&files.join("none.txt")), "c.txt"),
            ErrorCode::NotFound,
        ),
        (rename("a.txt".to_owned(), "c.txt"), ErrorCode::InvalidPath),
    ] {
        let reply = ask(&mut client, request.clone()).await;
        assert_eq!(error_code(&reply), Some(expected), "{request:?}: {reply:?}");
    }
    // Nothing was replaced or created.
    assert_eq!(std::fs::read_to_string(files.join("a.txt")).unwrap(), "a");
    assert_eq!(std::fs::read_to_string(files.join("b.txt")).unwrap(), "b");
    let mut names: Vec<String> = std::fs::read_dir(&files)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap())
        .collect();
    names.sort();
    assert_eq!(names, ["a.txt", "b.txt"]);
}

/// Width, height and whether any pixel is partly clear, from a PNG.
fn png_facts(png: &[u8]) -> (u32, u32, bool, bool) {
    let decoder = png::Decoder::new(std::io::Cursor::new(png));
    let mut reader = decoder.read_info().unwrap();
    let mut pixels = vec![0u8; reader.output_buffer_size().unwrap()];
    let frame = reader.next_frame(&mut pixels).unwrap();
    assert_eq!(frame.color_type, png::ColorType::Rgba);
    let alpha: Vec<u8> = pixels[..frame.buffer_size()]
        .as_chunks::<4>()
        .0
        .iter()
        .map(|pixel| pixel[3])
        .collect();
    (
        frame.width,
        frame.height,
        alpha.iter().any(|&a| a > 0),
        alpha.iter().any(|&a| a < 255),
    )
}

#[tokio::test]
async fn entries_are_described_and_their_icons_drawn() {
    let core = start_core();
    let (mut client, _events) = greeted(&core).await;
    let files = core.files();
    std::fs::write(files.join("notes.txt"), "text").unwrap();
    std::fs::write(files.join("README"), "read me").unwrap();
    std::fs::create_dir(files.join("sub")).unwrap();
    std::fs::copy(CORE_EXE, files.join("tool.exe")).unwrap();
    let reply = ask(
        &mut client,
        Request::ListDirectory {
            path: text(&files),
            include_hidden: None,
            sort: None,
            watch: false,
        },
    )
    .await;
    let Response::ListingOpened {
        listing_id,
        section_handle,
        ..
    } = reply
    else {
        panic!("expected listing_opened, got {reply:?}")
    };
    assert_eq!(
        names_in(&client, section_handle),
        ["sub", "notes.txt", "README", "tool.exe"]
    );

    let describe = |from, count| Request::DescribeEntries {
        listing_id,
        from,
        count,
    };
    let reply = ask(&mut client, describe(0, 10)).await;
    let Response::EntryDetails {
        listing_id: described,
        generation,
        from,
        details,
    } = reply
    else {
        panic!("expected entry_details, got {reply:?}")
    };
    assert_eq!(
        (described, generation, from, details.len()),
        (listing_id, 1, 0, 4)
    );
    let keys: Vec<&str> = details.iter().map(|d| d.icon_key.as_str()).collect();
    assert_eq!(keys[..3], ["folder", "ext:.txt", "generic"]);
    assert!(keys[3].starts_with("path:"), "{keys:?}");
    assert!(
        details.iter().all(|d| !d.type_name.is_empty()),
        "{details:?}"
    );

    for key in &keys {
        for size in [16, 48] {
            let reply = ask(
                &mut client,
                Request::GetIcon {
                    key: (*key).to_owned(),
                    size,
                },
            )
            .await;
            let Response::Icon {
                key: answered,
                size: answered_size,
                png_base64,
            } = reply
            else {
                panic!("{key} at {size}: expected icon, got {reply:?}")
            };
            assert_eq!((answered.as_str(), answered_size), (*key, size));
            let (width, height, shows, clear) = png_facts(&BASE64.decode(png_base64).unwrap());
            assert_eq!((width, height), (size, size), "{key}");
            assert!(
                shows && clear,
                "{key} at {size}: the alpha channel is empty or full"
            );
        }
    }

    refusals(&mut client, listing_id).await;
    assert_eq!(
        ask(&mut client, Request::CloseListing { listing_id }).await,
        Response::Ok
    );
}

/// A range past the end, an unknown listing, too many, unknown icons.
async fn refusals(client: &mut PipeClient, listing_id: u64) {
    let describe = |from, count| Request::DescribeEntries {
        listing_id,
        from,
        count,
    };
    let reply = ask(client, describe(4, 10)).await;
    assert!(
        matches!(&reply, Response::EntryDetails { details, .. } if details.is_empty()),
        "{reply:?}"
    );
    let reply = ask(
        client,
        Request::DescribeEntries {
            listing_id: 999_999,
            from: 0,
            count: 1,
        },
    )
    .await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::NoSuchListing),
        "{reply:?}"
    );
    let reply = ask(client, describe(0, 513)).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ProtocolError),
        "{reply:?}"
    );
    for (key, size, code) in [
        ("path:0123456789abcdef", 32, ErrorCode::NotFound),
        ("nothing", 32, ErrorCode::NotFound),
        ("folder", 20, ErrorCode::ProtocolError),
    ] {
        let reply = ask(
            client,
            Request::GetIcon {
                key: key.to_owned(),
                size,
            },
        )
        .await;
        assert_eq!(error_code(&reply), Some(code), "{key} {size}: {reply:?}");
    }
}

#[tokio::test]
async fn a_watched_listing_is_described_at_its_current_generation() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let files = core.files();
    std::fs::write(files.join("a.txt"), "a").unwrap();
    let reply = ask(
        &mut client,
        Request::ListDirectory {
            path: text(&files),
            include_hidden: None,
            sort: None,
            watch: true,
        },
    )
    .await;
    let Response::ListingOpened { listing_id, .. } = reply else {
        panic!("expected listing_opened, got {reply:?}")
    };
    std::fs::create_dir(files.join("new")).unwrap();
    refreshed_to(&client, &mut events, &["new", "a.txt"]).await;
    let reply = ask(
        &mut client,
        Request::DescribeEntries {
            listing_id,
            from: 0,
            count: 10,
        },
    )
    .await;
    let Response::EntryDetails {
        generation,
        details,
        ..
    } = reply
    else {
        panic!("expected entry_details, got {reply:?}")
    };
    assert!(generation >= 2, "{generation}");
    let keys: Vec<&str> = details.iter().map(|d| d.icon_key.as_str()).collect();
    assert_eq!(keys, ["folder", "ext:.txt"]);
}
