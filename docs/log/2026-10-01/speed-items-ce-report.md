# Speed items C and E: the coder's report

Proposals C and E of the [speed review](speed-review.md), which the creator
chose on 2026-10-01 with the other five (A, B, D, F and G went to a second
coder in parallel). A coder on Opus built both in its own worktree.
C: the marketplace makes its cards in parts. E: a pane keeps the listing
of the tab that went behind last, so a switch back does not list the
folder again. Both are on main, with their tests, docs and numbers.

## Commits on main

| Commit | What |
|---|---|
| 1dca09d | C: `CardSlices` in `CabinetOS.Core` and the marketplace view: the cards that fill the view at once, the rest a screenful per dispatcher turn at low priority, a generation number that stops the slices of an older set; "marketplace cards complete"; the snapshot step `market:<label>`; `CardSlicesTests`, `MarketplaceCardsEndToEndTests`; docs/ui.md, CHANGELOG |
| 5f55479 | E: `ParkedListing` in `CabinetOS.Core`, `PaneModel` and `MainWindow.Tabs.cs`: the kept listing, taken back by its tab, released by time, events, its tab or a mismatch; `kept` on "listing shown"; `CABINETOS_UI_PARKED_LISTING_MS`; `ParkedListingTests`, `KeptListingEndToEndTests`; the live check's real-key step in section 12; docs/ui.md, CHANGELOG |
| 3653c2f | C, after the paired runs: cards that would come before the view's own first layout wait for the frame after it; the next set takes a laid-out card's height; no "complete" line for a replaced set; `make_ms` on both card lines; the end-to-end test of a tab during the slices made independent of when the index arrives |
| 10a0410 | The speed runner reads the cards' last slice (`cards_complete_ms`, `slices`, `make_ms`, frames up to it) and whether each tab switch was kept |
| (this report) | This file and its row in the folder's README |

Each push followed `git fetch` and `git rebase origin/main`; nothing was
forced. One rebase met the other coder's CHANGELOG lines; both sets of
lines are kept.

## C. The marketplace's cards in parts

**What changed.** `MarketplaceView.RenderCards` made every card in the
frame after the index arrived. Now a new set of items (the index read, a
tab, a search) makes at once only the cards that fill the view: the grid's
columns (`CardSlices.Columns`, the rule `CardGrid` uses too) times the
rows down to the view's bottom, with the height of a laid-out card, else
the design's card (113 px and twice the theme's card padding). The rest
come a screenful per turn of the dispatcher at low priority. A newer set
makes the older set's next slice empty (a generation number), so a tab or
a search during the slices leaves only its own cards. The cards are made
in the items' order, which is the order Tab follows. A card is reused while
its item is the same object, so a tab that brings items back does not
build their cards again. A new set starts at the top of the grid; the index
read again (the same IDs in the same order) keeps the grid where it was
scrolled and makes the cards down to there at once. When the index comes
before the view's own first layout, the cards wait for the frame after it.

"marketplace cards shown" keeps its meaning (the first frame with the
first cards on screen); it now has `cards` (made at once), `total` and
`make_ms`. The new "marketplace cards complete" has `cards`, `slices`,
`make_ms` and `ms` (since the opening, or since the set started).

**Numbers** (`speed-review.ps1`, scenario `market`: `cmd:marketplace.browse`
on the local index of 50 items in a 1400 × 900 window; the main
checkout's Release build of 03:14 as "before" and this worktree's Release
build as "after", both on the same release core; the builds in turn run by
run; median [lowest to highest]):

| 7 paired runs each (build 3653c2f) | Before | After |
|---|---|---|
| Cards made at once | 50 | 15 |
| The opening's longest frame (frame gap) | 115.6 ms [98.3 to 128] | 66.4 ms [53.6 to 80.8] |
| WinUI's own work in that frame | 100 ms [79.3 to 102.9] | 65.6 ms [53.4 to 68.6] |
| First cards on screen, after the command | 104 ms [88 to 113] | 105 ms [98 to 125] |
| Making the first 15 cards (`make_ms`) | | 14.7 ms [12 to 16.4] |
| The cards' own frames | the 98 to 128 ms frame | at most one of 33.9 to 40.5 ms in 5 runs, none over 33 ms in 2 |
| All 50 cards in (4 slices) | at once | 184 ms [160 to 221] after the command; 31.8 ms [24.7 to 33.3] of making in all |

