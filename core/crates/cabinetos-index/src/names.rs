//! Every distinct file name of a volume, stored once.
//!
//! A system drive repeats names a lot (`index.js`, `LICENSE`, the side-by-side
//! store), so each entry holds a 4-byte name ID instead of its own copy. A
//! name is kept twice: in UTF-16 as NTFS stores it, for paths, and in
//! lowercase UTF-8, for case-insensitive substring search.

use std::hash::BuildHasher;

use hashbrown::{DefaultHashBuilder, HashTable};

/// Distinct names, by ID (IDs count from 0).
pub(crate) struct NameTable {
    /// Every name back to back, UTF-16.
    units: Vec<u16>,
    /// Name `id` is `units[starts[id]..starts[id + 1]]`.
    starts: Vec<u32>,
    /// Every name lowercased, UTF-8, back to back.
    lower: Vec<u8>,
    /// Name `id` lowercased is `lower[lower_starts[id]..lower_starts[id + 1]]`.
    lower_starts: Vec<u32>,
    /// Name IDs, hashed by their UTF-16 units.
    table: HashTable<u32>,
    hasher: DefaultHashBuilder,
}

impl Default for NameTable {
    fn default() -> Self {
        Self {
            units: Vec::new(),
            starts: vec![0],
            lower: Vec::new(),
            lower_starts: vec![0],
            table: HashTable::new(),
            hasher: DefaultHashBuilder::default(),
        }
    }
}

fn offset(len: usize) -> u32 {
    u32::try_from(len).expect("the name arenas stay under 4 G units")
}

impl NameTable {
    /// How many distinct names there are.
    pub(crate) fn len(&self) -> usize {
        self.starts.len() - 1
    }

    /// The ID of `name`, adding it when it is new.
    pub(crate) fn intern(&mut self, name: &[u16]) -> u32 {
        let Self {
            units,
            starts,
            lower,
            lower_starts,
            table,
            hasher,
        } = self;
        let hash = hasher.hash_one(name);
        let stored = |id: u32| -> &[u16] {
            let id = id as usize;
            &units[starts[id] as usize..starts[id + 1] as usize]
        };
        if let Some(&id) = table.find(hash, |&id| stored(id) == name) {
            return id;
        }
        let id = offset(starts.len() - 1);
        units.extend_from_slice(name);
        starts.push(offset(units.len()));
        lowercase_utf8(name, lower);
        lower_starts.push(offset(lower.len()));
        table.insert_unique(hash, id, |&id| {
            let id = id as usize;
            hasher.hash_one(&units[starts[id] as usize..starts[id + 1] as usize])
        });
        id
    }

    /// The name as NTFS stores it.
    pub(crate) fn units(&self, id: u32) -> &[u16] {
        let id = id as usize;
        &self.units[self.starts[id] as usize..self.starts[id + 1] as usize]
    }

    /// The name lowercased, UTF-8.
    pub(crate) fn lower(&self, id: u32) -> &[u8] {
        let id = id as usize;
        &self.lower[self.lower_starts[id] as usize..self.lower_starts[id + 1] as usize]
    }

    /// Frees the arenas' unused capacity once a build is done.
    pub(crate) fn shrink_to_fit(&mut self) {
        self.units.shrink_to_fit();
        self.starts.shrink_to_fit();
        self.lower.shrink_to_fit();
        self.lower_starts.shrink_to_fit();
        let Self {
            units,
            starts,
            table,
            hasher,
            ..
        } = self;
        table.shrink_to_fit(|&id| {
            let id = id as usize;
            hasher.hash_one(&units[starts[id] as usize..starts[id + 1] as usize])
        });
    }

    /// The heap memory the table holds, in bytes.
    pub(crate) fn heap_bytes(&self) -> usize {
        self.units.capacity() * 2
            + (self.starts.capacity() + self.lower_starts.capacity()) * 4
            + self.lower.capacity()
            + hash_table_bytes(self.table.capacity(), size_of::<u32>())
    }
}

