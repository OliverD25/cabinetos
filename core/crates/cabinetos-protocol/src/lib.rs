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
mod preview;
mod quick_view;
mod secret;
pub mod shm;
mod terminal;
mod theme;
mod update;
mod window;

#[cfg(feature = "schema")]
pub mod schema;

pub use id::{InvalidRequestId, RequestId, extension_id_problem};
pub use index::{
    FileHit, HitKind, INDEXER_PIPE_NAME, IndexState, IndexerErrorCode, IndexerRequest,
    IndexerResponse, SearchSource, VolumeStatus,
};
pub use job::{
    Conflict, ConflictKind, ConflictPolicy, JobAction, JobInfo, JobKind, JobOptions, JobProgress,
    JobRequest, JobState, JobStep, LinkPolicy, Rate, Resolution, UndoLeft, UndoLeftReason,
};
pub use market::{
    Author, Catalogue, Download, ExtensionKind, INDEX_SCHEMA_VERSION, MarketCapability,
    MarketIndex, MarketItem, Rating, Stars, Tile, ToolInfo,
};
pub use message::{
    CommandInfo, CommandInput, CommandSource, CommandTarget, DiskIdentity, EntryDetail, Envelope,
    ErrorCode, Event, Incoming, Keymap, KeymapBinding, MAX_DESCRIBED, MeasureResult, RefreshReason,
    Request, Response, SearchHit, ShellMenuItem, SortKey, SortSpec, VolumeDetails,
};
pub use plugin::{CapabilityInfo, CapabilityLevel, PluginInfo, PluginState};
pub use preview::{ChangeKind, OpenedListing, PreviewRow};
pub use quick_view::{
    MAX_KIND_PATTERNS, MAX_RENDER_SIZE, NO_VIEWER, OfferReason, QuickViewKind, QuickViewOfferItem,
    QuickViewer, THUMBNAIL_SIZES, ThumbnailReason, kind_pattern_matches, kind_pattern_order,
    kind_pattern_problem,
};
pub use secret::SecretText;
pub use terminal::{TerminalMode, TerminalSession, TerminalState};
pub use theme::{
    Chrome, Color, Decimal, FileTypeColors, METRICS, MetricSpec, MetricUnit, Metrics, MicaTint,
    Opacity, Palette, Rgb, THEME_FORMAT, TerminalColors, Theme, ThemeInfo, ThemeKind,
};
pub use update::{
    DEFAULT_UPDATE_SOURCE, UPDATE_SCHEMA_VERSION, UpdateChannel, UpdateNotes, UpdatePhase,
    UpdateRelease, UpdateRequires, UpdateStatus, UpdateZip,
};
pub use window::{Pane, PaneState, WindowPanes, WindowState, WindowTab};

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
/// `themes`; the theme kind `system`; `tools_changed` sent again to a client
/// that fell behind; version 12 Total Commander's keys and small commands
/// (sub-phase 11a): the sort key `extension`, `create_file`, `edit_path`,
/// `show_properties`, `measure_paths` with `measure_started`, the events
/// `measure_progress` and `measure_finished`, and `cancel_measure`;
/// `match_entries` with its reply `entry_matches`; and
/// `terminal_type_paths`; version 13 the foundations of Phase 14:
/// `window_state` and `get_window_state` (reply `window_state`, error
/// code `no_window`); previews (`preview_listing` and `open_preview` with
/// the reply `preview_opened`, `preview_apply` with the reply
/// `jobs_started`, `preview_cancel`, the events `preview_applied` and
/// `preview_cancelled`, the error codes `no_such_preview` and
/// `too_many_previews`, the listing header's preview flag) and the job
/// kind `steps`; secrets in the Windows Credential Manager (`secret_set`,
/// `secret_get` with the reply `secret`, `secret_delete`, `secret_list`
/// with the reply `secret_names`, the error codes `no_such_secret` and
/// `secret_error`); and `save_log_bundle` with the reply `log_bundle`;
/// version 14 in-app updates: `update_status`, `update_check`,
/// `update_download`, `update_apply`, `update_rollback` and
/// `update_snooze` with the reply `update_state`, the events
/// `update_state_changed` and `update_progress`, and the error code
/// `update_error`; version 15 the context menu of Phase 18: the command
/// source `program` and the error codes `unknown_program`,
/// `program_refused` and `command_line_too_long` of the `program.<name>`
/// commands, and Windows' own menu (`shell_menu` with the reply
/// `shell_menu`, `shell_menu_invoke`, the error codes `shell_menu_error`,
/// `no_such_menu` and `shell_menu_off`); version 16 sessions bound to a
/// pane (unit 1 of the terminal sprint): `terminal_open` takes the required
/// `pane` and the optional `mode`, `terminal_opened` and `terminal_list`
/// report the mode and whether the session is `linkable`,
/// `terminal_set_mode`, the event `terminal_mode_changed`, the error code
/// `not_linkable`; and `terminal_sync_cwd` is gone; version 17 the prompt
/// hook (unit 2 of the terminal sprint): `terminal_pane_folder` with the
/// reply `terminal_pane_folder`, the event `terminal_folder_changed`, and
/// the shell's reported `folder` in `terminal_list`; version 18 the GUI
/// context for `cab` (unit 4 of the terminal sprint): `gui_context` with
/// the reply `gui_context`, and `marked_total` in a pane of `window_state`;
/// version 19 `dual` in `window_state` and in the reply `gui_context`:
/// whether the window shows both panes, so "the other pane" can be refused
/// when only one shows (unit 5 of the terminal sprint); version 20 the two
/// marketplace catalogues (Phase 23, ADR 0022): `marketplace_refresh` and
/// `marketplace_search` take an optional `catalogue` (`extensions`, the
/// default, or `themes`), and a theme's item carries `appearance`,
/// `density` and `tile`; version 21 Quick View (Phase 25, ADR 0023):
/// `get_thumbnail` with the reply `thumbnail`, `render_image` with the
/// reply `rendered_image`, `quick_view_table` with the reply
/// `quick_view_table` and the event `quick_view_table_changed`, and
/// `quick_view_offer` with the reply `quick_view_offer`.
pub const PROTOCOL_VERSION: u32 = 21;
