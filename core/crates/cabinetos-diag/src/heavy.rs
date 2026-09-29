//! Heavy mode: every event at every level, into `heavy-<process>.<date>.jsonl`
//! next to the normal log, while `logging.heavy` is on (docs/diagnostics.md,
//! "Heavy mode").
//!
//! The queue is counted in bytes, not lines. Up to [`HEAVY_QUEUE_CAP`] a
//! thread that logs hands its line over and goes on, as in normal mode.
//! Above it, the thread waits until the writer has written enough: no line
//! is ever dropped for being late, at the cost of a slower operation, and
//! the wait is written into the file afterwards (`heavy log waited`). This
//! is the one exception to Article 12's "logging never blocks the main I/O
//! pipeline", decided by the creator for heavy mode only (ADR 0013). A
//! thread marked with [`never_wait_for_heavy_log`] (the core's async
//! workers, which read and answer the pipe; the window's UI thread) never
//! waits: its lines may go [`HEAVY_NEVER_WAIT_EXTRA`] over the cap, and
//! beyond that they are dropped and counted.
//!
//! A file holds at most [`HEAVY_FILE_BYTES`]; then the next part starts
//! (`heavy-core.2026-09-29.1.jsonl`). After each new file and each
//! [`HEAVY_CAP_CHECK_BYTES`] written, the heavy files of every process in
//! the folder are measured, and the oldest are deleted until they hold at
//! most [`HEAVY_DISK_CAP`]. A file another process still writes cannot be
//! deleted (it is opened without delete sharing) and is left for later.

use std::cell::Cell;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, PoisonError};
use std::time::{Duration, Instant};

use crossbeam_channel::{Receiver, Sender};
use serde_json::{Map, Value};
use tracing::subscriber::Interest;
use tracing::{Event, Metadata, Subscriber};
use tracing_subscriber::layer::{Context, Filter};
use tracing_subscriber::registry::LookupSpan;

use crate::format::{Around, is_id_span, render_event, render_note};
use crate::{Boundary, clock};

/// Bytes of lines that may wait for the heavy writer before a thread that
/// logs waits for it: 256 MiB.
pub const HEAVY_QUEUE_CAP: u64 = 256 * 1024 * 1024;

/// How far the lines of threads that never wait may go over
/// [`HEAVY_QUEUE_CAP`] before they are dropped: 32 MiB.
pub const HEAVY_NEVER_WAIT_EXTRA: u64 = 32 * 1024 * 1024;

/// The most one heavy file holds before the next part starts: 256 MiB.
pub const HEAVY_FILE_BYTES: u64 = 256 * 1024 * 1024;

/// The most the heavy files of every process in the log folder may hold
/// together: 2 GiB. The oldest are deleted first.
pub const HEAVY_DISK_CAP: u64 = 2 * 1024 * 1024 * 1024;

/// How often, in bytes written, the folder is measured against
/// [`HEAVY_DISK_CAP`]: every 64 MiB (and at each new file).
pub const HEAVY_CAP_CHECK_BYTES: u64 = 64 * 1024 * 1024;

/// Heavy files are named `heavy-<process>.<date>.jsonl`, and a further
/// part of the same day `heavy-<process>.<date>.<n>.jsonl`. The process
/// name comes second: the normal log's writer deletes old files by the
/// process name at the start of the file name.
pub const HEAVY_FILE_PREFIX: &str = "heavy-";

/// The targets of lines only heavy mode writes (payloads, file entries,
/// host calls, network requests) start with this; the normal log leaves
/// them out whatever its level.
pub const HEAVY_TARGET_PREFIX: &str = "heavy::";

/// Environment variable that turns heavy mode on (`1`) or off (`0`) for the
/// process, whatever its configuration says.
pub const LOG_HEAVY_ENV: &str = "CABINETOS_LOG_HEAVY";

/// The heavy writer's thread.
pub(crate) const WRITER_THREAD: &str = "cabinetos-heavy-log";

/// A batch the writer writes at once.
const BATCH_BYTES: usize = 1024 * 1024;

/// How long a waiting thread sleeps before it looks again, in case a wake-up
/// crossed its wait.
const WAIT_SLICE: Duration = Duration::from_millis(50);

/// Whether heavy mode is on.
static ON: AtomicBool = AtomicBool::new(false);

thread_local! {
    static NEVER_WAIT: Cell<bool> = const { Cell::new(false) };
}

