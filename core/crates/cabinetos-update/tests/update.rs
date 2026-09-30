//! The updater end to end, without a core: a channel's `latest.json` in a
//! folder under `%TEMP%\cabinetos-core-test\` (or on a web server on
//! 127.0.0.1), a release zip made by the test, and a fake install folder
//! with `release.json`, in which a real program runs while it is swapped.
//! The Apps entry lives under a test key of its own. Nothing here reaches
//! the network or touches the real install or the real entry.

use std::collections::BTreeMap;
use std::fs;
use std::io::{Cursor, Read, Write};
use std::net::TcpListener;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex};

use cabinetos_fs::registry::{self, RegValue};
use cabinetos_protocol::{UpdateChannel, UpdatePhase};
use cabinetos_update::{
    DISCARD_DIR, ErrorKind, Notice, PREVIOUS_DIR, Paths, RECORD_FILE, STATE_FILE, Settings, Updater,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tempfile::TempDir;

fn scratch() -> TempDir {
    let root = std::env::temp_dir().join("cabinetos-core-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("update")
        .tempdir_in(root)
        .unwrap()
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .fold(String::new(), |mut text, byte| {
            let _ = std::fmt::Write::write_fmt(&mut text, format_args!("{byte:02x}"));
            text
        })
}

fn now_ms() -> u64 {
    u64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis(),
    )
    .unwrap()
}

fn release_json(version: &str) -> String {
    json!({"product": "CabinetOS", "version": version, "commit": "test"}).to_string()
}

/// A release zip: `release.json`, the two programs, the scripts, a file in
/// a folder, and `extra`.
fn release_zip(version: &str, extra: &[(&str, &[u8])]) -> Vec<u8> {
    let release = release_json(version);
    let mut files: Vec<(&str, &[u8])> = vec![
        ("release.json", release.as_bytes()),
        ("CabinetOS.exe", b"the new window"),
        ("cabinetos-core.exe", b"the new core"),
        ("install.ps1", b"# the installer"),
        ("uninstall.ps1", b"# the uninstaller"),
        ("Assets/xterm/new.txt", b"new asset"),
    ];
    files.extend_from_slice(extra);
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    for (name, bytes) in files {
        writer.start_file(name, options).unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

/// One test's folders: the install, the updater's own folder, and the
/// feed (the `update.source` folder).
struct Setup {
    root: TempDir,
    notices: Arc<Mutex<Vec<Notice>>>,
    apps_key: String,
}

impl Setup {
    fn new(name: &str) -> Self {
        let root = scratch();
        for folder in ["install", "update", "feed"] {
            fs::create_dir_all(root.path().join(folder)).unwrap();
        }
        Self {
            root,
            notices: Arc::new(Mutex::new(Vec::new())),
            apps_key: format!(
                r"Software\CabinetOS-test-update-{}-{name}",
                std::process::id()
            ),
        }
    }

    fn install(&self) -> PathBuf {
        self.root.path().join("install")
    }

    fn dir(&self) -> PathBuf {
        self.root.path().join("update")
    }

    fn feed(&self) -> PathBuf {
        self.root.path().join("feed")
    }

    /// The running version's files: `release.json`, the two programs, an
    /// asset, and the installer's record.
    fn install_version(&self, version: &str) {
        let install = self.install();
        fs::write(install.join("release.json"), release_json(version)).unwrap();
        fs::write(install.join("CabinetOS.exe"), b"the old window").unwrap();
        fs::write(install.join("cabinetos-core.exe"), b"the old core").unwrap();
        fs::create_dir_all(install.join("Assets").join("xterm")).unwrap();
        fs::write(
            install.join("Assets").join("xterm").join("old.txt"),
            b"old asset",
        )
        .unwrap();
        let record = json!({
            "product": "CabinetOS",
            "version": version,
            "installedUtc": "2026-09-30T00:00:00Z",
            "scope": "user",
            "files": ["release.json", "CabinetOS.exe", "cabinetos-core.exe", r"Assets\xterm\old.txt"],
            "startMenuShortcut": null,
            "path": null,
            "indexer": false
        });
        // Windows PowerShell writes a byte-order mark.
        let mut text = "\u{feff}".to_owned();
        text.push_str(&serde_json::to_string_pretty(&record).unwrap());
        fs::write(install.join(RECORD_FILE), text).unwrap();
    }

    /// Publishes `zip` as `version` of `channel`: the zip, its notes and
    /// `latest.json`, with the zip's real hash unless `sha256` says another.
    fn publish(&self, channel: &str, version: &str, zip: &[u8], sha256_override: Option<&str>) {
        let folder = self.feed().join(channel);
        fs::create_dir_all(folder.join("files")).unwrap();
        let name = format!("CabinetOS-{version}-win-x64.zip");
        fs::write(folder.join("files").join(&name), zip).unwrap();
        fs::write(
            folder.join(format!("notes-{version}.md")),
            format!("## [{version}] - 2026-09-30\n\n### Added\n\n- **In-app updates**.\n"),
        )
        .unwrap();
        let latest = json!({
            "schemaVersion": 1,
            "channel": channel,
            "version": version,
            "published": "2026-09-30",
            "zip": {
                "url": format!("files/{name}"),
                "sha256": sha256_override.map_or_else(|| sha256(zip), str::to_owned),
                "size": zip.len()
            },
            "notes": {"url": format!("notes-{version}.md")},
            "requires": {"windowsAppRuntime": "2.5", "dotnet": "10.0"}
        });
        fs::write(folder.join("latest.json"), latest.to_string()).unwrap();
    }

    fn settings(&self, channel: UpdateChannel) -> Settings {
        Settings {
            check: true,
            channel,
            source: self.feed().display().to_string(),
            allow_insecure: false,
        }
    }

    fn open(&self, current: &str, settings: &Settings) -> Updater {
        let notices = Arc::clone(&self.notices);
        Updater::open(
            Paths {
                install: self.install(),
                dir: self.dir(),
                apps_key: self.apps_key.clone(),
            },
            current,
            settings,
            move |notice| notices.lock().unwrap().push(notice),
        )
    }

    fn states(&self) -> Vec<UpdatePhase> {
        self.notices
            .lock()
            .unwrap()
            .iter()
            .filter_map(|notice| match notice {
                Notice::State(status) => Some(status.state),
                Notice::Progress(_) => None,
            })
            .collect()
    }

    fn saved(&self) -> Value {
        serde_json::from_slice(&fs::read(self.dir().join(STATE_FILE)).unwrap()).unwrap()
    }
}

impl Drop for Setup {
    fn drop(&mut self) {
        let _ = registry::delete_user_key(&self.apps_key);
    }
}

/// The files under `dir`, relative, with their contents; `previous\` and
/// `previous-old\` left out.
fn tree(dir: &Path) -> BTreeMap<String, Vec<u8>> {
    fn walk(root: &Path, dir: &Path, into: &mut BTreeMap<String, Vec<u8>>) {
        for entry in fs::read_dir(dir).unwrap().flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            if dir == root && (name == PREVIOUS_DIR || name == DISCARD_DIR) {
                continue;
            }
            if path.is_dir() {
                walk(root, &path, into);
            } else {
                let relative = path.strip_prefix(root).unwrap().display().to_string();
                into.insert(relative, fs::read(&path).unwrap());
            }
        }
    }
    let mut files = BTreeMap::new();
    walk(dir, dir, &mut files);
    files
}

#[test]
fn a_newer_version_is_found_downloaded_checked_and_unpacked() {
    let setup = Setup::new("download");
    setup.install_version("0.1.0");
    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    assert!(updater.updatable());
    assert_eq!(updater.status().state, UpdatePhase::Unchecked);

    let checked = updater.check(&settings).unwrap();
    assert_eq!(checked.state, UpdatePhase::Available);
    let latest = checked.latest.as_ref().unwrap();
    assert_eq!(latest.version, "0.2.0");
    assert_eq!(latest.requires.dotnet.as_deref(), Some("10.0"));
    assert!(
        checked
            .notes
            .as_deref()
            .unwrap()
            .contains("**In-app updates**")
    );
    assert!(
        checked
            .notes_url
            .as_deref()
            .unwrap()
            .ends_with(r"stable\notes-0.2.0.md")
    );
    assert_eq!(
        checked.install_dir.as_deref(),
        Some(setup.install().display().to_string().as_str())
    );
    assert!(checked.checked_at_ms.is_some());

    let downloaded = updater.download(&settings).unwrap();
    assert_eq!(downloaded.state, UpdatePhase::Downloaded);
    let staged = setup.dir().join("staging").join("0.2.0");
    assert!(staged.join("files").join("release.json").is_file());
    assert!(
        staged
            .join("files")
            .join("Assets")
            .join("xterm")
            .join("new.txt")
            .is_file()
    );
    assert!(
        !staged.join("download.zip").exists(),
        "the zip goes once unpacked"
    );
    assert_eq!(setup.saved()["staged"], "0.2.0");
    assert_eq!(
        setup.states(),
        [
            UpdatePhase::Checking,
            UpdatePhase::Available,
            UpdatePhase::Downloading,
            UpdatePhase::Downloaded
        ]
    );
    let progress: Vec<(u64, u64)> = setup
        .notices
        .lock()
        .unwrap()
        .iter()
        .filter_map(|notice| match notice {
            Notice::Progress(progress) => Some((progress.bytes, progress.total)),
            Notice::State(_) => None,
        })
        .collect();
    let size = zip.len() as u64;
    assert_eq!(progress.first(), Some(&(0, size)));
    assert_eq!(progress.last(), Some(&(size, size)));

    // Downloading again is nothing to do; a new start remembers it.
    assert_eq!(
        updater.download(&settings).unwrap().state,
        UpdatePhase::Downloaded
    );
    drop(updater);
    let reopened = setup.open("0.1.0", &settings);
    let status = reopened.status();
    assert_eq!(status.state, UpdatePhase::Downloaded);
    assert!(status.notes.unwrap().contains("In-app updates"));
}

#[test]
fn a_wrong_hash_deletes_the_download_and_nothing_is_staged() {
    let setup = Setup::new("hash");
    setup.install_version("0.1.0");
    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, Some(&"0".repeat(64)));
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    updater.check(&settings).unwrap();
    let error = updater.download(&settings).unwrap_err();
    assert_eq!(error.kind, ErrorKind::HashMismatch);
    assert!(error.message.contains("it was deleted"), "{error}");
    assert!(!setup.dir().join("staging").join("0.2.0").exists());
    let status = updater.status();
    assert_eq!(status.state, UpdatePhase::Failed);
    assert_eq!(status.message.as_deref(), Some(error.message.as_str()));
    assert!(setup.saved().get("staged").is_none_or(Value::is_null));
    let refused = updater.apply().unwrap_err();
    assert_eq!(refused.kind, ErrorKind::Refused);

    // The right zip, published again, installs.
    setup.publish("stable", "0.2.0", &zip, None);
    updater.check(&settings).unwrap();
    assert_eq!(
        updater.download(&settings).unwrap().state,
        UpdatePhase::Downloaded
    );
}

#[test]
fn versions_compare_as_semantic_versions_on_each_channel() {
    let setup = Setup::new("versions");
    setup.install_version("0.1.0");
    let stable = setup.settings(UpdateChannel::Stable);
    let preview = setup.settings(UpdateChannel::Preview);
    // The install folder holds the version that runs.
    let state = |current: &str, settings: &Settings| {
        setup.install_version(current);
        let updater = setup.open(current, settings);
        updater.check(settings).map(|status| status.state)
    };

    setup.publish("stable", "0.1.0", b"zip", None);
    assert_eq!(
        state("0.1.0", &stable),
        Ok(UpdatePhase::UpToDate),
        "the same"
    );
    assert_eq!(state("0.1.1", &stable), Ok(UpdatePhase::UpToDate), "older");
    assert_eq!(state("0.0.9", &stable), Ok(UpdatePhase::Available), "newer");

    setup.publish("stable", "0.2.0-preview.1", b"zip", None);
    let error = state("0.1.0", &stable).unwrap_err();
    assert!(
        error.message.contains("never offers a pre-release"),
        "{error}"
    );

    setup.publish("preview", "0.2.0-preview.1", b"zip", None);
    assert_eq!(state("0.1.0", &preview), Ok(UpdatePhase::Available));
    assert_eq!(
        state("0.2.0-preview.1", &preview),
        Ok(UpdatePhase::UpToDate)
    );
    assert_eq!(
        state("0.2.0", &preview),
        Ok(UpdatePhase::UpToDate),
        "a release is newer than its previews"
    );
    setup.publish("preview", "0.2.0", b"zip", None);
    assert_eq!(
        state("0.2.0-preview.3", &preview),
        Ok(UpdatePhase::Available)
    );
}

#[test]
fn the_daily_check_asks_once_a_day() {
    let setup = Setup::new("daily");
    setup.install_version("0.1.0");
    // No feed at all: a request would fail.
    let settings = Settings {
        source: setup.root.path().join("no-feed").display().to_string(),
        ..setup.settings(UpdateChannel::Stable)
    };
    let now = now_ms();
    let hour = 60 * 60 * 1000;
    fs::write(
        setup.dir().join(STATE_FILE),
        json!({"lastCheckMs": now - hour}).to_string(),
    )
    .unwrap();
    let updater = setup.open("0.1.0", &settings);
    assert!(!updater.due(&settings, now));
    assert!(
        updater.run_daily(&settings).is_none(),
        "checked an hour ago"
    );
    assert!(setup.states().is_empty(), "no request, no state change");
    assert!(updater.due(&settings, now + 24 * hour));
    assert!(updater.due(&settings, now - 2 * hour), "a clock set back");
    let off = Settings {
        check: false,
        ..settings.clone()
    };
    assert!(!updater.due(&off, now + 24 * hour));

    fs::write(
        setup.dir().join(STATE_FILE),
        json!({"lastCheckMs": now - 25 * hour}).to_string(),
    )
    .unwrap();
    let updater = setup.open("0.1.0", &settings);
    let ran = updater.run_daily(&settings).expect("a day has passed");
    let error = ran.unwrap_err();
    assert!(error.message.contains("latest.json"), "{error}");
    assert_eq!(updater.status().state, UpdatePhase::Failed);
    assert!(updater.due(&settings, now), "a failed check is tried again");

    // With a feed, the daily check also downloads what is newer.
    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, None);
    let with_feed = setup.settings(UpdateChannel::Stable);
    let done = updater.run_daily(&with_feed).unwrap().unwrap();
    assert_eq!(done.state, UpdatePhase::Downloaded);
    assert!(updater.run_daily(&with_feed).is_none());
}

