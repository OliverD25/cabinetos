//! Shared-memory layouts of the data channel (brief §4, PLAN §3).
//!
//! For a directory listing the core fills a memory-mapped section and hands
//! the UI a duplicated handle. The UI reads the section through a pointer
//! (unsafe C#), with no copy and no serialization. A section holds, in order:
//!
//! 1. a [`ListingHeader`] (40 bytes) at offset 0;
//! 2. `entry_count` [`ListingEntry`] records (16 bytes each) at
//!    `entries_offset`;
//! 3. `entry_count` [`ListingMeta`] records (40 bytes each) at `meta_offset`,
//!    in the same order: entry `i` and metadata `i` describe the same item;
//! 4. the name arena at `name_arena_offset`: file names as UTF-16 code units.
//!
//! All numbers are little-endian. The entries are already sorted as the client
//! asked. Byte diagram: `docs/ipc.md`.
//!
//! These layouts are a binary contract with C#. The tests below pin every size
//! and field offset, so any change is a deliberate, visible act that must also
//! raise [`ListingHeader::VERSION`].
//!
//! Names are UTF-16 because Windows APIs return UTF-16 and C# strings are
//! UTF-16: nobody converts anything.

/// The start of a listing section.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ListingHeader {
    /// Always [`ListingHeader::MAGIC`].
    pub magic: u32,
    /// Layout version, [`ListingHeader::VERSION`] when written by this build.
    pub version: u32,
    /// Number of [`ListingEntry`] records, and of [`ListingMeta`] records.
    pub entry_count: u32,
    /// Byte offset of the name arena from the start of the section.
    pub name_arena_offset: u32,
    /// Length of the name arena in bytes.
    pub name_arena_len: u32,
    /// 1 for a listing's first section, one more for each refresh.
    pub generation: u32,
    /// Byte offset of the [`ListingMeta`] array from the start of the section.
    pub meta_offset: u32,
    /// Byte offset of the [`ListingEntry`] array from the start of the section.
    pub entries_offset: u32,
    /// No flags are defined yet; writers put 0 and readers ignore unknown bits.
    pub flags: u32,
    /// Padding to a multiple of 8 bytes, so the entry array that follows is
    /// 8-byte aligned. Always 0.
    pub reserved: u32,
}

impl ListingHeader {
    /// The bytes `b"CBLS"` read as a little-endian `u32`.
    pub const MAGIC: u32 = u32::from_le_bytes(*b"CBLS");
    /// Version of the layout defined in this module.
    pub const VERSION: u32 = 2;
}

/// One entry of a listing: 16 bytes, 8-byte aligned.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ListingEntry {
    /// Identifies the entry within its listing. On NTFS it is the file
    /// reference number; otherwise see [`ListingEntry::FLAG_ID_IS_NAME_HASH`].
    pub id: u64,
    /// Byte offset of the name from the start of the name arena.
    pub name_offset: u32,
    /// Length of the name in UTF-16 code units (not bytes).
    pub name_len: u16,
    /// An [`EntryKind`] value, stored as a raw byte. A byte from shared memory
    /// may hold any value, so it is read through [`EntryKind::from_raw`].
    pub kind: u8,
    /// Bit flags, see the `FLAG_` constants.
    pub flags: u8,
}

impl ListingEntry {
    /// `id` is not a file reference number (the file system has none) but a
    /// hash of the upper-cased name with the top bit set.
    pub const FLAG_ID_IS_NAME_HASH: u8 = 1;
    /// A junction: a folder that stands for another folder on a local
    /// volume (kind [`EntryKind::ReparsePoint`]).
    pub const FLAG_JUNCTION: u8 = 2;
    /// A symbolic link, to a file or to a folder (the folder attribute says
    /// which); kind [`EntryKind::ReparsePoint`].
    pub const FLAG_SYMBOLIC_LINK: u8 = 4;
    /// A mount point: a folder that stands for a whole volume (kind
    /// [`EntryKind::ReparsePoint`]). Its reparse tag is a junction's; the
    /// target, a volume, tells them apart.
    pub const FLAG_MOUNT_POINT: u8 = 8;
    /// The entry's data is not on this disk: a cloud file not downloaded
    /// (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`), a cloud folder whose list
    /// of files is not fetched yet (`FILE_ATTRIBUTE_RECALL_ON_OPEN`), or a
    /// file moved to other storage (`FILE_ATTRIBUTE_OFFLINE`). Reading it
    /// fetches it; showing it must not.
    pub const FLAG_NOT_ON_DISK: u8 = 16;
}

