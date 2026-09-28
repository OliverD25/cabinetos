using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

public class DiagnosticsTests
{
    private static readonly DateTime At = new(2026, 9, 28, 1, 2, 3, 4, DateTimeKind.Utc);

    [Fact]
    public void A_line_has_the_documented_keys_in_the_documented_order()
    {
        var line = LogLine.Format(At, LogLevel.Info, "cabinetos_ui::pipe", "request sent",
            "01J9ZQ4X7K3M5N8P2R6S0T1V4W", "request",
            [new LogField("request", "ping"), new LogField("bytes", 42)], "ui");
        Assert.Equal(
            """{"ts":"2026-09-28T01:02:03.004Z","level":"INFO","boundary":"frontend","target":"cabinetos_ui::pipe","message":"request sent","request_id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","span":"request","fields":{"request":"ping","bytes":42},"thread":"ui"}""",
            line);
    }

    [Fact]
    public void Optional_keys_are_left_out_and_text_in_any_script_stays_readable()
    {
        var line = LogLine.Format(At, LogLevel.Warn, "cabinetos_ui::pane", "Документи \"quoted\"", null, null, null, "ThreadId(7)");
        Assert.Equal(
            """{"ts":"2026-09-28T01:02:03.004Z","level":"WARN","boundary":"frontend","target":"cabinetos_ui::pane","message":"Документи \"quoted\"","thread":"ThreadId(7)"}""",
            line);
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal(["ts", "level", "boundary", "target", "message", "thread"],
            parsed.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(null, LogLevel.Info, "cabinetos_ui::pipe", true)]
    [InlineData(null, LogLevel.Debug, "cabinetos_ui::pipe", false)]
    [InlineData("debug", LogLevel.Debug, "cabinetos_ui::pipe", true)]
    [InlineData("warn,cabinetos_ui::pipe=trace", LogLevel.Trace, "cabinetos_ui::pipe", true)]
    [InlineData("warn,cabinetos_ui::pipe=trace", LogLevel.Info, "cabinetos_ui::keys", false)]
    [InlineData("info,cabinetos_ui=debug", LogLevel.Debug, "cabinetos_ui::keys", true)]
    [InlineData("info,cabinetos_ui=debug", LogLevel.Debug, "cabinetos_uix", false)]
    [InlineData("error,cabinetos_ui=debug,cabinetos_ui::keys=warn", LogLevel.Info, "cabinetos_ui::keys", false)]
    [InlineData("nonsense", LogLevel.Info, "cabinetos_ui::app", true)]
    public void The_filter_reads_the_cores_syntax(string? spec, LogLevel level, string target, bool enabled) =>
        Assert.Equal(enabled, LogFilter.Parse(spec).IsEnabled(level, target));

    [Fact]
    public void The_writer_appends_json_lines_to_the_daily_file()
    {
        var dir = Repo.NewTempFolder("diag");
        try
        {
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At))
            {
                writer.Write(LogLevel.Info, "cabinetos_ui::test", "one", "01J9ZQ4X7K3M5N8P2R6S0T1V4W", "request");
                writer.Write(LogLevel.Debug, "cabinetos_ui::test", "filtered out");
                writer.Write(LogLevel.Error, "cabinetos_ui::test", "two", fields: [new LogField("code", 5)]);
                Assert.True(writer.Flush(TimeSpan.FromSeconds(5)));
                Assert.Equal(Path.Combine(dir, "ui.2026-09-28.jsonl"), writer.CurrentFilePath);
            }
            var lines = File.ReadAllLines(Path.Combine(dir, "ui.2026-09-28.jsonl"));
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("frontend", first.RootElement.GetProperty("boundary").GetString());
            Assert.Equal("01J9ZQ4X7K3M5N8P2R6S0T1V4W", first.RootElement.GetProperty("request_id").GetString());
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("ERROR", second.RootElement.GetProperty("level").GetString());
            Assert.Equal(5, second.RootElement.GetProperty("fields").GetProperty("code").GetInt32());
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void Two_windows_append_to_one_daily_file_without_losing_or_breaking_a_line()
    {
        // Two windows are two processes, each with its own handle on today's file (edge cases, class D).
        var dir = Repo.NewTempFolder("diag");
        try
        {
            const int each = 3000;
            using (var first = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At))
            using (var second = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At))
            {
                var writers = new[] { first, second };
                Parallel.For(0, 2, w =>
                {
                    for (var i = 0; i < each; i++)
                    {
                        writers[w].Write(LogLevel.Info, "cabinetos_ui::test", $"window {w} line {i} " + new string('x', i % 700));
                        if (i % 97 == 0)
                        {
                            // The writer thread takes lines in batches; pausing makes many of them, from both sides.
                            Thread.Sleep(1);
                        }
                    }
                });
                Assert.True(first.Flush(TimeSpan.FromSeconds(10)));
                Assert.True(second.Flush(TimeSpan.FromSeconds(10)));
            }

            var lines = File.ReadAllLines(Path.Combine(dir, "ui.2026-09-28.jsonl"));
            var seen = new HashSet<string>();
            foreach (var line in lines)
            {
                using var parsed = JsonDocument.Parse(line);
                var message = parsed.RootElement.GetProperty("message").GetString()!;
                seen.Add(string.Join(' ', message.Split(' ').Take(4)));
            }
            Assert.Equal(2 * each, lines.Length);
            Assert.Equal(2 * each, seen.Count);
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_ring_keeps_the_last_256_lines_oldest_first()
    {
        var dir = Repo.NewTempFolder("diag");
        try
        {
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At);
            for (var i = 0; i < 300; i++)
            {
                writer.Write(LogLevel.Info, "cabinetos_ui::test", $"line {i}");
            }
            var recent = writer.RecentLines();
            Assert.Equal(LogWriter.RingCapacity, recent.Count);
            Assert.Contains("\"line 44\"", recent[0]);
            Assert.Contains("\"line 299\"", recent[^1]);
            writer.Flush(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_crash_trace_has_the_cores_shape_with_boundary_frontend()
    {
        var dir = Repo.NewTempFolder("diag");
        try
        {
            string? path;
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At))
            {
                writer.Write(LogLevel.Info, "cabinetos_ui::test", "before the crash");
                Exception thrown;
                try
                {
                    throw new InvalidOperationException("self-test crash");
                }
                catch (InvalidOperationException error)
                {
                    thrown = error;
                }
                path = writer.WriteCrashReport(thrown, "unhandled exception on the UI thread");
            }
            Assert.NotNull(path);
            Assert.Equal("crash-20260928T010203004Z.json", Path.GetFileName(path));
            using var crash = JsonDocument.Parse(File.ReadAllText(path));
            var root = crash.RootElement;
            Assert.Equal(
                ["boundary", "process", "version", "message", "location", "thread", "backtrace", "recent_events"],
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("frontend", root.GetProperty("boundary").GetString());
            Assert.Equal("ui", root.GetProperty("process").GetString());
            Assert.Equal("0.1.0", root.GetProperty("version").GetString());
            Assert.Contains("self-test crash", root.GetProperty("message").GetString());
            Assert.Contains("InvalidOperationException", root.GetProperty("backtrace").GetString());
            var events = root.GetProperty("recent_events").EnumerateArray().ToList();
            Assert.Contains(events, e => e.GetProperty("message").GetString() == "before the crash");
            // The log file was flushed before the trace returned.
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "ui.2026-09-28.jsonl")));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void Only_the_newest_14_daily_files_are_kept()
    {
        var dir = Repo.NewTempFolder("diag");
        try
        {
            for (var day = 1; day <= 20; day++)
            {
                File.WriteAllText(Path.Combine(dir, $"ui.2026-08-{day:00}.jsonl"), "{}\n");
            }
            File.WriteAllText(Path.Combine(dir, "core.2026-08-01.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(dir, "crash-20260801T000000000Z.json"), "{}");
            using (var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At))
            {
                writer.Write(LogLevel.Info, "cabinetos_ui::test", "today");
                writer.Flush(TimeSpan.FromSeconds(5));
            }
            var uiFiles = Directory.GetFiles(dir, "ui.*.jsonl").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(LogWriter.KeptLogFiles, uiFiles.Count);
            Assert.Equal("ui.2026-08-08.jsonl", uiFiles[0]);
            Assert.Equal("ui.2026-09-28.jsonl", uiFiles[^1]);
            Assert.True(File.Exists(Path.Combine(dir, "core.2026-08-01.jsonl")));
            Assert.True(File.Exists(Path.Combine(dir, "crash-20260801T000000000Z.json")));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_directory_comes_from_the_environment_first()
    {
        Assert.Equal(@"D:\logs", Diag.DefaultDirectory(name => name == Diag.LogDirEnv ? @"D:\logs" : null));
        Assert.EndsWith(Path.Combine("CabinetOS", "logs"), Diag.DefaultDirectory(_ => null));
    }
}
