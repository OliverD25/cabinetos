//! Running one job on its own thread.
//!
//! 1. **Scanning:** walk the sources into a plan (folders, files, totals),
//!    then ask the engine's gate, if any, whether the job may go on.
//! 2. **Folders:** create the destination folders in order, on this thread,
//!    so every folder exists (or is known to wait) before any file goes
//!    into it.
//! 3. **Files:** one worker, or several on solid state disks, take work from
//!    the job's queue. A file that hits a conflict is set aside with a
//!    `job_conflict` event and the workers go on (brief §3: a conflict never
//!    blocks the queue). A decision puts it back into the queue. Work under
//!    a folder that waits for a decision waits with it.
//! 4. **Finishing:** folder times, emptied source folders of a move, the
//!    final state.

use std::io::{Read, Seek, SeekFrom};
use std::sync::Mutex;
use std::sync::atomic::Ordering;
use std::time::Instant;

use cabinetos_protocol::{
    Conflict, ConflictKind, ConflictPolicy, Event, JobKind, JobState, LinkPolicy, Resolution,
};

use crate::bin;
use crate::job::{Counters, DirState, DirStatus, Job, Parked, Phase, Target, Work, lock};
use crate::plan::{
    self, FileItem, FileKind, Plan, PlanError, RemoveKind, RenameItem, Transfer, file_name, join,
};
use crate::recycle::{self, Apartment};
use crate::win::{self, CopyFlags, Step, code};
use crate::{Engine, JobPreview};

/// How many automatic decisions (job rules, the conflict policy) one piece
/// of work may take before it is set aside for the user.
const AUTOMATIC_STEPS: usize = 3;

/// Files up to this size are compared whole by `verify`; larger ones by
/// samples.
const VERIFY_WHOLE_UP_TO: u64 = 1024 * 1024;
const VERIFY_SAMPLES: u64 = 16;
const VERIFY_SAMPLE_SIZE: u64 = 64 * 1024;

/// What happened to one piece of work.
#[derive(Debug)]
enum Outcome {
    Done,
    Skipped,
    Failed,
    /// Needs a decision.
    Conflict {
        kind: ConflictKind,
        destination: Option<String>,
    },
    /// Waits for its folder; already filed under it.
    Held,
    /// Replaced by other work (a folder merge); counts as done.
    Expanded,
    /// A folder of a permanent delete that was not empty yet.
    Deferred,
    Cancelled,
}

/// Asks the engine's gate, if one is set, whether the job may go on.
fn ask_gate(engine: &Engine, job: &Job, files_total: u64, bytes_total: u64) -> Result<(), String> {
    let Some(gate) = engine.gate.get() else {
        return Ok(());
    };
    let request = &job.request;
    gate.check(&JobPreview {
        job_id: job.id,
        kind: &request.kind,
        sources: &request.sources,
        destination: request.destination.as_deref(),
        files_total,
        bytes_total,
    })
    .inspect_err(|message| {
        tracing::info!(job_id = job.id, reason = %message, "the job was refused before it started");
    })
}

/// Runs `job` to its end. Called on the job's own thread.
pub(crate) fn run(engine: &Engine, job: &Job) {
    let _ = job.started.set(Instant::now());
    if job.control.is_cancelled() {
        finish(engine, job, &JobState::Cancelled);
        return;
    }
    set_phase(engine, job, Phase::Scanning);
    // The publisher only follows started jobs; this one just started.
    engine.wake_publisher();
    let plan = match make_plan(job) {
        Ok(plan) => plan,
        Err(PlanError::Cancelled) => {
            finish(engine, job, &JobState::Cancelled);
            return;
        }
        Err(PlanError::Source(message)) => {
            finish(engine, job, &JobState::Failed { message });
            return;
        }
    };
    let unreadable = plan.unreadable.len() as u64;
    if let Err(message) = ask_gate(engine, job, plan.count() + unreadable, plan.bytes) {
        finish(engine, job, &JobState::Failed { message });
        return;
    }
    if let Some(destination) = &job.request.destination
        && let Err(error) = std::fs::create_dir_all(destination)
    {
        finish(
            engine,
            job,
            &JobState::Failed {
                message: format!("cannot create {destination}: {error}"),
            },
        );
        return;
    }
    let counters = &job.counters;
    counters
        .files_total
        .store(plan.count() + unreadable, Ordering::Relaxed);
    counters.bytes_total.store(plan.bytes, Ordering::Relaxed);
    Counters::add(&counters.files_done, unreadable);
    Counters::add(&counters.files_failed, unreadable);
    for (path, error) in &plan.unreadable {
        tracing::warn!(job_id = job.id, path = %path, error = %win::message(*error), "cannot read a folder of the job");
    }
    {
        let mut queue = lock(&job.queue);
        queue.dirs = vec![
            DirStatus {
                state: DirState::Pending,
                destination: String::new(),
            };
            plan.dirs.len()
        ];
    }
    set_phase(engine, job, Phase::Running);

    let recycling = matches!(job.request.kind, JobKind::Delete { permanent: false });
    let apartment = if recycling {
        match Apartment::enter() {
            Ok(apartment) => Some(apartment),
            Err(error) => {
                finish(
                    engine,
                    job,
                    &JobState::Failed {
                        message: format!("cannot start the shell: {}", win::message(error)),
                    },
                );
                return;
            }
        }
    } else {
        None
    };
    let run = Run {
        engine,
        job,
        plan: Mutex::new(plan),
        deferred: Mutex::new(Vec::new()),
    };
    run.execute(apartment.as_ref());
    let cancelled = job.control.is_cancelled();
    if !cancelled {
        run.finish_folders();
    }
    drop(run);
    drop(apartment);
    let state = if cancelled {
        JobState::Cancelled
    } else if Counters::get(&job.counters.files_failed) > 0 {
        JobState::CompletedWithErrors
    } else {
        JobState::Completed
    };
    finish(engine, job, &state);
}

