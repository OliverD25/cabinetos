//! The job engine on real files. Every test works in its own folder under
//! `%TEMP%\cabinetos-jobs-test\`, which it removes when it ends.

use std::collections::BTreeMap;
use std::fs;
use std::hash::{DefaultHasher, Hasher};
use std::os::windows::fs::{FileTypeExt, MetadataExt};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::mpsc::{self, Receiver};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use cabinetos_jobs::{EngineConfig, EventSink, JobError, JobGate, JobPreview, JobQueueManager};
use cabinetos_protocol::{
    Conflict, ConflictKind, ConflictPolicy, ErrorCode, Event, JobAction, JobKind, JobOptions,
    JobProgress, JobRequest, JobState, JobStep, LinkPolicy, Resolution,
};
use tempfile::TempDir;

/// Long enough for any of these jobs on a loaded machine.
const JOB_DEADLINE: Duration = Duration::from_secs(120);

fn test_root() -> PathBuf {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    root
}

fn scratch(name: &str) -> TempDir {
    tempfile::Builder::new()
        .prefix(name)
        .tempdir_in(test_root())
        .unwrap()
}

struct Engine {
    manager: JobQueueManager,
    events: Mutex<Receiver<(Instant, Event)>>,
}

fn engine() -> Engine {
    engine_with(EngineConfig::default())
}

fn engine_with(config: EngineConfig) -> Engine {
    let (sender, events) = mpsc::channel();
    let sink: EventSink = Arc::new(move |event| {
        let _ = sender.send((Instant::now(), event));
    });
    Engine {
        manager: JobQueueManager::new(config, sink),
        events: Mutex::new(events),
    }
}

impl Engine {
    fn start(
        &self,
        kind: JobKind,
        sources: &[&Path],
        destination: Option<&Path>,
        options: JobOptions,
    ) -> u64 {
        self.manager
            .start(JobRequest {
                kind,
                sources: sources
                    .iter()
                    .map(|path| path.display().to_string())
                    .collect(),
                destination: destination.map(|path| path.display().to_string()),
                options,
            })
            .unwrap()
    }

    /// The next event of `job`, within `deadline`.
    fn next(&self, job: u64, deadline: Instant) -> Option<Event> {
        let events = self.events.lock().unwrap();
        loop {
            let left = deadline.checked_duration_since(Instant::now())?;
            let (_, event) = events.recv_timeout(left).ok()?;
            let of_job = match &event {
                Event::JobProgress(progress) => progress.job_id == job,
                Event::JobConflict(conflict) => conflict.job_id == job,
                Event::JobStateChanged { job_id, .. } => *job_id == job,
                _ => false,
            };
            if of_job {
                return Some(event);
            }
        }
    }

    /// Waits for the job to end; returns its final progress and every
    /// conflict it reported.
    fn finish(&self, job: u64) -> (JobProgress, Vec<Conflict>) {
        let deadline = Instant::now() + JOB_DEADLINE;
        let mut last = None;
        let mut conflicts = Vec::new();
        loop {
            match self.next(job, deadline) {
                Some(Event::JobProgress(progress)) => last = Some(progress),
                Some(Event::JobConflict(conflict)) => conflicts.push(conflict),
                Some(Event::JobStateChanged { state, .. }) if state.is_terminal() => {
                    let last = last.expect("a final progress comes before the final state");
                    assert_eq!(
                        last.state, state,
                        "the final progress carries the final state"
                    );
                    return (last, conflicts);
                }
                Some(_) => {}
                None => panic!("job {job} did not end within {JOB_DEADLINE:?}"),
            }
        }
    }

    /// Collects conflicts until `count` arrived and a progress shows
    /// `done` files handled.
    fn conflicts_and_progress(&self, job: u64, count: usize, done: u64) -> Vec<Conflict> {
        let deadline = Instant::now() + JOB_DEADLINE;
        let mut conflicts = Vec::new();
        let mut reached = false;
        while conflicts.len() < count || !reached {
            match self.next(job, deadline) {
                Some(Event::JobConflict(conflict)) => conflicts.push(conflict),
                Some(Event::JobProgress(progress)) => {
                    assert!(
                        !progress.state.is_terminal(),
                        "the job ended while files waited: {progress:?}"
                    );
                    reached |=
                        progress.files_done >= done && progress.conflicts_open == count as u64;
                }
                Some(Event::JobStateChanged { state, .. }) => {
                    assert!(
                        !state.is_terminal(),
                        "the job ended while files waited: {state:?}"
                    );
                }
                Some(_) => {}
                None => panic!(
                    "expected {count} conflicts and {done} files done in time; got {conflicts:?}"
                ),
            }
        }
        conflicts
    }

    fn progress(&self, job: u64) -> JobProgress {
        self.manager
            .list()
            .into_iter()
            .find(|info| info.progress.job_id == job)
            .expect("the job is listed")
            .progress
    }
}

fn content_hash(path: &Path) -> u64 {
    let mut hasher = DefaultHasher::new();
    hasher.write(&fs::read(path).unwrap());
    hasher.finish()
}

/// Every entry under `root`, by relative path: `dir`, `link -> target`, or
/// `file size hash`.
fn describe(root: &Path) -> BTreeMap<String, String> {
    let mut found = BTreeMap::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(dir) = pending.pop() {
        for entry in fs::read_dir(&dir).unwrap() {
            let path = entry.unwrap().path();
            let relative = path.strip_prefix(root).unwrap().display().to_string();
            let metadata = fs::symlink_metadata(&path).unwrap();
            if metadata.file_type().is_symlink() {
                found.insert(
                    relative,
                    format!("link -> {}", fs::read_link(&path).unwrap().display()),
                );
            } else if metadata.is_dir() {
                found.insert(relative, "dir".to_owned());
                pending.push(path);
            } else {
                found.insert(
                    relative,
                    format!("file {} {:x}", metadata.len(), content_hash(&path)),
                );
            }
        }
    }
    found
}

/// Writes `size` bytes that differ from file to file.
fn write_file(path: &Path, size: usize, seed: u64) {
    let mut data = Vec::with_capacity(size);
    let mut value = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1;
    while data.len() < size {
        value ^= value << 13;
        value ^= value >> 7;
        value ^= value << 17;
        data.extend_from_slice(&value.to_le_bytes());
    }
    data.truncate(size);
    fs::write(path, data).unwrap();
}

fn junction(link: &Path, target: &Path) {
    let status = Command::new("cmd")
        .args(["/C", "mklink", "/J"])
        .arg(link)
        .arg(target)
        .output()
        .unwrap();
    assert!(status.status.success(), "mklink /J failed: {status:?}");
}

fn options(on_conflict: ConflictPolicy) -> JobOptions {
    JobOptions {
        on_conflict,
        ..JobOptions::default()
    }
}

