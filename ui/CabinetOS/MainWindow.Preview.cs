using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Preview;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tools;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS;

// The window's general parts of the AI phase (docs/ui.md, "The preview pane", "Plugin commands that
// ask", "Plugin events"). Nothing here knows an agent (Article 10): a plugin command may ask for a
// line of text, a preview a plugin proposes shows in the other pane, a plugin's event may be a
// notice, and a tool page may follow a plugin's events and take rows dropped on it.
public sealed partial class MainWindow
{
    private const string PreviewTarget = "cabinetos_ui::preview";

    private PreviewPane[] _previewViews = null!;
    private bool _previewMadeDual;
    private bool _previewBusy;
    private readonly HashSet<string> _previewsOpening = new(StringComparer.Ordinal);

    // The snapshot aid's preview that was made and not shown yet (`preview:make`), for `agent-event`.
    private string? _madePreview;

    private PreviewPane? ShownPreview => Array.Find(_previewViews, view => view.IsShown);

    private void SetUpPreview()
    {
        _previewViews = [LeftPreview, RightPreview];
        for (var i = 0; i < _previewViews.Length; i++)
        {
            var pane = i;
            _previewViews[pane].PaneIndex = pane;
            _paneViews[pane].DragChanged += SetDropCatchers;
            _editorViews[pane].PathsDropped = paths => DropOnTool(pane, paths);
        }
        _router.AskInput = AskPluginInputAsync;
    }

    // ----- A plugin command that asks for a line of text -----

    // The prompt box with the command's title and placeholder; an empty answer runs nothing, like Esc.
    private async Task<string?> AskPluginInputAsync(CommandInfo command)
    {
        var answer = await PromptView.ShowAsync(new PromptRequest(
            PluginInput.Label(command),
            PromptKind.Text,
            [],
            Placeholder: command.Input?.Placeholder ?? "",
            Hint: "Enter runs the command · Esc cancels"));
        return answer is { Text: var text } && text.Trim().Length > 0 ? text.Trim() : null;
    }

    // ----- Showing a preview -----

    /// <summary>
    /// Opens the preview <paramref name="id"/> (a plugin proposed it) in the other pane: <c>open_preview</c>
    /// answers its listing, which is read into rows. A preview already shown is left as it is.
    /// </summary>
    private async Task OpenPreviewAsync(string id, string? requestId = null)
    {
        if (ShownPreview?.Session?.Id == id)
        {
            ShownPreview.FocusPane();
            return;
        }
        if (_unavailable.Contains("open_preview") || !_previewsOpening.Add(id))
        {
            return;
        }
        try
        {
            switch (await RequestSafelyAsync(new OpenPreviewRequest(id) { Id = requestId ?? "" }))
            {
                case PreviewOpenedReply opened:
                    await ShowPreviewAsync(opened);
                    break;
                case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                    Unavailable("open_preview", "Previews need a newer core.");
                    break;
                case ErrorReply { Code: ErrorCodes.NoSuchPreview }:
                    ShowNotice("That preview is gone: it was applied, cancelled or is too old.");
                    break;
                case ErrorReply error:
                    ShowNotice($"Preview: {error.Message}", isError: true);
                    break;
            }
        }
        finally
        {
            _previewsOpening.Remove(id);
        }
    }

    // The reply's section is read once into rows and closed: a preview is small, and nothing keeps the core's listing open.
    private async Task ShowPreviewAsync(PreviewOpenedReply opened)
    {
        PreviewSession session;
        try
        {
            var section = opened.TakeSection() ?? throw new InvalidDataException("the reply carries no section");
            using var view = ListingView.Open(section, opened.SectionSize);
            if (!view.IsPreview)
            {
                throw new InvalidDataException("the listing is not a preview");
            }
            session = PreviewSession.From(opened.Preview, opened.Title, view);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            Diag.Warn(PreviewTarget, "a preview could not be read", new LogField("preview", opened.Preview), new LogField("error", error.Message));
            ShowNotice($"Preview: {error.Message}", isError: true);
            return;
        }
        finally
        {
            _ = RequestSafelyAsync(new CloseListingRequest(opened.Listing.ListingId));
        }
        if (ShownPreview is { Session: { } earlier } && earlier.Id != session.Id)
        {
            // A new proposal replaces the one still waiting for an answer: that one is dropped, not applied.
            await CancelPreviewAsync(quiet: true);
        }
        if (!_dual)
        {
            // The preview needs the other pane: single-pane mode shows two panes while it waits.
            ApplyDual(true);
            _previewMadeDual = true;
        }
        var pane = 1 - _active;
        // The preview is see-through like any pane: what lies under it (the list, a tool's page) is hidden while it shows.
        _paneViews[pane].Visibility = Visibility.Collapsed;
        _editorViews[pane].Visibility = Visibility.Collapsed;
        _previewViews[pane].Show(session);
        _previewViews[pane].FocusPane();
    }

