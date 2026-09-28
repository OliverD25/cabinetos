//! `list_volumes`: the volume behind every drive letter, asked in parallel,
//! each with a time limit, so a slow drive holds up only itself.
//!
//! A network drive whose server does not answer within [`NETWORK_LIMIT`]
//! is left out, as is a drive that is not ready (an empty card reader) or
//! fails otherwise; the reply lists what answered. The query of a drive
//! that ran out of time goes on on its blocking thread, which cannot be
//! stopped; until it ends, that letter is left out without a new query, so
//! a server that is gone holds one thread, not one per request.
//!
//! [`watch`] sends `volumes_changed` when drive letters come or go.

use std::collections::BTreeSet;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use cabinetos_fs::DriveWatcher;
use cabinetos_fs::volume::{self, Drive, DriveKind};
use cabinetos_protocol::{ErrorCode, Event, Response, VolumeDetails};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinSet;

use crate::events::EventHub;

/// How long a network drive may take to answer.
pub(crate) const NETWORK_LIMIT: Duration = Duration::from_millis(200);

/// How long a local drive may take: an optical drive may need to spin up.
pub(crate) const LOCAL_LIMIT: Duration = Duration::from_secs(2);

/// How long to wait for the rest of a burst of device announcements: one
/// USB stick with two volumes is two, and Windows may repeat one.
const SETTLE: Duration = Duration::from_millis(500);

/// Letters whose last query ran out of time and still runs.
static STILL_ASKING: Mutex<BTreeSet<char>> = Mutex::new(BTreeSet::new());

fn still_asking() -> MutexGuard<'static, BTreeSet<char>> {
    STILL_ASKING.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The reply to `list_volumes`: every drive letter's volume that answered,
/// in letter order.
pub(crate) async fn list_volumes() -> Response {
    let drives = match tokio::task::spawn_blocking(volume::drives).await {
        Ok(drives) => drives,
        Err(error) => std::panic::resume_unwind(error.into_panic()),
    };
    let mut queries = JoinSet::new();
    for drive in drives {
        queries.spawn(query(drive));
    }
    let mut volumes = Vec::new();
    while let Some(done) = queries.join_next().await {
        match done {
            Ok(Some(details)) => volumes.push(details),
            Ok(None) => {}
            Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
            Err(_) => {
                return Response::Error {
                    code: ErrorCode::Internal,
                    message: "the request was cancelled".to_owned(),
                };
            }
        }
    }
    volumes.sort_by_key(|details| details.drive_letter);
    Response::Volumes { volumes }
}

/// Sends `volumes_changed` to every connection that said `hello` whenever
/// the drive letters change, once the announcements have settled and only
/// when the volumes differ from the last ones sent. The watching stops when
/// the returned watcher drops; `None` when it cannot start (the core then
/// runs without the event).
pub(crate) fn watch(events: Arc<EventHub>) -> Option<DriveWatcher> {
    let (changed_tx, mut changed_rx) = mpsc::unbounded_channel();
    let watcher = match DriveWatcher::start(move || {
        let _ = changed_tx.send(());
    }) {
        Ok(watcher) => watcher,
        Err(error) => {
            tracing::warn!(%error, "cannot watch drive letters; volumes_changed will not be sent");
            return None;
        }
    };
    tokio::spawn(async move {
        let mut last = None;
        // Ends when the watcher drops: its thread takes the sender along.
        while changed_rx.recv().await.is_some() {
            tokio::time::sleep(SETTLE).await;
            while changed_rx.try_recv().is_ok() {}
            let Response::Volumes { volumes } = list_volumes().await else {
                continue;
            };
            let now = drive_set(&volumes);
            if last.as_ref() == Some(&now) {
                continue;
            }
            tracing::info!(volumes = volumes.len(), "the drive letters changed");
            last = Some(now);
            events.publish(Event::VolumesChanged { volumes });
        }
    });
    Some(watcher)
}

/// What says that the drives changed, leaving out the free space, which
/// changes all the time.
fn drive_set(volumes: &[VolumeDetails]) -> Vec<(Option<char>, String, String, u64)> {
    volumes
        .iter()
        .map(|volume| {
            (
                volume.drive_letter,
                volume.volume_guid_path.clone(),
                volume.label.clone(),
                volume.total_bytes,
            )
        })
        .collect()
}

/// The volume behind `drive`, or `None` when it is left out.
async fn query(drive: Drive) -> Option<VolumeDetails> {
    let letter = drive.letter;
    if still_asking().contains(&letter) {
        tracing::debug!(%letter, "the drive's last query still runs; left out");
        return None;
    }
    let limit = if drive.kind == DriveKind::Remote {
        NETWORK_LIMIT
    } else {
        LOCAL_LIMIT
    };
    let (answer_tx, mut answer_rx) = oneshot::channel();
    let asking = tokio::task::spawn_blocking(move || {
        let answer = volume::info_for_path(&format!("{letter}:\\"));
        if answer_tx.send(answer).is_err() {
            // Nobody waits for it any more: the letter may be asked again.
            still_asking().remove(&letter);
        }
    });
    let answer = match tokio::time::timeout(limit, &mut answer_rx).await {
        Ok(Ok(answer)) => answer,
        Ok(Err(_)) => {
            // The query ended without an answer: it panicked.
            if let Err(error) = asking.await
                && error.is_panic()
            {
                std::panic::resume_unwind(error.into_panic());
            }
            return None;
        }
        Err(_) => {
            still_asking().insert(letter);
            // From here on the query's answer cannot arrive; one sent in the
            // moment before is still there.
            answer_rx.close();
            let Ok(answer) = answer_rx.try_recv() else {
                tracing::info!(%letter, kind = ?drive.kind, ?limit, "the drive did not answer in time; left out");
                return None;
            };
            still_asking().remove(&letter);
            answer
        }
    };
    match answer {
        Ok(mut details) => {
            // The letter asked for, even when the volume has another one
            // too (a `subst` letter shows its folder's volume).
            details.drive_letter = Some(letter);
            Some(details)
        }
        Err(error) => {
            tracing::debug!(%letter, kind = ?drive.kind, %error, "drive left out");
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn the_system_drive_is_listed_once_in_letter_order() {
        let Response::Volumes { volumes } = list_volumes().await else {
            panic!("expected volumes")
        };
        let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
        let letter = system.chars().next().unwrap().to_ascii_uppercase();
        let letters: Vec<char> = volumes.iter().filter_map(|v| v.drive_letter).collect();
        assert_eq!(
            letters.iter().filter(|&&l| l == letter).count(),
            1,
            "{letters:?}"
        );
        assert!(
            letters.windows(2).all(|pair| pair[0] < pair[1]),
            "{letters:?}"
        );
        let system = volumes
            .iter()
            .find(|v| v.drive_letter == Some(letter))
            .unwrap();
        assert!(!system.filesystem.is_empty() && system.total_bytes > 0);
    }

    #[tokio::test]
    async fn a_letter_still_asked_is_left_out() {
        // No drive answers to a letter past Z, so this reserves nothing real.
        let drive = Drive {
            letter: '[',
            kind: DriveKind::Remote,
        };
        still_asking().insert('[');
        assert_eq!(query(drive).await, None);
        still_asking().remove(&'[');
    }
}
