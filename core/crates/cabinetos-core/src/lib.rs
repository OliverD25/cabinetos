//! The headless CabinetOS core: process startup, the pipe server, session
//! lifetime, and the wiring of every library crate.
//!
//! The UI is only a view (brief §1, the Dumb UI Rule): all work happens here,
//! behind the named pipe. The core lists directories into shared memory,
//! keeps watched listings current with events, reports volumes and disks,
//! owns the configuration file, the commands, the keymap and the colour
//! themes, runs the jobs, the Core Plugins and the terminal sessions,
//! installs extensions from the marketplace, updates its own install (a
//! per-user release), logs every request with its ID, and exits with its
//! parent process. The protocol is in
//! `docs/ipc.md`.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: every
//! connection is served asynchronously, so no request waits on another),
//! Article 6 (Universal Configuration: an edit to `cabinetos.json` or to a
//! theme file takes effect at once), Article 7 (Absolute Keyboard Control: the keymap and
//! the Immutable System Tier live here), Article 8 (Sandboxed
//! Extensibility: a plugin that crashes is removed and the core goes on;
//! themes are JSON files; an installed plugin waits for the user's review),
//! Article 9 (Workspace & Terminal Integration: shells in pseudo-consoles,
//! started only when a client asks), Article 10 (The Zero-Bloat
//! Foundation: the core is the bare navigation engine; features arrive as
//! extensions) and Article 12 (Unified Diagnostics: each request is handled
//! inside a span carrying its ID).
#![forbid(unsafe_code)]

mod connection;
mod events;
mod icons;
mod listing;
mod market;
mod measure;
mod plugins;
mod preview;
mod programs;
mod quickview;
mod search;
mod secrets;
mod settings;
mod terminal;
mod themes;
mod update;
mod volumes;
mod window;
mod workspace;

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use cabinetos_diag::{Boundary, DiagConfig, DiagError};
use cabinetos_ipc::{IpcError, PipeName, PipeServer};
use cabinetos_jobs::{EngineConfig, JobQueueManager};
use cabinetos_protocol::{Envelope, ErrorCode, PROTOCOL_VERSION, Request, RequestId};
use serde_json::Value;
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;
use tracing::Instrument;

use crate::events::{EventHub, Services};
use crate::settings::Settings;

/// The core's version, reported in `pong`.
pub const CORE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Environment variable that sets the number of async worker threads.
pub const WORKERS_ENV: &str = "CABINETOS_WORKERS";

/// Environment variable that sets the undo folder (the journal and the
/// saved copies, docs/jobs.md "Undo"); tests point it at a folder of their
/// own. Default: `%LOCALAPPDATA%\CabinetOS\undo`.
pub const UNDO_DIR_ENV: &str = "CABINETOS_UNDO_DIR";

/// Environment variable that sets the core's cache folder (the drawings of
/// `render_image` go into its `render` folder); tests point it at a folder
/// of their own. Default: `%LOCALAPPDATA%\CabinetOS\cache`.
pub const CACHE_DIR_ENV: &str = "CABINETOS_CACHE_DIR";

/// Async worker threads when `CABINETOS_WORKERS` is not set. The workers only
/// route messages and wait for events; disk work runs on blocking threads
/// (`spawn_blocking`) or on dedicated threads (directory watchers), never on
/// a worker.
pub const DEFAULT_WORKERS: usize = 4;

/// Largest accepted `CABINETOS_WORKERS` value.
const MAX_WORKERS: usize = 64;

/// The async worker count for a `CABINETOS_WORKERS` value: the value itself
/// when it is a whole number from 1 to 64, otherwise [`DEFAULT_WORKERS`]. The
/// second element returns a rejected value, so the caller can warn about it.
#[must_use]
pub fn worker_threads(value: Option<&str>) -> (usize, Option<String>) {
    let Some(text) = value.map(str::trim).filter(|text| !text.is_empty()) else {
        return (DEFAULT_WORKERS, None);
    };
    match text.parse::<usize>() {
        Ok(count) if (1..=MAX_WORKERS).contains(&count) => (count, None),
        _ => (DEFAULT_WORKERS, Some(text.to_owned())),
    }
}

