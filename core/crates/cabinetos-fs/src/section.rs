//! Writing a listing into a shared-memory section, and reading it back.
//!
//! The layout is defined and pinned in `cabinetos_protocol::shm` (diagram in
//! `docs/ipc.md`): header, entry array, metadata array, name arena. The
//! writer sizes the section exactly and writes it in one pass; a large
//! listing is split among several threads, each writing its own part of the
//! three arrays, so no byte has two writers. The reader is
//! a safe, bounds-checked view for Rust clients (the CLI, tests); the C# UI
//! reads the same bytes through pointers.

use std::mem::offset_of;

use cabinetos_protocol::ChangeKind;
use cabinetos_protocol::shm::{EntryKind, ListingEntry, ListingHeader, ListingMeta, PreviewRow};

use crate::Listing;

/// A section that does not hold a valid listing, or a listing that does not
/// fit the layout.
#[derive(Debug, thiserror::Error)]
#[error("listing section: {0}")]
pub struct LayoutError(String);

impl LayoutError {
    pub(crate) fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

const HEADER_SIZE: usize = size_of::<ListingHeader>();
const ENTRY_SIZE: usize = size_of::<ListingEntry>();
const META_SIZE: usize = size_of::<ListingMeta>();

/// Where each part of a section starts, in bytes.
#[derive(Clone, Copy, Debug)]
struct Layout {
    entry_count: u32,
    entries_offset: u32,
    meta_offset: u32,
    name_arena_offset: u32,
    name_arena_len: u32,
    total: u32,
}

impl Layout {
    fn for_listing(listing: &Listing) -> Result<Self, LayoutError> {
        let too_large = || LayoutError("the listing is larger than 4 GiB".to_owned());
        let count = listing.entries.len();
        let entries_offset = HEADER_SIZE;
        let meta_offset = count
            .checked_mul(ENTRY_SIZE)
            .and_then(|size| size.checked_add(entries_offset))
            .ok_or_else(too_large)?;
        let name_arena_offset = count
            .checked_mul(META_SIZE)
            .and_then(|size| size.checked_add(meta_offset))
            .ok_or_else(too_large)?;
        let name_arena_len = listing
            .entries
            .iter()
            .map(|entry| usize::from(entry.name_len) * 2)
            .sum::<usize>();
        let total = name_arena_offset
            .checked_add(name_arena_len)
            .ok_or_else(too_large)?;
        let to_u32 = |value: usize| u32::try_from(value).map_err(|_| too_large());
        Ok(Self {
            entry_count: to_u32(count)?,
            entries_offset: to_u32(entries_offset)?,
            meta_offset: to_u32(meta_offset)?,
            name_arena_offset: to_u32(name_arena_offset)?,
            name_arena_len: to_u32(name_arena_len)?,
            total: to_u32(total)?,
        })
    }
}

/// Writes one listing into a section, in the order of the listing's entries.
#[derive(Debug)]
pub struct ListingWriter<'a> {
    listing: &'a Listing,
    layout: Layout,
}

impl<'a> ListingWriter<'a> {
    /// Plans the section for `listing`. Fails only if it would exceed 4 GiB,
    /// the limit of the layout's 32-bit offsets.
    pub fn new(listing: &'a Listing) -> Result<Self, LayoutError> {
        Ok(Self {
            listing,
            layout: Layout::for_listing(listing)?,
        })
    }

    /// The exact number of bytes the listing takes in the section.
    #[must_use]
    pub fn section_size(&self) -> usize {
        self.layout.total as usize
    }

    /// Writes the whole listing into `out`, which must hold at least
    /// [`section_size`](Self::section_size) bytes (the rest is left alone).
    /// A large listing is written by several threads, each into its own
    /// part of the entries, the metadata and the names.
    pub fn write(&self, out: &mut [u8], generation: u32) -> Result<(), LayoutError> {
        self.write_with_threads(out, generation, write_threads(self.listing.len()))
    }

