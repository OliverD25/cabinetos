using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// Log bundles and the crash offer in the window (docs/diagnostics.md, "Bundles"): the zip the
/// window writes when it crashes in heavy mode, what it holds, and the "Open crash folder" check
/// at the next start. The core's own bundle is the same zip, asked for with <c>save_log_bundle</c>.
/// </summary>
public class LogBundleTests
{
    private static readonly DateTime At = new(2026, 9, 28, 1, 2, 3, 4, DateTimeKind.Utc);

    private static string Line(string ts, string message) =>
        $$"""{"ts":"{{ts}}","level":"INFO","boundary":"engine","target":"t","message":"{{message}}","thread":"x"}""";

    private static BundleFacts Facts(JsonNode? config = null) => new("ui", "0.1.0", 13, "10.0.26200.6899 (25H2)", config);

    private static Dictionary<string, string> Entries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // A log file as the writers make it: a line feed ends each line, on Windows too.
    private static void WriteLog(string path, IEnumerable<string> lines) =>
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));

    [Fact]
    public void A_bundle_holds_the_last_minutes_of_every_log_the_crash_traces_of_a_day_and_bundle_json()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            // now is 01:02:03.004, so ten minutes back is 00:52:03.004.
            WriteLog(Path.Combine(dir, "core.2026-09-28.jsonl"),
            [
                Line("2026-09-28T00:50:00.000Z", "too old"),
                Line("2026-09-28T00:52:03.003Z", "a millisecond too old"),
                Line("2026-09-28T00:52:03.004Z", "exactly at the edge"),
                Line("2026-09-28T01:00:00.000Z", "recent"),
                "not a stamped line stays with its neighbours",
                Line("2026-09-28T01:02:00.000Z", "newest"),
            ]);
            WriteLog(Path.Combine(dir, "heavy-core.2026-09-28.jsonl"), [Line("2026-09-28T01:01:00.000Z", "heavy recent")]);
            WriteLog(Path.Combine(dir, "ui.2026-09-27.jsonl"), [Line("2026-09-27T23:00:00.000Z", "yesterday")]);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a log");
            File.WriteAllText(Path.Combine(dir, "crash-20260928T005500000Z.json"), """{"message":"a crash"}""");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "crash-20260928T005500000Z.json"), At.AddMinutes(-7));
            File.WriteAllText(Path.Combine(dir, "crash-20260926T000000000Z.json"), """{"message":"an old crash"}""");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "crash-20260926T000000000Z.json"), At.AddHours(-50));
            var config = JsonNode.Parse("""{"ui":{"theme":"nord"},"secrets":{"token":"sk-live-1"}}""");
            LogMask.MaskSecrets(config);
            var zip = Path.Combine(dir, "crash-20260928T010203004Z.zip");

            LogBundle.Write(dir, zip, At, 10, "crash", Facts(config));

            var entries = Entries(zip);
            Assert.Equal(
                ["bundle.json", "core.2026-09-28.jsonl", "crash-20260928T005500000Z.json", "heavy-core.2026-09-28.jsonl"],
                entries.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(
                [Line("2026-09-28T00:52:03.004Z", "exactly at the edge"), Line("2026-09-28T01:00:00.000Z", "recent"),
                    "not a stamped line stays with its neighbours", Line("2026-09-28T01:02:00.000Z", "newest")],
                Lines(entries["core.2026-09-28.jsonl"]));
            Assert.Equal([Line("2026-09-28T01:01:00.000Z", "heavy recent")], Lines(entries["heavy-core.2026-09-28.jsonl"]));
            Assert.Equal("""{"message":"a crash"}""", entries["crash-20260928T005500000Z.json"]);

            using var manifest = JsonDocument.Parse(entries["bundle.json"]);
            var root = manifest.RootElement;
            Assert.Equal(
                ["created", "reason", "process", "minutes", "since", "versions", "windows_build", "environment", "config", "files"],
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("2026-09-28T01:02:03.004Z", root.GetProperty("created").GetString());
            Assert.Equal("crash", root.GetProperty("reason").GetString());
            Assert.Equal("ui", root.GetProperty("process").GetString());
            Assert.Equal(10, root.GetProperty("minutes").GetInt32());
            Assert.Equal("2026-09-28T00:52:03.004Z", root.GetProperty("since").GetString());
            Assert.Equal("0.1.0", root.GetProperty("versions").GetProperty("cabinetos").GetString());
            Assert.Equal(13, root.GetProperty("versions").GetProperty("protocol").GetInt32());
            Assert.Equal("10.0.26200.6899 (25H2)", root.GetProperty("windows_build").GetString());
            Assert.Equal("nord", root.GetProperty("config").GetProperty("ui").GetProperty("theme").GetString());
            Assert.Equal("***", root.GetProperty("config").GetProperty("secrets").GetProperty("token").GetString());
            var files = root.GetProperty("files").EnumerateArray().ToList();
            Assert.Equal(["core.2026-09-28.jsonl", "crash-20260928T005500000Z.json", "heavy-core.2026-09-28.jsonl"], files.Select(f => f.GetProperty("name").GetString()));
            Assert.Equal(4, files[0].GetProperty("lines").GetInt32());
            Assert.False(files[1].TryGetProperty("lines", out _), "a crash trace has no line count");
            Assert.DoesNotContain("sk-live-1", entries["bundle.json"]);
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void The_environment_in_a_bundle_has_the_cabinetos_variables_and_masks_the_ones_that_look_like_keys()
    {
        var dir = Repo.NewTempFolder("bundle");
        Environment.SetEnvironmentVariable("CABINETOS_TEST_BUNDLE_API_KEY", "sk-live-2");
        Environment.SetEnvironmentVariable("CABINETOS_TEST_BUNDLE_MODE", "plain");
        Environment.SetEnvironmentVariable("NOT_CABINETOS_TEST_BUNDLE", "hidden");
        try
        {
            var zip = Path.Combine(dir, "bundle.zip");
            LogBundle.Write(dir, zip, At, 10, "asked", Facts());
            var text = Entries(zip)["bundle.json"];
            using var manifest = JsonDocument.Parse(text);
            var environment = manifest.RootElement.GetProperty("environment");
            Assert.Equal("***", environment.GetProperty("CABINETOS_TEST_BUNDLE_API_KEY").GetString());
            Assert.Equal("plain", environment.GetProperty("CABINETOS_TEST_BUNDLE_MODE").GetString());
            Assert.False(environment.TryGetProperty("NOT_CABINETOS_TEST_BUNDLE", out _));
            Assert.DoesNotContain("sk-live-2", text);
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("config").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CABINETOS_TEST_BUNDLE_API_KEY", null);
            Environment.SetEnvironmentVariable("CABINETOS_TEST_BUNDLE_MODE", null);
            Environment.SetEnvironmentVariable("NOT_CABINETOS_TEST_BUNDLE", null);
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_big_file_is_read_from_its_end_and_lines_across_the_piece_edges_stay_whole()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            var path = Path.Combine(dir, "heavy-core.2026-09-28.jsonl");
            var recent = 0;
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                // Some 5 MiB of old lines, then some 3 MiB of recent ones: the read goes backwards in pieces of 1 MiB.
                for (var n = 0; n < 45_000; n++)
                {
                    writer.WriteLine(Line("2026-09-28T00:10:00.000Z", $"old {n:000000} " + new string('o', 60)));
                }
                for (var n = 0; n < 27_000; n++)
                {
                    writer.WriteLine(Line("2026-09-28T01:01:00.000Z", $"recent {n:000000} " + new string('r', 60)));
                    recent++;
                }
            }
            Assert.True(new FileInfo(path).Length > 7 * 1024 * 1024);

            var (bytes, count) = LogBundle.RecentLines(path, "2026-09-28T00:52:03.004Z");

            Assert.Equal(recent, count);
            var lines = Lines(Encoding.UTF8.GetString(bytes));
            Assert.Equal(recent, lines.Length);
            Assert.All(lines, line => Assert.StartsWith("""{"ts":"2026-09-28T01:01:00.000Z",""", line));
            // Every line is whole JSON, and they come in the order they were written.
            Assert.Equal(Enumerable.Range(0, recent).Select(n => $"recent {n:000000} "),
                lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("message").GetString()![..14]));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_file_with_nothing_recent_is_left_out_after_reading_one_piece()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            var path = Path.Combine(dir, "ui.2026-09-27.jsonl");
            WriteLog(path, Enumerable.Range(0, 20_000).Select(n => Line("2026-09-27T12:00:00.000Z", $"old {n}")));
            var (bytes, count) = LogBundle.RecentLines(path, "2026-09-28T00:52:03.004Z");
            Assert.Empty(bytes);
            Assert.Equal(0, count);
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_bundle_reads_a_log_another_process_still_has_open()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            var path = Path.Combine(dir, "core.2026-09-28.jsonl");
            // As the core holds its file: open for writing, sharing reads, writes and deletes.
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write(Encoding.UTF8.GetBytes(Line("2026-09-28T01:01:00.000Z", "written and still open") + "\n"));
            writer.Flush();
            var zip = Path.Combine(dir, "bundle.zip");
            LogBundle.Write(dir, zip, At, 10, "asked", Facts());
            Assert.Equal([Line("2026-09-28T01:01:00.000Z", "written and still open")], Lines(Entries(zip)["core.2026-09-28.jsonl"]));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    // ----- The crash hook's bundle -----

    [Fact]
    public void A_crash_in_heavy_mode_writes_the_bundle_after_the_trace_under_the_traces_name()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            var now = At.AddMinutes(-30);
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => now);
            writer.SetHeavy(true);
            writer.Write(LogLevel.Info, "cabinetos_ui::test", "half an hour before the crash");
            writer.Write(LogLevel.Trace, "cabinetos_ui::test", "half an hour before, trace");
            now = At;
            writer.Write(LogLevel.Info, "cabinetos_ui::test", "just before the crash");
            writer.Write(LogLevel.Trace, "cabinetos_ui::test", "just before, trace");
            writer.Write(LogLevel.Debug, "heavy::keys", "key pressed");

            var trace = writer.WriteCrashReport(new InvalidOperationException("boom"), "unhandled exception");
            var bundle = writer.WriteCrashBundle(trace, Facts());

            Assert.Equal(Path.Combine(dir, "crash-20260928T010203004Z.zip"), bundle);
            var entries = Entries(bundle!);
            // The trace, the writer's normal file and its heavy file, and the manifest.
            Assert.Equal(
                ["bundle.json", "crash-20260928T010203004Z.json", "heavy-ui.2026-09-28.jsonl", "ui.2026-09-28.jsonl"],
                entries.Keys.Order(StringComparer.Ordinal));
            var normal = Lines(entries["ui.2026-09-28.jsonl"]).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString());
            Assert.Equal(["just before the crash"], normal);
            var heavy = string.Join('\n', Lines(entries["heavy-ui.2026-09-28.jsonl"]).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()));
            Assert.Contains("just before, trace", heavy);
            Assert.Contains("key pressed", heavy);
            Assert.DoesNotContain("half an hour before", heavy);
            using var crash = JsonDocument.Parse(entries["crash-20260928T010203004Z.json"]);
            Assert.Equal("frontend", crash.RootElement.GetProperty("boundary").GetString());
            using var manifest = JsonDocument.Parse(entries["bundle.json"]);
            Assert.Equal("crash", manifest.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_bundle_that_cannot_be_written_leaves_the_trace_and_no_half_zip()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            using var writer = new LogWriter(dir, LogFilter.Default, "ui", "0.1.0", () => At);
            var trace = writer.WriteCrashReport(new InvalidOperationException("boom"), "unhandled exception");
            Assert.NotNull(trace);
            // A folder where the zip should go: creating the file fails.
            Directory.CreateDirectory(Path.Combine(dir, "crash-20260928T010203004Z.zip"));

            Assert.Null(writer.WriteCrashBundle(trace, Facts()));

            Assert.True(File.Exists(trace));
            Assert.True(Directory.Exists(Path.Combine(dir, "crash-20260928T010203004Z.zip")));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Collection(DiagStateTests.Name)]
    public class ThroughTheProcessLog
    {
        [Fact]
        public void Diag_Crash_writes_the_bundle_only_while_heavy_mode_is_on_and_masks_the_configuration()
        {
            var dir = Repo.NewTempFolder("bundle");
            try
            {
                Diag.Init("0.1.0", dir);
                Diag.SetBundleProtocol(13);
                Diag.SetBundleConfig(JsonDocument.Parse("""{"ui":{"theme":"nord"},"plugins":{"x":{"api_key":"sk-live-3"}}}""").RootElement);
                Diag.ForgetCrashForTests();

                var withoutHeavy = Diag.Crash(new InvalidOperationException("first"), "a crash without heavy mode");
                Assert.NotNull(withoutHeavy);
                Assert.Empty(Directory.GetFiles(dir, "crash-*.zip"));

                Diag.ForgetCrashForTests();
                Assert.True(Diag.Writer!.SetHeavy(true));
                Diag.Info("cabinetos_ui::test", "a line before the second crash");
                var trace = Diag.Crash(new InvalidOperationException("second"), "a crash in heavy mode");
                Assert.NotNull(trace);

                var zip = Path.ChangeExtension(trace, ".zip")!;
                Assert.True(File.Exists(zip));
                var entries = Entries(zip);
                Assert.Contains("bundle.json", entries.Keys);
                Assert.Contains(Path.GetFileName(trace), entries.Keys);
                using var manifest = JsonDocument.Parse(entries["bundle.json"]);
                Assert.Equal(13, manifest.RootElement.GetProperty("versions").GetProperty("protocol").GetInt32());
                Assert.Equal("nord", manifest.RootElement.GetProperty("config").GetProperty("ui").GetProperty("theme").GetString());
                Assert.DoesNotContain("sk-live-3", entries["bundle.json"]);
                Assert.False(string.IsNullOrEmpty(manifest.RootElement.GetProperty("windows_build").GetString()));
            }
            finally
            {
                Diag.ForgetCrashForTests();
                Diag.Shutdown();
                Repo.RemoveTempFolder(dir);
            }
        }
    }

    // ----- The offer at the next start -----

    private static string Zip(string dir, string name, DateTime modified)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0x50, 0x4B]);
        File.SetLastWriteTimeUtc(path, modified);
        return path;
    }

    [Fact]
    public void A_crash_zip_newer_than_the_last_start_is_offered_once()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            CrashNotice.WriteMarker(dir, At);
            Zip(dir, "crash-20260928T000000000Z.zip", At.AddMinutes(-10));
            var newest = Zip(dir, "crash-20260928T010000000Z.zip", At.AddMinutes(5));
            Zip(dir, "crash-20260928T010500000Z.zip", At.AddMinutes(1));
            Zip(dir, "bundle-20260928T010500000Z.zip", At.AddMinutes(9));

            Assert.Equal(At, CrashNotice.ReadMarker(dir));
            var start = At.AddHours(1);
            Assert.Equal(newest, CrashNotice.CheckAtStart(dir, start));
            // The start was recorded: the same zip is not offered again.
            Assert.Equal(start, CrashNotice.ReadMarker(dir));
            Assert.Null(CrashNotice.CheckAtStart(dir, start.AddMinutes(1)));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void Without_a_record_of_the_last_start_a_crash_zip_of_the_last_day_counts()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            var recent = Zip(dir, "crash-20260928T000000000Z.zip", At.AddHours(-3));
            Zip(dir, "crash-20260926T000000000Z.zip", At.AddHours(-60));
            Assert.Null(CrashNotice.ReadMarker(dir));
            Assert.Equal(recent, CrashNotice.CheckAtStart(dir, At));
            Assert.True(File.Exists(Path.Combine(dir, CrashNotice.MarkerName)));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    [Fact]
    public void A_start_with_no_crash_zip_or_an_unreadable_marker_offers_nothing_and_does_not_fail()
    {
        var dir = Repo.NewTempFolder("bundle");
        try
        {
            Assert.Null(CrashNotice.CheckAtStart(dir, At));
            File.WriteAllText(Path.Combine(dir, CrashNotice.MarkerName), "not a time");
            Assert.Null(CrashNotice.ReadMarker(dir));
            Assert.Null(CrashNotice.CheckAtStart(dir, At.AddMinutes(1)));
            Assert.Null(CrashNotice.CheckAtStart(Path.Combine(dir, "no such folder"), At));
        }
        finally
        {
            Repo.RemoveTempFolder(dir);
        }
    }

    // ----- save_log_bundle against the real core -----

    [Collection(HandleTests.Name)]
    public class WithTheRealCore
    {
        [Fact]
        public async Task The_core_answers_save_log_bundle_with_a_zip_in_the_log_folder()
        {
            var coreExe = EndToEndTests.FindCoreOrSkip();
            var root = Repo.NewTempFolder("e2e-bundle");
            try
            {
                await using var core = await EndToEndTests.StartCoreAsync(coreExe, root);
                await core.Client.HelloAsync();

                // What "Diagnostics: Save Log Bundle" asks for. The core's own log is written by a thread
                // of its own, so the first bundle may come before its first line is on disk.
                LogBundleReply bundle;
                Dictionary<string, string> entries;
                var tries = 0;
                do
                {
                    bundle = await core.Client.RequestAsync<LogBundleReply>(new SaveLogBundleRequest(10));
                    entries = Entries(bundle.Path);
                    await Task.Delay(200);
                }
                while (++tries < 10 && !entries.Keys.Any(name => name.StartsWith("core.", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal)));

                Assert.Equal(Path.Combine(root, "logs"), Path.GetDirectoryName(bundle.Path));
                Assert.Matches(@"^bundle-\d{8}T\d{9}Z(-\d+)?\.zip$", Path.GetFileName(bundle.Path));
                using var manifest = JsonDocument.Parse(entries["bundle.json"]);
                Assert.Equal("asked", manifest.RootElement.GetProperty("reason").GetString());
                Assert.Equal("core", manifest.RootElement.GetProperty("process").GetString());
                Assert.Contains(entries.Keys, name => name.StartsWith("core.", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal));
            }
            finally
            {
                Repo.RemoveTempFolder(root);
            }
        }
    }
}
