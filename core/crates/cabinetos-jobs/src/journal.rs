//! The undo journal (`docs/jobs.md`, "Undo"): at its end, every job that
//! changed something writes one line to `journal.jsonl` in the undo folder,
//! with what it did per entry. A file a job replaces is first moved to
//! `<undo folder>\<job>\<n>`. The journal keeps the last 200 jobs and the
//! saved copies at most 256 MiB, the oldest removed first. `undo_job`
//! turns a job's line back into steps that reverse it.

use std::collections::HashSet;
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use cabinetos_protocol::{ErrorCode, JobKind, JobState, JobStep, UndoLeft, UndoLeftReason};
use serde::{Deserialize, Serialize};

use crate::JobError;
use crate::job::{Job, lock};

/// The journal's file in the undo folder.
pub(crate) const FILE: &str = "journal.jsonl";

/// Jobs the journal keeps; older lines go, with their saved copies.
pub(crate) const KEEP_JOBS: usize = 200;

/// The most the saved copies may take together; a larger file is replaced
/// without a saved copy.
pub(crate) const SAVED_CAP: u64 = 256 * 1024 * 1024;

/// Entries one job records; a job that does more cannot be undone.
pub(crate) const MAX_ENTRIES: usize = 100_000;

/// One thing a job did.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub(crate) enum UndoEntry {
    /// It made this file or folder (a copy, a new folder or file).
    Created { path: String },
    /// It renamed or moved `from` to `to`.
    Moved { from: String, to: String },
    /// It replaced this file; the old one is `saved`, or was not saved.
    Overwritten { path: String, saved: Option<String> },
    /// It put this into the Recycle Bin.
    Recycled { path: String },
    /// It deleted this for good.
    Deleted { path: String },
    /// A move removed this source folder once it was empty.
    RemovedFolder { path: String },
    /// It put a saved copy back here (an undo).
    Restored { path: String },
}

/// What a job records while it runs.
#[derive(Debug, Default)]
pub(crate) struct UndoLog {
    pub(crate) entries: Vec<UndoEntry>,
    /// More than `MAX_ENTRIES`: the entries were dropped.
    pub(crate) truncated: bool,
    /// Saved copies made so far: the next one's number.
    pub(crate) saved: u64,
}

/// One line of the journal.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct JobRecord {
    pub(crate) job: u64,
    /// `copy`, `move`, `delete`, `delete_permanently` or `steps`.
    pub(crate) kind: String,
    /// How it ended: `completed`, `completed_with_errors`, `failed` or
    /// `cancelled`.
    pub(crate) state: String,
    /// Milliseconds since 1970-01-01 UTC.
    pub(crate) ended_ms: u64,
    /// The job this one undid.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub(crate) undoes: Option<u64>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub(crate) truncated: bool,
    pub(crate) entries: Vec<UndoEntry>,
}

/// The steps that reverse a job, and what they cannot bring back.
#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct UndoPlan {
    pub(crate) steps: Vec<JobStep>,
    pub(crate) left: Vec<UndoLeft>,
}

/// The journal in one undo folder.
#[derive(Debug)]
pub(crate) struct Journal {
    dir: PathBuf,
    /// Lines in the file; guards every write.
    lines: Mutex<usize>,
}

impl Journal {
    /// Opens the journal in `dir` (created at the first write), and makes
    /// new job IDs follow the newest in it, so an ID names one job across
    /// restarts of the core.
    pub(crate) fn open(dir: PathBuf) -> Self {
        let records = read_records(&dir.join(FILE));
        if let Some(newest) = records.iter().map(|record| record.job).max() {
            crate::continue_job_ids_after(newest);
        }
        Self {
            dir,
            lines: Mutex::new(records.len()),
        }
    }

    /// A free path for the saved copy of a file `job` is about to replace.
    pub(crate) fn save_path(&self, job: &Job) -> Option<String> {
        let folder = self.dir.join(job.id.to_string());
        if let Err(error) = std::fs::create_dir_all(&folder) {
            tracing::warn!(folder = %folder.display(), %error, "cannot make a folder for saved copies");
            return None;
        }
        let number = {
            let mut log = lock(&job.undo);
            log.saved += 1;
            log.saved - 1
        };
        Some(folder.join(number.to_string()).display().to_string())
    }

