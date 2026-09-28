//! The pipelined listing. The calling thread only asks the kernel for
//! records (`NtQueryDirectoryFile`, about 64 KiB per call). Meanwhile one
//! worker thread parses each filled buffer, builds its entries' sort keys,
//! sorts them, and merges the sorted runs as it goes. After the kernel's
//! last call, only the last buffer and one merge pass are left; the merge
//! writes the display order, with every name's place in the section, and
//! the entries themselves never move.
//!
//! One worker, not a pool: a buffer takes the worker about half the time
//! the kernel takes to fill the next one, and with one worker taking the
//! buffers in order, every entry's index in the worker's arrays is its
//! place in the enumeration. More workers measured slower: they left a
//! larger merge at the end.
//!
//! The order is exactly the serial path's: both use [`sort::compare`], and
//! tests compare their sections byte for byte. A directory that fits in the
//! first [`SERIAL_BUFFERS`] buffers takes the serial path whole.

use std::num::NonZero;
use std::os::windows::io::OwnedHandle;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering as Atomic};

use cabinetos_protocol::SortSpec;
use crossbeam_channel::{Receiver, Sender};

use crate::enumerate::{self, Buffer};
use crate::sort::{self, NameKeys, Packed, Ties};
use crate::{FsError, ListOptions, Listing, Placed};

/// A directory that fits in this many buffers is read and sorted on the
/// calling thread; a larger one gets the worker after that many. NTFS
/// fills about 64 KiB per call, so this is about 2,200 entries of typical
/// names. The worker costs about 0.2 ms, so a directory that ends a few
/// buffers later is slower pipelined (measured: +0.2 ms at 2,000 and 3,000
/// entries); from about 4,000 entries on it is faster (−20% at 5,000 and
/// 10,000). Switching later only moved that band up and took most of the
/// gain at 5,000 and 10,000 (after 8 buffers: +8% and −10%).
const SERIAL_BUFFERS: usize = 4;

/// Filled buffers that may wait for the worker.
const QUEUE: usize = 16;

/// The parts of [`Item::pos`]: the group in the top bit, the name's length
/// in UTF-16 units below it, the entry's index in the low 32 bits.
const GROUP_BIT: u64 = 1 << 63;
const NAME_LEN_SHIFT: u32 = 32;
const INDEX_MASK: u64 = u32::MAX as u64;

/// Reads and sorts the directory at `path`. It starts as the serial path
/// does (one buffer, parsed on this thread as it comes); when the directory
/// needs more than [`SERIAL_BUFFERS`] buffers and the machine has a second
/// core, a worker takes over the parsed entries and everything after them.
pub(crate) fn list(path: &str, options: &ListOptions) -> Result<Listing, FsError> {
    let directory = enumerate::open(path)?;
    let mut buffer = Buffer::new(options.buffer_size);
    let mut listing = Listing::default();
    // One core: a pipeline would only take turns with itself.
    let pipeline = has_two_cores();
    let mut calls = 0;
    loop {
        let len = buffer.fill(path, &directory, calls == 0)?;
        if len == 0 {
            sort::sort(&mut listing, options.sort);
            return Ok(listing);
        }
        enumerate::parse_records(
            path,
            buffer.bytes(len),
            options.include_hidden,
            &mut listing,
        )?;
        calls += 1;
        if pipeline && calls == SERIAL_BUFFERS {
            return pipelined(path, options, &directory, listing, buffer);
        }
    }
}

fn has_two_cores() -> bool {
    std::thread::available_parallelism().map_or(1, NonZero::get) >= 2
}

/// One filled buffer.
struct Work {
    buffer: Buffer,
    len: usize,
}

/// One entry in a sort run: the packed fields of [`sort::compare`]. `pos`
/// holds the group, the name's length and the entry's index (see
/// [`GROUP_BIT`]); its index part is the place in the enumeration.
#[derive(Clone, Copy, Debug)]
struct Item {
    prefix: u128,
    primary: u64,
    pos: u64,
}

