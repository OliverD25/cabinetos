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

    private void SetUpQuickOpen()
    {
        _quickOpen = new QuickOpenModel(_session);
        QuickOpenView.Model = _quickOpen;
        QuickOpenView.QueryChanged += OnQuickOpenTextChanged;
        QuickOpenView.OpenRequested += (row, otherPane) => _ = OpenFromQuickOpenAsync(row, otherPane);
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

    // quick-open:<text> opens Quick Open and types; quick-open-key:enter|ctrl+enter|down|esc presses the key there.
    private async Task RunQuickOpenStepAsync(string kind, string argument)
    {
        if (kind == "quick-open")
        {
            if (!_quickOpen.IsOpen)
            {
                await _router.ExecuteAsync("quickOpen.show", trigger: "snapshot");
            }
            QuickOpenView.Type(argument);
            // The keys pause, the core answers: the checks read the rows after that.
            await Task.Delay(500);
            return;
        }
        switch (argument)
        {
            case "enter" or "ctrl+enter":
                await QuickOpenView.OpenHighlightedAsync(otherPane: argument == "ctrl+enter");
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
