//! The plugin host against the committed fixture plugins
//! (`sdk/fixtures/plugins`, built by `sdk/templates/build-fixtures.ps1`):
//! manifests, capability gating, crashes, deadlines, fuel, memory, job
//! judging, web requests to a test server on this computer, folder
//! watching, and the Agent extension's plugin (`sdk/extensions/agent`, built
//! by `sdk/extensions/build-extensions.ps1`) run with its fake provider. No
//! WebAssembly toolchain is needed to run these.

use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use cabinetos_plugins::{HostConfig, HostServices, PluginCommand, PluginHost, PluginSettings};
use cabinetos_protocol::{ErrorCode, Event, JobKind, PluginState};

/// What the host told the core.
#[derive(Default)]
struct Recorder {
    events: Mutex<Vec<Event>>,
    command_sets: Mutex<Vec<(String, Vec<String>)>>,
    /// The requests plugins sent with `core-request`: the plugin and the
    /// JSON the core would have run.
    core_requests: Mutex<Vec<(String, String)>>,
    /// What `plugins.agent.settings` answers, as JSON text.
    agent_settings: Mutex<Option<String>>,
    /// How many previews the pretend core has opened for the agent.
    previews: Mutex<u64>,
}

/// What the real core answers the agent's requests with, in the forms the
/// protocol gives them; a window state is "no window", as it is with none.
fn agent_reply(request: &str, previews: &mut u64) -> String {
    let request: serde_json::Value = serde_json::from_str(request).unwrap();
    let reply = match request["type"].as_str().unwrap_or_default() {
        "preview_listing" => {
            *previews += 1;
            serde_json::json!({
                "type": "preview_opened",
                "preview": format!("preview-{previews}"),
                "title": request["title"],
                "listing": { "listing_id": previews, "section_handle": 0, "section_size": 100,
                             "entry_count": request["rows"].as_array().map_or(0, Vec::len),
                             "generation": 1, "elapsed_us": 5 },
            })
        }
        "preview_apply" => serde_json::json!({ "type": "jobs_started", "jobs": [11, 12] }),
        "undo_job" => {
            serde_json::json!({ "type": "undo_started", "job_id": 40, "undoes": request["job"], "left": [] })
        }
        "get_window_state" => {
            serde_json::json!({ "type": "error", "code": "no_window", "message": "no window has said what it shows" })
        }
        _ => serde_json::json!({ "type": "ok" }),
    };
    reply.to_string()
}

impl HostServices for Recorder {
    fn config_value(&self, path: &str) -> Option<String> {
        // The host decides what a plugin may see: this answers everything
        // it is asked, so a leak would show.
        match path {
            "appearance.theme" => Some("\"dark\"".to_owned()),
            "plugins.requester.settings.provider" => Some("\"fake\"".to_owned()),
            "plugins.other.settings.provider" => Some("\"leak\"".to_owned()),
            "plugins.requester.granted" => Some("[\"core:request\"]".to_owned()),
            "plugins" => Some("{\"other\":{}}".to_owned()),
            "plugins.agent.settings" => self.agent_settings.lock().unwrap().clone(),
            _ => None,
        }
    }

    fn core_request(&self, plugin_id: &str, request: &str) -> Result<String, String> {
        self.core_requests
            .lock()
            .unwrap()
            .push((plugin_id.to_owned(), request.to_owned()));
        if request.contains("no_window") {
            return Err("the core says no".to_owned());
        }
        if plugin_id == "agent" {
            return Ok(agent_reply(request, &mut self.previews.lock().unwrap()));
        }
        Ok(r#"{"id":"reply","type":"ok"}"#.to_owned())
    }

    fn publish(&self, event: Event) {
        self.events.lock().unwrap().push(event);
    }

    fn secret(&self, name: &str) -> Option<String> {
        (name == "fetcher-test").then(|| SECRET.to_owned())
    }

    fn set_commands(&self, plugin_id: &str, _plugin_name: &str, commands: &[PluginCommand]) {
        let ids = commands.iter().map(|command| command.id.clone()).collect();
        self.command_sets
            .lock()
            .unwrap()
            .push((plugin_id.to_owned(), ids));
    }
}

impl Recorder {
    fn events(&self) -> Vec<Event> {
        self.events.lock().unwrap().clone()
    }

    /// The commands the plugin has now, as the core would see them.
    fn commands(&self, plugin_id: &str) -> Option<Vec<String>> {
        self.command_sets
            .lock()
            .unwrap()
            .iter()
            .rev()
            .find(|(id, _)| id == plugin_id)
            .map(|(_, commands)| commands.clone())
    }

    fn crashes(&self, plugin_id: &str) -> Vec<String> {
        self.events()
            .into_iter()
            .filter_map(|event| match event {
                Event::PluginCrashed {
                    plugin_id: id,
                    message,
                } if id == plugin_id => Some(message),
                _ => None,
            })
            .collect()
    }
}

/// The value of the one stored secret, `fetcher-test`.
const SECRET: &str = "s3cr3t-for-the-tests";

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/fixtures/plugins")
}

/// A plugins folder with copies of some fixtures, and a data folder.
struct Setup {
    temp: tempfile::TempDir,
    recorder: Arc<Recorder>,
}

impl Setup {
    fn new(ids: &[&str]) -> Self {
        let temp = tempfile::tempdir().unwrap();
        for id in ids {
            let to = temp.path().join("plugins").join(id);
            std::fs::create_dir_all(&to).unwrap();
            for file in ["plugin.json", "plugin.wasm"] {
                std::fs::copy(fixtures().join(id).join(file), to.join(file)).unwrap();
            }
        }
        Self {
            temp,
            recorder: Arc::new(Recorder::default()),
        }
    }

    fn plugins(&self) -> PathBuf {
        self.temp.path().join("plugins")
    }

    fn data(&self) -> PathBuf {
        self.temp.path().join("data")
    }

    fn edit_manifest(&self, id: &str, change: impl FnOnce(&mut serde_json::Value)) {
        let path = self.plugins().join(id).join("plugin.json");
        let mut manifest: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        change(&mut manifest);
        std::fs::write(&path, serde_json::to_string_pretty(&manifest).unwrap()).unwrap();
    }