An earlier set of 5 paired runs on build 1dca09d (before the layout wait)
gave a longest frame of 114.9 ms [103.5 to 121.5] before and 83.4 ms [57.8
to 104.3] after. It showed two kinds of opening: when the index arrived
after the view's first layout, 15 cards; when it arrived before, the view
had no size, the window's size stood in, 28 cards were made and shared
one frame with the view's first layout. 3653c2f makes every opening the
first kind.

**The goal of about 20 ms is not reached, and why.** The cards are no
longer the long frame. The 66 ms frame that is left comes before any card
exists: it is the marketplace view's own first layout (its nav, its search
box, its toolbar and buttons, made and laid out for the first time). A
second opening in the same window has no frame over 33 ms at all (checked
once with the after build: open, close, open again, frame statistics on).
So what is left is first-time work of WinUI, which the cards cannot
change. A way to take it out of the opening is proposed below.

**Tests.** `CardSlicesTests` (window core, no window): an index of 120
items in a `MarketplaceModel` makes the first screenful at once and all
120 after nine more slices, in the index's order; the Themes tab during
the slices stops Discover's slices and leaves the 40 themes only; the same
items are the same set, new objects are not, and the same IDs in order
are the index read again; a screen's columns times rows. 
`MarketplaceCardsEndToEndTests` (`CABINETOS_UI_E2E=1`, a real window on a
local index of 120 items the test writes): the first screenful is logged
(4 to 119 cards of 120), then all 120 in the index's order, and Tab from
the cards of items 11, 47 and 118 goes to the next item's card; Discover
and Themes clicked in one dispatcher turn leave the 40 themes, in order,
and no set of 120 is completed after that.

## E. A pane keeps the listing of the tab that went behind last

**What changed.** When a tab goes behind (`ShowFrontTabAsync`), the pane
no longer closes its listing: `PaneModel.RestoreAsync` gets the tab that
leaves, and the listing it showed goes into a slot (`ParkedListing`, one
per pane) with its folder, its order, its listing ID and section, and its
cursor, anchor and marks. The core keeps watching the folder. When that
tab comes to the front again and its folder and order still match, the
pane takes the listing back and only binds the rows again (`ShowKept`):
no `list_directory`, no new section, no new type names (the rows ask
`describe_entries` again for what is on screen, and the known extensions
fill in meanwhile). The tab's scroll position, find text and quick search
work as before.

The kept listing is let go, and closed in the core (so the core's log has
its "listing closed"), when its 30 s are up (a constant with its reason,
`ParkedListing.DefaultLifetimeMs`), when another tab goes behind in its
place, when the core says its folder changed (`listing_refreshed`) or is
gone (`listing_lost`), when its tab is closed or moved to the other pane,
and when it no longer fits its tab (another folder or order, as after a
change of `panes.sort`). The tab then lists its folder as before. A
restart of the core forgets it without a close, as the pane's own listing
is forgotten. A tab in the column view keeps nothing and is restored as
before. `ui.tabs`, `window_state` and the shell state's `pane0_listings`
(the listings a pane shows) are unchanged.

**Numbers** (`speed-review.ps1`, scenario `tabs`: a tab on 100,000 files and
one on 10,000 folders, `tab:next` four times; 5 paired runs each on build
5f55479, so 10 switches to each folder per build):

| Switch to | Tab shown, before → after | Listing's first frame, before → after | `list_directory` per switch | Worst frame in the second after |
|---|---|---|---|---|
| 100,000 files | 76.5 ms [69 to 242] → 6 ms [5 to 8] | 86.9 ms [78.7 to 256.1] → 17 ms [13.9 to 20.5] | 1 → 0 | 27.6 ms [18.8 to 42.9] → 27.3 ms [18.3 to 34.8] |
| 10,000 folders | 12.5 ms [11 to 103] → 5 ms [4 to 6] | 23.1 ms [19.8 to 114.9] → 14.6 ms [13.3 to 19.4] | 1 → 0 | 26.2 ms [18.3 to 42.9] → 27 ms [18.3 to 30.4] |

The before numbers are higher than the speed review's (51 to 60 ms to the
tab) because the machine was busier: other coders' builds and tests ran,
and the CPU before a run was 16 to 52 %. The pairs saw the same load.

