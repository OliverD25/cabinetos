using CabinetOS.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace CabinetOS.Services;

/// <summary>
/// The UI thread's synchronization context: every <c>await</c> on the UI
/// thread continues through it. It works like WinUI's own
/// <c>DispatcherQueueSynchronizationContext</c>, and also writes the crash
/// trace when a continuation throws. WinUI ends the process for an exception
/// in a queued callback without raising <c>UnhandledException</c>, so without
/// this a failing <c>async</c> handler would leave no trace (Article 12).
/// </summary>
internal sealed class UiSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
{
    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!queue.TryEnqueue(() => Run(d, state)))
        {
            Diag.Warn("cabinetos_ui::app", "the UI thread no longer takes work; a continuation was dropped");
        }
    }

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (!queue.HasThreadAccess)
        {
            throw new NotSupportedException("waiting for the UI thread from another thread is not allowed");
        }
        Run(d, state);
    }

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => new UiSynchronizationContext(queue);

    private static void Run(SendOrPostCallback d, object? state)
    {
        try
        {
            d(state);
        }
        catch (Exception error)
        {
            Diag.Crash(error, "unhandled exception on the UI thread");
            throw;
        }
    }
}