#[test]
fn copies_a_tree_with_empty_folders_a_big_file_a_junction_and_a_long_path() {
    let dir = scratch("tree");
    let source = dir.path().join("src");
    let destination = dir.path().join("dst");
    for group in 0..20 {
        let folder = source.join(format!("group {group}")).join("inner");
        fs::create_dir_all(&folder).unwrap();
        for number in 0..100 {
            let seed = group * 100 + number;
            write_file(
                &folder.join(format!("file {number}.txt")),
                1 + usize::try_from(seed * 37 % 4096).unwrap(),
                seed,
            );
        }
    }
    for empty in ["empty a", "empty b\\nested empty"] {
        fs::create_dir_all(source.join(empty)).unwrap();
    }
    write_file(&source.join("big.bin"), 64 * 1024 * 1024, 7);
    junction(&source.join("shortcut"), &source.join("group 3"));
    let mut deep = source.join("deep");
    while deep.as_os_str().len() < 300 {
        deep = deep.join("a folder with a long name");
    }
    fs::create_dir_all(&deep).unwrap();
    write_file(&deep.join("far away.txt"), 1000, 99);
    assert!(deep.join("far away.txt").as_os_str().len() > 260);

    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert_eq!(last.files_done, last.files_total);
    assert_eq!(last.bytes_done, last.bytes_total);
    assert!(last.files_total > 2000, "{last:?}");

    let copy = destination.join("src");
    assert_eq!(
        describe(&source),
        describe(&copy),
        "the copy equals the source"
    );
    for relative in ["big.bin", "group 7\\inner\\file 42.txt", "empty a"] {
        let (original, copied) = (
            fs::metadata(source.join(relative)).unwrap(),
            fs::metadata(copy.join(relative)).unwrap(),
        );
        assert_eq!(
            copied.last_write_time(),
            original.last_write_time(),
            "{relative}"
        );
        assert_eq!(
            copied.creation_time(),
            original.creation_time(),
            "{relative}"
        );
    }
}

/// A tree of the fixture's names beyond ASCII (docs/ui.md, "Edge cases"):
/// folders, files, café composed and decomposed side by side, and a name
/// of 255 UTF-16 units.
fn names_tree(root: &Path) {
    let files = [
        "Звіт 2026.txt",
        "Їжак і Єнот.md",
        "日本語のファイル.txt",
        "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
        "caf\u{e9}.txt",
        "cafe\u{301}.txt",
        "مستند.txt",
    ];
    for folder in ["", "Ґанок", "中文文件夹", "📁 photos"] {
        let folder = root.join(folder);
        fs::create_dir_all(&folder).unwrap();
        for (seed, file) in (1..).zip(files) {
            write_file(
                &folder.join(file),
                100 + usize::try_from(seed).unwrap(),
                seed,
            );
        }
    }
    write_file(&root.join("a".repeat(251) + ".txt"), 255, 255);
}

#[test]
fn names_beyond_ascii_are_copied_moved_and_deleted() {
    let dir = scratch("names");
    let source = dir.path().join("Звіт і фото");
    names_tree(&source);
    let before = describe(&source);
    assert_eq!(before.len(), 3 + 4 * 7 + 1);
    let engine = engine();

    let copies = dir.path().join("копії");
    fs::create_dir(&copies).unwrap();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&copies),
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    let copy = copies.join("Звіт і фото");
    assert_eq!(describe(&copy), before);

    let moved_into = dir.path().join("中文文件夹 📁");
    fs::create_dir(&moved_into).unwrap();
    let job = engine.start(
        JobKind::Move,
        &[&copy],
        Some(&moved_into),
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert!(!copy.exists());
    let moved = moved_into.join("Звіт і фото");
    assert_eq!(describe(&moved), before);

    let job = engine.start(
        JobKind::Delete { permanent: true },
        &[&moved],
        None,
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert!(!moved.exists());
    assert_eq!(describe(&source), before, "the source is untouched");
}

#[test]
fn a_case_only_twin_is_a_conflict_that_names_the_file_there() {
    let dir = scratch("case");
    let (source, destination) = (dir.path().join("src"), dir.path().join("dst"));
    fs::create_dir_all(&source).unwrap();
    fs::create_dir_all(&destination).unwrap();
    let names = ["Report.txt", "ЗВІТ.txt"];
    let there = ["report.txt", "звіт.txt"];
    for (name, existing) in names.iter().zip(there) {
        fs::write(source.join(name), "new").unwrap();
        fs::write(destination.join(existing), "old").unwrap();
    }
    let engine = engine();
    let listed = |folder: &Path| {
        let mut names: Vec<String> = fs::read_dir(folder)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().into_string().unwrap())
            .collect();
        names.sort();
        names
    };

    // A copy: the folder is case-insensitive, so each name is taken.
    let sources: Vec<PathBuf> = names.iter().map(|name| source.join(name)).collect();
    let sources: Vec<&Path> = sources.iter().map(PathBuf::as_path).collect();
    let job = engine.start(
        JobKind::Copy,
        &sources,
        Some(&destination),
        JobOptions::default(),
    );
    let mut conflicts = engine.conflicts_and_progress(job, 2, 0);
    conflicts.sort_by(|a, b| a.source.cmp(&b.source));
    for (conflict, existing) in conflicts.iter().zip(there) {
        assert!(
            matches!(conflict.kind, ConflictKind::FileExists { dest_size: 3, .. }),
            "{conflict:?}"
        );
        // The card names the file that is there, as the folder spells it.
        assert_eq!(
            conflict.destination.as_deref(),
            Some(destination.join(existing).display().to_string().as_str()),
            "{conflict:?}"
        );
    }
    // Keep both: the new name is free in any case. Overwrite: the file
    // there keeps its spelling and takes the new content.
    engine
        .manager
        .resolve(
            job,
            conflicts[0].conflict_id,
            &Resolution::Rename { new_name: None },
            false,
        )
        .unwrap();
    engine
        .manager
        .resolve(job, conflicts[1].conflict_id, &Resolution::Overwrite, false)
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert_eq!(
        listed(&destination),
        ["Report (2).txt", "report.txt", "звіт.txt"]
    );
    let read = |name: &str| fs::read_to_string(destination.join(name)).unwrap();
    assert_eq!(read("Report (2).txt"), "new");
    assert_eq!(read("report.txt"), "old");
    assert_eq!(read("звіт.txt"), "new");

    // A move on one volume that overwrites is a rename: the moved file's
    // spelling replaces the one there.
    let job = engine.start(
        JobKind::Move,
        &[&source.join("Report.txt")],
        Some(&destination),
        options(ConflictPolicy::Overwrite),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert_eq!(
        listed(&destination),
        ["Report (2).txt", "Report.txt", "звіт.txt"]
    );
    assert_eq!(read("Report.txt"), "new");
}

#[test]
fn a_file_that_exists_waits_while_the_rest_completes() {
    let dir = scratch("conflicts");
    let (source, destination) = (dir.path().join("src"), dir.path().join("dst"));
    fs::create_dir_all(source.join("sub")).unwrap();
    fs::create_dir_all(destination.join("src").join("sub")).unwrap();
    for number in 0..50 {
        write_file(
            &source.join("sub").join(format!("{number}.txt")),
            100 + number,
            number as u64,
        );
    }
    for number in 0..5 {
        fs::write(
            destination
                .join("src")
                .join("sub")
                .join(format!("{number}.txt")),
            "old",
        )
        .unwrap();
    }

    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        JobOptions::default(),
    );
    // 50 files and 2 folders; 45 files and the folders go through.
    let mut conflicts = engine.conflicts_and_progress(job, 5, 47);
    assert!(
        conflicts
            .iter()
            .all(|conflict| matches!(conflict.kind, ConflictKind::FileExists { dest_size: 3, .. }))
    );
    assert_eq!(engine.progress(job).state, JobState::Running);
    assert_eq!(engine.manager.open_conflicts().len(), 5);

    conflicts.sort_by(|a, b| a.source.cmp(&b.source));
    let decisions = [
        Resolution::Overwrite,
        Resolution::Overwrite,
        Resolution::Skip,
        Resolution::Skip,
        Resolution::Rename { new_name: None },
    ];
    for (conflict, decision) in conflicts.iter().zip(&decisions) {
        engine
            .manager
            .resolve(job, conflict.conflict_id, decision, false)
            .unwrap();
    }
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert_eq!(last.files_skipped, 2);
    assert_eq!(last.files_done, last.files_total);

    let target = |name: &str| destination.join("src").join("sub").join(name);
    let names: Vec<String> = conflicts
        .iter()
        .map(|conflict| {
            Path::new(&conflict.source)
                .file_name()
                .unwrap()
                .to_string_lossy()
                .into_owned()
        })
        .collect();
    for name in &names[..2] {
        assert_eq!(
            content_hash(&target(name)),
            content_hash(&source.join("sub").join(name)),
            "overwritten"
        );
    }
    for name in &names[2..4] {
        assert_eq!(fs::read_to_string(target(name)).unwrap(), "old", "skipped");
    }
    let renamed = names[4].replace(".txt", " (2).txt");
    assert_eq!(
        content_hash(&target(&renamed)),
        content_hash(&source.join("sub").join(&names[4]))
    );
    assert_eq!(fs::read_to_string(target(&names[4])).unwrap(), "old");
    assert!(matches!(
        engine
            .manager
            .resolve(job, conflicts[0].conflict_id, &Resolution::Skip, false),
        Err(JobError {
            code: ErrorCode::NoSuchConflict,
            ..
        })
    ));
}

