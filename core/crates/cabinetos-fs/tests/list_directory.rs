//! `list_directory` against real directories in the temp folder.

use std::fs::{self, File, OpenOptions};
use std::os::windows::fs::OpenOptionsExt;
use std::path::Path;
use std::process::Command;

use cabinetos_fs::{FsError, ListOptions, Listing, ListingReader, ListingWriter, list_directory};
use cabinetos_protocol::shm::{EntryKind, ListingEntry};
use cabinetos_protocol::{SortKey, SortSpec};

const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;
const FILE_ATTRIBUTE_SYSTEM: u32 = 0x4;
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;

fn list(path: &Path) -> Listing {
    list_directory(path.to_str().unwrap(), &ListOptions::default()).unwrap()
}

fn names(listing: &Listing) -> Vec<String> {
    listing
        .entries()
        .iter()
        .map(|entry| listing.name_string(entry))
        .collect()
}

fn touch(path: &Path, contents: &[u8]) {
    fs::write(path, contents).unwrap();
}

#[test]
fn lists_files_and_directories_with_metadata() {
    let dir = tempfile::tempdir().unwrap();
    touch(&dir.path().join("b.txt"), b"hello");
    touch(&dir.path().join("a.bin"), &[0u8; 1000]);
    fs::create_dir(dir.path().join("sub")).unwrap();

    let listing = list(dir.path());
    assert_eq!(names(&listing), ["sub", "a.bin", "b.txt"]);
    let entries = listing.entries();
    assert_eq!(entries[0].kind, EntryKind::Directory);
    assert_eq!(entries[1].kind, EntryKind::File);
    assert_eq!(entries[1].meta.size, 1000);
    assert_eq!(entries[2].meta.size, 5);
    for entry in entries {
        // NTFS: real file reference numbers, no name hashes.
        assert_ne!(entry.id, 0);
        assert_eq!(entry.flags & ListingEntry::FLAG_ID_IS_NAME_HASH, 0);
        assert!(entry.meta.modified > 0 && entry.meta.created > 0);
    }
    let ids: std::collections::HashSet<u64> = entries.iter().map(|e| e.id).collect();
    assert_eq!(ids.len(), 3, "IDs are unique");
}

#[test]
fn hidden_and_system_entries_are_listed_only_on_request() {
    let dir = tempfile::tempdir().unwrap();
    touch(&dir.path().join("visible.txt"), b"");
    for (name, attributes) in [
        ("hidden.txt", FILE_ATTRIBUTE_HIDDEN),
        ("system.txt", FILE_ATTRIBUTE_SYSTEM),
    ] {
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .attributes(attributes)
            .open(dir.path().join(name))
            .unwrap();
    }
    fs::create_dir(dir.path().join("hidden-dir")).unwrap();
    let status = Command::new("attrib")
        .arg("+h")
        .arg(dir.path().join("hidden-dir"))
        .status()
        .unwrap();
    assert!(status.success());

    assert_eq!(names(&list(dir.path())), ["visible.txt"]);
    let all = list_directory(
        dir.path().to_str().unwrap(),
        &ListOptions {
            include_hidden: true,
            ..ListOptions::default()
        },
    )
    .unwrap();
    assert_eq!(
        names(&all),
        ["hidden-dir", "hidden.txt", "system.txt", "visible.txt"]
    );
}

#[test]
fn unicode_names_survive_unchanged() {
    let dir = tempfile::tempdir().unwrap();
    let wanted = [
        "Привіт, світе.txt",
        "漢字とかな.md",
        "😀🎉 party",
        "Ґанок і Їжак",
    ];
    for name in wanted {
        touch(&dir.path().join(name), b"x");
    }
    let mut listed = names(&list(dir.path()));
    listed.sort();
    let mut expected: Vec<String> = wanted.iter().map(|s| (*s).to_owned()).collect();
    expected.sort();
    assert_eq!(listed, expected);
}

#[test]
fn paths_longer_than_260_characters_work() {
    let dir = tempfile::tempdir().unwrap();
    let mut long = dir.path().to_path_buf();
    while long.as_os_str().len() < 300 {
        long.push("a-rather-long-directory-name-to-pass-max-path");
    }
    let verbatim = format!(r"\\?\{}", long.display());
    fs::create_dir_all(&verbatim).unwrap();
    touch(Path::new(&format!(r"{verbatim}\deep.txt")), b"deep");
    assert!(long.as_os_str().len() > 260);

    // Without the prefix: list_directory adds it.
    let listing = list(&long);
    assert_eq!(names(&listing), ["deep.txt"]);
    assert_eq!(listing.entries()[0].meta.size, 4);
}

