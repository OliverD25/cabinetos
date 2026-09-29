//! Diagnostics for every CabinetOS process: structured JSON Lines logs, a ring
//! buffer of recent events, and a crash trace written by the panic hook.
//!
//! - [`init`] installs a `tracing` subscriber. Events are formatted as one JSON
//!   object per line (schema in `docs/diagnostics.md`) and handed to a
//!   background thread that appends them to `<process>.<UTC date>.jsonl`, a
//!   file that rolls over daily; the newest [`KEPT_LOG_FILES`] files are kept.
//!   The calling thread never waits for the disk, and never takes a lock: the
//!   ring buffer of recent events is a lock-free queue.
//! - Every event carries the process's [`Boundary`], the `request_id` of the
//!   request it belongs to, and the `trace_id` of the user action that
//!   request is part of ([`span_for_action`]), so one action can be followed
//!   from the UI through the pipe into the core, its jobs and its plugin
//!   calls. [`current_trace`] reads the trace back, for the messages the
//!   core sends.
//! - On a panic, the hook writes `crash-<timestamp>.json` with the backtrace and
//!   the last events from the ring buffer, then flushes and closes the log
//!   writer: the process is expected to end. [`on_panic`] lets it start its
//!   shutdown from there, whichever thread panicked.
//! - [`set_level`] changes the level while the process runs (the core applies
//!   `logging.level` from `cabinetos.json`); `CABINETOS_LOG` wins over it.
//! - [`set_heavy`] turns heavy mode on or off (the core applies
//!   `logging.heavy`; `CABINETOS_LOG_HEAVY` wins over it): every event at
//!   every level also goes into `heavy-<process>.<date>.jsonl`, and a thread
//!   that logs may wait for that writer (the `heavy` module, ADR 0013).
//!
//! Serves Constitution Article 12 (Unified, Zero-Latency Diagnostics & Logging)
//! and Article 1 (logging never blocks the caller). Brief §8.
#![forbid(unsafe_code)]

mod bundle;
mod clock;
mod format;
mod heavy;
mod mask;
mod panic;
mod ring;

use std::ffi::OsString;
use std::io::IsTerminal;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};

use cabinetos_protocol::RequestId;
use serde::Serialize;
use tracing::Subscriber;
use tracing_appender::non_blocking::{NonBlockingBuilder, WorkerGuard};
use tracing_appender::rolling::{RollingFileAppender, Rotation};
use tracing_subscriber::Layer;
use tracing_subscriber::filter::FilterExt;
use tracing_subscriber::filter::{LevelFilter, Targets, filter_fn};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::registry::{LookupSpan, Registry};
use tracing_subscriber::reload;

pub use bundle::{
    BUNDLE_MINUTES, MAX_BUNDLE_MINUTES, save_bundle, set_bundle_config, set_bundle_windows_build,
};
pub use heavy::{
    HEAVY_CAP_CHECK_BYTES, HEAVY_DISK_CAP, HEAVY_FILE_BYTES, HEAVY_FILE_PREFIX,
    HEAVY_NEVER_WAIT_EXTRA, HEAVY_QUEUE_CAP, HEAVY_TARGET_PREFIX, LOG_HEAVY_ENV, heavy_enabled,
    never_wait_for_heavy_log,
};
pub use mask::{
    MASK, PAYLOAD_CAP, cap_text, is_secret_env, mask_secrets, masked_json, masked_json_bytes,
};
pub use panic::on_panic;
pub use ring::{RING_CAPACITY, recent_events};

/// Environment variable that overrides the log directory.
pub const LOG_DIR_ENV: &str = "CABINETOS_LOG_DIR";
/// Environment variable that, when `1`, also prints events to stderr.
pub const LOG_STDERR_ENV: &str = "CABINETOS_LOG_STDERR";
/// Environment variable with a log filter, for example `debug` or
/// `info,cabinetos_ipc=trace`. The default is `info`.
pub const LOG_FILTER_ENV: &str = "CABINETOS_LOG";

/// How many daily log files each process keeps, today's included. Older files
/// of the same process are deleted when a process starts and at each daily
/// rollover. Crash traces are never deleted.
pub const KEPT_LOG_FILES: usize = 14;

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
    /// Write the JSON Lines file. When `false`, events go to stderr only, and
    /// only warnings and errors unless `CABINETOS_LOG` asks for more; the CLI
    /// runs this way unless it is given a log directory.
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
    /// The log directory could not be created.
    #[error("cannot create the log directory {dir}: {source}")]
    LogDir {
        /// The directory that was tried.
        dir: PathBuf,
        /// What went wrong.
        #[source]
        source: std::io::Error,
    },
    /// The log file could not be created.
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
    /// Whether the process writes log files at all; heavy mode needs them.
    log_file: bool,
}

/// Set when `CABINETOS_LOG_HEAVY` decided heavy mode: the configuration
/// cannot change it then.
static HEAVY_FROM_ENV: AtomicBool = AtomicBool::new(false);

