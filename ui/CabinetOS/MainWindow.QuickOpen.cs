using System.Diagnostics;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Core.Shell;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;

namespace CabinetOS;

// Quick Open (Phase 16; the creator's SHELL_REDESIGN.md §3; docs/ui.md, "Quick Open"): Ctrl+P, or a click on the command
// center, lists the workspace's files and folders on the command palette's surface. A ">" typed first switches to the
// commands; Backspace in the palette's empty box comes back. Enter opens a row in the active pane, Ctrl+Enter in the
// other: a folder is listed, a file's folder is listed with the file under the cursor.
public sealed partial class MainWindow
{
    private const string QuickOpenTarget = "cabinetos_ui::quick_open";

    private QuickOpenModel _quickOpen = null!;

    // The last row's opening, which the snapshot aid's quick-open-key:enter waits for (the pane lists the folder, the cursor goes to the row).
    private Task _quickOpenOpening = Task.CompletedTask;

    private void SetUpQuickOpen()
    {
        _quickOpen = new QuickOpenModel(_session);
        QuickOpenView.Model = _quickOpen;
        QuickOpenView.QueryChanged += OnQuickOpenTextChanged;
        QuickOpenView.OpenRequested += (row, otherPane) => _quickOpenOpening = OpenFromQuickOpenAsync(row, otherPane);
        QuickOpenView.CloseRequested += () => CloseQuickOpen(returnFocus: true);
        Palette.FilesRequested += () =>
        {
            _palette.Close();
            OpenQuickOpen();
        };
    }

    private void RegisterQuickOpenCommands() =>
        _router.RegisterUiHandler("quickOpen.show", _ =>
        {
            if (_quickOpen.IsOpen)
            {
                CloseQuickOpen(returnFocus: true);
            }
            else
            {
                OpenQuickOpen();
            }
        });

    /// <summary>Shows Quick Open on the workspace: the repository that holds the active folder, else that folder.</summary>
    private void OpenQuickOpen()
    {
        EndAddressEdit();
        FileMenu.Close();
        CloseOtherOverlays(Overlay.QuickOpen);
        var root = WorkspaceRoot();
        _quickOpen.Open(root);
        QuickOpenView.Show();
        Diag.Info(QuickOpenTarget, "quick open shown", new LogField("root", root), new LogField("branch", _workspace?.Branch ?? ""));
    }

    /// <summary>Hides Quick Open; the keyboard goes back to the active pane first, unless something else takes it.</summary>
    private void CloseQuickOpen(bool returnFocus)
    {
        if (!_quickOpen.IsOpen)
        {
            return;
        }
        if (returnFocus)
        {
            // Before the box collapses: a collapsing focused box hands the keyboard to whatever comes next.
            FocusActivePane();
        }
        _quickOpen.Close();
        QuickOpenView.Hide();
    }

    // A ">" first switches to the commands with the rest of the text; anything else is searched once the keys pause.
    private void OnQuickOpenTextChanged(string text)
    {
        var (_, query, switched) = PaletteInput.Read(PaletteMode.Files, text);
        if (!switched)
        {
            QuickOpenView.Search();
            return;
        }
        CloseQuickOpen(returnFocus: false);
        TogglePalette();
        Palette.TypeQuery(query);
    }

    private async Task OpenFromQuickOpenAsync(QuickOpenRow row, bool otherPane)
    {
        CloseQuickOpen(returnFocus: false);
        var target = otherPane ? 1 - _active : _active;
        if (otherPane && !EnsureDual())
        {
            return;
        }
        if (target != _active)
        {
            SetActive(target);
        }
        if (_searchPane == target)
        {
            EndSearch(focusPane: false);
        }
        if (_editorViews[target].IsOpen)
        {
            // A tool tab in front: the pane's folder tab comes forward to show the row.
            ShowFolderTabBehindTool(target);
        }
        var folder = row.IsFolder ? row.Path : DisplayFormat.Parent(row.Path) ?? row.Path;
        Diag.Info(QuickOpenTarget, "quick open went to a row", new LogField("pane", target), new LogField("folder", row.IsFolder),
            new LogField("other_pane", otherPane));
        await _panes[target].NavigateAsync(folder, null, NavigationKind.New, row.IsFolder ? null : row.Name);
        _paneViews[target].ScrollToFocus();
        _paneViews[target].Focus(FocusState.Programmatic);
    }

    // ----- The snapshot aid -----

    // The text reaches the model when XAML raises the box's TextChanged (its next frame, which a busy machine draws a few
    // hundred milliseconds late), the search goes out after the keys pause (80 ms) and the core answers after that. The
    // model's Changed with the typed text as its Query is that answer. The 500 ms stay as the least wait (the speed review
    // types with this step at that pace); then the step waits for the answer, at most 20 s. A text that begins with ">"
    // closes Quick Open for the palette instead, which ends the wait too; a text the box already holds raises no change.
    private async Task TypeAndWaitForQuickOpenAnswerAsync(string text)
    {
        var answered = false;
        void OnChanged() => answered |= _quickOpen.Query == text;
        var started = Stopwatch.GetTimestamp();
        _quickOpen.Changed += OnChanged;
        try
        {
            QuickOpenView.Type(text);
            await Task.Delay(500);
            for (var waited = 0; !answered && _quickOpen.IsOpen && waited < 20_000; waited += 20)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            _quickOpen.Changed -= OnChanged;
        }
        LogField[] fields =
        [
            new("text", text), new("answered", answered), new("open", _quickOpen.IsOpen), new("rows", _quickOpen.Rows.Count),
            new("ms", Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds)),
        ];
        if (answered || !_quickOpen.IsOpen)
        {
            Diag.Info(QuickOpenTarget, "quick-open step ended", fields);
        }
        else
        {
            Diag.Warn(QuickOpenTarget, "quick-open step ended before Quick Open answered", fields);
        }
    }

    // quick-open:<text> opens Quick Open and types; quick-open-key:enter|ctrl+enter|down|esc presses the key there.
    private async Task RunQuickOpenStepAsync(string kind, string argument)
    {
        if (kind == "quick-open")
        {
            if (!_quickOpen.IsOpen)
            {
                await _router.ExecuteAsync("quickOpen.show", trigger: "snapshot");
            }
            await TypeAndWaitForQuickOpenAnswerAsync(argument);
            return;
        }
        switch (argument)
        {
            case "enter" or "ctrl+enter":
                await QuickOpenView.OpenHighlightedAsync(otherPane: argument == "ctrl+enter");
                // The opening lists a folder through the core, and the pane takes the keyboard (GotFocus, on XAML's next frames).
                await _quickOpenOpening;
                await SettleFramesAsync();
                await Task.Delay(400);
                break;
            case "down":
                _quickOpen.MoveHighlight(1);
                break;
            case "esc":
                await _router.ExecuteAsync("overlay.close", trigger: "snapshot");
                break;
        }
        await Task.Delay(150);
    }
}
