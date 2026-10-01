//! Colour themes (`docs/themes.md`): the JSON theme files in the themes
//! folder, and what `theme_changed` carries to a client. The file and the
//! wire use the same object, so a client applies what the file says. A
//! theme sets colours, and may set sizes ([`Metrics`]) and turn on elements
//! of the window ([`Chrome`]); never commands, keys or the layout.

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
    /// Whether the theme is for dark mode, light mode, or follows Windows.
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
    /// The sizes that differ from the default theme's: a density preset
    /// such as Commander Compact. Absent: the default theme's sizes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub metrics: Option<Metrics>,
    /// The elements of the window this theme shows or hides. Absent: as
    /// the default theme has them.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub chrome: Option<Chrome>,
}

/// Dark, light, or as Windows is set.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(rename_all = "snake_case")]
pub enum ThemeKind {
    /// Light text on a dark backdrop.
    Dark,
    /// Dark text on a light backdrop.
    Light,
    /// Light or dark as Windows is set; the window decides which, and
    /// follows when the user changes it.
    System,
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
    /// Dark, light, or as Windows is set.
    pub kind: ThemeKind,
    /// Its accent colour; `null` follows the Windows accent colour.
    pub accent: Option<Rgb>,
    /// Its tint over the Mica backdrop; `null` shows plain Mica.
    pub mica: Option<MicaTint>,
    /// Whether it sets any metric: a density preset such as Commander
    /// Compact, which a picker may mark. `false` when an older core leaves
    /// it out.
    #[serde(default)]
    pub has_metrics: bool,
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
            mica: theme.mica.clone(),
            has_metrics: theme
                .metrics
                .as_ref()
                .is_some_and(|metrics| !metrics.is_empty()),
        }
    }
}

/// The version of the theme format, in the schema's `$id`
/// (`urn:cabinetos:theme:2`). Format 1 held the colours; format 2 adds the
/// optional `metrics` and `chrome`, so every format-1 theme is a valid
/// format-2 theme.
pub const THEME_FORMAT: u32 = 2;

/// How a metric is measured.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MetricUnit {
    /// Whole device-independent pixels; a theme writes them without a
    /// fraction (`20`, not `20.0`).
    Px,
    /// A number such as a line height (`1.3`), an opacity (`0.94`) or a
    /// column's weight.
    Ratio,
    /// A share of the window, in percent.
    Percent,
}

/// One metric of the theme format.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MetricSpec {
    /// Its key in `metrics`.
    pub name: &'static str,
    /// How it is measured.
    pub unit: MetricUnit,
    /// The smallest value a theme may give.
    pub min: f64,
    /// The largest value a theme may give.
    pub max: f64,
    /// The default theme's value (`docs/design/README.md`): what a theme
    /// that leaves the metric out gets.
    pub default: f64,
    /// Commander Compact's value (`docs/design/compact/COMPACT_THEME.md`).
    pub compact: f64,
    /// What it sizes.
    pub what: &'static str,
}

/// A metric whose value is a number that need not be whole: a line height,
/// an opacity, a column's weight, a share in percent. Never NaN (JSON has
/// no NaN), so it compares like a whole number. A whole value is written
/// without a fraction (`17`, not `17.0`), as a theme file has it.
#[derive(Clone, Copy, Debug, PartialEq, PartialOrd, Deserialize)]
#[serde(try_from = "f64")]
pub struct Decimal(f64);

impl Serialize for Decimal {
    #[expect(
        clippy::cast_possible_truncation,
        reason = "only a whole value well inside i64's range is cast"
    )]
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let whole = self.0.trunc();
        if (self.0 - whole).abs() < f64::EPSILON && whole.abs() < 1e15 {
            serializer.serialize_i64(whole as i64)
        } else {
            serializer.serialize_f64(self.0)
        }
    }
}

impl Eq for Decimal {}

impl Decimal {
    /// The number, if it is finite.
    #[must_use]
    pub fn new(value: f64) -> Option<Self> {
        value.is_finite().then_some(Self(value))
    }

    /// The value.
    #[must_use]
    pub const fn get(self) -> f64 {
        self.0
    }
}

impl TryFrom<f64> for Decimal {
    type Error = String;

    fn try_from(value: f64) -> Result<Self, Self::Error> {
        Self::new(value).ok_or_else(|| format!("{value} is not a number"))
    }
}

impl From<Decimal> for f64 {
    fn from(value: Decimal) -> Self {
        value.0
    }
}

