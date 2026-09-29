//! Measuring what is under a path: how many files and folders it holds, and
//! how many bytes the files take. The job engine measures an item before
//! it goes to the Recycle Bin (does it fit, is a path in it too long); the
//! core measures folders for the Size column (`measure_paths`).
//!
//! The walk reads each folder with the NT enumeration and does not sort
//! it. Hidden and system entries count. A link is never followed: it counts
//! as the file or folder it looks like, and adds no bytes. A folder that
//! cannot be read counts in `unreadable`, and what is in it is missing from
//! the totals.

use std::os::windows::fs::MetadataExt;

use cabinetos_protocol::shm::EntryKind;

use crate::{DEFAULT_BUFFER_SIZE, FsError, attributes, enumerate};

/// What a walk found.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Tree {
    /// Files, links to files included; the path itself when it is a file.
    pub files: u64,
    /// Folders under the path (not the path itself), links to folders
    /// included.
    pub folders: u64,
    /// The sizes of the files together.
    pub bytes: u64,
    /// Folders that could not be read.
    pub unreadable: u64,
    /// The length of the longest path in it (its own included), in UTF-16
    /// units, the way Windows counts a path against `MAX_PATH`.
    pub longest_path: usize,
}

/// Why a walk gave no result.
#[derive(Debug)]
pub enum MeasureError {
    /// `stop` asked for it.
    Stopped,
    /// The path itself cannot be looked at.
    Root(FsError),
}

/// Walks the tree under `path`. `stop` is asked before each folder is read,
/// and `progress` gets the totals so far after each one.
pub fn measure_tree(
    path: &str,
    stop: &dyn Fn() -> bool,
    progress: &mut dyn FnMut(&Tree),
) -> Result<Tree, MeasureError> {
    let root = std::fs::symlink_metadata(path).map_err(|error| {
        MeasureError::Root(match error.raw_os_error() {
            Some(code) => FsError::from_win32(path, code.cast_unsigned()),
            None => FsError::Io {
                path: path.to_owned(),
                source: error,
            },
        })
    })?;
    let mut tree = Tree {
        longest_path: path.encode_utf16().count(),
        ..Tree::default()
    };
    let is_folder = root.file_attributes() & attributes::DIRECTORY != 0;
    if root.file_type().is_symlink() || !is_folder {
        if !is_folder {
            tree.files = 1;
            tree.bytes = root.file_size();
        }
        return Ok(tree);
    }
    let mut pending = vec![path.to_owned()];
    while let Some(folder) = pending.pop() {
        if stop() {
            return Err(MeasureError::Stopped);
        }
        match enumerate::read_directory(&folder, true, DEFAULT_BUFFER_SIZE) {
            Ok(listing) => {
                let folder_units = folder.trim_end_matches('\\').encode_utf16().count();
                for entry in listing.entries() {
                    tree.longest_path = tree
                        .longest_path
                        .max(folder_units + 1 + listing.name(entry).len());
                    match entry.kind {
                        EntryKind::Directory => {
                            tree.folders += 1;
                            pending.push(join(&folder, &listing.name_string(entry)));
                        }
                        EntryKind::ReparsePoint
                            if entry.meta.attributes & attributes::DIRECTORY != 0 =>
                        {
                            tree.folders += 1;
                        }
                        EntryKind::ReparsePoint => tree.files += 1,
                        EntryKind::File | EntryKind::Unknown => {
                            tree.files += 1;
                            tree.bytes += entry.meta.size;
                        }
                    }
                }
            }
            Err(_) => tree.unreadable += 1,
        }
        progress(&tree);
    }
    Ok(tree)
}

