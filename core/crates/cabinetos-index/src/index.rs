//! The index of one volume: every file and directory, keyed by file reference
//! number (FRN), with its parent, name and attributes. Paths are not stored;
//! they are rebuilt by walking parents up to the root, so a renamed or moved
//! directory needs one update, not one per descendant.

use hashbrown::HashMap;

use crate::names::{self, NameTable};
use crate::record::{UsnRecord, reason};

/// A file reference number: MFT segment (low 48 bits) and sequence number
/// (high 16 bits). The sequence number changes when a segment is reused, so
/// a stale reference never matches a new file.
pub type Frn = u64;

/// The MFT segment of the root directory.
pub const ROOT_SEGMENT: u64 = 5;

/// Segments below this are NTFS's own files (`$MFT`, `$Extend`, …); they and
/// everything under them stay out of search results.
const FIRST_USER_SEGMENT: u64 = 16;

/// Deepest parent chain followed; a longer one means a loop or damage.
const MAX_DEPTH: usize = 1024;

/// A slot without an entry, ready for reuse.
const FREE: u32 = u32::MAX;

const DIRECTORY: u32 = 0x10;

/// The MFT segment of `frn`.
#[must_use]
pub const fn segment(frn: Frn) -> u64 {
    frn & 0x0000_FFFF_FFFF_FFFF
}

/// What an index holds about one entry.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EntryInfo {
    /// Its parent directory.
    pub parent: Frn,
    /// Its name.
    pub name: String,
    /// `FILE_ATTRIBUTE_*` bits.
    pub attributes: u32,
}

/// Every file and directory of one volume.
pub struct VolumeIndex {
    letter: char,
    pub(crate) names: NameTable,
    /// FRN → slot in the arrays below.
    slots: HashMap<Frn, u32>,
    pub(crate) frn: Vec<Frn>,
    pub(crate) parent: Vec<Frn>,
    /// Name ID, or [`FREE`].
    pub(crate) name: Vec<u32>,
    pub(crate) attributes: Vec<u32>,
    /// Slots of removed entries, reused first.
    free: Vec<u32>,
}

impl std::fmt::Debug for VolumeIndex {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VolumeIndex")
            .field("letter", &self.letter)
            .field("entries", &self.len())
            .field("names", &self.names.len())
            .finish_non_exhaustive()
    }
}

impl VolumeIndex {
    /// An empty index of the volume with drive letter `letter`.
    #[must_use]
    pub fn new(letter: char) -> Self {
        Self {
            letter: letter.to_ascii_uppercase(),
            names: NameTable::default(),
            slots: HashMap::new(),
            frn: Vec::new(),
            parent: Vec::new(),
            name: Vec::new(),
            attributes: Vec::new(),
            free: Vec::new(),
        }
    }

    /// The volume's drive letter, upper case.
    #[must_use]
    pub fn letter(&self) -> char {
        self.letter
    }

    /// How many entries it holds.
    #[must_use]
    pub fn len(&self) -> usize {
        self.slots.len()
    }

