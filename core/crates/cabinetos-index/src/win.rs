//! The Windows calls behind the index: elevation, the NTFS volumes, a
//! volume's MFT enumeration and change journal, and a folder's file ID. Every
//! unsafe block states its invariant in a `SAFETY:` comment.

use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use windows::Win32::Foundation::{
    ERROR_ACCESS_DENIED, ERROR_HANDLE_EOF, ERROR_JOURNAL_DELETE_IN_PROGRESS,
    ERROR_JOURNAL_ENTRY_DELETED, ERROR_JOURNAL_NOT_ACTIVE, GENERIC_READ, HANDLE,
};
use windows::Win32::Security::{GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation};
use windows::Win32::Storage::FileSystem::{
    BY_HANDLE_FILE_INFORMATION, CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAGS_AND_ATTRIBUTES,
    FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, GetDriveTypeW,
    GetFileInformationByHandle, GetLogicalDrives, GetVolumeInformationW, OPEN_EXISTING,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::Ioctl::{
    FSCTL_ENUM_USN_DATA, FSCTL_QUERY_USN_JOURNAL, FSCTL_READ_USN_JOURNAL, MFT_ENUM_DATA_V1,
    READ_USN_JOURNAL_DATA_V1, USN_JOURNAL_DATA_V0,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::PCWSTR;

/// `GetDriveTypeW` results for drives that may hold an indexable volume.
const DRIVE_REMOVABLE: u32 = 2;
const DRIVE_FIXED: u32 = 3;
const DRIVE_RAMDISK: u32 = 6;

/// Whether this process runs with Administrator rights (an elevated token).
/// Reading a volume's MFT and change journal needs them.
#[must_use]
pub fn is_elevated() -> bool {
    let mut raw = HANDLE::default();
    // SAFETY: GetCurrentProcess returns a pseudo-handle that needs no
    // closing; `raw` is a valid place for the output handle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw mut raw) }.is_err() {
        return false;
    }
    // SAFETY: OpenProcessToken succeeded, so `raw` is a new handle this
    // function owns; OwnedHandle closes it.
    let token = unsafe { OwnedHandle::from_raw_handle(raw.0) };
    let mut elevation = TOKEN_ELEVATION::default();
    let mut returned = 0u32;
    // SAFETY: the token is open; `elevation` is a valid, correctly sized
    // output; `returned` is a valid output location.
    let queried = unsafe {
        GetTokenInformation(
            HANDLE(token.as_raw_handle()),
            TokenElevation,
            Some((&raw mut elevation).cast()),
            u32::try_from(size_of::<TOKEN_ELEVATION>()).expect("a few bytes"),
            &raw mut returned,
        )
    };
    queried.is_ok() && elevation.TokenIsElevated != 0
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

/// The file system of the volume behind `letter`, such as `NTFS`.
#[must_use]
pub fn file_system(letter: char) -> Option<String> {
    let root = wide(&format!("{letter}:\\"));
    let mut name = [0u16; 64];
    // SAFETY: `root` is NUL-terminated and outlives the call; `name` is a
    // writable buffer whose length the binding passes; the other outputs are
    // not wanted.
    unsafe {
        GetVolumeInformationW(
            PCWSTR(root.as_ptr()),
            None,
            None,
            None,
            None,
            Some(&mut name),
        )
    }
    .ok()?;
    let len = name
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(name.len());
    Some(String::from_utf16_lossy(&name[..len]))
}

/// The drive letters of NTFS volumes on fixed, removable or RAM disks. Network
/// drives and optical drives are never indexed.
#[must_use]
pub fn ntfs_volumes() -> Vec<char> {
    // SAFETY: no arguments; returns a bit mask.
    let mask = unsafe { GetLogicalDrives() };
    (b'A'..=b'Z')
        .filter(|letter| mask & (1 << (letter - b'A')) != 0)
        .map(char::from)
        .filter(|&letter| {
            let root = wide(&format!("{letter}:\\"));
            // SAFETY: `root` is NUL-terminated and outlives the call.
            let kind = unsafe { GetDriveTypeW(PCWSTR(root.as_ptr())) };
            matches!(kind, DRIVE_FIXED | DRIVE_REMOVABLE | DRIVE_RAMDISK)
                && file_system(letter).as_deref() == Some("NTFS")
        })
        .collect()
}

/// The file reference number of the file or folder at `path` (absolute).
pub fn file_id(path: &str) -> windows::core::Result<u64> {
    let verbatim = if path.starts_with(r"\\") {
        wide(path)
    } else {
        wide(&format!(r"\\?\{path}"))
    };
    // SAFETY: `verbatim` is NUL-terminated and outlives the call; reading
    // attributes needs no special right; backup semantics lets a folder open.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(verbatim.as_ptr()),
            FILE_READ_ATTRIBUTES.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    }?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else
    // owns.
    let handle = unsafe { OwnedHandle::from_raw_handle(raw.0) };
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: the handle is open; `info` is a valid output.
    unsafe { GetFileInformationByHandle(HANDLE(handle.as_raw_handle()), &raw mut info) }?;
    Ok((u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow))
}

/// An output buffer for the FSCTLs: 8-byte aligned, as USN records are.
pub(crate) struct Buffer(Vec<u64>);

impl Buffer {
    pub(crate) fn new(bytes: usize) -> Self {
        Self(vec![0; bytes.div_ceil(8)])
    }

    fn capacity(&self) -> u32 {
        u32::try_from(size_of_val(self.0.as_slice())).expect("buffers are a few MiB")
    }

    /// The first `len` bytes the driver wrote.
    fn bytes(&self, len: usize) -> &[u8] {
        let len = len.min(size_of_val(self.0.as_slice()));
        // SAFETY: the pointer covers `size_of_val` initialized bytes of the
        // u64 vector, `len` is no more than that, u8 needs no alignment, and
        // the slice borrows `self`, so the memory outlives it.
        unsafe { std::slice::from_raw_parts(self.0.as_ptr().cast::<u8>(), len) }
    }
}

/// A volume's change journal, as `FSCTL_QUERY_USN_JOURNAL` describes it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Journal {
    /// Changes when the journal is deleted and created again.
    pub(crate) id: u64,
    /// The oldest record still in the journal.
    pub(crate) first_usn: i64,
    /// Where the next record will be written.
    pub(crate) next_usn: i64,
}