#[test]
fn skip_and_overwrite_policies_decide_without_asking() {
    for policy in [ConflictPolicy::Skip, ConflictPolicy::Overwrite] {
        let dir = scratch("policy");
        let (source, destination) = (dir.path().join("src"), dir.path().join("dst"));
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(destination.join("src")).unwrap();
        for number in 0..20 {
            write_file(&source.join(format!("{number}.txt")), 50, number);
        }
        for number in 0..5 {
            fs::write(destination.join("src").join(format!("{number}.txt")), "old").unwrap();
        }
        let engine = engine();
        let job = engine.start(
            JobKind::Copy,
            &[&source],
            Some(&destination),
            options(policy),
        );
        let (last, conflicts) = engine.finish(job);
        assert_eq!(last.state, JobState::Completed);
        assert!(conflicts.is_empty(), "{policy:?}: {conflicts:?}");
        let kept = fs::read_to_string(destination.join("src").join("3.txt")).unwrap_or_default();
        if policy == ConflictPolicy::Skip {
            assert_eq!(last.files_skipped, 5);
            assert_eq!(kept, "old");
        } else {
            assert_eq!(last.files_skipped, 0);
            assert_eq!(
                content_hash(&destination.join("src").join("3.txt")),
                content_hash(&source.join("3.txt"))
            );
        }
    }
}

#[test]
fn a_read_only_destination_is_access_denied_until_overwrite() {
    let dir = scratch("readonly");
    let (source, destination) = (dir.path().join("a.txt"), dir.path().join("dst"));
    write_file(&source, 64, 1);
    fs::create_dir_all(&destination).unwrap();
    let existing = destination.join("a.txt");
    fs::write(&existing, "old").unwrap();
    let mut permissions = fs::metadata(&existing).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&existing, permissions).unwrap();

    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        options(ConflictPolicy::Overwrite),
    );
    let conflicts = engine.conflicts_and_progress(job, 1, 0);
    assert_eq!(conflicts[0].kind, ConflictKind::AccessDenied);
    engine
        .manager
        .resolve(job, conflicts[0].conflict_id, &Resolution::Overwrite, false)
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed);
    assert_eq!(content_hash(&existing), content_hash(&source));
}

/// Starts a copy of `big` and pauses it once bytes move. `None` if the copy
/// was over before the pause could land (a very fast disk); the caller
/// tries again.
fn paused_copy(engine: &Engine, big: &Path, destination: &Path) -> Option<u64> {
    let job = engine.start(
        JobKind::Copy,
        &[big],
        Some(destination),
        JobOptions::default(),
    );
    let deadline = Instant::now() + JOB_DEADLINE;
    while engine.progress(job).bytes_done == 0 {
        assert!(Instant::now() < deadline, "no bytes moved");
        std::thread::sleep(Duration::from_millis(1));
    }
    engine.manager.control(job, JobAction::Pause).unwrap();
    // The chunk in flight lands; the next one waits.
    std::thread::sleep(Duration::from_millis(200));
    let progress = engine.progress(job);
    if progress.state.is_terminal() || progress.bytes_done == progress.bytes_total {
        engine.finish(job);
        return None;
    }
    assert_eq!(progress.state, JobState::Paused);
    Some(job)
}

#[test]
fn pause_stops_the_bytes_and_resume_finishes_the_copy() {
    let dir = scratch("pause");
    let big = dir.path().join("big.bin");
    write_file(&big, 256 * 1024 * 1024, 3);
    let engine = engine();
    for attempt in 0..3 {
        let destination = dir.path().join(format!("dst{attempt}"));
        let Some(job) = paused_copy(&engine, &big, &destination) else {
            continue;
        };
        let before = engine.progress(job).bytes_done;
        std::thread::sleep(Duration::from_millis(500));
        assert_eq!(
            engine.progress(job).bytes_done,
            before,
            "no bytes move while paused"
        );
        engine.manager.control(job, JobAction::Resume).unwrap();
        let (last, _) = engine.finish(job);
        assert_eq!(last.state, JobState::Completed);
        assert_eq!(
            content_hash(&destination.join("big.bin")),
            content_hash(&big)
        );
        return;
    }
    panic!("the copy finished before a pause could land, three times");
}

#[test]
fn cancel_removes_the_partial_copy() {
    let dir = scratch("cancel");
    let big = dir.path().join("big.bin");
    write_file(&big, 256 * 1024 * 1024, 4);
    let engine = engine();
    for attempt in 0..3 {
        let destination = dir.path().join(format!("dst{attempt}"));
        let Some(job) = paused_copy(&engine, &big, &destination) else {
            continue;
        };
        assert!(
            destination.join("big.bin").exists(),
            "the partial copy is there while paused"
        );
        engine.manager.control(job, JobAction::Cancel).unwrap();
        let (last, _) = engine.finish(job);
        assert_eq!(last.state, JobState::Cancelled);
        assert!(
            !destination.join("big.bin").exists(),
            "the partial copy is gone"
        );
        return;
    }
    panic!("the copy finished before a pause could land, three times");
}

/// File IDs (NTFS file reference numbers) of the files in `dir`, by name.
fn file_ids(dir: &Path) -> BTreeMap<String, u64> {
    let listing =
        cabinetos_fs::list_directory(dir.to_str().unwrap(), &cabinetos_fs::ListOptions::default())
            .unwrap();
    listing
        .entries()
        .iter()
        .map(|entry| (listing.name_string(entry), entry.id))
        .collect()
}

