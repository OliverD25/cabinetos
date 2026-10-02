use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::RequestId;
use crate::index::{FileHit, SearchSource, VolumeStatus, default_file_search_limit};
use crate::job::{
    Conflict, JobAction, JobInfo, JobProgress, JobRequest, JobState, Resolution, UndoLeft,
};
use crate::market::{Catalogue, ExtensionKind, MarketItem, ToolInfo};
use crate::plugin::{PluginInfo, PluginState};
use crate::preview::{OpenedListing, PreviewRow};
use crate::secret::SecretText;
use crate::terminal::{TerminalMode, TerminalSession};
use crate::theme::{Theme, ThemeInfo};
use crate::update::UpdateStatus;
use crate::window::{Pane, WindowState};

/// One message on the control channel: a request ID, the trace of the user
/// action it belongs to, and the message body.
///
/// On the wire the body's fields sit next to `id` and `trace` in one flat
/// JSON object:
///
/// ```json
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ping"}
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","trace":"01J9ZQ4X7K3M5N8P2R6S0T1V4V","type":"ping"}
/// {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"pong","protocol_version":3,"core_version":"0.1.0"}
/// ```
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Envelope<T> {
    /// Created by the sender of a request. The reply carries the same ID. An
    /// event carries a fresh ID of its own.
    pub id: RequestId,
    /// The user action this message belongs to: one ULID per action, made
    /// by the window (or one per CLI run), carried by every request of the
    /// action, by its replies, and by the events of the jobs and plugin
    /// calls it started. A request without one is its own action: the core
    /// uses its `id`. Older peers leave it out and ignore it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub trace: Option<RequestId>,
    /// The message itself.
    #[serde(flatten)]
    pub body: T,
}

impl<T> Envelope<T> {
    /// Wraps `body` with the given ID and no trace.
    pub fn new(id: RequestId, body: T) -> Self {
        Self {
            id,
            trace: None,
            body,
        }
    }

    /// Wraps `body` with the given ID and trace.
    pub fn traced(id: RequestId, trace: Option<RequestId>, body: T) -> Self {
        Self { id, trace, body }
    }

