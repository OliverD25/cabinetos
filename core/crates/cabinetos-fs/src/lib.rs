//! Filesystem engine: directory enumeration through the NT API, sorting in
//! the core, the shared-memory listing format, volume and physical-disk
//! detection, and change watching.
//!
//! [`list_directory`] reads a directory with `NtQueryDirectoryFile`, which
//! returns names, file IDs, sizes, times and attributes in one pass, and
//! sorts the result. [`ListingWriter`] puts it into a shared-memory section
//! for the UI; [`ListingReader`] reads a section back.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: a directory is
//! read in one pass and handed over without copying) and Article 5
//! (Dual-Pane Foundation: independent listings per pane). Brief §2.
//!
//! [`volume::info_for_path`] tells which volume and physical disk a path is
//! on, so the job engine can keep copies on one disk from competing.
//! [`DirectoryWatcher`] reports when a directory changes, so its listing can
//! be read again.
//!
//! Unsafe code is allowed only in the modules that call Windows directly
//! (`enumerate`, `volume`, `watch`, `time`, and one function in `sort`), each
//! block with a `SAFETY:` comment.

#[allow(unsafe_code)]
mod enumerate;
mod error;
mod path;
mod section;
mod sort;
pub mod time;
#[allow(unsafe_code)]
pub mod volume;
#[allow(unsafe_code)]
mod watch;

use cabinetos_protocol::SortSpec;
use cabinetos_protocol::shm::{EntryKind, ListingMeta};

pub use error::FsError;
pub use section::{EntryView, LayoutError, ListingReader, ListingWriter};
pub use watch::{DirectoryChanged, DirectoryWatcher};

/// The default buffer for one `NtQueryDirectoryFile` call: 256 KiB, about
/// 2,000 entries per system call.
pub const DEFAULT_BUFFER_SIZE: usize = 256 * 1024;

/// How to list a directory.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ListOptions {
    /// Also list entries with the hidden or the system attribute.
    pub include_hidden: bool,
    /// The order of the entries.
    pub sort: SortSpec,
    /// Bytes per `NtQueryDirectoryFile` call. A tuning knob for benchmarks;
    /// values below 4 KiB are raised to 4 KiB.
    pub buffer_size: usize,
}

impl Default for ListOptions {
    fn default() -> Self {
        Self {
            include_hidden: false,
            sort: SortSpec::default(),
            buffer_size: DEFAULT_BUFFER_SIZE,
        }
    }
}

/// Reads the directory at `path` (absolute or relative; any length) and
/// sorts it. `.` and `..` are never included.
pub fn list_directory(path: &str, options: &ListOptions) -> Result<Listing, FsError> {
    let mut listing = enumerate::read_directory(path, options.include_hidden, options.buffer_size)?;
    sort::sort(&mut listing, options.sort);
    Ok(listing)
}

/// A directory listing in memory, in display order.
#[derive(Clone, Debug, Default)]
pub struct Listing {
    entries: Vec<Entry>,
    /// Every name back to back, UTF-16, without terminators.
    names: Vec<u16>,
}

impl Listing {
    /// Number of entries.
    #[must_use]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// Whether the directory has no (listed) entries.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// The entries, in display order.
    #[must_use]
    pub fn entries(&self) -> &[Entry] {
        &self.entries
    }

    /// The name of `entry` as UTF-16 units.
    #[must_use]
    pub fn name(&self, entry: &Entry) -> &[u16] {
        let start = entry.name_start as usize;
        &self.names[start..start + usize::from(entry.name_len)]
    }

    /// The name of `entry` as a string (invalid UTF-16 shows as U+FFFD).
    #[must_use]
    pub fn name_string(&self, entry: &Entry) -> String {
        String::from_utf16_lossy(self.name(entry))
    }
}

/// One entry of a [`Listing`].
#[derive(Clone, Copy, Debug)]
pub struct Entry {
    /// The file reference number, or a name hash (see `flags`).
    pub id: u64,
    /// File, directory or link.
    pub kind: EntryKind,
    /// `ListingEntry` flags, such as `FLAG_ID_IS_NAME_HASH`.
    pub flags: u8,
    /// Size, times and attributes.
    pub meta: ListingMeta,
    name_start: u32,
    name_len: u16,
}

/// The file attributes the engine looks at (`FILE_ATTRIBUTE_*`).
pub(crate) mod attributes {
    pub(crate) const HIDDEN: u32 = 0x2;
    pub(crate) const SYSTEM: u32 = 0x4;
    pub(crate) const HIDDEN_OR_SYSTEM: u32 = HIDDEN | SYSTEM;
    pub(crate) const DIRECTORY: u32 = 0x10;
    pub(crate) const REPARSE_POINT: u32 = 0x400;
}

/// Fetches expensive, display-only details in the background and streams them
/// to the UI: icons and shell type names. Size, times and attributes do not
/// need it; they arrive with the listing itself.
///
/// Placeholder: Phase 5 of `docs/PLAN.md` fills it in.
pub struct Hydrator;
