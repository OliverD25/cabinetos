# Quick View, the core's part (Phase 25, step 2, items C1 to C6 of ADR 0023)

Coder report, Opus 5.5, 2026-10-03. Branch `worktree-agent-a3d9117b76f4ae7e0`,
from main's `feb4df4` (ADR 0023 merged). The creator was away; every
decision the ADR left open is below as a `what - because - undo` line. The
window's items (W1 to W9) are another coder's, against the same ADR; no
window code was written beyond the two lines that keep the window's fast
tests green. Nothing opened a window on this PC, and nothing ran on the
laptop.

Constitution: Article 1 (Zero-Compromise Performance) is why the shell's
image factory runs only on threads of the core's own, with limits; Article 10
(Zero-Bloat Foundation) is why no viewer and no pack name is in the core
(the table and the offer come from manifests); Article 11 (Bifurcated
Extension Architecture) is why a viewer is a Tool Extension page that never
talks to the core; Article 7 is why each action is a rebindable command.

## What was built

| Item | Commit | What it is | Its tests |
|---|---|---|---|
| C1. Protocol 21 | `80a3601` | `get_thumbnail` / `thumbnail`, `render_image` / `rendered_image`, `quick_view_table` / `quick_view_table`, `quick_view_offer` / `quick_view_offer`, the event `quick_view_table_changed` ([quick_view.rs](../../../core/crates/cabinetos-protocol/src/quick_view.rs), [message.rs](../../../core/crates/cabinetos-protocol/src/message.rs)); the kind grammar beside `extension_id_problem`; `sdk/protocol/*.schema.json` regenerated; [ipc.md](../../ipc.md) "Quick View" | round trips, the tags, two wire-form tests, the schema snapshots, the grammar's own tests |
| C2. Thumbnails | `73cc9a6` | [thumbnail.rs](../../../core/crates/cabinetos-fs/src/thumbnail.rs) (the `unsafe` calls: `SHCreateItemFromParsingName`, `IShellItemImageFactory::GetImage`, `GetDIBits`, each with `// SAFETY:`); [quickview.rs](../../../core/crates/cabinetos-core/src/quickview.rs) (two threads in COM apartments, 2 s, stuck threads replaced, `busy` at four, newest first, reads ahead, the 128-picture cache, the warm start after the first listing, cloud files from the shell's cache only) | a PNG gives its aspect; `.xyz` gives `none`; a missing file `not_found`; the second request from the cache; a queued request superseded; reads ahead kept at four; a stub that never returns gives `timeout` four times, then `busy` (threads 3, 4, 5, 5), and a returned thread takes requests again; the warm start starts two threads once; end to end over the pipe |
| C3. Drawings | `73cc9a6` (same commit) | `render_image` on the same threads and queue, 5 s, `image.png` in `%LOCALAPPDATA%\CabinetOS\cache\render\<ULID>\`, emptied at start, 16 kept | a 1024×768 PNG drawn at 512 is 512×384 on disk; the 17th drawing removes the first folder; a file the shell cannot draw is `io`; end to end over the pipe |
| C4. The table | `afa349e` | `quickView` read strictly in [tools.rs](../../../core/crates/cabinetos-market/src/tools.rs); the order of decision 1.2; `--dev-tools-dir`; `quickView.viewers` in `cabinetos-config` and the schema; rebuilt at start, after a tool install or uninstall and after the setting changes; sent to a client that fell behind; `tool.schema.json` gains `quickView`; the fixture viewer [quickview-fixture](../../../sdk/fixtures/tools/quickview-fixture/README.md) | two viewers on one kind give the earlier install first; a whole name before an extension and `*.tar.gz` before `*.gz`; the user's choice first; `none` gives `off`; a bad pattern (and a bad page, an unknown key, no kinds, 513 kinds) leaves the viewer out and keeps its pane use; the dev folder wins; a core test installs the fixture from a local index and sees `tools_changed` then `quick_view_table_changed`, then the event again after `set_value` and after the uninstall; the setting's checks |
| C5. The offer | `98b7353` | `quick_view_offer`: the catalogue's tool items matched by their manifests' `quickView.kinds`, newest runnable version, not installed; the catalogue from this session's read, from disk, from the cache under seven days, else the web once per session; `offline`; trust rule 6 of [marketplace.md](../../marketplace.md) | the first matching item in index order; an installed one skipped; one that needs a newer core skipped; a bad block claims nothing; the seven-day rule on the cache; end to end: offered until installed, `no_item`, `offline` |
| C6. The commands | `86c2449` | `quickView.toggle` (`space`, `filesView`), `quickView.chooseViewer`, `quickView.installViewer` (no keys); `edit.toggleSelectionInPlace` to `shift+space`; [keybindings.md](../../keybindings.md) | IDs, titles, keys, contexts, the tier unchanged, Space and Shift+Space each with one command; a keymap test that `space` can be bound back to `edit.toggleSelectionInPlace` once Quick View has another key (and is refused while both have it) |
| Documents | the report's commit | `sdk/tools/quickview-messages.schema.json`, `CHANGELOG.md`, this report and its row | the schema parses; its key pattern refuses Space, Up, Enter and Ctrl |

## Measured on this PC (release build)

| What | Time |
|---|---|
| `get_thumbnail` of a 1200×600 PNG, first request of the process, through the queue | 165 ms (the shell's start: COM, the image factory, the PNG codec) |
| The same, from the core's memory cache | 0.1 ms |
| Warm start on a folder (the cache-only call each thread makes after the first listing) | 66 to 70 ms, on the thread, before any Space |
| A 12-megapixel PNG at 256 after the warm start, first time | 56 to 94 ms |
| A new 12-megapixel PNG at 256, the shell already started | 24 ms |
| The same file again (the shell's own thumbnail cache) | 4.7 to 9.3 ms |
| The same file at 768 | 33 to 40 ms |

The 100 ms goal of decision 4.5 is the window's to measure on the laptop
(W9). The core's part of it is the shell call: 5 to 25 ms once the shell is
started and the file is cached or easy, and the first file after the start
may cost 56 to 94 ms. Debug builds roughly double the first numbers.

## Checks (from `core/`, the last run, at the report's commit)

`cargo build --workspace` 0, `cargo test --workspace` 0,
`cargo clippy --workspace --all-targets -- -D warnings` 0,
`cargo fmt --all -- --check` 0, `cargo deny check` 0; 958 tests passed, 7
ignored (one of them the new timing printout). The window, from `ui/`:
`dotnet build CabinetOS.sln -warnaserror` 0 (Debug) and 0 (`-c Release`),
`dotnet test --solution CabinetOS.sln --no-build` 0: 1427 tests, 1355
passed, 72 skipped (they open a window and need `CABINETOS_UI_E2E=1`). The C1 commit alone is red on clippy (a test over
the line limit after `cargo fmt`); the C2 commit splits it and every later
commit is green.

## Decided (what - because - undo)

- The kind grammar refuses `?`, control characters, leading or trailing
  spaces and patterns over 255 characters, beside `*` alone and `*` inside -
  because the ADR says "no other wildcards" and a file name has at most 255
  characters - drop the checks in `kind_pattern_problem`.
- Kinds in the table are lower case, each once - because they are compared
  without case and two spellings of one kind would be two entries - keep
  the pattern as first written in `quick_view_table`.
- `kinds` is in the order to try: the kinds of `quickView.viewers` first,
  then the claimed ones, each group whole names first and longer extensions
  first; the window takes the first entry whose pattern matches - because
  decision 1.2 puts the user's choice before specificity, and an ordered
  list lets the window stay a plain lookup - sort all kinds by specificity
  alone.
- An `off` kind has `"viewers": []` - because the ADR's example shows it
  so; the cost is that the panel's drop-down cannot list the claimants of
  an off kind - list the claimants and let the window check `off` first.
- A choice in `quickView.viewers` that names a tool which is not a viewer is
  left out of the table; one that names a viewer which does not claim the
  kind is kept - because the user's word cannot point at nothing, and a
  hand edit may give any viewer any kind - require the claim too.
- `quickView.viewers` keys that are not kinds, and values that are neither
  an ID nor `none`, are configuration errors that name the key - because
  the file is strict everywhere else - accept and ignore them.
- `set_value` on `quickView.viewers` sets the whole object - because a key
  such as `*.pdf` has a dot, so it cannot be a step of a dotted path - none
  needed.
- The thumbnail's 2 s (and the drawing's 5 s) count from the moment a
  thread starts the shell call, not from the request's arrival; a tokio
  timer per job marks the thread stuck, so no third thread is needed and a
  closed connection still has its stuck thread replaced - because a queued
  request is not the shell's fault - count from arrival in `queue()`.
- At the fourth stuck thread no replacement is made and the waiting
  requests are answered `busy`; with three stuck, a replacement keeps two
  working threads - because that is "busy when four are stuck" with the
  fewest threads - change `max_stuck`.
- A stuck thread that comes back keeps working only while fewer than two
  others work, else it ends - because the pool should return to two - keep
  every thread.
- Drawings share the two threads and the queue, after the waiting
  thumbnails without `ahead` and before the reads ahead - because a third
  pool would be idle threads for every user, and one render per token is in
  flight - give drawings a thread of their own.
- A drawing uses `SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY` - because a
  generic icon for an image the page cannot decode would be wrong - drop
  `THUMBNAILONLY` in `ImageRequest::flags`.
- A drawing that fails, times out or meets four stuck threads is an `io`
  error whose message says which - because the ADR adds no error code -
  add codes in a later protocol.
- The render cache is `%LOCALAPPDATA%\CabinetOS\cache\render`, or
  `CABINETOS_CACHE_DIR\render` for tests; it is emptied once at start on
  the blocking pool, and the first drawing waits for that - because a
  drawing must not be removed while it is written - empty it lazily. Two
  cores (two windows) share it, so one core's start empties the other's
  drawings; a page that already loaded one keeps it.
- Thumbnail PNGs are written with fast compression - because a thumbnail is
  made once and shown at once - the default compression.
- A bitmap whose alpha is zero everywhere is opaque; otherwise the shell's
  premultiplied alpha is made straight - because the shell's thumbnails of
  photos carry no alpha - none.
- The core reads `--dev-tools-dir` only, not the `CABINETOS_TOOLS_DIR` the
  window may pass on in its environment - because the ADR names the flag -
  read the variable as a fallback.
- Hand-copied tools are ordered by folder name, which is their ID; tools in
  `installed.json` with the same time by ID - because folder name and ID
  are the same - none.
- The offer counts as installed what is in the record of installs, the
  tools folder or the development folder - because a hand-copied viewer
  should not be offered again - the record alone.
- The offer uses the catalogue a `marketplace_refresh` read in this session
  before the cache, and a failed web read answers `offline` for the rest of
  the session, even when an older cached copy exists - because the ADR
  says "at most once per core session" and "when the catalogue cannot be
  read, offline" - fall back to the stale copy.
- `sdk/tools/quickview-messages.schema.json` was written here, because the
  handout names it; ADR 0023 gives it to the panel's unit (W2), so the two
  branches add the same file - keep one at the merge.
- The fixture viewer's page implements the whole table of the ADR
  (`shown`, `shown-keys`, `failed`, `hang`, `crash`, `slow`, a PNG), not
  only what the core's tests need - because the window's tests need it and
  a second copy would conflict - the window's coder may replace it.
- The window gets two lines only: `quick_view_table_changed` in
  `MessageCodec.EventTypes` (its schema test requires every event type of
  the schema to be known as an event) and protocol 21 in the core-only
  `EndToEndTests` - because the records belong to the panel's unit - none.

## Not done, and for the merge

- **The two branches must merge together.** With this branch alone, Space
  in a file pane runs `quickView.toggle`, which the window does not handle
  yet, and marking in place is on Shift+Space, which the window takes from
  the core's keymap (not tried: no window ran). The live check's step "11a:
  Space on photos: marked in place and measured" presses Space and will
  fail until W5 changes it to Shift+Space; `docs/ui.md` (its key table row
  "Space | edit.toggleSelectionInPlace") is the panel unit's to update.
- **`plus` is not in the core's key grammar.** ADR 0023's page keys include
  `plus` (and the example `"keys":["left","right","plus","minus"]`), but
  `cabinetos-commands` knows `equal`, `numpadadd` and `minus`, not `plus`.
  The messages schema follows the ADR; the window's grant code must decide
  what `plus` means (`shift+equal` or `numpadadd`), or the ADR changes.
- The `settings-three-ways` audit gains `quickView.viewers` when the panel
  makes it reachable from the window and the palette (W8).
- A stray `cabinetos-cli.exe term cwd` from a terminal test of an earlier
  run held this worktree's `target\debug\cabinetos-cli.exe`, which stopped
  `cargo build`. It was not killed (the handout says kill nothing); the
  locked file was renamed to `cabinetos-cli.exe.orphan-52716` in `target`
  so cargo could write a new one. A second such process from 02:56 belongs
  to another checkout. The terminal tests leave this process behind now
  and then; that is worth a look of its own.
