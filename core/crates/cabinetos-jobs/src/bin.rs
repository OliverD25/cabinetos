//! Whether the Recycle Bin can take an item, found out before the shell is
//! asked. With `FOF_NOCONFIRMATION`, the shell deletes for good, without a
//! word, whatever its bin cannot take: an item bigger than the bin, any item
//! on a drive whose bin is turned off, any item on a drive without a bin
//! (removable and network drives). The engine checks first and raises a
//! `recycle_bin_too_small` conflict instead, so nothing is destroyed that
//! the user meant to keep recoverable.
//!
//! Only reads: the drive type, whether the shell has a bin there
//! (`SHQueryRecycleBinW`), and the bin's settings in the user's registry
//! (`HKCU\...\Explorer\BitBucket\Volume\{GUID}`: `MaxCapacity` in MiB,
//! `NukeOnDelete`).

use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::Storage::FileSystem::GetDriveTypeW;
use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
use windows::Win32::UI::Shell::{SHQUERYRBINFO, SHQueryRecycleBinW};
use windows::core::PCWSTR;

/// `DRIVE_FIXED`: only fixed drives have a Recycle Bin.
const DRIVE_FIXED: u32 = 3;

/// Without a size on record, a volume's bin may use this share of it (the
/// Windows default).
const DEFAULT_SHARE_PERCENT: u64 = 5;

const BIT_BUCKET: &str = r"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";

/// What the Recycle Bin of a volume can take.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Capacity {
    /// Nothing: the drive has no bin, or its bin is turned off.
    Nothing,
    /// Items up to this many bytes.
    Bytes(u64),
}

impl Capacity {
    /// Whether an item of `size` bytes goes to the bin rather than being
    /// deleted for good.
    pub(crate) fn fits(self, size: u64) -> bool {
        match self {
            Self::Nothing => false,
            Self::Bytes(limit) => size <= limit,
        }
    }
}

/// What Windows says about the bin of one volume.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct Facts {
    pub(crate) fixed_drive: bool,
    pub(crate) bin_available: bool,
    pub(crate) nuke_on_delete: bool,
    /// `MaxCapacity` in MiB, when the user's registry has it.
    pub(crate) max_capacity_mib: Option<u32>,
    pub(crate) volume_bytes: u64,
}

/// The capacity those facts give.
pub(crate) fn capacity_from(facts: &Facts) -> Capacity {
    if !facts.fixed_drive || !facts.bin_available || facts.nuke_on_delete {
        return Capacity::Nothing;
    }
    Capacity::Bytes(match facts.max_capacity_mib {
        Some(mib) => u64::from(mib) * 1024 * 1024,
        None => facts.volume_bytes / 100 * DEFAULT_SHARE_PERCENT,
    })
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

/// The capacity of the Recycle Bin of the volume `path` is on. A volume
/// Windows cannot describe (a network share) has no bin.
pub(crate) fn capacity(path: &str) -> Capacity {
    let Ok(volume) = cabinetos_fs::volume::info_for_path(path) else {
        return Capacity::Nothing;
    };
    let root = volume.drive_letter.map_or_else(
        || volume.volume_guid_path.clone(),
        |letter| format!("{letter}:\\"),
    );
    let root = wide(&root);
    // SAFETY: `root` is NUL-terminated and outlives the call.
    let fixed_drive = unsafe { GetDriveTypeW(PCWSTR(root.as_ptr())) } == DRIVE_FIXED;
    let mut info = SHQUERYRBINFO {
        cbSize: u32::try_from(size_of::<SHQUERYRBINFO>()).unwrap_or(u32::MAX),
        i64Size: 0,
        i64NumItems: 0,
    };
    // SAFETY: `root` is NUL-terminated and outlives the call; `info` has its
    // size set as the call requires.
    let bin_available = unsafe { SHQueryRecycleBinW(PCWSTR(root.as_ptr()), &raw mut info) }.is_ok();
    let key = volume
        .volume_guid_path
        .find('{')
        .and_then(|start| {
            let end = volume.volume_guid_path[start..].find('}')?;
            Some(format!(
                "{BIT_BUCKET}{}",
                &volume.volume_guid_path[start..=start + end]
            ))
        })
        .unwrap_or_default();
    let (nuke_on_delete, max_capacity_mib) = if key.is_empty() {
        (false, None)
    } else {
        (
            read_dword(&key, "NukeOnDelete") == Some(1),
            read_dword(&key, "MaxCapacity"),
        )
    };
    capacity_from(&Facts {
        fixed_drive,
        bin_available,
        nuke_on_delete,
        max_capacity_mib,
        volume_bytes: volume.total_bytes,
    })
}

/// A `REG_DWORD` value under `HKEY_CURRENT_USER`, if there is one.
fn read_dword(key: &str, value: &str) -> Option<u32> {
    let (key, value) = (wide(key), wide(value));
    let mut data = 0u32;
    let mut size = u32::try_from(size_of::<u32>()).unwrap_or(4);
    // SAFETY: both names are NUL-terminated and outlive the call; `data` and
    // `size` describe a 4-byte buffer for the DWORD.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            PCWSTR(value.as_ptr()),
            RRF_RT_REG_DWORD,
            None,
            Some((&raw mut data).cast()),
            Some(&raw mut size),
        )
    };
    (status == ERROR_SUCCESS).then_some(data)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn facts() -> Facts {
        Facts {
            fixed_drive: true,
            bin_available: true,
            nuke_on_delete: false,
            max_capacity_mib: Some(100),
            volume_bytes: 1_000_000_000_000,
        }
    }

    #[test]
    fn the_bin_takes_what_fits_its_size() {
        let capacity = capacity_from(&facts());
        assert_eq!(capacity, Capacity::Bytes(100 * 1024 * 1024));
        assert!(capacity.fits(100 * 1024 * 1024));
        assert!(!capacity.fits(100 * 1024 * 1024 + 1));
    }

    #[test]
    fn without_a_size_on_record_the_bin_is_five_percent() {
        let capacity = capacity_from(&Facts {
            max_capacity_mib: None,
            ..facts()
        });
        assert_eq!(capacity, Capacity::Bytes(50_000_000_000));
    }

    #[test]
    fn no_bin_takes_nothing() {
        for facts in [
            Facts {
                fixed_drive: false,
                ..facts()
            },
            Facts {
                bin_available: false,
                ..facts()
            },
            Facts {
                nuke_on_delete: true,
                ..facts()
            },
        ] {
            assert_eq!(capacity_from(&facts), Capacity::Nothing, "{facts:?}");
            assert!(!capacity_from(&facts).fits(1));
        }
    }

    #[test]
    fn this_machines_temp_folder_has_a_bin() {
        // Only reads: the drive type, the bin and its registry settings. A
        // CI runner's account may have no bin at all, so there it is only
        // asked, not checked.
        let temp = std::env::temp_dir();
        if std::env::var_os("CI").is_some() {
            let _ = capacity(temp.to_str().unwrap());
            return;
        }
        let capacity = capacity(temp.to_str().unwrap());
        assert!(
            matches!(capacity, Capacity::Bytes(bytes) if bytes > 0),
            "{capacity:?}"
        );
    }
}
