//! Planning a job: walking the sources with the Phase 2 enumeration
//! (`NtQueryDirectoryFile`, names and metadata in one pass) to list every
//! folder and file, with the totals the progress needs.
//!
//! Items refer to their folder by index and carry only their own name, so a
//! folder renamed by a conflict decision takes its contents along.

use std::os::windows::fs::{FileTypeExt, MetadataExt};

use cabinetos_fs::{ListOptions, list_directory};
use cabinetos_protocol::LinkPolicy;
use cabinetos_protocol::shm::EntryKind;

use crate::win::Times;

/// `FILE_ATTRIBUTE_DIRECTORY`.
const DIRECTORY: u32 = 0x10;

/// How a file reaches its destination.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Transfer {
    /// `CopyFileExW`.
    Copy,
    /// Copy, then delete the source (a move across volumes).
    CopyAndDelete,
    /// `MoveFileExW` on one volume (a folder merge in a move).
    Rename,
}

/// What a file item is.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum FileKind {
    File,
    /// A symbolic link to a file, copied as a link.
    FileLink,
    /// A junction or a symbolic link to a folder, copied as a link.
    DirLink,
}

/// A folder to create at the destination.
#[derive(Clone, Debug)]
pub(crate) struct DirItem {
    pub(crate) source: String,
    /// Its name at the destination.
    pub(crate) name: String,
    /// The folder it goes into; `None`: the job's destination.
    pub(crate) parent: Option<usize>,
    pub(crate) times: Times,
    /// Remove the source folder at the end if it is empty (a move).
    pub(crate) remove_source: bool,
}

/// A file or a link to copy or move.
#[derive(Clone, Debug)]
pub(crate) struct FileItem {
    pub(crate) source: String,
    pub(crate) name: String,
    pub(crate) parent: Option<usize>,
    pub(crate) kind: FileKind,
    pub(crate) transfer: Transfer,
    pub(crate) size: u64,
    pub(crate) times: Times,
    pub(crate) attributes: u32,
}

/// A source renamed into the destination in one step (a move on one
/// volume).
#[derive(Clone, Debug)]
pub(crate) struct RenameItem {
    pub(crate) source: String,
    pub(crate) name: String,
    pub(crate) is_dir: bool,
    pub(crate) size: u64,
    pub(crate) times: Times,
}

/// What a removal deletes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum RemoveKind {
    /// A file, or a link to a file: `DeleteFileW`.
    File,
    /// An empty folder, or a link to a folder: `RemoveDirectoryW`.
    Dir,
}

/// One path of a permanent delete. Folders come after their contents.
#[derive(Clone, Debug)]
pub(crate) struct RemoveItem {
    pub(crate) path: String,
    pub(crate) kind: RemoveKind,
    pub(crate) attributes: u32,
}

/// Everything a job will do.
#[derive(Debug, Default)]
pub(crate) struct Plan {
    pub(crate) dirs: Vec<DirItem>,
    pub(crate) files: Vec<FileItem>,
    pub(crate) renames: Vec<RenameItem>,
    pub(crate) removals: Vec<RemoveItem>,
    pub(crate) recycles: Vec<String>,
    /// Bytes to copy.
    pub(crate) bytes: u64,
    /// Folders that could not be read while planning, with the error.
    pub(crate) unreadable: Vec<(String, u32)>,
}

impl Plan {
    /// Files and folders to handle.
    pub(crate) fn count(&self) -> u64 {
        (self.dirs.len()
            + self.files.len()
            + self.renames.len()
            + self.removals.len()
            + self.recycles.len()) as u64
    }
}

/// Stops a walk early.
pub(crate) trait Stop {
    fn stop(&self) -> bool;
}

impl<F: Fn() -> bool> Stop for F {
    fn stop(&self) -> bool {
        self()
    }
}

/// Why planning failed.
#[derive(Debug)]
pub(crate) enum PlanError {
    /// The job was cancelled while planning.
    Cancelled,
    /// A source root could not be read at all.
    Source(String),
}

/// The last component of `path`.
pub(crate) fn file_name(path: &str) -> &str {
    path.trim_end_matches(['\\', '/'])
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or(path)
}

/// `folder\name`, with one separator.
pub(crate) fn join(folder: &str, name: &str) -> String {
    if folder.ends_with(['\\', '/']) {
        format!("{folder}{name}")
    } else {
        format!("{folder}\\{name}")
    }
}

/// A root as the plan sees it.
struct Root {
    is_dir: bool,
    link: Option<FileKind>,
    size: u64,
    times: Times,
    attributes: u32,
}

fn root(path: &str) -> Result<Root, PlanError> {
    let metadata = std::fs::symlink_metadata(path)
        .map_err(|error| PlanError::Source(format!("{path}: {error}")))?;
    let file_type = metadata.file_type();
    let link = if file_type.is_symlink_dir() {
        Some(FileKind::DirLink)
    } else if file_type.is_symlink() {
        Some(FileKind::FileLink)
    } else {
        None
    };
    Ok(Root {
        is_dir: file_type.is_dir(),
        link,
        size: metadata.file_size(),
        times: Times {
            created: metadata.creation_time().cast_signed(),
            accessed: metadata.last_access_time().cast_signed(),
            modified: metadata.last_write_time().cast_signed(),
        },
        attributes: metadata.file_attributes(),
    })
}

