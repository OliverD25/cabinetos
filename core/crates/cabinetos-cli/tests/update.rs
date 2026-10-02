//! `cabinetos-cli update` against a real core: a development build says it
//! does not update itself; a copy of the core in a fake install folder
//! checks a feed folder, downloads, and swaps, in words and as JSON.
//!
//! Needs `cabinetos-core.exe` next to `cabinetos-cli.exe`. `cargo test
//! --workspace` builds both. After `cargo test -p cabinetos-cli` alone, build
//! the core first with `cargo build -p cabinetos-core`.

use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::{Duration, Instant};

use cabinetos_ipc::PipeName;
use tempfile::TempDir;

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const STARTUP_DEADLINE: Duration = Duration::from_secs(10);
const NEWER: &str = "99.0.0";

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

/// Starts `exe` with its folders in `dir` and `config`, and waits until it
/// answers `ping` through the CLI.
fn start_core(dir: TempDir, exe: &Path, config: &str) -> Core {
    let pipe = PipeName::random();
    let config_path = dir.path().join("cabinetos.json");
    fs::write(&config_path, config).unwrap();
    let child = Command::new(exe)
        .args(["--pipe", pipe.token()])
        .arg("--config")
        .arg(&config_path)
        .arg("--update-dir")
        .arg(dir.path().join("update"))
        .env("CABINETOS_LOG_DIR", dir.path().join("logs"))
        .env("CABINETOS_PLUGINS_DIR", dir.path().join("plugins"))
        .env(
            "CABINETOS_PLUGINS_DATA_DIR",
            dir.path().join("plugins-data"),
        )
        .env("CABINETOS_THEMES_DIR", dir.path().join("themes"))
        .env("CABINETOS_UNDO_DIR", dir.path().join("undo"))
        // No Apps entry here: a key that does not exist is left alone.
        .env(
            "CABINETOS_UPDATE_APPS_KEY",
            format!(r"Software\CabinetOS-test-cli-update-{}", std::process::id()),
        )
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

#[test]
fn a_development_build_says_it_does_not_update_itself() {
    let core = start_core(tempfile::tempdir().unwrap(), &core_exe(), "{}");
    let output = cli(&core, &["update"]);
    assert!(output.status.success(), "{}", stderr(&output));
    assert!(
        stdout(&output).contains("does not update itself")
            && stdout(&output).contains("development build"),
        "{}",
        stdout(&output)
    );
    let json = cli(&core, &["update", "status", "--json"]);
    assert!(json.status.success(), "{}", stderr(&json));
    let reply: serde_json::Value = serde_json::from_slice(&json.stdout).unwrap();
    assert_eq!(reply["type"], "update_state");
    assert_eq!(reply["state"], "not_updatable");
    let check = cli(&core, &["update", "check"]);
    assert!(!check.status.success());
    assert!(
        stderr(&check).contains("update_error"),
        "{}",
        stderr(&check)
    );
}

fn sha256_hex(bytes: &[u8]) -> String {
    use sha2::Digest;
    sha2::Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

fn release_zip() -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let release = format!(r#"{{"product":"CabinetOS","version":"{NEWER}"}}"#);
    for (name, bytes) in [
        ("release.json", release.as_bytes()),
        ("CabinetOS.exe", b"the new window".as_slice()),
        ("cabinetos-core.exe", b"the new core".as_slice()),
    ] {
        writer
            .start_file(name, zip::write::SimpleFileOptions::default())
            .unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

/// A fake install with a copy of the core, and a feed whose `latest.json`
/// names [`NEWER`], in `dir`. Returns the install folder and the feed.
fn fake_release(dir: &TempDir) -> (PathBuf, PathBuf) {
    let install = dir.path().join("install");
    fs::create_dir_all(&install).unwrap();
    fs::copy(core_exe(), install.join("cabinetos-core.exe")).unwrap();
    fs::write(
        install.join("release.json"),
        format!(
            r#"{{"product":"CabinetOS","version":"{}"}}"#,
            env!("CARGO_PKG_VERSION")
        ),
    )
    .unwrap();
    let feed = dir.path().join("feed");
    let stable = feed.join("stable");
    fs::create_dir_all(&stable).unwrap();
    let zip = release_zip();
    fs::write(stable.join("release.zip"), &zip).unwrap();
    fs::write(stable.join("notes.md"), "- New.\n").unwrap();
    let latest = format!(
        r#"{{"schemaVersion":1,"channel":"stable","version":"{NEWER}","published":"2026-10-01",
            "zip":{{"url":"release.zip","sha256":"{}","size":{}}},"notes":{{"url":"notes.md"}},
            "requires":{{"dotnet":"10.0"}}}}"#,
        sha256_hex(&zip),
        zip.len()
    );
    fs::write(stable.join("latest.json"), latest).unwrap();
    (install, feed)
}

#[test]
fn a_release_checks_downloads_and_swaps_in_words() {
    let dir = tempfile::tempdir().unwrap();
    let (install, feed) = fake_release(&dir);
    // Step by step: the download waits for apply.
    let config = serde_json::json!({"update": {
        "check": false,
        "autoInstall": false,
        "source": feed.display().to_string()
    }});
    let core = start_core(
        dir,
        &install.join("cabinetos-core.exe"),
        &config.to_string(),
    );

    let status = cli(&core, &["update"]);
    assert!(
        stdout(&status).contains("not checked yet"),
        "{}",
        stdout(&status)
    );
    let check = cli(&core, &["update", "check"]);
    assert!(check.status.success(), "{}", stderr(&check));
    let words = stdout(&check);
    assert!(words.contains(&format!("{NEWER} is available")), "{words}");
    assert!(words.contains("it needs .NET 10.0"), "{words}");
    let download = cli(&core, &["update", "download"]);
    assert!(download.status.success(), "{}", stderr(&download));
    let words = stdout(&download);
    assert!(
        words.contains(&format!("CabinetOS {NEWER}: ")),
        "the progress line: {words}"
    );
    assert!(words.contains(&format!("{NEWER} is downloaded")), "{words}");
    let apply = cli(&core, &["update", "apply"]);
    assert!(apply.status.success(), "{}", stderr(&apply));
    let words = stdout(&apply);
    assert!(
        words.contains(&format!("{NEWER} is in place; restart CabinetOS")),
        "{words}"
    );
    assert!(words.contains("kept for a rollback"), "{words}");
    assert_eq!(
        fs::read(install.join("cabinetos-core.exe")).unwrap(),
        b"the new core"
    );
}

#[test]
fn with_auto_install_a_download_is_put_in_place_at_once() {
    let dir = tempfile::tempdir().unwrap();
    let (install, feed) = fake_release(&dir);
    let config =
        serde_json::json!({"update": {"check": false, "source": feed.display().to_string()}});
    let core = start_core(
        dir,
        &install.join("cabinetos-core.exe"),
        &config.to_string(),
    );

    let check = cli(&core, &["update", "check"]);
    assert!(check.status.success(), "{}", stderr(&check));
    let download = cli(&core, &["update", "download"]);
    assert!(download.status.success(), "{}", stderr(&download));
    let words = stdout(&download);
    assert!(
        words.contains(&format!("{NEWER} is in place; restart CabinetOS")),
        "{words}"
    );
    assert_eq!(
        fs::read(install.join("cabinetos-core.exe")).unwrap(),
        b"the new core"
    );
}
