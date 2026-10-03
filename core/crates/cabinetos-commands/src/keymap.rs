//! Compiling the keymap: default bindings plus the user's overrides, checked
//! against the Immutable System Tier and for conflicts.

use std::collections::HashMap;

use crate::keys::{KeyChord, KeySequence};
use crate::registry::CommandRegistry;

/// After the first combination of a chord, how long the second may take.
/// The UI's chord state machine uses this value from the keymap.
pub const CHORD_WINDOW_MS: u32 = 1000;

/// The user's change to one command's bindings: one entry of `keybindings`
/// in `cabinetos.json`.
///
/// A command with any override loses its default bindings and gets the
/// overrides' keys instead; an override without keys (`"keys": ""`) leaves
/// the command unbound. An override without `when` keeps the command's own
/// context, so rebinding F5 does not make it fire inside text boxes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Override {
    /// The command's ID.
    pub command: String,
    /// The new keys; `None` removes the binding.
    pub keys: Option<KeySequence>,
    /// The context; `None` keeps the command's own.
    pub when: Option<String>,
}

/// One binding in effect.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Binding {
    /// The keys.
    pub keys: KeySequence,
    /// The command they run.
    pub command: String,
    /// The context; `None` means everywhere.
    pub when: Option<String>,
}

/// Every binding in effect.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Keymap {
    bindings: Vec<Binding>,
    immutable: Vec<String>,
}

impl Keymap {
    /// The bindings, by command in registry order.
    #[must_use]
    pub fn bindings(&self) -> &[Binding] {
        &self.bindings
    }

    /// The keys bound to `command` now.
    #[must_use]
    pub fn keys_of(&self, command: &str) -> Vec<&KeySequence> {
        self.bindings
            .iter()
            .filter(|binding| binding.command == command)
            .map(|binding| &binding.keys)
            .collect()
    }

    /// The keymap as the UI receives it.
    #[must_use]
    pub fn to_wire(&self) -> cabinetos_protocol::Keymap {
        cabinetos_protocol::Keymap {
            chord_window_ms: CHORD_WINDOW_MS,
            bindings: self
                .bindings
                .iter()
                .map(|binding| cabinetos_protocol::KeymapBinding {
                    keys: binding.keys.to_string(),
                    command: binding.command.clone(),
                    when: binding.when.clone(),
                })
                .collect(),
            immutable: self.immutable.clone(),
        }
    }
}

/// A keymap that compiled, with notes about overrides that were skipped.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Compiled {
    /// The keymap.
    pub keymap: Keymap,
    /// Overrides for commands nobody has registered (perhaps from a plugin
    /// that is not installed); they are ignored, not an error.
    pub warnings: Vec<String>,
}

/// A keymap that cannot be compiled. `entry` is the index of the override
/// (the `keybindings` entry) at fault, when an override is at fault.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
pub enum KeymapError {
    /// An override tries to change a command of the Immutable System Tier.
    #[error(
        "{command} belongs to the Immutable System Tier; its keys cannot be changed \
         (the tier: palette.show, overlay.close, keys.open)"
    )]
    ImmutableCommand {
        /// The immutable command.
        command: String,
        /// The override at fault.
        entry: Option<usize>,
    },
    /// A binding would use keys of the Immutable System Tier.
    #[error(
        "{keys} belongs to {owner} in the Immutable System Tier and cannot also \
         run {command}"
    )]
    ImmutableKeys {
        /// The keys in question.
        keys: String,
        /// The immutable command that owns them.
        owner: String,
        /// The command that tried to use them.
        command: String,
        /// The override at fault.
        entry: Option<usize>,
    },
    /// Two commands have the same keys in the same context.
    #[error("{keys} is bound to both {first} and {second}{}", context(.when.as_deref()))]
    Conflict {
        /// The keys in question.
        keys: String,
        /// The command bound first.
        first: String,
        /// The command bound second.
        second: String,
        /// The shared context.
        when: Option<String>,
        /// The override at fault.
        entry: Option<usize>,
    },
    /// A combination is both a binding of its own and the start of a chord,
    /// so the UI could not tell whether to run it or wait for the rest.
    #[error(
        "{prefix} starts the chord {chord} of {chord_command}, so it cannot also run {command} on its own"
    )]
    ChordPrefix {
        /// The combination in question.
        prefix: String,
        /// The chord it starts.
        chord: String,
        /// The command of the chord.
        chord_command: String,
        /// The command bound to the combination alone.
        command: String,
        /// The override at fault.
        entry: Option<usize>,
    },
}

fn context(when: Option<&str>) -> String {
    when.map(|when| format!(" when {when}")).unwrap_or_default()
}

