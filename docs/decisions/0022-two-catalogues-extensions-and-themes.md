# ADR 0022: The marketplace has two catalogues, extensions and themes, and the theme gallery is its own page

- Status: accepted
- Date: 2026-10-03
- Decided by: the creator, in chat on 2026-10-02 and 2026-10-03: themes and
  extensions do not share a catalogue, and the theme gallery comes with the
  first three-ways gaps in Phase 23 ([PLAN.md](../PLAN.md)). The details
  below, such as the tile keys, were chosen by the coder of Phase 23 inside
  that decision, and are marked as such.

## Context

The marketplace page showed plugins, tools and themes in one list, from one
file, `index.json`. The public index holds 41 themes and no plugin, so the
page the creator saw was a list of colour schemes with a WebAssembly caption.
A person who wants a theme does not look for it among plugins, and a person
who wants a plugin does not want 41 themes in the way. A theme is also
something you want to see before you install it, and a text card cannot show
that (Article 3, Native Modern Aesthetics; Article 4, Progressive Disclosure).

## Decision

1. **Two catalogue files, one format.** The marketplace site publishes
   `index.json` for extensions (kinds `plugin` and `tool`) and `themes.json`
   for themes, both made by `sdk/marketplace/build-index.ps1`. They have the
   same shape (`schemaVersion`, `generatedAt`, `items`) and the same item
   format, so one schema, `sdk/marketplace/index.schema.json`, describes
   both. The core reads both with the same cache, `ETag`, trust rules and
   `allowInsecure` rule. A new setting, `marketplace.themes`, says where the
   themes file is; its default is
   `https://oliverd25.github.io/cabinetos-marketplace/themes.json`.
2. **The window shows them on two pages.** The Extensions page keeps the
   old marketplace page's job (Discover, Plugins, Tools, Installed) without
   themes. The theme gallery is new. The sidebar rail keeps its one
   marketplace button, which opens Extensions, and `marketplace.browse` keeps
   its ID (a command is never renamed). The gallery is reached from the theme
   picker's last row, "Browse more themes", and from the palette's
   "Themes: Browse" (`themes.browse`). No new rail button: a button for each
   kind of extension would be bloat the user did not ask for (Article 10).
3. **A theme item says how to paint its tile.** Beside the usual keys it has
   `appearance` (`dark`, `light` or `system`, the theme file's `kind`),
   `density` (`true` when the theme sets `metrics`) and `tile`
   (`background`, `text`, `accent`, each `#RRGGBB`). Coder's choice, the
   keys the tile is read from: the window's file rows are `palette.layerFill`
   over the Mica tint over plain Mica, with `palette.textPrimary` for the
   names and the accent for the selection pill. So `background` is
   `layerFill` laid over the theme's `mica.tint` at `mica.opacity` (over
   plain Mica, `#202020` dark or `#F3F3F3` light, when the theme has no
   tint), `text` is `textPrimary` and `accent` is `accent`, or the default
   accent (`#60CDFF`, `#005FB8` for a light theme) when the theme has none.
   The script computes them; the gallery draws them and does not need the
   theme file to do so. A theme item without them (an older index) gets a
   plain tile.
4. **The core reads the two apart.** `marketplace_refresh` and
   `marketplace_search` take an optional `catalogue` (`extensions`, the
   default, or `themes`). A themes server that is down does not hide the
   extensions. `install_extension` finds an item in either, so
   `cabinetos-cli market install nord` still works. The protocol version
   rises from 19 to 20, because a request without a catalogue used to list
   every kind and now lists extensions only ([ipc.md](../ipc.md)).
5. **The transition rule.** Publishing `themes.json` to the public site is
   the creator's step, so until it is done the site has only `index.json`,
   with the themes inside it. When the themes address answers 404 (or a
   local folder has no `themes.json`), the core offers the theme items of
   `index.json` as the themes, with one log line. When `themes.json` exists,
   the theme items of `index.json` are ignored, with one log line. Any other
   failure of the themes address is an error for the gallery alone, not a
   reason to fall back: only a 404 means "not published yet".

Options considered:

- **One catalogue with a `kind` filter, as before.** Nothing to build, and
  it was how Phase 9 shipped. Rejected by the creator: the gallery is a
  different page with a different look, and the index of a user who installs
  41 themes should not grow with them.
- **A third file per theme with the tile colours, read by the window.**
  Rejected: the gallery would download 41 theme files (about 2 KB each) to
  paint 41 tiles, and an empty gallery on a slow line is the first thing a
  user would see. Three colours in the catalogue cost about 100 bytes a
  theme.
- **A separate schema file for `themes.json`.** Rejected: the two files have
  one item format, and a second schema would repeat 300 lines and drift. The
  schema's description says it covers both.
- **A new rail button for themes.** Rejected (Article 10): the rail has the
  user's most used views, and a theme is chosen once in a while. The picker
  and the palette are where a user already goes to change a theme.

## Consequences

- `build-index.ps1` writes two files and checks every item against the
  format before it writes. A Rust test builds both and reads them with the
  core's own reader, so a script that writes an item the core would leave
  out fails the test.
- **The creator publishes the new catalogues.** Until `themes.json` is on
  the site, the window works through the transition rule. When the new
  `index.json` replaces the old one, CabinetOS 0.1.0 and 0.1.1, which read
  `index.json` only, list no theme in their marketplace until they update.
  The command is in [marketplace.md](../marketplace.md), "The public
  catalogues".
- A local `marketplace.index` leaves `marketplace.themes` at the public
  address: a developer sets both. The tests that install a theme from a
  local index set both too.
- `marketplace.themes` is an advanced setting and, like `marketplace.index`,
  has no window control until the Settings page of Phase 11c (gap 8 of the
  `settings-three-ways` skill's audit).
- The transition rule can be removed in a later version, once a release with
  the two files is published and the old versions are rare.
