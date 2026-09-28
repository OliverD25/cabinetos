//! File search: the indexer when it answers, a bounded walk when it does not
//! (ADR 0002: the core works unchanged without the indexer).
//!
//! The core asks the indexer over its pipe with a 200 ms limit, one request
//! per connection (a named pipe connects in microseconds). When the indexer
//! does not answer, the core waits a while before asking again (1 s, then
//! doubling to 30 s), and says so once in its log. Meanwhile it walks one
//! folder tree itself with the NT enumeration, for about 2 s and 20,000
//! entries, and ranks the hits the way the indexer does. The limits are
//! checked before each folder; a folder is read whole, so one slow folder
//! (a network share that stopped answering) can hold the walk longer.

use std::collections::VecDeque;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use cabinetos_fs::ListOptions;
use cabinetos_index::{Matcher, Rank};
use cabinetos_ipc::{PipeName, exchange};
use cabinetos_protocol::shm::{EntryKind, ListingEntry};
use cabinetos_protocol::{
    Envelope, ErrorCode, FileHit, HitKind, INDEXER_PIPE_NAME, IndexerRequest, IndexerResponse,
    RequestId, Response, SearchSource,
};

use crate::listing::{self, Failure};

/// Environment variable that points the core at another indexer pipe. For
/// tests; the indexer's pipe name is fixed otherwise.
pub const INDEXER_PIPE_ENV: &str = "CABINETOS_INDEXER_PIPE";

/// How long the core waits for the indexer.
const INDEXER_TIMEOUT: Duration = Duration::from_millis(200);

/// The wait before asking an indexer that did not answer again: doubling
/// from the first to the last.
const RETRY_FIRST: Duration = Duration::from_secs(1);
const RETRY_LAST: Duration = Duration::from_secs(30);

/// The most hits one search returns.
const MAX_LIMIT: u32 = 1000;

/// Limits of the walk without an indexer.
#[derive(Clone, Copy, Debug)]
pub(crate) struct WalkLimits {
    /// Stop after this long.
    pub(crate) time: Duration,
    /// Stop after looking at this many entries.
    pub(crate) entries: usize,
}

/// The limits the core uses: a walk never holds up the user for long.
pub(crate) const WALK_LIMITS: WalkLimits = WalkLimits {
    time: Duration::from_secs(2),
    entries: 20_000,
};

/// The core's way to the indexer.
pub(crate) struct IndexerLink {
    pipe: PipeName,
    state: Mutex<LinkState>,
}

#[derive(Default)]
struct LinkState {
    /// When to ask again after the indexer did not answer.
    retry_at: Option<Instant>,
    failures: u32,
    /// Whether the log already says the indexer is missing.
    announced: bool,
}

impl IndexerLink {
    /// The link to the indexer's fixed pipe, or to the one
    /// `CABINETOS_INDEXER_PIPE` names.
    pub(crate) fn from_env() -> Self {
        let name = std::env::var(INDEXER_PIPE_ENV)
            .ok()
            .filter(|name| !name.trim().is_empty())
            .unwrap_or_else(|| INDEXER_PIPE_NAME.to_owned());
        Self {
            pipe: PipeName::from_full(name),
            state: Mutex::new(LinkState::default()),
        }
    }

    fn state(&self) -> MutexGuard<'_, LinkState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Asks the indexer; `None` when it does not answer in time. After a
    /// failure it is not asked again for a while, unless `even_if_waiting`.
    pub(crate) async fn ask(
        &self,
        id: &RequestId,
        request: IndexerRequest,
        even_if_waiting: bool,
    ) -> Option<IndexerResponse> {
        if !even_if_waiting
            && self
                .state()
                .retry_at
                .is_some_and(|retry_at| Instant::now() < retry_at)
        {
            return None;
        }
        let envelope = Envelope::new(id.clone(), request);
        match exchange::<_, Envelope<IndexerResponse>>(&self.pipe, &envelope, INDEXER_TIMEOUT).await
        {
            Ok(reply) => {
                let mut state = self.state();
                if state.announced {
                    tracing::info!(pipe = %self.pipe, "the indexer answers again; searching its index");
                }
                *state = LinkState::default();
                Some(reply.body)
            }
            Err(error) => {
                let mut state = self.state();
                state.failures = state.failures.saturating_add(1);
                let wait = RETRY_FIRST
                    .saturating_mul(1 << (state.failures - 1).min(5))
                    .min(RETRY_LAST);
                state.retry_at = Some(Instant::now() + wait);
                if !state.announced {
                    state.announced = true;
                    tracing::info!(
                        pipe = %self.pipe,
                        %error,
                        "no indexer answers; searching by walking folders until one does"
                    );
                }
                None
            }
        }
    }
}

