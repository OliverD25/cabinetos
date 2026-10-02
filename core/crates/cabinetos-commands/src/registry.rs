//! The registry of every command the system knows.

use cabinetos_protocol::{CommandInput, CommandSource, CommandTarget};

use crate::keys::KeySequence;

/// A named action. Buttons, menus, the palette, key bindings and plugins all
/// run commands, so an action behaves the same wherever it is triggered.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Command {
    /// `category.verbObject`, for example `view.toggleDualPane`.
    pub id: String,
    /// The palette group, for example `View`.
    pub category: String,
    /// The human name, for example `Toggle Dual Pane`.
    pub title: String,
    /// The keys it has before the user changes anything.
    pub default_keys: Vec<KeySequence>,
    /// Who provides it.
    pub source: CommandSource,
    /// Who runs it.
    pub target: CommandTarget,
    /// The context of its bindings, for example `filesView`; `None` means
    /// everywhere.
    pub when: Option<String>,
    /// Whether it belongs to the Immutable System Tier (Article 7): its
    /// bindings cannot be changed, and no other command may use its keys.
    pub immutable: bool,
    /// The prompt for a line of text it wants first; plugins' commands only.
    pub input: Option<CommandInput>,
}

/// The commands of the Immutable System Tier: the palette, the way out of
/// any overlay, and the keyboard shortcut editor. With these, nobody can
/// lock themselves out of the rest.
pub const IMMUTABLE_TIER: [&str; 3] = ["palette.show", "overlay.close", "keys.open"];

/// One of the core's own commands, as seeded.
struct Seed {
    id: &'static str,
    category: &'static str,
    title: &'static str,
    /// Default keys, in the order the palette shows them; none for a
    /// command without keys. All of them share the command's `when`.
    keys: &'static [&'static str],
    target: CommandTarget,
    when: Option<&'static str>,
}

const fn seed(
    id: &'static str,
    category: &'static str,
    title: &'static str,
    keys: &'static [&'static str],
    target: CommandTarget,
    when: Option<&'static str>,
) -> Seed {
    Seed {
        id,
        category,
        title,
        keys,
        target,
        when,
    }
}

const UI: CommandTarget = CommandTarget::Ui;
const FILES: Option<&str> = Some("filesView");
const PALETTE: Option<&str> = Some("paletteOpen");
const TERMINAL: Option<&str> = Some("terminalFocus");