/// The Rust type of a metric's value.
macro_rules! metric_type {
    (Px) => {
        u16
    };
    (Ratio) => {
        Decimal
    };
    (Percent) => {
        Decimal
    };
}

/// Declares [`Metrics`] and [`METRICS`] from one table, so the file's keys,
/// the bounds, the schema and the docs cannot drift apart.
macro_rules! metrics {
    ($(
        $field:ident $name:literal $unit:ident [$min:literal, $max:literal]
        $default:literal => $compact:literal, $what:literal;
    )+) => {
        /// The sizes a theme changes (`docs/themes.md`, "Metrics and
        /// chrome"): a flat object of named numbers, every one optional. A
        /// metric left out, like the whole object, keeps the default theme's
        /// value. Unknown keys are refused, and so is a value outside the
        /// metric's bounds ([`Metrics::problem`]).
        #[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
        #[serde(deny_unknown_fields)]
        pub struct Metrics {
            $(
                #[doc = $what]
                #[serde(rename = $name, default, skip_serializing_if = "Option::is_none")]
                pub $field: Option<metric_type!($unit)>,
            )+
        }

        /// Every metric of the theme format, in the order of the handout's
        /// "Metrics" section.
        pub const METRICS: &[MetricSpec] = &[
            $(
                MetricSpec {
                    name: $name,
                    unit: MetricUnit::$unit,
                    min: $min,
                    max: $max,
                    default: $default,
                    compact: $compact,
                    what: $what,
                },
            )+
        ];

        impl Metrics {
            /// Each metric this theme sets, with its value, in the order of
            /// [`METRICS`].
            #[must_use]
            pub fn values(&self) -> Vec<(&'static MetricSpec, f64)> {
                let mut specs = METRICS.iter();
                let mut values = Vec::new();
                $(
                    let spec = specs.next().expect("one spec per metric");
                    if let Some(value) = self.$field {
                        values.push((spec, f64::from(value)));
                    }
                )+
                values
            }
        }
    };
}

