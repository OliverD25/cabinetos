//! One plugin instance on a thread of its own. The thread owns the
//! `Store` (no store ever crosses threads) and takes calls from a channel.
//! Every call gets fresh fuel and a wall-clock deadline. Any trap ends the
//! thread and drops the store: the instance is gone, the core goes on.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc::{Receiver, SyncSender};
use std::sync::{Arc, Mutex, PoisonError, Weak};
use std::time::{Duration, Instant};

use wasmtime::component::{Component, ResourceTable};
use wasmtime::{Store, Trap};
use wasmtime_wasi::{WasiCtx, WasiCtxView, WasiView};

use crate::bindings::cabinetos::plugin::host::{self, LogLevel};
use crate::bindings::cabinetos::plugin::types;
use crate::bindings::{Activation, CorePlugin, JobSummary, JobVerdict};
use crate::manifest::Capability;
use crate::sandbox::{self, Limiter, LineLog, Mounts, Stream};
use crate::{HostInner, Loaded, PluginCommand};

/// A call for the instance.
pub(crate) enum Call {
    /// Run a command; the reply is its JSON result or its error text.
    Command {
        id: String,
        args: String,
        reply: SyncSender<Reply<Result<String, String>>>,
    },
    /// Judge a job about to start.
    BeforeJob {
        job: JobSummary,
        reply: SyncSender<Reply<JobVerdict>>,
    },
    /// A folder the plugin may read was opened (no reply).
    Listing { path: String, entries: u32 },
    /// Say goodbye (`deactivate`) and end.
    Stop,
}

/// The answer to a call.
pub(crate) enum Reply<T> {
    Done(T),
    /// The instance trapped during the call; it is gone.
    Crashed(String),
}

/// What the host sees of a worker from outside.
#[derive(Default)]
pub(crate) struct Activity {
    /// Notifications waiting in the channel.
    pub(crate) queued: AtomicUsize,
    /// When the call running now started; `None` between calls.
    busy_since: Mutex<Option<Instant>>,
}

impl Activity {
    /// How long the call running now has been running.
    pub(crate) fn busy_for(&self) -> Option<Duration> {
        self.busy_since
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .map(|since| since.elapsed())
    }

    fn set(&self, since: Option<Instant>) {
        *self
            .busy_since
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = since;
    }
}

/// Runs one call into the instance: the epoch ticker runs, and the host can
/// see how long it takes.
fn timed<T>(inner: &HostInner, activity: &Activity, call: impl FnOnce() -> T) -> T {
    activity.set(Some(Instant::now()));
    let result = inner.guard(call);
    activity.set(None);
    result
}

/// The data of a plugin's store.
pub(crate) struct State {
    wasi: WasiCtx,
    table: ResourceTable,
    limiter: Limiter,
    plugin: Arc<Loaded>,
    /// `register-command` works only while this is set.
    activating: bool,
    registered: Vec<PluginCommand>,
}

impl WasiView for State {
    fn ctx(&mut self) -> WasiCtxView<'_> {
        WasiCtxView {
            ctx: &mut self.wasi,
            table: &mut self.table,
        }
    }
}

impl types::Host for State {}

impl host::Host for State {
    fn register_command(
        &mut self,
        id: String,
        title: String,
        category: String,
        default_keys: Vec<String>,
    ) -> wasmtime::Result<()> {
        let plugin = &self.plugin;
        if !self.activating {
            wasmtime::bail!("register-command may only be called from activate");
        }
        if !plugin.granted.contains(&Capability::CmdRegister) {
            wasmtime::bail!("register-command needs the cmd:register capability");
        }
        if !plugin
            .manifest
            .commands
            .iter()
            .any(|declared| declared.id == id)
        {
            wasmtime::bail!("{id} is not declared in plugin.json, so it cannot be registered");
        }
        for keys in &default_keys {
            if let Err(error) = keys.parse::<cabinetos_commands::KeySequence>() {
                wasmtime::bail!("{id}: {error}");
            }
        }
        if !self.registered.iter().any(|command| command.id == id) {
            self.registered.push(PluginCommand {
                id,
                title,
                category,
                default_keys,
            });
        }
        Ok(())
    }

    fn log(&mut self, level: LogLevel, message: String) -> wasmtime::Result<()> {
        match level {
            LogLevel::Trace => tracing::trace!("{message}"),
            LogLevel::Debug => tracing::debug!("{message}"),
            LogLevel::Info => tracing::info!("{message}"),
            LogLevel::Warn => tracing::warn!("{message}"),
            LogLevel::Error => tracing::error!("{message}"),
        }
        Ok(())
    }