    /// [`write`](Self::write) with the entries split among `threads`
    /// threads; 1 writes everything in one pass on this thread. The bytes
    /// are the same either way.
    pub fn write_with_threads(
        &self,
        out: &mut [u8],
        generation: u32,
        threads: usize,
    ) -> Result<(), LayoutError> {
        let layout = self.layout;
        if out.len() < layout.total as usize {
            return Err(LayoutError(format!(
                "the section has {} bytes, the listing needs {}",
                out.len(),
                layout.total
            )));
        }
        let header = [
            (offset_of!(ListingHeader, magic), ListingHeader::MAGIC),
            (offset_of!(ListingHeader, version), ListingHeader::VERSION),
            (offset_of!(ListingHeader, entry_count), layout.entry_count),
            (
                offset_of!(ListingHeader, name_arena_offset),
                layout.name_arena_offset,
            ),
            (
                offset_of!(ListingHeader, name_arena_len),
                layout.name_arena_len,
            ),
            (offset_of!(ListingHeader, generation), generation),
            (offset_of!(ListingHeader, meta_offset), layout.meta_offset),
            (
                offset_of!(ListingHeader, entries_offset),
                layout.entries_offset,
            ),
            (offset_of!(ListingHeader, flags), 0),
            (offset_of!(ListingHeader, preview_offset), 0),
        ];
        for (at, value) in header {
            put(out, at, &value.to_le_bytes());
        }

        // The three parts follow each other (see `Layout`); each thread gets
        // its own slice of every part, so no byte has two writers.
        let count = layout.entry_count as usize;
        let rest = &mut out[layout.entries_offset as usize..layout.total as usize];
        let (entries_out, rest) = rest.split_at_mut(count * ENTRY_SIZE);
        let (meta_out, names_out) = rest.split_at_mut(count * META_SIZE);
        let threads = threads.clamp(1, count.max(1));
        if threads == 1 {
            write_range(self.listing, 0..count, entries_out, meta_out, names_out, 0);
            return Ok(());
        }
        // Every start is below `count`, as `threads <= count`; the product
        // fits, as `count` fits in 32 bits.
        let starts: Vec<usize> = (0..threads)
            .map(|thread| thread * count / threads)
            .collect();
        let name_starts = self.name_offsets_at(&starts);
        std::thread::scope(|scope| {
            let (mut entries_rest, mut meta_rest, mut names_rest) =
                (entries_out, meta_out, names_out);
            for thread in 0..threads {
                let (first, end) = (
                    starts[thread],
                    starts.get(thread + 1).map_or(count, |&next| next),
                );
                let names_end = name_starts
                    .get(thread + 1)
                    .map_or(layout.name_arena_len as usize, |&next| next);
                let names_len = names_end - name_starts[thread];
                let (entries, rest) =
                    std::mem::take(&mut entries_rest).split_at_mut((end - first) * ENTRY_SIZE);
                entries_rest = rest;
                let (meta, rest) =
                    std::mem::take(&mut meta_rest).split_at_mut((end - first) * META_SIZE);
                meta_rest = rest;
                let (names, rest) = std::mem::take(&mut names_rest).split_at_mut(names_len);
                names_rest = rest;
                let listing = self.listing;
                let name_start = name_starts[thread];
                scope.spawn(move || {
                    write_range(listing, first..end, entries, meta, names, name_start);
                });
            }
        });
        Ok(())
    }

    /// Where, in bytes, the name of the entry at each display position in
    /// `positions` (ascending) starts in the name arena.
    fn name_offsets_at(&self, positions: &[usize]) -> Vec<usize> {
        let listing = self.listing;
        if let Some(order) = &listing.order {
            return positions
                .iter()
                .map(|&position| order[position].name_offset as usize)
                .collect();
        }
        let mut offsets = Vec::with_capacity(positions.len());
        let mut cursor = 0usize;
        let mut next = 0usize;
        for &position in positions {
            for entry in &listing.entries[next..position] {
                cursor += usize::from(entry.name_len) * 2;
            }
            next = position;
            offsets.push(cursor);
        }
        offsets
    }
}

/// How many threads [`ListingWriter::write`] uses for `count` entries.
fn write_threads(count: usize) -> usize {
    if count < PARALLEL_WRITE_FROM {
        return 1;
    }
    std::thread::available_parallelism()
        .map_or(1, std::num::NonZero::get)
        .min(MAX_WRITE_THREADS)
}

/// Listings at least this long are written by several threads.
const PARALLEL_WRITE_FROM: usize = 16_384;