metrics! {
    // Global
    font_size "fontSize" Px [8.0, 32.0] 13.0 => 12.0,
        "The base text size of the window.";
    line_height "lineHeight" Ratio [1.0, 2.5] 1.4 => 1.3,
        "The line height of the window's text, as a multiple of its size.";
    backdrop_opacity "backdropOpacity" Ratio [0.0, 1.0] 0.86 => 0.94,
        "How much the window's backdrop covers the desktop behind it: 0.86 is plain Mica, the default look; more lets less of the desktop show through behind dense text.";
    radius_control "radiusControl" Px [0.0, 16.0] 4.0 => 2.0,
        "The corner radius of buttons, fields and other inner controls.";
    radius_surface "radiusSurface" Px [0.0, 16.0] 8.0 => 0.0,
        "The corner radius of the panes, the sidebar, the terminal and the marketplace.";
    gap "gap" Px [0.0, 48.0] 8.0 => 0.0,
        "The space between the window's surfaces: the sidebar, the panes and the docks.";
    body_padding "bodyPadding" Px [0.0, 64.0] 8.0 => 0.0,
        "The space between the window's edges and its surfaces, at the sides and the bottom.";
    hairline_opacity "hairlineOpacity" Ratio [0.0, 1.0] 0.06 => 0.08,
        "The opacity of the 1 px lines under the top row, the tab strips, the path rows and the sidebar's workspace row, in the theme's text colour.";
    // Title bar
    title_bar_height "titleBarHeight" Px [14.0, 80.0] 40.0 => 30.0,
        "The height of the title bar of the shell before Phase 16; the top row (topRowHeight) replaced it, so it sizes nothing now.";
    tab_height "tabHeight" Px [14.0, 80.0] 32.0 => 24.0,
        "The height of a workspace tab in the title bar of the shell before Phase 16; the workspace pill replaced it, so it sizes nothing now.";
    tab_padding_x "tabPaddingX" Px [0.0, 64.0] 14.0 => 10.0,
        "The space at each side of a workspace tab's text in the shell before Phase 16; it sizes nothing now.";
    tab_min_width "tabMinWidth" Px [40.0, 240.0] 96.0 => 80.0,
        "The narrowest a workspace tab got in the shell before Phase 16; it sizes nothing now.";
    tab_font_size "tabFontSize" Px [8.0, 32.0] 12.0 => 11.0,
        "The text size of a pane's tabs.";
    tab_radius "tabRadius" Px [0.0, 16.0] 8.0 => 6.0,
        "The radius of the two top corners of a pane's tabs: the tab in front is a card with these corners.";
    tab_max_width "tabMaxWidth" Px [80.0, 400.0] 160.0 => 170.0,
        "The widest a pane's tab gets; a longer name ends with an ellipsis.";
    caption_button_width "captionButtonWidth" Px [24.0, 96.0] 46.0 => 40.0,
        "The width of the minimize, maximize and close buttons.";
    // Command bar (the shell before Phase 16)
    command_bar_height "commandBarHeight" Px [14.0, 80.0] 48.0 => 32.0,
        "The height of the command bar of the shell before Phase 16; the top row replaced it, so it sizes nothing now.";
    icon_button_size "iconButtonSize" Px [14.0, 80.0] 32.0 => 26.0,
        "The width and height of an icon button in the command bar of the shell before Phase 16; topRowButtonSize replaced it.";
    field_height "fieldHeight" Px [14.0, 80.0] 32.0 => 24.0,
        "The height of the address and search fields of the shell before Phase 16; it sizes nothing now.";
    toggle_height "toggleHeight" Px [14.0, 80.0] 32.0 => 26.0,
        "The height of the dual and single pane toggle of the shell before Phase 16; the toggle is a top-row button now.";
    // Top row (Phase 16)
    top_row_height "topRowHeight" Px [14.0, 80.0] 40.0 => 32.0,
        "The height of the top row: the menu, the app's title, the Quick Open chip, the view buttons and the caption buttons. Windows draws the caption buttons 32 px high, so the row is never lower.";
    top_row_button_size "topRowButtonSize" Px [14.0, 80.0] 36.0 => 26.0,
        "The width and height of an icon button in the top row: the menu and the view buttons.";
    workspace_pill_height "workspacePillHeight" Px [14.0, 80.0] 24.0 => 22.0,
        "The height of the workspace pill in the top row of the shell before v2 of the redesign; the sidebar's workspace row replaced it, so it sizes nothing now.";
    workspace_pill_radius "workspacePillRadius" Px [0.0, 16.0] 4.0 => 2.0,
        "The corner radius of the workspace pill of the shell before v2 of the redesign; it sizes nothing now.";
    command_center_height "commandCenterHeight" Px [14.0, 80.0] 24.0 => 22.0,
        "The height of the command center of the shell before v2 of the redesign; the Quick Open chip (quickOpenChipHeight) replaced it, so it sizes nothing now.";
    command_center_radius "commandCenterRadius" Px [0.0, 16.0] 4.0 => 3.0,
        "The corner radius of the command center of the shell before v2 of the redesign; it sizes nothing now.";
    quick_open_chip_height "quickOpenChipHeight" Px [14.0, 80.0] 24.0 => 22.0,
        "The height of the Quick Open chip at the right of the top row: a search glyph and Ctrl+P.";
    // Sidebar
    workspace_header_height "workspaceHeaderHeight" Px [14.0, 80.0] 28.0 => 26.0,
        "The height of the sidebar's first row, the workspace switcher: a dot, the workspace's name, its branch and a chevron.";
    sidebar_min_width "sidebarMinWidth" Px [100.0, 600.0] 180.0 => 150.0,
        "The narrowest the sidebar gets.";
    sidebar_width_percent "sidebarWidthPercent" Percent [5.0, 50.0] 20.0 => 17.0,
        "The sidebar's width as a share of the window's, kept between its narrowest and widest.";
    sidebar_max_width "sidebarMaxWidth" Px [100.0, 600.0] 224.0 => 190.0,
        "The widest the sidebar gets.";
    sidebar_header_font_size "sidebarHeaderFontSize" Px [8.0, 32.0] 11.0 => 10.0,
        "The text size of a sidebar section's label.";
    sidebar_header_padding_top "sidebarHeaderPaddingTop" Px [0.0, 64.0] 14.0 => 8.0,
        "The space above a sidebar section's label.";
    sidebar_header_padding_x "sidebarHeaderPaddingX" Px [0.0, 64.0] 12.0 => 8.0,
        "The space at each side of a sidebar section's label.";
    sidebar_header_padding_bottom "sidebarHeaderPaddingBottom" Px [0.0, 64.0] 4.0 => 2.0,
        "The space below a sidebar section's label.";
    sidebar_row_height "sidebarRowHeight" Px [14.0, 80.0] 34.0 => 22.0,
        "The height of a workspace row and of a pinned folder's row in the sidebar; left out, a pinned folder's row keeps the default theme's 32.";
    sidebar_row_inset "sidebarRowInset" Px [0.0, 16.0] 4.0 => 0.0,
        "The space between a sidebar row and the sidebar's edges; 0 makes the rows full width.";
    sidebar_row_radius "sidebarRowRadius" Px [0.0, 16.0] 4.0 => 0.0,
        "The corner radius of a sidebar row.";
    selection_bar_width "selectionBarWidth" Px [0.0, 8.0] 3.0 => 2.0,
        "The width of the accent bar at the left of a selected row, in the sidebar and in the file lists.";
    drive_row_padding_y "driveRowPaddingY" Px [0.0, 64.0] 6.0 => 3.0,
        "The space above and below a drive row's content in the sidebar.";
    drive_row_padding_x "driveRowPaddingX" Px [0.0, 64.0] 10.0 => 8.0,
        "The space at each side of a drive row's content in the sidebar.";
    tag_radius "tagRadius" Px [0.0, 16.0] 12.0 => 2.0,
        "The corner radius of a tag chip in the sidebar.";
    tag_font_size "tagFontSize" Px [8.0, 32.0] 12.0 => 11.0,
        "The text size of a tag chip.";
    // Panes
    pane_header_height "paneHeaderHeight" Px [14.0, 80.0] 36.0 => 24.0,
        "The height of a pane's header in the shell before Phase 16; the breadcrumb row replaced it, so it sizes nothing now.";
    tab_row "tabRow" Px [14.0, 80.0] 36.0 => 28.0,
        "The height of a pane's tab strip, the band at the top of the pane. The tab in front stands on its bottom edge, lower than the strip by a quarter of what the strip has over 20 px; the other tabs are lower by twice that and 2 px more.";
    toolbar_row_height "toolbarRowHeight" Px [14.0, 80.0] 28.0 => 24.0,
        "The height of a pane's toolbar row, under its tab strip: Back, Forward, Up, the drive, its free space and Find.";
    path_row_height "pathRowHeight" Px [14.0, 80.0] 24.0 => 20.0,
        "The height of a pane's path row, under its toolbar row: the folder's path and the filter.";
    breadcrumb_row_height "breadcrumbRowHeight" Px [14.0, 80.0] 28.0 => 22.0,
        "The height of a pane's breadcrumb row of the shell before v2 of the redesign; the toolbar row and the path row replaced it, so it sizes nothing now.";
    nav_button_size "navButtonSize" Px [14.0, 80.0] 20.0 => 20.0,
        "The height of a button in a pane's toolbar row (Back, Forward, Up, Find); the button is 2 px wider than high.";
    column_header_padding_y "columnHeaderPaddingY" Px [0.0, 64.0] 4.0 => 2.0,
        "The space above and below the column headers' text.";
    column_header_padding_x "columnHeaderPaddingX" Px [0.0, 64.0] 14.0 => 8.0,
        "The space at each side of the column headers' row.";
    name_column_weight "nameColumnWeight" Ratio [0.1, 10.0] 1.0 => 1.6,
        "The Name column's share of a file list's width, against the other weighted columns.";
    modified_column_weight "modifiedColumnWeight" Ratio [0.1, 10.0] 0.55 => 0.9,
        "The Modified column's share, against the other weighted columns.";
    type_column_weight "typeColumnWeight" Ratio [0.1, 10.0] 0.45 => 0.7,
        "The Type column's share, against the other weighted columns.";
    name_column_min_width "nameColumnMinWidth" Px [0.0, 400.0] 120.0 => 0.0,
        "The narrowest the Name column gets.";
    size_column_width "sizeColumnWidth" Px [32.0, 160.0] 64.0 => 60.0,
        "The width of the Size column.";
    column_gap "columnGap" Px [0.0, 48.0] 0.0 => 8.0,
        "The space between a file list's columns.";
    row_height "rowHeight" Px [14.0, 80.0] 30.0 => 20.0,
        "The height of a row in a file list.";
    row_padding_x "rowPaddingX" Px [0.0, 64.0] 10.0 => 8.0,
        "The space at each side of a file row's content.";
    row_radius "rowRadius" Px [0.0, 16.0] 4.0 => 0.0,
        "The corner radius of a file row's selection and hover fill.";
    row_icon_gap "rowIconGap" Px [0.0, 48.0] 10.0 => 6.0,
        "The space between a file row's icon and its name.";
    secondary_font_size "secondaryFontSize" Px [8.0, 32.0] 12.0 => 11.0,
        "The text size of a file row's Modified, Type and Size columns.";
    column_view_width "columnViewWidth" Px [120.0, 600.0] 220.0 => 180.0,
        "The width of each column of a pane's column view (a tab in the columns mode).";
    // Editors
    editor_tab_height "editorTabHeight" Px [14.0, 80.0] 36.0 => 26.0,
        "The height of the editor's tab strip.";
    markdown_padding_y "markdownPaddingY" Px [0.0, 64.0] 28.0 => 14.0,
        "The space above and below the Markdown preview's text.";
    markdown_padding_x "markdownPaddingX" Px [0.0, 64.0] 40.0 => 20.0,
        "The space at each side of the Markdown preview's text.";
    markdown_line_height "markdownLineHeight" Ratio [1.0, 2.5] 1.6 => 1.5,
        "The Markdown preview's line height, as a multiple of its text size.";
    hex_row_height "hexRowHeight" Px [14.0, 80.0] 22.0 => 18.0,
        "The height of a row in the hex view.";
    hex_column_gap "hexColumnGap" Px [0.0, 48.0] 18.0 => 14.0,
        "The space between the hex view's columns.";
    // Terminal
    terminal_dock_min_height "terminalDockMinHeight" Px [60.0, 800.0] 120.0 => 100.0,
        "The lowest the terminal gets when it is docked at the bottom.";
    terminal_dock_height_percent "terminalDockHeightPercent" Percent [10.0, 80.0] 30.0 => 26.0,
        "The bottom-docked terminal's height as a share of the window's, kept between its lowest and highest.";
    terminal_dock_max_height "terminalDockMaxHeight" Px [60.0, 800.0] 240.0 => 200.0,
        "The highest the terminal gets when it is docked at the bottom.";
    terminal_header_height "terminalHeaderHeight" Px [14.0, 80.0] 34.0 => 24.0,
        "The height of the terminal's header.";
    terminal_tab_height "terminalTabHeight" Px [14.0, 80.0] 26.0 => 20.0,
        "The height of a terminal session's tab.";
    terminal_padding_y "terminalPaddingY" Px [0.0, 64.0] 10.0 => 6.0,
        "The space above and below the terminal's text.";
    terminal_padding_x "terminalPaddingX" Px [0.0, 64.0] 12.0 => 8.0,
        "The space at each side of the terminal's text.";
    terminal_line_height "terminalLineHeight" Ratio [1.0, 2.5] 1.6 => 1.45,
        "The terminal's line height, as a multiple of its text size.";
    // Marketplace
    marketplace_tab_height "marketplaceTabHeight" Px [14.0, 80.0] 32.0 => 24.0,
        "The height of a tab in the marketplace's side list.";
    marketplace_tab_radius "marketplaceTabRadius" Px [0.0, 16.0] 4.0 => 0.0,
        "The corner radius of a marketplace tab.";
    marketplace_card_gap "marketplaceCardGap" Px [0.0, 48.0] 10.0 => 4.0,
        "The space between the marketplace's cards.";
    marketplace_card_padding_y "marketplaceCardPaddingY" Px [0.0, 64.0] 14.0 => 8.0,
        "The space above and below a marketplace card's content.";
    marketplace_card_padding_x "marketplaceCardPaddingX" Px [0.0, 64.0] 14.0 => 10.0,
        "The space at each side of a marketplace card's content.";
    marketplace_card_radius "marketplaceCardRadius" Px [0.0, 16.0] 8.0 => 2.0,
        "The corner radius of a marketplace card.";
    // Overlays
    palette_row_height "paletteRowHeight" Px [14.0, 80.0] 36.0 => 28.0,
        "The height of a row in the command palette.";
    menu_row_height "menuRowHeight" Px [14.0, 80.0] 32.0 => 26.0,
        "The height of an item in a context menu.";
    dropdown_row_height "dropdownRowHeight" Px [14.0, 80.0] 26.0 => 26.0,
        "The height of a row in the top row's menus: the hamburger menu and the workspace list.";
    // Status bar
    status_bar_height "statusBarHeight" Px [14.0, 80.0] 26.0 => 22.0,
        "The height of the status bar.";
    status_bar_padding_x "statusBarPaddingX" Px [0.0, 64.0] 14.0 => 8.0,
        "The space at each side of the status bar's content.";
    status_bar_gap "statusBarGap" Px [0.0, 48.0] 16.0 => 12.0,
        "The space between the status bar's items.";
    // Function-key bar: the default theme has none, so its values are the
    // handout's, for any theme that shows the bar.
    fkey_bar_height "fkeyBarHeight" Px [14.0, 80.0] 24.0 => 24.0,
        "The height of the function-key bar, when `chrome.fkeyBar` shows it (the default theme has no bar; the values left out are the handout's).";
    fkey_bar_gap "fkeyBarGap" Px [0.0, 48.0] 1.0 => 1.0,
        "The space between the function-key bar's buttons.";
    fkey_button_radius "fkeyButtonRadius" Px [0.0, 16.0] 2.0 => 2.0,
        "The corner radius of a function-key button.";
    fkey_bar_font_size "fkeyBarFontSize" Px [8.0, 32.0] 11.0 => 11.0,
        "The text size of the function-key bar.";
}

