//! The Core Plugin host: headless WebAssembly components in `wasmtime`, each
//! in its own sandbox and on its own thread (`docs/plugins.md`).
//!
//! - [`PluginHost::load_all`] reads every `<plugins dir>/<id>/plugin.json`
//!   and `plugin.wasm`, checks the manifest, and starts each plugin the
//!   configuration enables and grants every capability it asks for. A
//!   plugin missing a grant waits in `needs_review`.
//! - One shared `Engine` compiles the components, with fuel and epoch
//!   interruption turned on. Each plugin instance has one `Store` on one
//!   thread, which takes calls from a channel; every call gets fresh fuel,
//!   a wall-clock deadline (a ticker advances the engine's epoch every
//!   10 ms while a call runs) and the memory limit.
//! - A trap, running out of fuel or time, or asking for too much memory
//!   ends the instance: logged at ERROR with the plugin's ID and backtrace,
//!   state `crashed`, a `plugin_crashed` event, its commands unregistered.
//!   The core keeps running.
//!
//! Serves Constitution Article 8 (Sandboxed Extensibility), Article 10 (The
//! Zero-Bloat Foundation: features arrive as opt-in extensions) and Article
//! 11 (Bifurcated Extension Architecture: this is the Core Plugin layer).
//! Brief §6 and §8 ("Plugin Trap Handling").
#![forbid(unsafe_code)]

#[allow(missing_docs, clippy::all, clippy::pedantic)]
mod bindings {
    wasmtime::component::bindgen!({
        path: "../../../sdk/wit",
        world: "core-plugin",
        imports: { default: trappable },
    });
}
pub mod manifest;
mod net;
mod sandbox;
mod worker;

use std::collections::{BTreeMap, HashSet, VecDeque};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError, Weak};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use cabinetos_protocol::{CapabilityInfo, ErrorCode, Event, JobKind, PluginInfo, PluginState};
use wasmtime::component::{HasSelf, Linker};
use wasmtime::{Config, Engine};

use crate::bindings::{CorePlugin, JobVerdict};
use crate::manifest::{Capability, Manifest};
use crate::worker::{Activity, Call, Reply, Report, State};

/// One plugin's settings in the configuration (`plugins.<id>` in
/// `cabinetos.json`).
pub use cabinetos_config::PluginSettings;

/// Environment variable naming the plugins folder, when no folder is given
/// on the command line.
pub const PLUGINS_DIR_ENV: &str = "CABINETOS_PLUGINS_DIR";

/// Environment variable naming the folder of the plugins' own folders.
pub const PLUGINS_DATA_DIR_ENV: &str = "CABINETOS_PLUGINS_DATA_DIR";

/// The plugins folder: `explicit` wins, then [`PLUGINS_DIR_ENV`], then
/// `%LOCALAPPDATA%\CabinetOS\plugins`.
#[must_use]
pub fn plugins_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(
        explicit,
        std::env::var_os(PLUGINS_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
        "plugins",
    )
}

/// The folder that holds each plugin's own folder: `explicit` wins, then
/// [`PLUGINS_DATA_DIR_ENV`], then `%LOCALAPPDATA%\CabinetOS\plugins-data`.
#[must_use]
pub fn plugins_data_dir(explicit: Option<PathBuf>) -> PathBuf {
    resolve_dir(
        explicit,
        std::env::var_os(PLUGINS_DATA_DIR_ENV),
        std::env::var_os("LOCALAPPDATA"),
        "plugins-data",
    )
}

/// Without `LOCALAPPDATA` (not a normal Windows session) the default is
/// under the temp folder, as for the logs.
fn resolve_dir(
    explicit: Option<PathBuf>,
    env_dir: Option<OsString>,
    local_app_data: Option<OsString>,
    leaf: &str,
) -> PathBuf {
    if let Some(dir) = explicit {
        return dir;
    }
    if let Some(dir) = env_dir.filter(|dir| !dir.is_empty()) {
        return PathBuf::from(dir);
    }
    local_app_data
        .filter(|dir| !dir.is_empty())
        .map_or_else(std::env::temp_dir, PathBuf::from)
        .join("CabinetOS")
        .join(leaf)
}

/// How often the ticker advances the engine's epoch while a plugin call
/// runs; deadlines are counted in these ticks.
pub(crate) const EPOCH_TICK: Duration = Duration::from_millis(10);

/// How much longer than its deadline a call may run before the host gives
/// up on the instance. Only a call stuck inside a host function (such as a
/// long sleep) gets this far: the epoch deadline stops running WebAssembly.
const STUCK_GRACE: Duration = Duration::from_secs(2);

/// Crashes within this window that make a plugin stay crashed.
const CRASH_WINDOW: Duration = Duration::from_mins(10);
const CRASHES_BEFORE_GIVING_UP: usize = 3;

