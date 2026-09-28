//! Configuration, commands and keybindings, end to end: the real
//! `cabinetos-core.exe` on a random pipe, reading its own configuration file
//! in a temporary directory (`--config`), with a real client.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_fs::ListingReader;
use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    CommandTarget, Envelope, ErrorCode, Event, Keymap, Request, Response, SortKey, SortSpec,
};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// The Phase 3 promise: an edit saved by hand reaches the clients within a
/// second.
const EDIT_DEADLINE: Duration = Duration::from_secs(1);
/// For events whose timing is not the point of the test.
const EVENT_DEADLINE: Duration = Duration::from_secs(5);

/// A running core, killed at the end of the test.
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

    fn log_dir(&self) -> PathBuf {
        self.dir.path().join("logs")
    }
}

/// Starts a core whose configuration file does not exist yet, so it
/// creates it with the defaults.
fn start_core() -> Core {
    start_core_with(None)
}

/// Starts a core, writing `initial` into its configuration file first.
fn start_core_with(initial: Option<&str>) -> Core {
    let dir = tempfile::tempdir().unwrap();
    let config = dir.path().join("config").join("cabinetos.json");
    if let Some(text) = initial {
        fs::create_dir_all(config.parent().unwrap()).unwrap();
        fs::write(&config, text).unwrap();
    }
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config)
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        // Never the real plugins folder, whatever is installed there.
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
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

/// A client that said hello, so it receives configuration events.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("config-test").await.unwrap();
    assert!(
        matches!(welcome.body, Response::Welcome { .. }),
        "{welcome:?}"
    );
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

async fn get_keymap(client: &mut PipeClient) -> Keymap {
    match ask(client, Request::GetKeymap).await {
        Response::Keymap(keymap) => keymap,
        other => panic!("expected keymap, got {other:?}"),
    }
}

fn set_keybinding(command: &str, keys: &str) -> Request {
    Request::SetKeybinding {
        command: command.to_owned(),
        keys: keys.to_owned(),
    }
}

fn execute(command: &str) -> Request {
    Request::ExecuteCommand {
        command: command.to_owned(),
        args: Value::Null,
    }
}

/// The keys bound to `command`.
fn keys_of(keymap: &Keymap, command: &str) -> Vec<String> {
    keymap
        .bindings
        .iter()
        .filter(|binding| binding.command == command)
        .map(|binding| binding.keys.clone())
        .collect()
}

fn error_code(response: &Response) -> Option<ErrorCode> {
    match response {
        Response::Error { code, .. } => Some(*code),
        _ => None,
    }
}

fn read_config(path: &Path) -> Value {
    serde_json::from_str(&fs::read_to_string(path).unwrap()).unwrap()
}

/// Waits for the first event `pick` accepts; `pick` may panic on events
/// that must not come. Returns what `pick` returned and the time since
/// `since`.
async fn wait_for<T>(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    since: Instant,
    deadline: Duration,
    mut pick: impl FnMut(&Event) -> Option<T>,
) -> (T, Duration) {
    loop {
        let left = deadline
            .checked_sub(since.elapsed())
            .unwrap_or_else(|| panic!("the event did not arrive within {deadline:?}"));
        let event = tokio::time::timeout(left, events.recv())
            .await
            .unwrap_or_else(|_| panic!("the event did not arrive within {deadline:?}"))
            .expect("the event stream ended")
            .body;
        if let Some(found) = pick(&event) {
            return (found, since.elapsed());
        }
    }
}

#[tokio::test]
async fn a_hand_edit_rebinds_a_command_within_a_second() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let before = get_keymap(&mut client).await;
    assert_eq!(keys_of(&before, "view.toggleSidebar"), ["ctrl+b"]);
    assert_eq!(before.chord_window_ms, 1000);
    assert_eq!(
        before.immutable,
        ["palette.show", "overlay.close", "keys.open"]
    );

    // Edit the text the way a person does, and save it in place.
    let path = core.config_path();
    let text = fs::read_to_string(&path).unwrap();
    let edited = text.replace(
        "\"keybindings\": []",
        "\"keybindings\": [\n    { \"command\": \"view.toggleSidebar\", \"keys\": \"ctrl+alt+b\" }\n  ]",
    );
    assert_ne!(edited, text, "the created file lists no keybindings");
    let saved = Instant::now();
    fs::write(&path, edited).unwrap();

    let mut config_changed_first = false;
    let (keymap, elapsed) = wait_for(&mut events, saved, EDIT_DEADLINE, |event| match event {
        Event::ConfigChanged { changed } => {
            assert_eq!(changed, &["keybindings"]);
            config_changed_first = true;
            None
        }
        Event::KeymapChanged { keymap } => Some(keymap.clone()),
        other => panic!("unexpected event {other:?}"),
    })
    .await;
    assert!(
        config_changed_first,
        "config_changed comes before keymap_changed"
    );
    assert_eq!(keys_of(&keymap, "view.toggleSidebar"), ["ctrl+alt+b"]);
    assert!(elapsed < EDIT_DEADLINE, "{elapsed:?}");
    assert_eq!(get_keymap(&mut client).await, keymap);
}

