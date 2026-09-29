//! Core Plugins end to end: the real `cabinetos-core.exe` with copies of the
//! committed fixture plugins (`sdk/fixtures/plugins`), a real client.
//! Everything the tests write lives under `%TEMP%\cabinetos-jobs-test\` (the
//! reader fixture's folder under `%TEMP%\cabinetos-plugins-test\`) and is
//! removed.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{
    CommandInfo, CommandSource, Envelope, ErrorCode, Event, JobKind, JobOptions, JobRequest,
    JobState, PluginInfo, PluginState, Request, Response,
};
use serde_json::{Value, json};
use tempfile::TempDir;
use tokio::sync::mpsc::UnboundedReceiver;

const CORE_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-core");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// Long enough to compile a component in a debug build on a slow runner.
const SETTLE_DEADLINE: Duration = Duration::from_secs(60);

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

/// A running core, killed at the end of the test. It owns its folder: the
/// plugins, their data, the log and the configuration.
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
        self.dir.path().join("cabinetos.json")
    }

    fn log_dir(&self) -> PathBuf {
        self.dir.path().join("logs")
    }

    /// Asks the core to exit and waits, so its log is complete.
    async fn stop(&mut self, client: &mut PipeClient) {
        assert_eq!(ask(client, Request::Shutdown).await, Response::Ok);
        let until = Instant::now() + STARTUP_DEADLINE;
        while self.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < until, "the core did not exit");
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    /// Every line of the core's log, parsed.
    fn log_lines(&self) -> Vec<Value> {
        let mut lines = Vec::new();
        for entry in fs::read_dir(self.log_dir()).unwrap() {
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

/// Starts a core with copies of the fixture `plugins` and `config` as its
/// configuration file.
fn start_core(plugins: &[&str], config: &Value) -> Core {
    start_core_with(plugins, config, &[], |_| {})
}

/// `start_core` with extra environment variables, and `prepare` run on the
/// plugins folder before the core starts.
fn start_core_with(
    plugins: &[&str],
    config: &Value,
    env: &[(&str, &str)],
    prepare: impl FnOnce(&Path),
) -> Core {
    let dir = scratch("plugins");
    for id in plugins {
        let to = dir.path().join("plugins").join(id);
        fs::create_dir_all(&to).unwrap();
        for file in ["plugin.json", "plugin.wasm"] {
            fs::copy(fixtures().join(id).join(file), to.join(file)).unwrap();
        }
    }
    prepare(&dir.path().join("plugins"));
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
        .envs(env.iter().copied())
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    Core { child, pipe, dir }
}

fn grants(pairs: &[(&str, &[&str])]) -> Value {
    let plugins: serde_json::Map<String, Value> = pairs
        .iter()
        .map(|(id, granted)| ((*id).to_owned(), json!({ "granted": granted })))
        .collect();
    json!({ "plugins": plugins })
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

/// A client that said hello, with its event stream.
async fn greeted(core: &Core) -> (PipeClient, UnboundedReceiver<Envelope<Event>>) {
    let mut client = connect(&core.pipe).await;
    let events = client.events().unwrap();
    let welcome = client.hello("plugins-test").await.unwrap();
    assert!(matches!(welcome.body, Response::Welcome { .. }));
    (client, events)
}

async fn ask(client: &mut PipeClient, request: Request) -> Response {
    client.request(request).await.unwrap().body
}

fn exec(command: &str, args: Value) -> Request {
    Request::ExecuteCommand {
        command: command.to_owned(),
        args,
    }
}

async fn plugins(client: &mut PipeClient) -> Vec<PluginInfo> {
    match ask(client, Request::ListPlugins).await {
        Response::Plugins { plugins } => plugins,
        other => panic!("expected plugins, got {other:?}"),
    }
}

async fn state(client: &mut PipeClient, id: &str) -> PluginState {
    plugins(client)
        .await
        .into_iter()
        .find(|plugin| plugin.id == id)
        .unwrap_or_else(|| panic!("no plugin {id}"))
        .state
}

async fn wait_state(
    client: &mut PipeClient,
    id: &str,
    check: impl Fn(&PluginState) -> bool,
) -> PluginState {
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let now = state(client, id).await;
        if check(&now) {
            return now;
        }
        assert!(Instant::now() < deadline, "{id} stayed {now:?}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

fn active(state: &PluginState) -> bool {
    *state == PluginState::Active
}

/// The next event that passes `check`.
async fn next_event(
    events: &mut UnboundedReceiver<Envelope<Event>>,
    check: impl Fn(&Event) -> bool,
) -> Event {
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
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

async fn commands(client: &mut PipeClient) -> Vec<CommandInfo> {
    match ask(client, Request::ListCommands).await {
        Response::Commands { commands } => commands,
        other => panic!("expected commands, got {other:?}"),
    }
}

fn error_of(reply: Response) -> (ErrorCode, String) {
    match reply {
        Response::Error { code, message } => (code, message),
        other => panic!("expected an error, got {other:?}"),
    }
}

/// Lists `C:\Windows` and closes the listing.
async fn lists_windows(client: &mut PipeClient) {
    let reply = ask(
        client,
        Request::ListDirectory {
            path: r"C:\Windows".to_owned(),
            include_hidden: None,
            sort: None,
            watch: false,
        },
    )
    .await;
    let Response::ListingOpened {
        listing_id,
        section_handle,
        entry_count,
        ..
    } = reply
    else {
        panic!("expected listing_opened, got {reply:?}");
    };
    drop(client.take_section(section_handle));
    assert!(entry_count > 10, "{entry_count}");
    assert_eq!(
        ask(client, Request::CloseListing { listing_id }).await,
        Response::Ok
    );
}

/// Phase 7's goal: a plugin that crashes is logged and removed while the
/// core keeps serving listings.
#[tokio::test]
async fn a_crashing_plugin_is_removed_while_the_core_keeps_serving_listings() {
    let mut core = start_core(
        &["crashy", "hello"],
        &grants(&[
            ("crashy", &["cmd:register"]),
            ("hello", &["cmd:register", "events:emit"]),
        ]),
    );
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "crashy", active).await;
    wait_state(&mut client, "hello", active).await;
    let listed = commands(&mut client).await;
    let crash = listed
        .iter()
        .find(|command| command.id == "crashy.crash")
        .expect("crashy registered its command");
    assert_eq!(
        crash.source,
        CommandSource::Plugin {
            id: "crashy".to_owned(),
            name: "Crashy".to_owned()
        }
    );

    let (error_code, message) = error_of(ask(&mut client, exec("crashy.crash", Value::Null)).await);
    assert_eq!(error_code, ErrorCode::PluginError);
    assert!(message.contains("crashy was asked to crash"), "{message}");
    let crashed = next_event(
        &mut events,
        |event| matches!(event, Event::PluginCrashed { plugin_id, .. } if plugin_id == "crashy"),
    )
    .await;
    assert!(
        matches!(crashed, Event::PluginCrashed { message, .. } if message.contains("wasm trap"))
    );
    assert!(matches!(
        state(&mut client, "crashy").await,
        PluginState::Crashed { .. }
    ));
    assert!(
        commands(&mut client)
            .await
            .iter()
            .all(|command| command.id != "crashy.crash"),
        "a crashed plugin's commands are unregistered"
    );
    let (error_code, message) = error_of(ask(&mut client, exec("crashy.crash", Value::Null)).await);
    assert_eq!(error_code, ErrorCode::UnknownCommand);
    assert!(
        message.contains("its plugin crashy is crashed"),
        "{message}"
    );

    // The same connection lists a directory right after, and the other
    // plugin still answers.
    lists_windows(&mut client).await;
    assert_eq!(
        ask(&mut client, exec("hello.say", Value::Null)).await,
        Response::CommandResult {
            result: json!({"message": "hello from Hello"})
        }
    );

    // reload_plugin brings it back at once.
    assert_eq!(
        ask(
            &mut client,
            Request::ReloadPlugin {
                plugin_id: "crashy".to_owned()
            }
        )
        .await,
        Response::Ok
    );
    wait_state(&mut client, "crashy", active).await;
    assert!(
        commands(&mut client)
            .await
            .iter()
            .any(|command| command.id == "crashy.crash")
    );

    core.stop(&mut client).await;
    let lines = core.log_lines();
    let trap = lines
        .iter()
        .find(|line| line["level"] == "ERROR" && line["plugin_id"] == "crashy")
        .expect("the crash is logged at ERROR with the plugin's ID");
    assert_eq!(trap["boundary"], "plugin");
    let details = trap["fields"]["details"].as_str().unwrap_or_default();
    assert!(details.contains("wasm backtrace"), "{trap}");
    // The fixtures keep their function names (sdk/templates/plugins).
    assert!(details.contains("Guest>::on_command"), "{trap}");
    // The panic's own text, as the plugin wrote it to stderr.
    let stderr = lines
        .iter()
        .find(|line| line["message"] == "crashy was asked to crash")
        .expect("the plugin's stderr line is in the log");
    assert_eq!(stderr["level"], "WARN");
    assert_eq!(stderr["boundary"], "plugin");
    assert_eq!(stderr["plugin_id"], "crashy");
    assert_eq!(stderr["fields"]["stream"], "stderr");
}

#[tokio::test]
async fn a_command_that_never_returns_is_stopped_at_its_deadline() {
    let core = start_core(&["spinner"], &grants(&[("spinner", &["cmd:register"])]));
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "spinner", active).await;

    let started = Instant::now();
    let (error_code, message) = error_of(ask(&mut client, exec("spinner.spin", Value::Null)).await);
    let took = started.elapsed();
    assert_eq!(error_code, ErrorCode::PluginError);
    assert!(
        message.contains("did not finish within 5000 ms"),
        "{message}"
    );
    assert!(
        took >= Duration::from_millis(4900) && took < Duration::from_secs(8),
        "{took:?}"
    );
    next_event(
        &mut events,
        |event| matches!(event, Event::PluginCrashed { plugin_id, .. } if plugin_id == "spinner"),
    )
    .await;
    assert!(matches!(
        ask(&mut client, Request::Ping).await,
        Response::Pong { .. }
    ));
}

#[tokio::test]
async fn grants_and_saved_settings_take_effect_without_a_restart() {
    let core = start_core(&["hello"], &grants(&[("hello", &["cmd:register"])]));
    let (mut client, mut events) = greeted(&core).await;
    assert_eq!(
        wait_state(&mut client, "hello", |state| *state != PluginState::Loading).await,
        PluginState::NeedsReview {
            missing: vec!["events:emit".to_owned()]
        }
    );

    let refused = |plugin: &str, capability: &str| Request::GrantCapabilities {
        plugin_id: plugin.to_owned(),
        capabilities: vec![capability.to_owned()],
    };
    let (error_code, message) = error_of(ask(&mut client, refused("hello", "process:run")).await);
    assert_eq!(error_code, ErrorCode::PluginError);
    assert!(message.contains("never granted"), "{message}");
    let (error_code, _) = error_of(ask(&mut client, refused("nobody", "cmd:register")).await);
    assert_eq!(error_code, ErrorCode::NoSuchPlugin);

    assert_eq!(
        ask(&mut client, refused("hello", "events:emit")).await,
        Response::Ok
    );
    wait_state(&mut client, "hello", active).await;
    let saved: Value =
        serde_json::from_str(&fs::read_to_string(core.config_path()).unwrap()).unwrap();
    assert_eq!(
        saved["plugins"]["hello"]["granted"],
        json!(["cmd:register", "events:emit"])
    );

    // An edit saved in the file, as a user would make it.
    let mut edited = saved;
    edited["plugins"]["hello"]["enabled"] = json!(false);
    fs::write(
        core.config_path(),
        serde_json::to_string_pretty(&edited).unwrap(),
    )
    .unwrap();
    next_event(&mut events, |event| {
        matches!(event, Event::PluginStateChanged { plugin_id, state: PluginState::Disabled } if plugin_id == "hello")
    })
    .await;
    assert!(
        commands(&mut client)
            .await
            .iter()
            .all(|command| command.id != "hello.say")
    );

    assert_eq!(
        ask(
            &mut client,
            Request::SetPluginEnabled {
                plugin_id: "hello".to_owned(),
                enabled: true
            }
        )
        .await,
        Response::Ok
    );
    wait_state(&mut client, "hello", active).await;
}

#[tokio::test]
async fn a_plugin_can_stop_a_job_before_it_starts() {
    let core = start_core(&["vetoer"], &grants(&[("vetoer", &["jobs:intercept"])]));
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "vetoer", active).await;
    let work = scratch("vetoer");
    let source = work.path().join("a.txt");
    fs::write(&source, "12345").unwrap();

    let copy_to = |destination: &Path| {
        Request::StartJob(JobRequest {
            kind: JobKind::Copy,
            sources: vec![source.display().to_string()],
            destination: Some(destination.display().to_string()),
            options: JobOptions::default(),
        })
    };
    let finished = async |client: &mut PipeClient,
                          events: &mut UnboundedReceiver<Envelope<Event>>,
                          request: Request| {
        let Response::JobStarted { job_id } = ask(client, request).await else {
            panic!("the job did not start");
        };
        match next_event(events, |event| {
            matches!(event, Event::JobStateChanged { job_id: id, state } if *id == job_id && state.is_terminal())
        })
        .await
        {
            Event::JobStateChanged { state, .. } => state,
            _ => unreachable!(),
        }
    };

    let forbidden = work.path().join("Forbidden");
    let state = finished(&mut client, &mut events, copy_to(&forbidden)).await;
    assert_eq!(
        state,
        JobState::Failed {
            message: format!(
                "denied by plugin vetoer: {} is a forbidden destination",
                forbidden.display()
            )
        }
    );
    assert!(!forbidden.exists(), "a refused job creates nothing");

    let fine = work.path().join("fine");
    let state = finished(&mut client, &mut events, copy_to(&fine)).await;
    assert_eq!(state, JobState::Completed);
    assert!(fine.join("a.txt").is_file());
}

#[tokio::test]
async fn a_plugin_s_output_reaches_the_core_log_marked_with_its_id() {
    let mut core = start_core(
        &["hello"],
        &grants(&[("hello", &["cmd:register", "events:emit"])]),
    );
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "hello", active).await;
    assert_eq!(
        ask(&mut client, exec("hello.say", json!({"loud": true}))).await,
        Response::CommandResult {
            result: json!({"message": "hello from Hello"})
        }
    );
    assert_eq!(
        next_event(&mut events, |event| matches!(
            event,
            Event::PluginEvent { .. }
        ))
        .await,
        Event::PluginEvent {
            plugin_id: "hello".to_owned(),
            name: "hello.said".to_owned(),
            payload: r#"{"greeting":"hello"}"#.to_owned(),
        }
    );
    core.stop(&mut client).await;

    let lines = core.log_lines();
    let printed = lines
        .iter()
        .find(|line| line["message"] == "Hello says hello")
        .expect("the plugin's stdout line is in the log");
    assert_eq!(printed["boundary"], "plugin");
    assert_eq!(printed["plugin_id"], "hello");
    assert_eq!(printed["fields"]["stream"], "stdout");
    let logged = lines
        .iter()
        .find(|line| {
            line["message"]
                .as_str()
                .is_some_and(|message| message.starts_with("Hello is active"))
        })
        .expect("the plugin's log call is in the log");
    assert_eq!(logged["boundary"], "plugin");
    assert_eq!(logged["plugin_id"], "hello");
    assert!(core.dir.path().join("plugins-data").join("hello").is_dir());
}

#[tokio::test]
async fn a_reader_is_told_about_listings_of_its_folder() {
    // The reader fixture's fs:read root.
    let root = std::env::temp_dir().join(r"cabinetos-plugins-test\reader");
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("inside.txt"), "12345").unwrap();
    let mut core = start_core(
        &["reader"],
        &grants(&[("reader", &["cmd:register", "fs:read"])]),
    );
    let (mut client, _events) = greeted(&core).await;
    wait_state(&mut client, "reader", active).await;

    let size = ask(
        &mut client,
        exec("reader.size", json!({ "path": root.join("inside.txt") })),
    )
    .await;
    let path = root.display().to_string();
    let reply = ask(
        &mut client,
        Request::ListDirectory {
            path: path.clone(),
            include_hidden: None,
            sort: None,
            watch: false,
        },
    )
    .await;
    if let Response::ListingOpened { section_handle, .. } = &reply {
        drop(client.take_section(*section_handle));
    }
    // The notification goes to the plugin's thread; give it a moment.
    tokio::time::sleep(Duration::from_millis(500)).await;
    core.stop(&mut client).await;
    let _ = fs::remove_dir_all(root.parent().unwrap());

    assert_eq!(
        size,
        Response::CommandResult {
            result: json!({"size": 5})
        }
    );
    assert!(matches!(reply, Response::ListingOpened { .. }), "{reply:?}");
    let told = format!("a pane opened /{} with 1 entries", path.replace('\\', "/"));
    let lines = core.log_lines();
    assert!(
        lines
            .iter()
            .any(|line| line["message"] == told.as_str() && line["plugin_id"] == "reader"),
        "no `{told}` in the log"
    );
}

/// Answers one web request on a free port of 127.0.0.1 with a short text,
/// and hands back what it got, head and body.
fn one_request_server() -> (u16, std::thread::JoinHandle<String>) {
    use std::io::{Read, Write};
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let server = std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut head = Vec::new();
        let mut byte = [0_u8; 1];
        while !head.ends_with(b"\r\n\r\n") {
            assert_eq!(
                stream.read(&mut byte).unwrap(),
                1,
                "the request ended early"
            );
            head.push(byte[0]);
        }
        let answer = "hello from the test server";
        write!(
            stream,
            "HTTP/1.1 200 OK\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{answer}",
            answer.len()
        )
        .unwrap();
        String::from_utf8(head).unwrap()
    });
    (port, server)
}

