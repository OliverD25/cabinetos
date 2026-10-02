using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI.Xaml;

namespace CabinetOS;

// Find in Pane (Phase 16; the creator's SHELL_REDESIGN.md §3; docs/ui.md, "Find in pane"): Ctrl+F opens the find widget of
// the active pane's tab. It filters that pane's list by name as the text is typed (the core matches, PaneFind asks); the
// other pane is not touched. Enter puts the cursor on the first match and keeps the widget; Esc closes it and every row
// shows again, the marks as they were. Another folder closes it; a tab keeps its text (PaneTab.FindQuery).
public sealed partial class MainWindow
{
    private const string FindTarget = "cabinetos_ui::find";

    private FindWidget[] _findViews = null!;

    // Questions to the core about a find's text that have not been answered, by pane: the snapshot aid's find: step waits for none.
    private readonly int[] _findAsking = new int[2];

    private void SetUpFind()
    {
        _findViews = [LeftFind, RightFind];
        for (var i = 0; i < _findViews.Length; i++)
        {
            var pane = i;
            var view = _findViews[pane];
            view.QueryChanged += text => _ = FindAsync(pane, text);
            view.FirstMatchRequested += () => SelectFirstMatch(pane);
            view.ListRequested += () => _paneViews[pane].Focus(FocusState.Keyboard);
            view.CloseRequested += () => CloseFind(pane, focusPane: true);
            _panes[pane].PropertyChanged += (_, e) =>
            {
                // The count says where the cursor is among the matches: it follows the selection too.
                if (e.PropertyName is nameof(PaneModel.Find) or nameof(PaneModel.Selection))
                {
                    UpdateFind(pane);
                }
            };
        }
    }

    // search.focus acts on the pane it names (the path row's filter label), else on the active one; the toolbar's Find
    // button toggles its pane's find (a command of the window's own, as its other buttons' are).
    private void RegisterFindCommands()
    {
        _router.RegisterUiHandler("search.focus", invocation => OpenFind(PaneOf(invocation)));
        _router.RegisterLocal("search.toggle", invocation =>
        {
            var pane = PaneOf(invocation);
            if (_panes[pane].Find.IsOpen && !_editorViews[pane].IsOpen)
            {
                CloseFind(pane, focusPane: true);
            }
            else
            {
                OpenFind(pane);
            }
        });
    }

    /// <summary>Opens the find widget of <paramref name="pane"/>'s tab, or gives it the keyboard with its text selected.</summary>
    private void OpenFind(int pane)
    {
        var model = _panes[pane];
        if (_editorViews[pane].IsOpen)
        {
            ShowNotice("Find in Pane finds names in a folder: this tab shows a file.");
            return;
        }
        if (model.Search is not null)
        {
            ShowNotice("These are search results: Esc goes back to the folder, then Ctrl+F finds in it.");
            return;
        }
        if (model.Path.Length == 0)
        {
            return;
        }
        if (pane != _active)
        {
            SetActive(pane);
        }
        EndQuickSearch();
        EndAddressEdit();
        var opened = !model.Find.IsOpen;
        model.Find.Open();
        UpdateFind(pane);
        _findViews[pane].FocusInput();
        if (opened)
        {
            Diag.Info(FindTarget, "find opened", new LogField("pane", pane), new LogField("path", model.Path));
        }
    }

    // Each key typed: the pane shows the rows whose names hold the text; an empty text shows them all.
    private async Task FindAsync(int pane, string text)
    {
        var model = _panes[pane];
        _findAsking[pane]++;
        try
        {
            if (!await model.SetFindTextAsync(text))
            {
                return;
            }
            Diag.Info(FindTarget, "find filtered", new LogField("pane", pane), new LogField("query_length", text.Length),
                new LogField("matches", model.Find.Matches), new LogField("rows", model.Count));
        }
        finally
        {
            _findAsking[pane]--;
        }
    }

    // Enter: the cursor goes to the first match, the widget stays and keeps the keyboard.
    private void SelectFirstMatch(int pane)
    {
        var selection = _panes[pane].Selection;
        if (!selection.IsFiltered || selection.ShownCount == 0)
        {
            return;
        }
        selection.MoveTo(selection.IndexAt(0), SelectMode.Single);
        _paneViews[pane].ScrollToFocus();
    }

