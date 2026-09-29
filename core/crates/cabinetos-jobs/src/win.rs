//! The file-system calls of the job engine. Each function takes plain paths,
//! turns them into the verbatim form (`\\?\`, so any length works), and
//! returns a Win32 error code on failure, so the rest of the crate stays safe
//! code and decides what an error means.

use std::ffi::c_void;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};

use windows::Win32::Foundation::{FILETIME, GENERIC_WRITE, HANDLE};
use windows::Win32::Storage::FileSystem::{
    COPY_FILE_COPY_SYMLINK, COPY_FILE_FAIL_IF_EXISTS, COPY_FILE_NO_BUFFERING, COPYFILE_FLAGS,
    COPYPROGRESSROUTINE_PROGRESS, CREATE_NEW, CopyFileExW, CreateDirectoryW, CreateFileW,
    DeleteFileW, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY, FILE_CREATION_DISPOSITION,
    FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_FLAGS_AND_ATTRIBUTES,
    FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE,
    FILE_WRITE_ATTRIBUTES, FIND_FIRST_EX_FLAGS, FindClose, FindExInfoBasic, FindExSearchNameMatch,
    FindFirstFileExW, GetFileAttributesExW, GetFileExInfoStandard,
    LPPROGRESS_ROUTINE_CALLBACK_REASON, MAXIMUM_REPARSE_DATA_BUFFER_SIZE, MOVE_FILE_FLAGS,
    MOVEFILE_COPY_ALLOWED, MOVEFILE_REPLACE_EXISTING, MoveFileExW, OPEN_EXISTING, PROGRESS_CANCEL,
    PROGRESS_CONTINUE, RemoveDirectoryW, SetFileAttributesW, SetFileTime,
    WIN32_FILE_ATTRIBUTE_DATA, WIN32_FIND_DATAW,
};
use windows::Win32::System::IO::DeviceIoControl;
use windows::Win32::System::Ioctl::{FSCTL_GET_REPARSE_POINT, FSCTL_SET_REPARSE_POINT};
use windows::core::PCWSTR;

/// A Win32 error code (`GetLastError`).
pub(crate) type Code = u32;

/// The Win32 error codes the engine tells apart.
pub(crate) mod code {
    pub(crate) const FILE_NOT_FOUND: u32 = 2;
    pub(crate) const PATH_NOT_FOUND: u32 = 3;
    pub(crate) const ACCESS_DENIED: u32 = 5;
    pub(crate) const CRC: u32 = 23;
    pub(crate) const SHARING_VIOLATION: u32 = 32;
    pub(crate) const LOCK_VIOLATION: u32 = 33;
    pub(crate) const HANDLE_DISK_FULL: u32 = 39;
    pub(crate) const FILE_EXISTS: u32 = 80;
    pub(crate) const DISK_FULL: u32 = 112;
    pub(crate) const INVALID_NAME: u32 = 123;
    pub(crate) const PRIVILEGE_NOT_HELD: u32 = 1314;
    pub(crate) const DIR_NOT_EMPTY: u32 = 145;
    pub(crate) const ALREADY_EXISTS: u32 = 183;
    pub(crate) const FILENAME_EXCED_RANGE: u32 = 206;
    pub(crate) const REQUEST_ABORTED: u32 = 1235;
}

/// The Win32 code inside a `windows` error: `HRESULT_FROM_WIN32` codes are
/// unwrapped, anything else is returned as the raw `HRESULT`.
pub(crate) fn code_of(error: &windows::core::Error) -> Code {
    let hresult = error.code().0.cast_unsigned();
    if hresult & 0xFFFF_0000 == 0x8007_0000 {
        hresult & 0xFFFF
    } else {
        hresult
    }
}

/// The text Windows gives for `code`.
pub(crate) fn message(code: Code) -> String {
    std::io::Error::from_raw_os_error(code.cast_signed()).to_string()
}

fn wide(path: &str) -> Result<Vec<u16>, Code> {
    cabinetos_fs::verbatim_wide(path).map_err(|_| code::INVALID_NAME)
}

/// Creation, last-access and last-write time, as FILETIME ticks.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Times {
    pub(crate) created: i64,
    pub(crate) accessed: i64,
    pub(crate) modified: i64,
}

/// What `GetFileAttributesExW` says about a path (a link itself, not its
/// target).
#[derive(Clone, Copy, Debug)]
pub(crate) struct Info {
    pub(crate) attributes: u32,
    pub(crate) size: u64,
    pub(crate) times: Times,
}

impl Info {
    pub(crate) fn is_directory(&self) -> bool {
        self.attributes & 0x10 != 0
    }

    pub(crate) fn is_read_only(&self) -> bool {
        self.attributes & FILE_ATTRIBUTE_READONLY.0 != 0
    }
}

