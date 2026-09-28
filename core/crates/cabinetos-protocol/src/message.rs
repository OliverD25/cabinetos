use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::RequestId;

/// One message on the control channel: a request ID plus the message body.
///
/// On the wire the body's fields sit next to `id` in one flat JSON object:
///
/// ```json
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ping"}
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"pong","protocol_version":3,"core_version":"0.1.0"}
/// ```
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Envelope<T> {
    /// Created by the sender of a request. The reply carries the same ID. An
    /// event carries a fresh ID of its own.
    pub id: RequestId,
    /// The message itself.
    #[serde(flatten)]
    pub body: T,
}

impl<T> Envelope<T> {
    /// Wraps `body` with the given ID.
    pub fn new(id: RequestId, body: T) -> Self {
        Self { id, body }
    }
}

/// A request from a client (the UI or the CLI) to the core.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Request {
    /// Asks whether the core is alive. The core answers `pong`.
    Ping,
    /// Asks the core to exit cleanly. The core answers `ok`, then exits.
    Shutdown,
    /// Introduces the client. Must come before `list_directory` on each
    /// connection, and subscribes the connection to configuration events.
    /// The core answers `welcome`.
    Hello {
        /// The client's process ID. The core duplicates shared-memory handles
        /// into this process, so it must be the process on the other end of
        /// the pipe; the core checks that.
        client_pid: u32,
        /// A name for logs, for example `CabinetOS` or `cabinetos-cli`.
        client_name: String,
    },
    /// Lists a directory into shared memory. The core answers
    /// `listing_opened`.
    ListDirectory {
        /// The directory, as an absolute or relative Windows path.
        path: String,
        /// Also list entries with the hidden or the system attribute. When
        /// absent, the configuration's `panes.showHidden` decides.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        include_hidden: Option<bool>,
        /// The order of the entries; directories always come first. When
        /// absent, the configuration's `panes.sort` decides.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        sort: Option<SortSpec>,
        /// Keep watching the directory and send `listing_refreshed` events
        /// when it changes.
        #[serde(default)]
        watch: bool,
    },
    /// Ends a listing: stops its watcher and releases the core's side of its
    /// shared memory. The core answers `ok`.
    CloseListing {
        /// The listing, from `listing_opened`.
        listing_id: u64,
    },
    /// Asks which volume and physical disk a path lives on. The core answers
    /// `volume_info`.
    VolumeInfo {
        /// Any path on the volume; it does not have to exist.
        path: String,
    },
    /// Asks for the configuration in effect and the path of its file. The
    /// core answers `config`.
    GetConfig,
    /// Asks for the compiled keymap. The core answers `keymap`.
    GetKeymap,
    /// Asks for every command, for the command palette. The core answers
    /// `commands`.
    ListCommands,
    /// Ranks the commands against what the user typed in the palette. The
    /// core answers `search_results`.
    SearchCommands {
        /// The text typed so far; spaces are ignored.
        query: String,
        /// At most this many results.
        #[serde(default = "default_search_limit")]
        limit: u32,
    },
    /// Runs a command. The core answers `command_result` for commands it
    /// runs itself and `command_routed` for commands the UI runs.
    ExecuteCommand {
        /// The command's ID, for example `help.about`. (Named `command`,
        /// not `id`: `id` is the request's own ID in the same object.)
        command: String,
        /// Arguments, if the command takes any.
        #[serde(default)]
        args: Value,
    },
    /// Binds a command to new keys, replacing its current binding. The core
    /// writes the configuration file and answers `keymap`.
    SetKeybinding {
        /// The command's ID.
        command: String,
        /// The keys, for example `ctrl+alt+b` or `ctrl+k ctrl+b`.
        keys: String,
    },
    /// Returns a command to its default binding. The core writes the
    /// configuration file and answers `keymap`.
    ResetKeybinding {
        /// The command's ID.
        command: String,
    },
}

fn default_search_limit() -> u32 {
    20
}

