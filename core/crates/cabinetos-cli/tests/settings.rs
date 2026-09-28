//! The CLI against a real core: `keys set` changes the configuration file and
//! the running core answers with the new keymap; the Immutable System Tier
//! refuses; `commands search` ranks; `config validate` needs no core.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`. `cargo test
//! --workspace` builds both. After `cargo test -p cabinetos-cli` alone, build
//! the core first with `cargo build -p cabinetos-core`.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

/// A running core with its own pipe and configuration file, killed at the
/// end of the test.
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

/// Starts a core and waits until it answers `ping` through the CLI.
fn start_core() -> Core {
    let dir = tempfile::tempdir().unwrap();
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
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
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_LOG_STDERR")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let core = Core { child, pipe, dir };
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

/// Runs the CLI against `core`.
fn cli(core: &Core, args: &[&str]) -> Output {
    Command::new(CLI_EXE)
        .args(["--pipe", core.pipe.token()])
        .args(args)
        .env_remove("CABINETOS_LOG")
        .env_remove("CABINETOS_CONFIG")
        .output()
        .unwrap()
}

fn stdout(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

/// The `keys list` lines for `command`.
fn listed_keys(core: &Core, command: &str) -> Vec<String> {
    let output = cli(core, &["keys", "list"]);
    assert!(output.status.success(), "{}", stderr(&output));
    stdout(&output)
        .lines()
        .filter(|line| line.split_whitespace().any(|word| word == command))
        .map(|line| line.split(command).next().unwrap().trim().to_owned())
        .collect()
}

#[test]
fn keys_set_changes_the_file_and_the_running_core() {
    let core = start_core();
    assert_eq!(listed_keys(&core, "view.toggleSidebar"), ["ctrl+b"]);

    let output = cli(&core, &["keys", "set", "view.toggleSidebar", "Ctrl+Alt+B"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "view.toggleSidebar: ctrl+alt+b\n");
    let file = fs::read_to_string(core.config_path()).unwrap();
    assert!(file.contains(r#""keys": "ctrl+alt+b""#), "{file}");
    assert_eq!(listed_keys(&core, "view.toggleSidebar"), ["ctrl+alt+b"]);

    let output = cli(&core, &["keys", "reset", "view.toggleSidebar"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "view.toggleSidebar: ctrl+b\n");
    assert_eq!(listed_keys(&core, "view.toggleSidebar"), ["ctrl+b"]);
}

#[test]
fn keys_set_refuses_the_immutable_tier() {
    let core = start_core();
    let output = cli(&core, &["keys", "set", "palette.show", "ctrl+p"]);
    assert!(!output.status.success());
    let message = stderr(&output);
    assert!(message.contains("Immutable System Tier"), "{message}");
    assert!(message.contains("immutable_binding"), "{message}");
    assert_eq!(listed_keys(&core, "palette.show"), ["ctrl+shift+p"]);
}

#[test]
fn commands_search_ranks_the_best_match_first() {
    let core = start_core();
    let output = cli(&core, &["commands", "search", "dual"]);
    assert!(output.status.success(), "{}", stderr(&output));
    let text = stdout(&output);
    let first = text.lines().next().unwrap_or_default();
    assert!(first.contains("view.toggleDualPane"), "{text}");
}

#[test]
fn config_validate_checks_a_file_without_a_core() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cabinetos.json");
    let validate = || {
        Command::new(CLI_EXE)
            .args(["config", "validate"])
            .arg(&path)
            .output()
            .unwrap()
    };

    fs::write(&path, "{\n  \"ui\": {\n    \"dualPan\": false\n  }\n}").unwrap();
    let output = validate();
    assert!(!output.status.success());
    let message = stderr(&output);
    assert!(message.contains("line 3"), "{message}");
    assert!(message.contains("dualPan"), "{message}");

    fs::write(
        &path,
        r#"{ "keybindings": [ { "command": "go.toPath", "keys": "ctrl+b" } ] }"#,
    )
    .unwrap();
    let output = validate();
    assert!(!output.status.success());
    let message = stderr(&output);
    assert!(
        message.contains("ctrl+b is bound to both view.toggleSidebar and go.toPath"),
        "{message}"
    );

    fs::write(&path, r#"{ "ui": { "layout": "rail" } }"#).unwrap();
    let output = validate();
    assert!(output.status.success(), "{}", stderr(&output));
    assert!(stdout(&output).trim_end().ends_with(": ok"));
}