#[tokio::test]
async fn a_plugin_reaches_its_host_with_a_secret_from_the_credential_manager() {
    let (port, server) = one_request_server();
    let prefix = format!("CabinetOS-test-{}-{}/", std::process::id(), line!());
    let secret = "sk-test-NET-DO-NOT-LOG-41d8";
    let mut core = start_core_with(
        &["fetcher"],
        &grants(&[("fetcher", &["cmd:register", "net"])]),
        &[
            ("CABINETOS_SECRETS_PREFIX", prefix.as_str()),
            // The most detailed log: the value must not reach even a trace line.
            ("CABINETOS_LOG", "trace"),
        ],
        |plugins| {
            let path = plugins.join("fetcher").join("plugin.json");
            let mut manifest: Value =
                serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
            manifest["capabilities"][1]["hosts"] = json!([format!("127.0.0.1:{port}")]);
            fs::write(&path, manifest.to_string()).unwrap();
        },
    );
    let (mut client, _events) = greeted(&core).await;
    wait_state(&mut client, "fetcher", active).await;
    let set = Request::SecretSet {
        name: "fetcher-test".to_owned(),
        value: cabinetos_protocol::SecretText(secret.to_owned()),
    };
    assert_eq!(ask(&mut client, set).await, Response::Ok);

    let reply = ask(
        &mut client,
        exec(
            "fetcher.get",
            json!({
                "url": format!("http://127.0.0.1:{port}/v1/models"),
                "secret": "fetcher-test",
                "header": "x-api-key",
            }),
        ),
    )
    .await;
    let delete = Request::SecretDelete {
        name: "fetcher-test".to_owned(),
    };
    assert_eq!(ask(&mut client, delete).await, Response::Ok);
    core.stop(&mut client).await;

    let Response::CommandResult { result } = reply else {
        panic!("{reply:?}")
    };
    assert_eq!(result["status"], 200);
    assert_eq!(result["body"], "hello from the test server");
    assert!(!result.to_string().contains(secret), "{result}");
    let head = server.join().unwrap();
    assert!(head.starts_with("GET /v1/models HTTP/1.1\r\n"), "{head}");
    assert!(head.contains(&format!("x-api-key: {secret}\r\n")), "{head}");
    let lines = core.log_lines();
    let request = lines
        .iter()
        .find(|line| line["message"] == "web request")
        .expect("no `web request` line in the log");
    assert_eq!(request["plugin_id"], "fetcher", "{request}");
    assert_eq!(request["fields"]["host"], "127.0.0.1", "{request}");
    assert_eq!(request["fields"]["status"], 200, "{request}");
    assert_eq!(request["fields"]["bytes"], 26, "{request}");
    assert!(
        lines.iter().all(|line| !line.to_string().contains(secret)),
        "the secret reached the log"
    );
}