/// Pairs of a lower and an upper limit: the first may not be above the
/// second, each one as the theme gives it or as the default theme has it.
const LIMITS: [(&str, &str); 2] = [
    ("sidebarMinWidth", "sidebarMaxWidth"),
    ("terminalDockMinHeight", "terminalDockMaxHeight"),
];

impl Metrics {
    /// Whether the theme sets no metric at all.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values().is_empty()
    }

    /// Why the metrics cannot be used: a value outside its metric's bounds,
    /// or a lower limit above its upper limit. `None` when they can.
    #[must_use]
    pub fn problem(&self) -> Option<String> {
        let values = self.values();
        for (spec, value) in &values {
            if !(spec.min..=spec.max).contains(value) {
                return Some(format!(
                    "metrics.{}: {value} is not from {} to {}",
                    spec.name, spec.min, spec.max
                ));
            }
        }
        let value_of = |name: &str| {
            values
                .iter()
                .find(|(spec, _)| spec.name == name)
                .map(|(_, value)| (*value, ""))
                .or_else(|| {
                    METRICS
                        .iter()
                        .find(|spec| spec.name == name)
                        .map(|spec| (spec.default, ", the default"))
                })
        };
        for (lower, upper) in LIMITS {
            if let (Some((low, low_note)), Some((high, high_note))) =
                (value_of(lower), value_of(upper))
                && low > high
            {
                return Some(format!(
                    "metrics.{lower} ({low}{low_note}) is above metrics.{upper} ({high}{high_note})"
                ));
            }
        }
        None
    }
}