/// The core's commands, in palette order: the design's command list
/// (`docs/design/FileForge.dc.html`, `COMMANDS`) without its plugin commands,
/// the shell's own navigation, file, edit and search commands, the window's
/// own commands (the sidebar's pins, the editor tabs, the transfer panel,
/// the plugin list, the terminal tabs), Total Commander's small commands
/// (sub-phase 11a), the tab commands (Phase 12), the shell's top row and
/// panes (Phase 16), the context menu's two (Phase 18), then the palette,
/// overlays, a new window and About, then the two of the file panes'
/// column widths, the folder sizes' toggle and the column view's.
/// Every one of them runs in the UI: the shell starts the file jobs
/// itself (`start_job`), makes a folder with `create_directory`, and shows
/// About with the versions from `welcome`.
const SEED: [Seed; 124] = [
    seed(
        "palette.show",
        "View",
        "Show Command Palette",
        &["ctrl+shift+p"],
        UI,
        None,
    ),
    seed(
        "overlay.close",
        "View",
        "Close Overlay",
        &["escape"],
        UI,
        None,
    ),
    seed(
        "keys.open",
        "Preferences",
        "Open Keyboard Shortcuts",
        &["ctrl+k ctrl+s"],
        UI,
        None,
    ),
    // F2 twice: here in the palette, and as `file.rename` in a file pane.
    seed(
        "keys.rebind",
        "Preferences",
        "Change Keys of Selected Command",
        &["f2"],
        UI,
        PALETTE,
    ),
    seed(
        "view.toggleDualPane",
        "View",
        "Toggle Dual Pane",
        &["ctrl+shift+d"],
        UI,
        None,
    ),
    seed(
        "view.toggleTerminal",
        "View",
        "Toggle Integrated Terminal",
        &["ctrl+backquote"],
        UI,
        None,
    ),
    seed(
        "view.focusOtherPane",
        "View",
        "Focus Other Pane",
        &["tab"],
        UI,
        FILES,
    ),
    seed(
        "view.toggleSidebar",
        "View",
        "Toggle Sidebar",
        &["ctrl+b"],
        UI,
        None,
    ),
    // A small always-on-top drawer (`ui.compactOverlay` is its size): Files'
    // key for it.
    seed(
        "view.toggleCompactOverlay",
        "View",
        "Toggle Compact Overlay",
        &["ctrl+alt+up"],
        UI,
        None,
    ),
    // `ui.layout` three ways (Phase 23, the settings-three-ways skill): one
    // command per value, and one that goes to the next. The window marks the
    // current layout in the palette row and checks it in the top row's menu.
    seed(
        "view.layoutClassic",
        "View",
        "Classic Layout",
        &[],
        UI,
        None,
    ),
    seed(
        "view.layoutRight",
        "View",
        "Terminal on the Right",
        &[],
        UI,
        None,
    ),
    seed("view.layoutRail", "View", "Activity Rail", &[], UI, None),
    seed(
        "view.cycleLayout",
        "View",
        "Next Layout",
        &["ctrl+k ctrl+l"],
        UI,
        None,
    ),
    seed(
        "view.showExplorer",
        "View",
        "Show Explorer",
        &["ctrl+shift+e"],
        UI,
        None,
    ),
    seed(
        "view.showSearch",
        "View",
        "Show Search",
        &["ctrl+shift+f"],
        UI,
        None,
    ),
    seed(
        "sidebar.locate",
        "Sidebar",
        "Locate Active Folder",
        &["shift+alt+l"],
        UI,
        None,
    ),
    seed("sidebar.lock", "Sidebar", "Lock Folder Tree", &[], UI, None),
    // `ui.sidebarAutoReveal` from the palette (Phase 23): the Explorer view
    // follows the active pane's folder, or stays where it is.
    seed(
        "sidebar.toggleFollow",
        "Sidebar",
        "Follow the Active Pane",
        &[],
        UI,
        None,
    ),
    seed("sidebar.pin", "Sidebar", "Pin Folder", &[], UI, None),
    seed("sidebar.unpin", "Sidebar", "Unpin Folder", &[], UI, None),
    seed(
        "pane.openSelected",
        "Pane",
        "Open Selected Item",
        &["enter"],
        UI,
        FILES,
    ),
    seed(
        "file.copyToOtherPane",
        "File",
        "Copy to Other Pane",
        &["f5"],
        UI,
        FILES,
    ),
    seed(
        "file.moveToOtherPane",
        "File",
        "Move to Other Pane",
        &["f6"],
        UI,
        FILES,
    ),
    seed("file.newFolder", "File", "New Folder", &["f7"], UI, FILES),
    // Shift+F6: Total Commander's rename in place, next to Explorer's F2.
    seed(
        "file.rename",
        "File",
        "Rename",
        &["f2", "shift+f6"],
        UI,
        FILES,
    ),
    seed(
        "file.delete",
        "File",
        "Delete to Recycle Bin",
        // F8: Total Commander's delete.
        &["delete", "f8"],
        UI,
        FILES,
    ),
    seed(
        "file.deletePermanently",
        "File",
        "Delete Permanently",
        &["shift+delete", "shift+f8"],
        UI,
        FILES,
    ),
    seed(
        "file.openInOtherPane",
        "File",
        "Open in Other Pane",
        &["ctrl+enter"],
        UI,
        FILES,
    ),
    seed(
        "file.properties",
        "File",
        "Properties",
        &["alt+enter"],
        UI,
        FILES,
    ),
    seed("edit.cut", "Edit", "Cut", &["ctrl+x"], UI, FILES),
    seed("edit.copy", "Edit", "Copy", &["ctrl+c"], UI, FILES),
    seed("edit.paste", "Edit", "Paste", &["ctrl+v"], UI, FILES),
    seed(
        "edit.selectAll",
        "Edit",
        "Select All",
        // Ctrl+Num +: Total Commander's "mark all".
        &["ctrl+a", "ctrl+numpadadd"],
        UI,
        FILES,
    ),
    seed(
        "edit.toggleSelection",
        "Edit",
        "Toggle Selection",
        &["insert"],
        UI,
        FILES,
    ),
    seed("go.back", "Go", "Back", &["alt+left"], UI, None),
    seed("go.forward", "Go", "Forward", &["alt+right"], UI, None),
    seed("go.up", "Go", "Up One Level", &["alt+up"], UI, None),
    seed("go.toPath", "Go", "Go to Path…", &["ctrl+l"], UI, None),
    // Phase 16: the find widget of the active pane's tab, which filters its
    // list by name. Alt+F7: Total Commander's search key, kept beside Ctrl+F.
    seed(
        "search.focus",
        "Search",
        "Find in Pane",
        &["ctrl+f", "alt+f7"],
        UI,
        None,
    ),
    seed("search.scope", "Search", "Whole Volume", &[], UI, None),
    // The design's binding: the second key without Ctrl.
    seed(
        "editor.openMarkdownPreview",
        "Editor",
        "Open Markdown Preview",
        &["ctrl+k v"],
        UI,
        FILES,
    ),
    seed("editor.close", "Editor", "Close Editor", &[], UI, None),
    seed("editor.reload", "Editor", "Reload Editor", &[], UI, None),
    seed("transfer.pause", "Transfer", "Pause", &[], UI, None),
    seed("transfer.resume", "Transfer", "Resume", &[], UI, None),
    seed("transfer.cancel", "Transfer", "Cancel", &[], UI, None),
    seed(
        "transfer.minimize",
        "Transfer",
        "Minimize Panel",
        &[],
        UI,
        None,
    ),
    seed("transfer.restore", "Transfer", "Show Panel", &[], UI, None),
    seed("transfer.next", "Transfer", "Show Next Job", &[], UI, None),
    seed("transfer.close", "Transfer", "Close Panel", &[], UI, None),
    seed(
        "conflict.resolve",
        "Transfer",
        "Resolve Conflict",
        &[],
        UI,
        None,
    ),
    // The Extensions page (Core Plugins and Tool Extensions); the themes
    // have their gallery, `themes.browse` (Phase 23, ADR 0022). The ID stays:
    // a command is never renamed.
    seed(
        "marketplace.browse",
        "Marketplace",
        "Browse Extensions",
        &["ctrl+shift+x"],
        UI,
        None,
    ),
    seed("themes.browse", "Themes", "Browse", &[], UI, None),
    seed("plugins.list", "Plugins", "Show Plugins", &[], UI, None),
    seed(
        "workspace.switch",
        "Workspace",
        "Switch Workspace…",
        &["ctrl+k ctrl+w"],
        UI,
        None,
    ),
    seed(
        "preferences.selectColorTheme",
        "Preferences",
        "Color Theme",
        &["ctrl+k ctrl+t"],
        UI,
        None,
    ),
    seed(
        "terminal.runTask",
        "Terminal",
        "Run Task…",
        &["ctrl+shift+b"],
        UI,
        None,
    ),
    // The terminal's tab keys hold only while a terminal has the keyboard
    // (terminal unit 1): Ctrl+Shift+T and Ctrl+Shift+W are no pane's keys,
    // and Alt+[ and Alt+] go by the key's place, so a Ukrainian layout
    // (where those keys are letters) has them too.
    seed(
        "terminal.new",
        "Terminal",
        "New Terminal",
        &["ctrl+shift+t"],
        UI,
        TERMINAL,
    ),
    seed("terminal.show", "Terminal", "Show Terminal", &[], UI, None),
    seed(
        "terminal.close",
        "Terminal",
        "Close Terminal",
        &["ctrl+shift+w"],
        UI,
        TERMINAL,
    ),
    seed(
        "terminal.reload",
        "Terminal",
        "Reload Terminal",
        &[],
        UI,
        None,
    ),
    seed(
        "terminal.previousTab",
        "Terminal",
        "Previous Terminal Tab",
        &["alt+bracketleft"],
        UI,
        TERMINAL,
    ),
    seed(
        "terminal.nextTab",
        "Terminal",
        "Next Terminal Tab",
        &["alt+bracketright"],
        UI,
        TERMINAL,
    ),
    seed(
        "terminal.setMode",
        "Terminal",
        "Lock or Link Terminal to Its Pane",
        &[],
        UI,
        None,
    ),
    // Terminal unit 3, the split mirror: Ctrl+\ splits the dock under the two
    // panes only while a terminal has the keyboard. In a pane the same keys
    // stay `go.root`: the binding with a context is the more specific one.
    seed(
        "terminal.toggleSplit",
        "Terminal",
        "Split Terminal Under the Panes",
        &["ctrl+backslash"],
        UI,
        TERMINAL,
    ),
    // Sub-phase 11a: Total Commander's keys and small commands
    // (docs/research/total-commander.md, Part 3 (b), N1 to N31).
    seed("go.root", "Go", "Up to Root", &["ctrl+backslash"], UI, None),
    seed(
        "go.chooseDriveLeft",
        "Go",
        "Choose Drive for Left Pane…",
        &["alt+f1"],
        UI,
        None,
    ),
    seed(
        "go.chooseDriveRight",
        "Go",
        "Choose Drive for Right Pane…",
        &["alt+f2"],
        UI,
        None,
    ),
    seed(
        "go.showInLeftPane",
        "Go",
        "Show in Left Pane",
        &["ctrl+left"],
        UI,
        FILES,
    ),
    seed(
        "go.showInRightPane",
        "Go",
        "Show in Right Pane",
        &["ctrl+right"],
        UI,
        FILES,
    ),
    seed(
        "go.pinnedFolders",
        "Go",
        "Pinned Folders…",
        &["ctrl+d"],
        UI,
        None,
    ),
    seed(
        "view.swapPanes",
        "View",
        "Swap Panes",
        &["ctrl+u"],
        UI,
        FILES,
    ),
    seed("view.refresh", "View", "Refresh", &["ctrl+r"], UI, FILES),
    seed(
        "view.sortByName",
        "View",
        "Sort by Name",
        &["ctrl+f3"],
        UI,
        FILES,
    ),
    seed(
        "view.sortByExtension",
        "View",
        "Sort by Extension",
        &["ctrl+f4"],
        UI,
        FILES,
    ),
    seed(
        "view.sortByModified",
        "View",
        "Sort by Date Modified",
        &["ctrl+f5"],
        UI,
        FILES,
    ),
    seed(
        "view.sortBySize",
        "View",
        "Sort by Size",
        &["ctrl+f6"],
        UI,
        FILES,
    ),
    seed(
        "edit.toggleSelectionInPlace",
        "Edit",
        "Toggle Selection in Place",
        &["space"],
        UI,
        FILES,
    ),
    seed(
        "edit.selectByPattern",
        "Edit",
        "Select by Pattern…",
        &["numpadadd"],
        UI,
        FILES,
    ),
    seed(
        "edit.unselectByPattern",
        "Edit",
        "Unselect by Pattern…",
        &["numpadsubtract"],
        UI,
        FILES,
    ),
    seed(
        "edit.selectSameExtension",
        "Edit",
        "Select Same Extension",
        &["alt+numpadadd"],
        UI,
        FILES,
    ),
    seed(
        "edit.unselectSameExtension",
        "Edit",
        "Unselect Same Extension",
        &["alt+numpadsubtract"],
        UI,
        FILES,
    ),
    seed(
        "edit.invertSelection",
        "Edit",
        "Invert Selection",
        &["numpadmultiply"],
        UI,
        FILES,
    ),
    seed(
        "edit.unselectAll",
        "Edit",
        "Unselect All",
        &["ctrl+numpadsubtract"],
        UI,
        FILES,
    ),
    seed(
        "edit.restoreSelection",
        "Edit",
        "Restore Selection",
        &["numpaddivide"],
        UI,
        FILES,
    ),
    seed(
        "edit.copyFullPath",
        "Edit",
        "Copy Full Path",
        &["ctrl+shift+c"],
        UI,
        FILES,
    ),
    seed(
        "edit.copyName",
        "Edit",
        "Copy Name",
        &["ctrl+k ctrl+n"],
        UI,
        FILES,
    ),
    seed(
        "edit.copyFolderPath",
        "Edit",
        "Copy Folder Path",
        &["ctrl+k ctrl+p"],
        UI,
        FILES,
    ),
    seed("file.view", "File", "View", &["f3"], UI, FILES),
    seed("file.edit", "File", "Edit", &["f4"], UI, FILES),
    seed(
        "file.newTextFile",
        "File",
        "New Text File",
        &["shift+f4"],
        UI,
        FILES,
    ),
    seed(
        "file.windowsProperties",
        "File",
        "Windows Properties",
        &[],
        UI,
        FILES,
    ),
    seed(
        "file.calculateFolderSize",
        "File",
        "Calculate Folder Size",
        &[],
        UI,
        FILES,
    ),
    seed(
        "file.calculateAllFolderSizes",
        "File",
        "Calculate All Folder Sizes",
        &["shift+alt+enter"],
        UI,
        FILES,
    ),
    // Ctrl+Alt+P since Phase 16: Ctrl+P is Quick Open's.
    seed(
        "terminal.insertPath",
        "Terminal",
        "Insert Folder Path",
        &["ctrl+alt+p"],
        UI,
        FILES,
    ),
    seed(
        "terminal.insertSelectedPaths",
        "Terminal",
        "Insert Selected Paths",
        &["ctrl+shift+enter"],
        UI,
        FILES,
    ),
    // Phase 12: tabs per pane. The window owns the tab state; these are its
    // commands, in the pane's context like the other pane commands.
    seed("tab.new", "Tab", "New Tab", &["ctrl+t"], UI, FILES),
    seed("tab.close", "Tab", "Close Tab", &["ctrl+w"], UI, FILES),
    seed("tab.next", "Tab", "Next Tab", &["ctrl+tab"], UI, FILES),
    seed(
        "tab.previous",
        "Tab",
        "Previous Tab",
        &["ctrl+shift+tab"],
        UI,
        FILES,
    ),
    seed("tab.toggleLock", "Tab", "Toggle Tab Lock", &[], UI, FILES),
    seed(
        "tab.openFolderInNewTab",
        "Tab",
        "Open Folder in New Tab",
        &["ctrl+up"],
        UI,
        FILES,
    ),
    // One command, two keys: the key says which pane the tab goes to.
    seed(
        "tab.moveToOtherPane",
        "Tab",
        "Move Tab to Other Pane",
        &["ctrl+k ctrl+right", "ctrl+k ctrl+left"],
        UI,
        FILES,
    ),
    // Phase 16: one command, nine keys; the digit says which tab comes to
    // the front. The tab strip's clicks run it with the tab's index.
    seed(
        "tab.select",
        "Tab",
        "Go to Tab",
        &[
            "ctrl+1", "ctrl+2", "ctrl+3", "ctrl+4", "ctrl+5", "ctrl+6", "ctrl+7", "ctrl+8",
            "ctrl+9",
        ],
        UI,
        FILES,
    ),
    // Phase 16: the top row. Quick Open lists the workspace's files in the
    // palette's frame; the menu is the top row's hamburger; Settings opens
    // `cabinetos.json` for editing.
    seed("quickOpen.show", "Go", "Quick Open…", &["ctrl+p"], UI, None),
    seed("menu.show", "View", "Show Menu", &[], UI, None),
    seed(
        "settings.open",
        "Preferences",
        "Open Settings",
        &["ctrl+comma"],
        UI,
        None,
    ),
    // Phase 18: the context menu comes from `contextMenu` in the settings.
    // Windows' own menu of the focused row, when `contextMenu.shellMenu` is
    // on (else the context menu); and the menu's edit mode, which turns the
    // open menu into it (or, with no menu open, edits the focused row's).
    seed(
        "menu.showShell",
        "File",
        "Show Windows Context Menu",
        &["ctrl+shift+f10"],
        UI,
        FILES,
    ),
    seed(
        "menu.edit",
        "Preferences",
        "Edit Context Menu…",
        &[],
        UI,
        None,
    ),
    // contextMenu.shellMenu from the palette (Phase 23): Shift+right-click
    // shows Windows' own menu, or does not.
    seed(
        "menu.toggleShellMenu",
        "Menu",
        "Toggle Windows' Shell Menu",
        &[],
        UI,
        None,
    ),
    // Explorer's key for another window of the same folder.
    seed("window.new", "Window", "New Window", &["ctrl+n"], UI, None),
    seed("help.about", "Help", "About CabinetOS", &[], UI, None),
    // Heavy logging and the log bundle (Phase 15); no keys, the palette is their place.
    seed(
        "diagnostics.toggleHeavy",
        "Diagnostics",
        "Toggle Heavy Logging",
        &[],
        UI,
        None,
    ),
    seed(
        "diagnostics.openLogFolder",
        "Diagnostics",
        "Open Log Folder",
        &[],
        UI,
        None,
    ),
    seed(
        "diagnostics.saveBundle",
        "Diagnostics",
        "Save Log Bundle",
        &[],
        UI,
        None,
    ),
    // In-app updates (Phase 17, ADR 0014); no keys: the menu, the status-bar pill
    // and the palette are their places.
    seed("update.check", "Update", "Check for Updates", &[], UI, None),
    seed("update.apply", "Update", "Restart to Update", &[], UI, None),
    seed(
        "update.rollback",
        "Update",
        "Roll Back to the Previous Version",
        &[],
        UI,
        None,
    ),
    seed(
        "update.showNotes",
        "Update",
        "Show Release Notes",
        &[],
        UI,
        None,
    ),
    // The file panes' column widths (`ui.columns`): the Name heading's
    // double-click fits Modified, Type and Size to their texts; the reset
    // gives the theme's widths back. No keys: the palette is their place.
    seed(
        "view.fitColumns",
        "View",
        "Fit Columns to Content",
        &[],
        UI,
        FILES,
    ),
    seed(
        "view.resetColumns",
        "View",
        "Reset Column Widths",
        &[],
        UI,
        FILES,
    ),
    // panes.folderSizes from the palette (Phase 19b): the window writes the
    // setting through set_value, so the file and the window agree. No key.
    seed(
        "view.toggleFolderSizes",
        "View",
        "Toggle Folder Sizes",
        &[],
        UI,
        None,
    ),
    // panes.showHidden from the palette (Phase 23): hidden and system
    // entries listed or not. Total Commander's Ctrl+H is the file panes'
    // own; here it is the second half of a chord like the other settings'.
    seed(
        "view.toggleHiddenFiles",
        "View",
        "Toggle Hidden Files",
        &["ctrl+k ctrl+h"],
        UI,
        None,
    ),
    // The column view (Phase 19f, ADR 0016): the active pane's front tab
    // shows its folder as columns, or as the list again.
    seed(
        "view.toggleColumns",
        "View",
        "Toggle Column View",
        &["ctrl+alt+c"],
        UI,
        FILES,
    ),
];

