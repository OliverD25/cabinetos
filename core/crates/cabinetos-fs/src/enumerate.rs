//! Reading a directory with `NtQueryDirectoryFile`.
//!
//! One call fills a buffer with many `FILE_ID_FULL_DIR_INFORMATION` records:
//! name, file reference number, size, times and attributes. So the whole
//! listing, metadata included, comes from a single pass over the directory.
//!
//! `FileIdFullDirectoryInformation` rather than `FileIdBothDirectoryInformation`:
//! the "both" class also returns the 8.3 short name, which NTFS looks up for
//! every entry. Measured on 100,000 files, warm: 46 ms instead of 100 ms, for
//! a field nothing uses.
//!
//! The unsafe code is limited to opening the directory, the system call, and
//! viewing the filled buffer as bytes. Parsing the records is safe,
//! bounds-checked code.

use std::mem::offset_of;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use cabinetos_protocol::shm::{EntryKind, ListingEntry, ListingMeta};
use windows::Wdk::Storage::FileSystem::{
    FILE_ID_FULL_DIR_INFORMATION, FileIdFullDirectoryInformation, NtQueryDirectoryFile,
};
use windows::Win32::Foundation::{
    HANDLE, NTSTATUS, RtlNtStatusToDosError, STATUS_NO_MORE_FILES, STATUS_NO_SUCH_FILE,
};
use windows::Win32::Storage::FileSystem::{
    BY_HANDLE_FILE_INFORMATION, CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_LIST_DIRECTORY,
    FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, GetFileInformationByHandle,
    OPEN_EXISTING, SYNCHRONIZE,
};
use windows::Win32::System::IO::IO_STATUS_BLOCK;
use windows::core::PCWSTR;

use crate::{Entry, FsError, Listing, attributes, link, path};

/// `STATUS_PENDING`: never expected on a handle opened for synchronous I/O.
const STATUS_PENDING: NTSTATUS = NTSTATUS(0x103);

/// Smallest buffer accepted: one record with a 255-character name needs
/// about 600 bytes; 4 KiB leaves room for several.
const MIN_BUFFER: usize = 4096;

/// Byte offsets of the fields read from each record.
mod field {
    use super::{FILE_ID_FULL_DIR_INFORMATION as Info, offset_of};
    pub(super) const NEXT_ENTRY: usize = offset_of!(Info, NextEntryOffset);
    pub(super) const CREATION_TIME: usize = offset_of!(Info, CreationTime);
    pub(super) const LAST_ACCESS_TIME: usize = offset_of!(Info, LastAccessTime);
    pub(super) const LAST_WRITE_TIME: usize = offset_of!(Info, LastWriteTime);
    pub(super) const END_OF_FILE: usize = offset_of!(Info, EndOfFile);
    pub(super) const FILE_ATTRIBUTES: usize = offset_of!(Info, FileAttributes);
    pub(super) const FILE_NAME_LENGTH: usize = offset_of!(Info, FileNameLength);
    /// Holds the reparse tag when the entry is a reparse point.
    pub(super) const EA_SIZE: usize = offset_of!(Info, EaSize);
    pub(super) const FILE_ID: usize = offset_of!(Info, FileId);
    pub(super) const FILE_NAME: usize = offset_of!(Info, FileName);
}

/// Reads every entry of the directory at `path`, in the order the file
/// system returns them. `.` and `..` are skipped; hidden and system entries
/// only when `include_hidden` is false.
pub(crate) fn read_directory(
    path: &str,
    include_hidden: bool,
    buffer_size: usize,
) -> Result<Listing, FsError> {
    let directory = open(path)?;
    let mut buffer = Buffer::new(buffer_size);
    let mut listing = Listing::default();
    let mut first = true;
    loop {
        let written = buffer.fill(path, &directory, first)?;
        first = false;
        if written == 0 {
            break;
        }
        parse_records(path, buffer.bytes(written), include_hidden, &mut listing)?;
    }
    Ok(listing)
}