    fn host(&self, change: impl FnOnce(&mut HostConfig)) -> PluginHost {
        let mut config = HostConfig::new(self.plugins(), self.data(), "0.1.0");
        config.restart_delay = Duration::from_millis(100);
        change(&mut config);
        PluginHost::new(config, Arc::clone(&self.recorder) as Arc<dyn HostServices>).unwrap()
    }
}

fn grants(pairs: &[(&str, &[&str])]) -> BTreeMap<String, PluginSettings> {
    pairs
        .iter()
        .map(|(id, granted)| {
            (
                (*id).to_owned(),
                PluginSettings {
                    enabled: true,
                    granted: granted.iter().map(|name| (*name).to_owned()).collect(),
                    ..PluginSettings::default()
                },
            )
        })
        .collect()
}

fn state(host: &PluginHost, id: &str) -> PluginState {
    host.list()
        .into_iter()
        .find(|plugin| plugin.id == id)
        .unwrap_or_else(|| panic!("no plugin {id}"))
        .state
}

/// Waits until the plugin's state passes the check. Compiling a component
/// takes a moment, most of all in a debug build.
fn wait_until(host: &PluginHost, id: &str, check: impl Fn(&PluginState) -> bool) -> PluginState {
    let deadline = Instant::now() + Duration::from_secs(60);
    loop {
        let now = state(host, id);
        if check(&now) {
            return now;
        }
        assert!(Instant::now() < deadline, "{id} stayed {now:?}");
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn active(state: &PluginState) -> bool {
    *state == PluginState::Active
}

fn crashed(state: &PluginState) -> bool {
    matches!(state, PluginState::Crashed { .. })
}

#[test]
fn hello_answers_logs_and_sends_an_event() {
    let setup = Setup::new(&["hello"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("hello", &["cmd:register", "events:emit"])]));
    wait_until(&host, "hello", active);

    assert_eq!(
        setup.recorder.commands("hello"),
        Some(vec!["hello.say".to_owned()])
    );
    let answer = host.execute("hello", "hello.say", "{}").unwrap();
    assert_eq!(answer, r#"{"message":"hello from Hello"}"#);
    assert!(
        setup.recorder.events().contains(&Event::PluginEvent {
            plugin_id: "hello".to_owned(),
            name: "hello.said".to_owned(),
            payload: r#"{"greeting":"hello"}"#.to_owned(),
        }),
        "{:?}",
        setup.recorder.events()
    );
    assert!(
        setup.data().join("hello").is_dir(),
        "the plugin's own folder is created"
    );

    let info = host
        .list()
        .into_iter()
        .find(|plugin| plugin.id == "hello")
        .unwrap();
    assert_eq!(
        (info.name.as_str(), info.version.as_str()),
        ("Hello", "0.1.0")
    );
    assert_eq!(info.commands, ["hello.say"]);
    assert!(
        info.capabilities
            .iter()
            .all(|capability| capability.granted)
    );

    let unknown = host.execute("hello", "hello.nope", "{}").unwrap_err();
    assert_eq!(unknown.code, ErrorCode::UnknownCommand);
}

#[test]
fn a_plugin_waits_until_every_capability_is_granted() {
    let setup = Setup::new(&["hello"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("hello", &["cmd:register"])]));
    assert_eq!(
        state(&host, "hello"),
        PluginState::NeedsReview {
            missing: vec!["events:emit".to_owned()]
        }
    );
    let refused = host.execute("hello", "hello.say", "{}").unwrap_err();
    assert!(refused.message.contains("events:emit"), "{refused}");

    host.apply_settings(&grants(&[("hello", &["cmd:register", "events:emit"])]));
    wait_until(&host, "hello", active);

    let mut off = grants(&[("hello", &["cmd:register", "events:emit"])]);
    off.get_mut("hello").unwrap().enabled = false;
    host.apply_settings(&off);
    assert_eq!(state(&host, "hello"), PluginState::Disabled);
    assert_eq!(setup.recorder.commands("hello"), Some(Vec::new()));
}

#[test]
fn manifest_problems_fail_the_plugin_with_a_reason() {
    let failed = |change: fn(&mut serde_json::Value)| {
        let setup = Setup::new(&["hello"]);
        setup.edit_manifest("hello", change);
        let host = setup.host(|_| {});
        host.load_all(&grants(&[("hello", &["cmd:register", "events:emit"])]));
        match wait_until(&host, "hello", |state| {
            matches!(state, PluginState::Failed { .. })
        }) {
            PluginState::Failed { message } => message,
            _ => unreachable!(),
        }
    };
    let unknown_key = failed(|manifest| manifest["colour"] = "red".into());
    assert!(unknown_key.contains("colour"), "{unknown_key}");
    // A 0.1 component lacks what 0.2 added: refused before it is compiled.
    let api = failed(|manifest| manifest["apiVersion"] = "0.1.0".into());
    assert!(api.contains("apiVersion 0.1.0"), "{api}");
    let never = failed(|manifest| {
        manifest["capabilities"]
            .as_array_mut()
            .unwrap()
            .push(serde_json::json!({"name": "process:run", "reason": "Starts programs."}));
    });
    assert!(never.contains("never grants"), "{never}");
    // The component registers hello.say, which this manifest no longer
    // declares: activation fails.
    let undeclared = failed(|manifest| manifest["commands"] = serde_json::json!([]));
    assert!(
        undeclared.contains("hello.say is not declared"),
        "{undeclared}"
    );
}

#[test]
fn a_missing_component_or_a_stranger_in_the_configuration_is_reported() {
    let setup = Setup::new(&["hello"]);
    std::fs::remove_file(setup.plugins().join("hello").join("plugin.wasm")).unwrap();
    let host = setup.host(|_| {});
    host.load_all(&grants(&[
        ("hello", &["cmd:register", "events:emit"]),
        ("nobody", &["cmd:register"]),
    ]));
    let PluginState::Failed { message } = state(&host, "hello") else {
        panic!("{:?}", state(&host, "hello"));
    };
    assert!(message.contains("plugin.wasm is missing"), "{message}");
    assert_eq!(
        host.list().len(),
        1,
        "a configured plugin that is not installed is only logged"
    );
    assert_eq!(
        host.reload("nobody").unwrap_err().code,
        ErrorCode::NoSuchPlugin
    );
}

#[test]
fn capabilities_gate_the_file_system() {
    let setup = Setup::new(&["reader"]);
    // The fixture's fs:read root: fixed by its manifest, so two test runs at the same time
    // (two worktrees building at once) share it. Nothing here deletes it, or the other run
    // loses its file under its feet; only a stale new.txt from a failed run is cleared.
    let root = PathBuf::from(std::env::var("TEMP").unwrap()).join(r"cabinetos-plugins-test\reader");
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("inside.txt"), "12345").unwrap();
    let _ = std::fs::remove_file(root.join("new.txt"));
    let outside = setup.temp.path().join("outside.txt");
    std::fs::write(&outside, "123").unwrap();

    let host = setup.host(|_| {});
    host.load_all(&BTreeMap::new());
    assert_eq!(
        state(&host, "reader"),
        PluginState::NeedsReview {
            missing: vec!["cmd:register".to_owned(), "fs:read".to_owned()]
        }
    );
    host.apply_settings(&grants(&[("reader", &["cmd:register", "fs:read"])]));
    wait_until(&host, "reader", active);

    let args = |path: &Path| serde_json::json!({ "path": path }).to_string();
    let inside = host.execute("reader", "reader.size", &args(&root.join("inside.txt")));
    let beyond = host.execute("reader", "reader.size", &args(&outside));
    // fs:read gives no right to write, not even inside its own root.
    let write_root = host.execute("reader", "reader.write", &args(&root.join("new.txt")));
    // Its own data folder is always writable.
    let write_data = host.execute("reader", "reader.write", r#"{"path":"data/note.txt"}"#);

    assert_eq!(inside.unwrap(), r#"{"size":5}"#);
    let beyond = beyond.unwrap_err();
    assert_eq!(beyond.code, ErrorCode::PluginError);
    assert!(beyond.message.starts_with("reader.size failed"), "{beyond}");
    let write_root = write_root.unwrap_err();
    assert!(
        write_root.message.starts_with("reader.write failed"),
        "{write_root}"
    );
    assert!(!root.join("new.txt").exists());
    assert_eq!(write_data.unwrap(), r#"{"written":true}"#);
    assert_eq!(
        std::fs::read_to_string(setup.data().join("reader").join("note.txt")).unwrap(),
        "written by Reader"
    );
    assert_eq!(
        state(&host, "reader"),
        PluginState::Active,
        "a refused file call is no crash"
    );
}

#[test]
fn a_crash_is_contained_and_the_plugin_comes_back() {
    let setup = Setup::new(&["crashy", "hello"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[
        ("crashy", &["cmd:register"]),
        ("hello", &["cmd:register", "events:emit"]),
    ]));
    wait_until(&host, "crashy", active);
    wait_until(&host, "hello", active);

    let error = host.execute("crashy", "crashy.crash", "{}").unwrap_err();
    assert_eq!(error.code, ErrorCode::PluginError);
    assert!(
        error.message.contains("crashy was asked to crash"),
        "{error}"
    );
    // The panic's place, from Rust's own panic message on stderr.
    assert!(
        error.message.contains("it said: panicked at crashy"),
        "{error}"
    );
    let crashes = setup.recorder.crashes("crashy");
    assert_eq!(crashes.len(), 1);
    assert!(crashes[0].contains("wasm trap"), "{}", crashes[0]);
    assert!(
        setup
            .recorder
            .command_sets
            .lock()
            .unwrap()
            .contains(&("crashy".to_owned(), Vec::new())),
        "its commands are unregistered"
    );
    assert!(
        setup
            .recorder
            .events()
            .iter()
            .any(|event| matches!(event, Event::PluginStateChanged { plugin_id, state: PluginState::Crashed { .. } } if plugin_id == "crashy"))
    );
    // Another plugin does not notice.
    assert_eq!(
        host.execute("hello", "hello.say", "{}").unwrap(),
        r#"{"message":"hello from Hello"}"#
    );
    // The first crash is followed by a fresh start.
    wait_until(&host, "crashy", active);
    assert_eq!(
        setup.recorder.commands("crashy"),
        Some(vec!["crashy.crash".to_owned()])
    );
}

#[test]
fn three_crashes_in_ten_minutes_keep_a_plugin_stopped_until_it_is_reloaded() {
    let setup = Setup::new(&["crashy"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("crashy", &["cmd:register"])]));
    for _ in 0..3 {
        wait_until(&host, "crashy", active);
        host.execute("crashy", "crashy.crash", "{}").unwrap_err();
    }
    std::thread::sleep(Duration::from_millis(500));
    assert!(
        crashed(&state(&host, "crashy")),
        "{:?}",
        state(&host, "crashy")
    );
    let why = host.explain_missing("crashy.crash").unwrap();
    assert!(why.starts_with("its plugin crashy is crashed"), "{why}");
    assert_eq!(host.explain_missing("nobody.nothing"), None);

    host.reload("crashy").unwrap();
    wait_until(&host, "crashy", active);
}

#[test]
fn a_command_that_never_returns_is_stopped_at_its_deadline() {
    let setup = Setup::new(&["spinner"]);
    let host = setup.host(|config| config.call_timeout = Duration::from_secs(1));
    host.load_all(&grants(&[("spinner", &["cmd:register"])]));
    wait_until(&host, "spinner", active);

    let started = Instant::now();
    let error = host.execute("spinner", "spinner.spin", "{}").unwrap_err();
    let took = started.elapsed();
    assert!(
        error.message.contains("did not finish within 1000 ms"),
        "{error}"
    );
    assert!(
        took >= Duration::from_secs(1) && took < Duration::from_secs(4),
        "{took:?}"
    );
    assert!(crashed(&state(&host, "spinner")));
}

#[test]
fn a_command_that_burns_its_fuel_is_stopped() {
    let setup = Setup::new(&["spinner"]);
    let host = setup.host(|config| config.fuel = 50_000_000);
    host.load_all(&grants(&[("spinner", &["cmd:register"])]));
    wait_until(&host, "spinner", active);
    let error = host.execute("spinner", "spinner.burn", "{}").unwrap_err();
    assert!(
        error.message.contains("used up its fuel of 50000000 units"),
        "{error}"
    );
}

#[test]
fn memory_beyond_the_limit_crashes_the_plugin_with_the_reason() {
    let setup = Setup::new(&["hog"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("hog", &["cmd:register"])]));
    wait_until(&host, "hog", active);
    let error = host.execute("hog", "hog.eat", "{}").unwrap_err();
    assert!(
        error.message.contains("MiB of memory; it may use 256 MiB"),
        "{error}"
    );
    assert!(crashed(&state(&host, "hog")));
}

#[test]
fn a_plugin_that_intercepts_jobs_can_stop_one() {
    let setup = Setup::new(&["vetoer", "hello"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[
        ("vetoer", &["jobs:intercept"]),
        ("hello", &["cmd:register", "events:emit"]),
    ]));
    wait_until(&host, "vetoer", active);
    wait_until(&host, "hello", active);

    let sources = [r"E:\cabinetos-scratch\a.txt".to_owned()];
    let denied = host
        .before_job(
            7,
            &JobKind::Copy,
            &sources,
            Some(r"E:\cabinetos-scratch\Forbidden"),
            1,
            10,
        )
        .unwrap_err();
    assert_eq!(
        denied,
        r"denied by plugin vetoer: E:\cabinetos-scratch\Forbidden is a forbidden destination"
    );
    host.before_job(
        8,
        &JobKind::Move,
        &sources,
        Some(r"E:\cabinetos-scratch\fine"),
        1,
        10,
    )
    .unwrap();
    host.before_job(
        9,
        &JobKind::Delete { permanent: true },
        &sources,
        None,
        1,
        10,
    )
    .unwrap();
}

/// A web server on a free port of 127.0.0.1, for the fetcher fixture. It
/// keeps every request it gets, head and body; it answers `/big` with 9 MiB,
/// `/slow` after 1.5 s, and anything else with a short text.
struct TestServer {
    port: u16,
    seen: Arc<Mutex<Vec<String>>>,
}

impl TestServer {
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let seen = Arc::new(Mutex::new(Vec::new()));
        let log = Arc::clone(&seen);
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let log = Arc::clone(&log);
                std::thread::spawn(move || serve(stream, &log));
            }
        });
        Self { port, seen }
    }

    fn url(&self, path: &str) -> String {
        format!("http://127.0.0.1:{}{path}", self.port)
    }

    fn seen(&self) -> Vec<String> {
        self.seen.lock().unwrap().clone()
    }
}

