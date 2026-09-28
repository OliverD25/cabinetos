//! Jobs: copy, move and delete (`docs/jobs.md`).
//!
//! A client starts a job with `start_job` and follows it through events:
//! `job_progress` (at most 30 per second per job), `job_conflict` for a file
//! that needs a decision, and `job_state_changed`. Jobs belong to the core,
//! not to the connection that started them.

use serde::{Deserialize, Serialize};

/// What a job does.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum JobKind {
    /// Copy the sources into the destination folder.
    Copy,
    /// Move the sources into the destination folder: a rename on the same
    /// volume, a copy and then a delete of each source file across volumes.
    Move,
    /// Delete the sources.
    Delete {
        /// Delete for good. Without it the sources go to the Recycle Bin.
        #[serde(default)]
        permanent: bool,
    },
}

/// A job to start.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct JobRequest {
    /// Copy, move or delete.
    pub kind: JobKind,
    /// Absolute paths of the files and folders to work on.
    pub sources: Vec<String>,
    /// The absolute path of the folder to copy or move into; created when it
    /// does not exist. A delete has none.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub destination: Option<String>,
    /// How to handle conflicts, links, verification and timestamps.
    #[serde(default)]
    pub options: JobOptions,
}

/// How a job behaves.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(default)]
pub struct JobOptions {
    /// What to do when a file already exists at the destination.
    pub on_conflict: ConflictPolicy,
    /// What to do with symbolic links and junctions.
    pub copy_links: LinkPolicy,
    /// After each copy, compare the sizes and sampled bytes of the source
    /// and the copy.
    pub verify: bool,
    /// Give each copy the creation, last-access and last-write times of its
    /// source.
    pub preserve_timestamps: bool,
}

impl Default for JobOptions {
    fn default() -> Self {
        Self {
            on_conflict: ConflictPolicy::Ask,
            copy_links: LinkPolicy::AsLink,
            verify: false,
            preserve_timestamps: true,
        }
    }
}

/// What to do when a file already exists at the destination.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ConflictPolicy {
    /// Set the file aside, report a `job_conflict`, and go on with the rest.
    #[default]
    Ask,
    /// Replace the existing file.
    Overwrite,
    /// Replace the existing file only when the source is newer; otherwise
    /// skip it.
    OverwriteIfNewer,
    /// Keep the existing file and skip the source.
    Skip,
    /// Give the new file a free name: `name (2).ext`.
    Rename,
}

/// What to do with symbolic links and junctions.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum LinkPolicy {
    /// Copy the link itself; the copy points where the original points.
    #[default]
    AsLink,
    /// Copy what the link points to.
    FollowTarget,
}

/// Where a job is in its life.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum JobState {
    /// Waiting for its disks: another job is using them.
    Queued,
    /// Walking the sources to count the files and bytes.
    Scanning,
    /// Working. Files waiting for a conflict decision do not change this.
    Running,
    /// Stopped by the user, or by a full disk, until resumed.
    Paused,
    /// Done; every file was handled (skipped files count as handled).
    Completed,
    /// Done, but some files failed.
    CompletedWithErrors,
    /// Stopped for good by the user.
    Cancelled,
    /// Stopped by an error that concerns the whole job.
    Failed {
        /// What went wrong.
        message: String,
    },
}

impl JobState {
    /// Whether the job has ended; no more events follow its last
    /// `job_state_changed`.
    #[must_use]
    pub const fn is_terminal(&self) -> bool {
        matches!(
            self,
            Self::Completed | Self::CompletedWithErrors | Self::Cancelled | Self::Failed { .. }
        )
    }
}