/// A program that runs from `program` until it is killed: a copy of
/// `cmd.exe` reading commands from a pipe nobody writes to.
fn run(program: &Path) -> Child {
    Command::new(program)
        .arg("/k")
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap()
}

#[test]
#[expect(
    clippy::too_many_lines,
    reason = "one swap and its rollback, step by step"
)]
fn the_swap_moves_a_running_program_aside_and_the_rollback_brings_it_back() {
    let setup = Setup::new("swap");
    setup.install_version("0.1.0");
    let install = setup.install();
    let system = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_owned());
    fs::copy(
        Path::new(&system).join("System32").join("cmd.exe"),
        install.join("CabinetOS.exe"),
    )
    .unwrap();
    let running_bytes = fs::read(install.join("CabinetOS.exe")).unwrap();
    let mut child = run(&install.join("CabinetOS.exe"));
    registry::write_user_values(
        &setup.apps_key,
        &[
            ("DisplayName", RegValue::Text("CabinetOS")),
            ("DisplayVersion", RegValue::Text("0.1.0")),
            (
                "InstallLocation",
                RegValue::Text(&format!("{}\\", install.display())),
            ),
            ("EstimatedSize", RegValue::Number(1)),
        ],
    )
    .unwrap();
    let before = tree(&install);

    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    updater.check(&settings).unwrap();
    updater.download(&settings).unwrap();
    let applied = updater.apply().unwrap();
    assert_eq!(applied.state, UpdatePhase::Ready);
    assert_eq!(applied.installed.as_deref(), Some("0.2.0"));
    assert_eq!(applied.previous.as_deref(), Some("0.1.0"));

    assert!(
        child.try_wait().unwrap().is_none(),
        "the program still runs"
    );
    let previous = install.join(PREVIOUS_DIR);
    assert_eq!(
        fs::read(previous.join("CabinetOS.exe")).unwrap(),
        running_bytes
    );
    assert_eq!(
        tree(&previous),
        before,
        "the running version, whole, in previous"
    );
    assert_eq!(
        fs::read(install.join("CabinetOS.exe")).unwrap(),
        b"the new window"
    );
    assert!(
        install
            .join("Assets")
            .join("xterm")
            .join("new.txt")
            .is_file()
    );
    assert!(
        !install
            .join("Assets")
            .join("xterm")
            .join("old.txt")
            .exists()
    );
    assert!(
        !install.join("install.ps1").exists(),
        "the installer is never installed"
    );
    assert!(install.join("uninstall.ps1").is_file());
    assert!(!install.join(DISCARD_DIR).exists());
    let record: Value =
        serde_json::from_slice(&fs::read(install.join(RECORD_FILE)).unwrap()).unwrap();
    assert_eq!(record["version"], "0.2.0");
    assert_eq!(record["scope"], "user");
    assert!(
        record["files"]
            .as_array()
            .unwrap()
            .contains(&json!(r"Assets\xterm\new.txt"))
    );
    assert_eq!(
        registry::read_user_string(&setup.apps_key, "DisplayVersion").as_deref(),
        Some("0.2.0")
    );
    assert!(!setup.dir().join("staging").join("0.2.0").exists());
    assert_eq!(setup.saved()["swap"]["to"], "0.2.0");
    assert!(setup.saved()["swap"].get("confirmedAtMs").is_none());

    // The next start runs 0.2.0 and confirms the swap.
    drop(updater);
    let restarted = setup.open("0.2.0", &settings);
    assert!(setup.saved()["swap"]["confirmedAtMs"].is_u64());
    assert_eq!(restarted.status().previous.as_deref(), Some("0.1.0"));
    assert_eq!(restarted.status().state, UpdatePhase::UpToDate);

    let rolled = restarted.rollback().unwrap();
    assert_eq!(rolled.state, UpdatePhase::Ready);
    assert_eq!(rolled.installed.as_deref(), Some("0.1.0"));
    assert_eq!(rolled.previous, None);
    assert!(
        child.try_wait().unwrap().is_none(),
        "the program still runs"
    );
    assert_eq!(tree(&install), before, "the old version is back");
    assert!(!previous.exists());
    assert!(
        !install.join(DISCARD_DIR).exists(),
        "nothing of 0.2.0 runs, so it is gone"
    );
    assert_eq!(
        registry::read_user_string(&setup.apps_key, "DisplayVersion").as_deref(),
        Some("0.1.0")
    );
    let again = restarted.rollback().unwrap_err();
    assert_eq!(again.kind, ErrorKind::Refused);

    child.kill().unwrap();
    child.wait().unwrap();
}

