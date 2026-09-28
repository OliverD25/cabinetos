//! The flags a listing gives an entry beyond its kind: what kind of link it
//! is, and whether its data is on this disk. Only one of them needs more
//! than the enumeration's record: whether a junction's target is a volume
//! (then it is a mount point), which its reparse data says.

use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use cabinetos_protocol::shm::ListingEntry;
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES,
    FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, MAXIMUM_REPARSE_DATA_BUFFER_SIZE,
    OPEN_EXISTING,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::Ioctl::FSCTL_GET_REPARSE_POINT;
use windows::core::PCWSTR;

use crate::{Listing, attributes, path};

/// `IO_REPARSE_TAG_MOUNT_POINT`: junctions and mount points both.
pub(crate) const TAG_MOUNT_POINT: u32 = 0xA000_0003;
/// `IO_REPARSE_TAG_SYMLINK`.
pub(crate) const TAG_SYMLINK: u32 = 0xA000_000C;

/// The link flag for an entry with these attributes and reparse tag. A
/// mount point is flagged a junction here; [`find_mount_points`] tells.
pub(crate) fn flag_of(attrs: u32, reparse_tag: u32) -> u8 {
    if attrs & attributes::REPARSE_POINT == 0 {
        return 0;
    }
    match reparse_tag {
        TAG_MOUNT_POINT => ListingEntry::FLAG_JUNCTION,
        TAG_SYMLINK => ListingEntry::FLAG_SYMBOLIC_LINK,
        _ => 0,
    }
}

/// The "not on this disk" flag for an entry with these attributes.
pub(crate) fn not_on_disk_flag(attrs: u32) -> u8 {
    if attrs & attributes::NOT_ON_DISK == 0 {
        0
    } else {
        ListingEntry::FLAG_NOT_ON_DISK
    }
}

/// Reflags the junctions in `listing` (of the folder `folder`) whose target
/// is a volume as mount points. Each is opened once; a junction that cannot
/// be read keeps its flag.
pub(crate) fn find_mount_points(folder: &str, listing: &mut Listing) {
    let folder = folder.trim_end_matches(['\\', '/']);
    let Listing {
        entries,
        names,
        display,
        ..
    } = listing;
    let mut changed = false;
    for entry in entries
        .iter_mut()
        .filter(|entry| entry.flags & ListingEntry::FLAG_JUNCTION != 0)
    {
        let start = entry.name_start as usize;
        let name = String::from_utf16_lossy(&names[start..start + usize::from(entry.name_len)]);
        if targets_a_volume(&format!("{folder}\\{name}")) == Some(true) {
            entry.flags =
                (entry.flags & !ListingEntry::FLAG_JUNCTION) | ListingEntry::FLAG_MOUNT_POINT;
            changed = true;
        }
    }
    if changed {
        // A copy in display order made before this would hold the old flag.
        *display = std::sync::OnceLock::new();
    }
}

/// Whether the junction at `path` leads to a volume (`\??\Volume{…}\`):
/// a mount point. `None` when its reparse data cannot be read.
fn targets_a_volume(path: &str) -> Option<bool> {
    let data = reparse_data(path)?;
    // REPARSE_DATA_BUFFER: tag, data length, reserved, then the mount point
    // buffer's substitute name offset and length (bytes, after the eight
    // bytes of its four fields).
    let read_u16 = |at: usize| Some(u16::from_le_bytes(data.get(at..at + 2)?.try_into().ok()?));
    let tag = u32::from_le_bytes(data.get(0..4)?.try_into().ok()?);
    if tag != TAG_MOUNT_POINT {
        return Some(false);
    }
    let offset = usize::from(read_u16(8)?);
    let length = usize::from(read_u16(10)?);
    let start = 16 + offset;
    let units: Vec<u16> = data
        .get(start..start + length)?
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u16::from_le_bytes(*pair))
        .collect();
    let substitute = String::from_utf16_lossy(&units);
    Some(
        substitute
            .get(..11)
            .is_some_and(|start| start.eq_ignore_ascii_case(r"\??\Volume{")),
    )
}

/// The reparse data of the link at `path`, which is not followed.
fn reparse_data(path: &str) -> Option<Vec<u8>> {
    let wide = path::verbatim_wide(path).ok()?;
    // SAFETY: `wide` is NUL-terminated and outlives the call; only the
    // link's attributes are asked for, and the link itself is opened.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            FILE_READ_ATTRIBUTES.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    }
    .ok()?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else
    // owns.
    let link = unsafe { OwnedHandle::from_raw_handle(raw.0) };
    let mut data = vec![0u8; MAXIMUM_REPARSE_DATA_BUFFER_SIZE as usize];
    let mut length = 0u32;
    // SAFETY: the handle is open for the call; `data` is writable for its
    // whole length, and `length` receives the bytes written.
    unsafe {
        DeviceIoControl(
            HANDLE(link.as_raw_handle()),
            FSCTL_GET_REPARSE_POINT,
            None,
            0,
            Some(data.as_mut_ptr().cast()),
            MAXIMUM_REPARSE_DATA_BUFFER_SIZE,
            Some(&raw mut length),
            None,
        )
    }
    .ok()?;
    data.truncate(length as usize);
    Some(data)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn data_elsewhere_is_marked_not_on_this_disk() {
        const OFFLINE: u32 = 0x1000;
        const RECALL_ON_OPEN: u32 = 0x4_0000;
        const RECALL_ON_DATA_ACCESS: u32 = 0x40_0000;
        const PINNED: u32 = 0x8_0000;
        const UNPINNED: u32 = 0x10_0000;
        let not_on_disk = ListingEntry::FLAG_NOT_ON_DISK;
        // OneDrive's "online only" file, and its folder whose list of
        // files is still in the cloud.
        assert_eq!(
            not_on_disk_flag(RECALL_ON_DATA_ACCESS | UNPINNED | 0x20),
            not_on_disk
        );
        assert_eq!(not_on_disk_flag(RECALL_ON_OPEN | 0x10), not_on_disk);
        // Moved to other storage by an archiving system.
        assert_eq!(not_on_disk_flag(OFFLINE | 0x20), not_on_disk);
        // Downloaded ("always keep on this device", or just opened): here.
        assert_eq!(not_on_disk_flag(PINNED | 0x20), 0);
        assert_eq!(not_on_disk_flag(UNPINNED | 0x20), 0);
        assert_eq!(not_on_disk_flag(0x20), 0);
    }

    #[test]
    fn only_junctions_and_symbolic_links_get_a_flag() {
        const ONEDRIVE: u32 = 0x9000_601A;
        const APP_ALIAS: u32 = 0x8000_001B;
        let reparse = attributes::REPARSE_POINT;
        assert_eq!(
            flag_of(reparse | 0x10, TAG_MOUNT_POINT),
            ListingEntry::FLAG_JUNCTION
        );
        assert_eq!(
            flag_of(reparse, TAG_SYMLINK),
            ListingEntry::FLAG_SYMBOLIC_LINK
        );
        assert_eq!(flag_of(reparse, ONEDRIVE), 0);
        assert_eq!(flag_of(reparse, APP_ALIAS), 0);
        // Without the attribute the field holds the extended attributes'
        // size, not a tag.
        assert_eq!(flag_of(0x20, TAG_SYMLINK), 0);
    }
}
