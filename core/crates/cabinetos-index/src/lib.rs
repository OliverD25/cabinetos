//! Volume index: an in-memory tree of every file on an NTFS volume, built from
//! the MFT and kept fresh by tailing the USN Journal. Shared by the elevated
//! indexer (which builds it) and the core (which queries it).
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: real-time
//! NT-level file indexing). Brief §2; elevation lives in a separate process
//! (ADR 0002).
//!
//! Status: stub. Phase 6 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early. Unsafe code
//! will be allowed here, isolated per module, once the MFT reader arrives.

/// Compact in-memory tree of one volume, keyed by file reference number.
pub struct VolumeIndex;

/// Reads `$MFT` directly to build a [`VolumeIndex`].
pub struct MftReader;

/// Follows the USN Journal (`FSCTL_READ_USN_JOURNAL`) to update a
/// [`VolumeIndex`] without rescanning the disk.
pub struct UsnTailer;
