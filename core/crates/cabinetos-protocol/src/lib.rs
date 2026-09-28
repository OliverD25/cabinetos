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
mod market;
mod message;
mod plugin;
pub mod shm;
mod terminal;
mod theme;

#[cfg(feature = "schema")]
pub mod schema;

pub use id::{InvalidRequestId, RequestId, extension_id_problem};
pub use index::{
    FileHit, HitKind, INDEXER_PIPE_NAME, IndexState, IndexerErrorCode, IndexerRequest,
    IndexerResponse, SearchSource, VolumeStatus,
};
pub use job::{
    Conflict, ConflictKind, ConflictPolicy, JobAction, JobInfo, JobKind, JobOptions, JobProgress,
    JobRequest, JobState, LinkPolicy, Rate, Resolution,
};
pub use market::{
    Author, Download, ExtensionKind, INDEX_SCHEMA_VERSION, MarketCapability, MarketIndex,
    MarketItem, Rating, Stars, ToolInfo,
};
pub use message::{
    CommandInfo, CommandSource, CommandTarget, DiskIdentity, EntryDetail, Envelope, ErrorCode,
    Event, Incoming, Keymap, KeymapBinding, MAX_DESCRIBED, RefreshReason, Request, Response,
    SearchHit, SortKey, SortSpec, VolumeDetails,
};
pub use plugin::{CapabilityInfo, CapabilityLevel, PluginInfo, PluginState};
pub use terminal::{TerminalSession, TerminalState};
pub use theme::{
    Color, FileTypeColors, MicaTint, Opacity, Palette, Rgb, TerminalColors, Theme, ThemeInfo,
    ThemeKind,
};

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
/// listing: `list_volumes` and the event `volumes_changed`, `get_value` and
/// `set_value` (with the reply `value`), `open_path`, `create_directory`
/// and `rename`, and the error code `already_exists`; version 9 the shell's
/// type names and icons (`describe_entries` and `entry_details`, `get_icon`
/// and `icon`); version 10 colour themes (`list_themes` and `themes`,
/// `get_theme` and `theme`, the event `theme_changed`, the error code
/// `no_such_theme`) and the marketplace (`marketplace_refresh` and
/// `marketplace_search` with the reply `marketplace_index`,
/// `install_extension`, `uninstall_extension`, `list_tools` and `tools`,
/// the events `install_progress`, `install_finished` and `tools_changed`,
/// the error codes `no_such_extension`, `marketplace_error`,
/// `hash_mismatch` and `incompatible`); version 11 the shell's small
/// requests: `items_per_second` in `job_progress`; one `marketplace_index`
/// item per extension, with `installedVersion`; each theme's `mica` in
/// `themes`.
pub const PROTOCOL_VERSION: u32 = 11;