/// Notifications (`on-listing-opened`) that may wait for one plugin; more
/// are dropped, so a slow plugin cannot make the core pile them up.
const MAX_QUEUED_NOTIFICATIONS: usize = 64;

/// What the host needs from the core.
pub trait HostServices: Send + Sync {
    /// A setting of `cabinetos.json` as JSON, by dotted path.
    fn config_value(&self, path: &str) -> Option<String>;
    /// Sends an event to the connected clients.
    fn publish(&self, event: Event);
    /// Replaces the commands a plugin registered (empty: it has none now).
    fn set_commands(&self, plugin_id: &str, plugin_name: &str, commands: &[PluginCommand]);
    /// The value of a stored secret, for a plugin's `http-request`; it goes
    /// into a header and never to the plugin. None when there is none (and
    /// in a core without secrets).
    fn secret(&self, name: &str) -> Option<String> {
        let _ = name;
        None
    }
}

/// A command a plugin registered.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PluginCommand {
    /// `<plugin id>.<name>`.
    pub id: String,
    /// The title in the palette.
    pub title: String,
    /// The palette group.
    pub category: String,
    /// Its default keys (the core drops those that clash).
    pub default_keys: Vec<String>,
}

/// How the host runs plugins.
#[derive(Clone, Debug)]
pub struct HostConfig {
    /// `<dir>/<id>/plugin.json` and `plugin.wasm` for each plugin.
    pub plugins_dir: PathBuf,
    /// `<dir>/<id>` is each plugin's own folder, `/data` in its sandbox.
    pub data_dir: PathBuf,
    /// This core's version, for `minCoreVersion` and `activate`.
    pub core_version: String,
    /// The deadline of a call (`activate`, `on-command`).
    pub call_timeout: Duration,
    /// The deadline of `before-job`.
    pub before_job_timeout: Duration,
    /// The fuel of one call.
    pub fuel: u64,
    /// The linear memory one instance may have.
    pub memory_bytes: usize,
    /// How long after a crash the host starts the plugin again (unless it
    /// crashed three times in ten minutes).
    pub restart_delay: Duration,
}

impl HostConfig {
    /// The defaults for the given folders.
    #[must_use]
    pub fn new(plugins_dir: PathBuf, data_dir: PathBuf, core_version: &str) -> Self {
        Self {
            plugins_dir,
            data_dir,
            core_version: core_version.to_owned(),
            call_timeout: Duration::from_secs(5),
            before_job_timeout: Duration::from_millis(500),
            fuel: 5_000_000_000,
            memory_bytes: 256 * 1024 * 1024,
            restart_delay: Duration::from_secs(5),
        }
    }
}

/// Why a request about a plugin failed.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
#[error("{message}")]
pub struct PluginError {
    /// The protocol error code.
    pub code: ErrorCode,
    /// What happened.
    pub message: String,
}

impl PluginError {
    fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

/// The limits of every call of one plugin.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Limits {
    pub(crate) call_timeout: Duration,
    pub(crate) before_job_timeout: Duration,
    pub(crate) fuel: u64,
    pub(crate) memory_bytes: usize,
}

/// A plugin ready to run: what its instance needs.
pub(crate) struct Loaded {
    pub(crate) id: String,
    pub(crate) manifest: Manifest,
    pub(crate) wasm: PathBuf,
    pub(crate) granted: HashSet<Capability>,
    pub(crate) read_roots: Vec<PathBuf>,
    pub(crate) write_roots: Vec<PathBuf>,
    pub(crate) data_dir: PathBuf,
    pub(crate) limits: Limits,
    pub(crate) services: Arc<dyn HostServices>,
    /// What `http-request` may reach (capability `net`).
    pub(crate) net: net::NetRules,
    /// The HTTP client every plugin shares.
    pub(crate) http: Arc<net::Net>,
}

/// A running instance's thread, as the host sees it.
struct Worker {
    calls: Sender<Call>,
    activity: Arc<Activity>,
}

/// One installed plugin.
struct Slot {
    dir: PathBuf,
    manifest: Result<Manifest, String>,
    state: PluginState,
    /// Counts starts; a report from an older instance is ignored.
    generation: u64,
    worker: Option<Worker>,
    loaded: Option<Arc<Loaded>>,
    commands: Vec<String>,
    crashes: VecDeque<Instant>,
    /// The settings the slot was last started or stopped with.
    applied: Option<PluginSettings>,
}

impl Slot {
    fn name(&self) -> String {
        self.manifest
            .as_ref()
            .map_or_else(|_| self.id_from_dir(), |manifest| manifest.name.clone())
    }

    fn id_from_dir(&self) -> String {
        self.dir
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default()
    }
}

