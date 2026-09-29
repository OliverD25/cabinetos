//! Heavy mode through `init`: `set_heavy` switches the heavy file on and off
//! while the process runs; it gets every level whatever the normal level,
//! and the lines only heavy mode writes stay out of the normal file.
//!
//! This is its own test binary: `init` installs a process-wide subscriber, so
//! it can run only once per process.

use std::fs;
use std::path::{Path, PathBuf};

use cabinetos_diag::{
    Boundary, DiagConfig, LOG_FILTER_ENV, LOG_HEAVY_ENV, heavy_enabled, init, set_heavy, set_level,
    span_for_action,
};
use cabinetos_protocol::RequestId;
use serde_json::Value;

fn lines_of(path: &Path) -> Vec<Value> {
    fs::read_to_string(path)
        .unwrap_or_default()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

fn messages(lines: &[Value]) -> Vec<String> {
    lines
        .iter()
        .map(|line| line["message"].as_str().unwrap().to_owned())
        .collect()
}

fn files_starting(dir: &Path, prefix: &str) -> Vec<PathBuf> {
    let mut files: Vec<PathBuf> = fs::read_dir(dir)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .starts_with(prefix)
        })
        .collect();
    files.sort();
    files
}

#[test]
fn heavy_mode_switches_while_the_process_runs() {
    let dir = tempfile::tempdir().unwrap();
    let guard = init(DiagConfig {
        process: "heavytest",
        boundary: Boundary::Engine,
        dir: Some(dir.path().to_path_buf()),
        log_file: true,
    })
    .unwrap();
    if std::env::var_os(LOG_HEAVY_ENV).is_some() || std::env::var_os(LOG_FILTER_ENV).is_some() {
        // The environment decides for this run; nothing to test here.
        return;
    }
    assert!(!heavy_enabled());
    tracing::debug!("debug while heavy is off");
    assert!(files_starting(dir.path(), "heavy-").is_empty());

    assert!(set_heavy(true));
    assert!(heavy_enabled());
    assert!(set_level(tracing::Level::WARN));
    let id = RequestId::new();
    let trace = RequestId::new();
    {
        let span = span_for_action(&id, &trace);
        let _entered = span.enter();
        tracing::trace!(detail = 1, "trace while heavy is on");
        tracing::debug!(target: "heavy::test", payload = "{}", "a heavy-only line");
        tracing::warn!("warn while heavy is on");
    }
    assert!(set_heavy(false));
    assert!(!heavy_enabled());
    tracing::warn!("warn after heavy is off");

    let heavy_files = files_starting(dir.path(), "heavy-heavytest.");
    assert_eq!(heavy_files.len(), 1, "{heavy_files:?}");
    let heavy = lines_of(&heavy_files[0]);
    let said = messages(&heavy);
    for expected in [
        "heavy logging is on",
        "trace while heavy is on",
        "a heavy-only line",
        "warn while heavy is on",
    ] {
        assert!(said.contains(&expected.to_owned()), "{expected}: {said:?}");
    }
    assert!(!said.contains(&"debug while heavy is off".to_owned()));
    assert!(!said.contains(&"warn after heavy is off".to_owned()));
    let traced = heavy
        .iter()
        .find(|line| line["message"] == "trace while heavy is on")
        .unwrap();
    assert_eq!(traced["level"], "TRACE");
    assert_eq!(traced["trace_id"], trace.as_str());
    assert_eq!(traced["request_id"], id.as_str());
    assert_eq!(traced["fields"]["detail"], 1);

    // Switched off, the file is closed: nothing holds it any more. A busy
    // machine may take its time.
    let deadline = std::time::Instant::now() + std::time::Duration::from_mins(2);
    while let Err(error) = fs::remove_file(&heavy_files[0]) {
        assert!(std::time::Instant::now() < deadline, "{error}");
        std::thread::sleep(std::time::Duration::from_millis(10));
    }

    // On again, a new file is opened.
    assert!(set_heavy(true));
    tracing::info!("after the second switch");
    assert!(set_heavy(false));
    let again = files_starting(dir.path(), "heavy-heavytest.");
    assert_eq!(
        messages(&lines_of(&again[0])).last().unwrap(),
        "heavy logging is off"
    );
    drop(guard);

    let normal = messages(&lines_of(&files_starting(dir.path(), "heavytest.")[0]));
    assert!(normal.contains(&"warn while heavy is on".to_owned()));
    assert!(normal.contains(&"warn after heavy is off".to_owned()));
    for absent in [
        "trace while heavy is on",
        "a heavy-only line",
        "debug while heavy is off",
    ] {
        assert!(!normal.contains(&absent.to_owned()), "{absent}: {normal:?}");
    }
}