/// The reply to `search`. `default_root` is where a walk starts when the
/// request names no root.
pub(crate) async fn search(
    link: &IndexerLink,
    id: &RequestId,
    query: String,
    limit: u32,
    root: Option<String>,
    default_root: String,
) -> Response {
    let started = Instant::now();
    let Some(matcher) = Matcher::new(&query) else {
        return Response::Error {
            code: ErrorCode::ProtocolError,
            message: "the query is empty".to_owned(),
        };
    };
    let limit = limit.min(MAX_LIMIT);
    let asked = IndexerRequest::Search {
        query,
        limit,
        root: root.clone(),
    };
    match link.ask(id, asked, false).await {
        Some(IndexerResponse::FileSearchResults { hits, complete, .. }) => {
            return Response::FileSearchResults {
                hits,
                source: SearchSource::Index,
                took_us: micros(started),
                complete,
            };
        }
        Some(IndexerResponse::Error { code, message }) => {
            tracing::debug!(?code, %message, "the indexer cannot answer this search; walking instead");
        }
        _ => {}
    }
    let root = root.unwrap_or(default_root);
    let walked =
        tokio::task::spawn_blocking(move || walk(&root, &matcher, limit as usize, WALK_LIMITS))
            .await;
    match walked {
        Ok(Ok(outcome)) => {
            tracing::info!(
                visited = outcome.visited,
                hits = outcome.hits.len(),
                complete = outcome.complete,
                "searched by walking folders"
            );
            Response::FileSearchResults {
                hits: outcome.hits,
                source: SearchSource::Walk,
                took_us: micros(started),
                complete: outcome.complete,
            }
        }
        Ok(Err((code, message))) => Response::Error { code, message },
        Err(error) if error.is_panic() => std::panic::resume_unwind(error.into_panic()),
        Err(_) => Response::Error {
            code: ErrorCode::Internal,
            message: "the search was cancelled".to_owned(),
        },
    }
}

/// The reply to `index_status`: the indexer's volumes, or `available: false`.
pub(crate) async fn index_status(link: &IndexerLink, id: &RequestId) -> Response {
    match link.ask(id, IndexerRequest::IndexStatus, true).await {
        Some(IndexerResponse::IndexStatus { volumes }) => Response::IndexStatus {
            available: true,
            volumes,
        },
        _ => Response::IndexStatus {
            available: false,
            volumes: Vec::new(),
        },
    }
}

fn micros(started: Instant) -> u64 {
    u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX)
}

/// What a walk found.
#[derive(Debug)]
pub(crate) struct WalkOutcome {
    /// The best hits, best first.
    pub(crate) hits: Vec<FileHit>,
    /// False when a limit stopped the walk with folders left.
    pub(crate) complete: bool,
    /// The entries looked at.
    pub(crate) visited: usize,
}

