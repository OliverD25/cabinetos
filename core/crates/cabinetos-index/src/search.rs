//! Search v1: case-insensitive substring over names, ranked.
//!
//! A search runs in three passes. First it decides, for each distinct name,
//! whether and how well it matches; there are fewer distinct names than
//! entries. Then it scans the entries' name IDs (a flat array) for matched
//! names, and applies the root filter. Last it picks the best, and builds
//! paths only for those. The first two passes are split across threads.
//!
//! The rank: a name that starts with the query comes before one that only
//! contains it, then shorter names come first (an exact match is the shortest
//! prefix match), then paths in order.

use std::num::NonZero;
use std::ops::Range;

use hashbrown::HashMap;
use memchr::memmem::Finder;

use crate::index::{Frn, VolumeIndex};
use crate::names;

/// Slices below this many items are scanned on one thread.
const PARALLEL_FROM: usize = 64 * 1024;

/// At most this many threads per pass.
const MAX_THREADS: usize = 16;

/// Candidates taken beyond `limit`, in case some have no path (NTFS's own
/// files, orphans).
const SLACK: usize = 64;

/// How well a name matches: smaller is better.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Rank(u32);

impl Rank {
    const NOT_PREFIX: u32 = 1 << 31;

    /// The rank of a name `len` bytes long (lowercase UTF-8) that starts with
    /// the query (`prefix`) or only contains it.
    #[must_use]
    pub fn new(prefix: bool, len: usize) -> Self {
        let len = u32::try_from(len)
            .unwrap_or(u32::MAX)
            .min(Self::NOT_PREFIX - 2)
            + 1;
        Self(if prefix { len } else { Self::NOT_PREFIX | len })
    }

    /// Whether the name starts with the query.
    #[must_use]
    pub fn is_prefix(self) -> bool {
        self.0 & Self::NOT_PREFIX == 0
    }
}

/// A query, ready to test names against.
#[derive(Clone, Debug)]
pub struct Matcher {
    lower: String,
    finder: Finder<'static>,
}

impl Matcher {
    /// The matcher for `query`, lowercased the way names are; `None` when the
    /// query is empty or only white space.
    #[must_use]
    pub fn new(query: &str) -> Option<Self> {
        let query = query.trim();
        if query.is_empty() {
            return None;
        }
        let lower = names::lowercase(query);
        let finder = Finder::new(lower.as_bytes()).into_owned();
        Some(Self { lower, finder })
    }

    /// The query, lowercased.
    #[must_use]
    pub fn query(&self) -> &str {
        &self.lower
    }

    /// The rank of a name already lowercased (UTF-8), or `None` when it does
    /// not contain the query.
    #[must_use]
    pub fn rank_lower(&self, lower: &[u8]) -> Option<Rank> {
        if lower.len() < self.lower.len() {
            return None;
        }
        if lower.starts_with(self.lower.as_bytes()) {
            return Some(Rank::new(true, lower.len()));
        }
        self.finder
            .find(lower)
            .map(|_| Rank::new(false, lower.len()))
    }

    /// The rank of a name as written, or `None` when it does not contain the
    /// query.
    #[must_use]
    pub fn rank(&self, name: &str) -> Option<Rank> {
        self.rank_lower(names::lowercase(name).as_bytes())
    }
}

/// One entry a search found.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct IndexHit {
    /// How well its name matched.
    pub rank: Rank,
    /// Its full path.
    pub path: String,
    /// Its file reference number.
    pub frn: Frn,
    /// Whether it is a directory.
    pub directory: bool,
}

/// Sorts hits best first: by rank, then by path.
pub fn sort_hits(hits: &mut [IndexHit]) {
    hits.sort_by(|a, b| a.rank.cmp(&b.rank).then_with(|| a.path.cmp(&b.path)));
}

