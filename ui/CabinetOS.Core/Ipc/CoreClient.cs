using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Ipc;

/// <summary>What the command router and the panes need from the core.</summary>
public interface ICoreChannel
{
    /// <summary>
    /// Sends <paramref name="request"/> (a fresh ULID is set when its
    /// <c>Id</c> is empty) and returns the reply, <see cref="ErrorReply"/> included.
    /// </summary>
    Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The core answered a request with <c>error</c>.</summary>
public sealed class CoreRequestException(string requestId, string requestType, string code, string message)
    : Exception($"{requestType} failed: {code}: {message}")
{
    /// <summary>The request's ULID, to find it in both logs.</summary>
    public string RequestId { get; } = requestId;

    /// <summary>The request type, for example <c>list_directory</c>.</summary>
    public string RequestType { get; } = requestType;

    /// <summary>The error code (docs/ipc.md).</summary>
    public string Code { get; } = code;

    /// <summary>The core's explanation.</summary>
    public string CoreMessage { get; } = message;
}

/// <summary>The connection to the core ended.</summary>
public sealed class CoreDisconnectedException(string reason) : IOException($"the connection to the core ended: {reason}")
{
    /// <summary>Why it ended.</summary>
    public string Reason { get; } = reason;
}

/// <summary>The core sent something this UI cannot use as the reply it expected.</summary>
public sealed class CoreProtocolException(string message) : IOException(message);

/// <summary>
/// The client end of the control channel (docs/ipc.md). A background task
/// reads every frame: replies complete the request with the same <c>id</c>;
/// events go to <see cref="Events"/>, which the window drains onto its UI
/// thread. Every request is logged with its <c>request_id</c> (Article 12).
/// </summary>
public sealed class CoreClient : ICoreChannel, IAsyncDisposable
{
    /// <summary>The name this client gives in <c>hello</c>.</summary>
    public const string ClientName = "CabinetOS.exe";

    private const string Target = "cabinetos_ui::pipe";

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Channel<CoreEvent> _events = Channel.CreateUnbounded<CoreEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private int _ended;
    private int _disposed;
    private string? _endReason;

    private CoreClient(Stream stream) => _stream = stream;

    /// <summary>Raised once, on the reader's thread, when the connection ends.</summary>
    public event Action<string>? Disconnected;

    /// <summary>The events the core sent, in order. Completes when the connection ends.</summary>
    public ChannelReader<CoreEvent> Events => _events.Reader;

    /// <summary>Whether the connection is still up.</summary>
    public bool IsConnected => Volatile.Read(ref _ended) == 0;

    /// <summary>Why the connection ended, once it has.</summary>
    public string? EndReason => Volatile.Read(ref _endReason);

    /// <summary>The pipe name for a launch token: <c>cabinetos-core-&lt;token&gt;</c>.</summary>
    public static string PipeNameForToken(string token) => "cabinetos-core-" + token;

    /// <summary>
    /// Connects to <c>\\.\pipe\&lt;pipeName&gt;</c>, waiting at most
    /// <paramref name="timeout"/> for it to accept.
    /// </summary>
    public static async Task<CoreClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return Start(pipe);
    }

    /// <summary>A client over any duplex stream (tests use an in-process pipe).</summary>
    public static CoreClient Start(Stream stream)
    {
        var client = new CoreClient(stream);
        client._reader = Task.Run(client.ReadLoopAsync);
        return client;
    }

    /// <summary>Says <c>hello</c> with this process's real ID, as the core requires.</summary>
    public Task<WelcomeReply> HelloAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<WelcomeReply>(new HelloRequest((uint)Environment.ProcessId, ClientName), cancellationToken);