/// Walks the tree under `root` breadth first (links are not followed) and
/// keeps the names that contain the query. Blocking.
pub(crate) fn walk(
    root: &str,
    matcher: &Matcher,
    limit: usize,
    limits: WalkLimits,
) -> Result<WalkOutcome, Failure> {
    if !std::path::Path::new(root).is_absolute() {
        return Err((
            ErrorCode::InvalidPath,
            format!("the search root `{root}` is not an absolute path"),
        ));
    }
    let started = Instant::now();
    let options = ListOptions {
        include_hidden: true,
        ..ListOptions::default()
    };
    let mut folders = VecDeque::from([root.trim_end_matches(['\\', '/']).to_owned()]);
    let mut found: Vec<(Rank, FileHit)> = Vec::new();
    let mut visited = 0usize;
    let mut complete = true;
    let mut first = true;
    'folders: while let Some(folder) = folders.pop_front() {
        if started.elapsed() >= limits.time || visited >= limits.entries {
            complete = false;
            break;
        }
        let path = if folder.ends_with(':') {
            format!("{folder}\\")
        } else {
            folder.clone()
        };
        let listing = match cabinetos_fs::list_directory(&path, &options) {
            Ok(listing) => listing,
            // The root must exist; a folder under it that cannot be read is
            // skipped, as Explorer's search does.
            Err(error) if first => return Err(listing::fs_failure(&error)),
            Err(_) => continue,
        };
        first = false;
        for entry in listing.entries() {
            if visited >= limits.entries {
                complete = false;
                break 'folders;
            }
            visited += 1;
            let name = listing.name_string(entry);
            let child = format!("{folder}\\{name}");
            let directory = entry.meta.attributes & 0x10 != 0;
            if let Some(rank) = matcher.rank(&name) {
                found.push((
                    rank,
                    FileHit {
                        path: child.clone(),
                        kind: if directory {
                            HitKind::Directory
                        } else {
                            HitKind::File
                        },
                        frn: (entry.flags & ListingEntry::FLAG_ID_IS_NAME_HASH == 0)
                            .then_some(entry.id),
                    },
                ));
            }
            if entry.kind == EntryKind::Directory {
                folders.push_back(child);
            }
        }
    }
    found.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.path.cmp(&b.1.path)));
    found.truncate(limit);
    Ok(WalkOutcome {
        hits: found.into_iter().map(|(_, hit)| hit).collect(),
        complete,
        visited,
    })
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use super::*;

    fn tree() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk")
            .tempdir_in(root)
            .unwrap();
        let make = |relative: &str| {
            let path = dir.path().join(relative);
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, "x").unwrap();
        };
        make("Report.pdf");
        make(r"docs\annual-report-2025.docx");
        make(r"docs\deep\er\report");
        make(r"docs\notes.txt");
        std::fs::create_dir(dir.path().join("reports")).unwrap();
        for n in 0..100 {
            make(&format!(r"bulk\file-{n:03}.bin"));
        }
        dir
    }

    fn names(outcome: &WalkOutcome) -> Vec<&str> {
        outcome
            .hits
            .iter()
            .map(|hit| hit.path.rsplit('\\').next().unwrap())
            .collect()
    }

    #[test]
    fn a_walk_finds_and_ranks_like_the_index() {
        let dir = tree();
        let root = dir.path().display().to_string();
        let outcome = walk(&root, &Matcher::new("REPORT").unwrap(), 10, WALK_LIMITS).unwrap();
        assert!(outcome.complete);
        assert_eq!(
            names(&outcome),
            ["report", "reports", "Report.pdf", "annual-report-2025.docx"]
        );
        let reports = outcome
            .hits
            .iter()
            .find(|hit| hit.path.ends_with("reports"))
            .unwrap();
        assert_eq!(reports.kind, HitKind::Directory);
        assert!(reports.frn.is_some(), "NTFS gives file IDs");
        assert!(outcome.hits[0].path.starts_with(&root));
        let one = walk(&root, &Matcher::new("report").unwrap(), 1, WALK_LIMITS).unwrap();
        assert_eq!(names(&one), ["report"]);
    }

    #[test]
    fn the_entry_limit_stops_the_walk() {
        let dir = tree();
        let root = dir.path().display().to_string();
        let limits = WalkLimits {
            time: Duration::from_secs(60),
            entries: 25,
        };
        let outcome = walk(&root, &Matcher::new("file-").unwrap(), 1000, limits).unwrap();
        assert!(!outcome.complete);
        assert_eq!(outcome.visited, 25);
        assert!(outcome.hits.len() < 25);
        let all = walk(&root, &Matcher::new("file-").unwrap(), 1000, WALK_LIMITS).unwrap();
        assert!(all.complete);
        assert_eq!(all.hits.len(), 100);
    }

    #[test]
    fn the_time_limit_stops_the_walk() {
        let dir = tree();
        let limits = WalkLimits {
            time: Duration::ZERO,
            entries: 20_000,
        };
        let outcome = walk(
            &dir.path().display().to_string(),
            &Matcher::new("x").unwrap(),
            10,
            limits,
        )
        .unwrap();
        assert!(!outcome.complete);
        assert_eq!(outcome.visited, 0);
    }

    #[test]
    fn a_missing_or_relative_root_is_refused() {
        let missing = Path::new(&std::env::temp_dir())
            .join("cabinetos-index-test")
            .join("no-such-folder");
        let (code, _) = walk(
            &missing.display().to_string(),
            &Matcher::new("x").unwrap(),
            10,
            WALK_LIMITS,
        )
        .unwrap_err();
        assert_eq!(code, ErrorCode::NotFound);
        let (code, _) = walk("relative", &Matcher::new("x").unwrap(), 10, WALK_LIMITS).unwrap_err();
        assert_eq!(code, ErrorCode::InvalidPath);
    }

    #[tokio::test]
    async fn a_missing_indexer_is_asked_again_only_after_a_pause() {
        let link = IndexerLink {
            pipe: PipeName::from_full(format!(
                r"\\.\pipe\cabinetos-indexer-missing-{:016x}",
                rand::random::<u64>()
            )),
            state: Mutex::new(LinkState::default()),
        };
        let id = RequestId::new();
        assert!(link.ask(&id, IndexerRequest::Ping, false).await.is_none());
        let first_wait = link.state().retry_at.unwrap();
        assert!(link.ask(&id, IndexerRequest::Ping, false).await.is_none());
        assert_eq!(
            link.state().retry_at,
            Some(first_wait),
            "not asked while waiting"
        );
        assert!(link.ask(&id, IndexerRequest::Ping, true).await.is_none());
        assert_eq!(link.state().failures, 2);
        assert!(link.state().announced);
    }
}