#[test]
fn a_move_on_one_volume_is_a_rename() {
    let dir = scratch("rename");
    let (source, destination) = (dir.path().join("src"), dir.path().join("dst"));
    fs::create_dir_all(&source).unwrap();
    fs::create_dir_all(&destination).unwrap();
    for number in 0..1000 {
        fs::write(source.join(format!("{number}.txt")), number.to_string()).unwrap();
    }
    let before = file_ids(&source);
    let engine = engine();
    let job = engine.start(
        JobKind::Move,
        &[&source],
        Some(&destination),
        JobOptions::default(),
    );
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed);
    assert!(!source.exists());
    // Same file IDs: the files were renamed, not copied. The time is only a
    // sanity bound: under 50 ms when the machine is idle, but a loaded one
    // (a full workspace test run) took longer, so it allows a second.
    assert_eq!(file_ids(&destination.join("src")), before);
    assert!(
        last.elapsed_ms < 1_000,
        "a rename takes moments, not {} ms",
        last.elapsed_ms
    );
    assert_eq!(last.bytes_total, 0, "a rename counts files, not bytes");
}

#[test]
fn a_move_into_a_folder_that_exists_merges_file_by_file() {
    let dir = scratch("merge");
    let (source, destination) = (dir.path().join("src"), dir.path().join("dst"));
    fs::create_dir_all(source.join("sub")).unwrap();
    fs::create_dir_all(destination.join("src").join("sub")).unwrap();
    fs::write(source.join("sub").join("new.txt"), "new").unwrap();
    fs::write(source.join("both.txt"), "from source").unwrap();
    fs::write(destination.join("src").join("both.txt"), "old").unwrap();
    let engine = engine();
    let job = engine.start(
        JobKind::Move,
        &[&source],
        Some(&destination),
        options(ConflictPolicy::Overwrite),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{conflicts:?}");
    assert_eq!(
        fs::read_to_string(destination.join("src").join("both.txt")).unwrap(),
        "from source"
    );
    assert_eq!(
        fs::read_to_string(destination.join("src").join("sub").join("new.txt")).unwrap(),
        "new"
    );
    assert!(!source.exists(), "the emptied source folders are gone");
}

#[test]
#[ignore = "needs a second volume: set CABINETOS_TEST_SECOND_VOLUME to a folder on another volume (allowed: under E:\\cabinetos-scratch\\) and run with --ignored"]
fn a_move_across_volumes_copies_then_deletes() {
    let second = PathBuf::from(
        std::env::var("CABINETOS_TEST_SECOND_VOLUME")
            .expect("CABINETOS_TEST_SECOND_VOLUME is not set"),
    );
    fs::create_dir_all(&second).unwrap();
    let there = tempfile::Builder::new()
        .prefix("cross")
        .tempdir_in(&second)
        .unwrap();
    let dir = scratch("cross");
    let source = dir.path().join("src");
    fs::create_dir_all(source.join("sub")).unwrap();
    for number in 0..200 {
        write_file(
            &source.join("sub").join(format!("{number}.bin")),
            10_000 + number,
            number as u64,
        );
    }
    let read_only = source.join("locked.txt");
    fs::write(&read_only, "read only").unwrap();
    let mut permissions = fs::metadata(&read_only).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&read_only, permissions).unwrap();
    let expected = describe(&source);

    let engine = engine();
    let options = JobOptions {
        verify: true,
        ..JobOptions::default()
    };
    let job = engine.start(JobKind::Move, &[&source], Some(there.path()), options);
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{conflicts:?}");
    assert!(
        last.bytes_total > 2_000_000,
        "a move across volumes counts the copied bytes"
    );
    assert_eq!(describe(&there.path().join("src")), expected);
    assert!(!source.exists(), "the sources are gone after their copies");
}

#[test]
fn a_permanent_delete_removes_a_tree_but_asks_for_read_only_files() {
    let dir = scratch("delete");
    let tree = dir.path().join("tree");
    let outside = dir.path().join("outside");
    fs::create_dir_all(tree.join("a").join("b")).unwrap();
    fs::create_dir_all(&outside).unwrap();
    fs::write(outside.join("keep.txt"), "keep").unwrap();
    for number in 0..30 {
        fs::write(tree.join("a").join(format!("{number}.txt")), "x").unwrap();
    }
    let read_only = tree.join("a").join("b").join("locked.txt");
    fs::write(&read_only, "locked").unwrap();
    let mut permissions = fs::metadata(&read_only).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&read_only, permissions).unwrap();
    // A junction out of the tree: deleting the tree removes the link only.
    junction(&tree.join("to outside"), &outside);

    let engine = engine();
    let job = engine.start(
        JobKind::Delete { permanent: true },
        &[&tree],
        None,
        JobOptions::default(),
    );
    let conflicts = engine.conflicts_and_progress(job, 1, 31);
    assert_eq!(conflicts[0].kind, ConflictKind::AccessDenied);
    assert_eq!(Path::new(&conflicts[0].source), read_only);
    engine
        .manager
        .resolve(job, conflicts[0].conflict_id, &Resolution::Overwrite, false)
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(!tree.exists(), "the tree is gone");
    assert_eq!(
        fs::read_to_string(outside.join("keep.txt")).unwrap(),
        "keep",
        "the link was not followed"
    );
}

#[test]
fn the_recycle_bin_takes_a_file() {
    // This puts a file into the Recycle Bin of the machine running the
    // test, so it runs only where that is fine (CI sets the variable).
    if std::env::var_os("CABINETOS_TEST_RECYCLE_BIN").is_none_or(|value| value != "1") {
        eprintln!(
            "skipped: set CABINETOS_TEST_RECYCLE_BIN=1 to send a test file to the Recycle Bin"
        );
        return;
    }
    let dir = scratch("recycle");
    let file = dir.path().join("cabinetos recycle test.txt");
    fs::write(&file, "to the bin").unwrap();
    let engine = engine();
    let job = engine.start(
        JobKind::Delete { permanent: false },
        &[&file],
        None,
        JobOptions::default(),
    );
    // An account without a Recycle Bin (possible on a CI runner) gets the
    // conflict instead of a silent permanent delete.
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        match engine.next(job, deadline) {
            Some(Event::JobConflict(conflict)) => {
                assert!(
                    matches!(conflict.kind, ConflictKind::RecycleBinTooSmall { size: 10 }),
                    "{conflict:?}"
                );
                eprintln!(
                    "this account has no usable Recycle Bin here; deleting for good as decided"
                );
                engine
                    .manager
                    .resolve(
                        job,
                        conflict.conflict_id,
                        &Resolution::DeletePermanently,
                        false,
                    )
                    .unwrap();
            }
            Some(Event::JobStateChanged { state, .. }) if state.is_terminal() => {
                assert_eq!(state, JobState::Completed);
                break;
            }
            Some(_) => {}
            None => panic!("the delete did not end"),
        }
    }
    assert!(!file.exists());
}