/// What the host's threads share.
pub(crate) struct HostInner {
    pub(crate) engine: Engine,
    pub(crate) linker: Linker<State>,
    pub(crate) config: HostConfig,
    /// The HTTP client of `http-request`, shared by every plugin.
    net: Arc<net::Net>,
    services: Arc<dyn HostServices>,
    slots: Mutex<BTreeMap<String, Slot>>,
    settings: Mutex<BTreeMap<String, PluginSettings>>,
    /// Calls running now; the epoch ticker only runs while there are some.
    running: Mutex<usize>,
    running_changed: Condvar,
    stopping: AtomicBool,
    myself: Weak<Self>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

impl HostInner {
    /// Runs one call with the epoch ticker running.
    pub(crate) fn guard<T>(&self, call: impl FnOnce() -> T) -> T {
        *lock(&self.running) += 1;
        self.running_changed.notify_all();
        let result = call();
        *lock(&self.running) -= 1;
        result
    }

    fn set_state(&self, id: &str, slot: &mut Slot, state: PluginState) {
        if slot.state != state {
            slot.state = state.clone();
            self.services.publish(Event::PluginStateChanged {
                plugin_id: id.to_owned(),
                state,
            });
        }
    }

    fn unregister(&self, id: &str, slot: &mut Slot) {
        if !slot.commands.is_empty() {
            slot.commands.clear();
            self.services.set_commands(id, &slot.name(), &[]);
        }
    }

    /// A worker's news about its instance.
    pub(crate) fn report(&self, id: &str, generation: u64, report: Report) {
        let mut slots = lock(&self.slots);
        let Some(slot) = slots.get_mut(id) else {
            return;
        };
        if slot.generation != generation {
            return;
        }
        match report {
            Report::Active(commands) => {
                slot.commands = commands.iter().map(|command| command.id.clone()).collect();
                self.services.set_commands(id, &slot.name(), &commands);
                self.set_state(id, slot, PluginState::Active);
            }
            Report::Failed(message) => {
                slot.worker = None;
                self.unregister(id, slot);
                tracing::error!(plugin_id = id, error = %message, "plugin failed to start");
                self.set_state(id, slot, PluginState::Failed { message });
            }
            Report::Crashed(message) => self.crashed(id, slot, message),
        }
    }

    fn crashed(&self, id: &str, slot: &mut Slot, message: String) {
        slot.worker = None;
        self.unregister(id, slot);
        let now = Instant::now();
        slot.crashes.push_back(now);
        while slot
            .crashes
            .front()
            .is_some_and(|first| now.duration_since(*first) > CRASH_WINDOW)
        {
            slot.crashes.pop_front();
        }
        self.services.publish(Event::PluginCrashed {
            plugin_id: id.to_owned(),
            message: message.clone(),
        });
        self.set_state(
            id,
            slot,
            PluginState::Crashed {
                message,
                at_ms: now_ms(),
            },
        );
        if slot.crashes.len() >= CRASHES_BEFORE_GIVING_UP {
            tracing::warn!(
                plugin_id = id,
                crashes = slot.crashes.len(),
                "the plugin crashed {CRASHES_BEFORE_GIVING_UP} times in 10 minutes; it stays stopped until it is reloaded"
            );
            return;
        }
        // Started again after a pause, unless something else starts or stops
        // it first (the generation then moved on).
        let generation = slot.generation;
        let host = self.myself.clone();
        let delay = self.config.restart_delay;
        let id = id.to_owned();
        let _ = std::thread::Builder::new()
            .name(format!("plugin-{id}-restart"))
            .spawn(move || {
                std::thread::sleep(delay);
                if let Some(host) = host.upgrade() {
                    host.restart_after_crash(&id, generation);
                }
            });
    }

    fn restart_after_crash(&self, id: &str, generation: u64) {
        if self.stopping.load(Ordering::SeqCst) {
            return;
        }
        let mut slots = lock(&self.slots);
        let Some(slot) = slots.get_mut(id) else {
            return;
        };
        if slot.generation == generation && matches!(slot.state, PluginState::Crashed { .. }) {
            tracing::info!(plugin_id = id, "starting the plugin again after its crash");
            if let Some(loaded) = slot.loaded.clone() {
                self.spawn(id, slot, loaded);
            }
        }
    }

    /// Starts a new instance for the slot.
    fn spawn(&self, id: &str, slot: &mut Slot, loaded: Arc<Loaded>) {
        slot.generation += 1;
        let generation = slot.generation;
        let (calls, receiver) = mpsc::channel();
        let activity = Arc::new(Activity::default());
        let thread_activity = Arc::clone(&activity);
        let host = self.myself.clone();
        let plugin = Arc::clone(&loaded);
        let spawned = std::thread::Builder::new()
            .name(format!("plugin-{id}"))
            .spawn(move || worker::run(&host, &plugin, generation, &receiver, &thread_activity));
        slot.loaded = Some(loaded);
        match spawned {
            Ok(_) => {
                slot.worker = Some(Worker { calls, activity });
                self.set_state(id, slot, PluginState::Loading);
            }
            Err(error) => self.set_state(
                id,
                slot,
                PluginState::Failed {
                    message: format!("cannot start its thread: {error}"),
                },
            ),
        }
    }

