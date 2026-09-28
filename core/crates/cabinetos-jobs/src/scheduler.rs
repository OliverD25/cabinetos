//! Which jobs may run now: one queue per physical disk (brief §3,
//! "drive-aware queuing").
//!
//! A job holds a slot on every disk it touches, and starts only when it can
//! take all of them at once. A spinning disk has one slot, so jobs on it run
//! one after another instead of making the heads jump between files. A solid
//! state disk has several. A disk Windows would not identify (a network
//! share, some USB bridges) counts as spinning: sequential is the safe
//! guess.
//!
//! Waiting jobs keep their order on every disk: a job that cannot start
//! reserves a slot on each of its disks, so a later job cannot overtake it
//! there and starve it. Quick jobs (deletes, renames on one volume) go
//! ahead of copies; within each class the order is first come, first
//! served.

use std::collections::HashMap;

/// Names a physical disk, or stands in for one whose identity is unknown.
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub(crate) enum DiskKey {
    /// A local disk: its device number (`\\.\PhysicalDriveN`).
    Device(u32),
    /// No disk identity: the volume or share root stands for the disk, so
    /// jobs on one share still take turns.
    Volume(String),
}

/// A disk a job touches.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) struct Disk {
    pub(crate) key: DiskKey,
    /// No seek penalty: several jobs may use it at once.
    pub(crate) solid_state: bool,
}

/// Which jobs go first on a disk.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Priority {
    /// Deletes and renames on one volume: little I/O, done in moments.
    Quick,
    /// Copies, and moves across volumes.
    Copy,
}

#[derive(Debug)]
struct Waiting {
    job: u64,
    priority: Priority,
    /// Submission order.
    sequence: u64,
    disks: Vec<Disk>,
    /// Paused while waiting: skipped, and reserves nothing.
    held: bool,
}

/// The slots of every disk and the jobs waiting for them.
#[derive(Debug)]
pub(crate) struct Scheduler {
    /// Jobs a solid state disk runs at once.
    solid_state_slots: usize,
    /// Slots in use per disk.
    used: HashMap<DiskKey, usize>,
    waiting: Vec<Waiting>,
    next_sequence: u64,
}

impl Scheduler {
    pub(crate) fn new(solid_state_slots: usize) -> Self {
        Self {
            solid_state_slots: solid_state_slots.max(1),
            used: HashMap::new(),
            waiting: Vec::new(),
            next_sequence: 0,
        }
    }

    /// Queues a job. A disk listed twice counts once.
    pub(crate) fn submit(&mut self, job: u64, priority: Priority, mut disks: Vec<Disk>) {
        disks.sort_by(|a, b| a.key.cmp(&b.key));
        disks.dedup_by(|a, b| a.key == b.key);
        self.waiting.push(Waiting {
            job,
            priority,
            sequence: self.next_sequence,
            disks,
            held: false,
        });
        self.next_sequence += 1;
    }

    /// Holds a waiting job back (paused) or lets it compete again. Returns
    /// whether the job was waiting.
    pub(crate) fn hold(&mut self, job: u64, held: bool) -> bool {
        match self.waiting.iter_mut().find(|waiting| waiting.job == job) {
            Some(waiting) => {
                waiting.held = held;
                true
            }
            None => false,
        }
    }

    /// Removes a waiting job (cancelled before it started). Returns whether
    /// it was waiting.
    pub(crate) fn withdraw(&mut self, job: u64) -> bool {
        let before = self.waiting.len();
        self.waiting.retain(|waiting| waiting.job != job);
        self.waiting.len() != before
    }

    /// The jobs that may start now, in the order they should start. Their
    /// slots are taken; give them back with [`release`](Self::release).
    pub(crate) fn admit(&mut self) -> Vec<(u64, Vec<Disk>)> {
        let mut order: Vec<usize> = (0..self.waiting.len()).collect();
        order.sort_by_key(|&index| {
            let waiting = &self.waiting[index];
            (waiting.priority, waiting.sequence)
        });
        let mut free: HashMap<DiskKey, isize> = HashMap::new();
        let mut starting = Vec::new();
        for index in order {
            let waiting = &self.waiting[index];
            if waiting.held {
                continue;
            }
            for disk in &waiting.disks {
                free.entry(disk.key.clone()).or_insert_with(|| {
                    let capacity = self.capacity(disk);
                    let used = self.used.get(&disk.key).copied().unwrap_or(0);
                    isize::try_from(capacity).unwrap_or(isize::MAX)
                        - isize::try_from(used).unwrap_or(isize::MAX)
                });
            }
            let can_start = waiting.disks.iter().all(|disk| free[&disk.key] > 0);
            // Starting takes a slot on each disk; waiting reserves one, so
            // later jobs cannot overtake this one on any of its disks.
            for disk in &waiting.disks {
                *free.get_mut(&disk.key).expect("inserted above") -= 1;
            }
            if can_start {
                starting.push(index);
            }
        }
        let mut started = Vec::with_capacity(starting.len());
        for &index in &starting {
            let waiting = &self.waiting[index];
            for disk in &waiting.disks {
                *self.used.entry(disk.key.clone()).or_insert(0) += 1;
            }
            started.push((waiting.job, waiting.disks.clone()));
        }
        let started_jobs: Vec<u64> = started.iter().map(|(job, _)| *job).collect();
        self.waiting
            .retain(|waiting| !started_jobs.contains(&waiting.job));
        started
    }

