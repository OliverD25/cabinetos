//! The job engine: copy, move and delete, on per-disk queues, with throttled
//! progress and per-file conflict handling (`docs/jobs.md`).
//!
//! - [`JobQueueManager::start`] checks the paths, finds the physical disks
//!   the job touches, and queues it. The scheduler runs one job at a time on
//!   a spinning disk and several on a solid state disk; a job starts when it
//!   has a slot on every disk it touches.
//! - A running job has its own thread: it walks the sources, creates the
//!   folders, then copies (`CopyFileExW`), renames (`MoveFileExW`) or
//!   deletes (Recycle Bin through `IFileOperation`, or `DeleteFileW`),
//!   several files at a time between solid state disks.
//! - A file that hits a conflict is set aside with an event, and the job
//!   goes on; [`JobQueueManager::resolve`] sends it back.
//! - Progress goes out at most 30 times per second per job.
//!
//! Jobs belong to the manager, not to the client that started them. Events
//! reach the caller through the [`EventSink`] given to
//! [`JobQueueManager::new`].
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: the interface
//! never freezes during heavy file operations; the engine never blocks the
//! caller) and Article 5 (Dual-Pane Foundation: transfers between panes).
//! Brief §3. Unsafe code is allowed only in `win` and `recycle`, each block
//! with a `SAFETY:` comment.

mod job;
mod plan;
pub mod progress;
#[allow(unsafe_code)]
mod recycle;
mod run;
mod scheduler;
#[allow(unsafe_code)]
mod win;

use std::collections::{BTreeMap, HashMap};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, Weak};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use cabinetos_protocol::{
    Conflict, ErrorCode, Event, JobAction, JobInfo, JobKind, JobRequest, JobState, Resolution,
};

use crate::job::{Counters, Job, as_policy, kind_tag, lock};
use crate::scheduler::{Disk, DiskKey, Priority, Scheduler};

/// Receives every event of every job: `job_progress`, `job_conflict`,
/// `job_state_changed`. Called on the engine's threads; it must return
/// quickly.
pub type EventSink = Arc<dyn Fn(Event) + Send + Sync>;

/// How the engine runs.
#[derive(Clone, Debug)]
pub struct EngineConfig {
    /// Jobs a solid state disk runs at once. A spinning disk, or a disk of
    /// unknown kind, runs one.
    pub solid_state_jobs: usize,
    /// Files one job copies at once when every disk it touches is solid
    /// state.
    pub solid_state_files_in_flight: usize,
    /// Files this large and larger are copied without the file cache
    /// (`COPY_FILE_NO_BUFFERING`).
    pub unbuffered_from: u64,
    /// The smallest gap between two progress events of one job.
    pub progress_gap: Duration,
    /// Finished jobs `list_jobs` still shows; older ones are forgotten.
    pub finished_jobs_kept: usize,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            solid_state_jobs: 4,
            solid_state_files_in_flight: 4,
            unbuffered_from: 256 * 1024 * 1024,
            progress_gap: progress::MIN_PROGRESS_GAP,
            finished_jobs_kept: 100,
        }
    }
}

/// Why a request about jobs was refused.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
#[error("{message}")]
pub struct JobError {
    /// The protocol error code.
    pub code: ErrorCode,
    /// What is wrong.
    pub message: String,
}

impl JobError {
    fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

/// Job and conflict IDs are unique for the life of the process.
static NEXT_JOB_ID: AtomicU64 = AtomicU64::new(1);
static NEXT_CONFLICT_ID: AtomicU64 = AtomicU64::new(1);

pub(crate) fn next_conflict_id() -> u64 {
    NEXT_CONFLICT_ID.fetch_add(1, Ordering::Relaxed)
}

/// Owns every job: queues them per disk, runs them, and keeps the finished
/// ones for `list_jobs`.
pub struct JobQueueManager {
    engine: Arc<Engine>,
}

impl std::fmt::Debug for JobQueueManager {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("JobQueueManager").finish_non_exhaustive()
    }
}

/// What the manager's threads share.
pub(crate) struct Engine {
    pub(crate) config: EngineConfig,
    pub(crate) sink: EventSink,
    jobs: Mutex<BTreeMap<u64, Arc<Job>>>,
    scheduler: Mutex<Scheduler>,
    threads: Mutex<Vec<JoinHandle<()>>>,
    /// Wakes the progress publisher; `true` asks it to stop.
    publisher: (Mutex<PublisherState>, Condvar),
    stopping: AtomicBool,
}