impl KeymapError {
    /// The override (the `keybindings` entry) at fault, if any.
    #[must_use]
    pub fn entry(&self) -> Option<usize> {
        match self {
            Self::ImmutableCommand { entry, .. }
            | Self::ImmutableKeys { entry, .. }
            | Self::Conflict { entry, .. }
            | Self::ChordPrefix { entry, .. } => *entry,
        }
    }

    /// Whether the Immutable System Tier is what refused the change.
    #[must_use]
    pub fn is_immutable(&self) -> bool {
        matches!(
            self,
            Self::ImmutableCommand { .. } | Self::ImmutableKeys { .. }
        )
    }
}

/// A binding before the checks, with where it came from.
struct Candidate<'a> {
    keys: &'a KeySequence,
    /// Index into the registry.
    command: usize,
    when: Option<String>,
    /// The override that produced it; `None` for a default binding.
    origin: Option<usize>,
}

/// Compiles the default bindings of `registry` with the user's `overrides`.
///
/// Rules (`docs/keybindings.md`): commands of the Immutable System Tier keep
/// their bindings and nobody else may use their keys; two commands may not
/// share keys in the same context; a combination that starts a chord may
/// not also be a binding on its own, in any context.
pub fn compile(
    registry: &CommandRegistry,
    overrides: &[Override],
) -> Result<Compiled, KeymapError> {
    let commands = registry.commands();
    let mut warnings = Vec::new();
    let mut by_command: HashMap<&str, Vec<(usize, &Override)>> = HashMap::new();
    for (index, entry) in overrides.iter().enumerate() {
        match registry.get(&entry.command) {
            None => warnings.push(format!(
                "keybindings[{index}]: no command `{}` is registered; the entry is ignored",
                entry.command
            )),
            Some(command) if command.immutable => {
                return Err(KeymapError::ImmutableCommand {
                    command: command.id.clone(),
                    entry: Some(index),
                });
            }
            Some(_) => by_command
                .entry(entry.command.as_str())
                .or_default()
                .push((index, entry)),
        }
    }

    let mut candidates: Vec<Candidate<'_>> = Vec::new();
    for (index, command) in commands.iter().enumerate() {
        if let Some(entries) = by_command.get(command.id.as_str()) {
            for (origin, entry) in entries {
                if let Some(keys) = &entry.keys {
                    candidates.push(Candidate {
                        keys,
                        command: index,
                        when: entry.when.clone().or_else(|| command.when.clone()),
                        origin: Some(*origin),
                    });
                }
            }
        } else {
            for keys in &command.default_keys {
                candidates.push(Candidate {
                    keys,
                    command: index,
                    when: command.when.clone(),
                    origin: None,
                });
            }
        }
    }
    // The same keys for the same command in the same context count once.
    let mut seen = Vec::new();
    candidates.retain(|candidate| {
        let key = (candidate.keys, candidate.command, candidate.when.clone());
        if seen.contains(&key) {
            false
        } else {
            seen.push(key);
            true
        }
    });

    check_immutable_keys(registry, &candidates)?;
    check_conflicts(registry, &candidates)?;
    check_chord_prefixes(registry, &candidates)?;

    let bindings = candidates
        .iter()
        .map(|candidate| Binding {
            keys: candidate.keys.clone(),
            command: commands[candidate.command].id.clone(),
            when: candidate.when.clone(),
        })
        .collect();
    let immutable = commands
        .iter()
        .filter(|command| command.immutable)
        .map(|command| command.id.clone())
        .collect();
    Ok(Compiled {
        keymap: Keymap {
            bindings,
            immutable,
        },
        warnings,
    })
}