/// The command a seed row describes.
fn from_seed(seed: &Seed) -> Command {
    Command {
        id: seed.id.to_owned(),
        category: seed.category.to_owned(),
        title: seed.title.to_owned(),
        default_keys: seed
            .keys
            .iter()
            .map(|keys| keys.parse().expect("the seed keys follow the key grammar"))
            .collect(),
        source: CommandSource::Core,
        target: seed.target,
        when: seed.when.map(str::to_owned),
        immutable: IMMUTABLE_TIER.contains(&seed.id),
        input: None,
    }
}

/// The prefix of the command a `programs` entry becomes: `program.<name>`.
pub const PROGRAM_PREFIX: &str = "program.";

/// The command of a `programs` entry: the core runs it (it starts the
/// program with the paths of the window's state), in the palette's
/// Programs group, bindable in a file pane like the file commands.
#[must_use]
pub fn program_command(name: &str, title: &str) -> Command {
    Command {
        id: format!("{PROGRAM_PREFIX}{name}"),
        category: "Programs".to_owned(),
        title: title.to_owned(),
        default_keys: Vec::new(),
        source: CommandSource::Program {
            name: name.to_owned(),
        },
        target: CommandTarget::Core,
        when: FILES.map(str::to_owned),
        immutable: false,
        input: None,
    }
}