/// Opens the directory at `path` for listing.
pub(crate) fn open(path: &str) -> Result<OwnedHandle, FsError> {
    let wide = path::verbatim_wide(path)?;
    open_directory(path, &wide)
}

/// Room for the records of one `NtQueryDirectoryFile` call, 8-byte aligned
/// as the records need.
pub(crate) struct Buffer(Vec<u64>);

impl Buffer {
    /// A buffer of `size` bytes (at least 4 KiB).
    pub(crate) fn new(size: usize) -> Self {
        Self(vec![0; size.max(MIN_BUFFER).div_ceil(8)])
    }

    fn capacity(&self) -> usize {
        self.0.len() * 8
    }

    /// Fills the buffer with the next records of `directory` (from the
    /// start when `first`); returns how many bytes it holds, 0 at the end
    /// of the directory.
    pub(crate) fn fill(
        &mut self,
        path: &str,
        directory: &OwnedHandle,
        first: bool,
    ) -> Result<usize, FsError> {
        let capacity = self.capacity();
        let len = u32::try_from(capacity).unwrap_or(u32::MAX);
        let mut status_block = IO_STATUS_BLOCK::default();
        // SAFETY: `directory` is an open directory handle for synchronous I/O
        // (no FILE_FLAG_OVERLAPPED), so the call completes before it returns
        // and nothing refers to `status_block` or the buffer afterwards. The
        // buffer is 8-byte aligned (u64 elements) and writable for `len`
        // bytes, which never exceeds its size.
        let status = unsafe {
            NtQueryDirectoryFile(
                HANDLE(directory.as_raw_handle()),
                None,
                None,
                None,
                &raw mut status_block,
                self.0.as_mut_ptr().cast(),
                len,
                FileIdFullDirectoryInformation,
                false,
                None,
                first,
            )
        };
        if status == STATUS_NO_MORE_FILES || status == STATUS_NO_SUCH_FILE {
            return Ok(0);
        }
        if status.0 < 0 || status == STATUS_PENDING {
            // SAFETY: RtlNtStatusToDosError only maps a number to a number.
            let code = unsafe { RtlNtStatusToDosError(status) };
            return Err(FsError::from_win32(path, code));
        }
        Ok(status_block.Information.min(capacity))
    }

    /// The first `len` bytes, as the last [`fill`](Self::fill) left them.
    pub(crate) fn bytes(&self, len: usize) -> &[u8] {
        let len = len.min(self.capacity());
        // SAFETY: the buffer is initialized memory (zeroed at allocation,
        // partly overwritten by the kernel) of at least `len` bytes, and a
        // byte slice has no alignment needs.
        unsafe { std::slice::from_raw_parts(self.0.as_ptr().cast::<u8>(), len) }
    }
}

/// Opens `wide` (verbatim, NUL-terminated) for listing and checks that it is a
/// directory.
fn open_directory(path: &str, wide: &[u16]) -> Result<OwnedHandle, FsError> {
    // SAFETY: `wide` is a NUL-terminated UTF-16 string that outlives the call;
    // no security attributes or template handle are passed.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            FILE_LIST_DIRECTORY.0 | SYNCHRONIZE.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    }
    .map_err(|error| FsError::from_windows(path, &error))?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else owns.
    let handle = unsafe { OwnedHandle::from_raw_handle(raw.0) };

    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: the handle is open for the call and `info` is a valid output.
    unsafe { GetFileInformationByHandle(HANDLE(handle.as_raw_handle()), &raw mut info) }
        .map_err(|error| FsError::from_windows(path, &error))?;
    if info.dwFileAttributes & attributes::DIRECTORY == 0 {
        return Err(FsError::InvalidPath {
            path: path.to_owned(),
            reason: "not a directory".to_owned(),
        });
    }
    Ok(handle)
}

