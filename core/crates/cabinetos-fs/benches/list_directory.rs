//! How fast a directory reaches shared memory, against `std::fs::read_dir`.
//!
//! Fixtures are generated once into `%TEMP%\cabinetos-bench\<n>\` with
//! `File::create` and reused on later runs (a `<n>.complete` marker next to
//! the folder says the folder is whole). The bench never deletes them; delete
//! the folder by hand to regenerate.
//!
//! Run from `core/`: `cargo bench -p cabinetos-fs`. CI only compiles this
//! (`cargo bench --no-run`).

use std::fs::{self, File};
use std::hint::black_box;
use std::path::PathBuf;
use std::time::Duration;

use cabinetos_fs::{ListOptions, ListingWriter, list_directory};
use cabinetos_ipc::SharedSection;
use criterion::{BenchmarkId, Criterion, criterion_group, criterion_main};

/// A folder of `count` empty files with realistic, mixed names: numbered
/// photos and documents, spaces and punctuation, Ukrainian and Japanese.
fn fixture(count: u32) -> PathBuf {
    let root = std::env::temp_dir().join("cabinetos-bench");
    let dir = root.join(count.to_string());
    let marker = root.join(format!("{count}.complete"));
    if marker.exists() {
        return dir;
    }
    fs::create_dir_all(&dir).expect("create the fixture folder");
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
        File::create(dir.join(name)).expect("create a fixture file");
    }
    File::create(&marker).expect("create the fixture marker");
    dir
}

/// The whole path the core takes: read, sort, create a section, write it.
fn publish(path: &str, options: &ListOptions) -> usize {
    let listing = list_directory(path, options).expect("list the fixture");
    let writer = ListingWriter::new(&listing).expect("plan the section");
    let section = SharedSection::create(writer.section_size()).expect("create a section");
    let mut view = section.map().expect("map the section");
    writer
        .write(view.as_mut_slice(), 1)
        .expect("write the section");
    listing.len()
}

/// The baseline: what a plain Rust program would do.
fn std_read_dir(path: &PathBuf) -> usize {
    let mut entries = Vec::new();
    for entry in fs::read_dir(path).expect("read the fixture") {
        let entry = entry.expect("read an entry");
        let metadata = entry.metadata().expect("read metadata");
        entries.push((entry.file_name(), metadata.len(), metadata.modified().ok()));
    }
    entries.len()
}

fn benches(c: &mut Criterion) {
    for count in [1_000_u32, 100_000] {
        let dir = fixture(count);
        let path = dir.to_str().expect("a UTF-8 temp path").to_owned();
        let options = ListOptions::default();

        let mut group = c.benchmark_group(format!("{count}_files"));
        if count >= 100_000 {
            group
                .sample_size(20)
                .measurement_time(Duration::from_secs(10));
        }
        group.bench_function("list_directory", |b| {
            b.iter(|| black_box(list_directory(&path, &options).expect("list")).len());
        });
        group.bench_function("list_and_publish", |b| {
            b.iter(|| black_box(publish(&path, &options)));
        });
        group.bench_function("std_read_dir_metadata", |b| {
            b.iter(|| black_box(std_read_dir(&dir)));
        });
        group.finish();
    }

    let dir = fixture(100_000);
    let path = dir.to_str().expect("a UTF-8 temp path").to_owned();
    let mut group = c.benchmark_group("buffer_size_100000_files");
    group
        .sample_size(20)
        .measurement_time(Duration::from_secs(10));
    for kib in [64_usize, 256, 1024] {
        let options = ListOptions {
            buffer_size: kib * 1024,
            ..ListOptions::default()
        };
        group.bench_with_input(
            BenchmarkId::from_parameter(format!("{kib}KiB")),
            &options,
            |b, options| {
                b.iter(|| black_box(list_directory(&path, options).expect("list")).len());
            },
        );
    }
    group.finish();
}

criterion_group!(list, benches);
criterion_main!(list);
