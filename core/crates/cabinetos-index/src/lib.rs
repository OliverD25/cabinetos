//! The volume index: every file and directory of an NTFS volume in memory,
//! keyed by file reference number, searchable by name in milliseconds.
//!
//! - [`record`] reads the USN records the NTFS driver returns, both for the
//!   whole MFT (`FSCTL_ENUM_USN_DATA`) and for each change
//!   (`FSCTL_READ_USN_JOURNAL`).
//! - [`VolumeIndex`] holds the entries: parent, interned name and attributes
//!   per file reference number. Paths are rebuilt from the parents, so a
//!   renamed directory is one update.
//! - [`Matcher`] and [`VolumeIndex::search`]: case-insensitive substring
//!   search, ranked, on several threads.
//!
//! Shared by the elevated indexer, which builds and serves the index, and the
//! core, which ranks the results of its own folder walk the same way when no
//! indexer runs (ADR 0002).
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: real-time
//! NT-level file indexing). Brief §2.

mod index;
mod names;
pub mod record;
mod search;

pub use index::{EntryInfo, Frn, ROOT_SEGMENT, VolumeIndex, segment};
pub use names::lowercase;
pub use search::{IndexHit, Matcher, Rank, sort_hits};
