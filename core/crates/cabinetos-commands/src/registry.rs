//! The registry of every command the system knows.

use cabinetos_protocol::{CommandSource, CommandTarget};

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
    /// Default keys; empty for none.
    keys: &'static str,
    target: CommandTarget,
    when: Option<&'static str>,
}

const fn seed(
    id: &'static str,
    category: &'static str,
    title: &'static str,
    keys: &'static str,
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

/// The core's commands, in palette order: the design's command list
/// (`docs/design/FileForge.dc.html`, `COMMANDS`) without its plugin commands,
/// the shell's own navigation, file, edit and search commands, the window's
/// own commands (the sidebar's pins, the editor tabs, the transfer panel,
/// the plugin list, the terminal tabs), then the palette, overlays and
/// About. Every one of them runs in the UI: the shell starts the file jobs
/// itself (`start_job`), makes a folder with `create_directory`, and shows
/// About with the versions from `welcome`.
const SEED: [Seed; 51] = [
    seed(
        "palette.show",
        "View",
        "Show Command Palette",
        "ctrl+shift+p",
        UI,
        None,
    ),
    seed("overlay.close", "View", "Close Overlay", "escape", UI, None),
    seed(
        "keys.open",
        "Preferences",
        "Open Keyboard Shortcuts",
        "ctrl+k ctrl+s",
        UI,
        None,
    ),
    // F2 twice: here in the palette, and as `file.rename` in a file pane.
    seed(
        "keys.rebind",
        "Preferences",
        "Change Keys of Selected Command",
        "f2",
        UI,
        PALETTE,
    ),
    seed(
        "view.toggleDualPane",
        "View",
        "Toggle Dual Pane",
        "ctrl+shift+d",
        UI,
        None,
    ),
    seed(
        "view.toggleTerminal",
        "View",
        "Toggle Integrated Terminal",
        "ctrl+backquote",
        UI,
        None,
    ),
    seed(
        "view.focusOtherPane",
        "View",
        "Focus Other Pane",
        "tab",
        UI,
        FILES,
    ),
    seed(
        "view.toggleSidebar",
        "View",
        "Toggle Sidebar",
        "ctrl+b",
        UI,
        None,
    ),
    seed("sidebar.pin", "Sidebar", "Pin Folder", "", UI, None),
    seed("sidebar.unpin", "Sidebar", "Unpin Folder", "", UI, None),
    seed(
        "pane.openSelected",
        "Pane",
        "Open Selected Item",
        "enter",
        UI,
        FILES,
    ),
    seed(
        "file.copyToOtherPane",
        "File",
        "Copy to Other Pane",
        "f5",
        UI,
        FILES,
    ),
    seed(
        "file.moveToOtherPane",
        "File",
        "Move to Other Pane",
        "f6",
        UI,
        FILES,
    ),
    seed("file.newFolder", "File", "New Folder", "f7", UI, FILES),
    seed("file.rename", "File", "Rename", "f2", UI, FILES),
    seed(
        "file.delete",
        "File",
        "Delete to Recycle Bin",
        "delete",
        UI,
        FILES,
    ),
    seed(
        "file.deletePermanently",
        "File",
        "Delete Permanently",
        "shift+delete",
        UI,
        FILES,
    ),
    seed(
        "file.openInOtherPane",
        "File",
        "Open in Other Pane",
        "ctrl+enter",
        UI,
        FILES,
    ),
    seed(
        "file.properties",
        "File",
        "Properties",
        "alt+enter",
        UI,
        FILES,
    ),
    seed("edit.cut", "Edit", "Cut", "ctrl+x", UI, FILES),
    seed("edit.copy", "Edit", "Copy", "ctrl+c", UI, FILES),
    seed("edit.paste", "Edit", "Paste", "ctrl+v", UI, FILES),
    seed("edit.selectAll", "Edit", "Select All", "ctrl+a", UI, FILES),
    seed(
        "edit.toggleSelection",
        "Edit",
        "Toggle Selection",
        "insert",
        UI,
        FILES,
    ),
    seed("go.back", "Go", "Back", "alt+left", UI, None),
    seed("go.forward", "Go", "Forward", "alt+right", UI, None),
    seed("go.up", "Go", "Up One Level", "alt+up", UI, None),
    seed("go.toPath", "Go", "Go to Path…", "ctrl+l", UI, None),
    seed("search.focus", "Search", "Find Files…", "ctrl+f", UI, None),
    seed("search.scope", "Search", "Whole Volume", "", UI, None),
    // The design's binding: the second key without Ctrl.
    seed(
        "editor.openMarkdownPreview",
        "Editor",
        "Open Markdown Preview",
        "ctrl+k v",
        UI,
        FILES,
    ),
    seed("editor.close", "Editor", "Close Editor", "", UI, None),
    seed("editor.reload", "Editor", "Reload Editor", "", UI, None),
    seed("transfer.pause", "Transfer", "Pause", "", UI, None),
    seed("transfer.resume", "Transfer", "Resume", "", UI, None),
    seed("transfer.cancel", "Transfer", "Cancel", "", UI, None),
    seed(
        "transfer.minimize",
        "Transfer",
        "Minimize Panel",
        "",
        UI,
        None,
    ),
    seed("transfer.restore", "Transfer", "Show Panel", "", UI, None),
    seed("transfer.next", "Transfer", "Show Next Job", "", UI, None),
    seed("transfer.close", "Transfer", "Close Panel", "", UI, None),
    seed(
        "conflict.resolve",
        "Transfer",
        "Resolve Conflict",
        "",
        UI,
        None,
    ),
    seed(
        "marketplace.browse",
        "Marketplace",
        "Browse Plugins and Themes",
        "ctrl+shift+x",
        UI,
        None,
    ),
    seed("plugins.list", "Plugins", "Show Plugins", "", UI, None),
    seed(
        "workspace.switch",
        "Workspace",
        "Switch Workspace…",
        "ctrl+k ctrl+w",
        UI,
        None,
    ),
    seed(
        "preferences.selectColorTheme",
        "Preferences",
        "Color Theme",
        "ctrl+k ctrl+t",
        UI,
        None,
    ),
    seed(
        "terminal.runTask",
        "Terminal",
        "Run Task…",
        "ctrl+shift+b",
        UI,
        None,
    ),
    seed("terminal.new", "Terminal", "New Terminal", "", UI, None),
    seed("terminal.show", "Terminal", "Show Terminal", "", UI, None),
    seed("terminal.close", "Terminal", "Close Terminal", "", UI, None),
    seed(
        "terminal.reload",
        "Terminal",
        "Reload Terminal",
        "",
        UI,
        None,
    ),
    seed("help.about", "Help", "About CabinetOS", "", UI, None),
];

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
        let commands = SEED
            .iter()
            .map(|seed| Command {
                id: seed.id.to_owned(),
                category: seed.category.to_owned(),
                title: seed.title.to_owned(),
                default_keys: if seed.keys.is_empty() {
                    Vec::new()
                } else {
                    vec![
                        seed.keys
                            .parse()
                            .expect("the seed keys follow the key grammar"),
                    ]
                },
                source: CommandSource::Core,
                target: seed.target,
                when: seed.when.map(str::to_owned),
                immutable: IMMUTABLE_TIER.contains(&seed.id),
            })
            .collect();
        Self { commands }
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

    #[test]
    fn seeds_the_design_commands_but_not_plugin_ones() {
        let registry = CommandRegistry::core();
        assert_eq!(registry.commands().len(), 51);
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
        assert_eq!(keys("file.copyToOtherPane"), ["f5"]);
        // PLAN.md conflict E: the command list's chord wins over the
        // sidebar hint "Ctrl+K W".
        assert_eq!(keys("workspace.switch"), ["ctrl+k ctrl+w"]);
        assert!(keys("help.about").is_empty());
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

    #[test]
    fn the_shell_s_commands_run_in_the_ui_with_its_keys() {
        let registry = CommandRegistry::core();
        for (id, keys, when) in [
            ("go.back", "alt+left", None),
            ("go.forward", "alt+right", None),
            ("go.up", "alt+up", None),
            ("pane.openSelected", "enter", Some("filesView")),
            ("keys.rebind", "f2", Some("paletteOpen")),
            ("file.rename", "f2", Some("filesView")),
            ("file.delete", "delete", Some("filesView")),
            ("file.deletePermanently", "shift+delete", Some("filesView")),
            ("file.openInOtherPane", "ctrl+enter", Some("filesView")),
            ("file.properties", "alt+enter", Some("filesView")),
            ("edit.cut", "ctrl+x", Some("filesView")),
            ("edit.copy", "ctrl+c", Some("filesView")),
            ("edit.paste", "ctrl+v", Some("filesView")),
            ("edit.selectAll", "ctrl+a", Some("filesView")),
            ("edit.toggleSelection", "insert", Some("filesView")),
            ("search.focus", "ctrl+f", None),
            ("file.copyToOtherPane", "f5", Some("filesView")),
            ("file.moveToOtherPane", "f6", Some("filesView")),
            ("file.newFolder", "f7", Some("filesView")),
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            let seeded: Vec<String> = command
                .default_keys
                .iter()
                .map(ToString::to_string)
                .collect();
            assert_eq!(seeded, [keys], "{id}");
            assert_eq!(command.when.as_deref(), when, "{id}");
        }
        // The window's own commands, so the palette ranks them and the user
        // can bind them; only the Markdown preview has keys of its own.
        for id in [
            "plugins.list",
            "terminal.new",
            "terminal.show",
            "terminal.close",
            "terminal.reload",
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
        ] {
            let command = registry.get(id).unwrap_or_else(|| panic!("{id}"));
            assert_eq!(command.target, CommandTarget::Ui, "{id}");
            assert!(command.default_keys.is_empty(), "{id}");
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

    #[test]
    fn ids_are_category_dot_verb_object() {
        for command in CommandRegistry::core().commands() {
            let (prefix, rest) = command.id.split_once('.').unwrap();
            assert!(
                prefix.chars().all(|c| c.is_ascii_lowercase()),
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