fn make_plan(job: &Job) -> Result<Plan, PlanError> {
    let stop = || job.control.is_cancelled();
    let mut plan = Plan::default();
    let links = job.request.options.copy_links;
    for (index, source) in job.request.sources.iter().enumerate() {
        let name = file_name(source);
        let added = match job.request.kind {
            JobKind::Copy => {
                plan::add_copy_root(&mut plan, source, name, None, Transfer::Copy, links, &stop)
            }
            JobKind::Move if job.same_volume.get(index).copied().unwrap_or(false) => {
                plan::add_rename_root(&mut plan, source)
            }
            JobKind::Move => plan::add_copy_root(
                &mut plan,
                source,
                name,
                None,
                Transfer::CopyAndDelete,
                links,
                &stop,
            ),
            JobKind::Delete { permanent: true } => plan::add_removal_root(&mut plan, source, &stop),
            JobKind::Delete { permanent: false } => {
                plan.recycles.push(source.clone());
                Ok(())
            }
        };
        match added {
            Ok(()) => {}
            Err(PlanError::Cancelled) => return Err(PlanError::Cancelled),
            // A source that went away after the job started fails alone.
            Err(PlanError::Source(message)) => {
                tracing::warn!(job_id = job.id, error = %message, "a source of the job is gone");
                plan.unreadable.push((source.clone(), code::FILE_NOT_FOUND));
            }
        }
    }
    Ok(plan)
}

fn set_phase(engine: &Engine, job: &Job, phase: Phase) {
    *lock(&job.phase) = phase;
    engine.announce(job);
}

/// Ends the job: the final progress, then the final state.
pub(crate) fn finish(engine: &Engine, job: &Job, state: &JobState) {
    let _ = job.final_elapsed_ms.set(job.elapsed_ms());
    if matches!(state, &JobState::Cancelled) {
        // Nothing waits for a decision any more.
        lock(&job.queue).parked.clear();
        job.counters.conflicts_open.store(0, Ordering::Relaxed);
    }
    *lock(&job.current_path) = None;
    *lock(&job.phase) = Phase::Done(state.clone());
    crate::progress::publish(job, &engine.sink, engine.config.progress_gap, true);
    engine.announce(job);
    let counters = &job.counters;
    tracing::info!(
        job_id = job.id,
        state = ?state,
        files_done = Counters::get(&counters.files_done),
        files_skipped = Counters::get(&counters.files_skipped),
        files_failed = Counters::get(&counters.files_failed),
        bytes = Counters::get(&counters.bytes_done),
        elapsed_ms = job.elapsed_ms(),
        "job ended"
    );
}

struct Run<'a> {
    engine: &'a Engine,
    job: &'a Job,
    plan: Mutex<Plan>,
    /// Removal indices of folders that were not empty when their turn came.
    deferred: Mutex<Vec<usize>>,
}

