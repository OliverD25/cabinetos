//! File search: the indexer when it answers, a bounded walk when it does not
//! (ADR 0002: the core works unchanged without the indexer).
//!
//! The core asks the indexer over its pipe with a 200 ms limit, one request
//! per connection (a named pipe connects in microseconds). When the indexer
//! does not answer, the core waits a while before asking again (1 s, then
//! doubling to 30 s), and says so once in its log. Meanwhile it walks one
//! folder tree itself with the NT enumeration, for about 2 s and 200,000
//! entries, and ranks the hits the way the indexer does. The limits are
//! checked before each folder; a folder is read whole, so one slow folder
//! (a network share that stopped answering) can hold the walk longer.
//! `complete` is false in the reply when either limit ended the walk.

use std::cmp::Ordering;
use std::collections::{BinaryHeap, VecDeque};
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

/// The limits the core uses: a walk never holds up the user for long. The
/// time is what ends a walk; the entry count is only a ceiling on its memory.
/// Listing 100,000 names takes about 60 ms, so a limit of 20,000 entries
/// (the first one) ended the walk of a large folder long before the time did,
/// and the names after them were never found.
pub(crate) const WALK_LIMITS: WalkLimits = WalkLimits {
    time: Duration::from_secs(2),
    entries: 200_000,
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
        let envelope = Envelope::traced(id.clone(), cabinetos_diag::current_trace(), request);
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

/// The best `limit` hits of a walk, kept while it walks. A hit that ranks
/// below all of them is dropped at once, so a query that matches every name
/// holds `limit` hits, not one per entry (the walk once kept up to 200,000
/// and cut after sorting them). Hits are ordered as the index orders them:
/// by rank, then by path. A walk meets each path once, so that order is total
/// and the kept hits are the first `limit` of a full sort.
struct BestHits {
    limit: usize,
    /// The worst kept hit is on top: the one a better hit replaces.
    kept: BinaryHeap<Kept>,
}

struct Kept {
    rank: Rank,
    hit: FileHit,
}

impl Kept {
    fn order(&self) -> (Rank, &str) {
        (self.rank, &self.hit.path)
    }
}

impl PartialEq for Kept {
    fn eq(&self, other: &Self) -> bool {
        self.order() == other.order()
    }
}

impl Eq for Kept {}

impl PartialOrd for Kept {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Kept {
    fn cmp(&self, other: &Self) -> Ordering {
        self.order().cmp(&other.order())
    }
}

impl BestHits {
    fn new(limit: usize) -> Self {
        Self {
            limit,
            kept: BinaryHeap::new(),
        }
    }

    /// Keeps the hit `make_hit` builds when it is among the best so far.
    /// `path` is the hit's path, so a hit that is not kept costs no copy.
    fn offer(&mut self, rank: Rank, path: &str, make_hit: impl FnOnce() -> FileHit) {
        if self.limit == 0 {
            return;
        }
        if self.kept.len() < self.limit {
            self.kept.push(Kept {
                rank,
                hit: make_hit(),
            });
        } else if let Some(mut worst) = self.kept.peek_mut()
            && (rank, path) < worst.order()
        {
            *worst = Kept {
                rank,
                hit: make_hit(),
            };
        }
    }

    /// The kept hits, best first.
    fn into_hits(self) -> Vec<FileHit> {
        self.kept
            .into_sorted_vec()
            .into_iter()
            .map(|kept| kept.hit)
            .collect()
    }
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
    let mut best = BestHits::new(limit);
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
                best.offer(rank, &child, || FileHit {
                    path: child.clone(),
                    kind: if directory {
                        HitKind::Directory
                    } else {
                        HitKind::File
                    },
                    frn: (entry.flags & ListingEntry::FLAG_ID_IS_NAME_HASH == 0)
                        .then_some(entry.id),
                });
            }
            if entry.kind == EntryKind::Directory {
                folders.push_back(child);
            }
        }
    }
    Ok(WalkOutcome {
        hits: best.into_hits(),
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
    fn a_walk_finds_names_beyond_ascii_without_case_or_normal_form() {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk-names")
            .tempdir_in(root)
            .unwrap();
        for relative in [
            "Звіт 2026.txt",
            r"Ґанок\Звіт 2026.txt",
            "caf\u{e9}.txt",
            "cafe\u{301}.txt",
            r"中文文件夹\日本語のファイル.txt",
            r"📁 photos\𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
            "مستند.txt",
        ] {
            let path = dir.path().join(relative);
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, "x").unwrap();
        }
        let root = dir.path().display().to_string();
        let found = |query: &str| {
            let outcome = walk(&root, &Matcher::new(query).unwrap(), 50, WALK_LIMITS).unwrap();
            assert!(outcome.complete);
            let mut found: Vec<String> = outcome
                .hits
                .iter()
                .map(|hit| hit.path[root.len() + 1..].to_owned())
                .collect();
            found.sort();
            found
        };
        assert_eq!(found("звіт"), ["Звіт 2026.txt", r"Ґанок\Звіт 2026.txt"]);
        assert_eq!(found("ҐАНОК"), ["Ґанок"]);
        assert_eq!(found("ファイル"), [r"中文文件夹\日本語のファイル.txt"]);
        assert_eq!(found("𝔫𝔦𝔠"), [r"📁 photos\𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt"]);
        assert_eq!(found("مستند"), ["مستند.txt"]);
        let mut both = ["caf\u{e9}.txt".to_owned(), "cafe\u{301}.txt".to_owned()];
        both.sort();
        assert_eq!(found("Caf\u{e9}"), both);
        assert_eq!(found("cafe\u{301}"), both);
    }

    #[test]
    fn a_walk_reaches_files_deeper_than_260_characters() {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk-long")
            .tempdir_in(root)
            .unwrap();
        let mut deep = dir.path().to_path_buf();
        while deep.as_os_str().len() < 300 {
            deep.push("segment-of-a-long-path-0123456789");
        }
        std::fs::create_dir_all(format!(r"\\?\{}", deep.display())).unwrap();
        std::fs::write(format!(r"\\?\{}\far away.txt", deep.display()), "x").unwrap();
        let root = dir.path().display().to_string();
        let outcome = walk(&root, &Matcher::new("far away").unwrap(), 10, WALK_LIMITS).unwrap();
        assert!(outcome.complete);
        // The path as the user knows it: no `\\?\` in an answer.
        let wanted = deep.join("far away.txt").display().to_string();
        assert!(wanted.len() > 300);
        assert_eq!(
            outcome
                .hits
                .iter()
                .map(|hit| hit.path.as_str())
                .collect::<Vec<_>>(),
            [wanted.as_str()]
        );
    }

    #[test]
    fn a_walk_never_follows_a_link_into_a_loop() {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk-loop")
            .tempdir_in(root)
            .unwrap();
        let junction = |link: &Path, target: &Path| {
            let output = std::process::Command::new("cmd")
                .args(["/c", "mklink", "/J"])
                .arg(link)
                .arg(target)
                .output()
                .unwrap();
            assert!(output.status.success(), "{output:?}");
        };
        let lp = dir.path().join("loop");
        std::fs::create_dir_all(lp.join("inner")).unwrap();
        std::fs::write(lp.join("inner").join("needle.txt"), "x").unwrap();
        junction(&lp.join("inner").join("back to loop"), &lp);
        junction(&dir.path().join("needle junction"), &lp);
        if let Err(error) = std::os::windows::fs::symlink_dir(&lp, lp.join("needle symlink")) {
            println!("no symbolic link here: {error}");
        }
        let root = dir.path().display().to_string();
        let outcome = walk(&root, &Matcher::new("needle").unwrap(), 50, WALK_LIMITS).unwrap();
        assert!(outcome.complete);
        assert!(outcome.visited < 20, "{outcome:?}");
        let mut found: Vec<&str> = outcome
            .hits
            .iter()
            .map(|hit| &hit.path[root.len() + 1..])
            .collect();
        found.sort_unstable();
        // The file once, by its real path; the links as entries of their
        // own, never entered.
        let mut wanted = vec![r"loop\inner\needle.txt", "needle junction"];
        if lp.join("needle symlink").exists() {
            wanted.push(r"loop\needle symlink");
        }
        wanted.sort_unstable();
        assert_eq!(found, wanted);
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
    fn a_walk_finds_a_name_past_the_first_20000_entries() {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk-large")
            .tempdir_in(root)
            .unwrap();
        for n in 0..25_000 {
            std::fs::File::create(dir.path().join(format!("file-{n:05}.bin"))).unwrap();
        }
        // The listing is sorted by name, so this one is entry 25,001.
        std::fs::File::create(dir.path().join("zz-needle.txt")).unwrap();
        let root = dir.path().display().to_string();
        let needle = Matcher::new("needle").unwrap();

        // What the first limit did: stopped before the name, and said so.
        let old = WalkLimits {
            time: Duration::from_secs(60),
            entries: 20_000,
        };
        let outcome = walk(&root, &needle, 10, old).unwrap();
        assert!(outcome.hits.is_empty());
        assert!(!outcome.complete);

        let outcome = walk(&root, &needle, 10, WALK_LIMITS).unwrap();
        assert_eq!(names(&outcome), ["zz-needle.txt"]);
        assert!(outcome.complete);
        assert_eq!(outcome.visited, 25_001);
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

    /// The 3,000 names of the bounded-collection tests: three lengths of
    /// number, names that start with `f` and names that only contain it, in
    /// ten folders, so rank ties are broken by path across folders.
    fn names_for_ranking() -> Vec<String> {
        (0..3000_u32)
            .map(|n| {
                let name = match n % 3 {
                    0 => format!("f{n}.dat"),
                    1 => format!("fx{n}.dat"),
                    _ => format!("of{n}.dat"),
                };
                format!(r"d{}\{name}", n % 10)
            })
            .collect()
    }

    /// What a walk must answer, worked out the plain way: rank every path,
    /// sort them all, cut to `limit`.
    fn sorted_in_full(
        root: &str,
        relative: &[String],
        matcher: &Matcher,
        limit: usize,
    ) -> Vec<String> {
        let mut all: Vec<(Rank, String)> = relative
            .iter()
            .filter_map(|relative| {
                let name = relative.rsplit('\\').next().unwrap();
                matcher
                    .rank(name)
                    .map(|rank| (rank, format!(r"{root}\{relative}")))
            })
            .collect();
        all.sort();
        all.truncate(limit);
        all.into_iter().map(|(_, path)| path).collect()
    }

    #[test]
    fn a_walk_keeps_the_same_best_hits_as_sorting_every_match() {
        let root = std::env::temp_dir().join("cabinetos-index-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("walk-best")
            .tempdir_in(root)
            .unwrap();
        let relative = names_for_ranking();
        for folder in 0..10 {
            std::fs::create_dir(dir.path().join(format!("d{folder}"))).unwrap();
        }
        for path in &relative {
            std::fs::File::create(dir.path().join(path)).unwrap();
        }
        let root = dir.path().display().to_string();
        let paths = |outcome: &WalkOutcome| -> Vec<String> {
            outcome.hits.iter().map(|hit| hit.path.clone()).collect()
        };
        // "f" is in every name: starting with it ranks better than only
        // containing it, shorter names rank better, and the path decides
        // between equal ranks.
        let matcher = Matcher::new("f").unwrap();
        let everything = relative.len();
        assert_eq!(everything, 3000);

        // No bound that matters: the limit is above the match count.
        let unbounded = walk(&root, &matcher, everything, WALK_LIMITS).unwrap();
        assert!(unbounded.complete);
        assert_eq!(unbounded.hits.len(), everything);
        assert_eq!(
            paths(&unbounded),
            sorted_in_full(&root, &relative, &matcher, everything)
        );

        // The bound: 50, as Quick Open asks. The same first 50, in order.
        let bounded = walk(&root, &matcher, 50, WALK_LIMITS).unwrap();
        assert!(bounded.complete);
        assert_eq!(bounded.visited, unbounded.visited);
        assert_eq!(bounded.hits.len(), 50);
        assert_eq!(paths(&bounded), paths(&unbounded)[..50]);
        assert_eq!(
            paths(&bounded),
            sorted_in_full(&root, &relative, &matcher, 50)
        );
        assert!(
            bounded.hits.iter().all(|hit| hit.kind == HitKind::File),
            "the hits keep their kind"
        );

        for limit in [0, 1, 7] {
            let outcome = walk(&root, &matcher, limit, WALK_LIMITS).unwrap();
            assert_eq!(paths(&outcome), paths(&unbounded)[..limit], "limit {limit}");
        }
    }

    #[test]
    fn the_best_hits_never_hold_more_than_the_limit() {
        // Hits in every order a walk could meet them: best first, worst
        // first, and shuffled; ranks tie often, so paths decide.
        let mut candidates: Vec<(Rank, String)> = (0..3000_usize)
            .map(|n| {
                let rank = Rank::new(n % 7 == 0, 1 + n % 11);
                (rank, format!(r"C:\root\d{}\f{}", n % 13, n))
            })
            .collect();
        let mut shuffled = candidates.clone();
        let mut state = 0x9E37_79B9_7F4A_7C15_u64;
        for i in (1..shuffled.len()).rev() {
            state = state
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            shuffled.swap(i, usize::try_from(state >> 33).unwrap() % (i + 1));
        }
        candidates.sort();
        let mut worst_first = candidates.clone();
        worst_first.reverse();

        for limit in [0, 1, 50, 2999, 3000, 5000] {
            let wanted: Vec<&str> = candidates
                .iter()
                .take(limit)
                .map(|(_, path)| path.as_str())
                .collect();
            for (index, order) in [&candidates, &worst_first, &shuffled]
                .into_iter()
                .enumerate()
            {
                let mut best = BestHits::new(limit);
                let mut made = 0;
                for (rank, path) in order {
                    best.offer(*rank, path, || {
                        made += 1;
                        FileHit {
                            path: path.clone(),
                            kind: HitKind::File,
                            frn: None,
                        }
                    });
                    assert!(best.kept.len() <= limit, "limit {limit}");
                }
                if index == 0 {
                    // Best first: after the first `limit`, nothing ranks
                    // better, so no later hit is even built.
                    assert_eq!(made, limit.min(candidates.len()), "limit {limit}");
                }
                let hits = best.into_hits();
                assert_eq!(
                    hits.iter().map(|hit| hit.path.as_str()).collect::<Vec<_>>(),
                    wanted,
                    "limit {limit}"
                );
            }
        }
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
