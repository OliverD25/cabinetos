using CabinetOS.Core.Ipc;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The UI's client against the real core. Runs when <c>CABINETOS_CORE_EXE</c>
/// is set or the repository's core is built (<c>cargo build -p cabinetos-core</c>
/// in <c>core/</c>); skipped otherwise, as on the CI runner of the ui job.
/// </summary>
[Collection(HandleTests.Name)]
public class EndToEndTests
{
    [Fact]
    public async Task The_real_core_lists_a_folder_into_memory_the_ui_can_read()
    {
        var coreExe = CoreLauncher.Find(
            Path.Combine(Repo.Root, "ui"),
            Environment.GetEnvironmentVariable,
            File.Exists);
        if (coreExe is null)
        {
            Assert.Skip("No cabinetos-core.exe: set CABINETOS_CORE_EXE, or run `cargo build -p cabinetos-core` in core/.");
        }

        var root = Repo.NewTempFolder("e2e");
        try
        {
            var folder = Path.Combine(root, "listing");
            Directory.CreateDirectory(Path.Combine(folder, "a folder"));
            for (var i = 0; i < 1000; i++)
            {
                File.WriteAllBytes(Path.Combine(folder, $"file-{i:0000}.txt"), []);
            }
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var environment = new Dictionary<string, string>
            {
                ["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json"),
                ["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs"),
                ["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins"),
                ["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data"),
            };

            await using var core = await CoreLauncher.StartAsync(coreExe, TimeSpan.FromSeconds(10), environment);
            var client = core.Client;

            var welcome = await client.HelloAsync();
            Assert.Equal(8u, welcome.ProtocolVersion);

            var keymap = Keymap.From((await client.RequestAsync<KeymapReply>(new GetKeymapRequest())).ToData());
            Assert.Equal(1000, keymap.ChordWindowMs);
            Assert.Equal("ctrl+shift+p", keymap.FirstFor("palette.show")!.Keys.ToString());
            Assert.Contains("keys.open", keymap.Immutable);

            var list = new ListDirectoryRequest(folder);
            var opened = await client.RequestAsync<ListingOpenedReply>(list);
            Assert.Equal(1001u, opened.EntryCount);
            using (var view = ListingView.Open(opened.TakeSection()!, opened.SectionSize))
            {
                Assert.Equal(1001, view.Count);
                Assert.Equal(1u, view.Generation);
                Assert.Equal("a folder", view.Name(0));
                Assert.Equal(EntryKind.Directory, view.Kind(0));
                Assert.Equal("file-0000.txt", view.Name(1));
                Assert.Equal("file-0999.txt", view.Name(1000));
                Assert.Equal(EntryKind.File, view.Kind(500));
                Assert.Equal(0UL, view.Size(500));
                Assert.True(view.Modified(500) > DateTime.UtcNow.AddHours(-1));
                Assert.Equal(1000, view.IndexOfName("FILE-0999.TXT"));
            }

            Assert.IsType<OkReply>(await client.RequestAsync(new CloseListingRequest(opened.ListingId)));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
            Assert.True(core.Process.HasExited);
            Assert.Equal(0, core.Process.ExitCode);

            // Article 12: the request ID the UI created is in the core's own log.
            var coreLog = Directory.GetFiles(Path.Combine(root, "logs"), "core.*.jsonl").Single();
            Assert.Contains(list.Id, File.ReadAllText(coreLog));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_real_core_copies_200_files_and_a_conflict_answered_with_skip_does_not_stop_the_job()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-copy");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var files = new List<string>();
            for (var i = 0; i < 200; i++)
            {
                var path = Path.Combine(source, $"file-{i:000}.txt");
                File.WriteAllText(path, $"new {i}");
                files.Add(path);
            }
            // The conflict: file-007.txt is already at the destination.
            File.WriteAllText(Path.Combine(destination, "file-007.txt"), "the one already there");

            await using var core = await StartCoreAsync(coreExe, root);
            using var ui = new UiThread();
            var job = await ui.RunAsync(async () =>
            {
                await core.Client.HelloAsync();
                var center = new TransferCenter(core.Client);
                _ = PumpAsync(core.Client, center);

                var start = await center.StartAsync(JobKind.Copy, files, destination);
                Assert.True(start.Started, start.ErrorMessage);
                await WaitUntilAsync(() => center.Conflicts.Current is not null, "a conflict");

                var conflict = center.Conflicts.Current!;
                Assert.Equal(ConflictKind.FileExists, conflict.Kind.Type);
                Assert.EndsWith("file-007.txt", conflict.Source);
                Assert.Null(await center.ResolveAsync(conflict, new Resolution(Resolution.SkipType), applyToSameKind: false));
                Assert.Null(center.Conflicts.Current);

                await WaitUntilAsync(() => start.Job!.IsFinal, "the end of the job");
                return start.Job!;
            });

            Assert.Equal(JobState.Completed, job.State.Type);
            Assert.Equal((200UL, 1UL, 0UL), (job.Progress!.FilesTotal, job.Progress.FilesSkipped, job.Progress.FilesFailed));
            Assert.Equal(200, Directory.GetFiles(destination).Length);
            Assert.Equal("the one already there", File.ReadAllText(Path.Combine(destination, "file-007.txt")));
            Assert.Equal("new 199", File.ReadAllText(Path.Combine(destination, "file-199.txt")));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_real_core_makes_a_folder_and_renames_it()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-folder");
        try
        {
            await using var core = await StartCoreAsync(coreExe, root);
            var client = core.Client;
            var created = Path.Combine(root, "New folder");

            var reply = await client.RequestAsync(new CreateDirectoryRequest(created));
            if (reply is ErrorReply { Code: ErrorCodes.UnknownRequest })
            {
                Assert.Skip("This core does not answer create_directory yet: build the core again.");
            }
            Assert.IsType<OkReply>(reply);
            Assert.True(Directory.Exists(created));
            Assert.Equal(ErrorCodes.AlreadyExists, Assert.IsType<ErrorReply>(await client.RequestAsync(new CreateDirectoryRequest(created))).Code);

            Assert.IsType<OkReply>(await client.RequestAsync(new RenameRequest(created, "Reports 2026")));
            Assert.False(Directory.Exists(created));
            Assert.True(Directory.Exists(Path.Combine(root, "Reports 2026")));

            Directory.CreateDirectory(Path.Combine(root, "taken"));
            var refused = Assert.IsType<ErrorReply>(await client.RequestAsync(new RenameRequest(Path.Combine(root, "Reports 2026"), "taken")));
            Assert.Equal(ErrorCodes.AlreadyExists, refused.Code);
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    private static string FindCoreOrSkip()
    {
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (coreExe is null)
        {
            Assert.Skip("No cabinetos-core.exe: set CABINETOS_CORE_EXE, or run `cargo build -p cabinetos-core` in core/.");
        }
        return coreExe;
    }

    private static Task<CoreConnection> StartCoreAsync(string coreExe, string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var environment = new Dictionary<string, string>
        {
            ["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json"),
            ["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs"),
            ["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins"),
            ["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data"),
        };
        return CoreLauncher.StartAsync(coreExe, TimeSpan.FromSeconds(10), environment);
    }

    // The window's event pump, in short: every core event goes to the job center on the UI thread.
    private static async Task PumpAsync(CoreClient client, TransferCenter center)
    {
        await foreach (var coreEvent in client.Events.ReadAllAsync())
        {
            center.OnEvent(coreEvent);
            (coreEvent as ICarriesSection)?.TakeSection()?.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"waited 30 s for {what}");
            }
            await Task.Delay(20);
        }
    }
}