impl Run<'_> {
    fn execute(&self, apartment: Option<&Apartment>) {
        // Folders first, in order, on this thread.
        let dir_count = lock(&self.plan).dirs.len();
        for index in 0..dir_count {
            if self.job.control.is_cancelled() {
                return;
            }
            if !self.job.control.wait_while_paused() {
                return;
            }
            let work = Work::new(Target::Dir(index));
            let outcome = self.process(&work, apartment);
            self.settle(work, outcome, apartment);
        }
        {
            let plan = lock(&self.plan);
            let mut queue = lock(&self.job.queue);
            queue
                .items
                .extend((0..plan.files.len()).map(|i| Work::new(Target::File(i))));
            queue
                .items
                .extend((0..plan.renames.len()).map(|i| Work::new(Target::Rename(i))));
            queue
                .items
                .extend((0..plan.removals.len()).map(|i| Work::new(Target::Remove(i))));
            queue
                .items
                .extend((0..plan.recycles.len()).map(|i| Work::new(Target::Recycle(i))));
        }
        let workers = self.workers();
        loop {
            self.work_until_done(workers, apartment);
            if self.job.control.is_cancelled() {
                return;
            }
            // Folders of a permanent delete that were not empty get one
            // more try, now that everything that could go is gone.
            let deferred = std::mem::take(&mut *lock(&self.deferred));
            if deferred.is_empty() {
                break;
            }
            let mut queue = lock(&self.job.queue);
            queue.items.extend(deferred.into_iter().map(|index| Work {
                last_try: true,
                ..Work::new(Target::Remove(index))
            }));
        }
        let leftovers: Vec<Work> = lock(&self.job.queue)
            .held
            .drain()
            .flat_map(|(_, works)| works)
            .collect();
        for work in leftovers {
            tracing::error!(job_id = self.job.id, target = ?work.target, "work left waiting for a folder");
            self.count(&work, &Outcome::Failed);
        }
    }

    /// Several files in flight only for copies and moves between solid
    /// state disks; one at a time anywhere near a spinning disk.
    fn workers(&self) -> usize {
        match self.job.request.kind {
            JobKind::Copy | JobKind::Move if self.job.solid_state_only => {
                self.engine.config.solid_state_files_in_flight.max(1)
            }
            _ => 1,
        }
    }

    fn work_until_done(&self, workers: usize, apartment: Option<&Apartment>) {
        std::thread::scope(|scope| {
            for number in 1..workers {
                let spawned = std::thread::Builder::new()
                    .name(format!("job-{}-{number}", self.job.id))
                    .spawn_scoped(scope, || self.worker(None));
                if let Err(error) = spawned {
                    tracing::warn!(%error, "cannot start a job worker; going on with fewer");
                }
            }
            self.worker(apartment);
        });
    }

    fn worker(&self, apartment: Option<&Apartment>) {
        while let Some(work) = self.next_work() {
            let outcome = if self.job.control.wait_while_paused() {
                self.process(&work, apartment)
            } else {
                Outcome::Cancelled
            };
            self.settle(work, outcome, apartment);
            lock(&self.job.queue).in_flight -= 1;
            self.job.work_ready.notify_all();
        }
    }

    /// The next piece of work; `None` when the job is cancelled, or when
    /// nothing is left, in flight, or waiting for a decision.
    fn next_work(&self) -> Option<Work> {
        let mut queue = lock(&self.job.queue);
        loop {
            if self.job.control.is_cancelled() {
                self.job.work_ready.notify_all();
                return None;
            }
            if let Some(work) = queue.items.pop_front() {
                queue.in_flight += 1;
                return Some(work);
            }
            if queue.in_flight == 0 && queue.parked.is_empty() {
                self.job.work_ready.notify_all();
                return None;
            }
            queue = self
                .job
                .work_ready
                .wait(queue)
                .unwrap_or_else(std::sync::PoisonError::into_inner);
        }
    }

    fn process(&self, work: &Work, apartment: Option<&Apartment>) -> Outcome {
        if self.job.control.is_cancelled() {
            return Outcome::Cancelled;
        }
        let resolution = work.resolution.as_ref();
        if matches!(resolution, Some(Resolution::Skip)) {
            return self.skip_target(work);
        }
        match work.target {
            Target::Dir(index) => self.make_dir(index, work),
            Target::File(index) => self.transfer(index, work),
            Target::Rename(index) => self.rename_root(index, work),
            Target::Remove(index) => self.remove(index, work),
            Target::Recycle(index) => self.recycle(index, work, apartment),
        }
    }

    /// A skipped folder of a copy takes its waiting contents along.
    fn skip_target(&self, work: &Work) -> Outcome {
        if let Target::Dir(index) = work.target {
            self.close_dir(index, DirState::Skipped);
        }
        Outcome::Skipped
    }

    /// Records the outcome, applying the job's rules to a conflict before
    /// setting the work aside.
    fn settle(&self, mut work: Work, mut outcome: Outcome, apartment: Option<&Apartment>) {
        let mut steps = 0;
        loop {
            let Outcome::Conflict { kind, destination } = outcome else {
                self.count(&work, &outcome);
                return;
            };
            if matches!(kind, ConflictKind::SourceVanished) {
                tracing::warn!(job_id = self.job.id, source = %self.source_of(&work), "the source is gone");
                self.count(&work, &Outcome::Failed);
                return;
            }
            let automatic = (steps < AUTOMATIC_STEPS)
                .then(|| self.automatic(&kind))
                .flatten();
            let Some(resolution) = automatic else {
                self.park(&work, kind, destination);
                return;
            };
            steps += 1;
            work.resolution = Some(resolution);
            work.answers = Some(kind.tag());
            outcome = self.process(&work, apartment);
        }
    }

    /// The decision the job already has for a conflict of this kind: a rule
    /// set with `apply_to_same_kind`, or the job's conflict policy for a
    /// file that exists.
    fn automatic(&self, kind: &ConflictKind) -> Option<Resolution> {
        if let Some(rule) = lock(&self.job.queue).policies.get(kind.tag()) {
            return Some(rule.clone());
        }
        let ConflictKind::FileExists {
            source_modified,
            dest_modified,
            ..
        } = kind
        else {
            return None;
        };
        match self.job.request.options.on_conflict {
            ConflictPolicy::Ask => None,
            ConflictPolicy::Overwrite => Some(Resolution::Overwrite),
            ConflictPolicy::OverwriteIfNewer if source_modified > dest_modified => {
                Some(Resolution::Overwrite)
            }
            ConflictPolicy::Skip | ConflictPolicy::OverwriteIfNewer => Some(Resolution::Skip),
            ConflictPolicy::Rename => Some(Resolution::Rename { new_name: None }),
        }
    }

    /// Sets `work` aside and tells the clients.
    fn park(&self, work: &Work, kind: ConflictKind, destination: Option<String>) {
        let conflict = Conflict {
            conflict_id: crate::next_conflict_id(),
            job_id: self.job.id,
            kind,
            source: self.source_of(work),
            destination,
        };
        let disk_full = matches!(conflict.kind, ConflictKind::DiskFull);
        {
            let mut queue = lock(&self.job.queue);
            if let Target::Dir(index) = work.target
                && let Some(status) = queue.dirs.get_mut(index)
            {
                status.state = DirState::Parked;
            }
            let answers = Some(conflict.kind.tag());
            queue.parked.insert(
                conflict.conflict_id,
                Parked {
                    work: Work {
                        resolution: None,
                        answers,
                        ..work.clone()
                    },
                    conflict: conflict.clone(),
                },
            );
        }
        Counters::add(&self.job.counters.conflicts_open, 1);
        tracing::info!(
            job_id = self.job.id,
            conflict_id = conflict.conflict_id,
            kind = conflict.kind.tag(),
            source = %conflict.source,
            "file set aside for a decision"
        );
        self.engine.emit(Event::JobConflict(conflict));
        if disk_full {
            // Every other file would fail the same way: stop until the user
            // makes room and says retry.
            self.job.control.pause_for_disk_full();
            self.engine.announce(self.job);
        }
    }

    /// Updates the counters for a finished piece of work.
    fn count(&self, work: &Work, outcome: &Outcome) {
        let counters = &self.job.counters;
        match outcome {
            Outcome::Done | Outcome::Expanded => Counters::add(&counters.files_done, 1),
            Outcome::Skipped => {
                Counters::add(&counters.files_done, 1);
                Counters::add(&counters.files_skipped, 1);
                Counters::subtract(&counters.bytes_total, self.bytes_of(work));
                if let Target::Dir(index) = work.target {
                    self.close_dir(index, DirState::Skipped);
                }
            }
            Outcome::Failed => {
                Counters::add(&counters.files_done, 1);
                Counters::add(&counters.files_failed, 1);
                Counters::subtract(&counters.bytes_total, self.bytes_of(work));
                if let Target::Dir(index) = work.target {
                    self.close_dir(index, DirState::Failed);
                }
            }
            Outcome::Deferred => {
                if let Target::Remove(index) = work.target {
                    lock(&self.deferred).push(index);
                }
            }
            Outcome::Held | Outcome::Cancelled | Outcome::Conflict { .. } => {}
        }
    }

    /// Marks a folder skipped or failed, with everything waiting under it.
    fn close_dir(&self, index: usize, state: DirState) {
        let waiting = {
            let mut queue = lock(&self.job.queue);
            match queue.dirs.get_mut(index) {
                Some(status) if status.state == state => return,
                Some(status) => status.state = state,
                None => return,
            }
            queue.held.remove(&index).unwrap_or_default()
        };
        let outcome = || {
            if state == DirState::Skipped {
                Outcome::Skipped
            } else {
                Outcome::Failed
            }
        };
        for work in waiting {
            self.count(&work, &outcome());
        }
    }

    /// A folder exists now: its waiting contents go back into the queue.
    fn dir_ready(&self, index: usize, state: DirState, destination: String) {
        {
            let mut queue = lock(&self.job.queue);
            queue.dirs[index] = DirStatus { state, destination };
            if let Some(waiting) = queue.held.remove(&index) {
                for work in waiting.into_iter().rev() {
                    queue.items.push_front(work);
                }
            }
        }
        self.job.work_ready.notify_all();
    }

    /// Where the contents of `parent` go, or what they must do instead.
    fn parent_destination(&self, parent: Option<usize>, work: &Work) -> Result<String, Outcome> {
        let Some(parent) = parent else {
            return Ok(self.job.request.destination.clone().unwrap_or_default());
        };
        let mut queue = lock(&self.job.queue);
        match queue.dirs[parent].state {
            DirState::Created | DirState::Existed => Ok(queue.dirs[parent].destination.clone()),
            DirState::Pending | DirState::Parked | DirState::Held => {
                if let Target::Dir(index) = work.target {
                    queue.dirs[index].state = DirState::Held;
                }
                queue.held.entry(parent).or_default().push(Work {
                    resolution: work.resolution.clone(),
                    ..work.clone()
                });
                Err(Outcome::Held)
            }
            DirState::Skipped => Err(Outcome::Skipped),
            DirState::Failed => Err(Outcome::Failed),
        }
    }

    fn make_dir(&self, index: usize, work: &Work) -> Outcome {
        let item = lock(&self.plan).dirs[index].clone();
        let parent = match self.parent_destination(item.parent, work) {
            Ok(parent) => parent,
            Err(outcome) => return outcome,
        };
        let resolution = work.resolution.as_ref();
        let name = match resolution {
            Some(Resolution::Rename { new_name }) => new_name
                .clone()
                .unwrap_or_else(|| free_name(&parent, &item.name, true)),
            _ => item.name.clone(),
        };
        let destination = join(&parent, &name);
        *lock(&self.job.current_path) = Some(item.source.clone());
        if matches!(resolution, Some(Resolution::Overwrite))
            && win::info(&destination).is_ok_and(|info| !info.is_directory())
        {
            // A file stands where the folder goes; overwrite replaces it.
            if work.may_clear_read_only() {
                let _ = win::clear_read_only(&destination);
            }
            if let Err(error) = win::delete_file(&destination) {
                return conflict_for(error, &item.source, Some(destination));
            }
        }
        match win::create_directory(&destination) {
            Ok(()) => {
                self.dir_ready(index, DirState::Created, destination);
                Outcome::Done
            }
            Err(code::ALREADY_EXISTS) => match win::info(&destination) {
                Ok(info) if info.is_directory() => {
                    self.dir_ready(index, DirState::Existed, destination);
                    Outcome::Done
                }
                Ok(info) => Outcome::Conflict {
                    kind: ConflictKind::FileExists {
                        source_size: 0,
                        source_modified: item.times.modified,
                        dest_size: info.size,
                        dest_modified: info.times.modified,
                    },
                    destination: Some(destination),
                },
                Err(error) => conflict_for(error, &item.source, Some(destination)),
            },
            Err(error) => conflict_for(error, &item.source, Some(destination)),
        }
    }

    fn transfer(&self, index: usize, work: &Work) -> Outcome {
        let item = lock(&self.plan).files[index].clone();
        let parent = match self.parent_destination(item.parent, work) {
            Ok(parent) => parent,
            Err(outcome) => return outcome,
        };
        let resolution = work.resolution.as_ref();
        let overwrite = matches!(resolution, Some(Resolution::Overwrite));
        let name = match resolution {
            Some(Resolution::Rename { new_name }) => new_name
                .clone()
                .unwrap_or_else(|| free_name(&parent, &item.name, false)),
            _ => item.name.clone(),
        };
        let destination = join(&parent, &name);
        if work.may_clear_read_only() {
            let _ = win::clear_read_only(&destination);
        }
        *lock(&self.job.current_path) = Some(item.source.clone());
        let outcome = match (item.transfer, item.kind) {
            (Transfer::Rename, _) => rename(
                &item.source,
                &destination,
                overwrite,
                item.size,
                item.times.modified,
            ),
            (_, FileKind::DirLink) => copy_dir_link(&item, &destination, overwrite),
            (_, FileKind::File | FileKind::FileLink) => {
                self.copy_file(&item, &destination, overwrite)
            }
        };
        if matches!(outcome, Outcome::Done) && item.transfer == Transfer::CopyAndDelete {
            return delete_moved_source(&item);
        }
        outcome
    }

    fn copy_file(&self, item: &FileItem, destination: &str, overwrite: bool) -> Outcome {
        let control = &self.job.control;
        let counters = &self.job.counters;
        let flags = CopyFlags {
            overwrite,
            unbuffered: item.kind == FileKind::File
                && item.size >= self.engine.config.unbuffered_from,
            symlink: item.kind == FileKind::FileLink,
        };
        let mut reported = 0u64;
        let result = win::copy_file(&item.source, destination, flags, &mut |total| {
            // Pausing blocks right here: the thread is the job's own.
            if !control.wait_while_paused() {
                return Step::Cancel;
            }
            Counters::add(&counters.bytes_done, total.saturating_sub(reported));
            reported = reported.max(total);
            Step::Continue
        });
        if let Err(error) = result {
            Counters::subtract(&counters.bytes_done, reported);
            if control.is_cancelled() {
                return Outcome::Cancelled;
            }
            return conflict_for(error, &item.source, Some(destination.to_owned()));
        }
        if item.kind == FileKind::File {
            // The file may have changed since it was planned; count what
            // was copied.
            if reported > item.size {
                Counters::add(&counters.bytes_total, reported - item.size);
            } else {
                Counters::subtract(&counters.bytes_total, item.size - reported);
            }
            let options = &self.job.request.options;
            if options.verify && !same_content(&item.source, destination) {
                Counters::subtract(&counters.bytes_done, reported);
                Counters::add(&counters.bytes_total, item.size.saturating_sub(reported));
                // Our own bad copy; the next try starts clean.
                let _ = win::delete_file(destination);
                return Outcome::Conflict {
                    kind: ConflictKind::Io {
                        code: code::CRC,
                        message: "the copy differs from its source (verify)".to_owned(),
                    },
                    destination: Some(destination.to_owned()),
                };
            }
            if options.preserve_timestamps
                && let Err(error) = win::set_times(destination, item.times, false)
            {
                tracing::warn!(path = %destination, error = %win::message(error), "cannot set the times of a copy");
            }
        }
        Outcome::Done
    }

    fn rename_root(&self, index: usize, work: &Work) -> Outcome {
        let resolution = work.resolution.as_ref();
        let item: RenameItem = lock(&self.plan).renames[index].clone();
        let root = self.job.request.destination.clone().unwrap_or_default();
        *lock(&self.job.current_path) = Some(item.source.clone());
        let name = match resolution {
            Some(Resolution::Rename { new_name }) => new_name
                .clone()
                .unwrap_or_else(|| free_name(&root, &item.name, item.is_dir)),
            _ => item.name.clone(),
        };
        let destination = join(&root, &name);
        let overwrite = matches!(resolution, Some(Resolution::Overwrite));
        if overwrite && item.is_dir && win::info(&destination).is_ok_and(|info| info.is_directory())
        {
            // A folder moves into an existing one: move its contents one by
            // one, with a conflict for each file that exists.
            return self.expand_merge(&item);
        }
        if work.may_clear_read_only() {
            let _ = win::clear_read_only(&destination);
        }
        rename(
            &item.source,
            &destination,
            overwrite && !item.is_dir,
            item.size,
            item.times.modified,
        )
    }

    /// Turns the rename of a folder into renames of its contents, merged
    /// into the folder of the same name at the destination.
    fn expand_merge(&self, item: &RenameItem) -> Outcome {
        let stop = || self.job.control.is_cancelled();
        let (dirs, files) = {
            let mut plan = lock(&self.plan);
            let (dirs_before, files_before) = (plan.dirs.len(), plan.files.len());
            if plan::add_copy_root(
                &mut plan,
                &item.source,
                &item.name,
                None,
                Transfer::Rename,
                LinkPolicy::AsLink,
                &stop,
            )
            .is_err()
            {
                return Outcome::Cancelled;
            }
            (dirs_before..plan.dirs.len(), files_before..plan.files.len())
        };
        let added = (dirs.len() + files.len()) as u64;
        Counters::add(&self.job.counters.files_total, added);
        {
            let mut queue = lock(&self.job.queue);
            queue.dirs.extend(dirs.clone().map(|_| DirStatus {
                state: DirState::Pending,
                destination: String::new(),
            }));
            // Folders first; files wait for theirs anyway.
            for index in files.rev() {
                queue.items.push_front(Work::new(Target::File(index)));
            }
            for index in dirs.rev() {
                queue.items.push_front(Work::new(Target::Dir(index)));
            }
        }
        self.job.work_ready.notify_all();
        Outcome::Expanded
    }

    /// Turns the Recycle Bin delete of `path` into a permanent one, contents
    /// before their folders, as the user decided.
    fn expand_permanent(&self, path: &str) -> Outcome {
        let stop = || self.job.control.is_cancelled();
        let added = {
            let mut plan = lock(&self.plan);
            let before = plan.removals.len();
            match plan::add_removal_root(&mut plan, path, &stop) {
                Ok(()) => before..plan.removals.len(),
                Err(PlanError::Cancelled) => return Outcome::Cancelled,
                Err(PlanError::Source(_)) => return Outcome::Done,
            }
        };
        Counters::add(&self.job.counters.files_total, added.len() as u64);
        {
            let mut queue = lock(&self.job.queue);
            for index in added.rev() {
                queue.items.push_front(Work::new(Target::Remove(index)));
            }
        }
        self.job.work_ready.notify_all();
        Outcome::Expanded
    }

    fn remove(&self, index: usize, work: &Work) -> Outcome {
        let item = lock(&self.plan).removals[index].clone();
        *lock(&self.job.current_path) = Some(item.path.clone());
        let overwrite = work.may_clear_read_only()
            || self.job.request.options.on_conflict
                == cabinetos_protocol::ConflictPolicy::Overwrite;
        if overwrite && item.attributes & 1 != 0 {
            let _ = win::clear_read_only(&item.path);
        }
        let removed = match item.kind {
            RemoveKind::File => win::delete_file(&item.path),
            RemoveKind::Dir => win::remove_directory(&item.path),
        };
        match removed {
            // Already gone is what a delete wants.
            Ok(()) | Err(code::FILE_NOT_FOUND | code::PATH_NOT_FOUND) => Outcome::Done,
            Err(code::DIR_NOT_EMPTY) if work.last_try => Outcome::Skipped,
            Err(code::DIR_NOT_EMPTY) => Outcome::Deferred,
            Err(error) => conflict_for(error, &item.path, None),
        }
    }

    fn recycle(&self, index: usize, work: &Work, apartment: Option<&Apartment>) -> Outcome {
        let path = lock(&self.plan).recycles[index].clone();
        *lock(&self.job.current_path) = Some(path.clone());
        if matches!(work.resolution, Some(Resolution::DeletePermanently)) {
            return self.expand_permanent(&path);
        }
        // The shell would delete for good, silently, what its bin cannot
        // take; ask first.
        let stop = || self.job.control.is_cancelled();
        let size = match plan::tree_size(&path, &stop) {
            Ok(size) => size,
            Err(PlanError::Cancelled) => return Outcome::Cancelled,
            Err(PlanError::Source(_)) => return Outcome::Done,
        };
        let capacity = self
            .engine
            .config
            .recycle_bin_capacity
            .map_or_else(|| bin::capacity(&path), bin::Capacity::Bytes);
        if !capacity.fits(size) {
            return Outcome::Conflict {
                kind: ConflictKind::RecycleBinTooSmall { size },
                destination: None,
            };
        }
        let Some(apartment) = apartment else {
            tracing::error!("a Recycle Bin delete ran without COM");
            return Outcome::Failed;
        };
        match recycle::recycle(apartment, &path) {
            Ok(()) => Outcome::Done,
            Err(error) if !win::exists(&path) && error != code::REQUEST_ABORTED => Outcome::Done,
            Err(error) => conflict_for(error, &path, None),
        }
    }

    fn source_of(&self, work: &Work) -> String {
        let plan = lock(&self.plan);
        match work.target {
            Target::Dir(index) => plan.dirs[index].source.clone(),
            Target::File(index) => plan.files[index].source.clone(),
            Target::Rename(index) => plan.renames[index].source.clone(),
            Target::Remove(index) => plan.removals[index].path.clone(),
            Target::Recycle(index) => plan.recycles[index].clone(),
        }
    }

    /// The bytes a piece of work was going to copy.
    fn bytes_of(&self, work: &Work) -> u64 {
        match work.target {
            Target::File(index) => {
                let plan = lock(&self.plan);
                let item = &plan.files[index];
                if item.kind == FileKind::File && item.transfer != Transfer::Rename {
                    item.size
                } else {
                    0
                }
            }
            _ => 0,
        }
    }

    /// Folder times (copies and moves keep them), and the emptied source
    /// folders of a move.
    fn finish_folders(&self) {
        let plan = lock(&self.plan);
        let queue = lock(&self.job.queue);
        let preserve = self.job.request.options.preserve_timestamps;
        // Children come after their parents in the plan, so backwards is
        // deepest first: a parent's times are set after its contents
        // changed it for the last time.
        for (index, dir) in plan.dirs.iter().enumerate().rev() {
            let status = &queue.dirs[index];
            if preserve
                && status.state == DirState::Created
                && let Err(error) = win::set_times(&status.destination, dir.times, true)
            {
                tracing::warn!(path = %status.destination, error = %win::message(error), "cannot set the times of a folder");
            }
            if dir.remove_source && matches!(status.state, DirState::Created | DirState::Existed) {
                // Fails, and stays, if something in it was skipped.
                let _ = win::remove_directory(&dir.source);
            }
        }
    }
}