/// Why reading the journal failed.
#[derive(Debug)]
pub(crate) enum ReadError {
    /// The records from the requested start were already purged.
    Purged,
    /// The journal was deleted, or is being deleted.
    NotActive,
    /// Anything else.
    Other(windows::core::Error),
}

/// An open NTFS volume, for its MFT and change journal. Needs Administrator
/// rights.
pub(crate) struct Volume {
    handle: OwnedHandle,
}

impl Volume {
    /// Opens `\\.\X:` for reading, shared with every other reader and writer.
    /// Without Administrator rights this fails with `ERROR_ACCESS_DENIED`.
    pub(crate) fn open(letter: char) -> windows::core::Result<Self> {
        let path = wide(&format!(r"\\.\{letter}:"));
        // SAFETY: `path` is NUL-terminated and outlives the call; no security
        // attributes or template handle. Read access to the volume is what
        // NTFS checks for its MFT enumeration and journal reads; the
        // sharing flags leave the volume usable by everyone else.
        let raw = unsafe {
            CreateFileW(
                PCWSTR(path.as_ptr()),
                GENERIC_READ.0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                None,
                OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES(0),
                None,
            )
        }?;
        // SAFETY: CreateFileW succeeded and returned a new handle nothing
        // else owns.
        let handle = unsafe { OwnedHandle::from_raw_handle(raw.0) };
        Ok(Self { handle })
    }

    /// Runs an FSCTL with `input` into `output`; returns the bytes written.
    fn control<T>(
        &self,
        code: u32,
        input: Option<&T>,
        output: &mut Buffer,
    ) -> windows::core::Result<usize> {
        let mut returned = 0u32;
        let (input_pointer, input_size) = match input {
            Some(input) => (
                Some(std::ptr::from_ref(input).cast()),
                u32::try_from(size_of::<T>()).expect("a small input"),
            ),
            None => (None, 0),
        };
        let capacity = output.capacity();
        // SAFETY: the handle is open; `input` (when given) is a valid value
        // of `input_size` bytes; `output` is writable for `capacity` bytes and
        // 8-byte aligned; `returned` is a valid output; the call is
        // synchronous (no OVERLAPPED), so nothing is used after it returns.
        unsafe {
            DeviceIoControl(
                HANDLE(self.handle.as_raw_handle()),
                code,
                input_pointer,
                input_size,
                Some(output.0.as_mut_ptr().cast()),
                capacity,
                Some(&raw mut returned),
                None,
            )
        }?;
        Ok(returned as usize)
    }

