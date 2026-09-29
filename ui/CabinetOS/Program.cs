using System.Reflection;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Platform;
using CabinetOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CabinetOS;

/// <summary>
/// The entry point. Diagnostics start before anything else (Article 12), so
/// even a failure to start WinUI leaves a log line and a crash trace.
/// </summary>
public static class Program
{
    /// <summary>The version in log lines and crash traces.</summary>
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        Thread.CurrentThread.Name = "ui";
        // In heavy mode a full log queue drops this thread's lines instead of stopping the window.
        LogWriter.NeverWaitForHeavyLog();
        Diag.Init(Version);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diag.Crash(e.ExceptionObject as Exception, "unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.Error("cabinetos_ui::app", "a background task failed and nobody observed it",
                new LogField("error", e.Exception.ToString()));
            e.SetObserved();
        };

        var os = Environment.OSVersion.Version;
        if (!WindowsPlatform.IsSupported(os))
        {
            Diag.Error("cabinetos_ui::app", "Windows is older than 11 22H2; not starting", new LogField("os", os.ToString()));
            WindowsPlatform.ShowError("CabinetOS",
                $"CabinetOS needs Windows 11 version 22H2 (build {WindowsPlatform.MinimumBuild}) or newer. This PC runs build {os.Build}.");
            Diag.Shutdown();
            return 1;
        }

        Diag.Info("cabinetos_ui::app", "starting",
            new LogField("version", Version), new LogField("os", os.ToString()), new LogField("pid", Environment.ProcessId));
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ignored =>
        {
            // WinUI ends the process without raising either unhandled-exception
            // event for a failure here, so the trace is written on the way out.
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new UiSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(args);
            }
            catch (Exception error)
            {
                Diag.Crash(error, "the application could not start");
                throw;
            }
        });
        Diag.Info("cabinetos_ui::app", "exited");
        Diag.Shutdown();
        return 0;
    }
}
