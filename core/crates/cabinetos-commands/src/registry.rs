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
const CORE: CommandTarget = CommandTarget::Core;
const FILES: Option<&str> = Some("filesView");

/// The core's commands, in palette order: the design's command list
/// (`docs/design/FileForge.dc.html`, `COMMANDS`) without its plugin commands,
/// then the palette, overlays and About.
const SEED: [Seed; 16] = [
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
    seed(
        "file.copyToOtherPane",
        "File",
        "Copy to Other Pane",
        "f5",
        CORE,
        FILES,
    ),
    seed(
        "file.moveToOtherPane",
        "File",
        "Move to Other Pane",
        "f6",
        CORE,
        FILES,
    ),
    seed("file.newFolder", "File", "New Folder", "f7", CORE, FILES),
    seed("go.toPath", "Go", "Go to Path…", "ctrl+l", UI, None),
    seed(
        "marketplace.browse",
        "Marketplace",
        "Browse Plugins and Themes",
        "ctrl+shift+x",
        UI,
        None,
    ),
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
    seed("help.about", "Help", "About CabinetOS", "", CORE, None),
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
    /// The core's own commands, seeded from the design's command list.
    /// Commands the design attributes to plugins (Markdown preview, hex view,
    /// Git, compression) are not here: plugins register them (Article 10).
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
        assert_eq!(registry.commands().len(), 16);
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
        };
        registry.register(copy).unwrap();
        assert!(registry.get("plugin.hello").is_some());
    }
}