#[test]
fn the_recycle_bin_takes_names_beyond_ascii() {
    // Puts files into the Recycle Bin of the machine running the test: CI
    // only, as above.
    if std::env::var_os("CABINETOS_TEST_RECYCLE_BIN").is_none_or(|value| value != "1") {
        eprintln!(
            "skipped: set CABINETOS_TEST_RECYCLE_BIN=1 to send test files to the Recycle Bin"
        );
        return;
    }
    let dir = scratch("recycle-names");
    let folder = dir.path().join("Ґанок 📁");
    fs::create_dir(&folder).unwrap();
    let files = ["Звіт 2026.txt", "cafe\u{301}.txt", "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt"];
    for file in files {
        fs::write(folder.join(file), "to the bin").unwrap();
    }
    let paths: Vec<PathBuf> = files.iter().map(|file| folder.join(file)).collect();
    let paths: Vec<&Path> = paths.iter().map(PathBuf::as_path).collect();
    let engine = engine();
    let job = engine.start(
        JobKind::Delete { permanent: false },
        &paths,
        None,
        JobOptions::default(),
    );
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        match engine.next(job, deadline) {
            Some(Event::JobConflict(conflict)) => {
                assert!(
                    matches!(conflict.kind, ConflictKind::RecycleBinTooSmall { .. }),
                    "{conflict:?}"
                );
                eprintln!("this account has no usable Recycle Bin here");
                engine
                    .manager
                    .resolve(
                        job,
                        conflict.conflict_id,
                        &Resolution::DeletePermanently,
                        false,
                    )
                    .unwrap();
            }
            Some(Event::JobStateChanged { state, .. }) if state.is_terminal() => {
                assert_eq!(state, JobState::Completed);
                break;
            }
            Some(_) => {}
            None => panic!("the delete did not end"),
        }
    }
    for path in paths {
        assert!(!path.exists(), "{}", path.display());
    }
}

/// A folder under `parent` whose path is at least `length` characters,
/// made with the verbatim (`\\?\`) form.
fn deep_folder(parent: &Path, length: usize) -> PathBuf {
    let mut deep = parent.to_path_buf();
    while deep.as_os_str().len() < length {
        deep.push("segment-of-a-long-path-0123456789");
    }
    fs::create_dir_all(format!(r"\\?\{}", deep.display())).unwrap();
    deep
}

#[test]
fn a_tree_deeper_than_260_characters_is_copied_moved_and_deleted() {
    let dir = scratch("long");
    let source = dir.path().join("src");
    let deep = deep_folder(&source, 300);
    write_file(&deep.join("far away.txt"), 1000, 1);
    write_file(&deep.join("Звіт 2026.txt"), 2000, 2);
    assert!(deep.join("far away.txt").as_os_str().len() > 300);
    let before = describe(&source);
    let engine = engine();

    // Into a destination that is itself deeper than 260 characters, where
    // one file is in the way: the conflict names it, keep both works there.
    let destination = deep_folder(&dir.path().join("dst"), 280);
    let copied_deep = destination.join(deep.strip_prefix(dir.path()).unwrap());
    fs::create_dir_all(format!(r"\\?\{}", copied_deep.display())).unwrap();
    fs::write(
        format!(r"\\?\{}", copied_deep.join("far away.txt").display()),
        "old",
    )
    .unwrap();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        JobOptions::default(),
    );
    let conflicts = engine.conflicts_and_progress(job, 1, 1);
    assert_eq!(
        conflicts[0].destination.as_deref(),
        Some(
            copied_deep
                .join("far away.txt")
                .display()
                .to_string()
                .as_str()
        )
    );
    assert!(
        !conflicts[0]
            .destination
            .as_deref()
            .unwrap()
            .starts_with(r"\\?\")
    );
    engine
        .manager
        .resolve(
            job,
            conflicts[0].conflict_id,
            &Resolution::Rename { new_name: None },
            false,
        )
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert_eq!(
        content_hash(&copied_deep.join("far away (2).txt")),
        content_hash(&deep.join("far away.txt"))
    );
    assert_eq!(
        fs::read_to_string(copied_deep.join("far away.txt")).unwrap(),
        "old"
    );

    // A move on one volume is a rename, whatever the depth.
    let moved_into = dir.path().join("moved");
    fs::create_dir(&moved_into).unwrap();
    let job = engine.start(
        JobKind::Move,
        &[&source],
        Some(&moved_into),
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert!(!source.exists());
    assert_eq!(describe(&moved_into.join("src")), before);

    let job = engine.start(
        JobKind::Delete { permanent: true },
        &[&moved_into.join("src"), &dir.path().join("dst")],
        None,
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    assert!(!moved_into.join("src").exists());
    assert!(!dir.path().join("dst").exists());
}

#[test]
fn a_path_too_long_for_the_recycle_bin_waits_for_an_explicit_decision() {
    // The shell's Recycle Bin takes paths of up to 259 characters and,
    // with the silent flags, may delete a longer one for good instead.
    // Nothing here reaches it: the check comes before the shell (and the
    // bin is made 0 bytes, so before the check existed the capacity
    // conflict came first), skip keeps the files, and a permanent delete
    // uses DeleteFileW.
    let dir = scratch("recycle-long");
    let folder = dir.path().join("deep tree");
    let deep = deep_folder(&folder, 300);
    write_file(&deep.join("far away.txt"), 10, 1);
    let long_file = deep.join("far away.txt");
    let engine = engine_with(EngineConfig {
        recycle_bin_capacity: Some(0),
        ..EngineConfig::default()
    });
    let recycle = |engine: &Engine, path: &Path| {
        engine.start(
            JobKind::Delete { permanent: false },
            &[path],
            None,
            JobOptions::default(),
        )
    };

    for path in [&long_file, &folder] {
        let job = recycle(&engine, path);
        let conflicts = engine.conflicts_and_progress(job, 1, 0);
        assert_eq!(
            conflicts[0].kind,
            ConflictKind::PathTooLong,
            "{conflicts:?}"
        );
        assert_eq!(conflicts[0].destination, None);
        assert!(long_file.exists(), "nothing is deleted before the decision");
        engine
            .manager
            .resolve(job, conflicts[0].conflict_id, &Resolution::Skip, false)
            .unwrap();
        let (last, _) = engine.finish(job);
        assert_eq!((last.state, last.files_skipped), (JobState::Completed, 1));
        assert!(long_file.exists(), "skip keeps it");
    }

    let job = recycle(&engine, &folder);
    let conflicts = engine.conflicts_and_progress(job, 1, 0);
    engine
        .manager
        .resolve(
            job,
            conflicts[0].conflict_id,
            &Resolution::DeletePermanently,
            false,
        )
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(!folder.exists(), "deleted for good, as decided");
}

/// A symbolic link, or `false` when this user may not make one (Windows
/// asks for Developer Mode or an elevated process).
fn symbolic_link(link: &Path, target: &Path, folder: bool) -> bool {
    let made = if folder {
        std::os::windows::fs::symlink_dir(target, link)
    } else {
        std::os::windows::fs::symlink_file(target, link)
    };
    match made {
        Ok(()) => true,
        Err(error) => {
            eprintln!("skipped a symbolic link: {error}");
            false
        }
    }
}

/// A folder with three files, the kind a link must never take with it.
fn precious(folder: &Path) -> BTreeMap<String, String> {
    fs::create_dir_all(folder.join("inner")).unwrap();
    for (number, name) in (1..).zip(["one.txt", "two.txt", r"inner\three.txt"]) {
        write_file(&folder.join(name), 100, number);
    }
    describe(folder)
}

#[test]
fn deleting_a_link_removes_the_link_and_never_what_it_points_to() {
    let dir = scratch("delete-link");
    let target = dir.path().join("target");
    let before = precious(&target);
    let links = dir.path().join("links");
    fs::create_dir(&links).unwrap();
    junction(&links.join("junction"), &target);
    let mut made = vec![links.join("junction")];
    if symbolic_link(&links.join("folder symlink"), &target, true) {
        made.push(links.join("folder symlink"));
    }
    if symbolic_link(
        &links.join("file symlink.txt"),
        &target.join("one.txt"),
        false,
    ) {
        made.push(links.join("file symlink.txt"));
    }
    let engine = engine();
    for link in &made {
        let job = engine.start(
            JobKind::Delete { permanent: true },
            &[link],
            None,
            JobOptions::default(),
        );
        let (last, conflicts) = engine.finish(job);
        assert_eq!(
            last.state,
            JobState::Completed,
            "{}: {last:?}",
            link.display()
        );
        assert!(conflicts.is_empty(), "{conflicts:?}");
        assert!(
            fs::symlink_metadata(link).is_err(),
            "{} is gone",
            link.display()
        );
        assert_eq!(
            describe(&target),
            before,
            "{} took nothing with it",
            link.display()
        );
    }
}

#[test]
fn a_link_given_as_the_source_is_copied_as_a_link_or_followed_and_moved_as_a_link() {
    let dir = scratch("copy-link");
    let target = dir.path().join("target");
    let before = precious(&target);
    let link = dir.path().join("junction");
    junction(&link, &target);
    let engine = engine();

    // as_link, the default: the copy points where the original points.
    let as_link = dir.path().join("as link");
    let job = engine.start(
        JobKind::Copy,
        &[&link],
        Some(&as_link),
        JobOptions::default(),
    );
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    let copied = as_link.join("junction");
    assert!(
        fs::symlink_metadata(&copied)
            .unwrap()
            .file_type()
            .is_symlink_dir()
    );
    assert_eq!(
        fs::read_link(&copied).unwrap(),
        fs::read_link(&link).unwrap()
    );
    assert_eq!(describe(&copied), before, "it leads to the same files");

    // follow_target: what it points to is copied, as Explorer does.
    let followed = dir.path().join("followed");
    let job = engine.start(
        JobKind::Copy,
        &[&link],
        Some(&followed),
        JobOptions {
            copy_links: LinkPolicy::FollowTarget,
            ..JobOptions::default()
        },
    );
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    let real = followed.join("junction");
    assert!(
        !fs::symlink_metadata(&real)
            .unwrap()
            .file_type()
            .is_symlink()
    );
    assert_eq!(describe(&real), before);

    // A move on one volume renames the link; what it points to stays put.
    let moved_into = dir.path().join("moved");
    fs::create_dir(&moved_into).unwrap();
    let job = engine.start(
        JobKind::Move,
        &[&link],
        Some(&moved_into),
        JobOptions::default(),
    );
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(fs::symlink_metadata(&link).is_err());
    assert!(
        fs::symlink_metadata(moved_into.join("junction"))
            .unwrap()
            .file_type()
            .is_symlink_dir()
    );
    assert_eq!(describe(&target), before, "the target did not move");
}

#[test]
fn a_symbolic_link_to_a_file_is_copied_as_a_link_wherever_this_user_may_make_one() {
    // Developer Mode lets a user make symbolic links without the privilege
    // that CopyFileExW's COPY_FILE_COPY_SYMLINK asks for; the copy must
    // work wherever making the link works.
    let dir = scratch("copy-file-link");
    let target = dir.path().join("kept.txt");
    fs::write(&target, "kept").unwrap();
    let absolute = dir.path().join("absolute.txt");
    if !symbolic_link(&absolute, &target, false) {
        return;
    }
    let relative = dir.path().join("relative.txt");
    std::os::windows::fs::symlink_file("kept.txt", &relative).unwrap();
    let destination = dir.path().join("dst");
    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&absolute, &relative],
        Some(&destination),
        JobOptions::default(),
    );
    let deadline = Instant::now() + JOB_DEADLINE;
    loop {
        match engine.next(job, deadline) {
            Some(Event::JobConflict(conflict)) => panic!("{conflict:?}"),
            Some(Event::JobStateChanged { state, .. }) if state.is_terminal() => {
                assert_eq!(state, JobState::Completed);
                break;
            }
            Some(_) => {}
            None => panic!("the copy did not end"),
        }
    }
    for name in ["absolute.txt", "relative.txt"] {
        let copy = destination.join(name);
        assert!(
            fs::symlink_metadata(&copy)
                .unwrap()
                .file_type()
                .is_symlink_file(),
            "{name} is a link"
        );
        assert_eq!(
            fs::read_link(&copy).unwrap(),
            fs::read_link(dir.path().join(name)).unwrap(),
            "{name} points where the original points"
        );
    }
    assert_eq!(fs::read_to_string(&target).unwrap(), "kept");
}

