//! `list_directory` against real directories in the temp folder.

use std::fs::{self, File, OpenOptions};
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
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

/// The fixture's folders beyond ASCII (docs/ui.md, "Edge cases").
const FOLDERS: [&str; 3] = ["Ґанок", "中文文件夹", "📁 photos"];

/// The fixture's files beyond ASCII: Cyrillic, Ukrainian letters, Japanese,
/// surrogate pairs, café composed (NFC) and decomposed (NFD, e + U+0301)
/// side by side, and right to left.
const FILES: [&str; 7] = [
    "Звіт 2026.txt",
    "Їжак і Єнот.md",
    "日本語のファイル.txt",
    "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
    "caf\u{e9}.txt",
    "cafe\u{301}.txt",
    "مستند.txt",
];

/// A name of 255 UTF-16 units, the most NTFS takes.
fn longest_name() -> String {
    "a".repeat(251) + ".txt"
}

fn scratch(prefix: &str) -> tempfile::TempDir {
    let root = std::env::temp_dir().join("cabinetos-fs-test");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix(prefix)
        .tempdir_in(root)
        .unwrap()
}

/// The fixture's names in `dir`; each file holds its own name, so a size
/// tells which file an entry is.
fn names_fixture(dir: &Path) -> Vec<String> {
    for folder in FOLDERS {
        fs::create_dir(dir.join(folder)).unwrap();
    }
    let longest = longest_name();
    assert_eq!(longest.encode_utf16().count(), 255);
    for file in FILES.iter().copied().chain([longest.as_str()]) {
        touch(&dir.join(file), file.as_bytes());
    }
    FOLDERS
        .iter()
        .chain(&FILES)
        .map(|name| (*name).to_owned())
        .chain([longest])
        .collect()
}

fn units(text: &str) -> Vec<u16> {
    text.encode_utf16().collect()
}

#[test]
fn names_beyond_ascii_come_back_unit_for_unit() {
    let dir = scratch("names");
    let made = names_fixture(dir.path());
    let listing = list(dir.path());

    let mut listed: Vec<Vec<u16>> = listing
        .entries()
        .iter()
        .map(|entry| listing.name(entry).to_vec())
        .collect();
    let mut expected: Vec<Vec<u16>> = made.iter().map(|name| units(name)).collect();
    listed.sort();
    expected.sort();
    // NFC and NFD café are two names; nothing is normalized on the way.
    assert_eq!(listed, expected);
    for entry in listing.entries() {
        let name = listing.name_string(entry);
        if entry.kind == EntryKind::File {
            assert_eq!(entry.meta.size, name.len() as u64, "{name}");
        }
    }

    // The same names, unit for unit, through the shared-memory section.
    let writer = ListingWriter::new(&listing).unwrap();
    let mut section = vec![0u8; writer.section_size()];
    writer.write(&mut section, 1).unwrap();
    let reader = ListingReader::new(&section).unwrap();
    let read: Vec<String> = reader.entries().map(|entry| entry.unwrap().name).collect();
    assert_eq!(read, names(&listing));
}

/// Explorer's comparison of two names.
#[allow(unsafe_code)]
fn explorer_order(a: &str, b: &str) -> i32 {
    let a: Vec<u16> = a.encode_utf16().chain([0]).collect();
    let b: Vec<u16> = b.encode_utf16().chain([0]).collect();
    // SAFETY: both strings are NUL-terminated and outlive the call.
    unsafe {
        windows::Win32::UI::Shell::StrCmpLogicalW(
            windows::core::PCWSTR(a.as_ptr()),
            windows::core::PCWSTR(b.as_ptr()),
        )
    }
}

/// Checks that `group` is in Explorer's order, with a tie (names Explorer
/// calls equal) ordered by their UTF-16 units.
fn assert_explorer_order(group: &[String]) {
    for pair in group.windows(2) {
        let order = explorer_order(&pair[0], &pair[1]);
        assert!(
            order < 0 || (order == 0 && units(&pair[0]) < units(&pair[1])),
            "{:?} before {:?}, Explorer says {order}; the whole group: {group:#?}",
            pair[0],
            pair[1]
        );
    }
}