/// Every command, in a stable order.
#[derive(Clone, Debug, Default)]
pub struct CommandRegistry {
    commands: Vec<Command>,
}

/// A command that cannot be registered.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
#[error("a command with ID `{0}` is already registered")]
pub struct DuplicateCommand(pub String);

impl CommandRegistry {
    /// The core's own commands, seeded from the design's command list and
    /// the shell's own commands, so every one of them can be rebound
    /// (Article 7). Commands the design attributes to plugins (hex view,
    /// Git, compression) are not here: plugins register them (Article 10).
    /// The Markdown preview is a Tool Extension the window opens, so its
    /// command, `editor.openMarkdownPreview`, is the window's and is here.
    #[must_use]
    pub fn core() -> Self {
        Self {
            commands: SEED.iter().map(from_seed).collect(),
        }
    }

    /// Adds a command, for example one a plugin provides (Phase 7).
    pub fn register(&mut self, command: Command) -> Result<(), DuplicateCommand> {
        if self.get(&command.id).is_some() {
            return Err(DuplicateCommand(command.id));
        }
        self.commands.push(command);
        Ok(())
    }

    /// Removes the command with this ID.
    pub fn unregister(&mut self, id: &str) -> Option<Command> {
        let index = self.commands.iter().position(|command| command.id == id)?;
        Some(self.commands.remove(index))
    }

    /// Removes every command the plugin registered: it stopped or crashed.
    pub fn unregister_plugin(&mut self, plugin_id: &str) {
        self.commands.retain(
            |command| !matches!(&command.source, CommandSource::Plugin { id, .. } if id == plugin_id),
        );
    }

