# ADR 0017: File tags live with the file and in a catalog; the sidebar lists them

- Status: proposed
- Date: 2026-10-01
- Decided by: nobody yet. The planning session wrote this in the
  sleep-mode run of 2026-10-01 for item 3 of the creator's card "New
  features I want to build Files Type Sprint" ([PLAN.md](../PLAN.md),
  Phase 19g), so the creator can decide it in the morning. Every choice
  below is a proposal; a tag store is a format the creator will live
  with, so it was not built unattended.

## Context

The card asks for the Files app's tagging: "assign custom, color-coded
tags (like Urgent, Invoices or Work) to any document; a file may have
several; clicking a tag in the sidebar surfaces every associated file
across your entire hard drive, regardless of what folder they live in."

What decides the shape:

- Article 6: a tag is a preference of the user's, but it belongs to a
  file, not to the config; still, the set of tag names and colours is
  configuration and belongs in `cabinetos.json`.
- Article 10: is tagging core? The sidebar's filter and the search are
  core; a viewer or an editor of tags is not. The smallest core is a
  store, a search, a sidebar section and three commands; the rest can be
  an extension.
- A tag that lives only in a database is lost when the file is copied to
  another PC or moved by another program. A tag that lives only in the
  file's own metadata cannot answer "every file with this tag" without a
  walk of the drive.
- NTFS keeps alternate data streams (ADS) with the file; the core's own
  copy and move (`CopyFileExW`) keep them; Explorer keeps them on NTFS;
  FAT, exFAT and most cloud drives drop them silently. The indexer
  (Phase 6) reads the MFT and knows names, not stream contents.
- The design's metrics `tagRadius` and `tagFontSize` exist for a Tags
  section that the window never had (docs/ui.md, "Where the window
  differs from the handout's page").

## Decision (proposed)

1. **A tag is a name and a colour** from a fixed set of eight colours
   (the theme's `permission…` and accent shades, so themes recolour
   them). The set of tags the user made is `tags` in `cabinetos.json`:
   `[{ "name": "Urgent", "color": "red" }, …]`, so a hand edit renames
   or recolours a tag everywhere (Article 6).
2. **A file's tags live in two places, written together.** In the file's
   NTFS stream `:CabinetOS.tags` as UTF-8 JSON (`["Urgent","Work"]`), so
   they travel with the file where streams survive; and in a catalog
   `%LOCALAPPDATA%\CabinetOS\tags\catalog.json` keyed by full path, so
   "every file with this tag" answers at once with no index and no walk.
   On a volume without streams (the core knows the file system), the
   catalog alone holds them and the window says so once.
3. **The catalog heals itself.** A path that no longer exists is dropped
   when the catalog is read; a file the core itself moves or renames
   is re-keyed by the core's own job; a file another program moved keeps
   its stream, and the next listing of its new folder reads the stream
   and re-keys the catalog entry.
4. **The core serves it**: requests `get_tags` (paths), `set_tags`
   (path, tags), `list_tagged` (tag) and `list_tags`; the events
   `tags_changed` (paths). The window does no file I/O (brief §1). The
   listing's `describe_entries` adds each entry's tags so the list can
   show them.
5. **The window shows it**: a Tags section in the sidebar (the design's
   metrics apply) listing the tags with their colours; a click lists
   that tag's files in the active pane the way search results are
   listed (each row with its folder), and Esc returns to the folder; a
   row's tags as small dots after the name in the list; the Properties
   dialog names them.
6. **Commands**: `tags.add` (a prompt in the palette's frame listing the
   tags, Enter adds to the marked rows), `tags.remove`, `tags.manage`
   (create, rename, recolour, delete), and "Tags…" in the context menu's
   file and multi-select targets. No default keys; the palette reaches
   them (Article 7).
7. **Not in the core**: a tag-based rule engine, automatic tagging, tag
   sync between PCs, tags on folders (later, the same format).

## Open questions for the creator

- Whether tags belong in the core at all, or in a core plugin (the
  store, the requests) plus a tool pane (the sidebar section), under
  Article 10. The planning session leans to the core, because the
  sidebar section and the search-like listing are the window's own
  surfaces and a plugin cannot draw in them.
- The colour set: eight named colours, or any colour?
- Whether a tag on a folder tags its files.

## Consequences (if accepted)

- A new core crate `cabinetos-tags` (the stream and the catalog, tested
  on a temp volume), four requests and one event in the protocol, and
  the jobs' rename and move hooks to re-key the catalog.
- The window's sidebar gets its first new section since Phase 13; the
  list rows gain a small element per row (measure the frames).
- Easier: finding work across folders. Harder: two stores to keep
  aligned; a stream that a cloud drive drops. The catalog is the source
  for search, the stream for travel, and a mismatch is settled in the
  stream's favour when both exist.
