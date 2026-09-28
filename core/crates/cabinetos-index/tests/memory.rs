//! Measures what the index costs per entry, with a counting allocator, on a
//! synthetic volume shaped like a real one: many repeated names (`index.js`,
//! `LICENSE`, `Debug`), many unique ones (`IMG_04711.JPG`), nested folders.
//! The target is under 120 bytes per entry on average (Phase 6).
//!
//! Run with `--nocapture` to see the figures.
#![allow(unsafe_code)]

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicUsize, Ordering};

use cabinetos_index::{Matcher, VolumeIndex};

/// Counts the bytes currently allocated.
struct Counting;

static ALLOCATED: AtomicUsize = AtomicUsize::new(0);

// SAFETY: every call is forwarded to the system allocator with the same
// arguments; the counter only observes sizes.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        // SAFETY: the caller upholds `alloc`'s contract; forwarded as is.
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() {
            ALLOCATED.fetch_add(layout.size(), Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: the caller upholds `dealloc`'s contract; forwarded as is.
        unsafe { System.dealloc(pointer, layout) };
        ALLOCATED.fetch_sub(layout.size(), Ordering::Relaxed);
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        // SAFETY: the caller upholds `alloc_zeroed`'s contract; forwarded.
        let pointer = unsafe { System.alloc_zeroed(layout) };
        if !pointer.is_null() {
            ALLOCATED.fetch_add(layout.size(), Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        // SAFETY: the caller upholds `realloc`'s contract; forwarded as is.
        let moved = unsafe { System.realloc(pointer, layout, new_size) };
        if !moved.is_null() {
            ALLOCATED.fetch_add(new_size, Ordering::Relaxed);
            ALLOCATED.fetch_sub(layout.size(), Ordering::Relaxed);
        }
        moved
    }
}

#[global_allocator]
static GLOBAL: Counting = Counting;

/// A small deterministic generator (xorshift64*), so every run measures the
/// same volume.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next() % bound
    }

    fn pick<'a>(&mut self, choices: &[&'a str]) -> &'a str {
        let at = usize::try_from(self.below(choices.len() as u64)).expect("a small index");
        choices[at]
    }
}

const COMMON_FILES: &[&str] = &[
    "index.js",
    "package.json",
    "README.md",
    "LICENSE",
    "__init__.py",
    "style.css",
    "main.rs",
    "icon.png",
    "desktop.ini",
    "Thumbs.db",
    "index.d.ts",
    "CHANGELOG.md",
    "config.json",
    "manifest.xml",
    "resources.pri",
    "AppxManifest.xml",
    "setup.cfg",
    "Makefile",
    "utils.py",
    "logo.svg",
    "favicon.ico",
    "strings.resw",
    "default.aspx",
    "tsconfig.json",
    ".gitignore",
];
const COMMON_DIRS: &[&str] = &[
    "src",
    "lib",
    "bin",
    "obj",
    "node_modules",
    "assets",
    "images",
    "Debug",
    "Release",
    "x64",
    "en-US",
    "locales",
    "resources",
    "test",
    "docs",
    "__pycache__",
    "dist",
    "build",
    "packages",
    "Microsoft",
    "Common Files",
    "cache",
    "temp",
    "AppData",
];
const WORDS: &[&str] = &[
    "Windows",
    "System",
    "Runtime",
    "Service",
    "Graphics",
    "Network",
    "Security",
    "Media",
    "Storage",
    "Search",
    "Shell",
    "Update",
    "Diagnostics",
    "Speech",
    "Input",
    "Print",
];

/// A file name: about half repeat, half are unique.
fn file_name(random: &mut Random, n: u64) -> String {
    match random.below(20) {
        0..=8 => random.pick(COMMON_FILES).to_owned(),
        9..=11 => format!("IMG_{:05}.JPG", n % 100_000),
        12..=13 => format!("report-{}-{n}.pdf", 2015 + random.below(10)),
        14..=16 => format!(
            "{}.{}.{}.dll",
            random.pick(WORDS),
            random.pick(WORDS),
            n % 997
        ),
        17 => format!("{:016x}.tmp", random.next()),
        _ => format!("chapter_{}_notes_{n}.docx", random.below(40)),
    }
}

fn directory_name(random: &mut Random, n: u64) -> String {
    if random.below(2) == 0 {
        random.pick(COMMON_DIRS).to_owned()
    } else {
        format!("project-{n}")
    }
}

const ROOT: u64 = 0x0005_0000_0000_0005;

#[test]
#[expect(clippy::cast_precision_loss, reason = "figures rounded for display")]
fn an_entry_costs_under_120_bytes() {
    const DIRECTORIES: u64 = 40_000;
    const FILES: u64 = 360_000;
    let mut random = Random(0x5EED_CAB1_4E70_0005);
    let mut units = Vec::with_capacity(256);

    let before = ALLOCATED.load(Ordering::Relaxed);
    let mut index = VolumeIndex::new('C');
    index.upsert(ROOT, ROOT, &[u16::from(b'.')], 0x10);
    for n in 0..DIRECTORIES {
        // Each folder sits under an earlier one, so the tree gets deep.
        let parent = if n < 20 {
            ROOT
        } else {
            0x0001_0000_0000_0000 | (16 + random.below(n))
        };
        units.clear();
        units.extend(directory_name(&mut random, n).encode_utf16());
        index.upsert(0x0001_0000_0000_0000 | (16 + n), parent, &units, 0x10);
    }
    for n in 0..FILES {
        let parent = 0x0001_0000_0000_0000 | (16 + random.below(DIRECTORIES));
        units.clear();
        units.extend(file_name(&mut random, n).encode_utf16());
        index.upsert(
            0x0002_0000_0000_0000 | (16 + DIRECTORIES + n),
            parent,
            &units,
            0x20,
        );
    }
    index.shrink_to_fit();
    drop(units);
    let retained = ALLOCATED.load(Ordering::Relaxed) - before;

    let entries = index.len();
    let measured = retained as f64 / entries as f64;
    let estimated = index.heap_bytes() as f64 / entries as f64;
    println!(
        "{entries} entries, {} distinct names: {retained} bytes held, {measured:.1} bytes per entry (the index's own estimate: {estimated:.1})",
        index.distinct_names()
    );
    assert!(measured < 120.0, "{measured:.1} bytes per entry");
    // The estimate the indexer logs stays close to the truth.
    assert!(
        (measured - estimated).abs() / measured < 0.1,
        "{measured:.1} vs {estimated:.1}"
    );

    let started = std::time::Instant::now();
    let hits = index.search(&Matcher::new("rep").unwrap(), None, 100);
    println!(
        "a 3-letter query over {entries} entries: {} hits in {} µs",
        hits.len(),
        started.elapsed().as_micros()
    );
    assert_eq!(hits.len(), 100);
}