    /// The journal's ID and range.
    pub(crate) fn journal(&self) -> windows::core::Result<Journal> {
        let mut output = Buffer::new(size_of::<USN_JOURNAL_DATA_V0>());
        let written = self.control::<()>(FSCTL_QUERY_USN_JOURNAL, None, &mut output)?;
        let bytes = output.bytes(written);
        let field = |at: usize| u64::from_le_bytes(bytes[at..at + 8].try_into().expect("8 bytes"));
        if bytes.len() < 24 {
            return Err(windows::core::Error::from(ERROR_JOURNAL_NOT_ACTIVE));
        }
        // USN_JOURNAL_DATA_V0: UsnJournalID, FirstUsn, NextUsn, …
        Ok(Journal {
            id: field(0),
            first_usn: field(8).cast_signed(),
            next_usn: field(16).cast_signed(),
        })
    }

    /// The next batch of the MFT enumeration from `start` (a file reference
    /// number): the output buffer, which begins with the start of the batch
    /// after it. `None` when the enumeration is done.
    pub(crate) fn enumerate<'a>(
        &self,
        start: u64,
        output: &'a mut Buffer,
    ) -> windows::core::Result<Option<&'a [u8]>> {
        let input = MFT_ENUM_DATA_V1 {
            StartFileReferenceNumber: start,
            LowUsn: 0,
            HighUsn: i64::MAX,
            MinMajorVersion: 2,
            MaxMajorVersion: 3,
        };
        match self.control(FSCTL_ENUM_USN_DATA, Some(&input), output) {
            Ok(written) => Ok(Some(output.bytes(written))),
            Err(error) if error.code() == ERROR_HANDLE_EOF.to_hresult() => Ok(None),
            Err(error) => Err(error),
        }
    }

    /// The journal's records from `start`, waiting up to about a second for
    /// the first one. The buffer begins with the USN to read from next.
    pub(crate) fn read_journal<'a>(
        &self,
        journal: &Journal,
        start: i64,
        output: &'a mut Buffer,
    ) -> Result<&'a [u8], ReadError> {
        let input = READ_USN_JOURNAL_DATA_V1 {
            StartUsn: start,
            ReasonMask: u32::MAX,
            ReturnOnlyOnClose: 0,
            // Return as soon as one record arrives, or when the timeout
            // ends, so the thread can notice it should stop.
            Timeout: 1,
            BytesToWaitFor: 1,
            UsnJournalID: journal.id,
            MinMajorVersion: 2,
            MaxMajorVersion: 3,
        };
        match self.control(FSCTL_READ_USN_JOURNAL, Some(&input), output) {
            Ok(written) => Ok(output.bytes(written)),
            Err(error) if error.code() == ERROR_JOURNAL_ENTRY_DELETED.to_hresult() => {
                Err(ReadError::Purged)
            }
            Err(error)
                if error.code() == ERROR_JOURNAL_NOT_ACTIVE.to_hresult()
                    || error.code() == ERROR_JOURNAL_DELETE_IN_PROGRESS.to_hresult() =>
            {
                Err(ReadError::NotActive)
            }
            Err(error) => Err(ReadError::Other(error)),
        }
    }
}

/// Whether `error` means the caller lacks the rights.
pub(crate) fn is_access_denied(error: &windows::core::Error) -> bool {
    error.code() == ERROR_ACCESS_DENIED.to_hresult()
}

/// Whether `error` means the volume has no change journal.
pub(crate) fn is_no_journal(error: &windows::core::Error) -> bool {
    error.code() == ERROR_JOURNAL_NOT_ACTIVE.to_hresult()
        || error.code() == ERROR_JOURNAL_DELETE_IN_PROGRESS.to_hresult()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_system_drive_is_ntfs_and_listed() {
        let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
        let letter = system.chars().next().unwrap().to_ascii_uppercase();
        assert_eq!(file_system(letter).as_deref(), Some("NTFS"));
        assert!(ntfs_volumes().contains(&letter), "{:?}", ntfs_volumes());
        assert_eq!(file_system('#'), None);
    }

    #[test]
    fn a_folder_has_a_file_id() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().display().to_string();
        let id = file_id(&path).unwrap();
        assert_ne!(id, 0);
        assert_eq!(file_id(&path).unwrap(), id, "stable");
        let root = file_id(&format!("{}\\", &path[..2])).unwrap();
        assert_eq!(root & 0x0000_FFFF_FFFF_FFFF, 5, "the root's segment is 5");
        assert!(file_id(&format!(r"{path}\missing")).is_err());
    }

    #[test]
    fn opening_a_volume_needs_administrator_rights() {
        if is_elevated() {
            // CI runs elevated: the elevated tests cover this path there.
            return;
        }
        let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
        let letter = system.chars().next().unwrap();
        let Err(error) = Volume::open(letter) else {
            panic!("an unelevated process opened the volume for reading");
        };
        assert!(is_access_denied(&error), "{error}");
    }
}