/// How long switching heavy mode off waits for the heavy writer to write
/// what is queued and close its file.
const HEAVY_CLOSE_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(5);

static PROCESS: OnceLock<ProcessInfo> = OnceLock::new();

/// The background writer's guard. The panic hook takes it out and drops it,
/// which flushes queued events before the process dies.
static WORKER: OnceLock<Mutex<Option<WorkerGuard>>> = OnceLock::new();

static INITIALIZED: AtomicBool = AtomicBool::new(false);

/// Changes the filter after [`init`]. Unset when `CABINETOS_LOG` chose the
/// filter: the environment wins over the configuration file.
static LEVEL: OnceLock<Box<dyn Fn(tracing::Level) -> bool + Send + Sync>> = OnceLock::new();

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
    let filter_from_env = filter_text
        .as_deref()
        .is_some_and(|text| !text.trim().is_empty())
        && bad_filter.is_none();
    let (level_filter, level_handle) = reload::Layer::new(filter);

    let (file_layer, worker_guard) = if log_file {
        // The appender prunes old files before it creates the directory, and
        // prints an error to stderr when the directory is not there yet.
        std::fs::create_dir_all(&log_dir).map_err(|source| DiagError::LogDir {
            dir: log_dir.clone(),
            source,
        })?;
        let appender = RollingFileAppender::builder()
            .rotation(Rotation::DAILY)
            .filename_prefix(process)
            .filename_suffix("jsonl")
            .max_log_files(KEPT_LOG_FILES)
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
    // When stderr is the only output (the CLI), it is also where the program
    // prints its results, so keep it to warnings unless asked for more.
    let stderr_level = if !log_file && filter_text.is_none() {
        LevelFilter::WARN
    } else {
        LevelFilter::TRACE
    };
    let stderr_layer = stderr_wanted.then(|| {
        tracing_subscriber::fmt::layer()
            .pretty()
            .with_writer(std::io::stderr)
            .with_ansi(std::io::stderr().is_terminal())
            .with_filter(stderr_level)
    });

    // Every layer has its own filter. The span IDs are kept for every span
    // that declares one, whatever the level: the trace they carry goes out
    // on the pipe even when nothing is logged. The file, stderr and the ring
    // buffer follow the level.
    // Lines only heavy mode writes stay out of them whatever the level.
    let logged = Layer::and_then(file_layer, stderr_layer)
        .and_then(ring::RingLayer::new(boundary))
        .with_filter(level_filter.and(filter_fn(|metadata| {
            !metadata.target().starts_with(HEAVY_TARGET_PREFIX)
        })));
    // Heavy mode has a filter of its own: `CABINETOS_LOG` does not limit it.
    let heavy_layer =
        log_file.then(|| heavy::HeavyLayer { boundary }.with_filter(heavy::HeavyFilter));
    let subscriber = Registry::default()
        .with(format::SpanIdsLayer.with_filter(filter_fn(format::is_id_span)))
        .with(logged)
        .with(heavy_layer);
    tracing::subscriber::set_global_default(subscriber)
        .map_err(|_| DiagError::SubscriberAlreadySet)?;

    let _ = WORKER.set(Mutex::new(worker_guard));
    if !filter_from_env {
        let _ = LEVEL.set(Box::new(move |level| {
            // `modify` also recomputes which log statements are enabled, so
            // a disabled statement stays a single cached check (Article 1).
            level_handle
                .modify(|filter| *filter = Targets::new().with_default(level))
                .is_ok()
        }));
    }
    let _ = PROCESS.set(ProcessInfo {
        process,
        boundary,
        log_dir: log_dir.clone(),
        log_file,
    });
    panic::install_panic_hook();

    if let Some(text) = bad_filter {
        tracing::warn!(
            filter = %text,
            "ignoring {LOG_FILTER_ENV}: it is not a valid filter; logging at info"
        );
    }
    heavy_from_env();
    Ok(DiagGuard { log_dir })
}

/// Applies `CABINETOS_LOG_HEAVY` when it is set; from then on the
/// configuration cannot change heavy mode.
fn heavy_from_env() {
    match parse_heavy(std::env::var(LOG_HEAVY_ENV).ok().as_deref()) {
        Ok(Some(on)) => {
            HEAVY_FROM_ENV.store(true, Ordering::SeqCst);
            apply_heavy(on);
        }
        Ok(None) => {}
        Err(text) => tracing::warn!(
            value = %text,
            "ignoring {LOG_HEAVY_ENV}: expected 1 or 0; the configuration decides"
        ),
    }
}

/// Turns heavy mode on or off, for example from `logging.heavy` in
/// `cabinetos.json`; it applies at once. Switching off writes what is
/// queued and closes the heavy file. Returns `false`, and changes nothing,
/// before [`init`], in a process that writes no log file, or when
/// `CABINETOS_LOG_HEAVY` decided: the environment wins over the
/// configuration file.
pub fn set_heavy(on: bool) -> bool {
    if HEAVY_FROM_ENV.load(Ordering::SeqCst) {
        return false;
    }
    apply_heavy(on)
}