    // The preview goes and the pane shows what its front tab shows again (its list, or its tool).
    private void EndPreviewView(PreviewPane view)
    {
        view.Hide();
        _ = QueueShow(view.PaneIndex, giveKeys: false);
    }

    private void HidePreview(PreviewPane view)
    {
        EndPreviewView(view);
        if (_previewMadeDual)
        {
            _previewMadeDual = false;
            if (_dual)
            {
                ApplyDual(false);
            }
        }
        FocusActivePane();
    }

    // ----- Enter applies, Esc cancels -----

    private async Task ApplyPreviewAsync()
    {
        if (ShownPreview is not { Session: { } session } view || _previewBusy)
        {
            return;
        }
        _previewBusy = true;
        try
        {
            switch (await RequestSafelyAsync(new PreviewApplyRequest(session.Id)))
            {
                case JobsStartedReply started:
                    HidePreview(view);
                    // The jobs come as events: the transfer pill and flyout show them, as for any job.
                    ShowNotice(started.Jobs.Count == 1 ? "Applying the changes: 1 job." : $"Applying the changes: {started.Jobs.Count} jobs.");
                    Diag.Info(PreviewTarget, "preview applied", new LogField("preview", session.Id), new LogField("jobs", started.Jobs.Count));
                    break;
                case ErrorReply { Code: ErrorCodes.NoSuchPreview }:
                    HidePreview(view);
                    ShowNotice("That preview is gone: it was applied, cancelled or is too old.");
                    break;
                case ErrorReply error:
                    // The first job was refused: the preview stays, and the pane says why.
                    view.ShowError(error.Message);
                    Diag.Info(PreviewTarget, "preview not applied", new LogField("preview", session.Id), new LogField("error", error.Message));
                    break;
            }
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private async Task CancelPreviewAsync(bool quiet = false)
    {
        if (ShownPreview is not { Session: { } session } view || _previewBusy)
        {
            return;
        }
        _previewBusy = true;
        try
        {
            var reply = await RequestSafelyAsync(new PreviewCancelRequest(session.Id));
            HidePreview(view);
            Diag.Info(PreviewTarget, "preview cancelled", new LogField("preview", session.Id), new LogField("reply", reply?.GetType().Name ?? "none"));
            if (!quiet)
            {
                ShowNotice("Preview cancelled: nothing changed.");
            }
        }
        finally
        {
            _previewBusy = false;
        }
    }

    // Another client applied or cancelled the preview on show, or it expired: the pane goes back to its folder.
    private void OnPreviewEvent(CoreEvent coreEvent)
    {
        var (id, applied) = coreEvent switch
        {
            PreviewAppliedEvent a => (a.Preview, true),
            PreviewCancelledEvent c => (c.Preview, false),
            _ => ("", false),
        };
        if (_previewBusy || ShownPreview is not { Session: { } session } view || session.Id != id)
        {
            return;
        }
        HidePreview(view);
        ShowNotice(applied ? "The changes were applied by another window." : "The preview is gone: it was cancelled or is too old.");
    }

    // While a preview is shown its two keys belong to it, before the keymap: Enter must not open the row under the
    // cursor and Esc must not close something else while changes wait for an answer. They act while the keyboard is
    // in the preview or in a list, not in a text box, a menu or a dialog (those take their own keys first).
    private bool HandlePreviewKey(KeyRoutedEventArgs e)
    {
        if (ShownPreview is not { } view || _palette.IsOpen || PromptView.IsOpen)
        {
            return false;
        }
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        if (focused is null or TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return false;
        }
        var inPreview = view.HasFocus;
        if (!inPreview && !IsInsideFilePane(focused))
        {
            return false;
        }
        var modifiers = CurrentModifiers();
        if (KeyNames.ComboFor((int)e.Key, modifiers)?.ToString() is { } combo)
        {
            switch (PreviewKeys.For(combo))
            {
                case PreviewKeyAction.Apply:
                    e.Handled = true;
                    _ = ApplyPreviewAsync();
                    return true;
                case PreviewKeyAction.Cancel:
                    e.Handled = true;
                    _ = CancelPreviewAsync();
                    return true;
            }
        }
        if (inPreview && modifiers == KeyModifiers.None && view.Scroll(e.Key))
        {
            e.Handled = true;
            return true;
        }
        return false;
    }

    private static bool IsInsideFilePane(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FilePane)
            {
                return true;
            }
        }
        return false;
    }

    // ----- What plugins say -----

    // A plugin's event: a notice its payload carries for the status bar, a preview its payload names (any event,
    // whatever its name, for both), and the tool pages that follow that plugin.
    private void OnPluginEvent(PluginEventEvent pluginEvent)
    {
        if (PluginEvents.NoticeText(pluginEvent.Payload) is { } notice)
        {
            ShowNotice(notice);
        }
        if (PluginEvents.ProposedPreview(pluginEvent.Payload) is { } preview)
        {
            _ = OpenPreviewAsync(preview);
        }
        OnBadgeEvent(pluginEvent);
        foreach (var host in AllToolHosts())
        {
            host.Deliver(pluginEvent);
        }
    }