    /// Stops the slot's instance, if any.
    fn stop(&self, id: &str, slot: &mut Slot) {
        slot.generation += 1;
        if let Some(worker) = slot.worker.take() {
            let _ = worker.calls.send(Call::Stop);
        }
        self.unregister(id, slot);
    }

    /// Starts, stops or holds the slot according to its manifest and
    /// settings.
    fn evaluate(&self, id: &str, slot: &mut Slot, settings: &PluginSettings) {
        slot.applied = Some(settings.clone());
        let manifest = match &slot.manifest {
            Ok(manifest) => manifest.clone(),
            Err(message) => {
                let message = message.clone();
                self.stop(id, slot);
                self.set_state(id, slot, PluginState::Failed { message });
                return;
            }
        };
        if !settings.enabled {
            self.stop(id, slot);
            self.set_state(id, slot, PluginState::Disabled);
            return;
        }
        if let Some(refused) = manifest
            .capabilities
            .iter()
            .filter_map(|request| Capability::parse(&request.name))
            .find(|capability| capability.never_granted())
        {
            self.stop(id, slot);
            self.set_state(
                id,
                slot,
                PluginState::Failed {
                    message: format!(
                        "it asks for {}, which this version of CabinetOS never grants",
                        refused.name()
                    ),
                },
            );
            return;
        }
        let missing: Vec<String> = manifest
            .capabilities
            .iter()
            .filter(|request| !settings.granted.contains(&request.name))
            .map(|request| request.name.clone())
            .collect();
        if !missing.is_empty() {
            self.stop(id, slot);
            self.set_state(id, slot, PluginState::NeedsReview { missing });
            return;
        }
        match self.prepare(&slot.dir, manifest) {
            Ok(loaded) => {
                self.stop(id, slot);
                self.spawn(id, slot, Arc::new(loaded));
            }
            Err(message) => {
                self.stop(id, slot);
                tracing::error!(plugin_id = id, error = %message, "plugin cannot start");
                self.set_state(id, slot, PluginState::Failed { message });
            }
        }
    }

    /// What an instance of this plugin needs.
    fn prepare(&self, dir: &Path, manifest: Manifest) -> Result<Loaded, String> {
        let wasm = dir.join(manifest::COMPONENT_FILE);
        if !wasm.is_file() {
            return Err(format!("{} is missing", wasm.display()));
        }
        let mut granted = HashSet::new();
        let mut read_roots = Vec::new();
        let mut write_roots = Vec::new();
        let mut net_rules = net::NetRules::default();
        for request in &manifest.capabilities {
            let Some(capability) = Capability::parse(&request.name) else {
                continue;
            };
            granted.insert(capability);
            if capability == Capability::Net {
                for host in &request.hosts {
                    net_rules.hosts.push(manifest::HostRule::parse(host)?);
                }
                net_rules.secrets.clone_from(&request.secrets);
            }
            let roots = match capability {
                Capability::FsRead => &mut read_roots,
                Capability::FsWrite => &mut write_roots,
                _ => continue,
            };
            for root in &request.roots {
                let root = manifest::expand_root(root)?;
                if !root.is_dir() {
                    return Err(format!("the folder {} does not exist", root.display()));
                }
                roots.push(root);
            }
        }
        Ok(Loaded {
            id: manifest.id.clone(),
            data_dir: self.config.data_dir.join(&manifest.id),
            manifest,
            wasm,
            granted,
            read_roots,
            write_roots,
            limits: Limits {
                call_timeout: self.config.call_timeout,
                before_job_timeout: self.config.before_job_timeout,
                fuel: self.config.fuel,
                memory_bytes: self.config.memory_bytes,
            },
            services: Arc::clone(&self.services),
            net: net_rules,
            http: Arc::clone(&self.net),
        })
    }
}

/// Advances the engine's epoch every 10 ms while a plugin call runs, and
/// sleeps otherwise.
fn tick(host: &Weak<HostInner>) {
    loop {
        let Some(inner) = host.upgrade() else { return };
        if inner.stopping.load(Ordering::SeqCst) {
            return;
        }
        {
            let running = lock(&inner.running);
            if *running == 0 {
                let _unused = inner
                    .running_changed
                    .wait_timeout(running, Duration::from_millis(500))
                    .unwrap_or_else(PoisonError::into_inner);
                continue;
            }
        }
        drop(inner);
        std::thread::sleep(EPOCH_TICK);
        if let Some(inner) = host.upgrade() {
            inner.engine.increment_epoch();
        }
    }
}

/// Runs the Core Plugins.
pub struct PluginHost {
    inner: Arc<HostInner>,
}

impl std::fmt::Debug for PluginHost {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginHost")
            .field("plugins_dir", &self.inner.config.plugins_dir)
            .finish_non_exhaustive()
    }
}

