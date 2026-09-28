//! Writing a listing into a shared-memory section, and reading it back.
//!
//! The layout is defined and pinned in `cabinetos_protocol::shm` (diagram in
//! `docs/ipc.md`): header, entry array, metadata array, name arena. The
//! writer sizes the section exactly and writes it in one pass. The reader is
//! a safe, bounds-checked view for Rust clients (the CLI, tests); the C# UI
//! reads the same bytes through pointers.

use std::mem::offset_of;

use cabinetos_protocol::shm::{EntryKind, ListingEntry, ListingHeader, ListingMeta};

use crate::Listing;

/// A section that does not hold a valid listing, or a listing that does not
/// fit the layout.
#[derive(Debug, thiserror::Error)]
#[error("listing section: {0}")]
pub struct LayoutError(String);

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
    pub fn write(&self, out: &mut [u8], generation: u32) -> Result<(), LayoutError> {
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
            (offset_of!(ListingHeader, reserved), 0),
        ];
        for (at, value) in header {
            put(out, at, &value.to_le_bytes());
        }

        let mut name_cursor = 0usize;
        for (index, entry) in self.listing.entries.iter().enumerate() {
            let name = self.listing.name(entry);
            let name_offset = u32::try_from(name_cursor).expect("checked by Layout");

            let at = layout.entries_offset as usize + index * ENTRY_SIZE;
            put(
                out,
                at + offset_of!(ListingEntry, id),
                &entry.id.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingEntry, name_offset),
                &name_offset.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingEntry, name_len),
                &entry.name_len.to_le_bytes(),
            );
            out[at + offset_of!(ListingEntry, kind)] = entry.kind.to_raw();
            out[at + offset_of!(ListingEntry, flags)] = entry.flags;

            let at = layout.meta_offset as usize + index * META_SIZE;
            let meta = &entry.meta;
            put(
                out,
                at + offset_of!(ListingMeta, size),
                &meta.size.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingMeta, modified),
                &meta.modified.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingMeta, created),
                &meta.created.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingMeta, accessed),
                &meta.accessed.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingMeta, attributes),
                &meta.attributes.to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingMeta, reserved),
                &0u32.to_le_bytes(),
            );

            let at = layout.name_arena_offset as usize + name_cursor;
            for (bytes, unit) in out[at..at + name.len() * 2]
                .as_chunks_mut::<2>()
                .0
                .iter_mut()
                .zip(name)
            {
                bytes.copy_from_slice(&unit.to_le_bytes());
            }
            name_cursor += name.len() * 2;
        }
        Ok(())
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
            reserved: field(offset_of!(ListingHeader, reserved))?,
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
        let name_offset =
            read_u32(bytes, at + offset_of!(ListingEntry, name_offset)).ok_or_else(malformed)?;
        let name_len =
            read_u16(bytes, at + offset_of!(ListingEntry, name_len)).ok_or_else(malformed)?;
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
            reserved: read_u32(bytes, at + offset_of!(ListingMeta, reserved))
                .ok_or_else(malformed)?,
        };

        // u32 offset plus twice a u16 length cannot overflow usize on 64 bits.
        let name_start = name_offset as usize;
        let name_end = name_start + usize::from(name_len) * 2;
        if name_end > self.header.name_arena_len as usize {
            return Err(LayoutError(format!(
                "the name of entry {index} lies outside the arena"
            )));
        }
        let arena = self.header.name_arena_offset as usize;
        let name_bytes = bytes
            .get(arena + name_start..arena + name_end)
            .ok_or_else(malformed)?;
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

    /// Every entry, in listing order.
    pub fn entries(&self) -> impl Iterator<Item = Result<EntryView, LayoutError>> + '_ {
        (0..self.len()).map(|index| self.entry(index))
    }
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
                    reserved: 0,
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
        assert_eq!(u32_at(36), 0, "reserved");
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

    #[test]
    fn the_writer_needs_room() {
        let listing = sample();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut too_small = vec![0u8; writer.section_size() - 1];
        assert!(writer.write(&mut too_small, 1).is_err());
    }
}
