//! A preview of proposed changes as a listing section: the header has
//! [`ListingHeader::FLAG_PREVIEW`], each entry's name is the row's full
//! path and its `id` the row's index, and after the name arena one
//! [`PreviewRow`] per entry gives the change and its target (layout in
//! `cabinetos_protocol::shm`, diagram in `docs/ipc.md`). A pane shows it as
//! it shows a folder; nothing on disk changes.

use std::mem::offset_of;
use std::os::windows::fs::MetadataExt;

use cabinetos_protocol::ChangeKind;
use cabinetos_protocol::shm::{EntryKind, ListingEntry, ListingHeader, ListingMeta, PreviewRow};

use crate::section::LayoutError;

const HEADER_SIZE: usize = size_of::<ListingHeader>();
const ENTRY_SIZE: usize = size_of::<ListingEntry>();
const META_SIZE: usize = size_of::<ListingMeta>();
const ROW_SIZE: usize = size_of::<PreviewRow>();

/// One row of a preview, as it goes into the section.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PreviewEntry {
    /// The row's full path: the entry's name.
    pub path: String,
    /// What the path is now (or will be, for a create).
    pub kind: EntryKind,
    /// Its size, times and attributes now; zeros when it does not exist.
    pub meta: ListingMeta,
    /// The change.
    pub change: ChangeKind,
    /// The change's target: the new full path of a rename, the folder of a
    /// move or copy.
    pub to: Option<String>,
}

impl PreviewEntry {
    /// A row for `path` with what the disk says about it now: its kind,
    /// size, times and attributes, or zeros and [`EntryKind::Unknown`] when
    /// it is not there. Reads one file's metadata; nothing else.
    #[must_use]
    pub fn with_metadata(path: &str, change: ChangeKind, to: Option<String>) -> Self {
        let (kind, meta) = match std::fs::symlink_metadata(path) {
            Ok(metadata) => {
                let kind = if metadata.file_type().is_symlink() {
                    EntryKind::ReparsePoint
                } else if metadata.is_dir() {
                    EntryKind::Directory
                } else {
                    EntryKind::File
                };
                let ticks = |value: u64| i64::try_from(value).unwrap_or(0);
                (
                    kind,
                    ListingMeta {
                        size: if metadata.is_dir() {
                            0
                        } else {
                            metadata.file_size()
                        },
                        modified: ticks(metadata.last_write_time()),
                        created: ticks(metadata.creation_time()),
                        accessed: ticks(metadata.last_access_time()),
                        attributes: metadata.file_attributes(),
                        reparse_tag: 0,
                    },
                )
            }
            Err(_) => (EntryKind::Unknown, ListingMeta::default()),
        };
        Self {
            path: path.to_owned(),
            kind,
            meta,
            change,
            to,
        }
    }
}

/// Writes a preview into a section.
#[derive(Debug)]
pub struct PreviewWriter<'a> {
    rows: &'a [PreviewEntry],
    names: Vec<Vec<u16>>,
    targets: Vec<Vec<u16>>,
    meta_offset: usize,
    arena_offset: usize,
    arena_len: usize,
    rows_offset: usize,
    total: usize,
}

impl<'a> PreviewWriter<'a> {
    /// Plans the section. Fails only when it would exceed 4 GiB or a path
    /// is longer than 65,535 UTF-16 units.
    pub fn new(rows: &'a [PreviewEntry]) -> Result<Self, LayoutError> {
        let too_large = || LayoutError::new("the preview is larger than 4 GiB");
        let names: Vec<Vec<u16>> = rows
            .iter()
            .map(|row| row.path.encode_utf16().collect())
            .collect();
        let targets: Vec<Vec<u16>> = rows
            .iter()
            .map(|row| row.to.as_deref().unwrap_or("").encode_utf16().collect())
            .collect();
        if names
            .iter()
            .chain(&targets)
            .any(|units| u16::try_from(units.len()).is_err())
        {
            return Err(LayoutError::new("a path of the preview is too long"));
        }
        let count = rows.len();
        let meta_offset = HEADER_SIZE + count * ENTRY_SIZE;
        let arena_offset = meta_offset + count * META_SIZE;
        let arena_len = names
            .iter()
            .chain(&targets)
            .map(|units| units.len() * 2)
            .sum::<usize>();
        let rows_offset = (arena_offset + arena_len).next_multiple_of(4);
        let total = rows_offset + count * ROW_SIZE;
        u32::try_from(total).map_err(|_| too_large())?;
        Ok(Self {
            rows,
            names,
            targets,
            meta_offset,
            arena_offset,
            arena_len,
            rows_offset,
            total,
        })
    }

