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
//!   (`list_directory`, `volume_info`) as tasks, so one slow directory never
//!   holds up the next request.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use cabinetos_diag::span_for_request;
use cabinetos_fs::{DirectoryWatcher, ListOptions};
use cabinetos_ipc::{IpcError, PipeConnection, SharedSection};
use cabinetos_protocol::{
    Envelope, ErrorCode, PROTOCOL_VERSION, Request, RequestId, Response, SortSpec,
};
use serde::Serialize;
use tokio::sync::mpsc;
use tokio::task::{AbortHandle, JoinError, JoinSet};
use tokio_util::sync::CancellationToken;
use tracing::Instrument;

use crate::listing::{self, Failure, Published, WatchedListing};
use crate::{CORE_VERSION, decode_request};

/// How long the writer may take to send what is still queued when the
/// connection ends.
const FINAL_WRITE_GRACE: Duration = Duration::from_secs(1);

/// Listing IDs are unique across all connections, so a listing ID in the log
/// names one listing.
static NEXT_LISTING_ID: AtomicU64 = AtomicU64::new(1);

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

    fn reply(&self, id: RequestId, response: Response) {
        self.send(&Envelope::new(id, response));
    }
}

/// The client, after `hello`.
struct Client {
    pid: u32,
}

/// What the session keeps for each open listing.
enum ListingSlot {
    /// Not watched: the core's handle to its section.
    Static(SharedSection),
    /// Watched: the task that keeps it current (and owns its section).
    Watched(AbortHandle),
    /// Its directory was lost; only `close_listing` is left to do.
    Lost,
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
    /// The task sent its own reply.
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
pub(crate) async fn handle_connection(connection: PipeConnection, shutdown: CancellationToken) {
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
                Some(Err(error)) => {
                    tracing::warn!(%error, "malformed frame; closing the connection");
                    break;
                }
            },
            Some(done) = session.tasks.join_next() => session.task_done(done),
        }
    }

    reader_task.abort();
    rethrow_panic(reader_task.await);
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
}

impl Session {
    fn handle_frame(&mut self, frame: &[u8]) {
        let started = Instant::now();
        let envelope = match decode_request(frame) {
            Ok(envelope) => envelope,
            Err(rejection) => {
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
                Request::ListDirectory {
                    path,
                    include_hidden,
                    sort,
                    watch,
                } => self.list_directory(&id, &span, path, include_hidden, sort, watch),
                Request::VolumeInfo { path } => {
                    self.volume_info(&id, &span, path);
                    None
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
        Response::Welcome {
            protocol_version: PROTOCOL_VERSION,
            core_version: CORE_VERSION.to_owned(),
        }
    }

    fn close_listing(&mut self, listing_id: u64) -> Response {
        match self.listings.remove(&listing_id) {
            Some(ListingSlot::Watched(task)) => task.abort(),
            Some(ListingSlot::Static(section)) => drop(section),
            Some(ListingSlot::Lost) => {}
            None => {
                return Response::Error {
                    code: ErrorCode::NoSuchListing,
                    message: format!("no open listing {listing_id} on this connection"),
                };
            }
        }
        tracing::debug!(listing_id, "listing closed");
        Response::Ok
    }

    /// Starts a listing; the reply comes when the task ends. Returns an
    /// immediate reply only when the request is refused.
    fn list_directory(
        &mut self,
        id: &RequestId,
        span: &tracing::Span,
        path: String,
        include_hidden: bool,
        sort: SortSpec,
        watch: bool,
    ) -> Option<Response> {
        let Some(client) = &self.client else {
            return Some(protocol_error("hello required"));
        };
        let client_pid = client.pid;
        let listing_id = NEXT_LISTING_ID.fetch_add(1, Ordering::Relaxed);
        let options = ListOptions {
            include_hidden,
            sort,
            ..ListOptions::default()
        };
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let result = open_listing(listing_id, path, options, watch, client_pid).await;
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

    fn volume_info(&mut self, id: &RequestId, span: &tracing::Span, path: String) {
        let out = self.out.clone();
        let request_id = id.clone();
        let started = Instant::now();
        self.tasks.spawn(
            async move {
                let reply = match tokio::task::spawn_blocking(move || {
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
                    *slot = ListingSlot::Lost;
                }
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
        let reply = Response::ListingOpened {
            listing_id,
            section_handle: published.client_handle,
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
        log_handled("list_directory", started, &reply);
        // Queued before the refresh task starts, so the client learns the
        // listing ID before any event about it.
        self.out.reply(request_id, reply);

        let slot = match watch {
            None => ListingSlot::Static(published.section),
            Some((directory_watcher, changes)) => {
                let listing = WatchedListing {
                    listing_id,
                    path,
                    options,
                    client_pid: self.client.as_ref().map_or(0, |client| client.pid),
                    section: published.section,
                    generation: 1,
                    changes,
                    _watcher: directory_watcher,
                    out: self.out.clone(),
                };
                let span = tracing::info_span!("listing", listing_id);
                let task = self.tasks.spawn(
                    async move {
                        let listing_id = listing::refresh_loop(listing).await;
                        TaskDone::RefreshEnded { listing_id }
                    }
                    .instrument(span),
                );
                ListingSlot::Watched(task)
            }
        };
        self.listings.insert(listing_id, slot);
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
    client_pid: u32,
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
    let published =
        run_blocking(move || listing::publish(&read_path, &options, 1, client_pid)).await?;
    Ok(Opened {
        path,
        options,
        published,
        watch,
    })
}

/// Runs blocking work on Tokio's blocking pool, never on an async worker.
async fn run_blocking<T: Send + 'static>(
    work: impl FnOnce() -> Result<T, Failure> + Send + 'static,
) -> Result<T, Failure> {
    match tokio::task::spawn_blocking(work).await {
        Ok(result) => result,
        Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
        Err(_) => Err((ErrorCode::Internal, "the request was cancelled".to_owned())),
    }
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

/// One line per request, inside its span, so its `request_id` is in the log.
fn log_handled(kind: &'static str, started: Instant, reply: &Response) {
    let elapsed_us = listing::micros(started.elapsed());
    if let Response::Error { code, message } = reply {
        tracing::info!(request = kind, elapsed_us, ?code, error = %message, "request failed");
    } else {
        tracing::info!(request = kind, elapsed_us, "request handled");
    }
}
