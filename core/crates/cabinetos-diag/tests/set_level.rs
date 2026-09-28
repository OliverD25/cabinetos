//! `set_level` changes what is written while the process runs.
//!
//! This is its own test binary: `init` installs a process-wide subscriber, so
//! it can run only once per process.

use std::fs;

use cabinetos_diag::{Boundary, DiagConfig, LOG_FILTER_ENV, init, set_level};
use serde_json::Value;

#[test]
fn set_level_changes_what_is_written() {
    assert!(
        !set_level(tracing::Level::DEBUG),
        "nothing to change before init"
    );
    let dir = tempfile::tempdir().unwrap();
    let guard = init(DiagConfig {
        process: "leveltest",
        boundary: Boundary::Engine,
        dir: Some(dir.path().to_path_buf()),
        log_file: true,
    })
    .unwrap();
    if std::env::var_os(LOG_FILTER_ENV).is_some() {
        // A filter from the environment wins; there is nothing to test here.
        assert!(!set_level(tracing::Level::DEBUG));
        return;
    }

    tracing::debug!("debug before");
    assert!(set_level(tracing::Level::DEBUG));
    tracing::debug!("debug after");
    assert!(set_level(tracing::Level::WARN));
    tracing::info!("info while warn");
    tracing::warn!("warn while warn");
    drop(guard);

    let mut messages = Vec::new();
    for entry in fs::read_dir(dir.path()).unwrap() {
        let text = fs::read_to_string(entry.unwrap().path()).unwrap();
        for line in text.lines() {
            let value: Value = serde_json::from_str(line).unwrap();
            messages.push(value["message"].as_str().unwrap().to_owned());
        }
    }
    assert!(
        !messages.contains(&"debug before".to_owned()),
        "{messages:?}"
    );
    assert!(messages.contains(&"debug after".to_owned()), "{messages:?}");
    assert!(
        !messages.contains(&"info while warn".to_owned()),
        "{messages:?}"
    );
    assert!(
        messages.contains(&"warn while warn".to_owned()),
        "{messages:?}"
    );
}
