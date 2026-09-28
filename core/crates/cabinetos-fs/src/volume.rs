//! Which volume and which physical disk a path lives on.
//!
//! The job engine (Phase 4) runs copies on the same physical disk one after
//! another and copies on different disks in parallel, so it needs the disk's
//! number and whether random access is slow (a spinning disk). Everything
//! here works without administrator rights: the disk is opened with no access
//! rights at all, which is enough for property queries.
//!
//! The volume part (mount point, GUID path, file system, label, space) must
//! succeed. The disk part is best effort: when a query fails, that field is
//! `None` and the rest is still returned.

use std::mem::offset_of;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use cabinetos_protocol::{DiskIdentity, VolumeDetails};
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_READ, FILE_SHARE_WRITE, GetDiskFreeSpaceExW,
    GetVolumeInformationW, GetVolumeNameForVolumeMountPointW, GetVolumePathNameW,
    GetVolumePathNamesForVolumeNameW, OPEN_EXISTING,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::Ioctl::{
    DEVICE_SEEK_PENALTY_DESCRIPTOR, IOCTL_STORAGE_GET_DEVICE_NUMBER, IOCTL_STORAGE_QUERY_PROPERTY,
    PropertyStandardQuery, STORAGE_ADAPTER_DESCRIPTOR, STORAGE_DEVICE_DESCRIPTOR,
    STORAGE_DEVICE_NUMBER, STORAGE_PROPERTY_ID, STORAGE_PROPERTY_QUERY, StorageAdapterProperty,
    StorageDeviceProperty, StorageDeviceSeekPenaltyProperty,
};
use windows::core::PCWSTR;

use crate::{FsError, path};

/// The volume that holds `path` and the physical disk under it. `path` does
/// not have to exist; its volume does.
pub fn info_for_path(path: &str) -> Result<VolumeDetails, FsError> {
    let wide = path::absolute_wide(path)?;
    let mount_point = volume_mount_point(path, &wide)?;
    let mount_wide: Vec<u16> = mount_point.encode_utf16().chain([0]).collect();

    let (label, filesystem) = volume_label_and_filesystem(path, &mount_wide)?;
    let (total_bytes, free_bytes) = volume_space(path, &mount_wide)?;
    let volume_guid_path = volume_guid_path(&mount_wide).unwrap_or_default();
    let drive_letter =
        drive_letter_of(&mount_point).or_else(|| first_drive_letter(&volume_guid_path));
    let disk = if volume_guid_path.is_empty() {
        None
    } else {
        disk_identity(&volume_guid_path)
    };

    Ok(VolumeDetails {
        drive_letter,
        volume_guid_path,
        filesystem,
        label,
        total_bytes,
        free_bytes,
        disk,
    })
}

/// The mount point of the volume holding `wide` (an absolute path), in its
/// plain form: `C:\`, `D:\mounted\folder\` or `\\server\share\`.
fn volume_mount_point(path: &str, wide: &[u16]) -> Result<String, FsError> {
    // The mount point is a prefix of the path, so the path's length is enough.
    let mut buffer = vec![0u16; wide.len() + 2];
    // SAFETY: `wide` is NUL-terminated and outlives the call; the binding
    // passes the output buffer's length.
    unsafe { GetVolumePathNameW(PCWSTR(wide.as_ptr()), &mut buffer) }
        .map_err(|error| FsError::from_windows(path, &error))?;
    let mount_point = from_wide(&buffer);
    Ok(plain_form(&mount_point))
}

/// `\\?\C:\` → `C:\` and `\\?\UNC\server\share\` → `\\server\share\`: the
/// volume functions below document only the plain forms.
fn plain_form(verbatim: &str) -> String {
    if let Some(rest) = verbatim.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = verbatim.strip_prefix(r"\\?\") {
        if rest.starts_with("Volume{") {
            verbatim.to_owned()
        } else {
            rest.to_owned()
        }
    } else {
        verbatim.to_owned()
    }
}

fn volume_label_and_filesystem(path: &str, mount: &[u16]) -> Result<(String, String), FsError> {
    let mut label = vec![0u16; 261];
    let mut filesystem = vec![0u16; 261];
    // SAFETY: `mount` is NUL-terminated and outlives the call; the binding
    // passes both output buffers' lengths; the other outputs are not wanted.
    unsafe {
        GetVolumeInformationW(
            PCWSTR(mount.as_ptr()),
            Some(&mut label),
            None,
            None,
            None,
            Some(&mut filesystem),
        )
    }
    .map_err(|error| FsError::from_windows(path, &error))?;
    Ok((from_wide(&label), from_wide(&filesystem)))
}

