using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The pipe client against a fake core on an in-process named pipe.</summary>
[Collection(HandleTests.Name)]
public class CoreClientTests
{
    /// <summary>Every wait is bounded, so a bug fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private static readonly object Pong = new { protocol_version = 7, core_version = "0.1.0" };

    [Fact]
    public async Task Hello_is_sent_with_this_process_and_the_ui_name()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        var hello = client.HelloAsync();
        var request = await core.ReadRequestAsync();
        Assert.Equal("hello", request.GetProperty("type").GetString());
        Assert.Equal(Environment.ProcessId, request.GetProperty("client_pid").GetInt32());
        Assert.Equal("CabinetOS.exe", request.GetProperty("client_name").GetString());
        Assert.True(Ulid.IsValid(request.GetProperty("id").GetString()));

        await core.ReplyAsync(request, "welcome", Pong);
        Assert.Equal(new WelcomeReply(7, "0.1.0"), await hello.WaitAsync(Step));
    }

    [Fact]
    public async Task Replies_are_matched_to_requests_by_id_in_any_order()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        var first = client.RequestAsync(new VolumeInfoRequest(@"C:\"));
        var firstRequest = await core.ReadRequestAsync();
        var second = client.RequestAsync(new PingRequest());
        var secondRequest = await core.ReadRequestAsync();

        await core.ReplyAsync(secondRequest, "pong", Pong);
        Assert.IsType<PongReply>(await second.WaitAsync(Step));
        Assert.False(first.IsCompleted);

        await core.ReplyAsync(firstRequest, "error", new { code = "not_found", message = "no such volume" });
        Assert.Equal(new ErrorReply("not_found", "no such volume"), await first.WaitAsync(Step));
    }

    [Fact]
    public async Task An_error_reply_throws_with_its_code_for_typed_requests()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        var volumes = client.RequestAsync<VolumesReply>(new ListVolumesRequest());
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request, "error", new { code = "unknown_request", message = "unknown request type `list_volumes`" });
        var error = await Assert.ThrowsAsync<CoreRequestException>(() => volumes.WaitAsync(Step));
        Assert.Equal(ErrorCodes.UnknownRequest, error.Code);
        Assert.Equal(request.GetProperty("id").GetString(), error.RequestId);
    }

    [Fact]
    public async Task Events_arrive_in_order_and_unknown_ones_are_skipped()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        await core.SendAsync("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"config_changed","changed":["ui.sidebar"]}""");
        await core.SendAsync("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4X","type":"a_future_event","x":1}""");
        await core.SendAsync("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4Y","type":"plugin_event","plugin_id":7,"name":"n","payload":"{}"}""");
        await core.SendAsync("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4Z","type":"listing_lost","listing_id":3,"message":"gone"}""");

        using var timeout = new CancellationTokenSource(Step);
        var changed = Assert.IsType<ConfigChangedEvent>(await client.Events.ReadAsync(timeout.Token));
        Assert.Equal(["ui.sidebar"], changed.Changed);
        Assert.Equal(new ListingLostEvent(3, "gone"), await client.Events.ReadAsync(timeout.Token));
    }

    [Fact]
    public async Task A_lost_connection_fails_waiting_requests_and_is_reported_once()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();
        var reasons = new List<string>();
        client.Disconnected += reasons.Add;

        var waiting = client.RequestAsync(new GetConfigRequest());
        await core.ReadRequestAsync();
        core.Disconnect();

        await Assert.ThrowsAsync<CoreDisconnectedException>(() => waiting.WaitAsync(Step));
        await client.Events.Completion.WaitAsync(Step);
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<CoreDisconnectedException>(() => client.RequestAsync(new PingRequest()).WaitAsync(Step));
        Assert.Single(reasons);
    }

    [Fact]
    public async Task A_section_handle_in_a_reply_is_owned_and_maps()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();
        var bytes = TestSections.Build([new SyntheticEntry(11, "one.txt", 1), new SyntheticEntry(12, "two", 2)]);
        var handle = TestSections.CreateSection(bytes);

        var listing = client.RequestAsync<ListingOpenedReply>(new ListDirectoryRequest(@"C:\fake") { Watch = true });
        var request = await core.ReadRequestAsync();
        Assert.True(request.GetProperty("watch").GetBoolean());
        await core.ReplyAsync(request, "listing_opened", new
        {
            listing_id = 5,
            section_handle = (long)handle,
            section_size = bytes.Length,
            entry_count = 2,
            generation = 1,
            elapsed_us = 10,
        });

        var opened = await listing.WaitAsync(Step);
        var view = ListingView.Open(opened.TakeSection()!, opened.SectionSize);
        Assert.Equal(2, view.Count);
        Assert.Equal("two", view.Name(1));
        Assert.Null(opened.TakeSection());
        view.Dispose();
        Assert.False(TestSections.IsOpen(handle));
    }

    [Fact]
    public async Task A_listing_nobody_waits_for_is_closed_on_both_sides()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();
        var bytes = TestSections.Build([new SyntheticEntry(1, "a", 1)]);
        var handle = TestSections.CreateSection(bytes);

        using var cancel = new CancellationTokenSource();
        var listing = client.RequestAsync(new ListDirectoryRequest(@"C:\slow"), cancel.Token);
        var request = await core.ReadRequestAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(Step));

        await core.ReplyAsync(request, "listing_opened", new
        {
            listing_id = 9,
            section_handle = (long)handle,
            section_size = bytes.Length,
            entry_count = 1,
            generation = 1,
            elapsed_us = 10,
        });

        var close = await core.ReadRequestAsync();
        Assert.Equal("close_listing", close.GetProperty("type").GetString());
        Assert.Equal(9, close.GetProperty("listing_id").GetInt32());
        Assert.False(TestSections.IsOpen(handle));
    }

    [Fact]
    public async Task A_reply_this_ui_does_not_understand_fails_only_its_request()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        var odd = client.RequestAsync(new PingRequest());
        await core.ReplyAsync(await core.ReadRequestAsync(), "pong_v2", new { extra = true });
        await Assert.ThrowsAsync<CoreProtocolException>(() => odd.WaitAsync(Step));

        var ping = client.RequestAsync(new PingRequest());
        await core.ReplyAsync(await core.ReadRequestAsync(), "pong", Pong);
        Assert.IsType<PongReply>(await ping.WaitAsync(Step));
    }

    [Fact]
    public async Task Closing_right_after_a_reply_on_the_ui_thread_closes_the_pipe_at_once()
    {
        await using var core = new FakeCore();
        using var ui = new UiThread();
        var client = await ui.RunAsync(core.ConnectClientAsync);

        var closed = ui.RunAsync(async () =>
        {
            await client.ShutdownCoreAsync(TimeSpan.FromSeconds(5));
            return true;
        });
        var shutdown = await core.ReadRequestAsync();
        Assert.Equal("shutdown", shutdown.GetProperty("type").GetString());
        await core.ReplyAsync(shutdown, "ok");
        Assert.True(await closed.WaitAsync(Step));

        // The core waits for its connections before it exits; the pipe must end now.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(async () => await core.ReadRequestAsync());
        Assert.True(watch.ElapsedMilliseconds < 1000, $"the pipe ended after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_frame_that_is_not_json_is_skipped_and_the_connection_goes_on()
    {
        await using var core = new FakeCore();
        await using var client = await core.ConnectClientAsync();

        await core.SendAsync("not json at all");
        var ping = client.RequestAsync(new PingRequest());
        await core.ReplyAsync(await core.ReadRequestAsync(), "pong", Pong);
        Assert.IsType<PongReply>(await ping.WaitAsync(Step));
        Assert.True(client.IsConnected);
    }
}
