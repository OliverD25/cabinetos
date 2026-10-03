//! The marketplace (`docs/marketplace.md`): the two catalogues a client
//! browses, and the Tool Extensions the core installed. An index item on
//! the wire is the item as the catalogue file has it, in the file's own
//! camelCase keys.

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::{CapabilityLevel, ThemeKind};

/// The version of the index format this core reads.
pub const INDEX_SCHEMA_VERSION: u32 = 1;

/// Which of the two lists a client asks for (protocol version 20, ADR 0022).
/// The marketplace publishes extensions and colour themes in separate
/// files, so the window can show them on separate pages.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum Catalogue {
    /// Core Plugins and Tool Extensions: `index.json`
    /// (`marketplace.index`). The default when a request names none.
    #[default]
    Extensions,
    /// Colour themes: `themes.json` (`marketplace.themes`), or the theme
    /// items of `index.json` while the themes address answers 404.
    Themes,
}

impl Catalogue {
    /// Whether an item of `kind` belongs in this catalogue: themes in the
    /// themes catalogue, plugins and tools in the extensions.
    #[must_use]
    pub const fn holds(self, kind: ExtensionKind) -> bool {
        matches!(
            (self, kind),
            (Self::Themes, ExtensionKind::Theme)
                | (
                    Self::Extensions,
                    ExtensionKind::Plugin | ExtensionKind::Tool
                )
        )
    }
}

/// A marketplace catalogue file: `index.json` for extensions, `themes.json`
/// for themes. Both have this format, a static file on a web server or in
/// a folder.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct MarketIndex {
    /// The format of this file; always 1 for now.
    pub schema_version: u32,
    /// When the index was built: an ISO 8601 date and time in UTC, for
    /// example `2026-09-28T02:00:00Z`.
    pub generated_at: String,
    /// What it offers. An index may list several versions of one extension.
    pub items: Vec<MarketItem>,
}

/// One extension, in one version, as the index offers it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "camelCase")]
pub struct MarketItem {
    /// Lower case letters, digits and `-`, starting with a letter: the
    /// plugin's ID, the theme's ID or the tool's ID.
    pub id: String,
    /// What it is.
    pub kind: ExtensionKind,
    /// The name people see.
    pub name: String,
    /// Who publishes it.
    pub author: Author,
    /// `major.minor.patch`.
    pub version: String,
    /// One or two sentences, for the card.
    pub description: String,
    /// The long description, for the detail view.
    #[serde(default)]
    pub long: String,
    /// What users think of it, when the index knows.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rating: Option<Rating>,
    /// How often it was installed, when the index knows.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub installs: Option<u64>,
    /// The size of the download in bytes.
    pub size: u64,
    /// Where to get it and how to check it.
    pub download: Download,
    /// Its own manifest: the plugin's `plugin.json`, the tool's `tool.json`,
    /// or the theme's header (`id`, `name`, `author`, `version`, `kind`,
    /// `accent`).
    pub manifest: Value,
    /// For a plugin: the capabilities it asks for, as its `plugin.json`
    /// lists them. The core adds each one's `level`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub capabilities: Vec<MarketCapability>,
    /// The oldest CabinetOS it runs on, `major.minor.patch`.
    pub min_core_version: String,
    /// Its license, for example `MIT`.
    pub license: String,
    /// The version installed from the marketplace, when it is installed.
    /// The core fills it in; an index leaves it out.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub installed_version: Option<String>,
    /// For a theme: dark, light, or as Windows is set (the theme file's
    /// `kind`), so the gallery can filter without reading the file.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub appearance: Option<ThemeKind>,
    /// For a theme: whether it sets metrics, which makes it a density
    /// preset such as Commander Compact.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub density: Option<bool>,
    /// For a theme: three colours the gallery paints its tile with.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tile: Option<Tile>,
}

/// The three colours of a theme's gallery tile, each `#RRGGBB` (ADR 0022):
/// the list background (the theme's `layerFill` laid over its Mica tint),
/// the primary text (`textPrimary`) and the accent.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Tile {
    /// The tile's fill: the colour behind the rows of a file list.
    pub background: String,
    /// The text colour of the tile's rows and name.
    pub text: String,
    /// The selection pill and the check.
    pub accent: String,
}

