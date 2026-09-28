//! `cabinetos-cli` with names beyond ASCII (docs/ui.md, "Edge cases"),
//! against a real core. What it prints into a pipe is UTF-8; on a console
//! the standard library writes through `WriteConsoleW`, so the console's
//! code page plays no part. Everything written lives under
//! `%TEMP%\cabinetos-core-test\`.
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

const FOLDERS: [&str; 3] = ["Ґанок", "中文文件夹", "📁 photos"];
const FILES: [&str; 7] = [
    "Звіт 2026.txt",
    "Їжак і Єнот.md",
    "日本語のファイル.txt",
    "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
    "caf\u{e9}.txt",
    "cafe\u{301}.txt",
    "مستند.txt",
];

fn scratch(prefix: &str) -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-core-test");
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

/// A core whose indexer pipe leads nowhere, so search walks.
fn start_core() -> Core {
    let dir = scratch("cli-names-core");
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

/// The output of a command that succeeded, which must be UTF-8.
fn stdout(output: &Output) -> String {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout.clone()).expect("the output is UTF-8")
}

/// The fixture's names in a folder; each file holds its own name.
fn fixture() -> TempDir {
    let dir = scratch("cli-names");
    for folder in FOLDERS {
        std::fs::create_dir(dir.path().join(folder)).unwrap();
    }
    for file in FILES {
        std::fs::write(dir.path().join(file), file).unwrap();
    }
    std::fs::write(dir.path().join("Ґанок").join("Звіт 2026.txt"), "older").unwrap();
    dir
}

#[test]
fn ls_prints_names_beyond_ascii_unit_for_unit() {
    let core = start_core();
    let dir = fixture();
    let text = stdout(&cli(&core, &["ls", &dir.path().display().to_string()]));
    let lines: Vec<&str> = text.lines().collect();
    for folder in FOLDERS {
        assert!(lines.contains(&format!("d {folder}").as_str()), "{text}");
    }
    for file in FILES {
        // The decomposed café comes out decomposed.
        assert!(
            lines.contains(&format!("- {file}").as_str()),
            "{file}: {text}"
        );
    }
}

#[test]
fn describe_prints_the_name_last_so_wide_names_keep_the_columns() {
    let core = start_core();
    let dir = fixture();
    let text = stdout(&cli(
        &core,
        &["describe", &dir.path().display().to_string()],
    ));
    let rows: Vec<&str> = text
        .lines()
        .filter(|line| !line.contains("entries described"))
        .collect();
    assert_eq!(rows.len(), FOLDERS.len() + FILES.len(), "{text}");
    for name in FOLDERS.iter().chain(&FILES) {
        let row = rows
            .iter()
            .find(|row| row.ends_with(name))
            .unwrap_or_else(|| panic!("no row ends with {name}: {text}"));
        let key = if FOLDERS.contains(name) {
            "folder"
        } else if Path::new(name)
            .extension()
            .is_some_and(|extension| extension == "md")
        {
            "ext:.md"
        } else {
            "ext:.txt"
        };
        assert!(row.contains(key), "{row}");
    }
    // Every column before the name is ASCII or the type name, so the name
    // starts where the columns end: after the icon key's padded column.
    let starts: Vec<usize> = rows
        .iter()
        .map(|row| {
            let name = FOLDERS
                .iter()
                .chain(&FILES)
                .find(|name| row.ends_with(*name))
                .unwrap();
            row[..row.len() - name.len()].chars().count()
        })
        .collect();
    assert!(starts.windows(2).all(|pair| pair[0] == pair[1]), "{text}");
}

#[test]
fn search_takes_a_query_beyond_ascii_and_prints_the_hits() {
    let core = start_core();
    let dir = fixture();
    let root = dir.path().display().to_string();
    let text = stdout(&cli(&core, &["search", "звіт", "--root", &root]));
    let lines: Vec<&str> = text.lines().collect();
    assert!(
        lines.contains(&format!(r"f {root}\Звіт 2026.txt").as_str()),
        "{text}"
    );
    assert!(
        lines.contains(&format!(r"f {root}\Ґанок\Звіт 2026.txt").as_str()),
        "{text}"
    );
    let text = stdout(&cli(&core, &["search", "caf\u{e9}", "--root", &root]));
    for file in ["caf\u{e9}.txt", "cafe\u{301}.txt"] {
        assert!(
            text.lines().any(|line| line == format!(r"f {root}\{file}")),
            "{file}: {text}"
        );
    }
}
