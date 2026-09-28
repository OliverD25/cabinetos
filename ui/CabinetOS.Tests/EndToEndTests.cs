using CabinetOS.Core.Ipc;
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
            Assert.Equal(7u, welcome.ProtocolVersion);

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
}
