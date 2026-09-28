//! Colour themes (`docs/themes.md`): the JSON theme files in the themes
//! folder, and what `theme_changed` carries to a client. The file and the
//! wire use the same object, so a client applies what the file says.

use serde::{Deserialize, Serialize};

/// A colour theme: `<id>.json` in the themes folder.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct Theme {
    /// Where editors find the schema of the file: the core keeps
    /// `theme.schema.json` next to the themes. The core ignores it.
    #[serde(rename = "$schema", default, skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    /// Lower case letters, digits and `-`, starting with a letter; also the
    /// file's name without `.json`.
    pub id: String,
    /// The name people see in the theme picker.
    pub name: String,
    /// Who made the theme.
    pub author: String,
    /// Where the colours come from and under which license, when they are
    /// someone else's.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub attribution: Option<String>,
    /// `major.minor.patch`.
    pub version: String,
    /// Whether the theme is for dark or light mode.
    pub kind: ThemeKind,
    /// The accent colour; `null` follows the Windows accent colour.
    #[serde(default)]
    pub accent: Option<Rgb>,
    /// A tint over the Mica backdrop; `null` shows plain Mica.
    #[serde(default)]
    pub mica: Option<MicaTint>,
    /// The colours of the window.
    pub palette: Palette,
    /// The colours of the integrated terminal.
    pub terminal: TerminalColors,
}

/// Dark or light.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ThemeKind {
    /// Light text on a dark backdrop.
    Dark,
    /// Dark text on a light backdrop.
    Light,
}

/// A tint laid over the Mica backdrop.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct MicaTint {
    /// The tint's colour.
    pub tint: Rgb,
    /// How much of the tint covers the backdrop: 0 (none) to 1 (all).
    pub opacity: Opacity,
}

/// The colours of the window. Colours with an alpha part (`#RRGGBBAA`) are
/// laid over the backdrop.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct Palette {
    /// Names, titles and everything the user reads first.
    pub text_primary: Color,
    /// Details such as dates, types and sizes.
    pub text_secondary: Color,
    /// Section labels, hints and paths.
    pub text_tertiary: Color,
    /// Commands that cannot run now.
    pub text_disabled: Color,
    /// The fill of panes, cards and sidebar rows.
    pub layer_fill: Color,
    /// Their 1 px border.
    pub layer_stroke: Color,
    /// The border of the focused pane.
    pub layer_stroke_active: Color,
    /// The fill of buttons and text fields.
    pub control_fill: Color,
    /// That fill under the mouse.
    pub control_fill_hover: Color,
    /// The palette, context menus and flyouts (the design's acrylic).
    pub acrylic_tint: Color,
    /// The terminal panel over the backdrop.
    pub terminal_background: Color,
    /// The body of the folder icon.
    pub folder_icon: Color,
    /// The front flap of the folder icon.
    pub folder_icon_front: Color,
    /// The stroke of file icons, by type.
    pub file_type_colors: FileTypeColors,
    /// A capability of level `low` in the review dialog.
    pub permission_low: Color,
    /// A capability of level `medium`.
    pub permission_medium: Color,
    /// A capability of level `high`.
    pub permission_high: Color,
}

/// The stroke colour of a file's icon, by its extension.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields)]
pub struct FileTypeColors {
    /// `.md`
    pub md: Color,
    /// `.rs`
    pub rs: Color,
    /// `.toml`
    pub toml: Color,
    /// `.exe`
    pub exe: Color,
    /// `.dll`
    pub dll: Color,
    /// `.bin`
    pub bin: Color,
    /// `.pdf`
    pub pdf: Color,
    /// `.zip`
    pub zip: Color,
}

/// The colours of the integrated terminal.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields)]
pub struct TerminalColors {
    /// Text in the default colour.
    pub foreground: Color,
    /// The background of the colour scheme: reverse video, and cells drawn
    /// where the panel is opaque.
    pub background: Color,
    /// The cursor.
    pub cursor: Color,
    /// The 16 ANSI colours: black, red, green, yellow, blue, magenta, cyan,
    /// white, then the bright ones in the same order.
    pub ansi: [Color; 16],
}

/// What the theme picker shows of a theme: `list_themes`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ThemeInfo {
    /// The theme's ID: its file's name without `.json`.
    pub id: String,
    /// Its name.
    pub name: String,
    /// Who made it.
    pub author: String,
    /// Its version.
    pub version: String,
    /// Dark or light.
    pub kind: ThemeKind,
    /// Its accent colour; `null` follows the Windows accent colour.
    pub accent: Option<Rgb>,
}

impl From<&Theme> for ThemeInfo {
    fn from(theme: &Theme) -> Self {
        Self {
            id: theme.id.clone(),
            name: theme.name.clone(),
            author: theme.author.clone(),
            version: theme.version.clone(),
            kind: theme.kind,
            accent: theme.accent.clone(),
        }
    }
}

/// An opaque colour, `#RRGGBB`. Read in any case, kept in upper case.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(try_from = "String", into = "String")]
pub struct Rgb(String);

impl Rgb {
    /// The colour as `#RRGGBB`.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for Rgb {
    type Error = String;