    /// <summary>
    /// Sends a request and returns its reply as <typeparamref name="T"/>. An
    /// <c>error</c> reply throws <see cref="CoreRequestException"/>.
    /// </summary>
    public async Task<T> RequestAsync<T>(CoreRequest request, CancellationToken cancellationToken = default)
        where T : CoreReply
    {
        var reply = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            T typed => typed,
            ErrorReply error => throw new CoreRequestException(request.Id, request.Type, error.Code, error.Message),
            _ => throw new CoreProtocolException($"{request.Type} got {reply.GetType().Name}, not {typeof(T).Name}"),
        };
    }

    /// <inheritdoc/>
    public async Task<CoreReply> RequestAsync(CoreRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.Id))
        {
            request.Id = Ulid.NewId();
        }
        // The request belongs to the action (the command run) that sends it.
        request.Trace ??= Diag.CurrentTrace;
        var id = request.Id;
        // A caller on the UI thread gets its continuation posted to that thread
        // while the reader is still handling this reply, before it reads the
        // next frame. So a reply reaches the UI before any event the core sent
        // after it (a listing_opened before its first refresh). A caller with
        // no such context continues on the thread pool, never on the reader.
        var completion = new TaskCompletionSource<CoreReply>(SynchronizationContext.Current is null
            ? TaskCreationOptions.RunContinuationsAsynchronously
            : TaskCreationOptions.None);
        if (!_pending.TryAdd(id, new Pending(completion, request.Type, Stopwatch.GetTimestamp(), request.Trace)))
        {
            throw new InvalidOperationException($"request ID {id} is already waiting for a reply");
        }
        if (!IsConnected)
        {
            _pending.TryRemove(id, out _);
            throw new CoreDisconnectedException(EndReason ?? "not connected");
        }

        var payload = MessageCodec.Encode(request);
        Diag.Request(LogLevel.Info, id, Target, "request sent", new LogField("request", request.Type), new LogField("bytes", payload.Length));
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Framing.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            EndConnection($"cannot write to the core: {error.Message}");
            throw new CoreDisconnectedException(EndReason ?? error.Message);
        }

        using var registration = cancellationToken.Register(() =>
        {
            // A reply that still arrives finds nobody waiting and is cleaned up.
            if (_pending.TryRemove(id, out var abandoned))
            {
                abandoned.Completion.TrySetCanceled(cancellationToken);
            }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the core to exit (<c>shutdown</c>), waiting at most
    /// <paramref name="timeout"/> for its answer, then closes this connection.
    /// The core gives open connections up to 2 s to finish before it exits, so
    /// the client closes its own at once instead of making the core wait.
    /// </summary>
    public async Task ShutdownCoreAsync(TimeSpan timeout)
    {
        if (IsConnected)
        {
            try
            {
                using var deadline = new CancellationTokenSource(timeout);
                await RequestAsync(new ShutdownRequest(), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                Diag.Info(Target, "shutdown request not answered", new LogField("error", error.Message));
            }
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the connection and closes every section handle nobody took. Safe to call twice.</summary>
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
            // The reader reports its own end; nothing more to do here.
        }
        EndConnection("the client closed the connection");
        while (_events.Reader.TryRead(out var leftover))
        {
            (leftover as ICarriesSection)?.TakeSection()?.Dispose();
        }
        _stop.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var reason = "the core closed the pipe";
        try
        {
            while (true)
            {
                var frame = await Framing.ReadFrameAsync(_stream, _stop.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }
                Dispatch(frame);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            reason = "the client closed the connection";
        }
        catch (Exception error)
        {
            reason = error.Message;
        }
        EndConnection(reason);
    }

    private void Dispatch(byte[] frame)
    {
        IncomingMessage message;
        try
        {
            message = MessageCodec.Decode(frame);
        }
        catch (JsonException error)
        {
            Diag.Error(Target, "a frame from the core is not a JSON object; ignored", new LogField("error", error.Message));
            return;
        }

        if (message.Body is ICarriesSection carrier && carrier.SectionHandle != 0)
        {
            carrier.Section = new SectionHandle((nint)carrier.SectionHandle);
        }

        if (message.IsEvent)
        {
            if (message.Body is CoreEvent coreEvent)
            {
                Diag.Log(LogLevel.Debug, Target, "event received", fields: [new LogField("event", message.Type)], traceId: message.Trace);
                if (!_events.Writer.TryWrite(coreEvent))
                {
                    (coreEvent as ICarriesSection)?.TakeSection()?.Dispose();
                }
            }
            else
            {
                Diag.Debug(Target, "event ignored", new LogField("event", message.Type), new LogField("error", message.Error));
            }
            return;
        }

        if (message.Id is not null && _pending.TryRemove(message.Id, out var pending))
        {
            var elapsedUs = (long)Stopwatch.GetElapsedTime(pending.StartedTicks).TotalMicroseconds;
            switch (message.Body)
            {
                case ErrorReply error:
                    Diag.Traced(LogLevel.Info, pending.Trace, message.Id, Target, "request failed",
                        new LogField("request", pending.Type), new LogField("code", error.Code),
                        new LogField("error", error.Message), new LogField("elapsed_us", elapsedUs));
                    pending.Completion.TrySetResult(error);
                    break;
                case CoreReply reply:
                    Diag.Traced(LogLevel.Info, pending.Trace, message.Id, Target, "reply received",
                        new LogField("request", pending.Type), new LogField("reply", message.Type),
                        new LogField("elapsed_us", elapsedUs));
                    pending.Completion.TrySetResult(reply);
                    break;
                default:
                    Diag.Traced(LogLevel.Warn, pending.Trace, message.Id, Target, "reply not understood",
                        new LogField("request", pending.Type), new LogField("reply", message.Type),
                        new LogField("error", message.Error));
                    pending.Completion.TrySetException(new CoreProtocolException(
                        $"the reply `{message.Type}` to {pending.Type} is not one this UI understands: {message.Error}"));
                    break;
            }
            return;
        }

        // Nobody waits for this reply: its request was cancelled.
        Diag.Log(LogLevel.Debug, Target, "reply to no waiting request", message.Id, "request",
            [new LogField("reply", message.Type)]);
        if (message.Body is ListingOpenedReply opened)
        {
            opened.TakeSection()?.Dispose();
            _ = CloseAbandonedListingAsync(opened.ListingId);
        }
    }

    private async Task CloseAbandonedListingAsync(ulong listingId)
    {
        try
        {
            await RequestAsync(new CloseListingRequest(listingId)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Diag.Debug(Target, "cannot close an abandoned listing", new LogField("listing_id", listingId), new LogField("error", error.Message));
        }
    }

    private void EndConnection(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return;
        }
        Volatile.Write(ref _endReason, reason);
        Diag.Info(Target, "connection to the core ended", new LogField("reason", reason));
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Completion.TrySetException(new CoreDisconnectedException(reason));
            }
        }
        _events.Writer.TryComplete();
        Disconnected?.Invoke(reason);
    }

    private sealed record Pending(TaskCompletionSource<CoreReply> Completion, string Type, long StartedTicks, string? Trace);
}
