## Report: the last two things the shell waits for (protocol version 9)

Context: the command registry (`core/crates/cabinetos-commands`), and a new type-name and icon service (`core/crates/cabinetos-fs/src/hydrate.rs`) wired through the core and the CLI.

**Result: done.** Both parts are pushed, and CI run https://github.com/OliverD25/cabinetos/actions/runs/36441383933 (on 863404c, which holds both parts) is green and was not cancelled:
- core: 434 passed, 0 failed, 5 ignored;
- ui: 203 passed, 3 skipped (the end-to-end tests, which need a core build);
- deny: ok.

The UI agent has already built on both parts: 12a6d51 (the commands) and cd51ea8 (type names and icons).

### Built
- **Part A: the shell's commands in the registry** (`registry.rs`). The seed grew from 16 to 32 commands. All the new ones have `target: ui`, with these default keys:

  | Keys | Command |
  |---|---|
  | alt+left | `go.back` |
  | alt+right | `go.forward` |
  | alt+up | `go.up` |
  | enter (filesView) | `pane.openSelected` |
  | f2 (paletteOpen) | `keys.rebind` |
  | f2 (filesView) | `file.rename` |
  | delete (filesView) | `file.delete` |
  | shift+delete (filesView) | `file.deletePermanently` |
  | ctrl+enter (filesView) | `file.openInOtherPane` |
  | alt+enter (filesView) | `file.properties` |
  | ctrl+x, ctrl+c, ctrl+v, ctrl+a (filesView) | `edit.cut`, `edit.copy`, `edit.paste`, `edit.selectAll` |
  | insert (filesView) | `edit.toggleSelection` |
  | ctrl+f | `search.focus` |

  - `file.copyToOtherPane`, `file.moveToOtherPane` and `file.newFolder` are now `target: ui`, so `execute_command` answers `command_routed` for them. `help.about` is the only command the core runs.
  - The conflict check already keys on (keys, when), so F2 twice is allowed. A new test proves both F2 bindings compile, and that moving `file.rename`'s F2 into `paletteOpen` is a conflict.
  - `docs/keybindings.md` lists all 32 commands and explains the shared F2. It replaces the stale "not_implemented until Phase 4" bullet.
- **Part B: type names and icons** (`cabinetos_fs::Hydrator`; it replaces the Phase 2 placeholder).
  - **`describe_entries { listing_id, from, count ≤ 512 }`** answers `entry_details { listing_id, generation, from, details: [{type_name, icon_key}] }` for that range of the listing's current section.
    - Type names come from `SHGetFileInfoW(SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES)`, cached per extension.
    - Icon keys: `folder`, `ext:.<lower-case ext>`, `generic`, or `path:<16 hex>` for `.exe`, `.ico` and `.lnk`.
  - **`get_icon { key, size 16|24|32|48 }`** answers `icon { key, size, png_base64 }`: an RGBA PNG, cached (2,000 entries).
  - **Errors:** unknown key or file gone → `not_found`; shell failure → `io`; a bad size or a count over 512 → `protocol_error`; unknown listing → `no_such_listing`.
  - **Core refactor:** a listing's slot is now one struct. It holds the folder and a shared current section, which a watched listing's refresh task replaces. This is how `describe_entries` reads the section clients see now. `Services` gained the hydrator.
  - **CLI:** `describe <path> [--from N --count M]` prints index, name, type name and icon key, then the round-trip time. `icon <key> [--size N] --out file.png` writes the PNG and prints its size from the header.
  - **COM apartment:** the helper moved from `open.rs` to a shared `com.rs`.
- **Docs and tests:**
  - `docs/ipc.md`: version 9 and a "Type names and icons" section.
  - `core/README.md`, and the schemas regenerated.
  - Tests:
    - type names: exact English names where the UI language is English, and distinct, non-empty names always;
    - PNGs decode at every size with a partly clear alpha channel;
    - cache limits (oldest out first), unknown keys, a program that is gone, keys that change when the file changes;
    - two end-to-end tests through the real binary: describe plus icons, and a watched listing at its current generation.
  - The UI's end-to-end test now pins version 9.

