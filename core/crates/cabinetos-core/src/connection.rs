//! One client connection.
//!
//! Three kinds of task share a connection:
//!
//! - a reader task that forwards frames to the session loop (reading a frame
//!   is not cancel-safe, so it cannot sit in a `select!`);
//! - a writer task that writes replies and events in the order they were
//!   queued, so a `listing_opened` always goes out before the events of that
//!   listing;
//! - the session loop, which answers quick requests itself and runs slow ones
//!   (`list_directory`, `describe_entries`, `match_entries`, `get_icon`, `volume_info`,
//!   `list_volumes`, `open_path`, `edit_path`, `show_properties`, `create_directory`,
//!   `create_file`, `rename`, `measure_paths`,
//!   `set_value`,
//!   the keybinding and plugin settings writes, `start_job`, a plugin's command, `reload_plugin`, `search`,
//!   `index_status`, `terminal_open`, `terminal_close`, `terminal_sync_cwd`,
//!   `list_themes`, `get_theme` of a named theme, `list_tools` and the
//!   marketplace requests)
//!   as tasks, so one slow directory, plugin, search, shell or download
//!   never holds up the next request. After `hello` it also forwards the configuration,
//!   theme, job, plugin, terminal and volume events every connection
//!   receives.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use cabinetos_diag::span_for_request;
use cabinetos_fs::{DirectoryWatcher, ListOptions};
use cabinetos_ipc::{IpcError, PipeConnection};
use cabinetos_protocol::{
    Envelope, ErrorCode, Event, JobRequest, MAX_DESCRIBED, PROTOCOL_VERSION, Request, RequestId,
    Response, SortSpec, TerminalState,
};
use serde::Serialize;
use serde_json::Value;
use tokio::sync::broadcast::{self, error::RecvError};
use tokio::sync::mpsc;
use tokio::task::{AbortHandle, JoinError, JoinSet};
use tokio_util::sync::CancellationToken;
use tracing::Instrument;

use crate::events::Services;
use crate::listing::{self, CurrentSection, Failure, Published, WatchedListing};
use crate::plugins::check_grants;
use crate::settings::{Settings, every_section};
use crate::{CORE_VERSION, Rejection, decode_request, terminal, volumes};
use crate::{measure, search};

/// How long the writer may take to send what is still queued when the
/// connection ends.
const FINAL_WRITE_GRACE: Duration = Duration::from_secs(1);

/// Listing IDs are unique across all connections, so a listing ID in the log
/// names one listing.
static NEXT_LISTING_ID: AtomicU64 = AtomicU64::new(1);

/// Measure IDs are unique across all connections, as listing IDs are.
static NEXT_MEASURE_ID: AtomicU64 = AtomicU64::new(1);

/// Queues messages for the connection's writer task.
#[derive(Clone, Debug)]
pub(crate) struct Outbox(pub(crate) mpsc::UnboundedSender<Vec<u8>>);

impl Outbox {
    /// Queues `message`. If the client is gone the message is dropped: there
    /// is nobody left to tell.
    pub(crate) fn send<T: Serialize>(&self, message: &T) {
        match serde_json::to_vec(message) {
            Ok(frame) => {
                let _ = self.0.send(frame);
            }
            Err(error) => tracing::error!(%error, "cannot serialize a message"),
        }
    }

    pub(crate) fn reply(&self, id: RequestId, response: Response) {
        self.send(&Envelope::new(id, response));
    }
}

/// The client, after `hello`.
struct Client {
    pid: u32,
}

/// What the session keeps for each open listing.
struct ListingSlot {
    /// The folder, as the client named it.
    path: String,
    /// The section clients read now, shared with a watched listing's
    /// refresh task. Dropping the last reference closes the core's handle.
    current: Arc<CurrentSection>,
    /// A watched listing's refresh task, until it ends (the directory was
    /// lost); only `close_listing` is left to do then.
    refresh: Option<AbortHandle>,
}

/// What a finished task reports to the session loop.
enum TaskDone {
    /// A `list_directory` request has its result.
    ListingReady {
        request_id: RequestId,
        listing_id: u64,
        started: Instant,
        result: Result<Opened, Failure>,
    },
    /// A watched listing was lost and its refresh task ended.
    RefreshEnded { listing_id: u64 },
    /// A measure sent its last event, or was refused.
    MeasureEnded { measure_id: u64 },
    /// The task sent what it had to: its reply, or events sent again.
    Replied,
}

/// A listing that is ready to be announced.
struct Opened {
    path: String,
    options: ListOptions,
    published: Published,
    watch: Option<(
        DirectoryWatcher,
        mpsc::UnboundedReceiver<cabinetos_fs::DirectoryChanged>,
    )>,
}