/// How long open connections may take to finish once shutdown starts.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(2);

/// How long running jobs may take to stop once shutdown starts. A copy that
/// is cancelled removes its partial file first.
const JOBS_GRACE: Duration = Duration::from_secs(2);

/// Pause after a failed accept, so a persistent failure cannot spin a CPU.
const ACCEPT_RETRY: Duration = Duration::from_millis(100);

/// Longest request type echoed back in an error message.
const MAX_ECHOED_TYPE: usize = 64;

/// How to start the core.
#[derive(Clone, Debug)]
pub struct CoreConfig {
    /// The pipe to listen on.
    pub pipe: PipeName,
    /// Exit when this process exits: the UI that started the core.
    pub parent_pid: Option<u32>,
    /// Log directory; `None` uses `CABINETOS_LOG_DIR` or the default.
    pub log_dir: Option<PathBuf>,
    /// The configuration file; `None` uses `CABINETOS_CONFIG` or
    /// `%APPDATA%\CabinetOS\cabinetos.json`.
    pub config_path: Option<PathBuf>,
    /// The plugins folder; `None` uses `CABINETOS_PLUGINS_DIR` or
    /// `%LOCALAPPDATA%\CabinetOS\plugins`.
    pub plugins_dir: Option<PathBuf>,
    /// The folder of the plugins' own folders; `None` uses
    /// `CABINETOS_PLUGINS_DATA_DIR` or `%LOCALAPPDATA%\CabinetOS\plugins-data`.
    pub plugins_data_dir: Option<PathBuf>,
    /// The themes folder; `None` uses `CABINETOS_THEMES_DIR` or
    /// `%LOCALAPPDATA%\CabinetOS\themes`.
    pub themes_dir: Option<PathBuf>,
    /// The folder the marketplace installs Tool Extensions into; `None`
    /// uses `%LOCALAPPDATA%\CabinetOS\tools`, where the window reads them.
    pub tools_dir: Option<PathBuf>,
    /// The marketplace's own folder (the index cache, downloads, the record
    /// of installs); `None` uses `CABINETOS_MARKETPLACE_DIR` or
    /// `%LOCALAPPDATA%\CabinetOS\marketplace`.
    pub marketplace_dir: Option<PathBuf>,
    /// The updater's own folder (its state, the staged download); `None`
    /// uses `CABINETOS_UPDATE_DIR` or `%LOCALAPPDATA%\CabinetOS\update`.
    pub update_dir: Option<PathBuf>,
}

