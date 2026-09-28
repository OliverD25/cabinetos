//! Diagnostics for every CabinetOS process: structured JSON Lines logs, a ring
//! buffer of recent events, and a crash trace written by the panic hook.
//!
//! - [`init`] installs a `tracing` subscriber. Events are formatted as one JSON
//!   object per line (schema in `docs/diagnostics.md`) and handed to a
//!   background thread that appends them to `<process>.<UTC date>.jsonl`, a
//!   file that rolls over daily. The calling thread never waits for the disk.
//! - Every event carries the process's [`Boundary`], and the `request_id` of
//!   the request it belongs to ([`span_for_request`]), so one action can be
//!   followed from the UI through the pipe into the core.
//! - On a panic, the hook writes `crash-<timestamp>.json` with the backtrace and
//!   the last events from the ring buffer, then flushes the log writer before
//!   the process dies.
//!
//! Serves Constitution Article 12 (Unified, Zero-Latency Diagnostics & Logging)
//! and Article 1 (logging never blocks the caller). Brief §8.
#![forbid(unsafe_code)]

mod clock;
mod format;
mod panic;
mod ring;

use std::ffi::OsString;
use std::io::IsTerminal;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};

use cabinetos_protocol::RequestId;
use serde::Serialize;
use tracing_appender::non_blocking::{NonBlockingBuilder, WorkerGuard};
use tracing_appender::rolling::{RollingFileAppender, Rotation};
use tracing_subscriber::filter::{LevelFilter, Targets};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::registry::Registry;

pub use ring::{RING_CAPACITY, recent_events};

/// Environment variable that overrides the log directory.
pub const LOG_DIR_ENV: &str = "CABINETOS_LOG_DIR";
/// Environment variable that, when `1`, also prints events to stderr.
pub const LOG_STDERR_ENV: &str = "CABINETOS_LOG_STDERR";
/// Environment variable with a log filter, for example `debug` or
/// `info,cabinetos_ipc=trace`. The default is `info`.
pub const LOG_FILTER_ENV: &str = "CABINETOS_LOG";

/// Where in the system an event happened. Each process has one fixed boundary,
/// so a crash trace names the side that failed (Article 12).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Boundary {
    /// The WinUI 3 window, or a client standing in for it such as the CLI.
    Frontend,
    /// The Rust core.
    Engine,
    /// A plugin sandbox.
    Plugin,
    /// The elevated indexer.
    Indexer,
    /// The pipe bridge between processes.
    Ipc,
}

impl Boundary {
    /// The lowercase name written into logs and crash files.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Frontend => "frontend",
            Self::Engine => "engine",
            Self::Plugin => "plugin",
            Self::Indexer => "indexer",
            Self::Ipc => "ipc",
        }
    }
}

/// How [`init`] sets up diagnostics for one process.
#[derive(Clone, Debug)]
pub struct DiagConfig {
    /// Process name: `"core"`, `"indexer"` or `"cli"`. It names the log files,
    /// for example `core.2026-09-28.jsonl`.
    pub process: &'static str,
    /// The boundary written into every event of this process.
    pub boundary: Boundary,
    /// Log directory. When `None`, the `CABINETOS_LOG_DIR` environment
    /// variable is used, and without it `%LOCALAPPDATA%\CabinetOS\logs`.
    pub dir: Option<PathBuf>,
    /// Write the JSON Lines file. When `false`, events go to stderr only; the
    /// CLI runs this way unless it is given a log directory.
    pub log_file: bool,
}

impl DiagConfig {
    /// A configuration that writes the log file to the default directory.
    #[must_use]
    pub fn new(process: &'static str, boundary: Boundary) -> Self {
        Self {
            process,
            boundary,
            dir: None,
            log_file: true,
        }
    }
}

/// Keeps the background log writer alive. Dropping it flushes every queued
/// event to disk; keep it until the process exits.
#[must_use = "dropping the guard flushes and stops the log writer"]
#[derive(Debug)]
pub struct DiagGuard {
    log_dir: PathBuf,
}

