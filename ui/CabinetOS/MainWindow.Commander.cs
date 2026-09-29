using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

// Total Commander's small commands (sub-phase 11a; docs/research/total-commander.md, Part 3 (b);
// docs/ui.md, "Total Commander's keys"). The core's registry lists them with target ui; the
// window does the work.
public sealed partial class MainWindow
{
    private readonly PatternHistory _patterns = new();
    private readonly QuickSearch _quick = new(() => Environment.TickCount64);
    private DispatcherQueueTimer _quickTimer = null!;
    private bool _keyTakenByWindow;
    private int _quickRequest;

    private void SetUpQuickSearch()
    {
        // The status bar shows the typed letters until a second passes without one.
        _quickTimer = DispatcherQueue.CreateTimer();
        _quickTimer.IsRepeating = false;
        _quickTimer.Interval = TimeSpan.FromMilliseconds(QuickSearch.QuietMs);
        _quickTimer.Tick += (_, _) => EndQuickSearch();
        foreach (var view in _paneViews)
        {
            view.CharacterReceived += OnPaneCharacter;
        }
    }

    private void RegisterCommanderCommands()
    {
        _router.RegisterUiHandler("go.root", invocation => GoRootAsync(invocation));
        _router.RegisterUiHandler("go.showInLeftPane", ListingOnly(invocation => ShowInPaneAsync(0, invocation)));
        _router.RegisterUiHandler("go.showInRightPane", ListingOnly(invocation => ShowInPaneAsync(1, invocation)));
        _router.RegisterUiHandler("view.swapPanes", _ => SwapPanes());
        _router.RegisterUiHandler("view.refresh", RefreshAsync);
        _router.RegisterUiHandler("view.sortByName", ListingOnly(invocation => SortActiveAsync(PaneSort.Name, invocation)));
        _router.RegisterUiHandler("view.sortByExtension", ListingOnly(invocation => SortActiveAsync(PaneSort.Extension, invocation)));
        _router.RegisterUiHandler("view.sortByModified", ListingOnly(invocation => SortActiveAsync(PaneSort.Modified, invocation)));
        _router.RegisterUiHandler("view.sortBySize", ListingOnly(invocation => SortActiveAsync(PaneSort.Size, invocation)));
        // Space: a folder it marks is measured, as Total Commander's Space does (the note's decision D8).
        _router.RegisterUiHandler("edit.toggleSelectionInPlace", ListingOnly(invocation =>
            Active.Selection.MarkInPlace() && Active.EntryAt(Active.FocusIndex) is { IsFolder: true } folder
                ? MeasureFoldersAsync([folder.Path], invocation)
                : Task.CompletedTask));
        _router.RegisterUiHandler("file.calculateFolderSize", ListingOnly(invocation =>
            MeasureFoldersAsync([.. Active.Targets().Where(t => t.IsFolder).Select(t => t.Path)], invocation,
                "Put the cursor on a folder, or mark folders: their sizes are counted.")));
        _router.RegisterUiHandler("file.calculateAllFolderSizes", ListingOnly(invocation =>
            MeasureFoldersAsync(AllFolders(Active), invocation, "This folder has no folders to measure.")));
        _router.RegisterUiHandler("edit.invertSelection", ListingOnly(_ => InvertSelection()));
        _router.RegisterUiHandler("edit.unselectAll", ListingOnly(_ =>
        {
            Active.RememberMarks();
            Active.Selection.Clear();
        }));
        _router.RegisterUiHandler("edit.restoreSelection", ListingOnly(_ => RestoreSelection()));
        _router.RegisterUiHandler("edit.copyFullPath", ListingOnly(_ => CopyLines([.. Active.Targets().Select(t => t.Path)], "path", "paths")));
        _router.RegisterUiHandler("edit.copyName", ListingOnly(_ => CopyLines([.. Active.Targets().Select(t => t.Name)], "name", "names")));
        _router.RegisterUiHandler("edit.copyFolderPath", _ => CopyLines(Active.Path.Length > 0 ? [Active.Path] : [], "folder path", "folder paths"));
        _router.RegisterUiHandler("file.view", ListingOnly(ViewAsync));
        _router.RegisterUiHandler("file.edit", ListingOnly(EditAsync));
        _router.RegisterUiHandler("file.newTextFile", ListingOnly(NewTextFileAsync));
        // The Properties dialog's button passes its paths; from a key or the palette, the targets.
        _router.RegisterUiHandler("file.windowsProperties", invocation =>
            CommandArgs.Texts(invocation.Args, "paths") is not null ? WindowsPropertiesAsync(invocation) : ListingOnly(WindowsPropertiesAsync)(invocation));
        _router.RegisterUiHandler("edit.selectByPattern", ListingOnly(invocation => MarkByPatternAsync(mark: true, invocation)));
        _router.RegisterUiHandler("edit.unselectByPattern", ListingOnly(invocation => MarkByPatternAsync(mark: false, invocation)));
        _router.RegisterUiHandler("edit.selectSameExtension", ListingOnly(invocation => MarkSameExtensionAsync(mark: true, invocation)));
        _router.RegisterUiHandler("edit.unselectSameExtension", ListingOnly(invocation => MarkSameExtensionAsync(mark: false, invocation)));
        _router.RegisterUiHandler("go.pinnedFolders", PinnedFoldersAsync);
        _router.RegisterUiHandler("go.chooseDriveLeft", invocation => ChooseDriveAsync(0, invocation));
        _router.RegisterUiHandler("go.chooseDriveRight", invocation => ChooseDriveAsync(1, invocation));
        _router.RegisterUiHandler("terminal.insertPath", invocation => InsertPathsAsync(Active.Path.Length > 0 ? [Active.Path] : [], invocation));
        _router.RegisterUiHandler("terminal.insertSelectedPaths", ListingOnly(invocation => InsertPathsAsync([.. Active.Targets().Select(t => t.Path)], invocation)));
    }