    /// Gives back the slots of a job that ended.
    pub(crate) fn release(&mut self, disks: &[Disk]) {
        for disk in disks {
            if let Some(used) = self.used.get_mut(&disk.key) {
                *used = used.saturating_sub(1);
                if *used == 0 {
                    self.used.remove(&disk.key);
                }
            }
        }
    }

    fn capacity(&self, disk: &Disk) -> usize {
        if disk.solid_state {
            self.solid_state_slots
        } else {
            1
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hdd(number: u32) -> Disk {
        Disk {
            key: DiskKey::Device(number),
            solid_state: false,
        }
    }

    fn ssd(number: u32) -> Disk {
        Disk {
            key: DiskKey::Device(number),
            solid_state: true,
        }
    }

    fn jobs(started: &[(u64, Vec<Disk>)]) -> Vec<u64> {
        started.iter().map(|(job, _)| *job).collect()
    }

    #[test]
    fn two_jobs_on_one_hard_disk_run_one_after_another() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0)]);
        scheduler.submit(2, Priority::Copy, vec![hdd(0)]);
        let first = scheduler.admit();
        assert_eq!(jobs(&first), [1]);
        assert!(scheduler.admit().is_empty(), "the disk is busy");
        scheduler.release(&first[0].1);
        assert_eq!(jobs(&scheduler.admit()), [2]);
    }

    #[test]
    fn jobs_on_different_solid_state_disks_run_at_once() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![ssd(1)]);
        scheduler.submit(2, Priority::Copy, vec![ssd(2)]);
        assert_eq!(jobs(&scheduler.admit()), [1, 2]);
    }

    #[test]
    fn a_solid_state_disk_runs_up_to_its_slots() {
        let mut scheduler = Scheduler::new(4);
        for job in 1..=5 {
            scheduler.submit(job, Priority::Copy, vec![ssd(1)]);
        }
        let started = scheduler.admit();
        assert_eq!(jobs(&started), [1, 2, 3, 4]);
        scheduler.release(&started[0].1);
        assert_eq!(jobs(&scheduler.admit()), [5]);
    }

    #[test]
    fn a_job_on_a_hard_disk_and_a_solid_state_disk_waits_for_both() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0)]);
        let first = scheduler.admit();
        scheduler.submit(2, Priority::Copy, vec![hdd(0), ssd(1)]);
        assert!(scheduler.admit().is_empty(), "the hard disk is busy");
        // The solid state disk still has room for others.
        scheduler.submit(3, Priority::Copy, vec![ssd(1)]);
        assert_eq!(jobs(&scheduler.admit()), [3]);
        scheduler.release(&first[0].1);
        assert_eq!(jobs(&scheduler.admit()), [2]);
    }

    #[test]
    fn waiting_jobs_keep_their_order_on_a_disk() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0)]);
        let first = scheduler.admit();
        scheduler.submit(2, Priority::Copy, vec![hdd(0), hdd(3)]);
        scheduler.submit(3, Priority::Copy, vec![hdd(3)]);
        // Job 2 cannot start (disk 0 is busy), and job 3 may not take disk 3
        // from under it.
        assert!(scheduler.admit().is_empty());
        scheduler.release(&first[0].1);
        let second = scheduler.admit();
        assert_eq!(jobs(&second), [2]);
        scheduler.release(&second[0].1);
        assert_eq!(jobs(&scheduler.admit()), [3]);
    }

    #[test]
    fn quick_jobs_go_ahead_of_copies() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0)]);
        let first = scheduler.admit();
        scheduler.submit(2, Priority::Copy, vec![hdd(0)]);
        scheduler.submit(3, Priority::Quick, vec![hdd(0)]);
        scheduler.release(&first[0].1);
        assert_eq!(jobs(&scheduler.admit()), [3]);
    }

    #[test]
    fn a_held_job_is_passed_over_and_reserves_nothing() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0)]);
        scheduler.submit(2, Priority::Copy, vec![hdd(0)]);
        assert!(scheduler.hold(1, true));
        assert_eq!(jobs(&scheduler.admit()), [2]);
        assert!(scheduler.hold(1, false));
        assert!(scheduler.admit().is_empty(), "job 2 still has the disk");
        assert!(scheduler.withdraw(1));
        assert!(!scheduler.withdraw(1));
        assert!(!scheduler.hold(9, true));
    }

    #[test]
    fn a_disk_listed_twice_takes_one_slot() {
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![hdd(0), hdd(0)]);
        let started = scheduler.admit();
        assert_eq!(started[0].1.len(), 1);
    }

    #[test]
    fn an_unknown_disk_counts_as_a_hard_disk() {
        let share = || Disk {
            key: DiskKey::Volume(r"\\server\share".to_owned()),
            solid_state: false,
        };
        let mut scheduler = Scheduler::new(4);
        scheduler.submit(1, Priority::Copy, vec![share()]);
        scheduler.submit(2, Priority::Copy, vec![share()]);
        assert_eq!(jobs(&scheduler.admit()), [1]);
    }
}
