//! The indexer: the elevated process around `cabinetos-index`. It indexes
//! NTFS volumes and answers read-only queries from the core over its own
//! named pipe (`docs/indexer.md`). It never performs file operations and runs
//! no plugin code (least privilege, ADR 0002).
//!
//! - [`PIPE_NAME`] and [`PIPE_SDDL`]: one indexer per machine, reachable by
//!   the interactive user from a normal (medium integrity) process although
//!   the indexer itself runs elevated.
//! - [`serve`] answers [`IndexerRequest`]s: `ping`, `index_status`,
//!   `search`. Nothing else is accepted.
//! - [`run`] ties it together: pipe, indexes, serving until shutdown.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: real-time
//! NT-level file indexing). Brief §2.
#![forbid(unsafe_code)]

use std::sync::Arc;
use std::time::Instant;

use cabinetos_diag::span_for_request;
use cabinetos_index::{Indexes, Matcher, SearchError, VolumeReport, VolumeState};
use cabinetos_ipc::{IpcError, PipeConnection, PipeName, PipeServer};
use cabinetos_protocol::{
    Envelope, FileHit, HitKind, IndexState, IndexerErrorCode, IndexerRequest, IndexerResponse,
    RequestId, VolumeStatus,
};
use serde_json::Value;
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;

/// The indexer's pipe. The name is fixed: there is one indexer per machine,
/// and the core finds it without being told.
pub const PIPE_NAME: &str = cabinetos_protocol::INDEXER_PIPE_NAME;

/// The pipe's security descriptor, in SDDL:
///
/// - `D:P` — a protected DACL, nothing inherited;
/// - `(D;;GA;;;NU)` — network logons are denied (remote clients are also
///   refused by the pipe itself);
/// - `(A;;GA;;;SY)(A;;GA;;;BA)` — SYSTEM and Administrators have full access;
/// - `(A;;GRGW;;;IU)` — the interactive user may read and write, which is
///   what a client needs to send requests;
/// - `S:(ML;;NW;;;ME)` — a medium mandatory integrity label with no-write-up.
///   Without it the pipe of an elevated process gets a high label, and
///   Windows refuses writes from the normal-rights core.
pub const PIPE_SDDL: &str = "D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)S:(ML;;NW;;;ME)";

/// The most hits one search returns.
pub const MAX_LIMIT: u32 = 1000;

/// The indexer's version, in `pong`.
pub const INDEXER_VERSION: &str = env!("CARGO_PKG_VERSION");

/// A search's hits, best first.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchReply {
    /// The hits.
    pub hits: Vec<FileHit>,
    /// False when a volume the search should cover is not indexed yet.
    pub complete: bool,
}

/// What the pipe serves: the real indexes, or a stand-in in tests.
pub trait IndexSource: Send + Sync + 'static {
    /// Every volume's state.
    fn status(&self) -> Vec<VolumeStatus>;

    /// The best `limit` hits for `query`, under `root` when given.
    fn search(
        &self,
        query: &str,
        limit: usize,
        root: Option<&str>,
    ) -> Result<SearchReply, (IndexerErrorCode, String)>;
}

impl IndexSource for Indexes {
    fn status(&self) -> Vec<VolumeStatus> {
        Indexes::status(self)
            .into_iter()
            .map(volume_status)
            .collect()
    }

    fn search(
        &self,
        query: &str,
        limit: usize,
        root: Option<&str>,
    ) -> Result<SearchReply, (IndexerErrorCode, String)> {
        let Some(matcher) = Matcher::new(query) else {
            return Ok(SearchReply {
                hits: Vec::new(),
                complete: true,
            });
        };
        match Indexes::search(self, &matcher, root, limit) {
            Ok(outcome) => Ok(SearchReply {
                hits: outcome
                    .hits
                    .into_iter()
                    .map(|hit| FileHit {
                        path: hit.path,
                        kind: if hit.directory {
                            HitKind::Directory
                        } else {
                            HitKind::File
                        },
                        frn: Some(hit.frn),
                    })
                    .collect(),
                complete: outcome.complete,
            }),
            Err(error @ SearchError::NotIndexed(_)) => {
                Err((IndexerErrorCode::NotIndexed, error.to_string()))
            }
            Err(error) => Err((IndexerErrorCode::InvalidRoot, error.to_string())),
        }
    }
}

fn volume_status(report: VolumeReport) -> VolumeStatus {
    VolumeStatus {
        letter: report.letter,
        state: match report.state {
            VolumeState::Building => IndexState::Building,
            VolumeState::Ready => IndexState::Ready,
            VolumeState::Rebuilding => IndexState::Rebuilding,
            VolumeState::Failed(message) => IndexState::Failed { message },
        },
        entries: report.entries,
        built_in_ms: report.built_in_ms,
        journal_lag: report.journal_lag,
    }
}