#[tokio::test]
async fn a_save_that_renames_a_new_file_into_place_is_picked_up() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let path = core.config_path();
    let temporary = path.with_file_name("cabinetos.json.tmp");
    fs::write(&temporary, r#"{ "ui": { "layout": "rail" } }"#).unwrap();
    let saved = Instant::now();
    fs::rename(&temporary, &path).unwrap();

    let (changed, elapsed) = wait_for(&mut events, saved, EDIT_DEADLINE, |event| match event {
        Event::ConfigChanged { changed } => Some(changed.clone()),
        other => panic!("unexpected event {other:?}"),
    })
    .await;
    assert_eq!(changed, ["ui.layout"]);
    assert!(elapsed < EDIT_DEADLINE, "{elapsed:?}");
    let Response::Config { config, .. } = ask(&mut client, Request::GetConfig).await else {
        panic!("expected config")
    };
    assert_eq!(config["ui"]["layout"], "rail");
}

#[tokio::test]
async fn a_broken_edit_is_reported_and_the_settings_stay() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let path = core.config_path();
    fs::write(&path, "{\n  \"ui\": {\n    \"layout\": 7\n  }\n}").unwrap();
    let ((line, message), _) =
        wait_for(
            &mut events,
            Instant::now(),
            EVENT_DEADLINE,
            |event| match event {
                Event::ConfigError { line, message, .. } => Some((*line, message.clone())),
                other => panic!("unexpected event {other:?}"),
            },
        )
        .await;
    assert_eq!(line, Some(3), "{message}");
    let Response::Config { config, .. } = ask(&mut client, Request::GetConfig).await else {
        panic!("expected config")
    };
    assert_eq!(
        config["ui"]["layout"], "classic",
        "the last good settings stay"
    );

    fs::write(&path, r#"{ "ui": { "layout": "right" } }"#).unwrap();
    let (changed, _) = wait_for(
        &mut events,
        Instant::now(),
        EVENT_DEADLINE,
        |event| match event {
            Event::ConfigChanged { changed } => Some(changed.clone()),
            other => panic!("unexpected event {other:?}"),
        },
    )
    .await;
    assert_eq!(changed, ["ui.layout"]);
}

#[tokio::test]
async fn the_immutable_tier_cannot_be_rebound() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;

    let reply = ask(&mut client, set_keybinding("palette.show", "ctrl+p")).await;
    assert_eq!(
        error_code(&reply),
        Some(ErrorCode::ImmutableBinding),
        "{reply:?}"
    );
    let Response::Error { message, .. } = &reply else {
        unreachable!()
    };
    assert!(message.contains("Immutable System Tier"), "{message}");
    // Nor may another command take the tier's keys.
    for (command, keys) in [
        ("view.toggleSidebar", "ctrl+shift+p"),
        ("go.toPath", "escape"),
        ("go.toPath", "ctrl+k ctrl+s"),
    ] {
        let reply = ask(&mut client, set_keybinding(command, keys)).await;
        assert_eq!(
            error_code(&reply),
            Some(ErrorCode::ImmutableBinding),
            "{command} {keys}: {reply:?}"
        );
    }
    assert_eq!(read_config(&core.config_path())["keybindings"], json!([]));

    // A hand edit that tries is refused with its line; the keymap stays.
    fs::write(
        core.config_path(),
        "{\n  \"keybindings\": [\n    { \"command\": \"overlay.close\", \"keys\": \"ctrl+q\" }\n  ]\n}",
    )
    .unwrap();
    let ((line, message), _) =
        wait_for(
            &mut events,
            Instant::now(),
            EVENT_DEADLINE,
            |event| match event {
                Event::ConfigError { line, message, .. } => Some((*line, message.clone())),
                other => panic!("unexpected event {other:?}"),
            },
        )
        .await;
    assert_eq!(line, Some(3), "{message}");
    assert!(message.contains("Immutable System Tier"), "{message}");
    assert_eq!(
        keys_of(&get_keymap(&mut client).await, "overlay.close"),
        ["escape"]
    );
}

