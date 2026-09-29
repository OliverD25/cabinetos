//! `init` writes one JSON object per line into the configured directory, and
//! dropping the guard flushes every event to disk.
//!
//! This is its own test binary: `init` installs a process-wide subscriber, so
//! it can run only once per process.

use std::fs;
use std::path::Path;

use cabinetos_diag::{
    Boundary, DiagConfig, DiagError, KEPT_LOG_FILES, init, recent_events, span_for_request,
};
use cabinetos_protocol::RequestId;
use serde_json::Value;

/// Reads every line of every `<process>.<date>.jsonl` file in `dir`.
fn read_log_lines(dir: &Path, process: &str) -> Vec<Value> {
    let mut lines = Vec::new();
    let mut files = 0;
    for entry in fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        let name = path.file_name().unwrap().to_string_lossy();
        let is_log = name.starts_with(&format!("{process}."))
            && path
                .extension()
                .is_some_and(|extension| extension == "jsonl");
        if is_log {
            files += 1;
            let text = fs::read_to_string(&path).unwrap();
            for line in text.lines() {
                lines.push(serde_json::from_str(line).unwrap_or_else(|error| {
                    panic!("not a JSON line ({error}): {line}");
                }));
            }
        }
    }
    assert!(files > 0, "no {process}.*.jsonl file in {}", dir.display());
    lines
}

/// `ts` must be RFC 3339 in UTC with milliseconds: `2026-09-28T01:02:03.004Z`.
fn assert_timestamp_format(ts: &str) {
    let bytes = ts.as_bytes();
    assert_eq!(bytes.len(), 24, "{ts}");
    for (index, byte) in bytes.iter().enumerate() {
        let expected_separator = match index {
            4 | 7 => Some(b'-'),
            10 => Some(b'T'),
            13 | 16 => Some(b':'),
            19 => Some(b'.'),
            23 => Some(b'Z'),
            _ => None,
        };
        match expected_separator {
            Some(separator) => assert_eq!(*byte, separator, "{ts}"),
            None => assert!(byte.is_ascii_digit(), "{ts}"),
        }
    }
}

fn count_logs(dir: &Path, process: &str) -> usize {
    fs::read_dir(dir)
        .unwrap()
        .filter(|entry| {
            let name = entry.as_ref().unwrap().file_name();
            let name = name.to_string_lossy();
            name.starts_with(&format!("{process}.")) && name.ends_with(".jsonl")
        })
        .count()
}

#[test]
fn writes_json_lines_and_flushes_on_drop() {
    let dir = tempfile::tempdir().unwrap();
    // Twenty old daily files of this process, plus files init must not touch.
    for day in 1..=20 {
        fs::write(
            dir.path().join(format!("diagtest.2025-01-{day:02}.jsonl")),
            "",
        )
        .unwrap();
    }
    fs::write(dir.path().join("crash-20250101T000000000Z.json"), "{}").unwrap();
    fs::write(dir.path().join("other.2025-01-01.jsonl"), "").unwrap();

    let guard = init(DiagConfig {
        process: "diagtest",
        boundary: Boundary::Engine,
        dir: Some(dir.path().to_path_buf()),
        log_file: true,
    })
    .unwrap();
    assert_eq!(guard.log_dir(), dir.path());

    // Old files were pruned to the cap, today's file included.
    assert_eq!(count_logs(dir.path(), "diagtest"), KEPT_LOG_FILES);
    assert!(dir.path().join("crash-20250101T000000000Z.json").exists());
    assert!(dir.path().join("other.2025-01-01.jsonl").exists());

    let id = RequestId::new();
    {
        let span = span_for_request(&id);
        let _entered = span.enter();
        tracing::info!(answer = 42, "inside the request");
    }
    tracing::info!("outside any request");

    assert!(matches!(
        init(DiagConfig::new("second", Boundary::Engine)),
        Err(DiagError::AlreadyInitialized)
    ));

    drop(guard);

    let lines = read_log_lines(dir.path(), "diagtest");
    let inside = lines
        .iter()
        .find(|line| line["message"] == "inside the request")
        .expect("the event inside the request span was not written");
    assert_eq!(inside["boundary"], "engine");
    assert_eq!(inside["request_id"], id.as_str());
    assert_eq!(
        inside["trace_id"],
        id.as_str(),
        "a request without a trace is its own"
    );
    assert_eq!(inside["level"], "INFO");
    assert_eq!(inside["span"], "request");
    assert_eq!(inside["fields"]["answer"], 42);
    assert_eq!(inside["target"], "log_file");
    assert!(
        inside["thread"]
            .as_str()
            .is_some_and(|thread| !thread.is_empty())
    );
    assert_timestamp_format(inside["ts"].as_str().unwrap());

    let outside = lines
        .iter()
        .find(|line| line["message"] == "outside any request")
        .expect("the event outside the span was not written");
    assert!(outside.get("request_id").is_none());
    assert!(outside.get("trace_id").is_none());

    let recent = recent_events();
    assert!(recent.len() >= 2);
    assert!(recent.last().unwrap().contains("outside any request"));
    assert!(recent[recent.len() - 2].contains(id.as_str()));
}