/// Why the core stopped with an error.
#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    /// Diagnostics could not start.
    #[error("cannot start diagnostics: {0}")]
    Diag(#[from] DiagError),
    /// The pipe could not be created.
    #[error("cannot create the pipe: {0}")]
    Pipe(#[from] IpcError),
    /// The parent process could not be watched, usually because it no
    /// longer exists.
    #[error("cannot watch parent process {pid}: {source}")]
    ParentWatch {
        /// The parent's process ID.
        pid: u32,
        /// What went wrong.
        #[source]
        source: IpcError,
    },
    /// A connection task panicked. The crash trace is in the log directory.
    #[error("a connection task panicked; see the crash trace in the log directory")]
    ConnectionPanicked,
    /// A thread of the core panicked. The crash trace is in the log
    /// directory.
    #[error("a thread of the core panicked; see the crash trace in the log directory")]
    Panicked,
}

/// The diagnostics configuration of the core process: process `core`,
/// boundary `engine`.
#[must_use]
pub fn diag_config(log_dir: Option<PathBuf>) -> DiagConfig {
    DiagConfig {
        process: "core",
        boundary: Boundary::Engine,
        dir: log_dir,
        log_file: true,
    }
}

/// Runs the core until `shutdown` is cancelled: by a `shutdown` request, by
/// Ctrl+C, or by the parent process exiting.
///
/// Initializes diagnostics first, so it must be called once per process.
#[expect(
    clippy::too_many_lines,
    reason = "the wiring of every service, in order"
)]
pub async fn run(config: CoreConfig, shutdown: CancellationToken) -> Result<(), CoreError> {
    let CoreConfig {
        pipe,
        parent_pid,
        log_dir,
        config_path,
        plugins_dir,
        plugins_data_dir,
        themes_dir,
        tools_dir,
        marketplace_dir,
        update_dir,
    } = config;
    let diag = cabinetos_diag::init(diag_config(log_dir))?;
    prepare_diagnostics();
    let panicked = stop_on_panic(&shutdown);
    warn_about_workers();
    let config_path = cabinetos_config::default_path(config_path);
    let config_path = std::path::absolute(&config_path).unwrap_or(config_path);
    let events = EventHub::new();
    let settings_events = Arc::clone(&events);
    let settings =
        match tokio::task::spawn_blocking(move || Settings::open(config_path, settings_events))
            .await
        {
            Ok(settings) => settings,
            Err(error) => std::panic::resume_unwind(error.into_panic()),
        };
    // Dropping the watchers at the end stops them without waiting.
    let _watcher = settings.watch();
    let dirs = cabinetos_market::Dirs {
        plugins: cabinetos_plugins::plugins_dir(plugins_dir),
        themes: cabinetos_themes::themes_dir(themes_dir),
        tools: cabinetos_market::tools_dir(tools_dir),
        market: cabinetos_market::marketplace_dir(marketplace_dir),
    };
    let (themes, _theme_watcher) = themes::start(dirs.themes.clone(), &settings, &events).await;
    let job_events = Arc::clone(&events);
    let jobs = JobQueueManager::new(
        EngineConfig {
            undo_dir: undo_dir(),
            ..EngineConfig::default()
        },
        Arc::new(move |event| job_events.publish(event)),
    );
    let secrets = secrets::from_env();
    let plugin_link = Arc::new(plugins::ServicesLink::default());
    let plugins = plugins::start(
        dirs.plugins.clone(),
        cabinetos_plugins::plugins_data_dir(plugins_data_dir),
        &settings,
        &events,
        &jobs,
        &secrets,
        &plugin_link,
    );
    if let Some(host) = &plugins {
        tokio::spawn(plugins::follow_settings(
            Arc::clone(host),
            settings.subscribe(),
        ));
    }
    let market = Arc::new(market::Marketplace::new(
        cabinetos_market::Market::new(dirs, CORE_VERSION),
        Arc::clone(&settings),
        Arc::clone(&events),
        plugins.clone(),
        Arc::clone(&themes),
    ));
    let update_settings = Arc::clone(&settings);
    let update_events = Arc::clone(&events);
    let updates = match tokio::task::spawn_blocking(move || {
        update::open(update_dir, &update_settings, &update_events)
    })
    .await
    {
        Ok(updates) => updates,
        Err(error) => std::panic::resume_unwind(error.into_panic()),
    };
    tokio::spawn(update::run_daily(
        Arc::clone(&updates),
        settings.subscribe(),
    ));
    let terminals = terminal::start(&events, pipe.token());
    let previews = preview::Previews::start(&events);
    // Dropping the watcher at the end stops it.
    let _drives = volumes::watch(Arc::clone(&events));
    let hydrator = Arc::new(cabinetos_fs::Hydrator::new());
    // The window asks for these first, and the shell draws one icon at a
    // time: they are drawn now, while the window builds itself.
    icons::at_start(&hydrator, &shutdown);
    let thumbnails = quickview::Thumbnails::new(&cache_dir());
    let clearing = Arc::clone(&thumbnails);
    drop(tokio::task::spawn_blocking(move || {
        clearing.clear_renders();
    }));
    let services = Arc::new(Services {
        settings,
        jobs,
        events,
        plugins,
        indexer: search::IndexerLink::from_env(),
        terminals,
        hydrator,
        thumbnails,
        themes,
        market,
        windows: window::WindowStates::default(),
        shell_menus: cabinetos_fs::ShellMenus::new(),
        previews,
        secrets,
        updates,
    });
    plugin_link.set(&services);
    let mut result = serve(&pipe, parent_pid, &shutdown, diag.log_dir(), &services).await;
    if result.is_ok() && panicked.load(Ordering::SeqCst) {
        result = Err(CoreError::Panicked);
    }
    // A download in progress ends, so it does not keep the core alive.
    services.updates.stop();
    // The shells get their hang-up; together they may take up to 2 s to end.
    let closing = Arc::clone(&services.terminals);
    let _ = tokio::task::spawn_blocking(move || closing.shutdown()).await;
    if let Some(host) = &services.plugins {
        host.shutdown();
    }
    let stopping = Arc::clone(&services);
    let _ = tokio::task::spawn_blocking(move || stopping.jobs.shutdown(JOBS_GRACE)).await;
    match &result {
        Ok(()) => tracing::info!("core stopped"),
        Err(error) => tracing::error!(%error, "core stopped with an error"),
    }
    drop(diag);
    result
}