### Commits (all pushed to origin/main)
- `3543018` commands: the shell's own commands in the registry, all bindable
- `863404c` core: the shell's type names and icons, protocol version 9

### Checks (local, on 863404c)
- `cargo build --workspace`: exit 0.
- `cargo test --workspace`: **434 passed**, 0 failed, 5 ignored. It was 421 before this task.
- `cargo clippy --workspace --all-targets -- -D warnings`: exit 0.
- `cargo fmt --all -- --check`: exit 0.
- `cargo deny check`: advisories, bans, licenses and sources all ok.
  - New crates: `png` 0.18.1 and `base64` 0.22.1, plus their dependencies `fdeflate`, `flate2`, `miniz_oxide`, `adler2`, `simd-adler32`.
  - Licenses are MIT, Apache-2.0 or Zlib alternatives, all allowed.
- UI tests run locally on the merged main: 206 pass.

### Live check
- **Setup:** release build. The core ran on the test pipe `v9live`, with its config, logs and plugins in my scratchpad.

```
pong id=01M3M93FJTHQAVP0Z887X5P7EJ protocol=9 core=0.1.0 rtt=0.56ms
== cabinetos-cli describe C:\Windows --count 10 (first run of this core)
    0  appcompat                        File folder                  folder
    1  apppatch                         File folder                  folder
    2  AppReadiness                     File folder                  folder
    3  bcastdvr                         File folder                  folder
    4  Boot                             File folder                  folder
    5  Branding                         File folder                  folder
    6  BrowserCore                      File folder                  folder
    7  CbsTemp                          File folder                  folder
    8  Cursors                          File folder                  folder
    9  debug                            File folder                  folder
10 of 108 entries described in 7.7 ms (round trip)
== again (warm caches): 10 of 108 entries described in 1.8 ms (round trip)
== the files (from 80): 83 explorer.exe Application path:2a7b483ac2c7cddc | 86 Info.xml Microsoft Edge HTML Document ext:.xml | 89 NvContainerRecovery.bat Windows Batch File ext:.bat | 92 pyshellext.amd64.dll Application extension ext:.dll | 99 system.ini Configuration settings ext:.ini | 105 WindowsUpdate.log Text Document ext:.log
28 of 108 entries described in 23.9 ms (round trip)
== cabinetos-cli icon ext:.txt --size 32 --out txt-32.png
txt-32.png: 32x32 PNG, 620 bytes
file size on disk: 620 bytes
IHDR: width 32 height 32 bit depth 8 color type 6 (6 = RGBA)
```

- **Core log times (`elapsed_us`):**
  - `describe_entries` (in the order above):
    - 7,461 µs on the first request (COM start and a new type name);
    - 1,712 and 1,540 µs warm;
    - 23,676 µs for 28 entries with 8 new extensions, about 2.5 ms per new type name.
  - `get_icon`: 7–26 ms, and 70 ms for one first icon; `explorer.exe` at 48 px took 13 ms.
- **What the icons look like:** I viewed the PNGs, and they are right: a yellow folder, the text page, the photo and PDF icons, `notepad.exe`, the Task Manager icon, and `explorer.exe`, all with transparent backgrounds. The 24 px halving is clean.
- **Refusals:**
  - `path:0000000000000000` → not_found;
  - `bogus` → not_found;
  - `folder --size 20` → protocol_error.

### Decided
- **`keys.rebind` is titled "Change Keys of Selected Command"** — because the shorter "Change Key Binding" made "keys" in the palette rank it above `keys.open` (a tie is broken by the shorter label). Undo: its title in `registry.rs`.
- **Palette order:** the new commands sit in their groups (Pane, File, Edit, Go, Search), not at the end. Undo: the order in `SEED`.
- **The hash in `path:` keys** is FNV-1a 64 over the lower-case full path, the last-write time and the size, as 16 hex digits — because a replaced program then gets a new key, so a client's icon cache stays right. FNV-1a gives the same result in every build. The core remembers the last 16,384 path keys; a key it forgot is `not_found` until the folder is described again. Undo: `path_key` and `PATHS_KEPT` in `hydrate.rs`.
- **Icons come from the system image lists**, not from `SHGFI_ICON`:
  - `SHGFI_SYSICONINDEX` gives the index, then `SHIL_SMALL` gives 16, `SHIL_LARGE` 32, and `SHIL_EXTRALARGE` 48;
  - 24 is the 48-pixel icon halved with an alpha-weighted average;
  - because `SHGFI_ICON` has no 48 and depends on the process's DPI setting.
  - A `path:` key uses the same call without `USEFILEATTRIBUTES`, so the file itself is read, as asked.
  - A shortcut's icon has no arrow overlay.
  - Undo: `draw_icon` in `hydrate.rs`.