#[tokio::test]
async fn a_plugin_hears_about_changes_in_a_folder_it_watches() {
    let watched = scratch("watched");
    let root = watched.path().display().to_string();
    let core = start_core_with(
        &["watcher"],
        &grants(&[("watcher", &["cmd:register", "events:emit", "fs:watch"])]),
        &[],
        |plugins| {
            let path = plugins.join("watcher").join("plugin.json");
            let mut manifest: Value =
                serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
            manifest["capabilities"][2]["roots"] = json!([root]);
            fs::write(&path, manifest.to_string()).unwrap();
        },
    );
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "watcher", active).await;
    let reply = ask(
        &mut client,
        exec("watcher.watch", json!({ "path": root.as_str() })),
    )
    .await;
    assert!(matches!(reply, Response::CommandResult { .. }), "{reply:?}");
    fs::write(watched.path().join("new.txt"), "x").unwrap();

    let event = next_event(
        &mut events,
        |event| matches!(event, Event::PluginEvent { name, .. } if name == "folder-changed"),
    )
    .await;
    let Event::PluginEvent {
        plugin_id, payload, ..
    } = event
    else {
        unreachable!()
    };
    assert_eq!(plugin_id, "watcher");
    let payload: Value = serde_json::from_str(&payload).unwrap();
    assert_eq!(payload["path"], root.as_str());
    assert_eq!(payload["changes"][0]["kind"], "created", "{payload}");
    assert_eq!(
        payload["changes"][0]["path"],
        watched.path().join("new.txt").display().to_string()
    );
}