impl Tile {
    /// Whether every colour is `#RRGGBB`, which is all the gallery draws.
    #[must_use]
    pub fn is_well_formed(&self) -> bool {
        [&self.background, &self.text, &self.accent]
            .into_iter()
            .all(|color| is_rgb(color))
    }
}

/// `#RRGGBB`, upper or lower case.
fn is_rgb(text: &str) -> bool {
    text.len() == 7 && text.starts_with('#') && text[1..].chars().all(|c| c.is_ascii_hexdigit())
}

/// What an extension is.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ExtensionKind {
    /// A Core Plugin: a WebAssembly component with its `plugin.json`.
    Plugin,
    /// A colour theme: one JSON file.
    Theme,
    /// A Tool Extension: a folder the Tool Dock hosts.
    Tool,
}

/// Who publishes an extension.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Author {
    /// The publisher's name.
    pub name: String,
    /// Whether the index vouches for the publisher. Shown only; publisher
    /// identities are checked in a later version.
    #[serde(default)]
    pub verified: bool,
    /// The publisher's web page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
}

/// What users think of an extension.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Rating {
    /// The average, from 0 to 5 stars.
    pub average: Stars,
    /// How many ratings the average is of.
    pub count: u64,
}

/// A download.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Download {
    /// An `https:` URL, or a path relative to the index. A plugin is a zip
    /// of `plugin.json` and `plugin.wasm`, or the `plugin.wasm` alone (its
    /// `plugin.json` is then the item's `manifest`); a theme is its JSON
    /// file; a tool is a zip of its folder.
    pub url: String,
    /// The SHA-256 of the download, 64 hex digits. A download that does not
    /// match is deleted and not installed.
    pub sha256: String,
}

/// A capability a plugin asks for, as the index lists it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct MarketCapability {
    /// For example `fs:read`.
    pub name: String,
    /// The plugin's own plain-language reason.
    pub reason: String,
    /// The folders it concerns, for `fs:read` and `fs:write`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub roots: Vec<String>,
    /// The hosts it may reach, for `net`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub hosts: Vec<String>,
    /// The secrets the core may put into its requests, for `net`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub secrets: Vec<String>,
    /// The request types the plugin may send the core, for `core:request`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub requests: Vec<String>,
    /// How much it lets the plugin do. The core fills it in; an index
    /// leaves it out.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub level: Option<CapabilityLevel>,
}

/// An installed Tool Extension: `tools/<id>/tool.json`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ToolInfo {
    /// The tool's ID; also its folder's name.
    pub id: String,
    /// Its name.
    pub name: String,
    /// Its version.
    pub version: String,
    /// Who made it.
    pub author: String,
    /// What it does.
    pub description: String,
    /// Its folder, from which the Tool Dock loads it.
    pub dir: String,
}

/// Stars, from 0 to 5. Never NaN, so it compares like a whole number.
#[derive(Clone, Copy, Debug, PartialEq, PartialOrd, Serialize, Deserialize)]
#[serde(try_from = "f64", into = "f64")]
pub struct Stars(f64);

impl Eq for Stars {}

impl Stars {
    /// The stars, if `value` is from 0 to 5.
    #[must_use]
    pub fn new(value: f64) -> Option<Self> {
        (0.0..=5.0).contains(&value).then_some(Self(value))
    }

    /// The value, from 0 to 5.
    #[must_use]
    pub const fn get(self) -> f64 {
        self.0
    }
}

impl TryFrom<f64> for Stars {
    type Error = String;

    fn try_from(value: f64) -> Result<Self, Self::Error> {
        Self::new(value).ok_or_else(|| format!("a rating of {value} is not from 0 to 5"))
    }
}

impl From<Stars> for f64 {
    fn from(stars: Stars) -> Self {
        stars.0
    }
}

