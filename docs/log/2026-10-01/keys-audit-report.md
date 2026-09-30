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
