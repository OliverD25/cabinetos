//! Values under the current user's registry hive (`HKEY_CURRENT_USER`):
//! the Settings > Apps entry of a per-user install, which the updater keeps
//! current after a swap (ADR 0014). Nothing here touches another hive: an
//! all-users install is never updated by the core.

use std::io;

use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS, WIN32_ERROR};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_SET_VALUE, REG_DWORD, REG_OPTION_NON_VOLATILE,
    REG_SZ, RRF_RT_REG_DWORD, RRF_RT_REG_SZ, RegCloseKey, RegCreateKeyExW, RegDeleteTreeW,
    RegGetValueW, RegOpenKeyExW, RegSetValueExW,
};
use windows::core::PCWSTR;

/// A value to write.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RegValue<'a> {
    /// `REG_SZ`.
    Text(&'a str),
    /// `REG_DWORD`.
    Number(u32),
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn failed(status: WIN32_ERROR) -> io::Error {
    io::Error::from_raw_os_error(i32::try_from(status.0).unwrap_or(i32::MAX))
}

/// Whether `key` (a path under `HKEY_CURRENT_USER`) exists.
#[must_use]
pub fn user_key_exists(key: &str) -> bool {
    let key = wide(key);
    let mut handle = HKEY::default();
    // SAFETY: the key name is NUL-terminated and outlives the call; `handle`
    // is written only on success, and closed right after.
    let status = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            Some(0),
            KEY_QUERY_VALUE,
            &raw mut handle,
        )
    };
    if status != ERROR_SUCCESS {
        return false;
    }
    // SAFETY: `handle` was opened above and is closed once.
    unsafe {
        let _ = RegCloseKey(handle);
    }
    true
}

/// The text value `name` of `key` under `HKEY_CURRENT_USER`; `None` when
/// the key or the value is missing, or the value is not text.
#[must_use]
pub fn read_user_string(key: &str, name: &str) -> Option<String> {
    let (key, name) = (wide(key), wide(name));
    let mut size = 0u32;
    // SAFETY: both names are NUL-terminated and outlive the call; with no
    // buffer, Windows only writes the size the value needs into `size`.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            PCWSTR(name.as_ptr()),
            RRF_RT_REG_SZ,
            None,
            None,
            Some(&raw mut size),
        )
    };
    if status != ERROR_SUCCESS {
        return None;
    }
    let mut buffer = vec![0u16; (size as usize).div_ceil(2) + 1];
    let mut size = u32::try_from(buffer.len() * 2).unwrap_or(u32::MAX);
    // SAFETY: as above; `buffer` holds `size` bytes, and Windows writes at
    // most that many, NUL included, or fails with ERROR_MORE_DATA.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            PCWSTR(name.as_ptr()),
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr().cast()),
            Some(&raw mut size),
        )
    };
    if status != ERROR_SUCCESS {
        return None;
    }
    let end = buffer
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16(&buffer[..end]).ok()
}

/// The number value `name` of `key` under `HKEY_CURRENT_USER`; `None` when
/// the key or the value is missing, or the value is not a `REG_DWORD`.
#[must_use]
pub fn read_user_number(key: &str, name: &str) -> Option<u32> {
    let (key, name) = (wide(key), wide(name));
    let mut value = 0u32;
    let mut size = 4u32;
    // SAFETY: both names are NUL-terminated and outlive the call; `value`
    // holds the four bytes `size` gives, and RRF_RT_REG_DWORD makes Windows
    // refuse any value that is not exactly a DWORD.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            PCWSTR(name.as_ptr()),
            RRF_RT_REG_DWORD,
            None,
            Some((&raw mut value).cast()),
            Some(&raw mut size),
        )
    };
    (status == ERROR_SUCCESS).then_some(value)
}

/// Writes `values` into `key` under `HKEY_CURRENT_USER`, creating the key
/// (and the keys on its way) when it is missing.
pub fn write_user_values(key: &str, values: &[(&str, RegValue<'_>)]) -> io::Result<()> {
    let key = wide(key);
    let mut handle = HKEY::default();
    // SAFETY: the key name is NUL-terminated and outlives the call; there is
    // no class and no security descriptor; `handle` is written only on
    // success.
    let status = unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            Some(0),
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            None,
            &raw mut handle,
            None,
        )
    };
    if status != ERROR_SUCCESS {
        return Err(failed(status));
    }
    let mut result = Ok(());
    for (name, value) in values {
        let name = wide(name);
        let (kind, bytes) = match value {
            RegValue::Text(text) => (
                REG_SZ,
                wide(text)
                    .iter()
                    .flat_map(|unit| unit.to_le_bytes())
                    .collect::<Vec<u8>>(),
            ),
            RegValue::Number(number) => (REG_DWORD, number.to_le_bytes().to_vec()),
        };
        // SAFETY: `handle` is open with KEY_SET_VALUE; the name is
        // NUL-terminated; `bytes` is the whole value, a NUL-terminated
        // UTF-16 string for REG_SZ or four bytes for REG_DWORD.
        let status =
            unsafe { RegSetValueExW(handle, PCWSTR(name.as_ptr()), Some(0), kind, Some(&bytes)) };
        if status != ERROR_SUCCESS {
            result = Err(failed(status));
            break;
        }
    }
    // SAFETY: `handle` was opened above and is closed once.
    unsafe {
        let _ = RegCloseKey(handle);
    }
    result
}

/// Removes `key` under `HKEY_CURRENT_USER` with everything in it. A key
/// that is not there counts as removed.
pub fn delete_user_key(key: &str) -> io::Result<()> {
    let key = wide(key);
    // SAFETY: the key name is NUL-terminated and outlives the call.
    let status = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(key.as_ptr())) };
    if status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND {
        Ok(())
    } else {
        Err(failed(status))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn values_are_written_read_and_removed_under_a_test_key() {
        // A key of its own, removed whole at the end.
        let key = format!(r"Software\CabinetOS-test-registry-{}", std::process::id());
        assert!(!user_key_exists(&key));
        write_user_values(
            &key,
            &[
                ("DisplayVersion", RegValue::Text("0.2.0-preview.1")),
                ("NoModify", RegValue::Number(1)),
            ],
        )
        .unwrap();
        assert!(user_key_exists(&key));
        assert_eq!(
            read_user_string(&key, "DisplayVersion").as_deref(),
            Some("0.2.0-preview.1")
        );
        assert_eq!(read_user_string(&key, "NoModify"), None, "not text");
        assert_eq!(read_user_string(&key, "Missing"), None);
        assert_eq!(read_user_number(&key, "NoModify"), Some(1));
        assert_eq!(
            read_user_number(&key, "DisplayVersion"),
            None,
            "not a number"
        );
        assert_eq!(read_user_number(&key, "Missing"), None);
        delete_user_key(&key).unwrap();
        assert!(!user_key_exists(&key));
        delete_user_key(&key).unwrap();
    }
}
