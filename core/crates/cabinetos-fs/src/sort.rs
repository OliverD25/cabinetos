//! Sorting a listing in the core, so the UI never sorts (brief §1).
//!
//! Directories (anything with the directory attribute, junctions included)
//! always come first. Within each group the entries follow the requested key;
//! `descending` reverses the order within each group.
//!
//! Names are compared in Explorer's natural order: case-insensitive, digits
//! compared as numbers (`file2` before `file10`). Explorer uses
//! `StrCmpLogicalW`. Here each name is turned once into a Windows sort key
//! (`LCMapStringEx` with `SORT_DIGITSASNUMBERS | NORM_IGNORECASE`, user
//! locale), and the keys are compared as bytes. Measured on 100,000 mixed
//! names: 41 ms instead of 155 ms, with the same order. Two more reasons:
//! a byte comparison is a strict total order, which Rust's sort requires (a
//! comparator that is not may make it panic), and a tie between equal keys
//! (possible in case-sensitive directories) falls back to the raw name.
//! Known differences from `StrCmpLogicalW`, all rare in file names: numbers
//! longer than 19 digits, and digits outside ASCII (such as ① or ٣).
//!
//! Sorting by extension puts the key of a file's extension in front of its
//! name's key, so the one byte comparison orders by extension, then by
//! name. Windows ends every key with its only 0 byte (keys are made to be
//! compared like C strings), so no extension's key is the start of
//! another's, and two files with different extensions never reach their
//! names. Folders keep their names' keys: the Type column calls every
//! folder a folder, whatever follows a dot in its name.

use std::cmp::Ordering;
use std::num::NonZero;

use cabinetos_protocol::shm::EntryKind;
use cabinetos_protocol::{SortKey, SortSpec};
use windows::Win32::Foundation::LPARAM;
use windows::Win32::Globalization::{
    LCMAP_SORTKEY, LCMapStringEx, NORM_IGNORECASE, SORT_DIGITSASNUMBERS,
};
use windows::core::PCWSTR;

use crate::{Entry, Listing, attributes};

/// Listings at least this long build their sort keys on several threads.
const PARALLEL_FROM: usize = 4096;

/// At most this many threads build sort keys.
const MAX_KEY_THREADS: usize = 8;

/// Sorts `listing` in place.
pub(crate) fn sort(listing: &mut Listing, spec: SortSpec) {
    let keys = NameKeys::build(listing, spec.key);
    let mut items: Vec<SortItem> = listing
        .entries
        .iter()
        .enumerate()
        .map(|(index, entry)| SortItem {
            group: group_rank(entry.meta.attributes),
            primary: primary_value(entry, spec.key),
            prefix: keys.prefix(index),
            index: u32::try_from(index).expect("a listing has fewer than 2^32 entries"),
        })
        .collect();
    let whole = Whole {
        listing,
        keys: &keys,
    };
    items.sort_unstable_by(|a, b| compare(&whole, spec.descending, a, b));
    listing.entries = items
        .iter()
        .map(|item| listing.entries[item.index as usize])
        .collect();
}

/// What the comparison needs of one entry, packed so that most comparisons
/// never leave this array.
struct SortItem {
    group: u8,
    /// Size, modification time or kind rank; 0 when sorting by name or
    /// extension.
    primary: u64,
    /// The first 16 bytes of the name's sort key, big-endian, zero-padded.
    prefix: u128,
    index: u32,
}

impl Packed for SortItem {
    fn group(&self) -> u8 {
        self.group
    }

    fn primary(&self) -> u64 {
        self.primary
    }

    fn prefix(&self) -> u128 {
        self.prefix
    }

    fn position(&self) -> u64 {
        u64::from(self.index)
    }
}

/// A whole listing and its keys: the ties of [`SortItem`]s.
struct Whole<'a> {
    listing: &'a Listing,
    keys: &'a NameKeys,
}

impl Ties<SortItem> for Whole<'_> {
    fn key(&self, item: &SortItem) -> &[u8] {
        self.keys.key(item.index as usize)
    }

    fn name(&self, item: &SortItem) -> &[u16] {
        self.listing
            .name(&self.listing.entries[item.index as usize])
    }
}

/// The fields of a sort item that most comparisons need.
pub(crate) trait Packed {
    /// [`group_rank`]: directories first.
    fn group(&self) -> u8;
    /// [`primary_value`].
    fn primary(&self) -> u64;
    /// The first 16 bytes of the name's sort key ([`NameKeys::prefix`]).
    fn prefix(&self) -> u128;
    /// The entry's place in the enumeration: equal only for the same entry.
    fn position(&self) -> u64;
}