/// At most this many threads write a section.
const MAX_WRITE_THREADS: usize = 4;

/// Writes the entries at display positions `positions` into their slices:
/// `entries_out` and `meta_out` start at the first position's entry, and
/// `names_out` at `name_start`, that entry's name offset in the arena.
fn write_range(
    listing: &Listing,
    positions: std::ops::Range<usize>,
    entries_out: &mut [u8],
    meta_out: &mut [u8],
    names_out: &mut [u8],
    name_start: usize,
) {
    let mut name_cursor = name_start;
    for (slot, position) in positions.enumerate() {
        let (entry, name_offset) = match &listing.order {
            Some(order) => {
                let placed = order[position];
                (
                    &listing.entries[placed.index as usize],
                    placed.name_offset as usize,
                )
            }
            None => (&listing.entries[position], name_cursor),
        };
        let name = listing.name(entry);

        let at = slot * ENTRY_SIZE;
        put(
            entries_out,
            at + offset_of!(ListingEntry, id),
            &entry.id.to_le_bytes(),
        );
        put(
            entries_out,
            at + offset_of!(ListingEntry, name_offset),
            &u32::try_from(name_offset)
                .expect("checked by Layout")
                .to_le_bytes(),
        );
        put(
            entries_out,
            at + offset_of!(ListingEntry, name_len),
            &entry.name_len.to_le_bytes(),
        );
        entries_out[at + offset_of!(ListingEntry, kind)] = entry.kind.to_raw();
        entries_out[at + offset_of!(ListingEntry, flags)] = entry.flags;

        let at = slot * META_SIZE;
        let meta = &entry.meta;
        put(
            meta_out,
            at + offset_of!(ListingMeta, size),
            &meta.size.to_le_bytes(),
        );
        put(
            meta_out,
            at + offset_of!(ListingMeta, modified),
            &meta.modified.to_le_bytes(),
        );
        put(
            meta_out,
            at + offset_of!(ListingMeta, created),
            &meta.created.to_le_bytes(),
        );
        put(
            meta_out,
            at + offset_of!(ListingMeta, accessed),
            &meta.accessed.to_le_bytes(),
        );
        put(
            meta_out,
            at + offset_of!(ListingMeta, attributes),
            &meta.attributes.to_le_bytes(),
        );
        put(
            meta_out,
            at + offset_of!(ListingMeta, reparse_tag),
            &meta.reparse_tag.to_le_bytes(),
        );

        let at = name_offset - name_start;
        for (bytes, unit) in names_out[at..at + name.len() * 2]
            .as_chunks_mut::<2>()
            .0
            .iter_mut()
            .zip(name)
        {
            bytes.copy_from_slice(&unit.to_le_bytes());
        }
        name_cursor = name_offset + name.len() * 2;
    }
}

fn put(out: &mut [u8], at: usize, bytes: &[u8]) {
    out[at..at + bytes.len()].copy_from_slice(bytes);
}

/// A safe, bounds-checked view of a listing section.
#[derive(Clone, Copy, Debug)]
pub struct ListingReader<'a> {
    bytes: &'a [u8],
    header: ListingHeader,
}

/// One entry, decoded from a section.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EntryView {
    /// The entry's ID (see `ListingEntry::id`).
    pub id: u64,
    /// What the entry is.
    pub kind: EntryKind,
    /// The entry's flags (see `ListingEntry::FLAG_ID_IS_NAME_HASH`).
    pub flags: u8,
    /// The name. Invalid UTF-16 (possible in Windows names) shows as U+FFFD.
    pub name: String,
    /// Size, times and attributes.
    pub meta: ListingMeta,
}