/// The metadata of one entry: 40 bytes, 8-byte aligned. Times are Windows
/// `FILETIME` ticks: 100-nanosecond intervals since 1601-01-01 UTC.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ListingMeta {
    /// File size in bytes (end of file); 0 for directories.
    pub size: u64,
    /// Last write time.
    pub modified: i64,
    /// Creation time.
    pub created: i64,
    /// Last access time.
    pub accessed: i64,
    /// Windows file attributes (`FILE_ATTRIBUTE_*`).
    pub attributes: u32,
    /// The reparse tag (`IO_REPARSE_TAG_*`) of a reparse point: which kind
    /// of link it is, or which other kind of reparse point (a cloud file,
    /// a compressed system file). 0 for any other entry. These bytes were
    /// reserved and always 0 before, so a reader that ignores them misses
    /// nothing else.
    pub reparse_tag: u32,
}

const _: () = assert!(size_of::<ListingHeader>() == 40);
const _: () = assert!(size_of::<ListingEntry>() == 16);
const _: () = assert!(size_of::<ListingMeta>() == 40);

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
    /// A link: a symbolic link or a junction (a name-surrogate reparse point).
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
        assert_eq!(size_of::<ListingHeader>(), 40);
        assert_eq!(align_of::<ListingHeader>(), 4);
        assert_eq!(offset_of!(ListingHeader, magic), 0);
        assert_eq!(offset_of!(ListingHeader, version), 4);
        assert_eq!(offset_of!(ListingHeader, entry_count), 8);
        assert_eq!(offset_of!(ListingHeader, name_arena_offset), 12);
        assert_eq!(offset_of!(ListingHeader, name_arena_len), 16);
        assert_eq!(offset_of!(ListingHeader, generation), 20);
        assert_eq!(offset_of!(ListingHeader, meta_offset), 24);
        assert_eq!(offset_of!(ListingHeader, entries_offset), 28);
        assert_eq!(offset_of!(ListingHeader, flags), 32);
        assert_eq!(offset_of!(ListingHeader, reserved), 36);
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
    fn listing_meta_layout_is_pinned() {
        assert_eq!(size_of::<ListingMeta>(), 40);
        assert_eq!(align_of::<ListingMeta>(), 8);
        assert_eq!(offset_of!(ListingMeta, size), 0);
        assert_eq!(offset_of!(ListingMeta, modified), 8);
        assert_eq!(offset_of!(ListingMeta, created), 16);
        assert_eq!(offset_of!(ListingMeta, accessed), 24);
        assert_eq!(offset_of!(ListingMeta, attributes), 32);
        assert_eq!(offset_of!(ListingMeta, reparse_tag), 36);
    }

    #[test]
    fn magic_is_cbls_in_little_endian() {
        assert_eq!(ListingHeader::MAGIC, 0x534C_4243);
        assert_eq!(ListingHeader::MAGIC.to_le_bytes(), *b"CBLS");
        assert_eq!(ListingHeader::VERSION, 2);
    }

    #[test]
    fn flags_are_pinned() {
        assert_eq!(ListingEntry::FLAG_ID_IS_NAME_HASH, 1);
        assert_eq!(ListingEntry::FLAG_JUNCTION, 2);
        assert_eq!(ListingEntry::FLAG_SYMBOLIC_LINK, 4);
        assert_eq!(ListingEntry::FLAG_MOUNT_POINT, 8);
        assert_eq!(ListingEntry::FLAG_NOT_ON_DISK, 16);
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
