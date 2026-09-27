//! Shared-memory layouts of the data channel (brief §4, PLAN §3).
//!
//! For a directory listing the core fills a memory-mapped section with a
//! [`ListingHeader`], an array of [`ListingEntry`] records and a name arena,
//! then hands the UI a duplicated handle. The UI reads the section through a
//! pointer (unsafe C#), with no copy and no serialization.
//!
//! These layouts are a binary contract with C#. The tests below pin every size
//! and field offset, so any change is a deliberate, visible act that must also
//! raise [`ListingHeader::VERSION`].
//!
//! Names in the arena are UTF-16 code units: Windows APIs return UTF-16 and C#
//! strings are UTF-16, so nobody converts anything.
//!
//! Phase 1 only defines the layouts; Phase 2 writes the first listing.

/// The start of a listing section.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ListingHeader {
    /// Always [`ListingHeader::MAGIC`].
    pub magic: u32,
    /// Layout version, [`ListingHeader::VERSION`] when written by this build.
    pub version: u32,
    /// Number of [`ListingEntry`] records in the section.
    pub entry_count: u32,
    /// Byte offset of the name arena from the start of the section.
    pub name_arena_offset: u32,
    /// Length of the name arena in bytes.
    pub name_arena_len: u32,
    /// Raised each time the core rewrites the section, so a reader can tell a
    /// stale view from a fresh one.
    pub generation: u32,
}

impl ListingHeader {
    /// The bytes `b"CBLS"` read as a little-endian `u32`.
    pub const MAGIC: u32 = u32::from_le_bytes(*b"CBLS");
    /// Version of the layout defined in this module.
    pub const VERSION: u32 = 1;
}

/// One entry of a listing: 16 bytes, 8-byte aligned.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ListingEntry {
    /// Identifies the entry within its listing; later metadata updates refer
    /// to it.
    pub id: u64,
    /// Byte offset of the name from the start of the name arena.
    pub name_offset: u32,
    /// Length of the name in UTF-16 code units (not bytes).
    pub name_len: u16,
    /// An [`EntryKind`] value, stored as a raw byte. A byte from shared memory
    /// may hold any value, so it is read through [`EntryKind::from_raw`].
    pub kind: u8,
    /// Reserved for flags. Zero for now.
    pub flags: u8,
}

const _: () = assert!(size_of::<ListingHeader>() == 24);
const _: () = assert!(size_of::<ListingEntry>() == 16);

/// What a [`ListingEntry`] is. The discriminants are the raw byte values
/// stored in [`ListingEntry::kind`].
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum EntryKind {
    /// Not determined, or a value this build does not know.
    Unknown = 0,
    /// A regular file.
    File = 1,
    /// A directory.
    Directory = 2,
    /// A reparse point: a symbolic link or a junction.
    ReparsePoint = 3,
}

impl EntryKind {
    /// Reads a raw byte. Values this build does not know become
    /// [`EntryKind::Unknown`].
    #[must_use]
    pub const fn from_raw(raw: u8) -> Self {
        match raw {
            1 => Self::File,
            2 => Self::Directory,
            3 => Self::ReparsePoint,
            _ => Self::Unknown,
        }
    }

    /// The raw byte stored in [`ListingEntry::kind`].
    #[must_use]
    pub const fn to_raw(self) -> u8 {
        self as u8
    }
}

#[cfg(test)]
mod tests {
    use std::mem::offset_of;

    use super::*;

    #[test]
    fn listing_header_layout_is_pinned() {
        assert_eq!(size_of::<ListingHeader>(), 24);
        assert_eq!(align_of::<ListingHeader>(), 4);
        assert_eq!(offset_of!(ListingHeader, magic), 0);
        assert_eq!(offset_of!(ListingHeader, version), 4);
        assert_eq!(offset_of!(ListingHeader, entry_count), 8);
        assert_eq!(offset_of!(ListingHeader, name_arena_offset), 12);
        assert_eq!(offset_of!(ListingHeader, name_arena_len), 16);
        assert_eq!(offset_of!(ListingHeader, generation), 20);
    }

    #[test]
    fn listing_entry_layout_is_pinned() {
        assert_eq!(size_of::<ListingEntry>(), 16);
        assert_eq!(align_of::<ListingEntry>(), 8);
        assert_eq!(offset_of!(ListingEntry, id), 0);
        assert_eq!(offset_of!(ListingEntry, name_offset), 8);
        assert_eq!(offset_of!(ListingEntry, name_len), 12);
        assert_eq!(offset_of!(ListingEntry, kind), 14);
        assert_eq!(offset_of!(ListingEntry, flags), 15);
    }

    #[test]
    fn magic_is_cbls_in_little_endian() {
        assert_eq!(ListingHeader::MAGIC, 0x534C_4243);
        assert_eq!(ListingHeader::MAGIC.to_le_bytes(), *b"CBLS");
        assert_eq!(ListingHeader::VERSION, 1);
    }

    #[test]
    fn entry_kind_values_are_pinned() {
        let kinds = [
            (EntryKind::Unknown, 0),
            (EntryKind::File, 1),
            (EntryKind::Directory, 2),
            (EntryKind::ReparsePoint, 3),
        ];
        for (kind, raw) in kinds {
            assert_eq!(kind.to_raw(), raw);
            assert_eq!(EntryKind::from_raw(raw), kind);
        }
        assert_eq!(EntryKind::from_raw(200), EntryKind::Unknown);
    }
}
