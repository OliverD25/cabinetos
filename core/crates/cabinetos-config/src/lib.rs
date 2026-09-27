//! Configuration: loads `%APPDATA%\CabinetOS\cabinetos.json`, validates it
//! against its published JSON Schema, fills in defaults, watches the file and
//! sends a diff to the UI when it changes.
//!
//! Serves Constitution Article 6 (Universal Configuration: the settings menu and
//! the raw file stay in sync in real time). Brief §7.
//!
//! Status: stub. Phase 3 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early.
#![forbid(unsafe_code)]

/// The parsed and validated contents of `cabinetos.json`, defaults included.
pub struct Config;

/// Watches `cabinetos.json` and re-parses it when it is edited by hand.
pub struct ConfigWatcher;

/// What changed between two versions of the configuration.
pub struct ConfigDiff;
