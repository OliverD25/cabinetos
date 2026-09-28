//! Noticing edits to the configuration file.
//!
//! The watcher watches the file's directory, not the file: editors often
//! save by writing a temporary file and renaming it over the old one, and a
//! watch on the old file would end with it. Every change in the directory
//! counts (the store's content hash sorts out which ones matter), and a burst
//! of changes, such as one save's write, rename and attribute change, is
//! reported once.

use std::path::Path;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use cabinetos_fs::{DirectoryChanged, DirectoryWatcher, FsError};

/// How long the directory must stay quiet before a change is reported.
pub const DEBOUNCE: Duration = Duration::from_millis(100);

/// The longest a report waits while changes keep coming, so a directory that
/// never goes quiet still gets its changes read.
const MAX_DELAY: Duration = Duration::from_millis(500);

/// What the watcher reports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WatchEvent {
    /// Something in the directory changed; read the file again.
    Changed,
    /// Watching stopped, for example because the directory was deleted.
    /// Nothing more follows.
    Failed(String),
}

/// Watches the directory of the configuration file until dropped or stopped.
#[derive(Debug)]
pub struct ConfigWatcher {
    watcher: Option<DirectoryWatcher>,
    thread: Option<JoinHandle<()>>,
}

impl ConfigWatcher {
    /// Starts watching the directory of `config_path`. `on_event` runs on a
    /// thread of the watcher's own, at most once per [`DEBOUNCE`] of quiet;
    /// it may block briefly (to read the file) without missing changes.
    pub fn start<F>(config_path: &Path, on_event: F) -> Result<Self, FsError>
    where
        F: FnMut(WatchEvent) + Send + 'static,
    {
        let dir = std::path::absolute(config_path)
            .ok()
            .and_then(|path| path.parent().map(Path::to_path_buf))
            .ok_or_else(|| FsError::InvalidPath {
                path: config_path.display().to_string(),
                reason: "the configuration file has no directory".to_owned(),
            })?;
        let dir = dir.to_str().ok_or_else(|| FsError::InvalidPath {
            path: dir.display().to_string(),
            reason: "the path is not valid Unicode".to_owned(),
        })?;
        let (changes_tx, changes_rx) = mpsc::channel();
        let watcher = DirectoryWatcher::start(dir, "config-watch".to_owned(), move |change| {
            let _ = changes_tx.send(change);
        })?;
        let thread = std::thread::Builder::new()
            .name("config-debounce".to_owned())
            .spawn(move || debounce(&changes_rx, on_event))
            .map_err(|source| FsError::Io {
                path: dir.to_owned(),
                source,
            })?;
        Ok(Self {
            watcher: Some(watcher),
            thread: Some(thread),
        })
    }

    /// Stops watching and waits for the watcher's threads to end. A report
    /// that is being delivered finishes first.
    pub fn stop(mut self) {
        if let Some(watcher) = self.watcher.take() {
            watcher.stop();
        }
        // The directory watcher's thread owned the only sender, so the
        // debounce thread ends as soon as it is idle.
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// Turns the directory watcher's notifications into reports: one per burst.
fn debounce(changes: &Receiver<DirectoryChanged>, mut on_event: impl FnMut(WatchEvent)) {
    while let Ok(first) = changes.recv() {
        if let DirectoryChanged::Failed(message) = first {
            on_event(WatchEvent::Failed(message));
            return;
        }
        let started = Instant::now();
        let mut failed = None;
        loop {
            match changes.recv_timeout(DEBOUNCE) {
                Ok(DirectoryChanged::Failed(message)) => {
                    failed = Some(message);
                    break;
                }
                Ok(_) if started.elapsed() < MAX_DELAY => {}
                Ok(_) | Err(RecvTimeoutError::Timeout) => break,
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
        on_event(WatchEvent::Changed);
        if let Some(message) = failed {
            on_event(WatchEvent::Failed(message));
            return;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reports(changes: Vec<DirectoryChanged>, gap: Duration) -> Vec<WatchEvent> {
        let (tx, rx) = mpsc::channel();
        let feeder = std::thread::spawn(move || {
            for change in changes {
                let _ = tx.send(change);
                std::thread::sleep(gap);
            }
            // Dropping the sender stops the debounce thread, as stopping the
            // watcher does; give it time to report the last burst first.
            std::thread::sleep(DEBOUNCE * 3);
        });
        let mut seen = Vec::new();
        debounce(&rx, |event| seen.push(event));
        feeder.join().unwrap();
        seen
    }

    #[test]
    fn a_burst_is_reported_once() {
        let seen = reports(vec![DirectoryChanged::Changed; 5], Duration::from_millis(5));
        assert_eq!(seen, [WatchEvent::Changed]);
    }

    #[test]
    fn changes_far_apart_are_reported_apart() {
        let seen = reports(
            vec![DirectoryChanged::Changed, DirectoryChanged::Overflow],
            DEBOUNCE * 3,
        );
        assert_eq!(seen, [WatchEvent::Changed, WatchEvent::Changed]);
    }

    #[test]
    fn a_failure_ends_the_reports() {
        let seen = reports(
            vec![
                DirectoryChanged::Changed,
                DirectoryChanged::Failed("gone".to_owned()),
                DirectoryChanged::Changed,
            ],
            Duration::from_millis(1),
        );
        assert_eq!(
            seen,
            [WatchEvent::Changed, WatchEvent::Failed("gone".to_owned())]
        );
    }
}