**Tests.** `ParkedListingTests` (window core, no window, real listing
sections and a fake core): only the tab that went behind takes its
listing back, with its cursor and marks, and nothing is closed; a second
tab behind lets the first go and closes it; the listing goes at 30,000 ms
and not at 29,999; the test hook's value is read only when it is a
positive number; `listing_refreshed` and `listing_lost` for it let it go,
for another listing not; a changed order or folder lets it go (case and a
closing backslash do not count as another folder); a closed or moved tab
lets it go; a restart of the core forgets it without a close.
`KeptListingEndToEndTests` (`CABINETOS_UI_E2E=1`, a real window and core,
the lifetime set to 3 s): four switches between two tabs send no
`list_directory` and take back one, two, one, two; the first tab comes
back with its cursor on file-07.txt; the core lists two once and one three
times in all (before: five and three); after 4.5 s the kept listing is
released ("its time was up") and the core logs its "listing closed"; the
next switch lists the folder again with `kept: false`. The existing tab
tests (`TabsEndToEndTests`, `ShellEndToEndTests` with its first tab's
cursor and scroll position, `ColumnViewEndToEndTests`,
`FolderSizesEndToEndTests`) pass.

The live check's section 12 has a new step with real keys, which I did
not run: after the three tabs, Ctrl+Shift+Tab and Ctrl+Tab take two kept
listings back and send no `list_directory`. It adds what the end-to-end
test cannot: the keys go through the window's real key handling.

## Checks

- Window: `dotnet.exe build CabinetOS.sln -warnaserror`, 0 warnings and 0
  errors, after the last rebase. The fast run: 1173 tests, 1118 passed,
  55 skipped (the end-to-end ones and those that need a core), 0 failed.
- The full run with `CABINETOS_UI_E2E=1` and a release core built from
  main at 10a0410 (core last changed in 1249ce0): 1173 tests, 1172
  passed, 1 failed, 0 skipped, in 2 min 8 s, while the other coder's
  speed runs used the machine. The failure was
  `RailEndToEndTests.The_tree_has_rows_on_the_screen_when_the_window_starts_in_the_rail_layout_before_any_key`
  ("the tree has its rows in the model and none on the screen"); its class
  run again alone passed 5 of 5. It concerns the sidebar's tree at start,
  which neither item touches; it may be a new flaky test on a busy
  machine.
- `build/check-scripts.ps1`: every script parses in PowerShell 7.6 and in
  Windows PowerShell 5.1.
- The core was not changed, so its five checks were not run.

## Decisions made alone

Each as what — because — undo.

1. C with slices on the dispatcher, not an `ItemsRepeater` with a uniform
   grid — the slices keep `CardGrid`'s look (each row as tall as its
   tallest card) and every card stays in the tree, so Tab walks through
   all of them; a repeater makes only the cards near the view — revert
   1dca09d and 3653c2f.
2. The first slice is the grid's columns times the rows down to the view's
   bottom, the row cut at the bottom included, and every later slice one
   screenful — the plan said "enough to fill the view" and "about one
   screenful per slice" — `MarketplaceView.CardsPerScreen` and
   `CardSlices.PerScreen`.
3. A card's height comes from a laid-out card, else 113 px plus twice
   `marketplaceCardPaddingY` — the card's parts are fixed sizes, and the
   number only decides how many cards are made at once — the constant in
   `MarketplaceView.CardHeight`.
4. Cards that would come before the view's own first layout wait for the
   frame after it (at most 1 s) — without it, 28 cards were made from the
   window's size and shared a frame with the view's first layout (55 to
   62 ms of work); the first cards may come a frame later — remove
   `WaitForLayout`.
5. A new set starts at the top of the grid; the index read again keeps
   the scroll position and makes the cards down to it at once — with
   slices the grid is only one screen tall at first, so the scroll
   position would otherwise jump on its own — `MarketplaceView.StartCards`.
6. "marketplace cards shown" now counts the cards made at once (it counted
   all) and has `total` and `make_ms`; "marketplace cards complete" is
   new — the plan kept "shown" as "the first cards are on screen" and
   asked for the second line — `LogAtFrame` calls in `MarketplaceView`.
7. The snapshot step `market:<label>` was added — the end-to-end test needs
   the grid's order, which is the keyboard's — the case in
   `MainWindow.xaml.cs` and the line in docs/ui.md.
