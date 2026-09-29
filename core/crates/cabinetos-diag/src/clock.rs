//! UTC timestamps for log lines and crash file names.

use time::OffsetDateTime;

/// The current time in UTC.
pub(crate) fn now() -> OffsetDateTime {
    OffsetDateTime::now_utc()
}

/// RFC 3339 in UTC with milliseconds: `2026-09-28T01:02:03.004Z`.
pub(crate) fn rfc3339_millis(t: OffsetDateTime) -> String {
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z",
        t.year(),
        u8::from(t.month()),
        t.day(),
        t.hour(),
        t.minute(),
        t.second(),
        t.millisecond()
    )
}

/// The UTC date, as log file names have it: `2026-09-28`.
pub(crate) fn date(t: OffsetDateTime) -> String {
    format!("{:04}-{:02}-{:02}", t.year(), u8::from(t.month()), t.day())
}

/// The same instant without separators, safe in a file name:
/// `20260928T010203004Z`.
pub(crate) fn compact_millis(t: OffsetDateTime) -> String {
    format!(
        "{:04}{:02}{:02}T{:02}{:02}{:02}{:03}Z",
        t.year(),
        u8::from(t.month()),
        t.day(),
        t.hour(),
        t.minute(),
        t.second(),
        t.millisecond()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(unix_millis: i128) -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp_nanos(unix_millis * 1_000_000).unwrap()
    }

    #[test]
    fn formats_rfc3339_with_milliseconds() {
        // 2026-09-28T01:02:03.004Z
        assert_eq!(
            rfc3339_millis(at(1_790_557_323_004)),
            "2026-09-28T01:02:03.004Z"
        );
        assert_eq!(rfc3339_millis(at(0)), "1970-01-01T00:00:00.000Z");
        // A leap day, one millisecond before midnight.
        assert_eq!(
            rfc3339_millis(at(1_709_251_199_999)),
            "2024-02-29T23:59:59.999Z"
        );
    }

    #[test]
    fn formats_the_file_date() {
        assert_eq!(date(at(1_790_557_323_004)), "2026-09-28");
    }

    #[test]
    fn formats_compact_file_stamp() {
        assert_eq!(compact_millis(at(1_790_557_323_004)), "20260928T010203004Z");
    }
}