#[derive(Default)]
struct PublisherState {
    wake: bool,
    stop: bool,
}

impl Engine {
    pub(crate) fn emit(&self, event: Event) {
        (self.sink)(event);
    }

    /// Sends `job_state_changed` if the state clients see has changed.
    pub(crate) fn announce(&self, job: &Job) {
        let state = job.state();
        let mut announced = lock(&job.announced);
        if announced.as_ref() != Some(&state) {
            *announced = Some(state.clone());
            tracing::debug!(job_id = job.id, state = ?state, "job state");
            self.emit(Event::JobStateChanged {
                job_id: job.id,
                state,
            });
        }
    }

    fn job(&self, id: u64) -> Result<Arc<Job>, JobError> {
        lock(&self.jobs)
            .get(&id)
            .cloned()
            .ok_or_else(|| JobError::new(ErrorCode::NoSuchJob, format!("no job {id}")))
    }

    pub(crate) fn wake_publisher(&self) {
        let (state, wake) = &self.publisher;
        lock(state).wake = true;
        wake.notify_all();
    }

    /// Starts every job the scheduler lets through.
    fn admit(self: &Arc<Self>) {
        if self.stopping.load(Ordering::SeqCst) {
            return;
        }
        let started = lock(&self.scheduler).admit();
        for (id, disks) in started {
            let Ok(job) = self.job(id) else {
                lock(&self.scheduler).release(&disks);
                continue;
            };
            let engine = Arc::clone(self);
            let running = Arc::clone(&job);
            let spawned = std::thread::Builder::new()
                .name(format!("job-{id}"))
                .spawn(move || {
                    let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                        run::run(&engine, &running);
                    }));
                    if outcome.is_err() {
                        // A panic is a bug. The panic hook has written the
                        // crash trace; stop the whole process, as the core
                        // does for any panic (fail fast), instead of leaving
                        // the job's disks locked forever.
                        std::process::abort();
                    }
                    engine.job_ended(&running);
                });
            match spawned {
                Ok(handle) => lock(&self.threads).push(handle),
                Err(error) => {
                    run::finish(
                        self,
                        &job,
                        &JobState::Failed {
                            message: format!("cannot start the job's thread: {error}"),
                        },
                    );
                    lock(&self.scheduler).release(&disks);
                }
            }
        }
        self.wake_publisher();
    }

    fn job_ended(self: &Arc<Self>, job: &Job) {
        lock(&self.scheduler).release(&job.disks);
        self.forget_old_jobs();
        self.admit();
    }

    /// Keeps at most `finished_jobs_kept` finished jobs.
    fn forget_old_jobs(&self) {
        let mut jobs = lock(&self.jobs);
        let finished: Vec<u64> = jobs
            .iter()
            .filter(|(_, job)| job.is_done())
            .map(|(id, _)| *id)
            .collect();
        let excess = finished
            .len()
            .saturating_sub(self.config.finished_jobs_kept);
        for id in finished.into_iter().take(excess) {
            jobs.remove(&id);
        }
        lock(&self.threads).retain(|thread| !thread.is_finished());
    }

    /// The jobs whose progress may change.
    fn active_jobs(&self) -> Vec<Arc<Job>> {
        lock(&self.jobs)
            .values()
            .filter(|job| !job.is_done() && job.started.get().is_some())
            .cloned()
            .collect()
    }
}

/// Sends the progress of the running jobs on a clock, and sleeps while no
/// job runs.
fn publisher(engine: &Weak<Engine>) {
    loop {
        let Some(engine) = engine.upgrade() else {
            return;
        };
        let active = engine.active_jobs();
        for job in &active {
            progress::publish(job, &engine.sink, engine.config.progress_gap, false);
        }
        let (state, wake) = &engine.publisher;
        let mut guard = lock(state);
        if guard.stop {
            return;
        }
        if !guard.wake {
            guard = if active.is_empty() {
                wake.wait(guard)
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
            } else {
                wake.wait_timeout(guard, engine.config.progress_gap)
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .0
            };
        }
        guard.wake = false;
        if guard.stop {
            return;
        }
    }
}

