//! Filesystem engine: directory enumeration through the NT API, asynchronous
//! metadata hydration, volume and physical-disk detection, and change watching.
//!
//! Serves Constitution Article 1 (Zero-Compromise Performance: a listing of
//! names appears at once, heavy metadata streams in afterwards) and Article 5
//! (Dual-Pane Foundation: two independent pane sessions). Brief §2.
//!
//! Status: stub. Phase 2 of `docs/PLAN.md` fills it in. The types below only
//! name the shape of the public API so it can be reviewed early. Unsafe code
//! will be allowed here, isolated per module, once the NT API calls arrive.

/// Lists one directory with `NtQueryDirectoryFile` (or `FindFirstFileExW` with
/// large fetch) and writes the names into a shared-memory listing.
pub struct DirectoryEnumerator;

/// Fetches the heavier metadata of a listing (size, dates, attributes, icon
/// key) on a background thread and streams it to the UI in chunks.
pub struct Hydrator;

/// The volume and the physical disk a path lives on. The job engine uses it to
/// decide which copies may run in parallel.
pub struct VolumeInfo;

/// Watches open directories with `ReadDirectoryChangesW`.
pub struct DirectoryWatcher;

/// Event sent when a watched directory changes on disk.
pub struct DirectoryChanged;