/// The elements of the window a theme shows or hides (`docs/themes.md`,
/// "Metrics and chrome"). A theme never changes a command, a key or the
/// layout; it may turn these on. Each one left out, like the whole object,
/// is as the default theme has it: off.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct Chrome {
    /// The function-key bar between the panes and the status bar: F3 View,
    /// F4 Edit, F5 Copy, F6 Move, F7 Mkdir, F8 Delete, Alt+F1 Drive. Its
    /// buttons run the commands those keys run.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fkey_bar: Option<bool>,
    /// Every other row of a file list a shade lighter.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub row_stripes: Option<bool>,
    /// 1 px separators between the window's surfaces instead of floating
    /// cards; with `gap` and `radiusSurface` at 0 the surfaces meet at the
    /// hairlines.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hairlines: Option<bool>,
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

    use super::{Color, METRICS, MetricUnit, Metrics, Opacity, Rgb};

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

    impl JsonSchema for Metrics {
        fn schema_name() -> Cow<'static, str> {
            "Metrics".into()
        }

        fn json_schema(_generator: &mut SchemaGenerator) -> Schema {
            let properties: serde_json::Map<String, serde_json::Value> = METRICS
                .iter()
                .map(|spec| {
                    let (kind, unit) = match spec.unit {
                        MetricUnit::Px => ("integer", "Whole pixels"),
                        MetricUnit::Ratio => ("number", "A number"),
                        MetricUnit::Percent => ("number", "Percent of the window"),
                    };
                    let property = serde_json::json!({
                        "description": format!(
                            "{} {unit} from {} to {}; left out, {}.",
                            spec.what, spec.min, spec.max, spec.default
                        ),
                        "type": kind,
                        "minimum": number(spec.min),
                        "maximum": number(spec.max),
                    });
                    (spec.name.to_owned(), property)
                })
                .collect();
            json_schema!({
                "description": "The sizes a theme changes, each optional; one left out keeps the default theme's value.",
                "type": "object",
                "properties": properties,
                "additionalProperties": false
            })
        }
    }

    /// `value` as JSON: a whole number without a fraction, as a theme
    /// writes pixels.
    #[expect(
        clippy::cast_possible_truncation,
        reason = "only whole numbers in the table's range are cast"
    )]
    fn number(value: f64) -> serde_json::Value {
        let whole = value.trunc();
        if (value - whole).abs() < f64::EPSILON && whole.abs() < 1e9 {
            serde_json::Value::from(whole as i64)
        } else {
            serde_json::Value::from(value)
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

    /// The shipped default theme's file, with `extra` keys added.
    fn default_with(extra: &serde_json::Value) -> serde_json::Value {
        let mut theme: serde_json::Value =
            serde_json::from_str(include_str!("../../../../sdk/themes/default.json")).unwrap();
        for (key, value) in extra.as_object().unwrap() {
            theme[key] = value.clone();
        }
        theme
    }

    #[test]
    fn metrics_and_chrome_are_optional() {
        let plain: Theme = serde_json::from_value(default_with(&json!({}))).unwrap();
        assert_eq!((plain.metrics, plain.chrome), (None, None));
        let text = serde_json::to_string(&plain).unwrap();
        assert!(
            !text.contains("metrics") && !text.contains("chrome"),
            "{text}"
        );

        let theme: Theme = serde_json::from_value(default_with(&json!({
            "metrics": {"rowHeight": 20, "lineHeight": 1.3},
            "chrome": {"fkeyBar": true}
        })))
        .unwrap();
        let metrics = theme.metrics.as_ref().unwrap();
        assert_eq!(metrics.row_height, Some(20));
        assert_eq!(metrics.line_height, Decimal::new(1.3));
        assert_eq!(metrics.font_size, None);
        let chrome = theme.chrome.unwrap();
        assert_eq!(
            (chrome.fkey_bar, chrome.row_stripes, chrome.hairlines),
            (Some(true), None, None)
        );
        // Carried as written: only the keys the file has.
        let sent = serde_json::to_value(&theme).unwrap();
        assert_eq!(sent["metrics"], json!({"rowHeight": 20, "lineHeight": 1.3}));
        assert_eq!(sent["chrome"], json!({"fkeyBar": true}));
    }

    #[test]
    fn metric_and_chrome_names_are_strict_and_pixels_are_whole() {
        for (extra, expected) in [
            (
                json!({"metrics": {"rowHight": 20}}),
                "unknown field `rowHight`",
            ),
            (
                json!({"metrics": {"rowHeight": 20.5}}),
                "invalid type: floating point",
            ),
            (
                json!({"metrics": {"rowHeight": -1}}),
                "invalid value: integer `-1`",
            ),
            (
                json!({"metrics": {"lineHeight": "1.3"}}),
                "invalid type: string",
            ),
            (
                json!({"chrome": {"stripes": true}}),
                "unknown field `stripes`",
            ),
            (
                json!({"chrome": {"fkeyBar": "yes"}}),
                "invalid type: string",
            ),
        ] {
            let error = serde_json::from_value::<Theme>(default_with(&extra)).unwrap_err();
            assert!(error.to_string().contains(expected), "{extra}: {error}");
        }
    }

    #[test]
    fn every_metric_is_named_once_with_bounds_that_hold_both_looks() {
        let mut names = std::collections::BTreeSet::new();
        for spec in METRICS {
            assert!(names.insert(spec.name), "{} twice", spec.name);
            assert!(
                spec.name
                    .chars()
                    .next()
                    .is_some_and(|c| c.is_ascii_lowercase())
                    && spec.name.chars().all(|c| c.is_ascii_alphanumeric()),
                "{} is not camelCase",
                spec.name
            );
            assert!(spec.min < spec.max, "{}", spec.name);
            for (look, value) in [("default", spec.default), ("compact", spec.compact)] {
                assert!(
                    (spec.min..=spec.max).contains(&value),
                    "{}: the {look} value {value} is outside {}..{}",
                    spec.name,
                    spec.min,
                    spec.max
                );
                if spec.unit == MetricUnit::Px {
                    assert!(
                        value.fract().abs() < f64::EPSILON,
                        "{}: pixels are whole",
                        spec.name
                    );
                }
            }
            assert!(!spec.what.is_empty(), "{}", spec.name);
        }
        // The table's names are the file's names, and every field has one.
        let compact: serde_json::Map<String, serde_json::Value> = METRICS
            .iter()
            .map(|spec| {
                // A whole value is written without a fraction, as pixels are.
                let value = json!(Decimal::new(spec.compact).unwrap());
                (spec.name.to_owned(), value)
            })
            .collect();
        let metrics: Metrics = serde_json::from_value(json!(compact)).unwrap();
        let values = metrics.values();
        assert_eq!(values.len(), METRICS.len());
        for ((spec, value), expected) in values.iter().zip(METRICS) {
            assert_eq!(spec.name, expected.name);
            assert!((value - expected.compact).abs() < 1e-9, "{}", spec.name);
        }
        assert_eq!(metrics.problem(), None);
    }

    #[test]
    fn values_outside_their_bounds_are_named() {
        let problem = |metrics: serde_json::Value| {
            serde_json::from_value::<Metrics>(metrics)
                .unwrap()
                .problem()
        };
        assert_eq!(problem(json!({})), None);
        assert_eq!(problem(json!({"rowHeight": 14, "fontSize": 32})), None);
        for (metrics, words) in [
            (
                json!({"rowHeight": 10}),
                ["rowHeight", "10", "from 14 to 80"],
            ),
            (
                json!({"rowHeight": 81}),
                ["rowHeight", "81", "from 14 to 80"],
            ),
            (json!({"fontSize": 7}), ["fontSize", "7", "from 8 to 32"]),
            (
                json!({"lineHeight": 0.5}),
                ["lineHeight", "0.5", "from 1 to 2.5"],
            ),
            (
                json!({"backdropOpacity": 1.5}),
                ["backdropOpacity", "1.5", "from 0 to 1"],
            ),
            (
                json!({"sidebarWidthPercent": 70}),
                ["sidebarWidthPercent", "70", "from 5 to 50"],
            ),
        ] {
            let found = problem(metrics.clone()).unwrap_or_else(|| panic!("{metrics} passed"));
            for word in words {
                assert!(found.contains(word), "{metrics}: {found}");
            }
        }
        // A lower limit above its upper limit, the other one as the default
        // theme has it (the sidebar is at most 224 px wide there).
        let found = problem(json!({"sidebarMinWidth": 300})).unwrap();
        assert!(
            found.contains("sidebarMinWidth") && found.contains("sidebarMaxWidth"),
            "{found}"
        );
        assert_eq!(
            problem(json!({"sidebarMinWidth": 300, "sidebarMaxWidth": 400})),
            None
        );
        let found = problem(json!({"terminalDockMaxHeight": 100})).unwrap();
        assert!(found.contains("terminalDockMinHeight"), "{found}");
    }

    #[test]
    fn theme_info_says_whether_a_theme_sets_metrics() {
        let plain: Theme = serde_json::from_value(default_with(&json!({}))).unwrap();
        assert!(!ThemeInfo::from(&plain).has_metrics);
        let empty: Theme = serde_json::from_value(default_with(&json!({"metrics": {}}))).unwrap();
        assert!(!ThemeInfo::from(&empty).has_metrics);
        let dense: Theme =
            serde_json::from_value(default_with(&json!({"metrics": {"rowHeight": 20}}))).unwrap();
        let info = ThemeInfo::from(&dense);
        assert!(info.has_metrics);
        assert_eq!(
            serde_json::to_value(&info).unwrap()["has_metrics"],
            json!(true)
        );
        // A core from before the field: not a density preset.
        let older: ThemeInfo = serde_json::from_value(json!({
            "id": "nord", "name": "Nord", "author": "CabinetOS", "version": "1.0.0",
            "kind": "dark", "accent": null, "mica": null
        }))
        .unwrap();
        assert!(!older.has_metrics);
    }

    #[test]
    fn a_whole_decimal_is_written_without_a_fraction() {
        for (value, written) in [(17.0, "17"), (1.6, "1.6"), (0.94, "0.94"), (0.0, "0")] {
            let decimal = Decimal::new(value).unwrap();
            assert_eq!(serde_json::to_string(&decimal).unwrap(), written);
            let back: Decimal = serde_json::from_str(written).unwrap();
            assert_eq!(back, decimal);
        }
        assert!(Decimal::new(f64::NAN).is_none());
        assert!(serde_json::from_str::<Decimal>("\"17\"").is_err());
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