8. The plan pointed at marketplace view tests in `ui/CabinetOS.Tests`;
   there were none (the test project cannot load WinUI; the marketplace's
   tests were of the model, and its end-to-end test used no window). The
   rules went into `CardSlices` with unit tests, and the view got an
   end-to-end test with a 120-item index the test writes — nothing to undo.
9. E: a `listing_refreshed` for a kept listing releases it (the tab lists
   again when it comes back) instead of applying the refresh — the
   existing refresh code works on the pane's own listing and selection,
   releasing is never wrong, and only a folder that changes while its tab
   is behind loses the gain — apply the refresh to the kept listing as
   `ColumnListing.ApplyRefresh` does.
10. "Any other navigation of the parked tab's folder" read as: the tab
    comes back when its folder or order no longer fit the kept listing,
    or its tab is closed or moved; the listing is then released. A third
    tab coming to the front leaves the kept listing in place until another
    tab goes behind — `ParkedListing.TakeBack` and `ReleaseUnlessAmong`.
11. The cursor, anchor and marks come back by index from the kept listing,
    not by name — it is the same listing, so the indexes are exact and no
    names are searched — `PaneModel.ShowKept`.
12. The type names of the rows on screen are asked again after a take-back
    (`describe_entries` for one page, a few milliseconds in the core, off
    the UI thread) instead of keeping the pane's details cache with the listing —
    a second slot for little gain, and the known extensions fill in at
    once — keep the `EntryDetailsCache` in `ParkedListing.Entry`.
13. The 30 s timer is a `Task.Delay` on the UI thread's context, owned by
    the `PaneModel` and cancelled by the next kept listing, not a
    `DispatcherQueueTimer` of the window — the slot moves with its pane
    model when the panes are swapped (Ctrl+U) — `PaneModel.ExpireKeptAsync`.
14. The test hook is an environment variable, `CABINETOS_UI_PARKED_LISTING_MS`,
    documented in docs/ui.md's table — the end-to-end tests start the
    window as a process and pass everything that way — remove it from
    `PaneModel`'s constructor.
15. A listing taken back logs "listing shown" (with `core_us` 0, `reply_ms`
    0 and the new field `kept: true`) — the speed runner and the live
    checks time tab switches by that line — `NavigationTiming.Kept` and
    the field in `FilePane`.
16. The window's `listing_lost` handler now hands the event to both panes,
    and each checks its own and its kept listing — the kept listing is not
    the pane's `ListingId` — the loop in `MainWindow.OnCoreEvent`.
17. The live check step was added to section 12 — real keys show that
    Ctrl+Tab and Ctrl+Shift+Tab reach the kept listing through the window's
    key handling, which the snapshot steps skip; I did not run it, as
    instructed — remove the step.
18. Measured on my own runner root, `%TEMP%\cabinetos-speed-ce`, with its
    own fixtures — the runner empties `<Root>\work` before each run, and
    the other coder's runner used the default root at the same time — the
    folder is removed at the end; `results.json` files are kept (below).
19. The before and after builds were copied aside and both ran on the
    main checkout's release core of 03:10, copied aside too — neither item
    touches the core, so the window is the only difference, and the copies
    keep the main checkout's build free for others — rerun with other
    `-Variants`.
20. The runner was extended (10a0410) to read the new lines — the
    plan asked for its numbers before and after, and the old analysis
    stopped at the first cards — revert 10a0410.
21. The commit trailer names Claude Opus 5.5, not the line the handout
    gave — the session's rule for the trailer names the model that wrote
    the commits, and only the creator's own instructions change it —
    nothing to undo in the pushed commits.

## What is not done

- **The first marketplace view's frozen frame of about 20 ms.** The
  cards' frames are at about 35 ms or less, but the view's own first
  layout is one frame of 54 to 81 ms before any card. A proposal: lay the
  marketplace view out once while the window is idle after start, hidden,
  as a62fb98 prepares the context menus; the measurement above says a
  second opening has no frame over 33 ms. It costs that frame of work at
  idle and the view's memory from the start, which is the creator's
  choice (Article 1 against Article 10).
- The live check was not run (the planning session runs it).
- docs/PLAN.md and the desk were not touched: the planning session keeps them.

## Results kept

The paired runs' `results.json` files stay in
`%TEMP%\cabinetos-speed-ce-results` (the logs, fixtures and build copies
were deleted): `paired` (C on 1dca09d and E on 5f55479, 5 runs each),
`paired-market2` (C on 3653c2f, 7 runs each), `runner-check` (one run of
the extended runner).