/// The source of a move, after its copy: it goes now.
fn delete_moved_source(item: &FileItem) -> Outcome {
    if item.attributes & 1 != 0 {
        let _ = win::clear_read_only(&item.source);
    }
    let deleted = if item.kind == FileKind::DirLink {
        win::remove_directory(&item.source)
    } else {
        win::delete_file(&item.source)
    };
    match deleted {
        Ok(()) => Outcome::Done,
        Err(error) => {
            // The copy is in place; the source stays as well.
            tracing::warn!(source = %item.source, error = %win::message(error), "moved, but the source cannot be deleted");
            Outcome::Failed
        }
    }
}

/// A "file exists" conflict with both sides described.
fn exists(source_size: u64, source_modified: i64, destination: &str) -> Outcome {
    let existing = win::info(destination).ok();
    Outcome::Conflict {
        kind: ConflictKind::FileExists {
            source_size,
            source_modified,
            dest_size: existing.map_or(0, |info| info.size),
            dest_modified: existing.map_or(0, |info| info.times.modified),
        },
        destination: Some(destination.to_owned()),
    }
}

/// The conflict for a Win32 error on `source`.
fn conflict_for(error: u32, source: &str, destination: Option<String>) -> Outcome {
    let kind = match error {
        code::ACCESS_DENIED => ConflictKind::AccessDenied,
        code::SHARING_VIOLATION | code::LOCK_VIOLATION => ConflictKind::SharingViolation,
        code::FILENAME_EXCED_RANGE => ConflictKind::PathTooLong,
        code::DISK_FULL | code::HANDLE_DISK_FULL => ConflictKind::DiskFull,
        code::FILE_NOT_FOUND | code::PATH_NOT_FOUND if !win::exists(source) => {
            ConflictKind::SourceVanished
        }
        error if is_exists(error) => {
            let source_info = win::info(source).ok();
            return exists(
                source_info.map_or(0, |info| info.size),
                source_info.map_or(0, |info| info.times.modified),
                destination.as_deref().unwrap_or_default(),
            );
        }
        other => ConflictKind::Io {
            code: other,
            message: win::message(other),
        },
    };
    Outcome::Conflict { kind, destination }
}