impl Request {
    /// Every `type` tag a request can carry. A frame whose `type` is not in
    /// this list is answered with [`ErrorCode::UnknownRequest`].
    pub const TYPES: &'static [&'static str] = &[
        "ping",
        "shutdown",
        "hello",
        "list_directory",
        "close_listing",
        "volume_info",
        "get_config",
        "get_keymap",
        "list_commands",
        "search_commands",
        "execute_command",
        "set_keybinding",
        "reset_keybinding",
    ];

    /// The `type` tag of this request on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::Ping => "ping",
            Self::Shutdown => "shutdown",
            Self::Hello { .. } => "hello",
            Self::ListDirectory { .. } => "list_directory",
            Self::CloseListing { .. } => "close_listing",
            Self::VolumeInfo { .. } => "volume_info",
            Self::GetConfig => "get_config",
            Self::GetKeymap => "get_keymap",
            Self::ListCommands => "list_commands",
            Self::SearchCommands { .. } => "search_commands",
            Self::ExecuteCommand { .. } => "execute_command",
            Self::SetKeybinding { .. } => "set_keybinding",
            Self::ResetKeybinding { .. } => "reset_keybinding",
        }
    }
}

/// The order of a listing. Directories always come before everything else;
/// `descending` reverses the order within each of the two groups.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SortSpec {
    /// What to sort by.
    #[serde(default)]
    pub key: SortKey,
    /// Largest, newest or last first.
    #[serde(default)]
    pub descending: bool,
}

/// What a listing is sorted by.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum SortKey {
    /// Natural order, as in Explorer: case-insensitive, with numbers compared
    /// by value, so `file2` comes before `file10`.
    #[default]
    Name,
    /// File size, then name.
    Size,
    /// Last-write time, then name.
    Modified,
    /// Entry kind (directory, file, link), then name.
    Kind,
}

/// The core's reply to a [`Request`].
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Response {
    /// Reply to `ping`.
    Pong {
        /// The protocol version the core speaks (`PROTOCOL_VERSION`, now 3).
        protocol_version: u32,
        /// The core's version, for example `0.1.0`.
        core_version: String,
    },
    /// The request succeeded and returns no data.
    Ok,
    /// The request failed.
    Error {
        /// What kind of failure.
        code: ErrorCode,
        /// A human-readable explanation, for logs and error dialogs.
        message: String,
    },
    /// Reply to `hello`.
    Welcome {
        /// The protocol version the core speaks (`PROTOCOL_VERSION`, now 3).
        protocol_version: u32,
        /// The core's version, for example `0.1.0`.
        core_version: String,
    },
    /// Reply to `list_directory`: the listing is complete in shared memory.
    ListingOpened {
        /// Names this listing in `close_listing` and in events.
        listing_id: u64,
        /// A handle to the shared-memory section, already valid in the
        /// client's process. The client owns it and must close it.
        section_handle: u64,
        /// How many bytes of the section hold the listing (the layout is in
        /// `docs/ipc.md`).
        section_size: u64,
        /// Entries in the listing.
        entry_count: u32,
        /// 1 for the first listing, one more for every refresh.
        generation: u32,
        /// Microseconds the core spent reading, sorting and writing it.
        elapsed_us: u64,
    },
    /// Reply to `volume_info`.
    VolumeInfo(VolumeDetails),
    /// Reply to `get_config`.
    Config {
        /// The configuration file the core reads.
        path: String,
        /// The configuration in effect, with defaults filled in, in the
        /// file's own format (see `sdk/config/cabinetos.schema.json`).
        config: Value,
    },
    /// Reply to `get_keymap`, `set_keybinding` and `reset_keybinding`.
    Keymap(Keymap),
    /// Reply to `list_commands`.
    Commands {
        /// Every command, in registry order.
        commands: Vec<CommandInfo>,
    },
    /// Reply to `search_commands`: the best matches first.
    SearchResults {
        /// The matching commands with their scores.
        hits: Vec<SearchHit>,
    },
    /// Reply to `execute_command` for a command the UI runs: the core does
    /// nothing and hands it back.
    CommandRouted {
        /// Always `ui` for now.
        target: CommandTarget,
    },
    /// Reply to `execute_command` for a command the core ran.
    CommandResult {
        /// What the command returned.
        result: Value,
    },
}

impl Response {
    /// Every `type` tag a response can carry.
    pub const TYPES: &'static [&'static str] = &[
        "pong",
        "ok",
        "error",
        "welcome",
        "listing_opened",
        "volume_info",
        "config",
        "keymap",
        "commands",
        "search_results",
        "command_routed",
        "command_result",
    ];

    /// The `type` tag of this response on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::Pong { .. } => "pong",
            Self::Ok => "ok",
            Self::Error { .. } => "error",
            Self::Welcome { .. } => "welcome",
            Self::ListingOpened { .. } => "listing_opened",
            Self::VolumeInfo(_) => "volume_info",
            Self::Config { .. } => "config",
            Self::Keymap(_) => "keymap",
            Self::Commands { .. } => "commands",
            Self::SearchResults { .. } => "search_results",
            Self::CommandRouted { .. } => "command_routed",
            Self::CommandResult { .. } => "command_result",
        }
    }
}