    /// Writes the line of a job that ended, when it changed anything; then
    /// keeps the last 200 jobs and the saved copies under the cap.
    pub(crate) fn write(&self, job: &Job, state: &JobState, undoes: Option<u64>) {
        let (entries, truncated) = {
            let mut log = lock(&job.undo);
            (std::mem::take(&mut log.entries), log.truncated)
        };
        if entries.is_empty() && !truncated {
            return;
        }
        let record = JobRecord {
            job: job.id,
            kind: kind_name(&job.request.kind).to_owned(),
            state: state_name(state).to_owned(),
            ended_ms: now_ms(),
            undoes,
            truncated,
            entries,
        };
        let mut lines = lock(&self.lines);
        if let Err(error) = self.append(&record) {
            tracing::warn!(job_id = job.id, %error, "cannot write the undo journal; this job cannot be undone");
            return;
        }
        *lines += 1;
        if *lines > KEEP_JOBS {
            *lines = self.trim();
        }
        self.cap_saved_copies(SAVED_CAP);
    }

    fn append(&self, record: &JobRecord) -> std::io::Result<()> {
        std::fs::create_dir_all(&self.dir)?;
        let mut line = serde_json::to_string(record).map_err(std::io::Error::other)?;
        line.push('\n');
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.dir.join(FILE))?
            .write_all(line.as_bytes())
    }

    /// Drops the oldest lines beyond `KEEP_JOBS`, and their saved copies.
    /// Returns the lines left.
    fn trim(&self) -> usize {
        let path = self.dir.join(FILE);
        let records = read_records(&path);
        let excess = records.len().saturating_sub(KEEP_JOBS);
        let (dropped, kept) = records.split_at(excess);
        let mut text = String::new();
        for record in kept {
            if let Ok(line) = serde_json::to_string(record) {
                text.push_str(&line);
                text.push('\n');
            }
        }
        let temporary = self.dir.join(format!("{FILE}.new"));
        let written =
            std::fs::write(&temporary, text).and_then(|()| std::fs::rename(&temporary, &path));
        if let Err(error) = written {
            tracing::warn!(%error, "cannot trim the undo journal");
            return records.len();
        }
        for record in dropped {
            let _ = std::fs::remove_dir_all(self.dir.join(record.job.to_string()));
        }
        kept.len()
    }

    /// Removes the oldest jobs' saved copies until the rest take at most
    /// `cap` bytes.
    fn cap_saved_copies(&self, cap: u64) {
        let Ok(folders) = std::fs::read_dir(&self.dir) else {
            return;
        };
        let mut jobs: Vec<(u64, PathBuf, u64)> = folders
            .flatten()
            .filter_map(|entry| {
                let job = entry.file_name().to_str()?.parse::<u64>().ok()?;
                let path = entry.path();
                let size = folder_size(&path);
                Some((job, path, size))
            })
            .collect();
        jobs.sort_by_key(|(job, _, _)| *job);
        let mut total: u64 = jobs.iter().map(|(_, _, size)| size).sum();
        for (job, path, size) in jobs {
            if total <= cap {
                break;
            }
            match std::fs::remove_dir_all(&path) {
                Ok(()) => {
                    tracing::info!(
                        job_id = job,
                        bytes = size,
                        "saved copies removed to keep the undo folder under 256 MiB"
                    );
                    total -= size;
                }
                Err(error) => {
                    tracing::warn!(folder = %path.display(), %error, "cannot remove old saved copies");
                }
            }
        }
    }

    /// The line of `job`, or of the newest job that is not an undo and was
    /// not undone yet. Also the job that undid it, if one did.
    pub(crate) fn find(&self, job: Option<u64>) -> Result<(JobRecord, Option<u64>), JobError> {
        let records = {
            let _writing = lock(&self.lines);
            read_records(&self.dir.join(FILE))
        };
        let undone_by = |id: u64| {
            records
                .iter()
                .rev()
                .find(|record| record.undoes == Some(id))
                .map(|record| record.job)
        };
        let found = match job {
            Some(id) => records.iter().rev().find(|record| record.job == id),
            None => records
                .iter()
                .rev()
                .find(|record| record.undoes.is_none() && undone_by(record.job).is_none()),
        };
        let Some(record) = found else {
            return Err(JobError::new(
                ErrorCode::NoSuchJob,
                match job {
                    Some(id) => format!(
                        "job {id} is not in the undo journal: it changed nothing, or it is older than the last {KEEP_JOBS} jobs"
                    ),
                    None => "the undo journal has no job left to undo".to_owned(),
                },
            ));
        };
        Ok((record.clone(), undone_by(record.job)))
    }
}

/// Every line of the journal that reads; a damaged line is skipped.
fn read_records(path: &Path) -> Vec<JobRecord> {
    let Ok(file) = std::fs::File::open(path) else {
        return Vec::new();
    };
    BufReader::new(file)
        .lines()
        .map_while(Result::ok)
        .filter_map(|line| serde_json::from_str(&line).ok())
        .collect()
}