#[tokio::test]
async fn set_and_reset_write_the_file_and_tell_every_client() {
    let core = start_core();
    let (mut client, mut events) = greeted(&core).await;
    let (_other, mut other_events) = greeted(&core).await;

    let reply = ask(
        &mut client,
        set_keybinding("view.toggleSidebar", "Ctrl+Alt+S"),
    )
    .await;
    let Response::Keymap(keymap) = reply else {
        panic!("expected keymap, got {reply:?}")
    };
    assert_eq!(keys_of(&keymap, "view.toggleSidebar"), ["ctrl+alt+s"]);
    assert_eq!(
        read_config(&core.config_path())["keybindings"],
        json!([{ "command": "view.toggleSidebar", "keys": "ctrl+alt+s" }])
    );

    // Every client that said hello hears about it, the one that asked too.
    for events in [&mut events, &mut other_events] {
        let (changed, _) = wait_for(
            events,
            Instant::now(),
            EVENT_DEADLINE,
            |event| match event {
                Event::ConfigChanged { changed } => Some(changed.clone()),
                other => panic!("unexpected event {other:?}"),
            },
        )
        .await;
        assert_eq!(changed, ["keybindings"]);
        let (announced, _) = wait_for(
            events,
            Instant::now(),
            EVENT_DEADLINE,
            |event| match event {
                Event::KeymapChanged { keymap } => Some(keymap.clone()),
                other => panic!("unexpected event {other:?}"),
            },
        )
        .await;
        assert_eq!(announced, keymap);
    }
    // The watcher sees the core's own write, and must not report it again.
    tokio::time::sleep(Duration::from_millis(600)).await;
    assert!(
        events.try_recv().is_err(),
        "the core's own write came back as an event"
    );

    let reply = ask(
        &mut client,
        Request::ResetKeybinding {
            command: "view.toggleSidebar".to_owned(),
        },
    )
    .await;
    let Response::Keymap(keymap) = reply else {
        panic!("expected keymap, got {reply:?}")
    };
    assert_eq!(keys_of(&keymap, "view.toggleSidebar"), ["ctrl+b"]);
    assert_eq!(read_config(&core.config_path())["keybindings"], json!([]));

    // Empty keys leave the command unbound.
    let reply = ask(&mut client, set_keybinding("view.toggleSidebar", "")).await;
    let Response::Keymap(keymap) = reply else {
        panic!("expected keymap, got {reply:?}")
    };
    assert!(keys_of(&keymap, "view.toggleSidebar").is_empty());
}

#[tokio::test]
async fn bad_keys_conflicts_and_unknown_commands_are_refused() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;
    for (command, keys, expected) in [
        ("view.toggleSidebar", "ctrl+nope", ErrorCode::InvalidKeys),
        (
            "view.toggleSidebar",
            "ctrl+a ctrl+b ctrl+c",
            ErrorCode::InvalidKeys,
        ),
        // ctrl+b runs view.toggleSidebar.
        ("go.toPath", "ctrl+b", ErrorCode::KeybindingConflict),
        // ctrl+k ctrl+w runs workspace.switch.
        (
            "view.toggleSidebar",
            "ctrl+k ctrl+w",
            ErrorCode::KeybindingConflict,
        ),
        // ctrl+k starts the chords, among them keys.open of the tier.
        ("go.toPath", "ctrl+k", ErrorCode::ImmutableBinding),
        ("no.suchCommand", "f9", ErrorCode::UnknownCommand),
    ] {
        let reply = ask(&mut client, set_keybinding(command, keys)).await;
        assert_eq!(
            error_code(&reply),
            Some(expected),
            "{command} {keys}: {reply:?}"
        );
    }
    let reply = ask(
        &mut client,
        Request::ResetKeybinding {
            command: "no.suchCommand".to_owned(),
        },
    )
    .await;
    assert_eq!(error_code(&reply), Some(ErrorCode::UnknownCommand));
    assert_eq!(read_config(&core.config_path())["keybindings"], json!([]));
}

