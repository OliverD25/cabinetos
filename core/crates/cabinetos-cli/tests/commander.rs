//! `cabinetos-cli` against a real core for Total Commander's small requests
//! (sub-phase 11a, protocol version 12). Everything written lives under
//! `%TEMP%\cabinetos-core-test\`. No test leaves a window open: nothing
//! here starts Notepad or another editor, and a property sheet goes with
//! the core that shows it.
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
    let root = std::env::temp_dir().join("cabinetos-core-test");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

fn shown(path: &Path) -> String {
    path.display().to_string()
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

/// A core with its own configuration file, logs and extension folders.
fn start_core() -> Core {
    let dir = scratch("commander-core");
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
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
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

/// The output of a command that succeeded.
fn stdout(output: &Output) -> String {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout.clone()).expect("the output is UTF-8")
}

/// The error of a command that failed.
fn stderr(output: &Output) -> String {
    assert!(
        !output.status.success(),
        "it worked: {}",
        String::from_utf8_lossy(&output.stdout)
    );
    String::from_utf8_lossy(&output.stderr).into_owned()
}

#[test]
fn mkfile_creates_an_empty_file_and_never_replaces_one() {
    let core = start_core();
    let dir = scratch("mkfile");
    let new = dir.path().join("New Text Document.txt");
    assert_eq!(
        stdout(&cli(&core, &["mkfile", &shown(&new)])),
        format!("created {}\n", shown(&new))
    );
    assert_eq!(std::fs::metadata(&new).unwrap().len(), 0);

    std::fs::write(&new, "typed").unwrap();
    let taken = stderr(&cli(&core, &["mkfile", &shown(&new)]));
    assert!(taken.contains("already_exists"), "{taken}");
    assert_eq!(std::fs::read_to_string(&new).unwrap(), "typed");
    std::fs::create_dir(dir.path().join("folder")).unwrap();
    let folder = stderr(&cli(&core, &["mkfile", &shown(&dir.path().join("folder"))]));
    assert!(folder.contains("already_exists"), "{folder}");

    let orphan = dir.path().join("missing").join("a.txt");
    let missing = stderr(&cli(&core, &["mkfile", &shown(&orphan)]));
    assert!(missing.contains("not_found"), "{missing}");
    let bad = stderr(&cli(
        &core,
        &["mkfile", &shown(&dir.path().join("a?b.txt"))],
    ));
    assert!(bad.contains("invalid_path"), "{bad}");

    let named = dir.path().join("Звіт 📁 cafe\u{301}.md");
    stdout(&cli(&core, &["mkfile", &shown(&named)]));
    assert!(named.is_file());
}
