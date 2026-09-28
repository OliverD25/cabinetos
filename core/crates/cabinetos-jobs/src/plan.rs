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
    let mut through_link = false;
    if let Some(kind) = info.link {
        if let (LinkPolicy::FollowTarget, Some(followed)) = (links, target(source)) {
            info = followed;
            through_link = true;
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
    let mut pending = vec![(plan.dirs.len() - 1, inside(transfer, through_link))];
    while let Some((dir_index, transfer)) = pending.pop() {
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

/// How the contents of a folder are transferred, when the folder itself
/// is by `transfer`. What a followed folder link holds is copied and never
/// deleted: the delete half of a move removes the link (its folder item,
/// with `RemoveDirectoryW`, which takes a link alone) and nothing through it.
fn inside(transfer: Transfer, through_link: bool) -> Transfer {
    if through_link && transfer == Transfer::CopyAndDelete {
        Transfer::Copy
    } else {
        transfer
    }
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
        pending: &mut Vec<(usize, Transfer)>,
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
            let mut through_link = false;
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
                            through_link = true;
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
                    pending.push((plan.dirs.len() - 1, inside(self.transfer, through_link)));
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

/// What a walk of a tree found.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Tree {
    /// Its size for a file or a link, the sum of every file in it for a
    /// folder.
    pub(crate) bytes: u64,
    /// The length of the longest path in it (its own included), in UTF-16
    /// units, the way Windows counts a path against `MAX_PATH`.
    pub(crate) longest_path: usize,
}

/// Walks the tree under `path`. Links are not followed.
pub(crate) fn measure_tree(path: &str, stop: &dyn Stop) -> Result<Tree, PlanError> {
    let info = root(path)?;
    let mut tree = Tree {
        bytes: 0,
        longest_path: path.encode_utf16().count(),
    };
    if info.link.is_some() || !info.is_dir {
        tree.bytes = info.size;
        return Ok(tree);
    }
    let mut pending = vec![path.to_owned()];
    while let Some(dir) = pending.pop() {
        if stop.stop() {
            return Err(PlanError::Cancelled);
        }
        let Ok(listing) = list_directory(&dir, &listing_options()) else {
            continue;
        };
        let dir_units = dir.trim_end_matches('\\').encode_utf16().count();
        for entry in listing.entries() {
            tree.longest_path = tree
                .longest_path
                .max(dir_units + 1 + listing.name(entry).len());
            match entry.kind {
                EntryKind::Directory => pending.push(join(&dir, &listing.name_string(entry))),
                EntryKind::ReparsePoint => {}
                EntryKind::File | EntryKind::Unknown => tree.bytes += entry.meta.size,
            }
        }
    }
    Ok(tree)
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
        FsError::AlreadyExists { .. } => crate::win::code::ALREADY_EXISTS,
        FsError::Io { source, .. } => source.raw_os_error().map_or(0, i32::cast_unsigned),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn junction(link: &std::path::Path, target: &std::path::Path) {
        let output = std::process::Command::new("cmd")
            .args(["/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .unwrap();
        assert!(output.status.success(), "{output:?}");
    }

    #[test]
    fn a_move_that_follows_a_link_never_deletes_through_it() {
        let dir = tempfile::Builder::new()
            .prefix("plan-follow")
            .tempdir_in(crate::test_dir())
            .unwrap();
        let target = dir.path().join("target");
        std::fs::create_dir_all(target.join("deep")).unwrap();
        std::fs::write(target.join("c.txt"), "c").unwrap();
        std::fs::write(target.join("deep").join("d.txt"), "d").unwrap();
        let source = dir.path().join("src");
        std::fs::create_dir_all(source.join("sub")).unwrap();
        std::fs::write(source.join("a.txt"), "a").unwrap();
        std::fs::write(source.join("sub").join("b.txt"), "b").unwrap();
        junction(&source.join("link"), &target);
        let stop = || false;

        // Inside a moved folder: its own files are moved, what the link
        // leads to is only copied; the link's own folder item goes at the
        // end (removing a link removes the link alone).
        let mut plan = Plan::default();
        let text = |path: &std::path::Path| path.display().to_string();
        add_copy_root(
            &mut plan,
            &text(&source),
            "src",
            None,
            Transfer::CopyAndDelete,
            LinkPolicy::FollowTarget,
            &stop,
        )
        .unwrap();
        for file in &plan.files {
            let through_link = file.source.contains(r"\link\");
            let wanted = if through_link {
                Transfer::Copy
            } else {
                Transfer::CopyAndDelete
            };
            assert_eq!(file.transfer, wanted, "{}", file.source);
        }
        assert_eq!(plan.files.len(), 4);
        for folder in &plan.dirs {
            let wanted = !folder.source.contains(r"\link\");
            assert_eq!(folder.remove_source, wanted, "{}", folder.source);
        }

        // A link given as the source, followed: the same.
        let mut plan = Plan::default();
        add_copy_root(
            &mut plan,
            &text(&source.join("link")),
            "link",
            None,
            Transfer::CopyAndDelete,
            LinkPolicy::FollowTarget,
            &stop,
        )
        .unwrap();
        assert_eq!(plan.files.len(), 2);
        assert!(
            plan.files
                .iter()
                .all(|file| file.transfer == Transfer::Copy)
        );
        assert!(plan.dirs[0].remove_source, "the link itself goes");
        assert!(plan.dirs[1..].iter().all(|folder| !folder.remove_source));
    }

    #[test]
    fn names_and_joins() {
        assert_eq!(file_name(r"C:\a\b.txt"), "b.txt");
        assert_eq!(file_name(r"C:\a\dir\"), "dir");
        assert_eq!(join(r"C:\a", "b"), r"C:\a\b");
        assert_eq!(join(r"E:\", "b"), r"E:\b");
    }
}