/// What a comparison looks up when the packed fields tie: the whole sort
/// key, then the raw name.
pub(crate) trait Ties<I> {
    /// The whole sort key of the item's name.
    fn key(&self, item: &I) -> &[u8];
    /// The item's name as UTF-16 units.
    fn name(&self, item: &I) -> &[u16];
}

/// The display order of two entries: the group, then the requested key,
/// the name's sort key and the raw name, and last the place in the
/// enumeration. Every way of sorting a listing uses this function, so they
/// all give the same order.
pub(crate) fn compare<I: Packed, T: Ties<I>>(ties: &T, descending: bool, a: &I, b: &I) -> Ordering {
    let group = a.group().cmp(&b.group());
    if group != Ordering::Equal {
        return group;
    }
    let within = a
        .primary()
        .cmp(&b.primary())
        .then(a.prefix().cmp(&b.prefix()))
        .then_with(|| ties.key(a).cmp(ties.key(b)))
        .then_with(|| ties.name(a).cmp(ties.name(b)))
        // Equal only for the same entry; keeps the order strict and total.
        .then(a.position().cmp(&b.position()));
    if descending { within.reverse() } else { within }
}

/// The value the sort key orders by, as an unsigned number. Times are
/// shifted from `i64` to `u64` without changing their order.
pub(crate) fn primary_value(entry: &Entry, key: SortKey) -> u64 {
    match key {
        // The extension is in the name's key (see the module's notes).
        SortKey::Name | SortKey::Extension => 0,
        SortKey::Size => entry.meta.size,
        SortKey::Modified => entry.meta.modified.cast_unsigned() ^ (1 << 63),
        SortKey::Kind => u64::from(kind_rank(entry.kind)),
    }
}

/// Directories before everything else.
pub(crate) fn group_rank(attrs: u32) -> u8 {
    u8::from(attrs & attributes::DIRECTORY == 0)
}

/// Order of kinds for [`SortKey::Kind`]: within the directory group plain
/// directories come before directory links, within the rest files before
/// file links.
fn kind_rank(kind: EntryKind) -> u8 {
    match kind {
        EntryKind::Directory => 0,
        EntryKind::File => 1,
        EntryKind::ReparsePoint => 2,
        EntryKind::Unknown => 3,
    }
}

/// The sort key of every name, in one byte arena. When sorting by
/// extension, a file's key starts with its extension's key.
pub(crate) struct NameKeys {
    bytes: Vec<u8>,
    /// `(start, len)` in `bytes`, by entry index.
    ranges: Vec<(u32, u32)>,
}

impl NameKeys {
    /// Builds the keys for sorting by `key`, on several threads for large
    /// listings: one `LCMapStringEx` call per name is the main cost of
    /// sorting.
    fn build(listing: &Listing, key: SortKey) -> Self {
        let count = listing.entries.len();
        let threads = if count >= PARALLEL_FROM {
            std::thread::available_parallelism()
                .map_or(1, NonZero::get)
                .min(MAX_KEY_THREADS)
        } else {
            1
        };
        if threads <= 1 {
            return Self::build_range(listing, 0, count, key);
        }
        let chunk = count.div_ceil(threads);
        let parts: Vec<Self> = std::thread::scope(|scope| {
            let workers: Vec<_> = (0..threads)
                .map(|thread| {
                    let start = (thread * chunk).min(count);
                    let end = (start + chunk).min(count);
                    scope.spawn(move || Self::build_range(listing, start, end, key))
                })
                .collect();
            workers
                .into_iter()
                .map(|worker| {
                    worker
                        .join()
                        .unwrap_or_else(|panic| std::panic::resume_unwind(panic))
                })
                .collect()
        });
        let mut keys = Self {
            bytes: Vec::with_capacity(parts.iter().map(|part| part.bytes.len()).sum()),
            ranges: Vec::with_capacity(count),
        };
        for part in parts {
            let base = u32::try_from(keys.bytes.len()).unwrap_or(u32::MAX);
            keys.bytes.extend_from_slice(&part.bytes);
            keys.ranges.extend(
                part.ranges
                    .iter()
                    .map(|&(start, len)| (start.saturating_add(base), len)),
            );
        }
        keys
    }

    /// The keys of entries `start..end`, with ranges into its own arena.
    fn build_range(listing: &Listing, start: usize, end: usize, key: SortKey) -> Self {
        let count = end - start;
        let mut keys = Self {
            bytes: Vec::with_capacity(count * 32),
            ranges: Vec::with_capacity(count),
        };
        keys.extend(listing, start, end, key, &mut Self::scratch());
        keys
    }