/// The volume a path lives on, and the physical disk under it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct VolumeDetails {
    /// The volume's drive letter, if it has one.
    pub drive_letter: Option<char>,
    /// The volume's GUID path, for example `\\?\Volume{…}\`. Empty when the
    /// volume has none, such as a network share.
    pub volume_guid_path: String,
    /// The file system: `NTFS`, `FAT32`, `exFAT`, `ReFS`, …
    pub filesystem: String,
    /// The volume label; may be empty.
    pub label: String,
    /// The volume's capacity in bytes.
    pub total_bytes: u64,
    /// Bytes free for the current user (disk quotas taken into account).
    pub free_bytes: u64,
    /// The physical disk, when Windows can tell (not for network shares).
    pub disk: Option<DiskIdentity>,
}

/// A physical disk. Two paths on the same `device_number` share one disk, so
/// copies between them compete for it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct DiskIdentity {
    /// The disk's number, as in `\\.\PhysicalDriveN`.
    pub device_number: u32,
    /// How the disk is attached: `NVMe`, `SATA`, `USB`, `SAS`, `SCSI`, `SD`,
    /// `RAID`, `Spaces`, `Virtual`, … or `Unknown`.
    pub bus_type: String,
    /// Whether random access is slow (a spinning disk). `None` when the disk
    /// does not say.
    pub seek_penalty: Option<bool>,
    /// `HDD` or `SSD`, derived from the seek penalty; `None` when unknown.
    pub media_type: Option<String>,
}

/// The compiled keymap: every binding in effect. The UI runs the chord state
/// machine with it; the core stays the source of truth.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Keymap {
    /// After the first key of a two-key chord, how long the second may take.
    pub chord_window_ms: u32,
    /// The bindings, grouped by command in registry order.
    pub bindings: Vec<KeymapBinding>,
    /// The commands of the Immutable System Tier: their bindings cannot be
    /// changed, and no other command may use their keys.
    pub immutable: Vec<String>,
}

/// One key binding.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct KeymapBinding {
    /// Normalized keys, for example `ctrl+shift+p` or `ctrl+k ctrl+s` (a
    /// chord: two combinations, one after the other). Grammar in
    /// `docs/keybindings.md`.
    pub keys: String,
    /// The command the keys run.
    pub command: String,
    /// The context in which the binding applies, for example `filesView`;
    /// absent means everywhere.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub when: Option<String>,
}

/// A command, as the palette shows it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CommandInfo {
    /// The command's ID, `category.verbObject`, for example
    /// `view.toggleDualPane`.
    pub id: String,
    /// The palette group, for example `View`.
    pub category: String,
    /// The human name, for example `Toggle Dual Pane`.
    pub title: String,
    /// The keys bound now (after the user's changes).
    pub keys: Vec<String>,
    /// The keys it has by default.
    pub default_keys: Vec<String>,
    /// Who provides the command.
    pub source: CommandSource,
    /// Who runs it.
    pub target: CommandTarget,
    /// The context of its default bindings; absent means everywhere.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub when: Option<String>,
    /// Whether it belongs to the Immutable System Tier.
    pub immutable: bool,
}

/// Who provides a command.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum CommandSource {
    /// Built into the core.
    Core,
    /// Registered by a plugin (Phase 7).
    Plugin {
        /// The plugin's ID.
        id: String,
    },
}

/// Who runs a command.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum CommandTarget {
    /// The core runs it.
    Core,
    /// The UI runs it itself (views, panes, overlays).
    Ui,
}

/// One command matching a palette search.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SearchHit {
    /// The command's ID.
    pub id: String,
    /// Higher is better; only the order matters.
    pub score: i32,
}

