//! The CLI's plugin commands against a real core with copies of the
//! committed fixture plugins: `plugins list|grant|reload|disable`,
//! `commands search|exec` and `events watch`.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`. `cargo test
//! --workspace` builds both.

use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
/// Long enough to compile a component in a debug build on a slow runner.
const SETTLE_DEADLINE: Duration = Duration::from_secs(60);

/// A running core, killed at the end of the test.
struct Core {
    child: Child,
    pipe: PipeName,
    _dir: TempDir,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn core_exe() -> PathBuf {
    let path = Path::new(CLI_EXE).with_file_name("cabinetos-core.exe");
    assert!(
        path.exists(),
        "{} is missing; build it with `cargo build -p cabinetos-core` (`cargo test --workspace` does)",
        path.display()
    );
    path
}

/// Starts a core with the `hello` and `crashy` fixtures; only `crashy` is
/// granted what it asks for.
fn start_core() -> Core {
    let dir = tempfile::tempdir().unwrap();
    let fixtures = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/fixtures/plugins");
    for id in ["hello", "crashy"] {
        let to = dir.path().join("plugins").join(id);
        fs::create_dir_all(&to).unwrap();
        for file in ["plugin.json", "plugin.wasm"] {
            fs::copy(fixtures.join(id).join(file), to.join(file)).unwrap();
        }
    }
    let config = dir.path().join("cabinetos.json");
    fs::write(
        &config,
        r#"{ "plugins": { "crashy": { "granted": ["cmd:register"] } } }"#,
    )
    .unwrap();
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config)
        .arg("--plugins-dir")
        .arg(dir.path().join("plugins"))
        .arg("--plugins-data-dir")
        .arg(dir.path().join("plugins-data"))
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let core = Core {
        child,
        pipe,
        _dir: dir,
    };
    let deadline = Instant::now() + STARTUP_DEADLINE;
    while !cli(&core, &["ping"]).status.success() {
        assert!(
            Instant::now() < deadline,
            "the core did not answer within {STARTUP_DEADLINE:?}"
        );
        std::thread::sleep(Duration::from_millis(50));
    }
    core
}

fn cli_command(core: &Core, args: &[&str]) -> Command {
    let mut command = Command::new(CLI_EXE);
    command
        .args(["--pipe", core.pipe.token()])
        .args(args)
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_CONFIG");
    command
}

fn cli(core: &Core, args: &[&str]) -> Output {
    cli_command(core, args).output().unwrap()
}

fn stdout(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

/// Runs `plugins list` until a line passes `check`.
fn wait_listed(core: &Core, check: impl Fn(&str) -> bool) -> String {
    let deadline = Instant::now() + SETTLE_DEADLINE;
    loop {
        let output = cli(core, &["plugins", "list"]);
        assert!(output.status.success(), "{}", stderr(&output));
        let text = stdout(&output);
        if text.lines().any(&check) {
            return text;
        }
        assert!(Instant::now() < deadline, "plugins list stayed:\n{text}");
        std::thread::sleep(Duration::from_millis(100));
    }
}

#[test]
fn plugins_commands_and_events_through_the_cli() {
    let core = start_core();
    let listed = wait_listed(&core, |line| line == "crashy 0.1.0 (Crashy): active");
    assert!(
        listed.contains("hello 0.1.0 (Hello): needs_review (grant cmd:register events:emit)"),
        "{listed}"
    );
    assert!(
        listed.contains(
            "  events:emit [low, NOT granted]: Tells the window each time it said hello."
        ),
        "{listed}"
    );

    let output = cli(
        &core,
        &["plugins", "grant", "hello", "cmd:register", "events:emit"],
    );
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "hello: active\n");

    let output = cli(&core, &["commands", "search", "say hello"]);
    assert!(output.status.success(), "{}", stderr(&output));
    let first = stdout(&output)
        .lines()
        .next()
        .unwrap_or_default()
        .to_owned();
    assert!(
        first.contains("hello.say") && first.ends_with("[Hello]"),
        "{first}"
    );

    let output = cli(&core, &["commands", "exec", "hello.say"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(
        stdout(&output),
        "{\n  \"message\": \"hello from Hello\"\n}\n"
    );

    // `events watch` sees the crash.
    let mut watch = cli_command(&core, &["events", "watch"])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let (lines_tx, lines) = mpsc::channel();
    let watched = BufReader::new(watch.stdout.take().unwrap());
    std::thread::spawn(move || {
        for line in watched.lines() {
            let Ok(line) = line else { break };
            if lines_tx.send(line).is_err() {
                break;
            }
        }
    });
    let next_line = |check: &dyn Fn(&str) -> bool| {
        let deadline = Instant::now() + SETTLE_DEADLINE;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            let line = lines
                .recv_timeout(left)
                .expect("events watch printed nothing in time");
            if check(&line) {
                return line;
            }
        }
    };
    next_line(&|line| line.starts_with("watching events"));

    let output = cli(&core, &["commands", "exec", "crashy.crash"]);
    assert!(!output.status.success());
    let message = stderr(&output);
    assert!(message.contains("crashy was asked to crash"), "{message}");
    assert!(message.contains("(plugin_error)"), "{message}");
    let crashed = next_line(&|line| line.contains(r#""type":"plugin_crashed""#));
    assert!(crashed.contains(r#""plugin_id":"crashy""#), "{crashed}");
    let _ = watch.kill();
    let _ = watch.wait();

    let output = cli(&core, &["plugins", "reload", "crashy"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "crashy: active\n");

    let output = cli(&core, &["plugins", "disable", "hello"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "hello: disabled\n");
    let output = cli(&core, &["commands", "exec", "hello.say"]);
    assert!(!output.status.success());
    assert!(
        stderr(&output).contains("its plugin hello is turned off"),
        "{}",
        stderr(&output)
    );

    let output = cli(&core, &["plugins", "grant", "hello", "net"]);
    assert!(!output.status.success());
    assert!(
        stderr(&output).contains("never granted"),
        "{}",
        stderr(&output)
    );
}
