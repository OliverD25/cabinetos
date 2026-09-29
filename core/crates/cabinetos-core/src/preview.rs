//! Previews of proposed changes (`preview_listing`, `open_preview`,
//! `preview_apply`, `preview_cancel`; `docs/ipc.md`, "Previews").
//!
//! A client or a plugin proposes rows; the core checks them and keeps them
//! under an ID. A window shows a preview as a listing, like a folder, and
//! nothing on disk changes until it is applied: then the rows run in
//! order, as a chain of jobs. A preview not applied within 10 minutes is
//! dropped. Serves Constitution Article 1: the proposal is shown from
//! shared memory, the work is done once, by the job engine.

use std::collections::HashMap;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError, Weak};
use std::time::Duration;

use cabinetos_fs::PreviewEntry;
use cabinetos_jobs::JobQueueManager;
use cabinetos_protocol::shm::{EntryKind, ListingMeta};
use cabinetos_protocol::{
    ChangeKind, ErrorCode, Event, JobKind, JobOptions, JobRequest, JobStep, PreviewRow,
};

use crate::events::EventHub;
use crate::listing::Failure;

/// How long a preview waits to be applied before it is dropped.
const TIME_TO_LIVE: Duration = Duration::from_secs(600);

/// Overrides [`TIME_TO_LIVE`] in milliseconds, for tests.
pub(crate) const TTL_ENV: &str = "CABINETOS_PREVIEW_TTL_MS";

/// Previews one client (or plugin) may keep alive at once.
pub(crate) const MAX_PER_OWNER: usize = 20;

/// Every preview alive.
pub(crate) struct Previews {
    alive: Mutex<HashMap<String, Preview>>,
    next: AtomicU64,
    time_to_live: Duration,
    events: Arc<EventHub>,
    runtime: tokio::runtime::Handle,
    this: Weak<Self>,
}

/// A checked preview.
#[derive(Clone, Debug)]
pub(crate) struct Preview {
    /// Who proposed it: a client (`CabinetOS#2`) or a plugin (`plugin:agent`).
    pub(crate) owner: String,
    pub(crate) title: String,
    pub(crate) rows: Vec<Checked>,
}

/// A row after its checks: `to` is the new full path of a rename and the
/// folder of a move or copy; a created folder's path has no trailing
/// separator.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Checked {
    pub(crate) path: String,
    pub(crate) kind: ChangeKind,
    pub(crate) to: Option<String>,
    /// For `create`: a folder, not a file.
    pub(crate) folder: bool,
}

impl Previews {
    /// The previews of a core; expiry runs on the current Tokio runtime.
    pub(crate) fn start(events: &Arc<EventHub>) -> Arc<Self> {
        Self::new(Arc::clone(events), tokio::runtime::Handle::current())
    }

    /// The previews of a core; expiry runs on `runtime`.
    fn new(events: Arc<EventHub>, runtime: tokio::runtime::Handle) -> Arc<Self> {
        let time_to_live = std::env::var(TTL_ENV)
            .ok()
            .and_then(|value| value.trim().parse::<u64>().ok())
            .map_or(TIME_TO_LIVE, Duration::from_millis);
        Arc::new_cyclic(|this| Self {
            alive: Mutex::new(HashMap::new()),
            next: AtomicU64::new(1),
            time_to_live,
            events,
            runtime,
            this: this.clone(),
        })
    }

    /// Checks `rows` and keeps them as a new preview of `owner`. Returns
    /// its ID.
    pub(crate) fn create(
        &self,
        owner: &str,
        title: String,
        rows: &[PreviewRow],
    ) -> Result<String, Failure> {
        let rows = check_rows(rows)?;
        let id = {
            let mut previews = self.lock();
            let count = previews
                .values()
                .filter(|preview| preview.owner == owner)
                .count();
            if count >= MAX_PER_OWNER {
                return Err((
                    ErrorCode::TooManyPreviews,
                    format!(
                        "{owner} has {MAX_PER_OWNER} previews alive; apply or cancel one first"
                    ),
                ));
            }
            let id = format!("preview-{}", self.next.fetch_add(1, Ordering::Relaxed));
            previews.insert(
                id.clone(),
                Preview {
                    owner: owner.to_owned(),
                    title,
                    rows,
                },
            );
            id
        };
        tracing::info!(preview = %id, owner, "preview proposed");
        let this = self.this.clone();
        let expiring = id.clone();
        let wait = self.time_to_live;
        self.runtime.spawn(async move {
            tokio::time::sleep(wait).await;
            if let Some(previews) = this.upgrade()
                && previews.lock().remove(&expiring).is_some()
            {
                tracing::info!(preview = %expiring, "preview expired: not applied in time");
                previews
                    .events
                    .publish(Event::PreviewCancelled { preview: expiring });
            }
        });
        Ok(id)
    }