/// Serves one client until it disconnects or sends a malformed frame.
pub(crate) async fn handle_connection(
    connection: PipeConnection,
    shutdown: CancellationToken,
    services: Arc<Services>,
) {
    tracing::debug!("client connected");
    let client_pid_from_windows = connection.client_process_id().ok();
    let (mut reader, mut writer) = connection.into_split();

    let (out_tx, mut out_rx) = mpsc::unbounded_channel::<Vec<u8>>();
    let writer_task = tokio::spawn(async move {
        while let Some(frame) = out_rx.recv().await {
            if let Err(error) = writer.write_frame(&frame).await {
                tracing::debug!(%error, "cannot write to the client");
                break;
            }
        }
    });

    let (frames_tx, mut frames_rx) = mpsc::channel::<Result<Vec<u8>, IpcError>>(8);
    let reader_task = tokio::spawn(async move {
        loop {
            let frame = reader.read_frame().await;
            let last = frame.is_err();
            if frames_tx.send(frame).await.is_err() || last {
                break;
            }
        }
    });

    let mut session = Session {
        out: Outbox(out_tx),
        client: None,
        client_pid_from_windows,
        listings: HashMap::new(),
        tasks: JoinSet::new(),
        shutdown,
        services,
        events: None,
        last_listed: None,
        measures: HashMap::new(),
    };
    loop {
        tokio::select! {
            frame = frames_rx.recv() => match frame {
                Some(Ok(frame)) => session.handle_frame(&frame),
                Some(Err(IpcError::Closed)) | None => {
                    tracing::debug!("client disconnected");
                    break;
                }
                Some(Err(IpcError::FrameTooLarge { len, max })) => {
                    session.reject_frame_too_large(len, max);
                    break;
                }
                // The pipe ended inside a frame: the client went away (a
                // window that crashed while it wrote), which breaks no rule.
                Some(Err(IpcError::Io(error)))
                    if matches!(
                        error.kind(),
                        std::io::ErrorKind::UnexpectedEof | std::io::ErrorKind::BrokenPipe
                    ) =>
                {
                    tracing::info!(%error, "the client left in the middle of a frame");
                    break;
                }
                Some(Err(error)) => {
                    tracing::warn!(%error, "malformed frame; closing the connection");
                    break;
                }
            },
            Some(done) = session.tasks.join_next() => session.task_done(done),
            event = next_event(session.events.as_mut()) => session.forward_event(event),
        }
    }

    reader_task.abort();
    rethrow_panic(reader_task.await);
    // A measure counts on a pool thread, which aborting its task does not
    // stop.
    session.stop_measures();
    // Aborting the tasks drops every watcher and section of this connection.
    session.tasks.shutdown().await;
    drop(session);
    // All senders are gone now, so the writer ends once the queue is empty.
    if let Ok(result) = tokio::time::timeout(FINAL_WRITE_GRACE, writer_task).await {
        rethrow_panic(result);
    } else {
        tracing::debug!("the client stopped reading; dropping unsent messages");
    }
}

/// The next configuration event, once the client said `hello`; until then,
/// never.
async fn next_event(
    events: Option<&mut broadcast::Receiver<Envelope<Event>>>,
) -> Result<Envelope<Event>, RecvError> {
    match events {
        Some(events) => events.recv().await,
        None => std::future::pending().await,
    }
}

/// Keeps the core's fail-fast rule: a panic in any task stops the core.
fn rethrow_panic(result: Result<(), JoinError>) {
    if let Err(error) = result
        && error.is_panic()
    {
        std::panic::resume_unwind(error.into_panic());
    }
}

struct Session {
    out: Outbox,
    client: Option<Client>,
    /// The client's process ID as Windows reports it, to check `hello`.
    client_pid_from_windows: Option<u32>,
    listings: HashMap<u64, ListingSlot>,
    tasks: JoinSet<TaskDone>,
    shutdown: CancellationToken,
    services: Arc<Services>,
    /// Configuration and job events, from `hello` on.
    events: Option<broadcast::Receiver<Envelope<Event>>>,
    /// The folder this connection listed last: where a search without a
    /// root walks, when no indexer answers.
    last_listed: Option<String>,
    /// The running measures of this connection, each with the flag that
    /// stops it.
    measures: HashMap<u64, Arc<AtomicBool>>,
}

impl Session {
    fn handle_frame(&mut self, frame: &[u8]) {
        let started = Instant::now();
        let envelope = match decode_request(frame) {
            Ok(envelope) => envelope,
            Err(rejection) => {
                self.reject(rejection);
                return;
            }
        };
        let Envelope { id, body: request } = envelope;
        let span = span_for_request(&id);
        let kind = request.type_tag();
        let reply = {
            let _entered = span.enter();
            match request {
                Request::Ping => Some(Response::Pong {
                    protocol_version: PROTOCOL_VERSION,
                    core_version: CORE_VERSION.to_owned(),
                }),
                Request::Shutdown => {
                    tracing::info!("shutdown requested by a client");
                    self.shutdown.cancel();
                    Some(Response::Ok)
                }
                Request::Hello {
                    client_pid,
                    client_name,
                } => Some(self.hello(client_pid, &client_name)),
                Request::CloseListing { listing_id } => Some(self.close_listing(listing_id)),
                request @ (Request::DescribeEntries { .. }
                | Request::MatchEntries { .. }
                | Request::GetIcon { .. }) => self.entry_request(&id, &span, kind, request),
                Request::ListDirectory {
                    path,
                    include_hidden,
                    sort,
                    watch,
                } => self.list_directory(&id, &span, path, include_hidden, sort, watch),
                request @ (Request::VolumeInfo { .. }
                | Request::ListVolumes
                | Request::OpenPath { .. }
                | Request::EditPath { .. }
                | Request::ShowProperties { .. }
                | Request::CreateDirectory { .. }
                | Request::CreateFile { .. }
                | Request::Rename { .. }) => self.file_request(&id, &span, kind, request),
                request @ (Request::MeasurePaths { .. } | Request::CancelMeasure { .. }) => {
                    self.measure_request(&id, &span, kind, request)
                }
                request @ (Request::GetConfig
                | Request::GetValue { .. }
                | Request::SetValue { .. }
                | Request::GetKeymap
                | Request::ListCommands
                | Request::SearchCommands { .. }
                | Request::SetKeybinding { .. }
                | Request::ResetKeybinding { .. }) => {
                    self.settings_request(&id, &span, kind, request)
                }
                Request::ExecuteCommand { command, args } => {
                    self.execute_command(&id, &span, command, &args)
                }
                request @ (Request::StartJob(_)
                | Request::ListJobs
                | Request::JobControl { .. }
                | Request::ResolveConflict { .. }) => self.job_request(&id, &span, request),
                request @ (Request::ListPlugins
                | Request::ReloadPlugin { .. }
                | Request::SetPluginEnabled { .. }
                | Request::GrantCapabilities { .. }) => {
                    self.plugin_request(&id, &span, kind, request)
                }
                request @ (Request::Search { .. } | Request::IndexStatus) => {
                    self.search_request(&id, &span, kind, request);
                    None
                }
                request @ (Request::TerminalOpen { .. }
                | Request::TerminalResize { .. }
                | Request::TerminalClose { .. }
                | Request::TerminalSyncCwd { .. }
                | Request::TerminalList) => self.terminal_request(&id, &span, kind, request),
                request @ (Request::ListThemes
                | Request::GetTheme { .. }
                | Request::ListTools
                | Request::MarketplaceRefresh
                | Request::MarketplaceSearch { .. }
                | Request::InstallExtension { .. }
                | Request::UninstallExtension { .. }) => {
                    self.extension_request(&id, &span, kind, request)
                }
            }
        };
        // Requests handled right here are done; the others log when they end.
        if let Some(reply) = reply {
            let _entered = span.enter();
            log_handled(kind, started, &reply);
            self.out.reply(id, reply);
        }
    }