fn serve(mut stream: TcpStream, seen: &Mutex<Vec<String>>) {
    let mut head = Vec::new();
    let mut byte = [0_u8; 1];
    while !head.ends_with(b"\r\n\r\n") {
        if stream.read(&mut byte).unwrap_or(0) == 0 {
            return;
        }
        head.push(byte[0]);
    }
    let head = String::from_utf8_lossy(&head).into_owned();
    let length = head
        .lines()
        .filter_map(|line| line.split_once(':'))
        .find(|(name, _)| name.eq_ignore_ascii_case("content-length"))
        .and_then(|(_, value)| value.trim().parse().ok())
        .unwrap_or(0);
    let mut body = vec![0; length];
    if stream.read_exact(&mut body).is_err() {
        return;
    }
    seen.lock()
        .unwrap()
        .push(format!("{head}{}", String::from_utf8_lossy(&body)));
    let answer = match head.split_whitespace().nth(1).unwrap_or("/") {
        "/big" => vec![b'x'; 9 * 1024 * 1024],
        "/slow" => {
            std::thread::sleep(Duration::from_millis(1500));
            b"slow but here".to_vec()
        }
        _ => b"hello from the test server".to_vec(),
    };
    let _ = write!(
        stream,
        "HTTP/1.1 200 OK\r\ncontent-type: text/plain\r\nx-test: yes\r\ncontent-length: {}\r\nconnection: close\r\n\r\n",
        answer.len()
    );
    let _ = stream.write_all(&answer);
}