    // Rule 1: a plugin command whose result names a preview opens it (instead of showing the raw result).
    private bool OpensPreview(CommandOutcome outcome)
    {
        if (outcome.Result is not { } result || _router.Find(outcome.CommandId) is not { Source.Kind: "plugin" }
            || PluginEvents.PreviewOfResult(result) is not { } preview)
        {
            return false;
        }
        _ = OpenPreviewAsync(preview, outcome.RequestId);
        return true;
    }

    // ----- Rows dropped on a tool's page -----

    // While rows are dragged, an open tool's page is covered so the drop reaches the window; a drop sends the paths on.
    private void SetDropCatchers(bool dragging)
    {
        foreach (var editor in _editorViews)
        {
            editor.SetDragging(dragging);
        }
    }

    private void DropOnTool(int pane, IReadOnlyList<string> paths)
    {
        if (_toolHosts[pane] is { } host && host.SendPathsDropped(paths))
        {
            Diag.Info("cabinetos_ui::tools", "paths dropped on a tool", new LogField("tool", host.Tool.Manifest.Id), new LogField("paths", paths.Count),
                new LogField("pane", pane));
            return;
        }
        ShowNotice("The tool is not ready to take files yet.");
    }

    // ----- The snapshot aid -----

    // fake-command adds a plugin command that asks for text (demo.ask) to the router's list, as a plugin's would
    // come from list_commands; cmd-nowait:demo.ask then shows its prompt box.
    // preview:rename shows a preview of the active folder's files (built here, proposed to the core with
    // preview_listing, opened as a plugin's would be); preview:make makes one and does not show it;
    // preview-key:enter|escape press the two keys; plugin-event:<name>|<payload> is a plugin's event
    // ($PREVIEW stands for the made preview); drop:<pane> drops the active pane's cursor row on that pane's tool.
    private async Task RunPreviewStepAsync(string kind, string argument)
    {
        switch (kind)
        {
            case "preview":
                var reply = await RequestSafelyAsync(new PreviewListingRequest("Rename the photos", SnapshotPreviewRows()));
                if (reply is PreviewOpenedReply opened)
                {
                    if (argument == "make")
                    {
                        _madePreview = opened.Preview;
                        _ = RequestSafelyAsync(new CloseListingRequest(opened.Listing.ListingId));
                    }
                    else
                    {
                        await ShowPreviewAsync(opened);
                    }
                }
                else
                {
                    Diag.Info("cabinetos_ui::snapshot", "preview: no preview", new LogField("reply", reply?.GetType().Name ?? "none"));
                }
                break;
            case "fake-command":
                var fake = new CommandInfo("demo.ask", "Demo", "Ask the demo", [], [], new CommandSource("plugin", "demo", "Demo"), "core", null, false,
                    new CommandInput("Ask the demo", "rename these to vacation_*"));
                _router.SetCommands([.. _router.Commands.Where(c => c.Id != fake.Id), fake]);
                break;
            case "preview-key":
                if (argument == "enter")
                {
                    await ApplyPreviewAsync();
                }
                else
                {
                    await CancelPreviewAsync();
                }
                break;
            case "plugin-event":
                var parts = argument.Replace("$PREVIEW", _madePreview ?? "", StringComparison.Ordinal).Split('|', 2);
                OnPluginEvent(new PluginEventEvent("snapshot", parts[0], parts.Length > 1 ? parts[1] : ""));
                break;
            case "drop" when int.TryParse(argument, out var pane) && pane is 0 or 1:
                if (Active.EntryAt(Active.FocusIndex) is { } entry)
                {
                    DropOnTool(pane, [entry.Path]);
                }
                break;
        }
        await Task.Delay(400);
    }

    private IReadOnlyList<PreviewRowRequest> SnapshotPreviewRows()
    {
        var pane = Active;
        var files = new List<string>();
        for (var i = 0; i < (pane.View?.Count ?? 0) && files.Count < 6; i++)
        {
            if (pane.EntryAt(i) is { IsFolder: false } entry)
            {
                files.Add(entry.Path);
            }
        }
        var archive = Path.Combine(pane.Path, "Archive");
        var rows = new List<PreviewRowRequest> { new(archive + @"\", "create") };
        foreach (var (path, index) in files.Select((p, i) => (p, i)))
        {
            rows.Add(index switch
            {
                < 3 => new PreviewRowRequest(path, "rename", "vacation_" + Path.GetFileName(path)),
                3 => new PreviewRowRequest(path, "delete"),
                4 => new PreviewRowRequest(path, "move", archive),
                _ => new PreviewRowRequest(path, "copy", archive),
            });
        }
        return rows;
    }
}