impl<'a> ListingReader<'a> {
    /// Checks the header and that every part lies inside `bytes`.
    pub fn new(bytes: &'a [u8]) -> Result<Self, LayoutError> {
        let field = |at: usize| read_u32(bytes, at).ok_or_else(|| short(bytes.len(), at + 4));
        let header = ListingHeader {
            magic: field(offset_of!(ListingHeader, magic))?,
            version: field(offset_of!(ListingHeader, version))?,
            entry_count: field(offset_of!(ListingHeader, entry_count))?,
            name_arena_offset: field(offset_of!(ListingHeader, name_arena_offset))?,
            name_arena_len: field(offset_of!(ListingHeader, name_arena_len))?,
            generation: field(offset_of!(ListingHeader, generation))?,
            meta_offset: field(offset_of!(ListingHeader, meta_offset))?,
            entries_offset: field(offset_of!(ListingHeader, entries_offset))?,
            flags: field(offset_of!(ListingHeader, flags))?,
            preview_offset: field(offset_of!(ListingHeader, preview_offset))?,
        };
        if header.magic != ListingHeader::MAGIC {
            return Err(LayoutError(format!("wrong magic {:#010x}", header.magic)));
        }
        if header.version != ListingHeader::VERSION {
            return Err(LayoutError(format!(
                "layout version {} (this build reads {})",
                header.version,
                ListingHeader::VERSION
            )));
        }
        let count = u64::from(header.entry_count);
        let preview = header.flags & ListingHeader::FLAG_PREVIEW != 0;
        let parts = [
            (
                "entries",
                header.entries_offset,
                count * ENTRY_SIZE as u64,
                8,
            ),
            ("metadata", header.meta_offset, count * META_SIZE as u64, 8),
            (
                "names",
                header.name_arena_offset,
                u64::from(header.name_arena_len),
                2,
            ),
            (
                "preview rows",
                header.preview_offset,
                if preview {
                    count * size_of::<PreviewRow>() as u64
                } else {
                    0
                },
                4,
            ),
        ];
        for (part, offset, len, align) in parts {
            if u64::from(offset) % align != 0 {
                return Err(LayoutError(format!(
                    "{part} at {offset} is not {align}-byte aligned"
                )));
            }
            let end = u64::from(offset) + len;
            if end > bytes.len() as u64 {
                return Err(short(
                    bytes.len(),
                    usize::try_from(end).unwrap_or(usize::MAX),
                ));
            }
        }
        Ok(Self { bytes, header })
    }

    /// The header.
    #[must_use]
    pub fn header(&self) -> ListingHeader {
        self.header
    }

    /// Number of entries.
    #[must_use]
    pub fn len(&self) -> usize {
        self.header.entry_count as usize
    }