#[test]
fn a_copy_that_fails_half_way_leaves_the_running_version_whole() {
    let setup = Setup::new("halfway");
    setup.install_version("0.1.0");
    let install = setup.install();
    // An earlier update's previous version, which must survive too.
    fs::create_dir_all(install.join(PREVIOUS_DIR)).unwrap();
    fs::write(
        install.join(PREVIOUS_DIR).join("release.json"),
        release_json("0.0.9"),
    )
    .unwrap();
    let before = tree(&install);
    let before_previous = tree(&install.join(PREVIOUS_DIR));

    let zip = release_zip("0.2.0", &[("zz-last.bin", b"last")]);
    setup.publish("stable", "0.2.0", &zip, None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    assert_eq!(updater.status().previous.as_deref(), Some("0.0.9"));
    updater.check(&settings).unwrap();
    updater.download(&settings).unwrap();

    // The last file cannot be read: the copy stops after the others.
    let staged = setup.dir().join("staging").join("0.2.0").join("files");
    let locked = fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(staged.join("zz-last.bin"))
        .unwrap();
    let error = updater.apply().unwrap_err();
    assert_eq!(error.kind, ErrorKind::Failed);
    assert!(error.message.contains("zz-last.bin"), "{error}");
    assert!(error.message.contains("nothing was changed"), "{error}");
    assert_eq!(tree(&install), before, "the running version is whole");
    assert_eq!(tree(&install.join(PREVIOUS_DIR)), before_previous);
    assert!(!install.join(DISCARD_DIR).exists());
    assert_eq!(updater.status().state, UpdatePhase::Failed);

    // The download stays; once the file can be read, the swap works.
    drop(locked);
    assert_eq!(updater.apply().unwrap().state, UpdatePhase::Ready);
    assert_eq!(fs::read(install.join("zz-last.bin")).unwrap(), b"last");
    assert_eq!(
        tree(&install.join(PREVIOUS_DIR)).get("release.json"),
        Some(&release_json("0.1.0").into_bytes())
    );
}

#[test]
fn a_development_build_is_not_updatable_and_touches_nothing() {
    let setup = Setup::new("development");
    fs::write(setup.install().join("cabinetos-core.exe"), b"core").unwrap();
    fs::remove_dir(setup.dir()).unwrap();
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    assert!(!updater.updatable());
    let status = updater.status();
    assert_eq!(status.state, UpdatePhase::NotUpdatable);
    assert!(status.reason.unwrap().contains("development build"));
    assert_eq!(status.install_dir, None);
    assert!(!updater.due(&settings, 0));
    assert!(updater.run_daily(&settings).is_none());
    for refused in [
        updater.check(&settings),
        updater.download(&settings),
        updater.apply(),
        updater.rollback(),
        updater.snooze(),
    ] {
        assert_eq!(refused.unwrap_err().kind, ErrorKind::Refused);
    }
    assert!(!setup.dir().exists(), "nothing was written");
}

/// The current user's security identifier, for `icacls`.
fn user_sid() -> String {
    let output = Command::new("whoami")
        .args(["/user", "/fo", "csv", "/nh"])
        .output()
        .unwrap();
    let text = String::from_utf8_lossy(&output.stdout);
    text.trim()
        .rsplit(',')
        .next()
        .unwrap()
        .trim_matches('"')
        .to_owned()
}

#[test]
fn a_folder_the_user_cannot_change_is_not_updatable() {
    let setup = Setup::new("readonly");
    setup.install_version("0.1.0");
    let install = setup.install();
    let sid = user_sid();
    let deny = Command::new("icacls")
        .arg(&install)
        .args(["/deny", &format!("*{sid}:(OI)(CI)(AD,WD)")])
        .output()
        .unwrap();
    assert!(
        deny.status.success(),
        "{}",
        String::from_utf8_lossy(&deny.stdout)
    );
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    let status = updater.status();
    let undo = Command::new("icacls")
        .arg(&install)
        .args(["/remove:d", &format!("*{sid}")])
        .output()
        .unwrap();
    assert!(undo.status.success());
    assert_eq!(status.state, UpdatePhase::NotUpdatable);
    let reason = status.reason.unwrap();
    assert!(reason.contains("without administrator rights"), "{reason}");
    assert!(reason.contains("installer or winget"), "{reason}");
    assert!(
        status.install_dir.is_some(),
        "a release, which the window names"
    );
}

#[test]
fn an_apps_entry_that_names_another_folder_is_left_alone() {
    let setup = Setup::new("apps");
    setup.install_version("0.1.0");
    registry::write_user_values(
        &setup.apps_key,
        &[
            ("DisplayVersion", RegValue::Text("0.1.0")),
            ("InstallLocation", RegValue::Text(r"C:\Somewhere\Else")),
        ],
    )
    .unwrap();
    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    updater.check(&settings).unwrap();
    updater.download(&settings).unwrap();
    updater.apply().unwrap();
    assert_eq!(
        registry::read_user_string(&setup.apps_key, "DisplayVersion").as_deref(),
        Some("0.1.0")
    );
}

#[test]
fn a_snooze_is_remembered_for_a_day() {
    let setup = Setup::new("snooze");
    setup.install_version("0.1.0");
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    let status = updater.snooze().unwrap();
    let until = status.snoozed_until_ms.unwrap();
    let now = now_ms();
    assert!(until > now + 23 * 60 * 60 * 1000 && until <= now + 24 * 60 * 60 * 1000);
    assert_eq!(setup.saved()["snoozedUntilMs"], until);
    assert_eq!(
        setup.open("0.1.0", &settings).status().snoozed_until_ms,
        Some(until)
    );
}

#[test]
fn another_channel_forgets_what_was_read_for_the_first() {
    let setup = Setup::new("channel");
    setup.install_version("0.1.0");
    setup.publish("stable", "0.2.0", b"zip", None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);
    assert_eq!(
        updater.check(&settings).unwrap().state,
        UpdatePhase::Available
    );
    updater.set_channel(UpdateChannel::Preview);
    let status = updater.status();
    assert_eq!(status.channel, UpdateChannel::Preview);
    assert_eq!(status.state, UpdatePhase::Unchecked);
    assert_eq!(status.latest, None);
    assert!(updater.due(&setup.settings(UpdateChannel::Preview), 0));
}

/// A web server on 127.0.0.1 that serves `files` by path, answers
/// `If-None-Match: "v1"` with 304, and records every request's head.
fn serve(files: BTreeMap<String, Vec<u8>>, requests: usize) -> (u16, Arc<Mutex<Vec<String>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let seen = Arc::new(Mutex::new(Vec::new()));
    let log = Arc::clone(&seen);
    std::thread::spawn(move || {
        for _ in 0..requests {
            let (mut stream, _) = listener.accept().unwrap();
            let mut head = Vec::new();
            let mut byte = [0; 1];
            while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
                head.push(byte[0]);
            }
            let head = String::from_utf8_lossy(&head).into_owned();
            let path = head.split_whitespace().nth(1).unwrap_or("/").to_owned();
            let response = if head.to_ascii_lowercase().contains("if-none-match: \"v1\"") {
                b"HTTP/1.1 304 Not Modified\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n".to_vec()
            } else if let Some(body) = files.get(&path) {
                let mut response = format!(
                    "HTTP/1.1 200 OK\r\nETag: \"v1\"\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                    body.len()
                )
                .into_bytes();
                response.extend_from_slice(body);
                response
            } else {
                b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_vec()
            };
            stream.write_all(&response).unwrap();
            log.lock().unwrap().push(head);
        }
    });
    (port, seen)
}