/// Marks the calling thread as one that never waits for the heavy writer:
/// the core's async workers, which read and answer the pipe, and the
/// window's UI thread. Its lines are dropped and counted instead once the
/// queue is [`HEAVY_NEVER_WAIT_EXTRA`] over its cap.
pub fn never_wait_for_heavy_log() {
    NEVER_WAIT.set(true);
}

/// Whether heavy mode is on in this process. Code that builds a large
/// heavy-only line (a payload) checks it first.
#[must_use]
pub fn heavy_enabled() -> bool {
    ON.load(Ordering::Relaxed)
}

pub(crate) fn set_on(on: bool) -> bool {
    ON.swap(on, Ordering::SeqCst)
}

/// The sizes heavy mode keeps to; the constants above, smaller in tests.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Limits {
    pub(crate) queue: u64,
    pub(crate) never_wait_extra: u64,
    pub(crate) file: u64,
    pub(crate) disk: u64,
    pub(crate) check_every: u64,
}

impl Limits {
    pub(crate) const DEFAULT: Self = Self {
        queue: HEAVY_QUEUE_CAP,
        never_wait_extra: HEAVY_NEVER_WAIT_EXTRA,
        file: HEAVY_FILE_BYTES,
        disk: HEAVY_DISK_CAP,
        check_every: HEAVY_CAP_CHECK_BYTES,
    };
}

enum Message {
    Line(String),
    /// Written when it comes: every line before it is in the file.
    Flush(Sender<()>),
    /// Like `Flush`, and the file is closed; the next line opens it again.
    Close(Sender<()>),
}

/// The byte-counted queue between the threads that log and the writer.
pub(crate) struct HeavyQueue {
    sender: Sender<Message>,
    queued: AtomicU64,
    dropped: AtomicU64,
    room: Mutex<()>,
    room_made: Condvar,
    writing: AtomicBool,
    limits: Limits,
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

impl HeavyQueue {
    /// Queues `line`. A thread that may wait waits while the queue is over
    /// its cap, and gets back how long it waited.
    pub(crate) fn push(&self, line: String) -> Option<Duration> {
        let len = line.len() as u64 + 1;
        let mut waited = None;
        if NEVER_WAIT.get() {
            let limit = self.limits.queue + self.limits.never_wait_extra;
            if self.queued.load(Ordering::Acquire) + len > limit {
                self.dropped.fetch_add(1, Ordering::Relaxed);
                return None;
            }
        } else if self.over(len) {
            let started = Instant::now();
            let mut guard = lock(&self.room);
            while self.over(len) {
                guard = self
                    .room_made
                    .wait_timeout(guard, WAIT_SLICE)
                    .unwrap_or_else(PoisonError::into_inner)
                    .0;
            }
            drop(guard);
            waited = Some(started.elapsed());
        }
        self.enqueue(line, len);
        waited
    }

    /// Queues a line the writer itself adds (a note about a wait), which
    /// neither waits nor is dropped.
    pub(crate) fn push_note(&self, line: String) {
        let len = line.len() as u64 + 1;
        self.enqueue(line, len);
    }

    fn enqueue(&self, line: String, len: u64) {
        self.queued.fetch_add(len, Ordering::AcqRel);
        if self.sender.send(Message::Line(line)).is_err() {
            self.queued.fetch_sub(len, Ordering::AcqRel);
        }
    }

    /// Whether a line of `len` bytes must wait: the queue holds lines and
    /// would go over its cap. A line larger than the cap alone never waits
    /// for an empty queue, and nothing waits for a writer that has ended.
    fn over(&self, len: u64) -> bool {
        let queued = self.queued.load(Ordering::Acquire);
        queued > 0 && queued + len > self.limits.queue && self.writing.load(Ordering::Acquire)
    }

    /// Asks the writer to write every queued line (and close the file, with
    /// `close`), waiting at most `timeout`. Returns whether it answered.
    pub(crate) fn flush(&self, close: bool, timeout: Duration) -> bool {
        let (ack, answered) = crossbeam_channel::bounded(1);
        let message = if close {
            Message::Close(ack)
        } else {
            Message::Flush(ack)
        };
        self.sender.send(message).is_ok() && answered.recv_timeout(timeout).is_ok()
    }

