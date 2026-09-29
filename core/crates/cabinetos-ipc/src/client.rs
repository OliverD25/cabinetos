//! The client end of the control channel.
//!
//! A background task reads everything the core sends. Replies go to the
//! request waiting for their `id`; events go to a channel (see
//! [`PipeClient::events`]). So requests may overlap, and events arrive
//! whenever the core sends them.
//!
//! Section handles arrive as plain numbers inside `listing_opened` and
//! `listing_refreshed`. The reader notes each one, and
//! [`PipeClient::take_section`] hands it out once as an owned
//! [`SharedSection`]. Handles never taken are closed when the client drops.

use std::collections::HashMap;
use std::io;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use cabinetos_protocol::{Envelope, Event, Incoming, Request, RequestId, Response};
use tokio::io::{ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinHandle;
use tokio::time::Instant;
use windows::Win32::Foundation::ERROR_PIPE_BUSY;

use crate::{IpcError, PipeName, SharedSection, codec, frame};

/// How long a client waits before retrying a busy pipe.
const BUSY_RETRY: Duration = Duration::from_millis(20);

/// A reply on its way to the request that waits for it.
type ReplySender = oneshot::Sender<Result<Envelope<Response>, IpcError>>;

/// A client of the core's pipe: the CLI now, the UI's model later.
#[derive(Debug)]
pub struct PipeClient {
    writer: WriteHalf<NamedPipeClient>,
    shared: Arc<Mutex<Shared>>,
    events: Option<mpsc::UnboundedReceiver<Envelope<Event>>>,
    reader: JoinHandle<()>,
}

/// What the client and its reader task share.
#[derive(Debug, Default)]
struct Shared {
    /// Requests waiting for their reply, by ID.
    pending: HashMap<RequestId, ReplySender>,
    /// Why the connection ended; later requests fail with it at once.
    ended: Option<String>,
    /// Set when the client drops: handles that still arrive are closed.
    dropped: bool,
    /// Section handles received and not yet taken: value → size.
    sections: HashMap<u64, usize>,
}

fn lock(shared: &Mutex<Shared>) -> MutexGuard<'_, Shared> {
    shared.lock().unwrap_or_else(PoisonError::into_inner)
}

impl PipeClient {
    /// Connects to the pipe, retrying while every instance is busy
    /// (`ERROR_PIPE_BUSY`) until `timeout` has passed. A pipe that does not
    /// exist fails at once with an I/O error of kind `NotFound`. Must run
    /// inside a Tokio runtime.
    pub async fn connect(name: &PipeName, timeout: Duration) -> Result<Self, IpcError> {
        let deadline = Instant::now() + timeout;
        let pipe = loop {
            match ClientOptions::new().open(name.as_str()) {
                Ok(pipe) => break pipe,
                Err(error) if is_pipe_busy(&error) => {
                    if Instant::now() >= deadline {
                        return Err(IpcError::Timeout(timeout));
                    }
                    tokio::time::sleep(BUSY_RETRY).await;
                }
                Err(error) => return Err(error.into()),
            }
        };
        let (reader, writer) = tokio::io::split(pipe);
        let shared = Arc::new(Mutex::new(Shared::default()));
        let (events_tx, events_rx) = mpsc::unbounded_channel();
        let reader = tokio::spawn(read_loop(reader, Arc::clone(&shared), events_tx));
        Ok(Self {
            writer,
            shared,
            events: Some(events_rx),
            reader,
        })
    }

    /// Sends `request` with a fresh [`RequestId`] and waits for the reply.
    pub async fn request(&mut self, request: Request) -> Result<Envelope<Response>, IpcError> {
        self.request_with_id(RequestId::new(), request).await
    }

    /// Sends `request` with the given ID and waits for the reply with that
    /// ID. If the connection ends first, or the core replies to an ID no
    /// request is waiting for, every waiting request fails.
    pub async fn request_with_id(
        &mut self,
        id: RequestId,
        request: Request,
    ) -> Result<Envelope<Response>, IpcError> {
        let (reply_tx, reply_rx) = oneshot::channel();
        {
            let mut shared = lock(&self.shared);
            if let Some(reason) = &shared.ended {
                return Err(IpcError::Protocol(format!(
                    "the connection has ended: {reason}"
                )));
            }
            shared.pending.insert(id.clone(), reply_tx);
        }
        if let Err(error) = codec::send(&mut self.writer, &Envelope::new(id.clone(), request)).await
        {
            lock(&self.shared).pending.remove(&id);
            return Err(error);
        }
        reply_rx.await.unwrap_or(Err(IpcError::Closed))
    }

    /// Introduces this process to the core (`hello`), which is required
    /// before listing directories.
    pub async fn hello(&mut self, client_name: &str) -> Result<Envelope<Response>, IpcError> {
        self.request(Request::Hello {
            client_pid: std::process::id(),
            client_name: client_name.to_owned(),
        })
        .await
    }

    /// The events the core sends (`listing_refreshed`, `listing_lost`), in
    /// order. Returns the receiver the first time and `None` afterwards.
    pub fn events(&mut self) -> Option<mpsc::UnboundedReceiver<Envelope<Event>>> {
        self.events.take()
    }

    /// Takes ownership of a section handle that arrived from the core in
    /// `listing_opened` or `listing_refreshed`. Each handle can be taken once;
    /// any other number is refused, so no unrelated handle can be closed.
    pub fn take_section(&self, section_handle: u64) -> Result<SharedSection, IpcError> {
        let size = lock(&self.shared)
            .sections
            .remove(&section_handle)
            .ok_or_else(|| {
                IpcError::Protocol(format!(
                    "{section_handle:#x} is not a section handle the core sent, or it was already taken"
                ))
            })?;
        Ok(adopt_section(section_handle, size))
    }
}

