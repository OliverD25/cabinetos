//! Facts about the Windows the process runs on, for diagnostics.

use std::fmt::Write as _;

use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::{
    HKEY_LOCAL_MACHINE, RRF_RT_REG_DWORD, RRF_RT_REG_SZ, RegGetValueW,
};
use windows::core::PCWSTR;

const CURRENT_VERSION: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

/// The Windows build, such as `10.0.26200.6899 (25H2)`: version, build,
/// update revision and the release's name, as the registry has them.
/// `None` when the build number cannot be read.
#[must_use]
pub fn windows_build() -> Option<String> {
    let build = read_string("CurrentBuildNumber")?;
    let major = read_dword("CurrentMajorVersionNumber").unwrap_or(10);
    let minor = read_dword("CurrentMinorVersionNumber").unwrap_or(0);
    let mut text = format!("{major}.{minor}.{build}");
    if let Some(revision) = read_dword("UBR") {
        let _ = write!(text, ".{revision}");
    }
    if let Some(release) = read_string("DisplayVersion") {
        let _ = write!(text, " ({release})");
    }
    Some(text)
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn read_dword(value: &str) -> Option<u32> {
    let (key, value) = (wide(CURRENT_VERSION), wide(value));
    let mut data = 0u32;
    let mut size = u32::try_from(size_of::<u32>()).unwrap_or(4);
    // SAFETY: both names are NUL-terminated and outlive the call; `data` and
    // `size` describe a 4-byte buffer for the DWORD.
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
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

fn read_string(value: &str) -> Option<String> {
    let (key, value) = (wide(CURRENT_VERSION), wide(value));
    let mut buffer = vec![0u16; 64];
    let mut size = u32::try_from(buffer.len() * 2).unwrap_or(u32::MAX);
    // SAFETY: both names are NUL-terminated and outlive the call; `buffer`
    // holds `size` bytes, and Windows writes at most that many, NUL
    // included (RRF_RT_REG_SZ), or fails with ERROR_MORE_DATA.
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(key.as_ptr()),
            PCWSTR(value.as_ptr()),
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
    String::from_utf16(&buffer[..end])
        .ok()
        .filter(|text| !text.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_build_of_this_windows_is_read() {
        let build = windows_build().expect("every Windows has a build number");
        let mut parts = build.split(' ').next().unwrap().split('.');
        assert_eq!(parts.next(), Some("10"), "{build}");
        assert_eq!(parts.next(), Some("0"), "{build}");
        let number: u32 = parts.next().unwrap().parse().unwrap();
        assert!(
            number >= 22621,
            "Windows 11 22H2 or later (ADR 0004): {build}"
        );
    }
}