#[test]
fn a_copy_of_a_file_whose_data_is_elsewhere_reads_it_and_is_here() {
    // A copy reads the whole file, so a cloud file is downloaded by it (the
    // sync provider fetches the data when CopyFileExW reads it). That is
    // what the user asked for, and the copy is an ordinary file on this
    // disk. The offline attribute stands in for the cloud ones, which only
    // a sync provider can set.
    const OFFLINE: u32 = 0x1000;
    let dir = scratch("copy-offline");
    let source = dir.path().join("elsewhere.bin");
    write_file(&source, 64 * 1024, 7);
    let status = Command::new("attrib")
        .arg("+O")
        .arg(&source)
        .status()
        .unwrap();
    assert!(status.success());
    assert_ne!(
        fs::metadata(&source).unwrap().file_attributes() & OFFLINE,
        0
    );
    let destination = dir.path().join("dst");
    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    let copy = destination.join("elsewhere.bin");
    assert_eq!(content_hash(&copy), content_hash(&source));
    assert_eq!(
        fs::metadata(&copy).unwrap().file_attributes() & OFFLINE,
        0,
        "the copy's data is on this disk"
    );
}

#[test]
fn following_links_stops_at_a_link_back_into_the_copy() {
    let dir = scratch("follow-loop");
    let source = dir.path().join("src");
    fs::create_dir_all(source.join("sub")).unwrap();
    write_file(&source.join("sub").join("file.txt"), 10, 1);
    junction(&source.join("sub").join("back to src"), &source);
    let destination = dir.path().join("dst");
    let engine = engine();
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&destination),
        JobOptions {
            copy_links: LinkPolicy::FollowTarget,
            ..JobOptions::default()
        },
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(conflicts.is_empty(), "{conflicts:?}");
    // The loop is copied as a link, not followed forever.
    let back = destination.join("src").join("sub").join("back to src");
    assert!(
        fs::symlink_metadata(&back)
            .unwrap()
            .file_type()
            .is_symlink_dir()
    );
    assert!(
        destination
            .join("src")
            .join("sub")
            .join("file.txt")
            .is_file()
    );
}

#[test]
fn the_recycle_bin_takes_a_junction_and_not_what_it_points_to() {
    // Puts a junction into the Recycle Bin of the machine running the
    // test: CI only, as above.
    if std::env::var_os("CABINETOS_TEST_RECYCLE_BIN").is_none_or(|value| value != "1") {
        eprintln!(
            "skipped: set CABINETOS_TEST_RECYCLE_BIN=1 to send a test junction to the Recycle Bin"
        );
        return;
    }
    let dir = scratch("recycle-link");
    let target = dir.path().join("target");
    let before = precious(&target);
    let link = dir.path().join("junction");
    junction(&link, &target);
    let engine = engine();
    let job = engine.start(
        JobKind::Delete { permanent: false },
        &[&link],
        None,
        JobOptions::default(),
    );
    let (last, conflicts) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?} {conflicts:?}");
    assert!(fs::symlink_metadata(&link).is_err());
    assert_eq!(describe(&target), before);
}