/// The fetcher fixture, allowed to reach only `server`.
fn fetcher(setup: &Setup, server: &TestServer, change: impl FnOnce(&mut HostConfig)) -> PluginHost {
    let host_rule = format!("127.0.0.1:{}", server.port);
    setup.edit_manifest("fetcher", |manifest| {
        manifest["capabilities"][1]["hosts"] = serde_json::json!([host_rule]);
    });
    let host = setup.host(change);
    host.load_all(&grants(&[("fetcher", &["cmd:register", "net"])]));
    wait_until(&host, "fetcher", active);
    host
}

fn fetch(host: &PluginHost, args: &serde_json::Value) -> Result<serde_json::Value, String> {
    host.execute("fetcher", "fetcher.get", &args.to_string())
        .map(|answer| serde_json::from_str(&answer).unwrap())
        .map_err(|error| error.message)
}

#[test]
fn net_puts_the_secret_into_the_header_and_never_into_the_plugin() {
    let server = TestServer::start();
    let setup = Setup::new(&["fetcher"]);
    let host = fetcher(&setup, &server, |_| {});

    let answer = fetch(
        &host,
        &serde_json::json!({
            "url": server.url("/echo"),
            "method": "POST",
            "body": "ping",
            "secret": "fetcher-test",
            "header": "x-api-key",
        }),
    )
    .unwrap();
    assert_eq!(answer["status"], 200);
    assert_eq!(answer["body"], "hello from the test server");
    assert_eq!(answer["headers"]["x-test"], "yes");
    assert!(!answer.to_string().contains(SECRET), "{answer}");
    let bearer = fetch(
        &host,
        &serde_json::json!({
            "url": server.url("/bearer"),
            "secret": "fetcher-test",
            "header": "Authorization",
        }),
    )
    .unwrap();
    assert!(!bearer.to_string().contains(SECRET), "{bearer}");

    let seen = server.seen();
    assert_eq!(seen.len(), 2, "{seen:?}");
    assert!(
        seen[0].starts_with("POST /echo HTTP/1.1\r\n"),
        "{}",
        seen[0]
    );
    assert!(
        seen[0].contains(&format!("x-api-key: {SECRET}\r\n")),
        "{}",
        seen[0]
    );
    assert!(seen[0].ends_with("\r\n\r\nping"), "{}", seen[0]);
    assert!(
        seen[1].contains(&format!("authorization: Bearer {SECRET}\r\n")),
        "{}",
        seen[1]
    );
}

