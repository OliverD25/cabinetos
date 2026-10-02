//! In-app updates as the core serves them (`docs/ipc.md`, "Updates"; ADR
//! 0014): the updater of the install this core runs from, its daily check,
//! and the requests.
//!
//! - The install folder is the folder of `cabinetos-core.exe`. A
//!   development build (no `release.json` there) and an all-users install
//!   never check: the daily task does not even start.
//! - The daily check looks 10 seconds after the start, then every hour
//!   whether a day has passed since the last check that worked; when it
//!   finds a newer version, it downloads it in the background.
//! - Every step runs on the blocking pool, in the span of its request or,
//!   for the daily check, of a trace of its own; `update_status` answers
//!   from memory.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use cabinetos_protocol::{ErrorCode, Event, RequestId, Response};
use cabinetos_update::{ErrorKind, Notice, Paths, UpdateError, Updater};
use tokio::sync::watch;
use tokio::time::Instant;

use crate::CORE_VERSION;
use crate::events::EventHub;
use crate::settings::{Settings, Snapshot};

/// The daily check's first look, after the start: the start is never
/// slowed by it.
const FIRST_LOOK: Duration = Duration::from_secs(10);

/// How often the daily check looks whether a day has passed, for a core
/// that runs for days.
const LOOK_EVERY: Duration = Duration::from_hours(1);

/// The `update.*` settings of a snapshot, as the updater takes them.
pub(crate) fn settings_of(snapshot: &Snapshot) -> cabinetos_update::Settings {
    let update = &snapshot.config.update;
    cabinetos_update::Settings {
        check: update.check,
        channel: update.channel,
        source: update.source.clone(),
        allow_insecure: update.allow_insecure,
        auto_install: update.auto_install,
    }
}

/// Opens the updater of the install this core runs from, telling every
/// client what it does. Blocking: it reads the update folder.
pub(crate) fn open(
    update_dir: Option<PathBuf>,
    settings: &Settings,
    events: &Arc<EventHub>,
) -> Arc<Updater> {
    let install = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(Path::to_path_buf))
        .unwrap_or_default();
    let paths = Paths {
        install,
        dir: cabinetos_update::update_dir(update_dir),
        apps_keys: cabinetos_update::apps_keys(),
    };
    let events = Arc::clone(events);
    Arc::new(Updater::open(
        paths,
        CORE_VERSION,
        &settings_of(&settings.snapshot()),
        move |notice| events.publish(event_of(notice)),
    ))
}

fn event_of(notice: Notice) -> Event {
    match notice {
        Notice::State(status) => Event::UpdateStateChanged(status),
        Notice::Progress(progress) => Event::UpdateProgress {
            version: progress.version,
            bytes: progress.bytes,
            total: progress.total,
            bytes_per_second: progress.bytes_per_second,
        },
    }
}

/// The daily check, and `update.channel` followed: runs until the core
/// stops. Nothing at all for an install that cannot update itself.
pub(crate) async fn run_daily(updater: Arc<Updater>, mut changes: watch::Receiver<Arc<Snapshot>>) {
    if !updater.updatable() {
        return;
    }
    let mut channel = settings_of(&changes.borrow_and_update()).channel;
    let mut next = Instant::now() + FIRST_LOOK;
    loop {
        tokio::select! {
            () = tokio::time::sleep_until(next) => {
                next = Instant::now() + LOOK_EVERY;
                let settings = settings_of(&changes.borrow());
                let daily = Arc::clone(&updater);
                // One trace per look, so its lines can be read together.
                let trace = RequestId::new();
                let span = cabinetos_diag::span_for_action(&trace, &trace);
                let _ = tokio::task::spawn_blocking(move || {
                    span.in_scope(|| {
                        if let Some(Err(error)) = daily.run_daily(&settings) {
                            tracing::info!(error = %error, "the daily update check did not finish");
                        }
                    });
                })
                .await;
            }
            followed = changes.changed() => {
                if followed.is_err() {
                    return;
                }
                let now = settings_of(&changes.borrow_and_update()).channel;
                if now != channel {
                    channel = now;
                    let follow = Arc::clone(&updater);
                    let _ = tokio::task::spawn_blocking(move || follow.set_channel(now)).await;
                    // The new channel's check comes soon, not in an hour.
                    next = Instant::now() + FIRST_LOOK;
                }
            }
        }
    }
}

/// The reply of an update step.
pub(crate) fn reply(result: Result<cabinetos_protocol::UpdateStatus, UpdateError>) -> Response {
    match result {
        Ok(status) => Response::UpdateState(Box::new(status)),
        Err(error) => Response::Error {
            code: match error.kind {
                ErrorKind::HashMismatch => ErrorCode::HashMismatch,
                ErrorKind::Refused | ErrorKind::Failed => ErrorCode::UpdateError,
            },
            message: error.message,
        },
    }
}