/// Appends the records in `bytes` (one filled buffer) to `listing`.
pub(crate) fn parse_records(
    path: &str,
    bytes: &[u8],
    include_hidden: bool,
    listing: &mut Listing,
) -> Result<(), FsError> {
    let malformed = || FsError::Io {
        path: path.to_owned(),
        source: std::io::Error::other("the file system returned malformed directory data"),
    };
    let mut offset = 0usize;
    loop {
        let record = bytes.get(offset..).ok_or_else(malformed)?;
        let name_bytes = read_u32(record, field::FILE_NAME_LENGTH).ok_or_else(malformed)? as usize;
        let name = record
            .get(field::FILE_NAME..field::FILE_NAME + name_bytes)
            .ok_or_else(malformed)?;
        let next = read_u32(record, field::NEXT_ENTRY).ok_or_else(malformed)?;
        let attrs = read_u32(record, field::FILE_ATTRIBUTES).ok_or_else(malformed)?;

        if !is_dot_entry(name) && (include_hidden || attrs & attributes::HIDDEN_OR_SYSTEM == 0) {
            let meta = ListingMeta {
                size: read_i64(record, field::END_OF_FILE)
                    .ok_or_else(malformed)?
                    .max(0)
                    .cast_unsigned(),
                modified: read_i64(record, field::LAST_WRITE_TIME).ok_or_else(malformed)?,
                created: read_i64(record, field::CREATION_TIME).ok_or_else(malformed)?,
                accessed: read_i64(record, field::LAST_ACCESS_TIME).ok_or_else(malformed)?,
                attributes: attrs,
                reparse_tag: 0,
            };
            // For a reparse point the extended attributes' size field holds
            // the reparse tag instead (`FILE_ID_FULL_DIR_INFORMATION`).
            let tag_or_size = read_u32(record, field::EA_SIZE).ok_or_else(malformed)?;
            let reparse_tag = if attrs & attributes::REPARSE_POINT != 0 {
                tag_or_size
            } else {
                0
            };
            let file_id = read_i64(record, field::FILE_ID).ok_or_else(malformed)?;
            listing
                .push(
                    name.as_chunks::<2>()
                        .0
                        .iter()
                        .map(|pair| u16::from_le_bytes(*pair)),
                    file_id.cast_unsigned(),
                    kind_of(attrs, reparse_tag),
                    ListingMeta {
                        reparse_tag,
                        ..meta
                    },
                    link::flag_of(attrs, reparse_tag),
                )
                .map_err(|()| malformed())?;
        }

        if next == 0 {
            return Ok(());
        }
        offset += next as usize;
    }
}

fn read_u32(bytes: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_le_bytes(bytes.get(at..at + 4)?.try_into().ok()?))
}

fn read_i64(bytes: &[u8], at: usize) -> Option<i64> {
    Some(i64::from_le_bytes(bytes.get(at..at + 8)?.try_into().ok()?))
}

/// `.` and `..`, as raw UTF-16LE bytes.
fn is_dot_entry(name: &[u8]) -> bool {
    matches!(name, [b'.', 0] | [b'.', 0, b'.', 0])
}

/// The entry kind from its attributes. Only name-surrogate reparse points
/// (symbolic links, junctions, WSL links) count as links; other reparse
/// points, such as OneDrive placeholders or deduplicated files, are ordinary
/// files and directories to the user.
pub(crate) fn kind_of(attrs: u32, reparse_tag: u32) -> EntryKind {
    /// `IsReparseTagNameSurrogate`: bit 29 of the tag.
    const NAME_SURROGATE: u32 = 0x2000_0000;
    if attrs & attributes::REPARSE_POINT != 0 && reparse_tag & NAME_SURROGATE != 0 {
        EntryKind::ReparsePoint
    } else if attrs & attributes::DIRECTORY != 0 {
        EntryKind::Directory
    } else {
        EntryKind::File
    }
}

/// The ID for an entry whose file system reports no file ID (FAT, some
/// network shares): a 64-bit FNV-1a hash of the upper-cased name, top bit
/// set. Returns the ID and the entry flags to store with it.
pub(crate) fn fallback_id(name: &[u16]) -> (u64, u8) {
    const OFFSET_BASIS: u64 = 0xCBF2_9CE4_8422_2325;
    const PRIME: u64 = 0x0000_0100_0000_01B3;
    let mut hash = OFFSET_BASIS;
    for &unit in name {
        let upper = char::from_u32(u32::from(unit))
            .and_then(|c| {
                let mut upper = c.to_uppercase();
                match (upper.next(), upper.next()) {
                    (Some(single), None) => u16::try_from(u32::from(single)).ok(),
                    _ => None,
                }
            })
            .unwrap_or(unit);
        for byte in upper.to_le_bytes() {
            hash ^= u64::from(byte);
            hash = hash.wrapping_mul(PRIME);
        }
    }
    (hash | (1 << 63), ListingEntry::FLAG_ID_IS_NAME_HASH)
}