impl Item {
    fn index(self) -> usize {
        self.index_u32() as usize
    }

    fn index_u32(self) -> u32 {
        (self.pos & INDEX_MASK) as u32
    }

    fn name_bytes(self) -> u32 {
        (((self.pos & !GROUP_BIT) >> NAME_LEN_SHIFT) as u32) * 2
    }
}

impl Packed for Item {
    fn group(&self) -> u8 {
        u8::from(self.pos & GROUP_BIT != 0)
    }

    fn primary(&self) -> u64 {
        self.primary
    }

    fn prefix(&self) -> u128 {
        self.prefix
    }

    fn position(&self) -> u64 {
        self.pos & INDEX_MASK
    }
}

/// The worker's entries and keys: the ties of its items.
struct Arena<'a> {
    listing: &'a Listing,
    keys: &'a NameKeys,
}

impl Ties<Item> for Arena<'_> {
    fn key(&self, item: &Item) -> &[u8] {
        self.keys.key(item.index())
    }

    fn name(&self, item: &Item) -> &[u16] {
        self.listing.name(&self.listing.entries[item.index()])
    }
}

/// The pipeline: this thread feeds the kernel's buffers to the worker,
/// which hands back the finished listing.
fn pipelined(
    path: &str,
    options: &ListOptions,
    directory: &OwnedHandle,
    parsed: Listing,
    buffer: Buffer,
) -> Result<Listing, FsError> {
    let (work_tx, work_rx) = crossbeam_channel::bounded::<Work>(QUEUE);
    let (free_tx, free_rx) = crossbeam_channel::unbounded::<Buffer>();
    // The buffer that read the first entries reads on.
    let _ = free_tx.send(buffer);
    let failed = AtomicBool::new(false);
    std::thread::scope(|scope| {
        let worker = std::thread::Builder::new()
            .name("list-sort".to_owned())
            .spawn_scoped(scope, || {
                let listing = work_on(path, options, parsed, &work_rx, &free_tx);
                if listing.is_err() {
                    failed.store(true, Atomic::Relaxed);
                    // The feeder may wait for room in the queue.
                    drop(work_rx);
                }
                listing
            });
        let Ok(worker) = worker else {
            return Err(FsError::Io {
                path: path.to_owned(),
                source: std::io::Error::other("cannot start a thread to sort the listing"),
            });
        };
        let fed = feed(path, options, directory, &work_tx, &free_rx, &failed);
        drop(work_tx);
        let listing = worker
            .join()
            .unwrap_or_else(|panic| std::panic::resume_unwind(panic));
        fed?;
        listing
    })
}

/// The calling thread's part: the kernel's buffers, one after another, to
/// the worker. Stops early when the worker failed.
fn feed(
    path: &str,
    options: &ListOptions,
    directory: &OwnedHandle,
    work: &Sender<Work>,
    free: &Receiver<Buffer>,
    failed: &AtomicBool,
) -> Result<(), FsError> {
    loop {
        if failed.load(Atomic::Relaxed) {
            return Ok(());
        }
        // A buffer the worker is done with, else a new one. The queue is
        // bounded, so only a few buffers are ever made.
        let mut buffer = free
            .try_recv()
            .unwrap_or_else(|_| Buffer::new(options.buffer_size));
        let len = buffer.fill(path, directory, false)?;
        if len == 0 {
            return Ok(());
        }
        if work.send(Work { buffer, len }).is_err() {
            // The worker has stopped: it failed, and says why.
            return Ok(());
        }
    }
}