/// The undo folder: `CABINETOS_UNDO_DIR`, else
/// `%LOCALAPPDATA%\CabinetOS\undo`; none outside a normal Windows session.
fn undo_dir() -> Option<std::path::PathBuf> {
    std::env::var_os(UNDO_DIR_ENV)
        .filter(|dir| !dir.is_empty())
        .map(std::path::PathBuf::from)
        .or_else(|| {
            std::env::var_os("LOCALAPPDATA").map(|local| {
                std::path::PathBuf::from(local)
                    .join("CabinetOS")
                    .join("undo")
            })
        })
}

/// The cache folder: `CABINETOS_CACHE_DIR`, else
/// `%LOCALAPPDATA%\CabinetOS\cache`, else under the temp folder.
fn cache_dir() -> PathBuf {
    std::env::var_os(CACHE_DIR_ENV)
        .filter(|dir| !dir.is_empty())
        .map_or_else(
            || {
                std::env::var_os("LOCALAPPDATA")
                    .filter(|dir| !dir.is_empty())
                    .map_or_else(std::env::temp_dir, PathBuf::from)
                    .join("CabinetOS")
                    .join("cache")
            },
            PathBuf::from,
        )
}

/// Logs a `CABINETOS_WORKERS` value that `worker_threads` refused.
fn warn_about_workers() {
    if let (_, Some(rejected)) = worker_threads(std::env::var(WORKERS_ENV).ok().as_deref()) {
        tracing::warn!(
            value = %rejected,
            "ignoring {WORKERS_ENV}: expected a whole number from 1 to {MAX_WORKERS}; using {DEFAULT_WORKERS}"
        );
    }
}

/// What the core adds to its diagnostics once they run: this thread runs the
/// pipe server, so it never waits for the heavy log; and log bundles name
/// the Windows build.
fn prepare_diagnostics() {
    cabinetos_diag::never_wait_for_heavy_log();
    if let Some(build) = cabinetos_fs::windows_build() {
        cabinetos_diag::set_bundle_windows_build(build);
    }
}

