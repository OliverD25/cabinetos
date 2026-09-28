//! Directory listings: reading a directory into a shared-memory section for a
//! client, and keeping a watched listing current.

use std::time::{Duration, Instant};

use cabinetos_fs::{DirectoryChanged, DirectoryWatcher, FsError, ListOptions, ListingWriter};
use cabinetos_ipc::SharedSection;
use cabinetos_protocol::{Envelope, ErrorCode, Event, RefreshReason, RequestId};
use tokio::sync::mpsc;

use crate::connection::Outbox;

/// After the first change of a burst, wait this long for the rest of it, so
/// that one refresh covers them all.
pub(crate) const DEBOUNCE: Duration = Duration::from_millis(50);

/// No listing is refreshed more than 30 times per second, whatever the disk
/// does (brief §3 sets the same limit for progress updates).
pub(crate) const MIN_REFRESH_INTERVAL: Duration = Duration::from_micros(33_334);

/// A failed request: the code and message of the error reply.
pub(crate) type Failure = (ErrorCode, String);

/// A listing written into a section and handed to the client.
pub(crate) struct Published {
    /// The core's handle to the section; the client has its own.
    pub(crate) section: SharedSection,
    /// The section's handle value in the client's process.
    pub(crate) client_handle: u64,
    /// Bytes of the section that hold the listing.
    pub(crate) size: u64,
    pub(crate) entry_count: u32,
    /// Time to read, sort and write the listing.
    pub(crate) elapsed_us: u64,
}

/// Reads `path`, writes it into a new section and duplicates the section's
/// handle into the client. Blocking: run it with `spawn_blocking`.
pub(crate) fn publish(
    path: &str,
    options: &ListOptions,
    generation: u32,
    client_pid: u32,
) -> Result<Published, Failure> {
    let started = Instant::now();
    let listing =
        cabinetos_fs::list_directory(path, options).map_err(|error| fs_failure(&error))?;
    let writer =
        ListingWriter::new(&listing).map_err(|error| (ErrorCode::Io, error.to_string()))?;
    let section = SharedSection::create(writer.section_size()).map_err(internal)?;
    {
        let mut view = section.map().map_err(internal)?;
        writer
            .write(view.as_mut_slice(), generation)
            .map_err(|error| (ErrorCode::Internal, error.to_string()))?;
    }
    let elapsed_us = micros(started.elapsed());
    let client_handle = section
        .duplicate_for(client_pid)
        .map_err(|error| {
            (
                ErrorCode::Internal,
                format!("cannot hand the listing to process {client_pid}: {error}"),
            )
        })?
        .0;
    Ok(Published {
        section,
        client_handle,
        size: writer.section_size() as u64,
        entry_count: u32::try_from(listing.len()).unwrap_or(u32::MAX),
        elapsed_us,
    })
}

/// The error reply for a filesystem error.
pub(crate) fn fs_failure(error: &FsError) -> Failure {
    let code = match error {
        FsError::NotFound { .. } => ErrorCode::NotFound,
        FsError::AccessDenied { .. } => ErrorCode::AccessDenied,
        FsError::InvalidPath { .. } => ErrorCode::InvalidPath,
        FsError::Io { .. } => ErrorCode::Io,
    };
    (code, error.to_string())
}

fn internal(error: impl std::fmt::Display) -> Failure {
    (ErrorCode::Internal, error.to_string())
}

pub(crate) fn micros(duration: Duration) -> u64 {
    u64::try_from(duration.as_micros()).unwrap_or(u64::MAX)
}

/// Everything a watched listing needs after it has been opened.
pub(crate) struct WatchedListing {
    pub(crate) listing_id: u64,
    pub(crate) path: String,
    pub(crate) options: ListOptions,
    pub(crate) client_pid: u32,
    /// The current section; replaced at every refresh.
    pub(crate) section: SharedSection,
    pub(crate) generation: u32,
    pub(crate) changes: mpsc::UnboundedReceiver<DirectoryChanged>,
    /// Kept alive for as long as the listing is; dropping it stops watching.
    pub(crate) _watcher: DirectoryWatcher,
    pub(crate) out: Outbox,
}