#[test]
fn a_file_too_big_for_the_recycle_bin_waits_for_an_explicit_decision() {
    // The bin is made 1 MiB; nothing here reaches the real Recycle Bin: the
    // check comes before the shell, skip keeps the file, and a permanent
    // delete uses DeleteFileW.
    let dir = scratch("toobig");
    let big = dir.path().join("big.bin");
    write_file(&big, 2 * 1024 * 1024, 1);
    let engine = engine_with(EngineConfig {
        recycle_bin_capacity: Some(1024 * 1024),
        ..EngineConfig::default()
    });
    let recycle = |engine: &Engine| {
        engine.start(
            JobKind::Delete { permanent: false },
            &[&big],
            None,
            JobOptions::default(),
        )
    };

    let job = recycle(&engine);
    let conflicts = engine.conflicts_and_progress(job, 1, 0);
    assert_eq!(
        conflicts[0].kind,
        ConflictKind::RecycleBinTooSmall {
            size: 2 * 1024 * 1024
        }
    );
    assert!(big.exists(), "nothing is deleted before the decision");
    for wrong in [Resolution::Overwrite, Resolution::Rename { new_name: None }] {
        assert!(matches!(
            engine
                .manager
                .resolve(job, conflicts[0].conflict_id, &wrong, false),
            Err(JobError {
                code: ErrorCode::InvalidResolution,
                ..
            })
        ));
    }
    engine
        .manager
        .resolve(job, conflicts[0].conflict_id, &Resolution::Skip, false)
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!((last.state, last.files_skipped), (JobState::Completed, 1));
    assert!(big.exists(), "skip keeps the file");

    let job = recycle(&engine);
    let conflicts = engine.conflicts_and_progress(job, 1, 0);
    engine
        .manager
        .resolve(
            job,
            conflicts[0].conflict_id,
            &Resolution::DeletePermanently,
            false,
        )
        .unwrap();
    let (last, _) = engine.finish(job);
    assert_eq!(last.state, JobState::Completed, "{last:?}");
    assert!(!big.exists(), "deleted for good, as decided");

    // Delete permanently answers only this conflict.
    let other = dir.path().join("other.txt");
    fs::write(&other, "x").unwrap();
    let destination = dir.path().join("dst");
    fs::create_dir_all(&destination).unwrap();
    fs::write(destination.join("other.txt"), "old").unwrap();
    let job = engine.start(
        JobKind::Copy,
        &[&other],
        Some(&destination),
        JobOptions::default(),
    );
    let conflicts = engine.conflicts_and_progress(job, 1, 0);
    assert!(matches!(
        engine.manager.resolve(
            job,
            conflicts[0].conflict_id,
            &Resolution::DeletePermanently,
            false
        ),
        Err(JobError {
            code: ErrorCode::InvalidResolution,
            ..
        })
    ));
    engine
        .manager
        .resolve(job, conflicts[0].conflict_id, &Resolution::Skip, false)
        .unwrap();
    engine.finish(job);
}

#[test]
fn a_gate_can_refuse_a_job_before_anything_is_written() {
    struct Vetoer;
    impl JobGate for Vetoer {
        fn check(&self, job: &JobPreview<'_>) -> Result<(), String> {
            match job.destination {
                Some(destination) if destination.contains("forbidden") => Err(format!(
                    "denied by the test gate: {} file of {} bytes",
                    job.files_total, job.bytes_total
                )),
                _ => Ok(()),
            }
        }
    }
    let root = scratch("gate");
    let source = root.path().join("a.txt");
    write_file(&source, 10, 1);
    let engine = engine();
    assert!(engine.manager.set_gate(Arc::new(Vetoer)));
    assert!(
        !engine.manager.set_gate(Arc::new(Vetoer)),
        "only the first gate counts"
    );

    let refused = root.path().join("forbidden");
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&refused),
        options(ConflictPolicy::Ask),
    );
    assert_eq!(
        engine.finish(job).0.state,
        JobState::Failed {
            message: "denied by the test gate: 1 file of 10 bytes".to_owned()
        }
    );
    assert!(!refused.exists(), "a refused job creates nothing");

    let allowed = root.path().join("fine");
    let job = engine.start(
        JobKind::Copy,
        &[&source],
        Some(&allowed),
        options(ConflictPolicy::Ask),
    );
    assert_eq!(engine.finish(job).0.state, JobState::Completed);
    assert!(allowed.join("a.txt").is_file());
}

#[test]
fn bad_requests_are_refused_before_anything_happens() {
    let dir = scratch("refused");
    let folder = dir.path().join("folder");
    fs::create_dir_all(&folder).unwrap();
    let engine = engine();
    let request = |kind: JobKind, sources: Vec<String>, destination: Option<String>| JobRequest {
        kind,
        sources,
        destination,
        options: JobOptions::default(),
    };
    let folder_text = folder.display().to_string();
    let cases = [
        (
            request(JobKind::Copy, vec![], Some(folder_text.clone())),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Copy,
                vec!["relative".to_owned()],
                Some(folder_text.clone()),
            ),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Copy,
                vec![dir.path().join("missing").display().to_string()],
                Some(folder_text.clone()),
            ),
            ErrorCode::NotFound,
        ),
        (
            request(JobKind::Copy, vec![folder_text.clone()], None),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Copy,
                vec![folder_text.clone()],
                Some(folder.join("inside").display().to_string()),
            ),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Copy,
                vec![folder_text.clone()],
                Some(dir.path().display().to_string()),
            ),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Delete { permanent: true },
                vec![folder_text.clone()],
                Some(folder_text.clone()),
            ),
            ErrorCode::InvalidPath,
        ),
        (
            request(
                JobKind::Delete { permanent: true },
                vec![r"C:\".to_owned()],
                None,
            ),
            ErrorCode::InvalidPath,
        ),
    ];
    for (request, code) in cases {
        let refused = engine.manager.start(request.clone()).unwrap_err();
        assert_eq!(refused.code, code, "{request:?}: {refused}");
    }
    assert!(folder.exists());
    assert!(matches!(
        engine.manager.control(999_999, JobAction::Pause),
        Err(JobError {
            code: ErrorCode::NoSuchJob,
            ..
        })
    ));
}

/// Copies 10,000 small files with 1, 2 and 4 files in flight and prints the
/// times: `cargo test -p cabinetos-jobs --release --test engine -- --ignored --nocapture measure`.
#[test]
#[ignore = "a measurement, not a test; run it by hand"]
#[expect(clippy::cast_precision_loss, reason = "numbers for display")]
fn measure_files_in_flight() {
    let dir = scratch("measure");
    let source = dir.path().join("src");
    for group in 0..100 {
        let folder = source.join(format!("g{group}"));
        fs::create_dir_all(&folder).unwrap();
        for number in 0..100 {
            let seed = group * 100 + number;
            write_file(
                &folder.join(format!("{number}.bin")),
                1024 + usize::try_from(seed * 7919 % (63 * 1024)).unwrap(),
                seed,
            );
        }
    }
    for round in 0..2 {
        for in_flight in [1, 2, 4, 8] {
            let engine = engine_with(EngineConfig {
                solid_state_files_in_flight: in_flight,
                ..EngineConfig::default()
            });
            let destination = dir.path().join(format!("dst-{round}-{in_flight}"));
            let started = Instant::now();
            let job = engine.start(
                JobKind::Copy,
                &[&source],
                Some(&destination),
                JobOptions::default(),
            );
            let (last, _) = engine.finish(job);
            let elapsed = started.elapsed();
            assert_eq!(last.state, JobState::Completed);
            println!(
                "round {round}, {in_flight} in flight: {} files, {:.1} MB in {:.0} ms ({:.0} files/s)",
                last.files_done,
                last.bytes_done as f64 / 1e6,
                elapsed.as_secs_f64() * 1000.0,
                last.files_done as f64 / elapsed.as_secs_f64()
            );
            fs::remove_dir_all(&destination).unwrap();
        }
    }
}