    fn config_get(&mut self, path: String) -> wasmtime::Result<Option<String>> {
        if !self.plugin.granted.contains(&Capability::ConfigRead) {
            tracing::debug!(path = %path, "config-get without config:read: none");
            return Ok(None);
        }
        Ok(self.plugin.services.config_value(&path))
    }

    fn emit(&mut self, name: String, payload: String) -> wasmtime::Result<()> {
        if !self.plugin.granted.contains(&Capability::EventsEmit) {
            tracing::debug!(event = %name, "emit without events:emit: dropped");
            return Ok(());
        }
        self.plugin
            .services
            .publish(cabinetos_protocol::Event::PluginEvent {
                plugin_id: self.plugin.id.clone(),
                name,
                payload,
            });
        Ok(())
    }
}

/// What the thread tells the host.
pub(crate) enum Report {
    /// Running, with the commands it registered.
    Active(Vec<PluginCommand>),
    /// It could not start.
    Failed(String),
    /// It trapped during a call.
    Crashed(String),
}

/// A running instance.
struct Instance {
    store: Store<State>,
    bindings: CorePlugin,
    last_error: Arc<Mutex<Option<String>>>,
}

/// The thread's body: start the instance, then serve calls until a trap,
/// `Stop`, or the host goes away.
pub(crate) fn run(
    host: &Weak<HostInner>,
    plugin: &Arc<Loaded>,
    generation: u64,
    calls: &Receiver<Call>,
    activity: &Arc<Activity>,
) {
    let span = tracing::info_span!("plugin", plugin_id = %plugin.id);
    let _entered = span.enter();
    let Some(inner) = host.upgrade() else { return };
    let mut instance = match start(&inner, plugin) {
        Ok(started) => started,
        Err(message) => {
            tracing::error!(error = %message, "the plugin could not start");
            inner.report(&plugin.id, generation, Report::Failed(message));
            return;
        }
    };
    let registered = std::mem::take(&mut instance.store.data_mut().registered);
    tracing::info!(commands = registered.len(), "the plugin is active");
    inner.report(&plugin.id, generation, Report::Active(registered));
    drop(inner);

    while let Ok(call) = calls.recv() {
        let Some(inner) = host.upgrade() else { return };
        match call {
            Call::Command { id, args, reply } => {
                let result = timed(&inner, activity, || {
                    prepare(&mut instance.store, plugin, plugin.limits.call_timeout);
                    instance
                        .bindings
                        .call_on_command(&mut instance.store, &id, &args)
                });
                match result {
                    Ok(answer) => {
                        let _ = reply.send(Reply::Done(answer));
                    }
                    Err(error) => {
                        let message = crash(
                            &inner,
                            plugin,
                            generation,
                            &instance,
                            &error,
                            plugin.limits.call_timeout,
                        );
                        let _ = reply.send(Reply::Crashed(message));
                        return;
                    }
                }
            }
            Call::BeforeJob { job, reply } => {
                let result = timed(&inner, activity, || {
                    prepare(
                        &mut instance.store,
                        plugin,
                        plugin.limits.before_job_timeout,
                    );
                    instance.bindings.call_before_job(&mut instance.store, &job)
                });
                match result {
                    Ok(verdict) => {
                        let _ = reply.send(Reply::Done(verdict));
                    }
                    Err(error) => {
                        let message = crash(
                            &inner,
                            plugin,
                            generation,
                            &instance,
                            &error,
                            plugin.limits.before_job_timeout,
                        );
                        let _ = reply.send(Reply::Crashed(message));
                        return;
                    }
                }
            }
            Call::Listing { path, entries } => {
                activity.queued.fetch_sub(1, Ordering::Relaxed);
                let result = timed(&inner, activity, || {
                    prepare(&mut instance.store, plugin, plugin.limits.call_timeout);
                    instance
                        .bindings
                        .call_on_listing_opened(&mut instance.store, &path, entries)
                });
                if let Err(error) = result {
                    crash(
                        &inner,
                        plugin,
                        generation,
                        &instance,
                        &error,
                        plugin.limits.call_timeout,
                    );
                    return;
                }
            }
            Call::Stop => {
                let _ = timed(&inner, activity, || {
                    prepare(&mut instance.store, plugin, Duration::from_secs(1));
                    instance.bindings.call_deactivate(&mut instance.store)
                });
                tracing::info!("the plugin stopped");
                return;
            }
        }
    }
}