    /// Whether the listing is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.header.entry_count == 0
    }

    /// Entry `index`, decoded.
    pub fn entry(&self, index: usize) -> Result<EntryView, LayoutError> {
        if index >= self.len() {
            return Err(LayoutError(format!("no entry {index} in {}", self.len())));
        }
        let bytes = self.bytes;
        let malformed = || LayoutError(format!("entry {index} is malformed"));
        let at = self.header.entries_offset as usize + index * ENTRY_SIZE;
        let id = read_u64(bytes, at + offset_of!(ListingEntry, id)).ok_or_else(malformed)?;
        let name_bytes = self.name_bytes(index, at)?;
        let kind = *bytes
            .get(at + offset_of!(ListingEntry, kind))
            .ok_or_else(malformed)?;
        let flags = *bytes
            .get(at + offset_of!(ListingEntry, flags))
            .ok_or_else(malformed)?;

        let at = self.header.meta_offset as usize + index * META_SIZE;
        let meta = ListingMeta {
            size: read_u64(bytes, at + offset_of!(ListingMeta, size)).ok_or_else(malformed)?,
            modified: read_i64(bytes, at + offset_of!(ListingMeta, modified))
                .ok_or_else(malformed)?,
            created: read_i64(bytes, at + offset_of!(ListingMeta, created))
                .ok_or_else(malformed)?,
            accessed: read_i64(bytes, at + offset_of!(ListingMeta, accessed))
                .ok_or_else(malformed)?,
            attributes: read_u32(bytes, at + offset_of!(ListingMeta, attributes))
                .ok_or_else(malformed)?,
            reparse_tag: read_u32(bytes, at + offset_of!(ListingMeta, reparse_tag))
                .ok_or_else(malformed)?,
        };

        let units: Vec<u16> = name_bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect();

        Ok(EntryView {
            id,
            kind: EntryKind::from_raw(kind),
            flags,
            name: String::from_utf16_lossy(&units),
            meta,
        })
    }

    /// Entry `index`'s name as UTF-16 little-endian bytes, and its
    /// attributes, without decoding the rest or allocating: for a pass over
    /// every name of a listing, as `match_entries` makes.
    pub fn name_and_attributes(&self, index: usize) -> Result<(&'a [u8], u32), LayoutError> {
        if index >= self.len() {
            return Err(LayoutError(format!("no entry {index} in {}", self.len())));
        }
        let at = self.header.entries_offset as usize + index * ENTRY_SIZE;
        let name = self.name_bytes(index, at)?;
        let attributes = read_u32(
            self.bytes,
            self.header.meta_offset as usize
                + index * META_SIZE
                + offset_of!(ListingMeta, attributes),
        )
        .ok_or_else(|| LayoutError(format!("entry {index} is malformed")))?;
        Ok((name, attributes))
    }

    // The name bytes of the entry whose record starts at `at`, checked to lie in the arena.
    fn name_bytes(&self, index: usize, at: usize) -> Result<&'a [u8], LayoutError> {
        let malformed = || LayoutError(format!("entry {index} is malformed"));
        let name_offset = read_u32(self.bytes, at + offset_of!(ListingEntry, name_offset))
            .ok_or_else(malformed)?;
        let name_len =
            read_u16(self.bytes, at + offset_of!(ListingEntry, name_len)).ok_or_else(malformed)?;
        // u32 offset plus twice a u16 length cannot overflow usize on 64 bits.
        let name_start = name_offset as usize;
        let name_end = name_start + usize::from(name_len) * 2;
        if name_end > self.header.name_arena_len as usize {
            return Err(LayoutError(format!(
                "the name of entry {index} lies outside the arena"
            )));
        }
        let arena = self.header.name_arena_offset as usize;
        self.bytes
            .get(arena + name_start..arena + name_end)
            .ok_or_else(malformed)
    }

    /// Every entry, in listing order.
    pub fn entries(&self) -> impl Iterator<Item = Result<EntryView, LayoutError>> + '_ {
        (0..self.len()).map(|index| self.entry(index))
    }

    /// Whether the listing is a preview of proposed changes
    /// ([`ListingHeader::FLAG_PREVIEW`]).
    #[must_use]
    pub fn is_preview(&self) -> bool {
        self.header.flags & ListingHeader::FLAG_PREVIEW != 0
    }

    /// The change and target of preview row `index`. An error for a
    /// listing that is no preview.
    pub fn preview_row(&self, index: usize) -> Result<PreviewView, LayoutError> {
        if !self.is_preview() {
            return Err(LayoutError::new("the listing is not a preview"));
        }
        if index >= self.len() {
            return Err(LayoutError(format!("no entry {index} in {}", self.len())));
        }
        let malformed = || LayoutError(format!("preview row {index} is malformed"));
        let at = self.header.preview_offset as usize + index * size_of::<PreviewRow>();
        let to_offset =
            read_u32(self.bytes, at + offset_of!(PreviewRow, to_offset)).ok_or_else(malformed)?;
        let to_len =
            read_u32(self.bytes, at + offset_of!(PreviewRow, to_len)).ok_or_else(malformed)?;
        let change = *self
            .bytes
            .get(at + offset_of!(PreviewRow, change))
            .ok_or_else(malformed)?;
        let to = if to_len == 0 {
            None
        } else {
            let start = to_offset as usize;
            let end = start + to_len as usize * 2;
            if end > self.header.name_arena_len as usize {
                return Err(LayoutError(format!(
                    "the target of preview row {index} lies outside the arena"
                )));
            }
            let arena = self.header.name_arena_offset as usize;
            let bytes = self
                .bytes
                .get(arena + start..arena + end)
                .ok_or_else(malformed)?;
            let units: Vec<u16> = bytes
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| u16::from_le_bytes(*pair))
                .collect();
            Some(String::from_utf16_lossy(&units))
        };
        Ok(PreviewView {
            change: ChangeKind::from_raw(change),
            to,
        })
    }
}

/// A preview row's change and target, decoded from a section.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PreviewView {
    /// The change; `None` for a value this build does not know.
    pub change: Option<ChangeKind>,
    /// The target: the new full path of a rename, the folder of a move or
    /// a copy; `None` when the row has none.
    pub to: Option<String>,
}

