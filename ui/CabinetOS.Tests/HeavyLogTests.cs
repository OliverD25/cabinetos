using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>Tests that set up the process's own log (<see cref="Diag.Init"/>) run alone: it is one static writer.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagStateTests
{
    public const string Name = "the process's log";
}

/// <summary>
/// Heavy logging mode in the window (docs/diagnostics.md, "Heavy mode"): the heavy file and its
/// queue, the masking, the switch, the pill and what the window records in it.
/// </summary>
public class HeavyLogTests
{
    private static readonly DateTime At = new(2026, 9, 28, 1, 2, 3, 4, DateTimeKind.Utc);

    private const string Trace = "01J9ZQ4X7K3M5N8P2R6S0T1V4V";
    private const string Request = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";

    // The limits of the real thing, made small so a test can reach them.
    private static HeavyLimits Limits(long queue = 1L << 20, long file = 1L << 30, long disk = long.MaxValue, long check = long.MaxValue) =>
        new(queue, NeverWaitExtraBytes: 1024, file, disk, check);

    // The heavy file while its writer may still have it open: reading must not need exclusive access.
    private static List<JsonElement> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }
        return lines;
    }

    private static string Text(JsonElement line, string name) => line.GetProperty(name).GetString()!;

    private static string HeavyPath(string dir, string name = "heavy-ui.2026-09-28.jsonl") => Path.Combine(dir, name);

    // ----- Masking -----

    [Fact]
    public void A_secret_message_s_value_is_masked_and_other_values_stay()
    {
        Assert.Equal(
            """{"id":"01M","type":"secret_set","name":"openai","value":"***"}""",
            LogMask.MaskedJson("""{"id":"01M","type":"secret_set","name":"openai","value":"sk-123"}"""u8).Text);
        Assert.Equal(
            """{"id":"01M","type":"secret","value":"***"}""",
            LogMask.MaskedJson("""{"id":"01M","type":"secret","value":"sk-123"}"""u8).Text);
        Assert.Equal(
            """{"type":"set_value","path":"ui.theme","value":"nord"}""",
            LogMask.MaskedJson("""{"type":"set_value","path":"ui.theme","value":"nord"}"""u8).Text);
    }

    [Fact]
    public void Fields_named_like_secrets_are_masked_at_any_depth_and_null_stays_null()
    {
        Assert.Equal(
            """{"args":{"Secret":"***","nested":[{"api_key":"***","keep":1}]},"token":null}""",
            LogMask.MaskedJson("""{"args":{"Secret":"s","nested":[{"api_key":"k","keep":1}]},"token":null}"""u8).Text);
        foreach (var name in new[] { "secret", "password", "passphrase", "api_key", "apikey", "token", "access_token", "refresh_token", "client_secret", "authorization", "x-api-key" })
        {
            Assert.Equal($$"""{"{{name}}":"***"}""", LogMask.MaskedJson(System.Text.Encoding.UTF8.GetBytes($$"""{"{{name}}":"v"}""")).Text);
        }
    }

    [Fact]
    public void Secret_headers_are_masked_in_every_shape()
    {
        Assert.Equal(
            """{"headers":{"Authorization":"***","Accept":"text/plain"}}""",
            LogMask.MaskedJson("""{"headers":{"Authorization":"Bearer x","Accept":"text/plain"}}"""u8).Text);
        Assert.Equal(
            """{"headers":[["x-api-key","***"],["accept","a"]]}""",
            LogMask.MaskedJson("""{"headers":[["x-api-key","k"],["accept","a"]]}"""u8).Text);
        Assert.Equal(
            """{"headers":[{"name":"Cookie","value":"***"},{"name":"Host","value":"h"}]}""",
            LogMask.MaskedJson("""{"headers":[{"name":"Cookie","value":"sid=1"},{"name":"Host","value":"h"}]}"""u8).Text);
        Assert.Equal(
            """{"headers":{"Proxy-Authorization":"***"}}""",
            LogMask.MaskedJson("""{"headers":{"Proxy-Authorization":"Basic y"}}"""u8).Text);
    }

    [Fact]
    public void A_long_payload_is_cut_at_a_character_boundary_and_marked()
    {
        var (text, cut) = LogMask.MaskedJson(System.Text.Encoding.UTF8.GetBytes($$"""{"a":"{{new string('ж', 100)}}"}"""), 20);
        Assert.True(cut);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 20);
        Assert.StartsWith("""{"a":"ж""", text);
        Assert.DoesNotContain('\uFFFD', text);

        var (whole, notCut) = LogMask.MaskedJson("""{"a":1}"""u8);
        Assert.False(notCut);
        Assert.Equal("""{"a":1}""", whole);

        var fields = Diag.PayloadFields(System.Text.Encoding.UTF8.GetBytes($$"""{"a":"{{new string('x', LogMask.PayloadCap)}}"}"""));
        Assert.Equal(["payload", "truncated"], fields.Select(f => f.Name));
        Assert.Equal(LogMask.PayloadCap, ((string)fields[0].Value!).Length);
        Assert.Equal(["payload"], Diag.PayloadFields("""{"a":1}"""u8).Select(f => f.Name));
    }

    [Fact]
    public void Cabinetos_variables_that_look_like_keys_are_secrets()
    {
        Assert.True(LogMask.IsSecretEnvironmentVariable("CABINETOS_OPENAI_API_KEY"));
        Assert.True(LogMask.IsSecretEnvironmentVariable("cabinetos_github_token"));
        Assert.False(LogMask.IsSecretEnvironmentVariable("CABINETOS_LOG_DIR"));
        Assert.False(LogMask.IsSecretEnvironmentVariable("OPENAI_API_KEY"), "only the CabinetOS variables");
    }

    [Fact]
    public void Bytes_that_are_not_json_are_not_written_because_a_secret_in_them_could_not_be_found()
    {
        var (text, _) = LogMask.MaskedJson("password=hunter2"u8);
        Assert.DoesNotContain("hunter2", text);
    }

    // ----- The heavy file -----

    [Fact]
    public void Heavy_mode_writes_every_level_into_its_own_file_and_the_normal_file_keeps_its_filter()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits()))
            {
                writer.Write(LogLevel.Trace, "cabinetos_ui::test", "before, off");
                Assert.False(writer.HeavyEnabled);
                Assert.True(writer.SetHeavy(true));
                Assert.False(writer.SetHeavy(true), "already on");
                writer.Write(LogLevel.Trace, "cabinetos_ui::test", "a trace", Request, "request", traceId: Trace);
                writer.Write(LogLevel.Debug, "cabinetos_ui::test", "a debug");
                writer.Write(LogLevel.Info, "cabinetos_ui::test", "an info");
                writer.Write(LogLevel.Debug, "heavy::keys", "a heavy-only line");
                Assert.True(writer.SetHeavy(false));
                writer.Write(LogLevel.Trace, "cabinetos_ui::test", "after, off");
                writer.Write(LogLevel.Info, "cabinetos_ui::test", "after, info");
                writer.Write(LogLevel.Debug, "heavy::keys", "after, heavy-only");
                Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
            }

            var normal = ReadLines(Path.Combine(dir, "ui.2026-09-28.jsonl")).Select(l => Text(l, "message")).ToList();
            Assert.Equal(["heavy logging is on", "an info", "heavy logging is off", "after, info"], normal);

            var heavy = ReadLines(HeavyPath(dir));
            Assert.Equal(
                ["heavy logging is on", "a trace", "a debug", "an info", "a heavy-only line", "heavy logging is off"],
                heavy.Select(l => Text(l, "message")));
            Assert.Equal("TRACE", Text(heavy[1], "level"));
            Assert.Equal(Trace, Text(heavy[1], "trace_id"));
            Assert.Equal(Request, Text(heavy[1], "request_id"));
            Assert.Equal("heavy::keys", Text(heavy[4], "target"));
            var on = heavy[0].GetProperty("fields");
            Assert.Equal(dir, on.GetProperty("dir").GetString());
            Assert.Equal(1L << 20, on.GetProperty("queue_cap_bytes").GetInt64());
            Assert.Equal(long.MaxValue, on.GetProperty("disk_cap_bytes").GetInt64());
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_real_limits_are_the_documented_ones()
    {
        var limits = HeavyLimits.Default;
        Assert.Equal(64L * 1024 * 1024, limits.QueueBytes);
        Assert.Equal(256L * 1024 * 1024, limits.FileBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, limits.DiskBytes);
        Assert.Equal(64L * 1024 * 1024, limits.CheckEveryBytes);
    }

    [Fact]
    public void An_open_heavy_file_cannot_be_deleted_and_going_off_closes_it()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits());
            writer.SetHeavy(true);
            Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
            var path = HeavyPath(dir);
            Assert.Equal(path, writer.HeavyFilePath);
            // No delete sharing: another process's cap cannot delete the file this one writes.
            Assert.ThrowsAny<Exception>(() => File.Delete(path));
            Assert.True(File.Exists(path));

            writer.SetHeavy(false);
            Assert.Null(writer.HeavyFilePath);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_thread_that_may_wait_waits_for_a_slow_writer_and_no_line_is_lost()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(queue: 2048)))
            {
                writer.BeforeHeavyWrite = _ => Thread.Sleep(5);
                writer.SetHeavy(true);
                var pad = new string('x', 90);
                for (var n = 0; n < 400; n++)
                {
                    writer.Write(LogLevel.Trace, "cabinetos_ui::test", $"line {n:000} {pad}", Request, "request", traceId: Trace);
                }
                writer.SetHeavy(false);
            }

            var lines = ReadLines(HeavyPath(dir));
            var numbered = lines.Select(l => Text(l, "message")).Where(m => m.StartsWith("line ", StringComparison.Ordinal)).ToList();
            Assert.Equal(Enumerable.Range(0, 400).Select(n => $"line {n:000} "), numbered.Select(m => m[..9]));
            var waits = lines.Where(l => Text(l, "message") == "heavy log waited").ToList();
            Assert.NotEmpty(waits);
            Assert.All(waits, wait => Assert.True(wait.GetProperty("fields").GetProperty("waited_ms").GetInt64() >= 0));
            // The wait is written under the trace and the request of the operation that waited (the
            // last line, "heavy logging is off", belongs to no action).
            var ofTheAction = waits.Where(w => w.TryGetProperty("trace_id", out _)).ToList();
            Assert.NotEmpty(ofTheAction);
            Assert.All(ofTheAction, wait =>
            {
                Assert.Equal(Trace, Text(wait, "trace_id"));
                Assert.Equal(Request, Text(wait, "request_id"));
            });
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_thread_that_never_waits_drops_and_counts_and_the_count_is_written()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var gate = new ManualResetEventSlim();
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(queue: 1000)))
            {
                // The writer is stuck until the test lets it go, so the queue fills up.
                writer.BeforeHeavyWrite = _ => gate.Wait(TimeSpan.FromSeconds(10));
                writer.SetHeavy(true);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var ui = new Thread(() =>
                {
                    LogWriter.NeverWaitForHeavyLog();
                    for (var n = 0; n < 100; n++)
                    {
                        writer.Write(LogLevel.Debug, "cabinetos_ui::test", $"ui line {n:000}");
                    }
                }) { Name = "ui" };
                ui.Start();
                Assert.True(ui.Join(TimeSpan.FromSeconds(5)), "the UI thread never waited for the writer");
                Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));

                var lost = writer.HeavyLostLines;
                Assert.InRange(lost, 1, 99);
                gate.Set();
                writer.SetHeavy(false);

                var lines = ReadLines(HeavyPath(dir));
                var kept = lines.Count(l => Text(l, "message").StartsWith("ui line ", StringComparison.Ordinal));
                var notes = lines.Where(l => Text(l, "message") == "heavy log dropped lines of threads that never wait").ToList();
                Assert.NotEmpty(notes);
                Assert.Equal(lost, notes.Sum(n => n.GetProperty("fields").GetProperty("dropped").GetInt64()));
                Assert.Equal(100, kept + lost);
            }
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_never_wait_scope_ends_and_the_thread_waits_again()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var gate = new ManualResetEventSlim();
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(queue: 300)))
            {
                writer.SetHeavy(true);
                Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
                // The writer is stuck from now on, so the queue keeps what it gets.
                writer.BeforeHeavyWrite = _ => gate.Wait(TimeSpan.FromSeconds(10));
                var pad = new string('x', 200);
                writer.Write(LogLevel.Debug, "cabinetos_ui::test", "fills the queue " + pad);
                using (LogWriter.NeverWait())
                {
                    // Over the cap and over the never-wait extra: dropped at once, not waited for.
                    var before = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (var n = 0; n < 20; n++)
                    {
                        writer.Write(LogLevel.Debug, "cabinetos_ui::test", "in the scope " + pad);
                    }
                    Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(before) < TimeSpan.FromSeconds(3));
                }
                Assert.True(writer.HeavyLostLines > 0);

                var release = new Thread(() =>
                {
                    Thread.Sleep(300);
                    gate.Set();
                });
                release.Start();
                var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                writer.Write(LogLevel.Debug, "cabinetos_ui::test", "after the scope " + pad);
                Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted) >= TimeSpan.FromMilliseconds(200), "outside the scope the thread waits");
                release.Join();
                writer.SetHeavy(false);
                Assert.Contains(ReadLines(HeavyPath(dir)), l => Text(l, "message").StartsWith("after the scope", StringComparison.Ordinal));
            }
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_full_heavy_file_continues_in_the_next_part()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(file: 600)))
            {
                writer.SetHeavy(true);
                for (var n = 0; n < 6; n++)
                {
                    writer.Write(LogLevel.Debug, "cabinetos_ui::test", $"part test {n} " + new string('x', 250));
                    // One batch per line, so the size check sees each one.
                    Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
                }
                writer.SetHeavy(false);
            }
            var names = Directory.GetFiles(dir, "heavy-ui.*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Contains("heavy-ui.2026-09-28.jsonl", names);
            Assert.Contains("heavy-ui.2026-09-28.1.jsonl", names);
            Assert.True(names.Count >= 3, string.Join(", ", names));
            var all = names.SelectMany(name => ReadLines(Path.Combine(dir, name!))).Select(l => Text(l, "message")).Where(m => m.StartsWith("part test", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, all.Count);
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_oldest_heavy_files_of_every_process_go_first_and_the_open_one_stays()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            void Make(string name, int size) => File.WriteAllBytes(Path.Combine(dir, name), new byte[size]);
            Make("heavy-core.2026-09-20.jsonl", 400);
            Make("heavy-ui.2026-09-21.jsonl", 300);
            Make("heavy-core.2026-09-21.jsonl", 300);
            Make("heavy-core.2026-09-21.1.jsonl", 300);
            Make("heavy-core.2026-09-22.jsonl", 500);
            Make("core.2026-09-20.jsonl", 5000);
            Make("crash-20260920T000000000Z.json", 5000);
            var current = Path.Combine(dir, "heavy-core.2026-09-22.jsonl");

            var deleted = HeavyFiles.EnforceDiskCap(dir, 900, current);

            Assert.Equal(["heavy-core.2026-09-20.jsonl", "heavy-core.2026-09-21.jsonl", "heavy-ui.2026-09-21.jsonl"], deleted.Select(d => d.Name));
            foreach (var kept in new[] { "heavy-core.2026-09-21.1.jsonl", "heavy-core.2026-09-22.jsonl", "core.2026-09-20.jsonl", "crash-20260920T000000000Z.json" })
            {
                Assert.True(File.Exists(Path.Combine(dir, kept)), kept);
            }
            Assert.Empty(HeavyFiles.EnforceDiskCap(dir, 900, current));

            // Even the only file left is not deleted while it is written.
            Assert.Single(HeavyFiles.EnforceDiskCap(dir, 10, current));
            Assert.True(File.Exists(current));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_new_heavy_file_keeps_the_folder_under_its_cap_and_the_normal_log_says_what_went()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "heavy-core.2026-09-20.jsonl"), new byte[600]);
            File.WriteAllBytes(Path.Combine(dir, "heavy-ui.2026-09-21.jsonl"), new byte[600]);
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(disk: 1000)))
            {
                writer.SetHeavy(true);
                writer.Write(LogLevel.Debug, "cabinetos_ui::test", "the first line of today");
                writer.SetHeavy(false);
            }
            Assert.False(File.Exists(Path.Combine(dir, "heavy-core.2026-09-20.jsonl")));
            Assert.True(File.Exists(Path.Combine(dir, "heavy-ui.2026-09-21.jsonl")));
            var note = ReadLines(Path.Combine(dir, "ui.2026-09-28.jsonl")).Single(l => Text(l, "message").StartsWith("heavy log files deleted", StringComparison.Ordinal));
            var fields = note.GetProperty("fields");
            Assert.Equal("heavy-core.2026-09-20.jsonl", fields.GetProperty("deleted").GetString());
            Assert.Equal(600, fields.GetProperty("freed_bytes").GetInt64());
            Assert.Equal(1000, fields.GetProperty("cap_bytes").GetInt64());
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    // ----- The switch and the pill -----

    [Fact]
    public void The_switch_follows_logging_heavy_and_the_pill_follows_the_switch()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits());
            var heavy = new HeavyLogSwitch(writer, fromEnvironment: false);
            Assert.False(heavy.Pill.Visible);

            var configured = UiSettings.FromConfig(JsonDocument.Parse("""{"logging":{"level":"info","heavy":true}}""").RootElement).HeavyLogging;
            Assert.True(configured);
            Assert.Equal(HeavyLogChange.TurnedOn, heavy.Follow(configured));
            Assert.True(heavy.IsOn);
            Assert.True(heavy.Pill.Visible);
            Assert.Equal("HEAVY LOG", heavy.Pill.Text);
            Assert.Contains("Click to turn it off", heavy.Pill.ToolTip);
            Assert.Equal(HeavyLogChange.None, heavy.Follow(true));

            Assert.Equal(HeavyLogChange.TurnedOff, heavy.Follow(false));
            Assert.False(heavy.Pill.Visible);
            Assert.Equal(HeavyLogChange.None, heavy.Follow(false));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_environment_decides_over_the_configuration()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits());
            writer.SetHeavy(true);
            var heavy = new HeavyLogSwitch(writer, fromEnvironment: true);
            Assert.Equal(HeavyLogChange.None, heavy.Follow(false));
            Assert.True(heavy.IsOn);
            Assert.Contains("CABINETOS_LOG_HEAVY", heavy.Pill.ToolTip);
            Assert.DoesNotContain("Click to turn it off", heavy.Pill.ToolTip);
            Assert.Equal(HeavyLogChange.None, new HeavyLogSwitch(null, fromEnvironment: false).Follow(true));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Theory]
    [InlineData(false, 0, false, "")]
    [InlineData(false, 9, false, "")]
    [InlineData(true, 0, true, "HEAVY LOG")]
    [InlineData(true, 1, true, "HEAVY LOG, 1 line lost")]
    [InlineData(true, 1204, true, "HEAVY LOG, 1,204 lines lost")]
    public void The_pill_says_how_many_lines_the_window_lost(bool on, long lost, bool visible, string text)
    {
        var pill = HeavyPillState.For(on, lost);
        Assert.Equal(visible, pill.Visible);
        Assert.Equal(text, pill.Text);
    }

    [Fact]
    public void The_pill_counts_the_lines_a_full_queue_dropped_since_heavy_mode_came_on()
    {
        var dir = Repo.NewTempFolder("heavy");
        try
        {
            using var gate = new ManualResetEventSlim();
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At, Limits(queue: 400));
            writer.BeforeHeavyWrite = _ => gate.Wait(TimeSpan.FromSeconds(10));
            var heavy = new HeavyLogSwitch(writer, fromEnvironment: false);
            heavy.Follow(true);
            var ui = new Thread(() =>
            {
                LogWriter.NeverWaitForHeavyLog();
                for (var n = 0; n < 50; n++)
                {
                    writer.Write(LogLevel.Debug, "cabinetos_ui::test", "lines " + new string('x', 100));
                }
            });
            ui.Start();
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
            Assert.Matches(@"^HEAVY LOG, \d+ lines lost$", heavy.Pill.Text);
            gate.Set();
            heavy.Follow(false);

            // A new run of heavy mode starts counting again.
            heavy.Follow(true);
            Assert.Equal("HEAVY LOG", heavy.Pill.Text);
            heavy.Follow(false);
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData(" ", null, null)]
    [InlineData("1", true, null)]
    [InlineData("true", true, null)]
    [InlineData("0", false, null)]
    [InlineData("false", false, null)]
    [InlineData("yes", null, "yes")]
    public void The_environment_variable_is_one_or_zero(string? text, bool? expected, string? invalid)
    {
        Assert.Equal(expected, Diag.ParseHeavy(text, out var bad));
        Assert.Equal(invalid, bad);
    }

    [Fact]
    public void Logging_heavy_is_read_from_the_configuration_and_false_when_it_is_missing_or_wrong()
    {
        Assert.False(UiSettings.FromConfig(JsonDocument.Parse("{}").RootElement).HeavyLogging);
        Assert.False(UiSettings.FromConfig(JsonDocument.Parse("""{"logging":{"heavy":"yes"}}""").RootElement).HeavyLogging);
        Assert.False(UiSettings.FromConfig(JsonDocument.Parse("""{"logging":{"heavy":false}}""").RootElement).HeavyLogging);
        Assert.True(UiSettings.FromConfig(JsonDocument.Parse("""{"logging":{"heavy":true}}""").RootElement).HeavyLogging);
    }

    // ----- Keys -----

    [Theory]
    // Typed characters: a text box logs that the user typed, not what.
    [InlineData(0x41, KeyModifiers.None, true, "text input", null, "")]
    [InlineData(0x41, KeyModifiers.Shift, true, "text input", null, "shift")]
    [InlineData(0x35, KeyModifiers.None, true, "text input", null, "")]
    [InlineData(0x20, KeyModifiers.None, true, "text input", null, "")]
    [InlineData(0xBE, KeyModifiers.None, true, "text input", null, "")]
    [InlineData(0x41, KeyModifiers.Ctrl | KeyModifiers.Alt, true, "text input", null, "ctrl+alt")]
    // Keys that are no characters, and shortcuts, keep their names in a box.
    [InlineData(0x0D, KeyModifiers.None, true, "key pressed", "enter", "")]
    [InlineData(0x1B, KeyModifiers.None, true, "key pressed", "escape", "")]
    [InlineData(0x25, KeyModifiers.None, true, "key pressed", "left", "")]
    [InlineData(0x71, KeyModifiers.None, true, "key pressed", "f2", "")]
    [InlineData(0x41, KeyModifiers.Ctrl, true, "key pressed", "a", "ctrl")]
    [InlineData(0x56, KeyModifiers.Ctrl | KeyModifiers.Shift, true, "key pressed", "v", "ctrl+shift")]
    [InlineData(0x41, KeyModifiers.Alt, true, "key pressed", "a", "alt")]
    [InlineData(0x41, KeyModifiers.Win, true, "key pressed", "a", "win")]
    // Outside a text box every key has its name.
    [InlineData(0x41, KeyModifiers.None, false, "key pressed", "a", "")]
    [InlineData(0x74, KeyModifiers.None, false, "key pressed", "f5", "")]
    // Modifiers alone, and a key the grammar has no name for.
    [InlineData(0x11, KeyModifiers.Ctrl, true, "key pressed", "ctrl", "ctrl")]
    [InlineData(0xA0, KeyModifiers.Shift, false, "key pressed", "shift", "shift")]
    [InlineData(0xAD, KeyModifiers.None, true, "key pressed", "vk_AD", "")]
    public void A_key_is_logged_by_name_but_the_text_typed_into_a_box_never_is(int virtualKey, KeyModifiers modifiers, bool inTextBox, string message, string? key, string held)
    {
        var entry = KeyLog.Describe(virtualKey, modifiers, inTextBox);
        Assert.Equal(message, entry.Message);
        Assert.Equal(key, entry.Key);
        Assert.Equal(held, entry.Modifiers);
    }

    // ----- Page messages -----

    [Fact]
    public void A_page_message_is_logged_by_its_name_and_size_never_by_its_content()
    {
        Assert.Equal("output", Diag.PageMessageName("""{"type":"output","session":3,"data":"c2VjcmV0"}"""));
        Assert.Equal("?", Diag.PageMessageName("""{"session":3}"""));
        Assert.Equal("?", Diag.PageMessageName("not json"));
        Assert.Equal("?", Diag.PageMessageName("[1,2]"));
    }

    // ----- The switch, through the real core -----

    [Collection(HandleTests.Name)]
    public class WithTheRealCore
    {
        [Fact]
        public async Task Setting_logging_heavy_through_the_core_turns_heavy_mode_on_in_the_core_and_the_window_and_off_again()
        {
            var coreExe = EndToEndTests.FindCoreOrSkip();
            var root = Repo.NewTempFolder("e2e-heavy");
            try
            {
                var logs = Path.Combine(root, "logs");
                // The window's own log, in the folder the core writes to: one folder, one cap for both.
                using var window = new LogWriter(logs, LogFilter.Default, "ui", "0.1.0");
                var heavy = new HeavyLogSwitch(window, fromEnvironment: false);
                await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
                var client = core.Client;
                await client.HelloAsync();
                var settings = new SettingsWriter(client);
                Assert.False(UiSettings.FromConfig((await client.RequestAsync<ConfigReply>(new GetConfigRequest())).Config).HeavyLogging);

                // What "Diagnostics: Toggle Heavy Logging" does: the core writes the setting, and the window
                // follows what the core reads back, as it does on config_changed.
                Assert.True(await settings.SetAsync("logging.heavy", true));
                var configured = UiSettings.FromConfig((await client.RequestAsync<ConfigReply>(new GetConfigRequest())).Config).HeavyLogging;
                Assert.True(configured);
                Assert.Equal(HeavyLogChange.TurnedOn, heavy.Follow(configured));
                Assert.True(heavy.Pill.Visible);

                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var coreHeavy = Path.Combine(logs, $"heavy-core.{today}.jsonl");
                var windowHeavy = Path.Combine(logs, $"heavy-ui.{today}.jsonl");
                await UntilAsync(() => File.Exists(coreHeavy), "the core's heavy file");
                window.Write(LogLevel.Trace, "cabinetos_ui::test", "a window line at trace level");
                Assert.True(window.Flush(TimeSpan.FromSeconds(5)));
                Assert.True(File.Exists(windowHeavy));

                Assert.True(await settings.SetAsync("logging.heavy", false));
                Assert.Equal(HeavyLogChange.TurnedOff, heavy.Follow(false));
                await UntilAsync(() => LastMessage(coreHeavy) == "heavy logging is off", "the core's heavy file to end");

                var coreLines = ReadLines(coreHeavy).Select(l => Text(l, "message")).ToList();
                Assert.Equal("heavy logging is on", coreLines[0]);
                Assert.Contains("request payload", coreLines);
                Assert.Equal("heavy logging is off", coreLines[^1]);
                var windowLines = ReadLines(windowHeavy).Select(l => Text(l, "message")).ToList();
                Assert.Equal(["heavy logging is on", "a window line at trace level", "heavy logging is off"], windowLines);
            }
            finally
            {
                Repo.RemoveTempFolder(root);
            }
        }

        // The last line of a file another process is still writing; null while it cannot be read whole.
        private static string? LastMessage(string path)
        {
            try
            {
                return ReadLines(path).Select(l => Text(l, "message")).LastOrDefault();
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                return null;
            }
        }

        private static async Task UntilAsync(Func<bool> condition, string what)
        {
            for (var waited = 0; waited < 10_000; waited += 100)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail($"timed out waiting for {what}");
        }
    }

    // ----- What the window's log carries, through the real client and router -----

    [Collection(DiagStateTests.Name)]
    public class ThroughTheProcessLog
    {
        private static readonly object Pong = new { protocol_version = 13, core_version = "0.1.0" };

        [Fact]
        public async Task A_command_and_its_requests_are_logged_with_payloads_masked_under_the_action_s_trace()
        {
            var dir = Repo.NewTempFolder("heavy");
            try
            {
                Diag.Init("0.1.0", dir);
                Assert.True(Diag.Writer!.SetHeavy(true));
                await using var core = new FakeCore();
                await using var client = await core.ConnectClientAsync();
                var router = new CommandRouter(client);
                router.RegisterLocal("test.ping", async _ =>
                {
                    await client.RequestAsync(new PingRequest());
                });

                var args = JsonDocument.Parse("""{"path":"C:\\x","token":"sk-live-1","nested":{"password":"hunter2"}}""").RootElement.Clone();
                var run = router.ExecuteAsync("test.ping", args, "key", traceId: Trace);
                var request = await core.ReadRequestAsync();
                await core.ReplyAsync(request, "pong", Pong);
                var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
                Assert.Equal(Trace, outcome.RequestId);
                Assert.Equal(Trace, request.GetProperty("trace").GetString());
                Diag.Writer!.SetHeavy(false);
                var heavy = ReadLines(HeavyPath(dir, $"heavy-ui.{DateTime.UtcNow:yyyy-MM-dd}.jsonl"));

                var commandRun = heavy.Single(l => Text(l, "message") == "command run");
                Assert.Equal("heavy::commands", Text(commandRun, "target"));
                Assert.Equal(Trace, Text(commandRun, "trace_id"));
                var fields = commandRun.GetProperty("fields");
                Assert.Equal("test.ping", fields.GetProperty("command").GetString());
                Assert.Equal("unlisted", fields.GetProperty("source").GetString());
                Assert.Equal("ui", fields.GetProperty("target").GetString());
                Assert.Equal("key", fields.GetProperty("trigger").GetString());
                var runArgs = fields.GetProperty("args").GetString()!;
                Assert.Contains("C:\\\\x", runArgs);
                Assert.Contains("\"token\":\"***\"", runArgs);
                Assert.DoesNotContain("sk-live-1", runArgs);
                Assert.DoesNotContain("hunter2", runArgs);

                var sent = heavy.Single(l => Text(l, "message") == "request payload");
                Assert.Equal("heavy::pipe", Text(sent, "target"));
                Assert.Equal(Trace, Text(sent, "trace_id"));
                Assert.Equal(request.GetProperty("id").GetString(), Text(sent, "request_id"));
                Assert.Contains("\"type\":\"ping\"", sent.GetProperty("fields").GetProperty("payload").GetString());

                var replied = heavy.Single(l => Text(l, "message") == "reply payload");
                Assert.Equal(Trace, Text(replied, "trace_id"));
                Assert.Contains("\"type\":\"pong\"", replied.GetProperty("fields").GetProperty("payload").GetString());

                var done = heavy.Single(l => Text(l, "message") == "command done");
                Assert.Equal("RanInUi", done.GetProperty("fields").GetProperty("outcome").GetString());
                int Position(string message) => heavy.FindIndex(l => Text(l, "message") == message);
                Assert.True(Position("command run") < Position("request payload"));
                Assert.True(Position("reply payload") < Position("command done"));

                // Nothing of it reached the normal file: its lines are the same as without heavy mode.
                var normal = ReadLines(Path.Combine(dir, $"ui.{DateTime.UtcNow:yyyy-MM-dd}.jsonl"));
                Assert.DoesNotContain(normal, l => Text(l, "target").StartsWith("heavy::", StringComparison.Ordinal));
                Assert.DoesNotContain(normal, l => l.GetRawText().Contains("sk-live-1", StringComparison.Ordinal));
            }
            finally
            {
                Diag.Shutdown();
                Repo.RemoveTempFolder(dir);
            }
        }

        [Fact]
        public async Task Without_heavy_mode_nothing_of_it_is_written()
        {
            var dir = Repo.NewTempFolder("heavy");
            try
            {
                Diag.Init("0.1.0", dir);
                Assert.False(Diag.HeavyEnabled);
                await using var core = new FakeCore();
                await using var client = await core.ConnectClientAsync();
                var pinging = client.RequestAsync(new PingRequest());
                await core.ReplyAsync(await core.ReadRequestAsync(), "pong", Pong);
                await pinging.WaitAsync(TimeSpan.FromSeconds(10));
                Diag.Writer!.Flush(TimeSpan.FromSeconds(5));
                Assert.Empty(Directory.GetFiles(dir, "heavy-*"));
                Assert.DoesNotContain(ReadLines(Path.Combine(dir, $"ui.{DateTime.UtcNow:yyyy-MM-dd}.jsonl")), l => Text(l, "message") == "request payload");
            }
            finally
            {
                Diag.Shutdown();
                Repo.RemoveTempFolder(dir);
            }
        }
    }
}