/// What a followed link points to.
fn target(path: &str) -> Option<Root> {
    let metadata = std::fs::metadata(path).ok()?;
    Some(Root {
        is_dir: metadata.is_dir(),
        link: None,
        size: metadata.file_size(),
        times: Times {
            created: metadata.creation_time().cast_signed(),
            accessed: metadata.last_access_time().cast_signed(),
            modified: metadata.last_write_time().cast_signed(),
        },
        attributes: metadata.file_attributes(),
    })
}

fn listing_options() -> ListOptions {
    ListOptions {
        // A copy must include hidden and system files.
        include_hidden: true,
        ..ListOptions::default()
    }
}

/// Adds the source root `source` and everything under it to a copy plan.
/// `parent`: the folder item it goes into (`None`: the job's destination).
pub(crate) fn add_copy_root(
    plan: &mut Plan,
    source: &str,
    name: &str,
    parent: Option<usize>,
    transfer: Transfer,
    links: LinkPolicy,
    stop: &dyn Stop,
) -> Result<(), PlanError> {
    let mut info = root(source)?;
    if let Some(kind) = info.link {
        if let (LinkPolicy::FollowTarget, Some(followed)) = (links, target(source)) {
            info = followed;
        } else {
            plan.files.push(FileItem {
                source: source.to_owned(),
                name: name.to_owned(),
                parent,
                kind,
                transfer,
                size: 0,
                times: info.times,
                attributes: info.attributes,
            });
            return Ok(());
        }
    }
    if !info.is_dir {
        plan.bytes += info.size;
        plan.files.push(FileItem {
            source: source.to_owned(),
            name: name.to_owned(),
            parent,
            kind: FileKind::File,
            transfer,
            size: info.size,
            times: info.times,
            attributes: info.attributes,
        });
        return Ok(());
    }

    let mut followed = Vec::new();
    if let Ok(canonical) = std::fs::canonicalize(source) {
        followed.push(canonical);
    }
    plan.dirs.push(DirItem {
        source: source.to_owned(),
        name: name.to_owned(),
        parent,
        times: info.times,
        remove_source: transfer != Transfer::Copy,
    });
    let mut pending = vec![plan.dirs.len() - 1];
    while let Some(dir_index) = pending.pop() {
        if stop.stop() {
            return Err(PlanError::Cancelled);
        }
        let dir_source = plan.dirs[dir_index].source.clone();
        match list_directory(&dir_source, &listing_options()) {
            Ok(listing) => {
                let walk = Walk {
                    links,
                    transfer,
                    dir_index,
                    dir_source: &dir_source,
                };
                walk.add_children(plan, &listing, &mut followed, &mut pending);
            }
            Err(error) => plan.unreadable.push((dir_source, fs_code(&error))),
        }
    }
    Ok(())
}

/// One folder's listing going into a copy plan.
struct Walk<'a> {
    links: LinkPolicy,
    transfer: Transfer,
    dir_index: usize,
    dir_source: &'a str,
}

impl Walk<'_> {
    /// Adds the entries of `listing`; folders to walk next go onto
    /// `pending`.
    fn add_children(
        &self,
        plan: &mut Plan,
        listing: &cabinetos_fs::Listing,
        followed: &mut Vec<std::path::PathBuf>,
        pending: &mut Vec<usize>,
    ) {
        for entry in listing.entries() {
            let child_name = listing.name_string(entry);
            let child = join(self.dir_source, &child_name);
            let times = Times {
                created: entry.meta.created,
                accessed: entry.meta.accessed,
                modified: entry.meta.modified,
            };
            let is_dir_link = entry.meta.attributes & DIRECTORY != 0;
            let mut kind = entry.kind;
            let mut size = entry.meta.size;
            if kind == EntryKind::ReparsePoint && self.links == LinkPolicy::FollowTarget {
                match target(&child) {
                    // Following a folder link that leads back into what is
                    // being copied would never end; such a link is copied
                    // as a link.
                    Some(found) if found.is_dir => {
                        let canonical = std::fs::canonicalize(&child).ok();
                        let cycle = canonical.as_ref().is_none_or(|canonical| {
                            followed.iter().any(|seen| {
                                canonical.starts_with(seen) || seen.starts_with(canonical)
                            })
                        });
                        if !cycle {
                            followed.extend(canonical);
                            kind = EntryKind::Directory;
                        }
                    }
                    Some(found) => {
                        kind = EntryKind::File;
                        size = found.size;
                    }
                    None => {}
                }
            }
            match kind {
                EntryKind::Directory => {
                    plan.dirs.push(DirItem {
                        source: child,
                        name: child_name,
                        parent: Some(self.dir_index),
                        times,
                        remove_source: self.transfer != Transfer::Copy,
                    });
                    pending.push(plan.dirs.len() - 1);
                }
                EntryKind::ReparsePoint => plan.files.push(FileItem {
                    source: child,
                    name: child_name,
                    parent: Some(self.dir_index),
                    kind: if is_dir_link {
                        FileKind::DirLink
                    } else {
                        FileKind::FileLink
                    },
                    transfer: self.transfer,
                    size: 0,
                    times,
                    attributes: entry.meta.attributes,
                }),
                EntryKind::File | EntryKind::Unknown => {
                    plan.bytes += size;
                    plan.files.push(FileItem {
                        source: child,
                        name: child_name,
                        parent: Some(self.dir_index),
                        kind: FileKind::File,
                        transfer: self.transfer,
                        size,
                        times,
                        attributes: entry.meta.attributes,
                    });
                }
            }
        }
    }
}