    /// Answers a frame that is not a valid request.
    fn reject(&self, rejection: Rejection) {
        let id = rejection.id.unwrap_or_else(RequestId::new);
        let _entered = span_for_request(&id).entered();
        tracing::warn!(code = ?rejection.code, error = %rejection.message, "request rejected");
        self.out.reply(
            id,
            Response::Error {
                code: rejection.code,
                message: rejection.message,
            },
        );
    }

    fn hello(&mut self, client_pid: u32, client_name: &str) -> Response {
        if self.client.is_some() {
            return protocol_error("hello was already received on this connection");
        }
        if let Some(actual) = self.client_pid_from_windows
            && actual != client_pid
        {
            return protocol_error(&format!(
                "client_pid {client_pid} is not the process on the other end of the pipe ({actual})"
            ));
        }
        tracing::info!(client_pid, client_name, "client said hello");
        self.client = Some(Client { pid: client_pid });
        self.events = Some(self.services.events.subscribe());
        // Conflicts raised before this client connected (a restarted UI)
        // still wait for a decision. One raised while this runs may arrive
        // twice; a client keys conflicts by ID.
        for conflict in self.services.jobs.open_conflicts() {
            self.out.send(&Envelope::new(
                RequestId::new(),
                Event::JobConflict(conflict),
            ));
        }
        Response::Welcome {
            protocol_version: PROTOCOL_VERSION,
            core_version: CORE_VERSION.to_owned(),
        }
    }

    fn close_listing(&mut self, listing_id: u64) -> Response {
        let Some(slot) = self.listings.remove(&listing_id) else {
            return no_such_listing(listing_id);
        };
        if let Some(task) = slot.refresh {
            task.abort();
        }
        tracing::debug!(listing_id, "listing closed");
        Response::Ok
    }