/// The worker: starts from the entries the calling thread parsed, then
/// parses each buffer onto the end of its arrays, and hands every new batch
/// of entries to a [`Sorter`].
fn work_on(
    path: &str,
    options: &ListOptions,
    parsed: Listing,
    work: &Receiver<Work>,
    free: &Sender<Buffer>,
) -> Result<Listing, FsError> {
    let mut listing = parsed;
    let mut sorter = Sorter::new(options.sort);
    let mut incoming = work.iter();
    let mut start = 0;
    loop {
        let end = listing.entries.len();
        sorter.add(path, &listing, start, end)?;
        start = end;
        let Some(Work { buffer, len }) = incoming.next() else {
            break;
        };
        let parsed = enumerate::parse_records(
            path,
            buffer.bytes(len),
            options.include_hidden,
            &mut listing,
        );
        // The kernel may fill it again while these entries are sorted.
        let _ = free.send(buffer);
        parsed?;
    }
    let order = sorter.finish(path, &listing)?;
    Ok(Listing {
        order: Some(order),
        display: OnceLock::new(),
        ..listing
    })
}

/// Sorts a listing's entries batch by batch, as they are parsed: builds the
/// batch's keys, sorts it, and merges the sorted runs as a binary counter
/// does (two runs of about the same size become one), so every entry moves
/// about log2(batches) times and few runs are left at the end. Then one
/// merge pass makes the display order.
struct Sorter {
    spec: SortSpec,
    keys: NameKeys,
    scratch: Vec<u16>,
    runs: Vec<Vec<Item>>,
}

impl Sorter {
    fn new(spec: SortSpec) -> Self {
        Self {
            spec,
            keys: NameKeys::new(),
            scratch: NameKeys::scratch(),
            runs: Vec::new(),
        }
    }

    /// Takes entries `start..end` of `listing`, the ones after those it
    /// already has.
    fn add(
        &mut self,
        path: &str,
        listing: &Listing,
        start: usize,
        end: usize,
    ) -> Result<(), FsError> {
        let descending = self.spec.descending;
        self.keys.extend(listing, start, end, &mut self.scratch);
        let arena = Arena {
            listing,
            keys: &self.keys,
        };
        let mut items = items(path, &arena, self.spec, start, end)?;
        items.sort_unstable_by(|a, b| sort::compare(&arena, descending, a, b));
        if !items.is_empty() {
            self.runs.push(items);
        }
        while let [.., below, top] = self.runs.as_slice()
            && top.len() >= below.len()
        {
            let top = self.runs.pop().expect("two runs");
            let below = self.runs.pop().expect("two runs");
            self.runs.push(merge(&arena, descending, &below, &top));
        }
        Ok(())
    }

    /// The display order of every entry taken.
    fn finish(self, path: &str, listing: &Listing) -> Result<Vec<Placed>, FsError> {
        let arena = Arena {
            listing,
            keys: &self.keys,
        };
        display_order(path, &arena, self.spec.descending, self.runs)
    }
}

/// The sort items of entries `start..end`, in their own order.
fn items(
    path: &str,
    arena: &Arena<'_>,
    spec: SortSpec,
    start: usize,
    end: usize,
) -> Result<Vec<Item>, FsError> {
    if u32::try_from(end).is_err() {
        return Err(FsError::Io {
            path: path.to_owned(),
            source: std::io::Error::other("the directory has more than 4 billion entries"),
        });
    }
    Ok(arena.listing.entries[start..end]
        .iter()
        .zip(start..)
        .map(|(entry, index)| {
            let group = if sort::group_rank(entry.meta.attributes) == 0 {
                0
            } else {
                GROUP_BIT
            };
            Item {
                prefix: arena.keys.prefix(index),
                primary: sort::primary_value(entry, spec.key),
                pos: group | (u64::from(entry.name_len) << NAME_LEN_SHIFT) | index as u64,
            }
        })
        .collect())
}

/// Two sorted runs as one.
fn merge(arena: &Arena<'_>, descending: bool, a: &[Item], b: &[Item]) -> Vec<Item> {
    let mut out = Vec::with_capacity(a.len() + b.len());
    merge_into(arena, descending, a, b, |item| out.push(item));
    out
}

