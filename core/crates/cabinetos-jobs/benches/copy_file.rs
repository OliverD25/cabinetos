//! `CopyFileExW` against a plain `ReadFile`/`WriteFile` loop with 4 MiB
//! buffers and `FILE_FLAG_NO_BUFFERING`, on a 2 GiB file. A measurement for
//! `docs/jobs.md`; the engine adopts nothing from it (`IoRing` is a later
//! spike). Its files live under `%TEMP%\cabinetos-jobs-test\` and are
//! removed at the end.
//!
//! Run: `cargo bench -p cabinetos-jobs --bench copy_file`.
#![allow(unsafe_code)]

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::path::Path;
use std::time::{Duration, Instant};

use criterion::{Criterion, Throughput, criterion_group, criterion_main};
use windows::Win32::Storage::FileSystem::{
    COPY_FILE_NO_BUFFERING, COPYFILE_FLAGS, CopyFileExW, FILE_FLAG_NO_BUFFERING,
};
use windows::core::PCWSTR;

const SIZE: u64 = 2 * 1024 * 1024 * 1024;
const BUFFER: usize = 4 * 1024 * 1024;
/// Sector alignment for unbuffered I/O.
const ALIGN: usize = 4096;

fn wide(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

fn copy_file_ex(source: &Path, destination: &Path, flags: COPYFILE_FLAGS) {
    let (source, destination) = (wide(source), wide(destination));
    // SAFETY: both paths are NUL-terminated and outlive the call; no
    // progress routine and no cancel flag.
    unsafe {
        CopyFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            None,
            None,
            None,
            flags,
        )
    }
    .expect("CopyFileExW");
}

/// Reads and writes 4 MiB at a time, bypassing the file cache on both
/// sides. The file size is a multiple of the buffer, so every transfer is
/// sector aligned.
fn read_write(source: &Path, destination: &Path) {
    let unbuffered = FILE_FLAG_NO_BUFFERING.0;
    let mut input = OpenOptions::new()
        .read(true)
        .custom_flags(unbuffered)
        .open(source)
        .expect("open the source");
    let mut output = OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .custom_flags(unbuffered)
        .open(destination)
        .expect("create the destination");
    let mut storage = vec![0u8; BUFFER + ALIGN];
    let offset = storage.as_ptr().align_offset(ALIGN);
    let buffer = &mut storage[offset..offset + BUFFER];
    loop {
        let read = input.read(buffer).expect("read");
        if read == 0 {
            break;
        }
        output.write_all(&buffer[..read]).expect("write");
    }
}

fn bench(c: &mut Criterion) {
    let root = std::env::temp_dir().join("cabinetos-jobs-test");
    fs::create_dir_all(&root).unwrap();
    let dir = tempfile::Builder::new()
        .prefix("bench")
        .tempdir_in(&root)
        .unwrap();
    let source = dir.path().join("source.bin");
    let destination = dir.path().join("copy.bin");
    {
        let block: Vec<u8> = (0..64 * 1024 * 1024_u32)
            .map(|n| (n.wrapping_mul(2_654_435_761) >> 24) as u8)
            .collect();
        let mut file = File::create(&source).unwrap();
        for _ in 0..SIZE / block.len() as u64 {
            file.write_all(&block).unwrap();
        }
        file.sync_all().unwrap();
    }

    let mut group = c.benchmark_group("copy 2 GiB");
    group
        .sample_size(10)
        .measurement_time(Duration::from_secs(20))
        .throughput(Throughput::Bytes(SIZE));
    let mut timed = |name: &str, copy: &dyn Fn()| {
        group.bench_function(name, |bencher| {
            bencher.iter_custom(|iterations| {
                let mut spent = Duration::ZERO;
                for _ in 0..iterations {
                    let started = Instant::now();
                    copy();
                    spent += started.elapsed();
                    fs::remove_file(&destination).unwrap();
                }
                spent
            });
        });
    };
    timed("CopyFileExW", &|| {
        copy_file_ex(&source, &destination, COPYFILE_FLAGS(0));
    });
    timed("CopyFileExW, COPY_FILE_NO_BUFFERING", &|| {
        copy_file_ex(&source, &destination, COPY_FILE_NO_BUFFERING);
    });
    timed("ReadFile/WriteFile, 4 MiB, FILE_FLAG_NO_BUFFERING", &|| {
        read_write(&source, &destination);
    });
    group.finish();
    drop(dir);
}

criterion_group!(benches, bench);
criterion_main!(benches);