impl PluginHost {
    /// A host with its engine and epoch ticker; no plugin loaded yet.
    pub fn new(config: HostConfig, services: Arc<dyn HostServices>) -> Result<Self, String> {
        let mut engine_config = Config::new();
        engine_config
            .wasm_component_model(true)
            .epoch_interruption(true)
            .consume_fuel(true);
        let engine = Engine::new(&engine_config)
            .map_err(|error| format!("cannot start wasmtime: {error:#}"))?;
        let mut linker = Linker::new(&engine);
        wasmtime_wasi::p2::add_to_linker_sync(&mut linker)
            .map_err(|error| format!("cannot link WASI: {error:#}"))?;
        CorePlugin::add_to_linker::<State, HasSelf<State>>(&mut linker, |state| state)
            .map_err(|error| format!("cannot link the host interface: {error:#}"))?;
        let inner = Arc::new_cyclic(|myself| HostInner {
            engine,
            linker,
            config,
            net: Arc::new(net::Net::default()),
            services,
            slots: Mutex::new(BTreeMap::new()),
            settings: Mutex::new(BTreeMap::new()),
            running: Mutex::new(0),
            running_changed: Condvar::new(),
            stopping: AtomicBool::new(false),
            myself: myself.clone(),
        });
        let ticker = Arc::downgrade(&inner);
        std::thread::Builder::new()
            .name("plugin-epoch".to_owned())
            .spawn(move || tick(&ticker))
            .map_err(|error| format!("cannot start the epoch ticker: {error}"))?;
        Ok(Self { inner })
    }

    /// Reads every plugin folder and starts what the settings allow.
    /// Compiling happens on each plugin's own thread; this returns quickly.
    pub fn load_all(&self, settings: &BTreeMap<String, PluginSettings>) {
        let dir = &self.inner.config.plugins_dir;
        let folders: Vec<PathBuf> = match std::fs::read_dir(dir) {
            Ok(entries) => entries
                .filter_map(Result::ok)
                .map(|entry| entry.path())
                .filter(|path| path.join(manifest::MANIFEST_FILE).is_file())
                .collect(),
            Err(error) => {
                tracing::info!(dir = %dir.display(), %error, "no plugins folder; no plugins");
                Vec::new()
            }
        };
        for id in settings.keys() {
            if !folders
                .iter()
                .any(|folder| folder.file_name().is_some_and(|name| name == id.as_str()))
            {
                tracing::warn!(
                    plugin_id = id,
                    "the configuration names a plugin that is not installed"
                );
            }
        }
        *lock(&self.inner.settings) = settings.clone();
        let mut slots = lock(&self.inner.slots);
        for folder in folders {
            let manifest = manifest::read(&folder, &self.inner.config.core_version);
            let id = match &manifest {
                Ok(manifest) => manifest.id.clone(),
                Err(_) => folder
                    .file_name()
                    .map(|name| name.to_string_lossy().into_owned())
                    .unwrap_or_default(),
            };
            if let Err(error) = &manifest {
                tracing::error!(plugin_id = %id, %error, "bad plugin manifest");
            }
            let slot = slots.entry(id.clone()).or_insert_with(|| Slot {
                dir: folder.clone(),
                manifest: manifest.clone(),
                state: PluginState::Loading,
                generation: 0,
                worker: None,
                loaded: None,
                commands: Vec::new(),
                crashes: VecDeque::new(),
                applied: None,
            });
            let plugin_settings = settings.get(&id).cloned().unwrap_or_default();
            self.inner.evaluate(&id, slot, &plugin_settings);
        }
    }

    /// The configuration changed: starts or stops the plugins whose
    /// settings changed.
    pub fn apply_settings(&self, settings: &BTreeMap<String, PluginSettings>) {
        *lock(&self.inner.settings) = settings.clone();
        let mut slots = lock(&self.inner.slots);
        for (id, slot) in slots.iter_mut() {
            let plugin_settings = settings.get(id).cloned().unwrap_or_default();
            if slot.applied.as_ref() != Some(&plugin_settings) {
                tracing::info!(plugin_id = %id, "plugin settings changed");
                self.inner.evaluate(id, slot, &plugin_settings);
            }
        }
    }