    /// No keys yet.
    pub(crate) fn new() -> Self {
        Self {
            bytes: Vec::new(),
            ranges: Vec::new(),
        }
    }

    /// A buffer for [`extend`](Self::extend). Sort keys are byte strings,
    /// but the binding takes a UTF-16 buffer and passes its length in units
    /// as the byte capacity: 1024 units hold keys of up to 1024 bytes,
    /// enough for names of about 200 characters. Longer names grow it.
    pub(crate) fn scratch() -> Vec<u16> {
        vec![0; 1024]
    }

    /// Appends the keys of entries `start..end` of `listing` for sorting by
    /// `key`; they get the next indices.
    pub(crate) fn extend(
        &mut self,
        listing: &Listing,
        start: usize,
        end: usize,
        key: SortKey,
        scratch: &mut Vec<u16>,
    ) {
        let by_extension = key == SortKey::Extension;
        for entry in &listing.entries[start..end] {
            let key_start = self.bytes.len();
            let name = listing.name(entry);
            if by_extension && entry.meta.attributes & attributes::DIRECTORY == 0 {
                append_extension_key(name, scratch, &mut self.bytes);
            }
            append_sort_key(name, scratch, &mut self.bytes);
            let len = self.bytes.len() - key_start;
            self.ranges.push((
                u32::try_from(key_start).unwrap_or(u32::MAX),
                u32::try_from(len).unwrap_or(0),
            ));
        }
    }

    pub(crate) fn key(&self, index: usize) -> &[u8] {
        let (start, len) = self.ranges[index];
        let start = start as usize;
        self.bytes.get(start..start + len as usize).unwrap_or(&[])
    }

    /// The first 16 key bytes as a number that orders like the bytes.
    pub(crate) fn prefix(&self, index: usize) -> u128 {
        let mut prefix = [0u8; 16];
        let key = self.key(index);
        let len = key.len().min(16);
        prefix[..len].copy_from_slice(&key[..len]);
        u128::from_be_bytes(prefix)
    }
}

/// Appends the sort key of `name`'s extension to `out`: the part after the
/// last dot, as the Type column reads it (`.gitignore` has one, `README`
/// and `trailing.` have none). A name without one gets a lone 0 byte, which
/// comes before every key Windows makes, so those files come first.
fn append_extension_key(name: &[u16], scratch: &mut Vec<u16>, out: &mut Vec<u8>) {
    let dot = name.iter().rposition(|&unit| unit == u16::from(b'.'));
    match dot {
        Some(dot) if dot + 1 < name.len() => append_sort_key(&name[dot + 1..], scratch, out),
        _ => out.push(0),
    }
}

/// Appends the natural-order sort key of `name` to `out`. If Windows cannot
/// make a key, the raw UTF-16 units are used, which still sorts
/// deterministically.
fn append_sort_key(name: &[u16], scratch: &mut Vec<u16>, out: &mut Vec<u8>) {
    const FLAGS: u32 = LCMAP_SORTKEY | SORT_DIGITSASNUMBERS.0 | NORM_IGNORECASE.0;
    if name.is_empty() {
        return;
    }
    let mut written = map_sort_key(name, FLAGS, Some(scratch.as_mut_slice()));
    if written == 0 {
        let needed = map_sort_key(name, FLAGS, None);
        if needed > 0 {
            scratch.resize(needed, 0);
            written = map_sort_key(name, FLAGS, Some(scratch.as_mut_slice()));
        }
    }
    if written == 0 {
        out.extend(name.iter().flat_map(|unit| unit.to_be_bytes()));
        return;
    }
    out.extend(
        scratch
            .iter()
            .flat_map(|unit| unit.to_le_bytes())
            .take(written),
    );
}