fn copy_dir_link(item: &FileItem, destination: &str, overwrite: bool) -> Outcome {
    if overwrite && win::exists(destination) {
        // An empty folder or a link can go; anything else stays.
        if let Err(error) = win::remove_directory(destination) {
            return conflict_for(error, &item.source, Some(destination.to_owned()));
        }
    }
    match win::copy_directory_link(&item.source, destination) {
        Ok(()) => Outcome::Done,
        Err(error) => conflict_for(error, &item.source, Some(destination.to_owned())),
    }
}

/// Renames `source` to `destination` on one volume.
fn rename(source: &str, destination: &str, replace: bool, size: u64, modified: i64) -> Outcome {
    match win::move_file(source, destination, replace) {
        Ok(()) => Outcome::Done,
        Err(error) if is_exists(error) => exists(size, modified, destination),
        Err(error) => conflict_for(error, source, Some(destination.to_owned())),
    }
}

fn is_exists(error: u32) -> bool {
    matches!(error, code::FILE_EXISTS | code::ALREADY_EXISTS)
}

/// A name in `folder` that is free: `name (2).ext`, `name (3).ext`, ...
pub(crate) fn free_name(folder: &str, name: &str, is_dir: bool) -> String {
    let (stem, extension) = match name.rfind('.') {
        Some(dot) if !is_dir && dot > 0 => name.split_at(dot),
        _ => (name, ""),
    };
    (2..=u32::MAX)
        .map(|number| format!("{stem} ({number}){extension}"))
        .find(|candidate| !win::exists(&join(folder, candidate)))
        .unwrap_or_else(|| name.to_owned())
}