fn volume_space(path: &str, mount: &[u16]) -> Result<(u64, u64), FsError> {
    let (mut free_to_caller, mut total) = (0u64, 0u64);
    // SAFETY: `mount` is NUL-terminated and outlives the call; the outputs are
    // valid locals.
    unsafe {
        GetDiskFreeSpaceExW(
            PCWSTR(mount.as_ptr()),
            Some(&raw mut free_to_caller),
            Some(&raw mut total),
            None,
        )
    }
    .map_err(|error| FsError::from_windows(path, &error))?;
    Ok((total, free_to_caller))
}

/// `\\?\Volume{…}\`, or `None` for volumes without one (network shares).
fn volume_guid_path(mount: &[u16]) -> Option<String> {
    // 50 characters hold any volume GUID path (documented).
    let mut buffer = vec![0u16; 64];
    // SAFETY: `mount` is NUL-terminated and outlives the call; the binding
    // passes the output buffer's length.
    unsafe { GetVolumeNameForVolumeMountPointW(PCWSTR(mount.as_ptr()), &mut buffer) }.ok()?;
    Some(from_wide(&buffer))
}

/// The letter of a mount point like `C:\`.
fn drive_letter_of(mount_point: &str) -> Option<char> {
    let mut chars = mount_point.chars();
    match (chars.next(), chars.next(), chars.next(), chars.next()) {
        (Some(letter), Some(':'), Some('\\'), None) if letter.is_ascii_alphabetic() => {
            Some(letter.to_ascii_uppercase())
        }
        _ => None,
    }
}

/// A volume mounted in a folder may also have a letter elsewhere.
fn first_drive_letter(volume_guid_path: &str) -> Option<char> {
    if volume_guid_path.is_empty() {
        return None;
    }
    let guid: Vec<u16> = volume_guid_path.encode_utf16().chain([0]).collect();
    let mut buffer = vec![0u16; 1024];
    let mut needed = 0u32;
    // SAFETY: `guid` is NUL-terminated and outlives the call; the binding
    // passes the buffer's length; `needed` is a valid output.
    unsafe {
        GetVolumePathNamesForVolumeNameW(PCWSTR(guid.as_ptr()), Some(&mut buffer), &raw mut needed)
    }
    .ok()?;
    // The result is a list of NUL-terminated strings, ended by an empty one.
    buffer
        .split(|&unit| unit == 0)
        .take_while(|name| !name.is_empty())
        .find_map(|name| drive_letter_of(&String::from_utf16_lossy(name)))
}

/// The physical disk under the volume, from storage queries.
fn disk_identity(volume_guid_path: &str) -> Option<DiskIdentity> {
    // The volume device is the GUID path without its trailing backslash.
    let device = volume_guid_path.trim_end_matches('\\');
    let volume = open_device(device)?;
    let number = device_number(&volume)?;
    // Property queries go to the disk itself; the volume handle is the
    // fallback if the disk cannot be opened.
    let disk = open_device(&format!(r"\\.\PhysicalDrive{number}"));
    let query_handle = disk.as_ref().unwrap_or(&volume);
    let seek_penalty = seek_penalty(query_handle);
    Some(DiskIdentity {
        device_number: number,
        bus_type: bus_type(query_handle).unwrap_or("Unknown").to_owned(),
        seek_penalty,
        media_type: seek_penalty.map(|slow| if slow { "HDD" } else { "SSD" }.to_owned()),
    })
}

/// Opens a device with no access rights: enough for storage queries, and
/// allowed without administrator rights.
fn open_device(name: &str) -> Option<OwnedHandle> {
    let wide: Vec<u16> = name.encode_utf16().chain([0]).collect();
    // SAFETY: `wide` is NUL-terminated and outlives the call; no security
    // attributes or template handle.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES(0),
            None,
        )
    }
    .ok()?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else owns.
    Some(unsafe { OwnedHandle::from_raw_handle(raw.0) })
}

fn device_number(device: &OwnedHandle) -> Option<u32> {
    let mut number = STORAGE_DEVICE_NUMBER::default();
    let size = u32::try_from(size_of::<STORAGE_DEVICE_NUMBER>()).ok()?;
    // SAFETY: the handle is open for the call; the output is a valid local of
    // `size` bytes; the call is synchronous (no OVERLAPPED).
    unsafe {
        DeviceIoControl(
            HANDLE(device.as_raw_handle()),
            IOCTL_STORAGE_GET_DEVICE_NUMBER,
            None,
            0,
            Some((&raw mut number).cast()),
            size,
            None,
            None,
        )
    }
    .ok()?;
    Some(number.DeviceNumber)
}

