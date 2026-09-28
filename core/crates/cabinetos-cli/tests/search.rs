//! `cabinetos-cli search` and `index status` against a real core with no
//! indexer: the phase's promise that the app works unchanged without it.
//! Everything written lives under `%TEMP%\cabinetos-index-test\`.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-index-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

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
        "{} is missing; build cabinetos-core first",
        path.display()
    );
    path
}

/// A core whose indexer pipe leads nowhere.
fn start_core() -> Core {
    let dir = scratch("cli-core");
    let pipe = PipeName::random();
    let child = Command::new(core_exe())
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.path().join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env(
            "CABINETOS_INDEXER_PIPE",
            format!(
                r"\\.\pipe\cabinetos-indexer-none-{}",
                PipeName::random().token()
            ),
        )
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
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
        assert!(Instant::now() < deadline, "the core did not answer");
        std::thread::sleep(Duration::from_millis(50));
    }
    core
}

fn cli(core: &Core, args: &[&str]) -> Output {
    Command::new(CLI_EXE)
        .args(["--pipe", core.pipe.token()])
        .args(args)
        .env_remove("CABINETOS_LOG")
        .output()
        .unwrap()
}

fn stdout(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

#[test]
fn search_and_index_status_work_without_an_indexer() {
    let core = start_core();
    let tree = scratch("cli-tree");
    std::fs::create_dir(tree.path().join("sub")).unwrap();
    std::fs::write(tree.path().join("foo.txt"), "x").unwrap();
    std::fs::write(tree.path().join("sub").join("seafood.md"), "x").unwrap();
    std::fs::write(tree.path().join("sub").join("bar.txt"), "x").unwrap();
    let root = tree.path().display().to_string();

    let output = cli(&core, &["search", "foo", "--root", &root]);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let text = stdout(&output);
    let lines: Vec<&str> = text.lines().collect();
    assert_eq!(lines[0], format!(r"f {root}\foo.txt"), "{text}");
    assert_eq!(lines[1], format!(r"f {root}\sub\seafood.md"), "{text}");
    assert!(
        lines[2].starts_with("2 hit(s); source: walk;") && lines[2].ends_with("; complete"),
        "{text}"
    );

    let output = cli(&core, &["index", "status"]);
    assert!(output.status.success());
    assert_eq!(
        stdout(&output),
        "available: no (no indexer answers; search walks folders instead)\n"
    );
}