#[test]
fn a_web_feed_is_read_with_its_etag_and_refused_over_plain_http_unless_allowed() {
    let setup = Setup::new("web");
    setup.install_version("0.1.0");
    let zip = release_zip("0.2.0", &[]);
    setup.publish("stable", "0.2.0", &zip, None);
    let folder = setup.feed().join("stable");
    let files = BTreeMap::from([
        (
            "/update/stable/latest.json".to_owned(),
            fs::read(folder.join("latest.json")).unwrap(),
        ),
        (
            "/update/stable/notes-0.2.0.md".to_owned(),
            fs::read(folder.join("notes-0.2.0.md")).unwrap(),
        ),
        (
            "/update/stable/files/CabinetOS-0.2.0-win-x64.zip".to_owned(),
            zip.clone(),
        ),
    ]);
    let (port, seen) = serve(files, 4);
    let mut settings = Settings {
        source: format!("http://127.0.0.1:{port}/update"),
        ..setup.settings(UpdateChannel::Stable)
    };
    let updater = setup.open("0.1.0", &settings);
    let refused = updater.check(&settings).unwrap_err();
    assert!(
        refused.message.contains("update.allowInsecure"),
        "{refused}"
    );

    settings.allow_insecure = true;
    let first = updater.check(&settings).unwrap();
    assert_eq!(first.state, UpdatePhase::Available);
    assert!(first.notes.unwrap().contains("In-app updates"));
    assert_eq!(setup.saved()["etag"], "\"v1\"");
    // Unchanged: the server answers 304, and the kept copy is used.
    let second = updater.check(&settings).unwrap();
    assert_eq!(second.latest, first.latest);
    assert_eq!(
        updater.download(&settings).unwrap().state,
        UpdatePhase::Downloaded
    );

    let seen = seen.lock().unwrap();
    assert!(seen[0].starts_with("GET /update/stable/latest.json"));
    assert!(!seen[0].to_ascii_lowercase().contains("if-none-match"));
    assert!(seen[1].starts_with("GET /update/stable/notes-0.2.0.md"));
    assert!(
        seen[2]
            .to_ascii_lowercase()
            .contains("if-none-match: \"v1\"")
    );
    assert!(seen[3].contains("/update/stable/files/CabinetOS-0.2.0-win-x64.zip"));
}