    // Ctrl+P, Ctrl+Shift+Enter: the terminal shows (its default shell starts when none runs), the core
    // types the paths at the prompt, quoted for that shell and without Enter, and the terminal gets
    // the keyboard to go on typing (the note's decision D11).
    private async Task InsertPathsAsync(IReadOnlyList<string> paths, CommandInvocation invocation)
    {
        if (paths.Count == 0)
        {
            return;
        }
        if (_unavailable.Contains("terminal_type_paths"))
        {
            ShowNotice("Typing paths into the terminal needs a newer core.");
            return;
        }
        await ShowDockAsync(invocation.RequestId);
        CoreReply? reply;
        try
        {
            reply = await _terminal.TypePathsAsync(paths, invocation.RequestId);
        }
        catch (IOException error)
        {
            ShowNotice(error.Message, isError: true);
            return;
        }
        switch (reply)
        {
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("terminal_type_paths", "Typing paths into the terminal needs a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"The paths could not be typed: {error.Message}", isError: true);
                break;
        }
        FocusTerminal();
    }

    // Quick search (Q1): a character typed in a pane that no key of the keymap took jumps to the first
    // name starting with the letters typed so far; the core finds it (match_entries, first_from).
    private void OnPaneCharacter(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_keyTakenByWindow || char.IsControl(e.Character) || e.OriginalSource is TextBox
            || sender is not FilePane { IsRenaming: false, Model: { Search: null, ListingId: > 0 } pane } view)
        {
            return;
        }
        var ctrl = IsDown(Windows.System.VirtualKey.Control);
        var alt = IsDown(Windows.System.VirtualKey.Menu);
        if (ctrl != alt)
        {
            // Ctrl or Alt alone is a shortcut, not typing; both together are AltGr, which types.
            return;
        }
        e.Handled = true;
        TypeIntoQuickSearch(view, pane, e.Character);
    }

    private void TypeIntoQuickSearch(FilePane view, PaneModel pane, char character)
    {
        if (_unavailable.Contains("match_entries"))
        {
            ShowNotice("Quick search needs a newer core.");
            return;
        }
        var text = _quick.Type(character);
        ShowQuickText($"Quick search: {text}");
        _ = JumpToNameAsync(pane, view, text, _quick.FirstFrom(pane.FocusIndex));
    }