    /// The exact number of bytes the preview takes in the section.
    #[must_use]
    pub fn section_size(&self) -> usize {
        self.total
    }

    /// Writes the whole preview into `out`, which must hold at least
    /// [`section_size`](Self::section_size) bytes.
    pub fn write(&self, out: &mut [u8]) -> Result<(), LayoutError> {
        if out.len() < self.total {
            return Err(LayoutError::new(format!(
                "the section has {} bytes, the preview needs {}",
                out.len(),
                self.total
            )));
        }
        out[..self.total].fill(0);
        let as_u32 = |value: usize| u32::try_from(value).expect("checked by new");
        let header = [
            (offset_of!(ListingHeader, magic), ListingHeader::MAGIC),
            (offset_of!(ListingHeader, version), ListingHeader::VERSION),
            (
                offset_of!(ListingHeader, entry_count),
                as_u32(self.rows.len()),
            ),
            (
                offset_of!(ListingHeader, name_arena_offset),
                as_u32(self.arena_offset),
            ),
            (
                offset_of!(ListingHeader, name_arena_len),
                as_u32(self.arena_len),
            ),
            (offset_of!(ListingHeader, generation), 1),
            (
                offset_of!(ListingHeader, meta_offset),
                as_u32(self.meta_offset),
            ),
            (
                offset_of!(ListingHeader, entries_offset),
                as_u32(HEADER_SIZE),
            ),
            (
                offset_of!(ListingHeader, flags),
                ListingHeader::FLAG_PREVIEW,
            ),
            (
                offset_of!(ListingHeader, preview_offset),
                as_u32(self.rows_offset),
            ),
        ];
        for (at, value) in header {
            put(out, at, &value.to_le_bytes());
        }
        // Names first, then the targets, all in the one arena.
        let mut cursor = 0usize;
        let mut place = |out: &mut [u8], units: &[u16]| -> usize {
            let start = cursor;
            let at = self.arena_offset + start;
            for (index, unit) in units.iter().enumerate() {
                put(out, at + index * 2, &unit.to_le_bytes());
            }
            cursor += units.len() * 2;
            start
        };
        let name_offsets: Vec<usize> = self.names.iter().map(|name| place(out, name)).collect();
        let target_offsets: Vec<usize> = self.targets.iter().map(|to| place(out, to)).collect();
        for (index, row) in self.rows.iter().enumerate() {
            let at = HEADER_SIZE + index * ENTRY_SIZE;
            put(
                out,
                at + offset_of!(ListingEntry, id),
                &(index as u64).to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(ListingEntry, name_offset),
                &as_u32(name_offsets[index]).to_le_bytes(),
            );
            let name_len = u16::try_from(self.names[index].len()).expect("checked by new");
            put(
                out,
                at + offset_of!(ListingEntry, name_len),
                &name_len.to_le_bytes(),
            );
            out[at + offset_of!(ListingEntry, kind)] = row.kind.to_raw();

            put_meta(out, self.meta_offset + index * META_SIZE, &row.meta);

            let at = self.rows_offset + index * ROW_SIZE;
            put(
                out,
                at + offset_of!(PreviewRow, to_offset),
                &as_u32(target_offsets[index]).to_le_bytes(),
            );
            put(
                out,
                at + offset_of!(PreviewRow, to_len),
                &as_u32(self.targets[index].len()).to_le_bytes(),
            );
            out[at + offset_of!(PreviewRow, change)] = row.change.to_raw();
        }
        Ok(())
    }
}

