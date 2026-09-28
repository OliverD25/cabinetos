//! The IPC contract between the UI, the core and the indexer.
//!
//! - **Control channel.** JSON messages carried in length-prefixed frames over a
//!   named pipe (ADR 0006). Each message is an [`Envelope`]: a [`RequestId`]
//!   plus a [`Request`] or a [`Response`]. The framing itself lives in
//!   `cabinetos-ipc`.
//! - **Data channel.** `#[repr(C)]` layouts in shared memory ([`shm`]), read by
//!   the UI through a pointer with no copy.
//!
//! The Rust types here are the source of truth. With the `schema` feature, the
//! [`schema`] module exports them as JSON Schema into `sdk/protocol/`, so the C#
//! side can be checked against them.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: bulk data goes
//! through shared memory, not through serialization) and Article 12 (Unified
//! Diagnostics: one request ID follows an action through every process).
//! Brief §4.
#![forbid(unsafe_code)]

mod id;
mod index;
mod job;
mod message;
mod plugin;
pub mod shm;
mod terminal;

#[cfg(feature = "schema")]
pub mod schema;

pub use id::{InvalidRequestId, RequestId};
pub use index::{
    FileHit, HitKind, INDEXER_PIPE_NAME, IndexState, IndexerErrorCode, IndexerRequest,
    IndexerResponse, SearchSource, VolumeStatus,
};
pub use job::{
    Conflict, ConflictKind, ConflictPolicy, JobAction, JobInfo, JobKind, JobOptions, JobProgress,
    JobRequest, JobState, LinkPolicy, Resolution,
};
pub use message::{
    CommandInfo, CommandSource, CommandTarget, DiskIdentity, Envelope, ErrorCode, Event, Incoming,
    Keymap, KeymapBinding, RefreshReason, Request, Response, SearchHit, SortKey, SortSpec,
    VolumeDetails,
};
pub use plugin::{CapabilityInfo, CapabilityLevel, PluginInfo, PluginState};
pub use terminal::{TerminalSession, TerminalState};

/// Version of the control-channel protocol. The core reports it in
/// [`Response::Pong`] and [`Response::Welcome`]. Raise it whenever a message
/// is added or changes shape. Version 2 added `hello`, directory listings,
/// volume information and events; version 3 the configuration, the keymap
/// and commands, and made `list_directory`'s `include_hidden` and `sort`
/// optional (the configuration fills them in); version 4 the jobs (copy,
/// move, delete); version 5 the Recycle Bin conflict
/// (`recycle_bin_too_small`, `delete_permanently`) and the plugins; version
/// 6 file search (`search`, `file_search_results`) and the indexer's state
/// (`index_status`); version 7 terminal sessions (`terminal_open` and the
/// other `terminal_*` messages); version 8 what the shell needs beyond
/// listing: `list_volumes`, `get_value` and `set_value` (with the reply
/// `value`), `open_path`, `create_directory` and `rename`, and the error
/// code `already_exists`.
pub const PROTOCOL_VERSION: u32 = 8;