    /// Every installed plugin, by ID.
    #[must_use]
    pub fn list(&self) -> Vec<PluginInfo> {
        let settings = lock(&self.inner.settings).clone();
        lock(&self.inner.slots)
            .iter()
            .map(|(id, slot)| {
                let granted = settings
                    .get(id)
                    .map(|settings| settings.granted.clone())
                    .unwrap_or_default();
                let (name, version, author, description, capabilities) = match &slot.manifest {
                    Ok(manifest) => (
                        manifest.name.clone(),
                        manifest.version.clone(),
                        manifest.author.clone(),
                        manifest.description.clone(),
                        manifest
                            .capabilities
                            .iter()
                            .map(|request| CapabilityInfo {
                                name: request.name.clone(),
                                level: Capability::parse(&request.name).map_or(
                                    cabinetos_protocol::CapabilityLevel::High,
                                    Capability::level,
                                ),
                                granted: granted.contains(&request.name),
                                reason: request.reason.clone(),
                                roots: request.roots.clone(),
                                hosts: request.hosts.clone(),
                                secrets: request.secrets.clone(),
                            })
                            .collect(),
                    ),
                    Err(_) => (
                        id.clone(),
                        String::new(),
                        String::new(),
                        String::new(),
                        Vec::new(),
                    ),
                };
                PluginInfo {
                    id: id.clone(),
                    name,
                    version,
                    author,
                    description,
                    state: slot.state.clone(),
                    capabilities,
                    commands: slot.commands.clone(),
                }
            })
            .collect()
    }

    /// Starts a plugin again from its folder (its manifest is read again),
    /// and forgets its crashes. A folder installed since the start is found
    /// too.
    pub fn reload(&self, id: &str) -> Result<(), PluginError> {
        let folder = self.inner.config.plugins_dir.join(id);
        let known = lock(&self.inner.slots).contains_key(id);
        if !known && !folder.join(manifest::MANIFEST_FILE).is_file() {
            return Err(PluginError::new(
                ErrorCode::NoSuchPlugin,
                format!("no plugin `{id}`"),
            ));
        }
        let settings = lock(&self.inner.settings)
            .get(id)
            .cloned()
            .unwrap_or_default();
        let manifest = manifest::read(&folder, &self.inner.config.core_version);
        let mut slots = lock(&self.inner.slots);
        let slot = slots.entry(id.to_owned()).or_insert_with(|| Slot {
            dir: folder.clone(),
            manifest: manifest.clone(),
            state: PluginState::Loading,
            generation: 0,
            worker: None,
            loaded: None,
            commands: Vec::new(),
            crashes: VecDeque::new(),
            applied: None,
        });
        slot.manifest = manifest;
        slot.crashes.clear();
        tracing::info!(plugin_id = id, "reloading the plugin");
        self.inner.evaluate(id, slot, &settings);
        Ok(())
    }

    /// Whether the plugin is installed.
    #[must_use]
    pub fn contains(&self, id: &str) -> bool {
        lock(&self.inner.slots).contains_key(id)
    }

    /// Forgets a plugin whose files are about to be removed: stops its
    /// instance and unregisters its commands. `list_plugins` no longer shows
    /// it. Returns whether the host knew it.
    pub fn remove(&self, id: &str) -> bool {
        let mut slots = lock(&self.inner.slots);
        let Some(mut slot) = slots.remove(id) else {
            return false;
        };
        self.inner.stop(id, &mut slot);
        tracing::info!(plugin_id = id, "plugin removed");
        true
    }

    /// Runs one of a plugin's commands and waits for its JSON result.
    /// Blocking: up to the call deadline.
    pub fn execute(
        &self,
        plugin_id: &str,
        command: &str,
        args: &str,
    ) -> Result<String, PluginError> {
        let (reply, answer) = mpsc::sync_channel(1);
        let activity = self.send(
            plugin_id,
            command,
            Call::Command {
                id: command.to_owned(),
                args: args.to_owned(),
                reply,
                cause: tracing::Span::current(),
            },
        )?;
        match self.wait(plugin_id, &activity, &answer) {
            Reply::Done(Ok(result)) => Ok(result),
            Reply::Done(Err(message)) => Err(PluginError::new(
                ErrorCode::PluginError,
                format!("{command} failed: {message}"),
            )),
            Reply::Crashed(message) => Err(PluginError::new(
                ErrorCode::PluginError,
                format!("the plugin {plugin_id} crashed while running {command}: {message}"),
            )),
        }
    }

    /// Queues a command for a running plugin that registered it.
    fn send(
        &self,
        plugin_id: &str,
        command: &str,
        call: Call,
    ) -> Result<Arc<Activity>, PluginError> {
        let slots = lock(&self.inner.slots);
        let slot = slots.get(plugin_id).ok_or_else(|| {
            PluginError::new(ErrorCode::NoSuchPlugin, format!("no plugin `{plugin_id}`"))
        })?;
        let worker = slot
            .worker
            .as_ref()
            .filter(|_| slot.state == PluginState::Active)
            .ok_or_else(|| {
                PluginError::new(
                    ErrorCode::PluginError,
                    format!(
                        "the plugin {plugin_id} is not running ({})",
                        describe(&slot.state)
                    ),
                )
            })?;
        if !slot.commands.iter().any(|registered| registered == command) {
            return Err(PluginError::new(
                ErrorCode::UnknownCommand,
                format!("the plugin {plugin_id} has no command {command}"),
            ));
        }
        worker.calls.send(call).map_err(|_| {
            PluginError::new(
                ErrorCode::PluginError,
                format!("the plugin {plugin_id} stopped"),
            )
        })?;
        Ok(Arc::clone(&worker.activity))
    }

