using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Protocol;
using CabinetOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CabinetOS;

// Diagnostics in the window (docs/ui.md, "Heavy logging"; docs/diagnostics.md, "Heavy mode"): the
// window's own log follows logging.heavy as the core's does, the pill in the status bar says heavy
// mode is on, and while it is on the window also writes what only it sees: every key by name (never
// the text typed into a box), the focus, the frame table, its requests and replies.
public sealed partial class MainWindow
{
    // How far back "Diagnostics: Save Log Bundle" asks the core to look, in minutes (the core's default).
    private const uint BundleMinutes = 10;

    private readonly HeavyLogSwitch _heavy = HeavyLogSwitch.ForProcess();
    private FrameMonitor? _heavyFrames;
    private DispatcherQueueTimer? _heavyPillTimer;
    private string _focusShown = "none";
    private string? _keyTrace;

    private void SetUpDiagnostics()
    {
        _router.RegisterUiHandler("diagnostics.toggleHeavy", ToggleHeavyAsync);
        _router.RegisterUiHandler("diagnostics.openLogFolder", _ => OpenFolderAsync(Diag.Writer?.Directory ?? Diag.DefaultDirectory()));
        _router.RegisterUiHandler("diagnostics.saveBundle", SaveLogBundleAsync);
        HeavyPill.Click += (_, _) => _ = _router.ExecuteAsync("diagnostics.toggleHeavy", trigger: "button");
        // The pill's count of lost lines changes without any event: it is read once a second while heavy mode is on.
        _heavyPillTimer = DispatcherQueue.CreateTimer();
        _heavyPillTimer.Interval = TimeSpan.FromSeconds(1);
        _heavyPillTimer.Tick += (_, _) => UpdateHeavyPill();
        // A button of the status bar goes through the router like every other (brief section 5).
        _router.RegisterLocal("diagnostics.openCrashFolder", OpenCrashFolderAsync);
        CrashFolderLink.Click += (_, _) => _ = _router.ExecuteAsync("diagnostics.openCrashFolder", trigger: "button");
        _ = OfferCrashBundleAsync();
    }

    // ----- The switch -----

    // logging.heavy, as the core sent it. The window's own log follows it; CABINETOS_LOG_HEAVY, when set,
    // decides for both processes, and then the first call only shows what is already on.
    private void ApplyHeavyLogging(bool configured, bool firstStart)
    {
        var change = _heavy.Follow(configured);
        if (change == HeavyLogChange.TurnedOff)
        {
            StopHeavyExtras();
        }
        else if (change == HeavyLogChange.TurnedOn || (firstStart && _heavy.IsOn))
        {
            StartHeavyExtras();
            ShowNotice("Heavy logging is on; the log folder grows to 2 GB.");
        }
        UpdateHeavyPill();
    }

    // The core writes the setting; both processes follow its config_changed, so the window's state
    // changes when the event comes back, never before.
    private async Task ToggleHeavyAsync(CommandInvocation invocation)
    {
        if (_heavy.FromEnvironment)
        {
            ShowNotice("CABINETOS_LOG_HEAVY decides heavy logging for this run. Unset it to use the setting.");
            return;
        }
        if (!await _settingsWriter.SetAsync("logging.heavy", !_heavy.IsOn))
        {
            ShowNotice("Heavy logging could not be switched: the core did not take the setting.", isError: true);
        }
    }

    private void StartHeavyExtras()
    {
        // The frame table every second, as CABINETOS_UI_FRAMESTATS=1 gives it (which then already runs its own monitor).
        if (_frames is null)
        {
            _heavyFrames ??= new FrameMonitor(() => _session.CoreProcessId, heavyOnly: true);
            _heavyFrames.Start();
        }
        FocusManager.GotFocus -= OnHeavyGotFocus;
        FocusManager.GotFocus += OnHeavyGotFocus;
        _heavyPillTimer?.Start();
    }

    private void StopHeavyExtras()
    {
        _heavyFrames?.Stop();
        FocusManager.GotFocus -= OnHeavyGotFocus;
        _heavyPillTimer?.Stop();
    }