/// Makes a panic on any thread stop the core, as a panic in a connection
/// task already does: the panic hook has written the crash trace and closed
/// the log writer, so running on would mean running without the thread
/// that panicked and without a log (fail fast; the window starts a new
/// core). Returns the flag that says a panic happened.
fn stop_on_panic(shutdown: &CancellationToken) -> Arc<AtomicBool> {
    let panicked = Arc::new(AtomicBool::new(false));
    let flag = Arc::clone(&panicked);
    let token = shutdown.clone();
    cabinetos_diag::on_panic(move || {
        flag.store(true, Ordering::SeqCst);
        // Cancelled from a thread of its own: the panicking thread may hold
        // a lock that cancelling needs, and it releases its locks as it
        // unwinds.
        let token = token.clone();
        let _ = std::thread::Builder::new()
            .name("panic-stop".to_owned())
            .spawn(move || token.cancel());
    });
    panicked
}

async fn serve(
    pipe: &PipeName,
    parent_pid: Option<u32>,
    shutdown: &CancellationToken,
    log_dir: &Path,
    services: &Arc<Services>,
) -> Result<(), CoreError> {
    let mut server = PipeServer::bind(pipe)?;

    if let Some(pid) = parent_pid {
        let token = shutdown.clone();
        cabinetos_ipc::process::watch_process_exit(pid, move || {
            tracing::info!(parent_pid = pid, "parent process exited; shutting down");
            token.cancel();
        })
        .map_err(|source| CoreError::ParentWatch { pid, source })?;
    }

    let ctrl_c = shutdown.clone();
    tokio::spawn(async move {
        match tokio::signal::ctrl_c().await {
            Ok(()) => {
                tracing::info!("Ctrl+C received; shutting down");
                ctrl_c.cancel();
            }
            Err(error) => tracing::warn!(%error, "cannot listen for Ctrl+C"),
        }
    });

    tracing::info!(
        pipe = %pipe,
        version = CORE_VERSION,
        protocol_version = PROTOCOL_VERSION,
        pid = std::process::id(),
        parent_pid,
        workers = tokio::runtime::Handle::current().metrics().num_workers(),
        log_dir = %log_dir.display(),
        config = %services.settings.path().display(),
        "core started"
    );

    let mut connections = JoinSet::new();
    let mut connection_number: u64 = 0;
    let outcome = loop {
        tokio::select! {
            () = shutdown.cancelled() => break Ok(()),
            accepted = server.accept() => match accepted {
                Ok(connection) => {
                    connection_number += 1;
                    let span = tracing::debug_span!("connection", connection = connection_number);
                    connections.spawn(
                        connection::handle_connection(
                            connection,
                            shutdown.clone(),
                            Arc::clone(services),
                        )
                        .instrument(span),
                    );
                }
                Err(error) => {
                    tracing::warn!(%error, "accepting a client failed");
                    tokio::select! {
                        () = shutdown.cancelled() => break Ok(()),
                        () = tokio::time::sleep(ACCEPT_RETRY) => {}
                    }
                }
            },
            Some(joined) = connections.join_next() => {
                // A panic is a bug. The panic hook has written the crash trace
                // and closed the log writer, so stop instead of running on
                // without a log (fail fast; the UI restarts the core).
                if joined.is_err_and(|error| error.is_panic()) {
                    shutdown.cancel();
                    break Err(CoreError::ConnectionPanicked);
                }
            }
        }
    };

    // Stop accepting: dropping the server removes the pipe name.
    drop(server);
    let drained = tokio::time::timeout(SHUTDOWN_GRACE, async {
        while connections.join_next().await.is_some() {}
    })
    .await;
    if drained.is_err() {
        tracing::debug!(
            open = connections.len(),
            "closing connections that did not finish in time"
        );
        connections.shutdown().await;
    }
    outcome
}

/// A frame that is not a valid request.
#[derive(Debug)]
pub(crate) struct Rejection {
    /// The frame's ID, when it had a valid one; the reply echoes it.
    pub(crate) id: Option<RequestId>,
    /// The frame's trace, when it had a valid one; the rejection is logged
    /// under it.
    pub(crate) trace: Option<RequestId>,
    pub(crate) code: ErrorCode,
    pub(crate) message: String,
}

