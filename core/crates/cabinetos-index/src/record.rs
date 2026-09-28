//! USN records as the NTFS driver returns them: `FSCTL_ENUM_USN_DATA` gives
//! one per MFT entry, `FSCTL_READ_USN_JOURNAL` one per change. Both output
//! buffers start with an 8-byte value (the next file reference number, or
//! the next USN) followed by records. The records are read by offset from
//! the bytes, so no pointer is ever cast.

/// The `USN_REASON_*` bits the index acts on.
pub mod reason {
    /// The file or directory was created.
    pub const FILE_CREATE: u32 = 0x0000_0100;
    /// The file or directory was deleted.
    pub const FILE_DELETE: u32 = 0x0000_0200;
    /// The record carries the name before a rename.
    pub const RENAME_OLD_NAME: u32 = 0x0000_1000;
    /// The record carries the name after a rename.
    pub const RENAME_NEW_NAME: u32 = 0x0000_2000;
    /// Attributes or times changed.
    pub const BASIC_INFO_CHANGE: u32 = 0x0000_8000;
    /// A hard link was added or removed; the record names that link.
    pub const HARD_LINK_CHANGE: u32 = 0x0001_0000;
    /// The last handle was closed: the record sums up the changes.
    pub const CLOSE: u32 = 0x8000_0000;
}

/// One file or directory as a USN record describes it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct UsnRecord<'a> {
    /// Its file reference number: MFT segment (low 48 bits) and sequence
    /// number (high 16 bits).
    pub frn: u64,
    /// Its parent directory's file reference number.
    pub parent: u64,
    /// The record's place in the journal (0 in an MFT enumeration of a file
    /// that never changed since the journal began).
    pub usn: i64,
    /// `USN_REASON_*` bits; 0 in an MFT enumeration.
    pub reason: u32,
    /// `FILE_ATTRIBUTE_*` bits.
    pub attributes: u32,
    /// The name, UTF-16 little-endian.
    name: &'a [u8],
}

impl UsnRecord<'_> {
    /// The name's length in UTF-16 units.
    #[must_use]
    pub fn name_len(&self) -> usize {
        self.name.len() / 2
    }

    /// Copies the name into `out` as UTF-16 units and returns them. `out`
    /// must hold [`name_len`](Self::name_len) units.
    pub fn name_into<'b>(&self, out: &'b mut [u16]) -> &'b [u16] {
        let len = self.name_len();
        let (pairs, _) = self.name.as_chunks::<2>();
        for (unit, pair) in out.iter_mut().zip(pairs) {
            *unit = u16::from_le_bytes(*pair);
        }
        &out[..len]
    }

    /// The name as a string (invalid UTF-16 shows as U+FFFD).
    #[must_use]
    pub fn name(&self) -> String {
        let mut units = vec![0u16; self.name_len()];
        String::from_utf16_lossy(self.name_into(&mut units))
    }
}

/// A record the parser cannot read. The buffer is not used further.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
pub enum RecordError {
    /// A record runs past the end of the buffer, or claims a length shorter
    /// than its header.
    #[error("the USN record at byte {offset} is cut off or has a bad length")]
    BadLength {
        /// Where the record starts in the buffer.
        offset: usize,
    },
    /// The name lies outside its record or has an odd byte count.
    #[error("the USN record at byte {offset} has a bad name")]
    BadName {
        /// Where the record starts in the buffer.
        offset: usize,
    },
}

/// The 8-byte value at the start of an FSCTL output buffer: the next start
/// file reference number (MFT enumeration) or the next USN (journal).
#[must_use]
pub fn header(buffer: &[u8]) -> Option<u64> {
    buffer
        .first_chunk::<8>()
        .map(|bytes| u64::from_le_bytes(*bytes))
}

/// The records of an FSCTL output buffer, after its 8-byte header.
#[must_use]
pub fn records(buffer: &[u8]) -> Records<'_> {
    Records {
        data: buffer,
        offset: 8.min(buffer.len()),
    }
}

/// Iterator over the records of a buffer; see [`records`]. After an error it
/// ends.
#[derive(Clone, Debug)]
pub struct Records<'a> {
    data: &'a [u8],
    offset: usize,
}

/// Header sizes up to and including the name offset field.
const V2_HEADER: usize = 60;
const V3_HEADER: usize = 76;

fn u16_at(data: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([data[at], data[at + 1]])
}