    /// The action's trace: `trace`, or the message's own `id` when it
    /// carries none.
    #[must_use]
    pub fn trace_or_id(&self) -> &RequestId {
        self.trace.as_ref().unwrap_or(&self.id)
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
    /// Asks for the shell's type name and an icon key of some entries of a
    /// listing, in the order of its current section. The core answers
    /// `entry_details`.
    DescribeEntries {
        /// The listing, from `listing_opened`.
        listing_id: u64,
        /// The first entry, by its index in the section.
        from: u32,
        /// How many entries, at most [`MAX_DESCRIBED`]; fewer come back at
        /// the end of the listing.
        count: u32,
    },
    /// Finds the entries of a listing whose names match patterns, in the
    /// order of its current section: marking by pattern and quick search.
    /// The core answers `entry_matches`.
    MatchEntries {
        /// The listing, from `listing_opened`.
        listing_id: u64,
        /// Total Commander's patterns: `*` and `?`; several separated by
        /// `;`; the ones after a `|` leave names out. Case is ignored.
        patterns: String,
        /// Folders (links to folders too) never match.
        #[serde(default)]
        files_only: bool,
        /// Only the first match at or after this index, going round to the
        /// start: quick search.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        first_from: Option<u32>,
    },
    /// Asks for the icon of an icon key, as a PNG. The core answers `icon`.
    GetIcon {
        /// The key, from `entry_details`: `folder`, `generic`, `ext:.txt` or
        /// `path:…`.
        key: String,
        /// Its width and height in pixels: 16, 24, 32 or 48.
        size: u32,
    },
    /// Asks which volume and physical disk a path lives on. The core answers
    /// `volume_info`.
    VolumeInfo {
        /// Any path on the volume; it does not have to exist.
        path: String,
    },
    /// Asks for every volume that has a drive letter. The core answers
    /// `volumes`.
    ListVolumes,
    /// Opens a file or folder with its default application, as a
    /// double-click in Explorer does. The core answers `ok` once the shell
    /// has handed it over.
    OpenPath {
        /// The file or folder, as an absolute path.
        path: String,
    },
    /// Opens a file for editing, and never runs it: with `files.editor`
    /// when it is set, else with the `edit` verb of the file's type, else
    /// with Notepad. The core answers `ok` once the editor is started.
    EditPath {
        /// The file, as an absolute path.
        path: String,
    },
    /// Shows Windows' own property sheet: for one path its sheet, for
    /// several the shell's combined one. The core answers `ok` once the
    /// shell has it; the sheet stays open until the user closes it.
    ShowProperties {
        /// The files and folders, as absolute paths; at least one.
        paths: Vec<String>,
    },
    /// Creates a folder; its parent must exist. The core answers `ok`.
    CreateDirectory {
        /// The new folder, as an absolute path.
        path: String,
    },
    /// Creates an empty file; its folder must exist, and nothing is ever
    /// replaced. The core answers `ok`.
    CreateFile {
        /// The new file, as an absolute path.
        path: String,
    },
    /// Renames a file or folder in the folder it is in, never replacing
    /// anything. The core answers `ok`.
    Rename {
        /// The file or folder, as an absolute path.
        path: String,
        /// Its new name: one name, without a folder.
        new_name: String,
    },
    /// Counts the files, folders and bytes under each path, for the Size
    /// column. The core answers `measure_started`; `measure_progress`
    /// events follow while it counts, then one `measure_finished`, all on
    /// this connection.
    MeasurePaths {
        /// The files and folders, as absolute paths.
        paths: Vec<String>,
    },
    /// Stops a measure of this connection. The core answers `ok`, also for
    /// a measure that has ended already; a running one ends with
    /// `measure_finished` and `cancelled`.
    CancelMeasure {
        /// The measure, from `measure_started`.
        measure_id: u64,
    },
    /// Asks for the configuration in effect and the path of its file. The
    /// core answers `config`.
    GetConfig,
    /// Asks for one setting in effect, defaults included. The core answers
    /// `value`.
    GetValue {
        /// The setting as a dotted path, for example `ui.dualPane` or
        /// `panes.sort`: object keys only, as `config_changed` names them.
        path: String,
    },
    /// Changes one setting. The core checks it as it checks the file,
    /// writes the file and answers `ok`; every connection that said `hello`
    /// then gets `config_changed`.
    SetValue {
        /// The setting as a dotted path, for example `ui.dualPane`.
        path: String,
        /// Its new value, in the file's own format: `false`, `"rail"`,
        /// `["D:\\work"]`.
        value: Value,
    },
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
    /// Runs a command. The core answers `command_result` for the commands
    /// it runs (a plugin's) and `command_routed` for commands the UI runs.
    ExecuteCommand {
        /// The command's ID, for example `view.toggleSidebar`. (Named
        /// `command`, not `id`: `id` is the request's own ID in the same
        /// object.)
        command: String,
        /// Arguments, if the command takes any.
        #[serde(default)]
        args: Value,
    },
    /// Binds a command to new keys, replacing its current binding. The core
    /// writes the configuration file and answers `keymap`. Needs no `hello`.
    SetKeybinding {
        /// The command's ID.
        command: String,
        /// The keys, for example `ctrl+alt+b` or `ctrl+k ctrl+b`; an empty
        /// string leaves the command without a binding.
        keys: String,
    },
    /// Returns a command to its default binding. The core writes the
    /// configuration file and answers `keymap`.
    ResetKeybinding {
        /// The command's ID.
        command: String,
    },
    /// Starts a copy, move or delete job. The core answers `job_started`
    /// once the paths are checked; the work itself follows as events. Needs
    /// no `hello`, but only connections that said `hello` get the events.
    StartJob(JobRequest),
    /// Asks for every job the core knows, running or finished. The core
    /// answers `jobs`.
    ListJobs,
    /// Pauses, resumes or cancels a job. The core answers `ok`.
    JobControl {
        /// The job, from `job_started`.
        job_id: u64,
        /// What to do.
        action: JobAction,
    },
    /// Decides what happens to a file that waits on a conflict. The core
    /// answers `ok`.
    ResolveConflict {
        /// The job, from `job_conflict`.
        job_id: u64,
        /// The conflict, from `job_conflict`.
        conflict_id: u64,
        /// The decision.
        resolution: Resolution,
        /// Also use this decision for the job's other conflicts of the same
        /// kind, the waiting ones and the ones still to come.
        #[serde(default)]
        apply_to_same_kind: bool,
    },
    /// Asks for every installed plugin. The core answers `plugins`.
    ListPlugins,
    /// Starts a plugin again from its folder: after a crash, or after its
    /// files changed. The core answers `ok`.
    ReloadPlugin {
        /// The plugin. (Named `plugin_id`, not `id`: `id` is the request's
        /// own ID in the same object.)
        plugin_id: String,
    },
    /// Turns a plugin on or off. The core writes the configuration file
    /// and answers `ok`.
    SetPluginEnabled {
        /// The plugin.
        plugin_id: String,
        /// On or off.
        enabled: bool,
    },
    /// Grants capabilities a plugin asks for. The core writes the
    /// configuration file and answers `ok`; the plugin starts once it has
    /// every capability it asks for.
    GrantCapabilities {
        /// The plugin.
        plugin_id: String,
        /// Capability names, for example `fs:read`.
        capabilities: Vec<String>,
    },
    /// Searches files and folders by name. The core asks the indexer and,
    /// when none answers in time, walks one folder tree itself; it answers
    /// `file_search_results` either way.
    Search {
        /// Text the names must contain, compared without case.
        query: String,
        /// The most hits to return.
        #[serde(default = "default_file_search_limit")]
        limit: u32,
        /// Only hits under this folder. Without it, the indexer searches
        /// every indexed volume, and a walk starts at the folder this
        /// connection listed last (or the user's profile folder).
        #[serde(default, skip_serializing_if = "Option::is_none")]
        root: Option<String>,
    },
    /// Asks for the indexer's state. The core answers `index_status`.
    IndexStatus,
    /// Starts a shell in a pseudo-console. The core answers
    /// `terminal_opened`; the session's bytes then travel on its own pipe.
    TerminalOpen {
        /// A profile from `terminal.profiles`; `terminal.defaultProfile`
        /// when absent.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        profile: Option<String>,
        /// The folder the shell starts in; the user's profile folder when
        /// absent.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        cwd: Option<String>,
        /// Width in character cells.
        cols: u16,
        /// Height in character cells.
        rows: u16,
        /// The file pane the session belongs to.
        pane: Pane,
        /// How it is bound to that pane; `locked` when absent. `linked`
        /// for a profile that is not linkable is `not_linkable`.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        mode: Option<TerminalMode>,
    },
    /// Changes a session's size. The core answers `ok`.
    TerminalResize {
        /// The session.
        session_id: u64,
        /// Width in character cells.
        cols: u16,
        /// Height in character cells.
        rows: u16,
    },
    /// Closes a session's pseudo-console, which ends its shell, and forgets
    /// the session. The core answers `ok`.
    TerminalClose {
        /// The session.
        session_id: u64,
    },
    /// Types paths at a session's prompt, each quoted as the shell reads it
    /// literally, separated by spaces, without Enter. The core answers
    /// `ok`.
    TerminalTypePaths {
        /// The session.
        session_id: u64,
        /// The paths, in the order to type them.
        paths: Vec<String>,
    },
    /// Changes how a session is bound to its pane. The core answers `ok`,
    /// and every connection that said `hello` gets `terminal_mode_changed`
    /// when the mode changed. `linked` for a session whose profile is not
    /// linkable is `not_linkable`.
    TerminalSetMode {
        /// The session.
        session_id: u64,
        /// The new mode.
        mode: TerminalMode,
    },
    /// Asks for every session. The core answers `terminal_sessions`.
    TerminalList,
    /// Asks for a session's mode and its pane's current folder, as the
    /// window last said (`window_state`). A linked shell's prompt hook asks
    /// this through `cab term cwd` each time the shell draws its prompt.
    /// The core answers `terminal_pane_folder`; it needs no `hello`.
    TerminalPaneFolder {
        /// The session.
        session_id: u64,
    },
    /// Asks for every valid theme in the themes folder. The core answers
    /// `themes`.
    ListThemes,
    /// Asks for one whole theme. The core answers `theme`.
    GetTheme {
        /// The theme's ID; without it, the theme in effect now. (Named
        /// `theme_id`, not `id`: `id` is the request's own ID in the same
        /// object.)
        #[serde(default, skip_serializing_if = "Option::is_none")]
        theme_id: Option<String>,
    },
    /// Asks for every installed Tool Extension. The core answers `tools`.
    ListTools,
    /// Reads a marketplace catalogue: the extensions' `index.json` named by
    /// `marketplace.index`, or the themes' `themes.json` named by
    /// `marketplace.themes` (while that address answers 404, the theme items
    /// of `index.json`). From disk, or from the web. The core answers
    /// `marketplace_index`. The core reaches the network only for this
    /// request, `marketplace_search` and `install_extension`.
    MarketplaceRefresh {
        /// Which list: the extensions (`index.json`, the default) or the
        /// themes (`themes.json`). Each is read, cached and answered on
        /// its own, so one that cannot be reached does not hide the other.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        catalogue: Option<Catalogue>,
    },
    /// Searches the catalogue read last (read first when there is none, or
    /// when its address changed). The core answers `marketplace_index`
    /// with the matching items, best first.
    MarketplaceSearch {
        /// Text to look for in the name, the ID or the publisher, ranked as
        /// the palette ranks commands; empty for every item.
        query: String,
        /// Only items of this kind.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        kind: Option<ExtensionKind>,
        /// Which list; without it, the extensions.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        catalogue: Option<Catalogue>,
    },
    /// Reads a theme of the themes catalogue without installing it, so the
    /// gallery can preview it on the whole window before the user installs
    /// it: the core downloads the theme file (into memory, nothing is put
    /// in the themes folder), checks its SHA-256 and the theme, and answers
    /// `theme` with the whole theme. An installed theme is read with
    /// `get_theme`.
    PreviewTheme {
        /// The theme's ID in the catalogue. (Named `extension_id`, not `id`:
        /// `id` is the request's own ID in the same object.)
        extension_id: String,
    },
    /// Downloads an extension from the index, checks its SHA-256, and
    /// installs it. The core answers `ok` once it is in place; a plugin then
    /// waits in `needs_review`. `install_progress` and `install_finished`
    /// follow its way.
    InstallExtension {
        /// The extension's ID in the index. (Named `extension_id`, not `id`:
        /// `id` is the request's own ID in the same object.)
        extension_id: String,
        /// The version; without it, the newest version this core can run.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        version: Option<String>,
    },
    /// Removes exactly the files an install put in place (a plugin's own
    /// data folder stays). The core answers `ok`.
    UninstallExtension {
        /// The extension's ID.
        extension_id: String,
    },
    /// Tells the core what a window shows: the pane that has the keyboard,
    /// each pane's tabs, its cursor row and its marked rows. The core keeps
    /// the last state of each client (the connection) until the client
    /// disconnects, and only stores it; a window may send it at every
    /// change. Needs `hello`. The core answers `ok`.
    WindowState(WindowState),
    /// Asks what a window shows. The core answers `window_state` with the
    /// state of the client that sent one last, or of the named client;
    /// `no_window` when there is none.
    GetWindowState {
        /// A client, as a `window_state` reply names it (`CabinetOS#2`);
        /// without it, the client that sent its state last.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        client: Option<String>,
    },
    /// Asks what the window the user works in shows, as a shell's `cab`
    /// needs it: the folder each pane shows and the active pane's
    /// selection. Answered from the newest `window_state` in memory; no
    /// file is read. The core answers `gui_context`, or `no_window` when
    /// no window has said what it shows. Needs no `hello`.
    GuiContext,
    /// Proposes changes without making them: the core checks the rows and
    /// writes them into shared memory as a listing, which a pane shows
    /// like a folder. Needs `hello`. The core answers `preview_opened`.
    PreviewListing {
        /// What the preview is about, for the pane's title.
        title: String,
        /// The changes, in the order they would run.
        rows: Vec<PreviewRow>,
    },
    /// Opens the listing of a preview that exists already, such as one a
    /// plugin proposed. Needs `hello`. The core answers `preview_opened`.
    OpenPreview {
        /// The preview, from `preview_opened` or an event that names it.
        preview: String,
    },
    /// Runs a preview's rows in order, as jobs; the preview is gone
    /// afterwards. The core answers `jobs_started`, and every client that
    /// said `hello` gets `preview_applied`.
    PreviewApply {
        /// The preview.
        preview: String,
    },
    /// Drops a preview without running it. The core answers `ok`, and every
    /// client that said `hello` gets `preview_cancelled`.
    PreviewCancel {
        /// The preview.
        preview: String,
    },
    /// Stores a secret (an API key, a token) in the Windows Credential
    /// Manager as `CabinetOS/<name>`, replacing one of that name. Only a
    /// window or the command line sends it: no plugin can. The core
    /// answers `ok`; it never logs the value.
    SecretSet {
        /// 1 to 128 letters, digits, `-`, `_` and `.`, such as `anthropic`.
        name: String,
        /// The value, 1 to 2,560 bytes of UTF-8.
        value: SecretText,
    },
    /// Reads a secret. The core answers `secret`.
    SecretGet {
        /// The secret's name.
        name: String,
    },
    /// Removes a secret. The core answers `ok`.
    SecretDelete {
        /// The secret's name.
        name: String,
    },
    /// Asks for the names of every stored secret. The core answers
    /// `secret_names`.
    SecretList,
    /// Asks the core for a log bundle: a zip in the log folder with the last
    /// `minutes` of every process's log files, the recent crash traces and
    /// a `bundle.json` about the machine (docs/diagnostics.md, "Bundles").
    /// The core answers `log_bundle`.
    SaveLogBundle {
        /// How many minutes back, 1 to 1,440; 10 when left out.
        #[serde(default = "default_bundle_minutes")]
        minutes: u32,
    },
    /// Reverses a finished job as a new `steps` job, from the undo journal
    /// (docs/jobs.md, "Undo"): renames and moves go back, copies go to the
    /// Recycle Bin, replaced files come back from their saved copies. The
    /// core answers `undo_started`; `not_undoable` when nothing can be
    /// reversed (a delete, a job already undone, one still running).
    UndoJob {
        /// The job to undo; absent means the newest job that is not an
        /// undo itself and was not undone yet.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        job: Option<u64>,
    },
    /// Asks which workspace a folder belongs to, for the window's workspace
    /// pill and Quick Open (Phase 16). Until workspaces exist it is the git
    /// repository that holds the folder. The core looks for the nearest
    /// `.git` at or above the folder and reads at most 1 KB of that
    /// repository's files (`HEAD`, and a worktree's `.git` file); it never
    /// runs git. The core answers `workspace_info`.
    WorkspaceInfo {
        /// An absolute folder; it need not exist.
        path: String,
    },
    /// Asks where the updater is. The core answers `update_state` at once,
    /// from what it keeps in memory.
    UpdateStatus,
    /// Reads the channel's `latest.json` now and compares its version with
    /// the running one. The core answers `update_state` once the check
    /// ended; `update_error` when this install cannot update itself.
    UpdateCheck,
    /// Downloads the newer version the last check found, checks its
    /// SHA-256 and unpacks it. `update_progress` tells how far; the core
    /// answers `update_state` (`downloaded`), or `hash_mismatch`.
    UpdateDownload,
    /// Swaps the downloaded version into the install folder; the running
    /// version goes into `previous\`. The core answers `update_state`
    /// (`ready`); the window then starts the new `CabinetOS.exe` and closes.
    UpdateApply,
    /// Brings the version in `previous\` back, the same way. The core
    /// answers `update_state` (`ready`).
    UpdateRollback,
    /// Records "not before a day from now": the window opens no update
    /// dialog until then (the user said Later). The core answers
    /// `update_state`.
    UpdateSnooze,
    /// Asks for Windows' own context menu of `paths` (Phase 18), when
    /// `contextMenu.shellMenu` is on. The core builds it on a background
    /// thread of its own and answers `shell_menu` within 3 s, else
    /// `shell_menu_error`; `shell_menu_off` while the setting is off. The
    /// menu stays until an item is chosen or 30 s pass; a new one from the
    /// same connection replaces it.
    ShellMenu {
        /// Absolute paths, all in one folder: the right-clicked entry, or
        /// the selection it is part of.
        paths: Vec<String>,
    },
    /// Runs an item of a menu from `shell_menu`, as a click in Explorer's
    /// menu does; the menu is gone afterwards. The core answers `ok` once
    /// the item is done, or after 5 s while it still runs (a dialog it
    /// opened); `no_such_menu` when the menu was used, expired or is
    /// another connection's; `shell_menu_error` when Windows refused.
    ShellMenuInvoke {
        /// The menu, from `shell_menu`.
        menu_id: u64,
        /// The item's `id`.
        item_id: u32,
    },
}

fn default_bundle_minutes() -> u32 {
    10
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
        "describe_entries",
        "match_entries",
        "get_icon",
        "volume_info",
        "list_volumes",
        "open_path",
        "edit_path",
        "show_properties",
        "create_directory",
        "create_file",
        "rename",
        "measure_paths",
        "cancel_measure",
        "get_config",
        "get_value",
        "set_value",
        "get_keymap",
        "list_commands",
        "search_commands",
        "execute_command",
        "set_keybinding",
        "reset_keybinding",
        "start_job",
        "list_jobs",
        "job_control",
        "resolve_conflict",
        "list_plugins",
        "reload_plugin",
        "set_plugin_enabled",
        "grant_capabilities",
        "search",
        "index_status",
        "terminal_open",
        "terminal_resize",
        "terminal_close",
        "terminal_type_paths",
        "terminal_set_mode",
        "terminal_list",
        "terminal_pane_folder",
        "list_themes",
        "get_theme",
        "list_tools",
        "marketplace_refresh",
        "marketplace_search",
        "preview_theme",
        "install_extension",
        "uninstall_extension",
        "window_state",
        "get_window_state",
        "gui_context",
        "preview_listing",
        "open_preview",
        "preview_apply",
        "preview_cancel",
        "secret_set",
        "secret_get",
        "secret_delete",
        "secret_list",
        "save_log_bundle",
        "undo_job",
        "workspace_info",
        "update_status",
        "update_check",
        "update_download",
        "update_apply",
        "update_rollback",
        "update_snooze",
        "shell_menu",
        "shell_menu_invoke",
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
            Self::DescribeEntries { .. } => "describe_entries",
            Self::MatchEntries { .. } => "match_entries",
            Self::GetIcon { .. } => "get_icon",
            Self::VolumeInfo { .. } => "volume_info",
            Self::ListVolumes => "list_volumes",
            Self::OpenPath { .. } => "open_path",
            Self::EditPath { .. } => "edit_path",
            Self::ShowProperties { .. } => "show_properties",
            Self::CreateDirectory { .. } => "create_directory",
            Self::CreateFile { .. } => "create_file",
            Self::Rename { .. } => "rename",
            Self::MeasurePaths { .. } => "measure_paths",
            Self::CancelMeasure { .. } => "cancel_measure",
            Self::GetConfig => "get_config",
            Self::GetValue { .. } => "get_value",
            Self::SetValue { .. } => "set_value",
            Self::GetKeymap => "get_keymap",
            Self::ListCommands => "list_commands",
            Self::SearchCommands { .. } => "search_commands",
            Self::ExecuteCommand { .. } => "execute_command",
            Self::SetKeybinding { .. } => "set_keybinding",
            Self::ResetKeybinding { .. } => "reset_keybinding",
            Self::StartJob(_) => "start_job",
            Self::ListJobs => "list_jobs",
            Self::JobControl { .. } => "job_control",
            Self::ResolveConflict { .. } => "resolve_conflict",
            Self::ListPlugins => "list_plugins",
            Self::ReloadPlugin { .. } => "reload_plugin",
            Self::SetPluginEnabled { .. } => "set_plugin_enabled",
            Self::GrantCapabilities { .. } => "grant_capabilities",
            Self::Search { .. } => "search",
            Self::IndexStatus => "index_status",
            Self::TerminalOpen { .. } => "terminal_open",
            Self::TerminalResize { .. } => "terminal_resize",
            Self::TerminalClose { .. } => "terminal_close",
            Self::TerminalTypePaths { .. } => "terminal_type_paths",
            Self::TerminalSetMode { .. } => "terminal_set_mode",
            Self::TerminalList => "terminal_list",
            Self::TerminalPaneFolder { .. } => "terminal_pane_folder",
            Self::ListThemes => "list_themes",
            Self::GetTheme { .. } => "get_theme",
            Self::ListTools => "list_tools",
            Self::MarketplaceRefresh { .. } => "marketplace_refresh",
            Self::MarketplaceSearch { .. } => "marketplace_search",
            Self::PreviewTheme { .. } => "preview_theme",
            Self::InstallExtension { .. } => "install_extension",
            Self::UninstallExtension { .. } => "uninstall_extension",
            Self::WindowState(_) => "window_state",
            Self::GetWindowState { .. } => "get_window_state",
            Self::GuiContext => "gui_context",
            Self::PreviewListing { .. } => "preview_listing",
            Self::OpenPreview { .. } => "open_preview",
            Self::PreviewApply { .. } => "preview_apply",
            Self::PreviewCancel { .. } => "preview_cancel",
            Self::SecretSet { .. } => "secret_set",
            Self::SecretGet { .. } => "secret_get",
            Self::SecretDelete { .. } => "secret_delete",
            Self::SecretList => "secret_list",
            Self::SaveLogBundle { .. } => "save_log_bundle",
            Self::UndoJob { .. } => "undo_job",
            Self::WorkspaceInfo { .. } => "workspace_info",
            Self::UpdateStatus => "update_status",
            Self::UpdateCheck => "update_check",
            Self::UpdateDownload => "update_download",
            Self::UpdateApply => "update_apply",
            Self::UpdateRollback => "update_rollback",
            Self::UpdateSnooze => "update_snooze",
            Self::ShellMenu { .. } => "shell_menu",
            Self::ShellMenuInvoke { .. } => "shell_menu_invoke",
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
    /// Files by extension (the part after the last dot), ignoring case, then
    /// by name; files without one first. Directories by name.
    Extension,
}

/// The core's reply to a [`Request`].
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Response {
    /// Reply to `ping`.
    Pong {
        /// The protocol version the core speaks (`PROTOCOL_VERSION`).
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
        /// The protocol version the core speaks (`PROTOCOL_VERSION`).
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
    /// Reply to `describe_entries`: one detail per entry, from `from` on.
    EntryDetails {
        /// The listing.
        listing_id: u64,
        /// The generation of the section the entries were read from; the
        /// details belong to that one.
        generation: u32,
        /// The index of the first entry described.
        from: u32,
        /// The entries' details, in section order.
        details: Vec<EntryDetail>,
    },
    /// Reply to `match_entries`.
    EntryMatches {
        /// The listing.
        listing_id: u64,
        /// The generation of the section the names were read from.
        generation: u32,
        /// The matching indexes as `[start, count]` pairs, in order; with
        /// `first_from`, at most one `[index, 1]`.
        ranges: Vec<[u32; 2]>,
    },
    /// Reply to `get_icon`.
    Icon {
        /// The key asked for.
        key: String,
        /// The width and height in pixels.
        size: u32,
        /// The icon as a PNG with an alpha channel, in base64.
        png_base64: String,
    },
    /// Reply to `measure_paths`: every path is there, and the count has
    /// begun.
    MeasureStarted {
        /// Names the measure in `cancel_measure` and in its events; unique
        /// for the life of the core.
        measure_id: u64,
    },
    /// Reply to `volume_info`.
    VolumeInfo(VolumeDetails),
    /// Reply to `list_volumes`: the volumes that answered, by drive letter.
    Volumes {
        /// Each volume as `volume_info` describes it.
        volumes: Vec<VolumeDetails>,
    },
    /// Reply to `get_config`.
    Config {
        /// The configuration file the core reads.
        path: String,
        /// The configuration in effect, with defaults filled in, in the
        /// file's own format (see `sdk/config/cabinetos.schema.json`).
        config: Value,
    },
    /// Reply to `get_value`.
    Value {
        /// The setting in effect, in the file's own format.
        value: Value,
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
    /// Reply to `start_job`: the job exists and is queued.
    JobStarted {
        /// Names the job in later requests and events; unique for the life
        /// of the core.
        job_id: u64,
    },
    /// Reply to `list_jobs`: every job, oldest first.
    Jobs {
        /// The jobs.
        jobs: Vec<JobInfo>,
    },
    /// Reply to `list_plugins`: every installed plugin, by ID.
    Plugins {
        /// The plugins.
        plugins: Vec<PluginInfo>,
    },
    /// Reply to `search`: the hits, best first (names that start with the
    /// query, then shorter names, then paths in order).
    FileSearchResults {
        /// The hits.
        hits: Vec<FileHit>,
        /// The indexer's index, or the core's own walk.
        source: SearchSource,
        /// How long the search took, in microseconds.
        took_us: u64,
        /// False when the search could not cover everything: a walk stopped
        /// at its time or entry limit, or a volume was still being indexed.
        complete: bool,
    },
    /// Reply to `index_status`.
    IndexStatus {
        /// Whether an indexer answered.
        available: bool,
        /// Its volumes; empty without an indexer.
        volumes: Vec<VolumeStatus>,
    },
    /// Reply to `terminal_open`: the shell runs.
    TerminalOpened {
        /// The new session's ID.
        session_id: u64,
        /// Its byte pipe: open it to read the shell's output and send it
        /// input (one client at a time).
        pipe: String,
        /// The shell's process ID.
        pid: u32,
        /// How the session is bound to its pane: the one asked for, or
        /// `locked`.
        mode: TerminalMode,
        /// Whether the session may be `linked` (its profile's `linkable`).
        linkable: bool,
    },
    /// Reply to `terminal_list`: every session, oldest first.
    TerminalSessions {
        /// The sessions.
        sessions: Vec<TerminalSession>,
    },
    /// Reply to `terminal_pane_folder`.
    TerminalPaneFolder {
        /// The session.
        session_id: u64,
        /// The file pane it belongs to.
        pane: Pane,
        /// How it is bound to that pane: a linked shell follows `folder`.
        mode: TerminalMode,
        /// The folder the pane shows (its tab in front), as the newest
        /// `window_state` says; `null` when no window has said what it
        /// shows, or the tab in front shows a tool.
        folder: Option<String>,
    },
    /// Reply to `list_themes`: every valid theme, by ID.
    Themes {
        /// The themes.
        themes: Vec<ThemeInfo>,
    },
    /// Reply to `get_theme` and `preview_theme`.
    Theme {
        /// The whole theme, as its file has it.
        theme: Box<Theme>,
    },
    /// Reply to `list_tools`: every installed Tool Extension, by ID.
    Tools {
        /// The tools.
        tools: Vec<ToolInfo>,
    },
    /// Reply to `marketplace_refresh` and `marketplace_search`.
    MarketplaceIndex {
        /// The items, in the index's own format (camelCase keys): in index
        /// order after a refresh, best first after a search.
        items: Vec<MarketItem>,
        /// Where the index came from: its URL or its file.
        source: String,
        /// When the index was read or confirmed unchanged, in milliseconds
        /// since 1970-01-01 UTC.
        fetched_at_ms: u64,
    },
    /// Reply to `get_window_state`.
    WindowState {
        /// The client that sent the state: its `hello` name and a number
        /// the core gives each connection, such as `CabinetOS#2`.
        client: String,
        /// When the core received the state, in milliseconds since
        /// 1970-01-01 UTC.
        sent_at_ms: u64,
        /// The state, as the client sent it.
        state: WindowState,
    },
    /// Reply to `gui_context`: what the newest window shows, worked out
    /// from its `window_state`.
    GuiContext {
        /// The pane that has the keyboard.
        active: Pane,
        /// The folder the left pane shows (its tab in front); `null` when
        /// that tab shows a tool, or the folder is not an absolute path.
        left: Option<String>,
        /// The folder the right pane shows, as `left`.
        right: Option<String>,
        /// What a command acts on in the active pane, by full path: its
        /// marked rows, or, when none is marked, the row the cursor is on;
        /// empty in an empty folder or in a tab that shows a tool.
        selection: Vec<String>,
        /// How many rows the selection has. More than `selection` lists
        /// when the window sent only the first 1,000 marked rows: a
        /// program that acts on the selection must then refuse.
        selection_total: u32,
        /// The full path of the row the cursor is on in the active pane;
        /// `null` in an empty folder or in a tab that shows a tool.
        cursor: Option<String>,
        /// Whether the window shows both panes (protocol version 19). With
        /// one pane shown, `right` is a folder nobody sees: a program that
        /// means "the other pane" must refuse.
        dual: bool,
    },
    /// Reply to `preview_listing` and `open_preview`: the preview is
    /// complete in shared memory.
    PreviewOpened {
        /// Names the preview in `preview_apply`, `preview_cancel` and the
        /// events.
        preview: String,
        /// What the preview is about.
        title: String,
        /// The listing, as `listing_opened` describes one; its header has
        /// the preview flag and each row's change and target.
        listing: OpenedListing,
    },
    /// Reply to `preview_apply`: the jobs that run the rows, in order. Each
    /// starts when the one before it ends.
    JobsStarted {
        /// The jobs' IDs, as `job_started` gives one.
        jobs: Vec<u64>,
    },
    /// Reply to `secret_get`.
    Secret {
        /// The value.
        value: SecretText,
    },
    /// Reply to `secret_list`: the names, sorted; never the values.
    SecretNames {
        /// The names.
        names: Vec<String>,
    },
    /// Reply to `save_log_bundle`.
    LogBundle {
        /// The zip's full path, in the log folder.
        path: String,
    },
    /// Reply to `undo_job`: the job that reverses it runs now.
    UndoStarted {
        /// The new job, a `steps` job, as `job_started` gives one.
        job_id: u64,
        /// The job it reverses.
        undoes: u64,
        /// What it cannot bring back, and why; empty when it reverses
        /// everything.
        left: Vec<UndoLeft>,
    },
    /// Reply to `workspace_info`.
    WorkspaceInfo {
        /// The nearest folder at or above the asked folder that holds a
        /// `.git` folder or file; the asked folder itself (without a
        /// trailing backslash) when none does.
        root: String,
        /// The branch the repository's `HEAD` names (`ref: refs/heads/<name>`
        /// gives the name); the first 7 characters of the commit for a
        /// detached `HEAD`; null outside a repository, or when `HEAD`
        /// cannot be read or names neither.
        branch: Option<String>,
    },
    /// Reply to the `update_*` requests: where the updater is now.
    UpdateState(Box<UpdateStatus>),
    /// Reply to `shell_menu`: Windows' menu, alive until an item is chosen
    /// or 30 s pass.
    ShellMenu {
        /// The menu, for `shell_menu_invoke`.
        menu_id: u64,
        /// Its items, top to bottom, without the ones Windows shows
        /// disabled or draws itself.
        items: Vec<ShellMenuItem>,
    },
}

/// One item of Windows' own context menu (`shell_menu`).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ShellMenuItem {
    /// What `shell_menu_invoke` takes; 0 for a separator and for an item
    /// that only opens a submenu.
    pub id: u32,
    /// The text as Windows shows it, without the `&` of its access key;
    /// empty for a separator.
    pub text: String,
    /// A divider line.
    pub separator: bool,
    /// A submenu's items, one level deep (a submenu inside it is left out).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub items: Vec<ShellMenuItem>,
}

impl Response {
    /// Every `type` tag a response can carry.
    pub const TYPES: &'static [&'static str] = &[
        "pong",
        "ok",
        "error",
        "welcome",
        "listing_opened",
        "entry_details",
        "entry_matches",
        "icon",
        "measure_started",
        "volume_info",
        "volumes",
        "config",
        "value",
        "keymap",
        "commands",
        "search_results",
        "command_routed",
        "command_result",
        "job_started",
        "jobs",
        "plugins",
        "file_search_results",
        "index_status",
        "terminal_opened",
        "terminal_sessions",
        "terminal_pane_folder",
        "themes",
        "theme",
        "tools",
        "marketplace_index",
        "window_state",
        "gui_context",
        "preview_opened",
        "jobs_started",
        "secret",
        "secret_names",
        "log_bundle",
        "undo_started",
        "workspace_info",
        "update_state",
        "shell_menu",
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
            Self::EntryDetails { .. } => "entry_details",
            Self::EntryMatches { .. } => "entry_matches",
            Self::Icon { .. } => "icon",
            Self::MeasureStarted { .. } => "measure_started",
            Self::VolumeInfo(_) => "volume_info",
            Self::Volumes { .. } => "volumes",
            Self::Config { .. } => "config",
            Self::Value { .. } => "value",
            Self::Keymap(_) => "keymap",
            Self::Commands { .. } => "commands",
            Self::SearchResults { .. } => "search_results",
            Self::CommandRouted { .. } => "command_routed",
            Self::CommandResult { .. } => "command_result",
            Self::JobStarted { .. } => "job_started",
            Self::Jobs { .. } => "jobs",
            Self::Plugins { .. } => "plugins",
            Self::FileSearchResults { .. } => "file_search_results",
            Self::IndexStatus { .. } => "index_status",
            Self::TerminalOpened { .. } => "terminal_opened",
            Self::TerminalSessions { .. } => "terminal_sessions",
            Self::TerminalPaneFolder { .. } => "terminal_pane_folder",
            Self::Themes { .. } => "themes",
            Self::Theme { .. } => "theme",
            Self::Tools { .. } => "tools",
            Self::MarketplaceIndex { .. } => "marketplace_index",
            Self::WindowState { .. } => "window_state",
            Self::GuiContext { .. } => "gui_context",
            Self::PreviewOpened { .. } => "preview_opened",
            Self::JobsStarted { .. } => "jobs_started",
            Self::Secret { .. } => "secret",
            Self::SecretNames { .. } => "secret_names",
            Self::LogBundle { .. } => "log_bundle",
            Self::UndoStarted { .. } => "undo_started",
            Self::WorkspaceInfo { .. } => "workspace_info",
            Self::UpdateState(_) => "update_state",
            Self::ShellMenu { .. } => "shell_menu",
        }
    }
}