/// The reply to one request. Blocking: a search scans the index.
pub fn respond(source: &dyn IndexSource, request: IndexerRequest) -> IndexerResponse {
    match request {
        IndexerRequest::Ping => IndexerResponse::Pong {
            indexer_version: INDEXER_VERSION.to_owned(),
        },
        IndexerRequest::IndexStatus => IndexerResponse::IndexStatus {
            volumes: source.status(),
        },
        IndexerRequest::Search { query, limit, root } => {
            let started = Instant::now();
            let limit = limit.min(MAX_LIMIT) as usize;
            match source.search(&query, limit, root.as_deref()) {
                Ok(reply) => IndexerResponse::FileSearchResults {
                    hits: reply.hits,
                    took_us: u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX),
                    complete: reply.complete,
                },
                Err((code, message)) => IndexerResponse::Error { code, message },
            }
        }
    }
}

/// The reply to one frame: a request, or a complaint about the frame.
async fn answer(frame: &[u8], source: &Arc<dyn IndexSource>) -> Envelope<IndexerResponse> {
    let value: Value = match serde_json::from_slice(frame) {
        Ok(value) => value,
        Err(error) => {
            return Envelope::new(
                RequestId::new(),
                IndexerResponse::Error {
                    code: IndexerErrorCode::ProtocolError,
                    message: format!("the frame is not valid JSON: {error}"),
                },
            );
        }
    };
    let id = value
        .get("id")
        .and_then(Value::as_str)
        .and_then(|text| text.parse::<RequestId>().ok());
    let kind = value
        .get("type")
        .and_then(Value::as_str)
        .map(|kind| kind.chars().take(64).collect::<String>());
    let Envelope { id, body } = match serde_json::from_value::<Envelope<IndexerRequest>>(value) {
        Ok(envelope) => envelope,
        Err(error) => {
            let (code, message) = match kind {
                Some(kind) if id.is_some() && !IndexerRequest::TYPES.contains(&kind.as_str()) => (
                    IndexerErrorCode::UnknownRequest,
                    format!(
                        "unknown request type `{kind}`; the indexer answers only read-only requests"
                    ),
                ),
                _ => (
                    IndexerErrorCode::ProtocolError,
                    format!("not a valid request envelope: {error}"),
                ),
            };
            return Envelope::new(
                id.unwrap_or_else(RequestId::new),
                IndexerResponse::Error { code, message },
            );
        }
    };
    let span = span_for_request(&id);
    let kind = body.type_tag();
    let started = Instant::now();
    let source = Arc::clone(source);
    let work_span = span.clone();
    let reply = match tokio::task::spawn_blocking(move || {
        work_span.in_scope(|| respond(source.as_ref(), body))
    })
    .await
    {
        Ok(reply) => reply,
        // A panic is a bug: stop, and let the crash trace say where.
        Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
        Err(_) => IndexerResponse::Error {
            code: IndexerErrorCode::Internal,
            message: "the request was cancelled".to_owned(),
        },
    };
    let _entered = span.enter();
    let elapsed_us = u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX);
    if let IndexerResponse::Error { code, message } = &reply {
        tracing::info!(request = kind, elapsed_us, ?code, error = %message, "request failed");
    } else {
        tracing::info!(request = kind, elapsed_us, "request handled");
    }
    Envelope::new(id, reply)
}

/// Serves one client until it disconnects.
async fn serve_connection(mut connection: PipeConnection, source: Arc<dyn IndexSource>) {
    loop {
        let frame = match connection.read_frame().await {
            Ok(frame) => frame,
            Err(IpcError::Closed) => return,
            Err(error) => {
                tracing::debug!(%error, "closing a client connection");
                return;
            }
        };
        let reply = answer(&frame, &source).await;
        if connection.send(&reply).await.is_err() {
            return;
        }
    }
}

/// Answers clients of `server` until `shutdown` is cancelled.
pub async fn serve(
    mut server: PipeServer,
    source: Arc<dyn IndexSource>,
    shutdown: CancellationToken,
) {
    let mut connections = JoinSet::new();
    loop {
        tokio::select! {
            () = shutdown.cancelled() => break,
            accepted = server.accept() => match accepted {
                Ok(connection) => {
                    connections.spawn(serve_connection(connection, Arc::clone(&source)));
                }
                Err(error) => {
                    tracing::warn!(%error, "accepting a client failed");
                    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
                }
            },
            Some(joined) = connections.join_next() => {
                if let Err(error) = joined && error.is_panic() {
                    std::panic::resume_unwind(error.into_panic());
                }
            }
        }
    }
    connections.shutdown().await;
}