fn short(len: usize, needed: usize) -> LayoutError {
    LayoutError(format!(
        "the section has {len} bytes, the layout needs {needed}"
    ))
}

fn read_u16(bytes: &[u8], at: usize) -> Option<u16> {
    Some(u16::from_le_bytes(bytes.get(at..at + 2)?.try_into().ok()?))
}

fn read_u32(bytes: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_le_bytes(bytes.get(at..at + 4)?.try_into().ok()?))
}

fn read_u64(bytes: &[u8], at: usize) -> Option<u64> {
    Some(u64::from_le_bytes(bytes.get(at..at + 8)?.try_into().ok()?))
}

fn read_i64(bytes: &[u8], at: usize) -> Option<i64> {
    Some(i64::from_le_bytes(bytes.get(at..at + 8)?.try_into().ok()?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Entry;

    fn sample() -> Listing {
        let mut listing = Listing::default();
        let items = [
            ("docs", EntryKind::Directory, 0u64, 0x10u32),
            ("Привіт.txt", EntryKind::File, 1234, 0x20),
            ("😀", EntryKind::ReparsePoint, 0, 0x420),
        ];
        for (index, (name, kind, size, attributes)) in items.into_iter().enumerate() {
            let start = listing.names.len();
            listing.names.extend(name.encode_utf16());
            listing.entries.push(Entry {
                id: 1000 + index as u64,
                kind,
                flags: u8::from(index == 2),
                meta: ListingMeta {
                    size,
                    modified: 133_000_000_000_000_000 + i64::try_from(index).unwrap(),
                    created: 132_000_000_000_000_000,
                    accessed: 134_000_000_000_000_000,
                    attributes,
                    reparse_tag: 0,
                },
                name_start: u32::try_from(start).unwrap(),
                name_len: u16::try_from(listing.names.len() - start).unwrap(),
            });
        }
        listing
    }

    #[test]
    fn round_trips_through_the_pinned_layout() {
        let listing = sample();
        let writer = ListingWriter::new(&listing).unwrap();
        // 40 header + 3 × 16 entries + 3 × 40 metadata + names (4 + 10 + 2 units).
        assert_eq!(writer.section_size(), 40 + 48 + 120 + 32);
        let mut section = vec![0xAAu8; writer.section_size() + 100];
        writer.write(&mut section, 7).unwrap();

        // Offsets straight from the bytes, against the documented layout.
        let u32_at = |at: usize| u32::from_le_bytes(section[at..at + 4].try_into().unwrap());
        assert_eq!(&section[0..4], b"CBLS");
        assert_eq!(u32_at(4), 2, "version");
        assert_eq!(u32_at(8), 3, "entry_count");
        assert_eq!(u32_at(12), 208, "name_arena_offset");
        assert_eq!(u32_at(16), 32, "name_arena_len");
        assert_eq!(u32_at(20), 7, "generation");
        assert_eq!(u32_at(24), 88, "meta_offset");
        assert_eq!(u32_at(28), 40, "entries_offset");
        assert_eq!(u32_at(32), 0, "flags");
        assert_eq!(u32_at(36), 0, "preview_offset");
        // The second entry's name starts after "docs" (4 units = 8 bytes).
        assert_eq!(u32_at(40 + 16 + 8), 8);

        let reader = ListingReader::new(&section).unwrap();
        assert_eq!(reader.len(), 3);
        assert_eq!(reader.header().generation, 7);
        let views: Vec<EntryView> = reader.entries().collect::<Result<_, _>>().unwrap();
        for (view, entry) in views.iter().zip(&listing.entries) {
            assert_eq!(view.id, entry.id);
            assert_eq!(view.kind, entry.kind);
            assert_eq!(view.flags, entry.flags);
            assert_eq!(view.meta, entry.meta);
            assert_eq!(view.name, listing.name_string(entry));
        }
    }

    #[test]
    fn an_empty_listing_is_just_a_header() {
        let listing = Listing::default();
        let writer = ListingWriter::new(&listing).unwrap();
        assert_eq!(writer.section_size(), 40);
        let mut section = vec![0u8; 40];
        writer.write(&mut section, 1).unwrap();
        let reader = ListingReader::new(&section).unwrap();
        assert!(reader.is_empty());
        assert!(reader.entry(0).is_err());
    }

    #[test]
    fn the_reader_rejects_damaged_sections() {
        let listing = sample();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 1).unwrap();

        assert!(ListingReader::new(&section[..30]).is_err(), "cut header");
        assert!(
            ListingReader::new(&section[..section.len() - 1]).is_err(),
            "cut arena"
        );
        let mut wrong_magic = section.clone();
        wrong_magic[0] = b'X';
        assert!(ListingReader::new(&wrong_magic).is_err());
        let mut wrong_version = section.clone();
        wrong_version[4] = 1;
        assert!(ListingReader::new(&wrong_version).is_err());
        let mut huge_count = section.clone();
        huge_count[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(ListingReader::new(&huge_count).is_err());
        let mut name_outside = section;
        name_outside[40 + 8..40 + 12].copy_from_slice(&1000u32.to_le_bytes());
        let reader = ListingReader::new(&name_outside).unwrap();
        assert!(reader.entry(0).is_err());
    }

    /// A listing of `count` entries with names of many lengths.
    fn many(count: usize) -> Listing {
        let mut listing = Listing::default();
        for index in 0..count {
            let start = listing.names.len();
            let name = "x".repeat(index % 37) + &index.to_string();
            listing.names.extend(name.encode_utf16());
            listing.entries.push(Entry {
                id: index as u64,
                kind: EntryKind::File,
                flags: 0,
                meta: ListingMeta {
                    size: index as u64 * 3,
                    attributes: 0x20,
                    ..ListingMeta::default()
                },
                name_start: u32::try_from(start).unwrap(),
                name_len: u16::try_from(listing.names.len() - start).unwrap(),
            });
        }
        listing
    }

    /// Panics at the first byte where `a` and `b` differ, without printing
    /// whole sections.
    fn assert_same_bytes(a: &[u8], b: &[u8], what: &str) {
        assert_eq!(a.len(), b.len(), "{what}: lengths");
        if let Some(at) = a.iter().zip(b).position(|(x, y)| x != y) {
            panic!("{what}: first difference at byte {at}");
        }
    }

    fn written(listing: &Listing, threads: usize) -> Vec<u8> {
        let writer = ListingWriter::new(listing).unwrap();
        let mut section = vec![0xAAu8; writer.section_size()];
        writer.write_with_threads(&mut section, 3, threads).unwrap();
        section
    }

    #[test]
    fn any_number_of_threads_writes_the_same_bytes() {
        let plain = many(10_001);
        // The same entries in another display order, given as an order...
        let permutation: Vec<usize> = (0..plain.len())
            .map(|i| (i * 7_919) % plain.len())
            .collect();
        let mut name_offset = 0u32;
        let order = permutation
            .iter()
            .map(|&index| {
                let placed = crate::Placed {
                    index: u32::try_from(index).unwrap(),
                    name_offset,
                };
                name_offset += u32::from(plain.entries[index].name_len) * 2;
                placed
            })
            .collect();
        let ordered = Listing {
            order: Some(order),
            ..plain.clone()
        };
        // ...and moved into that order.
        let moved = Listing {
            entries: permutation
                .iter()
                .map(|&index| plain.entries[index])
                .collect(),
            ..plain.clone()
        };
        for listing in [&plain, &ordered] {
            let one = written(listing, 1);
            for threads in 2..=7 {
                assert_same_bytes(
                    &written(listing, threads),
                    &one,
                    &format!("{threads} threads"),
                );
            }
        }
        assert_same_bytes(
            &written(&ordered, 4),
            &written(&moved, 1),
            "ordered and moved",
        );
        assert!(ListingReader::new(&written(&ordered, 3)).is_ok());
        // As many threads as entries or more, and no entries.
        for count in [0, 5, 10] {
            let small = many(count);
            let one = written(&small, 1);
            for threads in 2..=12 {
                assert_same_bytes(
                    &written(&small, threads),
                    &one,
                    &format!("{count} entries, {threads} threads"),
                );
            }
        }
    }

    #[test]
    fn the_writer_needs_room() {
        let listing = sample();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut too_small = vec![0u8; writer.section_size() - 1];
        assert!(writer.write(&mut too_small, 1).is_err());
    }
}
