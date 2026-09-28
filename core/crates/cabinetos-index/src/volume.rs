//! Indexing volumes. One thread per volume builds the index from the MFT,
//! then follows the change journal and applies each change within
//! milliseconds. When the journal can no longer say what changed (it was
//! replaced, or purged records the index had not read), the thread builds a
//! new index while the old one keeps answering searches, then swaps them.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError, RwLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::index::VolumeIndex;
use crate::record;
use crate::search::{IndexHit, Matcher, sort_hits};
use crate::win::{self, Buffer, Journal, ReadError, Volume};

/// Bytes per MFT enumeration call: about 10,000 records.
const ENUMERATION_BUFFER: usize = 1024 * 1024;

/// Bytes per journal read: about 600 records.
const JOURNAL_BUFFER: usize = 64 * 1024;

/// Journal read failures in a row before the volume is given up.
const MAX_READ_FAILURES: u32 = 10;

/// A read that returns nothing sooner than this did not wait; the thread
/// then pauses, so it cannot spin.
const QUICK_EMPTY_READ: Duration = Duration::from_millis(50);
const EMPTY_READ_PAUSE: Duration = Duration::from_millis(200);

/// Where a volume's index is.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum VolumeState {
    /// The first index is being built; searches skip the volume.
    Building,
    /// The index is current and follows the change journal.
    Ready,
    /// A new index is being built; the previous one answers meanwhile.
    Rebuilding,
    /// The volume cannot be indexed, for the given reason.
    Failed(String),
}

/// The state of one volume's index.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VolumeReport {
    /// The drive letter.
    pub letter: char,
    /// Where the index is.
    pub state: VolumeState,
    /// The entries it holds.
    pub entries: u64,
    /// How long the last build took.
    pub built_in_ms: Option<u64>,
    /// Journal bytes written but not yet applied; 0 when current.
    pub journal_lag: Option<u64>,
    /// The heap memory the index holds.
    pub memory_bytes: u64,
}

/// A search's hits, best first, and whether every volume it covered was
/// indexed.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchOutcome {
    /// The hits.
    pub hits: Vec<IndexHit>,
    /// False when a volume the search should cover is still being built or
    /// could not be indexed.
    pub complete: bool,
}

/// Why a search cannot run.
#[derive(Debug, thiserror::Error)]
pub enum SearchError {
    /// The root is not an absolute path on a drive letter.
    #[error("the root `{0}` is not an absolute path on a drive such as C:\\")]
    BadRoot(String),
    /// The root's drive is not indexed.
    #[error("drive {0}: is not indexed")]
    NotIndexed(char),
    /// The root cannot be opened.
    #[error("cannot open {root}: {error}")]
    RootUnreadable {
        /// The root as given.
        root: String,
        /// What Windows said.
        error: windows::core::Error,
    },
}

/// One volume, as its thread and the searches share it.
struct Shared {
    letter: char,
    index: RwLock<Option<VolumeIndex>>,
    report: Mutex<VolumeReport>,
}

impl Shared {
    fn report(&self) -> MutexGuard<'_, VolumeReport> {
        self.report.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn set_state(&self, state: VolumeState) {
        self.report().state = state;
    }
}

/// The indexes of several volumes, each kept by its own thread.
pub struct Indexes {
    volumes: Vec<Arc<Shared>>,
    stop: Arc<AtomicBool>,
    threads: Mutex<Vec<JoinHandle<()>>>,
}

impl std::fmt::Debug for Indexes {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let letters: Vec<char> = self.volumes.iter().map(|volume| volume.letter).collect();
        f.debug_struct("Indexes")
            .field("volumes", &letters)
            .finish_non_exhaustive()
    }
}

