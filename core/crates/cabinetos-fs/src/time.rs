//! Showing Windows file times as local calendar time, for Rust clients such as
//! the CLI. (The C# UI formats times itself.)

use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
use windows::Win32::System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime};

/// A local calendar time, to the second.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LocalTime {
    /// Year, for example 2026.
    pub year: u16,
    /// Month, 1 to 12.
    pub month: u16,
    /// Day of the month, 1 to 31.
    pub day: u16,
    /// Hour, 0 to 23.
    pub hour: u16,
    /// Minute, 0 to 59.
    pub minute: u16,
    /// Second, 0 to 59.
    pub second: u16,
}

/// Converts `FILETIME` ticks (as in `ListingMeta`) to local time. Uses the
/// daylight-saving rule in force on that date, as Explorer does, not the
/// current one. `None` for values Windows cannot convert (such as 0).
#[allow(unsafe_code)]
#[must_use]
pub fn local_time(ticks: i64) -> Option<LocalTime> {
    let ticks = u64::try_from(ticks).ok().filter(|&ticks| ticks > 0)?;
    let stamp = FILETIME {
        dwLowDateTime: u32::try_from(ticks & 0xFFFF_FFFF).ok()?,
        dwHighDateTime: u32::try_from(ticks >> 32).ok()?,
    };
    let mut utc = SYSTEMTIME::default();
    let mut local = SYSTEMTIME::default();
    // SAFETY: both pointers are to valid, initialized locals that outlive the
    // calls; None selects the current time zone.
    unsafe {
        FileTimeToSystemTime(&raw const stamp, &raw mut utc).ok()?;
        SystemTimeToTzSpecificLocalTime(None, &raw const utc, &raw mut local).ok()?;
    }
    Some(LocalTime {
        year: local.wYear,
        month: local.wMonth,
        day: local.wDay,
        hour: local.wHour,
        minute: local.wMinute,
        second: local.wSecond,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_a_known_instant() {
        // 2026-09-28 01:02:03 UTC. Every time zone is within ±14 hours, so
        // the local date is the 27th, 28th or 29th of September 2026.
        let ticks = 11_644_473_600 * 10_000_000 + 1_790_557_323 * 10_000_000;
        let local = local_time(ticks).unwrap();
        assert_eq!((local.year, local.month), (2026, 9));
        assert!((27..=29).contains(&local.day));
        // Offsets are whole minutes (some are half hours), so only the second
        // is fixed.
        assert_eq!(local.second, 3);
    }

    #[test]
    fn rejects_zero_and_negative_times() {
        assert_eq!(local_time(0), None);
        assert_eq!(local_time(-5), None);
    }
}
