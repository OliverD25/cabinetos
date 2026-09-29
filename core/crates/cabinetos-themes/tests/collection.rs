//! The theme collection in `sdk/themes/collection`: the marketplace's
//! themes, which the core does not ship. Every file is a valid theme under
//! its own name, none repeats a theme the core ships, and
//! `marketplace.csv` has exactly one row per file, with what the index
//! needs from it. `sdk/marketplace/build-index.ps1 -Collection` refuses the
//! same mistakes, but only when the index is built; here they fail the
//! five checks, so a new theme that breaks them is caught at once.

use std::collections::BTreeSet;
use std::path::PathBuf;

use cabinetos_themes::{SHIPPED, parse};

/// The collection, found from this crate's folder as the shipped themes
/// are: `core/crates/cabinetos-themes` to `sdk/themes/collection`.
fn collection() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../sdk/themes/collection")
}

/// The records of CSV text as RFC 4180 has them (and PowerShell's
/// `Import-Csv` reads them): fields separated by commas; a quoted field may
/// hold commas, line breaks and quotes, which are doubled.
fn csv_records(text: &str) -> Vec<Vec<String>> {
    let mut records = Vec::new();
    let mut record = Vec::new();
    let mut field = String::new();
    let mut quoted = false;
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if quoted {
            match c {
                '"' if chars.peek() == Some(&'"') => {
                    chars.next();
                    field.push('"');
                }
                '"' => quoted = false,
                _ => field.push(c),
            }
            continue;
        }
        match c {
            '"' => quoted = true,
            ',' => record.push(std::mem::take(&mut field)),
            '\r' => {}
            '\n' => {
                record.push(std::mem::take(&mut field));
                records.push(std::mem::take(&mut record));
            }
            _ => field.push(c),
        }
    }
    assert!(!quoted, "a quoted field is never closed");
    if !field.is_empty() || !record.is_empty() {
        record.push(field);
        records.push(record);
    }
    records
}

#[test]
fn the_csv_reader_reads_quotes_as_import_csv_does() {
    let records = csv_records("id,description\r\na,\"one, \"\"two\"\"\nthree\"\nb,plain");
    assert_eq!(
        records,
        [
            vec!["id", "description"],
            vec!["a", "one, \"two\"\nthree"],
            vec!["b", "plain"]
        ]
    );
}

#[test]
fn every_theme_of_the_collection_is_valid_new_and_listed_once() {
    let folder = collection();
    let entries =
        std::fs::read_dir(&folder).unwrap_or_else(|error| panic!("{}: {error}", folder.display()));
    let mut files = BTreeSet::new();
    for entry in entries {
        let path = entry.unwrap().path();
        // Windows and the index script see `.JSON` as `.json` too.
        if !path
            .extension()
            .is_some_and(|extension| extension.eq_ignore_ascii_case("json"))
        {
            continue;
        }
        let id = path.file_stem().unwrap().to_str().unwrap().to_owned();
        let text = std::fs::read_to_string(&path).unwrap();
        // Strict: the theme format, the checks the core makes on a theme
        // file, and the id equal to the file's name.
        let theme =
            parse(&text, Some(&id)).unwrap_or_else(|problem| panic!("{id}.json: {problem}"));
        assert_eq!(theme.id, id);
        assert!(
            !SHIPPED.iter().any(|(shipped, _)| *shipped == id),
            "{id} ships with the core; the collection must not repeat it"
        );
        files.insert(id);
    }
    assert!(!files.is_empty(), "no theme file in {}", folder.display());

    let catalog = std::fs::read_to_string(folder.join("marketplace.csv")).unwrap();
    let records = csv_records(catalog.strip_prefix('\u{feff}').unwrap_or(&catalog));
    let (header, rows) = records.split_first().expect("marketplace.csv is empty");
    assert_eq!(*header, ["id", "license", "source", "description"]);
    let mut listed = BTreeSet::new();
    for row in rows {
        assert_eq!(row.len(), header.len(), "{row:?}");
        let (id, license, description) = (&row[0], &row[1], &row[3]);
        assert!(
            !license.trim().is_empty() && !description.trim().is_empty(),
            "{id}: its row needs a license and a description"
        );
        assert!(listed.insert(id.clone()), "{id} has two rows");
    }
    let without_row: Vec<_> = files.difference(&listed).collect();
    let without_file: Vec<_> = listed.difference(&files).collect();
    assert!(
        without_row.is_empty() && without_file.is_empty(),
        "files without a row in marketplace.csv: {without_row:?}; \
         rows without a file: {without_file:?}"
    );
}