/// Hands the items of two sorted runs to `take`, in order.
fn merge_into(
    arena: &Arena<'_>,
    descending: bool,
    a: &[Item],
    b: &[Item],
    mut take: impl FnMut(Item),
) {
    let (mut i, mut j) = (0, 0);
    while i < a.len() && j < b.len() {
        if sort::compare(arena, descending, &b[j], &a[i]).is_lt() {
            take(b[j]);
            j += 1;
        } else {
            take(a[i]);
            i += 1;
        }
    }
    a[i..].iter().chain(&b[j..]).for_each(|&item| take(item));
}

/// The display order from the runs left on the stack: the small runs merged
/// first, the last merge writing each entry's index and name offset.
fn display_order(
    path: &str,
    arena: &Arena<'_>,
    descending: bool,
    mut runs: Vec<Vec<Item>>,
) -> Result<Vec<Placed>, FsError> {
    // The stack holds the largest run at the bottom.
    while runs.len() > 2 {
        let top = runs.pop().expect("two runs");
        let below = runs.pop().expect("two runs");
        runs.push(merge(arena, descending, &below, &top));
    }
    let b = if runs.len() == 2 {
        runs.pop().expect("two runs")
    } else {
        Vec::new()
    };
    let a = runs.pop().unwrap_or_default();
    let names: u64 = a
        .iter()
        .chain(&b)
        .map(|item| u64::from(item.name_bytes()))
        .sum();
    if u32::try_from(names).is_err() {
        return Err(FsError::Io {
            path: path.to_owned(),
            source: std::io::Error::other("the listing's names are larger than 4 GiB"),
        });
    }
    // The last merge is the one step after the kernel's last call that
    // touches every entry, so it runs in parts on idle cores: each part
    // starts where the merge would be after the entries before it.
    let total = a.len() + b.len();
    let parts = if total >= PARALLEL_MERGE_FROM {
        MERGE_PARTS
    } else {
        1
    };
    let bounds: Vec<(usize, usize)> = (0..=parts)
        .map(|part| {
            let rank = total * part / parts;
            let from_a = split(arena, descending, &a, &b, rank);
            (from_a, rank - from_a)
        })
        .collect();
    let (a, b) = (&a[..], &b[..]);
    let pieces: Vec<Vec<Placed>> = std::thread::scope(|scope| {
        let later: Vec<_> = bounds[1..]
            .windows(2)
            .map(|pair| {
                let (from, to) = (pair[0], pair[1]);
                scope.spawn(move || place(arena, descending, a, b, from, to))
            })
            .collect();
        let first = place(arena, descending, a, b, bounds[0], bounds[1]);
        std::iter::once(first)
            .chain(later.into_iter().map(|piece| {
                piece
                    .join()
                    .unwrap_or_else(|panic| std::panic::resume_unwind(panic))
            }))
            .collect()
    });
    let mut order = Vec::with_capacity(total);
    for piece in pieces {
        order.extend_from_slice(&piece);
    }
    Ok(order)
}

/// Parts of the last merge, each on its own thread.
const MERGE_PARTS: usize = 4;

/// The last merge is split from this many entries on.
const PARALLEL_MERGE_FROM: usize = 16_384;

/// How many of the first `rank` items of the merge of `a` and `b` come
/// from `a` (the merge path's crossing, found by binary search).
fn split(arena: &Arena<'_>, descending: bool, a: &[Item], b: &[Item], rank: usize) -> usize {
    let (mut low, mut high) = (rank.saturating_sub(b.len()), rank.min(a.len()));
    while low < high {
        let from_a = low + (high - low) / 2;
        // `a[from_a]` is among the first `rank` when it comes before the
        // last item that `b` would give.
        if sort::compare(arena, descending, &a[from_a], &b[rank - from_a - 1]).is_lt() {
            low = from_a + 1;
        } else {
            high = from_a;
        }
    }
    low
}