impl JobQueueManager {
    /// A manager whose events go to `sink`.
    #[must_use]
    pub fn new(config: EngineConfig, sink: EventSink) -> Self {
        let engine = Arc::new(Engine {
            scheduler: Mutex::new(Scheduler::new(config.solid_state_jobs)),
            config,
            sink,
            jobs: Mutex::new(BTreeMap::new()),
            threads: Mutex::new(Vec::new()),
            publisher: (Mutex::new(PublisherState::default()), Condvar::new()),
            stopping: AtomicBool::new(false),
        });
        let weak = Arc::downgrade(&engine);
        if let Err(error) = std::thread::Builder::new()
            .name("job-progress".to_owned())
            .spawn(move || publisher(&weak))
        {
            tracing::error!(%error, "cannot start the job progress thread; jobs report only their end");
        }
        Self { engine }
    }

    /// Checks the paths of `request`, finds the disks it touches and queues
    /// it. Returns the job's ID. Blocking: it looks at the file system.
    pub fn start(&self, request: JobRequest) -> Result<u64, JobError> {
        if self.engine.stopping.load(Ordering::SeqCst) {
            return Err(JobError::new(
                ErrorCode::Internal,
                "the core is shutting down",
            ));
        }
        let request = validate(request)?;
        let mut disks = Vec::new();
        let mut volumes: HashMap<String, (Disk, Option<String>)> = HashMap::new();
        let mut volume_of = |path: &str| -> (Disk, Option<String>) {
            // Sources picked together share a folder; look each folder up once.
            let folder = Path::new(path)
                .parent()
                .map_or_else(|| path.to_owned(), |parent| parent.display().to_string());
            volumes
                .entry(folder.to_lowercase())
                .or_insert_with(|| disk_of(path))
                .clone()
        };
        let destination = request.destination.as_deref().map(&mut volume_of);
        let mut same_volume = Vec::new();
        for source in &request.sources {
            let (disk, volume) = volume_of(source);
            same_volume.push(match (&volume, &destination) {
                (Some(source), Some((_, Some(target)))) => source.eq_ignore_ascii_case(target),
                _ => false,
            });
            disks.push(disk);
        }
        if let Some((disk, _)) = destination {
            disks.push(disk);
        }
        let priority = match request.kind {
            JobKind::Delete { .. } => Priority::Quick,
            JobKind::Move if same_volume.iter().all(|same| *same) => Priority::Quick,
            JobKind::Copy | JobKind::Move => Priority::Copy,
        };
        let id = NEXT_JOB_ID.fetch_add(1, Ordering::Relaxed);
        tracing::info!(
            job_id = id,
            kind = ?request.kind,
            sources = request.sources.len(),
            destination = request.destination.as_deref().unwrap_or(""),
            disks = ?disks,
            "job queued"
        );
        let job = Arc::new(Job::new(id, request, disks.clone(), same_volume));
        lock(&self.engine.jobs).insert(id, Arc::clone(&job));
        self.engine.announce(&job);
        lock(&self.engine.scheduler).submit(id, priority, disks);
        self.engine.admit();
        Ok(id)
    }

    /// Every job the manager knows, oldest first.
    #[must_use]
    pub fn list(&self) -> Vec<JobInfo> {
        let jobs: Vec<Arc<Job>> = lock(&self.engine.jobs).values().cloned().collect();
        jobs.iter().map(|job| job.info()).collect()
    }

    /// The conflicts waiting for a decision, in every job, oldest first. A
    /// client that connects later (a restarted UI) needs them to answer.
    #[must_use]
    pub fn open_conflicts(&self) -> Vec<Conflict> {
        let jobs: Vec<Arc<Job>> = lock(&self.engine.jobs).values().cloned().collect();
        jobs.iter().flat_map(|job| job.open_conflicts()).collect()
    }

    /// Pauses, resumes or cancels a job. Doing it to a job that has ended
    /// does nothing.
    pub fn control(&self, job_id: u64, action: JobAction) -> Result<(), JobError> {
        let job = self.engine.job(job_id)?;
        if job.is_done() {
            return Ok(());
        }
        match action {
            JobAction::Pause => {
                job.control.pause();
                lock(&self.engine.scheduler).hold(job_id, true);
                self.engine.announce(&job);
            }
            JobAction::Resume => {
                job.control.resume();
                let waiting = lock(&self.engine.scheduler).hold(job_id, false);
                self.engine.announce(&job);
                if waiting {
                    self.engine.admit();
                }
            }
            JobAction::Cancel => self.cancel(&job),
        }
        Ok(())
    }