    /// <summary>Closes the find widget of <paramref name="pane"/>: the text goes and every row shows again.</summary>
    private void CloseFind(int pane, bool focusPane)
    {
        var model = _panes[pane];
        if (!model.Find.IsOpen)
        {
            return;
        }
        // The list takes the keyboard before the box collapses, so it does not go elsewhere (see the palette).
        if (focusPane || _findViews[pane].HasFocus)
        {
            _paneViews[pane].Focus(FocusState.Programmatic);
        }
        model.Find.Close();
        _paneViews[pane].ScrollToFocus();
        Diag.Info(FindTarget, "find closed", new LogField("pane", pane), new LogField("rows", model.ShownCount));
    }

    // Esc: the find whose box has the keyboard, else the active pane's; false when neither is open.
    private bool CloseFindForEscape()
    {
        for (var i = 0; i < _findViews.Length; i++)
        {
            if (_findViews[i].HasFocus)
            {
                CloseFind(i, focusPane: true);
                return true;
            }
        }
        if (_panes[_active].Find.IsOpen && !_editorViews[_active].IsOpen)
        {
            CloseFind(_active, focusPane: true);
            return true;
        }
        return false;
    }

    // The widget shows what the pane's find holds: open or not, its text, the folder it finds in, the count. The toolbar's
    // Find button and the path row's filter label follow it.
    private void UpdateFind(int pane)
    {
        var find = _panes[pane].Find;
        var view = _findViews[pane];
        _crumbViews[pane].SetFind(find.IsOpen && !_editorViews[pane].IsOpen, find.Query);
        // A tool tab in front covers the folder tab, and its find with it: the find comes back with the folder tab.
        if (!find.IsOpen || _editorViews[pane].IsOpen)
        {
            if (view.IsOpen)
            {
                if (view.HasFocus)
                {
                    _paneViews[pane].Focus(FocusState.Programmatic);
                }
                view.Hide();
            }
            return;
        }
        var folder = _panes[pane].FolderName;
        if (!view.IsOpen || view.Query != find.Query || view.Folder != folder)
        {
            view.Show(find.Query ?? "", folder);
        }
        view.SetCount(find.Count);
    }

    // ----- The snapshot aid -----

    // XAML raises the box's text change on its next frame, and the core answers a moment after that. On a busy machine a
    // frame comes every few hundred milliseconds (0.5 s and more on the laptop with two test runs beside), so the fixed wait of
    // the find: step ended before the filter ran. The filter is applied when the pane's find holds the text and no answer
    // is on its way; until then the pane still shows the rows of the text before. A box that lost the typed text (the
    // find's own update puts the pane's text back when another step opens the find before the change was raised) ends the
    // wait at once, with the reason in the log: it would wait for a text that never comes.
    private async Task WaitForFindAsync(int pane, string text)
    {
        var reason = "20 s passed";
        for (var waited = 0; waited < 20_000; waited += 20)
        {
            if (_panes[pane].Find.Query == text && _findAsking[pane] == 0)
            {
                return;
            }
            if (_findViews[pane].Query != text)
            {
                reason = "the box no longer holds the text";
                break;
            }
            await Task.Delay(20);
        }
        Diag.Warn(FindTarget, "the find step ended before the filter was applied", new LogField("pane", pane), new LogField("text", text),
            new LogField("query", _panes[pane].Find.Query ?? ""), new LogField("box", _findViews[pane].Query),
            new LogField("asking", _findAsking[pane]), new LogField("reason", reason));
    }

    // find:<text> types into the active pane's find (opening it); find-key:enter|down|esc presses the key there.
    private async Task RunFindStepAsync(string kind, string argument)
    {
        var pane = _active;
        if (kind == "find")
        {
            OpenFind(pane);
            _findViews[pane].Type(argument);
            // The core answers in a few milliseconds on a quiet machine: the checks read the rows after it did.
            await Task.Delay(300);
            await WaitForFindAsync(pane, argument);
            return;
        }
        switch (argument)
        {
            case "enter":
                _findViews[pane].PressEnter();
                break;
            case "down":
                _paneViews[pane].Focus(FocusState.Keyboard);
                break;
            case "esc":
                await _router.ExecuteAsync("overlay.close", trigger: "snapshot");
                break;
        }
        await Task.Delay(150);
    }
}
