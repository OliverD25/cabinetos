using System.Collections.Concurrent;

namespace CabinetOS.Tests.Support;

/// <summary>
/// A thread with a synchronization context, like the window's UI thread:
/// awaits that start on it continue on it.
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the thread and waits for its task.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }, null);
        return done.Task;
    }

    /// <summary>
    /// Queues a callback. One that comes after the thread finished (an event
    /// pump still running when its test failed) is dropped: throwing here
    /// would end the test host and every test after it.
    /// </summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The queue was marked complete.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public void Dispose() => _queue.CompleteAdding();

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}