impl Listing {
    /// Adds one entry. `Err` when the name does not fit the layout (more than
    /// 65,535 UTF-16 units) or the names outgrow 4 GiB.
    fn push(
        &mut self,
        name: impl Iterator<Item = u16>,
        file_id: u64,
        kind: EntryKind,
        meta: ListingMeta,
        link_flag: u8,
    ) -> Result<(), ()> {
        let start = self.names.len();
        self.names.extend(name);
        let len = self.names.len() - start;
        let (Ok(name_start), Ok(name_len)) = (u32::try_from(start), u16::try_from(len)) else {
            self.names.truncate(start);
            return Err(());
        };
        let (id, flags) = if file_id == 0 {
            fallback_id(&self.names[start..])
        } else {
            (file_id, 0)
        };
        self.entries.push(Entry {
            id,
            kind,
            flags: flags | link_flag,
            meta,
            name_start,
            name_len,
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_fields_are_where_windows_puts_them() {
        // FILE_ID_FULL_DIR_INFORMATION as documented; a changed binding
        // would silently misread every record.
        assert_eq!(field::NEXT_ENTRY, 0);
        assert_eq!(field::CREATION_TIME, 8);
        assert_eq!(field::LAST_ACCESS_TIME, 16);
        assert_eq!(field::LAST_WRITE_TIME, 24);
        assert_eq!(field::END_OF_FILE, 40);
        assert_eq!(field::FILE_ATTRIBUTES, 56);
        assert_eq!(field::FILE_NAME_LENGTH, 60);
        assert_eq!(field::EA_SIZE, 64);
        assert_eq!(field::FILE_ID, 72);
        assert_eq!(field::FILE_NAME, 80);
    }

    #[test]
    fn classifies_kinds() {
        const SYMLINK: u32 = 0xA000_000C;
        const JUNCTION: u32 = 0xA000_0003;
        const ONEDRIVE: u32 = 0x9000_601A;
        let dir = attributes::DIRECTORY;
        let reparse = attributes::REPARSE_POINT;
        assert_eq!(kind_of(0x20, 0), EntryKind::File);
        assert_eq!(kind_of(dir, 0), EntryKind::Directory);
        assert_eq!(kind_of(dir | reparse, JUNCTION), EntryKind::ReparsePoint);
        assert_eq!(kind_of(reparse, SYMLINK), EntryKind::ReparsePoint);
        assert_eq!(kind_of(dir | reparse, ONEDRIVE), EntryKind::Directory);
        assert_eq!(kind_of(reparse, ONEDRIVE), EntryKind::File);
    }

    #[test]
    fn fallback_ids_ignore_case_and_are_marked() {
        let units = |s: &str| s.encode_utf16().collect::<Vec<u16>>();
        let (lower, flags) = fallback_id(&units("readme.txt"));
        let (upper, _) = fallback_id(&units("README.TXT"));
        let (other, _) = fallback_id(&units("readme.md"));
        assert_eq!(lower, upper);
        assert_ne!(lower, other);
        assert_eq!(lower >> 63, 1);
        assert_eq!(flags, ListingEntry::FLAG_ID_IS_NAME_HASH);
    }

    #[test]
    fn recognizes_dot_entries() {
        assert!(is_dot_entry(&[b'.', 0]));
        assert!(is_dot_entry(&[b'.', 0, b'.', 0]));
        assert!(!is_dot_entry(&[b'.', 0, b'a', 0]));
        assert!(!is_dot_entry(&[b'.', 0, b'.', 0, b'.', 0]));
    }
}