    /// The requests about the entries of a listing: `describe_entries`,
    /// `match_entries`, and `get_icon` for the icon keys they give.
    fn entry_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        match request {
            Request::DescribeEntries {
                listing_id,
                from,
                count,
            } => self.describe_entries(id, span, kind, (listing_id, from, count)),
            request @ Request::MatchEntries { .. } => self.match_entries(id, span, kind, request),
            Request::GetIcon { key, size } => self.get_icon(id, span, kind, key, size),
            _ => None,
        }
    }

    /// `measure_paths` and `cancel_measure`.
    fn measure_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        match request {
            Request::MeasurePaths { paths } => self.measure_paths(id, span, kind, paths),
            Request::CancelMeasure { measure_id } => Some(self.cancel_measure(measure_id)),
            _ => None,
        }
    }

    /// `describe_entries`, on the blocking pool: the shell may be asked.
    fn describe_entries(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        (listing_id, from, count): (u64, u32, u32),
    ) -> Option<Response> {
        if count > MAX_DESCRIBED {
            return Some(protocol_error(&format!(
                "count is {count}; at most {MAX_DESCRIBED} entries are described at once"
            )));
        }
        let Some(slot) = self.listings.get(&listing_id) else {
            return Some(no_such_listing(listing_id));
        };
        let (_, section) = slot.current.get();
        let folder = slot.path.clone();
        let hydrator = Arc::clone(&self.services.hydrator);
        self.spawn_reply(id, span, kind, move || {
            let view = match section.map_readonly() {
                Ok(view) => view,
                Err(error) => return failure_reply((ErrorCode::Internal, error.to_string())),
            };
            match hydrator.describe(view.as_slice(), &folder, from, count) {
                Ok((generation, details)) => Response::EntryDetails {
                    listing_id,
                    generation,
                    from,
                    details,
                },
                Err(error) => failure_reply((ErrorCode::Internal, error.to_string())),
            }
        });
        None
    }

    /// `match_entries`, on the blocking pool: it reads every name of the
    /// listing's current section.
    fn match_entries(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        let Request::MatchEntries {
            listing_id,
            patterns,
            files_only,
            first_from,
        } = request
        else {
            return None;
        };
        let Some(slot) = self.listings.get(&listing_id) else {
            return Some(no_such_listing(listing_id));
        };
        let (_, section) = slot.current.get();
        self.spawn_reply(id, span, kind, move || {
            let view = match section.map_readonly() {
                Ok(view) => view,
                Err(error) => return failure_reply((ErrorCode::Internal, error.to_string())),
            };
            let patterns = cabinetos_fs::NamePatterns::parse(&patterns);
            match cabinetos_fs::match_entries(view.as_slice(), &patterns, files_only, first_from) {
                Ok((generation, ranges)) => Response::EntryMatches {
                    listing_id,
                    generation,
                    ranges,
                },
                Err(error) => failure_reply((ErrorCode::Internal, error.to_string())),
            }
        });
        None
    }

    /// `get_icon`, on the blocking pool: the shell draws the icon.
    fn get_icon(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        key: String,
        size: u32,
    ) -> Option<Response> {
        if !cabinetos_fs::ICON_SIZES.contains(&size) {
            return Some(protocol_error(&format!(
                "size is {size}; icons come in 16, 24, 32 or 48 pixels"
            )));
        }
        let hydrator = Arc::clone(&self.services.hydrator);
        self.spawn_reply(id, span, kind, move || {
            match hydrator.icon_png(&key, size) {
                Ok(png) => Response::Icon {
                    key,
                    size,
                    png_base64: BASE64.encode(png),
                },
                Err(error) => failure_reply(listing::fs_failure(&error)),
            }
        });
        None
    }

    /// Starts a listing; the reply comes when the task ends. Returns an
    /// immediate reply only when the request is refused.
    fn list_directory(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        path: String,
        include_hidden: Option<bool>,
        sort: Option<SortSpec>,
        watch: bool,
    ) -> Option<Response> {
        if self.client.is_none() {
            return Some(protocol_error("hello required"));
        }
        let listing_id = NEXT_LISTING_ID.fetch_add(1, Ordering::Relaxed);
        // What the request leaves out, the `panes` settings decide.
        let panes = &self.services.settings.snapshot().config.panes;
        let options = ListOptions {
            include_hidden: include_hidden.unwrap_or(panes.show_hidden),
            sort: sort.unwrap_or_else(|| panes.sort.into()),
            ..ListOptions::default()
        };
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let result = open_listing(listing_id, path, options, watch).await;
                TaskDone::ListingReady {
                    request_id,
                    listing_id,
                    started,
                    result,
                }
            }
            .instrument(span.clone()),
        );
        None
    }

    /// The volume and file requests, as tasks: each asks the disk, the
    /// network or the shell. Answers at once only to refuse a path that is
    /// not absolute: the core's own folder means nothing to a client.
    fn file_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        match request {
            Request::VolumeInfo { path } => self.volume_info(id, span, path),
            Request::ListVolumes => {
                self.spawn_task_reply(id, span, kind, volumes::list_volumes());
            }
            Request::OpenPath { path } => {
                if let Some(refusal) = not_absolute(&path) {
                    return Some(refusal);
                }
                self.spawn_reply(id, span, kind, move || {
                    answer_fs(cabinetos_fs::open_path(&path))
                });
            }
            Request::EditPath { path } => {
                if let Some(refusal) = not_absolute(&path) {
                    return Some(refusal);
                }
                // Read at each edit: a changed editor applies at once.
                let editor = self
                    .services
                    .settings
                    .snapshot()
                    .config
                    .files
                    .editor
                    .clone();
                self.spawn_reply(id, span, kind, move || edit_path(&path, editor));
            }
            // The sheet runs on a thread the shell makes in this process,
            // so it outlives the pool thread that asked for it.
            Request::ShowProperties { paths } => {
                if paths.is_empty() {
                    return Some(protocol_error(
                        "paths is empty; name at least one file or folder",
                    ));
                }
                if let Some(refusal) = paths.iter().find_map(|path| not_absolute(path)) {
                    return Some(refusal);
                }
                self.spawn_reply(id, span, kind, move || {
                    answer_fs(cabinetos_fs::show_properties(&paths))
                });
            }
            // None needs a job: one name, done at once. A watched listing
            // of the folder hears about it from its watcher.
            Request::CreateDirectory { path } => {
                if let Some(refusal) = not_absolute(&path) {
                    return Some(refusal);
                }
                self.spawn_reply(id, span, kind, move || {
                    answer_fs(cabinetos_fs::create_directory(&path))
                });
            }
            Request::CreateFile { path } => {
                if let Some(refusal) = not_absolute(&path) {
                    return Some(refusal);
                }
                self.spawn_reply(id, span, kind, move || {
                    answer_fs(cabinetos_fs::create_file(&path))
                });
            }
            Request::Rename { path, new_name } => {
                if let Some(refusal) = not_absolute(&path) {
                    return Some(refusal);
                }
                self.spawn_reply(id, span, kind, move || {
                    answer_fs(cabinetos_fs::rename(&path, &new_name))
                });
            }
            _ => {}
        }
        None
    }

    /// `measure_paths`: checks the paths and counts them on the blocking
    /// pool. The reply goes out there, before the first event of the
    /// measure; only a path that is not absolute is refused here.
    fn measure_paths(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        paths: Vec<String>,
    ) -> Option<Response> {
        if let Some(refusal) = paths.iter().find_map(|path| not_absolute(path)) {
            return Some(refusal);
        }
        let measure_id = NEXT_MEASURE_ID.fetch_add(1, Ordering::Relaxed);
        let stop = Arc::new(AtomicBool::new(false));
        self.measures.insert(measure_id, Arc::clone(&stop));
        let out = self.out.clone();
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let counted = blocking_in_span(move || {
                    let reply = match measure::check(&paths) {
                        Ok(()) => Response::MeasureStarted { measure_id },
                        Err(refusal) => failure_reply(refusal),
                    };
                    log_handled(kind, started, &reply);
                    let counting = matches!(reply, Response::MeasureStarted { .. });
                    out.reply(request_id, reply);
                    if counting {
                        measure::count(measure_id, paths, &stop, &out);
                    }
                })
                .await;
                if let Err(error) = counted {
                    rethrow_panic(Err(error));
                }
                TaskDone::MeasureEnded { measure_id }
            }
            .instrument(span.clone()),
        );
        None
    }

    /// `cancel_measure`: `ok` whether or not the measure still runs, as a
    /// cancel may cross the measure's end.
    fn cancel_measure(&self, measure_id: u64) -> Response {
        if let Some(stop) = self.measures.get(&measure_id) {
            stop.store(true, Ordering::Relaxed);
            tracing::info!(measure_id, "measure cancel asked");
        }
        Response::Ok
    }

    /// Stops every measure of this connection.
    fn stop_measures(&self) {
        for stop in self.measures.values() {
            stop.store(true, Ordering::Relaxed);
        }
    }

    /// The configuration, command and keymap requests. The reads answer at
    /// once from the settings in effect; the changes write the file on the
    /// blocking pool.
    fn settings_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        let settings = &self.services.settings;
        match request {
            Request::GetConfig => Some(settings.get_config()),
            Request::GetValue { path } => Some(settings.get_value(&path)),
            Request::GetKeymap => Some(settings.get_keymap()),
            Request::ListCommands => Some(settings.list_commands()),
            Request::SearchCommands { query, limit } => {
                Some(settings.search_commands(&query, limit))
            }
            Request::SetValue { path, value } => {
                let themes = Arc::clone(&self.services.themes);
                self.write_setting(id, span, kind, move |settings| {
                    let current = settings.snapshot().config.ui.theme.clone();
                    // A theme that cannot be used is refused here, so the
                    // file never names it because of a client; a hand edit
                    // that names one is reported instead.
                    settings.set_value(&path, value, |config| {
                        if config.ui.theme == current {
                            Ok(())
                        } else {
                            themes.check(&config.ui.theme)
                        }
                    })
                });
                None
            }
            Request::SetKeybinding { command, keys } => {
                self.write_setting(id, span, kind, move |settings| {
                    settings.set_keybinding(&command, &keys)
                });
                None
            }
            Request::ResetKeybinding { command } => {
                self.write_setting(id, span, kind, move |settings| {
                    settings.reset_keybinding(&command)
                });
                None
            }
            _ => None,
        }
    }

    /// The theme, tool and marketplace requests.
    fn extension_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        if let Request::ListThemes | Request::GetTheme { .. } = request {
            return self.theme_request(id, span, kind, request);
        }
        self.market_request(id, span, kind, request);
        None
    }

    /// `list_themes` and `get_theme` of a named theme read theme files, on
    /// the blocking pool; the theme in effect answers at once.
    fn theme_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        let themes = Arc::clone(&self.services.themes);
        match request {
            Request::ListThemes => {
                self.spawn_reply(id, span, kind, move || themes.list());
                None
            }
            Request::GetTheme { theme_id: None } => Some(themes.get(None)),
            Request::GetTheme {
                theme_id: Some(theme_id),
            } => {
                self.spawn_reply(id, span, kind, move || themes.get(Some(&theme_id)));
                None
            }
            _ => None,
        }
    }

    /// The marketplace requests and `list_tools`, on the blocking pool: each
    /// reads folders, and a refresh, a search or an install may wait for
    /// the network.
    fn market_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) {
        let market = Arc::clone(&self.services.market);
        match request {
            Request::ListTools => self.spawn_reply(id, span, kind, move || Response::Tools {
                tools: market.tools(),
            }),
            Request::MarketplaceRefresh => {
                self.spawn_reply(id, span, kind, move || market.refresh());
            }
            Request::MarketplaceSearch { query, kind: only } => {
                self.spawn_reply(id, span, kind, move || market.search(&query, only));
            }
            Request::InstallExtension {
                extension_id,
                version,
            } => self.spawn_reply(id, span, kind, move || {
                market.install(&extension_id, version.as_deref())
            }),
            Request::UninstallExtension { extension_id } => {
                self.spawn_reply(id, span, kind, move || market.uninstall(&extension_id));
            }
            _ => {}
        }
    }

    fn volume_info(&mut self, id: &RequestId, span: &tracing::Span, path: String) {
        let out = self.out.clone();
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let reply = match blocking_in_span(move || {
                    cabinetos_fs::volume::info_for_path(&path)
                })
                .await
                {
                    Ok(Ok(details)) => Response::VolumeInfo(details),
                    Ok(Err(error)) => failure_reply(listing::fs_failure(&error)),
                    Err(error) => {
                        rethrow_panic(Err(error));
                        failure_reply((ErrorCode::Internal, "the request was cancelled".to_owned()))
                    }
                };
                log_handled("volume_info", started, &reply);
                out.reply(request_id, reply);
                TaskDone::Replied
            }
            .instrument(span.clone()),
        );
    }

    /// Changes a setting or a keybinding on the blocking pool: it reads and
    /// writes the configuration file.
    fn write_setting(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        write: impl FnOnce(&Settings) -> Response + Send + 'static,
    ) {
        let settings = Arc::clone(&self.services.settings);
        self.spawn_reply(id, span, kind, move || write(&settings));
    }

    /// `search` and `index_status`, as tasks: each may wait for the indexer
    /// (at most 200 ms) or walk folders (at most 2 s).
    fn search_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) {
        let services = Arc::clone(&self.services);
        let request_id = id.clone();
        match request {
            Request::Search { query, limit, root } => {
                let default_root = self.default_search_root();
                self.spawn_task_reply(id, span, kind, async move {
                    search::search(
                        &services.indexer,
                        &request_id,
                        query,
                        limit,
                        root,
                        default_root,
                    )
                    .await
                });
            }
            Request::IndexStatus => {
                self.spawn_task_reply(id, span, kind, async move {
                    search::index_status(&services.indexer, &request_id).await
                });
            }
            _ => {}
        }
    }

    /// The terminal requests. `terminal_resize` and `terminal_list` answer
    /// at once; the others run on the blocking pool (starting a shell takes
    /// tens of milliseconds, closing one up to 2 s).
    fn terminal_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        let terminals = Arc::clone(&self.services.terminals);
        let answer = |result: Result<(), cabinetos_terminal::TerminalError>| match result {
            Ok(()) => Response::Ok,
            Err(error) => failure_reply((error.code, error.message)),
        };
        match request {
            Request::TerminalOpen {
                profile,
                cwd,
                cols,
                rows,
            } => {
                // Read at each open: an edited profile applies to the next
                // shell at once.
                let settings = self.services.settings.snapshot();
                let profile = match terminal::profile(&settings.config.terminal, profile.as_deref())
                {
                    Ok(profile) => profile,
                    Err(refusal) => return Some(failure_reply(refusal)),
                };
                self.spawn_reply(id, span, kind, move || {
                    match terminals.open(&profile, cwd.as_deref(), cols, rows) {
                        Ok(opened) => Response::TerminalOpened {
                            session_id: opened.session_id,
                            pipe: opened.pipe,
                            pid: opened.pid,
                        },
                        Err(error) => failure_reply((error.code, error.message)),
                    }
                });
                None
            }
            Request::TerminalResize {
                session_id,
                cols,
                rows,
            } => Some(answer(terminals.resize(session_id, cols, rows))),
            Request::TerminalClose { session_id } => {
                self.spawn_reply(id, span, kind, move || answer(terminals.close(session_id)));
                None
            }
            Request::TerminalSyncCwd { session_id, path } => {
                self.spawn_reply(id, span, kind, move || {
                    answer(terminals.sync_cwd(session_id, &path))
                });
                None
            }
            Request::TerminalList => Some(Response::TerminalSessions {
                sessions: terminals.list(),
            }),
            _ => None,
        }
    }

    /// Where a search without a root walks: the folder listed last on this
    /// connection, else the user's profile folder.
    fn default_search_root(&self) -> String {
        self.last_listed
            .clone()
            .unwrap_or_else(|| std::env::var("USERPROFILE").unwrap_or_else(|_| "C:\\".to_owned()))
    }

    /// Runs the async `work` as a task and replies with its answer.
    fn spawn_task_reply(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        work: impl Future<Output = Response> + Send + 'static,
    ) {
        let out = self.out.clone();
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let reply = work.await;
                log_handled(kind, started, &reply);
                out.reply(request_id, reply);
                TaskDone::Replied
            }
            .instrument(span.clone()),
        );
    }

    /// Runs `work` on the blocking pool and replies with its answer.
    fn spawn_reply(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        work: impl FnOnce() -> Response + Send + 'static,
    ) {
        let out = self.out.clone();
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let reply = match blocking_in_span(work).await {
                    Ok(reply) => reply,
                    Err(error) => {
                        rethrow_panic(Err(error));
                        failure_reply((ErrorCode::Internal, "the request was cancelled".to_owned()))
                    }
                };
                log_handled(kind, started, &reply);
                out.reply(request_id, reply);
                TaskDone::Replied
            }
            .instrument(span.clone()),
        );
    }

    /// `execute_command`: a plugin's command runs on the plugin's thread
    /// (as a task: it may take up to the plugin's deadline); the core's
    /// own answer at once. A command that is gone because its plugin
    /// crashed or stopped says so.
    fn execute_command(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        command: String,
        args: &Value,
    ) -> Option<Response> {
        tracing::info!(command = %command, "command requested");
        if let (Some(plugins), Some(plugin_id)) = (
            &self.services.plugins,
            self.services.settings.plugin_of(&command),
        ) {
            let plugins = Arc::clone(plugins);
            // A plugin always gets a JSON object or value, never nothing.
            let args = if args.is_null() {
                "{}".to_owned()
            } else {
                args.to_string()
            };
            self.spawn_reply(id, span, "execute_command", move || {
                match plugins.execute(&plugin_id, &command, &args) {
                    Ok(result) => match serde_json::from_str(&result) {
                        Ok(result) => Response::CommandResult { result },
                        Err(error) => failure_reply((
                            ErrorCode::PluginError,
                            format!("{command} answered with text that is not JSON: {error}"),
                        )),
                    },
                    Err(error) => failure_reply((error.code, error.message)),
                }
            });
            return None;
        }
        let reply = self.services.settings.execute(&command);
        if let Response::Error {
            code: ErrorCode::UnknownCommand,
            message,
        } = &reply
            && let Some(why) = self
                .services
                .plugins
                .as_ref()
                .and_then(|plugins| plugins.explain_missing(&command))
        {
            return Some(failure_reply((
                ErrorCode::UnknownCommand,
                format!("{message}: {why}"),
            )));
        }
        Some(reply)
    }

    /// The plugin requests. `list_plugins` answers at once; the others run
    /// as tasks (they read plugin folders or write the configuration file).
    fn plugin_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        kind: &'static str,
        request: Request,
    ) -> Option<Response> {
        let Some(plugins) = self.services.plugins.clone() else {
            return Some(match request {
                Request::ListPlugins => Response::Plugins {
                    plugins: Vec::new(),
                },
                _ => failure_reply((
                    ErrorCode::NoSuchPlugin,
                    "the plugin host is not running; see the core's log".to_owned(),
                )),
            });
        };
        let settings = Arc::clone(&self.services.settings);
        match request {
            Request::ListPlugins => Some(Response::Plugins {
                plugins: plugins.list(),
            }),
            Request::ReloadPlugin { plugin_id } => {
                self.spawn_reply(id, span, kind, move || match plugins.reload(&plugin_id) {
                    Ok(()) => Response::Ok,
                    Err(error) => failure_reply((error.code, error.message)),
                });
                None
            }
            Request::SetPluginEnabled { plugin_id, enabled } => {
                if !plugins.contains(&plugin_id) {
                    return Some(no_such_plugin(&plugin_id));
                }
                self.spawn_reply(id, span, kind, move || {
                    let reply = settings.update_plugin(&plugin_id, |entry| entry.enabled = enabled);
                    if matches!(reply, Response::Ok) {
                        plugins.apply_settings(&settings.snapshot().config.plugins);
                    }
                    reply
                });
                None
            }
            Request::GrantCapabilities {
                plugin_id,
                capabilities,
            } => {
                if !plugins.contains(&plugin_id) {
                    return Some(no_such_plugin(&plugin_id));
                }
                if let Err(message) = check_grants(&capabilities) {
                    return Some(failure_reply((ErrorCode::PluginError, message)));
                }
                self.spawn_reply(id, span, kind, move || {
                    let reply = settings.update_plugin(&plugin_id, |entry| {
                        for capability in capabilities {
                            if !entry.granted.contains(&capability) {
                                entry.granted.push(capability);
                            }
                        }
                    });
                    if matches!(reply, Response::Ok) {
                        plugins.apply_settings(&settings.snapshot().config.plugins);
                    }
                    reply
                });
                None
            }
            _ => None,
        }
    }

    /// The job requests. `start_job` runs as a task (it looks at the file
    /// system); the others answer at once.
    fn job_request(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        request: Request,
    ) -> Option<Response> {
        let jobs = &self.services.jobs;
        let answer = |result: Result<(), cabinetos_jobs::JobError>| match result {
            Ok(()) => Response::Ok,
            Err(error) => job_error(error),
        };
        match request {
            Request::StartJob(request) => {
                self.start_job(id, span, request);
                None
            }
            Request::ListJobs => Some(Response::Jobs { jobs: jobs.list() }),
            Request::JobControl { job_id, action } => Some(answer(jobs.control(job_id, action))),
            Request::ResolveConflict {
                job_id,
                conflict_id,
                resolution,
                apply_to_same_kind,
            } => Some(answer(jobs.resolve(
                job_id,
                conflict_id,
                &resolution,
                apply_to_same_kind,
            ))),
            _ => None,
        }
    }

    /// Checks the paths and queues a job on the blocking pool: the checks
    /// look at the file system.
    fn start_job(&mut self, id: &RequestId, span: &tracing::Span, request: JobRequest) {
        let out = self.out.clone();
        let services = Arc::clone(&self.services);
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let reply = match blocking_in_span(move || services.jobs.start(request)).await {
                    Ok(Ok(job_id)) => Response::JobStarted { job_id },
                    Ok(Err(error)) => job_error(error),
                    Err(error) => {
                        rethrow_panic(Err(error));
                        failure_reply((ErrorCode::Internal, "the request was cancelled".to_owned()))
                    }
                };
                log_handled("start_job", started, &reply);
                out.reply(request_id, reply);
                TaskDone::Replied
            }
            .instrument(span.clone()),
        );
    }

    /// Passes a configuration or job event on to the client.
    fn forward_event(&mut self, event: Result<Envelope<Event>, RecvError>) {
        match event {
            Ok(event) => self.out.send(&event),
            Err(RecvError::Lagged(missed)) => {
                // The events are gone; telling the client to read everything
                // again is as good as the events it missed.
                tracing::warn!(missed, "the client fell behind on configuration events");
                self.out.send(&Envelope::new(
                    RequestId::new(),
                    Event::ConfigChanged {
                        changed: every_section(),
                    },
                ));
                self.out.send(&Envelope::new(
                    RequestId::new(),
                    Event::KeymapChanged {
                        keymap: self.services.settings.snapshot().keymap.to_wire(),
                    },
                ));
                self.out.send(&Envelope::new(
                    RequestId::new(),
                    Event::ThemeChanged {
                        theme: Box::new(self.services.themes.applied()),
                    },
                ));
                for job in self.services.jobs.list() {
                    self.out.send(&Envelope::new(
                        RequestId::new(),
                        Event::JobProgress(job.progress),
                    ));
                }
                for conflict in self.services.jobs.open_conflicts() {
                    self.out.send(&Envelope::new(
                        RequestId::new(),
                        Event::JobConflict(conflict),
                    ));
                }
                for plugin in self.services.plugins.iter().flat_map(|host| host.list()) {
                    self.out.send(&Envelope::new(
                        RequestId::new(),
                        Event::PluginStateChanged {
                            plugin_id: plugin.id,
                            state: plugin.state,
                        },
                    ));
                }
                for session in self.services.terminals.list() {
                    if let TerminalState::Exited { code } = session.state {
                        self.out.send(&Envelope::new(
                            RequestId::new(),
                            Event::TerminalExited {
                                session_id: session.session_id,
                                exit_code: code,
                            },
                        ));
                    }
                }
                self.resend_tools();
            }
            Err(RecvError::Closed) => self.events = None,
        }
    }

    /// Sends the tools as they are now, for a client that may have missed a
    /// `tools_changed`. Reading the tools folder is disk work, so it runs on
    /// the blocking pool, and the event follows the others.
    fn resend_tools(&mut self) {
        let market = Arc::clone(&self.services.market);
        let out = self.out.clone();
        self.tasks.spawn(async move {
            match tokio::task::spawn_blocking(move || market.tools()).await {
                Ok(tools) => out.send(&Envelope::new(
                    RequestId::new(),
                    Event::ToolsChanged { tools },
                )),
                Err(error) => rethrow_panic(Err(error)),
            }
            TaskDone::Replied
        });
    }

    fn task_done(&mut self, done: Result<TaskDone, JoinError>) {
        let done = match done {
            Ok(done) => done,
            Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
            // Aborted: a closed listing's refresh task.
            Err(_) => return,
        };
        match done {
            TaskDone::ListingReady {
                request_id,
                listing_id,
                started,
                result,
            } => self.listing_ready(request_id, listing_id, started, result),
            TaskDone::RefreshEnded { listing_id } => {
                if let Some(slot) = self.listings.get_mut(&listing_id) {
                    slot.refresh = None;
                }
            }
            TaskDone::MeasureEnded { measure_id } => {
                self.measures.remove(&measure_id);
            }
            TaskDone::Replied => {}
        }
    }

    fn listing_ready(
        &mut self,
        request_id: RequestId,
        listing_id: u64,
        started: Instant,
        result: Result<Opened, Failure>,
    ) {
        let span = span_for_request(&request_id);
        let _entered = span.enter();
        let opened = match result {
            Ok(opened) => opened,
            Err(failure) => {
                let reply = failure_reply(failure);
                log_handled("list_directory", started, &reply);
                self.out.reply(request_id, reply);
                return;
            }
        };
        let Opened {
            path,
            options,
            published,
            watch,
        } = opened;
        let client_pid = self.client.as_ref().map_or(0, |client| client.pid);
        // Handed over here, where the reply that carries it is queued; a
        // connection that ended first never runs this.
        let section_handle = match published.hand_to(client_pid) {
            Ok(handle) => handle,
            Err(failure) => {
                let reply = failure_reply(failure);
                log_handled("list_directory", started, &reply);
                self.out.reply(request_id, reply);
                return;
            }
        };
        let reply = Response::ListingOpened {
            listing_id,
            section_handle,
            section_size: published.size,
            entry_count: published.entry_count,
            generation: 1,
            elapsed_us: published.elapsed_us,
        };
        tracing::info!(
            listing_id,
            path = %path,
            entries = published.entry_count,
            elapsed_us = published.elapsed_us,
            watched = watch.is_some(),
            "listing opened"
        );
        self.last_listed = Some(path.clone());
        log_handled("list_directory", started, &reply);
        // Queued before the refresh task starts, so the client learns the
        // listing ID before any event about it.
        self.out.reply(request_id, reply);
        if let Some(plugins) = &self.services.plugins {
            plugins.listing_opened(&path, published.entry_count);
        }

        let current = CurrentSection::new(1, published.section);
        let refresh = watch.map(|(directory_watcher, changes)| {
            let listing = WatchedListing {
                listing_id,
                path: path.clone(),
                options,
                client_pid,
                current: Arc::clone(&current),
                changes,
                _watcher: directory_watcher,
                out: self.out.clone(),
            };
            let span = tracing::info_span!("listing", listing_id);
            self.tasks.spawn(
                async move {
                    let listing_id = listing::refresh_loop(listing).await;
                    TaskDone::RefreshEnded { listing_id }
                }
                .instrument(span),
            )
        });
        self.listings.insert(
            listing_id,
            ListingSlot {
                path,
                current,
                refresh,
            },
        );
    }

    fn reject_frame_too_large(&self, len: usize, max: usize) {
        // The request's ID is inside the payload that was never read, so the
        // error reply gets a fresh ID.
        let id = RequestId::new();
        let _entered = span_for_request(&id).entered();
        tracing::warn!(len, max, "frame too large; closing the connection");
        self.out.reply(
            id,
            Response::Error {
                code: ErrorCode::FrameTooLarge,
                message: format!(
                    "frame of {len} bytes is larger than the {max}-byte limit; \
                     large data belongs in shared memory"
                ),
            },
        );
    }
}

