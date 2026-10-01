# Keys audit, 2026-10-01 (Phase 19d)

Context: the creator's words before sleeping, "double check the
shortcuts, I felt like they don't work all the time in our app".
Constitution Article 7 says the mouse is optional and the Immutable
System Tier (Ctrl+Shift+P, Esc, Ctrl+K Ctrl+S) must always work.

**The short answer.** The feeling is right. Twelve faults were found.
Five are fixed, each with a test that failed first. Seven are proposals:
each one changes a rule the creator decided, or is rare and small, or
needs a test the night could not build. The biggest cause by far is a
documented rule, not a bug: while the keyboard is in any text box, only
the three keys of the Immutable System Tier work. The find box, the address box, the palette,
Quick Open, the Search field and the marketplace all hold a text box, so
Ctrl+B, Ctrl+`, Ctrl+T, Ctrl+W, Ctrl+Tab and the chords do nothing there.
That rule is proposal P1 below; it needs the creator's yes.

## The faults, ranked by how often a user meets them

| # | Fault | How often | Status |
|---|---|---|---|
| P1 | In a text box only the Immutable System Tier works | every find, address edit, palette, Quick Open, Search, marketplace | proposed |
| F1 | In a dialog only Esc worked: Tab, Enter, Space and the arrows did nothing | every dialog | fixed, f4eca09 |
| F2 | A chord failed when its first key was held long enough to repeat | a chord held about half a second | fixed, 7487adb |
| F3 | Tab did nothing while the other pane showed a Markdown Preview; F5 then copied into the hidden folder | every preview in the other pane | fixed, 5336885 |
| P2 | Inside a tool page only Ctrl+Shift+P and Ctrl+` work: Tab, Ctrl+W, Ctrl+Tab, Esc stay in the page | every preview the keyboard enters | proposed |
| F4 | Tab left the theme picker and the plugin list open with the keyboard under them | Tab in those two | fixed, 6db2c21 |
| F5 | A chord that is bound was reported "not bound" where it does not apply | every chord in a text box | fixed, a0b5d44 |
| P3 | A held toggle key repeats: the palette, the terminal and the sidebar flicker | a toggle held about half a second | proposed |
| P4 | Overlays stack: Ctrl+K Ctrl+T from the drive list opens the picker over it, and Esc closes the wrong one first | rare | proposed |
| P5 | The permissions review lets Tab out, as the plugin list did (by reading) | rare | proposed |
| P6 | Esc or Ctrl+Shift+P pressed during a chord's wait is swallowed as "not bound" | rare | proposed |
| P7 | The palette opened from a tool page gives the keyboard back to the pane, not the page | rare | proposed |

## Each fault

**P1, text boxes block the shortcuts (proposed, size S).**
Reproduce: Ctrl+F, type a name, press Ctrl+B, Ctrl+`, Ctrl+T or Ctrl+W.
Nothing happens. The same in the address box (Ctrl+L), the palette,
Quick Open, the Search field and the marketplace, whose search field
holds the keyboard whenever the marketplace shows.
Cause: `ChordStateMachine.Applies` lets a binding without `when` run in
`textInput` only when it is in the Immutable System Tier
(keybindings.md, "Contexts"). The rule protects typing, but it also
blocks keys that type nothing, such as Ctrl+B.
Proposal A: in a text box, a binding without `when` also applies when its
combination cannot type or edit text. That means it has Ctrl or Alt (not
both, which is AltGr), or it is F1 to F24, and it is not one of the
box's own keys: Ctrl+A, C, V, X, Z, Y, Ctrl+Backspace, Ctrl+Delete,
Ctrl+Left, Right, Home, End (with or without Shift), Ctrl+Insert,
Shift+Insert, Shift+Delete. Typing stays safe, and Ctrl+B, Ctrl+`,
Ctrl+P, Ctrl+, and the chords without `when` (Ctrl+K Ctrl+T, Ctrl+K
Ctrl+W) work everywhere. Size S: one pure function
and its tests in `CabinetOS.Core/Keys`, and the rule's text in
keybindings.md and ui.md.
Proposal B, a separate choice: the find box and the address box belong
to a pane, so they could also hold `filesView` for keys that type
nothing (Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+1 to 9). Size S.

