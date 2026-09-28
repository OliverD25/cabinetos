using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CabinetOS.Core.Terminal;

namespace CabinetOS.Tests;

/// <summary>The terminal pane's parts that run without a window: the byte pump, the page protocol, the folder sync rule.</summary>
public class TerminalTests
{
    [Fact]
    public void Output_is_coalesced_into_one_message_per_flush_and_only_the_first_add_asks_for_one()
    {
        long now = 1000;
        var output = new OutputCoalescer(() => now);

        Assert.True(output.Add("hel"u8));
        Assert.False(output.Add("lo "u8));
        Assert.False(output.Add("wörld"u8));

        var chunks = output.Flush();
        Assert.Equal("hello wörld", Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(chunks))));
        Assert.False(output.HasPending);
        Assert.True(output.Add("again"u8));
    }

    [Fact]
    public void Flushes_are_at_least_16_ms_apart_so_the_page_gets_at_most_60_a_second()
    {
        long now = 1000;
        var output = new OutputCoalescer(() => now);
        Assert.Equal(0, output.MillisecondsUntilDue());
        output.Flush();

        now += 5;
        Assert.Equal(11, output.MillisecondsUntilDue());
        now += 11;
        Assert.Equal(0, output.MillisecondsUntilDue());
    }

    [Fact]
    public void A_big_burst_is_split_into_chunks_that_join_back_to_the_same_bytes()
    {
        var output = new OutputCoalescer(() => 0);
        var bytes = new byte[(OutputCoalescer.MaxChunkBytes * 2) + 1000];
        new Random(7).NextBytes(bytes);
        output.Add(bytes.AsSpan(0, 1000));
        output.Add(bytes.AsSpan(1000));

        var chunks = output.Flush();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(bytes, chunks.SelectMany(Convert.FromBase64String).ToArray());
    }

    [Fact]
    public async Task The_pipe_pumps_output_in_and_keys_out_and_says_when_it_ends()
    {
        var name = "cabinetos-uitest-term-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        var output = new OutputCoalescer(() => Environment.TickCount64);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = await TerminalPipe.ConnectAsync($@"\\.\pipe\{name}", output, TimeSpan.FromSeconds(5));
        pipe.OutputWaiting += () => waiting.TrySetResult();
        pipe.Ended += () => ended.TrySetResult();
        await accept.WaitAsync(TimeSpan.FromSeconds(5));

        await server.WriteAsync("PS C:\\> "u8.ToArray()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PS C:\\> ", Encoding.UTF8.GetString(output.Flush().SelectMany(Convert.FromBase64String).ToArray()));

        // The pipe has no buffer: a write completes when the other end reads, so the read starts first.
        var typed = new byte[4];
        var read = server.ReadExactlyAsync(typed).AsTask();
        await pipe.WriteAsync("dir\r"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("dir\r", Encoding.UTF8.GetString(typed));

        server.Disconnect();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pipe.IsOpen);
    }

    [Fact]
    public void A_half_typed_line_or_a_full_screen_program_stops_the_folder_sync()
    {
        var typing = new TypingTracker();
        Assert.Equal(CwdSyncDecision.Sync, CwdSyncRule.Decide(@"D:\docs", @"C:\Users\me", running: true, typing));

        typing.OnInput("git sta");
        Assert.Equal(CwdSyncDecision.SkipTyping, CwdSyncRule.Decide(@"D:\docs", @"C:\Users\me", true, typing));
        typing.OnInput("tus\r");
        Assert.Equal(CwdSyncDecision.Sync, CwdSyncRule.Decide(@"D:\docs", @"C:\Users\me", true, typing));
        typing.OnInput("oops");
        typing.OnInput("\u0003");
        Assert.False(typing.LinePending);

        // Focus reports come from the terminal, not from the user's keys.
        typing.OnInput("\u001b[I");
        Assert.False(typing.LinePending);
        // The up arrow brings back a command: that is a line on the prompt.
        typing.OnInput("\u001b[A");
        Assert.True(typing.LinePending);
        typing.OnSynced();

        typing.FullScreen = true;
        Assert.Equal(CwdSyncDecision.SkipFullScreen, CwdSyncRule.Decide(@"D:\docs", null, true, typing));
        Assert.Equal(CwdSyncDecision.SkipSameFolder, CwdSyncRule.Decide(@"D:\docs\", @"d:\DOCS", true, typing));
        Assert.Equal(CwdSyncDecision.SkipNotRunning, CwdSyncRule.Decide(@"D:\docs", null, false, typing));
    }

    [Fact]
    public void The_debouncer_hands_out_the_last_value_once_the_user_stopped()
    {
        long now = 0;
        var debouncer = new Debouncer<string>(() => now, 300);
        Assert.Equal(-1, debouncer.MillisecondsUntilDue());

        debouncer.Set(@"C:\a");
        now = 200;
        debouncer.Set(@"C:\b");
        now = 450;
        Assert.False(debouncer.TryTake(out _));
        Assert.Equal(50, debouncer.MillisecondsUntilDue());
        now = 500;
        Assert.True(debouncer.TryTake(out var folder));
        Assert.Equal(@"C:\b", folder);
        Assert.False(debouncer.HasPending);
    }

    [Theory]
    [InlineData("""{"type":"ready"}""", "ready")]
    [InlineData("""{"type":"input","session":3,"data":"ls\r"}""", "input")]
    [InlineData("""{"type":"binary","session":3,"data":"G1tN"}""", "binary")]
    [InlineData("""{"type":"resize","session":3,"cols":120,"rows":30}""", "resize")]
    [InlineData("""{"type":"buffer","session":3,"alternate":true}""", "buffer")]
    [InlineData("""{"type":"key","keys":"ctrl+backquote"}""", "key")]
    public void The_page_messages_the_window_takes(string json, string type) =>
        Assert.Equal(type, TerminalPageMessages.Parse(json)?.Type);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""["input"]""")]
    [InlineData("""{"type":"input","data":"no session"}""")]
    [InlineData("""{"type":"resize","session":3,"cols":0,"rows":30}""")]
    [InlineData("""{"type":"resize","session":3,"cols":40000,"rows":30}""")]
    [InlineData("""{"type":"launch","session":3}""")]
    public void Malformed_or_unknown_page_messages_are_dropped(string json) =>
        Assert.Null(TerminalPageMessages.Parse(json));

    [Fact]
    public void Keys_reach_the_shell_as_bytes_and_output_reaches_the_page_as_base64()
    {
        Assert.Equal("ls\r"u8.ToArray(), TerminalPageMessages.Bytes(TerminalPageMessages.Parse("""{"type":"input","session":3,"data":"ls\r"}""")!));
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x4D }, TerminalPageMessages.Bytes(TerminalPageMessages.Parse("""{"type":"binary","session":3,"data":"G1tN"}""")!));

        using var output = JsonDocument.Parse(TerminalPageMessages.Output(3, "aGk="));
        Assert.Equal(("output", 3UL, "aGk="), (output.RootElement.GetProperty("type").GetString(), output.RootElement.GetProperty("session").GetUInt64(), output.RootElement.GetProperty("data").GetString()));
        using var keys = JsonDocument.Parse(TerminalPageMessages.PassKeys(["ctrl+shift+p", "ctrl+backquote"]));
        Assert.Equal(2, keys.RootElement.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public void The_profiles_come_from_the_config_with_the_documented_defaults()
    {
        using var config = JsonDocument.Parse("""{"terminal":{"defaultProfile":"cmd","profiles":[{"name":"pwsh"},{"name":"cmd"},{"command":"x"}]}}""");
        var profiles = TerminalProfiles.FromConfig(config.RootElement);
        Assert.Equal("cmd", profiles.DefaultProfile);
        Assert.Equal(["pwsh", "cmd"], profiles.Names);
        using var empty = JsonDocument.Parse("{}");
        Assert.Same(TerminalProfiles.Defaults, TerminalProfiles.FromConfig(empty.RootElement));
    }
}
