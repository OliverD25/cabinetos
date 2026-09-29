//! The CLI's Phase 14 commands against a real core: `state` prints what a
//! window told the core; `secret` stores, reads, lists and removes secrets;
//! `undo` reverses a job.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both. A test that plays the window talks to the
//! core through `cabinetos-ipc`, as the window does.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::{PipeClient, PipeName};
use cabinetos_protocol::{Pane, PaneState, Request, Response, WindowPanes, WindowState, WindowTab};
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

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
    #[allow(dead_code)]
    fn files(&self) -> PathBuf {
        let files = self.dir.path().join("files");
        std::fs::create_dir_all(&files).unwrap();
        files
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

fn start_core() -> Core {
    start_core_with(&[])
}

/// A core with extra environment variables.
fn start_core_with(env: &[(&str, &str)]) -> Core {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("cli14")
        .tempdir_in(root)
        .unwrap();
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("config").join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
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
        .envs(env.iter().copied())
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

fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .unwrap()
}

#[test]
fn state_prints_what_the_window_said() {
    let core = start_core();
    let output = cli(&core, &["state"]);
    assert!(!output.status.success());
    assert!(stderr(&output).contains("no_window"), "{}", stderr(&output));

    let runtime = runtime();
    let state = WindowState {
        active_pane: Pane::Left,
        panes: WindowPanes {
            left: PaneState {
                tabs: vec![
                    WindowTab {
                        path: r"C:\Users\me".to_owned(),
                        locked: false,
                        tool: None,
                    },
                    WindowTab {
                        path: r"E:\Звіт 2026".to_owned(),
                        locked: true,
                        tool: None,
                    },
                ],
                active: 1,
                cursor: Some(r"E:\Звіт 2026\a.txt".to_owned()),
                marked: vec![r"E:\Звіт 2026\a.txt".to_owned()],
            },
            right: PaneState::default(),
        },
    };
    // The window stays connected while the CLI asks: its state goes with it.
    let window = runtime.block_on(async {
        let mut window = PipeClient::connect(&core.pipe, Duration::from_secs(5))
            .await
            .unwrap();
        window.hello("CabinetOS").await.unwrap();
        let reply = window
            .request(Request::WindowState(state.clone()))
            .await
            .unwrap();
        assert_eq!(reply.body, Response::Ok);
        window
    });

    let output = cli(&core, &["state"]);
    assert!(output.status.success(), "{}", stderr(&output));
    let text = stdout(&output);
    assert!(text.starts_with("window CabinetOS#"), "{text}");
    for wanted in [
        "left pane (has the keyboard)",
        "\n   1   C:\\Users\\me\n",
        "\n  *2   E:\\Звіт 2026  [locked]\n",
        r"  cursor: E:\Звіт 2026\a.txt",
        "  marked: 1",
        "right pane\n  no tabs\n  cursor: none\n  marked: 0",
    ] {
        assert!(text.contains(wanted), "{wanted:?} in\n{text}");
    }

    let output = cli(&core, &["state", "--json"]);
    assert!(output.status.success(), "{}", stderr(&output));
    let printed: WindowState = serde_json::from_str(&stdout(&output)).unwrap();
    assert_eq!(printed, state);

    let output = cli(&core, &["state", "--client", "nobody#9"]);
    assert!(!output.status.success());
    assert!(stderr(&output).contains("nobody#9"), "{}", stderr(&output));
    drop(window);
}

#[test]
fn secret_stores_reads_lists_and_removes() {
    let prefix = format!("CabinetOS-test-cli-{}/", std::process::id());
    let core = start_core_with(&[("CABINETOS_SECRETS_PREFIX", prefix.as_str())]);
    // From standard input, as the docs advise: the value stays out of the
    // shell's history.
    let mut child = Command::new(CLI_EXE)
        .args(["--pipe", core.pipe.token(), "secret", "set", "cli.test"])
        .env_remove("CABINETOS_LOG")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    {
        use std::io::Write;
        let mut stdin = child.stdin.take().unwrap();
        stdin.write_all(b"sk-from-stdin\r\n").unwrap();
    }
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "stored cli.test\n");

    let output = cli(&core, &["secret", "set", "cli.other", "--value", "v2"]);
    assert!(output.status.success(), "{}", stderr(&output));
    let output = cli(&core, &["secret", "get", "cli.test"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert_eq!(stdout(&output), "sk-from-stdin\n");
    let output = cli(&core, &["secret", "list"]);
    assert_eq!(stdout(&output), "cli.other\ncli.test\n");
    for name in ["cli.test", "cli.other"] {
        let output = cli(&core, &["secret", "delete", name]);
        assert!(output.status.success(), "{}", stderr(&output));
    }
    let output = cli(&core, &["secret", "get", "cli.test"]);
    assert!(!output.status.success());
    assert!(
        stderr(&output).contains("no_such_secret"),
        "{}",
        stderr(&output)
    );
    assert_eq!(stdout(&cli(&core, &["secret", "list"])), "no secrets\n");
}

#[test]
fn undo_reverses_the_last_job_or_a_named_one_and_refuses_a_delete() {
    let core = start_core();
    let files = core.files();
    let from = files.join("from");
    let to = files.join("to");
    std::fs::create_dir_all(&from).unwrap();
    std::fs::create_dir_all(&to).unwrap();
    std::fs::write(from.join("a.txt"), "a").unwrap();
    std::fs::write(from.join("b.txt"), "b").unwrap();
    let path = |path: &Path| path.display().to_string();

    let copied = cli(&core, &["copy", &path(&from.join("a.txt")), &path(&to)]);
    assert!(copied.status.success(), "{}", stderr(&copied));
    let moved = cli(&core, &["move", &path(&from.join("b.txt")), &path(&to)]);
    assert!(moved.status.success(), "{}", stderr(&moved));
    let copy_job: u64 = stdout(&copied)
        .split_whitespace()
        .nth(1)
        .and_then(|id| id.parse().ok())
        .unwrap_or_else(|| panic!("no job id in {}", stdout(&copied)));

    let output = cli(&core, &["undo", "--last"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert!(
        stdout(&output).contains("undoes job"),
        "{}",
        stdout(&output)
    );
    assert!(
        stdout(&output).contains(": completed"),
        "{}",
        stdout(&output)
    );
    assert!(from.join("b.txt").exists(), "the move went back");

    let output = cli(&core, &["undo", &copy_job.to_string()]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert!(
        stdout(&output).contains(&format!("undoes job {copy_job}")),
        "{}",
        stdout(&output)
    );
    assert!(
        !to.join("a.txt").exists(),
        "the copy went to the Recycle Bin"
    );

    let deleted = cli(&core, &["delete", &path(&from.join("a.txt"))]);
    assert!(deleted.status.success(), "{}", stderr(&deleted));
    let output = cli(&core, &["undo", "--last"]);
    assert!(!output.status.success());
    assert!(
        stderr(&output).contains("not_undoable"),
        "{}",
        stderr(&output)
    );
    assert!(
        stderr(&output).contains("Recycle Bin"),
        "{}",
        stderr(&output)
    );
    let output = cli(&core, &["undo"]);
    assert!(!output.status.success(), "a job or --last is needed");
}
