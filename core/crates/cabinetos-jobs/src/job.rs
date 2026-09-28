//! One job's state, shared by its threads, the progress publisher and the
//! requests that control it.

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Condvar, Mutex, MutexGuard, OnceLock, PoisonError};
use std::time::Instant;

use cabinetos_protocol::{
    Conflict, ConflictKind, JobInfo, JobProgress, JobRequest, JobState, Rate, Resolution,
};

use crate::progress::Meter;
use crate::scheduler::Disk;

/// Locks a mutex, ignoring poisoning: a panicking job thread stops the core
/// anyway (fail fast), so the data is never used half-updated for long.
pub(crate) fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Where a job is, apart from being paused.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Phase {
    Queued,
    Scanning,
    Running,
    Done(JobState),
}

/// What the work of a job acts on: an index into one list of its plan.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Target {
    Dir(usize),
    File(usize),
    Rename(usize),
    Remove(usize),
    Recycle(usize),
}

/// One piece of work, with the decision that sends it again after a
/// conflict.
#[derive(Clone, Debug)]
pub(crate) struct Work {
    pub(crate) target: Target,
    pub(crate) resolution: Option<Resolution>,
    /// The kind of conflict `resolution` answers. Overwrite clears a
    /// read-only attribute only when it answers `access_denied`.
    pub(crate) answers: Option<&'static str>,
    /// A folder of a permanent delete that was tried once while not empty.
    pub(crate) last_try: bool,
}

impl Work {
    pub(crate) fn new(target: Target) -> Self {
        Self {
            target,
            resolution: None,
            answers: None,
            last_try: false,
        }
    }

    /// Whether the work may clear a read-only attribute: it answers an
    /// `access_denied` conflict with overwrite.
    pub(crate) fn may_clear_read_only(&self) -> bool {
        matches!(self.resolution, Some(Resolution::Overwrite))
            && self.answers == Some("access_denied")
    }
}

/// A file set aside for a decision.
#[derive(Debug)]
pub(crate) struct Parked {
    pub(crate) work: Work,
    pub(crate) conflict: Conflict,
}

/// Where a destination folder of a copy or move stands.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum DirState {
    /// Not handled yet.
    Pending,
    /// Created by this job.
    Created,
    /// Already there; the job merges into it.
    Existed,
    /// Waiting for a conflict decision.
    Parked,
    /// Waiting for its own folder.
    Held,
    Skipped,
    Failed,
}

/// A destination folder of a copy or move, by plan index.
#[derive(Clone, Debug)]
pub(crate) struct DirStatus {
    pub(crate) state: DirState,
    /// Its path, once it exists.
    pub(crate) destination: String,
}

/// The job's work: what is left to do, what waits for a decision, and
/// what waits for its folder, with the folders' states. One lock, so no
/// work is lost between them.
#[derive(Debug, Default)]
pub(crate) struct Queue {
    pub(crate) items: VecDeque<Work>,
    pub(crate) in_flight: usize,
    pub(crate) parked: BTreeMap<u64, Parked>,
    /// Work waiting for its folder (by folder index) to exist.
    pub(crate) held: HashMap<usize, Vec<Work>>,
    /// Decisions to apply to later conflicts, by conflict kind.
    pub(crate) policies: HashMap<&'static str, Resolution>,
    pub(crate) dirs: Vec<DirStatus>,
}

/// Pause and cancel.
#[derive(Debug, Default)]
pub(crate) struct Control {
    paused: Mutex<bool>,
    resumed: Condvar,
    cancelled: AtomicBool,
    /// The job paused itself because a disk is full.
    pub(crate) paused_by_disk_full: AtomicBool,
}

impl Control {
    pub(crate) fn is_paused(&self) -> bool {
        *lock(&self.paused)
    }

    pub(crate) fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    pub(crate) fn pause(&self) {
        *lock(&self.paused) = true;
    }

    pub(crate) fn resume(&self) {
        *lock(&self.paused) = false;
        self.paused_by_disk_full.store(false, Ordering::SeqCst);
        self.resumed.notify_all();
    }

    pub(crate) fn cancel(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
        self.resumed.notify_all();
    }

    /// Blocks while the job is paused. Returns `false` once it is cancelled.
    pub(crate) fn wait_while_paused(&self) -> bool {
        let mut paused = lock(&self.paused);
        while *paused && !self.is_cancelled() {
            paused = self
                .resumed
                .wait(paused)
                .unwrap_or_else(PoisonError::into_inner);
        }
        !self.is_cancelled()
    }
}

/// The numbers of `job_progress`.
#[derive(Debug, Default)]
pub(crate) struct Counters {
    pub(crate) bytes_done: AtomicU64,
    pub(crate) bytes_total: AtomicU64,
    pub(crate) files_done: AtomicU64,
    pub(crate) files_total: AtomicU64,
    pub(crate) files_skipped: AtomicU64,
    pub(crate) files_failed: AtomicU64,
    pub(crate) conflicts_open: AtomicU64,
}

impl Counters {
    pub(crate) fn add(counter: &AtomicU64, amount: u64) {
        counter.fetch_add(amount, Ordering::Relaxed);
    }

    pub(crate) fn subtract(counter: &AtomicU64, amount: u64) {
        let _ = counter.fetch_update(Ordering::Relaxed, Ordering::Relaxed, |value| {
            Some(value.saturating_sub(amount))
        });
    }

    pub(crate) fn get(counter: &AtomicU64) -> u64 {
        counter.load(Ordering::Relaxed)
    }
}