    /// <summary>Types <paramref name="text"/> into the active pane's quick search, as its keys would (the snapshot aid).</summary>
    private async Task QuickSearchForSnapshotAsync(string text)
    {
        foreach (var character in text)
        {
            if (Active is { Search: null, ListingId: > 0 } pane)
            {
                TypeIntoQuickSearch(_paneViews[_active], pane, character);
            }
            await Task.Delay(120);
        }
    }

    private async Task JumpToNameAsync(PaneModel pane, FilePane view, string text, uint firstFrom)
    {
        var request = ++_quickRequest;
        if (QuickSearch.PatternFor(text) is not { } pattern)
        {
            ShowQuickText($"Quick search: {text} · no name starts so");
            return;
        }
        var reply = await RequestSafelyAsync(new MatchEntriesRequest(pane.ListingId, pattern) { FirstFrom = firstFrom });
        if (request != _quickRequest)
        {
            // Another letter came meanwhile: its answer counts, not this one.
            return;
        }
        switch (reply)
        {
            case EntryMatchesReply matches when pane.View is { } shown && matches.ListingId == pane.ListingId && matches.Generation == shown.Generation:
                if (EntryRanges.Rows(matches.Ranges, shown.Count).FirstOrDefault(-1) is var row and >= 0)
                {
                    pane.Selection.MoveTo(row, pane.Selection.KeyMode(shift: false, ctrl: false));
                    view.ScrollToFocus();
                }
                else
                {
                    ShowQuickText($"Quick search: {text} · no name starts so");
                }
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                EndQuickSearch();
                Unavailable("match_entries", "Quick search needs a newer core.");
                break;
        }
    }

    private void ShowQuickText(string text)
    {
        QuickText.Text = text;
        QuickText.Visibility = Visibility.Visible;
        _quickTimer.Stop();
        _quickTimer.Start();
    }

    // Esc, another folder, or a second without a letter: the next letter starts a new search.
    private void EndQuickSearch()
    {
        _quick.Clear();
        _quickTimer?.Stop();
        QuickText.Visibility = Visibility.Collapsed;
    }

    // Alt+F1, Alt+F2: the drive list under that pane's header, the sidebar's list_volumes data;
    // the pane becomes the active one, and the right one shows first when one pane was shown.
    // A letter picks its drive at once. A drive goes to the folder this pane last showed there,
    // else to its root.
    private async Task ChooseDriveAsync(int paneIndex, CommandInvocation invocation)
    {
        if (_sidebar.Drives.Count == 0)
        {
            ShowNotice("No drives to choose from: this core does not list volumes.");
            return;
        }
        if (paneIndex == 1 && !_dual)
        {
            ApplyDual(true);
            _ = PersistAsync(ShellState.DualPaneKey, true);
        }
        var pane = _panes[paneIndex];
        var view = _paneViews[paneIndex];
        view.Focus(FocusState.Programmatic);
        SetActive(paneIndex);
        var drives = _sidebar.Drives.ToList();
        var current = DriveMemory.LetterOf(pane.Path);
        var rows = drives.Select(d => new PromptRow(d.Name, d.FreeText, "", Key: d.Path.Length > 0 ? d.Path[0] : null)).ToList();
        var request = new PromptRequest("Drives", PromptKind.Pick, rows, ShowInput: false,
            Hint: "A letter or Enter goes to the drive: to the folder this pane last showed there.");
        view.UpdateLayout();
        var answer = PromptView.ShowAsync(request, view.HeaderElement);
        // The drive the pane is on starts highlighted.
        var here = drives.FindIndex(d => d.Path.Length > 0 && char.ToUpperInvariant(d.Path[0]) == current);
        PromptView.Highlight(Math.Max(0, here));
        if (await answer is not { Row: { } row } || drives.FirstOrDefault(d => d.Name == row.Title) is not { } drive)
        {
            return;
        }
        var target = drive.Path.Length > 0 ? pane.Drives.FolderOn(drive.Path[0]) : drive.Path;
        // The folder remembered there may be gone since: then the drive's root.
        if (!await pane.NavigateAsync(target, invocation.RequestId) && !string.Equals(target, drive.Path, StringComparison.OrdinalIgnoreCase))
        {
            await pane.NavigateAsync(drive.Path);
        }
    }