/// `folder\name`, with one separator.
fn join(folder: &str, name: &str) -> String {
    if folder.ends_with(['\\', '/']) {
        format!("{folder}{name}")
    } else {
        format!("{folder}\\{name}")
    }
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::Path;

    use super::*;

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("measure")
            .tempdir_in(root)
            .unwrap()
    }

    fn text(path: &Path) -> String {
        path.to_str().unwrap().to_owned()
    }

    fn measured(path: &Path) -> Tree {
        measure_tree(&text(path), &|| false, &mut |_| {}).unwrap()
    }

    fn junction(link: &Path, target: &Path) {
        let output = std::process::Command::new("cmd")
            .args(["/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .unwrap();
        assert!(output.status.success(), "{output:?}");
    }

    #[test]
    fn files_folders_and_bytes_are_counted_links_are_not_followed() {
        let dir = scratch();
        let root = dir.path().join("root");
        fs::create_dir_all(root.join("a").join("deep")).unwrap();
        fs::create_dir_all(root.join("b")).unwrap();
        fs::write(root.join("one.txt"), "1").unwrap();
        fs::write(root.join("a").join("two.bin"), [0u8; 20]).unwrap();
        fs::write(root.join("a").join("deep").join("Звіт 📁.md"), "300 bytes!").unwrap();
        fs::write(root.join("b").join("hidden.txt"), "hidden").unwrap();
        let hidden = std::process::Command::new("attrib")
            .arg("+h")
            .arg(root.join("b").join("hidden.txt"))
            .output()
            .unwrap();
        assert!(hidden.status.success(), "{hidden:?}");
        // A link to a big folder outside adds a folder and nothing else.
        let outside = dir.path().join("outside");
        fs::create_dir(&outside).unwrap();
        fs::write(outside.join("big.bin"), [0u8; 4096]).unwrap();
        junction(&root.join("link"), &outside);

        let tree = measured(&root);
        assert_eq!(
            (tree.files, tree.folders, tree.bytes, tree.unreadable),
            (4, 4, 1 + 20 + 10 + 6, 0)
        );
        let longest = text(&root.join("a").join("deep").join("Звіт 📁.md"));
        assert_eq!(tree.longest_path, longest.encode_utf16().count());

        // A file is one file; a link as the path is not entered.
        let file = measured(&root.join("a").join("two.bin"));
        assert_eq!((file.files, file.folders, file.bytes), (1, 0, 20));
        let link = measured(&root.join("link"));
        assert_eq!((link.files, link.folders, link.bytes), (0, 0, 0));
        let deep = measured(&root.join("a").join("deep"));
        assert_eq!((deep.files, deep.folders, deep.bytes), (1, 0, 10));
    }

    /// Takes the right to list `folder` from everyone, and gives it back
    /// when dropped, so the temporary folder can go.
    struct Unlistable<'a>(&'a Path);

    impl<'a> Unlistable<'a> {
        fn new(folder: &'a Path) -> Self {
            let output = std::process::Command::new("icacls")
                .arg(folder)
                .args(["/deny", "*S-1-1-0:(RD)"])
                .output()
                .unwrap();
            assert!(output.status.success(), "{output:?}");
            Self(folder)
        }
    }

    impl Drop for Unlistable<'_> {
        fn drop(&mut self) {
            let _ = std::process::Command::new("icacls")
                .arg(self.0)
                .args(["/remove:d", "*S-1-1-0"])
                .output();
        }
    }

    #[test]
    fn progress_follows_each_folder_and_stop_ends_the_walk() {
        let dir = scratch();
        for index in 0..5 {
            let folder = dir.path().join(format!("folder {index}"));
            fs::create_dir(&folder).unwrap();
            fs::write(folder.join("a.txt"), "abc").unwrap();
        }
        let mut seen = Vec::new();
        let tree =
            measure_tree(&text(dir.path()), &|| false, &mut |tree| seen.push(*tree)).unwrap();
        assert_eq!(seen.len(), 6, "the root and five folders");
        assert_eq!(seen.last(), Some(&tree));
        assert!(seen.windows(2).all(|pair| pair[0].files <= pair[1].files));
        assert_eq!((tree.files, tree.folders, tree.bytes), (5, 5, 15));

        let asked = std::cell::Cell::new(0);
        let stop = || {
            asked.set(asked.get() + 1);
            asked.get() > 3
        };
        let stopped = measure_tree(&text(dir.path()), &stop, &mut |_| {});
        assert!(matches!(stopped, Err(MeasureError::Stopped)), "{stopped:?}");
    }

    #[test]
    fn a_path_that_is_not_there_is_an_error_and_a_folder_it_cannot_read_is_counted() {
        let dir = scratch();
        let missing = measure_tree(&text(&dir.path().join("gone")), &|| false, &mut |_| {});
        assert!(
            matches!(missing, Err(MeasureError::Root(FsError::NotFound { .. }))),
            "{missing:?}"
        );
        let root = dir.path().join("root");
        let locked = root.join("locked");
        fs::create_dir_all(&locked).unwrap();
        fs::write(root.join("kept.txt"), "kept").unwrap();
        fs::write(locked.join("secret.txt"), "secret").unwrap();
        let _unlistable = Unlistable::new(&locked);
        let tree = measured(&root);
        assert_eq!(
            (tree.files, tree.folders, tree.bytes, tree.unreadable),
            (1, 1, 4, 1)
        );
    }

    #[test]
    fn a_long_path_is_walked() {
        let dir = scratch();
        let mut deep = dir.path().to_path_buf();
        while deep.as_os_str().len() < 400 {
            deep.push("a-folder-name-of-forty-characters-long-x");
        }
        fs::create_dir_all(&deep).unwrap();
        fs::write(deep.join("far.txt"), "far").unwrap();
        let tree = measured(dir.path());
        assert_eq!((tree.files, tree.bytes), (1, 3));
        assert!(tree.longest_path > 400, "{}", tree.longest_path);
    }
}
