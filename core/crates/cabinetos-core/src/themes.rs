//! The colour theme in effect (`docs/themes.md`).
//!
//! `ui.theme` in the configuration names the theme; its file is
//! `<themes folder>\<id>.json`. The core reads it, and every connection that
//! said `hello` gets `theme_changed` with the whole theme whenever the theme
//! in effect changes: another `ui.theme` (through `set_value` or an edit of
//! the file), or a saved edit of the theme's own file. A theme that cannot
//! be used (no such file, or an invalid one) never applies: the last good
//! theme stays, and a `config_error` event says why, once per problem.

use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use cabinetos_config::{ConfigWatcher, WatchEvent};
use cabinetos_protocol::{ErrorCode, Event, Response, Theme};
use cabinetos_themes::{ThemeError, ThemeFolder, default_theme};
use tokio::sync::watch;

use crate::events::EventHub;
use crate::settings::Snapshot;

/// The themes folder and the theme in effect.
pub(crate) struct Themes {
    folder: ThemeFolder,
    state: Mutex<State>,
    events: Arc<EventHub>,
}

struct State {
    /// The theme `ui.theme` names.
    wanted: String,
    /// The theme in effect: the wanted one while it is valid, else the
    /// last one that was (at the start, the shipped default).
    applied: Theme,
    /// The problem reported last, so one problem is reported once.
    reported: Option<String>,
}

impl Themes {
    /// Opens the themes folder, writing the shipped themes that are
    /// missing, and puts the theme `wanted` in effect; the shipped default
    /// when that one cannot be used. Blocking: it reads and may write files.
    pub(crate) fn open(dir: PathBuf, wanted: &str, events: Arc<EventHub>) -> Arc<Self> {
        let folder = ThemeFolder::open(dir);
        let (applied, reported) = match folder.load(wanted) {
            Ok(theme) => (theme, None),
            Err(error) => {
                let message = format!("ui.theme: {error}; the theme `default` is in effect");
                tracing::warn!(error = %message, "the configured theme cannot be used");
                (default_theme(), Some(message))
            }
        };
        tracing::info!(
            dir = %folder.dir().display(),
            theme = %applied.id,
            "themes ready"
        );
        Arc::new(Self {
            folder,
            state: Mutex::new(State {
                wanted: wanted.to_owned(),
                applied,
                reported,
            }),
            events,
        })
    }

    /// Starts watching the themes folder, so a saved edit of the theme in
    /// effect applies at once. The watching stops when the returned watcher
    /// drops.
    pub(crate) fn watch(self: &Arc<Self>) -> Option<ConfigWatcher> {
        let themes = Arc::clone(self);
        let watching =
            ConfigWatcher::watch_dir(self.folder.dir(), "themes", move |event| match event {
                WatchEvent::Changed => themes.refresh(),
                WatchEvent::Failed(message) => {
                    tracing::warn!(error = %message, "stopped watching the themes folder");
                }
            });
        match watching {
            Ok(watcher) => Some(watcher),
            Err(error) => {
                tracing::warn!(
                    %error,
                    "cannot watch the themes folder; theme edits apply after a restart"
                );
                None
            }
        }
    }

    /// The reply to `list_themes`. Blocking: it reads every theme file.
    pub(crate) fn list(&self) -> Response {
        Response::Themes {
            themes: self.folder.list(),
        }
    }

    /// The reply to `get_theme`: the theme `id`, or the theme in effect.
    /// Blocking when it reads a file.
    pub(crate) fn get(&self, id: Option<&str>) -> Response {
        let Some(id) = id else {
            return Response::Theme {
                theme: Box::new(self.applied()),
            };
        };
        match self.folder.load(id) {
            Ok(theme) => Response::Theme {
                theme: Box::new(theme),
            },
            Err(ThemeError::NotFound(message)) => Response::Error {
                code: ErrorCode::NoSuchTheme,
                message,
            },
            Err(ThemeError::Invalid(message)) => Response::Error {
                code: ErrorCode::ConfigError,
                message,
            },
        }
    }

    /// The theme in effect.
    pub(crate) fn applied(&self) -> Theme {
        self.lock().applied.clone()
    }

    /// Whether `ui.theme` may name `id`: its file is a valid theme. Checked
    /// before `set_value` writes the file. Blocking: it reads the file.
    pub(crate) fn check(&self, id: &str) -> Result<(), String> {
        self.folder
            .load(id)
            .map(|_| ())
            .map_err(|error| format!("ui.theme: {error}"))
    }

    /// `ui.theme` changed: puts that theme in effect, or reports why it
    /// cannot be. Blocking: it reads the theme's file.
    pub(crate) fn select(&self, wanted: &str) {
        let mut state = self.lock();
        wanted.clone_into(&mut state.wanted);
        self.resolve(&mut state);
    }

    /// A theme file changed: reads the wanted theme again.
    pub(crate) fn refresh(&self) {
        let mut state = self.lock();
        self.resolve(&mut state);
    }

    fn resolve(&self, state: &mut State) {
        match self.folder.load(&state.wanted) {
            Ok(theme) => {
                state.reported = None;
                if theme != state.applied {
                    tracing::info!(theme = %theme.id, "theme applied");
                    state.applied = theme.clone();
                    self.events.publish(Event::ThemeChanged {
                        theme: Box::new(theme),
                    });
                }
            }
            Err(error) => {
                let message = format!(
                    "ui.theme: {error}; the theme `{}` stays in effect",
                    state.applied.id
                );
                if state.reported.as_ref() != Some(&message) {
                    tracing::warn!(error = %message, "the configured theme cannot be used");
                    self.events.publish(Event::ConfigError {
                        line: None,
                        column: None,
                        message: message.clone(),
                    });
                    state.reported = Some(message);
                }
            }
        }
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Keeps the theme in effect in step with `ui.theme`, whether a client or an
/// edit of the file changed it.
pub(crate) async fn follow_settings(
    themes: Arc<Themes>,
    mut changes: watch::Receiver<Arc<Snapshot>>,
) {
    let mut wanted = changes.borrow_and_update().config.ui.theme.clone();
    // The configuration may have changed between opening the themes and
    // this task's start.
    if themes.lock().wanted != wanted {
        let first = wanted.clone();
        let selecting = Arc::clone(&themes);
        let _ = tokio::task::spawn_blocking(move || selecting.select(&first)).await;
    }
    while changes.changed().await.is_ok() {
        let now = changes.borrow_and_update().config.ui.theme.clone();
        if now != wanted {
            wanted.clone_from(&now);
            let selecting = Arc::clone(&themes);
            let _ = tokio::task::spawn_blocking(move || selecting.select(&now)).await;
        }
    }
}