/// Runs a standard storage property query into `out` (8-byte aligned).
/// Returns the bytes written.
fn query_property(
    device: &OwnedHandle,
    property: STORAGE_PROPERTY_ID,
    out: &mut [u64],
) -> Option<usize> {
    let query = STORAGE_PROPERTY_QUERY {
        PropertyId: property,
        QueryType: PropertyStandardQuery,
        AdditionalParameters: [0],
    };
    let mut written = 0u32;
    let out_size = u32::try_from(size_of_val(out)).ok()?;
    // SAFETY: the handle is open for the call; `query` is a valid input of
    // its size; `out` is writable for `out_size` bytes; `written` is a valid
    // output; the call is synchronous.
    unsafe {
        DeviceIoControl(
            HANDLE(device.as_raw_handle()),
            IOCTL_STORAGE_QUERY_PROPERTY,
            Some((&raw const query).cast()),
            u32::try_from(size_of::<STORAGE_PROPERTY_QUERY>()).ok()?,
            Some(out.as_mut_ptr().cast()),
            out_size,
            Some(&raw mut written),
            None,
        )
    }
    .ok()?;
    Some(written as usize)
}

fn seek_penalty(device: &OwnedHandle) -> Option<bool> {
    let mut out = [0u64; 4];
    let written = query_property(device, StorageDeviceSeekPenaltyProperty, &mut out)?;
    let at = offset_of!(DEVICE_SEEK_PENALTY_DESCRIPTOR, IncursSeekPenalty);
    let bytes = as_bytes(&out, written);
    Some(*bytes.get(at)? != 0)
}

fn bus_type(device: &OwnedHandle) -> Option<&'static str> {
    let mut out = [0u64; 128];
    if let Some(written) = query_property(device, StorageDeviceProperty, &mut out) {
        let at = offset_of!(STORAGE_DEVICE_DESCRIPTOR, BusType);
        let bytes = as_bytes(&out, written);
        if let Some(raw) = bytes.get(at..at + 4) {
            let code = i32::from_le_bytes(raw.try_into().ok()?);
            return Some(bus_type_name(code));
        }
    }
    let written = query_property(device, StorageAdapterProperty, &mut out)?;
    let at = offset_of!(STORAGE_ADAPTER_DESCRIPTOR, BusType);
    Some(bus_type_name(i32::from(*as_bytes(&out, written).get(at)?)))
}

/// The first `len` bytes of an 8-byte-aligned buffer.
fn as_bytes(buffer: &[u64], len: usize) -> Vec<u8> {
    buffer
        .iter()
        .flat_map(|word| word.to_le_bytes())
        .take(len)
        .collect()
}

/// `STORAGE_BUS_TYPE` values as short names.
fn bus_type_name(code: i32) -> &'static str {
    match code {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    }
}

/// A NUL-terminated UTF-16 buffer as a string.
fn from_wide(buffer: &[u16]) -> String {
    let end = buffer
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plain_forms_of_mount_points() {
        assert_eq!(plain_form(r"\\?\C:\"), r"C:\");
        assert_eq!(plain_form(r"\\?\UNC\server\share\"), r"\\server\share\");
        assert_eq!(plain_form(r"C:\"), r"C:\");
        assert_eq!(plain_form(r"\\?\Volume{abc}\"), r"\\?\Volume{abc}\");
    }

    #[test]
    fn drive_letters_only_from_roots() {
        assert_eq!(drive_letter_of(r"C:\"), Some('C'));
        assert_eq!(drive_letter_of(r"h:\"), Some('H'));
        assert_eq!(drive_letter_of(r"D:\mnt\"), None);
        assert_eq!(drive_letter_of(r"\\server\share\"), None);
    }

    #[test]
    fn bus_type_names() {
        assert_eq!(bus_type_name(17), "NVMe");
        assert_eq!(bus_type_name(11), "SATA");
        assert_eq!(bus_type_name(7), "USB");
        assert_eq!(bus_type_name(99), "Unknown");
    }

    #[test]
    fn the_system_drive_has_a_volume_and_a_disk() {
        let windows = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_owned());
        let info = info_for_path(&windows).unwrap();
        assert!(info.drive_letter.is_some());
        assert!(
            info.volume_guid_path.starts_with(r"\\?\Volume{"),
            "{info:?}"
        );
        assert_eq!(info.filesystem, "NTFS");
        assert!(info.total_bytes > 0 && info.free_bytes <= info.total_bytes);
        // CI runners' virtual disks may not report a disk; this PC does.
        if let Some(disk) = &info.disk {
            assert!(!disk.bus_type.is_empty());
        }
    }

    #[test]
    fn a_missing_drive_is_not_found() {
        let Some(letter) = ('D'..='Z')
            .rev()
            .find(|letter| !std::path::Path::new(&format!(r"{letter}:\")).exists())
        else {
            return;
        };
        let error = info_for_path(&format!(r"{letter}:\")).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    }

    #[test]
    fn a_missing_path_on_an_existing_volume_still_has_a_volume() {
        let windows = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_owned());
        let info = info_for_path(&format!(r"{windows}\no\such\folder")).unwrap();
        assert_eq!(info.filesystem, "NTFS");
    }
}
