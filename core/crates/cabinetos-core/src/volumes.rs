//! `list_volumes`: the volume behind every drive letter, asked in parallel,
//! each with a time limit, so a slow drive holds up only itself.
//!
//! A network drive whose server does not answer within [`NETWORK_LIMIT`]
//! is left out, as is a drive that is not ready (an empty card reader) or
//! fails otherwise; the reply lists what answered. The query of a drive
//! that ran out of time goes on on its blocking thread, which cannot be
//! stopped; until it ends, that letter is left out without a new query, so
//! a server that is gone holds one thread, not one per request.

use std::collections::BTreeSet;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use cabinetos_fs::volume::{self, Drive, DriveKind};
use cabinetos_protocol::{ErrorCode, Response, VolumeDetails};
use tokio::sync::oneshot;
use tokio::task::JoinSet;

/// How long a network drive may take to answer.
pub(crate) const NETWORK_LIMIT: Duration = Duration::from_millis(200);

/// How long a local drive may take: an optical drive may need to spin up.
pub(crate) const LOCAL_LIMIT: Duration = Duration::from_secs(2);

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