/// The most entries one `describe_entries` may ask for.
pub const MAX_DESCRIBED: u32 = 512;

/// What a file pane shows beside a name, from the shell.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct EntryDetail {
    /// The shell's name for the type, in the user's language, for example
    /// `Text Document` or `File folder`.
    pub type_name: String,
    /// The icon to show, for `get_icon`: `folder` for every folder,
    /// `ext:<extension>` (lower case, with its dot) for a file by its
    /// extension, `generic` for a file without one, and `path:<16 hex
    /// digits>` for an `.exe`, `.ico` or `.lnk` file, whose icon is its own.
    pub icon_key: String,
}

/// What a measure found under one path.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct MeasureResult {
    /// The path, as the request gave it.
    pub path: String,
    /// Files, links to files included; 1 for a file.
    pub files: u64,
    /// Folders under it (not itself), links to folders included; a link
    /// is never entered.
    pub folders: u64,
    /// The sizes of the files together.
    pub bytes: u64,
    /// Folders that could not be read; what is in them is not counted.
    pub unreadable: u64,
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
    /// When the command wants a line of text first: the window asks for it
    /// and runs the command with `{"input": <text>, "path", "paths"}`.
    /// Only plugins' commands have it (`plugin.json`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub input: Option<CommandInput>,
}