#[test]
fn an_empty_directory_has_no_entries() {
    let dir = tempfile::tempdir().unwrap();
    let listing = list(dir.path());
    assert!(listing.is_empty(), "{:?}", names(&listing));
}

#[test]
fn a_junction_is_a_reparse_point() {
    let dir = tempfile::tempdir().unwrap();
    fs::create_dir(dir.path().join("target")).unwrap();
    let output = Command::new("cmd")
        .args(["/c", "mklink", "/J"])
        .arg(dir.path().join("link"))
        .arg(dir.path().join("target"))
        .output()
        .unwrap();
    assert!(output.status.success(), "mklink /J failed: {output:?}");

    let listing = list(dir.path());
    let link = listing
        .entries()
        .iter()
        .find(|entry| listing.name_string(entry) == "link")
        .unwrap();
    assert_eq!(link.kind, EntryKind::ReparsePoint);
    assert_ne!(link.meta.attributes & FILE_ATTRIBUTE_REPARSE_POINT, 0);
    // A junction has the directory attribute, so it sorts among directories.
    assert_eq!(names(&listing), ["link", "target"]);
}

#[test]
fn a_missing_directory_is_not_found() {
    let dir = tempfile::tempdir().unwrap();
    let missing = dir.path().join("does-not-exist");
    let error = list_directory(missing.to_str().unwrap(), &ListOptions::default()).unwrap_err();
    assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    assert!(error.to_string().contains("does-not-exist"));
}

#[test]
fn a_file_is_an_invalid_path() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("plain.txt");
    touch(&file, b"x");
    let error = list_directory(file.to_str().unwrap(), &ListOptions::default()).unwrap_err();
    assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
}

#[test]
fn a_malformed_path_is_an_invalid_path() {
    let error = list_directory(r"C:\bad|name", &ListOptions::default()).unwrap_err();
    assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
}

#[test]
fn sorts_naturally_with_directories_first() {
    let dir = tempfile::tempdir().unwrap();
    for name in ["file10", "file2", "File1", "file20"] {
        touch(&dir.path().join(name), b"");
    }
    for name in ["dir10", "dir9"] {
        fs::create_dir(dir.path().join(name)).unwrap();
    }
    assert_eq!(
        names(&list(dir.path())),
        ["dir9", "dir10", "File1", "file2", "file10", "file20"]
    );

    let by_name_desc = list_directory(
        dir.path().to_str().unwrap(),
        &ListOptions {
            sort: SortSpec {
                key: SortKey::Name,
                descending: true,
            },
            ..ListOptions::default()
        },
    )
    .unwrap();
    assert_eq!(
        names(&by_name_desc),
        ["dir10", "dir9", "file20", "file10", "file2", "File1"]
    );
}

#[test]
fn many_files_across_several_buffers() {
    let dir = tempfile::tempdir().unwrap();
    for n in 0..3000 {
        File::create(dir.path().join(format!("entry-{n:05}.dat"))).unwrap();
    }
    // A small buffer forces many system calls.
    let listing = list_directory(
        dir.path().to_str().unwrap(),
        &ListOptions {
            buffer_size: 4096,
            ..ListOptions::default()
        },
    )
    .unwrap();
    assert_eq!(listing.len(), 3000);
    assert_eq!(
        listing.name_string(&listing.entries()[0]),
        "entry-00000.dat"
    );
    assert_eq!(
        listing.name_string(&listing.entries()[2999]),
        "entry-02999.dat"
    );
}

#[test]
fn a_real_listing_round_trips_through_a_section() {
    let dir = tempfile::tempdir().unwrap();
    fs::create_dir(dir.path().join("Photos")).unwrap();
    touch(&dir.path().join("Привіт.txt"), b"hello");
    touch(&dir.path().join("notes.md"), &[b'x'; 300]);

    let listing = list(dir.path());
    let writer = ListingWriter::new(&listing).unwrap();
    let mut section = vec![0u8; writer.section_size()];
    writer.write(&mut section, 1).unwrap();

    let reader = ListingReader::new(&section).unwrap();
    assert_eq!(reader.len(), 3);
    let read: Vec<(String, EntryKind, u64)> = reader
        .entries()
        .map(|entry| {
            let entry = entry.unwrap();
            (entry.name, entry.kind, entry.meta.size)
        })
        .collect();
    assert_eq!(
        read,
        [
            ("Photos".to_owned(), EntryKind::Directory, 0),
            ("notes.md".to_owned(), EntryKind::File, 300),
            ("Привіт.txt".to_owned(), EntryKind::File, 5),
        ]
    );
}