- **Icons are drawn one at a time (a mutex)** — because the system image lists are not documented as safe to use from several threads. Every call enters and leaves an STA COM apartment. Undo: `drawing` in `hydrate.rs`.
- **A describe range past the end answers an empty list, not an error**, so the UI can ask for a whole screen without knowing the count. Undo: the `end` computation in `describe`.
- **Part B is one commit** — because the workspace lock file gained `png` and `base64` together; the fs crate and the core wiring were not split.
- **I pushed Part A before building Part B**, as the per-unit rule says. While their handlers were still registered as local, the UI's router would have shown "arrives in a later version" for these commands. The UI agent switched to `RegisterUiHandler` 16 minutes later, in 12a6d51, so no fix is needed now.

### Needs the user
- **Ready text for `docs/PLAN.md` (Phase 5).** Replace "Waiting for the core: shell type names and icons (being built)." with:
  > The core's side of the rest is built (protocol version 9): the shell's commands are in the core's registry with their keys, so every one of them shows in the palette and can be rebound (`go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind`, `file.rename`, `file.delete`, `file.deletePermanently`, `file.openInOtherPane`, `file.properties`, `edit.cut`, `edit.copy`, `edit.paste`, `edit.selectAll`, `edit.toggleSelection`, `search.focus`; F2 renames in a pane and records keys in the palette), and the shell's type names and icons come from the core: `describe_entries` gives each shown row the shell's type name (by extension, no disk access) and an icon key (`folder`, `ext:.txt`, `generic`, or `path:` for programs, icons and shortcuts, whose icon is their own), and `get_icon` gives that icon as a PNG of 16, 24, 32 or 48 pixels from the system image lists. Measured 2026-09-28 on this PC (release build): 10 entries of `C:\Windows` described in 7.5 ms on the first request and 1.5–1.7 ms after; an icon in 7–26 ms the first time, then from the core's cache.
- **README status line:** change "(`core/`, 421 tests, CI green)" to "(`core/`, 434 tests, CI green)". You may also add "…and the shell's type names and icons…" to the list of what the core gives the shell. The UI count is the UI agent's own: 206 on main now.
- **For the UI agent: the exact command IDs now in the core's registry** (it has adopted them in 12a6d51):
  - `go.back`, `go.forward`, `go.up`, `pane.openSelected`, `keys.rebind`;
  - `file.rename`, `file.delete`, `file.deletePermanently`, `file.openInOtherPane`, `file.properties`;
  - `edit.cut`, `edit.copy`, `edit.paste`, `edit.selectAll`, `edit.toggleSelection`, `search.focus`;
  - `file.copyToOtherPane`, `file.moveToOtherPane` and `file.newFolder` are now `target: ui`.
  - `docs/ui.md:229` still says the core answers `not_implemented` for the three file commands.

### Known gaps
- **The first icon of a program can be slow.** It takes up to 0.8 s in a debug build (the shell reads the file); in release I saw 7–70 ms. Each icon is cached after that.
- **Shortcut icons have no arrow overlay**, and special folders (Desktop, Documents with `desktop.ini` icons) all show the plain `folder` icon.
- **Type names are cached per extension for the life of the core**, so an association changed while it runs shows the old name until a restart. The key cache holds 4,096 extensions.
- **English type names are asserted only where the user's UI language is English** (this PC and CI). Elsewhere the tests check only that the names are distinct and not empty.

### Noticed out of scope
- `cargo deny` warns about a new duplicate: `miniz_oxide` 0.8.9 (through png) and 0.9.1. It is a warning only, like the existing duplicates in the wasmtime tree.
- Backspace for Up stays a UI-local habit (the UI agent says so). Pressing Backspace in a pane cannot be rebound.