/// What `CopyFileExW` keeps by itself, and what `preserve_timestamps` adds.
#[test]
fn copyfile_keeps_the_write_time_and_the_option_adds_the_creation_time() {
    use std::fs::FileTimes;
    use std::os::windows::fs::FileTimesExt;
    use std::time::SystemTime;

    let dir = scratch("times");
    let source = dir.path().join("old.txt");
    fs::write(&source, "old").unwrap();
    let old = SystemTime::UNIX_EPOCH + Duration::from_secs(1_000_000_000);
    fs::File::options()
        .write(true)
        .open(&source)
        .unwrap()
        .set_times(
            FileTimes::new()
                .set_modified(old)
                .set_accessed(old)
                .set_created(old),
        )
        .unwrap();
    let original = fs::metadata(&source).unwrap();

    let engine = engine();
    for preserve in [false, true] {
        let destination = dir.path().join(format!("dst-{preserve}"));
        let options = JobOptions {
            preserve_timestamps: preserve,
            ..JobOptions::default()
        };
        let job = engine.start(JobKind::Copy, &[&source], Some(&destination), options);
        assert_eq!(engine.finish(job).0.state, JobState::Completed);
        let copied = fs::metadata(destination.join("old.txt")).unwrap();
        assert_eq!(
            copied.last_write_time(),
            original.last_write_time(),
            "CopyFileExW keeps the write time"
        );
        if preserve {
            assert_eq!(copied.creation_time(), original.creation_time());
        } else {
            assert_ne!(
                copied.creation_time(),
                original.creation_time(),
                "without the option the copy is created now"
            );
        }
    }
}

#[test]
fn a_steps_job_runs_its_steps_in_order_and_counts_what_failed() {
    let dir = scratch("steps");
    let folder = dir.path().join("New folder");
    let path = |name: &str| folder.join(name).display().to_string();
    let steps = vec![
        JobStep::CreateFolder {
            path: folder.display().to_string(),
        },
        JobStep::CreateFile {
            path: path("a.txt"),
        },
        JobStep::Rename {
            from: path("a.txt"),
            to: path("b.txt"),
        },
        // Gone already: fails, and the job goes on.
        JobStep::Rename {
            from: path("a.txt"),
            to: path("c.txt"),
        },
        // Never replaces anything.
        JobStep::CreateFile {
            path: path("b.txt"),
        },
    ];
    let engine = engine();
    let job = engine
        .manager
        .start(JobRequest {
            kind: JobKind::Steps { steps },
            sources: Vec::new(),
            destination: None,
            options: JobOptions::default(),
        })
        .unwrap();
    let info = engine
        .manager
        .list()
        .into_iter()
        .find(|info| info.progress.job_id == job)
        .unwrap();
    assert_eq!(info.sources.len(), 5, "the sources are the steps' paths");
    let (progress, conflicts) = engine.finish(job);
    assert_eq!(progress.state, JobState::CompletedWithErrors);
    assert_eq!((progress.files_done, progress.files_failed), (5, 2));
    assert!(conflicts.is_empty());
    assert!(folder.join("b.txt").exists() && !folder.join("a.txt").exists());

    let refused = engine.manager.start(JobRequest {
        kind: JobKind::Steps {
            steps: vec![JobStep::CreateFile {
                path: "relative.txt".to_owned(),
            }],
        },
        sources: Vec::new(),
        destination: None,
        options: JobOptions::default(),
    });
    assert_eq!(refused.unwrap_err().code, ErrorCode::InvalidPath);
}

/// Holds every job in its gate until released.
struct Turnstile(Mutex<bool>, std::sync::Condvar);

impl JobGate for Turnstile {
    fn check(&self, _job: &JobPreview<'_>) -> Result<(), String> {
        let mut open = self.0.lock().unwrap();
        while !*open {
            open = self.1.wait(open).unwrap();
        }
        Ok(())
    }
}

#[test]
fn chained_jobs_wait_their_turn_and_a_cancel_takes_the_rest_along() {
    let dir = scratch("chain");
    let folder = dir.path().join("Sorted");
    let source = dir.path().join("a.txt");
    fs::write(&source, b"alpha").unwrap();
    let steps = |step: JobStep| JobRequest {
        kind: JobKind::Steps { steps: vec![step] },
        sources: Vec::new(),
        destination: None,
        options: JobOptions::default(),
    };
    let copy = JobRequest {
        kind: JobKind::Copy,
        sources: vec![source.display().to_string()],
        destination: Some(folder.display().to_string()),
        options: JobOptions::default(),
    };
    let engine = engine();
    // The second job's source does not exist yet when the chain starts: it
    // is checked only when its turn comes.
    let made = folder.join("a.txt");
    let chain = engine
        .manager
        .start_chain(vec![
            steps(JobStep::CreateFolder {
                path: folder.display().to_string(),
            }),
            copy.clone(),
            steps(JobStep::Rename {
                from: made.display().to_string(),
                to: folder.join("b.txt").display().to_string(),
            }),
        ])
        .unwrap();
    for job in &chain {
        assert_eq!(
            engine.finish(*job).0.state,
            JobState::Completed,
            "job {job}"
        );
    }
    assert!(folder.join("b.txt").exists() && source.exists());

    // Held at the gate, the first job cannot end; cancelling the second,
    // still waiting in the chain, ends it and the third at once.
    let gate = Arc::new(Turnstile(Mutex::new(false), std::sync::Condvar::new()));
    assert!(
        engine
            .manager
            .set_gate(Arc::clone(&gate) as Arc<dyn JobGate>)
    );
    let chain = engine
        .manager
        .start_chain(vec![
            steps(JobStep::CreateFile {
                path: dir.path().join("first.txt").display().to_string(),
            }),
            steps(JobStep::CreateFile {
                path: dir.path().join("second.txt").display().to_string(),
            }),
            steps(JobStep::CreateFile {
                path: dir.path().join("third.txt").display().to_string(),
            }),
        ])
        .unwrap();
    engine.manager.control(chain[1], JobAction::Cancel).unwrap();
    let states: Vec<JobState> = engine
        .manager
        .list()
        .into_iter()
        .filter(|info| chain[1..].contains(&info.progress.job_id))
        .map(|info| info.progress.state)
        .collect();
    assert_eq!(states, [JobState::Cancelled, JobState::Cancelled]);
    *gate.0.lock().unwrap() = true;
    gate.1.notify_all();
    assert_eq!(engine.finish(chain[0]).0.state, JobState::Completed);
    assert!(dir.path().join("first.txt").exists());
    assert!(!dir.path().join("second.txt").exists() && !dir.path().join("third.txt").exists());

    // The first job refused: nothing is queued, the chain is refused whole.
    let refused = engine.manager.start_chain(vec![JobRequest {
        sources: vec![dir.path().join("missing.txt").display().to_string()],
        ..copy
    }]);
    assert_eq!(refused.unwrap_err().code, ErrorCode::NotFound);
}