    fn cancel(&self, job: &Arc<Job>) {
        job.control.cancel();
        job.work_ready.notify_all();
        if lock(&self.engine.scheduler).withdraw(job.id) {
            // It never started: nobody else will end it.
            run::finish(&self.engine, job, &JobState::Cancelled);
            self.engine.forget_old_jobs();
            self.engine.admit();
        }
    }

    /// Decides what happens to a file that waits on a conflict.
    pub fn resolve(
        &self,
        job_id: u64,
        conflict_id: u64,
        resolution: &Resolution,
        apply_to_same_kind: bool,
    ) -> Result<(), JobError> {
        let job = self.engine.job(job_id)?;
        if let Resolution::Rename {
            new_name: Some(name),
        } = resolution
        {
            check_name(name)?;
        }
        let no_conflict = || {
            JobError::new(
                ErrorCode::NoSuchConflict,
                format!("job {job_id} has no waiting conflict {conflict_id}"),
            )
        };
        if *resolution == Resolution::CancelJob {
            if !lock(&job.queue).parked.contains_key(&conflict_id) {
                return Err(no_conflict());
            }
            self.cancel(&job);
            return Ok(());
        }
        let resolved_disk_full = {
            let mut queue = lock(&job.queue);
            let parked = queue.parked.remove(&conflict_id).ok_or_else(no_conflict)?;
            let tag = kind_tag(&parked.conflict);
            let mut resolved = vec![(parked.work, resolution.clone())];
            if apply_to_same_kind && let Some(rule) = as_policy(resolution) {
                queue.policies.insert(tag, rule.clone());
                let same: Vec<u64> = queue
                    .parked
                    .iter()
                    .filter(|(_, other)| kind_tag(&other.conflict) == tag)
                    .map(|(id, _)| *id)
                    .collect();
                for id in same {
                    if let Some(other) = queue.parked.remove(&id) {
                        resolved.push((other.work, rule.clone()));
                    }
                }
            }
            Counters::subtract(&job.counters.conflicts_open, resolved.len() as u64);
            for (mut work, resolution) in resolved.into_iter().rev() {
                work.resolution = Some(resolution);
                queue.items.push_front(work);
            }
            tag == "disk_full"
        };
        job.work_ready.notify_all();
        tracing::info!(job_id, conflict_id, resolution = ?resolution, apply_to_same_kind, "conflict resolved");
        if resolved_disk_full && job.control.paused_by_disk_full.load(Ordering::SeqCst) {
            job.control.resume();
            self.engine.announce(&job);
        }
        Ok(())
    }

    /// Cancels every job and waits up to `timeout` for the running ones to
    /// stop, so no partly copied file is left behind.
    pub fn shutdown(&self, timeout: Duration) {
        self.engine.stopping.store(true, Ordering::SeqCst);
        let jobs: Vec<Arc<Job>> = lock(&self.engine.jobs).values().cloned().collect();
        for job in jobs.iter().filter(|job| !job.is_done()) {
            self.cancel(job);
        }
        let threads: Vec<JoinHandle<()>> = std::mem::take(&mut *lock(&self.engine.threads));
        let deadline = Instant::now() + timeout;
        for thread in threads {
            while !thread.is_finished() && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(10));
            }
            if thread.is_finished() {
                let _ = thread.join();
            }
        }
        let (state, wake) = &self.engine.publisher;
        lock(state).stop = true;
        wake.notify_all();
    }
}

/// A plain file name for `rename`: no folder part, nothing Windows refuses.
fn check_name(name: &str) -> Result<(), JobError> {
    let bad = name.is_empty()
        || name == "."
        || name == ".."
        || name.ends_with(['.', ' '])
        || name.chars().any(|c| c < ' ' || "\\/:*?\"<>|".contains(c));
    if bad {
        Err(JobError::new(
            ErrorCode::InvalidPath,
            format!("`{name}` is not a valid file name"),
        ))
    } else {
        Ok(())
    }
}

fn lowered(path: &str) -> String {
    path.trim_end_matches(['\\', '/']).to_lowercase()
}

/// Whether `path` lies inside `folder` (or is it).
fn is_within(path: &str, folder: &str) -> bool {
    let (path, folder) = (lowered(path), lowered(folder));
    path == folder || path.starts_with(&format!("{folder}\\"))
}

