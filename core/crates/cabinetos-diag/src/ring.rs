//! The ring buffer of recent events: the last [`RING_CAPACITY`] formatted log
//! lines, kept in memory so a crash trace can show what led up to the crash.
//!
//! Writers never lock (Article 12 asks for a lock-free pipeline): every event
//! goes into a bounded lock-free queue that drops its oldest line when full.
//! Readers move the queued lines into an archive of their own and copy it.
//! Only readers (a crash trace, a diagnostics dump) take the archive's lock,
//! and they are rare.

use std::collections::VecDeque;
use std::sync::{LazyLock, Mutex, PoisonError, TryLockError};

use crossbeam_queue::ArrayQueue;
use tracing::{Event, Subscriber};
use tracing_subscriber::layer::Context;
use tracing_subscriber::registry::LookupSpan;

use crate::Boundary;
use crate::format::render_line;

/// How many recent events the ring buffer keeps.
pub const RING_CAPACITY: usize = 256;

/// The process-wide ring buffer that the ring layer fills.
static RING: LazyLock<RingBuffer> = LazyLock::new(|| RingBuffer::new(RING_CAPACITY));

/// The most recent formatted events of this process, oldest first and newest
/// last. At most [`RING_CAPACITY`] lines. Never blocks a thread that logs.
pub fn recent_events() -> Vec<String> {
    RING.snapshot()
}

/// Like [`recent_events`], but never waits, for the panic hook: the panicking
/// thread may itself be in the middle of a snapshot.
pub(crate) fn recent_events_without_waiting() -> Vec<String> {
    RING.snapshot_without_waiting()
}

pub(crate) struct RingBuffer {
    /// Where writers put lines. Lock-free; drops the oldest line when full.
    queue: ArrayQueue<String>,
    /// Lines already taken out of the queue, oldest first. Only readers use it.
    archive: Mutex<VecDeque<String>>,
    capacity: usize,
}

impl RingBuffer {
    pub(crate) fn new(capacity: usize) -> Self {
        Self {
            queue: ArrayQueue::new(capacity),
            archive: Mutex::new(VecDeque::with_capacity(capacity)),
            capacity,
        }
    }

    pub(crate) fn push(&self, line: String) {
        self.queue.force_push(line);
    }

    pub(crate) fn snapshot(&self) -> Vec<String> {
        let mut archive = self.archive.lock().unwrap_or_else(PoisonError::into_inner);
        self.drain_into(&mut archive);
        archive.iter().cloned().collect()
    }

    /// Tries the archive a limited number of times; another reader holds it
    /// only briefly. If it stays busy (this very thread may hold it), returns
    /// the queued lines alone: the newest ones, which matter most in a crash.
    pub(crate) fn snapshot_without_waiting(&self) -> Vec<String> {
        for _ in 0..100 {
            match self.archive.try_lock() {
                Ok(mut archive) => {
                    self.drain_into(&mut archive);
                    return archive.iter().cloned().collect();
                }
                Err(TryLockError::Poisoned(poisoned)) => {
                    let mut archive = poisoned.into_inner();
                    self.drain_into(&mut archive);
                    return archive.iter().cloned().collect();
                }
                Err(TryLockError::WouldBlock) => std::thread::yield_now(),
            }
        }
        let mut lines = VecDeque::with_capacity(self.capacity);
        self.drain_into(&mut lines);
        lines.into()
    }

    /// Moves queued lines to the end of `archive`, keeping it at `capacity`.
    /// Takes at most `capacity` lines, so busy writers cannot keep a reader
    /// here forever; later lines stay queued for the next reader.
    fn drain_into(&self, archive: &mut VecDeque<String>) {
        for _ in 0..self.capacity {
            let Some(line) = self.queue.pop() else { break };
            if archive.len() == self.capacity {
                archive.pop_front();
            }
            archive.push_back(line);
        }
    }
}

/// A `tracing` layer that renders every event with the log-file format and
/// keeps the line in the process-wide ring buffer.
pub(crate) struct RingLayer {
    boundary: Boundary,
}

impl RingLayer {
    pub(crate) fn new(boundary: Boundary) -> Self {
        Self { boundary }
    }
}

impl<S> tracing_subscriber::Layer<S> for RingLayer
where
    S: Subscriber + for<'a> LookupSpan<'a>,
{
    fn on_event(&self, event: &Event<'_>, ctx: Context<'_, S>) {
        RING.push(render_line(event, ctx.event_scope(event), self.boundary));
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::*;

    fn lines(range: std::ops::Range<usize>) -> Vec<String> {
        range.map(|n| format!("line {n}")).collect()
    }

    #[test]
    fn keeps_at_most_capacity_and_returns_newest_last() {
        let ring = RingBuffer::new(RING_CAPACITY);
        for line in lines(0..300) {
            ring.push(line);
        }
        let snapshot = ring.snapshot();
        assert_eq!(snapshot, lines(44..300));
    }

    #[test]
    fn a_snapshot_does_not_consume_the_lines() {
        let ring = RingBuffer::new(4);
        for line in lines(0..2) {
            ring.push(line);
        }
        assert_eq!(ring.snapshot(), lines(0..2));
        assert_eq!(ring.snapshot(), lines(0..2));

        // Older lines from the archive and newer ones from the queue join up,
        // still limited to the capacity.
        for line in lines(2..5) {
            ring.push(line);
        }
        assert_eq!(ring.snapshot(), lines(1..5));

        // When the queue overflowed since the last snapshot, only the newest
        // lines survive, in order.
        for line in lines(5..12) {
            ring.push(line);
        }
        assert_eq!(ring.snapshot(), lines(8..12));
    }

    #[test]
    fn without_waiting_falls_back_to_the_queue() {
        let ring = RingBuffer::new(4);
        ring.push("archived".to_owned());
        assert_eq!(ring.snapshot(), vec!["archived".to_owned()]);
        ring.push("queued".to_owned());

        let held = ring.archive.lock().unwrap();
        assert_eq!(ring.snapshot_without_waiting(), vec!["queued".to_owned()]);
        drop(held);
        assert_eq!(ring.snapshot(), vec!["archived".to_owned()]);
    }

    #[test]
    fn writers_never_wait_for_a_reader() {
        let ring = Arc::new(RingBuffer::new(8));
        let held = ring.archive.lock().unwrap();
        let writer = {
            let ring = Arc::clone(&ring);
            std::thread::spawn(move || {
                for line in lines(0..1000) {
                    ring.push(line);
                }
            })
        };
        // The writer finishes although a reader holds the archive.
        writer.join().unwrap();
        drop(held);
        assert_eq!(ring.snapshot(), lines(992..1000));
    }
}
