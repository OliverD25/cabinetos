//! The ring buffer of recent events: the last [`RING_CAPACITY`] formatted log
//! lines, kept in memory so a crash trace can show what led up to the crash.

use std::collections::VecDeque;
use std::sync::{Mutex, PoisonError, TryLockError};

use tracing::{Event, Subscriber};
use tracing_subscriber::layer::Context;
use tracing_subscriber::registry::LookupSpan;

use crate::Boundary;
use crate::format::render_line;

/// How many recent events the ring buffer keeps.
pub const RING_CAPACITY: usize = 256;

/// The process-wide ring buffer that the ring layer fills.
static RING: RingBuffer = RingBuffer::new(RING_CAPACITY);

/// The most recent formatted events of this process, oldest first and newest
/// last. At most [`RING_CAPACITY`] lines.
pub fn recent_events() -> Vec<String> {
    RING.snapshot()
}

/// Like [`recent_events`], but gives up instead of waiting for the lock. The
/// panic hook uses it: if the panicking thread itself holds the lock, waiting
/// would hang the dying process. `None` means the lock stayed busy.
pub(crate) fn try_recent_events() -> Option<Vec<String>> {
    RING.try_snapshot()
}

pub(crate) struct RingBuffer {
    lines: Mutex<VecDeque<String>>,
    capacity: usize,
}

impl RingBuffer {
    pub(crate) const fn new(capacity: usize) -> Self {
        Self {
            lines: Mutex::new(VecDeque::new()),
            capacity,
        }
    }

    pub(crate) fn push(&self, line: String) {
        let mut lines = self.lines.lock().unwrap_or_else(PoisonError::into_inner);
        if lines.len() == self.capacity {
            lines.pop_front();
        }
        lines.push_back(line);
    }

    pub(crate) fn snapshot(&self) -> Vec<String> {
        let lines = self.lines.lock().unwrap_or_else(PoisonError::into_inner);
        lines.iter().cloned().collect()
    }

    /// Tries the lock a limited number of times. Another thread holds it only
    /// for a push, so a few yields are enough; if this thread holds it, no
    /// number of tries would help.
    pub(crate) fn try_snapshot(&self) -> Option<Vec<String>> {
        for _ in 0..1000 {
            match self.lines.try_lock() {
                Ok(lines) => return Some(lines.iter().cloned().collect()),
                Err(TryLockError::Poisoned(poisoned)) => {
                    return Some(poisoned.into_inner().iter().cloned().collect());
                }
                Err(TryLockError::WouldBlock) => std::thread::yield_now(),
            }
        }
        None
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
    use super::*;

    #[test]
    fn keeps_at_most_capacity_and_returns_newest_last() {
        let ring = RingBuffer::new(RING_CAPACITY);
        for n in 0..300 {
            ring.push(format!("line {n}"));
        }
        let lines = ring.snapshot();
        assert_eq!(lines.len(), RING_CAPACITY);
        assert_eq!(lines.first().unwrap(), "line 44");
        assert_eq!(lines.last().unwrap(), "line 299");
    }

    #[test]
    fn try_snapshot_gives_up_while_the_lock_is_held() {
        let ring = RingBuffer::new(4);
        ring.push("a".to_owned());
        let held = ring.lines.lock().unwrap();
        assert_eq!(ring.try_snapshot(), None);
        drop(held);
        assert_eq!(ring.try_snapshot(), Some(vec!["a".to_owned()]));
    }
}
