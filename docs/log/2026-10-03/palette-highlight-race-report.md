# The command palette's COMException under load

Branch `worktree-agent-a182f2e3a435f9ed5`, coder on Opus 5.5, the night of
2026-10-03 (away mode: nothing here was decided by the creator while the work
ran). It closes the plan's open item "a COMException from the command
palette's list, a product fault that is open" ([PLAN.md](../../PLAN.md), the
2026-10-02 rounds of flaky tests).

## What was seen

Three full end-to-end runs on the Omen laptop logged the same ERROR line in the
window of `ShellEndToEndTests.Quick_Open_finds_in_the_repository...`. The test
always passed alone.

- `_io\test-runs\tests-2026-10-02-2117-rd-omen-laptop.txt` and `...-2136-...`
  (the third round of flaky tests), and `tests-2026-10-03-0243-...` (Phase 24).
- The line: "a background task failed and nobody observed it", a
  `COMException (0x800F1000)` "Element is already the child of another
  element" from `ItemsRepeater.GetOrCreateElement` in
  `CommandPalette.BringHighlightIntoView` (`CommandPalette.xaml.cs:140`),
  reached through `PaletteModel.SetHighlight`, `PaletteModel.SearchAsync` and
  `MainWindow.RefreshCommandsAsync(CoreEvent)`.
- A first round (2138100, 2026-10-02) saw the same exception through the
  palette's own search after a close and added the `!IsOpen` check to
  `SearchAsync`. Its comment blamed "its collapsed list". That was not the
  cause, as below.

## The cause

The palette asked WinUI's `ItemsRepeater` for a row while the repeater was not
in the window's live tree. The repeater then added a row it had recycled to its
children a second time. Each step below was checked in WinUI's own source
(microsoft/microsoft-ui-xaml, `main`, read on 2026-10-03):

