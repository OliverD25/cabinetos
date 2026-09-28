//! Settings, commands and keybindings: the core's side of `cabinetos.json`.
//!
//! One [`Settings`] serves every connection. It owns the configuration file
//! (through [`ConfigStore`]), the command registry and the compiled keymap.
//! Requests read a snapshot and never wait for the disk. Changes, whether an
//! edit saved in an editor or a `set_keybinding` request, go through the
//! store on a blocking thread, and every connection that said `hello` hears
//! about them as events: `config_changed`, then `keymap_changed` when the
//! keymap differs, or `config_error` when the file cannot be used.
//!
//! Plugins add and remove their commands while the core runs. Locks are
//! always taken in one order: the store, then the registry. `Settings`
//! never calls into the plugin host, so the host may call in here while it
//! holds its own lock.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError, RwLock, RwLockReadGuard, RwLockWriteGuard};

use cabinetos_commands::{
    Command, CommandRegistry, Compiled, KeySequence, Keymap, KeymapError, command_info, compile,
    search,
};
use cabinetos_config::{
    Config, ConfigError, ConfigStore, ConfigWatcher, LogLevel, Opened, PluginSettings, Rejection,
    Reload, UpdateError, WatchEvent,
};
use cabinetos_plugins::PluginCommand;
use cabinetos_protocol::{
    CommandSource, CommandTarget, ErrorCode, Event, PROTOCOL_VERSION, Response,
};
use serde_json::{Value, json};
use tokio::sync::watch;

use crate::CORE_VERSION;
use crate::events::EventHub;

/// The settings in effect.
#[derive(Debug)]
pub(crate) struct Snapshot {
    pub(crate) config: Config,
    pub(crate) keymap: Keymap,
}

/// The configuration, the commands and the keymap of this core.
pub(crate) struct Settings {
    /// The core's commands and the running plugins' commands.
    registry: RwLock<CommandRegistry>,
    path: PathBuf,
    /// Held while the file is read or written, so changes apply in order.
    store: Mutex<ConfigStore>,
    current: watch::Sender<Arc<Snapshot>>,
    events: Arc<EventHub>,
}

impl Settings {
    /// Opens the configuration file at `path` (creating it on first run) and
    /// compiles the keymap. A file with an error leaves the defaults in
    /// effect until it is fixed. Blocking: it reads and may write the file.
    pub(crate) fn open(path: PathBuf, events: Arc<EventHub>) -> Arc<Self> {
        let registry = CommandRegistry::core();
        let mut compiled = None;
        let (store, opened) = ConfigStore::open(path.clone(), |config| {
            compiled = Some(compile(&registry, &config.overrides())?);
            Ok(())
        });
        match &opened {
            Opened::Loaded => tracing::info!(path = %path.display(), "configuration loaded"),
            Opened::Created => tracing::info!(
                path = %path.display(),
                "configuration file created with the defaults"
            ),
            Opened::NotCreated(error) => tracing::warn!(
                path = %path.display(),
                %error,
                "cannot create the configuration file; using the defaults"
            ),
            Opened::Invalid(error) => tracing::warn!(
                path = %path.display(),
                %error,
                "the configuration file has an error; using the defaults until it is fixed"
            ),
        }
        // Without a loaded file the store holds the defaults, and the
        // defaults always compile (a test in cabinetos-commands says so).
        let compiled = compiled.unwrap_or_else(|| {
            compile(&registry, &store.config().overrides()).expect("the default keymap compiles")
        });
        log_warnings(&compiled.warnings);
        apply_log_level(store.config().logging.level);
        let snapshot = Snapshot {
            config: store.config().clone(),
            keymap: compiled.keymap,
        };
        let (current, _) = watch::channel(Arc::new(snapshot));
        Arc::new(Self {
            registry: RwLock::new(registry),
            path,
            store: Mutex::new(store),
            current,
            events,
        })
    }