/// The prompt a command that wants a line of text shows.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields)]
pub struct CommandInput {
    /// The prompt's heading; absent means the command's title.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    /// The grey text in the empty box, such as `Ask the agent…`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub placeholder: Option<String>,
}

/// Who provides a command.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum CommandSource {
    /// Built into the core.
    Core,
    /// Registered by a plugin.
    Plugin {
        /// The plugin's ID.
        id: String,
        /// The plugin's display name, for the badge on its commands.
        name: String,
    },
    /// An entry of `programs` in the configuration: the command
    /// `program.<name>`, which the core runs.
    Program {
        /// The program's `name`.
        name: String,
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
    /// How far a job has come. At most 30 per second per job, and always
    /// one when the job ends. Sent to every connection that said `hello`.
    JobProgress(JobProgress),
    /// A file of a job needs a decision (`resolve_conflict`). The file waits;
    /// the rest of the job goes on.
    JobConflict(Conflict),
    /// A job changed state. After a final state (`completed`,
    /// `completed_with_errors`, `cancelled`, `failed`) no more events follow
    /// for that job.
    JobStateChanged {
        /// The job.
        job_id: u64,
        /// Its new state.
        state: JobState,
    },
    /// A plugin changed state. Sent to every connection that said `hello`.
    PluginStateChanged {
        /// The plugin.
        plugin_id: String,
        /// Its new state.
        state: PluginState,
    },
    /// A plugin trapped or ran out of time, fuel or memory. Its instance is
    /// gone and its commands are unregistered; the core goes on.
    PluginCrashed {
        /// The plugin.
        plugin_id: String,
        /// The trap.
        message: String,
    },
    /// An event a plugin sent (capability `events:emit`).
    PluginEvent {
        /// The plugin.
        plugin_id: String,
        /// The event's name, chosen by the plugin.
        name: String,
        /// Its payload, as the plugin wrote it (usually JSON).
        payload: String,
    },
    /// A terminal session's shell exited. Sent to every connection that said
    /// `hello`. Its last output is on the byte pipe before it closes.
    TerminalExited {
        /// The session.
        session_id: u64,
        /// The shell's exit code.
        exit_code: u32,
    },
    /// A terminal session's mode changed (`terminal_set_mode`, from any
    /// client). Sent to every connection that said `hello`, so a second
    /// window or the command line sees it.
    TerminalModeChanged {
        /// The session.
        session_id: u64,
        /// Its new mode.
        mode: TerminalMode,
    },
    /// A terminal session's shell reported a new current folder: its
    /// prompt hook prints it (OSC 9;9) each time the shell draws its
    /// prompt, and the core sends this when it differs from the last one.
    /// Sent to every connection that said `hello`.
    TerminalFolderChanged {
        /// The session.
        session_id: u64,
        /// The shell's folder, as the shell wrote it (a Windows path; a
        /// WSL shell's Linux folders are `\\wsl.localhost\…` paths).
        folder: String,
    },
    /// How far a measure has come: the totals so far of the path it counts
    /// now. At most 30 per second per measure, and none for a measure that
    /// ends sooner. Sent on the connection that asked.
    MeasureProgress {
        /// The measure, from `measure_started`.
        measure_id: u64,
        /// The path it counts now, as the request gave it.
        path: String,
        /// Files so far.
        files: u64,
        /// Folders so far.
        folders: u64,
        /// Bytes so far.
        bytes: u64,
    },
    /// A measure ended. Sent on the connection that asked.
    MeasureFinished {
        /// The measure, from `measure_started`.
        measure_id: u64,
        /// One result per path counted to the end, in the request's order;
        /// after a cancel, the paths not finished are left out.
        results: Vec<MeasureResult>,
        /// Whether `cancel_measure` stopped it.
        cancelled: bool,
    },
    /// A drive letter appeared or went away: a USB stick, a card in a
    /// reader, a mapped network share. Sent to every connection that said
    /// `hello`, with the list `list_volumes` would answer now.
    VolumesChanged {
        /// Each volume as `volume_info` describes it, by drive letter.
        volumes: Vec<VolumeDetails>,
    },
    /// The theme in effect changed: `ui.theme` names another theme, or the
    /// file of the theme in effect was saved. Sent to every connection that
    /// said `hello`, with the whole theme, so a client applies it without
    /// asking.
    ThemeChanged {
        /// The theme in effect now.
        theme: Box<Theme>,
    },
    /// How far a download for `install_extension` has come. At most 30 per
    /// second, and always one when the download is complete.
    InstallProgress {
        /// The extension.
        extension_id: String,
        /// Bytes downloaded so far.
        bytes: u64,
        /// The download's size, as the index gives it.
        total: u64,
    },
    /// An `install_extension` ended. Sent to every connection that said
    /// `hello`, whichever asked.
    InstallFinished {
        /// The extension.
        extension_id: String,
        /// Whether it is installed now.
        ok: bool,
        /// What happened, for the user: `installed hello 0.1.0`, or why not.
        message: String,
        /// The version installed from the marketplace now that the install
        /// ended: the new one when it worked; when it failed, the one
        /// installed before, if any. Absent when none is installed. Added
        /// within protocol version 11: a core from before it leaves it out.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        installed_version: Option<String>,
    },
    /// A Tool Extension was installed or removed. Sent to every connection
    /// that said `hello`, with the list `list_tools` would answer now.
    ToolsChanged {
        /// Every installed tool.
        tools: Vec<ToolInfo>,
    },
    /// A preview was applied: its rows run as these jobs. Sent to every
    /// connection that said `hello`.
    PreviewApplied {
        /// The preview.
        preview: String,
        /// The jobs, in order.
        jobs: Vec<u64>,
    },
    /// A preview was dropped: cancelled, or not applied within 10 minutes.
    /// Sent to every connection that said `hello`.
    PreviewCancelled {
        /// The preview.
        preview: String,
    },
    /// The updater moved on: a check, a download, a swap or a snooze, the
    /// daily check included. The same shape as `update_state`. Sent to
    /// every connection that said `hello`.
    UpdateStateChanged(Box<UpdateStatus>),
    /// How far an update's download has come: at most 4 a second, and
    /// always one when it is complete.
    UpdateProgress {
        /// The version downloading.
        version: String,
        /// Bytes so far.
        bytes: u64,
        /// The zip's size, as `latest.json` gives it.
        total: u64,
        /// The speed since the download began.
        bytes_per_second: u64,
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
        "job_progress",
        "job_conflict",
        "job_state_changed",
        "plugin_state_changed",
        "plugin_crashed",
        "plugin_event",
        "terminal_exited",
        "terminal_mode_changed",
        "terminal_folder_changed",
        "measure_progress",
        "measure_finished",
        "volumes_changed",
        "theme_changed",
        "install_progress",
        "install_finished",
        "tools_changed",
        "preview_applied",
        "preview_cancelled",
        "update_state_changed",
        "update_progress",
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
            Self::JobProgress(_) => "job_progress",
            Self::JobConflict(_) => "job_conflict",
            Self::JobStateChanged { .. } => "job_state_changed",
            Self::PluginStateChanged { .. } => "plugin_state_changed",
            Self::PluginCrashed { .. } => "plugin_crashed",
            Self::PluginEvent { .. } => "plugin_event",
            Self::TerminalExited { .. } => "terminal_exited",
            Self::TerminalModeChanged { .. } => "terminal_mode_changed",
            Self::TerminalFolderChanged { .. } => "terminal_folder_changed",
            Self::MeasureProgress { .. } => "measure_progress",
            Self::MeasureFinished { .. } => "measure_finished",
            Self::VolumesChanged { .. } => "volumes_changed",
            Self::ThemeChanged { .. } => "theme_changed",
            Self::InstallProgress { .. } => "install_progress",
            Self::InstallFinished { .. } => "install_finished",
            Self::ToolsChanged { .. } => "tools_changed",
            Self::PreviewApplied { .. } => "preview_applied",
            Self::PreviewCancelled { .. } => "preview_cancelled",
            Self::UpdateStateChanged(_) => "update_state_changed",
            Self::UpdateProgress { .. } => "update_progress",
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
    /// Something with that name is already there (a new folder or a rename).
    AlreadyExists,
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
    /// No job has that `job_id`.
    NoSuchJob,
    /// The job has no waiting conflict with that `conflict_id`.
    NoSuchConflict,
    /// The resolution does not fit the conflict, such as
    /// `delete_permanently` for a file that exists.
    InvalidResolution,
    /// No plugin has that ID.
    NoSuchPlugin,
    /// A plugin's command failed: the plugin reported an error, crashed, or
    /// is not running.
    PluginError,
    /// No terminal session has that `session_id` (or its shell has exited,
    /// for a request that needs it running).
    NoSuchSession,
    /// No terminal profile has that name.
    UnknownProfile,
    /// A program could not be started. A terminal's shell: its program is
    /// not on the `PATH`, the folder does not exist, the session limit is
    /// reached, or Windows refused. The editor of `files.editor`: its
    /// program is neither a file nor a program on the `PATH`.
    SpawnFailed,
    /// No valid theme has that ID in the themes folder.
    NoSuchTheme,
    /// The index has no extension with that ID (or not that version), or
    /// none with that ID was installed from the marketplace.
    NoSuchExtension,
    /// The marketplace cannot do it: the index cannot be read or is refused
    /// (plain `http:` without `marketplace.allowInsecure`), a download
    /// failed, the download is not what its kind needs, or something is in
    /// the way.
    MarketplaceError,
    /// The download's SHA-256 is not the one the index gives. The download
    /// was deleted and nothing was installed.
    HashMismatch,
    /// The extension needs a newer CabinetOS (`minCoreVersion`).
    Incompatible,
    /// No window told the core what it shows (`window_state`), or not the
    /// client named.
    NoWindow,
    /// No preview has that ID: it was never made, was applied or
    /// cancelled, or expired after 10 minutes.
    NoSuchPreview,
    /// The client has 20 previews alive already; apply or cancel one first.
    TooManyPreviews,
    /// No secret has that name.
    NoSuchSecret,
    /// The secret's name or value is not one the Credential Manager keeps
    /// (see `secret_set`), or it refused.
    SecretError,
    /// The job cannot be undone: it deleted (to the Recycle Bin or for
    /// good), it was undone already, it still runs, or nothing it did can
    /// be reversed. The message says which, and what to do instead.
    NotUndoable,
    /// The updater cannot do it: this install cannot update itself (a
    /// development build, an all-users install), there is nothing to
    /// download or to apply, no previous version to roll back to, another
    /// update step is running, or the step failed (the message says why).
    UpdateError,
    /// `program.<name>`: no entry of `programs` has that name.
    UnknownProgram,
    /// `program.<name>`: the program cannot start now. Its command is
    /// neither a file nor a program on the `PATH`, a token has nothing to
    /// stand for (no focused entry for `{path}`), or Windows refused.
    ProgramRefused,
    /// `program.<name>`: the program's command line would be longer than
    /// 30,000 characters (many selected paths).
    CommandLineTooLong,
    /// Windows could not build its menu within 3 s (the paths are not in
    /// one folder, one is gone, a shell extension is slow), or could not
    /// run the item; the message says why.
    ShellMenuError,
    /// No menu has that `menu_id` for this connection: an item was chosen
    /// already, it waited longer than 30 s, or a newer one replaced it.
    NoSuchMenu,
    /// `contextMenu.shellMenu` is off: the window shows its own menu.
    ShellMenuOff,
    /// A terminal session cannot be `linked`: its profile says
    /// `"linkable": false` (no prompt hook can be added to its program).
    NotLinkable,
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;
    use crate::job::{ConflictKind, ConflictPolicy, JobKind, JobOptions, LinkPolicy, Rate};
    use crate::market::{Author, Download, MarketCapability, Tile};
    use crate::theme::ThemeKind;

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

    /// A whole theme, every colour the same.
    fn theme() -> Theme {
        let gray = json!("#FFFFFF8B");
        let palette: serde_json::Map<String, Value> = [
            "textPrimary",
            "textSecondary",
            "textTertiary",
            "textDisabled",
            "layerFill",
            "layerStroke",
            "layerStrokeActive",
            "controlFill",
            "controlFillHover",
            "acrylicTint",
            "terminalBackground",
            "folderIcon",
            "folderIconFront",
            "permissionLow",
            "permissionMedium",
            "permissionHigh",
        ]
        .into_iter()
        .map(|key| (key.to_owned(), gray.clone()))
        .chain([(
            "fileTypeColors".to_owned(),
            json!({"md": gray, "rs": gray, "toml": gray, "exe": gray, "dll": gray, "bin": gray, "pdf": gray, "zip": gray}),
        )])
        .collect();
        serde_json::from_value(json!({
            "id": "nord",
            "name": "Nord",
            "author": "CabinetOS",
            "attribution": "Colours from Nord, MIT License.",
            "version": "1.0.0",
            "kind": "dark",
            "accent": "#88c0d0",
            "mica": {"tint": "#2E3440", "opacity": 0.88},
            "palette": palette,
            "terminal": {"foreground": gray, "background": "#2E3440", "cursor": gray, "ansi": vec![gray; 16]}
        }))
        .unwrap()
    }

    fn market_item() -> MarketItem {
        MarketItem {
            id: "hello".to_owned(),
            kind: ExtensionKind::Plugin,
            name: "Hello".to_owned(),
            author: Author {
                name: "CabinetOS".to_owned(),
                verified: false,
                url: None,
            },
            version: "0.1.0".to_owned(),
            description: "Says hello.".to_owned(),
            long: "The sample Core Plugin.".to_owned(),
            rating: None,
            installs: Some(12),
            size: 73_347,
            download: Download {
                url: "files/hello-0.1.0.zip".to_owned(),
                sha256: "ab".repeat(32),
            },
            manifest: json!({"id": "hello", "name": "Hello"}),
            capabilities: vec![MarketCapability {
                name: "cmd:register".to_owned(),
                reason: "Adds the Say Hello command.".to_owned(),
                roots: Vec::new(),
                hosts: Vec::new(),
                secrets: Vec::new(),
                requests: Vec::new(),
                level: Some(crate::plugin::CapabilityLevel::Low),
            }],
            min_core_version: "0.1.0".to_owned(),
            license: "MIT".to_owned(),
            installed_version: Some("0.1.0".to_owned()),
            appearance: None,
            density: None,
            tile: None,
        }
    }

    fn theme_item() -> MarketItem {
        MarketItem {
            id: "nord".to_owned(),
            kind: ExtensionKind::Theme,
            name: "Nord".to_owned(),
            appearance: Some(ThemeKind::Dark),
            density: Some(false),
            tile: Some(Tile {
                background: "#353B49".to_owned(),
                text: "#ECEFF4".to_owned(),
                accent: "#88C0D0".to_owned(),
            }),
            capabilities: Vec::new(),
            installed_version: None,
            ..market_item()
        }
    }

    fn tool() -> ToolInfo {
        ToolInfo {
            id: "md-preview".to_owned(),
            name: "Markdown Preview".to_owned(),
            version: "1.0.0".to_owned(),
            author: "CabinetOS".to_owned(),
            description: "Shows Markdown.".to_owned(),
            dir: r"C:\Users\me\AppData\Local\CabinetOS\tools\md-preview".to_owned(),
        }
    }

    #[expect(clippy::too_many_lines, reason = "one example of every request")]
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
            Request::DescribeEntries {
                listing_id: 7,
                from: 0,
                count: 128,
            },
            Request::MatchEntries {
                listing_id: 7,
                patterns: "*.txt;*.md|readme*".to_owned(),
                files_only: true,
                first_from: Some(12),
            },
            Request::GetIcon {
                key: "ext:.txt".to_owned(),
                size: 32,
            },
            Request::VolumeInfo {
                path: r"C:\".to_owned(),
            },
            Request::ListVolumes,
            Request::OpenPath {
                path: r"C:\Users\me\notes.txt".to_owned(),
            },
            Request::EditPath {
                path: r"C:\Users\me\build.cmd".to_owned(),
            },
            Request::ShowProperties {
                paths: vec![
                    r"C:\Users\me\notes.txt".to_owned(),
                    r"C:\Users\me\photos".to_owned(),
                ],
            },
            Request::CreateDirectory {
                path: r"C:\Users\me\New folder".to_owned(),
            },
            Request::CreateFile {
                path: r"C:\Users\me\New Text Document.txt".to_owned(),
            },
            Request::Rename {
                path: r"C:\Users\me\notes.txt".to_owned(),
                new_name: "notes 2026.txt".to_owned(),
            },
            Request::MeasurePaths {
                paths: vec![r"C:\Users\me\photos".to_owned()],
            },
            Request::CancelMeasure { measure_id: 4 },
            Request::GetConfig,
            Request::GetValue {
                path: "ui.dualPane".to_owned(),
            },
            Request::SetValue {
                path: "ui.pinned".to_owned(),
                value: json!([r"D:\work"]),
            },
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
            Request::StartJob(JobRequest {
                kind: JobKind::Copy,
                sources: vec![r"C:\a".to_owned(), r"C:\b.txt".to_owned()],
                destination: Some(r"D:\target".to_owned()),
                options: JobOptions {
                    on_conflict: ConflictPolicy::OverwriteIfNewer,
                    copy_links: LinkPolicy::FollowTarget,
                    verify: true,
                    preserve_timestamps: false,
                },
            }),
            Request::ListJobs,
            Request::JobControl {
                job_id: 3,
                action: JobAction::Pause,
            },
            Request::ResolveConflict {
                job_id: 3,
                conflict_id: 9,
                resolution: Resolution::Rename {
                    new_name: Some("b (copy).txt".to_owned()),
                },
                apply_to_same_kind: true,
            },
            Request::ListPlugins,
            Request::ReloadPlugin {
                plugin_id: "hello".to_owned(),
            },
            Request::SetPluginEnabled {
                plugin_id: "hello".to_owned(),
                enabled: false,
            },
            Request::GrantCapabilities {
                plugin_id: "reader".to_owned(),
                capabilities: vec!["fs:read".to_owned()],
            },
            Request::Search {
                query: "cat".to_owned(),
                limit: 50,
                root: Some(r"C:\Users".to_owned()),
            },
            Request::IndexStatus,
            Request::TerminalOpen {
                profile: Some("pwsh".to_owned()),
                cwd: Some(r"E:\work".to_owned()),
                cols: 120,
                rows: 30,
                pane: Pane::Right,
                mode: Some(TerminalMode::Linked),
            },
            Request::TerminalResize {
                session_id: 3,
                cols: 80,
                rows: 24,
            },
            Request::TerminalClose { session_id: 3 },
            Request::TerminalTypePaths {
                session_id: 3,
                paths: vec![r"D:\docs\a b.txt".to_owned(), r"D:\docs\c.md".to_owned()],
            },
            Request::TerminalSetMode {
                session_id: 3,
                mode: TerminalMode::Linked,
            },
            Request::TerminalList,
            Request::TerminalPaneFolder { session_id: 3 },
            Request::ListThemes,
            Request::GetTheme {
                theme_id: Some("nord".to_owned()),
            },
            Request::ListTools,
            Request::MarketplaceRefresh {
                catalogue: Some(Catalogue::Themes),
            },
            Request::MarketplaceSearch {
                query: "nord".to_owned(),
                kind: Some(ExtensionKind::Theme),
                catalogue: Some(Catalogue::Themes),
            },
            Request::PreviewTheme {
                extension_id: "nord".to_owned(),
            },
            Request::InstallExtension {
                extension_id: "hello".to_owned(),
                version: Some("0.1.0".to_owned()),
            },
            Request::UninstallExtension {
                extension_id: "hello".to_owned(),
            },
            Request::WindowState(window_state()),
            Request::GetWindowState {
                client: Some("CabinetOS#2".to_owned()),
            },
            Request::GuiContext,
            Request::PreviewListing {
                title: "Rename 2 photos".to_owned(),
                rows: vec![
                    PreviewRow {
                        path: r"C:\Users\me\IMG_1.jpg".to_owned(),
                        kind: crate::ChangeKind::Rename,
                        to: Some("2026-09-30 beach.jpg".to_owned()),
                    },
                    PreviewRow {
                        path: r"C:\Users\me\Beach\".to_owned(),
                        kind: crate::ChangeKind::Create,
                        to: None,
                    },
                ],
            },
            Request::OpenPreview {
                preview: "preview-3".to_owned(),
            },
            Request::PreviewApply {
                preview: "preview-3".to_owned(),
            },
            Request::PreviewCancel {
                preview: "preview-3".to_owned(),
            },
            Request::SecretSet {
                name: "anthropic".to_owned(),
                value: SecretText("sk-ant-example".to_owned()),
            },
            Request::SecretGet {
                name: "anthropic".to_owned(),
            },
            Request::SecretDelete {
                name: "anthropic".to_owned(),
            },
            Request::SecretList,
            Request::SaveLogBundle { minutes: 5 },
            Request::UndoJob { job: Some(12) },
            Request::WorkspaceInfo {
                path: r"C:\repo\src".to_owned(),
            },
            Request::UpdateStatus,
            Request::UpdateCheck,
            Request::UpdateDownload,
            Request::UpdateApply,
            Request::UpdateRollback,
            Request::UpdateSnooze,
            Request::ShellMenu {
                paths: vec![r"C:\data\a.txt".to_owned(), r"C:\data\b.txt".to_owned()],
            },
            Request::ShellMenuInvoke {
                menu_id: 3,
                item_id: 19,
            },
        ]
    }

    fn update_status() -> UpdateStatus {
        UpdateStatus {
            state: crate::UpdatePhase::Downloaded,
            reason: None,
            message: None,
            current: "0.1.0".to_owned(),
            channel: crate::UpdateChannel::Stable,
            latest: Some(crate::UpdateRelease {
                schema_version: 1,
                channel: crate::UpdateChannel::Stable,
                version: "0.2.0".to_owned(),
                published: "2026-10-01".to_owned(),
                zip: crate::UpdateZip {
                    url: "https://github.com/OliverD25/cabinetos/releases/download/v0.2.0/CabinetOS-0.2.0-win-x64.zip".to_owned(),
                    sha256: "ab".repeat(32),
                    size: 80_000_000,
                },
                notes: crate::UpdateNotes {
                    url: "https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.2.0.md".to_owned(),
                },
                requires: crate::UpdateRequires {
                    windows_app_runtime: Some("2.5".to_owned()),
                    dotnet: Some("10.0".to_owned()),
                },
            }),
            notes_url: Some("https://oliverd25.github.io/cabinetos-marketplace/update/stable/notes-0.2.0.md".to_owned()),
            notes: Some("## [0.2.0] - 2026-10-01\n\n- In-app updates.\n".to_owned()),
            checked_at_ms: Some(1_790_000_000_000),
            snoozed_until_ms: None,
            previous: Some("0.0.9".to_owned()),
            installed: None,
            install_dir: Some(r"C:\Users\me\AppData\Local\Programs\CabinetOS".to_owned()),
        }
    }

    fn window_state() -> WindowState {
        use crate::window::{Pane, PaneState, WindowPanes, WindowTab};
        WindowState {
            active_pane: Pane::Right,
            panes: WindowPanes {
                left: PaneState {
                    tabs: vec![WindowTab {
                        path: r"C:\Users\me".to_owned(),
                        locked: false,
                        tool: None,
                    }],
                    active: 0,
                    cursor: Some(r"C:\Users\me\notes.txt".to_owned()),
                    marked: Vec::new(),
                    marked_total: None,
                },
                right: PaneState {
                    tabs: vec![
                        WindowTab {
                            path: r"D:\work".to_owned(),
                            locked: true,
                            tool: None,
                        },
                        WindowTab {
                            path: r"D:\work\README.md".to_owned(),
                            locked: false,
                            tool: Some("md-preview".to_owned()),
                        },
                    ],
                    active: 1,
                    cursor: None,
                    marked: vec![r"D:\work\a.txt".to_owned()],
                    marked_total: Some(1500),
                },
            },
            dual: true,
        }
    }

    fn progress() -> JobProgress {
        JobProgress {
            job_id: 3,
            state: JobState::Running,
            bytes_done: 1_200_000_000,
            bytes_total: 2_700_000_000,
            files_done: 8412,
            files_total: 10_001,
            files_skipped: 2,
            files_failed: 0,
            conflicts_open: 1,
            current_path: Some(r"C:\a\big.bin".to_owned()),
            speed_bps: 610_000_000,
            items_per_second: Rate::new(412.5),
            eta_seconds: Some(3),
            elapsed_ms: 2400,
        }
    }

    fn volume() -> VolumeDetails {
        VolumeDetails {
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
        }
    }

    #[expect(clippy::too_many_lines, reason = "one example of every response")]
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
            Response::EntryDetails {
                listing_id: 7,
                generation: 2,
                from: 0,
                details: vec![
                    EntryDetail {
                        type_name: "File folder".to_owned(),
                        icon_key: "folder".to_owned(),
                    },
                    EntryDetail {
                        type_name: "Text Document".to_owned(),
                        icon_key: "ext:.txt".to_owned(),
                    },
                ],
            },
            Response::EntryMatches {
                listing_id: 7,
                generation: 2,
                ranges: vec![[0, 5], [10, 2]],
            },
            Response::Icon {
                key: "ext:.txt".to_owned(),
                size: 32,
                png_base64: "iVBORw0KGgo=".to_owned(),
            },
            Response::MeasureStarted { measure_id: 4 },
            Response::VolumeInfo(volume()),
            Response::Volumes {
                volumes: vec![
                    volume(),
                    VolumeDetails {
                        drive_letter: Some('Z'),
                        volume_guid_path: String::new(),
                        filesystem: "NTFS".to_owned(),
                        label: "share".to_owned(),
                        disk: None,
                        ..volume()
                    },
                ],
            },
            Response::Config {
                path: r"C:\Users\me\AppData\Roaming\CabinetOS\cabinetos.json".to_owned(),
                config: json!({"version": 1, "ui": {"layout": "classic"}}),
            },
            Response::Value {
                value: json!(false),
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
                    input: None,
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
            Response::JobStarted { job_id: 3 },
            Response::Plugins {
                plugins: vec![PluginInfo {
                    id: "reader".to_owned(),
                    name: "Reader".to_owned(),
                    version: "0.1.0".to_owned(),
                    author: "CabinetOS tests".to_owned(),
                    description: "Reads.".to_owned(),
                    state: PluginState::NeedsReview {
                        missing: vec!["fs:read".to_owned()],
                    },
                    capabilities: vec![crate::plugin::CapabilityInfo {
                        name: "fs:read".to_owned(),
                        level: crate::plugin::CapabilityLevel::Medium,
                        granted: false,
                        reason: "Reads files.".to_owned(),
                        roots: vec![r"C:\\data".to_owned()],
                        hosts: Vec::new(),
                        secrets: Vec::new(),
                        requests: Vec::new(),
                    }],
                    commands: Vec::new(),
                }],
            },
            Response::Jobs {
                jobs: vec![JobInfo {
                    kind: JobKind::Move,
                    sources: vec![r"C:\a".to_owned()],
                    destination: Some(r"D:\target".to_owned()),
                    progress: progress(),
                }],
            },
            Response::FileSearchResults {
                hits: vec![crate::FileHit {
                    path: r"C:\Users\me\cat.jpg".to_owned(),
                    kind: crate::HitKind::File,
                    frn: None,
                }],
                source: SearchSource::Walk,
                took_us: 1500,
                complete: false,
            },
            Response::IndexStatus {
                available: true,
                volumes: vec![VolumeStatus {
                    letter: 'C',
                    state: crate::IndexState::Ready,
                    entries: 1_234_567,
                    built_in_ms: Some(2900),
                    journal_lag: Some(0),
                }],
            },
            Response::TerminalOpened {
                session_id: 3,
                pipe: r"\\.\pipe\cabinetos-term-0123456789abcdef".to_owned(),
                pid: 4242,
                mode: TerminalMode::Locked,
                linkable: true,
            },
            Response::TerminalSessions {
                sessions: vec![TerminalSession {
                    session_id: 3,
                    profile: "cmd".to_owned(),
                    cwd: r"C:\Users\me".to_owned(),
                    cols: 80,
                    rows: 24,
                    pid: 4242,
                    state: crate::TerminalState::Running,
                    pipe: r"\\.\pipe\cabinetos-term-0123456789abcdef".to_owned(),
                    attached: true,
                    pane: Pane::Left,
                    mode: TerminalMode::Locked,
                    linkable: false,
                    folder: Some(r"C:\Users\me\docs".to_owned()),
                }],
            },
            Response::TerminalPaneFolder {
                session_id: 3,
                pane: Pane::Right,
                mode: TerminalMode::Linked,
                folder: Some(r"D:\work".to_owned()),
            },
            Response::Themes {
                themes: vec![ThemeInfo::from(&theme())],
            },
            Response::Theme {
                theme: Box::new(theme()),
            },
            Response::Tools {
                tools: vec![tool()],
            },
            Response::MarketplaceIndex {
                items: vec![market_item(), theme_item()],
                source: r"C:\market\index.json".to_owned(),
                fetched_at_ms: 1_790_000_000_000,
            },
            Response::WindowState {
                client: "CabinetOS#2".to_owned(),
                sent_at_ms: 1_790_000_000_000,
                state: window_state(),
            },
            Response::GuiContext {
                active: Pane::Right,
                left: Some(r"C:\Users\me".to_owned()),
                right: None,
                selection: vec![r"D:\work\a.txt".to_owned()],
                selection_total: 1500,
                cursor: None,
                dual: false,
            },
            Response::PreviewOpened {
                preview: "preview-3".to_owned(),
                title: "Rename 2 photos".to_owned(),
                listing: OpenedListing {
                    listing_id: 9,
                    section_handle: 0x1A8,
                    section_size: 512,
                    entry_count: 2,
                    generation: 1,
                    elapsed_us: 80,
                },
            },
            Response::JobsStarted { jobs: vec![4, 5] },
            Response::Secret {
                value: SecretText("sk-ant-example".to_owned()),
            },
            Response::SecretNames {
                names: vec!["anthropic".to_owned(), "openai".to_owned()],
            },
            Response::LogBundle {
                path: r"C:\Users\me\AppData\Local\CabinetOS\logs\bundle-20260930T010203004Z.zip"
                    .to_owned(),
            },
            Response::UndoStarted {
                job_id: 13,
                undoes: 12,
                left: vec![UndoLeft {
                    path: r"C:\Users\me\old.txt".to_owned(),
                    reason: crate::job::UndoLeftReason::InRecycleBin,
                }],
            },
            Response::WorkspaceInfo {
                root: r"C:\repo".to_owned(),
                branch: Some("main".to_owned()),
            },
            Response::UpdateState(Box::new(update_status())),
            Response::ShellMenu {
                menu_id: 3,
                items: vec![
                    ShellMenuItem {
                        id: 19,
                        text: "Open".to_owned(),
                        separator: false,
                        items: Vec::new(),
                    },
                    ShellMenuItem {
                        id: 0,
                        text: String::new(),
                        separator: true,
                        items: Vec::new(),
                    },
                    ShellMenuItem {
                        id: 0,
                        text: "Send to".to_owned(),
                        separator: false,
                        items: vec![ShellMenuItem {
                            id: 40,
                            text: "Desktop (create shortcut)".to_owned(),
                            separator: false,
                            items: Vec::new(),
                        }],
                    },
                ],
            },
        ]
    }

    #[test]
    fn a_shell_menu_leaves_out_empty_submenus_on_the_wire() {
        let item = ShellMenuItem {
            id: 19,
            text: "Open".to_owned(),
            separator: false,
            items: Vec::new(),
        };
        assert_eq!(
            serde_json::to_value(&item).unwrap(),
            json!({"id": 19, "text": "Open", "separator": false})
        );
        let back: ShellMenuItem =
            serde_json::from_value(json!({"id": 19, "text": "Open", "separator": false})).unwrap();
        assert_eq!(back, item);
    }

    #[expect(clippy::too_many_lines, reason = "one example of every event")]
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
            Event::JobProgress(progress()),
            Event::JobConflict(Conflict {
                conflict_id: 9,
                job_id: 3,
                kind: ConflictKind::FileExists {
                    source_size: 10,
                    source_modified: 133_000_000_000_000_000,
                    dest_size: 12,
                    dest_modified: 132_000_000_000_000_000,
                },
                source: r"C:\a\b.txt".to_owned(),
                destination: Some(r"D:\target\a\b.txt".to_owned()),
            }),
            Event::JobStateChanged {
                job_id: 3,
                state: JobState::Failed {
                    message: "the destination disk is gone".to_owned(),
                },
            },
            Event::PluginStateChanged {
                plugin_id: "crashy".to_owned(),
                state: PluginState::Crashed {
                    message: "wasm trap: unreachable".to_owned(),
                    at_ms: 1_790_000_000_000,
                },
            },
            Event::PluginCrashed {
                plugin_id: "crashy".to_owned(),
                message: "wasm trap: unreachable".to_owned(),
            },
            Event::PluginEvent {
                plugin_id: "hello".to_owned(),
                name: "hello.said".to_owned(),
                payload: r#"{"greeting":"hello"}"#.to_owned(),
            },
            Event::TerminalExited {
                session_id: 3,
                exit_code: 0,
            },
            Event::TerminalModeChanged {
                session_id: 3,
                mode: TerminalMode::Linked,
            },
            Event::TerminalFolderChanged {
                session_id: 3,
                folder: r"D:\Звіт 'a b'".to_owned(),
            },
            Event::MeasureProgress {
                measure_id: 4,
                path: r"C:\Users\me\photos".to_owned(),
                files: 1200,
                folders: 31,
                bytes: 4_500_000_000,
            },
            Event::MeasureFinished {
                measure_id: 4,
                results: vec![MeasureResult {
                    path: r"C:\Users\me\photos".to_owned(),
                    files: 5310,
                    folders: 120,
                    bytes: 18_400_000_000,
                    unreadable: 1,
                }],
                cancelled: false,
            },
            Event::VolumesChanged {
                volumes: vec![volume()],
            },
            Event::ThemeChanged {
                theme: Box::new(theme()),
            },
            Event::InstallProgress {
                extension_id: "hello".to_owned(),
                bytes: 4096,
                total: 73_347,
            },
            Event::InstallFinished {
                extension_id: "hello".to_owned(),
                ok: true,
                message: "installed hello 0.1.0".to_owned(),
                installed_version: Some("0.1.0".to_owned()),
            },
            Event::ToolsChanged {
                tools: vec![tool()],
            },
            Event::PreviewApplied {
                preview: "preview-3".to_owned(),
                jobs: vec![4, 5],
            },
            Event::PreviewCancelled {
                preview: "preview-4".to_owned(),
            },
            Event::UpdateStateChanged(Box::new(update_status())),
            Event::UpdateProgress {
                version: "0.2.0".to_owned(),
                bytes: 4_000_000,
                total: 80_000_000,
                bytes_per_second: 2_000_000,
            },
        ]
    }

    #[test]
    fn update_messages_have_the_documented_wire_form() {
        let value = serde_json::to_value(Envelope::new(
            id(),
            Response::UpdateState(Box::new(update_status())),
        ))
        .unwrap();
        assert_eq!(value["type"], "update_state");
        assert_eq!(value["state"], "downloaded");
        assert_eq!(value["current"], "0.1.0");
        assert_eq!(value["channel"], "stable");
        assert_eq!(value["latest"]["schemaVersion"], 1);
        assert_eq!(value["latest"]["zip"]["size"], 80_000_000);
        assert_eq!(value["latest"]["requires"]["windowsAppRuntime"], "2.5");
        assert_eq!(value["previous"], "0.0.9");
        for absent in ["reason", "message", "snoozed_until_ms", "installed"] {
            assert!(value.get(absent).is_none(), "{absent}");
        }
        let bare: Response = serde_json::from_value(json!({
            "type": "update_state", "state": "not_updatable",
            "reason": "a development build", "current": "0.1.0", "channel": "stable"
        }))
        .unwrap();
        let Response::UpdateState(status) = bare else {
            panic!("expected update_state");
        };
        assert_eq!(status.state, crate::UpdatePhase::NotUpdatable);
        assert_eq!(status.latest, None);
        let request = json!({"id": ID, "type": "update_snooze"});
        let envelope: Envelope<Request> = serde_json::from_value(request).unwrap();
        assert_eq!(envelope.body, Request::UpdateSnooze);
        let progress = serde_json::to_value(Event::UpdateProgress {
            version: "0.2.0".to_owned(),
            bytes: 1,
            total: 2,
            bytes_per_second: 3,
        })
        .unwrap();
        assert_eq!(
            progress,
            json!({"type": "update_progress", "version": "0.2.0", "bytes": 1, "total": 2, "bytes_per_second": 3})
        );
    }

    #[test]
    fn terminal_sessions_have_a_pane_and_a_mode_on_the_wire() {
        let open =
            json!({"id": ID, "type": "terminal_open", "cols": 80, "rows": 25, "pane": "right"});
        let envelope: Envelope<Request> = serde_json::from_value(open).unwrap();
        assert_eq!(
            envelope.body,
            Request::TerminalOpen {
                profile: None,
                cwd: None,
                cols: 80,
                rows: 25,
                pane: Pane::Right,
                mode: None,
            }
        );
        // The pane is required: a session always belongs to one.
        let paneless = json!({"id": ID, "type": "terminal_open", "cols": 80, "rows": 25});
        assert!(serde_json::from_value::<Envelope<Request>>(paneless).is_err());
        let wrong =
            json!({"id": ID, "type": "terminal_open", "cols": 80, "rows": 25, "pane": "middle"});
        assert!(serde_json::from_value::<Envelope<Request>>(wrong).is_err());

        let set = serde_json::to_value(Request::TerminalSetMode {
            session_id: 3,
            mode: TerminalMode::Linked,
        })
        .unwrap();
        assert_eq!(
            set,
            json!({"type": "terminal_set_mode", "session_id": 3, "mode": "linked"})
        );
        let changed = serde_json::to_value(Event::TerminalModeChanged {
            session_id: 3,
            mode: TerminalMode::Locked,
        })
        .unwrap();
        assert_eq!(
            changed,
            json!({"type": "terminal_mode_changed", "session_id": 3, "mode": "locked"})
        );
        let opened = serde_json::to_value(Response::TerminalOpened {
            session_id: 3,
            pipe: "p".to_owned(),
            pid: 7,
            mode: TerminalMode::Locked,
            linkable: false,
        })
        .unwrap();
        assert_eq!(
            opened,
            json!({"type": "terminal_opened", "session_id": 3, "pipe": "p", "pid": 7, "mode": "locked", "linkable": false})
        );
        // The folder sync of protocol 15 is gone.
        let sync = json!({"id": ID, "type": "terminal_sync_cwd", "session_id": 3, "path": "C:\\"});
        assert!(serde_json::from_value::<Envelope<Request>>(sync).is_err());
        assert!(!Request::TYPES.contains(&"terminal_sync_cwd"));
    }

    #[test]
    fn the_gui_context_has_the_documented_wire_form() {
        let ask = json!({"id": ID, "type": "gui_context"});
        let envelope: Envelope<Request> = serde_json::from_value(ask).unwrap();
        assert_eq!(envelope.body, Request::GuiContext);
        let answer = serde_json::to_value(Response::GuiContext {
            active: Pane::Left,
            left: Some(r"E:\work".to_owned()),
            right: None,
            selection: vec![r"E:\work\a.txt".to_owned(), r"E:\work\b.txt".to_owned()],
            selection_total: 2,
            cursor: Some(r"E:\work\b.txt".to_owned()),
            dual: true,
        })
        .unwrap();
        // A pane that shows no folder is null, not left out.
        assert_eq!(
            answer,
            json!({
                "type": "gui_context", "active": "left", "left": "E:\\work", "right": null,
                "selection": ["E:\\work\\a.txt", "E:\\work\\b.txt"], "selection_total": 2,
                "cursor": "E:\\work\\b.txt", "dual": true
            })
        );
        let bare = serde_json::to_value(Response::GuiContext {
            active: Pane::Right,
            left: None,
            right: None,
            selection: Vec::new(),
            selection_total: 0,
            cursor: None,
            dual: false,
        })
        .unwrap();
        assert_eq!(bare["cursor"], Value::Null);
        assert_eq!(bare["selection"], json!([]));
        assert_eq!(bare["dual"], json!(false));
    }

    #[test]
    fn a_window_state_says_whether_both_panes_show_and_older_ones_mean_yes() {
        let state = window_state();
        assert!(state.dual, "the sample window shows both panes");
        let wire = serde_json::to_value(&state).unwrap();
        assert_eq!(wire["dual"], json!(true));
        let single = WindowState {
            dual: false,
            ..window_state()
        };
        let back: WindowState =
            serde_json::from_value(serde_json::to_value(&single).unwrap()).unwrap();
        assert!(!back.dual);
        // A window older than protocol 19 sends no `dual`: both panes show.
        let mut older = wire;
        older.as_object_mut().unwrap().remove("dual");
        let read: WindowState = serde_json::from_value(older).unwrap();
        assert!(read.dual);
        assert!(WindowState::default().dual);
    }

    #[test]
    fn the_prompt_hook_s_messages_have_the_documented_wire_form() {
        let ask = json!({"id": ID, "type": "terminal_pane_folder", "session_id": 3});
        let envelope: Envelope<Request> = serde_json::from_value(ask).unwrap();
        assert_eq!(envelope.body, Request::TerminalPaneFolder { session_id: 3 });
        let answer = serde_json::to_value(Response::TerminalPaneFolder {
            session_id: 3,
            pane: Pane::Left,
            mode: TerminalMode::Linked,
            folder: Some(r"E:\work".to_owned()),
        })
        .unwrap();
        assert_eq!(
            answer,
            json!({"type": "terminal_pane_folder", "session_id": 3, "pane": "left", "mode": "linked", "folder": "E:\\work"})
        );
        // No window: the folder is null, not left out.
        let none = serde_json::to_value(Response::TerminalPaneFolder {
            session_id: 3,
            pane: Pane::Right,
            mode: TerminalMode::Locked,
            folder: None,
        })
        .unwrap();
        assert_eq!(none["folder"], Value::Null);
        let changed = serde_json::to_value(Event::TerminalFolderChanged {
            session_id: 3,
            folder: r"E:\a b".to_owned(),
        })
        .unwrap();
        assert_eq!(
            changed,
            json!({"type": "terminal_folder_changed", "session_id": 3, "folder": "E:\\a b"})
        );
    }

    #[test]
    fn steps_jobs_have_the_documented_wire_form() {
        let request = Request::StartJob(JobRequest {
            kind: JobKind::Steps {
                steps: vec![
                    crate::JobStep::Rename {
                        from: r"C:\a.txt".to_owned(),
                        to: r"C:\b.txt".to_owned(),
                    },
                    crate::JobStep::CreateFolder {
                        path: r"C:\new".to_owned(),
                    },
                    crate::JobStep::CreateFile {
                        path: r"C:\new\empty.txt".to_owned(),
                    },
                    crate::JobStep::Recycle {
                        path: r"C:\old".to_owned(),
                    },
                    crate::JobStep::Restore {
                        saved: r"C:\undo\7\0".to_owned(),
                        to: r"C:\b.txt".to_owned(),
                    },
                ],
            },
            sources: Vec::new(),
            destination: None,
            options: JobOptions::default(),
        });
        let value = serde_json::to_value(Envelope::new(id(), request.clone())).unwrap();
        assert_eq!(value["kind"]["type"], "steps");
        assert_eq!(
            value["kind"]["steps"][0],
            json!({"op": "rename", "from": r"C:\a.txt", "to": r"C:\b.txt"})
        );
        assert_eq!(value["kind"]["steps"][4]["op"], "restore");
        let back: Envelope<Request> = serde_json::from_value(value).unwrap();
        assert_eq!(back.body, request);
    }

    #[test]
    fn install_finished_names_the_installed_version_when_there_is_one() {
        let finished = |installed_version: Option<&str>| {
            serde_json::to_value(Event::InstallFinished {
                extension_id: "hello".to_owned(),
                ok: false,
                message: "the download of hello 0.2.0 has the wrong SHA-256".to_owned(),
                installed_version: installed_version.map(str::to_owned),
            })
            .unwrap()
        };
        assert_eq!(finished(Some("0.1.0"))["installed_version"], "0.1.0");
        assert!(finished(None).get("installed_version").is_none());
        // A core from before the field leaves it out; a client reads that
        // as none.
        let older: Event = serde_json::from_value(json!({
            "type": "install_finished",
            "extension_id": "hello",
            "ok": true,
            "message": "installed hello 0.1.0"
        }))
        .unwrap();
        assert!(matches!(
            older,
            Event::InstallFinished {
                installed_version: None,
                ..
            }
        ));
    }

    #[test]
    fn ping_has_the_documented_wire_form() {
        let json = serde_json::to_string(&Envelope::new(id(), Request::Ping)).unwrap();
        assert_eq!(json, format!(r#"{{"id":"{ID}","type":"ping"}}"#));
    }

    #[test]
    fn the_trace_sits_next_to_the_id_and_may_be_left_out() {
        let trace: RequestId = "01J9ZQ4X7K3M5N8P2R6S0T1V4V".parse().unwrap();
        let traced = Envelope::traced(id(), Some(trace.clone()), Request::Ping);
        let json = serde_json::to_string(&traced).unwrap();
        assert_eq!(
            json,
            format!(r#"{{"id":"{ID}","trace":"{trace}","type":"ping"}}"#)
        );
        let parsed: Envelope<Request> = serde_json::from_str(&json).unwrap();
        assert_eq!(parsed, traced);
        assert_eq!(parsed.trace_or_id(), &trace);

        let untraced: Envelope<Request> =
            serde_json::from_value(json!({"id": ID, "type": "ping"})).unwrap();
        assert_eq!(untraced.trace, None);
        assert_eq!(untraced.trace_or_id(), &id());

        let bad = json!({"id": ID, "trace": "nope", "type": "ping"});
        assert!(serde_json::from_value::<Envelope<Request>>(bad).is_err());

        let event = Envelope::traced(id(), Some(trace.clone()), every_event().remove(0));
        let incoming: Envelope<Incoming> =
            serde_json::from_str(&serde_json::to_string(&event).unwrap()).unwrap();
        assert_eq!(incoming.trace, Some(trace));
    }

    #[test]
    fn a_log_bundle_asks_for_ten_minutes_unless_told() {
        let asked: Envelope<Request> =
            serde_json::from_value(json!({"id": ID, "type": "save_log_bundle"})).unwrap();
        assert_eq!(asked.body, Request::SaveLogBundle { minutes: 10 });
        let value = serde_json::to_value(Envelope::new(
            id(),
            Response::LogBundle {
                path: r"C:\logs\bundle-20260930T010203004Z.zip".to_owned(),
            },
        ))
        .unwrap();
        assert_eq!(
            value,
            json!({"id": ID, "type": "log_bundle", "path": r"C:\logs\bundle-20260930T010203004Z.zip"})
        );
    }

    #[test]
    fn workspace_info_always_has_its_branch_field() {
        let asked: Envelope<Request> = serde_json::from_value(
            json!({"id": ID, "type": "workspace_info", "path": r"C:\repo\src"}),
        )
        .unwrap();
        assert_eq!(
            asked.body,
            Request::WorkspaceInfo {
                path: r"C:\repo\src".to_owned()
            }
        );
        // Outside a repository the branch is null, not left out.
        let value = serde_json::to_value(Envelope::new(
            id(),
            Response::WorkspaceInfo {
                root: r"C:\notes".to_owned(),
                branch: None,
            },
        ))
        .unwrap();
        assert_eq!(
            value,
            json!({"id": ID, "type": "workspace_info", "root": r"C:\notes", "branch": null})
        );
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
    fn volumes_hold_what_volume_info_holds() {
        let value = serde_json::to_value(Envelope::new(
            id(),
            Response::Volumes {
                volumes: vec![volume()],
            },
        ))
        .unwrap();
        assert_eq!(value["type"], "volumes");
        let single = serde_json::to_value(volume()).unwrap();
        assert_eq!(value["volumes"][0], single);
        assert_eq!(value["volumes"][0]["drive_letter"], "C");
        assert_eq!(value["volumes"][0]["disk"]["media_type"], "SSD");
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
            (ErrorCode::AlreadyExists, "already_exists"),
            (ErrorCode::NoSuchListing, "no_such_listing"),
            (ErrorCode::Io, "io"),
            (ErrorCode::UnknownCommand, "unknown_command"),
            (ErrorCode::NotImplemented, "not_implemented"),
            (ErrorCode::InvalidKeys, "invalid_keys"),
            (ErrorCode::KeybindingConflict, "keybinding_conflict"),
            (ErrorCode::ImmutableBinding, "immutable_binding"),
            (ErrorCode::ConfigError, "config_error"),
            (ErrorCode::NoSuchJob, "no_such_job"),
            (ErrorCode::NoSuchConflict, "no_such_conflict"),
            (ErrorCode::InvalidResolution, "invalid_resolution"),
            (ErrorCode::NoSuchPlugin, "no_such_plugin"),
            (ErrorCode::PluginError, "plugin_error"),
            (ErrorCode::NoSuchTheme, "no_such_theme"),
            (ErrorCode::NoSuchExtension, "no_such_extension"),
            (ErrorCode::MarketplaceError, "marketplace_error"),
            (ErrorCode::HashMismatch, "hash_mismatch"),
            (ErrorCode::Incompatible, "incompatible"),
            (ErrorCode::NoWindow, "no_window"),
            (ErrorCode::NoSuchPreview, "no_such_preview"),
            (ErrorCode::TooManyPreviews, "too_many_previews"),
            (ErrorCode::NoSuchSecret, "no_such_secret"),
            (ErrorCode::SecretError, "secret_error"),
            (ErrorCode::NotUndoable, "not_undoable"),
            (ErrorCode::UpdateError, "update_error"),
            (ErrorCode::NotLinkable, "not_linkable"),
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
                id: "md".to_owned(),
                name: "Markdown Preview".to_owned(),
            })
            .unwrap(),
            json!({"kind": "plugin", "id": "md", "name": "Markdown Preview"})
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

    #[test]
    fn job_messages_have_the_documented_wire_form() {
        let start = json!({
            "id": ID,
            "type": "start_job",
            "kind": {"type": "copy"},
            "sources": [r"C:\a"],
            "destination": r"D:\b"
        });
        let envelope: Envelope<Request> = serde_json::from_value(start).unwrap();
        assert_eq!(
            envelope.body,
            Request::StartJob(JobRequest {
                kind: JobKind::Copy,
                sources: vec![r"C:\a".to_owned()],
                destination: Some(r"D:\b".to_owned()),
                options: JobOptions::default(),
            })
        );
        let delete =
            json!({"id": ID, "type": "start_job", "kind": {"type": "delete"}, "sources": ["x"]});
        let envelope: Envelope<Request> = serde_json::from_value(delete).unwrap();
        assert!(matches!(
            envelope.body,
            Request::StartJob(JobRequest {
                kind: JobKind::Delete { permanent: false },
                destination: None,
                ..
            })
        ));
        let defaults = JobOptions::default();
        assert_eq!(defaults.on_conflict, ConflictPolicy::Ask);
        assert_eq!(defaults.copy_links, LinkPolicy::AsLink);
        assert!(!defaults.verify && defaults.preserve_timestamps);
        let partial: JobOptions = serde_json::from_value(json!({"verify": true})).unwrap();
        assert!(partial.verify && partial.preserve_timestamps);

        let value =
            serde_json::to_value(Envelope::new(id(), Event::JobProgress(progress()))).unwrap();
        assert_eq!(value["type"], "job_progress");
        assert_eq!(value["state"], json!({"type": "running"}));
        assert_eq!(value["files_total"], 10_001);
        assert_eq!(value["items_per_second"], 412.5);
        let mut first = progress();
        first.items_per_second = None;
        let value = serde_json::to_value(Event::JobProgress(first)).unwrap();
        assert!(value.get("items_per_second").is_none());
        assert!(serde_json::from_value::<Rate>(json!(-1)).is_err());
        let listed = every_response()
            .into_iter()
            .find(|response| matches!(response, Response::Jobs { .. }))
            .unwrap();
        let value = serde_json::to_value(Envelope::new(id(), listed)).unwrap();
        assert_eq!(value["jobs"][0]["kind"], json!({"type": "move"}));
        assert_eq!(value["jobs"][0]["bytes_done"], 1_200_000_000_u64);

        let resolve = json!({
            "id": ID, "type": "resolve_conflict", "job_id": 3, "conflict_id": 9,
            "resolution": {"type": "rename"}
        });
        let envelope: Envelope<Request> = serde_json::from_value(resolve).unwrap();
        assert_eq!(
            envelope.body,
            Request::ResolveConflict {
                job_id: 3,
                conflict_id: 9,
                resolution: Resolution::Rename { new_name: None },
                apply_to_same_kind: false,
            }
        );
        let io = Event::JobConflict(Conflict {
            conflict_id: 10,
            job_id: 3,
            kind: ConflictKind::Io {
                code: 1117,
                message: "The request could not be performed because of an I/O device error."
                    .to_owned(),
            },
            source: r"C:\a\c.txt".to_owned(),
            destination: None,
        });
        let json = serde_json::to_string(&Envelope::new(id(), io.clone())).unwrap();
        assert_eq!(
            serde_json::from_str::<Envelope<Event>>(&json).unwrap().body,
            io
        );
        let delete = Request::StartJob(JobRequest {
            kind: JobKind::Delete { permanent: true },
            sources: vec![r"C:\old".to_owned()],
            destination: None,
            options: JobOptions::default(),
        });
        let value = serde_json::to_value(&delete).unwrap();
        assert_eq!(value["kind"], json!({"type": "delete", "permanent": true}));
        assert!(value.get("destination").is_none());

        let control = serde_json::to_value(Request::JobControl {
            job_id: 3,
            action: JobAction::Resume,
        })
        .unwrap();
        assert_eq!(control["action"], "resume");
    }

    #[test]
    fn theme_messages_have_the_documented_wire_form() {
        let json =
            serde_json::to_string(&Envelope::new(id(), Request::GetTheme { theme_id: None }))
                .unwrap();
        assert_eq!(json, format!(r#"{{"id":"{ID}","type":"get_theme"}}"#));
        let value = serde_json::to_value(Envelope::new(
            id(),
            Event::ThemeChanged {
                theme: Box::new(theme()),
            },
        ))
        .unwrap();
        assert_eq!(value["type"], "theme_changed");
        assert_eq!(value["theme"]["id"], "nord");
        assert_eq!(value["theme"]["accent"], "#88C0D0");
        assert_eq!(
            value["theme"]["mica"],
            json!({"tint": "#2E3440", "opacity": 0.88})
        );
        assert_eq!(
            value["theme"]["palette"]["fileTypeColors"]["rs"],
            "#FFFFFF8B"
        );
        assert_eq!(
            value["theme"]["terminal"]["ansi"].as_array().unwrap().len(),
            16
        );
        assert!(value["theme"].get("$schema").is_none());
        let listed = serde_json::to_value(ThemeInfo::from(&theme())).unwrap();
        assert_eq!(
            listed,
            json!({"id": "nord", "name": "Nord", "author": "CabinetOS", "version": "1.0.0", "kind": "dark", "accent": "#88C0D0", "mica": {"tint": "#2E3440", "opacity": 0.88}, "has_metrics": false})
        );
        let mut plain = theme();
        plain.accent = None;
        plain.mica = None;
        let value = serde_json::to_value(&plain).unwrap();
        assert!(value["accent"].is_null() && value["mica"].is_null());
        assert_eq!(plain.kind, ThemeKind::Dark);
    }

    #[test]
    fn marketplace_messages_have_the_documented_wire_form() {
        let install = json!({"id": ID, "type": "install_extension", "extension_id": "hello"});
        let envelope: Envelope<Request> = serde_json::from_value(install).unwrap();
        assert_eq!(
            envelope.body,
            Request::InstallExtension {
                extension_id: "hello".to_owned(),
                version: None,
            }
        );
        let search = json!({"id": ID, "type": "marketplace_search", "query": ""});
        let envelope: Envelope<Request> = serde_json::from_value(search).unwrap();
        assert_eq!(
            envelope.body,
            Request::MarketplaceSearch {
                query: String::new(),
                kind: None,
                catalogue: None,
            }
        );
        // Protocol 20: both requests may name a catalogue; one that does not
        // is for the extensions, as every older client's requests are.
        let refresh = json!({"id": ID, "type": "marketplace_refresh"});
        let envelope: Envelope<Request> = serde_json::from_value(refresh).unwrap();
        assert_eq!(
            envelope.body,
            Request::MarketplaceRefresh { catalogue: None }
        );
        let themes = json!({"id": ID, "type": "marketplace_refresh", "catalogue": "themes"});
        let envelope: Envelope<Request> = serde_json::from_value(themes).unwrap();
        assert_eq!(
            envelope.body,
            Request::MarketplaceRefresh {
                catalogue: Some(Catalogue::Themes)
            }
        );
        let wire = serde_json::to_value(Envelope::new(
            id(),
            Request::MarketplaceRefresh { catalogue: None },
        ))
        .unwrap();
        assert!(wire.get("catalogue").is_none(), "{wire}");
        let index = every_response()
            .into_iter()
            .find(|response| matches!(response, Response::MarketplaceIndex { .. }))
            .unwrap();
        let value = serde_json::to_value(Envelope::new(id(), index)).unwrap();
        assert_eq!(value["type"], "marketplace_index");
        assert_eq!(value["fetched_at_ms"], 1_790_000_000_000_u64);
        let item = &value["items"][0];
        assert_eq!(item["kind"], "plugin");
        assert_eq!(item["minCoreVersion"], "0.1.0");
        assert_eq!(item["download"]["url"], "files/hello-0.1.0.zip");
        assert_eq!(item["capabilities"][0]["level"], "low");
        assert_eq!(item["installedVersion"], "0.1.0");
        assert!(item.get("rating").is_none());
    }

    #[test]
    fn terminal_states_are_the_last_ones() {
        let terminal = [
            JobState::Completed,
            JobState::CompletedWithErrors,
            JobState::Cancelled,
            JobState::Failed {
                message: String::new(),
            },
        ];
        assert!(terminal.iter().all(JobState::is_terminal));
        let alive = [
            JobState::Queued,
            JobState::Scanning,
            JobState::Running,
            JobState::Paused,
        ];
        assert!(!alive.iter().any(JobState::is_terminal));
        assert_eq!(ConflictKind::DiskFull.tag(), "disk_full");
        assert_eq!(
            ConflictKind::RecycleBinTooSmall { size: 1 }.tag(),
            "recycle_bin_too_small"
        );
        assert_eq!(
            serde_json::to_value(Resolution::DeletePermanently).unwrap(),
            json!({"type": "delete_permanently"})
        );
    }
}