    /// A copy of the preview `id`.
    pub(crate) fn get(&self, id: &str) -> Result<Preview, Failure> {
        self.lock().get(id).cloned().ok_or_else(|| no_such(id))
    }

    /// Drops the preview `id` and tells every client.
    pub(crate) fn cancel(&self, id: &str) -> Result<(), Failure> {
        self.lock().remove(id).ok_or_else(|| no_such(id))?;
        tracing::info!(preview = %id, "preview cancelled");
        self.events.publish(Event::PreviewCancelled {
            preview: id.to_owned(),
        });
        Ok(())
    }

    /// Runs the preview `id` as a chain of jobs, in the order of its rows,
    /// and tells every client. The preview is gone afterwards; when the
    /// first job is refused, it stays. Blocking: the first job's paths
    /// are checked on disk.
    pub(crate) fn apply(&self, id: &str, jobs: &JobQueueManager) -> Result<Vec<u64>, Failure> {
        let preview = self.lock().remove(id).ok_or_else(|| no_such(id))?;
        match jobs.start_chain(job_requests(&preview.rows)) {
            Ok(started) => {
                tracing::info!(preview = %id, jobs = ?started, "preview applied");
                self.events.publish(Event::PreviewApplied {
                    preview: id.to_owned(),
                    jobs: started.clone(),
                });
                Ok(started)
            }
            Err(error) => {
                self.lock().insert(id.to_owned(), preview);
                Err((error.code, error.message))
            }
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, Preview>> {
        self.alive.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl Preview {
    /// The rows as they go into the listing section, with what the disk
    /// says about each path now. Blocking: one metadata read per row.
    pub(crate) fn entries(&self) -> Vec<PreviewEntry> {
        self.rows
            .iter()
            .map(|row| {
                if row.kind == ChangeKind::Create {
                    PreviewEntry {
                        path: row.path.clone(),
                        kind: if row.folder {
                            EntryKind::Directory
                        } else {
                            EntryKind::File
                        },
                        meta: ListingMeta::default(),
                        change: row.kind,
                        to: None,
                    }
                } else {
                    PreviewEntry::with_metadata(&row.path, row.kind, row.to.clone())
                }
            })
            .collect()
    }
}

fn no_such(id: &str) -> Failure {
    (
        ErrorCode::NoSuchPreview,
        format!("no preview `{id}`: it was applied, cancelled or expired, or never made"),
    )
}

/// Checks each row and normalizes its target.
fn check_rows(rows: &[PreviewRow]) -> Result<Vec<Checked>, Failure> {
    if rows.is_empty() {
        return Err((
            ErrorCode::ProtocolError,
            "rows is empty; a preview needs at least one change".to_owned(),
        ));
    }
    rows.iter().map(check_row).collect()
}

fn check_row(row: &PreviewRow) -> Result<Checked, Failure> {
    let invalid = |message: String| (ErrorCode::InvalidPath, message);
    let path = row.path.as_str();
    let trimmed = path.trim_end_matches(['\\', '/']);
    if !Path::new(path).is_absolute() || Path::new(trimmed).parent().is_none() {
        return Err(invalid(format!(
            "{path}: not an absolute path below a volume root"
        )));
    }
    let to = row.to.as_deref().filter(|to| !to.is_empty());
    let checked = |to: Option<String>, folder: bool| Checked {
        path: trimmed.to_owned(),
        kind: row.kind,
        to,
        folder,
    };
    match row.kind {
        ChangeKind::Rename => {
            let Some(to) = to else {
                return Err(invalid(format!(
                    "{path}: a rename needs `to`, the new name"
                )));
            };
            let folder = Path::new(trimmed)
                .parent()
                .map(|parent| parent.display().to_string())
                .unwrap_or_default();
            let target = if to.contains(['\\', '/']) {
                let parent = Path::new(to)
                    .parent()
                    .map(|parent| parent.display().to_string());
                if !Path::new(to).is_absolute()
                    || !parent.is_some_and(|parent| parent.eq_ignore_ascii_case(&folder))
                {
                    return Err(invalid(format!(
                        "{path}: a rename stays in its folder; `{to}` is elsewhere (use move)"
                    )));
                }
                to.to_owned()
            } else {
                check_name(to).map_err(invalid)?;
                Path::new(&folder).join(to).display().to_string()
            };
            Ok(checked(Some(target), false))
        }
        ChangeKind::Move | ChangeKind::Copy => {
            let Some(to) = to else {
                return Err(invalid(format!(
                    "{path}: a move or a copy needs `to`, the folder it goes into"
                )));
            };
            if !Path::new(to).is_absolute() {
                return Err(invalid(format!("{to}: not an absolute path")));
            }
            Ok(checked(Some(to.to_owned()), false))
        }
        ChangeKind::Delete | ChangeKind::Create => {
            if let Some(to) = to {
                return Err(invalid(format!(
                    "{path}: a delete or a create has no `to` (got `{to}`)"
                )));
            }
            let folder = row.kind == ChangeKind::Create && trimmed.len() != path.len();
            Ok(checked(None, folder))
        }
    }
}

/// A plain file name: no folder part, nothing Windows refuses.
fn check_name(name: &str) -> Result<(), String> {
    let bad = name == "."
        || name == ".."
        || name.ends_with(['.', ' '])
        || name.chars().any(|c| c < ' ' || "\\/:*?\"<>|".contains(c));
    if bad {
        Err(format!("`{name}` is not a valid file name"))
    } else {
        Ok(())
    }
}

/// The jobs that run `rows` in order. Neighbouring rows that fit one job
/// share it: renames and creates become one `steps` job, moves or copies
/// into one folder one move or copy, deletes one delete to the Recycle
/// Bin.
pub(crate) fn job_requests(rows: &[Checked]) -> Vec<JobRequest> {
    let mut requests: Vec<JobRequest> = Vec::new();
    for row in rows {
        let step = match row.kind {
            ChangeKind::Rename => Some(JobStep::Rename {
                from: row.path.clone(),
                to: row.to.clone().unwrap_or_default(),
            }),
            ChangeKind::Create if row.folder => Some(JobStep::CreateFolder {
                path: row.path.clone(),
            }),
            ChangeKind::Create => Some(JobStep::CreateFile {
                path: row.path.clone(),
            }),
            ChangeKind::Move | ChangeKind::Copy | ChangeKind::Delete => None,
        };
        let last = requests.last_mut();
        match (step, row.kind, last) {
            (
                Some(step),
                _,
                Some(JobRequest {
                    kind: JobKind::Steps { steps },
                    ..
                }),
            ) => steps.push(step),
            (Some(step), ..) => requests.push(JobRequest {
                kind: JobKind::Steps { steps: vec![step] },
                sources: Vec::new(),
                destination: None,
                options: JobOptions::default(),
            }),
            (None, ChangeKind::Delete, Some(last))
                if matches!(last.kind, JobKind::Delete { permanent: false }) =>
            {
                last.sources.push(row.path.clone());
            }
            (None, kind, Some(last))
                if matches!(
                    (&last.kind, kind),
                    (JobKind::Move, ChangeKind::Move) | (JobKind::Copy, ChangeKind::Copy)
                ) && last.destination == row.to =>
            {
                last.sources.push(row.path.clone());
            }
            (None, kind, _) => requests.push(JobRequest {
                kind: match kind {
                    ChangeKind::Move => JobKind::Move,
                    ChangeKind::Copy => JobKind::Copy,
                    _ => JobKind::Delete { permanent: false },
                },
                sources: vec![row.path.clone()],
                destination: row.to.clone(),
                options: JobOptions::default(),
            }),
        }
    }
    requests
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(path: &str, kind: ChangeKind, to: Option<&str>) -> PreviewRow {
        PreviewRow {
            path: path.to_owned(),
            kind,
            to: to.map(str::to_owned),
        }
    }

    #[test]
    fn rows_are_checked_and_their_targets_made_whole() {
        let checked = check_rows(&[
            row(
                r"C:\photos\IMG_1.jpg",
                ChangeKind::Rename,
                Some("beach.jpg"),
            ),
            row(
                r"C:\photos\IMG_2.jpg",
                ChangeKind::Rename,
                Some(r"C:\Photos\sea.jpg"),
            ),
            row(r"C:\photos\Beach\", ChangeKind::Create, None),
            row(r"C:\photos\notes.txt", ChangeKind::Create, None),
            row(
                r"C:\photos\IMG_3.jpg",
                ChangeKind::Move,
                Some(r"D:\archive"),
            ),
        ])
        .unwrap();
        assert_eq!(checked[0].to.as_deref(), Some(r"C:\photos\beach.jpg"));
        assert_eq!(checked[1].to.as_deref(), Some(r"C:\Photos\sea.jpg"));
        assert_eq!(
            (checked[2].path.as_str(), checked[2].folder),
            (r"C:\photos\Beach", true)
        );
        assert!(!checked[3].folder);
        assert_eq!(checked[4].to.as_deref(), Some(r"D:\archive"));

        for (bad, code, words) in [
            (
                row("photos\\a.jpg", ChangeKind::Delete, None),
                ErrorCode::InvalidPath,
                "absolute",
            ),
            (
                row(r"C:\", ChangeKind::Delete, None),
                ErrorCode::InvalidPath,
                "volume root",
            ),
            (
                row(r"C:\a.jpg", ChangeKind::Rename, None),
                ErrorCode::InvalidPath,
                "needs `to`",
            ),
            (
                row(r"C:\a.jpg", ChangeKind::Rename, Some("b?.jpg")),
                ErrorCode::InvalidPath,
                "not a valid file name",
            ),
            (
                row(r"C:\x\a.jpg", ChangeKind::Rename, Some(r"C:\y\a.jpg")),
                ErrorCode::InvalidPath,
                "use move",
            ),
            (
                row(r"C:\a.jpg", ChangeKind::Copy, None),
                ErrorCode::InvalidPath,
                "needs `to`",
            ),
            (
                row(r"C:\a.jpg", ChangeKind::Move, Some("elsewhere")),
                ErrorCode::InvalidPath,
                "absolute",
            ),
            (
                row(r"C:\a.jpg", ChangeKind::Delete, Some(r"C:\b")),
                ErrorCode::InvalidPath,
                "no `to`",
            ),
        ] {
            let (got, message) = check_rows(std::slice::from_ref(&bad)).unwrap_err();
            assert_eq!(got, code, "{bad:?}: {message}");
            assert!(message.contains(words), "{bad:?}: {message}");
        }
        assert_eq!(check_rows(&[]).unwrap_err().0, ErrorCode::ProtocolError);
    }

    #[test]
    fn neighbouring_rows_share_a_job_and_the_order_stays() {
        let rows = check_rows(&[
            row(r"C:\p\Beach\", ChangeKind::Create, None),
            row(r"C:\p\1.jpg", ChangeKind::Rename, Some("a.jpg")),
            row(r"C:\p\a.jpg", ChangeKind::Move, Some(r"C:\p\Beach")),
            row(r"C:\p\2.jpg", ChangeKind::Move, Some(r"C:\p\Beach")),
            row(r"C:\p\3.jpg", ChangeKind::Move, Some(r"C:\p\Other")),
            row(r"C:\p\4.jpg", ChangeKind::Copy, Some(r"C:\p\Other")),
            row(r"C:\p\old.txt", ChangeKind::Delete, None),
            row(r"C:\p\older.txt", ChangeKind::Delete, None),
            row(r"C:\p\new.txt", ChangeKind::Create, None),
        ])
        .unwrap();
        let requests = job_requests(&rows);
        let shapes: Vec<(String, Vec<&str>, Option<&str>)> = requests
            .iter()
            .map(|request| {
                let kind = match &request.kind {
                    JobKind::Steps { steps } => format!("steps {}", steps.len()),
                    other => format!("{other:?}"),
                };
                (
                    kind,
                    request.sources.iter().map(String::as_str).collect(),
                    request.destination.as_deref(),
                )
            })
            .collect();
        assert_eq!(
            shapes,
            [
                ("steps 2".to_owned(), vec![], None),
                (
                    "Move".to_owned(),
                    vec![r"C:\p\a.jpg", r"C:\p\2.jpg"],
                    Some(r"C:\p\Beach")
                ),
                ("Move".to_owned(), vec![r"C:\p\3.jpg"], Some(r"C:\p\Other")),
                ("Copy".to_owned(), vec![r"C:\p\4.jpg"], Some(r"C:\p\Other")),
                (
                    "Delete { permanent: false }".to_owned(),
                    vec![r"C:\p\old.txt", r"C:\p\older.txt"],
                    None
                ),
                ("steps 1".to_owned(), vec![], None),
            ]
        );
        let JobKind::Steps { steps } = &requests[0].kind else {
            panic!("steps first")
        };
        assert_eq!(
            steps,
            &[
                JobStep::CreateFolder {
                    path: r"C:\p\Beach".to_owned()
                },
                JobStep::Rename {
                    from: r"C:\p\1.jpg".to_owned(),
                    to: r"C:\p\a.jpg".to_owned()
                }
            ]
        );
    }
}
