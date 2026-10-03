//! The marketplace's build script against the core's reader (ADR 0022):
//! `sdk/marketplace/build-index.ps1` writes `index.json` and `themes.json`,
//! and the core must accept every item of both. This is the check that the
//! two files follow the format, for whoever publishes them: the script
//! validates as it writes, and this test reads the result the way the core
//! does. Nothing here reaches the network.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use cabinetos_market::{Index, Market, Source, parse_catalogue};
use cabinetos_protocol::{Catalogue, ExtensionKind, MarketIndex, MarketItem, ThemeKind};
use serde_json::Value;

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..")
}

/// Builds both catalogues with the collection into a fresh folder, and
/// returns the folder. The theme collection is what the public site offers.
fn build(into: &Path) {
    let script = repo().join("sdk/marketplace/build-index.ps1");
    let output = Command::new("powershell")
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(&script)
        .arg("-OutDir")
        .arg(into)
        .args(["-Collection", "-ThemesOnly"])
        .output()
        .expect("Windows PowerShell starts");
    assert!(
        output.status.success(),
        "build-index.ps1 failed:\n{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn raw_items(file: &Path) -> Vec<Value> {
    let text = fs::read_to_string(file).unwrap();
    let json: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap();
    json["items"].as_array().unwrap().clone()
}

#[test]
fn both_files_follow_the_format_and_hold_only_their_own_kinds() {
    let scratch = tempfile::tempdir().unwrap();
    build(scratch.path());
    let index_file = scratch.path().join("index.json");
    let themes_file = scratch.path().join("themes.json");

    // Both files have the index's shape (the typed form of the schema).
    for file in [&index_file, &themes_file] {
        let text = fs::read_to_string(file).unwrap();
        let typed: MarketIndex = serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap();
        assert_eq!(typed.schema_version, 1, "{}", file.display());
        assert!(!typed.generated_at.is_empty(), "{}", file.display());
    }

    // -ThemesOnly: the public index has no fixture plugins, and no theme.
    assert!(raw_items(&index_file).is_empty());

    // The core accepts every theme item: none is left out.
    let raw = raw_items(&themes_file);
    let text = fs::read_to_string(&themes_file).unwrap();
    let themes = parse_catalogue(&text, Catalogue::Themes).unwrap();
    assert_eq!(themes.len(), raw.len(), "no item may be left out");
    assert_eq!(
        themes.len(),
        52,
        "the seven shipped themes and the 45 of the collection"
    );
    assert!(themes.iter().all(|item| item.kind == ExtensionKind::Theme));
    assert!(
        themes
            .iter()
            .all(|item| item.appearance.is_some() && item.density.is_some() && item.tile.is_some()),
        "every theme item has its appearance, density and tile"
    );
    assert!(themes.iter().all(|item| {
        item.tile
            .as_ref()
            .is_some_and(cabinetos_protocol::Tile::is_well_formed)
    }));
    assert!(
        raw.iter()
            .all(|item| item["download"]["sha256"].as_str().unwrap().len() == 64)
    );
}

#[test]
fn the_tiles_and_marks_say_what_the_themes_are() {
    let scratch = tempfile::tempdir().unwrap();
    build(scratch.path());
    let text = fs::read_to_string(scratch.path().join("themes.json")).unwrap();
    let themes = parse_catalogue(&text, Catalogue::Themes).unwrap();
    let find = |id: &str| -> &MarketItem {
        themes
            .iter()
            .find(|item| item.id == id)
            .unwrap_or_else(|| panic!("{id} is in the catalogue"))
    };

    // The only density preset is Commander Compact, which follows Windows.
    let dense: Vec<&str> = themes
        .iter()
        .filter(|item| item.density == Some(true))
        .map(|item| item.id.as_str())
        .collect();
    assert_eq!(dense, ["commander-compact"]);
    assert_eq!(
        find("commander-compact").appearance,
        Some(ThemeKind::System)
    );
    assert_eq!(find("default").appearance, Some(ThemeKind::System));

    // Dark and light themes carry their kind; the colours are the theme's.
    assert_eq!(find("nord").appearance, Some(ThemeKind::Dark));
    assert_eq!(find("github-light").appearance, Some(ThemeKind::Light));
    let nord = find("nord").tile.as_ref().unwrap();
    assert_eq!(nord.accent, "#88C0D0", "the theme's accent");
    assert_eq!(nord.text, "#ECEFF4", "textPrimary");
    // layerFill (#3B425280) laid over the Mica tint (#2E3440 at 0.88) over
    // plain dark Mica (#202020): a dark slate, not the raw tint or the fill.
    assert_eq!(nord.background, "#343A47");
    let light = find("github-light").tile.as_ref().unwrap();
    assert!(
        light.background.as_str() > "#C0C0C0",
        "a light theme's tile is light: {}",
        light.background
    );
    assert_eq!(light.accent, "#0969DA");
    // A system theme without an accent shows the design's accent.
    assert_eq!(find("default").tile.as_ref().unwrap().accent, "#60CDFF");
}

#[test]
fn the_built_folder_serves_both_catalogues_to_the_market_client() {
    let scratch = tempfile::tempdir().unwrap();
    build(scratch.path());
    let market = Market::new(
        cabinetos_market::Dirs {
            plugins: scratch.path().join("p"),
            themes: scratch.path().join("t"),
            tools: scratch.path().join("l"),
            market: scratch.path().join("m"),
        },
        "0.1.0",
    );
    let folder = Source::Local(scratch.path().to_path_buf());
    let themes: Index = market
        .fetch_themes(&folder, || Ok(folder.clone()), false)
        .unwrap();
    assert_eq!(themes.items.len(), 52);
    assert!(themes.source.ends_with("themes.json"), "{}", themes.source);
    let extensions = market
        .fetch(&folder, false)
        .unwrap()
        .only(Catalogue::Extensions);
    assert!(extensions.items.is_empty());
}
