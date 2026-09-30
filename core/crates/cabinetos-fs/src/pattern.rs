//! Name patterns in Total Commander's syntax, for marking rows by pattern
//! and for quick search (`match_entries`). `*` stands for any run of
//! characters and `?` for one; `;` separates patterns, and the patterns
//! after a `|` leave names out. Case is ignored, as Windows ignores it in
//! names. Two rules come from DOS, as in Total Commander and cmd: a
//! pattern ending in `.*` also matches a name without an extension, so
//! `*.*` matches every name; and one ending in a lone `.` matches only
//! names without a dot, so `*.` is every name without an extension.

use crate::{LayoutError, ListingReader, attributes};

/// Parsed patterns: names that match one of `include` (or anything, when
/// there is none) and none of `exclude`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NamePatterns {
    include: Vec<Pattern>,
    exclude: Vec<Pattern>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct Pattern {
    /// The pattern with its case folded.
    glob: Vec<char>,
    /// How its end reads.
    end: End,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum End {
    /// As written.
    Plain,
    /// It ended in `.*`, which may also match nothing after the rest.
    AnyExtension,
    /// It ended in a lone `.`: the name has no dot.
    NoExtension,
}

impl NamePatterns {
    /// Reads `text`: patterns separated by `;`, the ones after the first
    /// `|` left out. Spaces around a pattern are dropped, and an empty
    /// pattern is none. No pattern to include means every name.
    #[must_use]
    pub fn parse(text: &str) -> Self {
        let (include, exclude) = text.split_once('|').unwrap_or((text, ""));
        Self {
            include: patterns(include),
            exclude: patterns(exclude),
        }
    }

    /// Whether `name` matches.
    #[must_use]
    pub fn matches(&self, name: &str) -> bool {
        self.matches_folded(&fold(name))
    }

    fn matches_folded(&self, name: &[char]) -> bool {
        (self.include.is_empty() || self.include.iter().any(|pattern| pattern.matches(name)))
            && !self.exclude.iter().any(|pattern| pattern.matches(name))
    }
}

fn patterns(text: &str) -> Vec<Pattern> {
    text.split(';')
        .map(|pattern| pattern.trim_matches(' '))
        .filter(|pattern| !pattern.is_empty())
        .map(Pattern::new)
        .collect()
}

impl Pattern {
    fn new(text: &str) -> Self {
        let mut glob = fold(text);
        let end = if glob.ends_with(&['.', '*']) {
            End::AnyExtension
        } else if glob.last() == Some(&'.') && glob.iter().any(|&c| c != '.') {
            glob.pop();
            End::NoExtension
        } else {
            End::Plain
        };
        Self { glob, end }
    }

    fn matches(&self, name: &[char]) -> bool {
        match self.end {
            End::Plain => glob(&self.glob, name),
            End::AnyExtension => {
                glob(&self.glob, name) || glob(&self.glob[..self.glob.len() - 2], name)
            }
            End::NoExtension => !name.contains(&'.') && glob(&self.glob, name),
        }
    }
}

/// `text` as characters with the case folded: each one that has a single
/// upper-case form gets it, as Windows compares names.
fn fold(text: &str) -> Vec<char> {
    text.chars().map(fold_char).collect()
}

/// A name as a section holds it (UTF-16 little-endian bytes) into `out`,
/// decoded as `String::from_utf16_lossy` decodes it and folded as [`fold`]
/// folds it. `out` is reused from name to name: a pass over 100,000 names
/// spent most of its 40 ms making three new buffers per name (the speed
/// review of 2026-10-01).
fn fold_utf16_into(name: &[u8], out: &mut Vec<char>) {
    out.clear();
    let units = name
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u16::from_le_bytes(*pair));
    out.extend(
        char::decode_utf16(units)
            .map(|unit| fold_char(unit.unwrap_or(char::REPLACEMENT_CHARACTER))),
    );
}

