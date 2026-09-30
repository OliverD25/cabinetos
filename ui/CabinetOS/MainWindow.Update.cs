using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Updates;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// In-app updates (Phase 17; docs/ui.md, "Updates"; ADR 0014): the core checks, downloads, verifies and swaps
// (cabinetos-update); the window shows where it is (the pill, the dot on the menu button and in About), shows the
// notes in the update dialog, and restarts itself into the new version. It reads no file and fetches nothing itself
// (brief section 1): the notes come in update_state.
public sealed partial class MainWindow
{
    private const string UpdateTarget = "cabinetos_ui::update";

    private readonly UpdateModel _update = new();

    // Set by a restart into a new version: the CabinetOS.exe the window starts once its core has stopped.
    private string? _restartExe;

    private void SetUpUpdates()
    {
        _router.RegisterUiHandler("update.check", CheckForUpdatesAsync);
        _router.RegisterUiHandler("update.apply", RestartToUpdateAsync);
        _router.RegisterUiHandler("update.rollback", RollBackAsync);
        _router.RegisterUiHandler("update.showNotes", invocation => ShowNotesAsync(invocation, automatic: false));
        UpdatePill.Click += (_, _) => _ = _router.ExecuteAsync("update.apply", trigger: "button");
        ShowUpdate();
    }

    // ----- The state -----

    // At every start of a core (the first, and after a restart of it): the updater's state from memory. A core before
    // protocol 14 answers unknown_request, and the window then offers nothing of the update.
    private async Task ReadUpdateStatusAsync()
    {
        CoreReply? reply;
        try
        {
            reply = await _session.RequestAsync(new UpdateStatusRequest());
        }
        catch (IOException error)
        {
            Diag.Info(UpdateTarget, "the update state could not be read", new LogField("error", error.Message));
            return;
        }
        if (reply is UpdateStateReply state)
        {
            OnUpdateState(state.Status, fromEvent: false);
        }
        else
        {
            _update.Forget();
            ShowUpdate();
        }
    }

    private void OnUpdateEvent(CoreEvent coreEvent)
    {
        switch (coreEvent)
        {
            case UpdateStateChangedEvent changed:
                OnUpdateState(changed.Status, fromEvent: true);
                break;
            case UpdateProgressEvent progress:
                _update.Apply(progress);
                ShowUpdate();
                break;
        }
    }

    private void OnUpdateState(UpdateStatus status, bool fromEvent)
    {
        var before = _update.Status?.State;
        _update.Apply(status);
        if (before != status.State)
        {
            Diag.Info(UpdateTarget, "update state", new LogField("state", status.State), new LogField("latest", status.Latest?.Version ?? ""),
                new LogField("installed", status.Installed ?? ""), new LogField("event", fromEvent));
        }
        ShowUpdate();
        // The snooze rule (UpdateText.OpensDialog): a download that finished opens the dialog once, unless Later was
        // chosen less than a day ago. A dialog already open is not pushed aside; the pill and the dot stay.
        if (_update.OpensDialog(DateTimeOffset.UtcNow) && _openDialog is null)
        {
            _ = ShowNotesAsync(null, automatic: true);
        }
    }