#[test]
fn net_refuses_other_hosts_unnamed_secrets_and_huge_answers() {
    let server = TestServer::start();
    let other = TestServer::start();
    let setup = Setup::new(&["fetcher"]);
    let host = fetcher(&setup, &server, |_| {});

    let error = fetch(&host, &serde_json::json!({ "url": other.url("/") })).unwrap_err();
    assert!(error.contains("not among the hosts"), "{error}");
    let error = fetch(
        &host,
        &serde_json::json!({ "url": format!("https://example.com:{}/", server.port) }),
    )
    .unwrap_err();
    assert!(error.contains("not among the hosts"), "{error}");
    let error = fetch(
        &host,
        &serde_json::json!({
            "url": server.url("/"),
            "secret": "someone-else",
            "header": "x-api-key",
        }),
    )
    .unwrap_err();
    assert!(error.contains("not among the secrets"), "{error}");
    let error = fetch(&host, &serde_json::json!({ "url": server.url("/big") })).unwrap_err();
    assert!(error.contains("larger than 8 MiB"), "{error}");
    assert!(other.seen().is_empty());
    assert_eq!(server.seen().len(), 1, "only /big reached the server");
    assert!(active(&state(&host, "fetcher")), "a refusal is no crash");
}

#[test]
fn net_needs_its_grant_and_waiting_for_the_network_counts_against_no_deadline() {
    let server = TestServer::start();
    let setup = Setup::new(&["fetcher"]);
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("fetcher", &["cmd:register"])]));
    assert_eq!(
        state(&host, "fetcher"),
        PluginState::NeedsReview {
            missing: vec!["net".to_owned()]
        }
    );
    drop(host);

    let host = fetcher(&setup, &server, |config| {
        config.call_timeout = Duration::from_secs(1);
    });
    let answer = fetch(&host, &serde_json::json!({ "url": server.url("/slow") })).unwrap();
    assert_eq!(answer["body"], "slow but here");
    let error = fetch(
        &host,
        &serde_json::json!({ "url": server.url("/slow"), "timeout_ms": 200 }),
    )
    .unwrap_err();
    assert!(error.contains("failed"), "{error}");
    assert!(active(&state(&host, "fetcher")));
}

/// The watcher fixture, allowed to watch `root` only.
fn watcher(setup: &Setup, root: &Path) -> PluginHost {
    let root = root.display().to_string();
    setup.edit_manifest("watcher", |manifest| {
        manifest["capabilities"][2]["roots"] = serde_json::json!([root]);
    });
    let host = setup.host(|_| {});
    host.load_all(&grants(&[(
        "watcher",
        &["cmd:register", "events:emit", "fs:watch"],
    )]));
    wait_until(&host, "watcher", active);
    host
}

/// The `folder-changed` payloads the watcher passed on so far.
fn folder_changes(recorder: &Recorder) -> Vec<serde_json::Value> {
    recorder
        .events()
        .into_iter()
        .filter_map(|event| match event {
            Event::PluginEvent { name, payload, .. } if name == "folder-changed" => {
                Some(serde_json::from_str(&payload).unwrap())
            }
            _ => None,
        })
        .collect()
}

/// Every change in `messages`, as (kind, path, old path).
fn changes_in(messages: &[serde_json::Value]) -> Vec<(String, String, Option<String>)> {
    messages
        .iter()
        .flat_map(|message| message["changes"].as_array().unwrap().clone())
        .map(|change| {
            (
                change["kind"].as_str().unwrap().to_owned(),
                change["path"].as_str().unwrap().to_owned(),
                change["old_path"].as_str().map(str::to_owned),
            )
        })
        .collect()
}