**F1, dialogs ignored every key but Esc (fixed).**
Reproduce: Shift+Delete on a file, then Tab or Enter. The focus stayed on
Cancel and nothing ran; the log said "key held by a dialog". The same in
Properties (tested). About, the update questions and uninstall go through
the same code.
Cause: WinUI routes a dialog's keys through the window's root on their
way in. The window's `PreviewKeyDown` held every key while a dialog was
open, on the belief that keys in the dialog never reach it.
Fix: a key whose focus is inside the open dialog goes on to the dialog;
no binding of the window runs for it. A key under the dialog is held as
before. Test: `KeysEndToEndTests.Tab_and_Enter_reach_the_buttons_of_a_dialog`.

**F2, a chord failed when its first key was held (fixed).**
Reproduce: hold Ctrl+K until Windows repeats it, then press Ctrl+T. With
one repeat the status bar said "Ctrl+K Ctrl+K is not bound"; with two,
the chord worked. So a chord worked or failed by how long Ctrl+K was held.
Cause: the machine read the repeat as the chord's second half.
Fix: a repeat of the waiting first half is the same press; it keeps the
wait and starts the chord window again. Tests: three new
`ChordStateMachineTests`; real keys with 2 and 3 repeats.

**F3, Tab into a tool tab in the other pane (fixed).**
Reproduce: Enter on a .md file (it opens in the other pane), then Tab.
The keyboard stayed in the first pane; F5 then copied the file into the
folder hidden under the preview.
Cause: `FocusOtherPane` focused the other pane's list, which the preview
covers and which cannot take the keyboard.
Fix: Tab gives the keyboard to the tool's page, as a click into it does.
Test: `KeysEndToEndTests.Tab_gives_the_keyboard_to_a_tool_tab_in_the_other_pane`.

**P2, keys inside a tool page (proposed, size S).**
Once the keyboard is in a Markdown Preview, only Ctrl+Shift+P and Ctrl+`
get out; Tab, Ctrl+W, Ctrl+Tab, Ctrl+1 and Esc stay in the page. This is
the documented rule for tool pages (tool-extensions.md, "The window's
keys"). Proposal: a tool page in a pane's tab hands back the tab keys
(`tab.close`, `tab.next`, `tab.previous`, `tab.select`, `tab.new`), as a
sidebar page hands back its three. Tab and Esc stay the page's, because a
page uses them for its links. Size S: `TerminalKeys.PassKeys` gets the
list, and the key script already hands back what it is given.

**F4, Tab left the theme picker and the plugin list (fixed).**
Reproduce: Ctrl+K Ctrl+T, Tab, Enter. Tab moved the keyboard to the
sidebar's Desktop row or the pane while the picker stayed; Enter then
opened a folder or went to Desktop. The plugin list sent it to the rail.
Cause: the palette, Quick Open and the prompts keep Tab; these two did not.
Fix: the picker takes Tab as the palette does; the plugin list cycles Tab
through its own buttons. Test:
`KeysEndToEndTests.Tab_keeps_the_keyboard_in_the_theme_picker_and_in_the_plugin_list`.

**F5, "not bound" for a bound chord (fixed).**
Reproduce: Ctrl+F, then Ctrl+K Ctrl+T. The status bar said "Ctrl+K
Ctrl+T is not bound to a command". Outside a pane, Ctrl+K V said the same.
Cause: the notice did not look for the chord in other contexts.
Fix: it says where the chord works: "... does not work while you type in
a box. Esc leaves the box." or "... works only in a file list." Test: a
new `ChordStateMachineTests` case; real keys in the find box.

**P3, held toggles repeat (proposed, size S).** Holding Ctrl+Shift+P,
Ctrl+` or Ctrl+B runs the toggle once per repeat, so the palette or the
terminal flickers and ends open or shut by chance. Proposal: a repeat
runs only the commands that are meant to repeat (Ctrl+Tab, Alt+Left,
Insert and the like); a list in the window, or a `repeat` flag in the
core's registry. The creator decides which commands repeat.

**P4, overlays stack (proposed, size S).** The drive list (Alt+F1) is not
a text box, so Ctrl+K Ctrl+T opens the theme picker over it. The first
Esc then closes the drive list and gives the keyboard to the pane while
the picker stays on screen. Proposal: one helper that closes the other
overlays, called by every overlay that opens.