impl Drop for PipeClient {
    fn drop(&mut self) {
        self.reader.abort();
        let untaken = {
            let mut shared = lock(&self.shared);
            shared.dropped = true;
            std::mem::take(&mut shared.sections)
        };
        for (handle, size) in untaken {
            drop(adopt_section(handle, size));
        }
    }
}

/// Wraps a handle value recorded by the reader as an owned section.
#[allow(unsafe_code)]
fn adopt_section(handle: u64, size: usize) -> SharedSection {
    // SAFETY: only values recorded by `record_section` reach this point: the
    // core duplicated them into this process for this connection alone (as
    // `listing_opened` and `listing_refreshed` promise), and each is removed
    // from the table before it is adopted, so it is owned exactly once.
    unsafe { SharedSection::from_raw_handle(handle, size) }
}

/// Notes a section handle from the core, or closes it if the client is gone.
fn record_section(shared: &Mutex<Shared>, handle: u64, size: u64) {
    let size = usize::try_from(size).unwrap_or(usize::MAX);
    let mut guard = lock(shared);
    if guard.dropped {
        drop(guard);
        drop(adopt_section(handle, size));
    } else {
        guard.sections.insert(handle, size);
    }
}

/// Reads until the connection ends, routing replies and events.
async fn read_loop(
    mut reader: ReadHalf<NamedPipeClient>,
    shared: Arc<Mutex<Shared>>,
    events: mpsc::UnboundedSender<Envelope<Event>>,
) {
    let failure = loop {
        let payload = match frame::read_frame(&mut reader).await {
            Ok(payload) => payload,
            Err(error) => break error,
        };
        match serde_json::from_slice::<Envelope<Incoming>>(&payload) {
            Ok(Envelope {
                id,
                trace,
                body: Incoming::Response(response),
            }) => {
                if let Response::ListingOpened {
                    section_handle,
                    section_size,
                    ..
                } = &response
                {
                    record_section(&shared, *section_handle, *section_size);
                }
                let waiter = lock(&shared).pending.remove(&id);
                match waiter {
                    Some(waiter) => {
                        let _ = waiter.send(Ok(Envelope::traced(id, trace, response)));
                    }
                    None => {
                        break IpcError::Protocol(format!(
                            "the core replied to request {id}, which no request is waiting for"
                        ));
                    }
                }
            }
            Ok(Envelope {
                id,
                trace,
                body: Incoming::Event(event),
            }) => {
                if let Event::ListingRefreshed {
                    section_handle,
                    section_size,
                    ..
                } = &event
                {
                    record_section(&shared, *section_handle, *section_size);
                }
                let _ = events.send(Envelope::traced(id, trace, event));
            }
            Err(error) => {
                // A reply this client cannot read fails its own request;
                // anything else (an event from a newer core) is skipped.
                let id = serde_json::from_slice::<serde_json::Value>(&payload)
                    .ok()
                    .and_then(|value| value.get("id")?.as_str()?.parse::<RequestId>().ok());
                let waiter = id.and_then(|id| lock(&shared).pending.remove(&id));
                if let Some(waiter) = waiter {
                    let _ = waiter.send(Err(IpcError::Protocol(error.to_string())));
                } else {
                    tracing::warn!(%error, "skipping a message this client cannot read");
                }
            }
        }
    };

    let waiting = {
        let mut shared = lock(&shared);
        shared.ended = Some(failure.to_string());
        std::mem::take(&mut shared.pending)
    };
    for (_, waiter) in waiting {
        let _ = waiter.send(Err(copy_of(&failure)));
    }
}

/// An equivalent error for each request that fails because of `error`.
fn copy_of(error: &IpcError) -> IpcError {
    match error {
        IpcError::Closed => IpcError::Closed,
        IpcError::FrameTooLarge { len, max } => IpcError::FrameTooLarge {
            len: *len,
            max: *max,
        },
        IpcError::Timeout(after) => IpcError::Timeout(*after),
        IpcError::Io(io) => IpcError::Io(io::Error::new(io.kind(), io.to_string())),
        other => IpcError::Protocol(other.to_string()),
    }
}

/// Connects to `name`, sends `message`, reads one reply and closes: one
/// request, one reply, no background reader. The whole exchange must finish
/// within `timeout`, busy retries included. A pipe that does not exist fails
/// at once with an I/O error of kind `NotFound`. Must run inside a Tokio
/// runtime.
pub async fn exchange<Q, R>(name: &PipeName, message: &Q, timeout: Duration) -> Result<R, IpcError>
where
    Q: serde::Serialize + ?Sized,
    R: serde::de::DeserializeOwned,
{
    let exchange = async {
        let mut pipe = loop {
            match ClientOptions::new().open(name.as_str()) {
                Ok(pipe) => break pipe,
                Err(error) if is_pipe_busy(&error) => tokio::time::sleep(BUSY_RETRY).await,
                Err(error) => return Err(error.into()),
            }
        };
        codec::send(&mut pipe, message).await?;
        codec::recv(&mut pipe).await
    };
    tokio::time::timeout(timeout, exchange)
        .await
        .map_err(|_| IpcError::Timeout(timeout))?
}

fn is_pipe_busy(error: &io::Error) -> bool {
    error
        .raw_os_error()
        .is_some_and(|code| code.cast_unsigned() == ERROR_PIPE_BUSY.0)
}
