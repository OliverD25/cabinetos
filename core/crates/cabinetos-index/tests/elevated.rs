//! The index on a real volume. Reading the MFT and the change journal needs
//! Administrator rights, so these tests are `#[ignore]`d and, when run
//! without the rights, skip with a message. CI runs them (its runners are
//! Administrators):
//!
//! ```text
//! cargo test --release -p cabinetos-index -- --ignored --nocapture
//! ```
//!
//! They index the volume that holds `%TEMP%`, because everything they write
//! lives under `%TEMP%\cabinetos-index-test\` and is removed at the end.

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use cabinetos_index::{Indexes, Matcher, VolumeReport, VolumeState, is_elevated};

/// Building a system drive with millions of files in a debug build.
const READY_DEADLINE: Duration = Duration::from_mins(15);

/// Phase 6: a change is visible to searches within a second.
const VISIBLE_WITHIN: Duration = Duration::from_secs(1);

fn elevated() -> bool {
    let elevated = is_elevated();
    if !elevated {
        eprintln!(
            "skipped: this test reads the MFT and the change journal, which needs Administrator rights; run it from an elevated terminal (CI does)"
        );
    }
    elevated
}

fn test_root() -> PathBuf {
    let root = std::env::temp_dir().join("cabinetos-index-test");
    std::fs::create_dir_all(&root).unwrap();
    root
}

fn letter_of(path: &Path) -> char {
    path.to_str()
        .unwrap()
        .chars()
        .next()
        .unwrap()
        .to_ascii_uppercase()
}

fn wait_ready(indexes: &Indexes) -> VolumeReport {
    let deadline = Instant::now() + READY_DEADLINE;
    loop {
        let report = indexes.status().remove(0);
        match &report.state {
            VolumeState::Ready => return report,
            VolumeState::Failed(message) => panic!("indexing failed: {message}"),
            _ => {}
        }
        assert!(Instant::now() < deadline, "not ready in time: {report:?}");
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// The paths of the hits for `token` under `root`.
fn found(indexes: &Indexes, token: &str, root: &Path) -> Vec<String> {
    let outcome = indexes
        .search(
            &Matcher::new(token).unwrap(),
            Some(root.to_str().unwrap()),
            100,
        )
        .unwrap();
    outcome.hits.into_iter().map(|hit| hit.path).collect()
}

/// How long until `check` holds for the hits of `token`.
fn until(
    indexes: &Indexes,
    token: &str,
    root: &Path,
    check: impl Fn(&[String]) -> bool,
) -> Duration {
    let started = Instant::now();
    loop {
        let paths = found(indexes, token, root);
        if check(&paths) {
            return started.elapsed();
        }
        assert!(
            started.elapsed() < VISIBLE_WITHIN * 10,
            "still {paths:?} after {:?}",
            started.elapsed()
        );
        std::thread::sleep(Duration::from_millis(5));
    }
}

fn ends_with(paths: &[String], name: &str) -> bool {
    paths
        .iter()
        .any(|path| path.ends_with(&format!("\\{name}")))
}

#[test]
#[ignore = "needs Administrator rights; CI runs it"]
fn a_volume_builds_and_follows_changes_within_a_second() {
    if !elevated() {
        return;
    }
    let dir = tempfile::Builder::new()
        .prefix("probe")
        .tempdir_in(test_root())
        .unwrap();
    // Made before the build: they come from the MFT enumeration.
    let token = format!("cbx{:012x}", rand::random::<u64>() & 0xFFFF_FFFF_FFFF);
    std::fs::create_dir(dir.path().join(format!("{token}-folder"))).unwrap();
    for n in 0..20 {
        std::fs::write(
            dir.path()
                .join(format!("{token}-folder"))
                .join(format!("{token}-early-{n}.txt")),
            "x",
        )
        .unwrap();
    }

    let letter = letter_of(dir.path());
    let indexes = Indexes::start(&[letter]);
    let report = wait_ready(&indexes);
    let bytes_per_entry = report.memory_bytes / report.entries.max(1);
    println!(
        "{letter}: {} entries indexed in {} ms, {} bytes of memory ({bytes_per_entry} bytes per entry)",
        report.entries,
        report.built_in_ms.unwrap(),
        report.memory_bytes,
    );
    assert!(report.entries > 1000, "{report:?}");
    assert!(bytes_per_entry < 120, "{report:?}");

    let early = found(&indexes, &format!("{token}-early"), dir.path());
    assert_eq!(early.len(), 20, "{early:?}");
    assert!(
        early[0].contains(&format!("\\{token}-folder\\")),
        "{early:?}"
    );

    // Changes after the build come from the change journal.
    let first = dir.path().join(format!("{token}-created.txt"));
    std::fs::write(&first, "x").unwrap();
    let appeared = until(&indexes, &token, dir.path(), |paths| {
        ends_with(paths, &format!("{token}-created.txt"))
    });
    println!("a created file was found after {appeared:?}");

    let renamed = dir.path().join(format!("{token}-renamed.txt"));
    std::fs::rename(&first, &renamed).unwrap();
    let rename_seen = until(&indexes, &token, dir.path(), |paths| {
        ends_with(paths, &format!("{token}-renamed.txt"))
            && !ends_with(paths, &format!("{token}-created.txt"))
    });
    println!("a rename was seen after {rename_seen:?}");

    let moved = dir
        .path()
        .join(format!("{token}-folder"))
        .join(format!("{token}-moved.txt"));
    std::fs::rename(&renamed, &moved).unwrap();
    let move_seen = until(&indexes, &token, dir.path(), |paths| {
        paths
            .iter()
            .any(|path| path.ends_with(&format!("\\{token}-folder\\{token}-moved.txt")))
    });
    println!("a move was seen after {move_seen:?}");

    std::fs::remove_file(&moved).unwrap();
    let delete_seen = until(&indexes, &token, dir.path(), |paths| {
        !ends_with(paths, &format!("{token}-moved.txt"))
    });
    println!("a delete was seen after {delete_seen:?}");

    for took in [appeared, rename_seen, move_seen, delete_seen] {
        assert!(took < VISIBLE_WITHIN, "{took:?}");
    }

    // A 3-letter query over the whole volume.
    let matcher = Matcher::new("win").unwrap();
    let started = Instant::now();
    let outcome = indexes.search(&matcher, None, 100).unwrap();
    println!(
        "a 3-letter query over {} entries: {} hits in {} µs (complete: {})",
        report.entries,
        outcome.hits.len(),
        started.elapsed().as_micros(),
        outcome.complete
    );
    assert!(outcome.complete);

    let lag = indexes.status().remove(0).journal_lag;
    println!("journal lag at the end: {lag:?} bytes");
    let stopping = Instant::now();
    indexes.stop();
    let stopped = stopping.elapsed();
    println!("stopping took {stopped:?}");
    assert!(stopped < Duration::from_secs(5), "{stopped:?}");
}