    // The pill in the status bar, the dot on the menu button.
    private void ShowUpdate()
    {
        var pill = _update.Pill();
        UpdatePill.Visibility = pill.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (pill.Visible)
        {
            UpdatePillText.Text = pill.Text;
            UpdatePillTrack.Visibility = pill.ShowsTrack ? Visibility.Visible : Visibility.Collapsed;
            UpdatePillFill.Width = UpdatePillTrack.Width * pill.Fraction;
            UpdatePill.IsHitTestVisible = pill.Restarts;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpdatePill, pill.Text);
            ToolTipService.SetToolTip(UpdatePill, pill.ToolTip);
        }
        var waiting = _update.WaitingVersion;
        MenuUpdateDot.Visibility = waiting is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(MenuButton, waiting is null ? "Menu" : $"Menu · CabinetOS {waiting} is ready");
    }

    // ----- The commands -----

    /// <summary>
    /// Update: Check for Updates. A version that already waits shows its dialog; otherwise the core reads the
    /// channel's latest.json now, and a newer version downloads at once with the pill. Its dialog then opens even
    /// after Later, since the user asked just now.
    /// </summary>
    private async Task CheckForUpdatesAsync(CommandInvocation invocation)
    {
        if (_update.Status is null)
        {
            await ReadUpdateStatusAsync();
        }
        switch (_update.Status?.State)
        {
            case null:
                ShowNotice("Updates need a newer core.");
                return;
            case UpdatePhases.NotUpdatable:
                ShowNotice(_update.Status.Reason ?? "This install does not update itself.");
                return;
            case UpdatePhases.Downloaded or UpdatePhases.Ready:
                await ShowNotesAsync(invocation, automatic: false);
                return;
            case UpdatePhases.Checking or UpdatePhases.Downloading or UpdatePhases.Applying:
                ShowNotice(UpdateText.AboutRow(_update.Status, _update.Progress, DateTime.Now).Text);
                return;
        }
        ShowNotice("Checking for updates…");
        if (await UpdateStepAsync(new UpdateCheckRequest { Id = invocation.RequestId }, "The update check failed") is not { } status)
        {
            return;
        }
        if (UpdateText.CheckedNotice(status) is { } notice)
        {
            ShowNotice(notice, isError: status.State == UpdatePhases.Failed);
            return;
        }
        if (status.State == UpdatePhases.Available)
        {
            ShowNotice($"Downloading CabinetOS {status.Latest?.Version}…");
            if (await UpdateStepAsync(new UpdateDownloadRequest(), "The download failed") is not { State: UpdatePhases.Downloaded })
            {
                return;
            }
            ShowNotice("");
        }
        if (_update.WaitingVersion is { } waiting && _update.DialogShownFor != waiting)
        {
            await ShowNotesAsync(invocation, automatic: false);
        }
    }

    /// <summary>
    /// Update: Restart to Update, and the pill's click: the downloaded version goes into the install folder (the core's
    /// swap), then the window restarts into it. A swap done already (by the command line or another window) restarts
    /// at once. A transfer that runs holds the restart: the core stops with the window.
    /// </summary>
    private async Task RestartToUpdateAsync(CommandInvocation invocation)
    {
        var status = _update.Status;
        if (status?.State is not (UpdatePhases.Downloaded or UpdatePhases.Ready))
        {
            ShowNotice("No update is downloaded. Update: Check for Updates looks for one.");
            return;
        }
        if (_transfers.Visible.Any(job => !job.IsFinal))
        {
            ShowNotice("A transfer is still running. Restart to update once it has finished.");
            return;
        }
        if (status.State == UpdatePhases.Downloaded)
        {
            ShowNotice($"Installing CabinetOS {status.Latest?.Version}…");
            status = await UpdateStepAsync(new UpdateApplyRequest(), "The update could not be installed");
            if (status is not { State: UpdatePhases.Ready })
            {
                return;
            }
        }
        RestartInto(status, invocation.RequestId);
    }

    /// <summary>
    /// Update: Roll Back to the Previous Version: after a yes, the version kept in previous\ goes back into the install
    /// folder, and the window restarts into it.
    /// </summary>
    private async Task RollBackAsync(CommandInvocation invocation)
    {
        var status = _update.Status;
        if (status is null || status.State == UpdatePhases.NotUpdatable)
        {
            ShowNotice(status?.Reason ?? "Updates need a newer core.");
            return;
        }
        if (status.Previous is not { } previous)
        {
            ShowNotice("No previous version is kept. The version before an update stays until the next one.");
            return;
        }
        if (_transfers.Visible.Any(job => !job.IsFinal))
        {
            ShowNotice("A transfer is still running. Roll back once it has finished.");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Roll back to CabinetOS {previous}?",
            Content = new TextBlock
            {
                Text = $"CabinetOS {previous} goes back into its folder, and CabinetOS restarts into it. The version in place now is removed; an update brings it back.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Roll back and restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            FocusActivePane();
            return;
        }
        ShowNotice($"Rolling back to CabinetOS {previous}…");
        if (await UpdateStepAsync(new UpdateRollbackRequest { Id = invocation.RequestId }, "The rollback failed") is { State: UpdatePhases.Ready } ready)
        {
            RestartInto(ready, invocation.RequestId);
        }
    }

    /// <summary>
    /// The update dialog (Update: Show Release Notes, and by itself once a download finished): the version and its
    /// notes, Restart now and Later. Later (and Esc) snoozes the dialog for a day while a version waits.
    /// </summary>
    private async Task ShowNotesAsync(CommandInvocation? invocation, bool automatic)
    {
        if (UpdateText.Dialog(_update.Status) is not { } view)
        {
            ShowNotice(_update.Status is null
                ? "Updates need a newer core."
                : "No newer version is known yet. Update: Check for Updates looks for one.");
            return;
        }
        _update.MarkDialogShown(view.Version);
        Diag.Info(UpdateTarget, "update dialog shown", new LogField("version", view.Version), new LogField("automatic", automatic),
            new LogField("notes_blocks", view.Notes.Count));
        var dialog = UpdateDialog.Create(view, RootGrid.XamlRoot, RootGrid.ActualTheme);
        var result = await ShowDialogAsync(dialog);
        var requestId = invocation?.RequestId ?? Ulid.NewId();
        if (result == ContentDialogResult.Primary)
        {
            await RestartToUpdateAsync(invocation ?? new CommandInvocation("update.apply", null, requestId, "dialog"));
            return;
        }
        FocusActivePane();
        if (view.SnoozesOnClose)
        {
            Diag.Request(LogLevel.Info, requestId, UpdateTarget, "update snoozed", new LogField("version", view.Version));
            await UpdateStepAsync(new UpdateSnoozeRequest(), "Later could not be recorded");
        }
    }

    // One update request: its update_state is applied and returned; an error is a notice, and null.
    private async Task<UpdateStatus?> UpdateStepAsync(CoreRequest request, string failure)
    {
        switch (await RequestSafelyAsync(request))
        {
            case UpdateStateReply reply:
                OnUpdateState(reply.Status, fromEvent: false);
                if (reply.Status.State == UpdatePhases.Failed)
                {
                    ShowNotice($"{failure}: {reply.Status.Message ?? "no reason given"}", isError: true);
                }
                return reply.Status;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                ShowNotice("Updates need a newer core.");
                return null;
            case ErrorReply error:
                ShowNotice($"{failure}: {error.Message}", isError: true);
                return null;
            default:
                return null;
        }
    }

    // ----- The restart -----

    // The new version is in the install folder: the window closes the way the close button does (the tabs and the last
    // folders are saved, the core stops), then starts the CabinetOS.exe there (OnClosing, StartRestart).
    private void RestartInto(UpdateStatus status, string requestId)
    {
        if (status.InstallDir is not { Length: > 0 } folder)
        {
            ShowNotice("The core did not say where CabinetOS is installed; start it again from the Start menu.", isError: true);
            return;
        }
        _restartExe = Path.Combine(folder, "CabinetOS.exe");
        Diag.Request(LogLevel.Info, requestId, UpdateTarget, "restarting into the new version",
            new LogField("installed", status.Installed ?? ""), new LogField("exe", _restartExe));
        _ = CloseWindowAsync();
    }

    // After the core stopped: the new window starts with a core of its own, from the install folder.
    private void StartRestart()
    {
        if (_restartExe is not { } exe)
        {
            return;
        }
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        // The new window finds the core next to it, not one a variable names, and runs no snapshot steps.
        start.Environment.Remove(Core.Ipc.CoreLauncher.CoreExeEnv);
        start.Environment.Remove(DevSnapshots.FolderEnv);
        start.Environment.Remove(DevSnapshots.StepsEnv);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            Diag.Info(UpdateTarget, "the new version started", new LogField("pid", process?.Id), new LogField("exe", exe));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The window is already gone: the log is the only place left to say it.
            Diag.Error(UpdateTarget, "the new version could not start", new LogField("exe", exe), new LogField("error", error.Message));
        }
    }
}
