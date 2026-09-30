//! Dates from the clock the sandbox gives (milliseconds since 1970, UTC).
//! No time-zone data is available to a plugin, so every time it writes is
//! UTC and says so.

/// Year, month, day, hour, minute and second of a time.
fn civil(ms: u64) -> (i64, u32, u32, u32, u32, u32) {
    let secs = ms / 1000;
    let days = i64::try_from(secs / 86_400).unwrap_or(i64::MAX / 2);
    let rest = secs % 86_400;
    // Days since 1970-01-01 to a calendar date (H. Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let shifted_month = (5 * day_of_year + 2) / 153;
    let day = u32::try_from(day_of_year - (153 * shifted_month + 2) / 5 + 1).unwrap_or(1);
    let month = u32::try_from(if shifted_month < 10 {
        shifted_month + 3
    } else {
        shifted_month - 9
    })
    .unwrap_or(1);
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    (
        year,
        month,
        day,
        u32::try_from(rest / 3600).unwrap_or(0),
        u32::try_from(rest % 3600 / 60).unwrap_or(0),
        u32::try_from(rest % 60).unwrap_or(0),
    )
}

/// `2026-09-30`, the day of a time.
#[must_use]
pub fn date(ms: u64) -> String {
    let (year, month, day, ..) = civil(ms);
    format!("{year:04}-{month:02}-{day:02}")
}

/// `2026-09-30T01:02:03Z`, for the audit log.
#[must_use]
pub fn iso(ms: u64) -> String {
    let (year, month, day, hour, minute, second) = civil(ms);
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}Z")
}

/// `2026-09-30 01:02`, for a listing of files.
#[must_use]
pub fn minute(ms: u64) -> String {
    let (year, month, day, hour, minute, _) = civil(ms);
    format!("{year:04}-{month:02}-{day:02} {hour:02}:{minute:02}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_moments_read_right() {
        assert_eq!(iso(0), "1970-01-01T00:00:00Z");
        // 2000-02-29 12:34:56 UTC, a leap day.
        assert_eq!(iso(951_827_696_000), "2000-02-29T12:34:56Z");
        // 2026-09-30 01:02:03 UTC.
        assert_eq!(iso(1_790_730_123_000), "2026-09-30T01:02:03Z");
        assert_eq!(date(1_790_730_123_000), "2026-09-30");
        assert_eq!(minute(1_790_730_123_000), "2026-09-30 01:02");
        // The last second of a year and the first of the next.
        assert_eq!(iso(1_767_225_599_000), "2025-12-31T23:59:59Z");
        assert_eq!(iso(1_767_225_600_000), "2026-01-01T00:00:00Z");
    }
}