/// A message the core sends on its own, not as a reply. Events share the
/// connection with replies; a client tells them apart by `type`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Event {
    /// A watched directory changed, and a new listing is complete in a new
    /// shared-memory section. The previous section stays valid until the
    /// client closes its handle to it.
    ListingRefreshed {
        /// The listing, from `listing_opened`.
        listing_id: u64,
        /// A handle to the new section, already valid in the client's
        /// process. The client owns it and must close it.
        section_handle: u64,
        /// How many bytes of the new section hold the listing.
        section_size: u64,
        /// Entries in the new listing.
        entry_count: u32,
        /// One more than the previous listing of this `listing_id`.
        generation: u32,
        /// Why the listing was read again.
        reason: RefreshReason,
    },
    /// A watched listing can no longer be kept current, for example because
    /// its directory was deleted. No more events follow for it.
    ListingLost {
        /// The listing, from `listing_opened`.
        listing_id: u64,
        /// What happened, for logs and the user.
        message: String,
    },
    /// The configuration file changed and the new settings are in effect.
    /// Sent to every connection that said `hello`.
    ConfigChanged {
        /// The settings that changed, as dotted paths such as `ui.layout`,
        /// `panes.sort` or `keybindings`.
        changed: Vec<String>,
    },
    /// The configuration file changed but cannot be used; the previous
    /// settings stay in effect.
    ConfigError {
        /// The line of the problem, counting from 1, when known.
        line: Option<u32>,
        /// The column of the problem, counting from 1, when known.
        column: Option<u32>,
        /// What is wrong.
        message: String,
    },
    /// The compiled keymap changed (after `config_changed` for the same
    /// change). Sent to every connection that said `hello`.
    KeymapChanged {
        /// The new keymap.
        keymap: Keymap,
    },
}

impl Event {
    /// Every `type` tag an event can carry. None of them is also a response
    /// tag, so a client can parse both from one stream.
    pub const TYPES: &'static [&'static str] = &[
        "listing_refreshed",
        "listing_lost",
        "config_changed",
        "config_error",
        "keymap_changed",
    ];

    /// The `type` tag of this event on the wire.
    #[must_use]
    pub const fn type_tag(&self) -> &'static str {
        match self {
            Self::ListingRefreshed { .. } => "listing_refreshed",
            Self::ListingLost { .. } => "listing_lost",
            Self::ConfigChanged { .. } => "config_changed",
            Self::ConfigError { .. } => "config_error",
            Self::KeymapChanged { .. } => "keymap_changed",
        }
    }
}

/// Why a watched listing was read again.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum RefreshReason {
    /// Entries were added, removed, renamed or modified.
    Changed,
    /// So many changes happened at once that Windows could not report them
    /// one by one; the directory was read again from scratch.
    Overflow,
}

/// Anything a client can receive: a reply or an event. On the wire both are
/// flat envelopes, told apart by their `type` tag.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(untagged)]
pub enum Incoming {
    /// A reply to one of the client's requests, matched by `id`.
    Response(Response),
    /// An event the core sent on its own.
    Event(Event),
}