/// How far a job has come.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct JobProgress {
    /// The job, from `job_started`.
    pub job_id: u64,
    /// Its state.
    pub state: JobState,
    /// Bytes copied so far.
    pub bytes_done: u64,
    /// Bytes to copy in all; 0 for a delete or a move on one volume.
    pub bytes_total: u64,
    /// Files and folders handled so far, skipped and failed ones included.
    pub files_done: u64,
    /// Files and folders to handle in all.
    pub files_total: u64,
    /// Of `files_done`, the ones skipped by a conflict decision.
    pub files_skipped: u64,
    /// Of `files_done`, the ones that failed.
    pub files_failed: u64,
    /// Files waiting for a conflict decision.
    pub conflicts_open: u64,
    /// The file being worked on now.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub current_path: Option<String>,
    /// Bytes per second, averaged over about the last second.
    pub speed_bps: u64,
    /// Seconds left at the current speed; absent in the first two seconds
    /// and when there are no bytes to count.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub eta_seconds: Option<u64>,
    /// Milliseconds since the job started working (after `queued`).
    pub elapsed_ms: u64,
}

/// A job as `list_jobs` describes it: its progress and what it works on.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct JobInfo {
    /// Copy, move or delete.
    pub kind: JobKind,
    /// The paths the job was started with.
    pub sources: Vec<String>,
    /// The folder it copies or moves into.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub destination: Option<String>,
    /// How far it has come.
    #[serde(flatten)]
    pub progress: JobProgress,
}

/// A file that needs a decision. The file waits; the rest of the job goes on.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Conflict {
    /// Names the conflict in `resolve_conflict`; unique across all jobs.
    pub conflict_id: u64,
    /// The job it belongs to.
    pub job_id: u64,
    /// What happened.
    pub kind: ConflictKind,
    /// The file or folder the job was working on.
    pub source: String,
    /// Where it was going, for a copy or a move.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub destination: Option<String>,
}

/// What stopped a file. Times are FILETIME ticks (100 ns since 1601-01-01
/// UTC), as in the listing section.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ConflictKind {
    /// The destination already has a file of that name.
    FileExists {
        /// Size of the source in bytes.
        source_size: u64,
        /// Last-write time of the source.
        source_modified: i64,
        /// Size of the existing file in bytes.
        dest_size: u64,
        /// Last-write time of the existing file.
        dest_modified: i64,
    },
    /// Windows denied access, for example to a read-only file.
    AccessDenied,
    /// Another program has the file open.
    SharingViolation,
    /// The path is too long for the destination's file system.
    PathTooLong,
    /// The destination disk is full; the job is paused.
    DiskFull,
    /// The source disappeared before it was handled.
    SourceVanished,
    /// The Recycle Bin cannot take the item: it is bigger than the bin, the
    /// bin is turned off, or the drive has none. Nothing was deleted; only
    /// `delete_permanently` or `skip` answer this.
    RecycleBinTooSmall {
        /// The item's size in bytes (a folder's whole contents).
        size: u64,
    },
    /// Any other error.
    Io {
        /// The Windows error code.
        code: u32,
        /// Its text.
        message: String,
    },
}

impl ConflictKind {
    /// The kind without its details, for `apply_to_same_kind`.
    #[must_use]
    pub const fn tag(&self) -> &'static str {
        match self {
            Self::FileExists { .. } => "file_exists",
            Self::AccessDenied => "access_denied",
            Self::SharingViolation => "sharing_violation",
            Self::PathTooLong => "path_too_long",
            Self::DiskFull => "disk_full",
            Self::SourceVanished => "source_vanished",
            Self::RecycleBinTooSmall { .. } => "recycle_bin_too_small",
            Self::Io { .. } => "io",
        }
    }
}

/// The decision for a file that waits.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Resolution {
    /// Replace the existing file (for `access_denied`: clear the read-only
    /// attribute first).
    Overwrite,
    /// Leave this file out.
    Skip,
    /// Copy or move under another name in the same folder.
    Rename {
        /// The new name (no folder part). Absent: the core picks a free
        /// name, `name (2).ext`.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        new_name: Option<String>,
    },
    /// Try once more, the same way.
    Retry,
    /// Delete for good what the Recycle Bin cannot take. Answers only a
    /// `recycle_bin_too_small` conflict.
    DeletePermanently,
    /// Stop the whole job.
    CancelJob,
}

/// What `job_control` does.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum JobAction {
    /// Stop moving bytes until `resume`; the job keeps its place.
    Pause,
    /// Go on after `pause`.
    Resume,
    /// Stop for good; a partly copied file is removed.
    Cancel,
}
