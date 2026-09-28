using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Ipc;

namespace CabinetOS.Tests.Support;

/// <summary>
/// The server end of an in-process named pipe, speaking the core's framing,
/// so the real <see cref="CoreClient"/> can be tested without the core.
/// </summary>
internal sealed class FakeCore : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;

    public FakeCore()
    {
        PipeName = "cabinetos-uitest-" + Guid.NewGuid().ToString("N");
        _server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public string PipeName { get; }

    /// <summary>Starts a client and accepts it.</summary>
    public async Task<CoreClient> ConnectClientAsync()
    {
        var accept = _server.WaitForConnectionAsync();
        var client = await CoreClient.ConnectAsync(PipeName, TimeSpan.FromSeconds(5));
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        return client;
    }

    /// <summary>Reads the next request the client sent.</summary>
    public async Task<JsonElement> ReadRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = await Framing.ReadFrameAsync(_server, timeout.Token) ?? throw new EndOfStreamException("the client closed the pipe");
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.Clone();
    }

    /// <summary>Sends one message as a frame.</summary>
    public Task SendAsync(string json) => Framing.WriteFrameAsync(_server, Encoding.UTF8.GetBytes(json)).AsTask();

    /// <summary>
    /// Replies to <paramref name="request"/> with its ID; the public properties
    /// of <paramref name="fields"/> become the reply's fields.
    /// </summary>
    public Task ReplyAsync(JsonElement request, string type, object? fields = null)
    {
        var reply = new JsonObject
        {
            ["id"] = request.GetProperty("id").GetString(),
            ["type"] = type,
        };
        if (fields is not null)
        {
            foreach (var (name, value) in JsonSerializer.SerializeToNode(fields)!.AsObject().ToList())
            {
                reply[name] = value?.DeepClone();
            }
        }
        return SendAsync(reply.ToJsonString());
    }

    /// <summary>Drops the connection, as a crashing core would.</summary>
    public void Disconnect() => _server.Disconnect();

    public ValueTask DisposeAsync()
    {
        _server.Dispose();
        return ValueTask.CompletedTask;
    }
}
