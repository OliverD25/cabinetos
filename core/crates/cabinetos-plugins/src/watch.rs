//! `watch-folder`: the folders a plugin with `fs:watch` watches
//! (`docs/plugins.md`, "Watching folders"). Each watch is the file-system
//! crate's watcher on the folder and a thread that gathers its changes for
//! 200 ms and sends them to the plugin as one `folder-changed` event. The
//! watches live in the instance's store: when the instance ends, they end.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::sync::{Arc, Weak};
use std::time::{Duration, Instant};

use cabinetos_fs::{DetailedChange, DirectoryWatcher, EntryChange, EntryChangeKind};

use crate::HostInner;

/// How long changes to one folder are gathered into one message.
pub(crate) const COALESCE: Duration = Duration::from_millis(200);

/// The most folders one instance watches at once.
pub(crate) const MAX_WATCHES: usize = 16;

/// The most changes one message carries; beyond, it says `overflow`.
const MAX_CHANGES: usize = 1000;

/// Hands events to one instance of a plugin: to the instance of this
/// generation only, so a watch of an instance that is gone reaches nobody.
#[derive(Clone)]
pub(crate) struct Notifier {
    host: Weak<HostInner>,
    plugin_id: String,
    generation: u64,
}

impl Notifier {
    pub(crate) fn new(host: Weak<HostInner>, plugin_id: String, generation: u64) -> Self {
        Self {
            host,
            plugin_id,
            generation,
        }
    }

    fn send(&self, name: &str, payload: String) {
        if let Some(host) = self.host.upgrade() {
            host.notify(&self.plugin_id, self.generation, name, payload);
        }
    }
}

/// One watched folder. Dropping it stops the watcher; its thread then ends.
struct Watch {
    _watcher: DirectoryWatcher,
    /// Cleared when the watch ended by itself (the folder went away).
    alive: Arc<AtomicBool>,
}

/// The folders one instance watches, by their lower-case path.
#[derive(Default)]
pub(crate) struct Watches {
    folders: HashMap<String, Watch>,
}

impl Watches {
    /// Starts watching `path` when it lies under one of `roots`.
    pub(crate) fn watch(
        &mut self,
        roots: &[PathBuf],
        path: &str,
        notifier: &Notifier,
    ) -> Result<(), String> {
        let folder = windows_path(path)?;
        let key = folder.to_lowercase();
        if !under_a_root(roots, &key) {
            return Err(format!(
                "{folder} is not under a folder plugin.json names for fs:watch"
            ));
        }
        self.folders
            .retain(|_, watch| watch.alive.load(Ordering::SeqCst));
        if self.folders.contains_key(&key) {
            return Ok(());
        }
        if self.folders.len() >= MAX_WATCHES {
            return Err(format!(
                "a plugin watches at most {MAX_WATCHES} folders at once"
            ));
        }
        let (sender, changes) = mpsc::channel();
        let watcher = DirectoryWatcher::start_detailed(
            &folder,
            format!("plugin-{}-watch", notifier.plugin_id),
            move |change| {
                let _ = sender.send(change);
            },
        )
        .map_err(|error| format!("cannot watch {folder}: {error}"))?;
        let alive = Arc::new(AtomicBool::new(true));
        let thread_alive = Arc::clone(&alive);
        let thread_notifier = notifier.clone();
        let thread_folder = folder.clone();
        std::thread::Builder::new()
            .name(format!("plugin-{}-changes", notifier.plugin_id))
            .spawn(move || gather(&thread_folder, &changes, &thread_notifier, &thread_alive))
            .map_err(|error| format!("cannot watch {folder}: {error}"))?;
        tracing::info!(folder = %folder, "watching a folder for the plugin");
        self.folders.insert(
            key,
            Watch {
                _watcher: watcher,
                alive,
            },
        );
        Ok(())
    }