**P5, the permissions review (proposed, size XS).** It is built like the
plugin list, so Tab leaves it after its last button. The fix is the same
one line (`TabFocusNavigation="Cycle"`). Not applied, because no test can
open the review yet (it needs a plugin that asks for a capability).

**P6, Esc during a chord's wait (proposed, size XS; by reading).** Ctrl+K
then Esc says "Ctrl+K Esc is not bound" and closes nothing; Ctrl+Shift+P
there is swallowed the same way. Proposal: Esc ends the wait quietly.

**P7, the palette from a tool page (proposed, size XS).** Esc after the
palette gives the keyboard to the pane; from the terminal it goes back to
the terminal. Proposal: remember the tool page as the terminal is
remembered.

## Where the keyboard can be, and which keys reach their command

Pane keys: F5, Enter, Tab, Space, Ctrl+F3, Ctrl+A, Delete, Backspace.
Global keys: Ctrl+Shift+P, Ctrl+B, Ctrl+`, Ctrl+, and the pane's Ctrl+T,
Ctrl+W, Ctrl+Tab, Ctrl+1 (these four are `filesView` bindings). Chords:
Ctrl+K Ctrl+T, Ctrl+K V, Ctrl+K Ctrl+W. The tier: Ctrl+Shift+P, Esc,
Ctrl+K Ctrl+S. "R" means checked with real keys, "C" by reading the code.

| Where the keyboard is | Pane keys | Global keys | Chords | The tier |
|---|---|---|---|---|
| a pane's list | all (R) | all (R) | all three (R) | all (R) |
| address box (Ctrl+L) | the box's own: Enter goes, Tab leaves (R) | only Ctrl+Shift+P (R, P1) | none; notice fixed (R, F5) | all (R) |
| find box (Ctrl+F) | the box's own: Enter finds, Down to the list (R) | only Ctrl+Shift+P (R, P1) | none (R, F5) | all (R) |
| Quick Open, the palette | their own; Tab kept (R) | only Ctrl+Shift+P (R, P1) | none (R, F5) | all (R); F2 rebinds in the palette (C) |
| a prompt with a box (Num +, Ctrl+D) | the prompt's (R) | only Ctrl+Shift+P (R, P1) | none (C) | all (R) |
| the drive list (Alt+F1) | letters, Enter, Tab kept (R) | Ctrl+B yes, Ctrl+T no (R) | Ctrl+K Ctrl+T stacks the picker (R, P4) | all (R) |
| theme picker | Up, Down, Enter; Tab fixed (R, F4) | no-`when` keys yes, Ctrl+T no (R) | yes (C) | all (R) |
| plugin list | its buttons; Tab fixed (R, F4) | no-`when` keys yes (R) | yes (C) | all (R) |
| permissions review | its buttons; Tab leaks (C, P5) | refused, modal (C) | refused (C) | Esc (C) |
| marketplace | its search box (R) | only Ctrl+Shift+P (R, P1) | none (C) | all (R) |
| context menu open | the menu's own (R) | no-`when` keys yes, Ctrl+T no (R) | yes (C) | all (R) |
| menu edit mode | its own keys (R) | held, by design (R) | held (C) | Esc only, by design (R) |
| Windows' menu | the menu's own (R) | no-`when` keys yes (R) | yes (C) | all (R) |
| terminal | to the shell, by design (R) | Ctrl+Shift+P, Ctrl+` (R) | to the shell (R) | Ctrl+Shift+P only, by design (R) |
| tool page (Markdown Preview) | to the page (R, P2) | Ctrl+Shift+P, Ctrl+` (R) | to the page (C) | Ctrl+Shift+P only (R) |
| sidebar tree (rail layout) | the tree's own; Tab to the pane (R) | all no-`when` keys (R) | yes (C) | all (R) |
| sidebar rows, rail, top row, tab strip, crumbs | never take the keyboard from a click (C, chrome test) | | | |
| a dialog | fixed: Tab, Enter, Space, arrows (R, F1) | held, by design (R) | held (C) | Esc (R) |
| rename box | the box's; Esc cancels, Tab commits and goes to the other pane (R) | only Ctrl+Shift+P (R, P1) | none (C) | all (R) |
| quick search | all; Space marks (R), Backspace goes up as in Explorer (C) | all (R) | all (C) | all (R) |
| other pane shows a tool | Tab fixed (R, F3) | all (R) | all (R) | all (R) |
| folder gone from disk | all reach their command, which says "not found" (R) | all (R) | yes (R) | all (R) |

## Timing

- **Just after a menu, flyout or dialog closes.** The keyboard is back in
  the pane within 20 ms. Keys pressed at once queue and land correctly.
- **Within the context menu's first frame.** Keys pressed with no gap
  after Shift+F10 queue behind the opening and land in the menu.
- **While a listing arrives.** Keys queue and act on the new listing
  (10,000 entries in 16 ms; the gap is too short to press into).
- **A chord half pressed, then the focus moves.** The wait stays; the
  second half is read in the new place. Deactivating the window ends it.
- **A key held so it repeats.** F2 above (fixed) and P3 (proposed).
- **After Alt+Tab away and back.** Checked with another window in front
  and then this one again, in the pane and in the terminal: the keyboard
  came back to the same place every time.

## The Ukrainian layout

The window's own layout was switched to Ukrainian (Enhanced), F0A80422,
with `WM_INPUTLANGCHANGEREQUEST`, and back to US afterwards. Ctrl+T,
Ctrl+W, Ctrl+P, Ctrl+Shift+P, Ctrl+K Ctrl+T, Ctrl+, and Ctrl+\ all ran
their commands, sent both as virtual keys and as physical keys (scan
codes). Ctrl+` ran from the pane and from the terminal page.
The window matches by virtual key, not by character. That is the right
choice: a Cyrillic layout keeps the Latin virtual keys on the same
physical keys (the key that types "е" is still VK_T), so Ctrl+T works on
every layout, as Windows' own Ctrl+C and Ctrl+V do. Matching by
character would break every Latin-letter key there. The backquote key is VK_OEM_3
on this layout too (it types an apostrophe), and the backslash key is
VK_OEM_5. The live check's new "keys" section repeats the Ctrl+T and
Ctrl+W part with physical keys.