impl Indexes {
    /// Starts indexing the volumes with these drive letters. Returns at once;
    /// each volume builds on its own thread.
    #[must_use]
    pub fn start(letters: &[char]) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let mut volumes = Vec::new();
        let mut threads = Vec::new();
        for letter in letters {
            let letter = letter.to_ascii_uppercase();
            let shared = Arc::new(Shared {
                letter,
                index: RwLock::new(None),
                report: Mutex::new(VolumeReport {
                    letter,
                    state: VolumeState::Building,
                    entries: 0,
                    built_in_ms: None,
                    journal_lag: None,
                    memory_bytes: 0,
                }),
            });
            let thread_shared = Arc::clone(&shared);
            let thread_stop = Arc::clone(&stop);
            match std::thread::Builder::new()
                .name(format!("index-{letter}"))
                .spawn(move || run(&thread_shared, &thread_stop))
            {
                Ok(thread) => threads.push(thread),
                Err(error) => shared.set_state(VolumeState::Failed(format!(
                    "cannot start its thread: {error}"
                ))),
            }
            volumes.push(shared);
        }
        Self {
            volumes,
            stop,
            threads: Mutex::new(threads),
        }
    }

    /// The state of every volume, in drive-letter order as given.
    #[must_use]
    pub fn status(&self) -> Vec<VolumeReport> {
        self.volumes
            .iter()
            .map(|volume| volume.report().clone())
            .collect()
    }

    /// The best `limit` entries whose name contains the query, best first:
    /// under `root` when given, else on every indexed volume.
    pub fn search(
        &self,
        matcher: &Matcher,
        root: Option<&str>,
        limit: usize,
    ) -> Result<SearchOutcome, SearchError> {
        if let Some(root) = root {
            let (letter, path) =
                normalize_root(root).ok_or_else(|| SearchError::BadRoot(root.to_owned()))?;
            let volume = self
                .volumes
                .iter()
                .find(|volume| volume.letter == letter)
                .ok_or(SearchError::NotIndexed(letter))?;
            let frn = win::file_id(&path).map_err(|error| SearchError::RootUnreadable {
                root: root.to_owned(),
                error,
            })?;
            let (hits, complete) = search_volume(volume, matcher, Some(frn), limit);
            return Ok(SearchOutcome { hits, complete });
        }
        let mut hits = Vec::new();
        let mut complete = true;
        for volume in &self.volumes {
            let (found, covered) = search_volume(volume, matcher, None, limit);
            hits.extend(found);
            complete &= covered;
        }
        sort_hits(&mut hits);
        hits.truncate(limit);
        Ok(SearchOutcome { hits, complete })
    }

    /// Stops every thread and waits for them; each notices within about a
    /// second.
    pub fn stop(&self) {
        self.stop.store(true, Ordering::SeqCst);
        let threads =
            std::mem::take(&mut *self.threads.lock().unwrap_or_else(PoisonError::into_inner));
        for thread in threads {
            let _ = thread.join();
        }
    }
}

