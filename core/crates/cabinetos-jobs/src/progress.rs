//! Progress events: at most 30 `job_progress` per second per job (brief §3,
//! "throttled IPC progress"), whatever the copy does. The byte counters are
//! atomics the copying threads bump after every chunk; a publisher reads
//! them on a clock and sends a record only when something changed.

use std::time::{Duration, Instant};

use cabinetos_protocol::{Event, JobProgress, Rate};

use crate::EventSink;
use crate::job::{Counters, Job, lock};

/// The smallest gap between two progress events of one job: 34 ms, so no
/// one-second window can hold more than 30 of them.
pub const MIN_PROGRESS_GAP: Duration = Duration::from_millis(34);

/// The speed is averaged over about this long.
const AVERAGE_OVER_SECONDS: f64 = 1.0;

/// No time left is estimated in the first seconds: the speed is still
/// settling.
const ETA_AFTER_MS: u64 = 2000;

/// A rate (bytes or items per second) as an exponential moving average:
/// each sample weighs in by the time it covers, so a sample one second old
/// counts for about a third of a fresh one.
#[derive(Debug, Default)]
pub(crate) struct Meter {
    last: Option<(Instant, u64)>,
    speed: f64,
}

impl Meter {
    /// Takes a reading of the count done so far and returns the rate per
    /// second; `None` at the first reading, which has no interval yet.
    #[expect(clippy::cast_precision_loss, reason = "a rate for display")]
    pub(crate) fn rate(&mut self, now: Instant, count: u64) -> Option<f64> {
        let measured = self.last.is_some();
        if let Some((then, before)) = self.last {
            let seconds = now.duration_since(then).as_secs_f64();
            if seconds > 0.0 {
                let rate = count.saturating_sub(before) as f64 / seconds;
                let weight = 1.0 - (-seconds / AVERAGE_OVER_SECONDS).exp();
                self.speed += weight * (rate - self.speed);
            }
        }
        self.last = Some((now, count));
        measured.then_some(self.speed.max(0.0))
    }

    /// Takes a reading of the bytes done so far and returns the speed in
    /// bytes per second.
    #[expect(
        clippy::cast_possible_truncation,
        clippy::cast_sign_loss,
        reason = "a speed for display"
    )]
    pub(crate) fn sample(&mut self, now: Instant, bytes: u64) -> u64 {
        self.rate(now, bytes).unwrap_or(0.0) as u64
    }

    /// Forgets the speed (paused, or finished).
    pub(crate) fn reset(&mut self, now: Instant, bytes: u64) {
        self.last = Some((now, bytes));
        self.speed = 0.0;
    }
}

/// Seconds left at `speed`, once the speed has had time to settle.
fn eta(progress: &JobProgress) -> Option<u64> {
    if progress.bytes_total == 0 || progress.speed_bps == 0 || progress.elapsed_ms < ETA_AFTER_MS {
        return None;
    }
    Some(
        progress
            .bytes_total
            .saturating_sub(progress.bytes_done)
            .div_ceil(progress.speed_bps),
    )
}

/// Whether two records differ in anything but the clock.
fn changed(before: &JobProgress, now: &JobProgress) -> bool {
    let mut before = before.clone();
    before.elapsed_ms = now.elapsed_ms;
    before.eta_seconds = now.eta_seconds;
    before != *now
}

/// Sends the job's progress if it changed and the last record went out at
/// least `gap` ago. The final record (`last`) always goes out, after
/// waiting out the gap, and nothing follows it.
pub(crate) fn publish(job: &Job, sink: &EventSink, gap: Duration, last: bool) {
    let mut emitter = lock(&job.emitter);
    // Once a job has ended, only its final record goes out.
    if emitter.finished || (!last && job.is_done()) {
        return;
    }
    if let Some(sent) = emitter.last_emit {
        let since = sent.elapsed();
        if since < gap {
            if !last {
                return;
            }
            std::thread::sleep(gap.saturating_sub(since));
        }
    }
    let now = Instant::now();
    let bytes = Counters::get(&job.counters.bytes_done);
    let items = Counters::get(&job.counters.files_done);
    let (speed, pace) = if last || job.control.is_paused() {
        emitter.meter.reset(now, bytes);
        emitter.items.reset(now, items);
        (0, Some(0.0))
    } else {
        (
            emitter.meter.sample(now, bytes),
            emitter.items.rate(now, items),
        )
    };
    let pace = pace.and_then(|rate| Rate::new((rate * 100.0).round() / 100.0));
    let mut progress = job.progress(speed, pace, None);
    progress.eta_seconds = eta(&progress);
    let send = last
        || emitter
            .last_sent
            .as_ref()
            .is_none_or(|sent| changed(sent, &progress));
    if send {
        emitter.last_emit = Some(now);
        emitter.last_sent = Some(progress.clone());
        emitter.finished = last;
        sink(Event::JobProgress(progress));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_speed_follows_the_rate_within_about_a_second() {
        let start = Instant::now();
        let mut meter = Meter::default();
        assert_eq!(meter.sample(start, 0), 0);
        let mut speed = 0;
        // 100 MB/s, sampled every 34 ms for two seconds.
        for step in 1..=59_u32 {
            let now = start + MIN_PROGRESS_GAP * step;
            speed = meter.sample(now, u64::from(step) * 3_400_000);
        }
        assert!((85_000_000..=100_000_000).contains(&speed), "{speed}");
        // A stall brings it down again within a second or so.
        let stalled = start + MIN_PROGRESS_GAP * 59 + Duration::from_secs(2);
        assert!(meter.sample(stalled, 59 * 3_400_000) < 20_000_000);
        meter.reset(stalled, 0);
        assert_eq!(meter.sample(stalled + Duration::from_millis(1), 0), 0);
    }

    #[test]
    fn the_pace_counts_items_and_waits_for_a_second_reading() {
        let start = Instant::now();
        let mut meter = Meter::default();
        assert_eq!(meter.rate(start, 0), None);
        let mut pace = 0.0;
        // 400 files a second, read every 34 ms for two seconds.
        for step in 1..=59_u32 {
            let now = start + MIN_PROGRESS_GAP * step;
            pace = meter.rate(now, u64::from(step) * 400 * 34 / 1000).unwrap();
        }
        assert!((330.0..=420.0).contains(&pace), "{pace}");
    }

    #[test]
    fn the_time_left_waits_for_the_speed_to_settle() {
        let mut progress = JobProgress {
            job_id: 1,
            state: cabinetos_protocol::JobState::Running,
            bytes_done: 1_000,
            bytes_total: 11_000,
            files_done: 0,
            files_total: 1,
            files_skipped: 0,
            files_failed: 0,
            conflicts_open: 0,
            current_path: None,
            speed_bps: 1_000,
            items_per_second: None,
            eta_seconds: None,
            elapsed_ms: 1_500,
        };
        assert_eq!(eta(&progress), None);
        progress.elapsed_ms = 2_500;
        assert_eq!(eta(&progress), Some(10));
        progress.speed_bps = 0;
        assert_eq!(eta(&progress), None);
    }
}
