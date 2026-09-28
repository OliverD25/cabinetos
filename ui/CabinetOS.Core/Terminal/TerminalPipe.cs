using System.IO.Pipes;
using CabinetOS.Core.Diagnostics;

namespace CabinetOS.Core.Terminal;

/// <summary>
/// The client end of a session's byte pipe (docs/terminal.md, "The byte
/// pipe"): raw bytes both ways, no framing. A background task reads the
/// shell's output into an <see cref="OutputCoalescer"/>; keys are written in
/// order. The session belongs to the core: closing this pipe detaches, the
/// shell goes on.
/// </summary>
public sealed class TerminalPipe : IAsyncDisposable
{
    private const string Target = "cabinetos_ui::terminal";

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private int _disposed;

    private TerminalPipe(Stream stream, OutputCoalescer output)
    {
        _stream = stream;
        Output = output;
    }

    /// <summary>Raised on the reader's thread when the buffer had been empty: schedule a flush.</summary>
    public event Action? OutputWaiting;

    /// <summary>Raised once, on the reader's thread, when the pipe ended (the shell exited, or detach).</summary>
    public event Action? Ended;

    /// <summary>The output waiting for the UI.</summary>
    public OutputCoalescer Output { get; }

    /// <summary>Whether the pipe still runs.</summary>
    public bool IsOpen => !_reader.IsCompleted;

    /// <summary>
    /// Opens <paramref name="pipe"/> (<c>\\.\pipe\cabinetos-term-…</c> as
    /// <c>terminal_opened</c> names it), waiting at most <paramref name="timeout"/>.
    /// </summary>
    public static async Task<TerminalPipe> ConnectAsync(string pipe, OutputCoalescer output, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        const string prefix = @"\\.\pipe\";
        var name = pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? pipe[prefix.Length..] : pipe;
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return Start(client, output);
    }

    /// <summary>A pipe over any duplex stream (tests use an in-process pipe).</summary>
    public static TerminalPipe Start(Stream stream, OutputCoalescer output)
    {
        var pipe = new TerminalPipe(stream, output);
        pipe._reader = Task.Run(pipe.ReadLoopAsync);
        return pipe;
    }

    /// <summary>Sends keys: text, <c>\r</c> for Enter, VT sequences for the other keys.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Diag.Debug(Target, "cannot write to a terminal pipe", new LogField("error", error.Message));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Detaches: the pipe closes and the shell keeps running in the core.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stop.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reader reports its own end.
        }
        _stop.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (Output.Add(buffer.AsSpan(0, read)))
                {
                    OutputWaiting?.Invoke();
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Diag.Debug(Target, "a terminal pipe ended", new LogField("error", error.Message));
        }
        Ended?.Invoke();
    }
}
