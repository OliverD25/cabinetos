# Commands and keybindings

How CabinetOS names its actions, how keys are written, how chords work, and
which bindings nobody can change. This is the spec the core and the UI share:
the core compiles the keymap, the UI runs the key state machine described
here. Constitution Article 7 (Absolute Keyboard Control & Command Palette);
brief §7; the design's "Interactions and keyboard" and "Command palette"
sections ([design/README.md](design/README.md)).

The source of truth is the Rust crate `core/crates/cabinetos-commands`. The
user's changes live in `cabinetos.json` ([config.md](config.md)). The
messages are in [ipc.md](ipc.md).

## Commands

Every action is a command. A button, a menu item, the palette, a key binding
and a plugin all run commands by ID, so an action behaves the same however it
is started. A command has:

| Field | Example | Meaning |
|---|---|---|
| `id` | `view.toggleDualPane` | `category.verbObject`, unique |
| `category`, `title` | `View`, `Toggle Dual Pane` | What the palette shows: `View: Toggle Dual Pane` |
| `default_keys` | `ctrl+shift+d` | Its keys before the user changes anything; may be empty |
| `source` | `core` | Who provides it: the core, or a plugin (`{"kind":"plugin","id":…}`) |
| `target` | `ui` | Who runs it: the UI, or the core |
| `when` | `filesView` | The context of its bindings; none means everywhere |
| `immutable` | `true` | Part of the Immutable System Tier (below) |

The core's commands, in palette order:

| ID | Title | Default keys | When | Runs in |
|---|---|---|---|---|
| `palette.show` | View: Show Command Palette | `ctrl+shift+p` | | UI |
| `overlay.close` | View: Close Overlay | `escape` | | UI |
| `keys.open` | Preferences: Open Keyboard Shortcuts | `ctrl+k ctrl+s` | | UI |
| `view.toggleDualPane` | View: Toggle Dual Pane | `ctrl+shift+d` | | UI |
| `view.toggleTerminal` | View: Toggle Integrated Terminal | `ctrl+backquote` | | UI |
| `view.focusOtherPane` | View: Focus Other Pane | `tab` | `filesView` | UI |
| `view.toggleSidebar` | View: Toggle Sidebar | `ctrl+b` | | UI |
| `file.copyToOtherPane` | File: Copy to Other Pane | `f5` | `filesView` | core |
| `file.moveToOtherPane` | File: Move to Other Pane | `f6` | `filesView` | core |
| `file.newFolder` | File: New Folder | `f7` | `filesView` | core |
| `go.toPath` | Go: Go to Path… | `ctrl+l` | | UI |
| `marketplace.browse` | Marketplace: Browse Plugins and Themes | `ctrl+shift+x` | | UI |
| `workspace.switch` | Workspace: Switch Workspace… | `ctrl+k ctrl+w` | | UI |
| `preferences.selectColorTheme` | Preferences: Color Theme | `ctrl+k ctrl+t` | | UI |
| `terminal.runTask` | Terminal: Run Task… | `ctrl+shift+b` | | UI |
| `help.about` | Help: About CabinetOS | | | core |

- The list is the design's `COMMANDS` array without its plugin commands
  (Markdown preview, hex view, Git, compression). Those arrive as plugins in
  Phase 7 and register their own commands (Article 10). The palette, the
  overlay exit, the shortcut editor and About are added.
- The design wrote the workspace switcher as "Ctrl+K W" in one place and
  "Ctrl+K then Ctrl+W" in another (PLAN.md, conflict E). The registry uses
  `ctrl+k ctrl+w`: Ctrl stays held for the second key, like every other chord
  here. The UI shows it as "Ctrl+K Ctrl+W".
- Asking the core to run a UI command (`execute_command`) returns
  `command_routed`: the core does nothing and hands it back. Of the core's
  own commands only `help.about` exists yet; the file commands answer
  `not_implemented` until Phase 4.

## Writing keys

A binding is one **combination** (`ctrl+shift+p`) or a **chord** of two
combinations separated by a space (`ctrl+k ctrl+s`). A combination is any
number of modifiers and exactly one key, joined with `+`.

- **Modifiers:** `ctrl`, `shift`, `alt`, `win`. Also accepted: `control` for
  `ctrl`, `meta` for `win`. A modifier may appear once.
- **Keys:** the letters `a`–`z`, the digits `0`–`9`, `f1`–`f24`, and
  `escape`, `enter`, `tab`, `space`, `backspace`, `delete`, `insert`, `home`,
  `end`, `pageup`, `pagedown`, `up`, `down`, `left`, `right`, `backquote`,
  `comma`, `period`, `slash`, `minus`, `equal`, `bracketleft`,
  `bracketright`, `backslash`, `semicolon`, `quote`.
- **Also accepted for keys:** `esc`, `return`, `del`, `ins`, `pgup`, `pgdn`,
  and the characters themselves: `` ` `` `,` `.` `/` `-` `=` `[` `]` `\` `;`
  `'`. The `+` key cannot be written as `+`, because `+` joins the parts;
  write `shift+equal`.
- **At most two combinations.** Two already multiply the number of free
  shortcuts, and a longer chord would make the UI's state machine harder to
  follow.

Keys are read without regard to case or to the order of the parts, and
stored in one **normal form**: lower case, modifiers in the order
`ctrl+shift+alt+win`, then the key, one space between the two combinations of
a chord. So `Shift+Ctrl+P` becomes `ctrl+shift+p`, and `Ctrl+K  Ctrl+S` (two
spaces) becomes `ctrl+k ctrl+s`. There is no space inside a combination. The
core writes, sends and compares only the normal form.