/// Why the indexer cannot run.
#[derive(Debug, thiserror::Error)]
pub enum RunError {
    /// The pipe cannot be created, usually because another indexer has it.
    #[error("cannot create {pipe}: {source}; is another indexer running?")]
    Pipe {
        /// The pipe's name.
        pipe: String,
        /// What went wrong.
        #[source]
        source: IpcError,
    },
}

/// Indexes `letters` and serves `pipe` until `shutdown` is cancelled. Needs
/// Administrator rights to index anything; without them every volume fails
/// and says so. Must run inside a Tokio runtime.
pub async fn run(
    letters: &[char],
    pipe: &PipeName,
    shutdown: CancellationToken,
) -> Result<(), RunError> {
    let server = PipeServer::bind_with_sddl(pipe, PIPE_SDDL).map_err(|source| RunError::Pipe {
        pipe: pipe.to_string(),
        source,
    })?;
    let indexes = Arc::new(Indexes::start(letters));
    tracing::info!(volumes = ?letters, pipe = %pipe, version = INDEXER_VERSION, "indexer started");
    serve(
        server,
        Arc::clone(&indexes) as Arc<dyn IndexSource>,
        shutdown,
    )
    .await;
    let stopping = Arc::clone(&indexes);
    let _ = tokio::task::spawn_blocking(move || stopping.stop()).await;
    tracing::info!("indexer stopped");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fake;

    impl IndexSource for Fake {
        fn status(&self) -> Vec<VolumeStatus> {
            vec![VolumeStatus {
                letter: 'C',
                state: IndexState::Ready,
                entries: 3,
                built_in_ms: Some(12),
                journal_lag: Some(0),
            }]
        }

        fn search(
            &self,
            query: &str,
            limit: usize,
            root: Option<&str>,
        ) -> Result<SearchReply, (IndexerErrorCode, String)> {
            if root == Some("Q:\\") {
                return Err((
                    IndexerErrorCode::NotIndexed,
                    "drive Q: is not indexed".to_owned(),
                ));
            }
            let hits = (0..limit.min(3))
                .map(|n| FileHit {
                    path: format!("C:\\{query}{n}.txt"),
                    kind: HitKind::File,
                    frn: Some(100 + n as u64),
                })
                .collect();
            Ok(SearchReply {
                hits,
                complete: true,
            })
        }
    }

    #[test]
    fn each_request_gets_its_reply() {
        assert!(matches!(
            respond(&Fake, IndexerRequest::Ping),
            IndexerResponse::Pong { .. }
        ));
        assert!(matches!(
            respond(&Fake, IndexerRequest::IndexStatus),
            IndexerResponse::IndexStatus { volumes } if volumes.len() == 1
        ));
        let IndexerResponse::FileSearchResults { hits, complete, .. } = respond(
            &Fake,
            IndexerRequest::Search {
                query: "cat".to_owned(),
                limit: 2,
                root: None,
            },
        ) else {
            panic!("expected hits");
        };
        assert_eq!(hits.len(), 2);
        assert!(complete);
        assert_eq!(
            respond(
                &Fake,
                IndexerRequest::Search {
                    query: "cat".to_owned(),
                    limit: 2,
                    root: Some("Q:\\".to_owned()),
                },
            ),
            IndexerResponse::Error {
                code: IndexerErrorCode::NotIndexed,
                message: "drive Q: is not indexed".to_owned()
            }
        );
    }

    #[tokio::test]
    async fn frames_that_are_not_requests_get_an_error() {
        let source: Arc<dyn IndexSource> = Arc::new(Fake);
        let reply = answer(b"not json", &source).await;
        assert!(matches!(
            reply.body,
            IndexerResponse::Error {
                code: IndexerErrorCode::ProtocolError,
                ..
            }
        ));
        let reply = answer(
            br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"delete_everything"}"#,
            &source,
        )
        .await;
        assert_eq!(reply.id.as_str(), "01J9ZQ4X7K3M5N8P2R6S0T1V4W");
        assert!(matches!(
            reply.body,
            IndexerResponse::Error {
                code: IndexerErrorCode::UnknownRequest,
                ..
            }
        ));
        let reply = answer(
            br#"{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ping"}"#,
            &source,
        )
        .await;
        assert!(matches!(reply.body, IndexerResponse::Pong { .. }));
    }

    #[test]
    fn the_security_descriptor_has_the_medium_label() {
        assert!(PIPE_SDDL.ends_with("S:(ML;;NW;;;ME)"));
        assert!(PIPE_SDDL.contains("(A;;GRGW;;;IU)"));
        assert!(PIPE_SDDL.starts_with("D:P(D;;GA;;;NU)"));
    }
}