/// `LCMapStringEx` for the user's locale. With `out`, writes the key and
/// returns its length in bytes; without, returns the length needed. 0 means
/// failure.
#[allow(unsafe_code)]
fn map_sort_key(name: &[u16], flags: u32, out: Option<&mut [u16]>) -> usize {
    // SAFETY: a null locale name means the user's default locale. `name` and
    // `out` are valid slices for the whole call; the binding passes their
    // lengths, and with LCMAP_SORTKEY Windows reads the output length as a
    // byte count, which is half the slice's real size, so it cannot write
    // past the end. No version information, reserved pointer or sort handle.
    let written = unsafe { LCMapStringEx(PCWSTR::null(), flags, name, out, None, None, LPARAM(0)) };
    usize::try_from(written).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use cabinetos_protocol::shm::ListingMeta;

    use super::*;
    use crate::Entry;

    fn listing(items: &[(&str, u32, u64, i64)]) -> Listing {
        let mut listing = Listing::default();
        for (index, &(name, attrs, size, modified)) in items.iter().enumerate() {
            let start = listing.names.len();
            listing.names.extend(name.encode_utf16());
            listing.entries.push(Entry {
                id: index as u64 + 1,
                kind: crate::enumerate::kind_of(attrs, 0),
                flags: 0,
                meta: ListingMeta {
                    size,
                    modified,
                    attributes: attrs,
                    ..ListingMeta::default()
                },
                name_start: u32::try_from(start).unwrap(),
                name_len: u16::try_from(listing.names.len() - start).unwrap(),
            });
        }
        listing
    }

    fn names(listing: &Listing) -> Vec<String> {
        listing
            .entries
            .iter()
            .map(|entry| listing.name_string(entry))
            .collect()
    }

    fn sorted(mut listing: Listing, key: SortKey, descending: bool) -> Vec<String> {
        sort(&mut listing, SortSpec { key, descending });
        names(&listing)
    }

    const DIR: u32 = attributes::DIRECTORY;
    const FILE: u32 = 0x20;

    #[test]
    fn natural_order_with_directories_first() {
        let items = [
            ("file10.txt", FILE, 0, 0),
            ("File2.txt", FILE, 0, 0),
            ("zeta", DIR, 0, 0),
            ("file1.txt", FILE, 0, 0),
            ("Alpha", DIR, 0, 0),
            ("beta10", DIR, 0, 0),
            ("beta9", DIR, 0, 0),
        ];
        assert_eq!(
            sorted(listing(&items), SortKey::Name, false),
            [
                "Alpha",
                "beta9",
                "beta10",
                "zeta",
                "file1.txt",
                "File2.txt",
                "file10.txt"
            ]
        );
        assert_eq!(
            sorted(listing(&items), SortKey::Name, true),
            [
                "zeta",
                "beta10",
                "beta9",
                "Alpha",
                "file10.txt",
                "File2.txt",
                "file1.txt"
            ]
        );
    }

    #[test]
    fn size_modified_and_kind_break_ties_by_name() {
        let items = [
            ("b.bin", FILE, 100, 3),
            ("a.bin", FILE, 100, 1),
            ("c.bin", FILE, 5, 2),
            ("dir", DIR, 0, 9),
        ];
        assert_eq!(
            sorted(listing(&items), SortKey::Size, false),
            ["dir", "c.bin", "a.bin", "b.bin"]
        );
        assert_eq!(
            sorted(listing(&items), SortKey::Size, true),
            ["dir", "b.bin", "a.bin", "c.bin"]
        );
        assert_eq!(
            sorted(listing(&items), SortKey::Modified, false),
            ["dir", "a.bin", "c.bin", "b.bin"]
        );
        assert_eq!(
            sorted(listing(&items), SortKey::Kind, false),
            ["dir", "a.bin", "b.bin", "c.bin"]
        );
    }

    #[test]
    fn links_sort_after_plain_entries_of_their_group_by_kind() {
        let reparse = attributes::REPARSE_POINT;
        let mut items = listing(&[
            ("b-link", DIR | reparse, 0, 0),
            ("a-dir", DIR, 0, 0),
            ("c-file", FILE, 0, 0),
        ]);
        items.entries[0].kind = EntryKind::ReparsePoint;
        sort(
            &mut items,
            SortSpec {
                key: SortKey::Kind,
                descending: false,
            },
        );
        assert_eq!(names(&items), ["a-dir", "b-link", "c-file"]);
    }

    /// Explorer's comparison, for checking the sort keys against it.
    #[allow(unsafe_code)]
    fn str_cmp_logical(a: &str, b: &str) -> Ordering {
        let a: Vec<u16> = a.encode_utf16().chain([0]).collect();
        let b: Vec<u16> = b.encode_utf16().chain([0]).collect();
        // SAFETY: both are NUL-terminated UTF-16 strings that outlive the call.
        let result = unsafe {
            windows::Win32::UI::Shell::StrCmpLogicalW(PCWSTR(a.as_ptr()), PCWSTR(b.as_ptr()))
        };
        result.cmp(&0)
    }

    /// The sort keys give exactly Explorer's order for the kinds of names
    /// people use: numbered photos and versions, mixed case, spaces and
    /// punctuation, Ukrainian, Japanese and emoji.
    #[test]
    fn matches_str_cmp_logical_on_typical_names() {
        let mut corpus: Vec<String> = [
            "IMG_0010.jpg",
            "img_0002.JPG",
            "IMG_0001.jpg",
            "Report 2.docx",
            "report 10.docx",
            "report 1.docx",
            "v1.10.0",
            "v1.9.0",
            "v1.9",
            "Привіт.txt",
            "привіт2.txt",
            "Їжак",
            "Ґанок",
            "漢字.txt",
            "かな",
            "😀 party",
            "_notes",
            "a-b",
            "a_b",
            "ab",
            "a b",
            "file01",
            "file1b",
            "file2",
            "file 3",
            "x64",
            "x86",
            "Äpfel",
            "Apfel",
            "Zebra",
        ]
        .into_iter()
        .map(str::to_owned)
        .collect();
        let words = [
            "report",
            "Photo",
            "draft",
            "IMG",
            "Backup",
            "Список",
            "漢字",
            "file",
        ];
        let separators = ["_", " ", "-", ""];
        for n in 0..600_u32 {
            corpus.push(format!(
                "{}{}{}.{}",
                words[n as usize % words.len()],
                separators[(n / 8) as usize % separators.len()],
                n.wrapping_mul(2_654_435_761) % 5_000,
                ["txt", "jpg", "PDF"][(n % 3) as usize],
            ));
        }
        corpus.sort();
        corpus.dedup();

        let mut expected = corpus.clone();
        expected.sort_by(|a, b| str_cmp_logical(a, b));
        let items: Vec<(&str, u32, u64, i64)> = corpus
            .iter()
            .map(|name| (name.as_str(), FILE, 0, 0))
            .collect();
        assert_eq!(sorted(listing(&items), SortKey::Name, false), expected);
    }

    #[test]
    fn case_variants_sort_deterministically() {
        let items = [("a", FILE, 0, 0), ("A", FILE, 0, 0), ("B", FILE, 0, 0)];
        let first = sorted(listing(&items), SortKey::Name, false);
        let reversed: Vec<_> = items.iter().rev().copied().collect();
        assert_eq!(first, sorted(listing(&reversed), SortKey::Name, false));
        assert_eq!(first[2], "B");
    }

    /// Files without an extension first, then by extension ignoring case
    /// (`json` before `jsonc`, `.gitignore` has one), then by name; the
    /// folders by name, whatever follows a dot.
    #[test]
    fn by_extension_then_name_with_folders_by_name() {
        let items = [
            ("b.TXT", FILE, 0, 0),
            ("a.md", FILE, 0, 0),
            ("README", FILE, 0, 0),
            ("c.txt", FILE, 0, 0),
            ("archive.tar.gz", FILE, 0, 0),
            (".gitignore", FILE, 0, 0),
            ("trailing.", FILE, 0, 0),
            ("a.txt", FILE, 0, 0),
            ("z.dir", DIR, 0, 0),
            ("b", DIR, 0, 0),
            ("a.zip", DIR, 0, 0),
            ("m.json", FILE, 0, 0),
            ("m.jsonc", FILE, 0, 0),
            ("l.JSONC", FILE, 0, 0),
            ("v10.mp3", FILE, 0, 0),
            ("v9.MP3", FILE, 0, 0),
        ];
        let ascending = [
            "a.zip",
            "b",
            "z.dir",
            "README",
            "trailing.",
            ".gitignore",
            "archive.tar.gz",
            "m.json",
            "l.JSONC",
            "m.jsonc",
            "a.md",
            "v9.MP3",
            "v10.mp3",
            "a.txt",
            "b.TXT",
            "c.txt",
        ];
        assert_eq!(
            sorted(listing(&items), SortKey::Extension, false),
            ascending
        );
        let mut descending = ascending;
        descending[..3].reverse();
        descending[3..].reverse();
        assert_eq!(
            sorted(listing(&items), SortKey::Extension, true),
            descending
        );
    }

    /// What sorting by extension relies on: Windows ends every key with its
    /// only 0 byte, so no key is the start of a different one.
    #[test]
    fn sort_keys_end_with_their_only_zero_byte() {
        let mut scratch = NameKeys::scratch();
        for name in [
            "txt",
            "TXT",
            "json",
            "jsonc",
            "7z",
            "001",
            "mp3",
            "a-b",
            "it's",
            "--x",
            "-",
            "'",
            "a b",
            "_",
            "~",
            "#",
            "\u{200b}",
            "e\u{301}",
            "é",
            "ß",
            "ı",
            "Привіт",
            "漢字",
            "かな",
            "😀",
            "x86",
        ] {
            let units: Vec<u16> = name.encode_utf16().collect();
            let mut key = Vec::new();
            append_sort_key(&units, &mut scratch, &mut key);
            assert_eq!(
                key.iter().position(|&byte| byte == 0),
                Some(key.len() - 1),
                "{name}: {key:x?}"
            );
        }
    }
}
