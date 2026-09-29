//! `measure_paths`: the files, folders and bytes under paths, for the Size
//! column (Total Commander's Space and Shift+Alt+Enter). A measure counts on
//! a thread of the blocking pool, so several run at once and none holds up
//! the next request; its events go to the connection that asked. The walk
//! is the job engine's (`cabinetos_fs::measure_tree`).

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use cabinetos_fs::{FsError, MeasureError, Tree};
use cabinetos_protocol::{Envelope, Event, MeasureResult, RequestId};

use crate::connection::Outbox;
use crate::listing::{self, Failure};

/// Progress goes out at most 30 times per second per measure.
const PROGRESS_EVERY: Duration = Duration::from_micros(1_000_000 / 30);

/// `Ok` when every path is there; else why the first one that is not is
/// not.
pub(crate) fn check(paths: &[String]) -> Result<(), Failure> {
    for path in paths {
        if let Err(error) = std::fs::symlink_metadata(path) {
            let error = match error.raw_os_error() {
                Some(code) => FsError::from_win32(path, code.cast_unsigned()),
                None => FsError::Io {
                    path: path.clone(),
                    source: error,
                },
            };
            return Err(listing::fs_failure(&error));
        }
    }
    Ok(())
}

/// Counts each path in turn, sending `measure_progress` at most 30 times a
/// second (none when the count ends sooner), then `measure_finished`. Ends
/// early, as cancelled, once `stop` is set.
pub(crate) fn count(measure_id: u64, paths: Vec<String>, stop: &AtomicBool, out: &Outbox) {
    let mut last = Instant::now();
    let mut results = Vec::with_capacity(paths.len());
    let mut cancelled = false;
    for path in paths {
        let stopped = || stop.load(Ordering::Relaxed);
        let mut report = |tree: &Tree| {
            if last.elapsed() >= PROGRESS_EVERY {
                last = Instant::now();
                out.send(&Envelope::new(
                    RequestId::new(),
                    Event::MeasureProgress {
                        measure_id,
                        path: path.clone(),
                        files: tree.files,
                        folders: tree.folders,
                        bytes: tree.bytes,
                    },
                ));
            }
        };
        let tree = match cabinetos_fs::measure_tree(&path, &stopped, &mut report) {
            Ok(tree) => tree,
            Err(MeasureError::Stopped) => {
                cancelled = true;
                break;
            }
            // Gone since the check: nothing is left to count, and it could
            // not be read.
            Err(MeasureError::Root(_)) => Tree {
                unreadable: 1,
                ..Tree::default()
            },
        };
        results.push(MeasureResult {
            path,
            files: tree.files,
            folders: tree.folders,
            bytes: tree.bytes,
            unreadable: tree.unreadable,
        });
    }
    tracing::info!(
        measure_id,
        counted = results.len(),
        cancelled,
        "measure finished"
    );
    out.send(&Envelope::new(
        RequestId::new(),
        Event::MeasureFinished {
            measure_id,
            results,
            cancelled,
        },
    ));
}