#[tokio::test]
async fn a_plugin_command_that_asks_for_text_says_so_in_the_list() {
    let core = start_core(
        &["fetcher", "hello"],
        &grants(&[
            ("fetcher", &["cmd:register", "net"]),
            ("hello", &["cmd:register", "events:emit"]),
        ]),
    );
    let (mut client, _events) = greeted(&core).await;
    wait_state(&mut client, "fetcher", active).await;
    wait_state(&mut client, "hello", active).await;

    let commands = commands(&mut client).await;
    let input_of = |id: &str| {
        commands
            .iter()
            .find(|command| command.id == id)
            .unwrap_or_else(|| panic!("no {id}"))
            .input
            .clone()
    };
    assert_eq!(
        input_of("fetcher.get"),
        Some(cabinetos_protocol::CommandInput {
            title: Some("Fetch a URL".to_owned()),
            placeholder: Some("http://localhost:8090/...".to_owned()),
        })
    );
    assert_eq!(input_of("hello.say"), None);
    assert_eq!(input_of("view.toggleDualPane"), None);

    // The window sends the text as `input`: the fixture takes it as its
    // URL, which the manifest's hosts then refuse, with no request made.
    let (error_code, message) = error_of(
        ask(
            &mut client,
            exec("fetcher.get", json!({ "input": "https://example.com/" })),
        )
        .await,
    );
    assert_eq!(error_code, ErrorCode::PluginError);
    assert!(
        message.contains("example.com:443 is not among the hosts"),
        "{message}"
    );
}

