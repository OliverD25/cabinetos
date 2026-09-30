//! Commands: the registry of every named command, the key grammar with
//! chords, keymap compilation with the Immutable System Tier, and the
//! palette's search.
//!
//! Every action is a command with an ID (`view.toggleDualPane`), a category,
//! a title and default keys. The UI asks the core for the compiled keymap
//! and runs the chord state machine itself; the core stays the source of
//! truth. The user's changes live in `cabinetos.json` (`keybindings`).
//! Grammar and rules: `docs/keybindings.md`.
//!
//! Serves Constitution Article 7 (Absolute Keyboard Control & Command
//! Palette: every action is a named command with a shortcut, chords, an
//! immutable system tier) and Article 4 (Progressive Disclosure: power
//! features are reached through the palette). Brief §7.
#![forbid(unsafe_code)]

mod keymap;
mod keys;
mod registry;
mod search;

pub use keymap::{Binding, CHORD_WINDOW_MS, Compiled, Keymap, KeymapError, Override, compile};
pub use keys::{Key, KeyChord, KeyParseError, KeySequence, MAX_CHORDS};
pub use registry::{
    Command, CommandRegistry, DuplicateCommand, IMMUTABLE_TIER, PROGRAM_PREFIX, program_command,
};
pub use search::{rank, search};

/// The palette's view of a command, with the keys bound now.
#[must_use]
pub fn command_info(command: &Command, keymap: &Keymap) -> cabinetos_protocol::CommandInfo {
    cabinetos_protocol::CommandInfo {
        id: command.id.clone(),
        category: command.category.clone(),
        title: command.title.clone(),
        keys: keymap
            .keys_of(&command.id)
            .iter()
            .map(ToString::to_string)
            .collect(),
        default_keys: command
            .default_keys
            .iter()
            .map(ToString::to_string)
            .collect(),
        source: command.source.clone(),
        target: command.target,
        when: command.when.clone(),
        immutable: command.immutable,
        input: command.input.clone(),
    }
}