/// Opens a listing: starts the watcher first (so a change during the first
/// read is not missed), then reads the directory into a section.
async fn open_listing(
    listing_id: u64,
    path: String,
    options: ListOptions,
    watch: bool,
) -> Result<Opened, Failure> {
    let watch = if watch {
        let (changes_tx, changes_rx) = mpsc::unbounded_channel();
        let watch_path = path.clone();
        let watcher = run_blocking(move || {
            DirectoryWatcher::start(&watch_path, format!("watch-{listing_id}"), move |change| {
                let _ = changes_tx.send(change);
            })
            .map_err(|error| listing::fs_failure(&error))
        })
        .await?;
        Some((watcher, changes_rx))
    } else {
        None
    };
    let read_path = path.clone();
    let published = run_blocking(move || listing::publish(&read_path, &options, 1)).await?;
    Ok(Opened {
        path,
        options,
        published,
        watch,
    })
}

/// Runs `work` on Tokio's blocking pool inside the current span. The
/// pool's threads start with no span, so without this what the work logs
/// (such as "job queued") would lose the request's ID.
fn blocking_in_span<T: Send + 'static>(
    work: impl FnOnce() -> T + Send + 'static,
) -> tokio::task::JoinHandle<T> {
    let span = tracing::Span::current();
    tokio::task::spawn_blocking(move || span.in_scope(work))
}

