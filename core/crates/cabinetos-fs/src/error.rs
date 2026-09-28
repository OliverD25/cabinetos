//! Errors of the filesystem engine, mapped from Windows error codes.

use std::io;

/// Why a filesystem operation failed. Each variant maps to one protocol error
/// code (`not_found`, `access_denied`, `invalid_path`, `already_exists`,
/// `io`).
#[derive(Debug, thiserror::Error)]
pub enum FsError {
    /// The path, or a directory on the way to it, does not exist.
    #[error("{path}: not found")]
    NotFound {
        /// The path as the caller gave it.
        path: String,
    },
    /// Windows denied access.
    #[error("{path}: access denied")]
    AccessDenied {
        /// The path as the caller gave it.
        path: String,
    },
    /// The path is malformed, or names a file where a directory is needed.
    #[error("{path}: {reason}")]
    InvalidPath {
        /// The path as the caller gave it.
        path: String,
        /// What is wrong with it.
        reason: String,
    },
    /// Something with that name is already there.
    #[error("{path}: already exists")]
    AlreadyExists {
        /// The path that is taken.
        path: String,
    },
    /// Any other failure reading from the disk or the network.
    #[error("{path}: {source}")]
    Io {
        /// The path as the caller gave it.
        path: String,
        /// The underlying error.
        #[source]
        source: io::Error,
    },
}

/// Win32 error codes that decide the mapping.
mod code {
    pub(super) const FILE_NOT_FOUND: u32 = 2;
    pub(super) const PATH_NOT_FOUND: u32 = 3;
    pub(super) const ACCESS_DENIED: u32 = 5;
    pub(super) const INVALID_DRIVE: u32 = 15;
    pub(super) const BAD_NETPATH: u32 = 53;
    pub(super) const BAD_NET_NAME: u32 = 67;
    pub(super) const FILE_EXISTS: u32 = 80;
    pub(super) const INVALID_NAME: u32 = 123;
    pub(super) const BAD_PATHNAME: u32 = 161;
    pub(super) const ALREADY_EXISTS: u32 = 183;
    pub(super) const FILENAME_TOO_LONG: u32 = 206;
    pub(super) const DIRECTORY: u32 = 267;
    pub(super) const DELETE_PENDING: u32 = 303;
}

impl FsError {
    /// Maps a Win32 error code (`GetLastError`) to an error for `path`.
    #[must_use]
    pub fn from_win32(path: &str, error: u32) -> Self {
        let path = path.to_owned();
        match error {
            code::FILE_NOT_FOUND
            | code::PATH_NOT_FOUND
            | code::INVALID_DRIVE
            | code::BAD_NETPATH
            | code::BAD_NET_NAME
            | code::DELETE_PENDING => Self::NotFound { path },
            code::ACCESS_DENIED => Self::AccessDenied { path },
            code::FILE_EXISTS | code::ALREADY_EXISTS => Self::AlreadyExists { path },
            code::INVALID_NAME | code::BAD_PATHNAME => Self::InvalidPath {
                path,
                reason: "the path is malformed".to_owned(),
            },
            code::FILENAME_TOO_LONG => Self::InvalidPath {
                path,
                reason: "a name in the path is too long".to_owned(),
            },
            code::DIRECTORY => Self::InvalidPath {
                path,
                reason: "not a directory".to_owned(),
            },
            other => Self::Io {
                path,
                source: io::Error::from_raw_os_error(other.cast_signed()),
            },
        }
    }

    /// Maps a `windows` crate error to an error for `path`.
    #[must_use]
    pub fn from_windows(path: &str, error: &windows::core::Error) -> Self {
        let hresult = error.code().0.cast_unsigned();
        // HRESULT_FROM_WIN32 puts the Win32 code in the low 16 bits under
        // facility 7.
        if hresult & 0xFFFF_0000 == 0x8007_0000 {
            Self::from_win32(path, hresult & 0xFFFF)
        } else {
            Self::Io {
                path: path.to_owned(),
                source: io::Error::other(error.message()),
            }
        }
    }

    /// The path the error is about.
    #[must_use]
    pub fn path(&self) -> &str {
        match self {
            Self::NotFound { path }
            | Self::AccessDenied { path }
            | Self::InvalidPath { path, .. }
            | Self::AlreadyExists { path }
            | Self::Io { path, .. } => path,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_win32_codes_to_the_protocol_classes() {
        assert!(matches!(
            FsError::from_win32("x", 2),
            FsError::NotFound { .. }
        ));
        assert!(matches!(
            FsError::from_win32("x", 3),
            FsError::NotFound { .. }
        ));
        assert!(matches!(
            FsError::from_win32("x", 5),
            FsError::AccessDenied { .. }
        ));
        assert!(matches!(
            FsError::from_win32("x", 123),
            FsError::InvalidPath { .. }
        ));
        assert!(matches!(
            FsError::from_win32("x", 267),
            FsError::InvalidPath { .. }
        ));
        assert!(matches!(
            FsError::from_win32("x", 206),
            FsError::InvalidPath { .. }
        ));
        for exists in [80, 183] {
            assert!(matches!(
                FsError::from_win32("x", exists),
                FsError::AlreadyExists { .. }
            ));
        }
        assert!(matches!(FsError::from_win32("x", 21), FsError::Io { .. }));
    }

    #[test]
    fn maps_windows_crate_errors_through_their_win32_code() {
        let not_found = windows::core::Error::from_hresult(windows::core::HRESULT(
            0x8007_0002_u32.cast_signed(),
        ));
        assert!(matches!(
            FsError::from_windows("x", &not_found),
            FsError::NotFound { .. }
        ));
        let other = windows::core::Error::from_hresult(windows::core::HRESULT(
            0x8000_4005_u32.cast_signed(),
        ));
        assert!(matches!(
            FsError::from_windows("x", &other),
            FsError::Io { .. }
        ));
    }
}
