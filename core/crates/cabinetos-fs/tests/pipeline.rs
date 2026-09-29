//! The pipelined listing writes exactly the section the serial one does,
//! byte for byte, on real folders: the benchmark's 100,000-file fixture,
//! and seeded random folders with directories, hidden files and repeated
//! sizes and times. Random folders live under `%TEMP%\cabinetos-fs-test\`
//! and are removed; the fixture stays in `%TEMP%\cabinetos-bench\`, as the
//! benchmark leaves it.

use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

use cabinetos_fs::{DEFAULT_BUFFER_SIZE, ListOptions, ListingWriter, list_directory};
use cabinetos_protocol::{SortKey, SortSpec};

const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;

/// The section `list_directory` and `ListingWriter` make for `path`. The
/// serial path is written on one thread, as in Phase 2; the pipelined one
/// as the writer chooses.
fn section(path: &Path, options: &ListOptions) -> Vec<u8> {
    let listing = list_directory(path.to_str().unwrap(), options).unwrap();
    let writer = ListingWriter::new(&listing).unwrap();
    let mut section = vec![0u8; writer.section_size()];
    if options.pipelined {
        writer.write(&mut section, 7).unwrap();
    } else {
        writer.write_with_threads(&mut section, 7, 1).unwrap();
    }
    section
}

fn specs() -> Vec<SortSpec> {
    [
        SortKey::Name,
        SortKey::Size,
        SortKey::Modified,
        SortKey::Kind,
        SortKey::Extension,
    ]
    .into_iter()
    .flat_map(|key| {
        [false, true]
            .into_iter()
            .map(move |descending| SortSpec { key, descending })
    })
    .collect()
}

/// Both paths, every order: the same bytes.
fn assert_same_sections(path: &Path, include_hidden: bool, buffer_size: usize) {
    for sort in specs() {
        let serial = ListOptions {
            include_hidden,
            sort,
            buffer_size,
            pipelined: false,
        };
        let pipelined = ListOptions {
            pipelined: true,
            ..serial
        };
        let expected = section(path, &serial);
        let actual = section(path, &pipelined);
        assert_eq!(expected.len(), actual.len(), "{sort:?}: section sizes");
        if let Some(at) = expected.iter().zip(&actual).position(|(x, y)| x != y) {
            panic!(
                "{}: the sections differ at byte {at} for {sort:?}, hidden {include_hidden}, buffer {buffer_size}",
                path.display()
            );
        }
    }
}

/// The benchmark's fixture of 100,000 empty files, made the benchmark's
/// way when it is missing (a `.complete` marker says it is whole).
fn fixture_100k() -> PathBuf {
    let count = 100_000u32;
    let root = std::env::temp_dir().join("cabinetos-bench");
    let dir = root.join(count.to_string());
    let marker = root.join(format!("{count}.complete"));
    if marker.exists() {
        return dir;
    }
    fs::create_dir_all(&dir).unwrap();
    let words = [
        "IMG", "report", "Photo", "draft", "invoice", "notes", "Backup", "data", "Звіт", "фото",
        "資料", "file",
    ];
    let separators = ["_", " ", "-", ""];
    let extensions = ["jpg", "txt", "pdf", "docx", "png"];
    for n in 0..count {
        let name = format!(
            "{}{}{n}.{}",
            words[n as usize % words.len()],
            separators[(n / 3) as usize % separators.len()],
            extensions[(n / 7) as usize % extensions.len()],
        );
        File::create(dir.join(name)).unwrap();
    }
    File::create(&marker).unwrap();
    dir
}

#[test]
fn the_100k_fixture_gives_the_same_section_both_ways() {
    let dir = fixture_100k();
    assert_same_sections(&dir, false, DEFAULT_BUFFER_SIZE);
}

/// A small, seeded generator, so a failure repeats.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        // xorshift64*
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, bound: usize) -> usize {
        usize::try_from(self.next() % u64::try_from(bound).unwrap()).unwrap()
    }

    fn pick<'a>(&mut self, items: &[&'a str]) -> &'a str {
        items[self.below(items.len())]
    }
}

/// A folder of `count` entries with random names (numbers, case, spaces,
/// Unicode, long shared beginnings), some directories and hidden files,
/// and sizes and modification times drawn from a few values, so that many
/// entries tie on them.
fn random_folder(random: &mut Random, count: usize) -> tempfile::TempDir {
    let root = std::env::temp_dir().join("cabinetos-fs-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("pipeline")
        .tempdir_in(root)
        .unwrap();
    let words = [
        "report",
        "Report",
        "photo",
        "IMG_",
        "Звіт",
        "資料",
        "a",
        "notes",
        "a-long-shared-beginning-past-sixteen-key-bytes-",
    ];
    let separators = ["", " ", "_", "-"];
    let base = SystemTime::UNIX_EPOCH + Duration::from_secs(1_700_000_000);
    // NTFS names ignore case, so two names may not differ only in case.
    let mut taken = HashSet::new();
    while taken.len() < count {
        let name = format!(
            "{}{}{}{}",
            random.pick(&words),
            random.pick(&separators),
            random.below(20_000),
            random.pick(&[".txt", ".TXT", "", ".jpg"]),
        );
        if !taken.insert(name.to_lowercase()) {
            continue;
        }
        let path = dir.path().join(&name);
        match random.below(12) {
            0 => fs::create_dir(&path).unwrap(),
            kind => {
                let hidden = if kind == 1 { FILE_ATTRIBUTE_HIDDEN } else { 0 };
                let file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .attributes(hidden)
                    .open(&path)
                    .unwrap();
                file.set_len([0, 1, 100, 4096][random.below(4)]).unwrap();
                let age = Duration::from_secs([0, 60, 86_400][random.below(3)]);
                file.set_modified(base - age).unwrap();
            }
        }
    }
    dir
}

#[test]
fn random_folders_give_the_same_section_both_ways() {
    let mut random = Random(0x5EED_CAB1_0E70_0101);
    // Past the switch to the pipeline, with a few buffers or many.
    for count in [3_000, 7_000] {
        let dir = random_folder(&mut random, count);
        for include_hidden in [false, true] {
            assert_same_sections(dir.path(), include_hidden, DEFAULT_BUFFER_SIZE);
        }
        // 4 KiB buffers: about 35 entries each, so many batches to merge.
        assert_same_sections(dir.path(), true, 4096);
    }
}