#[test]
fn a_watched_folder_s_changes_arrive_gathered_and_only_under_the_roots() {
    let setup = Setup::new(&["watcher"]);
    let root = setup.temp.path().join("watched");
    std::fs::create_dir_all(root.join("sub")).unwrap();
    let host = watcher(&setup, &root);
    let watch = |path: &str| {
        host.execute(
            "watcher",
            "watcher.watch",
            &serde_json::json!({ "path": path }).to_string(),
        )
    };

    let outside = watch(&setup.temp.path().display().to_string()).unwrap_err();
    assert!(outside.message.contains("not under a folder"), "{outside}");
    let dots = watch(&format!(r"{}\sub\..\..", root.display())).unwrap_err();
    assert!(dots.message.contains("`..`"), "{dots}");
    let answer: serde_json::Value =
        serde_json::from_str(&watch(&root.display().to_string()).unwrap()).unwrap();
    assert_eq!(
        answer["roots"],
        serde_json::json!([root.display().to_string()])
    );
    // The sandbox form names the same folder: still one watch.
    let guest = format!("/{}", root.display().to_string().replace('\\', "/"));
    watch(&guest).unwrap();

    std::fs::write(root.join("a.txt"), "one").unwrap();
    std::fs::rename(root.join("a.txt"), root.join("b.txt")).unwrap();
    std::fs::remove_file(root.join("b.txt")).unwrap();
    // A burst: many changes, a few messages.
    let burst = Instant::now();
    for n in 0..40 {
        std::fs::write(root.join(format!("burst-{n}.txt")), "x").unwrap();
    }
    let burst = burst.elapsed();

    let at = |name: &str| root.join(name).display().to_string();
    let deadline = Instant::now() + Duration::from_secs(10);
    let messages = loop {
        let messages = folder_changes(&setup.recorder);
        let changes = changes_in(&messages);
        if changes
            .iter()
            .any(|(kind, path, _)| kind == "created" && *path == at("burst-39.txt"))
        {
            break messages;
        }
        assert!(
            Instant::now() < deadline,
            "the changes did not come: {changes:?}"
        );
        std::thread::sleep(Duration::from_millis(20));
    };
    let changes = changes_in(&messages);
    let first_three: Vec<_> = changes
        .iter()
        .filter(|(kind, _, _)| kind != "modified")
        .take(3)
        .cloned()
        .collect();
    assert_eq!(
        first_three,
        [
            ("created".to_owned(), at("a.txt"), None),
            ("renamed".to_owned(), at("b.txt"), Some(at("a.txt"))),
            ("removed".to_owned(), at("b.txt"), None),
        ]
    );
    assert!(
        messages
            .iter()
            .all(|message| message["path"] == root.display().to_string()
                && message["overflow"] == false),
        "{messages:?}"
    );
    // At most one message per 200 ms: the burst took `burst`, so it cannot
    // have more messages than 200 ms windows, plus the first one's.
    let allowed = usize::try_from(burst.as_millis() / 200).unwrap() + 2;
    assert!(
        messages.len() <= allowed,
        "{} messages for a burst of {burst:?}",
        messages.len()
    );

    host.execute(
        "watcher",
        "watcher.unwatch",
        &serde_json::json!({ "path": root.display().to_string() }).to_string(),
    )
    .unwrap();
    let before = folder_changes(&setup.recorder).len();
    std::fs::write(root.join("after.txt"), "x").unwrap();
    std::thread::sleep(Duration::from_millis(600));
    assert_eq!(folder_changes(&setup.recorder).len(), before, "unwatched");
}

#[test]
fn watches_end_with_the_plugin() {
    let setup = Setup::new(&["watcher"]);
    let root = setup.temp.path().join("watched");
    std::fs::create_dir_all(&root).unwrap();
    let host = watcher(&setup, &root);
    host.execute(
        "watcher",
        "watcher.watch",
        &serde_json::json!({ "path": root.display().to_string() }).to_string(),
    )
    .unwrap();
    let mut off = grants(&[("watcher", &["cmd:register", "events:emit", "fs:watch"])]);
    off.get_mut("watcher").unwrap().enabled = false;
    host.apply_settings(&off);
    assert_eq!(state(&host, "watcher"), PluginState::Disabled);
    std::thread::sleep(Duration::from_millis(300));
    std::fs::write(root.join("late.txt"), "x").unwrap();
    std::thread::sleep(Duration::from_millis(600));
    assert!(folder_changes(&setup.recorder).is_empty());
    // The folder was never locked by the watch.
    std::fs::remove_dir_all(&root).unwrap();
}

/// The requester fixture with every capability it asks for.
fn requester(setup: &Setup) -> PluginHost {
    let host = setup.host(|_| {});
    host.load_all(&grants(&[(
        "requester",
        &["cmd:register", "config:read", "events:emit", "core:request"],
    )]));
    wait_until(&host, "requester", active);
    host
}

/// `requester.ask` with `request`: the core's reply, or the error text.
fn ask_core(host: &PluginHost, request: &serde_json::Value) -> Result<serde_json::Value, String> {
    host.execute(
        "requester",
        "requester.ask",
        &serde_json::json!({ "request": request }).to_string(),
    )
    .map(|answer| serde_json::from_str::<serde_json::Value>(&answer).unwrap()["reply"].clone())
    .map_err(|error| error.message)
}

#[test]
fn core_request_passes_only_what_the_manifest_lists_and_the_core_sets_the_id() {
    let setup = Setup::new(&["requester"]);
    let host = requester(&setup);

    let reply = ask_core(
        &host,
        &serde_json::json!({ "type": "get_window_state", "id": "mine", "trace": "mine" }),
    )
    .unwrap();
    assert_eq!(reply["type"], "ok");
    let seen = setup.recorder.core_requests.lock().unwrap().clone();
    assert_eq!(
        seen,
        [(
            "requester".to_owned(),
            r#"{"type":"get_window_state"}"#.to_owned()
        )],
        "the plugin's own id and trace never reach the core"
    );

    // Not listed: refused by name, and the core never hears of it.
    let refused = ask_core(&host, &serde_json::json!({ "type": "list_jobs" })).unwrap_err();
    assert!(refused.contains("`list_jobs`"), "{refused}");
    // Listed in the manifest on purpose, and still never allowed.
    for kind in ["hello", "secret_get"] {
        let refused = ask_core(&host, &serde_json::json!({ "type": kind })).unwrap_err();
        assert!(refused.contains(&format!("`{kind}`")), "{refused}");
        assert!(refused.contains("never allowed"), "{refused}");
    }
    let refused = ask_core(&host, &serde_json::json!("not a request")).unwrap_err();
    assert!(
        refused.contains("not JSON") || refused.contains("object"),
        "{refused}"
    );
    assert_eq!(setup.recorder.core_requests.lock().unwrap().len(), 1);

    // An error of the core comes back as an error, and the plugin lives.
    let error = ask_core(
        &host,
        &serde_json::json!({ "type": "search", "query": "no_window" }),
    )
    .unwrap_err();
    assert!(error.contains("the core says no"), "{error}");
    assert!(active(&state(&host, "requester")));
}