    fn written(&self, bytes: u64) {
        self.queued.fetch_sub(bytes, Ordering::AcqRel);
        // Taking the lock orders this wake-up after a waiter's check.
        drop(lock(&self.room));
        self.room_made.notify_all();
    }
}

/// The heavy writer: its queue, and its thread.
pub(crate) struct Heavy {
    pub(crate) queue: Arc<HeavyQueue>,
}

impl Heavy {
    /// Starts the writer thread for `process`'s heavy files in `dir`.
    pub(crate) fn start(
        dir: PathBuf,
        process: &'static str,
        boundary: Boundary,
        limits: Limits,
    ) -> std::io::Result<Self> {
        Self::start_with(
            HeavyFiles::new(dir, process, limits),
            boundary,
            limits,
            Box::new(|_| {}),
        )
    }

    /// Starts the writer with `files`; `pause` runs before each batch (a
    /// slow disk, in tests).
    pub(crate) fn start_with(
        files: HeavyFiles,
        boundary: Boundary,
        limits: Limits,
        pause: Box<dyn Fn(usize) + Send>,
    ) -> std::io::Result<Self> {
        let (sender, receiver) = crossbeam_channel::unbounded();
        let queue = Arc::new(HeavyQueue {
            sender,
            queued: AtomicU64::new(0),
            dropped: AtomicU64::new(0),
            room: Mutex::new(()),
            room_made: Condvar::new(),
            writing: AtomicBool::new(true),
            limits,
        });
        let writer_queue = Arc::clone(&queue);
        std::thread::Builder::new()
            .name(WRITER_THREAD.to_owned())
            .spawn(move || {
                write_loop(&writer_queue, &receiver, files, boundary, &*pause);
                writer_queue.writing.store(false, Ordering::Release);
                writer_queue.written(0);
            })?;
        Ok(Self { queue })
    }
}

/// The writer thread: batches of lines into the files, until every sender
/// is gone.
fn write_loop(
    queue: &HeavyQueue,
    receiver: &Receiver<Message>,
    mut files: HeavyFiles,
    boundary: Boundary,
    pause: &dyn Fn(usize),
) {
    // What it logs itself (files deleted for the cap) must not wait for
    // itself.
    never_wait_for_heavy_log();
    let mut batch: Vec<u8> = Vec::with_capacity(BATCH_BYTES);
    while let Ok(first) = receiver.recv() {
        let mut acks = Vec::new();
        let mut close = false;
        let mut lines_bytes = 0;
        let mut take = |message: Message, batch: &mut Vec<u8>| match message {
            Message::Line(line) => {
                lines_bytes += line.len() as u64 + 1;
                batch.extend_from_slice(line.as_bytes());
                batch.push(b'\n');
            }
            Message::Flush(ack) => acks.push(ack),
            Message::Close(ack) => {
                acks.push(ack);
                close = true;
            }
        };
        take(first, &mut batch);
        while batch.len() < BATCH_BYTES {
            match receiver.try_recv() {
                Ok(message) => take(message, &mut batch),
                Err(_) => break,
            }
        }
        let dropped = queue.dropped.swap(0, Ordering::Relaxed);
        if dropped > 0 {
            let mut fields = Map::new();
            fields.insert("dropped".to_owned(), Value::from(dropped));
            let note = render_note(
                Around::default(),
                boundary,
                ("WARN", "cabinetos_diag::heavy"),
                "heavy log dropped lines of threads that never wait",
                fields,
            );
            batch.extend_from_slice(note.as_bytes());
            batch.push(b'\n');
        }
        if !batch.is_empty() {
            pause(batch.len());
            files.write(&batch);
            batch.clear();
        }
        queue.written(lines_bytes);
        // A line that got past the switch just before heavy mode went off
        // opened the file again: it must not stay open.
        if close || !heavy_enabled() {
            files.close();
        }
        for ack in acks {
            let _ = ack.send(());
        }
    }
    files.close();
}

/// The file being written, with its date and size.
struct Current {
    file: File,
    path: PathBuf,
    date: String,
    size: u64,
}

/// The heavy files of one process: the one written now, the next part, the
/// next day, and the folder's cap.
pub(crate) struct HeavyFiles {
    dir: PathBuf,
    process: &'static str,
    limits: Limits,
    current: Option<Current>,
    since_check: u64,
}

impl HeavyFiles {
    pub(crate) fn new(dir: PathBuf, process: &'static str, limits: Limits) -> Self {
        Self {
            dir,
            process,
            limits,
            current: None,
            since_check: 0,
        }
    }