    /// Whether it holds no entry.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.slots.is_empty()
    }

    /// How many distinct names its entries use.
    #[must_use]
    pub fn distinct_names(&self) -> usize {
        self.names.len()
    }

    /// Adds the entry, or updates it when `frn` is known.
    pub fn upsert(&mut self, frn: Frn, parent: Frn, name: &[u16], attributes: u32) {
        let id = self.names.intern(name);
        if let Some(&slot) = self.slots.get(&frn) {
            let slot = slot as usize;
            self.parent[slot] = parent;
            self.name[slot] = id;
            self.attributes[slot] = attributes;
            return;
        }
        let slot = if let Some(slot) = self.free.pop() {
            let index = slot as usize;
            self.frn[index] = frn;
            self.parent[index] = parent;
            self.name[index] = id;
            self.attributes[index] = attributes;
            slot
        } else {
            let slot = u32::try_from(self.frn.len()).expect("fewer than 4 G entries");
            self.frn.push(frn);
            self.parent.push(parent);
            self.name.push(id);
            self.attributes.push(attributes);
            slot
        };
        self.slots.insert(frn, slot);
    }

    /// Removes the entry; `false` when it was not there.
    pub fn remove(&mut self, frn: Frn) -> bool {
        let Some(slot) = self.slots.remove(&frn) else {
            return false;
        };
        self.name[slot as usize] = FREE;
        self.free.push(slot);
        true
    }

    /// Adds the entry an MFT enumeration returned.
    pub fn insert_record(&mut self, record: &UsnRecord<'_>) {
        let mut units = [0u16; 512];
        if record.name_len() <= units.len() {
            let name = record.name_into(&mut units);
            self.upsert(record.frn, record.parent, name, record.attributes);
        } else {
            let mut units = vec![0u16; record.name_len()];
            let name = record.name_into(&mut units);
            self.upsert(record.frn, record.parent, name, record.attributes);
        }
    }

    /// Applies a change journal record: a delete removes the entry; a
    /// rename's first half (the old name) is skipped, since the second half
    /// follows; so is a hard link change, which names only one link. Every
    /// other record says how the entry looks now, and is stored as such.
    pub fn apply(&mut self, record: &UsnRecord<'_>) {
        let bits = record.reason;
        if bits & reason::FILE_DELETE != 0 {
            self.remove(record.frn);
        } else if bits & reason::RENAME_OLD_NAME != 0 && bits & reason::RENAME_NEW_NAME == 0 {
            // The old name; the new one follows in its own record.
        } else if bits & reason::HARD_LINK_CHANGE != 0 && bits & reason::FILE_CREATE == 0 {
            // A link came or went; the file keeps the name it is indexed under.
        } else {
            self.insert_record(record);
        }
    }

    /// What the index holds about `frn`.
    #[must_use]
    pub fn entry(&self, frn: Frn) -> Option<EntryInfo> {
        let slot = *self.slots.get(&frn)? as usize;
        Some(EntryInfo {
            parent: self.parent[slot],
            name: String::from_utf16_lossy(self.names.units(self.name[slot])),
            attributes: self.attributes[slot],
        })
    }

    /// The slot of `frn`.
    pub(crate) fn slot(&self, frn: Frn) -> Option<u32> {
        self.slots.get(&frn).copied()
    }

    /// Whether the slot holds an entry that may appear in search results:
    /// not a free slot, and not one of NTFS's own files.
    pub(crate) fn searchable(&self, slot: usize) -> bool {
        self.name[slot] != FREE && segment(self.frn[slot]) >= FIRST_USER_SEGMENT
    }

    /// Whether the entry is a directory.
    pub(crate) fn is_directory(&self, slot: usize) -> bool {
        self.attributes[slot] & DIRECTORY != 0
    }

    /// The full path of `frn`, such as `C:\Users\me\a.txt`; `None` when it
    /// is not indexed, is one of NTFS's own files, or its parents do not lead
    /// to the root.
    #[must_use]
    pub fn path(&self, frn: Frn) -> Option<String> {
        if segment(frn) == ROOT_SEGMENT {
            return Some(format!("{}:\\", self.letter));
        }
        self.path_of_slot(self.slot(frn)? as usize)
    }

    /// The full path of the entry in `slot`; see [`path`](Self::path).
    pub(crate) fn path_of_slot(&self, slot: usize) -> Option<String> {
        if !self.searchable(slot) {
            return None;
        }
        let mut chain = Vec::with_capacity(16);
        let mut current = slot;
        loop {
            chain.push(self.name[current]);
            let parent = self.parent[current];
            if segment(parent) == ROOT_SEGMENT {
                break;
            }
            if segment(parent) < FIRST_USER_SEGMENT || chain.len() >= MAX_DEPTH {
                return None;
            }
            current = self.slot(parent)? as usize;
        }
        let mut units: Vec<u16> = format!("{}:", self.letter).encode_utf16().collect();
        for id in chain.iter().rev() {
            units.push(u16::from(b'\\'));
            units.extend_from_slice(self.names.units(*id));
        }
        Some(String::from_utf16_lossy(&units))
    }

    /// Whether `slot` lies (at any depth) under the directory `root`.
    /// `memo` remembers the answer for every directory passed on the way,
    /// so a search that asks for many entries walks each directory once.
    pub(crate) fn is_under(&self, slot: usize, root: Frn, memo: &mut HashMap<Frn, bool>) -> bool {
        let mut passed = Vec::new();
        let mut parent = self.parent[slot];
        let answer = loop {
            if parent == root {
                break true;
            }
            if let Some(&known) = memo.get(&parent) {
                break known;
            }
            if segment(parent) == ROOT_SEGMENT || passed.len() >= MAX_DEPTH {
                break false;
            }
            passed.push(parent);
            match self.slot(parent) {
                Some(next) => parent = self.parent[next as usize],
                None => break false,
            }
        };
        for directory in passed {
            memo.insert(directory, answer);
        }
        answer
    }

    /// Frees unused capacity once a build is done.
    pub fn shrink_to_fit(&mut self) {
        self.names.shrink_to_fit();
        self.slots.shrink_to_fit();
        self.frn.shrink_to_fit();
        self.parent.shrink_to_fit();
        self.name.shrink_to_fit();
        self.attributes.shrink_to_fit();
        self.free.shrink_to_fit();
    }

    /// The heap memory the index holds, in bytes: its arrays, its map and
    /// its names.
    #[must_use]
    pub fn heap_bytes(&self) -> usize {
        self.names.heap_bytes()
            + names::hash_table_bytes(self.slots.capacity(), size_of::<(Frn, u32)>())
            + (self.frn.capacity() + self.parent.capacity()) * size_of::<Frn>()
            + (self.name.capacity() + self.attributes.capacity() + self.free.capacity())
                * size_of::<u32>()
    }

    /// The number of slots, used and free.
    pub(crate) fn slot_count(&self) -> usize {
        self.frn.len()
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::record::{self, encode};

    pub(crate) const ROOT: Frn = 0x0005_0000_0000_0005;

    pub(crate) fn utf16(text: &str) -> Vec<u16> {
        text.encode_utf16().collect()
    }

    /// A small tree:
    /// `C:\Users\me\notes.txt`, `C:\Users\me\Photos\cat.jpg`, `C:\$MFT`.
    pub(crate) fn sample() -> VolumeIndex {
        let mut index = VolumeIndex::new('c');
        index.upsert(ROOT, ROOT, &utf16("."), DIRECTORY);
        index.upsert(0x0000_0000_0000_0000, ROOT, &utf16("$MFT"), 0x6);
        index.upsert(0x0001_0000_0000_0100, ROOT, &utf16("Users"), DIRECTORY);
        index.upsert(
            0x0001_0000_0000_0101,
            0x0001_0000_0000_0100,
            &utf16("me"),
            DIRECTORY,
        );
        index.upsert(
            0x0001_0000_0000_0102,
            0x0001_0000_0000_0101,
            &utf16("notes.txt"),
            0x20,
        );
        index.upsert(
            0x0001_0000_0000_0103,
            0x0001_0000_0000_0101,
            &utf16("Photos"),
            DIRECTORY,
        );
        index.upsert(
            0x0001_0000_0000_0104,
            0x0001_0000_0000_0103,
            &utf16("cat.jpg"),
            0x20,
        );
        index
    }

    #[test]
    fn paths_are_rebuilt_from_parents() {
        let index = sample();
        assert_eq!(index.letter(), 'C');
        assert_eq!(index.len(), 7);
        assert_eq!(index.path(ROOT).as_deref(), Some("C:\\"));
        assert_eq!(
            index.path(0x0001_0000_0000_0104).as_deref(),
            Some(r"C:\Users\me\Photos\cat.jpg")
        );
        assert_eq!(
            index.path(0).as_deref(),
            None,
            "NTFS's own files stay hidden"
        );
        assert_eq!(index.path(0x9999), None);
    }

    #[test]
    fn an_orphan_or_a_loop_has_no_path() {
        let mut index = sample();
        index.upsert(
            0x0001_0000_0000_0200,
            0x0001_0000_0000_0999,
            &utf16("lost.txt"),
            0x20,
        );
        assert_eq!(index.path(0x0001_0000_0000_0200), None);
        index.upsert(
            0x0001_0000_0000_0300,
            0x0001_0000_0000_0301,
            &utf16("a"),
            DIRECTORY,
        );
        index.upsert(
            0x0001_0000_0000_0301,
            0x0001_0000_0000_0300,
            &utf16("b"),
            DIRECTORY,
        );
        assert_eq!(index.path(0x0001_0000_0000_0300), None);
        // An entry under $Extend (segment 11) is NTFS's own.
        index.upsert(
            0x0001_0000_0000_0400,
            0x000B_0000_0000_000B,
            &utf16("$UsnJrnl"),
            0x26,
        );
        assert_eq!(index.path(0x0001_0000_0000_0400), None);
    }

    fn apply(index: &mut VolumeIndex, bytes: &[u8]) {
        let buffer = encode::buffer(0, &[bytes.to_vec()]);
        for parsed in record::records(&buffer) {
            index.apply(&parsed.unwrap());
        }
    }

    #[test]
    fn journal_records_create_rename_move_and_delete() {
        let mut index = sample();
        let me = 0x0001_0000_0000_0101;
        let photos = 0x0001_0000_0000_0103;
        let new = 0x0002_0000_0000_0500;
        apply(
            &mut index,
            &encode::v2(new, me, 10, reason::FILE_CREATE, 0x20, "draft.txt"),
        );
        assert_eq!(index.path(new).as_deref(), Some(r"C:\Users\me\draft.txt"));

        // A rename arrives as the old name, then the new one.
        apply(
            &mut index,
            &encode::v2(new, me, 11, reason::RENAME_OLD_NAME, 0x20, "draft.txt"),
        );
        assert_eq!(index.path(new).as_deref(), Some(r"C:\Users\me\draft.txt"));
        apply(
            &mut index,
            &encode::v2(new, me, 12, reason::RENAME_NEW_NAME, 0x20, "final.txt"),
        );
        assert_eq!(index.path(new).as_deref(), Some(r"C:\Users\me\final.txt"));

        // A move is a rename with another parent.
        apply(
            &mut index,
            &encode::v2(
                new,
                photos,
                13,
                reason::RENAME_NEW_NAME | reason::CLOSE,
                0x20,
                "final.txt",
            ),
        );
        assert_eq!(
            index.path(new).as_deref(),
            Some(r"C:\Users\me\Photos\final.txt")
        );

        // Renaming a directory moves everything under it at once.
        apply(
            &mut index,
            &encode::v2(
                photos,
                me,
                14,
                reason::RENAME_NEW_NAME,
                DIRECTORY,
                "Pictures",
            ),
        );
        assert_eq!(
            index.path(new).as_deref(),
            Some(r"C:\Users\me\Pictures\final.txt")
        );

        apply(
            &mut index,
            &encode::v2(
                new,
                photos,
                15,
                reason::FILE_DELETE | reason::CLOSE,
                0x20,
                "final.txt",
            ),
        );
        assert_eq!(index.path(new), None);
        assert_eq!(index.len(), 7);

        // A record for a file the index never saw adds it (a change that
        // raced the enumeration).
        apply(
            &mut index,
            &encode::v2(
                0x0003_0000_0000_0600,
                me,
                16,
                reason::BASIC_INFO_CHANGE,
                0x20,
                "late.txt",
            ),
        );
        assert_eq!(
            index.path(0x0003_0000_0000_0600).as_deref(),
            Some(r"C:\Users\me\late.txt")
        );
        // A hard link change does not rename the file.
        apply(
            &mut index,
            &encode::v2(
                0x0003_0000_0000_0600,
                photos,
                17,
                reason::HARD_LINK_CHANGE,
                0x20,
                "link.txt",
            ),
        );
        assert_eq!(
            index.path(0x0003_0000_0000_0600).as_deref(),
            Some(r"C:\Users\me\late.txt")
        );
    }

    #[test]
    fn removed_slots_are_reused() {
        let mut index = sample();
        let slots = index.slot_count();
        assert!(index.remove(0x0001_0000_0000_0102));
        assert!(!index.remove(0x0001_0000_0000_0102));
        index.upsert(0x0002_0000_0000_0700, ROOT, &utf16("new.txt"), 0x20);
        assert_eq!(index.slot_count(), slots);
        assert_eq!(index.entry(0x0002_0000_0000_0700).unwrap().name, "new.txt");
        assert_eq!(index.entry(0x0001_0000_0000_0102), None);
    }

    #[test]
    fn ancestry_checks_remember_directories() {
        let index = sample();
        let mut memo = HashMap::new();
        let cat = index.slot(0x0001_0000_0000_0104).unwrap() as usize;
        let notes = index.slot(0x0001_0000_0000_0102).unwrap() as usize;
        assert!(index.is_under(cat, 0x0001_0000_0000_0103, &mut memo));
        assert!(!index.is_under(notes, 0x0001_0000_0000_0103, &mut memo));
        let mut memo = HashMap::new();
        assert!(index.is_under(cat, ROOT, &mut memo));
        assert!(index.is_under(notes, 0x0001_0000_0000_0100, &mut memo));
        assert!(memo.contains_key(&0x0001_0000_0000_0101));
    }

    #[test]
    fn long_names_are_kept_whole() {
        let mut index = VolumeIndex::new('D');
        let long = "x".repeat(600);
        let buffer = encode::buffer(
            0,
            &[encode::v2(0x0001_0000_0000_0100, ROOT, 0, 0, 0x20, &long)],
        );
        for parsed in record::records(&buffer) {
            index.insert_record(&parsed.unwrap());
        }
        assert_eq!(index.path(0x0001_0000_0000_0100).unwrap().len(), 3 + 600);
    }
}