#[test]
fn a_manifest_names_known_request_types_and_core_request_needs_them() {
    let setup = Setup::new(&["requester"]);
    setup.edit_manifest("requester", |manifest| {
        manifest["capabilities"][3]["requests"] = serde_json::json!(["get_window_state", "nope"]);
    });
    let host = setup.host(|_| {});
    host.load_all(&grants(&[(
        "requester",
        &["cmd:register", "config:read", "events:emit", "core:request"],
    )]));
    let PluginState::Failed { message } = state(&host, "requester") else {
        panic!("{:?}", state(&host, "requester"))
    };
    assert!(
        message.contains("`nope` is not a request type"),
        "{message}"
    );
    drop(host);

    setup.edit_manifest("requester", |manifest| {
        manifest["capabilities"][3]["requests"] = serde_json::json!([]);
    });
    let host = setup.host(|_| {});
    host.load_all(&grants(&[("requester", &["core:request"])]));
    let PluginState::Failed { message } = state(&host, "requester") else {
        panic!("{:?}", state(&host, "requester"))
    };
    assert!(message.contains("needs `requests`"), "{message}");
    drop(host);

    // The grant is what lets it start at all.
    setup.edit_manifest("requester", |manifest| {
        manifest["capabilities"][3]["requests"] = serde_json::json!(["search"]);
    });
    let host = setup.host(|_| {});
    host.load_all(&grants(&[(
        "requester",
        &["cmd:register", "config:read", "events:emit"],
    )]));
    assert_eq!(
        state(&host, "requester"),
        PluginState::NeedsReview {
            missing: vec!["core:request".to_owned()]
        }
    );
    let listed = host.list();
    let capability = &listed[0].capabilities[3];
    assert_eq!(capability.name, "core:request");
    assert_eq!(
        capability.requests,
        ["search"],
        "the review dialog shows them"
    );
}

/// The settings a plugin reads: its own, and nothing else of the
/// `plugins` section.
#[test]
fn a_plugin_reads_its_own_settings_and_not_those_of_others() {
    let setup = Setup::new(&["requester"]);
    let host = requester(&setup);
    let read = |path: &str| -> serde_json::Value {
        let answer = host
            .execute(
                "requester",
                "requester.setting",
                &serde_json::json!({ "path": path }).to_string(),
            )
            .unwrap();
        serde_json::from_str::<serde_json::Value>(&answer).unwrap()["value"].clone()
    };
    assert_eq!(read("plugins.requester.settings.provider"), "fake");
    assert_eq!(read("appearance.theme"), "dark");
    for hidden in [
        "plugins.other.settings.provider",
        "plugins.requester.granted",
        "plugins",
    ] {
        assert_eq!(read(hidden), serde_json::Value::Null, "{hidden}");
    }
}

#[test]
fn a_change_of_a_plugin_s_settings_is_told_to_it_and_does_not_restart_it() {
    let setup = Setup::new(&["requester"]);
    let host = requester(&setup);
    let commands_before = setup.recorder.command_sets.lock().unwrap().len();

    let mut changed = grants(&[(
        "requester",
        &["cmd:register", "config:read", "events:emit", "core:request"],
    )]);
    changed
        .get_mut("requester")
        .unwrap()
        .settings
        .insert("provider".to_owned(), serde_json::json!("fake"));
    host.apply_settings(&changed);

    let deadline = Instant::now() + Duration::from_secs(10);
    while !setup.recorder.events().iter().any(|event| {
        matches!(event, Event::PluginEvent { plugin_id, name, .. }
            if plugin_id == "requester" && name == "settings-changed")
    }) {
        assert!(Instant::now() < deadline, "the plugin was not told");
        std::thread::sleep(Duration::from_millis(20));
    }
    assert!(active(&state(&host, "requester")));
    assert_eq!(
        setup.recorder.command_sets.lock().unwrap().len(),
        commands_before,
        "no restart: its commands were not registered again"
    );

    // A grant that changes still restarts it.
    let mut fewer = changed.clone();
    fewer.get_mut("requester").unwrap().granted.pop();
    host.apply_settings(&fewer);
    assert_eq!(
        state(&host, "requester"),
        PluginState::NeedsReview {
            missing: vec!["core:request".to_owned()]
        }
    );
}

// ----- The Agent extension's plugin -----

/// The Agent's plugin as WebAssembly, with the fake provider that gives
/// `replies` one after the other, and `files` as the one folder it may
/// read. Its data folder gets the replies before it starts.
fn agent(setup: &Setup, files: &Path, replies: &[String], tier: u8) -> PluginHost {
    let root = files.display().to_string();
    setup.edit_manifest("agent", |manifest| {
        for capability in manifest["capabilities"].as_array_mut().unwrap() {
            if capability["name"] == "fs:read" {
                capability["roots"] = serde_json::json!([root]);
            }
        }
    });
    let data = setup.data().join("agent");
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(
        data.join("fake-replies.json"),
        serde_json::to_string(replies).unwrap(),
    )
    .unwrap();
    *setup.recorder.agent_settings.lock().unwrap() =
        Some(serde_json::json!({ "provider": "fake", "tier": tier }).to_string());
    let host = setup.host(|_| {});
    host.load_all(&grants(&[(
        "agent",
        &[
            "cmd:register",
            "config:read",
            "events:emit",
            "fs:read",
            "net",
            "core:request",
        ],
    )]));
    wait_until(&host, "agent", active);
    host
}

/// An Agent command's answer, or the error text.
fn agent_command(
    host: &PluginHost,
    command: &str,
    args: &serde_json::Value,
) -> Result<serde_json::Value, String> {
    host.execute("agent", command, &args.to_string())
        .map(|answer| serde_json::from_str(&answer).unwrap())
        .map_err(|error| error.message)
}

/// The requests the agent sent the core, in order, as JSON.
fn agent_requests(recorder: &Recorder) -> Vec<serde_json::Value> {
    recorder
        .core_requests
        .lock()
        .unwrap()
        .iter()
        .filter(|(plugin, _)| plugin == "agent")
        .map(|(_, request)| serde_json::from_str(request).unwrap())
        .collect()
}

/// The agent's own folder: the lines of its files, by name.
fn agent_lines(setup: &Setup, prefix: &str) -> Vec<String> {
    let data = setup.data().join("agent");
    let mut names: Vec<_> = std::fs::read_dir(&data)
        .unwrap()
        .flatten()
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with(prefix))
        .collect();
    names.sort();
    names
        .iter()
        .flat_map(|name| {
            std::fs::read_to_string(data.join(name))
                .unwrap()
                .lines()
                .map(str::to_owned)
                .collect::<Vec<_>>()
        })
        .collect()
}