/// Why a request failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    /// The envelope was valid, but its `type` is not a request this core knows.
    UnknownRequest,
    /// The frame was not a valid envelope (bad JSON, a missing or malformed
    /// `id`, fields that do not fit the request type), or the request came
    /// out of order, such as `list_directory` before `hello`.
    ProtocolError,
    /// The frame was longer than the 16 MiB limit. The core closes the
    /// connection after this reply, because large data belongs in shared
    /// memory.
    FrameTooLarge,
    /// The core failed while handling a valid request.
    Internal,
    /// The path does not exist.
    NotFound,
    /// Windows denied access to the path.
    AccessDenied,
    /// The path is malformed, or it names a file where a directory is needed.
    InvalidPath,
    /// No open listing on this connection has that `listing_id`.
    NoSuchListing,
    /// Reading from the disk or the network failed.
    Io,
    /// No command has that ID.
    UnknownCommand,
    /// The command exists but the core cannot run it yet (it arrives in a
    /// later phase).
    NotImplemented,
    /// The keys do not follow the key grammar (`docs/keybindings.md`).
    InvalidKeys,
    /// The binding would take keys another command already uses, or make a
    /// chord's first key also a binding of its own.
    KeybindingConflict,
    /// The change touches the Immutable System Tier: its commands keep their
    /// bindings, and nobody else may use their keys.
    ImmutableBinding,
    /// The configuration file cannot be changed now: it has an error the
    /// user must fix first, or it cannot be written.
    ConfigError,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    const ID: &str = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";

    fn id() -> RequestId {
        ID.parse().unwrap()
    }

    fn keymap() -> Keymap {
        Keymap {
            chord_window_ms: 1000,
            bindings: vec![
                KeymapBinding {
                    keys: "ctrl+shift+p".to_owned(),
                    command: "palette.show".to_owned(),
                    when: None,
                },
                KeymapBinding {
                    keys: "f5".to_owned(),
                    command: "file.copyToOtherPane".to_owned(),
                    when: Some("filesView".to_owned()),
                },
            ],
            immutable: vec!["palette.show".to_owned()],
        }
    }

    fn every_request() -> Vec<Request> {
        vec![
            Request::Ping,
            Request::Shutdown,
            Request::Hello {
                client_pid: 42,
                client_name: "test".to_owned(),
            },
            Request::ListDirectory {
                path: r"C:\Windows".to_owned(),
                include_hidden: Some(true),
                sort: Some(SortSpec {
                    key: SortKey::Modified,
                    descending: true,
                }),
                watch: true,
            },
            Request::CloseListing { listing_id: 7 },
            Request::VolumeInfo {
                path: r"C:\".to_owned(),
            },
            Request::GetConfig,
            Request::GetKeymap,
            Request::ListCommands,
            Request::SearchCommands {
                query: "dual".to_owned(),
                limit: 5,
            },
            Request::ExecuteCommand {
                command: "help.about".to_owned(),
                args: json!({"verbose": true}),
            },
            Request::SetKeybinding {
                command: "view.toggleSidebar".to_owned(),
                keys: "ctrl+alt+b".to_owned(),
            },
            Request::ResetKeybinding {
                command: "view.toggleSidebar".to_owned(),
            },
        ]
    }

    fn every_response() -> Vec<Response> {
        vec![
            Response::Pong {
                protocol_version: 3,
                core_version: "0.1.0".to_owned(),
            },
            Response::Ok,
            Response::Error {
                code: ErrorCode::NotFound,
                message: "no such directory".to_owned(),
            },
            Response::Welcome {
                protocol_version: 3,
                core_version: "0.1.0".to_owned(),
            },
            Response::ListingOpened {
                listing_id: 7,
                section_handle: 0x1A4,
                section_size: 4096,
                entry_count: 12,
                generation: 1,
                elapsed_us: 1234,
            },
            Response::VolumeInfo(VolumeDetails {
                drive_letter: Some('C'),
                volume_guid_path: r"\\?\Volume{0e5e0000-0000-0000-0000-100000000000}\".to_owned(),
                filesystem: "NTFS".to_owned(),
                label: String::new(),
                total_bytes: 2_000_000_000_000,
                free_bytes: 1_000_000_000_000,
                disk: Some(DiskIdentity {
                    device_number: 0,
                    bus_type: "NVMe".to_owned(),
                    seek_penalty: Some(false),
                    media_type: Some("SSD".to_owned()),
                }),
            }),
            Response::Config {
                path: r"C:\Users\me\AppData\Roaming\CabinetOS\cabinetos.json".to_owned(),
                config: json!({"version": 1, "ui": {"layout": "classic"}}),
            },
            Response::Keymap(keymap()),
            Response::Commands {
                commands: vec![CommandInfo {
                    id: "view.toggleDualPane".to_owned(),
                    category: "View".to_owned(),
                    title: "Toggle Dual Pane".to_owned(),
                    keys: vec!["ctrl+shift+d".to_owned()],
                    default_keys: vec!["ctrl+shift+d".to_owned()],
                    source: CommandSource::Core,
                    target: CommandTarget::Ui,
                    when: None,
                    immutable: false,
                }],
            },
            Response::SearchResults {
                hits: vec![SearchHit {
                    id: "view.toggleDualPane".to_owned(),
                    score: 120,
                }],
            },
            Response::CommandRouted {
                target: CommandTarget::Ui,
            },
            Response::CommandResult {
                result: json!({"product": "CabinetOS"}),
            },
        ]
    }

    fn every_event() -> Vec<Event> {
        vec![
            Event::ListingRefreshed {
                listing_id: 7,
                section_handle: 0x1A8,
                section_size: 4096,
                entry_count: 13,
                generation: 2,
                reason: RefreshReason::Overflow,
            },
            Event::ListingLost {
                listing_id: 7,
                message: "the directory was deleted".to_owned(),
            },
            Event::ConfigChanged {
                changed: vec!["ui.layout".to_owned(), "keybindings".to_owned()],
            },
            Event::ConfigError {
                line: Some(12),
                column: Some(5),
                message: "unknown field `dualPan`".to_owned(),
            },
            Event::KeymapChanged { keymap: keymap() },
        ]
    }

    #[test]
    fn ping_has_the_documented_wire_form() {
        let json = serde_json::to_string(&Envelope::new(id(), Request::Ping)).unwrap();
        assert_eq!(json, format!(r#"{{"id":"{ID}","type":"ping"}}"#));
    }

    #[test]
    fn pong_has_the_documented_wire_form() {
        let pong = Response::Pong {
            protocol_version: 3,
            core_version: "0.1.0".to_owned(),
        };
        let json = serde_json::to_string(&Envelope::new(id(), pong)).unwrap();
        assert_eq!(
            json,
            format!(r#"{{"id":"{ID}","type":"pong","protocol_version":3,"core_version":"0.1.0"}}"#)
        );
    }

    #[test]
    fn newtype_replies_are_flat_on_the_wire() {
        let volume = every_response()
            .into_iter()
            .find(|response| matches!(response, Response::VolumeInfo(_)))
            .unwrap();
        let value = serde_json::to_value(Envelope::new(id(), volume)).unwrap();
        assert_eq!(value["type"], "volume_info");
        assert_eq!(value["drive_letter"], "C");
        assert_eq!(value["disk"]["bus_type"], "NVMe");

        let value = serde_json::to_value(Envelope::new(id(), Response::Keymap(keymap()))).unwrap();
        assert_eq!(value["type"], "keymap");
        assert_eq!(value["chord_window_ms"], 1000);
        assert_eq!(value["bindings"][1]["when"], "filesView");
        assert!(value["bindings"][0].get("when").is_none());
    }

    #[test]
    fn every_message_round_trips() {
        for request in every_request() {
            let envelope = Envelope::new(id(), request);
            let json = serde_json::to_string(&envelope).unwrap();
            assert_eq!(
                serde_json::from_str::<Envelope<Request>>(&json).unwrap(),
                envelope
            );
        }
        for response in every_response() {
            let envelope = Envelope::new(id(), response);
            let json = serde_json::to_string(&envelope).unwrap();
            assert_eq!(
                serde_json::from_str::<Envelope<Response>>(&json).unwrap(),
                envelope
            );
        }
        for event in every_event() {
            let envelope = Envelope::new(id(), event);
            let json = serde_json::to_string(&envelope).unwrap();
            assert_eq!(
                serde_json::from_str::<Envelope<Event>>(&json).unwrap(),
                envelope
            );
        }
    }

    #[test]
    fn incoming_tells_replies_from_events() {
        for response in every_response() {
            let json = serde_json::to_string(&Envelope::new(id(), response.clone())).unwrap();
            let incoming: Envelope<Incoming> = serde_json::from_str(&json).unwrap();
            assert_eq!(incoming.body, Incoming::Response(response));
        }
        for event in every_event() {
            let json = serde_json::to_string(&Envelope::new(id(), event.clone())).unwrap();
            let incoming: Envelope<Incoming> = serde_json::from_str(&json).unwrap();
            assert_eq!(incoming.body, Incoming::Event(event));
        }
        let unknown = json!({"id": ID, "type": "listing_exploded"});
        assert!(serde_json::from_value::<Envelope<Incoming>>(unknown).is_err());
    }

    #[test]
    fn optional_fields_have_defaults() {
        let minimal = json!({"id": ID, "type": "list_directory", "path": r"C:\"});
        let envelope: Envelope<Request> = serde_json::from_value(minimal).unwrap();
        assert_eq!(
            envelope.body,
            Request::ListDirectory {
                path: r"C:\".to_owned(),
                include_hidden: None,
                sort: None,
                watch: false,
            }
        );
        let partial_sort =
            json!({"id": ID, "type": "list_directory", "path": "x", "sort": {"key": "size"}});
        let envelope: Envelope<Request> = serde_json::from_value(partial_sort).unwrap();
        assert!(matches!(
            envelope.body,
            Request::ListDirectory {
                sort: Some(SortSpec {
                    key: SortKey::Size,
                    descending: false
                }),
                ..
            }
        ));
        // Absent fields stay absent on the wire.
        let json = serde_json::to_value(Envelope::new(
            id(),
            Request::ListDirectory {
                path: "x".to_owned(),
                include_hidden: None,
                sort: None,
                watch: false,
            },
        ))
        .unwrap();
        assert!(json.get("include_hidden").is_none() && json.get("sort").is_none());

        let search = json!({"id": ID, "type": "search_commands", "query": "dual"});
        let envelope: Envelope<Request> = serde_json::from_value(search).unwrap();
        assert!(matches!(
            envelope.body,
            Request::SearchCommands { limit: 20, .. }
        ));
        let execute = json!({"id": ID, "type": "execute_command", "command": "help.about"});
        let envelope: Envelope<Request> = serde_json::from_value(execute).unwrap();
        assert_eq!(
            envelope.body,
            Request::ExecuteCommand {
                command: "help.about".to_owned(),
                args: Value::Null,
            }
        );
    }

    #[test]
    fn error_codes_are_snake_case() {
        let codes = [
            (ErrorCode::UnknownRequest, "unknown_request"),
            (ErrorCode::ProtocolError, "protocol_error"),
            (ErrorCode::FrameTooLarge, "frame_too_large"),
            (ErrorCode::Internal, "internal"),
            (ErrorCode::NotFound, "not_found"),
            (ErrorCode::AccessDenied, "access_denied"),
            (ErrorCode::InvalidPath, "invalid_path"),
            (ErrorCode::NoSuchListing, "no_such_listing"),
            (ErrorCode::Io, "io"),
            (ErrorCode::UnknownCommand, "unknown_command"),
            (ErrorCode::NotImplemented, "not_implemented"),
            (ErrorCode::InvalidKeys, "invalid_keys"),
            (ErrorCode::KeybindingConflict, "keybinding_conflict"),
            (ErrorCode::ImmutableBinding, "immutable_binding"),
            (ErrorCode::ConfigError, "config_error"),
        ];
        for (code, text) in codes {
            assert_eq!(serde_json::to_value(code).unwrap(), json!(text));
        }
    }

    #[test]
    fn command_sources_and_targets_have_stable_names() {
        assert_eq!(
            serde_json::to_value(CommandSource::Core).unwrap(),
            json!({"kind": "core"})
        );
        assert_eq!(
            serde_json::to_value(CommandSource::Plugin {
                id: "md".to_owned()
            })
            .unwrap(),
            json!({"kind": "plugin", "id": "md"})
        );
        assert_eq!(
            serde_json::to_value(CommandTarget::Ui).unwrap(),
            json!("ui")
        );
    }

    #[test]
    fn type_tags_match_the_wire() {
        let requests = every_request();
        assert_eq!(Request::TYPES.len(), requests.len());
        for request in requests {
            let value = serde_json::to_value(Envelope::new(id(), request.clone())).unwrap();
            assert_eq!(value["type"], json!(request.type_tag()));
            assert!(Request::TYPES.contains(&request.type_tag()));
        }
        let responses = every_response();
        assert_eq!(Response::TYPES.len(), responses.len());
        for response in responses {
            let value = serde_json::to_value(Envelope::new(id(), response.clone())).unwrap();
            assert_eq!(value["type"], json!(response.type_tag()));
            assert!(Response::TYPES.contains(&response.type_tag()));
        }
        let events = every_event();
        assert_eq!(Event::TYPES.len(), events.len());
        for event in events {
            let value = serde_json::to_value(Envelope::new(id(), event.clone())).unwrap();
            assert_eq!(value["type"], json!(event.type_tag()));
            assert!(Event::TYPES.contains(&event.type_tag()));
        }
    }

    #[test]
    fn event_tags_never_collide_with_response_tags() {
        for tag in Event::TYPES {
            assert!(
                !Response::TYPES.contains(tag),
                "{tag} is both an event and a response"
            );
        }
    }

    #[test]
    fn rejects_unknown_types_and_missing_ids() {
        let unknown = json!({"id": ID, "type": "format_disk"});
        assert!(serde_json::from_value::<Envelope<Request>>(unknown).is_err());
        let no_id = json!({"type": "ping"});
        assert!(serde_json::from_value::<Envelope<Request>>(no_id).is_err());
        let bad_id = json!({"id": "nope", "type": "ping"});
        assert!(serde_json::from_value::<Envelope<Request>>(bad_id).is_err());
    }

    #[test]
    fn ignores_unknown_fields_for_forward_compatibility() {
        let extra = json!({"id": ID, "type": "ping", "added_later": true});
        let envelope: Envelope<Request> = serde_json::from_value(extra).unwrap();
        assert_eq!(envelope.body, Request::Ping);
    }
}