#[tokio::test]
async fn commands_are_listed_searched_and_run() {
    let core = start_core();
    let mut client = connect(&core.pipe).await;

    let Response::Commands { commands } = ask(&mut client, Request::ListCommands).await else {
        panic!("expected commands")
    };
    assert_eq!(commands.len(), 16);
    let sidebar = commands
        .iter()
        .find(|command| command.id == "view.toggleSidebar")
        .unwrap();
    assert_eq!(sidebar.keys, ["ctrl+b"]);
    assert_eq!(sidebar.default_keys, ["ctrl+b"]);
    assert_eq!(
        commands.iter().filter(|command| command.immutable).count(),
        3
    );

    let Response::SearchResults { hits } = ask(
        &mut client,
        Request::SearchCommands {
            query: "dual".to_owned(),
            limit: 5,
        },
    )
    .await
    else {
        panic!("expected search results")
    };
    assert_eq!(hits[0].id, "view.toggleDualPane");
    assert!(hits.len() <= 5);

    match ask(&mut client, execute("help.about")).await {
        Response::CommandResult { result } => {
            assert_eq!(result["core_version"], env!("CARGO_PKG_VERSION"));
        }
        other => panic!("expected command_result, got {other:?}"),
    }
    assert_eq!(
        ask(&mut client, execute("view.toggleSidebar")).await,
        Response::CommandRouted {
            target: CommandTarget::Ui
        }
    );
    let reply = ask(&mut client, execute("file.newFolder")).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::NotImplemented));
    let reply = ask(&mut client, execute("no.suchCommand")).await;
    assert_eq!(error_code(&reply), Some(ErrorCode::UnknownCommand));

    match ask(&mut client, Request::GetConfig).await {
        Response::Config { path, config } => {
            assert_eq!(PathBuf::from(path), core.config_path());
            assert_eq!(config["ui"]["layout"], "classic");
            assert_eq!(config["terminal"]["defaultProfile"], "pwsh");
        }
        other => panic!("expected config, got {other:?}"),
    }
}

#[tokio::test]
async fn listings_follow_the_configured_order() {
    let core = start_core_with(Some(
        r#"{ "panes": { "sort": { "key": "size", "descending": true } } }"#,
    ));
    let (mut client, _events) = greeted(&core).await;
    let dir = tempfile::tempdir().unwrap();
    for (name, content) in [("a.txt", "a"), ("b.txt", "bbb"), ("c.txt", "cc")] {
        fs::write(dir.path().join(name), content).unwrap();
    }
    let list = |sort| Request::ListDirectory {
        path: dir.path().to_str().unwrap().to_owned(),
        include_hidden: None,
        sort,
        watch: false,
    };

    let reply = ask(&mut client, list(None)).await;
    let Response::ListingOpened { section_handle, .. } = reply else {
        panic!("expected listing_opened, got {reply:?}")
    };
    assert_eq!(
        names_in(&client, section_handle),
        ["b.txt", "c.txt", "a.txt"]
    );

    // A request that names its order gets that order.
    let by_name = Some(SortSpec {
        key: SortKey::Name,
        descending: false,
    });
    let reply = ask(&mut client, list(by_name)).await;
    let Response::ListingOpened { section_handle, .. } = reply else {
        panic!("expected listing_opened, got {reply:?}")
    };
    assert_eq!(
        names_in(&client, section_handle),
        ["a.txt", "b.txt", "c.txt"]
    );
}

#[tokio::test]
async fn the_log_level_comes_from_the_file() {
    let mut core = start_core_with(Some(r#"{ "logging": { "level": "debug" } }"#));
    let mut client = connect(&core.pipe).await;
    assert_eq!(ask(&mut client, Request::Shutdown).await, Response::Ok);
    let until = Instant::now() + STARTUP_DEADLINE;
    while core.child.try_wait().unwrap().is_none() {
        assert!(Instant::now() < until, "the core did not exit");
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
    let mut levels = Vec::new();
    for entry in fs::read_dir(core.log_dir()).unwrap() {
        let path = entry.unwrap().path();
        if path
            .extension()
            .is_some_and(|extension| extension == "jsonl")
        {
            for line in fs::read_to_string(&path).unwrap().lines() {
                let value: Value = serde_json::from_str(line).unwrap();
                levels.push(value["level"].as_str().unwrap().to_owned());
            }
        }
    }
    assert!(levels.iter().any(|level| level == "DEBUG"), "{levels:?}");
}

/// The names in a section, read the way the CLI reads them.
fn names_in(client: &PipeClient, section_handle: u64) -> Vec<String> {
    let section = client.take_section(section_handle).unwrap();
    let view = section.map_readonly().unwrap();
    let reader = ListingReader::new(view.as_slice()).unwrap();
    reader.entries().map(|entry| entry.unwrap().name).collect()
}