#[test]
fn names_beyond_ascii_sort_as_explorer_sorts_them() {
    let dir = scratch("order");
    names_fixture(dir.path());
    let listing = list(dir.path());
    let listed = names(&listing);
    let (folders, files) = listed.split_at(FOLDERS.len());
    let mut expected_folders: Vec<&str> = FOLDERS.to_vec();
    let mut got_folders: Vec<&str> = folders.iter().map(String::as_str).collect();
    expected_folders.sort_unstable();
    got_folders.sort_unstable();
    assert_eq!(got_folders, expected_folders, "folders come first");
    assert_explorer_order(folders);
    assert_explorer_order(files);
    println!("the order: {listed:#?}");
}

/// A folder where Windows tells names apart by case, or `None` when this
/// user may not make one (`fsutil` needs Windows 10 1803 or later; an
/// older build or a policy may refuse).
fn case_sensitive_folder(parent: &Path) -> Option<PathBuf> {
    let folder = parent.join("case");
    fs::create_dir(&folder).unwrap();
    let output = Command::new("fsutil.exe")
        .args(["file", "setCaseSensitiveInfo"])
        .arg(&folder)
        .arg("enable")
        .output()
        .unwrap();
    if output.status.success() {
        Some(folder)
    } else {
        println!(
            "skipped: fsutil cannot make a case-sensitive folder here: {}",
            String::from_utf8_lossy(&output.stdout).trim()
        );
        None
    }
}

#[test]
fn names_that_differ_only_by_case_are_two_rows_in_a_case_sensitive_folder() {
    let dir = scratch("case");
    let Some(folder) = case_sensitive_folder(dir.path()) else {
        return;
    };
    for (name, text) in [
        ("report.txt", "lower"),
        ("Report.txt", "upper"),
        ("ЗВІТ.txt", "ЗВІТ"),
        ("звіт.txt", "звіт"),
    ] {
        touch(&folder.join(name), text.as_bytes());
    }
    // Explorer calls each pair equal; the units decide: capitals first.
    assert_eq!(
        names(&list(&folder)),
        ["Report.txt", "report.txt", "ЗВІТ.txt", "звіт.txt"]
    );
}

#[test]
fn a_name_with_a_lone_surrogate_keeps_its_units_in_the_listing() {
    use std::os::windows::ffi::OsStringExt;
    let dir = scratch("surrogate");
    // Not valid UTF-16, but NTFS takes any units: `a`, a lone high
    // surrogate, `.txt`.
    let raw: Vec<u16> = [0x61, 0xD800].into_iter().chain(units(".txt")).collect();
    touch(
        &dir.path().join(std::ffi::OsString::from_wide(&raw)),
        b"odd",
    );
    let listing = list(dir.path());
    assert_eq!(listing.len(), 1);
    assert_eq!(listing.name(&listing.entries()[0]), raw.as_slice());
    // As text (the protocol's JSON), the lone surrogate becomes U+FFFD.
    assert_eq!(listing.name_string(&listing.entries()[0]), "a\u{FFFD}.txt");
}

/// A folder under `parent` whose path is at least `length` characters,
/// made with the verbatim (`\\?\`) form so nothing depends on the machine's
/// long-path policy.
fn deep_folder(parent: &Path, length: usize) -> PathBuf {
    let mut deep = parent.to_path_buf();
    while deep.as_os_str().len() < length {
        deep.push("segment-of-a-long-path-0123456789");
    }
    fs::create_dir_all(format!(r"\\?\{}", deep.display())).unwrap();
    deep
}

#[test]
fn a_long_path_lists_the_same_in_the_plain_and_the_verbatim_form() {
    let dir = scratch("long");
    let deep = deep_folder(dir.path(), 300);
    for name in ["deep file.txt", "Звіт 2026.txt"] {
        touch(
            Path::new(&format!(r"\\?\{}\{name}", deep.display())),
            b"deep",
        );
    }
    let plain = list(&deep);
    let verbatim =
        list_directory(&format!(r"\\?\{}", deep.display()), &ListOptions::default()).unwrap();
    assert_eq!(names(&plain), ["deep file.txt", "Звіт 2026.txt"]);
    assert_eq!(names(&verbatim), names(&plain));
    assert!(deep.join("deep file.txt").as_os_str().len() > 300);
}