/// Adds a source to rename into the destination in one step.
pub(crate) fn add_rename_root(plan: &mut Plan, source: &str) -> Result<(), PlanError> {
    let info = root(source)?;
    plan.renames.push(RenameItem {
        source: source.to_owned(),
        name: file_name(source).to_owned(),
        is_dir: info.is_dir || info.link == Some(FileKind::DirLink),
        size: info.size,
        times: info.times,
    });
    Ok(())
}

/// The bytes under `path`: its size for a file or a link, the sum of every
/// file in it for a folder. Links are not followed.
pub(crate) fn tree_size(path: &str, stop: &dyn Stop) -> Result<u64, PlanError> {
    let info = root(path)?;
    if info.link.is_some() || !info.is_dir {
        return Ok(info.size);
    }
    let mut total = 0u64;
    let mut pending = vec![path.to_owned()];
    while let Some(dir) = pending.pop() {
        if stop.stop() {
            return Err(PlanError::Cancelled);
        }
        let Ok(listing) = list_directory(&dir, &listing_options()) else {
            continue;
        };
        for entry in listing.entries() {
            match entry.kind {
                EntryKind::Directory => pending.push(join(&dir, &listing.name_string(entry))),
                EntryKind::ReparsePoint => {}
                EntryKind::File | EntryKind::Unknown => total += entry.meta.size,
            }
        }
    }
    Ok(total)
}

/// Adds `source` and everything under it to a permanent delete, contents
/// before their folder. Links are removed, never followed.
pub(crate) fn add_removal_root(
    plan: &mut Plan,
    source: &str,
    stop: &dyn Stop,
) -> Result<(), PlanError> {
    let info = root(source)?;
    if info.link.is_some() || !info.is_dir {
        plan.removals.push(RemoveItem {
            path: source.to_owned(),
            kind: if info.link == Some(FileKind::DirLink) || info.is_dir {
                RemoveKind::Dir
            } else {
                RemoveKind::File
            },
            attributes: info.attributes,
        });
        return Ok(());
    }
    // Depth-first; a folder is emitted when its contents are done.
    let mut stack = vec![(source.to_owned(), info.attributes, false)];
    while let Some((dir, attributes, expanded)) = stack.pop() {
        if stop.stop() {
            return Err(PlanError::Cancelled);
        }
        if expanded {
            plan.removals.push(RemoveItem {
                path: dir,
                kind: RemoveKind::Dir,
                attributes,
            });
            continue;
        }
        let listing = match list_directory(&dir, &listing_options()) {
            Ok(listing) => listing,
            Err(error) => {
                plan.unreadable.push((dir.clone(), fs_code(&error)));
                plan.removals.push(RemoveItem {
                    path: dir,
                    kind: RemoveKind::Dir,
                    attributes,
                });
                continue;
            }
        };
        stack.push((dir.clone(), attributes, true));
        for entry in listing.entries() {
            let child = join(&dir, &listing.name_string(entry));
            match entry.kind {
                EntryKind::Directory => stack.push((child, entry.meta.attributes, false)),
                EntryKind::ReparsePoint => plan.removals.push(RemoveItem {
                    path: child,
                    kind: if entry.meta.attributes & DIRECTORY != 0 {
                        RemoveKind::Dir
                    } else {
                        RemoveKind::File
                    },
                    attributes: entry.meta.attributes,
                }),
                EntryKind::File | EntryKind::Unknown => plan.removals.push(RemoveItem {
                    path: child,
                    kind: RemoveKind::File,
                    attributes: entry.meta.attributes,
                }),
            }
        }
    }
    Ok(())
}

/// A Win32 code for a planning error, for the job's summary.
fn fs_code(error: &cabinetos_fs::FsError) -> u32 {
    use cabinetos_fs::FsError;
    match error {
        FsError::NotFound { .. } => crate::win::code::PATH_NOT_FOUND,
        FsError::AccessDenied { .. } => crate::win::code::ACCESS_DENIED,
        FsError::InvalidPath { .. } => crate::win::code::INVALID_NAME,
        FsError::Io { source, .. } => source.raw_os_error().map_or(0, i32::cast_unsigned),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_and_joins() {
        assert_eq!(file_name(r"C:\a\b.txt"), "b.txt");
        assert_eq!(file_name(r"C:\a\dir\"), "dir");
        assert_eq!(join(r"C:\a", "b"), r"C:\a\b");
        assert_eq!(join(r"E:\", "b"), r"E:\b");
    }
}