fn ticks(time: FILETIME) -> i64 {
    ((u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime)).cast_signed()
}

fn filetime(ticks: i64) -> FILETIME {
    let ticks = ticks.cast_unsigned();
    FILETIME {
        dwLowDateTime: (ticks & 0xFFFF_FFFF) as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    }
}

/// The attributes, size and times of `path`.
pub(crate) fn info(path: &str) -> Result<Info, Code> {
    let wide = wide(path)?;
    let mut data = WIN32_FILE_ATTRIBUTE_DATA::default();
    // SAFETY: `wide` is NUL-terminated and outlives the call; `data` is the
    // output structure GetFileExInfoStandard asks for.
    unsafe {
        GetFileAttributesExW(
            PCWSTR(wide.as_ptr()),
            GetFileExInfoStandard,
            (&raw mut data).cast::<c_void>(),
        )
    }
    .map_err(|error| code_of(&error))?;
    Ok(Info {
        attributes: data.dwFileAttributes,
        size: (u64::from(data.nFileSizeHigh) << 32) | u64::from(data.nFileSizeLow),
        times: Times {
            created: ticks(data.ftCreationTime),
            accessed: ticks(data.ftLastAccessTime),
            modified: ticks(data.ftLastWriteTime),
        },
    })
}

/// Whether anything exists at `path`.
pub(crate) fn exists(path: &str) -> bool {
    info(path).is_ok()
}

/// `path` with its last name spelled as its folder stores it, which may
/// differ in case from the name asked for (`Report.txt` asked, `report.txt`
/// there); `path` itself when nothing is there.
pub(crate) fn spelled(path: &str) -> String {
    let Ok(wide) = wide(path) else {
        return path.to_owned();
    };
    let mut data = WIN32_FIND_DATAW::default();
    // SAFETY: `wide` is NUL-terminated and outlives the call; `data` is the
    // structure FindExInfoBasic fills.
    let found = unsafe {
        FindFirstFileExW(
            PCWSTR(wide.as_ptr()),
            FindExInfoBasic,
            (&raw mut data).cast::<c_void>(),
            FindExSearchNameMatch,
            None,
            FIND_FIRST_EX_FLAGS(0),
        )
    };
    let Ok(search) = found else {
        return path.to_owned();
    };
    // SAFETY: the search handle is open and is not used after this.
    let _ = unsafe { FindClose(search) };
    let end = data
        .cFileName
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(data.cFileName.len());
    let Ok(name) = String::from_utf16(&data.cFileName[..end]) else {
        return path.to_owned();
    };
    match path.rfind(['\\', '/']) {
        Some(separator) => format!("{}{name}", &path[..=separator]),
        None => name,
    }
}

/// What the progress callback tells a running copy.
pub(crate) enum Step {
    Continue,
    Cancel,
}

/// How to copy one file.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct CopyFlags {
    /// Replace an existing destination; without it the copy fails with
    /// `FILE_EXISTS`.
    pub(crate) overwrite: bool,
    /// Bypass the file cache (for huge files).
    pub(crate) unbuffered: bool,
    /// Copy a symbolic link as a link.
    pub(crate) symlink: bool,
}

struct Progress<'a> {
    report: &'a mut dyn FnMut(u64) -> Step,
}

/// Copies `source` to `destination` with `CopyFileExW`. `report` gets the
/// bytes copied so far after each chunk, on this thread, and may block (to
/// pause) or cancel. A cancelled copy fails with `REQUEST_ABORTED`, and
/// Windows deletes the partial destination.
pub(crate) fn copy_file(
    source: &str,
    destination: &str,
    flags: CopyFlags,
    report: &mut dyn FnMut(u64) -> Step,
) -> Result<(), Code> {
    let source = wide(source)?;
    let destination = wide(destination)?;
    let mut copy_flags = COPYFILE_FLAGS(0);
    if !flags.overwrite {
        copy_flags |= COPY_FILE_FAIL_IF_EXISTS;
    }
    if flags.unbuffered {
        copy_flags |= COPY_FILE_NO_BUFFERING;
    }
    if flags.symlink {
        copy_flags |= COPY_FILE_COPY_SYMLINK;
    }
    let mut progress = Progress { report };
    // SAFETY: both paths are NUL-terminated and outlive the call. `progress`
    // outlives the call too, and `on_progress` only runs during it, on this
    // thread, so the pointer it gets is valid and not aliased.
    unsafe {
        CopyFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            Some(on_progress),
            Some((&raw mut progress).cast::<c_void>().cast_const()),
            None,
            copy_flags,
        )
    }
    .map_err(|error| code_of(&error))
}