    /// Stops watching `path`. Whether it was watched.
    pub(crate) fn unwatch(&mut self, path: &str) -> bool {
        windows_path(path).is_ok_and(|folder| self.folders.remove(&folder.to_lowercase()).is_some())
    }
}

/// `C:\a\b` from `C:\a\b`, `C:/a/b` or the sandbox form `/C:/a/b`, without
/// a trailing separator; absolute, and with no `.` or `..` in it.
fn windows_path(path: &str) -> Result<String, String> {
    let drive_form = path
        .strip_prefix('/')
        .filter(|rest| rest.as_bytes().get(1) == Some(&b':'));
    let text = drive_form.unwrap_or(path).replace('/', "\\");
    let trimmed = text.trim_end_matches('\\');
    let folder = if trimmed.len() == 2 && trimmed.ends_with(':') {
        format!("{trimmed}\\")
    } else {
        trimmed.to_owned()
    };
    if !Path::new(&folder).is_absolute() {
        return Err(format!("`{path}` is not an absolute path"));
    }
    if folder
        .split('\\')
        .skip(1)
        .any(|part| part == "." || part == "..")
    {
        return Err(format!("`{path}` may not contain `.` or `..`"));
    }
    Ok(folder)
}

/// Whether the lower-case `folder` is one of `roots` or lies under one.
fn under_a_root(roots: &[PathBuf], folder: &str) -> bool {
    roots.iter().any(|root| {
        let root = root.display().to_string().to_lowercase();
        let root = root.trim_end_matches('\\');
        folder == root || folder.starts_with(&format!("{root}\\"))
    })
}