    fn try_from(text: String) -> Result<Self, Self::Error> {
        match hex_digits(&text) {
            Some(6) => Ok(Self(text.to_ascii_uppercase())),
            _ => Err(format!("`{text}` is not a colour: use #RRGGBB")),
        }
    }
}

impl From<Rgb> for String {
    fn from(color: Rgb) -> Self {
        color.0
    }
}

/// A colour, `#RRGGBB` or with alpha `#RRGGBBAA` (`00` clear, `FF` solid).
/// Read in any case, kept in upper case.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(try_from = "String", into = "String")]
pub struct Color(String);

impl Color {
    /// The colour as `#RRGGBB` or `#RRGGBBAA`.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for Color {
    type Error = String;

    fn try_from(text: String) -> Result<Self, Self::Error> {
        match hex_digits(&text) {
            Some(6 | 8) => Ok(Self(text.to_ascii_uppercase())),
            _ => Err(format!(
                "`{text}` is not a colour: use #RRGGBB or #RRGGBBAA"
            )),
        }
    }
}

impl From<Color> for String {
    fn from(color: Color) -> Self {
        color.0
    }
}

/// The number of hex digits after the `#`, if the text is `#` and hex
/// digits only.
fn hex_digits(text: &str) -> Option<usize> {
    let digits = text.strip_prefix('#')?;
    digits
        .chars()
        .all(|c| c.is_ascii_hexdigit())
        .then_some(digits.len())
}

/// How much of a tint covers what is under it: from 0 to 1. Never NaN, so
/// it compares like a whole number.
#[derive(Clone, Copy, Debug, PartialEq, PartialOrd, Serialize, Deserialize)]
#[serde(try_from = "f64", into = "f64")]
pub struct Opacity(f64);

impl Eq for Opacity {}

impl Opacity {
    /// The opacity, if `value` is from 0 to 1.
    #[must_use]
    pub fn new(value: f64) -> Option<Self> {
        (0.0..=1.0).contains(&value).then_some(Self(value))
    }

    /// The value, from 0 to 1.
    #[must_use]
    pub const fn get(self) -> f64 {
        self.0
    }
}

impl TryFrom<f64> for Opacity {
    type Error = String;

    fn try_from(value: f64) -> Result<Self, Self::Error> {
        Self::new(value).ok_or_else(|| format!("opacity {value} is not from 0 to 1"))
    }
}

impl From<Opacity> for f64 {
    fn from(opacity: Opacity) -> Self {
        opacity.0
    }
}

#[cfg(feature = "schema")]
mod schema {
    use std::borrow::Cow;

    use schemars::{JsonSchema, Schema, SchemaGenerator, json_schema};

    use super::{Color, Opacity, Rgb};

    impl JsonSchema for Rgb {
        fn schema_name() -> Cow<'static, str> {
            "Rgb".into()
        }

        fn json_schema(_generator: &mut SchemaGenerator) -> Schema {
            json_schema!({
                "description": "An opaque colour, #RRGGBB.",
                "type": "string",
                "pattern": "^#[0-9A-Fa-f]{6}$"
            })
        }
    }

    impl JsonSchema for Color {
        fn schema_name() -> Cow<'static, str> {
            "Color".into()
        }

        fn json_schema(_generator: &mut SchemaGenerator) -> Schema {
            json_schema!({
                "description": "A colour, #RRGGBB, or #RRGGBBAA with alpha (00 clear, FF solid).",
                "type": "string",
                "pattern": "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"
            })
        }
    }

    impl JsonSchema for Opacity {
        fn schema_name() -> Cow<'static, str> {
            "Opacity".into()
        }

        fn json_schema(_generator: &mut SchemaGenerator) -> Schema {
            json_schema!({
                "description": "From 0 (none) to 1 (all).",
                "type": "number",
                "minimum": 0,
                "maximum": 1
            })
        }
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn colours_are_checked_and_kept_in_upper_case() {
        let color: Color = serde_json::from_value(json!("#60cdff")).unwrap();
        assert_eq!(color.as_str(), "#60CDFF");
        let color: Color = serde_json::from_value(json!("#ffffff8b")).unwrap();
        assert_eq!(serde_json::to_value(&color).unwrap(), json!("#FFFFFF8B"));
        for bad in ["60CDFF", "#60CDF", "#60CDFFA", "#GGGGGG", "", "#"] {
            let error = serde_json::from_value::<Color>(json!(bad)).unwrap_err();
            assert!(
                error.to_string().contains("is not a colour"),
                "{bad}: {error}"
            );
        }
        assert!(serde_json::from_value::<Rgb>(json!("#88C0D0")).is_ok());
        assert!(serde_json::from_value::<Rgb>(json!("#88C0D0FF")).is_err());
    }

    #[test]
    fn opacity_is_from_zero_to_one() {
        let opacity: Opacity = serde_json::from_value(json!(0.88)).unwrap();
        assert_eq!(opacity, Opacity::new(0.88).unwrap());
        assert_eq!(serde_json::to_value(opacity).unwrap(), json!(0.88));
        assert!(serde_json::from_value::<Opacity>(json!(1)).is_ok());
        for bad in [json!(-0.1), json!(1.5), json!("0.5")] {
            assert!(
                serde_json::from_value::<Opacity>(bad.clone()).is_err(),
                "{bad}"
            );
        }
    }
}