impl DiagGuard {
    /// The directory that holds this process's log and crash files.
    #[must_use]
    pub fn log_dir(&self) -> &Path {
        &self.log_dir
    }
}

impl Drop for DiagGuard {
    fn drop(&mut self) {
        flush_log_writer();
    }
}

/// Why [`init`] failed.
#[derive(Debug, thiserror::Error)]
pub enum DiagError {
    /// [`init`] was already called in this process.
    #[error("diagnostics are already initialized in this process")]
    AlreadyInitialized,
    /// The log directory or file could not be created.
    #[error("cannot open the log file in {dir}: {source}")]
    LogFile {
        /// The directory that was tried.
        dir: PathBuf,
        /// What went wrong.
        #[source]
        source: tracing_appender::rolling::InitError,
    },
    /// Some other global `tracing` subscriber was installed first.
    #[error("another global tracing subscriber is already installed")]
    SubscriberAlreadySet,
}

/// What the panic hook needs to know about this process.
struct ProcessInfo {
    process: &'static str,
    boundary: Boundary,
    log_dir: PathBuf,
}

static PROCESS: OnceLock<ProcessInfo> = OnceLock::new();

/// The background writer's guard. The panic hook takes it out and drops it,
/// which flushes queued events before the process dies.
static WORKER: OnceLock<Mutex<Option<WorkerGuard>>> = OnceLock::new();

static INITIALIZED: AtomicBool = AtomicBool::new(false);