fn folder_size(path: &Path) -> u64 {
    std::fs::read_dir(path).map_or(0, |entries| {
        entries
            .flatten()
            .filter_map(|entry| entry.metadata().ok())
            .map(|metadata| metadata.len())
            .sum()
    })
}

fn kind_name(kind: &JobKind) -> &'static str {
    match kind {
        JobKind::Copy => "copy",
        JobKind::Move => "move",
        JobKind::Delete { permanent: false } => "delete",
        JobKind::Delete { permanent: true } => "delete_permanently",
        JobKind::Steps { .. } => "steps",
    }
}

fn state_name(state: &JobState) -> &'static str {
    match state {
        JobState::Completed => "completed",
        JobState::CompletedWithErrors => "completed_with_errors",
        JobState::Failed { .. } => "failed",
        JobState::Cancelled => "cancelled",
        _ => "running",
    }
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            u64::try_from(since.as_millis()).unwrap_or(u64::MAX)
        })
}

/// The steps that reverse `record`, newest entry first: moves go back,
/// saved copies come back, what the job made goes to the Recycle Bin (only
/// the outermost: a folder takes what it holds along), removed folders
/// come back first of all.
pub(crate) fn plan_undo(record: &JobRecord) -> UndoPlan {
    let created: HashSet<String> = record
        .entries
        .iter()
        .filter_map(|entry| match entry {
            UndoEntry::Created { path } => Some(path.to_lowercase()),
            _ => None,
        })
        .collect();
    let inside_created = |path: &str| {
        let lower = path.to_lowercase();
        Path::new(&lower)
            .ancestors()
            .skip(1)
            .any(|ancestor| created.contains(&ancestor.display().to_string()))
    };
    let mut plan = UndoPlan::default();
    let mut left = |path: &str, reason| {
        plan.left.push(UndoLeft {
            path: path.to_owned(),
            reason,
        });
    };
    let mut steps = Vec::new();
    for entry in record.entries.iter().rev() {
        match entry {
            UndoEntry::Created { path } => {
                if !inside_created(path) {
                    steps.push(JobStep::Recycle { path: path.clone() });
                }
            }
            UndoEntry::Moved { from, to } => steps.push(JobStep::Rename {
                from: to.clone(),
                to: from.clone(),
            }),
            UndoEntry::Overwritten {
                path,
                saved: Some(saved),
            } if Path::new(saved).is_file() => steps.push(JobStep::Restore {
                saved: saved.clone(),
                to: path.clone(),
            }),
            UndoEntry::Overwritten {
                path,
                saved: Some(_),
            } => left(path, UndoLeftReason::SavedCopyRemoved),
            UndoEntry::Overwritten { path, saved: None } => left(path, UndoLeftReason::NotSaved),
            UndoEntry::Recycled { path } => left(path, UndoLeftReason::InRecycleBin),
            UndoEntry::Deleted { path } => left(path, UndoLeftReason::DeletedForGood),
            UndoEntry::Restored { path } => left(path, UndoLeftReason::PutBack),
            UndoEntry::RemovedFolder { path } => {
                steps.push(JobStep::CreateFolder { path: path.clone() });
            }
        }
    }
    // A removed source folder must stand before anything moves back into
    // it: those steps go first, parents before children (the move removed
    // them children first, so the reversed order has parents first).
    let (folders, rest): (Vec<JobStep>, Vec<JobStep>) = steps
        .into_iter()
        .partition(|step| matches!(step, JobStep::CreateFolder { .. }));
    plan.steps = folders.into_iter().chain(rest).collect();
    plan
}