/// Checks and normalizes the paths of a request.
fn validate(mut request: JobRequest) -> Result<JobRequest, JobError> {
    let invalid = |message: String| JobError::new(ErrorCode::InvalidPath, message);
    if request.sources.is_empty() {
        return Err(invalid("a job needs at least one source".to_owned()));
    }
    let mut sources: Vec<String> = Vec::new();
    for source in &request.sources {
        let path = Path::new(source);
        if !path.is_absolute() {
            return Err(invalid(format!("{source}: the path must be absolute")));
        }
        let normalized =
            std::path::absolute(path).map_err(|error| invalid(format!("{source}: {error}")))?;
        if normalized.parent().is_none() {
            return Err(invalid(format!(
                "{source}: a volume root cannot be copied, moved or deleted"
            )));
        }
        let normalized = normalized.display().to_string();
        match std::fs::symlink_metadata(&normalized) {
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Err(JobError::new(
                    ErrorCode::NotFound,
                    format!("{source}: not found"),
                ));
            }
            Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
                return Err(JobError::new(
                    ErrorCode::AccessDenied,
                    format!("{source}: access denied"),
                ));
            }
            Err(error) => return Err(JobError::new(ErrorCode::Io, format!("{source}: {error}"))),
        }
        if !sources
            .iter()
            .any(|seen| lowered(seen) == lowered(&normalized))
        {
            sources.push(normalized);
        }
    }
    for outer in &sources {
        for inner in &sources {
            if outer != inner && is_within(inner, outer) {
                return Err(invalid(format!(
                    "{inner} is inside {outer}; list {outer} alone"
                )));
            }
        }
    }
    match request.kind {
        JobKind::Copy | JobKind::Move => {
            let destination = request
                .destination
                .as_deref()
                .ok_or_else(|| invalid("a copy or a move needs a destination folder".to_owned()))?;
            let path = Path::new(destination);
            if !path.is_absolute() {
                return Err(invalid(format!("{destination}: the path must be absolute")));
            }
            let destination = std::path::absolute(path)
                .map_err(|error| invalid(format!("{destination}: {error}")))?
                .display()
                .to_string();
            if std::fs::metadata(&destination).is_ok_and(|metadata| !metadata.is_dir()) {
                return Err(invalid(format!("{destination} is not a folder")));
            }
            for source in &sources {
                if is_within(&destination, source) {
                    return Err(invalid(format!(
                        "{destination} is inside {source}: a folder cannot go into itself"
                    )));
                }
                let parent = Path::new(source)
                    .parent()
                    .map(|parent| parent.display().to_string());
                if parent.is_some_and(|parent| lowered(&parent) == lowered(&destination)) {
                    return Err(invalid(format!("{source} is already in {destination}")));
                }
            }
            request.destination = Some(destination);
        }
        JobKind::Delete { .. } => {
            if request.destination.is_some() {
                return Err(invalid("a delete has no destination".to_owned()));
            }
        }
    }
    request.sources = sources;
    Ok(request)
}

/// The disk under `path`, and its volume's GUID path.
fn disk_of(path: &str) -> (Disk, Option<String>) {
    let Ok(details) = cabinetos_fs::volume::info_for_path(path) else {
        let root = Path::new(path)
            .ancestors()
            .last()
            .map_or_else(|| path.to_owned(), |root| root.display().to_string());
        let disk = Disk {
            key: DiskKey::Volume(root.to_lowercase()),
            solid_state: false,
        };
        return (disk, None);
    };
    let volume = details.volume_guid_path.to_lowercase();
    let disk = match details.disk {
        Some(disk) => Disk {
            key: DiskKey::Device(disk.device_number),
            solid_state: disk.seek_penalty == Some(false),
        },
        None => Disk {
            key: DiskKey::Volume(volume.clone()),
            solid_state: false,
        },
    };
    (disk, Some(volume))
}

/// The folder all test data of this crate goes under:
/// `%TEMP%\cabinetos-jobs-test`.
#[cfg(test)]
pub(crate) fn test_dir() -> std::path::PathBuf {
    let dir = std::env::temp_dir().join("cabinetos-jobs-test");
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_for_rename_are_checked() {
        assert!(check_name("b (copy).txt").is_ok());
        for bad in [
            "",
            ".",
            "..",
            "a\\b",
            "a/b",
            "c:",
            "x?",
            "trailing.",
            "space ",
        ] {
            assert!(check_name(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn containment_ignores_case_and_trailing_separators() {
        assert!(is_within(r"C:\A\b", r"c:\a\"));
        assert!(is_within(r"C:\A", r"c:\a"));
        assert!(!is_within(r"C:\ab", r"C:\a"));
    }
}