    /// Starts watching the file, so saved edits take effect at once. The
    /// watching stops when the returned watcher drops.
    pub(crate) fn watch(self: &Arc<Self>) -> Option<ConfigWatcher> {
        let settings = Arc::clone(self);
        match ConfigWatcher::start(&self.path, move |event| settings.on_watch(event)) {
            Ok(watcher) => Some(watcher),
            Err(error) => {
                tracing::warn!(
                    %error,
                    "cannot watch the configuration file; edits take effect after a restart"
                );
                None
            }
        }
    }

    /// The configuration file.
    pub(crate) fn path(&self) -> &Path {
        &self.path
    }

    /// The settings in effect now.
    pub(crate) fn snapshot(&self) -> Arc<Snapshot> {
        self.current.borrow().clone()
    }

    /// The settings in effect, now and after every change.
    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Snapshot>> {
        self.current.subscribe()
    }

    /// The reply to `get_config`.
    pub(crate) fn get_config(&self) -> Response {
        match serde_json::to_value(&self.snapshot().config) {
            Ok(config) => Response::Config {
                path: self.path.display().to_string(),
                config,
            },
            Err(error) => error_reply(ErrorCode::Internal, error.to_string()),
        }
    }

    /// The reply to `get_value`: one setting in effect, by its dotted path.
    pub(crate) fn get_value(&self, path: &str) -> Response {
        let config = match serde_json::to_value(&self.snapshot().config) {
            Ok(config) => config,
            Err(error) => return error_reply(ErrorCode::Internal, error.to_string()),
        };
        let mut value = &config;
        for key in path.split('.') {
            match value.get(key) {
                Some(inner) => value = inner,
                None => {
                    return error_reply(
                        ErrorCode::ConfigError,
                        format!("there is no setting `{path}`"),
                    );
                }
            }
        }
        Response::Value {
            value: value.clone(),
        }
    }

    /// The reply to `set_value`: changes one setting by its dotted path,
    /// checked as a file would be, and writes the file. Blocking.
    pub(crate) fn set_value(&self, path: &str, value: Value) -> Response {
        let mut store = self.lock_store();
        let mut compiled = None;
        let result = store.set_value(path, value, |config| {
            compiled = Some(compile(&self.registry(), &config.overrides())?);
            Ok(())
        });
        match result {
            Ok(changed) => {
                if !changed.is_empty() {
                    self.apply(store.config().clone(), compiled, changed);
                }
                Response::Ok
            }
            Err(UpdateError::Rejected(Rejection { message, .. })) => {
                error_reply(ErrorCode::ConfigError, message)
            }
            Err(UpdateError::FileHasError(error)) => self.file_has_error(&error),
            Err(UpdateError::Io(error)) => self.cannot_write(&error),
        }
    }

    /// The reply to `get_keymap`.
    pub(crate) fn get_keymap(&self) -> Response {
        Response::Keymap(self.snapshot().keymap.to_wire())
    }

    /// The reply to `list_commands`.
    pub(crate) fn list_commands(&self) -> Response {
        let snapshot = self.snapshot();
        Response::Commands {
            commands: self
                .registry()
                .commands()
                .iter()
                .map(|command| command_info(command, &snapshot.keymap))
                .collect(),
        }
    }

    /// The reply to `search_commands`.
    pub(crate) fn search_commands(&self, query: &str, limit: u32) -> Response {
        Response::SearchResults {
            hits: search(
                &self.registry(),
                query,
                usize::try_from(limit).unwrap_or(usize::MAX),
            ),
        }
    }