fn fold_char(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

/// Whether `name` matches `pattern` of `*` and `?`: a walk that remembers
/// the last `*` and lets it take one more character on a mismatch.
fn glob(pattern: &[char], name: &[char]) -> bool {
    let (mut p, mut n) = (0, 0);
    let mut star: Option<(usize, usize)> = None;
    while n < name.len() {
        match pattern.get(p) {
            Some('*') => {
                star = Some((p, n));
                p += 1;
            }
            Some(&c) if c == '?' || c == name[n] => {
                p += 1;
                n += 1;
            }
            _ => match star {
                Some((star_p, star_n)) => {
                    p = star_p + 1;
                    n = star_n + 1;
                    star = Some((star_p, star_n + 1));
                }
                None => return false,
            },
        }
    }
    pattern[p..].iter().all(|&c| c == '*')
}

/// The entries of a listing section whose names match `patterns`, as
/// `[start, count]` ranges of their indexes, with the section's
/// generation. `files_only`: folders (links to folders included) never
/// match. With `first_from`, only the first match at or after that index,
/// going round to the start, as `[index, 1]`: quick search.
pub fn match_entries(
    section: &[u8],
    patterns: &NamePatterns,
    files_only: bool,
    first_from: Option<u32>,
) -> Result<(u32, Vec<[u32; 2]>), LayoutError> {
    let reader = ListingReader::new(section)?;
    let generation = reader.header().generation;
    let count = reader.len();
    let mut folded = Vec::new();
    let mut matching = |index: usize| -> Result<bool, LayoutError> {
        let (name, bits) = reader.name_and_attributes(index)?;
        if files_only && bits & attributes::DIRECTORY != 0 {
            return Ok(false);
        }
        fold_utf16_into(name, &mut folded);
        Ok(patterns.matches_folded(&folded))
    };
    // A section's entry count is a u32, so every index fits.
    let at = |index: usize| u32::try_from(index).unwrap_or(u32::MAX);
    if let Some(from) = first_from {
        let start = usize::try_from(from)
            .ok()
            .filter(|&start| start < count)
            .unwrap_or(0);
        for index in (start..count).chain(0..start) {
            if matching(index)? {
                return Ok((generation, vec![[at(index), 1]]));
            }
        }
        return Ok((generation, Vec::new()));
    }
    let mut ranges: Vec<[u32; 2]> = Vec::new();
    for index in 0..count {
        if !matching(index)? {
            continue;
        }
        let index = at(index);
        match ranges.last_mut() {
            Some([start, length]) if *start + *length == index => *length += 1,
            _ => ranges.push([index, 1]),
        }
    }
    Ok((generation, ranges))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn matching<'a>(patterns: &str, names: &[&'a str]) -> Vec<&'a str> {
        let patterns = NamePatterns::parse(patterns);
        names
            .iter()
            .copied()
            .filter(|name| patterns.matches(name))
            .collect()
    }

    const NAMES: [&str; 9] = [
        "README",
        "readme.md",
        "notes.TXT",
        "a.txt.bak",
        "Makefile",
        "photo 1.jpg",
        "photo 12.jpg",
        ".gitignore",
        "Звіт.txt",
    ];

    #[test]
    fn stars_and_question_marks_ignore_case() {
        assert_eq!(matching("*.txt", &NAMES), ["notes.TXT", "Звіт.txt"]);
        assert_eq!(matching("photo ?.jpg", &NAMES), ["photo 1.jpg"]);
        assert_eq!(matching("PHOTO*", &NAMES), ["photo 1.jpg", "photo 12.jpg"]);
        assert_eq!(matching("звіт*", &NAMES), ["Звіт.txt"]);
        assert_eq!(
            matching("*a*e*", &NAMES),
            ["README", "readme.md", "Makefile"]
        );
        assert_eq!(matching("readme", &NAMES), ["README"]);
        assert!(matching("?", &NAMES).is_empty());
    }

    #[test]
    fn several_patterns_and_the_ones_left_out() {
        assert_eq!(
            matching("*.txt;*.md|readme*", &NAMES),
            ["notes.TXT", "Звіт.txt"]
        );
        assert_eq!(
            matching(" *.txt ; *.md ", &NAMES),
            ["readme.md", "notes.TXT", "Звіт.txt"]
        );
        // Nothing to include: every name, less the ones left out.
        assert_eq!(
            matching("|*.jpg;.*;*.txt*", &NAMES),
            ["README", "readme.md", "Makefile"]
        );
        assert_eq!(matching("", &NAMES).len(), NAMES.len());
        assert_eq!(matching(";;", &NAMES).len(), NAMES.len());
    }

    #[test]
    fn dos_endings_match_names_without_an_extension() {
        assert_eq!(matching("*.*", &NAMES).len(), NAMES.len());
        assert_eq!(matching("readme.*", &NAMES), ["README", "readme.md"]);
        assert_eq!(matching("*.", &NAMES), ["README", "Makefile"]);
        assert_eq!(matching("make*.", &NAMES), ["Makefile"]);
    }

    /// A real folder, listed and written as the core writes it: folders
    /// first, then by name.
    #[test]
    fn a_section_gives_ranges_and_quick_search_goes_round() {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("match")
            .tempdir_in(root)
            .unwrap();
        std::fs::create_dir(dir.path().join("folder.txt")).unwrap();
        for name in ["a.txt", "b.md", "c.txt", "d.jpg", "e.txt", "README"] {
            std::fs::write(dir.path().join(name), name).unwrap();
        }
        let listing =
            crate::list_directory(dir.path().to_str().unwrap(), &crate::ListOptions::default())
                .unwrap();
        let writer = crate::ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 7).unwrap();
        // 0 folder.txt, 1 a.txt, 2 b.md, 3 c.txt, 4 d.jpg, 5 e.txt, 6 README
        let found = |patterns: &str, files_only: bool, first_from: Option<u32>| {
            let patterns = NamePatterns::parse(patterns);
            let (generation, ranges) =
                match_entries(&section, &patterns, files_only, first_from).unwrap();
            assert_eq!(generation, 7);
            ranges
        };
        assert_eq!(found("*.txt", false, None), [[0, 2], [3, 1], [5, 1]]);
        assert_eq!(found("*.txt", true, None), [[1, 1], [3, 1], [5, 1]]);
        assert_eq!(found("*.txt;*.md", false, None), [[0, 4], [5, 1]]);
        assert_eq!(found("*.*", false, None), [[0, 7]]);
        assert_eq!(found("*.txt", true, Some(4)), [[5, 1]]);
        assert_eq!(
            found("*.txt", true, Some(6)),
            [[1, 1]],
            "round to the start"
        );
        assert_eq!(found("*.txt", true, Some(100)), [[1, 1]]);
        assert_eq!(found("r*", false, Some(0)), [[6, 1]]);
        assert!(found("zzz*", false, None).is_empty());
        assert!(found("zzz*", false, Some(3)).is_empty());
    }

    /// The pass over a section decodes and folds each name into one reused
    /// buffer: it must match exactly the names that `matches` matches on the
    /// decoded entries, beyond ASCII and in any case.
    #[test]
    fn a_section_matches_the_names_its_entries_decode_to() {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        std::fs::create_dir_all(&root).unwrap();
        let dir = tempfile::Builder::new()
            .prefix("match-decoded")
            .tempdir_in(root)
            .unwrap();
        std::fs::create_dir(dir.path().join("Звіти")).unwrap();
        for name in NAMES
            .iter()
            .chain(&["ÄRGER.txt", "straße.md", "資料 1.txt", "IMG_7716.JPG"])
        {
            std::fs::write(dir.path().join(name), name).unwrap();
        }
        let listing =
            crate::list_directory(dir.path().to_str().unwrap(), &crate::ListOptions::default())
                .unwrap();
        let writer = crate::ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 1).unwrap();
        let reader = ListingReader::new(&section).unwrap();
        for text in [
            "*.txt",
            "*звіт*",
            "ärger*",
            "STRASSE*",
            "straße*",
            "*1*",
            "img*|*.jpg",
            "*.",
            "",
        ] {
            let patterns = NamePatterns::parse(text);
            for files_only in [false, true] {
                let (_, ranges) = match_entries(&section, &patterns, files_only, None).unwrap();
                let found: Vec<usize> = ranges
                    .iter()
                    .flat_map(|&[start, length]| start as usize..(start + length) as usize)
                    .collect();
                let expected: Vec<usize> = (0..reader.len())
                    .filter(|&index| {
                        let entry = reader.entry(index).unwrap();
                        !(files_only && entry.meta.attributes & attributes::DIRECTORY != 0)
                            && patterns.matches(&entry.name)
                    })
                    .collect();
                assert_eq!(found, expected, "{text:?}, files only {files_only}");
            }
        }
    }

    #[test]
    fn a_long_name_against_many_stars_ends() {
        let long = "a".repeat(2000);
        assert!(!NamePatterns::parse("*a*a*a*a*a*b").matches(&long));
        assert!(NamePatterns::parse("*a*a*a*a*a*").matches(&long));
    }
}
