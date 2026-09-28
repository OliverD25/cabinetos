using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace CabinetOS;

/// <summary>The WinUI application: one window, crash traces for the UI thread.</summary>
public partial class App : Application
{
    private readonly string[] _args;
    private MainWindow? _window;

    /// <summary>Creates the application; <c>--self-test-crash</c> proves the crash path.</summary>
    public App(string[] args)
    {
        _args = args;
        // The design's mode and the default theme's; a light theme switches the window's content (docs/ui.md, "Themes").
        RequestedTheme = ApplicationTheme.Dark;
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => LogXamlFailure("a resource reference failed", e.Message);
        DebugSettings.BindingFailed += (_, e) => LogXamlFailure("a binding failed", e.Message);
    }

    // A XAML failure often ends the process from native code a moment later,
    // before the background writer runs; these events are rare, so flush now.
    private static void LogXamlFailure(string what, string detail)
    {
        Diag.Warn("cabinetos_ui::xaml", what, new LogField("detail", detail));
        Diag.Writer?.Flush(TimeSpan.FromSeconds(1));
    }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var toolsDir = Array.IndexOf(_args, "--tools-dir") is var at and >= 0 && at + 1 < _args.Length ? _args[at + 1] : null;
            _window = new MainWindow(selfTestCrash: _args.Contains("--self-test-crash"), toolsDir);
            _window.Activate();
        }
        catch (Exception error)
        {
            Diag.Crash(error, "the window could not be created");
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Not handled: like the core after a panic, the UI stops rather than run
        // on in an unknown state. The trace and the flushed log say where it broke.
        var path = Diag.Crash(e.Exception, $"unhandled exception on the UI thread: {e.Message}");
        Diag.Error("cabinetos_ui::app", "crash trace written", new LogField("path", path));
        Diag.Writer?.Flush(TimeSpan.FromSeconds(2));
    }
}