/// The `CopyFileExW` progress routine: hands the running total to the
/// `Progress` in `data`.
unsafe extern "system" fn on_progress(
    _total_size: i64,
    transferred: i64,
    _stream_size: i64,
    _stream_transferred: i64,
    _stream_number: u32,
    _reason: LPPROGRESS_ROUTINE_CALLBACK_REASON,
    _source: HANDLE,
    _destination: HANDLE,
    data: *const c_void,
) -> COPYPROGRESSROUTINE_PROGRESS {
    // SAFETY: `data` is the `Progress` that `copy_file` passed to
    // CopyFileExW; it lives for the whole call and nothing else touches it
    // while the callback runs.
    let progress = unsafe { &mut *data.cast::<Progress<'_>>().cast_mut() };
    match (progress.report)(u64::try_from(transferred).unwrap_or(0)) {
        Step::Continue => PROGRESS_CONTINUE,
        Step::Cancel => PROGRESS_CANCEL,
    }
}

/// Renames `source` to `destination` on the same volume (a file or a whole
/// folder, in one step).
pub(crate) fn move_file(source: &str, destination: &str, replace: bool) -> Result<(), Code> {
    let source = wide(source)?;
    let destination = wide(destination)?;
    let flags = if replace {
        MOVEFILE_REPLACE_EXISTING
    } else {
        MOVE_FILE_FLAGS(0)
    };
    // SAFETY: both paths are NUL-terminated and outlive the call.
    unsafe { MoveFileExW(PCWSTR(source.as_ptr()), PCWSTR(destination.as_ptr()), flags) }
        .map_err(|error| code_of(&error))
}

/// Moves `source` to `destination`, a file also to another volume (Windows
/// copies it and deletes the source); a folder only on its volume.
pub(crate) fn move_anywhere(source: &str, destination: &str, replace: bool) -> Result<(), Code> {
    let source = wide(source)?;
    let destination = wide(destination)?;
    let flags = if replace {
        MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING
    } else {
        MOVEFILE_COPY_ALLOWED
    };
    // SAFETY: both paths are NUL-terminated and outlive the call.
    unsafe { MoveFileExW(PCWSTR(source.as_ptr()), PCWSTR(destination.as_ptr()), flags) }
        .map_err(|error| code_of(&error))
}

/// Deletes a file, or a link to a file.
pub(crate) fn delete_file(path: &str) -> Result<(), Code> {
    let wide = wide(path)?;
    // SAFETY: `wide` is NUL-terminated and outlives the call.
    unsafe { DeleteFileW(PCWSTR(wide.as_ptr())) }.map_err(|error| code_of(&error))
}

/// Removes an empty folder, or a link to a folder (never what it points to).
pub(crate) fn remove_directory(path: &str) -> Result<(), Code> {
    let wide = wide(path)?;
    // SAFETY: `wide` is NUL-terminated and outlives the call.
    unsafe { RemoveDirectoryW(PCWSTR(wide.as_ptr())) }.map_err(|error| code_of(&error))
}

/// Creates one folder; its parent must exist.
pub(crate) fn create_directory(path: &str) -> Result<(), Code> {
    let wide = wide(path)?;
    // SAFETY: `wide` is NUL-terminated and outlives the call; default
    // security.
    unsafe { CreateDirectoryW(PCWSTR(wide.as_ptr()), None) }.map_err(|error| code_of(&error))
}

/// Sets the attributes of `path`; `0` becomes `FILE_ATTRIBUTE_NORMAL`.
pub(crate) fn set_attributes(path: &str, attributes: u32) -> Result<(), Code> {
    let wide = wide(path)?;
    let attributes = if attributes == 0 {
        FILE_ATTRIBUTE_NORMAL
    } else {
        FILE_FLAGS_AND_ATTRIBUTES(attributes)
    };
    // SAFETY: `wide` is NUL-terminated and outlives the call.
    unsafe { SetFileAttributesW(PCWSTR(wide.as_ptr()), attributes) }
        .map_err(|error| code_of(&error))
}

/// Clears the read-only attribute of `path`, if it is set.
pub(crate) fn clear_read_only(path: &str) -> Result<(), Code> {
    let current = info(path)?;
    if current.is_read_only() {
        set_attributes(path, current.attributes & !FILE_ATTRIBUTE_READONLY.0)
    } else {
        Ok(())
    }
}

fn open(
    path: &str,
    access: u32,
    disposition: FILE_CREATION_DISPOSITION,
    flags: FILE_FLAGS_AND_ATTRIBUTES,
) -> Result<OwnedHandle, Code> {
    let wide = wide(path)?;
    // SAFETY: `wide` is NUL-terminated and outlives the call; no security
    // attributes or template handle.
    let raw = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            disposition,
            flags,
            None,
        )
    }
    .map_err(|error| code_of(&error))?;
    // SAFETY: CreateFileW succeeded and returned a new handle nothing else
    // owns.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw.0) })
}