    /// Waits for the reply to a command. The worker's own deadline ends a
    /// call that runs WebAssembly too long; a call still running
    /// [`STUCK_GRACE`] after that is stuck in a host function, and the
    /// instance is given up on: crashed, its thread left to end on its own.
    fn wait<T>(
        &self,
        plugin_id: &str,
        activity: &Activity,
        answer: &mpsc::Receiver<Reply<T>>,
    ) -> Reply<T> {
        // Time spent waiting for the network (`http-request`) is taken out
        // of `busy_for`: it counts against no deadline.
        let limit = self.inner.config.call_timeout + STUCK_GRACE;
        loop {
            match answer.recv_timeout(Duration::from_millis(100)) {
                Ok(reply) => return reply,
                // The instance ended before it took this call: an earlier
                // call crashed it, or it was stopped.
                Err(RecvTimeoutError::Disconnected) => {
                    return Reply::Crashed("it stopped before it answered".to_owned());
                }
                Err(RecvTimeoutError::Timeout) => {
                    if activity.busy_for().is_some_and(|busy| busy > limit) {
                        let message = format!(
                            "it did not answer within {} ms",
                            self.inner.config.call_timeout.as_millis()
                        );
                        let mut slots = lock(&self.inner.slots);
                        if let Some(slot) = slots.get_mut(plugin_id) {
                            slot.generation += 1;
                            tracing::error!(plugin_id, error = %message, "the plugin is stuck; it is given up on");
                            self.inner.crashed(plugin_id, slot, message.clone());
                        }
                        return Reply::Crashed(message);
                    }
                }
            }
        }
    }

    /// Asks every running plugin with `jobs:intercept` about a job about to
    /// start. The first refusal wins; a plugin that crashes or is too slow
    /// counts as allowing it. Blocking: up to the `before-job` deadline.
    pub fn before_job(
        &self,
        job_id: u64,
        kind: &JobKind,
        sources: &[String],
        destination: Option<&str>,
        files_total: u64,
        bytes_total: u64,
    ) -> Result<(), String> {
        let kind = match kind {
            JobKind::Copy => bindings::cabinetos::plugin::types::JobKind::Copy,
            JobKind::Move => bindings::cabinetos::plugin::types::JobKind::Move,
            JobKind::Delete { permanent: false } => {
                bindings::cabinetos::plugin::types::JobKind::Delete
            }
            JobKind::Delete { permanent: true } => {
                bindings::cabinetos::plugin::types::JobKind::DeletePermanently
            }
            // The WIT's job kinds predate `steps`: a plugin sees the
            // weightiest thing the steps do. Anything to the Recycle Bin
            // is a delete; renames and restores are a move; creating
            // folders and files alone is a copy.
            JobKind::Steps { steps } => {
                use cabinetos_protocol::JobStep;
                if steps
                    .iter()
                    .any(|step| matches!(step, JobStep::Recycle { .. }))
                {
                    bindings::cabinetos::plugin::types::JobKind::Delete
                } else if steps
                    .iter()
                    .any(|step| matches!(step, JobStep::Rename { .. } | JobStep::Restore { .. }))
                {
                    bindings::cabinetos::plugin::types::JobKind::Move
                } else {
                    bindings::cabinetos::plugin::types::JobKind::Copy
                }
            }
        };
        let summary = bindings::JobSummary {
            job_id,
            kind,
            sources: sources.to_vec(),
            destination: destination.map(ToOwned::to_owned),
            files_total,
            bytes_total,
        };
        let mut waiting = Vec::new();
        {
            let slots = lock(&self.inner.slots);
            for (id, slot) in slots.iter() {
                let intercepts = slot
                    .loaded
                    .as_ref()
                    .is_some_and(|loaded| loaded.granted.contains(&Capability::JobsIntercept));
                if let (true, PluginState::Active, Some(worker)) =
                    (intercepts, &slot.state, &slot.worker)
                {
                    let (reply, answer) = mpsc::sync_channel(1);
                    if worker
                        .calls
                        .send(Call::BeforeJob {
                            job: summary.clone(),
                            reply,
                            cause: tracing::Span::current(),
                        })
                        .is_ok()
                    {
                        waiting.push((id.clone(), answer));
                    }
                }
            }
        }
        let deadline =
            Instant::now() + self.inner.config.before_job_timeout + Duration::from_secs(1);
        for (id, answer) in waiting {
            let left = deadline.saturating_duration_since(Instant::now());
            match answer.recv_timeout(left) {
                Ok(Reply::Done(JobVerdict::Deny(reason))) => {
                    tracing::info!(plugin_id = %id, job_id, reason = %reason, "a plugin stopped a job");
                    return Err(format!("denied by plugin {id}: {reason}"));
                }
                Ok(Reply::Done(JobVerdict::Allow)) => {}
                Ok(Reply::Crashed(message)) => {
                    tracing::warn!(plugin_id = %id, job_id, error = %message, "a plugin crashed judging a job; the job goes on");
                }
                Err(_) => {
                    tracing::warn!(plugin_id = %id, job_id, "a plugin was too slow judging a job; the job goes on");
                }
            }
        }
        Ok(())
    }