    private void UpdateHeavyPill()
    {
        var pill = _heavy.Pill;
        HeavyPill.Visibility = pill.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (pill.Visible && HeavyPillText.Text != pill.Text)
        {
            HeavyPillText.Text = pill.Text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HeavyPill, pill.Text);
            ToolTipService.SetToolTip(HeavyPill, pill.ToolTip);
        }
    }

    // ----- The log folder and the bundle -----

    private string? _crashBundle;

    // The last run crashed while heavy mode was on if a crash-*.zip is newer than the last start
    // (CrashNotice). The folder is read off the UI thread (brief section 1); the offer stays in the
    // status bar until it is used, since a notice would be gone in five seconds. A run that started and
    // never wrote its clean end (a native failure of WinUI leaves no crash trace) is one line in the log
    // and nothing on screen: it is for the next look at the log (Article 12), not a question for the user.
    private async Task OfferCrashBundleAsync()
    {
        var directory = Diag.Writer?.Directory ?? Diag.DefaultDirectory();
        var check = await Task.Run(() => CrashNotice.CheckAtStart(directory, DateTime.UtcNow));
        if (check.UncleanStartUtc is { } started)
        {
            Diag.Warn(Target, "previous run ended without closing", new LogField("started_utc", started.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (check.CrashBundle is not { } bundle)
        {
            return;
        }
        _crashBundle = bundle;
        CrashFolderLink.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(CrashFolderLink, $"The last run crashed while heavy logging was on. The logs around the crash are in {Path.GetFileName(bundle)}.");
        Diag.Info(Target, "a crash bundle from the last run is offered", new LogField("path", bundle));
        ShowNotice("The last run crashed while heavy logging was on: the logs around the crash are in a zip in the log folder.");
    }

    private async Task OpenCrashFolderAsync(CommandInvocation invocation)
    {
        CrashFolderLink.Visibility = Visibility.Collapsed;
        await OpenFolderAsync(Path.GetDirectoryName(_crashBundle) ?? Diag.Writer?.Directory ?? Diag.DefaultDirectory());
    }

    // The core opens it (open_path): the window makes no shell call of its own (brief section 1).
    private async Task OpenFolderAsync(string folder)
    {
        if (_session.CoreProcessId is { } corePid)
        {
            WindowsPlatform.AllowForeground(corePid);
        }
        CoreReply reply;
        try
        {
            reply = await _session.RequestAsync(new OpenPathRequest(folder));
        }
        catch (IOException error)
        {
            reply = new ErrorReply(ErrorCodes.Io, error.Message);
        }
        if (reply is ErrorReply refused)
        {
            ShowNotice($"Cannot open {folder}: {refused.Message}", isError: true);
        }
    }

    private async Task SaveLogBundleAsync(CommandInvocation invocation)
    {
        CoreReply reply;
        try
        {
            reply = await _session.RequestAsync(new SaveLogBundleRequest(BundleMinutes));
        }
        catch (IOException error)
        {
            ShowNotice($"Cannot save the log bundle: {error.Message}", isError: true);
            return;
        }
        switch (reply)
        {
            case LogBundleReply bundle:
                ShowNotice($"Saved {Path.GetFileName(bundle.Path)}, the last {BundleMinutes} minutes of every log.");
                await OpenFolderAsync(Path.GetDirectoryName(bundle.Path) ?? Diag.DefaultDirectory());
                break;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                ShowNotice("Saving a log bundle needs a newer core.");
                break;
            case ErrorReply error:
                ShowNotice($"Cannot save the log bundle: {error.Message}", isError: true);
                break;
        }
    }

    // ----- What heavy mode records in the window -----

    // Every key press by name and modifiers, and the type of the element that has the keyboard. The text
    // typed into a box is never written: such a key is "text input" without the character (KeyLog). A key
    // gets its own ULID; a command the key starts takes it as its trace (TakeKeyTrace), so the press, the
    // command and the requests of the command are one chain. A key that becomes no command is a chain of one.
    private void LogHeavyKey(KeyRoutedEventArgs e)
    {
        _keyTrace = null;
        if (!Diag.HeavyEnabled)
        {
            return;
        }
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        var inTextBox = focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;
        var entry = KeyLog.Describe((int)e.Key, CurrentModifiers(), inTextBox);
        if (entry.Message == KeyLog.KeyMessage)
        {
            _keyTrace = Ulid.NewId();
        }
        var fields = new List<LogField>(4);
        if (entry.Key is { } key)
        {
            fields.Add(new LogField("key", key));
        }
        fields.Add(new LogField("modifiers", entry.Modifiers));
        fields.Add(new LogField("element", DescribeElement(focused)));
        if (e.KeyStatus.WasKeyDown)
        {
            fields.Add(new LogField("repeat", true));
        }
        Diag.Log(LogLevel.Debug, "heavy::keys", entry.Message, fields: fields, traceId: _keyTrace);
    }

    private string? TakeKeyTrace()
    {
        var trace = _keyTrace;
        _keyTrace = null;
        return trace;
    }

    // Where the keyboard was and where it went, by element type and name, and which window (the terminal's
    // page, a tool's page, the window's own controls) Windows sends the keys to (KeyboardOwnerName).
    private void OnHeavyGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (!Diag.HeavyEnabled)
        {
            return;
        }
        var to = DescribeElement(e.NewFocusedElement);
        var keys = WindowsPlatform.KeyboardFocus(_uiThreadId);
        Diag.Heavy("focus", "focus changed", new LogField("from", _focusShown), new LogField("to", to),
            new LogField("keys_to", keys is { } owner ? KeyboardOwnerName(owner.Window, owner.Class, owner.ProcessId) : "none"));
        _focusShown = to;
    }

    // The element's type and its name (x:Name, else its accessible name): "TextBox AddressEdit". Never its text.
    private static string DescribeElement(object? element)
    {
        if (element is null)
        {
            return "none";
        }
        var type = element.GetType().Name;
        if (element is FrameworkElement framework)
        {
            var name = framework.Name.Length > 0 ? framework.Name : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(framework);
            if (!string.IsNullOrEmpty(name))
            {
                return $"{type} {name}";
            }
        }
        return type;
    }
}