/// The watch's thread: gathers each burst of changes for 200 ms and sends
/// it. Ends when the watcher stops (the plugin unwatched, or its instance
/// ended) or fails (the folder went away: `folder-unwatched`).
fn gather(
    folder: &str,
    changes: &Receiver<DetailedChange>,
    notifier: &Notifier,
    alive: &AtomicBool,
) {
    while let Ok(first) = changes.recv() {
        let mut batch = Batch::default();
        let mut ended = batch.add(folder, first);
        let until = Instant::now() + COALESCE;
        while ended.is_none() {
            let left = until.saturating_duration_since(Instant::now());
            if left.is_zero() {
                break;
            }
            match changes.recv_timeout(left) {
                Ok(change) => ended = batch.add(folder, change),
                Err(RecvTimeoutError::Timeout) => break,
                // Unwatched: the plugin asked to hear no more.
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
        if !batch.is_empty() {
            notifier.send("folder-changed", batch.payload(folder));
        }
        if let Some(message) = ended {
            alive.store(false, Ordering::SeqCst);
            tracing::warn!(folder = %folder, error = %message, "a plugin's folder watch ended");
            notifier.send(
                "folder-unwatched",
                serde_json::json!({ "path": folder, "message": message }).to_string(),
            );
            return;
        }
    }
}

/// The changes of one message.
#[derive(Default)]
struct Batch {
    changes: Vec<serde_json::Value>,
    last: Option<EntryChange>,
    overflow: bool,
}

impl Batch {
    /// Adds what the watcher reported; `Some` with the reason when watching
    /// ended.
    fn add(&mut self, folder: &str, change: DetailedChange) -> Option<String> {
        match change {
            DetailedChange::Entries(entries) => {
                for entry in entries {
                    // Windows often reports one write as several changes.
                    if self.last.as_ref() == Some(&entry) {
                        continue;
                    }
                    if self.changes.len() >= MAX_CHANGES {
                        self.overflow = true;
                        break;
                    }
                    self.changes.push(serde_json::json!({
                        "kind": kind_name(entry.kind),
                        "path": join(folder, &entry.name),
                        "old_path": entry.old_name.as_deref().map(|old| join(folder, old)),
                    }));
                    self.last = Some(entry);
                }
                None
            }
            DetailedChange::Overflow => {
                self.overflow = true;
                None
            }
            DetailedChange::Failed(message) => Some(message),
        }
    }

    fn is_empty(&self) -> bool {
        self.changes.is_empty() && !self.overflow
    }

    fn payload(&self, folder: &str) -> String {
        serde_json::json!({
            "path": folder,
            "changes": self.changes,
            "overflow": self.overflow,
        })
        .to_string()
    }
}

const fn kind_name(kind: EntryChangeKind) -> &'static str {
    match kind {
        EntryChangeKind::Created => "created",
        EntryChangeKind::Modified => "modified",
        EntryChangeKind::Removed => "removed",
        EntryChangeKind::Renamed => "renamed",
    }
}

fn join(folder: &str, name: &str) -> String {
    if folder.ends_with('\\') {
        format!("{folder}{name}")
    } else {
        format!("{folder}\\{name}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_come_in_either_form_and_stay_under_their_roots() {
        assert_eq!(windows_path(r"C:\a\b\").unwrap(), r"C:\a\b");
        assert_eq!(windows_path("/C:/a/b").unwrap(), r"C:\a\b");
        assert_eq!(windows_path("C:/a/b/").unwrap(), r"C:\a\b");
        assert_eq!(windows_path("/D:/").unwrap(), r"D:\");
        assert_eq!(
            windows_path(r"\\server\share\x").unwrap(),
            r"\\server\share\x"
        );
        for bad in ["a\\b", "/a/b", r"C:\a\..\b", r"C:\a\.\b", ""] {
            assert!(windows_path(bad).is_err(), "{bad}");
        }
        let roots = [PathBuf::from(r"C:\Users\Me\Inbox")];
        assert!(under_a_root(&roots, r"c:\users\me\inbox"));
        assert!(under_a_root(&roots, r"c:\users\me\inbox\sub"));
        assert!(!under_a_root(&roots, r"c:\users\me\inbox2"));
        assert!(!under_a_root(&roots, r"c:\users\me"));
    }

    #[test]
    fn a_batch_joins_paths_drops_repeats_and_says_when_it_is_full() {
        let entry = |kind, name: &str, old: Option<&str>| EntryChange {
            kind,
            name: name.to_owned(),
            old_name: old.map(str::to_owned),
        };
        let mut batch = Batch::default();
        let ended = batch.add(
            r"C:\in",
            DetailedChange::Entries(vec![
                entry(EntryChangeKind::Created, "a.txt", None),
                entry(EntryChangeKind::Modified, "a.txt", None),
                entry(EntryChangeKind::Modified, "a.txt", None),
                entry(EntryChangeKind::Renamed, "b.txt", Some("a.txt")),
            ]),
        );
        assert!(ended.is_none());
        let payload: serde_json::Value = serde_json::from_str(&batch.payload(r"C:\in")).unwrap();
        assert_eq!(
            payload,
            serde_json::json!({
                "path": r"C:\in",
                "changes": [
                    {"kind": "created", "path": r"C:\in\a.txt", "old_path": null},
                    {"kind": "modified", "path": r"C:\in\a.txt", "old_path": null},
                    {"kind": "renamed", "path": r"C:\in\b.txt", "old_path": r"C:\in\a.txt"},
                ],
                "overflow": false,
            })
        );
        assert_eq!(join(r"D:\", "x"), r"D:\x");

        let mut full = Batch::default();
        let many = (0..=MAX_CHANGES)
            .map(|n| entry(EntryChangeKind::Created, &format!("{n}.txt"), None))
            .collect();
        full.add(r"C:\in", DetailedChange::Entries(many));
        assert_eq!(full.changes.len(), MAX_CHANGES);
        assert!(full.overflow);
        let mut lost = Batch::default();
        lost.add(r"C:\in", DetailedChange::Overflow);
        assert!(!lost.is_empty(), "an overflow alone is worth a message");
        assert_eq!(
            Batch::default().add(r"C:\in", DetailedChange::Failed("gone".to_owned())),
            Some("gone".to_owned())
        );
    }
}
