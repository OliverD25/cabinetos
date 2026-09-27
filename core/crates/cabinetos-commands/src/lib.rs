//! Commands: the registry of every named command, the keybinding model with
//! chord sequences, and the immutable system tier.
//!
//! Serves Constitution Article 7 (Absolute Keyboard Control & Command Palette:
//! every action is a named command with a shortcut) and Article 4 (Progressive
//! Disclosure: power features are reached through the palette). Brief §7.
//!
//! Status: stub. Phase 3 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early.
#![forbid(unsafe_code)]

/// Every command the system knows: id, category, name, default binding and
/// source (core or plugin).
pub struct CommandRegistry;

/// The discrete name of a command, for example `pane.toggleSingle`.
pub struct CommandId;

/// A key or a chord sequence (for example `Ctrl+K` then `Ctrl+C` within
/// 1000 ms) bound to a [`CommandId`].
pub struct Keybinding;

/// The protected set of system shortcuts that user configuration cannot
/// override, so nobody can lock themselves out of core functions.
pub struct ImmutableTier;