/// Runs blocking work on Tokio's blocking pool, never on an async worker.
async fn run_blocking<T: Send + 'static>(
    work: impl FnOnce() -> Result<T, Failure> + Send + 'static,
) -> Result<T, Failure> {
    match blocking_in_span(work).await {
        Ok(result) => result,
        Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
        Err(_) => Err((ErrorCode::Internal, "the request was cancelled".to_owned())),
    }
}

fn no_such_plugin(plugin_id: &str) -> Response {
    failure_reply((
        ErrorCode::NoSuchPlugin,
        format!("no plugin `{plugin_id}` is installed"),
    ))
}

fn no_such_listing(listing_id: u64) -> Response {
    failure_reply((
        ErrorCode::NoSuchListing,
        format!("no open listing {listing_id} on this connection"),
    ))
}

fn protocol_error(message: &str) -> Response {
    Response::Error {
        code: ErrorCode::ProtocolError,
        message: message.to_owned(),
    }
}

fn failure_reply((code, message): Failure) -> Response {
    Response::Error { code, message }
}

/// `ok`, or the error reply for a filesystem error.
fn answer_fs(result: Result<(), cabinetos_fs::FsError>) -> Response {
    match result {
        Ok(()) => Response::Ok,
        Err(error) => failure_reply(listing::fs_failure(&error)),
    }
}

