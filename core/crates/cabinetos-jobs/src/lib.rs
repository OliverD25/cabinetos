//! Job engine: drive-aware copy, move and delete queues with throttled progress
//! and per-file conflict handling.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: the interface
//! never freezes during heavy file operations) and Article 5 (Dual-Pane
//! Foundation: transfers between panes). Brief §3.
//!
//! Status: stub. Phase 4 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early. Unsafe code
//! will be allowed here, isolated per module, once `CopyFileExW` arrives.

/// Owns one queue per physical disk: one job at a time on an HDD, several in
/// parallel on NVMe and SSD drives.
pub struct JobQueueManager;

/// Identifies one copy, move or delete job.
pub struct JobId;

/// Progress of a job, coalesced to at most 30 updates per second before it is
/// sent to the UI.
pub struct JobProgress;

/// State of one file that hit a conflict ("file exists", "access denied").
/// That file pauses; the rest of the batch continues.
pub struct ConflictState;
