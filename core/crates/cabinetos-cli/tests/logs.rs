//! `log trace` and `log tail` over prepared log folders: no core needed.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

const CLI_EXE: &str = env!("CARGO_BIN_EXE_cabinetos-cli");
const TRACE: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4V";
const REQUEST: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";
const OTHER: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4X";

fn cli(args: &[&str]) -> Output {
    Command::new(CLI_EXE)
        .args(args)
        .env_remove("CABINETOS_LOG")
        .output()
        .unwrap()
}

fn stdout(output: &Output) -> String {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout.clone()).unwrap()
}

fn line(
    ts: &str,
    boundary: &str,
    message: &str,
    trace: &str,
    request: &str,
    thread: &str,
) -> String {
    format!(
        r#"{{"ts":"{ts}","level":"INFO","boundary":"{boundary}","target":"t","message":"{message}","trace_id":"{trace}","request_id":"{request}","thread":"{thread}"}}"#
    )
}

/// The window's, the core's and the core's heavy file of one day, each
/// with lines of the traced action and of another one.
fn prepared(dir: &Path) {
    let ui = [
        line(
            "2026-09-29T10:00:00.001Z",
            "frontend",
            "command executed",
            TRACE,
            TRACE,
            "ui",
        ),
        line(
            "2026-09-29T10:00:00.002Z",
            "frontend",
            "request sent",
            TRACE,
            REQUEST,
            "ui",
        ),
        line(
            "2026-09-29T10:00:00.050Z",
            "frontend",
            "reply received",
            TRACE,
            REQUEST,
            "ui",
        ),
        line(
            "2026-09-29T10:00:01.000Z",
            "frontend",
            "someone else",
            OTHER,
            OTHER,
            "ui",
        ),
    ];
    let core = [
        line(
            "2026-09-29T10:00:00.010Z",
            "engine",
            "job queued",
            TRACE,
            REQUEST,
            "core-rt-2",
        ),
        line(
            "2026-09-29T10:00:00.040Z",
            "engine",
            "request handled",
            TRACE,
            REQUEST,
            "core-rt-2",
        ),
        line(
            "2026-09-29T10:00:00.030Z",
            "engine",
            "not this one",
            OTHER,
            OTHER,
            "core-rt-1",
        ),
    ];
    // The heavy file repeats the core's lines (a millisecond later: its own
    // writer's clock) and has one of its own.
    let heavy = [
        line(
            "2026-09-29T10:00:00.011Z",
            "engine",
            "job queued",
            TRACE,
            REQUEST,
            "core-rt-2",
        ),
        line(
            "2026-09-29T10:00:00.020Z",
            "engine",
            "entry done",
            TRACE,
            REQUEST,
            "job-1",
        ),
        line(
            "2026-09-29T10:00:00.041Z",
            "engine",
            "request handled",
            TRACE,
            REQUEST,
            "core-rt-2",
        ),
    ];
    fs::write(dir.join("ui.2026-09-29.jsonl"), ui.join("\n") + "\n").unwrap();
    fs::write(dir.join("core.2026-09-29.jsonl"), core.join("\n") + "\n").unwrap();
    fs::write(
        dir.join("heavy-core.2026-09-29.jsonl"),
        heavy.join("\n") + "\n",
    )
    .unwrap();
    fs::write(dir.join("crash-20260929T100000000Z.json"), "{}").unwrap();
}

