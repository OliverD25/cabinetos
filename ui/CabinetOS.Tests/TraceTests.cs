using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Trace ids (docs/diagnostics.md, "Trace ids"): a command run is one user action,
/// and every request its handler sends carries the run's ULID as <c>trace</c>.
/// </summary>
[Collection(HandleTests.Name)]
public class TraceTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private static readonly object Pong = new { protocol_version = 13, core_version = "0.1.0" };

    [Fact]
    public async Task Every_request_a_command_s_handler_sends_carries_the_command_s_trace()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();
        var router = new CommandRouter(client);
        string? runId = null;
        router.Executing += invocation => runId = invocation.RequestId;
        router.RegisterLocal("test.twoRequests", async _ =>
        {
            await client.RequestAsync(new PingRequest());
            await client.RequestAsync(new GetConfigRequest());
        });

        var run = router.ExecuteAsync("test.twoRequests", trigger: "key");
        var first = await core.ReadRequestAsync();
        await core.ReplyAsync(first, "pong", Pong);
        var second = await core.ReadRequestAsync();
        await core.ReplyAsync(second, "ok");
        var outcome = await run.WaitAsync(Step);

        Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
        Assert.NotNull(runId);
        Assert.Equal(runId, first.GetProperty("trace").GetString());
        Assert.Equal(runId, second.GetProperty("trace").GetString());
        Assert.NotEqual(runId, second.GetProperty("id").GetString());
        Assert.Null(Diag.CurrentTrace);

        // A request outside any command is an action of its own: no trace.
        var alone = client.RequestAsync(new PingRequest());
        var plain = await core.ReadRequestAsync();
        Assert.False(plain.TryGetProperty("trace", out _));
        await core.ReplyAsync(plain, "pong", Pong);
        await alone.WaitAsync(Step);
    }

    [Fact]
    public void A_trace_scope_nests_and_gives_the_previous_trace_back()
    {
        Assert.Null(Diag.CurrentTrace);
        using (Diag.BeginTrace("01J9ZQ4X7K3M5N8P2R6S0T1V4V"))
        {
            using (Diag.BeginTrace("01J9ZQ4X7K3M5N8P2R6S0T1V4W"))
            {
                Assert.Equal("01J9ZQ4X7K3M5N8P2R6S0T1V4W", Diag.CurrentTrace);
            }
            Assert.Equal("01J9ZQ4X7K3M5N8P2R6S0T1V4V", Diag.CurrentTrace);
        }
        Assert.Null(Diag.CurrentTrace);
    }

    [Fact]
    public void A_line_s_trace_comes_before_its_request_id()
    {
        var line = LogLine.Format(new DateTime(2026, 9, 29, 1, 2, 3, 4, DateTimeKind.Utc), LogLevel.Info,
            "cabinetos_ui::commands", "command executed", "01J9ZQ4X7K3M5N8P2R6S0T1V4V",
            "01J9ZQ4X7K3M5N8P2R6S0T1V4W", "request", null, "ui");
        Assert.Equal(
            """{"ts":"2026-09-29T01:02:03.004Z","level":"INFO","boundary":"frontend","target":"cabinetos_ui::commands","message":"command executed","trace_id":"01J9ZQ4X7K3M5N8P2R6S0T1V4V","request_id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","span":"request","thread":"ui"}""",
            line);
    }

    [Fact]
    public void An_event_s_trace_is_read_and_a_request_s_is_written_next_to_its_id()
    {
        var message = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","trace":"01J9ZQ4X7K3M5N8P2R6S0T1V4V","type":"config_changed","changed":["ui"]}"""u8);
        Assert.Equal("01J9ZQ4X7K3M5N8P2R6S0T1V4V", message.Trace);
        Assert.IsType<ConfigChangedEvent>(message.Body);
        Assert.Null(MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"ok"}"""u8).Trace);

        var ping = new PingRequest { Id = "01J9ZQ4X7K3M5N8P2R6S0T1V4W", Trace = "01J9ZQ4X7K3M5N8P2R6S0T1V4V" };
        Assert.Equal(
            """{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","trace":"01J9ZQ4X7K3M5N8P2R6S0T1V4V","type":"ping"}""",
            System.Text.Encoding.UTF8.GetString(MessageCodec.Encode(ping)));
        using var document = JsonDocument.Parse(MessageCodec.Encode(ping));
        Assert.Equal(["id", "trace", "type"], document.RootElement.EnumerateObject().Select(p => p.Name));
    }
}