/// What the progress publisher remembers about a job.
#[derive(Debug, Default)]
pub(crate) struct Emitter {
    pub(crate) last_emit: Option<Instant>,
    /// The last progress sent, to send only changes.
    pub(crate) last_sent: Option<JobProgress>,
    /// Bytes per second.
    pub(crate) meter: Meter,
    /// Files and folders per second.
    pub(crate) items: Meter,
    /// The final progress went out; nothing more is sent.
    pub(crate) finished: bool,
}

/// One job.
#[derive(Debug)]
pub(crate) struct Job {
    pub(crate) id: u64,
    pub(crate) request: JobRequest,
    pub(crate) disks: Vec<Disk>,
    /// Every disk it touches is solid state: several files may be in
    /// flight.
    pub(crate) solid_state_only: bool,
    /// For a move: which sources are on the destination's volume.
    pub(crate) same_volume: Vec<bool>,
    pub(crate) phase: Mutex<Phase>,
    pub(crate) control: Control,
    pub(crate) counters: Counters,
    pub(crate) current_path: Mutex<Option<String>>,
    pub(crate) started: OnceLock<Instant>,
    /// Frozen when the job ends.
    pub(crate) final_elapsed_ms: OnceLock<u64>,
    pub(crate) queue: Mutex<Queue>,
    pub(crate) work_ready: Condvar,
    pub(crate) emitter: Mutex<Emitter>,
    /// The last state announced with `job_state_changed`.
    pub(crate) announced: Mutex<Option<JobState>>,
}

impl Job {
    pub(crate) fn new(
        id: u64,
        request: JobRequest,
        disks: Vec<Disk>,
        same_volume: Vec<bool>,
    ) -> Self {
        let solid_state_only = disks.iter().all(|disk| disk.solid_state);
        Self {
            id,
            request,
            disks,
            solid_state_only,
            same_volume,
            phase: Mutex::new(Phase::Queued),
            control: Control::default(),
            counters: Counters::default(),
            current_path: Mutex::new(None),
            started: OnceLock::new(),
            final_elapsed_ms: OnceLock::new(),
            queue: Mutex::new(Queue::default()),
            work_ready: Condvar::new(),
            emitter: Mutex::new(Emitter::default()),
            announced: Mutex::new(None),
        }
    }

    /// The state clients see.
    pub(crate) fn state(&self) -> JobState {
        match &*lock(&self.phase) {
            Phase::Done(state) => state.clone(),
            _ if self.control.is_paused() => JobState::Paused,
            Phase::Queued => JobState::Queued,
            Phase::Scanning => JobState::Scanning,
            Phase::Running => JobState::Running,
        }
    }

    pub(crate) fn is_done(&self) -> bool {
        matches!(*lock(&self.phase), Phase::Done(_))
    }

    pub(crate) fn elapsed_ms(&self) -> u64 {
        if let Some(frozen) = self.final_elapsed_ms.get() {
            return *frozen;
        }
        self.started.get().map_or(0, |started| {
            u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX)
        })
    }

    /// A progress record with the given speed, pace and time left.
    pub(crate) fn progress(
        &self,
        speed_bps: u64,
        items_per_second: Option<Rate>,
        eta_seconds: Option<u64>,
    ) -> JobProgress {
        let counters = &self.counters;
        JobProgress {
            job_id: self.id,
            state: self.state(),
            bytes_done: Counters::get(&counters.bytes_done),
            bytes_total: Counters::get(&counters.bytes_total),
            files_done: Counters::get(&counters.files_done),
            files_total: Counters::get(&counters.files_total),
            files_skipped: Counters::get(&counters.files_skipped),
            files_failed: Counters::get(&counters.files_failed),
            conflicts_open: Counters::get(&counters.conflicts_open),
            current_path: lock(&self.current_path).clone(),
            speed_bps,
            items_per_second,
            eta_seconds,
            elapsed_ms: self.elapsed_ms(),
        }
    }

    /// The job as `list_jobs` shows it.
    pub(crate) fn info(&self) -> JobInfo {
        let (speed, pace, eta) = {
            let emitter = lock(&self.emitter);
            emitter.last_sent.as_ref().map_or((0, None, None), |sent| {
                (sent.speed_bps, sent.items_per_second, sent.eta_seconds)
            })
        };
        JobInfo {
            kind: self.request.kind.clone(),
            sources: self.request.sources.clone(),
            destination: self.request.destination.clone(),
            progress: self.progress(speed, pace, eta),
        }
    }

    /// The waiting conflicts, oldest first.
    pub(crate) fn open_conflicts(&self) -> Vec<Conflict> {
        lock(&self.queue)
            .parked
            .values()
            .map(|parked| parked.conflict.clone())
            .collect()
    }
}

/// Whether a resolution can be kept as the job's rule for a conflict kind.
pub(crate) fn as_policy(resolution: &Resolution) -> Option<Resolution> {
    match resolution {
        Resolution::Overwrite
        | Resolution::Skip
        | Resolution::Retry
        | Resolution::DeletePermanently => Some(resolution.clone()),
        // A chosen name fits one file only; the rule picks free names.
        Resolution::Rename { .. } => Some(Resolution::Rename { new_name: None }),
        Resolution::CancelJob => None,
    }
}

/// The conflict kind of a waiting file, for `apply_to_same_kind`.
pub(crate) fn kind_tag(conflict: &Conflict) -> &'static str {
    ConflictKind::tag(&conflict.kind)
}