    /// Replaces the commands of the configuration's `programs` with these,
    /// each `(name, title)` the command `program.<name>` (Phase 18). They
    /// have no default keys, so they never clash with a binding; a key the
    /// user gives one in `keybindings` binds it as any other command. A
    /// program whose ID another command has (a plugin's) is left out, and
    /// its name returned.
    pub fn set_programs<'a>(
        &mut self,
        programs: impl IntoIterator<Item = (&'a str, &'a str)>,
    ) -> Vec<String> {
        self.commands
            .retain(|command| !matches!(command.source, CommandSource::Program { .. }));
        let mut left_out = Vec::new();
        for (name, title) in programs {
            if self.register(program_command(name, title)).is_err() {
                left_out.push(name.to_owned());
            }
        }
        left_out
    }

    /// The command with this ID.
    #[must_use]
    pub fn get(&self, id: &str) -> Option<&Command> {
        self.commands.iter().find(|command| command.id == id)
    }

    /// Every command, in registration order.
    #[must_use]
    pub fn commands(&self) -> &[Command] {
        &self.commands
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn texts(keys: &[KeySequence]) -> Vec<String> {
        keys.iter().map(ToString::to_string).collect()
    }

    #[test]
    fn a_command_may_have_several_default_keys() {
        use crate::keymap::{Override, compile};

        let command = from_seed(&seed(
            "file.delete",
            "File",
            "Delete",
            &["delete", "f8"],
            UI,
            FILES,
        ));
        assert_eq!(texts(&command.default_keys), ["delete", "f8"]);
        let mut registry = CommandRegistry::default();
        registry.register(command).unwrap();

        // Both are bound, in the seed's order, in the command's context, and
        // the keymap the UI receives carries both.
        let wire = compile(&registry, &[]).unwrap().keymap.to_wire();
        let bound: Vec<(&str, Option<&str>)> = wire
            .bindings
            .iter()
            .map(|binding| (binding.keys.as_str(), binding.when.as_deref()))
            .collect();
        assert_eq!(
            bound,
            [("delete", Some("filesView")), ("f8", Some("filesView"))]
        );

        // The pencil (`keys.rebind`) writes one override for the command,
        // which replaces every default key; a reset brings both back.
        let rebound = compile(
            &registry,
            &[Override {
                command: "file.delete".to_owned(),
                keys: Some("ctrl+d".parse().unwrap()),
                when: None,
            }],
        )
        .unwrap();
        let keys: Vec<String> = rebound
            .keymap
            .keys_of("file.delete")
            .iter()
            .map(ToString::to_string)
            .collect();
        assert_eq!(keys, ["ctrl+d"]);
    }

    #[test]
    fn the_immutable_tier_keeps_its_one_key_each() {
        let registry = CommandRegistry::core();
        for (id, key) in [
            ("palette.show", "ctrl+shift+p"),
            ("overlay.close", "escape"),
            ("keys.open", "ctrl+k ctrl+s"),
        ] {
            let command = registry.get(id).unwrap();
            assert!(command.immutable, "{id}");
            assert_eq!(texts(&command.default_keys), [key], "{id}");
        }
        assert_eq!(
            IMMUTABLE_TIER,
            ["palette.show", "overlay.close", "keys.open"]
        );
    }

    /// Phase 19c: the compact overlay runs in the window from anywhere, on
    /// Files' key, and no other command has that key.
    #[test]
    fn the_compact_overlay_command_is_seeded_on_a_key_of_its_own() {
        let registry = CommandRegistry::core();
        let command = registry.get("view.toggleCompactOverlay").unwrap();
        assert_eq!(
            (command.category.as_str(), command.title.as_str()),
            ("View", "Toggle Compact Overlay")
        );
        assert_eq!(command.target, CommandTarget::Ui);
        assert_eq!(command.source, CommandSource::Core);
        assert_eq!(command.when, None);
        assert!(!command.immutable);
        assert_eq!(texts(&command.default_keys), ["ctrl+alt+up"]);
        let sharing: Vec<&str> = registry
            .commands()
            .iter()
            .filter(|other| texts(&other.default_keys).contains(&"ctrl+alt+up".to_owned()))
            .map(|other| other.id.as_str())
            .collect();
        assert_eq!(sharing, ["view.toggleCompactOverlay"]);
    }

    #[test]
    fn folder_sizes_has_a_toggle_for_the_palette_and_no_key() {
        let registry = CommandRegistry::core();
        let toggle = registry.get("view.toggleFolderSizes").unwrap();
        assert_eq!(toggle.target, CommandTarget::Ui);
        assert_eq!(
            (toggle.category.as_str(), toggle.title.as_str()),
            ("View", "Toggle Folder Sizes")
        );
        // A setting's switch works from anywhere, not only over a file pane.
        assert_eq!(toggle.when, None);
        assert!(toggle.default_keys.is_empty());
    }

    /// Phase 19f (ADR 0016): the column view's toggle acts on the active
    /// pane, on Ctrl+Alt+C, which no other command has.
    #[test]
    fn the_column_view_toggle_is_seeded_on_a_key_of_its_own_in_the_files_view() {
        let registry = CommandRegistry::core();
        let command = registry.get("view.toggleColumns").unwrap();
        assert_eq!(
            (command.category.as_str(), command.title.as_str()),
            ("View", "Toggle Column View")
        );
        assert_eq!(command.target, CommandTarget::Ui);
        assert_eq!(command.source, CommandSource::Core);
        assert_eq!(command.when.as_deref(), Some("filesView"));
        assert!(!command.immutable);
        assert_eq!(texts(&command.default_keys), ["ctrl+alt+c"]);
        let sharing: Vec<&str> = registry
            .commands()
            .iter()
            .filter(|other| texts(&other.default_keys).contains(&"ctrl+alt+c".to_owned()))
            .map(|other| other.id.as_str())
            .collect();
        assert_eq!(sharing, ["view.toggleColumns"]);
        // The default keymap compiles with it.
        crate::keymap::compile(&registry, &[]).unwrap();
    }

    #[test]
    fn the_terminal_tab_keys_hold_only_in_the_terminal_and_the_pane_keeps_its_own() {
        let registry = CommandRegistry::core();
        for (id, title, keys) in [
            ("terminal.new", "New Terminal", "ctrl+shift+t"),
            ("terminal.close", "Close Terminal", "ctrl+shift+w"),
            (
                "terminal.previousTab",
                "Previous Terminal Tab",
                "alt+bracketleft",
            ),
            ("terminal.nextTab", "Next Terminal Tab", "alt+bracketright"),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(command.title, title, "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert_eq!(command.when.as_deref(), Some("terminalFocus"), "{id}");
            assert_eq!(texts(&command.default_keys), [keys], "{id}");
        }
        let set_mode = registry.get("terminal.setMode").unwrap();
        assert!(set_mode.default_keys.is_empty());
        assert_eq!(set_mode.when, None);
        // Ctrl+Shift+C stays the pane's Copy Full Path: the terminal's copy
        // is the page's own key, not a command.
        let copy = registry.get("edit.copyFullPath").unwrap();
        assert_eq!(texts(&copy.default_keys), ["ctrl+shift+c"]);
        assert_eq!(copy.when.as_deref(), Some("filesView"));
        // No other command has the four keys, in any context.
        for keys in [
            "ctrl+shift+t",
            "ctrl+shift+w",
            "alt+bracketleft",
            "alt+bracketright",
        ] {
            let owners: Vec<&str> = registry
                .commands()
                .iter()
                .filter(|command| texts(&command.default_keys).contains(&keys.to_owned()))
                .map(|command| command.id.as_str())
                .collect();
            assert_eq!(owners.len(), 1, "{keys}: {owners:?}");
        }
        crate::keymap::compile(&registry, &[]).unwrap();
    }

    #[test]
    fn ctrl_backslash_splits_the_terminal_and_stays_up_to_root_in_a_pane() {
        let registry = CommandRegistry::core();
        let split = registry.get("terminal.toggleSplit").unwrap();
        assert_eq!(
            (split.category.as_str(), split.title.as_str()),
            ("Terminal", "Split Terminal Under the Panes")
        );
        assert_eq!(split.target, CommandTarget::Ui);
        assert_eq!(split.when.as_deref(), Some("terminalFocus"));
        assert!(!split.immutable);
        assert_eq!(texts(&split.default_keys), ["ctrl+backslash"]);
        // The same keys are Up to Root everywhere else: two commands share keys
        // only with different contexts, and the one with a context wins.
        let root = registry.get("go.root").unwrap();
        assert_eq!(texts(&root.default_keys), ["ctrl+backslash"]);
        assert_eq!(root.when, None);
        crate::keymap::compile(&registry, &[]).unwrap();
    }

    #[test]
    fn seeds_the_design_commands_but_not_plugin_ones() {
        let registry = CommandRegistry::core();
        assert_eq!(registry.commands().len(), 124);
        let keys = |id: &str| {
            registry
                .get(id)
                .unwrap()
                .default_keys
                .iter()
                .map(ToString::to_string)
                .collect::<Vec<_>>()
        };
        assert_eq!(keys("view.toggleDualPane"), ["ctrl+shift+d"]);
        assert_eq!(keys("view.toggleTerminal"), ["ctrl+backquote"]);
        assert_eq!(keys("view.toggleCompactOverlay"), ["ctrl+alt+up"]);
        assert_eq!(keys("file.copyToOtherPane"), ["f5"]);
        // PLAN.md conflict E: the command list's chord wins over the
        // sidebar hint "Ctrl+K W".
        assert_eq!(keys("workspace.switch"), ["ctrl+k ctrl+w"]);
        assert!(keys("help.about").is_empty());
        // A second window, Explorer's key; the shell opens it.
        assert_eq!(keys("window.new"), ["ctrl+n"]);
        let new_window = registry.get("window.new").unwrap();
        assert_eq!(new_window.target, CommandTarget::Ui);
        assert_eq!(
            (new_window.category.as_str(), new_window.title.as_str()),
            ("Window", "New Window")
        );
        assert_eq!(keys("editor.openMarkdownPreview"), ["ctrl+k v"]);
        for plugin_command in ["md", "hex", "gitcommit", "gitlog", "compress"] {
            assert!(
                registry
                    .commands()
                    .iter()
                    .all(|command| !command.id.contains(plugin_command)),
                "{plugin_command}"
            );
        }
    }

    /// A seeded command as a test expects it: id, category, title, default
    /// keys and `when`.
    type Expected = (
        &'static str,
        &'static str,
        &'static str,
        &'static [&'static str],
        Option<&'static str>,
    );

    /// Sub-phase 11a (`docs/research/total-commander.md`, Part 3 (b), N1 to
    /// N31): Total Commander's small commands, with the note's titles, keys
    /// and contexts.
    const TOTAL_COMMANDER: [Expected; 31] = [
        ("go.root", "Go", "Up to Root", &["ctrl+backslash"], None),
        (
            "go.chooseDriveLeft",
            "Go",
            "Choose Drive for Left Pane…",
            &["alt+f1"],
            None,
        ),
        (
            "go.chooseDriveRight",
            "Go",
            "Choose Drive for Right Pane…",
            &["alt+f2"],
            None,
        ),
        (
            "go.showInLeftPane",
            "Go",
            "Show in Left Pane",
            &["ctrl+left"],
            Some("filesView"),
        ),
        (
            "go.showInRightPane",
            "Go",
            "Show in Right Pane",
            &["ctrl+right"],
            Some("filesView"),
        ),
        (
            "go.pinnedFolders",
            "Go",
            "Pinned Folders…",
            &["ctrl+d"],
            None,
        ),
        (
            "view.swapPanes",
            "View",
            "Swap Panes",
            &["ctrl+u"],
            Some("filesView"),
        ),
        (
            "view.refresh",
            "View",
            "Refresh",
            &["ctrl+r"],
            Some("filesView"),
        ),
        (
            "view.sortByName",
            "View",
            "Sort by Name",
            &["ctrl+f3"],
            Some("filesView"),
        ),
        (
            "view.sortByExtension",
            "View",
            "Sort by Extension",
            &["ctrl+f4"],
            Some("filesView"),
        ),
        (
            "view.sortByModified",
            "View",
            "Sort by Date Modified",
            &["ctrl+f5"],
            Some("filesView"),
        ),
        (
            "view.sortBySize",
            "View",
            "Sort by Size",
            &["ctrl+f6"],
            Some("filesView"),
        ),
        (
            "edit.toggleSelectionInPlace",
            "Edit",
            "Toggle Selection in Place",
            &["space"],
            Some("filesView"),
        ),
        (
            "edit.selectByPattern",
            "Edit",
            "Select by Pattern…",
            &["numpadadd"],
            Some("filesView"),
        ),
        (
            "edit.unselectByPattern",
            "Edit",
            "Unselect by Pattern…",
            &["numpadsubtract"],
            Some("filesView"),
        ),
        (
            "edit.selectSameExtension",
            "Edit",
            "Select Same Extension",
            &["alt+numpadadd"],
            Some("filesView"),
        ),
        (
            "edit.unselectSameExtension",
            "Edit",
            "Unselect Same Extension",
            &["alt+numpadsubtract"],
            Some("filesView"),
        ),
        (
            "edit.invertSelection",
            "Edit",
            "Invert Selection",
            &["numpadmultiply"],
            Some("filesView"),
        ),
        (
            "edit.unselectAll",
            "Edit",
            "Unselect All",
            &["ctrl+numpadsubtract"],
            Some("filesView"),
        ),
        (
            "edit.restoreSelection",
            "Edit",
            "Restore Selection",
            &["numpaddivide"],
            Some("filesView"),
        ),
        (
            "edit.copyFullPath",
            "Edit",
            "Copy Full Path",
            &["ctrl+shift+c"],
            Some("filesView"),
        ),
        (
            "edit.copyName",
            "Edit",
            "Copy Name",
            &["ctrl+k ctrl+n"],
            Some("filesView"),
        ),
        (
            "edit.copyFolderPath",
            "Edit",
            "Copy Folder Path",
            &["ctrl+k ctrl+p"],
            Some("filesView"),
        ),
        ("file.view", "File", "View", &["f3"], Some("filesView")),
        ("file.edit", "File", "Edit", &["f4"], Some("filesView")),
        (
            "file.newTextFile",
            "File",
            "New Text File",
            &["shift+f4"],
            Some("filesView"),
        ),
        (
            "file.windowsProperties",
            "File",
            "Windows Properties",
            &[],
            Some("filesView"),
        ),
        (
            "file.calculateFolderSize",
            "File",
            "Calculate Folder Size",
            &[],
            Some("filesView"),
        ),
        (
            "file.calculateAllFolderSizes",
            "File",
            "Calculate All Folder Sizes",
            &["shift+alt+enter"],
            Some("filesView"),
        ),
        (
            "terminal.insertPath",
            "Terminal",
            "Insert Folder Path",
            &["ctrl+alt+p"],
            Some("filesView"),
        ),
        (
            "terminal.insertSelectedPaths",
            "Terminal",
            "Insert Selected Paths",
            &["ctrl+shift+enter"],
            Some("filesView"),
        ),
    ];

    /// Every one of them is seeded as the note says, and runs in the window.
    #[test]
    fn total_commander_s_commands_are_seeded_as_the_note_says() {
        let registry = CommandRegistry::core();
        for (id, category, title, keys, when) in TOTAL_COMMANDER {
            let command = registry
                .get(id)
                .unwrap_or_else(|| panic!("{id} is not seeded"));
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                (category, title),
                "{id}"
            );
            assert_eq!(texts(&command.default_keys), keys, "{id}");
            assert_eq!(command.when.as_deref(), when, "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert_eq!(command.source, CommandSource::Core, "{id}");
            assert!(!command.immutable, "{id}");
        }
    }

    #[test]
    fn the_shell_s_commands_run_in_the_ui_with_its_keys() {
        let registry = CommandRegistry::core();
        for (id, keys, when) in [
            ("go.back", &["alt+left"][..], None),
            ("go.forward", &["alt+right"][..], None),
            ("go.up", &["alt+up"][..], None),
            ("pane.openSelected", &["enter"][..], Some("filesView")),
            ("keys.rebind", &["f2"][..], Some("paletteOpen")),
            ("file.rename", &["f2", "shift+f6"][..], Some("filesView")),
            ("file.delete", &["delete", "f8"][..], Some("filesView")),
            (
                "file.deletePermanently",
                &["shift+delete", "shift+f8"][..],
                Some("filesView"),
            ),
            (
                "file.openInOtherPane",
                &["ctrl+enter"][..],
                Some("filesView"),
            ),
            ("file.properties", &["alt+enter"][..], Some("filesView")),
            ("edit.cut", &["ctrl+x"][..], Some("filesView")),
            ("edit.copy", &["ctrl+c"][..], Some("filesView")),
            ("edit.paste", &["ctrl+v"][..], Some("filesView")),
            (
                "edit.selectAll",
                &["ctrl+a", "ctrl+numpadadd"][..],
                Some("filesView"),
            ),
            ("edit.toggleSelection", &["insert"][..], Some("filesView")),
            ("search.focus", &["ctrl+f", "alt+f7"][..], None),
            ("file.copyToOtherPane", &["f5"][..], Some("filesView")),
            ("file.moveToOtherPane", &["f6"][..], Some("filesView")),
            ("file.newFolder", &["f7"][..], Some("filesView")),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            let seeded: Vec<String> = command
                .default_keys
                .iter()
                .map(ToString::to_string)
                .collect();
            assert_eq!(seeded, keys, "{id}");
            assert_eq!(command.when.as_deref(), when, "{id}");
        }
        // The window's own commands, so the palette ranks them and the user
        // can bind them; only the Markdown preview has keys of its own.
        for id in [
            "plugins.list",
            "terminal.show",
            "terminal.reload",
            "terminal.setMode",
            "search.scope",
            "editor.close",
            "editor.reload",
            "transfer.pause",
            "transfer.resume",
            "transfer.cancel",
            "transfer.close",
            "transfer.minimize",
            "transfer.restore",
            "transfer.next",
            "conflict.resolve",
            "sidebar.pin",
            "sidebar.unpin",
            "help.about",
            "view.fitColumns",
            "view.resetColumns",
            "view.toggleFolderSizes",
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert!(command.default_keys.is_empty(), "{id}");
        }
        // The column widths' two act on the file panes.
        for (id, title) in [
            ("view.fitColumns", "Fit Columns to Content"),
            ("view.resetColumns", "Reset Column Widths"),
        ] {
            let command = registry.get(id).unwrap();
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                ("View", title),
                "{id}"
            );
            assert_eq!(command.when.as_deref(), Some("filesView"), "{id}");
        }
        let preview = registry.get("editor.openMarkdownPreview").unwrap();
        assert_eq!(preview.when.as_deref(), Some("filesView"));
        assert_eq!(
            registry
                .get("plugins.list")
                .map(|command| command.title.as_str()),
            Some("Show Plugins")
        );
        // The core runs none of them: the file jobs are started by the
        // shell, and About is the shell's view.
        let core: Vec<&str> = registry
            .commands()
            .iter()
            .filter(|command| command.target == CommandTarget::Core)
            .map(|command| command.id.as_str())
            .collect();
        assert!(core.is_empty(), "{core:?}");
    }

    /// Phase 23: the first four gaps of the settings-three-ways audit each get
    /// their commands (a choice has one per value, a toggle has one), and the
    /// theme gallery its way in. All run in the window; the keys that are
    /// chords are free (no other command has them in any context).
    #[test]
    fn the_three_ways_commands_of_phase_23_are_seeded_with_their_titles_and_keys() {
        let registry = CommandRegistry::core();
        for (id, category, title, keys) in [
            ("view.layoutClassic", "View", "Classic Layout", &[][..]),
            ("view.layoutRight", "View", "Terminal on the Right", &[][..]),
            ("view.layoutRail", "View", "Activity Rail", &[][..]),
            (
                "view.cycleLayout",
                "View",
                "Next Layout",
                &["ctrl+k ctrl+l"][..],
            ),
            (
                "view.toggleHiddenFiles",
                "View",
                "Toggle Hidden Files",
                &["ctrl+k ctrl+h"][..],
            ),
            (
                "sidebar.toggleFollow",
                "Sidebar",
                "Follow the Active Pane",
                &[][..],
            ),
            (
                "menu.toggleShellMenu",
                "Menu",
                "Toggle Windows' Shell Menu",
                &[][..],
            ),
            ("themes.browse", "Themes", "Browse", &[][..]),
            (
                "marketplace.browse",
                "Marketplace",
                "Browse Extensions",
                &["ctrl+shift+x"][..],
            ),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                (category, title),
                "{id}"
            );
            let have: Vec<String> = command
                .default_keys
                .iter()
                .map(ToString::to_string)
                .collect();
            assert_eq!(have, keys, "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert_eq!(command.when, None, "{id}");
        }
        // The layout commands' IDs say the values of `ui.layout`.
        assert!(registry.get("view.layoutClassic").is_some());
        // The chords are the only commands on them, and the keymap compiles.
        for chord in ["ctrl+k ctrl+l", "ctrl+k ctrl+h"] {
            let holders: Vec<&str> = registry
                .commands()
                .iter()
                .filter(|command| {
                    command
                        .default_keys
                        .iter()
                        .any(|keys| keys.to_string() == chord)
                })
                .map(|command| command.id.as_str())
                .collect();
            assert_eq!(holders.len(), 1, "{chord}: {holders:?}");
        }
        crate::keymap::compile(&registry, &[]).unwrap();
    }

    /// Phase 13: the commands of the activity rail and its sidebar. They run
    /// in the window from anywhere, and the keys are the design's (VS Code's).
    #[test]
    fn the_sidebar_commands_are_seeded_with_their_keys() {
        let registry = CommandRegistry::core();
        for (id, category, title, keys) in [
            (
                "view.showExplorer",
                "View",
                "Show Explorer",
                &["ctrl+shift+e"][..],
            ),
            (
                "view.showSearch",
                "View",
                "Show Search",
                &["ctrl+shift+f"][..],
            ),
            (
                "sidebar.locate",
                "Sidebar",
                "Locate Active Folder",
                &["shift+alt+l"][..],
            ),
            ("sidebar.lock", "Sidebar", "Lock Folder Tree", &[][..]),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                (category, title),
                "{id}"
            );
            assert_eq!(texts(&command.default_keys), keys, "{id}");
            assert_eq!(command.when, None, "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert!(!command.immutable, "{id}");
        }
        // The keymap accepts them beside every other key.
        assert!(crate::keymap::compile(&registry, &[]).is_ok());
    }

    /// Phase 12: the tab commands, with the keys the creator chose. Every
    /// one runs in the window and applies in a file pane.
    #[test]
    fn the_tab_commands_are_seeded_with_their_keys() {
        let registry = CommandRegistry::core();
        for (id, title, keys) in [
            ("tab.new", "New Tab", &["ctrl+t"][..]),
            ("tab.close", "Close Tab", &["ctrl+w"][..]),
            ("tab.next", "Next Tab", &["ctrl+tab"][..]),
            ("tab.previous", "Previous Tab", &["ctrl+shift+tab"][..]),
            ("tab.toggleLock", "Toggle Tab Lock", &[][..]),
            (
                "tab.openFolderInNewTab",
                "Open Folder in New Tab",
                &["ctrl+up"][..],
            ),
            (
                "tab.moveToOtherPane",
                "Move Tab to Other Pane",
                &["ctrl+k ctrl+right", "ctrl+k ctrl+left"][..],
            ),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                ("Tab", title),
                "{id}"
            );
            assert_eq!(texts(&command.default_keys), keys, "{id}");
            assert_eq!(command.when.as_deref(), Some("filesView"), "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert!(!command.immutable, "{id}");
        }
        // The keymap accepts them beside every other key: no key is used
        // twice in the pane, and none is the first half of a chord and a
        // key of its own.
        let compiled = crate::keymap::compile(&registry, &[]).unwrap();
        let keys_of = |id: &str| -> Vec<String> {
            compiled
                .keymap
                .keys_of(id)
                .iter()
                .map(ToString::to_string)
                .collect()
        };
        assert_eq!(
            keys_of("tab.moveToOtherPane"),
            ["ctrl+k ctrl+right", "ctrl+k ctrl+left"]
        );
        assert_eq!(keys_of("go.showInRightPane"), ["ctrl+right"]);
    }

    /// Phase 16: the shell redesign's commands (the creator's
    /// `SHELL_REDESIGN.md`, "Keyboard additions"). Quick Open takes Ctrl+P, so
    /// Total Commander's "path to the command line" moves to Ctrl+Alt+P;
    /// Ctrl+F keeps its command, which now opens the pane's find widget.
    #[test]
    fn the_shell_redesign_commands_are_seeded_with_their_keys() {
        let registry = CommandRegistry::core();
        let digits: Vec<String> = (1..=9).map(|n| format!("ctrl+{n}")).collect();
        let digits: Vec<&str> = digits.iter().map(String::as_str).collect();
        for (id, category, title, keys, when) in [
            ("quickOpen.show", "Go", "Quick Open…", &["ctrl+p"][..], None),
            (
                "settings.open",
                "Preferences",
                "Open Settings",
                &["ctrl+comma"][..],
                None,
            ),
            ("menu.show", "View", "Show Menu", &[][..], None),
            (
                "tab.select",
                "Tab",
                "Go to Tab",
                &digits[..],
                Some("filesView"),
            ),
            (
                "search.focus",
                "Search",
                "Find in Pane",
                &["ctrl+f", "alt+f7"][..],
                None,
            ),
            (
                "terminal.insertPath",
                "Terminal",
                "Insert Folder Path",
                &["ctrl+alt+p"][..],
                Some("filesView"),
            ),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(
                (command.category.as_str(), command.title.as_str()),
                (category, title),
                "{id}"
            );
            assert_eq!(texts(&command.default_keys), keys, "{id}");
            assert_eq!(command.when.as_deref(), when, "{id}");
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert_eq!(command.source, CommandSource::Core, "{id}");
            assert!(!command.immutable, "{id}");
        }
        // Back, Forward and Up keep their IDs and keys (Backspace is the
        // pane's own key for Up); the tab keys stay the tab commands'.
        for (id, key) in [
            ("go.back", "alt+left"),
            ("go.forward", "alt+right"),
            ("go.up", "alt+up"),
            ("go.toPath", "ctrl+l"),
            ("tab.new", "ctrl+t"),
            ("tab.close", "ctrl+w"),
            ("tab.next", "ctrl+tab"),
            ("tab.previous", "ctrl+shift+tab"),
        ] {
            assert_eq!(
                texts(&registry.get(id).unwrap().default_keys),
                [key],
                "{id}"
            );
        }
        // The keymap takes them beside every other key: no key is used twice
        // in a context, and the immutable tier keeps its own.
        let compiled = crate::keymap::compile(&registry, &[]).unwrap();
        let keys_of = |id: &str| -> Vec<String> {
            compiled
                .keymap
                .keys_of(id)
                .iter()
                .map(ToString::to_string)
                .collect()
        };
        assert_eq!(keys_of("quickOpen.show"), ["ctrl+p"]);
        assert_eq!(keys_of("terminal.insertPath"), ["ctrl+alt+p"]);
        assert_eq!(keys_of("tab.select").len(), 9);
        assert_eq!(keys_of("palette.show"), ["ctrl+shift+p"]);
    }

    #[test]
    fn ids_are_category_dot_verb_object() {
        for command in CommandRegistry::core().commands() {
            let (prefix, rest) = command.id.split_once('.').unwrap();
            // The category may be camelCase, as `quickOpen` is (Phase 16).
            assert!(
                prefix.chars().next().unwrap().is_ascii_lowercase()
                    && prefix.chars().all(|c| c.is_ascii_alphanumeric()),
                "{}",
                command.id
            );
            assert!(
                rest.chars().next().unwrap().is_ascii_lowercase(),
                "{}",
                command.id
            );
            assert!(
                rest.chars().all(|c| c.is_ascii_alphanumeric()),
                "{}",
                command.id
            );
        }
    }

    #[test]
    fn the_immutable_tier_is_marked() {
        let registry = CommandRegistry::core();
        let immutable: Vec<&str> = registry
            .commands()
            .iter()
            .filter(|command| command.immutable)
            .map(|command| command.id.as_str())
            .collect();
        assert_eq!(immutable, IMMUTABLE_TIER);
    }

    #[test]
    fn the_context_menu_has_two_commands_of_its_own() {
        let registry = CommandRegistry::core();
        let show = registry.get("menu.showShell").unwrap();
        assert_eq!(texts(&show.default_keys), ["ctrl+shift+f10"]);
        assert_eq!(show.when.as_deref(), Some("filesView"));
        assert_eq!(show.target, CommandTarget::Ui);
        let edit = registry.get("menu.edit").unwrap();
        assert!(edit.default_keys.is_empty());
        assert_eq!(
            (edit.category.as_str(), edit.title.as_str()),
            ("Preferences", "Edit Context Menu…")
        );
        // Shift+F10 stays the pane's own key for the context menu.
        let compiled = crate::keymap::compile(&registry, &[]).unwrap();
        assert!(
            compiled
                .keymap
                .to_wire()
                .bindings
                .iter()
                .all(|binding| binding.keys != "shift+f10")
        );
    }

    #[test]
    fn programs_become_commands_that_the_core_runs() {
        let mut registry = CommandRegistry::core();
        let core_count = registry.commands().len();
        let left_out = registry.set_programs([("code", "Open in Code"), ("notes", "notes")]);
        assert!(left_out.is_empty());
        let code = registry.get("program.code").unwrap();
        assert_eq!(
            (
                code.category.as_str(),
                code.title.as_str(),
                code.target,
                code.when.as_deref()
            ),
            (
                "Programs",
                "Open in Code",
                CommandTarget::Core,
                Some("filesView")
            )
        );
        assert_eq!(
            code.source,
            CommandSource::Program {
                name: "code".to_owned()
            }
        );
        assert!(code.default_keys.is_empty() && !code.immutable);
        // A new list replaces the old one; the core's commands stay.
        registry.set_programs([("notes", "Notes")]);
        assert!(registry.get("program.code").is_none());
        assert_eq!(registry.get("program.notes").unwrap().title, "Notes");
        assert_eq!(registry.commands().len(), core_count + 1);
        // A plugin's command with the same ID keeps its place.
        let mut plugin = program_command("taken", "x");
        plugin.source = CommandSource::Plugin {
            id: "p".to_owned(),
            name: "P".to_owned(),
        };
        registry.register(plugin).unwrap();
        assert_eq!(registry.set_programs([("taken", "Taken")]), ["taken"]);
        assert!(matches!(
            registry.get("program.taken").unwrap().source,
            CommandSource::Plugin { .. }
        ));
        registry.set_programs([]);
        assert_eq!(registry.commands().len(), core_count + 1);
        // A key in the settings binds a program as any other command.
        registry.set_programs([("code", "Code")]);
        let compiled = crate::keymap::compile(
            &registry,
            &[crate::keymap::Override {
                command: "program.code".to_owned(),
                keys: Some("ctrl+alt+e".parse().unwrap()),
                when: None,
            }],
        )
        .unwrap();
        let keys: Vec<String> = compiled
            .keymap
            .keys_of("program.code")
            .iter()
            .map(ToString::to_string)
            .collect();
        assert_eq!(keys, ["ctrl+alt+e"]);
    }

    #[test]
    fn a_duplicate_id_is_refused() {
        let mut registry = CommandRegistry::core();
        let mut copy = registry.get("help.about").unwrap().clone();
        assert_eq!(
            registry.register(copy.clone()),
            Err(DuplicateCommand("help.about".to_owned()))
        );
        copy.id = "plugin.hello".to_owned();
        copy.source = CommandSource::Plugin {
            id: "hello".to_owned(),
            name: "Hello".to_owned(),
        };
        registry.register(copy).unwrap();
        assert!(registry.get("plugin.hello").is_some());
    }

    #[test]
    fn a_plugin_s_commands_leave_together() {
        let mut registry = CommandRegistry::core();
        let core_count = registry.commands().len();
        let mut command = registry.get("help.about").unwrap().clone();
        command.source = CommandSource::Plugin {
            id: "hello".to_owned(),
            name: "Hello".to_owned(),
        };
        for id in ["hello.say", "hello.wave"] {
            command.id = id.to_owned();
            registry.register(command.clone()).unwrap();
        }
        assert_eq!(registry.unregister("hello.wave").unwrap().id, "hello.wave");
        assert_eq!(registry.unregister("hello.wave"), None);
        registry.unregister_plugin("other");
        assert_eq!(registry.commands().len(), core_count + 1);
        registry.unregister_plugin("hello");
        assert_eq!(registry.commands().len(), core_count);
    }
}