fn check_immutable_keys(
    registry: &CommandRegistry,
    candidates: &[Candidate<'_>],
) -> Result<(), KeymapError> {
    let commands = registry.commands();
    for protected in candidates.iter().filter(|c| commands[c.command].immutable) {
        for other in candidates.iter().filter(|c| !commands[c.command].immutable) {
            if other.keys == protected.keys {
                return Err(KeymapError::ImmutableKeys {
                    keys: other.keys.to_string(),
                    owner: commands[protected.command].id.clone(),
                    command: commands[other.command].id.clone(),
                    entry: other.origin,
                });
            }
        }
    }
    Ok(())
}

fn check_conflicts(
    registry: &CommandRegistry,
    candidates: &[Candidate<'_>],
) -> Result<(), KeymapError> {
    let commands = registry.commands();
    let mut taken: HashMap<(&KeySequence, Option<&str>), &Candidate<'_>> = HashMap::new();
    for candidate in candidates {
        let slot = (candidate.keys, candidate.when.as_deref());
        match taken.get(&slot) {
            Some(first) if first.command != candidate.command => {
                return Err(KeymapError::Conflict {
                    keys: candidate.keys.to_string(),
                    first: commands[first.command].id.clone(),
                    second: commands[candidate.command].id.clone(),
                    when: candidate.when.clone(),
                    entry: candidate.origin.or(first.origin),
                });
            }
            Some(_) => {}
            None => {
                taken.insert(slot, candidate);
            }
        }
    }
    Ok(())
}

fn check_chord_prefixes(
    registry: &CommandRegistry,
    candidates: &[Candidate<'_>],
) -> Result<(), KeymapError> {
    let commands = registry.commands();
    // For each first combination, one chord that starts with it; an
    // immutable one if there is one, so the error names the tier.
    let mut chords: HashMap<KeyChord, &Candidate<'_>> = HashMap::new();
    for candidate in candidates.iter().filter(|c| c.keys.is_chord()) {
        let slot = chords.entry(candidate.keys.first()).or_insert(candidate);
        if commands[candidate.command].immutable && !commands[slot.command].immutable {
            *slot = candidate;
        }
    }
    for single in candidates.iter().filter(|c| !c.keys.is_chord()) {
        let Some(chord) = chords.get(&single.keys.first()) else {
            continue;
        };
        let (single_command, chord_command) = (&commands[single.command], &commands[chord.command]);
        if single_command.immutable || chord_command.immutable {
            let (owner, intruder) = if single_command.immutable {
                (single, *chord)
            } else {
                (*chord, single)
            };
            return Err(KeymapError::ImmutableKeys {
                keys: intruder.keys.to_string(),
                owner: commands[owner.command].id.clone(),
                command: commands[intruder.command].id.clone(),
                entry: intruder.origin,
            });
        }
        return Err(KeymapError::ChordPrefix {
            prefix: single.keys.to_string(),
            chord: chord.keys.to_string(),
            chord_command: chord_command.id.clone(),
            command: single_command.id.clone(),
            entry: single.origin.or(chord.origin),
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn over(command: &str, keys: &str) -> Override {
        Override {
            command: command.to_owned(),
            keys: (!keys.is_empty()).then(|| keys.parse().unwrap()),
            when: None,
        }
    }

    fn compile_with(overrides: &[Override]) -> Result<Compiled, KeymapError> {
        compile(&CommandRegistry::core(), overrides)
    }

    fn keys(keymap: &Keymap, command: &str) -> Vec<String> {
        keymap
            .keys_of(command)
            .iter()
            .map(ToString::to_string)
            .collect()
    }

    #[test]
    fn the_defaults_compile() {
        let compiled = compile_with(&[]).unwrap();
        assert!(compiled.warnings.is_empty());
        let keymap = compiled.keymap;
        assert_eq!(keys(&keymap, "palette.show"), ["ctrl+shift+p"]);
        assert_eq!(keys(&keymap, "keys.open"), ["ctrl+k ctrl+s"]);
        assert!(keys(&keymap, "help.about").is_empty());
        let wire = keymap.to_wire();
        assert_eq!(wire.chord_window_ms, 1000);
        assert_eq!(
            wire.immutable,
            ["palette.show", "overlay.close", "keys.open"]
        );
        let copy = wire
            .bindings
            .iter()
            .find(|b| b.command == "file.copyToOtherPane")
            .unwrap();
        assert_eq!(copy.when.as_deref(), Some("filesView"));
    }

    #[test]
    fn an_override_replaces_the_default_and_keeps_its_context() {
        let keymap = compile_with(&[
            over("view.toggleSidebar", "ctrl+alt+b"),
            over("file.copyToOtherPane", "f9"),
        ])
        .unwrap()
        .keymap;
        assert_eq!(keys(&keymap, "view.toggleSidebar"), ["ctrl+alt+b"]);
        let copy = keymap
            .bindings()
            .iter()
            .find(|b| b.command == "file.copyToOtherPane")
            .unwrap();
        assert_eq!(copy.keys.to_string(), "f9");
        assert_eq!(copy.when.as_deref(), Some("filesView"));
    }

    #[test]
    fn empty_keys_unbind_and_several_entries_add_up() {
        let keymap = compile_with(&[
            over("view.toggleSidebar", ""),
            over("view.toggleDualPane", "ctrl+1"),
            over("view.toggleDualPane", "ctrl+2"),
        ])
        .unwrap()
        .keymap;
        assert!(keys(&keymap, "view.toggleSidebar").is_empty());
        assert_eq!(keys(&keymap, "view.toggleDualPane"), ["ctrl+1", "ctrl+2"]);
    }

    #[test]
    fn unknown_commands_are_warnings() {
        let compiled = compile_with(&[over("plugin.gone", "ctrl+9")]).unwrap();
        assert_eq!(compiled.warnings.len(), 1);
        assert!(compiled.warnings[0].contains("plugin.gone"));
    }

    #[test]
    fn the_immutable_tier_cannot_be_rebound() {
        for command in ["palette.show", "overlay.close", "keys.open"] {
            let error = compile_with(&[over(command, "ctrl+alt+p")]).unwrap_err();
            assert!(error.is_immutable(), "{error}");
            assert_eq!(error.entry(), Some(0));
            assert!(error.to_string().contains("Immutable System Tier"));
        }
        let unbind = compile_with(&[over("palette.show", "")]).unwrap_err();
        assert!(matches!(unbind, KeymapError::ImmutableCommand { .. }));
    }

    #[test]
    fn nobody_else_may_use_immutable_keys() {
        let error = compile_with(&[over("help.about", "ctrl+shift+p")]).unwrap_err();
        assert_eq!(
            error,
            KeymapError::ImmutableKeys {
                keys: "ctrl+shift+p".to_owned(),
                owner: "palette.show".to_owned(),
                command: "help.about".to_owned(),
                entry: Some(0),
            }
        );
        // Starting a chord with Escape would delay closing overlays.
        let error = compile_with(&[over("help.about", "escape h")]).unwrap_err();
        assert!(error.is_immutable(), "{error}");
        let error = compile_with(&[over("help.about", "ctrl+k ctrl+s")]).unwrap_err();
        assert!(error.is_immutable(), "{error}");
    }

    #[test]
    fn two_commands_on_the_same_keys_conflict() {
        let error = compile_with(&[over("help.about", "ctrl+b")]).unwrap_err();
        assert_eq!(
            error,
            KeymapError::Conflict {
                keys: "ctrl+b".to_owned(),
                first: "view.toggleSidebar".to_owned(),
                second: "help.about".to_owned(),
                when: None,
                entry: Some(0),
            }
        );
        assert!(error.to_string().contains("view.toggleSidebar"));
        assert!(error.to_string().contains("help.about"));
    }

    #[test]
    fn different_contexts_do_not_conflict() {
        let mut in_terminal = over("help.about", "f5");
        in_terminal.when = Some("terminalFocus".to_owned());
        let keymap = compile_with(&[in_terminal]).unwrap().keymap;
        assert_eq!(keys(&keymap, "help.about"), ["f5"]);
        assert_eq!(keys(&keymap, "file.copyToOtherPane"), ["f5"]);
    }

    #[test]
    fn f2_renames_in_a_pane_and_rebinds_in_the_palette() {
        let keymap = compile_with(&[]).unwrap().keymap;
        let f2: Vec<(&str, Option<&str>)> = keymap
            .bindings()
            .iter()
            .filter(|binding| binding.keys.to_string() == "f2")
            .map(|binding| (binding.command.as_str(), binding.when.as_deref()))
            .collect();
        assert_eq!(
            f2,
            [
                ("keys.rebind", Some("paletteOpen")),
                ("file.rename", Some("filesView"))
            ]
        );
        // In the same context the two would conflict.
        let mut same_context = over("file.rename", "f2");
        same_context.when = Some("paletteOpen".to_owned());
        let error = compile_with(&[same_context]).unwrap_err();
        assert_eq!(
            error,
            KeymapError::Conflict {
                keys: "f2".to_owned(),
                first: "keys.rebind".to_owned(),
                second: "file.rename".to_owned(),
                when: Some("paletteOpen".to_owned()),
                entry: Some(0),
            }
        );
    }

    #[test]
    fn a_chord_prefix_cannot_be_a_binding_too() {
        let error = compile_with(&[
            over("view.toggleSidebar", "ctrl+q"),
            over("help.about", "ctrl+q ctrl+q"),
        ])
        .unwrap_err();
        assert_eq!(
            error,
            KeymapError::ChordPrefix {
                prefix: "ctrl+q".to_owned(),
                chord: "ctrl+q ctrl+q".to_owned(),
                chord_command: "help.about".to_owned(),
                command: "view.toggleSidebar".to_owned(),
                entry: Some(0),
            }
        );
        let error = compile_with(&[over("help.about", "ctrl+b ctrl+a")]).unwrap_err();
        assert!(matches!(error, KeymapError::ChordPrefix { .. }), "{error}");
        // Ctrl+K alone would break the chord of keys.open, which is immutable.
        let error = compile_with(&[over("help.about", "ctrl+k")]).unwrap_err();
        assert!(
            matches!(
                &error,
                KeymapError::ImmutableKeys { owner, command, entry: Some(0), .. }
                    if owner == "keys.open" && command == "help.about"
            ),
            "{error}"
        );
        // A new chord on a free first combination is fine.
        let keymap = compile_with(&[over("help.about", "ctrl+k ctrl+j")])
            .unwrap()
            .keymap;
        assert_eq!(keys(&keymap, "help.about"), ["ctrl+k ctrl+j"]);
    }
}