/// Parses a frame as a request envelope, and classifies a failure:
/// `unknown_request` when the envelope is fine but its `type` is not one this
/// core knows (a newer UI, for example), `protocol_error` for everything else.
pub(crate) fn decode_request(frame: &[u8]) -> Result<Envelope<Request>, Rejection> {
    let value: Value = serde_json::from_slice(frame).map_err(|error| Rejection {
        id: None,
        trace: None,
        code: ErrorCode::ProtocolError,
        message: format!("the frame is not valid JSON: {error}"),
    })?;
    let id = value
        .get("id")
        .and_then(Value::as_str)
        .and_then(|text| text.parse::<RequestId>().ok());
    let trace = value
        .get("trace")
        .and_then(Value::as_str)
        .and_then(|text| text.parse::<RequestId>().ok());
    let kind = value
        .get("type")
        .and_then(Value::as_str)
        .map(|kind| kind.chars().take(MAX_ECHOED_TYPE).collect::<String>());

    serde_json::from_value(value).map_err(|error| match kind {
        Some(kind) if id.is_some() && !Request::TYPES.contains(&kind.as_str()) => Rejection {
            id,
            trace,
            code: ErrorCode::UnknownRequest,
            message: format!("unknown request type `{kind}`"),
        },
        _ => Rejection {
            id,
            trace,
            code: ErrorCode::ProtocolError,
            message: format!("not a valid request envelope: {error}"),
        },
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const ID: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";

    #[test]
    fn worker_count_defaults_to_four_and_rejects_nonsense() {
        assert_eq!(worker_threads(None), (DEFAULT_WORKERS, None));
        assert_eq!(worker_threads(Some("  ")), (DEFAULT_WORKERS, None));
        assert_eq!(worker_threads(Some("8")), (8, None));
        assert_eq!(worker_threads(Some(" 1 ")), (1, None));
        for bad in ["0", "65", "-2", "four", "2.5"] {
            assert_eq!(
                worker_threads(Some(bad)),
                (DEFAULT_WORKERS, Some(bad.to_owned())),
                "{bad}"
            );
        }
    }

    #[test]
    fn decodes_a_valid_request() {
        let envelope =
            decode_request(br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ping"}"#).unwrap();
        assert_eq!(envelope.id.as_str(), ID);
        assert_eq!(envelope.body, Request::Ping);
    }

    #[test]
    fn an_unknown_type_is_unknown_request_and_keeps_the_id() {
        let rejection =
            decode_request(br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"format_disk"}"#)
                .unwrap_err();
        assert_eq!(rejection.code, ErrorCode::UnknownRequest);
        assert_eq!(rejection.id.unwrap().as_str(), ID);
        assert!(rejection.message.contains("format_disk"));
    }

    #[test]
    fn everything_else_is_a_protocol_error() {
        let cases: [(&[u8], bool); 6] = [
            (b"not json", false),
            (b"[1,2,3]", false),
            (br#"{"type":"ping"}"#, false),
            (br#"{"id":"nope","type":"ping"}"#, false),
            (br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":5}"#, true),
            (br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W"}"#, true),
        ];
        for (frame, has_id) in cases {
            let rejection = decode_request(frame).unwrap_err();
            assert_eq!(
                rejection.code,
                ErrorCode::ProtocolError,
                "{}",
                String::from_utf8_lossy(frame)
            );
            assert_eq!(
                rejection.id.is_some(),
                has_id,
                "{}",
                String::from_utf8_lossy(frame)
            );
        }
    }

    #[test]
    fn a_long_unknown_type_is_cut_in_the_reply() {
        let long_type = "x".repeat(10_000);
        let frame = format!(r#"{{"id":"{ID}","type":"{long_type}"}}"#);
        let rejection = decode_request(frame.as_bytes()).unwrap_err();
        assert_eq!(rejection.code, ErrorCode::UnknownRequest);
        assert!(rejection.message.len() < 200);
    }
}