## How it was checked

- The whole key path was read first: `MainWindow.Keyboard.cs`,
  `OnPreviewKeyDown`, the chord machine, the keymap and its contexts, the
  views' own key handlers, and the pages' key scripts.
- Real keys: a driver in the scratch folder started the Debug window on a
  scratch configuration with heavy logging, and checked before every key
  that the foreground window was its own (other coders' test windows came
  and went all night; it waited and brought its window back without a
  key). Two runs: 695 keys reached the window, 494 of them ran a command,
  about 2,000 characters were typed. Each experiment read the heavy log:
  "key pressed" with the focused element, "focus changed", "command
  executed", the notices.
- A new snapshot step, `key:<keys>`, posts real key messages to the
  window's own input window, so WinUI routes them as real keys without
  the window in front. It reproduced F1, F3 and F4 before the fixes.
- Tests: `KeysEndToEndTests` (3, end to end), 4 new chord tests, 7 key
  name tests. The live check's new "keys" section ran alone on the Debug
  build in Windows PowerShell 5.1 with real keys: 7 checks, all True.

## What could not be tried

- The permissions review (P5): no plugin that asks for a capability in
  the scratch setup.
- A real drive removed while listed: a folder renamed under the pane was
  used instead.
- A real Alt+Tab: the driver took the front with a window of its own
  instead, because Alt+Tab could land keys in another program.
- Of the dialogs, Properties and "Delete permanently?" were driven with
  real keys. About, the update questions, uninstall and the core-failure
  dialog go through the same `ShowDialogAsync` and the same fix; they
  were not opened.

## Side effects of the run

- Two Enter presses on scratch text files (alpha.txt, beta.txt) opened
  them in the Notepad already running on the PC, as two new tabs. They
  hold one "x" each and can be closed without saving.
- Once, the first character typed by the driver into the address box
  arrived as "È" instead of "C" (injected Unicode characters). It did not
  happen again and is not a key binding matter.

## Round two

The planning session's decision: build P1 with both of its rules, and P3,
P5, P6 and P7. P2 and P4 stay proposals. The same coder built them in the
same worktree, on 2026-10-01 from 02:00.

### What changed