/// Keeps a watched listing current until its directory is lost. Returns the
/// listing's ID. Aborting the task (on `close_listing`, or when the
/// connection ends) drops the watcher and the section.
pub(crate) async fn refresh_loop(mut watched: WatchedListing) -> u64 {
    let listing_id = watched.listing_id;
    let mut last_refresh: Option<Instant> = None;
    while let Some(first) = watched.changes.recv().await {
        let mut reason = match first {
            DirectoryChanged::Changed => RefreshReason::Changed,
            DirectoryChanged::Overflow => RefreshReason::Overflow,
            DirectoryChanged::Failed(message) => {
                lose(&watched, &message);
                return listing_id;
            }
        };
        tokio::time::sleep(DEBOUNCE).await;
        if let Some(last) = last_refresh {
            tokio::time::sleep_until((last + MIN_REFRESH_INTERVAL).into()).await;
        }
        // Everything reported while waiting is covered by this refresh.
        while let Ok(more) = watched.changes.try_recv() {
            match more {
                DirectoryChanged::Changed => {}
                DirectoryChanged::Overflow => reason = RefreshReason::Overflow,
                DirectoryChanged::Failed(message) => {
                    lose(&watched, &message);
                    return listing_id;
                }
            }
        }

        let generation = watched.generation + 1;
        let (path, options, client_pid) =
            (watched.path.clone(), watched.options, watched.client_pid);
        let published = match tokio::task::spawn_blocking(move || {
            publish(&path, &options, generation, client_pid)
        })
        .await
        {
            Ok(result) => result,
            Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
            Err(_) => return listing_id,
        };
        match published {
            Ok(published) => {
                watched.out.send(&Envelope::new(
                    RequestId::new(),
                    Event::ListingRefreshed {
                        listing_id,
                        section_handle: published.client_handle,
                        section_size: published.size,
                        entry_count: published.entry_count,
                        generation,
                        reason,
                    },
                ));
                tracing::debug!(
                    listing_id,
                    generation,
                    entries = published.entry_count,
                    elapsed_us = published.elapsed_us,
                    ?reason,
                    "listing refreshed"
                );
                // The event is queued; the core's handle to the previous
                // section closes here. The client's handle keeps it alive.
                watched.section = published.section;
                watched.generation = generation;
                last_refresh = Some(Instant::now());
            }
            Err((_, message)) => {
                lose(&watched, &message);
                return listing_id;
            }
        }
    }
    lose(&watched, "watching stopped");
    listing_id
}

/// Tells the client the listing can no longer be kept current.
fn lose(watched: &WatchedListing, message: &str) {
    tracing::info!(listing_id = watched.listing_id, path = %watched.path, message, "listing lost");
    watched.out.send(&Envelope::new(
        RequestId::new(),
        Event::ListingLost {
            listing_id: watched.listing_id,
            message: message.to_owned(),
        },
    ));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn refreshes_stay_within_thirty_per_second() {
        // Each refresh waits for the debounce, and never comes sooner than
        // the minimum interval after the previous one.
        assert!(MIN_REFRESH_INTERVAL.as_secs_f64() >= 1.0 / 30.0);
        assert!(DEBOUNCE >= MIN_REFRESH_INTERVAL);
    }

    async fn next_event(out: &mut mpsc::UnboundedReceiver<Vec<u8>>) -> Event {
        let frame = tokio::time::timeout(Duration::from_secs(2), out.recv())
            .await
            .unwrap()
            .unwrap();
        serde_json::from_slice::<Envelope<Event>>(&frame)
            .unwrap()
            .body
    }

    /// The refresh loop, fed by hand: an overflow in a burst makes the
    /// refresh's reason `overflow`, and a watcher failure loses the listing.
    #[tokio::test]
    async fn an_injected_overflow_refreshes_with_reason_overflow() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("a.txt"), b"").unwrap();
        let path = dir.path().to_str().unwrap().to_owned();
        let options = ListOptions::default();
        // The sections are handed to this very process; their client-side
        // handles stay open until the test process ends.
        let client_pid = std::process::id();
        let first = publish(&path, &options, 1, client_pid).unwrap();
        let watcher = DirectoryWatcher::start(&path, "watch-test".to_owned(), |_| {}).unwrap();
        let (changes_tx, changes_rx) = mpsc::unbounded_channel();
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        let task = tokio::spawn(refresh_loop(WatchedListing {
            listing_id: 7,
            path,
            options,
            client_pid,
            section: first.section,
            generation: 1,
            changes: changes_rx,
            _watcher: watcher,
            out: Outbox(out_tx),
        }));

        changes_tx.send(DirectoryChanged::Changed).unwrap();
        changes_tx.send(DirectoryChanged::Overflow).unwrap();
        let refreshed = next_event(&mut out_rx).await;
        assert!(
            matches!(
                refreshed,
                Event::ListingRefreshed {
                    listing_id: 7,
                    generation: 2,
                    entry_count: 1,
                    reason: RefreshReason::Overflow,
                    ..
                }
            ),
            "{refreshed:?}"
        );

        changes_tx
            .send(DirectoryChanged::Failed("gone".to_owned()))
            .unwrap();
        let lost = next_event(&mut out_rx).await;
        assert_eq!(
            lost,
            Event::ListingLost {
                listing_id: 7,
                message: "gone".to_owned()
            }
        );
        assert_eq!(task.await.unwrap(), 7);
    }

    #[test]
    fn filesystem_errors_map_to_protocol_codes() {
        let cases = [
            (FsError::from_win32("x", 2), ErrorCode::NotFound),
            (FsError::from_win32("x", 5), ErrorCode::AccessDenied),
            (FsError::from_win32("x", 267), ErrorCode::InvalidPath),
            (FsError::from_win32("x", 21), ErrorCode::Io),
        ];
        for (error, code) in cases {
            assert_eq!(fs_failure(&error).0, code);
        }
    }
}