Keys that break these rules are refused with an error that names the problem,
for example `` `ctrl+nope` is not a valid key binding: `nope` is not a key
name ``: in a request as the error code `invalid_keys`, in the file as a
`config_error` with the line.

## Chords

The keymap the core sends has `chord_window_ms` (1000). The UI's key state
machine works like this:

1. **Idle.** When a combination is pressed (pressing a modifier alone does
   not count):
   - if it is the first half of a chord bound in the current context, the UI
     waits for the second half (step 2) and says so in the status bar, for
     example "Ctrl+K was pressed. Waiting for the second key…";
   - otherwise, if it is bound on its own in the current context, the UI
     runs that command;
   - otherwise the key goes on to whatever has focus (typing, for example).
2. **Waiting.** The next combination, if it comes within `chord_window_ms`,
   ends the wait:
   - if it completes a chord that starts with the first half, the UI runs
     that command;
   - otherwise nothing runs, and the key is not passed on. The status bar
     says the chord is not bound.
   If no combination comes within `chord_window_ms`, the wait ends and
   nothing runs.
3. Back to idle.

No combination is ever both the first half of a chord and a binding on its
own (the core refuses such a keymap, below). So in step 1 the UI never has to
guess whether to run a command now or wait.

**Contexts.** A binding with a `when` applies only while that context holds;
a binding without one applies everywhere. When both kinds match the same
keys, the one with the `when` wins: it is the more specific. The core treats
`when` values as plain names and never evaluates them; the UI decides which
hold. The names in use: `filesView` (a file pane has focus), `paletteOpen`
(the command palette is open), `textInput` (a text box has focus),
`terminalFocus` (the terminal has focus).

## The Immutable System Tier

Three commands keep their keys whatever the configuration says:

| Command | Keys | Why |
|---|---|---|
| `palette.show` | `ctrl+shift+p` | The way to every other command |
| `overlay.close` | `escape` | The way out of any overlay |
| `keys.open` | `ctrl+k ctrl+s` | The way to repair a broken keymap |

With these three, nobody can configure themselves out of the application.
The rules:

1. Their bindings cannot be changed or removed.
2. No other command may use their keys.
3. No other command may be bound to a combination that starts one of their
   chords: `ctrl+k` alone would make `ctrl+k ctrl+s` unreachable.

A `set_keybinding` request that breaks a rule gets the error code
`immutable_binding`, and the file is not changed. A configuration file that
breaks one is refused as a whole: the core keeps the settings it had and sends
`config_error` with the line of the offending entry. The same holds when the
core starts with such a file: it uses the defaults until the file is fixed.

## Changing bindings

The user's changes are the `keybindings` list in `cabinetos.json`. It lists
only what differs from the defaults:

```json
"keybindings": [
  { "command": "view.toggleSidebar", "keys": "ctrl+alt+b" },
  { "command": "view.toggleTerminal", "keys": "" },
  { "command": "file.newFolder", "keys": "ctrl+shift+n", "when": "filesView" }
]
```

- An entry replaces **all** default keys of its command. Several entries for
  one command give it several bindings.
- `"keys": ""` leaves the command without keys.
- Without `when`, the entry keeps the command's own context. So rebinding
  `file.copyToOtherPane` does not make it fire inside a text box.
- An entry for a command nobody registered is ignored, with a warning in the
  core's log: the plugin that registered it may have been removed.

The core compiles the defaults and these entries into the keymap, and refuses
the whole configuration when:

- an entry breaks the Immutable System Tier (above);
- two commands get the same keys in the same context: "ctrl+b is bound to
  both view.toggleSidebar and go.toPath";
- a combination is bound on its own and also starts a chord, in any context.
  With `go.toPath` on `ctrl+g ctrl+p` and `view.toggleSidebar` on `ctrl+g`:
  "ctrl+g starts the chord ctrl+g ctrl+p of go.toPath, so it cannot also run
  view.toggleSidebar on its own".

The same keys in different contexts are allowed (see "Contexts" above).

**From the UI or the command line.** `set_keybinding` binds a command to new
keys and `reset_keybinding` gives it its defaults back; both write the file
and answer with the new keymap. `set_keybinding` replaces the user's earlier
entries for that command with one entry without `when`. From a terminal:

```text
cabinetos-cli keys list
cabinetos-cli keys set view.toggleSidebar "ctrl+alt+b"
cabinetos-cli keys reset view.toggleSidebar
cabinetos-cli keys watch
```

`keys watch` prints every change the core announces, with the time since the
file was written.

## Palette search

The palette sends what the user typed (`search_commands`); the core ranks the
commands, since the UI does no data processing (brief §1). A command matches
when the typed letters appear in order, not necessarily together, in
`Category: Title` or in its ID. Case and spaces in the query are ignored.
Among all the ways a command matches, the best one counts:

| For | Points |
|---|---|
| each matched letter | +16 |
| a matched letter that starts a word (after a space, `:`, `.`, `-`, `_`, `/`, or at a lower-to-upper case change as in `toggleDualPane`) | +24 |
| a matched letter right after the previous matched one | +20 |
| each letter skipped between two matched ones | −3, at most −30 per gap |
| each letter skipped before the first match | −1, at most −15 |

The highest score comes first; a tie goes to the shorter text, then to the
registry order. An empty query lists every command in registry order.
Examples: `dual` finds `view.toggleDualPane` first, `new fold` finds
`file.newFolder`, `tdp` finds `view.toggleDualPane` by its word starts.