/// Runs `work` over `0..len`, split across threads when `len` is large.
fn parallel<T: Send>(len: usize, work: impl Fn(Range<usize>) -> T + Sync) -> Vec<T> {
    let threads = std::thread::available_parallelism()
        .map_or(1, NonZero::get)
        .min(MAX_THREADS);
    if threads <= 1 || len < PARALLEL_FROM {
        return vec![work(0..len)];
    }
    let chunk = len.div_ceil(threads);
    let work = &work;
    std::thread::scope(|scope| {
        let running: Vec<_> = (0..threads)
            .map(|part| {
                let range = (part * chunk).min(len)..((part + 1) * chunk).min(len);
                scope.spawn(move || work(range))
            })
            .collect();
        running
            .into_iter()
            .map(|thread| {
                thread
                    .join()
                    .unwrap_or_else(|panic| std::panic::resume_unwind(panic))
            })
            .collect()
    })
}

impl VolumeIndex {
    /// The best `limit` entries whose name contains the query, best first.
    /// With `root`, only entries under that directory (at any depth).
    #[must_use]
    pub fn search(&self, matcher: &Matcher, root: Option<Frn>, limit: usize) -> Vec<IndexHit> {
        if limit == 0 || self.is_empty() {
            return Vec::new();
        }
        // Pass 1: the rank of each distinct name; 0 means no match.
        let name_ranks: Vec<u32> = parallel(self.names.len(), |range| {
            range
                .map(|id| {
                    let id = u32::try_from(id).expect("name IDs are u32");
                    matcher
                        .rank_lower(self.names.lower(id))
                        .map_or(0, |rank| rank.0)
                })
                .collect::<Vec<u32>>()
        })
        .concat();

        // Pass 2: the entries with a matching name, under the root.
        let mut candidates: Vec<(u32, u32)> = parallel(self.slot_count(), |range| {
            let mut memo = HashMap::new();
            let mut found = Vec::new();
            for slot in range {
                if !self.searchable(slot) {
                    continue;
                }
                let rank = name_ranks[self.name[slot] as usize];
                if rank == 0 {
                    continue;
                }
                if let Some(root) = root
                    && !self.is_under(slot, root, &mut memo)
                {
                    continue;
                }
                found.push((rank, u32::try_from(slot).expect("slots are u32")));
            }
            found
        })
        .concat();

        // Pass 3: the best ones, with their paths.
        let order = |a: &(u32, u32), b: &(u32, u32)| {
            a.0.cmp(&b.0)
                .then_with(|| {
                    self.names
                        .lower(self.name[a.1 as usize])
                        .cmp(self.names.lower(self.name[b.1 as usize]))
                })
                .then_with(|| a.1.cmp(&b.1))
        };
        let wanted = limit.saturating_add(SLACK);
        if candidates.len() > wanted {
            candidates.select_nth_unstable_by(wanted - 1, order);
            candidates.truncate(wanted);
        }
        candidates.sort_unstable_by(order);
        let mut hits = Vec::with_capacity(limit.min(candidates.len()));
        for (rank, slot) in candidates {
            let slot = slot as usize;
            if let Some(path) = self.path_of_slot(slot) {
                hits.push(IndexHit {
                    rank: Rank(rank),
                    path,
                    frn: self.frn[slot],
                    directory: self.is_directory(slot),
                });
                if hits.len() == limit {
                    break;
                }
            }
        }
        sort_hits(&mut hits);
        hits
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::index::tests::{ROOT, sample, utf16};

    fn names_of(hits: &[IndexHit]) -> Vec<&str> {
        hits.iter()
            .map(|hit| hit.path.rsplit('\\').next().unwrap())
            .collect()
    }

    #[test]
    fn an_empty_query_matches_nothing() {
        assert!(Matcher::new("").is_none());
        assert!(Matcher::new("   ").is_none());
        assert_eq!(Matcher::new(" Ab ").unwrap().query(), "ab");
    }

    #[test]
    fn ranks_put_prefixes_then_short_names_first() {
        let matcher = Matcher::new("cat").unwrap();
        let exact = matcher.rank("CAT").unwrap();
        let prefix = matcher.rank("catalog.txt").unwrap();
        let inner = matcher.rank("bobcat").unwrap();
        let long_inner = matcher.rank("the-bobcat-list").unwrap();
        assert!(exact < prefix && prefix < inner && inner < long_inner);
        assert!(exact.is_prefix() && !inner.is_prefix());
        assert_eq!(matcher.rank("dog"), None);
        assert_eq!(matcher.rank("ca"), None);
    }

    #[test]
    fn finds_names_case_insensitively_with_paths() {
        let index = sample();
        let hits = index.search(&Matcher::new("CAT").unwrap(), None, 10);
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].path, r"C:\Users\me\Photos\cat.jpg");
        assert!(!hits[0].directory);
        assert_eq!(hits[0].frn, 0x0001_0000_0000_0104);