    /// Appends `bytes` in one write. A write that fails (a full disk)
    /// closes the file; the next batch tries again.
    fn write(&mut self, bytes: &[u8]) {
        let today = clock::date(clock::now());
        let full = self
            .current
            .as_ref()
            .is_none_or(|current| current.date != today || current.size >= self.limits.file);
        if full {
            self.current = None;
            match self.open(&today) {
                Ok(current) => self.current = Some(current),
                Err(error) => {
                    tracing::warn!(%error, dir = %self.dir.display(), "cannot open the heavy log file");
                    return;
                }
            }
            self.enforce_cap();
        }
        let Some(current) = self.current.as_mut() else {
            return;
        };
        if let Err(error) = current.file.write_all(bytes) {
            tracing::warn!(%error, path = %current.path.display(), "cannot write the heavy log file");
            self.current = None;
            return;
        }
        let written = bytes.len() as u64;
        current.size += written;
        self.since_check += written;
        if self.since_check >= self.limits.check_every {
            self.enforce_cap();
        }
    }

    fn close(&mut self) {
        self.current = None;
    }

    /// Opens the first part of `date` that is not full yet.
    fn open(&self, date: &str) -> std::io::Result<Current> {
        std::fs::create_dir_all(&self.dir)?;
        let mut part = 0_u32;
        loop {
            let path = self.dir.join(heavy_file_name(self.process, date, part));
            let size = std::fs::metadata(&path).map_or(0, |metadata| metadata.len());
            if size < self.limits.file {
                // Append-only (FILE_APPEND_DATA), so another process's
                // lines never overwrite these; no delete sharing, so no
                // other process's cap deletes the file while it is written.
                let file = OpenOptions::new()
                    .append(true)
                    .create(true)
                    .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
                    .open(&path)?;
                return Ok(Current {
                    file,
                    path,
                    date: date.to_owned(),
                    size,
                });
            }
            part += 1;
        }
    }

    fn enforce_cap(&mut self) {
        self.since_check = 0;
        let keep = self.current.as_ref().map(|current| current.path.as_path());
        let deleted = enforce_disk_cap(&self.dir, self.limits.disk, keep);
        if !deleted.is_empty() {
            let freed: u64 = deleted.iter().map(|(_, size)| size).sum();
            let names: Vec<&str> = deleted.iter().map(|(name, _)| name.as_str()).collect();
            tracing::info!(
                deleted = %names.join(", "),
                freed_bytes = freed,
                cap_bytes = self.limits.disk,
                "heavy log files deleted to keep the folder under its cap"
            );
        }
    }
}

const FILE_SHARE_READ: u32 = 0x1;
const FILE_SHARE_WRITE: u32 = 0x2;

/// `heavy-core.2026-09-29.jsonl`, and `heavy-core.2026-09-29.3.jsonl` for
/// part 3.
pub(crate) fn heavy_file_name(process: &str, date: &str, part: u32) -> String {
    if part == 0 {
        format!("{HEAVY_FILE_PREFIX}{process}.{date}.jsonl")
    } else {
        format!("{HEAVY_FILE_PREFIX}{process}.{date}.{part}.jsonl")
    }
}

/// Deletes the oldest heavy files in `dir` (by date, then part) until all
/// of them hold at most `cap` bytes. `keep` (the file this process writes)
/// and files another process holds open are never deleted. Returns each
/// deleted file's name and size.
pub(crate) fn enforce_disk_cap(dir: &Path, cap: u64, keep: Option<&Path>) -> Vec<(String, u64)> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut files: Vec<((String, u32), String, PathBuf, u64)> = entries
        .flatten()
        .filter_map(|entry| {
            let name = entry.file_name().into_string().ok()?;
            let stem = name
                .strip_prefix(HEAVY_FILE_PREFIX)?
                .strip_suffix(".jsonl")?;
            let (_, stamp) = stem.split_once('.')?;
            let order = match stamp.split_once('.') {
                Some((date, part)) => (date.to_owned(), part.parse().unwrap_or(0)),
                None => (stamp.to_owned(), 0),
            };
            let size = entry.metadata().ok()?.len();
            Some((order, name, entry.path(), size))
        })
        .collect();
    let mut total: u64 = files.iter().map(|(_, _, _, size)| size).sum();
    if total <= cap {
        return Vec::new();
    }
    files.sort();
    let mut deleted = Vec::new();
    for (_, name, path, size) in files {
        if total <= cap {
            break;
        }
        if keep.is_some_and(|keep| keep == path) {
            continue;
        }
        if std::fs::remove_file(&path).is_ok() {
            total -= size;
            deleted.push((name, size));
        }
    }
    deleted
}