/// The requester fixture's grants: everything it asks for.
const REQUESTER_GRANTS: &[&str] = &["cmd:register", "config:read", "events:emit", "core:request"];

/// `requester.ask` with `request`: the core's reply as JSON, or the error.
async fn ask_core(client: &mut PipeClient, request: Value) -> Result<Value, (ErrorCode, String)> {
    match ask(client, exec("requester.ask", json!({ "request": request }))).await {
        Response::CommandResult { result } => Ok(result["reply"].clone()),
        other => Err(error_of(other)),
    }
}

/// A plugin proposes a preview and undoes what applied it, all through
/// `core-request`; the core answers in the plugin's name.
#[tokio::test(flavor = "multi_thread")]
#[expect(
    clippy::too_many_lines,
    reason = "one plugin's requests from end to end"
)]
async fn a_plugin_proposes_a_preview_and_undoes_it_through_core_requests() {
    let core = start_core(&["requester"], &grants(&[("requester", REQUESTER_GRANTS)]));
    let files = scratch("requests");
    fs::write(files.path().join("a.txt"), b"alpha").unwrap();
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "requester", active).await;

    // No window has spoken: the core's own error comes back as an answer.
    let reply = ask_core(&mut client, json!({ "type": "get_window_state" }))
        .await
        .unwrap();
    assert_eq!(reply["type"], "error", "{reply}");
    assert_eq!(reply["code"], "no_window", "{reply}");
    assert!(
        reply["id"].as_str().is_some_and(|id| id.len() == 26),
        "{reply}"
    );
    let window = cabinetos_protocol::WindowState {
        active_pane: cabinetos_protocol::Pane::Left,
        panes: cabinetos_protocol::WindowPanes {
            left: cabinetos_protocol::PaneState {
                tabs: vec![cabinetos_protocol::WindowTab {
                    path: files.path().display().to_string(),
                    locked: false,
                    tool: None,
                }],
                active: 0,
                cursor: None,
                marked: Vec::new(),
            },
            right: cabinetos_protocol::PaneState::default(),
        },
    };
    assert_eq!(
        ask(&mut client, Request::WindowState(window)).await,
        Response::Ok
    );
    let reply = ask_core(&mut client, json!({ "type": "get_window_state" }))
        .await
        .unwrap();
    assert_eq!(reply["type"], "window_state", "{reply}");
    assert_eq!(
        reply["state"]["panes"]["left"]["tabs"][0]["path"],
        files.path().display().to_string()
    );

    // A listing comes back as its description only: nothing to map.
    let reply = ask_core(
        &mut client,
        json!({ "type": "list_directory", "path": files.path().display().to_string(), "watch": true }),
    )
    .await
    .unwrap();
    assert_eq!(reply["type"], "listing_opened", "{reply}");
    assert_eq!(reply["section_handle"], 0, "{reply}");
    assert_eq!(reply["entry_count"], 1, "{reply}");

    // The plugin proposes; nothing changes on disk until the window applies.
    let from = files.path().join("a.txt").display().to_string();
    let reply = ask_core(
        &mut client,
        json!({
            "type": "preview_listing",
            "title": "Rename a",
            "rows": [{ "path": from, "kind": "rename", "to": "vacation_a.txt" }],
        }),
    )
    .await
    .unwrap();
    assert_eq!(reply["type"], "preview_opened", "{reply}");
    assert_eq!(reply["listing"]["section_handle"], 0, "{reply}");
    assert_eq!(reply["listing"]["entry_count"], 1, "{reply}");
    let preview = reply["preview"].as_str().unwrap().to_owned();
    assert!(files.path().join("a.txt").exists());

    let applied = ask(&mut client, Request::PreviewApply { preview }).await;
    let Response::JobsStarted { jobs } = applied else {
        panic!("{applied:?}")
    };
    let renamed = files.path().join("vacation_a.txt");
    let deadline = Instant::now() + SETTLE_DEADLINE;
    while !renamed.exists() {
        assert!(Instant::now() < deadline, "the preview was not applied");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    next_event(&mut events, |event| {
        matches!(event, Event::JobStateChanged { job_id, state }
            if *job_id == jobs[0] && state.is_terminal())
    })
    .await;

    // Undo through the same door: the newest job that is not an undo.
    let reply = ask_core(&mut client, json!({ "type": "undo_job" }))
        .await
        .unwrap();
    assert_eq!(reply["type"], "undo_started", "{reply}");
    assert_eq!(reply["undoes"], jobs[0], "{reply}");
    let deadline = Instant::now() + SETTLE_DEADLINE;
    while !files.path().join("a.txt").exists() {
        assert!(Instant::now() < deadline, "the rename was not undone");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    // A plugin applies its own preview, too (a tier that needs no window).
    let reply = ask_core(
        &mut client,
        json!({
            "type": "preview_listing",
            "title": "Make a folder",
            "rows": [{ "path": format!("{}\\Sorted\\", files.path().display()), "kind": "create" }],
        }),
    )
    .await
    .unwrap();
    let reply = ask_core(
        &mut client,
        json!({ "type": "preview_apply", "preview": reply["preview"] }),
    )
    .await
    .unwrap();
    assert_eq!(reply["type"], "jobs_started", "{reply}");
    let deadline = Instant::now() + SETTLE_DEADLINE;
    while !files.path().join("Sorted").is_dir() {
        assert!(
            Instant::now() < deadline,
            "the plugin's own apply did not run"
        );
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    assert!(active(&state(&mut client, "requester").await));
}

#[tokio::test]
async fn a_plugin_is_refused_what_its_manifest_does_not_list_and_what_no_manifest_allows() {
    let mut core = start_core(&["requester"], &grants(&[("requester", REQUESTER_GRANTS)]));
    let (mut client, _events) = greeted(&core).await;
    wait_state(&mut client, "requester", active).await;

    // Not in the manifest: the type is named in the refusal.
    let (error_code, message) = ask_core(&mut client, json!({ "type": "list_jobs" }))
        .await
        .unwrap_err();
    assert_eq!(error_code, ErrorCode::PluginError);
    assert!(message.contains("`list_jobs`"), "{message}");
    // In the manifest on purpose, and refused all the same.
    for kind in ["hello", "secret_get"] {
        let (_, message) = ask_core(&mut client, json!({ "type": kind, "name": "anthropic" }))
            .await
            .unwrap_err();
        assert!(message.contains(&format!("`{kind}`")), "{message}");
        assert!(message.contains("never allowed"), "{message}");
    }
    // Without the grant the plugin does not even start.
    core.stop(&mut client).await;
    let core = start_core(
        &["requester"],
        &grants(&[("requester", &["cmd:register", "config:read", "events:emit"])]),
    );
    let (mut client, _events) = greeted(&core).await;
    let state = wait_state(&mut client, "requester", |state| {
        matches!(state, PluginState::NeedsReview { .. })
    })
    .await;
    assert_eq!(
        state,
        PluginState::NeedsReview {
            missing: vec!["core:request".to_owned()]
        }
    );
    let listed = plugins(&mut client).await;
    let capability = listed[0]
        .capabilities
        .iter()
        .find(|capability| capability.name == "core:request")
        .unwrap();
    assert!(capability.requests.contains(&"preview_listing".to_owned()));
}

/// `config set plugins.<id>.settings.<key> <value>` through the existing
/// `set_value`, read back by the plugin with `config-get`, and told to it
/// as an event without a restart.
#[tokio::test]
async fn a_plugin_reads_its_own_settings_from_the_file_and_is_told_of_a_change() {
    let core = start_core(&["requester"], &grants(&[("requester", REQUESTER_GRANTS)]));
    let (mut client, mut events) = greeted(&core).await;
    wait_state(&mut client, "requester", active).await;
    let read = |path: &'static str| exec("requester.setting", json!({ "path": path }));
    let Response::CommandResult { result } =
        ask(&mut client, read("plugins.requester.settings")).await
    else {
        panic!("no result")
    };
    assert_eq!(
        result["value"],
        json!({}),
        "no settings yet: an empty object"
    );

    let set = Request::SetValue {
        path: "plugins.requester.settings.provider".to_owned(),
        value: json!("anthropic"),
    };
    assert_eq!(ask(&mut client, set).await, Response::Ok);
    let told = next_event(
        &mut events,
        |event| matches!(event, Event::PluginEvent { name, .. } if name == "settings-changed"),
    )
    .await;
    assert!(matches!(told, Event::PluginEvent { plugin_id, .. } if plugin_id == "requester"));
    assert!(
        active(&state(&mut client, "requester").await),
        "not restarted"
    );

    let value_of = |reply: Response| match reply {
        Response::CommandResult { result } => result["value"].clone(),
        other => panic!("{other:?}"),
    };
    let own = value_of(ask(&mut client, read("plugins.requester.settings.provider")).await);
    assert_eq!(own, "anthropic");
    let all = value_of(ask(&mut client, read("plugins.requester.settings")).await);
    assert_eq!(all, json!({ "provider": "anthropic" }));
    // Its grants, its switch and the section itself are not its business.
    for hidden in ["plugins", "plugins.requester", "plugins.requester.granted"] {
        let hidden = value_of(ask(&mut client, read(hidden)).await);
        assert_eq!(hidden, Value::Null);
    }
    // The file says it, and a hand edit is told to the plugin as well.
    let text = fs::read_to_string(core.config_path()).unwrap();
    assert!(text.contains(r#""provider": "anthropic""#), "{text}");
    let edited = text.replace("anthropic", "openai");
    fs::write(core.config_path(), edited).unwrap();
    next_event(
        &mut events,
        |event| matches!(event, Event::PluginEvent { name, .. } if name == "settings-changed"),
    )
    .await;
    let own = value_of(ask(&mut client, read("plugins.requester.settings.provider")).await);
    assert_eq!(own, "openai");
}