    /// The reply to `execute_command` for a command that is not a plugin's
    /// (the connection sends those to the plugin host): the core runs its
    /// own commands and hands the UI's back. Of the core's commands only
    /// `help.about` exists yet; the file operations arrive with their phase.
    pub(crate) fn execute(&self, command: &str) -> Response {
        let Some(target) = self.registry().get(command).map(|found| found.target) else {
            return unknown_command(command);
        };
        match (target, command) {
            (CommandTarget::Ui, _) => Response::CommandRouted {
                target: CommandTarget::Ui,
            },
            (CommandTarget::Core, "help.about") => Response::CommandResult {
                result: json!({
                    "name": "CabinetOS",
                    "core_version": CORE_VERSION,
                    "protocol_version": PROTOCOL_VERSION,
                    "config_path": self.path.display().to_string(),
                }),
            },
            (CommandTarget::Core, _) => error_reply(
                ErrorCode::NotImplemented,
                format!("{command} is not implemented yet; it arrives in a later phase"),
            ),
        }
    }

    /// The reply to `set_keybinding`: binds `command` to `keys` (an empty
    /// string: no binding), writes the file and answers with the new keymap.
    /// Blocking.
    pub(crate) fn set_keybinding(&self, command: &str, keys: &str) -> Response {
        let Some(immutable) = self.registry().get(command).map(|found| found.immutable) else {
            return unknown_command(command);
        };
        if immutable {
            return keymap_failure(&KeymapError::ImmutableCommand {
                command: command.to_owned(),
                entry: None,
            });
        }
        let keys = match keys.trim() {
            "" => None,
            text => match text.parse::<KeySequence>() {
                Ok(keys) => Some(keys),
                Err(error) => return error_reply(ErrorCode::InvalidKeys, error.to_string()),
            },
        };
        let mut store = self.lock_store();
        let mut compiled = None;
        let result = store.set_keybinding(command, keys, |config| {
            compiled = Some(compile(&self.registry(), &config.overrides())?);
            Ok(())
        });
        self.finish_update(&store, result, compiled)
    }

    /// The reply to `reset_keybinding`: removes the user's entries for
    /// `command`, so its default keys apply again. Blocking.
    pub(crate) fn reset_keybinding(&self, command: &str) -> Response {
        let has_entries = || {
            self.snapshot()
                .config
                .keybindings
                .iter()
                .any(|entry| entry.command == command)
        };
        // Entries for a command nobody registers (a plugin that was removed)
        // can still be cleaned up.
        if self.registry().get(command).is_none() && !has_entries() {
            return unknown_command(command);
        }
        let mut store = self.lock_store();
        let mut compiled = None;
        let result = store.unset_keybinding(command, |config| {
            compiled = Some(compile(&self.registry(), &config.overrides())?);
            Ok(())
        });
        self.finish_update(&store, result, compiled)
    }

    /// Changes the settings of one plugin (`plugins.<id>`) and writes the
    /// file. Answers `ok`; the caller then lets the plugin host act on it.
    /// Blocking.
    pub(crate) fn update_plugin(
        &self,
        plugin_id: &str,
        change: impl FnOnce(&mut PluginSettings),
    ) -> Response {
        let mut store = self.lock_store();
        let mut compiled = None;
        let result = store.update(
            |config| {
                change(config.plugins.entry(plugin_id.to_owned()).or_default());
                Ok(())
            },
            |config| {
                compiled = Some(compile(&self.registry(), &config.overrides())?);
                Ok(())
            },
        );
        match result {
            Ok(changed) => {
                if !changed.is_empty() {
                    self.apply(store.config().clone(), compiled, changed);
                }
                Response::Ok
            }
            Err(error) => self.update_failure(error),
        }
    }

    /// The plugin that registered `command`, if a plugin did.
    pub(crate) fn plugin_of(&self, command: &str) -> Option<String> {
        match &self.registry().get(command)?.source {
            CommandSource::Plugin { id, .. } => Some(id.clone()),
            CommandSource::Core => None,
        }
    }