fn put_meta(out: &mut [u8], at: usize, meta: &ListingMeta) {
    let fields: [(usize, &[u8]); 6] = [
        (offset_of!(ListingMeta, size), &meta.size.to_le_bytes()),
        (
            offset_of!(ListingMeta, modified),
            &meta.modified.to_le_bytes(),
        ),
        (
            offset_of!(ListingMeta, created),
            &meta.created.to_le_bytes(),
        ),
        (
            offset_of!(ListingMeta, accessed),
            &meta.accessed.to_le_bytes(),
        ),
        (
            offset_of!(ListingMeta, attributes),
            &meta.attributes.to_le_bytes(),
        ),
        (
            offset_of!(ListingMeta, reparse_tag),
            &meta.reparse_tag.to_le_bytes(),
        ),
    ];
    for (offset, bytes) in fields {
        put(out, at + offset, bytes);
    }
}

fn put(out: &mut [u8], at: usize, bytes: &[u8]) {
    out[at..at + bytes.len()].copy_from_slice(bytes);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ListingReader;

    #[test]
    fn a_preview_reads_back_with_its_changes_and_targets() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("Звіт 2026.txt");
        std::fs::write(&file, b"twelve bytes").unwrap();
        let file = file.display().to_string();
        let folder = dir.path().display().to_string();
        let rows = vec![
            PreviewEntry::with_metadata(
                &file,
                ChangeKind::Rename,
                Some(format!(r"{folder}\report.txt")),
            ),
            PreviewEntry::with_metadata(&folder, ChangeKind::Copy, Some(r"D:\backup".to_owned())),
            PreviewEntry::with_metadata(r"C:\no such file here", ChangeKind::Delete, None),
        ];
        let writer = PreviewWriter::new(&rows).unwrap();
        let mut bytes = vec![0xAA; writer.section_size() + 7];
        writer.write(&mut bytes).unwrap();
        let reader = ListingReader::new(&bytes[..writer.section_size()]).unwrap();
        assert!(reader.is_preview());
        assert_eq!(reader.len(), 3);
        let entry = reader.entry(0).unwrap();
        assert_eq!(
            (entry.id, entry.name.as_str(), entry.kind),
            (0, file.as_str(), EntryKind::File)
        );
        assert_eq!(entry.meta.size, 12);
        assert!(entry.meta.modified > 0);
        assert_eq!(reader.entry(1).unwrap().kind, EntryKind::Directory);
        let missing = reader.entry(2).unwrap();
        assert_eq!((missing.kind, missing.meta.size), (EntryKind::Unknown, 0));
        let changes: Vec<_> = (0..3)
            .map(|index| reader.preview_row(index).unwrap())
            .collect();
        assert_eq!(changes[0].change, Some(ChangeKind::Rename));
        assert_eq!(
            changes[0].to.as_deref(),
            Some(format!(r"{folder}\report.txt").as_str())
        );
        assert_eq!(changes[1].change, Some(ChangeKind::Copy));
        assert_eq!(changes[1].to.as_deref(), Some(r"D:\backup"));
        assert_eq!(
            (changes[2].change, changes[2].to.as_deref()),
            (Some(ChangeKind::Delete), None)
        );
        // A preview row is 12 bytes, 4-byte aligned, after the names.
        let header = reader.header();
        assert_eq!(header.preview_offset % 4, 0);
        assert!(header.preview_offset >= header.name_arena_offset + header.name_arena_len);
        assert_eq!(
            header.preview_offset as usize + 3 * 12,
            writer.section_size()
        );
    }

    #[test]
    fn an_empty_preview_is_a_valid_listing() {
        let writer = PreviewWriter::new(&[]).unwrap();
        let mut bytes = vec![0; writer.section_size()];
        writer.write(&mut bytes).unwrap();
        let reader = ListingReader::new(&bytes).unwrap();
        assert!(reader.is_preview() && reader.is_empty());
    }

    #[test]
    fn a_folder_listing_is_no_preview() {
        let listing = crate::list_directory(
            &std::env::temp_dir().display().to_string(),
            &crate::ListOptions::default(),
        )
        .unwrap();
        let writer = crate::ListingWriter::new(&listing).unwrap();
        let mut bytes = vec![0; writer.section_size()];
        writer.write(&mut bytes, 1).unwrap();
        let reader = ListingReader::new(&bytes).unwrap();
        assert!(!reader.is_preview());
        assert!(reader.preview_row(0).is_err());
    }
}