/// The heap bytes of a hashbrown table that can hold `capacity` values of
/// `value_size` bytes: its buckets (a power of two, at most 7/8 full), one
/// control byte per bucket, and one group of control bytes more.
pub(crate) fn hash_table_bytes(capacity: usize, value_size: usize) -> usize {
    if capacity == 0 {
        return 0;
    }
    let buckets = if capacity < 8 {
        (capacity + 1).next_power_of_two()
    } else {
        (capacity * 8 / 7).next_power_of_two()
    };
    buckets * (value_size + 1) + 16
}

/// Appends `name` lowercased, as UTF-8. ASCII names (most of them) take a
/// fast path; others go through Unicode lowercasing, and invalid UTF-16
/// becomes U+FFFD.
pub(crate) fn lowercase_utf8(name: &[u16], out: &mut Vec<u8>) {
    if name.iter().all(|&unit| unit < 0x80) {
        out.extend(name.iter().map(|&unit| {
            u8::try_from(unit)
                .expect("checked: ASCII")
                .to_ascii_lowercase()
        }));
        return;
    }
    let mut buffer = [0u8; 4];
    for decoded in char::decode_utf16(name.iter().copied()) {
        let c = decoded.unwrap_or(char::REPLACEMENT_CHARACTER);
        for lower in c.to_lowercase() {
            out.extend_from_slice(lower.encode_utf8(&mut buffer).as_bytes());
        }
    }
}

/// `text` lowercased the same way as names are.
#[must_use]
pub fn lowercase(text: &str) -> String {
    let units: Vec<u16> = text.encode_utf16().collect();
    let mut out = Vec::with_capacity(text.len());
    lowercase_utf8(&units, &mut out);
    String::from_utf8(out).expect("lowercase_utf8 writes UTF-8")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn utf16(text: &str) -> Vec<u16> {
        text.encode_utf16().collect()
    }

    #[test]
    fn a_name_is_stored_once() {
        let mut names = NameTable::default();
        let a = names.intern(&utf16("index.js"));
        let b = names.intern(&utf16("README.md"));
        assert_eq!(names.intern(&utf16("index.js")), a);
        assert_ne!(a, b);
        assert_eq!(names.len(), 2);
        assert_eq!(String::from_utf16_lossy(names.units(b)), "README.md");
        assert_eq!(names.lower(b), b"readme.md");
        // Case matters for identity: NTFS keeps the name as written.
        assert_ne!(names.intern(&utf16("Index.js")), a);
    }

    #[test]
    fn lookups_survive_growth_and_shrinking() {
        let mut names = NameTable::default();
        let ids: Vec<u32> = (0..5000)
            .map(|n| names.intern(&utf16(&format!("file-{n}.txt"))))
            .collect();
        names.shrink_to_fit();
        for (n, id) in ids.iter().enumerate() {
            assert_eq!(names.intern(&utf16(&format!("file-{n}.txt"))), *id);
        }
        assert_eq!(names.len(), 5000);
        assert!(names.heap_bytes() > 5000 * 12);
    }

    #[test]
    fn lowercasing_covers_unicode_and_bad_utf16() {
        assert_eq!(lowercase("ÄÖÜ Straße.TXT"), "äöü straße.txt");
        assert_eq!(lowercase("ΣΟΦΙΑ"), "σοφια");
        let mut out = Vec::new();
        lowercase_utf8(&[0x0041, 0xD800, 0x0042], &mut out);
        assert_eq!(String::from_utf8(out).unwrap(), "a\u{FFFD}b");
    }

    #[test]
    fn hash_table_sizes_follow_hashbrown() {
        assert_eq!(hash_table_bytes(0, 16), 0);
        assert_eq!(hash_table_bytes(3, 16), 4 * 17 + 16);
        assert_eq!(hash_table_bytes(7, 4), 8 * 5 + 16);
        assert_eq!(hash_table_bytes(14, 16), 16 * 17 + 16);
        assert_eq!(hash_table_bytes(1_000_000, 16), 2_097_152 * 17 + 16);
    }
}