fn u32_at(data: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(data[at..at + 4].try_into().expect("four bytes"))
}

fn u64_at(data: &[u8], at: usize) -> u64 {
    u64::from_le_bytes(data[at..at + 8].try_into().expect("eight bytes"))
}

impl<'a> Iterator for Records<'a> {
    type Item = Result<UsnRecord<'a>, RecordError>;

    fn next(&mut self) -> Option<Self::Item> {
        loop {
            let start = self.offset;
            let rest = &self.data[start..];
            if rest.len() < 8 {
                return None;
            }
            let length = u32_at(rest, 0) as usize;
            if length < 8 || length > rest.len() {
                self.offset = self.data.len();
                return Some(Err(RecordError::BadLength { offset: start }));
            }
            let record = &rest[..length];
            self.offset += length;
            let parsed = match u16_at(record, 4) {
                2 => parse(record, V2_HEADER, start, |record| {
                    (u64_at(record, 8), u64_at(record, 16), 24)
                }),
                // A 128-bit file ID: on NTFS the high half is zero, and the
                // low half is the 64-bit file reference number.
                3 => parse(record, V3_HEADER, start, |record| {
                    (u64_at(record, 8), u64_at(record, 24), 40)
                }),
                // Version 4 (range tracking) has no name; later versions are
                // unknown. Neither describes a name, so both are skipped.
                _ => continue,
            };
            if parsed.is_err() {
                self.offset = self.data.len();
            }
            return Some(parsed);
        }
    }
}

/// Reads one record. `ids` returns the file and parent IDs and the offset of
/// the `Usn` field, which the rest of the layout follows.
fn parse(
    record: &[u8],
    header: usize,
    start: usize,
    ids: impl Fn(&[u8]) -> (u64, u64, usize),
) -> Result<UsnRecord<'_>, RecordError> {
    if record.len() < header {
        return Err(RecordError::BadLength { offset: start });
    }
    let (frn, parent, usn_at) = ids(record);
    // Usn, TimeStamp, Reason, SourceInfo, SecurityId, FileAttributes,
    // FileNameLength, FileNameOffset.
    let usn = u64_at(record, usn_at).cast_signed();
    let reason = u32_at(record, usn_at + 16);
    let attributes = u32_at(record, usn_at + 28);
    let name_length = usize::from(u16_at(record, usn_at + 32));
    let name_offset = usize::from(u16_at(record, usn_at + 34));
    let name = record
        .get(name_offset..name_offset + name_length)
        .filter(|name| name.len() % 2 == 0)
        .ok_or(RecordError::BadName { offset: start })?;
    Ok(UsnRecord {
        frn,
        parent,
        usn,
        reason,
        attributes,
        name,
    })
}

/// Builds records the way the driver lays them out, for tests.
#[cfg(test)]
pub(crate) mod encode {
    /// A version 2 record, padded to 8 bytes.
    pub(crate) fn v2(
        frn: u64,
        parent: u64,
        usn: i64,
        reason: u32,
        attributes: u32,
        name: &str,
    ) -> Vec<u8> {
        let name: Vec<u8> = name.encode_utf16().flat_map(u16::to_le_bytes).collect();
        let length = (60 + name.len()).next_multiple_of(8);
        let mut out = vec![0u8; length];
        out[0..4].copy_from_slice(&u32::try_from(length).unwrap().to_le_bytes());
        out[4..6].copy_from_slice(&2u16.to_le_bytes());
        out[8..16].copy_from_slice(&frn.to_le_bytes());
        out[16..24].copy_from_slice(&parent.to_le_bytes());
        out[24..32].copy_from_slice(&usn.to_le_bytes());
        out[40..44].copy_from_slice(&reason.to_le_bytes());
        out[52..56].copy_from_slice(&attributes.to_le_bytes());
        out[56..58].copy_from_slice(&u16::try_from(name.len()).unwrap().to_le_bytes());
        out[58..60].copy_from_slice(&60u16.to_le_bytes());
        out[60..60 + name.len()].copy_from_slice(&name);
        out
    }