#[cfg(feature = "schema")]
impl schemars::JsonSchema for Stars {
    fn schema_name() -> std::borrow::Cow<'static, str> {
        "Stars".into()
    }

    fn json_schema(_generator: &mut schemars::SchemaGenerator) -> schemars::Schema {
        schemars::json_schema!({
            "description": "From 0 to 5 stars.",
            "type": "number",
            "minimum": 0,
            "maximum": 5
        })
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn an_item_reads_with_its_optional_parts_left_out() {
        let item: MarketItem = serde_json::from_value(json!({
            "id": "hello",
            "kind": "plugin",
            "name": "Hello",
            "author": { "name": "CabinetOS" },
            "version": "0.1.0",
            "description": "Says hello.",
            "size": 1234,
            "download": { "url": "files/hello-0.1.0.zip", "sha256": "ab" },
            "manifest": { "id": "hello" },
            "minCoreVersion": "0.1.0",
            "license": "MIT",
            "somethingNewer": true
        }))
        .unwrap();
        assert_eq!(item.kind, ExtensionKind::Plugin);
        assert!(!item.author.verified);
        assert_eq!((&item.rating, item.installs), (&None, None));
        assert_eq!(item.installed_version, None);
        assert!(item.long.is_empty() && item.capabilities.is_empty());
        let wire = serde_json::to_value(&item).unwrap();
        assert_eq!(wire["minCoreVersion"], "0.1.0");
        assert!(wire.get("rating").is_none() && wire.get("somethingNewer").is_none());
    }

    #[test]
    fn a_theme_item_carries_its_appearance_density_and_tile() {
        let mut value = json!({
            "id": "nord",
            "kind": "theme",
            "name": "Nord",
            "author": { "name": "CabinetOS" },
            "version": "1.0.0",
            "description": "A dark colour theme.",
            "size": 3000,
            "download": { "url": "files/nord-1.0.0.json", "sha256": "ab" },
            "manifest": { "id": "nord" },
            "minCoreVersion": "0.1.0",
            "license": "MIT",
            "appearance": "system",
            "density": true,
            "tile": { "background": "#353B49", "text": "#ECEFF4", "accent": "#88C0D0" }
        });
        let item: MarketItem = serde_json::from_value(value.clone()).unwrap();
        assert_eq!(item.appearance, Some(ThemeKind::System));
        assert_eq!(item.density, Some(true));
        let tile = item.tile.unwrap();
        assert!(tile.is_well_formed());
        for bad in ["353B49", "#353B4", "#353B49FF", "#GGGGGG", "#35 B49", ""] {
            let tile = Tile {
                background: bad.to_owned(),
                ..tile.clone()
            };
            assert!(!tile.is_well_formed(), "{bad}");
        }
        value["appearance"] = json!("sepia");
        assert!(
            serde_json::from_value::<MarketItem>(value).is_err(),
            "an unknown appearance is not read"
        );
    }

    #[test]
    fn a_catalogue_is_named_in_lower_case_and_defaults_to_the_extensions() {
        assert_eq!(Catalogue::default(), Catalogue::Extensions);
        assert_eq!(
            serde_json::to_value(Catalogue::Themes).unwrap(),
            json!("themes")
        );
        let read: Catalogue = serde_json::from_value(json!("extensions")).unwrap();
        assert_eq!(read, Catalogue::Extensions);
        // Each kind belongs to exactly one catalogue.
        for kind in [
            ExtensionKind::Plugin,
            ExtensionKind::Theme,
            ExtensionKind::Tool,
        ] {
            let holders = [Catalogue::Extensions, Catalogue::Themes]
                .into_iter()
                .filter(|catalogue| catalogue.holds(kind))
                .count();
            assert_eq!(holders, 1, "{kind:?}");
        }
        assert!(Catalogue::Themes.holds(ExtensionKind::Theme));
        assert!(Catalogue::Extensions.holds(ExtensionKind::Tool));
    }

    #[test]
    fn stars_are_from_zero_to_five() {
        let rating: Rating = serde_json::from_value(json!({"average": 4.8, "count": 120})).unwrap();
        assert_eq!(rating.average, Stars::new(4.8).unwrap());
        assert!(serde_json::from_value::<Stars>(json!(5.5)).is_err());
        assert!(serde_json::from_value::<Stars>(json!(-1)).is_err());
    }
}
