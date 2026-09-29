//! `copy` and `delete` through the CLI against a real core. Everything the
//! test writes lives under `%TEMP%\cabinetos-jobs-test\` and is removed.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`; `cargo test
//! --workspace` builds both.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");

struct Core {
    child: Child,
    pipe: PipeName,
}

impl Drop for Core {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn scratch() -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli")
        .tempdir_in(root)
        .unwrap()
}

fn start_core(dir: &Path) -> Core {
    let core_exe: PathBuf = Path::new(CLI_EXE).with_file_name("cabinetos-core.exe");
    assert!(
        core_exe.exists(),
        "build cabinetos-core first (cargo test --workspace does)"
    );
    let pipe = PipeName::random();
    let child = Command::new(core_exe)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(dir.join("cabinetos.json"))
        .env("CABINETOS_LOG_DIR", dir.join("logs"))
        // Never the real plugins folder, whatever is installed there.
        .env("CABINETOS_PLUGINS_DIR", dir.join("plugins"))
        .env("CABINETOS_PLUGINS_DATA_DIR", dir.join("plugins-data"))
        .env("CABINETOS_THEMES_DIR", dir.join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.join("undo"))
        .env_remove("CABINETOS_CONFIG")
        .env_remove("CABINETOS_LOG")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let core = Core { child, pipe };
    let deadline = Instant::now() + Duration::from_secs(10);
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

#[test]
fn copy_answers_conflicts_and_counts_progress_events() {
    let data = scratch();
    let source = data.path().join("src");
    let destination = data.path().join("dst");
    fs::create_dir_all(&source).unwrap();
    fs::create_dir_all(destination.join("src")).unwrap();
    for number in 0..40 {
        fs::write(source.join(format!("{number}.txt")), "new").unwrap();
    }
    for number in 0..3 {
        fs::write(destination.join("src").join(format!("{number}.txt")), "old").unwrap();
    }
    let core = start_core(&data.path().join("core"));
    let (source_text, destination_text) = (
        source.display().to_string(),
        destination.display().to_string(),
    );
    let output = cli(
        &core,
        &[
            "copy",
            &source_text,
            &destination_text,
            "--resolve",
            "skip",
            "--stats",
        ],
    );
    let text = String::from_utf8_lossy(&output.stdout);
    assert!(
        output.status.success(),
        "{text}{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(text.matches("conflict ").count(), 3, "{text}");
    assert!(
        text.contains("completed: 41 of 41 files and folders, 3 skipped"),
        "{text}"
    );
    assert!(text.contains("progress events:"), "{text}");
    assert_eq!(
        fs::read_to_string(destination.join("src").join("1.txt")).unwrap(),
        "old"
    );
    assert_eq!(
        fs::read_to_string(destination.join("src").join("39.txt")).unwrap(),
        "new"
    );

    let listed = cli(&core, &["jobs"]);
    assert!(String::from_utf8_lossy(&listed.stdout).contains("completed"));

    let output = cli(&core, &["delete", &destination_text, "--permanent"]);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(!destination.exists());
    assert!(!cli(&core, &["job", "pause", "999999"]).status.success());
}
