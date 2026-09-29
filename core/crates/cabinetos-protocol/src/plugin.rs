//! Core Plugins as clients see them (`docs/plugins.md`).

use serde::{Deserialize, Serialize};

/// One installed plugin.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PluginInfo {
    /// The plugin's ID, from its `plugin.json`; also its folder's name.
    pub id: String,
    /// Its display name, shown as a badge on its commands.
    pub name: String,
    /// Its version.
    pub version: String,
    /// Who made it.
    pub author: String,
    /// What it does, in a sentence or two.
    pub description: String,
    /// Where it is in its life.
    pub state: PluginState,
    /// What it asks to do, for the review dialog.
    pub capabilities: Vec<CapabilityInfo>,
    /// The IDs of the commands it registered (while it is active).
    pub commands: Vec<String>,
}

/// Where a plugin is in its life.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum PluginState {
    /// Being compiled and started.
    Loading,
    /// Running; its commands are registered.
    Active,
    /// Turned off in the configuration.
    Disabled,
    /// It asks for capabilities the user has not granted; it does not run
    /// until they are.
    NeedsReview {
        /// The capabilities still to grant.
        missing: Vec<String>,
    },
    /// It could not start: a bad manifest, a component that does not load,
    /// or `activate` failed.
    Failed {
        /// Why.
        message: String,
    },
    /// It trapped or ran out of time, fuel or memory; its instance was
    /// dropped.
    Crashed {
        /// The trap.
        message: String,
        /// When, in milliseconds since 1970-01-01 UTC.
        at_ms: u64,
    },
}

/// One capability a plugin asks for.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CapabilityInfo {
    /// For example `fs:read`.
    pub name: String,
    /// How much it lets the plugin do.
    pub level: CapabilityLevel,
    /// Whether the user granted it.
    pub granted: bool,
    /// The plugin's own plain-language reason.
    pub reason: String,
    /// The folders it concerns, for `fs:read` and `fs:write`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub roots: Vec<String>,
    /// The hosts it may reach, for `net` (protocol 13).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub hosts: Vec<String>,
    /// The secrets the core may put into its requests, for `net`
    /// (protocol 13).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub secrets: Vec<String>,
}

/// How much a capability lets a plugin do, as the review dialog colors it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum CapabilityLevel {
    /// Little risk: registering commands, reading the settings.
    Low,
    /// Access to the user's files.
    Medium,
    /// Reaching outside the machine or the user's secrets.
    High,
}
