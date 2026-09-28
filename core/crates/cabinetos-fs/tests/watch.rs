//! `DirectoryWatcher` against real directories in the temp folder.

use std::fs;
use std::sync::mpsc;
use std::time::{Duration, Instant};

use cabinetos_fs::{DirectoryChanged, DirectoryWatcher, FsError};

const WAIT: Duration = Duration::from_secs(2);

fn start(path: &std::path::Path) -> (DirectoryWatcher, mpsc::Receiver<DirectoryChanged>) {
    let (sender, receiver) = mpsc::channel();
    let watcher = DirectoryWatcher::start(
        path.to_str().unwrap(),
        "watch-test".to_owned(),
        move |change| {
            let _ = sender.send(change);
        },
    )
    .unwrap();
    (watcher, receiver)
}

#[test]
fn reports_a_new_file() {
    let dir = tempfile::tempdir().unwrap();
    let (watcher, changes) = start(dir.path());
    fs::write(dir.path().join("new.txt"), b"hello").unwrap();
    assert_eq!(changes.recv_timeout(WAIT), Ok(DirectoryChanged::Changed));
    watcher.stop();
}

#[test]
fn reports_changes_made_right_after_start() {
    // `start` returns only once Windows holds the first request, so a change
    // made immediately afterwards is never missed.
    for _ in 0..20 {
        let dir = tempfile::tempdir().unwrap();
        let (watcher, changes) = start(dir.path());
        fs::create_dir(dir.path().join("sub")).unwrap();
        assert_eq!(changes.recv_timeout(WAIT), Ok(DirectoryChanged::Changed));
        watcher.stop();
    }
}

#[test]
fn stopping_is_prompt_and_quiet() {
    let dir = tempfile::tempdir().unwrap();
    let (watcher, changes) = start(dir.path());
    let started = Instant::now();
    watcher.stop();
    // Generous bounds: they catch a hang, not a slow machine.
    assert!(started.elapsed() < Duration::from_secs(2));
    fs::write(dir.path().join("after-stop.txt"), b"x").unwrap();
    assert!(changes.recv_timeout(Duration::from_millis(200)).is_err());
}

#[test]
fn dropping_does_not_block() {
    let dir = tempfile::tempdir().unwrap();
    let (watcher, _changes) = start(dir.path());
    let started = Instant::now();
    drop(watcher);
    assert!(started.elapsed() < Duration::from_secs(1));
}

#[test]
fn does_not_block_deleting_the_directory() {
    let parent = tempfile::tempdir().unwrap();
    let dir = parent.path().join("watched");
    fs::create_dir(&dir).unwrap();
    let (watcher, changes) = start(&dir);
    fs::remove_dir(&dir).unwrap();
    assert!(!dir.exists());
    // Windows reports the deletion; whatever it says first, it arrives.
    let first = changes.recv_timeout(WAIT).unwrap();
    println!("after deleting the watched directory: {first:?}");
    watcher.stop();
}

#[test]
fn a_missing_directory_cannot_be_watched() {
    let dir = tempfile::tempdir().unwrap();
    let result = DirectoryWatcher::start(
        dir.path().join("missing").to_str().unwrap(),
        "watch-test".to_owned(),
        |_| {},
    );
    assert!(
        matches!(result, Err(FsError::NotFound { .. })),
        "{result:?}"
    );
}

#[test]
fn watches_a_folder_deeper_than_260_characters() {
    let root = std::env::temp_dir().join("cabinetos-fs-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("watch-long")
        .tempdir_in(root)
        .unwrap();
    let mut deep = dir.path().to_path_buf();
    while deep.as_os_str().len() < 300 {
        deep.push("segment-of-a-long-path-0123456789");
    }
    fs::create_dir_all(format!(r"\\?\{}", deep.display())).unwrap();
    let (watcher, changes) = start(&deep);
    fs::write(format!(r"\\?\{}\new.txt", deep.display()), b"deep").unwrap();
    assert_eq!(changes.recv_timeout(WAIT), Ok(DirectoryChanged::Changed));
    watcher.stop();
}

#[test]
fn watching_a_junction_reports_changes_in_what_it_points_to() {
    let root = std::env::temp_dir().join("cabinetos-fs-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("watch-link")
        .tempdir_in(root)
        .unwrap();
    let target = dir.path().join("target");
    fs::create_dir(&target).unwrap();
    let link = dir.path().join("junction");
    let output = std::process::Command::new("cmd")
        .args(["/c", "mklink", "/J"])
        .arg(&link)
        .arg(&target)
        .output()
        .unwrap();
    assert!(output.status.success(), "{output:?}");
    let (watcher, changes) = start(&link);
    fs::write(target.join("new.txt"), b"through the real path").unwrap();
    assert_eq!(changes.recv_timeout(WAIT), Ok(DirectoryChanged::Changed));
    watcher.stop();
}