| # | Now | Commit |
|---|---|---|
| P1 | In a text box a bound key that types nothing runs; the box keeps the keys that type or edit (rule A). The active pane's find box and address box are `filesView` too, so F5, Ctrl+T and Ctrl+F3 act on the pane from there (rule B) | 02817cb |
| P3 | A key held down runs its command once; its repeats run nothing. Six commands repeat: `tab.next`, `tab.previous`, `edit.toggleSelection` (Insert), `go.back`, `go.forward`, `go.up` | 0e6b24f |
| P5 | Tab stays inside the permissions review (`TabFocusNavigation="Cycle"`, by reading) | a332407 |
| P6 | During a chord's wait Esc only ends the wait; Ctrl+Shift+P ends it and opens the palette | bc36551 |
| P7 | The palette opened from a tool's page (a pane's tab or the sidebar) gives the keyboard back to that page | c2a8d8a |
| live check | The keys section checks the find-box keys and a held Ctrl+B; F5 copies only between the fixture's folders | 02817cb, 0e6b24f, 0c0a8af |

The rules are written in [keybindings.md](../../keybindings.md) ("Keys
held down", "Text boxes" under "Contexts", and step 2 of "Chords") and in
[ui.md](../../ui.md) (the find box, the address box, Quick Open, the
Search field, the compact overlay's key, the tool pages, the palette).

### Decisions the plan did not cover

Each is "what, because, undo".

- **AltGr with a character stays with the box.** Ctrl+Alt with a letter,
  a digit or a punctuation key types characters on many layouts (Polish,
  German, Ukrainian Enhanced). Undo: `(true, true) => false` in
  `TextInputKeys.StaysWithBox`. So Ctrl+Alt+P (`terminal.insertPath`) and
  the column view's new Ctrl+Alt+C do not run from a box. Ctrl+Alt+Up
  (the compact overlay) runs: an arrow types nothing.
- **Alt with a digit stays with the box.** Alt with the keypad's digits
  types a character by its code, and the grammar names those digits like
  the top row's. Undo: `(false, true) => false`.
- **Ctrl+Insert stays with the box.** A box copies with it, as with
  Ctrl+C. Undo: take `insert` out of `CtrlEditing`.
- **Every key without Ctrl, Alt or Win stays with the box**, the function
  keys aside: also Insert, PageUp, PageDown, the keypad's operators and
  Shift+Delete, which the creator's lists did not name. The rule runs only
  combinations and function keys, and Shift+Delete cuts text in a box: it
  must never delete files from a find box. Undo: none needed.
- **The other Ctrl combinations run:** Ctrl+Up, Ctrl+Down, Ctrl+PageUp,
  Ctrl+PageDown, Ctrl+Enter, Ctrl+Space, Ctrl with the keypad's operators.
  A one-line box does nothing with them. Undo: add them to `CtrlEditing`.
  Ctrl+Shift+X (the marketplace) and Ctrl+Shift+C (copy full path) stay
  with the box, because the rule gives it Shift with each of its edit
  keys.
- **Win with any key runs.** A box types nothing with Win. Undo: return
  true for Win in `StaysWithBox`.
- **A chord goes by its first half.** Once Ctrl+K has started the wait,
  the second key belongs to the chord, so Ctrl+K V works in the find box.
  Undo: judge the second half in `Applies`.
- **Rule B is for the active pane only.** The other pane's find box and
  address box are text input only: the pane's commands act on the active
  pane, so F5 in the other pane's box would copy from the wrong pane.
  Undo: add the other pane's boxes in `CurrentContexts` and make that pane
  active.
- **Rule A covers every box**, the rename box, the palette, Quick Open and
  the prompts included. So Alt+Left or Ctrl+L while a name is typed in
  place now runs; the rename ends as it does on any change of listing.
  Ctrl+P in Quick Open closes it (`quickOpen.show` toggles), and in the
  palette it switches to Quick Open, as in VS Code.
- **The six commands that repeat.** The registry binds no scroll command
  (PageUp and PageDown are the list's own keys). Insert marks the row and
  moves the cursor down; Back, Forward and Up move through folders as the
  arrows move through rows, change nothing, and repeat in Explorer too.
  Undo: edit `ChordStateMachine.RepeatingCommands`.
- **A web page passes a held key once, whatever its command.** The pages
  pass back only the toggles and ways out, and do not know the list.
  Undo: take out the `event.repeat` checks in `terminal.js` and
  `ToolKeyScript`.
- **The tier's single keys win over a chord that ends with them** during
  a wait, so there is always a way out. Ctrl+K pressed during a Ctrl+K
  wait still ends it with the notice. Undo: look for the chord before the
  tier in the waiting branch of `OnKey`.
- **P7 covers the sidebar's tool pages too**, and a page that is gone or
  hidden by then sends the keyboard to the active pane. Undo:
  `WayBackToToolPage` returns null for the sidebar.
- **No DONE file for the real-key runs.** The section ran alone twice at
  night; a Notepad window in front would stop other agents' checks. Undo:
  run the whole live check through `run-livecheck.ps1`.

### Evidence

- The UI builds with `-warnaserror`: 0 warnings. The fast tests: 1103
  passed, 32 end-to-end skipped, of 1135.
- The full run with the end-to-end tests, on a release core built from
  main after the rebase onto the column view: 1134 of 1135.
  `CompactOverlayEndToEndTests.The_dock_comes_back_with_its_terminal...`
  failed once and then passed 2 of 3 alone; it drives only `cmd:` steps,
  no keys, and was flaky in round one too.
- New tests: in `ChordStateMachineTests` the text-box table (74 keys: 26
  run, 48 stay, each against a binding without context and one of
  `filesView`), the pane's own boxes, the held key (Ctrl+Shift+P and
  Ctrl+B held run nothing, Ctrl+Tab repeats), the repeat list, Esc and
  Ctrl+Shift+P during a wait. End to end: in the find box a letter
  filters, F5 copies the cursor row, Ctrl+A selects the box's text and
  leaves the pane's selection, Ctrl+T and Ctrl+W open and close a tab;
  and the palette from a Markdown Preview in the other pane gives the
  keyboard back to the page on Esc and after "Toggle Sidebar". That test
  failed with the fix switched off (the keyboard went to `FilePane`).
- The live check's keys section, alone on the Debug build in Windows
  PowerShell 5.1 with real keys: 14 of 14 True. The Ukrainian layout was
  switched for the window only and switched back (04090409).
- `build/check-scripts.ps1`: the live check parses in PowerShell 7.6.6 and
  5.1.

### Side effect of the first real-key run

The first run of the section on its own had the right pane on
`C:\Users\Admin\Documents`: the live check sets the right pane in an
earlier section, and a Tab that did not switch panes put the find there.
F5 then copied the folder `Ableton` (69 files, 470 KB) from Documents into
the run's scratch folder
`%TEMP%\cabinetos-ui-test\keys-section-2\files\keys20-other`. Documents
was only read. The copy is still in `%TEMP%`: deleting files was outside
this run's rules. The step now shows the fixture's folder `sub` in the
right pane with Ctrl+Right and presses F5 only when the log says both
panes are the fixture's (0c0a8af); the second run was clean.

### Still open

- **P2** (keys inside a tool page) and **P4** (overlays stack) stay
  proposals. P4 is now easier to reach: Ctrl+K Ctrl+T works in every box,
  and opening the theme picker closes neither the palette, Quick Open nor
  a prompt, so from their boxes the picker opens over them (by reading).
  P4's one helper that closes the other overlays would cover it.
- No test opens the permissions review (P5): that needs a plugin that
  asks for a capability.

## Round three

The planning session's decision, under the creator's "move on as far as you
can": build P2 and P4, the two proposals round two left open. A coder (Sonnet
5.5) built them on 2026-10-01 in the afternoon, in its own worktree, with the
end-to-end suite run on this PC while the creator was away.

### What changed

| # | Now | Commit |
|---|---|---|
| P2 | A tool's page hands back the tab keys: `tab.next`, `tab.previous`, `tab.close`, `tab.new`, `tab.select` (Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W, Ctrl+T, Ctrl+1 to 9), under the user's own keys. A key from a page in a pane's tab changes that pane's tabs, and the keyboard goes to the tab that comes to the front. The terminal hands back Ctrl+Tab and Ctrl+Shift+Tab only | 109435f |
| P4 | Opening an overlay closes the other overlays first, through one step (`CloseOtherOverlays`), so at most one is open and Esc closes the one on screen | bb6fa2a |

The rules are written in [keybindings.md](../../keybindings.md) ("Tool pages
and the terminal", "One overlay at a time"), in [ui.md](../../ui.md) (the
palette, the prompts, the terminal's keys, the Tool Extensions' keys, the
snapshot steps `key:` and `focus:`) and in
[tool-extensions.md](../../tool-extensions.md) ("The window's keys").

### Decisions the plan did not cover

Each is "what, because, undo".

- **The terminal hands back only Ctrl+Tab and Ctrl+Shift+Tab.** The handout
  said Ctrl+W and Ctrl+T are not shell keys, so passing them would be safe.
  They are shell keys: Ctrl+W is readline's delete word and vim's window
  prefix, Ctrl+T is readline's transpose and the file picker of fzf, and
  xterm.js turns Ctrl+3 to Ctrl+8 into control codes. A shell user who deletes
  a word would close a tab. The handout's own rule, "the terminal must keep the
  keys a shell needs", decided it. Undo: add `tab.close`, `tab.new` and
  `tab.select` to `TerminalKeys.TerminalTabWays`. **The planning session
  should confirm this**, because it differs from the handout's words.
- **Tab (`view.focusOtherPane`) is not handed back, and no way to the other
  pane is.** The handout named "the way to the other pane" among the tab
  keys. It can mean two things: Tab (`view.focusOtherPane`), or
  `tab.moveToOtherPane` (a chord, and chords never pass back, because their
  first key may be the page's). The audit's own text says Tab and Esc stay the
  page's, because a page uses them for its links and fields (the agent's chat
  has a text box), and a Tab that left the page would break both. So neither
  was built. From a page the way to the pane is Ctrl+Tab, Ctrl+Shift+Tab,
  Ctrl+1 to 9, or Ctrl+W on the tool's tab. Undo, if Tab should leave a
  page: add `view.focusOtherPane` to `TerminalKeys.TabWays`.
- **Sidebar pages get the tab keys too.** The handout listed "a sidebar tool"
  among the pages. A tab key there changes the active pane's tabs, as in VS
  Code, where Ctrl+Tab works from a side bar. Undo: pass no `paneWays` for
  `_sidebarPageKeys` in `ApplyToolKeys`.
- **A key from a page in a pane's tab names that pane.** A Markdown Preview
  opens in the other pane, and Tab to it does not make that pane the active
  one (only a pane's list does). Without the pane, Ctrl+W in the preview would
  close a tab of the pane the user is not in. `PageKeyArguments` adds the pane
  (and Go to Tab's digit); `tab.next` and `tab.previous` take the `pane`
  argument like the other tab commands. Undo: leave the pane out in
  `PageKeyArguments`.
- **From the terminal, Ctrl+Tab moves the keyboard to the pane.** The terminal
  is not one of the pane's tabs, and a tab command gives the keyboard to the
  tab that comes to the front, as it does from a list. Undo: none needed.
- **A held key from a page runs once**, as in round two: the pages do not know
  the list of commands that repeat. So Ctrl+Tab held in a preview moves one
  tab; in a list it repeats. Undo: the same as in round two.
- **The snapshot step `key:` sends a key into a page that has the keyboard,
  through DevTools (`Input.dispatchKeyEvent`).** Before, the key went to the
  window's input window, which drops it as the page's, so a test could never
  reach the page's script (the first run showed it: no command ran). The page's
  script now runs as for a real key, and a key that closes the page (Ctrl+W)
  does not hold the step up. Undo: take out the branch in
  `PressKeysForSnapshotAsync`. The tests' `FinishAsync` also prints the
  window's last log lines when a step never ends.
- **The overlays are the palette, Quick Open, a prompt, the theme picker and
  the plugin list.** The drive list is a prompt (`PromptBox` without a box),
  so it is covered with the pattern box, the pinned folders and a plugin's
  question. Undo: remove the calls of `CloseOtherOverlays`.
- **A prompt closes the others through an event** (`PromptBox.Opening`), not
  at its five callers, so a sixth caller cannot forget it.
- **The permissions review stays as it is.** It is a dialog of the window (the
  router refuses every command but its own while it shows). It opens over the
  plugin list or the marketplace that asked for it, and by itself when a
  plugin newly waits for a review. That last one does not close what the user
  is doing, because a background event must not throw away a typed prompt.
  The palette still closes it.
- **The marketplace closes all the overlays when it opens.** It covers the
  panes, and it already closed the picker and the plugin list; a palette,
  Quick Open or a drive list under it would hold the keyboard without being
  seen. Undo: `CloseOtherOverlays(opening: null)` in `OpenMarket`.
- **Each overlay closes the way Esc closes it** (the keyboard goes to the pane
  first, then the new overlay takes it in the same turn). So the palette now
  closes Quick Open with the keyboard handed back (it did not before), and the
  picker and the plugin list close before the palette opens, not after.
  Nothing showed a difference; the new overlay takes the keyboard in the same
  turn. `keys.open` (Ctrl+K Ctrl+S) goes through the palette's opening step
  now. Undo: the old order is in the history of `TogglePalette`.
- **New log evidence.** A line "overlays closed for another" (the opening one
  and the closed ones), and the field `overlays` on the snapshot step's
  "keyboard focus" line. Both are cheap and only the tests read them.
- **`docs/PLAN.md` was not touched.** Its line about the keys audit still says
  P2 and P4 stay proposals. The desk's mirror card follows the file, so the
  planning session should change both together.

### Evidence

- `dotnet build CabinetOS.sln -warnaserror`: 0 warnings, 0 errors. The fast
  tests: 1135 passed, 58 end-to-end skipped, of 1193.
- The whole suite with the end-to-end tests, on this PC, with the release
  core, twice (before and after the rebase onto the live check commits): 1192
  of 1193 each time. The one failure is
  `ContextMenuEndToEndTests+Alone.Entering_the_edit_mode_over_100000_selected_rows_adds_no_slow_frame`:
  a generation 2 collection of 32 ms in the frame that opens the menu's edit
  mode. It drives no key. The same suite on main without these commits fails
  the same test (1176 of 1177), and it passes when run alone (3 of 3 on main,
  1 of 2 on this branch). It is flaky and was flaky in round one; it needs its
  own look.
- New tests. `OverlayRuleTests` (5): what an opening overlay closes, that
  it never closes itself, the order, and the marketplace's case. `TerminalTests`
  (5): the page's list with the tab keys, a rebound key, a way out that wins on
  the same keys whatever the order, the terminal's two keys, and the old list
  without the tab ways. End to end in `KeysEndToEndTests` (2):
  `A_tool_page_hands_back_the_tab_keys_and_the_keyboard_follows_the_front_tab`
  presses Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1, Ctrl+2, Ctrl+T, Ctrl+2 and Ctrl+W
  with the keyboard in a Markdown Preview in the other pane (the inactive
  one), and reads the other pane's row and the keyboard's place after each
  one; `Opening_an_overlay_closes_the_others_and_Esc_closes_the_one_on_screen`
  opens the theme picker over the palette, Quick Open and the drive list with
  Ctrl+K Ctrl+T, and the plugin list and the palette over each other, and
  checks that the first Esc leaves no overlay open and the keyboard in the pane.
- Each end-to-end test failed with its fix switched off. P4: the palette and
  the picker were open together (`Palette,ThemePicker`), and after the first
  Esc `ThemePicker` was still open, in all three places. P2: Ctrl+Tab sent into
  the page ran no command, and the keyboard stayed in the page.
- All of it ran on this PC. The Omen laptop was not used: the creator's rule
  changed during the run (they went away), and the coordinator asked for this
  PC. No real key or mouse event was sent on either machine: the `key:` step
  posts key messages to the window or, for a page, sends DevTools key events.
- Not tested: the terminal's Ctrl+Tab (the lists are unit tested; the
  end-to-end tests have no shell), a tab key from a page in the sidebar, and
  the path where Windows sends the keys to the window instead of the page
  (`HandleKeyForPage`; it uses the same arguments).

### Still open

- The two points to confirm: the terminal's Ctrl+W and Ctrl+T (above), and
  whether Tab should leave a tool's page.
- P2 changes the table "Where the keyboard can be" for the two rows of the
  pages (the tool page and the terminal), and P4 for the drive list and the
  picker. The table above is the audit's state of the morning and is not
  rewritten.
- The permissions review still has no test (P5).