/// `edit_path`: finds the program of `files.editor`, as a terminal profile
/// finds its shell, and starts it, or the file type's editor.
fn edit_path(path: &str, editor: Option<cabinetos_config::EditorProgram>) -> Response {
    let editor = match editor {
        Some(editor) => match cabinetos_terminal::find_program(&editor.command) {
            Some(program) => Some(cabinetos_fs::Editor {
                program,
                args: editor.args,
            }),
            None => {
                return failure_reply((
                    ErrorCode::SpawnFailed,
                    format!(
                        "files.editor: `{}` is neither a file nor a program on the PATH",
                        editor.command
                    ),
                ));
            }
        },
        None => None,
    };
    answer_fs(cabinetos_fs::edit_path(path, editor.as_ref()))
}

/// `invalid_path` for a path that is not absolute (a drive letter and a
/// backslash, or a UNC share).
fn not_absolute(path: &str) -> Option<Response> {
    (!std::path::Path::new(path).is_absolute()).then(|| {
        failure_reply((
            ErrorCode::InvalidPath,
            format!("{path}: not an absolute path"),
        ))
    })
}

fn job_error(error: cabinetos_jobs::JobError) -> Response {
    Response::Error {
        code: error.code,
        message: error.message,
    }
}

/// One line per request, inside its span, so its `request_id` is in the log.
fn log_handled(kind: &'static str, started: Instant, reply: &Response) {
    let elapsed_us = listing::micros(started.elapsed());
    if let Response::Error { code, message } = reply {
        tracing::info!(request = kind, elapsed_us, ?code, error = %message, "request failed");
    } else {
        tracing::info!(request = kind, elapsed_us, "request handled");
    }
}