    /// Replaces the commands of one plugin (none: it stopped or crashed)
    /// and compiles the keymap again. A default key that clashes with a
    /// binding in use is dropped with a warning: the command stays, without
    /// keys. Called on a plugin host thread.
    pub(crate) fn set_plugin_commands(
        &self,
        plugin_id: &str,
        plugin_name: &str,
        commands: &[PluginCommand],
    ) {
        let store = self.lock_store();
        let overrides = store.config().overrides();
        let compiled = {
            let mut registry = self.registry_mut();
            registry.unregister_plugin(plugin_id);
            for command in commands {
                add_plugin_command(&mut registry, &overrides, plugin_id, plugin_name, command);
            }
            compile(&registry, &overrides)
        };
        match compiled {
            Ok(compiled) => {
                let previous = self.snapshot();
                if compiled.keymap != previous.keymap {
                    log_warnings(&compiled.warnings);
                    let wire = compiled.keymap.to_wire();
                    self.current.send_replace(Arc::new(Snapshot {
                        config: store.config().clone(),
                        keymap: compiled.keymap,
                    }));
                    self.publish(Event::KeymapChanged { keymap: wire });
                }
            }
            Err(error) => tracing::warn!(
                plugin_id,
                %error,
                "the keybindings do not fit the plugin's commands; the keymap in use stays"
            ),
        }
        tracing::info!(
            plugin_id,
            commands = commands.len(),
            "plugin commands registered"
        );
    }

    fn finish_update(
        &self,
        store: &ConfigStore,
        result: Result<Vec<String>, UpdateError<KeymapError>>,
        compiled: Option<Compiled>,
    ) -> Response {
        match result {
            Ok(changed) => {
                if !changed.is_empty() {
                    self.apply(store.config().clone(), compiled, changed);
                }
                self.get_keymap()
            }
            Err(error) => self.update_failure(error),
        }
    }

    fn update_failure(&self, error: UpdateError<KeymapError>) -> Response {
        match error {
            UpdateError::Rejected(error) => keymap_failure(&error),
            UpdateError::FileHasError(error) => self.file_has_error(&error),
            UpdateError::Io(error) => self.cannot_write(&error),
        }
    }

    fn file_has_error(&self, error: &ConfigError) -> Response {
        error_reply(
            ErrorCode::ConfigError,
            format!(
                "{} has an error to fix first ({error}); nothing was changed",
                self.path.display()
            ),
        )
    }

    fn cannot_write(&self, error: &std::io::Error) -> Response {
        error_reply(
            ErrorCode::ConfigError,
            format!("cannot write {}: {error}", self.path.display()),
        )
    }

    fn on_watch(&self, event: WatchEvent) {
        match event {
            WatchEvent::Changed => self.reload(),
            WatchEvent::Failed(message) => {
                tracing::warn!(error = %message, "stopped watching the configuration file");
                self.publish(Event::ConfigError {
                    line: None,
                    column: None,
                    message: format!(
                        "the core stopped watching {}: {message}; edits take effect after a restart",
                        self.path.display()
                    ),
                });
            }
        }
    }

    /// Reads the file after a change on disk. Runs on the watcher's thread.
    fn reload(&self) {
        let mut store = self.lock_store();
        let mut compiled = None;
        let outcome = store.reload(|config| {
            compiled = Some(compile(&self.registry(), &config.overrides())?);
            Ok(())
        });
        match outcome {
            Reload::Unchanged => {}
            Reload::Changed(changed) => self.apply(store.config().clone(), compiled, changed),
            Reload::Invalid(error) => {
                tracing::warn!(
                    %error,
                    "the configuration file has an error; the settings in use stay"
                );
                self.publish(config_error_event(error));
            }
        }
    }

