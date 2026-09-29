//! The panic hook: writes a crash trace, then flushes the log writer before the
//! process dies (brief §8, Article 12).
//!
//! The hook runs on the panicking thread, before unwinding, possibly while that
//! thread holds locks. It therefore takes no lock it could wait on and emits
//! no `tracing` events; the crash file carries everything.

use std::backtrace::Backtrace;
use std::fs::OpenOptions;
use std::io::{ErrorKind, Write};
use std::panic::PanicHookInfo;
use std::path::{Path, PathBuf};
use std::sync::{Once, OnceLock};

use serde::Serialize;
use serde_json::Value;

use crate::format::thread_label;
use crate::{Boundary, PROCESS, ProcessInfo, bundle, clock, flush_log_writer, heavy, ring};

/// The contents of `crash-<timestamp>.json`.
#[derive(Serialize)]
struct CrashReport<'a> {
    boundary: Boundary,
    process: &'a str,
    version: &'a str,
    message: &'a str,
    location: Option<CrashLocation<'a>>,
    thread: String,
    backtrace: String,
    /// The ring buffer, oldest first. Each line is embedded as a JSON object.
    recent_events: Vec<Value>,
}

#[derive(Serialize)]
struct CrashLocation<'a> {
    file: &'a str,
    line: u32,
    column: u32,
}

static INSTALL: Once = Once::new();

/// What the process does after a panic's crash trace; see [`on_panic`].
static ON_PANIC: OnceLock<Box<dyn Fn() + Send + Sync>> = OnceLock::new();

/// Runs `callback` after every panic, once its crash trace is written and
/// the log writer is closed, on the panicking thread. A process that must
/// not run on after a panic anywhere (without the thread that panicked,
/// and without a log file) starts its shutdown here. The callback must
/// return at once and wait for nothing, not even a lock: the panicking
/// thread may hold it. Only the first call counts; it returns `false` for
/// the others.
pub fn on_panic(callback: impl Fn() + Send + Sync + 'static) -> bool {
    ON_PANIC.set(Box::new(callback)).is_ok()
}

/// Installs the crash hook once per process. [`init`](crate::init) calls it.
/// The previous hook still runs afterwards, so the usual panic message is
/// printed as well.
pub(crate) fn install_panic_hook() {
    INSTALL.call_once(|| {
        let previous = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            if let Some(process) = PROCESS.get() {
                write_crash_trace(process, info);
            }
            flush_log_writer();
            // After the flush, so the zip has the lines logged just before
            // the crash; the crash trace is on disk whatever happens here.
            if heavy::heavy_enabled()
                && let Some(process) = PROCESS.get()
            {
                write_crash_bundle(process);
            }
            if let Some(callback) = ON_PANIC.get() {
                callback();
            }
            previous(info);
        }));
    });
}

fn write_crash_trace(process: &ProcessInfo, info: &PanicHookInfo<'_>) {
    let recent_events = ring::recent_events_without_waiting()
        .into_iter()
        .map(|line| serde_json::from_str(&line).unwrap_or(Value::String(line)))
        .collect();
    let report = CrashReport {
        boundary: process.boundary,
        process: process.process,
        // Every crate in the workspace shares one version.
        version: env!("CARGO_PKG_VERSION"),
        message: info
            .payload_as_str()
            .unwrap_or("(the panic payload is not a string)"),
        location: info.location().map(|location| CrashLocation {
            file: location.file(),
            line: location.line(),
            column: location.column(),
        }),
        thread: thread_label(),
        backtrace: Backtrace::force_capture().to_string(),
        recent_events,
    };

    let mut stderr = std::io::stderr();
    let written = serde_json::to_vec_pretty(&report)
        .map_err(std::io::Error::from)
        .and_then(|json| write_crash_file(&process.log_dir, &json));
    // writeln! instead of eprintln!: a failed write to stderr must not panic
    // inside the panic hook.
    let _ = match written {
        Ok(path) => writeln!(stderr, "crash trace written to {}", path.display()),
        Err(error) => writeln!(
            stderr,
            "could not write a crash trace to {}: {error}",
            process.log_dir.display()
        ),
    };
}

/// Writes `crash-<time>.zip`, the log bundle of a crash in heavy mode, and
/// says where on stderr.
fn write_crash_bundle(process: &ProcessInfo) {
    let mut stderr = std::io::stderr();
    let _ = match bundle::save_crash_bundle(&process.log_dir, process.process) {
        Ok(path) => writeln!(stderr, "crash bundle written to {}", path.display()),
        Err(error) => writeln!(stderr, "could not write a crash bundle: {error}"),
    };
}

/// Writes `crash-<YYYYMMDDTHHMMSSmmmZ>.json`. Never overwrites: if another
/// process crashed in the same millisecond, the process ID is added.
fn write_crash_file(dir: &Path, json: &[u8]) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(dir)?;
    let stamp = clock::compact_millis(clock::now());
    let mut path = dir.join(format!("crash-{stamp}.json"));
    let mut file = match OpenOptions::new().write(true).create_new(true).open(&path) {
        Ok(file) => file,
        Err(error) if error.kind() == ErrorKind::AlreadyExists => {
            path = dir.join(format!("crash-{stamp}-{}.json", std::process::id()));
            OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&path)?
        }
        Err(error) => return Err(error),
    };
    file.write_all(json)?;
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crash_files_never_overwrite_each_other() {
        let dir = tempfile::tempdir().unwrap();
        let first = write_crash_file(dir.path(), b"{}").unwrap();
        let second = write_crash_file(dir.path(), b"{}").unwrap();
        assert_ne!(first, second);
        let name = first.file_name().unwrap().to_str().unwrap();
        assert!(
            name.starts_with("crash-") && name.ends_with("Z.json"),
            "{name}"
        );
        assert_eq!(name.len(), "crash-20260928T010203004Z.json".len());
    }
}
