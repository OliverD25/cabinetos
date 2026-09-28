//! File search, and the indexer's own pipe (`docs/indexer.md`).
//!
//! A client asks the core (`search`, `index_status` in [`crate::Request`]).
//! The core asks the elevated indexer over `\\.\pipe\cabinetos-indexer`
//! with the messages below, and falls back to walking folders when no
//! indexer answers. The indexer's messages are read-only by design (ADR
//! 0002): there is no request that changes anything.

use serde::{Deserialize, Serialize};

/// Whether a hit is a file or a directory.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum HitKind {
    /// A file (a link to a file included).
    File,
    /// A directory (a junction or a link to a directory included).
    Directory,
}

/// One file or directory a search found.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct FileHit {
    /// Its full path, such as `C:\Users\me\notes.txt`.
    pub path: String,
    /// File or directory.
    pub kind: HitKind,
    /// Its NTFS file reference number, when known (a 64-bit unsigned
    /// integer; it can exceed 2^53).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub frn: Option<u64>,
}

/// Where a search's hits came from.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum SearchSource {
    /// The indexer's index of whole volumes.
    Index,
    /// The core's own bounded walk of one folder tree (no indexer).
    Walk,
}

/// Where a volume's index is.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum IndexState {
    /// The first index is being built; searches skip the volume.
    Building,
    /// The index is current and follows the change journal.
    Ready,
    /// A new index is being built; the previous one answers meanwhile.
    Rebuilding,
    /// The volume cannot be indexed.
    Failed {
        /// Why.
        message: String,
    },
}

/// One volume, as the indexer reports it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct VolumeStatus {
    /// The drive letter.
    pub letter: char,
    /// Where its index is.
    pub state: IndexState,
    /// The entries the index holds.
    pub entries: u64,
    /// How long the last build took, in milliseconds.
    #[serde(default)]
    pub built_in_ms: Option<u64>,
    /// Change-journal bytes written but not yet applied to the index; 0 when
    /// it is current.
    #[serde(default)]
    pub journal_lag: Option<u64>,
}

pub(crate) const fn default_file_search_limit() -> u32 {
    100
}

/// A request on the indexer's pipe.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum IndexerRequest {
    /// Asks whether the indexer is alive. It answers `pong`.
    Ping,
    /// Asks for every volume's state. It answers `index_status`.
    IndexStatus,
    /// Searches the index by name. It answers `file_search_results`.
    Search {
        /// Text the names must contain, compared without case.
        query: String,
        /// The most hits to return.
        #[serde(default = "default_file_search_limit")]
        limit: u32,
        /// Only hits under this folder (at any depth); every indexed volume
        /// when absent.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        root: Option<String>,
    },
}

impl IndexerRequest {
    /// Every `type` tag an indexer request can carry.
    pub const TYPES: &'static [&'static str] = &["ping", "index_status", "search"];

    /// The `type` tag of this request on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::Ping => "ping",
            Self::IndexStatus => "index_status",
            Self::Search { .. } => "search",
        }
    }
}

/// A reply on the indexer's pipe.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum IndexerResponse {
    /// Reply to `ping`.
    Pong {
        /// The indexer's version.
        indexer_version: String,
    },
    /// Reply to `index_status`.
    IndexStatus {
        /// Every volume the indexer was asked to index.
        volumes: Vec<VolumeStatus>,
    },
    /// Reply to `search`: the hits, best first.
    FileSearchResults {
        /// The hits.
        hits: Vec<FileHit>,
        /// The indexer's time for the search, in microseconds.
        took_us: u64,
        /// False when a volume the search should cover is still being
        /// built or could not be indexed.
        complete: bool,
    },
    /// The request failed.
    Error {
        /// Why, for programs.
        code: IndexerErrorCode,
        /// Why, for people.
        message: String,
    },
}

impl IndexerResponse {
    /// The `type` tag of this reply on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::Pong { .. } => "pong",
            Self::IndexStatus { .. } => "index_status",
            Self::FileSearchResults { .. } => "file_search_results",
            Self::Error { .. } => "error",
        }
    }
}

/// Why an indexer request failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum IndexerErrorCode {
    /// The request's `type` is not one the indexer knows.
    UnknownRequest,
    /// The frame is not a valid request.
    ProtocolError,
    /// The root's volume is not indexed.
    NotIndexed,
    /// The root is not an absolute folder path, or cannot be opened.
    InvalidRoot,
    /// The indexer failed while handling a valid request.
    Internal,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;
    use crate::{Envelope, RequestId};

    const ID: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";

    #[test]
    fn indexer_messages_are_flat_and_tagged() {
        let id: RequestId = ID.parse().unwrap();
        let search = Envelope::new(
            id.clone(),
            IndexerRequest::Search {
                query: "cat".to_owned(),
                limit: 5,
                root: Some(r"C:\Users".to_owned()),
            },
        );
        assert_eq!(
            serde_json::to_value(&search).unwrap(),
            json!({"id": ID, "type": "search", "query": "cat", "limit": 5, "root": r"C:\Users"})
        );
        let defaults: IndexerRequest =
            serde_json::from_value(json!({"type": "search", "query": "x"})).unwrap();
        assert_eq!(
            defaults,
            IndexerRequest::Search {
                query: "x".to_owned(),
                limit: 100,
                root: None
            }
        );
        let reply = IndexerResponse::FileSearchResults {
            hits: vec![FileHit {
                path: r"C:\cat.jpg".to_owned(),
                kind: HitKind::File,
                frn: Some(0x0005_0000_0000_0104),
            }],
            took_us: 812,
            complete: true,
        };
        assert_eq!(
            serde_json::to_value(&reply).unwrap(),
            json!({"type": "file_search_results", "hits": [{"path": r"C:\cat.jpg", "kind": "file", "frn": 1_407_374_883_553_540_u64}], "took_us": 812, "complete": true})
        );
        let status = IndexerResponse::IndexStatus {
            volumes: vec![VolumeStatus {
                letter: 'C',
                state: IndexState::Failed {
                    message: "no journal".to_owned(),
                },
                entries: 0,
                built_in_ms: None,
                journal_lag: None,
            }],
        };
        assert_eq!(
            serde_json::to_value(&status).unwrap(),
            json!({"type": "index_status", "volumes": [{"letter": "C", "state": {"type": "failed", "message": "no journal"}, "entries": 0, "built_in_ms": null, "journal_lag": null}]})
        );
    }

    #[test]
    fn indexer_type_tags_match_the_wire() {
        let requests = [
            IndexerRequest::Ping,
            IndexerRequest::IndexStatus,
            IndexerRequest::Search {
                query: "q".to_owned(),
                limit: 1,
                root: None,
            },
        ];
        for request in &requests {
            let value = serde_json::to_value(request).unwrap();
            assert_eq!(value["type"], request.type_tag());
            assert!(IndexerRequest::TYPES.contains(&request.type_tag()));
        }
        assert_eq!(IndexerRequest::TYPES.len(), requests.len());
        let error = IndexerResponse::Error {
            code: IndexerErrorCode::NotIndexed,
            message: "drive Q: is not indexed".to_owned(),
        };
        assert_eq!(
            serde_json::to_value(&error).unwrap(),
            json!({"type": "error", "code": "not_indexed", "message": "drive Q: is not indexed"})
        );
    }
}