    /// Makes `config` the settings in effect and tells every connection.
    /// `compiled` is its keymap, when it was compiled; otherwise the keymap
    /// in effect stays. The caller holds the store lock, so changes are
    /// applied in the order they were made.
    fn apply(&self, config: Config, compiled: Option<Compiled>, changed: Vec<String>) {
        let previous = self.snapshot();
        let keymap = match compiled {
            Some(compiled) => {
                log_warnings(&compiled.warnings);
                compiled.keymap
            }
            None => previous.keymap.clone(),
        };
        let keymap_changed = keymap != previous.keymap;
        if config.logging.level != previous.config.logging.level {
            apply_log_level(config.logging.level);
        }
        tracing::info!(?changed, keymap_changed, "configuration changed");
        let wire_keymap = keymap_changed.then(|| keymap.to_wire());
        self.current
            .send_replace(Arc::new(Snapshot { config, keymap }));
        // `changed` may be empty: the file was fixed back to the settings in
        // effect, and a client that showed the error must hear so.
        self.publish(Event::ConfigChanged { changed });
        if let Some(keymap) = wire_keymap {
            self.publish(Event::KeymapChanged { keymap });
        }
    }

    fn publish(&self, event: Event) {
        self.events.publish(event);
    }

    fn lock_store(&self) -> MutexGuard<'_, ConfigStore> {
        self.store.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn registry(&self) -> RwLockReadGuard<'_, CommandRegistry> {
        self.registry.read().unwrap_or_else(PoisonError::into_inner)
    }

    fn registry_mut(&self) -> RwLockWriteGuard<'_, CommandRegistry> {
        self.registry
            .write()
            .unwrap_or_else(PoisonError::into_inner)
    }
}

/// Registers one plugin command; its default keys are dropped when they
/// clash with a binding in use.
fn add_plugin_command(
    registry: &mut CommandRegistry,
    overrides: &[cabinetos_commands::Override],
    plugin_id: &str,
    plugin_name: &str,
    command: &PluginCommand,
) {
    let mut entry = Command {
        id: command.id.clone(),
        category: command.category.clone(),
        title: command.title.clone(),
        // The host checked them against the key grammar already.
        default_keys: command
            .default_keys
            .iter()
            .filter_map(|keys| keys.parse().ok())
            .collect(),
        source: CommandSource::Plugin {
            id: plugin_id.to_owned(),
            name: plugin_name.to_owned(),
        },
        target: CommandTarget::Core,
        when: None,
        immutable: false,
    };
    if registry.register(entry.clone()).is_err() {
        tracing::warn!(
            plugin_id,
            command = %entry.id,
            "another command has this ID already; the plugin's command is left out"
        );
        return;
    }
    if !entry.default_keys.is_empty() && compile(registry, overrides).is_err() {
        registry.unregister(&entry.id);
        tracing::warn!(
            plugin_id,
            command = %entry.id,
            keys = ?command.default_keys,
            "the plugin's default keys clash with a binding in use; the command has no keys"
        );
        entry.default_keys.clear();
        let _ = registry.register(entry);
    }
}

/// The top-level sections of the file, for a client that missed events and
/// must assume everything changed.
pub(crate) fn every_section() -> Vec<String> {
    match serde_json::to_value(Config::default()) {
        Ok(Value::Object(sections)) => sections
            .keys()
            .filter(|key| key.as_str() != "$schema")
            .cloned()
            .collect(),
        _ => Vec::new(),
    }
}

fn config_error_event(error: ConfigError) -> Event {
    Event::ConfigError {
        line: error.line,
        column: error.column,
        message: error.message,
    }
}

fn apply_log_level(level: LogLevel) {
    if !cabinetos_diag::set_level(level.to_tracing()) {
        tracing::debug!(
            ?level,
            "logging.level is not applied: CABINETOS_LOG sets the log filter"
        );
    }
}

fn log_warnings(warnings: &[String]) {
    for warning in warnings {
        tracing::warn!(%warning, "keybinding entry ignored");
    }
}

fn keymap_failure(error: &KeymapError) -> Response {
    let code = if error.is_immutable() {
        ErrorCode::ImmutableBinding
    } else {
        ErrorCode::KeybindingConflict
    };
    error_reply(code, error.to_string())
}

fn unknown_command(command: &str) -> Response {
    error_reply(
        ErrorCode::UnknownCommand,
        format!("no command `{command}` is registered"),
    )
}

fn error_reply(code: ErrorCode, message: String) -> Response {
    Response::Error { code, message }
}