    /// Tells the plugins that may read `path` (`fs:read`) that a pane opened
    /// it, with the path as the plugin sees it (`/C:/...`). Never waits.
    pub fn listing_opened(&self, path: &str, entries: u32) {
        let lowered = path.to_lowercase();
        let slots = lock(&self.inner.slots);
        for slot in slots.values() {
            let (Some(loaded), Some(worker), PluginState::Active) =
                (&slot.loaded, &slot.worker, &slot.state)
            else {
                continue;
            };
            let readable = loaded
                .read_roots
                .iter()
                .chain(&loaded.write_roots)
                .any(|root| {
                    let root = root.display().to_string().to_lowercase();
                    let root = root.trim_end_matches('\\');
                    lowered == root || lowered.starts_with(&format!("{root}\\"))
                });
            if readable && worker.activity.queued.load(Ordering::Relaxed) < MAX_QUEUED_NOTIFICATIONS
            {
                worker.activity.queued.fetch_add(1, Ordering::Relaxed);
                let _ = worker.calls.send(Call::Listing {
                    path: sandbox::guest_path(Path::new(path)),
                    entries,
                    cause: tracing::Span::current(),
                });
            }
        }
    }

    /// Why a command a plugin declared is not registered now, such as
    /// "its plugin crashy crashed: ...". `None` when no plugin declares it.
    #[must_use]
    pub fn explain_missing(&self, command: &str) -> Option<String> {
        let slots = lock(&self.inner.slots);
        slots.iter().find_map(|(id, slot)| {
            let declared = slot.manifest.as_ref().is_ok_and(|manifest| {
                manifest
                    .commands
                    .iter()
                    .any(|declared| declared.id == command)
            });
            declared.then(|| format!("its plugin {id} is {}", describe(&slot.state)))
        })
    }

    /// Stops every plugin. Their threads end on their own.
    pub fn shutdown(&self) {
        self.inner.stopping.store(true, Ordering::SeqCst);
        let mut slots = lock(&self.inner.slots);
        for (id, slot) in slots.iter_mut() {
            self.inner.stop(id, slot);
        }
        self.inner.running_changed.notify_all();
    }
}

impl Drop for PluginHost {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// A state in words, for messages.
fn describe(state: &PluginState) -> String {
    match state {
        PluginState::Loading => "still starting".to_owned(),
        PluginState::Active => "running".to_owned(),
        PluginState::Disabled => "turned off".to_owned(),
        PluginState::NeedsReview { missing } => {
            format!("waiting for {} to be granted", missing.join(", "))
        }
        PluginState::Failed { message } => format!("failed: {message}"),
        PluginState::Crashed { message, .. } => format!("crashed: {message}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folders_come_from_the_flag_then_the_variable_then_local_app_data() {
        let flag = Some(PathBuf::from(r"C:\flag"));
        let env = Some(OsString::from(r"C:\env"));
        let local = Some(OsString::from(r"C:\Users\me\AppData\Local"));
        assert_eq!(
            resolve_dir(flag, env.clone(), local.clone(), "plugins"),
            PathBuf::from(r"C:\flag")
        );
        assert_eq!(
            resolve_dir(None, env, local.clone(), "plugins"),
            PathBuf::from(r"C:\env")
        );
        assert_eq!(
            resolve_dir(None, Some(OsString::new()), local, "plugins-data"),
            PathBuf::from(r"C:\Users\me\AppData\Local\CabinetOS\plugins-data")
        );
        assert!(resolve_dir(None, None, None, "plugins").starts_with(std::env::temp_dir()));
    }

    #[test]
    fn states_read_as_words() {
        assert_eq!(describe(&PluginState::Disabled), "turned off");
        assert_eq!(
            describe(&PluginState::NeedsReview {
                missing: vec!["fs:read".to_owned(), "cmd:register".to_owned()]
            }),
            "waiting for fs:read, cmd:register to be granted"
        );
    }
}
