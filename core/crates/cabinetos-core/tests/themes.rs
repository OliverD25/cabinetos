//! Colour themes end to end: the real `cabinetos-core.exe` with its own
//! themes folder (`--themes-dir`) and configuration file in
//! `%TEMP%\cabinetos-core-test\`, and a real client.

use std::fs;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Envelope, ErrorCode, Event, Request, Response, Theme};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// A saved edit reaches the clients within a second (Phase 3's promise for
/// the configuration holds for themes too).
const EDIT_DEADLINE: Duration = Duration::from_secs(1);
const EVENT_DEADLINE: Duration = Duration::from_secs(5);

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
    fn themes_dir(&self) -> PathBuf {
        self.dir.path().join("themes")
    }

    fn config_path(&self) -> PathBuf {
        self.dir.path().join("cabinetos.json")
    }
}

/// Starts a core with `config` as its configuration file.
fn start_core(config: &Value) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("themes")
        .tempdir_in(root)
        .unwrap();
    let config_path = dir.path().join("cabinetos.json");
    fs::write(&config_path, serde_json::to_string_pretty(config).unwrap()).unwrap();
    let pipe = PipeName::random();
    let child = Command::new(CORE_EXE)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .arg("--themes-dir")
        .arg(dir.path().join("themes"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env_remove("CABINETOS_THEMES_DIR")
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
    let welcome = client.hello("themes-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

/// The theme in effect, or the theme `id`.
async fn theme(client: &mut PipeClient, id: Option<&str>) -> Theme {
    match ask(
        client,
        Request::GetTheme {
            theme_id: id.map(str::to_owned),
        },
    )
    .await
    {
        Response::Theme { theme } => *theme,
        other => panic!("expected a theme, got {other:?}"),
    }
}

fn error_of(reply: Response) -> (ErrorCode, String) {
    match reply {
        Response::Error { code, message } => (code, message),
        other => panic!("expected an error, got {other:?}"),
    }
}

/// The next event that passes `check`, within `deadline`.
async fn next_event(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    deadline: Duration,
    check: impl Fn(&Event) -> bool,
) -> Event {
    let until = Instant::now() + deadline;
    loop {
        let left = until.saturating_duration_since(Instant::now());
        let event = tokio::time::timeout(left, events.recv())
            .await
            .expect("the event did not come in time")
            .expect("the event stream ended")
            .body;
        if check(&event) {
            return event;
        }
    }
}

fn changed_theme(event: &Event) -> Option<&Theme> {
    match event {
        Event::ThemeChanged { theme } => Some(theme),
        _ => None,
    }
}

/// The file of the theme `id` with one change.
fn edited(core: &Core, id: &str, change: impl FnOnce(&mut Value)) -> String {
    let path = core.themes_dir().join(format!("{id}.json"));
    let mut value: Value = serde_json::from_str(&fs::read_to_string(path).unwrap()).unwrap();
    change(&mut value);
    serde_json::to_string_pretty(&value).unwrap()
}

/// Phase 9's goal for themes: a theme applies live to a connected client,
/// whether `ui.theme` changes or the theme's own file is saved.
#[tokio::test]
async fn a_theme_applies_live_and_a_broken_edit_never_applies() {
    let core = start_core(&json!({}));
    let (mut client, mut events) = greeted(&core).await;

    // The shipped themes were written into the empty folder.
    let Response::Themes { themes } = ask(&mut client, Request::ListThemes).await else {
        panic!("expected themes");
    };
    let ids: Vec<&str> = themes.iter().map(|theme| theme.id.as_str()).collect();
    assert_eq!(
        ids,
        ["catppuccin-mocha", "default", "nord", "rose-pine-moon"]
    );
    assert!(core.themes_dir().join("theme.schema.json").is_file());
    let default = theme(&mut client, None).await;
    assert_eq!(default.id, "default");
    assert_eq!((default.accent, default.mica), (None, None));

    // set_value brings config_changed and theme_changed with the whole theme.
    let reply = ask(
        &mut client,
        Request::SetValue {
            path: "ui.theme".to_owned(),
            value: json!("nord"),
        },
    )
    .await;
    assert_eq!(reply, Response::Ok);
    let changed = next_event(&mut events, EVENT_DEADLINE, |event| {
        changed_theme(event).is_some()
    })
    .await;
    let nord = changed_theme(&changed).unwrap();
    assert_eq!(nord.id, "nord");
    assert_eq!(nord.accent.as_ref().unwrap().as_str(), "#88C0D0");
    assert_eq!(nord.mica.as_ref().unwrap().tint.as_str(), "#2E3440");
    assert_eq!(theme(&mut client, None).await.id, "nord");

    // Saving the theme's own file applies the edit at once.
    let text = edited(&core, "nord", |theme| theme["accent"] = json!("#112233"));
    fs::write(core.themes_dir().join("nord.json"), text).unwrap();
    let changed = next_event(&mut events, EDIT_DEADLINE, |event| {
        changed_theme(event).is_some()
    })
    .await;
    assert_eq!(
        changed_theme(&changed)
            .unwrap()
            .accent
            .as_ref()
            .unwrap()
            .as_str(),
        "#112233"
    );

    // A broken save is reported and never applied half-way.
    let text = edited(&core, "nord", |theme| {
        theme["palette"]["textPrimary"] = json!("white");
        theme["accent"] = json!("#445566");
    });
    fs::write(core.themes_dir().join("nord.json"), text).unwrap();
    let reported = next_event(&mut events, EDIT_DEADLINE, |event| {
        matches!(event, Event::ConfigError { .. })
    })
    .await;
    let Event::ConfigError { message, .. } = reported else {
        unreachable!()
    };
    assert!(
        message.contains("nord.json") && message.contains("is not a colour"),
        "{message}"
    );
    let in_effect = theme(&mut client, None).await;
    assert_eq!(in_effect.accent.unwrap().as_str(), "#112233");
    assert_eq!(in_effect.palette.text_primary.as_str(), "#ECEFF4");
}

/// An unknown theme never applies: `set_value` refuses it, and a hand edit
/// that names one is reported while the theme in effect stays.
#[tokio::test]
async fn an_unknown_theme_keeps_the_last_one() {
    let core = start_core(&json!({"ui": {"theme": "catppuccin-mocha"}}));
    let (mut client, mut events) = greeted(&core).await;
    assert_eq!(theme(&mut client, None).await.id, "catppuccin-mocha");

    let (error_code, message) = error_of(
        ask(
            &mut client,
            Request::SetValue {
                path: "ui.theme".to_owned(),
                value: json!("solarized"),
            },
        )
        .await,
    );
    assert_eq!(error_code, ErrorCode::ConfigError);
    assert!(message.contains("solarized"), "{message}");
    assert_eq!(
        ask(
            &mut client,
            Request::GetValue {
                path: "ui.theme".to_owned()
            }
        )
        .await,
        Response::Value {
            value: json!("catppuccin-mocha")
        }
    );

    // Other settings still change while the theme stays.
    let reply = ask(
        &mut client,
        Request::SetValue {
            path: "ui.sidebar".to_owned(),
            value: json!(false),
        },
    )
    .await;
    assert_eq!(reply, Response::Ok);

    // A hand edit to an unknown theme: the file is in effect, the theme is
    // not, and a config_error says why.
    let mut config: Value =
        serde_json::from_str(&fs::read_to_string(core.config_path()).unwrap()).unwrap();
    config["ui"]["theme"] = json!("missing-theme");
    fs::write(
        core.config_path(),
        serde_json::to_string_pretty(&config).unwrap(),
    )
    .unwrap();
    let reported = next_event(&mut events, EVENT_DEADLINE, |event| {
        matches!(event, Event::ConfigError { .. })
    })
    .await;
    let Event::ConfigError { message, .. } = reported else {
        unreachable!()
    };
    assert!(
        message.contains("missing-theme") && message.contains("`catppuccin-mocha` stays"),
        "{message}"
    );
    assert_eq!(theme(&mut client, None).await.id, "catppuccin-mocha");

    // The theme appears: it applies at once.
    let text = edited(&core, "nord", |theme| theme["id"] = json!("missing-theme"));
    fs::write(core.themes_dir().join("missing-theme.json"), text).unwrap();
    let changed = next_event(&mut events, EDIT_DEADLINE, |event| {
        changed_theme(event).is_some()
    })
    .await;
    assert_eq!(changed_theme(&changed).unwrap().id, "missing-theme");

    for (id, expected) in [
        ("nothing-here", ErrorCode::NoSuchTheme),
        (r"..\themes\nord", ErrorCode::NoSuchTheme),
    ] {
        let reply = ask(
            &mut client,
            Request::GetTheme {
                theme_id: Some(id.to_owned()),
            },
        )
        .await;
        assert_eq!(error_of(reply).0, expected, "{id}");
    }
    assert_eq!(theme(&mut client, Some("nord")).await.name, "Nord");
}