impl Drop for Indexes {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Searches one volume; the flag says whether its index covered it.
fn search_volume(
    volume: &Shared,
    matcher: &Matcher,
    root: Option<u64>,
    limit: usize,
) -> (Vec<IndexHit>, bool) {
    let covered = matches!(
        volume.report().state,
        VolumeState::Ready | VolumeState::Rebuilding
    );
    let guard = volume.index.read().unwrap_or_else(PoisonError::into_inner);
    match guard.as_ref() {
        Some(index) => (index.search(matcher, root, limit), covered),
        None => (Vec::new(), false),
    }
}

/// `C:`, `C:\x`, `c:/x/`, `\\?\C:\x` → (`C`, `C:\x`); `None` for anything
/// that is not an absolute path on a drive letter.
fn normalize_root(root: &str) -> Option<(char, String)> {
    let mut root = root
        .strip_prefix(r"\\?\")
        .unwrap_or(root)
        .replace('/', "\\");
    while root.contains(r"\\") {
        root = root.replace(r"\\", r"\");
    }
    let mut chars = root.chars();
    let letter = chars.next()?.to_ascii_uppercase();
    if !letter.is_ascii_alphabetic() || chars.next()? != ':' {
        return None;
    }
    let rest = chars.as_str().trim_end_matches('\\');
    if !rest.is_empty() && !rest.starts_with('\\') {
        return None;
    }
    // A verbatim path gets no cleanup from Windows: no trailing separator,
    // except on the drive's root.
    let path = if rest.is_empty() {
        format!("{letter}:\\")
    } else {
        format!("{letter}:{rest}")
    };
    Some((letter, path))
}

/// What following the journal ended with.
enum Follow {
    Stopped,
    Rebuild(String),
    Failed(String),
}

/// The thread of one volume.
fn run(shared: &Shared, stop: &AtomicBool) {
    let span = tracing::info_span!("volume", letter = %shared.letter);
    let _entered = span.enter();
    let mut first = true;
    loop {
        if stop.load(Ordering::SeqCst) {
            return;
        }
        shared.set_state(if first {
            VolumeState::Building
        } else {
            VolumeState::Rebuilding
        });
        let started = Instant::now();
        let (volume, journal, index) = match build(shared.letter, stop) {
            Ok(Some(built)) => built,
            Ok(None) => return,
            Err(message) => {
                tracing::error!(error = %message, "cannot index the volume");
                shared.set_state(VolumeState::Failed(message));
                return;
            }
        };
        let took = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);
        let entries = index.len() as u64;
        let memory = index.heap_bytes() as u64;
        tracing::info!(
            entries,
            distinct_names = index.distinct_names(),
            memory_bytes = memory,
            bytes_per_entry = memory.checked_div(entries).unwrap_or(0),
            built_in_ms = took,
            "index built"
        );
        *shared.index.write().unwrap_or_else(PoisonError::into_inner) = Some(index);
        {
            let mut report = shared.report();
            report.state = VolumeState::Ready;
            report.entries = entries;
            report.built_in_ms = Some(took);
            report.journal_lag = Some(0);
            report.memory_bytes = memory;
        }
        first = false;
        match follow(shared, &volume, &journal, stop) {
            Follow::Stopped => return,
            Follow::Rebuild(reason) => {
                tracing::warn!(reason = %reason, "rebuilding the index");
            }
            Follow::Failed(message) => {
                tracing::error!(error = %message, "stopped following the volume");
                shared.set_state(VolumeState::Failed(message));
                return;
            }
        }
    }
}

/// Reads every MFT entry into a new index. `Ok(None)` when told to stop.
fn build(
    letter: char,
    stop: &AtomicBool,
) -> Result<Option<(Volume, Journal, VolumeIndex)>, String> {
    match win::file_system(letter) {
        Some(system) if system == "NTFS" => {}
        Some(system) => {
            return Err(format!(
                "{letter}: is {system}; only NTFS volumes are indexed"
            ));
        }
        None => return Err(format!("there is no volume {letter}:")),
    }
    let volume = Volume::open(letter).map_err(|error| {
        if win::is_access_denied(&error) {
            format!("{letter}: cannot be read without Administrator rights")
        } else {
            format!("cannot open {letter}: ({error})")
        }
    })?;
    // The journal position comes first: whatever changes while the MFT is
    // read is in the journal after it, and is applied once reading ends.
    let journal = volume.journal().map_err(|error| {
        if win::is_no_journal(&error) {
            format!("{letter}: has no change journal, so an index could not be kept current")
        } else {
            format!("cannot query the change journal of {letter}: ({error})")
        }
    })?;
    let mut index = VolumeIndex::new(letter);
    let mut buffer = Buffer::new(ENUMERATION_BUFFER);
    let mut start = 0u64;
    loop {
        if stop.load(Ordering::SeqCst) {
            return Ok(None);
        }
        let Some(bytes) = volume
            .enumerate(start, &mut buffer)
            .map_err(|error| format!("cannot read the MFT of {letter}: ({error})"))?
        else {
            break;
        };
        let Some(next) = record::header(bytes) else {
            break;
        };
        for parsed in record::records(bytes) {
            let parsed = parsed.map_err(|error| format!("{letter}: {error}"))?;
            index.insert_record(&parsed);
        }
        if next <= start {
            break;
        }
        start = next;
    }
    index.shrink_to_fit();
    Ok(Some((volume, journal, index)))
}

/// Applies the journal's records as they come, until told to stop or the
/// journal can no longer continue.
fn follow(shared: &Shared, volume: &Volume, journal: &Journal, stop: &AtomicBool) -> Follow {
    let mut buffer = Buffer::new(JOURNAL_BUFFER);
    let mut next = journal.next_usn;
    let mut failures = 0u32;
    loop {
        if stop.load(Ordering::SeqCst) {
            return Follow::Stopped;
        }
        let asked = Instant::now();
        match volume.read_journal(journal, next, &mut buffer) {
            Ok(bytes) => {
                failures = 0;
                let Some(after) = record::header(bytes) else {
                    continue;
                };
                let mut applied = 0u64;
                let mut entries = None;
                if bytes.len() > 8 {
                    let mut guard = shared.index.write().unwrap_or_else(PoisonError::into_inner);
                    let Some(index) = guard.as_mut() else {
                        return Follow::Rebuild("the index went missing".to_owned());
                    };
                    for parsed in record::records(bytes) {
                        match parsed {
                            Ok(parsed) => {
                                index.apply(&parsed);
                                applied += 1;
                            }
                            Err(error) => return Follow::Rebuild(error.to_string()),
                        }
                    }
                    entries = Some(index.len() as u64);
                }
                next = after.cast_signed();
                let lag = match volume.journal() {
                    Ok(now) if now.id != journal.id => {
                        return Follow::Rebuild("the change journal was replaced".to_owned());
                    }
                    Ok(now) => Some((now.next_usn - next).max(0).cast_unsigned()),
                    Err(_) => None,
                };
                {
                    let mut report = shared.report();
                    if let Some(entries) = entries {
                        report.entries = entries;
                    }
                    report.journal_lag = lag;
                }
                if applied > 0 {
                    tracing::trace!(applied, "journal records applied");
                } else if asked.elapsed() < QUICK_EMPTY_READ {
                    std::thread::sleep(EMPTY_READ_PAUSE);
                }
            }
            Err(ReadError::Purged) => {
                return Follow::Rebuild(
                    "the change journal dropped records the index had not read".to_owned(),
                );
            }
            Err(ReadError::NotActive) => {
                return Follow::Failed("the change journal was deleted".to_owned());
            }
            Err(ReadError::Other(error)) => {
                match volume.journal() {
                    Ok(now) if now.id != journal.id || next < now.first_usn => {
                        return Follow::Rebuild("the change journal was replaced".to_owned());
                    }
                    Err(error) if win::is_no_journal(&error) => {
                        return Follow::Failed("the change journal was deleted".to_owned());
                    }
                    _ => {}
                }
                failures += 1;
                tracing::warn!(%error, failures, "reading the change journal failed");
                if failures >= MAX_READ_FAILURES {
                    return Follow::Failed(format!(
                        "reading the change journal keeps failing: {error}"
                    ));
                }
                std::thread::sleep(Duration::from_secs(1));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roots_are_normalized_to_a_drive_path() {
        assert_eq!(normalize_root("C:"), Some(('C', r"C:\".to_owned())));
        assert_eq!(normalize_root(r"c:\"), Some(('C', r"C:\".to_owned())));
        assert_eq!(
            normalize_root(r"d:\Users\me\"),
            Some(('D', r"D:\Users\me".to_owned()))
        );
        assert_eq!(normalize_root("e:/a//b"), Some(('E', r"E:\a\b".to_owned())));
        assert_eq!(normalize_root(r"\\?\F:\x"), Some(('F', r"F:\x".to_owned())));
        assert_eq!(normalize_root("relative"), None);
        assert_eq!(normalize_root(r"C:relative"), None);
        assert_eq!(normalize_root(r"\\server\share"), None);
        assert_eq!(normalize_root(""), None);
    }

    #[test]
    fn a_volume_that_cannot_be_read_fails_with_a_reason() {
        if win::is_elevated() {
            return;
        }
        let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
        let letter = system.chars().next().unwrap();
        let indexes = Indexes::start(&[letter]);
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let report = indexes.status().remove(0);
            if let VolumeState::Failed(message) = report.state {
                assert!(message.contains("Administrator"), "{message}");
                break;
            }
            assert!(Instant::now() < deadline, "{report:?}");
            std::thread::sleep(Duration::from_millis(20));
        }
        let matcher = Matcher::new("x").unwrap();
        let outcome = indexes.search(&matcher, None, 10).unwrap();
        assert!(outcome.hits.is_empty() && !outcome.complete);
        assert!(matches!(
            indexes.search(&matcher, Some(r"Q:\x"), 10),
            Err(SearchError::NotIndexed('Q'))
        ));
        assert!(matches!(
            indexes.search(&matcher, Some("nowhere"), 10),
            Err(SearchError::BadRoot(_))
        ));
    }
}