## Noticed, out of scope

- The full run's one failure, the rail tree at start (above), passed
  alone; if it fails again on a busy machine it is worth a look of its own.
- `MarketplaceView.Render` calls `model.Items` several times per change
  (the cards, the caption, each tab's count), and each call filters the
  whole index again; an install's 30 progress events a second do that
  each time. Cheap at 50 or 120 items, but it grows with the collection.

## Follow-up: the marketplace view prepared while the window is idle

The planning session chose the proposal under "What is not done" above
(the creator's "do as you recommend"): lay the marketplace view out once,
hidden, while the window is idle after start, so the first opening of the
marketplace has no frame over 33 ms. The same coder built it in the same
worktree.

### Commits on main

| Commit | What |
|---|---|
| cd3e597 | `MarketplaceView.PrepareLayout`: the nav, the toolbar, the notice, the cards' scroller and one sample card made and laid out in one dispatcher turn, the view hidden again before the next frame; "marketplace view prepared" with `ms`; run in the menu shapes' idle slot, one low-priority turn after the last shape, after a quiet second (`InputQuiet` in `CabinetOS.Core`, fed by the window's keys and pointer); an opening counts its cards only after its own first layout; `InputQuietTests`, a new test in `MarketplaceCardsEndToEndTests` |
| 80bb907 | docs/ui.md ("The view prepared ahead", the log line, the tests) and a CHANGELOG line |
| (this section) | This follow-up |

Each push followed `git fetch` and `git rebase origin/main`; nothing was
forced.

### What changed

- **When.** The context menu shapes are built 0.75 s after the first
  folders are shown, one per low-priority dispatcher turn (a62fb98). The
  chain now has a fifth turn: the marketplace view. So it never comes
  before the first listing is on screen. In the 7 measured runs it came
  20 to 21 ms after the last shape, and 3.5 s before the speed runner's
  command.
- **Only when the window is idle.** The window notes every key
  (`PreviewKeyDown`), pointer press, pointer move and wheel over its root,
  handled or not. The preparation runs only after one second without any
  of them; input in that second puts it off until a quiet second follows,
  and the check is made again in a low-priority turn. So it does not take
  a frame from someone who scrolls, drags a scroll bar or types.
- **What is prepared.** The view is made visible, rendered from its model
  (no index yet: the nav, the counts at zero, the toolbar), the notice and
  the cards' scroller are both made visible, one sample card is added, and
  `UpdateLayout` lays it all out in the same turn. Then the sample card is
  removed and the view is collapsed again. No frame comes in between, so
  nothing of it reaches the screen. The sample card names no real
  extension and has every part of a card (the verified check, the rating,
  the installs), so the card's template and texts are made once.
- **No index, no index card.** The core reads the index only at the first
  look (trust rule 6). So when the view is prepared, the index has never
  arrived, and only the view's frame is prepared: the brief's "the frame
  only" case is the only case. The cards of the first opening come as
  before: the first screenful at once, the rest in slices.
- **Log.** "marketplace view prepared" (`cabinetos_ui::market`, info) with
  `ms`, the time of the preparation's turn: 19.9 to 21.7 ms in the Release
  build, about 43 to 64 ms in the Debug build. "marketplace cards shown"
  and "marketplace cards complete" are unchanged.
- **Sizes after the preparation.** The hidden view kept the sizes of its
  layout. One trial opening counted its first screenful from those and made
  42 cards instead of 15. An opening now counts its first cards only after
  its own first layout (`LayoutUpdated` after `Open`), not when the card
  area has any width.

### Numbers

`speed-review.ps1`, scenario `market` (`cmd:marketplace.browse` on the local
index of 50 items in a 1400 × 900 window). "Before" is the Release build of
main at 68c999f (the code of origin/main at 96390b6, whose only newer commit
is a report), "after" is this worktree's Release build with the
preparation. Both ran on the same release core (built from 1249ce0), the
builds in turn run by run, 7 runs each, none shared with another CabinetOS
window. Median and range:

| 7 paired runs each | Before (68c999f) | After |
|---|---|---|
| **The opening's longest frame (frame gap)** | **51.6 ms (44.6 to 63.6)** | **42.4 ms (37.8 to 47.7)** |
| WinUI's own work in the slowest frame | 54.5 ms (8.3 to 66.9) | 43.1 ms (5.6 to 47.1) |
| Frames over 33 ms in the opening | 2 (2 to 3) | 2 (1 to 2) |
| The command to "marketplace shown" | 12 ms (10 to 13) | 8 ms (7 to 10) |
| The command to the first cards on screen | 88 ms (70 to 104) | 74 ms (59 to 81) |
| Making the first 15 cards (`make_ms`) | 12 ms (10.5 to 15) | 8.1 ms (6.4 to 9.7) |
| All 50 cards in, after the opening | 146.6 ms (121.6 to 188.5) | 128.3 ms (121.4 to 157.7) |
| The preparation at idle | none | 20.4 ms (19.9 to 21.7), no slow frame |

The opening's frames over 33 ms, each run, as gap / WinUI's work:

- Before: 35.4/0, 44.6/8.3, 37.3/0 · 41.2/0, 51.6/49.9 · 50.8/0, 63.6/66.9,
  44.2/3.3 · 44.4/0, 48.5/53.3 · 46/0, 58.4/61 · 40.9/0, 57.4/60.4, 43.7/3.2
  · 42.1/0, 48.3/54.5.
- After: 36/0, 39/40.2 · 39.3/0, 40.4/43.1 · 41.8/0, 46.3/47.1 · 47.7/0,
  43.1/44.8 · 34.1/0, 42.7/43.7 · 36/0, 37.8/5.6 · 42.4/41.5.

The "before" here is lower than the 66.4 ms in section C above. Main now
also holds the other coder's items A, B, D, F and G, and the machine was
quieter; the pairs are what compare.

**The goal, no frame over 33 ms at the first opening, is not reached.**
The longest frame is about 9 ms shorter, the first cards come about 14 ms
sooner, and the opening's synchronous part is a third shorter. One or two
frames of 34 to 48 ms stay in every run, in both builds:

1. A frame gap of 34 to 48 ms for which WinUI reports no frame work: the
   window's own turns of the opening (the command, what floats over the
   panes closed, the panes hidden, the view shown, focused and laid out,
   the index's reply read into the model).
2. The next frame, with 38 to 47 ms of WinUI's own work: the first 15
   cards made, laid out and drawn, and the view drawn for the first time.
   "marketplace cards shown" is logged right after it.

A second opening in one window has no frame over 33 ms (section C). What a
second opening has and the prepared first one does not: the 15 cards are
made and drawn already, and the view was drawn once.

### What was tried and dropped

Three unpaired runs each, after build, the opening's longest frame:

| Variant | Runs |
|---|---|
| The view laid out ahead, no sample card | about 55 ms (and once 42 cards, the stale sizes above) |
| Laid out ahead with the sample card (kept) | 36.1, 39.5, 41.8 ms |
| Also drawn ahead: visible at opacity 0 for one frame, then collapsed | 42.6, 39.8, 39.2 ms |
| Also drawn ahead: rendered into a `RenderTargetBitmap` that is thrown away | 35.2, 45, 42.7 ms |

Drawing the view ahead gave no measurable gain over laying it out, and
both ways keep the view in the tree for one frame or more. So the kept
version lays out and draws nothing.

### Checks

- `dotnet.exe build CabinetOS.sln -warnaserror` after the rebase: 0
  warnings, 0 errors. The fast run: 1177 tests, 1137 passed, 40 skipped,
  0 failed.
- `MarketplaceCardsEndToEndTests` with `CABINETOS_UI_E2E=1` and the release
  core: 3 of 3 passed.
- Full runs with `CABINETOS_UI_E2E=1` (1177 tests, about 2 min 7 s
  each): 10 with the preparation and, as a control, 5 with its one call
  commented out. Every run had failures, all in tests that wait a fixed
  time on a machine that runs many windows at once:

  | Test that failed | Preparation on (10 runs) | Off (5 runs) |
  |---|---|---|
  | `RailEndToEndTests`, the tree's rows at start | 10 | 5 |
  | `ShellEndToEndTests`, Quick Open (no rows 500 ms after typing) | 3 | 1 |
  | `ContextMenuEndToEndTests`, a menu asked for while it closes | 2 | 0 |
  | `ContextMenuEndToEndTests+Alone`, the edit mode over 100,000 rows (a 33.4 ms garbage-collection pause) | 2 | 0 |
  | `CompactOverlayEndToEndTests`, the dock comes back | 1 | 0 |
  | The new prepared-view test (expected without the preparation) | 0 | 5 |

  The rail test passed alone 5 of 5, twice. The Quick Open failure of
  the control run had no preparation in its log. The menu test (the 19f
  report records the same failure in a full run before this change) was
  checked against the preparation, because both run in the same idle
  chain: alone it passed 4 of 4, its class alone 6 of 6 twice, and its
  steps in six windows at once kept every menu on screen. With a scratch
  delay (not committed) the preparation was moved into the test's menu
  steps at 14 moments, from the first menu's opening to just after the
  reopen: every menu stayed on screen. The preparation's low-priority
  turn never got between the close and the reopen; it came about 60 ms
  after the second menu opened. The last 5 runs with the preparation had
  no menu or Quick Open failure. The builds of those runs differed from
  the commit only by the log copies in two tests and the inactive scratch
  hook. So I found no failure that the preparation causes, but 10 and 5
  runs cannot rule out that it shifts the timing of these tests on a
  busy machine.
- The core was not changed; its five checks were not run.

### Decisions made alone

Each as what — because — undo.

1. One second without a key, pointer press, pointer move or wheel over the
   window's root, handled events included — because the menu shapes have
   no input check of their own to copy, and the brief asks that the
   preparation not take a frame from someone scrolling or typing; a move
   counts because dragging a scroll bar is moves; one second covers the
   pause between typed keys — undo: drop the `_quiet` check in
   `PrepareMarketplaceWhenQuiet`, or change `InputQuiet.DefaultQuietMs`.
2. Input inside a tool's web view (WebView2) is not watched — because it
   never reaches the window's XAML tree; the worst case is one 20 ms turn,
   which fits in one frame — undo: feed `InputQuiet` from the tool host
   too.
3. The preparation is the fifth turn of the menu-shape chain, not a timer
   of its own — because the brief says the same slot after the menu shapes,
   and the chain starts only after the first folders are on screen — undo:
   remove the `kinds.Count == 0` branch in `PrepareContextMenusSoonAsync`.
4. "No cards" is read as no cards of the index; one made-up sample card is
   laid out and removed in the same turn — because it took the opening's
   longest frame from about 55 to about 40 ms in the trials, and it shows
   nothing and installs nothing — undo: remove the sample card's four lines
   in `PrepareLayout` and `SampleItem`.
5. Laid out, not drawn — because the two ways of drawing ahead gave no
   measurable gain (table above) — undo: none needed.
6. An opening counts its first cards after its own first layout — because
   the sizes kept from the hidden layout can be another window size's —
   undo: go back to `CardArea.ActualWidth <= 0`, which is wrong once the
   view was prepared.
7. The line "marketplace view prepared" comes from the view, under
   `cabinetos_ui::market` like the other marketplace lines, at info level —
   because it is one line per run — undo: none needed.
8. The end-to-end test waits 2.5 s before the command and asserts the
   order — because in the normal run the preparation comes about 0.8 s
   after the first folders; a real mouse moved over the test's window puts
   it off by a second each time, so the test can fail while someone moves
   the mouse over it — undo: a longer wait in the test.
9. The "before" build is main's Release build at 68c999f, copied aside,
   both builds on the same core copy — because the brief says to pair
   against the current main build, and the newer origin/main commit
   changes only a report — undo: none needed.
10. Nothing beyond the preparation was changed to reach 33 ms — because the
    frames left are the opening's own work and the first cards' first
    drawing, and changing them means changing C's rule of a screenful at
    once or the opening's order, which the brief did not ask — undo: none
    needed.

### What is left, as proposals

- **A smaller first slice.** The second remaining frame is the first 15
  cards' first layout and drawing. A first slice of one row (5 cards in a
  1400 × 900 window), with the rest in the slices as now, would make that
  frame smaller. It changes C's rule that the first screenful comes at
  once: the rows below would come one or two frames later. Not measured.
- **The opening in two turns.** The first remaining frame is the window's
  own turn of the opening. Reading the index's reply into the model in a
  turn of its own, after the view's first frame, could split it. Not
  measured.

### Results kept

The paired runs' `results.json` stays in
`%TEMP%\cabinetos-speed-ce-results\paired-prepared` (the logs, the build
copies and the scratch folders were deleted).