    // Num +, Num -: the pattern box in the palette's frame. It offers the last pattern, lists ten,
    // and marks files only unless "Include folders" is on (the note's decision D9). The core matches.
    private async Task MarkByPatternAsync(bool mark, CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.View is null || pane.ListingId == 0)
        {
            return;
        }
        if (_unavailable.Contains("match_entries"))
        {
            ShowNotice("Marking by pattern needs a newer core.");
            return;
        }
        var answer = await PromptView.ShowAsync(new PromptRequest(
            mark ? "Select" : "Unselect",
            PromptKind.Text,
            [.. _patterns.Items.Select(pattern => new PromptRow(pattern, Glyph: ""))],
            Text: _patterns.Last,
            Placeholder: "*.txt;*.md",
            Option: "Include folders",
            Hint: "* and ? stand for any text and one character; ; separates patterns; | leaves out the ones after it"));
        if (answer is null || answer.Text.Trim().Length == 0)
        {
            return;
        }
        _patterns.Add(answer.Text);
        await MarkMatchesAsync(pane, answer.Text.Trim(), filesOnly: !answer.OptionChecked, mark, invocation.RequestId);
    }

    // Alt+Num +, Alt+Num -: every file with the cursor file's extension.
    private Task MarkSameExtensionAsync(bool mark, CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.EntryAt(pane.FocusIndex) is not { IsFolder: false } entry)
        {
            ShowNotice("Put the cursor on a file: its extension picks the files.");
            return Task.CompletedTask;
        }
        if (_unavailable.Contains("match_entries"))
        {
            ShowNotice("Marking by pattern needs a newer core.");
            return Task.CompletedTask;
        }
        return MarkMatchesAsync(pane, PatternHistory.SameExtension(entry.Name), filesOnly: true, mark, invocation.RequestId);
    }

    // match_entries answers ranges of the section it read; if the listing changed meanwhile, it is
    // asked once more about the new one, so no mark lands on the wrong row.
    private async Task MarkMatchesAsync(PaneModel pane, string patterns, bool filesOnly, bool mark, string requestId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reply = await RequestSafelyAsync(new MatchEntriesRequest(pane.ListingId, patterns) { FilesOnly = filesOnly, Id = attempt == 0 ? requestId : "" });
            switch (reply)
            {
                case EntryMatchesReply matches when pane.View is { } view && matches.ListingId == pane.ListingId && matches.Generation == view.Generation:
                    if (!mark)
                    {
                        pane.RememberMarks();
                    }
                    pane.Selection.SetMarks(EntryRanges.Rows(matches.Ranges, view.Count), mark);
                    var count = EntryRanges.Count(matches.Ranges);
                    var what = count == 1 ? (filesOnly ? "1 file" : "1 entry") : $"{count:N0} {(filesOnly ? "files" : "entries")}";
                    ShowNotice(count == 0 ? $"Nothing matches {patterns}." : $"{(mark ? "Marked" : "Unmarked")} {what} matching {patterns}.");
                    return;
                case EntryMatchesReply:
                    continue;
                case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                    Unavailable("match_entries", "Marking by pattern needs a newer core.");
                    return;
                case ErrorReply error:
                    ShowNotice($"{patterns}: {error.Message}", isError: true);
                    return;
                default:
                    return;
            }
        }
    }

    // Ctrl+D: the sidebar's folders in the palette's frame; Enter goes there in the active pane. The
    // last row pins the folder this pane shows, when it is not pinned yet.
    private async Task PinnedFoldersAsync(CommandInvocation invocation)
    {
        var folder = Active.Path;
        var rows = _sidebar.Pinned.Select(item => new PromptRow(item.Name, item.Path, "")).ToList();
        if (folder.Length > 0 && !_sidebar.IsPinned(folder))
        {
            rows.Add(new PromptRow($"Pin {DisplayFormat.FolderName(folder)}", folder, "", Sticky: true));
        }
        var answer = await PromptView.ShowAsync(new PromptRequest("Go to", PromptKind.Pick, rows,
            Placeholder: "Type to narrow the list",
            Hint: "Enter goes to the folder; the last row pins the folder this pane shows."));
        if (answer?.Row is not { } row)
        {
            return;
        }
        await _router.ExecuteAsync(row.Sticky ? "sidebar.pin" : "go.toPath", CommandArgs.With("path", row.Detail), "palette");
    }

    // F3: the cursor file in the first installed tool that shows it. Never its default
    // application, which for a program would run it (the note's decision D5).
    private async Task ViewAsync(CommandInvocation invocation)
    {
        if (Active.EntryAt(Active.FocusIndex) is not { } entry)
        {
            return;
        }
        if (entry.IsFolder)
        {
            // Total Commander's F3 on a folder shows how big it is.
            await MeasureFoldersAsync([entry.Path], invocation);
            return;
        }
        if (await ToolForAsync(entry.Name) is { } tool)
        {
            await OpenInToolAsync(tool, entry.Path);
            return;
        }
        ShowNotice($"No installed tool shows {entry.Name}. Viewers are opt-in: find one in the marketplace (Ctrl+Shift+X).");
    }

    // F4: the cursor file opens for editing in files.editor, the type's edit verb, or Notepad (the core picks).
    private async Task EditAsync(CommandInvocation invocation)
    {
        if (Active.EntryAt(Active.FocusIndex) is not { } entry)
        {
            return;
        }
        if (entry.IsFolder)
        {
            ShowNotice($"F4 edits files; {entry.Name} is a folder.");
            return;
        }
        await EditPathAsync(entry.Path, entry.Name, invocation.RequestId);
    }

    private async Task EditPathAsync(string path, string name, string? requestId)
    {
        if (_unavailable.Contains("edit_path"))
        {
            ShowNotice("Editing files needs a newer core.");
            return;
        }
        // The editor starts from the core, a background process: let it come to the front.
        if (_session.CoreProcessId is { } corePid)
        {
            WindowsPlatform.AllowForeground(corePid);
        }
        switch (await RequestSafelyAsync(new EditPathRequest(path) { Id = requestId ?? "" }))
        {
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("edit_path", "Editing files needs a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"Cannot edit {name}: {error.Message}", isError: true);
                break;
        }
    }

    // Shift+F4: a name box over a new row; the core creates the empty file, and it opens for
    // editing. A name that is taken opens that file instead, as in Total Commander.
    private async Task NewTextFileAsync(CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.Path.Length == 0 || pane.View is null)
        {
            return;
        }
        if (_unavailable.Contains("create_file"))
        {
            ShowNotice("New text files need a newer core.");
            return;
        }
        var name = await _paneViews[pane.Index].BeginNewNameAsync("New Text Document.txt");
        if (name is null)
        {
            return;
        }
        var path = DisplayFormat.Join(pane.Path, name);
        switch (await RequestSafelyAsync(new CreateFileRequest(path) { Id = invocation.RequestId }))
        {
            case OkReply:
                await pane.SelectWhenListedAsync(name, TimeSpan.FromSeconds(2));
                _paneViews[pane.Index].ScrollToFocus();
                await EditPathAsync(path, name, requestId: null);
                break;
            case ErrorReply { Code: ErrorCodes.AlreadyExists }:
                var existing = pane.View?.IndexOfName(name) ?? -1;
                if (existing >= 0 && pane.View!.IsFolder(existing))
                {
                    ShowNotice($"{name} is a folder: a text file cannot take its name.", isError: true);
                    break;
                }
                if (existing >= 0)
                {
                    pane.Selection.MoveTo(existing, pane.Selection.KeyMode(shift: false, ctrl: false));
                    _paneViews[pane.Index].ScrollToFocus();
                }
                await EditPathAsync(path, name, requestId: null);
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("create_file", "New text files need a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"New text file: {error.Message}", isError: true);
                break;
        }
    }

    // Windows' own property sheet (the core's process shows it): the targets, else the folder.
    private async Task WindowsPropertiesAsync(CommandInvocation invocation)
    {
        IReadOnlyList<string> paths = CommandArgs.Texts(invocation.Args, "paths") ?? [.. Active.Targets().Select(t => t.Path)];
        if (paths.Count == 0 && Active.Path.Length > 0)
        {
            paths = [Active.Path];
        }
        if (paths.Count == 0)
        {
            return;
        }
        if (_unavailable.Contains("show_properties"))
        {
            ShowNotice("Windows' property sheet needs a newer core.");
            return;
        }
        if (_session.CoreProcessId is { } corePid)
        {
            WindowsPlatform.AllowForeground(corePid);
        }
        switch (await RequestSafelyAsync(new ShowPropertiesRequest(paths) { Id = invocation.RequestId }))
        {
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("show_properties", "Windows' property sheet needs a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"Windows Properties: {error.Message}", isError: true);
                break;
        }
    }

    // Ctrl+Shift+C, Ctrl+K Ctrl+N, Ctrl+K Ctrl+P: text on Windows' clipboard, one per line and
    // without quotes (the note's decision D10). CabinetOS's own file clipboard is not touched.
    private void CopyLines(IReadOnlyList<string> lines, string one, string many)
    {
        if (lines.Count == 0)
        {
            return;
        }
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(string.Join("\r\n", lines));
            Clipboard.SetContent(package);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Diag.Info(Target, "the Windows clipboard refused the text", new LogField("error", error.Message), new LogField("hresult", error.HResult));
            ShowNotice($"The Windows clipboard is busy: the {(lines.Count == 1 ? one : many)} could not be copied.", isError: true);
            return;
        }
        ShowNotice(lines.Count == 1 ? $"Copied the {one}: {lines[0]}" : $"Copied {lines.Count:N0} {many}.");
    }

    // The core counts (measure_paths); the Size column shows the running total in the tertiary
    // colour, then the total. A folder counted already or being counted is not asked again.
    private async Task MeasureFoldersAsync(IReadOnlyList<string> paths, CommandInvocation invocation, string? noneNotice = null)
    {
        if (paths.Count == 0)
        {
            if (noneNotice is not null)
            {
                ShowNotice(noneNotice);
            }
            return;
        }
        if (_unavailable.Contains("measure_paths"))
        {
            ShowNotice("Folder sizes need a newer core.");
            return;
        }
        CoreReply? reply;
        try
        {
            reply = await Active.MeasureAsync(paths, invocation.RequestId);
        }
        catch (IOException error)
        {
            ShowNotice(error.Message, isError: true);
            return;
        }
        switch (reply)
        {
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                Unavailable("measure_paths", "Folder sizes need a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"Folder size: {error.Message}", isError: true);
                break;
        }
    }

    private static List<string> AllFolders(PaneModel pane)
    {
        var folders = new List<string>();
        if (pane.View is { } view)
        {
            for (var i = 0; i < view.Count; i++)
            {
                if (view.IsFolder(i))
                {
                    folders.Add(DisplayFormat.Join(pane.Path, view.Name(i)));
                }
            }
        }
        return folders;
    }

    // measure_progress and measure_finished go to the pane that asked; folders that could not be
    // read are not in the sizes, and the status bar says so.
    private void OnMeasureEvent(CoreEvent coreEvent)
    {
        foreach (var pane in _panes)
        {
            if (!pane.ApplyMeasure(coreEvent))
            {
                continue;
            }
            if (coreEvent is MeasureFinishedEvent finished && finished.Results.Aggregate(0UL, (sum, r) => sum + r.Unreadable) is > 0 and var unreadable)
            {
                ShowNotice(unreadable == 1
                    ? "1 folder could not be read; what is in it is not in the size."
                    : $"{unreadable:N0} folders could not be read; what is in them is not in the sizes.");
            }
            return;
        }
    }

    // Num *: the files' marks turn around; the folders keep theirs (Total Commander's rule).
    private void InvertSelection()
    {
        if (Active.View is not { } view)
        {
            return;
        }
        Active.RememberMarks();
        Active.Selection.Invert(view.IsFolder);
    }

    // Num /: the marks the last file command or unmark cleared in this pane, by name.
    private void RestoreSelection()
    {
        var pane = Active;
        if (pane.View is not { } view)
        {
            return;
        }
        if (pane.SavedMarks.IsEmpty)
        {
            ShowNotice("Nothing to restore: no marks were cleared in this pane yet.");
            return;
        }
        if (pane.SavedMarks.Restore(view, pane.Selection) == 0)
        {
            ShowNotice($"None of the {pane.SavedMarks.Names.Count:N0} marked names is in this folder.");
        }
    }

    // Ctrl+F3 to Ctrl+F6: the active pane's own order, sent with its listings; the same key again
    // reverses it. The other pane and panes.sort stay as they are.
    private async Task SortActiveAsync(string key, CommandInvocation invocation)
    {
        var pane = Active;
        if (pane.Path.Length == 0)
        {
            return;
        }
        if (_session.ProtocolVersion < PaneSort.ProtocolFor(key))
        {
            ShowNotice($"Sorting by {key} needs a newer core.");
            return;
        }
        var sort = PaneSort.Next(pane.EffectiveSort, key);
        if (await pane.SortAsync(sort, invocation.RequestId))
        {
            ShowNotice($"Sorted by {PaneSort.Describe(sort)}.");
        }
    }

    // Ctrl+\: the drive's root, C:\, or the share's, \\server\share\.
    private async Task GoRootAsync(CommandInvocation invocation)
    {
        var root = DisplayFormat.Root(Active.Path);
        if (root.Length > 0 && !string.Equals(root.TrimEnd('\\'), Active.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            await Active.NavigateAsync(root, invocation.RequestId, NavigationKind.New, null);
        }
    }

    // Ctrl+Left, Ctrl+Right: the folder under the cursor, or the active pane's folder when the
    // cursor is on a file, shown in that pane. The right pane shows first when one pane was shown.
    private async Task ShowInPaneAsync(int target, CommandInvocation invocation)
    {
        var pane = Active;
        var folder = pane.EntryAt(pane.FocusIndex) is { IsFolder: true } entry ? entry.Path : pane.Path;
        if (folder.Length == 0)
        {
            return;
        }
        if (target == 1 && !_dual)
        {
            ApplyDual(true);
            _ = PersistAsync(ShellState.DualPaneKey, true);
        }
        await _panes[target].NavigateAsync(folder, invocation.RequestId);
    }

    // Ctrl+U: the two panes change places with everything they hold (folder, marks, history,
    // sort, search results). The keyboard stays on the same side, which shows the other folder.
    private void SwapPanes()
    {
        if (!_dual)
        {
            ShowNotice("Swapping needs two panes: press Ctrl+Shift+D.");
            return;
        }
        if (_strips.Any(strip => strip.Tabs.Any(t => t.IsTool)))
        {
            // A tool's process belongs to its pane's editor; the tabs it shows cannot change sides.
            ShowNotice("Close the tool tabs first: a tool stays in its pane.");
            return;
        }
        (_panes[0], _panes[1]) = (_panes[1], _panes[0]);
        // The tabs go with the panes: each pane's strip, and the tab its model holds.
        (_strips[0], _strips[1]) = (_strips[1], _strips[0]);
        (_held[0], _held[1]) = (_held[1], _held[0]);
        for (var i = 0; i < _panes.Length; i++)
        {
            _panes[i].Index = i;
            _panes[i].IsActive = i == _active;
            _paneViews[i].Model = _panes[i];
            _tabViews[i].Model = _strips[i];
            var pane = i;
            _panes[i].LockedNavigation = (path, requestId, selectName) => OpenFolderInNewTabAsync(pane, path, requestId, selectName);
        }
        OnTabsChanged();
        if (_searchPane >= 0)
        {
            _searchPane = 1 - _searchPane;
        }
        UpdateStatus();
        UpdateNavigationButtons();
        UpdateCrumbs();
        _sidebar.SetActivePath(Active.Path);
        _terminal.SetActiveFolder(Active.Path);
        ScheduleToolContext();
        _paneViews[_active].Focus(FocusState.Programmatic);
    }

    // Ctrl+R: the folder listed again, for drives whose changes Windows does not report; the marks
    // stay. In search results, the search runs again.
    private Task RefreshAsync(CommandInvocation invocation) =>
        Active.Search is null ? Active.RefreshAsync(invocation.RequestId) : SearchWhenDueAsync(now: true);
}