/// The places of the merge of `a[from.0..to.0]` and `b[from.1..to.1]`,
/// whose names follow those of everything before `from`.
fn place(
    arena: &Arena<'_>,
    descending: bool,
    a: &[Item],
    b: &[Item],
    from: (usize, usize),
    to: (usize, usize),
) -> Vec<Placed> {
    // `display_order` checked that all the names fit in 4 GiB.
    let mut name_offset: u32 = a[..from.0]
        .iter()
        .chain(&b[..from.1])
        .map(|item| item.name_bytes())
        .sum();
    let mut out = Vec::with_capacity(to.0 - from.0 + to.1 - from.1);
    merge_into(
        arena,
        descending,
        &a[from.0..to.0],
        &b[from.1..to.1],
        |item| {
            out.push(Placed {
                index: item.index_u32(),
                name_offset,
            });
            name_offset += item.name_bytes();
        },
    );
    out
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::SortKey;
    use cabinetos_protocol::shm::ListingMeta;

    use super::*;
    use crate::Entry;
    use crate::attributes::{DIRECTORY, HIDDEN, REPARSE_POINT};

    /// A small, seeded generator, so a failure repeats.
    struct Random(u64);

    impl Random {
        fn next(&mut self) -> u64 {
            // xorshift64*
            self.0 ^= self.0 >> 12;
            self.0 ^= self.0 << 25;
            self.0 ^= self.0 >> 27;
            self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
        }

        fn below(&mut self, bound: usize) -> usize {
            usize::try_from(self.next() % u64::try_from(bound).unwrap()).unwrap()
        }

        fn pick<'a>(&mut self, items: &[&'a str]) -> &'a str {
            items[self.below(items.len())]
        }
    }

    /// A listing that no folder could hold, to reach every tie-break:
    /// names equal but for case, exact duplicates, long shared prefixes
    /// (past the 16 key bytes kept in each item), numbers, Unicode; few
    /// distinct sizes and times; directories, links and hidden entries.
    fn synthetic(random: &mut Random, count: usize) -> Listing {
        let words = [
            "report",
            "Report",
            "REPORT",
            "photo",
            "IMG_",
            "Звіт",
            "звіт",
            "資料",
            "a",
            "a-very-long-shared-beginning-that-outlasts-sixteen-key-bytes-",
        ];
        let separators = ["", " ", "_", "-", "."];
        let mut listing = Listing::default();
        for index in 0..count {
            let name = match random.below(10) {
                0 => "same name".to_owned(),
                1 => format!("{}{}", random.pick(&words), random.below(3)),
                _ => format!(
                    "{}{}{}{}",
                    random.pick(&words),
                    random.pick(&separators),
                    random.below(5000),
                    random.pick(&[".txt", ".TXT", "", ".jpg", " (2).pdf"]),
                ),
            };
            let mut attributes = if random.below(8) == 0 {
                DIRECTORY
            } else {
                0x20
            };
            if random.below(20) == 0 {
                attributes |= HIDDEN;
            }
            let reparse = random.below(25) == 0;
            if reparse {
                attributes |= REPARSE_POINT;
            }
            let start = listing.names.len();
            listing.names.extend(name.encode_utf16());
            listing.entries.push(Entry {
                id: index as u64 + 1,
                kind: crate::enumerate::kind_of(attributes, if reparse { 0xA000_000C } else { 0 }),
                flags: 0,
                meta: ListingMeta {
                    size: [0, 1, 4096, 1 << 40][random.below(4)] + random.below(3) as u64,
                    modified: [0, 133_000_000_000_000_000, -5][random.below(3)]
                        + i64::try_from(random.below(2)).unwrap(),
                    attributes,
                    ..ListingMeta::default()
                },
                name_start: u32::try_from(start).unwrap(),
                name_len: u16::try_from(listing.names.len() - start).unwrap(),
            });
        }
        listing
    }

    fn specs() -> Vec<SortSpec> {
        [
            SortKey::Name,
            SortKey::Size,
            SortKey::Modified,
            SortKey::Kind,
        ]
        .into_iter()
        .flat_map(|key| {
            [false, true]
                .into_iter()
                .map(move |descending| SortSpec { key, descending })
        })
        .collect()
    }

    /// The entries in display order, as `(id, name)`.
    fn shown(listing: &Listing) -> Vec<(u64, String)> {
        listing
            .entries()
            .iter()
            .map(|entry| (entry.id, listing.name_string(entry)))
            .collect()
    }

    /// Sorts `listing` the pipelined way, in batches of random sizes.
    fn batched(random: &mut Random, listing: &Listing, spec: SortSpec) -> Listing {
        let mut sorter = Sorter::new(spec);
        let mut start = 0;
        while start < listing.entries.len() {
            let end = (start + 1 + random.below(700)).min(listing.entries.len());
            sorter.add("synthetic", listing, start, end).unwrap();
            start = end;
        }
        let order = sorter.finish("synthetic", listing).unwrap();
        Listing {
            entries: listing.entries.clone(),
            names: listing.names.clone(),
            order: Some(order),
            display: OnceLock::new(),
        }
    }

    #[test]
    fn batches_and_merges_give_the_serial_order() {
        let mut random = Random(0x5EED_CAB1_0E70_0001);
        // Sizes below and above the split of the last merge.
        for count in [0, 1, 2, 700, 5_000, PARALLEL_MERGE_FROM + 3_333] {
            let listing = synthetic(&mut random, count);
            for spec in specs() {
                let mut serial = listing.clone();
                sort::sort(&mut serial, spec);
                let pipelined = batched(&mut random, &listing, spec);
                assert!(
                    shown(&serial) == shown(&pipelined),
                    "{count} entries sorted by {spec:?} in a different order"
                );
            }
        }
    }

    #[test]
    fn name_offsets_follow_the_display_order() {
        let mut random = Random(0x5EED_CAB1_0E70_0002);
        let listing = synthetic(&mut random, PARALLEL_MERGE_FROM * 2);
        let pipelined = batched(&mut random, &listing, SortSpec::default());
        let order = pipelined.order.as_ref().unwrap();
        let mut offset = 0u32;
        for placed in order {
            assert_eq!(placed.name_offset, offset);
            offset += u32::from(listing.entries[placed.index as usize].name_len) * 2;
        }
        let mut seen: Vec<u32> = order.iter().map(|placed| placed.index).collect();
        seen.sort_unstable();
        assert!(
            seen.iter()
                .copied()
                .eq(0..u32::try_from(listing.len()).unwrap())
        );
    }

    #[test]
    fn the_merge_path_split_takes_the_first_items() {
        let mut random = Random(0x5EED_CAB1_0E70_0003);
        let listing = synthetic(&mut random, 3_000);
        let spec = SortSpec::default();
        let mut sorter = Sorter::new(spec);
        // A smaller run on a larger one stays separate.
        sorter.add("synthetic", &listing, 0, 2_000).unwrap();
        sorter.add("synthetic", &listing, 2_000, 3_000).unwrap();
        let [a, b] = <[Vec<Item>; 2]>::try_from(sorter.runs.clone()).unwrap();
        let arena = Arena {
            listing: &listing,
            keys: &sorter.keys,
        };
        let merged = merge(&arena, false, &a, &b);
        for rank in [0, 1, 999, 1_500, 2_000, 2_999, 3_000] {
            let from_a = split(&arena, false, &a, &b, rank);
            let first: std::collections::HashSet<u64> =
                merged[..rank].iter().map(|item| item.pos).collect();
            let expected: std::collections::HashSet<u64> = a[..from_a]
                .iter()
                .chain(&b[..rank - from_a])
                .map(|item| item.pos)
                .collect();
            assert_eq!(first, expected, "rank {rank}");
        }
    }
}