/// The heavy layer's filter: every event and span while heavy mode is on,
/// and the spans that carry IDs always, so a span made before the switch
/// still gives its IDs to the lines inside it.
pub(crate) struct HeavyFilter;

impl<S> Filter<S> for HeavyFilter {
    fn enabled(&self, metadata: &Metadata<'_>, _: &Context<'_, S>) -> bool {
        heavy_enabled() || is_id_span(metadata)
    }

    fn callsite_enabled(&self, metadata: &'static Metadata<'static>) -> Interest {
        if heavy_enabled() || is_id_span(metadata) {
            Interest::always()
        } else {
            Interest::never()
        }
    }
}

/// The process's heavy writer, once heavy mode was first turned on; its
/// error when the writer's thread could not start.
pub(crate) static HEAVY: std::sync::OnceLock<Result<Heavy, String>> = std::sync::OnceLock::new();

/// Writes every event it sees into the heavy queue.
pub(crate) struct HeavyLayer {
    pub(crate) boundary: Boundary,
}

impl<S> tracing_subscriber::Layer<S> for HeavyLayer
where
    S: Subscriber + for<'a> LookupSpan<'a>,
{
    fn on_event(&self, event: &Event<'_>, ctx: Context<'_, S>) {
        if !heavy_enabled() {
            return;
        }
        let Some(Ok(heavy)) = HEAVY.get() else {
            return;
        };
        let around = Around::of(ctx.event_scope(event));
        let line = render_event(event, around.clone(), self.boundary);
        if let Some(waited) = heavy.queue.push(line) {
            let mut fields = Map::new();
            fields.insert(
                "waited_ms".to_owned(),
                Value::from(u64::try_from(waited.as_millis()).unwrap_or(u64::MAX)),
            );
            heavy.queue.push_note(render_note(
                around,
                self.boundary,
                ("WARN", "cabinetos_diag::heavy"),
                "heavy log waited",
                fields,
            ));
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::AtomicUsize;

    use super::*;

    fn limits(queue: u64) -> Limits {
        Limits {
            queue,
            never_wait_extra: 1024,
            file: 1024 * 1024,
            disk: u64::MAX,
            check_every: u64::MAX,
        }
    }

    fn read_lines(dir: &Path) -> Vec<String> {
        let mut names: Vec<PathBuf> = std::fs::read_dir(dir)
            .unwrap()
            .map(|entry| entry.unwrap().path())
            .collect();
        names.sort();
        names
            .iter()
            .flat_map(|path| {
                std::fs::read_to_string(path)
                    .unwrap()
                    .lines()
                    .map(str::to_owned)
                    .collect::<Vec<_>>()
            })
            .collect()
    }

    #[test]
    fn a_thread_waits_for_a_slow_writer_and_no_line_is_lost() {
        let dir = tempfile::tempdir().unwrap();
        let batches = Arc::new(AtomicUsize::new(0));
        let counted = Arc::clone(&batches);
        let heavy = Heavy::start_with(
            HeavyFiles::new(dir.path().to_path_buf(), "test", limits(2048)),
            Boundary::Engine,
            limits(2048),
            Box::new(move |_| {
                counted.fetch_add(1, Ordering::Relaxed);
                std::thread::sleep(Duration::from_millis(5));
            }),
        )
        .unwrap();
        let mut waits = 0;
        let mut longest = Duration::ZERO;
        for n in 0..400 {
            if let Some(waited) = heavy
                .queue
                .push(format!("{{\"n\":{n},\"pad\":\"{}\"}}", "x".repeat(90)))
            {
                waits += 1;
                longest = longest.max(waited);
            }
        }
        assert!(heavy.queue.flush(true, Duration::from_secs(10)));
        assert!(waits > 0, "400 lines of 100 bytes do not fit a 2 KiB queue");
        assert!(longest > Duration::ZERO);
        let lines = read_lines(dir.path());
        let numbers: Vec<u64> = lines
            .iter()
            .map(|line| {
                serde_json::from_str::<Value>(line).unwrap()["n"]
                    .as_u64()
                    .unwrap()
            })
            .collect();
        assert_eq!(
            numbers,
            (0..400).collect::<Vec<_>>(),
            "every line, in order"
        );
        assert!(batches.load(Ordering::Relaxed) > 1);
    }

    #[test]
    fn a_thread_that_never_waits_drops_and_the_count_is_written() {
        let dir = tempfile::tempdir().unwrap();
        let (release, gate) = crossbeam_channel::bounded::<()>(2);
        let heavy = Heavy::start_with(
            HeavyFiles::new(dir.path().to_path_buf(), "test", limits(1000)),
            Boundary::Engine,
            limits(1000),
            // The writer is stuck until the test lets it go.
            Box::new(move |_| {
                let _ = gate.recv_timeout(Duration::from_secs(10));
            }),
        )
        .unwrap();
        let queue = Arc::clone(&heavy.queue);
        let started = Instant::now();
        let kept = std::thread::spawn(move || {
            never_wait_for_heavy_log();
            (0..100)
                .filter(|n| queue.push(format!("{n:0>99}")).is_none())
                .count()
        })
        .join()
        .unwrap();
        assert!(
            started.elapsed() < Duration::from_secs(5),
            "it never waited"
        );
        assert_eq!(kept, 100);
        let _ = release.send(());
        let _ = release.send(());
        assert!(heavy.queue.flush(true, Duration::from_secs(10)));
        let lines: Vec<Value> = read_lines(dir.path())
            .iter()
            .map(|line| serde_json::from_str(line).unwrap_or(Value::Null))
            .collect();
        let notes: Vec<&Value> = lines
            .iter()
            .filter(|line| line["message"] == "heavy log dropped lines of threads that never wait")
            .collect();
        assert!(!notes.is_empty());
        let dropped: u64 = notes
            .iter()
            .map(|note| note["fields"]["dropped"].as_u64().unwrap())
            .sum();
        // 1,000 bytes of cap and 1,024 more for such threads hold 20 lines
        // of 100 bytes: the rest is dropped and counted.
        assert_eq!(dropped, 80);
        assert_eq!(lines.len() - notes.len(), 20);
    }

    #[test]
    fn a_full_file_continues_in_the_next_part() {
        let dir = tempfile::tempdir().unwrap();
        let mut files = HeavyFiles::new(
            dir.path().to_path_buf(),
            "core",
            Limits {
                file: 100,
                ..limits(1024)
            },
        );
        for _ in 0..3 {
            files.write(&[b'x'; 60]);
        }
        files.close();
        let today = clock::date(clock::now());
        let sizes: Vec<(String, u64)> = [0, 1]
            .iter()
            .map(|part| {
                let name = heavy_file_name("core", &today, *part);
                let size = std::fs::metadata(dir.path().join(&name)).unwrap().len();
                (name, size)
            })
            .collect();
        assert_eq!(sizes[0].1, 120, "a file takes whole batches up to its size");
        assert_eq!(sizes[1].1, 60);
        assert!(sizes[1].0.ends_with(".1.jsonl"), "{}", sizes[1].0);
    }

    #[test]
    fn the_oldest_heavy_files_go_first_and_the_open_one_stays() {
        let dir = tempfile::tempdir().unwrap();
        let write = |name: &str, size: usize| {
            std::fs::write(dir.path().join(name), vec![b'x'; size]).unwrap();
        };
        write("heavy-core.2026-09-20.jsonl", 400);
        write("heavy-ui.2026-09-21.jsonl", 300);
        write("heavy-core.2026-09-21.jsonl", 300);
        write("heavy-core.2026-09-21.1.jsonl", 300);
        write("heavy-core.2026-09-22.jsonl", 500);
        write("core.2026-09-20.jsonl", 5000);
        write("crash-20260920T000000000Z.json", 5000);
        let current = dir.path().join("heavy-core.2026-09-22.jsonl");

        let deleted = enforce_disk_cap(dir.path(), 900, Some(&current));
        let names: Vec<&str> = deleted.iter().map(|(name, _)| name.as_str()).collect();
        assert_eq!(
            names,
            [
                "heavy-core.2026-09-20.jsonl",
                "heavy-core.2026-09-21.jsonl",
                "heavy-ui.2026-09-21.jsonl",
            ]
        );
        for kept in [
            "heavy-core.2026-09-21.1.jsonl",
            "heavy-core.2026-09-22.jsonl",
            "core.2026-09-20.jsonl",
            "crash-20260920T000000000Z.json",
        ] {
            assert!(dir.path().join(kept).exists(), "{kept}");
        }
        assert!(enforce_disk_cap(dir.path(), 900, Some(&current)).is_empty());

        // Even the only file left is not deleted while it is written.
        let deleted = enforce_disk_cap(dir.path(), 10, Some(&current));
        assert_eq!(deleted.len(), 1);
        assert!(current.exists());
    }
}