/// A folder with two files in it for the agent to look at.
fn agent_files(setup: &Setup) -> PathBuf {
    let files = setup.temp.path().join("files");
    std::fs::create_dir_all(&files).unwrap();
    std::fs::write(files.join("old.txt"), "x").unwrap();
    std::fs::write(files.join("notes.md"), "y").unwrap();
    files
}

#[test]
fn the_agent_looks_by_fs_read_proposes_a_preview_and_undoes_it_once_the_window_applied_it() {
    let setup = Setup::new(&["agent"]);
    let files = agent_files(&setup);
    let root = files.display().to_string();
    let replies = [
        format!("First I look.\n```\nls \"{root}\"\n```"),
        format!("Renaming it.\n```\nrename \"{root}\\old.txt\" new.txt\n```"),
    ];
    let host = agent(&setup, &files, &replies, 2);

    let answer = agent_command(
        &host,
        "agent.ask",
        &serde_json::json!({ "input": "rename old.txt to new.txt" }),
    )
    .unwrap();
    assert_eq!(answer["preview"], "preview-1");
    assert_eq!(answer["text"], "Renaming it.");
    assert_eq!(answer["tier"], 2);
    let commands = answer["commands"].as_array().unwrap();
    assert_eq!(
        (commands[0]["kind"].as_str(), commands[0]["status"].as_str()),
        (Some("read"), Some("ok"))
    );
    assert_eq!(
        (commands[1]["kind"].as_str(), commands[1]["status"].as_str()),
        (Some("write"), Some("proposed"))
    );

    // The change is a preview of one row, and nothing else went to the core.
    let requests = agent_requests(&setup.recorder);
    let listing: Vec<_> = requests
        .iter()
        .filter(|request| request["type"] == "preview_listing")
        .collect();
    assert_eq!(listing.len(), 1, "{requests:?}");
    assert_eq!(
        listing[0]["rows"],
        serde_json::json!([{
            "path": format!("{root}\\old.txt"),
            "kind": "rename",
            "to": "new.txt",
        }])
    );
    assert!(
        requests
            .iter()
            .all(|request| request["type"] != "preview_apply"),
        "tier 2 leaves applying to the user"
    );
    // It changed nothing itself: it has no right to write there.
    assert!(files.join("old.txt").exists() && !files.join("new.txt").exists());

    // The listing it gave the model was read by the plugin from the disk.
    let asked = agent_lines(&setup, "fake-requests");
    assert_eq!(asked.len(), 2);
    assert!(
        asked[1].contains("old.txt") && asked[1].contains("notes.md"),
        "{}",
        asked[1]
    );
    // The audit log has the interaction, with what became of it.
    let audit = agent_lines(&setup, "audit.");
    assert_eq!(audit.len(), 1);
    let entry: serde_json::Value = serde_json::from_str(&audit[0]).unwrap();
    assert_eq!(
        (
            entry["source"].as_str(),
            entry["provider"].as_str(),
            entry["preview"].as_str()
        ),
        (Some("ask"), Some("fake"), Some("preview-1"))
    );
    assert!(
        setup.recorder.events().iter().any(|event| matches!(event,
            Event::PluginEvent { plugin_id, name, .. } if plugin_id == "agent" && name == "agent.reply"))
    );

    // The user applies the preview: the core tells the agent which jobs
    // started, and `agent.undo` reverses them.
    host.send_event(
        "agent",
        "preview-applied",
        serde_json::json!({ "preview": "preview-1", "jobs": [11] }).to_string(),
    );
    let undone = agent_command(&host, "agent.undo", &serde_json::json!({})).unwrap();
    assert_eq!(undone["undoes"], serde_json::json!([11]));
    let undo: Vec<_> = agent_requests(&setup.recorder)
        .into_iter()
        .filter(|request| request["type"] == "undo_job")
        .collect();
    assert_eq!(undo, [serde_json::json!({ "type": "undo_job", "job": 11 })]);
    assert!(active(&state(&host, "agent")));
}

#[test]
fn at_tier_three_the_agent_applies_at_once_and_undoes_the_last_job_first() {
    let setup = Setup::new(&["agent"]);
    let files = agent_files(&setup);
    let root = files.display().to_string();
    let replies = [format!(
        "Done.\n```\nrename \"{root}\\old.txt\" new.txt\n```"
    )];
    let host = agent(&setup, &files, &replies, 3);

    let answer = agent_command(
        &host,
        "agent.chat",
        &serde_json::json!({ "message": "rename old.txt to new.txt" }),
    )
    .unwrap();
    assert_eq!(answer["commands"][0]["status"], "applied");
    let types: Vec<_> = agent_requests(&setup.recorder)
        .iter()
        .map(|request| request["type"].as_str().unwrap().to_owned())
        .collect();
    let opened = types.iter().position(|kind| kind == "preview_listing");
    let applied = types.iter().position(|kind| kind == "preview_apply");
    assert!(
        opened.is_some() && applied > opened,
        "a preview, then its apply: {types:?}"
    );
    // A chat names its preview in an event, and a status-bar notice says what was done.
    let events = setup.recorder.events();
    let named = |wanted: &str| {
        events.iter().find_map(|event| match event {
            Event::PluginEvent {
                plugin_id,
                name,
                payload,
            } if plugin_id == "agent" && name == wanted => {
                Some(serde_json::from_str::<serde_json::Value>(payload).unwrap())
            }
            _ => None,
        })
    };
    assert_eq!(
        named("agent.preview").unwrap(),
        serde_json::json!({ "preview": "preview-1" })
    );
    assert!(named("agent.notice").unwrap()["notice"].is_string());

    agent_command(&host, "agent.undo", &serde_json::json!({})).unwrap();
    let undone: Vec<_> = agent_requests(&setup.recorder)
        .iter()
        .filter(|request| request["type"] == "undo_job")
        .map(|request| request["job"].as_u64().unwrap())
        .collect();
    assert_eq!(
        undone,
        [12, 11],
        "the job that started last is undone first"
    );
}