1. **The palette starts collapsed** (`MainWindow.xaml`,
   `<views:CommandPalette x:Name="Palette" Visibility="Collapsed" />`). XAML
   does not measure a collapsed element (`CUIElement::MeasureInternal`: "If
   Layout is suspended i.e, element is collapsed"). So the palette's
   `ScrollViewer` (`ListScroller`) gets its template only at the first layout
   after the palette is shown.
2. **A ScrollViewer's content is not live before its template.**
   `CContentControl::EnterImpl` enters the content with `fIsLive = FALSE`. The
   content becomes live when the template's presenter adds it. Until the
   palette's first layout, the `ItemsRepeater` `List` and every row it makes
   are outside the live tree.
3. **`VisualTreeHelper.GetParent` answers null outside the live tree**
   (`VisualTreeHelper::GetParentStaticPrivate`: "Don't return the parent unless
   we're in the live tree").
4. **An answer clears the rows into the recycle pool, still as children.**
   `PaletteModel.SearchAsync` shows each answer with `Rows.Clear()` and
   `Rows.Add(...)`. The clear is a Reset: `ViewManager::OnItemsSourceChanged`
   gives every realized row back to the template's `RecyclePool`, which keeps
   it as a child of the repeater (its owner).
5. **The next `GetOrCreateElement` takes that row back and appends it.**
   `ViewManager::GetElementFromElementFactory` gets the same row from the pool
   (same owner, so the pool does not remove it from the children). Then it runs
   `if (GetParent(element) != repeater) children.Append(element)`. `GetParent`
   is null (step 3), so it appends a row that is already in the collection.
   `CDOCollection::PreAddToCollection` refuses it:
   `AG_E_MANAGED_ELEMENT_ASSOCIATED`, `E_NER_INVALID_OPERATION` (0x800F1000),
   "Element is already the child of another element".

So the fault needs the palette's first showing in a window. Before WinUI lays
the palette out, it needs a call of `BringHighlightIntoView` that makes a row,
then a clear of the rows, then a second call. In the Quick Open test, `>tab`
opens the palette for the first time. The opening searches for "", the typed
text searches for "tab", and the sidebar and workspace steps before it changed
the configuration, so a `config_changed` event refreshes the palette too. On a
busy laptop two of these answers land inside one frame; alone, a frame comes
between them. 2138100's case was the same: the palette opened and closed before
its first layout, so its list was not "collapsed" but never laid out.

## The unobserved task

The task of the stack was started in `MainWindow.OnCoreEvent` as
`_ = RefreshCommandsAsync(coreEvent)`. Nobody awaited it, so its exception came
out only when the garbage collector finalized the task: one line, "a background
task failed and nobody observed it", without the place, and nothing at all if
the window closed first. 2138100's stack started in the palette's timer,
`_ = _model?.SearchAsync(Input.Text)`.

`Diag.Observe(task, target, message, fields)` (new, in
`CabinetOS.Core/Diagnostics/Diag.cs`) logs such a failure at once, as an ERROR
line with the message (the place) and the exception with its stack. Every task
nobody awaited whose chain reaches `BringHighlightIntoView` goes through it now:

- `MainWindow.OnCoreEvent`: "refreshing the commands after a core event
  failed", with the event's type.
- `CommandPalette`: the timer's search ("the palette's search for the typed
  text failed") and Enter ("running the palette's highlighted command failed").
- `PaletteModel`: the opening's search, the command list read again when a hit
  is missing, and the two commits of recorded keys.

A failure there stays an ERROR line, so an end-to-end test still fails on it.

## The fix

Two parts, both in `CommandPalette.BringHighlightIntoView`.

1. **Ask the list for a row only when it is in the live tree** (53028e3).
   When `List.IsLoaded` is false, the call remembers the request and returns;
   the list's `Loaded` event brings the highlight into view then.
2. **Bring the row into view at its real size** (9393537). Part 1 alone
   stopped the exception, but the new end-to-end test then found the
   highlighted row (row 20) outside the list's visible part. At the list's
   first layout, row 20 is outside the rows the list has laid out:
   `GetOrCreateElement` makes it and measures it, but does not lay it out.
   `StartBringIntoView` takes its target from the render size
   (`UIElement::StartBringIntoViewWithOptionsImpl`), which is 0 x 0 for such a
   row, so the scroll aimed at a point and not at the row. The target is now
   the row's measured size when it has no render size yet. A laid-out row keeps
   its render size, as before. The same could happen before this work whenever
   the highlight jumped past the rows the list had laid out.

Why it is safe:

- `IsLoaded` is true when the element is live and its `Loaded` has been raised
  (`FrameworkElement::get_IsLoadedImpl`; `CEventManager::RaiseLoadedEvent`
  takes the element off the pending list before it raises the event, so
  `IsLoaded` is already true inside the handler). Once live, the list stays
  live while the palette collapses and shows again. After the first showing
  the check is one property read, and the old path runs.
- `Loaded` comes with the palette's first layout, the same layout that first
  draws the list. Nothing waits on a timer, so there is no delay a user can see.
- The highlight itself (`PaletteRow.IsHighlighted`, the row's visual state)
  never depended on this call; only the scroll did.
- No `try`/`catch` around the WinUI call: the call no longer happens in the
  state that throws.
- It also covers the window's close: the list leaves the tree, `IsLoaded` is
  false, and nothing is asked of it.

2138100's `!IsOpen` check in `SearchAsync` stays: a closed palette needs no
answer. Its comment now names the real cause.

## The proof

- **The fault made to happen on purpose.** A new snapshot step
  `palette-burst:<rows>` (`MainWindow.RunPaletteBurstStepAsync`) asks the core
  for the whole list while the palette is closed. Then, in one turn of the UI
  thread, it opens the palette for the first time, asks for a refresh that
  keeps the highlighted command, shows the answer twice (`PaletteModel.Show`,
  split out of `SearchAsync` for this) and moves the highlight 20 rows down.
  `palette-state:<label>` logs where the highlighted row lies in the list. The
  new end-to-end test
  `ShellEndToEndTests.The_palette_takes_two_answers_and_a_key_before_its_first_layout_and_keeps_the_highlight_in_view`
  runs both, then PageDown, and checks the highlight (20, then 28) and that its
  row lies in the list's visible part each time.
- **Before the fix** (6aee8ee), on the laptop: the test failed on two ERROR
  lines, both the same `COMException (0x800F1000)` "Element is already the
  child of another element" at `CommandPalette.xaml.cs:140` through
  `PaletteModel.Show`. One came from the burst itself. The other came from the
  refresh's real answer of the core, which also arrived before the first
  layout (`_io\test-runs\tests-2026-10-03-0341-rd-omen-laptop.txt`).
- **With part 1 only** (53028e3), on the laptop: no exception, but "the
  highlighted row is not in the list's visible part" with the highlight on 20
  (`tests-2026-10-03-0347-...`). This found part 2.
- **Part 2 reverted on the final tree** (f164a0f), on the laptop: the log line
  says where row 20 landed: `row_top` 392, `row_bottom` 428, `viewport` 392,
  `offset` 328 (`tests-2026-10-03-0437-...`). The row's top sits exactly on the
  visible part's bottom edge: the list scrolled just far enough to show a point
  at the row's top, which is what a 0 x 0 target asks for.
- **With both parts**, the full suite on the laptop, at a87653d (main merged
  in, this PC's release core copied there): 1427 tests, 1422 passed, 5 failed,
  none of them the palette's (`tests-2026-10-03-0423-...`). The new test and the
  Quick Open test passed, and no line of the output has `COMException` or
  "already the child". The 5: the three update tests, which fail on `main`
  too; the start test's first `hello` (74 ms against its 60 ms limit, a timing
  limit seen failing before); and
  `TabsEndToEndTests.Tabs_and_locks_of_both_panes...` ("the first window did
  not close" within 15 s; it does not open the palette, it was on the list of
  tests seen failing under load, and it passed alone on the laptop right after,
  `tests-2026-10-03-0434-...`).
- **An earlier full run** at 7cbf335 (`tests-2026-10-03-0413-...`) had 14
  failures because the laptop still had a release core older than 0.1.2
  (protocol 19 against 20, and Phase 23's commands missing). The new test and
  the Quick Open test passed there too, with no `COMException`. A release core
  built in this worktree went over for the run above.
- **The new unit test**
  `DiagnosticsTests+ObservedTasks.A_task_nobody_awaits_logs_its_failure_at_once_with_its_place_and_a_task_that_ends_well_logs_nothing`:
  a failing task gives one ERROR line with the place's message, its field, and
  the exception itself (not the aggregate) with the method that threw. A task
  that ends well and a finished one give none.
- **On this PC**, at 7cbf335 (main's 0.1.2 merged in; the later merge a87653d
  brought only `docs/PLAN.md`): `dotnet build
  CabinetOS.sln -warnaserror` exit 0 (0 warnings), `-c Release` exit 0
  (0 warnings), `dotnet test --solution CabinetOS.sln --no-build` exit 0:
  1427 tests, 1355 passed, 72 skipped (the opt-in end-to-end tests), 0 failed.
  `cargo build --workspace` exit 0 (the core is unchanged).

## Decided without the creator

1. **The reproduction is an end-to-end test, not a fast one.** The handout
   expected the fast tests to build the palette's view model. They cannot:
   `CabinetOS.Tests` references only `CabinetOS.Core`, and `PaletteModel` and
   `CommandPalette` live in the app project. The handout allowed a test where
   one already drives the real control, and the Quick Open test does. Undo:
   remove the test and the two steps.
2. **`PaletteModel.Show` is `internal` and split out of `SearchAsync`**, so the
   step can show two answers without the core's timing. `SearchAsync` does what
   it did. Undo: inline it again.
3. **`Diag.Observe` logs at ERROR level** and covers every task nobody awaited
   on the palette's path to `BringHighlightIntoView`, not only the one task of
   the stack. Other `_ =` tasks of the window are unchanged. Undo: put `_ =`
   back at the seven places.
4. **The `palette-burst` step logs its own COMException as an ERROR** and goes
   on, so a return of the fault fails the test with the stack instead of a
   90-second wait for the last snapshot. It is the snapshot aid's only catch.
5. **The measured size as the scroll target** (part 2) changes the scroll of
   every highlight move whose row is not laid out yet, not only the deferred
   one. For a laid-out row nothing changes. Undo: 9393537.
6. **Part 2 was committed before the run that measured the row's place.**
   That run's first try failed to build (`CS2012`, a file lock from another
   run), and the laptop was then busy with other sessions for 20 minutes. The
   measurement came afterwards, from a local branch `palette-diag-no-target-rect`
   (the final tree with part 2 reverted, f164a0f, not pushed, not for merging;
   it can be deleted).

## What was tried and left

- **Applying the ScrollViewer's template early** (`ListScroller.ApplyTemplate()`
  once the window is loaded) would make the list live from the start. Left: it
  expands the template at every start for a palette many sessions never open
  (Article 1, the start time), and the palette would still depend on WinUI's
  order without saying so.
- **Scrolling by offset** (row index times row height) instead of
  `GetOrCreateElement`. Left: it replaces WinUI's own bring-into-view, the row
  height comes from the metrics, and any later `GetOrCreateElement` would meet
  the same state.
- **A `try`/`catch` around the call**: not a fix, and the handout forbids it.

## Not done, and why

- The merge commit a87653d (main's plan commit, `64875ea`) has git's default
  message, without the `Co-Authored-By` line. It was pushed before I saw it,
  and changing it would need a force-push.

- `docs/PLAN.md`'s open item is not edited: the plan belongs to the planning
  session, and a change to it needs the desk card refreshed. The item can close
  with this branch.
- Nothing ran on this PC with a window: the creator's own CabinetOS runs here
  and the consent file says held.