/// Whether two files have the same size and bytes: all of them up to 1 MiB,
/// 16 samples of 64 KiB, first and last included, above.
fn same_content(source: &str, destination: &str) -> bool {
    let compare = || -> std::io::Result<bool> {
        let mut first = std::fs::File::open(source)?;
        let mut second = std::fs::File::open(destination)?;
        let size = first.metadata()?.len();
        if size != second.metadata()?.len() {
            return Ok(false);
        }
        let ranges: Vec<(u64, u64)> = if size <= VERIFY_WHOLE_UP_TO {
            vec![(0, size)]
        } else {
            let last_start = size - VERIFY_SAMPLE_SIZE;
            (0..VERIFY_SAMPLES)
                .map(|sample| {
                    (
                        last_start * sample / (VERIFY_SAMPLES - 1),
                        VERIFY_SAMPLE_SIZE,
                    )
                })
                .collect()
        };
        let mut left = Vec::new();
        let mut right = Vec::new();
        for (offset, length) in ranges {
            let length = usize::try_from(length).unwrap_or(usize::MAX);
            left.resize(length, 0);
            right.resize(length, 0);
            first.seek(SeekFrom::Start(offset))?;
            second.seek(SeekFrom::Start(offset))?;
            first.read_exact(&mut left)?;
            second.read_exact(&mut right)?;
            if left != right {
                return Ok(false);
            }
        }
        Ok(true)
    };
    compare().unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn free_names_count_up_before_the_extension() {
        let dir = tempfile::Builder::new()
            .prefix("names")
            .tempdir_in(crate::test_dir())
            .unwrap();
        let folder = dir.path().to_str().unwrap();
        std::fs::write(dir.path().join("a.txt"), "").unwrap();
        std::fs::write(dir.path().join("a (2).txt"), "").unwrap();
        assert_eq!(free_name(folder, "a.txt", false), "a (3).txt");
        assert_eq!(free_name(folder, "b.txt", false), "b (2).txt");
        assert_eq!(free_name(folder, "v1.2", true), "v1.2 (2)");
        assert_eq!(free_name(folder, ".profile", false), ".profile (2)");
    }
}