/// Sets up diagnostics for this process. Call it once, first thing in `main`,
/// and keep the returned guard until the process exits.
pub fn init(config: DiagConfig) -> Result<DiagGuard, DiagError> {
    let DiagConfig {
        process,
        boundary,
        dir,
        log_file,
    } = config;
    if INITIALIZED.swap(true, Ordering::SeqCst) {
        return Err(DiagError::AlreadyInitialized);
    }

    let log_dir = resolve_log_dir(
        dir,
        std::env::var_os(LOG_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
    );
    let filter_text = std::env::var(LOG_FILTER_ENV).ok();
    let (filter, bad_filter) = parse_filter(filter_text.as_deref());

    let (file_layer, worker_guard) = if log_file {
        let appender = RollingFileAppender::builder()
            .rotation(Rotation::DAILY)
            .filename_prefix(process)
            .filename_suffix("jsonl")
            .build(&log_dir)
            .map_err(|source| DiagError::LogFile {
                dir: log_dir.clone(),
                source,
            })?;
        let (writer, guard) = NonBlockingBuilder::default()
            .thread_name("cabinetos-log-writer")
            .finish(appender);
        let layer = tracing_subscriber::fmt::layer()
            .event_format(format::JsonFormat::new(boundary))
            .with_writer(writer)
            .with_ansi(false);
        (Some(layer), Some(guard))
    } else {
        (None, None)
    };

    let stderr_wanted =
        !log_file || std::env::var_os(LOG_STDERR_ENV).is_some_and(|value| value == "1");
    let stderr_layer = stderr_wanted.then(|| {
        tracing_subscriber::fmt::layer()
            .pretty()
            .with_writer(std::io::stderr)
            .with_ansi(std::io::stderr().is_terminal())
    });

    let subscriber = Registry::default()
        .with(filter)
        .with(format::SpanIdsLayer)
        .with(file_layer)
        .with(stderr_layer)
        .with(ring::RingLayer::new(boundary));
    tracing::subscriber::set_global_default(subscriber)
        .map_err(|_| DiagError::SubscriberAlreadySet)?;

    let _ = WORKER.set(Mutex::new(worker_guard));
    let _ = PROCESS.set(ProcessInfo {
        process,
        boundary,
        log_dir: log_dir.clone(),
    });
    panic::install_panic_hook();

    if let Some(text) = bad_filter {
        tracing::warn!(
            filter = %text,
            "ignoring {LOG_FILTER_ENV}: it is not a valid filter; logging at info"
        );
    }
    Ok(DiagGuard { log_dir })
}

/// A span for one request. Everything logged inside it carries the request's
/// ID as `request_id`, in this process's log and in the crash trace.
pub fn span_for_request(id: &RequestId) -> tracing::Span {
    tracing::info_span!("request", request_id = %id)
}

/// Picks the log directory: an explicit directory wins, then the
/// `CABINETOS_LOG_DIR` variable, then `%LOCALAPPDATA%\CabinetOS\logs`. Without
/// `LOCALAPPDATA` (not a normal Windows session) it falls back to the temp
/// directory.
fn resolve_log_dir(
    explicit: Option<PathBuf>,
    env_dir: Option<OsString>,
    local_app_data: Option<OsString>,
) -> PathBuf {
    if let Some(dir) = explicit {
        return dir;
    }
    if let Some(dir) = env_dir.filter(|dir| !dir.is_empty()) {
        return PathBuf::from(dir);
    }
    local_app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join("logs")
}

/// Parses `CABINETOS_LOG`. A value that does not parse is returned so `init`
/// can warn about it once logging works; the level then stays at `info`.
fn parse_filter(text: Option<&str>) -> (Targets, Option<String>) {
    let default = Targets::new().with_default(LevelFilter::INFO);
    match text.map(str::trim).filter(|text| !text.is_empty()) {
        None => (default, None),
        Some(text) => match text.parse::<Targets>() {
            Ok(targets) => (targets, None),
            Err(_) => (default, Some(text.to_owned())),
        },
    }
}

/// Stops the background writer after it has written every queued event.
/// Called when the [`DiagGuard`] drops and by the panic hook.
fn flush_log_writer() {
    let Some(slot) = WORKER.get() else { return };
    // try_lock: the panic hook must never wait here. If another thread holds
    // the slot, that thread is already flushing.
    let guard = match slot.try_lock() {
        Ok(mut slot) => slot.take(),
        Err(std::sync::TryLockError::Poisoned(poisoned)) => poisoned.into_inner().take(),
        Err(std::sync::TryLockError::WouldBlock) => None,
    };
    drop(guard);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_dir_wins_over_env_and_default() {
        let dir = resolve_log_dir(
            Some(PathBuf::from(r"D:\explicit")),
            Some(r"D:\from-env".into()),
            Some(r"C:\Users\x\AppData\Local".into()),
        );
        assert_eq!(dir, PathBuf::from(r"D:\explicit"));
    }

    #[test]
    fn env_dir_wins_over_default() {
        let dir = resolve_log_dir(
            None,
            Some(r"D:\from-env".into()),
            Some(r"C:\Users\x\AppData\Local".into()),
        );
        assert_eq!(dir, PathBuf::from(r"D:\from-env"));
    }

    #[test]
    fn default_is_under_local_app_data() {
        let dir = resolve_log_dir(None, None, Some(r"C:\Users\x\AppData\Local".into()));
        assert_eq!(
            dir,
            PathBuf::from(r"C:\Users\x\AppData\Local\CabinetOS\logs")
        );
        let empty_env = resolve_log_dir(None, Some("".into()), Some(r"C:\L".into()));
        assert_eq!(empty_env, PathBuf::from(r"C:\L\CabinetOS\logs"));
    }

    #[test]
    fn filter_defaults_to_info_and_reports_bad_values() {
        let (_, bad) = parse_filter(None);
        assert_eq!(bad, None);
        let (targets, bad) = parse_filter(Some("debug"));
        assert_eq!(bad, None);
        assert!(targets.would_enable("cabinetos_core", &tracing::Level::DEBUG));
        let (targets, bad) = parse_filter(Some("=nonsense="));
        assert_eq!(bad.as_deref(), Some("=nonsense="));
        assert!(targets.would_enable("cabinetos_core", &tracing::Level::INFO));
        assert!(!targets.would_enable("cabinetos_core", &tracing::Level::DEBUG));
    }

    #[test]
    fn boundary_names_are_lowercase() {
        for boundary in [
            Boundary::Frontend,
            Boundary::Engine,
            Boundary::Plugin,
            Boundary::Indexer,
            Boundary::Ipc,
        ] {
            let json = serde_json::to_value(boundary).unwrap();
            assert_eq!(json, serde_json::json!(boundary.as_str()));
        }
    }
}