        let hits = index.search(&Matcher::new("o").unwrap(), None, 10);
        assert_eq!(names_of(&hits), ["Photos", "notes.txt"]);
        assert!(hits[0].directory);
        // NTFS's own files never show, even when their names match.
        assert!(
            index
                .search(&Matcher::new("mft").unwrap(), None, 10)
                .is_empty()
        );
    }

    #[test]
    fn the_root_filter_keeps_descendants_only() {
        let mut index = sample();
        index.upsert(0x0001_0000_0000_0700, ROOT, &utf16("cat.txt"), 0x20);
        let matcher = Matcher::new("cat").unwrap();
        assert_eq!(index.search(&matcher, None, 10).len(), 2);
        let under_photos = index.search(&matcher, Some(0x0001_0000_0000_0103), 10);
        assert_eq!(names_of(&under_photos), ["cat.jpg"]);
        let under_root = index.search(&matcher, Some(ROOT), 10);
        assert_eq!(under_root.len(), 2);
        let under_nothing = index.search(&matcher, Some(0x0001_0000_0000_0102), 10);
        assert!(under_nothing.is_empty());
    }

    #[test]
    fn the_limit_keeps_the_best() {
        let mut index = VolumeIndex::new('E');
        index.upsert(ROOT, ROOT, &utf16("."), 0x10);
        for n in 0..300 {
            index.upsert(
                0x0001_0000_0000_1000 + n,
                ROOT,
                &utf16(&format!("xx-report-{n:03}.pdf")),
                0x20,
            );
        }
        index.upsert(0x0001_0000_0000_2000, ROOT, &utf16("report.pdf"), 0x20);
        index.upsert(0x0001_0000_0000_2001, ROOT, &utf16("Report"), 0x10);
        let hits = index.search(&Matcher::new("report").unwrap(), None, 5);
        assert_eq!(
            names_of(&hits),
            [
                "Report",
                "report.pdf",
                "xx-report-000.pdf",
                "xx-report-001.pdf",
                "xx-report-002.pdf"
            ]
        );
        assert!(
            index
                .search(&Matcher::new("report").unwrap(), None, 0)
                .is_empty()
        );
    }

    #[test]
    fn large_indexes_give_the_same_answer_across_threads() {
        let mut index = VolumeIndex::new('F');
        index.upsert(ROOT, ROOT, &utf16("."), 0x10);
        let folders = 50u64;
        for folder in 0..folders {
            index.upsert(
                0x0001_0000_0001_0000 + folder,
                ROOT,
                &utf16(&format!("folder{folder}")),
                0x10,
            );
        }
        for n in 0..200_000u64 {
            let parent = 0x0001_0000_0001_0000 + n % folders;
            index.upsert(
                0x0001_0000_0010_0000 + n,
                parent,
                &utf16(&format!("file{n:06}.dat")),
                0x20,
            );
        }
        let matcher = Matcher::new("99").unwrap();
        let hits = index.search(&matcher, Some(0x0001_0000_0001_0007), 1000);
        // file0nn99n in folder7: n % 50 == 7 and the name contains "99".
        let expected = (0..200_000u64)
            .filter(|n| n % folders == 7 && format!("file{n:06}.dat").contains("99"))
            .count();
        assert_eq!(hits.len(), expected.min(1000));
        assert!(hits.iter().all(|hit| hit.path.starts_with(r"F:\folder7\")));
        assert!(hits.windows(2).all(|pair| pair[0].rank <= pair[1].rank));
    }
}