/// Why nothing of `record` can be undone, when nothing can.
pub(crate) fn refusal(record: &JobRecord, plan: &UndoPlan) -> Option<String> {
    let id = record.job;
    if record.truncated {
        return Some(format!(
            "job {id} changed more than {MAX_ENTRIES} entries; the journal does not keep that much, so it cannot be undone"
        ));
    }
    if !plan.steps.is_empty() {
        return None;
    }
    let all = |reason| plan.left.iter().all(|left| left.reason == reason);
    Some(if all(UndoLeftReason::InRecycleBin) {
        format!(
            "job {id} put its files into the Recycle Bin; restore them from there (open the Recycle Bin, select them, Restore)"
        )
    } else if all(UndoLeftReason::DeletedForGood) {
        format!("job {id} deleted its files for good; nothing can bring them back")
    } else {
        format!("nothing that job {id} did can be reversed")
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(entries: Vec<UndoEntry>) -> JobRecord {
        JobRecord {
            job: 7,
            kind: "move".to_owned(),
            state: "completed".to_owned(),
            ended_ms: 1,
            undoes: None,
            truncated: false,
            entries,
        }
    }

    fn created(path: &str) -> UndoEntry {
        UndoEntry::Created {
            path: path.to_owned(),
        }
    }

    #[test]
    fn an_undo_reverses_newest_first_and_folders_come_back_first() {
        let saved = tempfile::NamedTempFile::new().unwrap();
        let saved = saved.path().display().to_string();
        let plan = plan_undo(&record(vec![
            created(r"D:\to\A"),
            created(r"D:\to\A\x.txt"),
            UndoEntry::Moved {
                from: r"C:\from\A\y.txt".to_owned(),
                to: r"D:\to\A\y.txt".to_owned(),
            },
            UndoEntry::Overwritten {
                path: r"D:\to\z.txt".to_owned(),
                saved: Some(saved.clone()),
            },
            UndoEntry::RemovedFolder {
                path: r"C:\from\A\sub".to_owned(),
            },
            UndoEntry::RemovedFolder {
                path: r"C:\from\A".to_owned(),
            },
        ]));
        assert_eq!(
            plan.steps,
            [
                JobStep::CreateFolder {
                    path: r"C:\from\A".to_owned()
                },
                JobStep::CreateFolder {
                    path: r"C:\from\A\sub".to_owned()
                },
                JobStep::Restore {
                    saved,
                    to: r"D:\to\z.txt".to_owned()
                },
                JobStep::Rename {
                    from: r"D:\to\A\y.txt".to_owned(),
                    to: r"C:\from\A\y.txt".to_owned()
                },
                // x.txt lies in the created folder A: it goes along.
                JobStep::Recycle {
                    path: r"D:\to\A".to_owned()
                },
            ]
        );
        assert!(plan.left.is_empty());
    }

    #[test]
    fn what_cannot_come_back_is_named_and_a_delete_is_refused_with_advice() {
        let plan = plan_undo(&record(vec![
            UndoEntry::Overwritten {
                path: r"D:\a.txt".to_owned(),
                saved: Some(r"C:\nowhere\undo\7\0".to_owned()),
            },
            UndoEntry::Overwritten {
                path: r"D:\b.txt".to_owned(),
                saved: None,
            },
            created(r"D:\c.txt"),
        ]));
        assert_eq!(plan.steps.len(), 1);
        assert_eq!(
            plan.left,
            [
                UndoLeft {
                    path: r"D:\b.txt".to_owned(),
                    reason: UndoLeftReason::NotSaved
                },
                UndoLeft {
                    path: r"D:\a.txt".to_owned(),
                    reason: UndoLeftReason::SavedCopyRemoved
                },
            ]
        );

        let recycled = record(vec![UndoEntry::Recycled {
            path: r"C:\old".to_owned(),
        }]);
        let refused = refusal(&recycled, &plan_undo(&recycled)).unwrap();
        assert!(refused.contains("restore them from there"), "{refused}");
        let deleted = record(vec![UndoEntry::Deleted {
            path: r"C:\old".to_owned(),
        }]);
        assert!(
            refusal(&deleted, &plan_undo(&deleted))
                .unwrap()
                .contains("for good")
        );
        let mut huge = record(Vec::new());
        huge.truncated = true;
        assert!(
            refusal(&huge, &plan_undo(&huge))
                .unwrap()
                .contains("100000")
        );
    }

    #[test]
    fn the_oldest_saved_copies_go_first() {
        let dir = tempfile::tempdir().unwrap();
        for (job, bytes) in [(3, 40), (12, 30), (5, 20)] {
            let folder = dir.path().join(job.to_string());
            std::fs::create_dir_all(&folder).unwrap();
            std::fs::write(folder.join("0"), vec![b'x'; bytes]).unwrap();
        }
        std::fs::create_dir_all(dir.path().join("not-a-job")).unwrap();
        let journal = Journal::open(dir.path().to_owned());
        journal.cap_saved_copies(55);
        // 90 bytes: job 3 goes (50 left), and that is enough.
        assert!(!dir.path().join("3").exists());
        assert!(dir.path().join("5").exists());
        assert!(dir.path().join("12").exists());
        assert!(dir.path().join("not-a-job").exists());
    }

    #[test]
    fn the_journal_line_has_its_documented_form() {
        let line = serde_json::to_value(record(vec![
            UndoEntry::Moved {
                from: "a".to_owned(),
                to: "b".to_owned(),
            },
            UndoEntry::Overwritten {
                path: "c".to_owned(),
                saved: None,
            },
        ]))
        .unwrap();
        assert_eq!(
            line,
            serde_json::json!({
                "job": 7, "kind": "move", "state": "completed", "ended_ms": 1,
                "entries": [
                    {"op": "moved", "from": "a", "to": "b"},
                    {"op": "overwritten", "path": "c", "saved": null},
                ],
            })
        );
    }
}
