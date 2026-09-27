//! Plugin host: runs headless Core Plugins as WebAssembly components in
//! `wasmtime`, enforces the capabilities each plugin was granted, and turns a
//! plugin trap into a logged, contained failure.
//!
//! Serves Constitution Article 8 (Sandboxed Extensibility), Article 10 (The
//! Zero-Bloat Foundation: features arrive as opt-in extensions) and Article 11
//! (Bifurcated Extension Architecture: this is the Core Plugin layer). Brief §6.
//!
//! Status: stub. Phase 7 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early.
#![forbid(unsafe_code)]

/// Owns the `wasmtime` engine; one store and one thread per plugin, with fuel
/// and memory limits.
pub struct PluginHost;

/// Identifies one installed plugin. It is logged with every event the plugin
/// causes and with every trap.
pub struct PluginId;

/// A plugin's manifest: name, version and the capabilities it requests.
pub struct PluginManifest;

/// One permission a plugin can request, such as `fs:read`, `fs:write`,
/// `cmd:register`, `process:run`, `net` or `credentials`.
pub struct Capability;
