//! The headless CabinetOS core: process startup, the pipe server, session
//! lifetime, and the wiring of every library crate.
//!
//! The UI is only a view (brief §1, the Dumb UI Rule): all work happens here,
//! behind the named pipe. In Phase 1 the core answers `ping` and `shutdown`,
//! logs every request with its ID, and exits with its parent process.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: every
//! connection is served asynchronously, so no request waits on another),
//! Article 10 (The Zero-Bloat Foundation: the core is the bare navigation
//! engine; features arrive as extensions) and Article 12 (Unified
//! Diagnostics: each request is handled inside a span carrying its ID).
#![forbid(unsafe_code)]

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use cabinetos_diag::{Boundary, DiagConfig, DiagError, span_for_request};
use cabinetos_ipc::{IpcError, PipeConnection, PipeName, PipeServer};
use cabinetos_protocol::{Envelope, ErrorCode, PROTOCOL_VERSION, Request, RequestId, Response};
use serde_json::Value;
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;
use tracing::Instrument;

/// The core's version, reported in `pong`.
pub const CORE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// How long open connections may take to finish once shutdown starts.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(2);

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
pub async fn run(config: CoreConfig, shutdown: CancellationToken) -> Result<(), CoreError> {
    let CoreConfig {
        pipe,
        parent_pid,
        log_dir,
    } = config;
    let diag = cabinetos_diag::init(diag_config(log_dir))?;
    let result = serve(&pipe, parent_pid, &shutdown, diag.log_dir()).await;
    match &result {
        Ok(()) => tracing::info!("core stopped"),
        Err(error) => tracing::error!(%error, "core stopped with an error"),
    }
    drop(diag);
    result
}

async fn serve(
    pipe: &PipeName,
    parent_pid: Option<u32>,
    shutdown: &CancellationToken,
    log_dir: &Path,
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
        log_dir = %log_dir.display(),
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
                        handle_connection(connection, shutdown.clone()).instrument(span),
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

/// Serves one client until it disconnects or sends a malformed frame.
async fn handle_connection(mut connection: PipeConnection, shutdown: CancellationToken) {
    tracing::debug!("client connected");
    loop {
        let frame = match connection.read_frame().await {
            Ok(frame) => frame,
            Err(IpcError::Closed) => {
                tracing::debug!("client disconnected");
                return;
            }
            Err(IpcError::FrameTooLarge { len, max }) => {
                // The request's ID is inside the payload that was never read,
                // so the error reply gets a fresh ID.
                let id = RequestId::new();
                let span = span_for_request(&id);
                let message = format!(
                    "frame of {len} bytes is larger than the {max}-byte limit; \
                     large data belongs in shared memory"
                );
                tracing::warn!(parent: &span, len, max, "frame too large; closing the connection");
                let reply = Envelope::new(
                    id,
                    Response::Error {
                        code: ErrorCode::FrameTooLarge,
                        message,
                    },
                );
                let _ = connection.send(&reply).await;
                return;
            }
            Err(error) => {
                tracing::warn!(%error, "malformed frame; closing the connection");
                return;
            }
        };
        if let Err(error) = handle_frame(&mut connection, &frame, &shutdown).await {
            tracing::warn!(%error, "cannot send the reply; closing the connection");
            return;
        }
    }
}

/// Answers one frame: the request's reply, or an error reply if the frame is
/// not a valid request.
async fn handle_frame(
    connection: &mut PipeConnection,
    frame: &[u8],
    shutdown: &CancellationToken,
) -> Result<(), IpcError> {
    let started = Instant::now();
    match decode_request(frame) {
        Ok(Envelope { id, body: request }) => {
            let span = span_for_request(&id);
            async {
                let reply = match request {
                    Request::Ping => Response::Pong {
                        protocol_version: PROTOCOL_VERSION,
                        core_version: CORE_VERSION.to_owned(),
                    },
                    Request::Shutdown => Response::Ok,
                };
                connection.send(&Envelope::new(id, reply)).await?;
                tracing::info!(
                    request = request.type_tag(),
                    elapsed_us = u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX),
                    "request handled"
                );
                if request == Request::Shutdown {
                    tracing::info!("shutdown requested by a client");
                    shutdown.cancel();
                }
                Ok(())
            }
            .instrument(span)
            .await
        }
        Err(rejection) => {
            let id = rejection.id.unwrap_or_else(RequestId::new);
            let span = span_for_request(&id);
            async {
                tracing::warn!(
                    code = ?rejection.code,
                    error = %rejection.message,
                    "request rejected"
                );
                let reply = Response::Error {
                    code: rejection.code,
                    message: rejection.message,
                };
                connection.send(&Envelope::new(id, reply)).await
            }
            .instrument(span)
            .await
        }
    }
}

/// A frame that is not a valid request.
#[derive(Debug)]
struct Rejection {
    /// The frame's ID, when it had a valid one; the reply echoes it.
    id: Option<RequestId>,
    code: ErrorCode,
    message: String,
}

/// Parses a frame as a request envelope, and classifies a failure:
/// `unknown_request` when the envelope is fine but its `type` is not one this
/// core knows (a newer UI, for example), `protocol_error` for everything else.
fn decode_request(frame: &[u8]) -> Result<Envelope<Request>, Rejection> {
    let value: Value = serde_json::from_slice(frame).map_err(|error| Rejection {
        id: None,
        code: ErrorCode::ProtocolError,
        message: format!("the frame is not valid JSON: {error}"),
    })?;
    let id = value
        .get("id")
        .and_then(Value::as_str)
        .and_then(|text| text.parse::<RequestId>().ok());
    let kind = value
        .get("type")
        .and_then(Value::as_str)
        .map(|kind| kind.chars().take(MAX_ECHOED_TYPE).collect::<String>());

    serde_json::from_value(value).map_err(|error| match kind {
        Some(kind) if id.is_some() && !Request::TYPES.contains(&kind.as_str()) => Rejection {
            id,
            code: ErrorCode::UnknownRequest,
            message: format!("unknown request type `{kind}`"),
        },
        _ => Rejection {
            id,
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
