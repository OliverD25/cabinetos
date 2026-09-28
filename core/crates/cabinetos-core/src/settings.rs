//! Settings, commands and keybindings: the core's side of `cabinetos.json`.
//!
//! One [`Settings`] serves every connection. It owns the configuration file
//! (through [`ConfigStore`]), the command registry and the compiled keymap.
//! Requests read a snapshot and never wait for the disk. Changes, whether an
//! edit saved in an editor or a `set_keybinding` request, go through the
//! store on a blocking thread, and every connection that said `hello` hears
//! about them as events: `config_changed`, then `keymap_changed` when the
//! keymap differs, or `config_error` when the file cannot be used.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use cabinetos_commands::{
    CommandRegistry, Compiled, KeySequence, Keymap, KeymapError, command_info, compile, search,
};
use cabinetos_config::{
    Config, ConfigError, ConfigStore, ConfigWatcher, LogLevel, Opened, Reload, UpdateError,
    WatchEvent,
};
use cabinetos_protocol::{
    CommandTarget, Envelope, ErrorCode, Event, PROTOCOL_VERSION, RequestId, Response,
};
use serde_json::{Value, json};
use tokio::sync::{broadcast, watch};

use crate::CORE_VERSION;

/// Configuration events a connection may fall behind by before it misses
/// some. Each is one saved edit, so this is never reached in practice.
const EVENT_BUFFER: usize = 256;

/// The settings in effect.
#[derive(Debug)]
pub(crate) struct Snapshot {
    pub(crate) config: Config,
    pub(crate) keymap: Keymap,
}

/// The configuration, the commands and the keymap of this core.
pub(crate) struct Settings {
    registry: CommandRegistry,
    path: PathBuf,
    /// Held while the file is read or written, so changes apply in order.
    store: Mutex<ConfigStore>,
    current: watch::Sender<Arc<Snapshot>>,
    events: broadcast::Sender<Envelope<Event>>,
}

impl Settings {
    /// Opens the configuration file at `path` (creating it on first run) and
    /// compiles the keymap. A file with an error leaves the defaults in
    /// effect until it is fixed. Blocking: it reads and may write the file.
    pub(crate) fn open(path: PathBuf) -> Arc<Self> {
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
        let (events, _) = broadcast::channel(EVENT_BUFFER);
        Arc::new(Self {
            registry,
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

    /// Configuration events from now on, for a connection that said `hello`.
    pub(crate) fn subscribe(&self) -> broadcast::Receiver<Envelope<Event>> {
        self.events.subscribe()
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

    /// The reply to `get_keymap`.
    pub(crate) fn get_keymap(&self) -> Response {
        Response::Keymap(self.snapshot().keymap.to_wire())
    }

    /// The reply to `list_commands`.
    pub(crate) fn list_commands(&self) -> Response {
        let snapshot = self.snapshot();
        Response::Commands {
            commands: self
                .registry
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
                &self.registry,
                query,
                usize::try_from(limit).unwrap_or(usize::MAX),
            ),
        }
    }

    /// The reply to `execute_command`: the core runs its own commands and
    /// hands the UI's back. Of the core's commands only `help.about` exists
    /// yet; the file operations arrive with their phase.
    pub(crate) fn execute(&self, command: &str) -> Response {
        let Some(found) = self.registry.get(command) else {
            return unknown_command(command);
        };
        match (found.target, command) {
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
        let Some(found) = self.registry.get(command) else {
            return unknown_command(command);
        };
        if found.immutable {
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
            compiled = Some(compile(&self.registry, &config.overrides())?);
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
        if self.registry.get(command).is_none() && !has_entries() {
            return unknown_command(command);
        }
        let mut store = self.lock_store();
        let mut compiled = None;
        let result = store.unset_keybinding(command, |config| {
            compiled = Some(compile(&self.registry, &config.overrides())?);
            Ok(())
        });
        self.finish_update(&store, result, compiled)
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
            Err(UpdateError::Rejected(error)) => keymap_failure(&error),
            Err(UpdateError::FileHasError(error)) => error_reply(
                ErrorCode::ConfigError,
                format!(
                    "{} has an error to fix first ({error}); nothing was changed",
                    self.path.display()
                ),
            ),
            Err(UpdateError::Io(error)) => error_reply(
                ErrorCode::ConfigError,
                format!("cannot write {}: {error}", self.path.display()),
            ),
        }
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
            compiled = Some(compile(&self.registry, &config.overrides())?);
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
        // An error means no connection is listening, which is fine.
        let _ = self.events.send(Envelope::new(RequestId::new(), event));
    }

    fn lock_store(&self) -> MutexGuard<'_, ConfigStore> {
        self.store.lock().unwrap_or_else(PoisonError::into_inner)
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