    /// A version 3 record (128-bit IDs), padded to 8 bytes.
    pub(crate) fn v3(frn: u64, parent: u64, reason: u32, attributes: u32, name: &str) -> Vec<u8> {
        let name: Vec<u8> = name.encode_utf16().flat_map(u16::to_le_bytes).collect();
        let length = (76 + name.len()).next_multiple_of(8);
        let mut out = vec![0u8; length];
        out[0..4].copy_from_slice(&u32::try_from(length).unwrap().to_le_bytes());
        out[4..6].copy_from_slice(&3u16.to_le_bytes());
        out[8..16].copy_from_slice(&frn.to_le_bytes());
        out[24..32].copy_from_slice(&parent.to_le_bytes());
        out[56..60].copy_from_slice(&reason.to_le_bytes());
        out[68..72].copy_from_slice(&attributes.to_le_bytes());
        out[72..74].copy_from_slice(&u16::try_from(name.len()).unwrap().to_le_bytes());
        out[74..76].copy_from_slice(&76u16.to_le_bytes());
        out[76..76 + name.len()].copy_from_slice(&name);
        out
    }

    /// An output buffer: the 8-byte header, then the records.
    pub(crate) fn buffer(header: u64, records: &[Vec<u8>]) -> Vec<u8> {
        let mut out = header.to_le_bytes().to_vec();
        for record in records {
            out.extend_from_slice(record);
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_version_2_and_3_records_after_the_header() {
        let buffer = encode::buffer(
            77,
            &[
                encode::v2(
                    0x0001_0000_0000_0040,
                    0x0005_0000_0000_0005,
                    9,
                    0,
                    0x20,
                    "a.txt",
                ),
                encode::v3(
                    0x0002_0000_0000_0041,
                    0x0001_0000_0000_0040,
                    reason::FILE_CREATE,
                    0x10,
                    "Ünïcode",
                ),
            ],
        );
        assert_eq!(header(&buffer), Some(77));
        let parsed: Vec<_> = records(&buffer).collect::<Result<_, _>>().unwrap();
        assert_eq!(parsed.len(), 2);
        assert_eq!(parsed[0].frn, 0x0001_0000_0000_0040);
        assert_eq!(parsed[0].parent, 0x0005_0000_0000_0005);
        assert_eq!(parsed[0].usn, 9);
        assert_eq!(parsed[0].attributes, 0x20);
        assert_eq!(parsed[0].name(), "a.txt");
        assert_eq!(parsed[1].parent, 0x0001_0000_0000_0040);
        assert_eq!(parsed[1].reason, reason::FILE_CREATE);
        assert_eq!(parsed[1].name(), "Ünïcode");
        assert_eq!(parsed[1].name_len(), 7);
    }

    #[test]
    fn an_empty_buffer_has_no_records() {
        assert_eq!(records(&[]).count(), 0);
        assert_eq!(records(&7u64.to_le_bytes()).count(), 0);
        assert_eq!(header(&[1, 2]), None);
    }

    #[test]
    fn unknown_versions_are_skipped() {
        let mut v4 = vec![0u8; 16];
        v4[0..4].copy_from_slice(&16u32.to_le_bytes());
        v4[4..6].copy_from_slice(&4u16.to_le_bytes());
        let buffer = encode::buffer(0, &[v4, encode::v2(40, 5, 0, 0, 0, "kept")]);
        let names: Vec<String> = records(&buffer)
            .map(|record| record.unwrap().name())
            .collect();
        assert_eq!(names, ["kept"]);
    }

    #[test]
    fn a_broken_record_ends_the_buffer_with_an_error() {
        let mut short = encode::v2(40, 5, 0, 0, 0, "abc");
        short[0..4].copy_from_slice(&4000u32.to_le_bytes());
        let buffer = encode::buffer(0, &[encode::v2(41, 5, 0, 0, 0, "ok"), short]);
        let parsed: Vec<_> = records(&buffer).collect();
        assert_eq!(parsed.len(), 2);
        assert!(parsed[0].is_ok());
        assert_eq!(parsed[1], Err(RecordError::BadLength { offset: 72 }));

        let mut bad_name = encode::v2(40, 5, 0, 0, 0, "abc");
        bad_name[58..60].copy_from_slice(&200u16.to_le_bytes());
        let buffer = encode::buffer(0, &[bad_name]);
        assert_eq!(
            records(&buffer).next(),
            Some(Err(RecordError::BadName { offset: 8 }))
        );

        let mut zero = encode::v2(40, 5, 0, 0, 0, "abc");
        zero[0..4].copy_from_slice(&0u32.to_le_bytes());
        assert!(matches!(
            records(&encode::buffer(0, &[zero])).next(),
            Some(Err(RecordError::BadLength { .. }))
        ));
    }
}