/// Compiles, instantiates and activates the plugin.
fn start(inner: &HostInner, plugin: &Arc<Loaded>) -> Result<Instance, String> {
    let component = Component::from_file(&inner.engine, &plugin.wasm)
        .map_err(|error| format!("cannot load {}: {error:#}", plugin.wasm.display()))?;
    std::fs::create_dir_all(&plugin.data_dir)
        .map_err(|error| format!("cannot create {}: {error}", plugin.data_dir.display()))?;
    let last_error = Arc::new(Mutex::new(None));
    let mounts = Mounts {
        data_dir: plugin.data_dir.clone(),
        read: plugin.read_roots.clone(),
        write: plugin.write_roots.clone(),
    };
    let wasi = sandbox::context(
        &mounts,
        LineLog::new(Stream::Stdout, Arc::clone(&last_error)),
        LineLog::new(Stream::Stderr, Arc::clone(&last_error)),
    )?;
    let mut store = Store::new(
        &inner.engine,
        State {
            wasi,
            table: ResourceTable::new(),
            limiter: Limiter {
                memory_bytes: plugin.limits.memory_bytes,
            },
            plugin: Arc::clone(plugin),
            activating: false,
            registered: Vec::new(),
        },
    );
    store.limiter(|state| &mut state.limiter);
    store.epoch_deadline_trap();

    let timeout = plugin.limits.call_timeout;
    let fail = |step: &str, error: &wasmtime::Error, last: &Arc<Mutex<Option<String>>>| {
        format!(
            "{step} failed: {}",
            summary(error, timeout, plugin.limits.fuel, last)
        )
    };
    let bindings = inner
        .guard(|| {
            prepare(&mut store, plugin, timeout);
            CorePlugin::instantiate(&mut store, &component, &inner.linker)
        })
        .map_err(|error| fail("instantiating", &error, &last_error))?;
    let info = inner
        .guard(|| {
            prepare(&mut store, plugin, timeout);
            bindings.call_info(&mut store)
        })
        .map_err(|error| fail("info", &error, &last_error))?;
    if info.id != plugin.manifest.id || info.version != plugin.manifest.version {
        return Err(format!(
            "the component says it is {} {}, but plugin.json says {} {}",
            info.id, info.version, plugin.manifest.id, plugin.manifest.version
        ));
    }
    let activation = Activation {
        core_version: inner.config.core_version.clone(),
        data_dir: sandbox::DATA_DIR.to_owned(),
        read_roots: plugin
            .read_roots
            .iter()
            .map(|root| sandbox::guest_path(root))
            .collect(),
        write_roots: plugin
            .write_roots
            .iter()
            .map(|root| sandbox::guest_path(root))
            .collect(),
    };
    store.data_mut().activating = true;
    let activated = inner
        .guard(|| {
            prepare(&mut store, plugin, timeout);
            bindings.call_activate(&mut store, &activation)
        })
        .map_err(|error| fail("activate", &error, &last_error))?;
    store.data_mut().activating = false;
    activated.map_err(|message| format!("activate refused: {message}"))?;
    Ok(Instance {
        store,
        bindings,
        last_error,
    })
}

/// Fresh fuel and a fresh deadline for the next call.
fn prepare(store: &mut Store<State>, plugin: &Loaded, timeout: Duration) {
    let _ = store.set_fuel(plugin.limits.fuel);
    let ticks = u64::try_from(timeout.as_millis().div_ceil(crate::EPOCH_TICK.as_millis()))
        .unwrap_or(u64::MAX);
    store.set_epoch_deadline(ticks.max(1));
}

/// Logs the trap, tells the host, and returns the message for the caller.
fn crash(
    inner: &HostInner,
    plugin: &Loaded,
    generation: u64,
    instance: &Instance,
    error: &wasmtime::Error,
    timeout: Duration,
) -> String {
    let message = summary(error, timeout, plugin.limits.fuel, &instance.last_error);
    tracing::error!(error = %message, details = ?error, "the plugin crashed; its instance is dropped");
    inner.report(&plugin.id, generation, Report::Crashed(message.clone()));
    message
}

/// One line for the user: what went wrong, and what the plugin last printed
/// to stderr (a Rust panic prints its reason there).
fn summary(
    error: &wasmtime::Error,
    timeout: Duration,
    fuel: u64,
    last_error: &Arc<Mutex<Option<String>>>,
) -> String {
    let base = match error.downcast_ref::<Trap>() {
        Some(Trap::Interrupt) => format!(
            "it did not finish within {} ms (wasm trap: interrupt)",
            timeout.as_millis()
        ),
        Some(Trap::OutOfFuel) => {
            format!("it used up its fuel of {fuel} units (wasm trap: all fuel consumed)")
        }
        // Its text starts with "wasm trap:" already.
        Some(trap) => trap.to_string(),
        None => error.root_cause().to_string(),
    };
    match last_error
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .take()
    {
        Some(said) => format!("{base}; it said: {said}"),
        None => base,
    }
}