#[test]
fn a_step_waits_for_another_core_and_sees_its_swap() {
    let setup = Setup::new("cores");
    setup.install_version("0.1.0");
    setup.publish("stable", "0.2.0", b"zip", None);
    let settings = setup.settings(UpdateChannel::Stable);
    let updater = setup.open("0.1.0", &settings);

    // Another core in the middle of a step holds the step lock.
    let other = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(setup.dir().join(".step.lock"))
        .unwrap();
    other.lock().unwrap();
    let refused = updater.check(&settings).unwrap_err();
    assert_eq!(refused.kind, ErrorKind::Refused);
    assert!(
        refused.message.contains("another CabinetOS window"),
        "{refused}"
    );
    drop(other);
    assert_eq!(
        updater.check(&settings).unwrap().state,
        UpdatePhase::Available
    );

    // Another core swapped 0.2.0 in: this one, still 0.1.0, says restart.
    setup.install_version("0.2.0");
    let refused = updater.download(&settings).unwrap_err();
    assert!(
        refused.message.contains("0.2.0 is in place already"),
        "{refused}"
    );
    let status = updater.status();
    assert_eq!(status.state, UpdatePhase::Ready);
    assert_eq!(status.installed.as_deref(), Some("0.2.0"));
}

#[test]
fn stopping_ends_a_download_and_leaves_nothing() {
    let setup = Setup::new("stop");
    setup.install_version("0.1.0");
    let size = 2 * 1024 * 1024;
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut head = Vec::new();
        let mut byte = [0; 1];
        while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
            head.push(byte[0]);
        }
        let header =
            format!("HTTP/1.1 200 OK\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n");
        let _ = stream.write_all(header.as_bytes());
        // Slowly: 64 KiB every 50 ms, until the client hangs up.
        let piece = vec![7; 65_536];
        for _ in 0..size / 65_536 {
            if stream.write_all(&piece).is_err() {
                return;
            }
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
    });
    let folder = setup.feed().join("stable");
    fs::create_dir_all(&folder).unwrap();
    let latest = json!({
        "schemaVersion": 1, "channel": "stable", "version": "0.2.0", "published": "2026-09-30",
        "zip": {"url": format!("http://127.0.0.1:{port}/zip"), "sha256": "0".repeat(64), "size": size},
        "notes": {"url": "notes.md"}
    });
    fs::write(folder.join("latest.json"), latest.to_string()).unwrap();
    let settings = Settings {
        allow_insecure: true,
        ..setup.settings(UpdateChannel::Stable)
    };

    let started = Arc::new(Mutex::new(false));
    let seen = Arc::clone(&started);
    let updater = Arc::new(Updater::open(
        Paths {
            install: setup.install(),
            dir: setup.dir(),
            apps_key: setup.apps_key.clone(),
        },
        "0.1.0",
        &settings,
        move |notice| {
            if matches!(notice, Notice::Progress(ref progress) if progress.bytes > 0) {
                *seen.lock().unwrap() = true;
            }
        },
    ));
    updater.check(&settings).unwrap();
    let stopper = Arc::clone(&updater);
    let watcher = std::thread::spawn(move || {
        while !*started.lock().unwrap() {
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        stopper.stop();
    });
    let error = updater.download(&settings).unwrap_err();
    watcher.join().unwrap();
    assert!(
        error.message.contains("the download was stopped"),
        "{error}"
    );
    assert!(!setup.dir().join("staging").join("0.2.0").exists());
    assert_eq!(
        updater.check(&settings).unwrap_err().kind,
        ErrorKind::Refused
    );
}