/// Gives `path` the creation, last-access and last-write times `times`.
pub(crate) fn set_times(path: &str, times: Times, directory: bool) -> Result<(), Code> {
    let flags = if directory {
        FILE_FLAG_BACKUP_SEMANTICS
    } else {
        FILE_FLAGS_AND_ATTRIBUTES(0)
    };
    let handle = open(path, FILE_WRITE_ATTRIBUTES.0, OPEN_EXISTING, flags)?;
    let (created, accessed, modified) = (
        filetime(times.created),
        filetime(times.accessed),
        filetime(times.modified),
    );
    // SAFETY: the handle is open with FILE_WRITE_ATTRIBUTES for the call, and
    // the three FILETIME values outlive it.
    unsafe {
        SetFileTime(
            HANDLE(handle.as_raw_handle()),
            Some(&raw const created),
            Some(&raw const accessed),
            Some(&raw const modified),
        )
    }
    .map_err(|error| code_of(&error))
}

/// Copies a link to a folder (a junction or a directory symbolic link) as a
/// link: a new folder with the same reparse data, so it points where the
/// original points. The target is not copied.
pub(crate) fn copy_directory_link(source: &str, destination: &str) -> Result<(), Code> {
    let data = reparse_data(source)?;
    create_directory(destination)?;
    let written = write_reparse_data(destination, &data);
    if written.is_err() {
        // Our own empty folder; nothing of the user's is lost.
        let _ = remove_directory(destination);
    }
    written
}

/// Copies a symbolic link to a file as a link, the way
/// [`copy_directory_link`] copies one to a folder: a new empty file with
/// the same reparse data. `CopyFileExW` asks for the privilege to create
/// symbolic links; this needs only what Developer Mode grants.
pub(crate) fn copy_file_link(source: &str, destination: &str) -> Result<(), Code> {
    let data = reparse_data(source)?;
    drop(open(
        destination,
        GENERIC_WRITE.0,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL,
    )?);
    let written = write_reparse_data(destination, &data);
    if written.is_err() {
        // Our own empty file; nothing of the user's is lost.
        let _ = delete_file(destination);
    }
    written
}

/// The reparse data of the link at `path`, which is not followed.
fn reparse_data(path: &str) -> Result<Vec<u8>, Code> {
    let link = open(
        path,
        FILE_READ_ATTRIBUTES.0,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
    )?;
    let mut buffer = vec![0u8; MAXIMUM_REPARSE_DATA_BUFFER_SIZE as usize];
    let mut length = 0u32;
    // SAFETY: the handle is open for the call; `buffer` is writable for its
    // whole length, and `length` receives the bytes written.
    unsafe {
        DeviceIoControl(
            HANDLE(link.as_raw_handle()),
            FSCTL_GET_REPARSE_POINT,
            None,
            0,
            Some(buffer.as_mut_ptr().cast::<c_void>()),
            MAXIMUM_REPARSE_DATA_BUFFER_SIZE,
            Some(&raw mut length),
            None,
        )
    }
    .map_err(|error| code_of(&error))?;
    buffer.truncate(length as usize);
    Ok(buffer)
}

/// Gives the file or folder at `path` the reparse data `data`.
fn write_reparse_data(path: &str, data: &[u8]) -> Result<(), Code> {
    let handle = open(
        path,
        GENERIC_WRITE.0,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
    )?;
    let length = u32::try_from(data.len()).map_err(|_| code::INVALID_NAME)?;
    // SAFETY: the handle is open with write access for the call; `data` is
    // the whole reparse buffer read from the original link.
    unsafe {
        DeviceIoControl(
            HANDLE(handle.as_raw_handle()),
            FSCTL_SET_REPARSE_POINT,
            Some(data.as_ptr().cast::<c_void>()),
            length,
            None,
            0,
            None,
            None,
        )
    }
    .map_err(|error| code_of(&error))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unwraps_win32_codes_from_errors() {
        let error = windows::core::Error::from_hresult(windows::core::HRESULT(
            0x8007_0005_u32.cast_signed(),
        ));
        assert_eq!(code_of(&error), code::ACCESS_DENIED);
        let other = windows::core::Error::from_hresult(windows::core::HRESULT(
            0x8027_0024_u32.cast_signed(),
        ));
        assert_eq!(code_of(&other), 0x8027_0024);
    }

    #[test]
    fn filetimes_round_trip() {
        let ticks = 133_000_000_123_456_789;
        assert_eq!(super::ticks(filetime(ticks)), ticks);
    }
}