#[test]
fn log_trace_prints_one_action_from_every_process_in_time_order() {
    let dir = tempfile::tempdir().unwrap();
    prepared(dir.path());
    let dir_arg = dir.path().to_str().unwrap();

    let printed = stdout(&cli(&["log", "trace", TRACE, "--dir", dir_arg]));
    let messages: Vec<&str> = printed
        .lines()
        .map(|line| {
            line.split(": ")
                .nth(1)
                .unwrap()
                .split(" request_id=")
                .next()
                .unwrap()
        })
        .collect();
    assert_eq!(
        messages,
        [
            "command executed",
            "request sent",
            "job queued",
            "entry done",
            "request handled",
            "reply received"
        ],
        "{printed}"
    );
    let first = printed.lines().next().unwrap();
    assert!(first.starts_with("frontend ui "), "{first}");
    let entry = printed.lines().nth(3).unwrap();
    assert!(entry.starts_with("engine   heavy-core "), "{entry}");

    // By request ID: the lines of that request only, raw.
    let raw = stdout(&cli(&["log", "trace", REQUEST, "--dir", dir_arg, "--json"]));
    assert_eq!(raw.lines().count(), 5, "{raw}");
    for line in raw.lines() {
        let value: serde_json::Value = serde_json::from_str(line).unwrap();
        assert_eq!(value["request_id"], REQUEST);
    }

    // A lower case ID finds the same lines.
    let lower = stdout(&cli(&[
        "log",
        "trace",
        &TRACE.to_lowercase(),
        "--dir",
        dir_arg,
    ]));
    assert_eq!(lower, printed);
}

#[test]
fn log_trace_says_so_when_nothing_matches_or_the_id_is_not_a_ulid() {
    let dir = tempfile::tempdir().unwrap();
    prepared(dir.path());
    let dir_arg = dir.path().to_str().unwrap();
    let missing = cli(&[
        "log",
        "trace",
        "01J9ZQ4X7K3M5N8P2R6S0T1V4Z",
        "--dir",
        dir_arg,
    ]);
    assert!(!missing.status.success());
    assert!(String::from_utf8_lossy(&missing.stderr).contains("no line"));
    let bad = cli(&["log", "trace", "nope", "--dir", dir_arg]);
    assert!(!bad.status.success());
    assert!(String::from_utf8_lossy(&bad.stderr).contains("not a trace or request ID"));
}

#[test]
fn log_tail_prints_the_newest_lines_of_the_newest_file() {
    let dir = tempfile::tempdir().unwrap();
    let old: Vec<String> = (0..3)
        .map(|n| {
            line(
                "2026-09-28T10:00:00.000Z",
                "engine",
                &format!("old {n}"),
                OTHER,
                OTHER,
                "t",
            )
        })
        .collect();
    let new: Vec<String> = (0..50)
        .map(|n| {
            line(
                "2026-09-29T10:00:00.000Z",
                "engine",
                &format!("new {n}"),
                OTHER,
                OTHER,
                "t",
            )
        })
        .collect();
    fs::write(
        dir.path().join("core.2026-09-28.jsonl"),
        old.join("\n") + "\n",
    )
    .unwrap();
    fs::write(
        dir.path().join("core.2026-09-29.jsonl"),
        new.join("\n") + "\n",
    )
    .unwrap();
    fs::write(
        dir.path().join("heavy-core.2026-09-29.jsonl"),
        new[..2].join("\n") + "\n",
    )
    .unwrap();
    fs::write(
        dir.path().join("heavy-core.2026-09-29.1.jsonl"),
        new[..1].join("\n") + "\n",
    )
    .unwrap();
    let dir_arg = dir.path().to_str().unwrap();

    let printed = stdout(&cli(&["log", "tail", "-n", "3", "--dir", dir_arg]));
    let messages: Vec<&str> = printed
        .lines()
        .map(|line| {
            line.split(": ")
                .nth(1)
                .unwrap()
                .split(" request_id=")
                .next()
                .unwrap()
        })
        .collect();
    assert_eq!(messages, ["new 47", "new 48", "new 49"], "{printed}");

    let heavy = stdout(&cli(&[
        "log", "tail", "--heavy", "--json", "--dir", dir_arg,
    ]));
    assert_eq!(
        heavy.lines().collect::<Vec<_>>(),
        [new[0].as_str()],
        "the newest part"
    );

    let none = cli(&["log", "tail", "--process", "indexer", "--dir", dir_arg]);
    assert!(!none.status.success());
}