fn apply_heavy(on: bool) -> bool {
    let Some(process) = PROCESS.get().filter(|process| process.log_file) else {
        return false;
    };
    if on {
        let started = heavy::HEAVY.get_or_init(|| {
            heavy::Heavy::start(
                process.log_dir.clone(),
                process.process,
                process.boundary,
                heavy::Limits::DEFAULT,
            )
            .map_err(|error| error.to_string())
        });
        if let Err(error) = started {
            tracing::error!(%error, "cannot start the heavy log writer; heavy logging stays off");
            return false;
        }
        if !heavy::set_on(true) {
            // Log statements that were off for every layer come on now.
            tracing::callsite::rebuild_interest_cache();
            tracing::info!(
                dir = %process.log_dir.display(),
                queue_cap_bytes = HEAVY_QUEUE_CAP,
                disk_cap_bytes = HEAVY_DISK_CAP,
                "heavy logging is on"
            );
        }
    } else if heavy_enabled() {
        // Logged first, so it is the heavy file's last line too.
        tracing::info!("heavy logging is off");
        heavy::set_on(false);
        tracing::callsite::rebuild_interest_cache();
        if let Some(Ok(heavy)) = heavy::HEAVY.get() {
            heavy.queue.flush(true, HEAVY_CLOSE_TIMEOUT);
        }
    }
    true
}

/// `CABINETOS_LOG_HEAVY`: `1` or `true` on, `0` or `false` off, unset or
/// empty for the configuration to decide; anything else is returned to be
/// warned about.
fn parse_heavy(text: Option<&str>) -> Result<Option<bool>, String> {
    match text.map(str::trim).filter(|text| !text.is_empty()) {
        None => Ok(None),
        Some("1" | "true") => Ok(Some(true)),
        Some("0" | "false") => Ok(Some(false)),
        Some(other) => Err(other.to_owned()),
    }
}

/// Sets the least important level this process logs, for example from
/// `logging.level` in `cabinetos.json`. Returns `false`, and changes nothing,
/// before [`init`] or when `CABINETOS_LOG` set the filter: a filter given
/// for one run wins over the configuration file.
pub fn set_level(level: tracing::Level) -> bool {
    LEVEL.get().is_some_and(|set| set(level))
}

/// A span for one request that is an action of its own (no trace came with
/// it): the request's ID is also its trace. Everything logged inside it
/// carries the ID as `request_id` and as `trace_id`, in this process's log
/// and in the crash trace.
pub fn span_for_request(id: &RequestId) -> tracing::Span {
    span_for_action(id, id)
}

/// A span for one request of a user action. Everything logged inside it
/// carries the request's ID as `request_id` and the action's trace as
/// `trace_id`; a job or plugin call started inside it carries the trace
/// on, and [`current_trace`] finds it for the messages sent inside it.
pub fn span_for_action(id: &RequestId, trace: &RequestId) -> tracing::Span {
    tracing::info_span!("request", trace_id = %trace, request_id = %id)
}

/// The trace of the user action the calling code works for: the
/// `trace_id` of the innermost span around it that has one. `None` outside
/// every traced span (a watcher, a timer: nobody's action) and before
/// [`init`].
#[must_use]
pub fn current_trace() -> Option<RequestId> {
    let text = tracing::dispatcher::get_default(|dispatch| {
        let registry = dispatch.downcast_ref::<Registry>()?;
        let current = registry.current_span();
        let span = registry.span(current.id()?)?;
        span.scope().find_map(|span| {
            span.extensions()
                .get::<format::SpanIds>()
                .and_then(|ids| ids.trace_id.clone())
        })
    })?;
    text.parse().ok()
}

/// The log directory of this machine's processes: `explicit` when given,
/// else `CABINETOS_LOG_DIR`, else `%LOCALAPPDATA%\CabinetOS\logs`. For
/// tools that read the logs, such as `cabinetos-cli log trace`.
#[must_use]
pub fn log_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_log_dir(
        explicit,
        std::env::var_os(LOG_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
    )
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
    // The heavy file first: it is written by its own thread, which the
    // panic hook must not wait for when it is the thread that panicked.
    let on_heavy_writer = std::thread::current().name() == Some(heavy::WRITER_THREAD);
    if !on_heavy_writer && let Some(Ok(heavy)) = heavy::HEAVY.get() {
        heavy.queue.flush(true, std::time::Duration::from_secs(2));
    }
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
    fn heavy_mode_from_the_environment_is_one_or_zero() {
        assert_eq!(parse_heavy(None), Ok(None));
        assert_eq!(parse_heavy(Some(" ")), Ok(None));
        assert_eq!(parse_heavy(Some("1")), Ok(Some(true)));
        assert_eq!(parse_heavy(Some("false")), Ok(Some(false)));
        assert_eq!(parse_heavy(Some("yes")), Err("yes".to_owned()));
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
