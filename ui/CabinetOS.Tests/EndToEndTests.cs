using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Jobs;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Market;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Terminal;
using CabinetOS.Core.Themes;
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
                ["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes"),
            };

            await using var core = await CoreLauncher.StartAsync(coreExe, TimeSpan.FromSeconds(10), environment);
            var client = core.Client;

            var welcome = await client.HelloAsync();
            // Version 13: window_state (Phases 12 to 14), after 12's sub-phase 11a requests.
            Assert.Equal(13u, welcome.ProtocolVersion);

            var keymap = Keymap.From((await client.RequestAsync<KeymapReply>(new GetKeymapRequest())).ToData());
            Assert.Equal(1000, keymap.ChordWindowMs);
            Assert.Equal("ctrl+shift+p", keymap.FirstFor("palette.show")!.Keys.ToString());
            Assert.Contains("keys.open", keymap.Immutable);
            // The shell's own commands are bindings of the core's keymap (protocol 9):
            // F2 renames in a pane and records keys in the palette.
            Assert.Equal(("f2", "filesView"), (keymap.FirstFor("file.rename")!.Keys.ToString(), keymap.FirstFor("file.rename")!.When));
            Assert.Equal(("f2", "paletteOpen"), (keymap.FirstFor("keys.rebind")!.Keys.ToString(), keymap.FirstFor("keys.rebind")!.When));
            Assert.Equal("enter", keymap.FirstFor("pane.openSelected")!.Keys.ToString());
            Assert.Equal("shift+delete", keymap.FirstFor("file.deletePermanently")!.Keys.ToString());

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

    [Fact]
    public async Task The_real_core_names_the_types_and_draws_the_icons_of_the_rows()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-details");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "listing")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "a folder"));
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
            File.WriteAllText(Path.Combine(folder, "README"), "x");
            File.WriteAllText(Path.Combine(folder, ".gitattributes"), "* text=auto");

            await using var core = await StartCoreAsync(coreExe, root);
            var client = core.Client;
            await client.HelloAsync();
            var opened = await client.RequestAsync<ListingOpenedReply>(new ListDirectoryRequest(folder));
            using var view = ListingView.Open(opened.TakeSection()!, opened.SectionSize);

            // The pane's way: the pages that cover the rows on screen, each asked once.
            var cache = new EntryDetailsCache();
            cache.Reset(opened.ListingId, view.Generation);
            var (from, count) = Assert.Single(cache.TakePagesToRequest(0, view.Count - 1, view.Count));
            Assert.Empty(cache.TakePagesToRequest(0, view.Count - 1, view.Count));
            var reply = await client.RequestAsync(new DescribeEntriesRequest(opened.ListingId, from, count));
            if (reply is ErrorReply { Code: ErrorCodes.UnknownRequest })
            {
                Assert.Skip("This core does not answer describe_entries yet: build the core again.");
            }
            var details = Assert.IsType<EntryDetailsReply>(reply);
            Assert.True(cache.Apply(details));
            Assert.Equal(4, details.Details.Count);
            Assert.Equal("folder", cache.Get(view.IndexOfName("a folder"))!.IconKey);
            Assert.Equal("ext:.txt", cache.Get(view.IndexOfName("notes.txt"))!.IconKey);
            Assert.Equal("generic", cache.Get(view.IndexOfName("README"))!.IconKey);
            // The shell's names are in the user's language: only that there is one.
            Assert.All(details.Details, detail => Assert.False(string.IsNullOrWhiteSpace(detail.TypeName)));
            // Protocol 11: where the shell answers a program identifier (txtfile, on the PC this was written on),
            // the core names the type as Explorer does, "GITATTRIBUTES File". Either way the column shows words.
            var gitattributes = cache.Get(view.IndexOfName(".gitattributes"))!.TypeName;
            Assert.False(IsProgramIdentifier(gitattributes), $"the Type column would show {gitattributes}");

            foreach (var size in IconSizes.Offered)
            {
                var icon = await client.RequestAsync<IconReply>(new GetIconRequest("ext:.txt", size));
                var png = Convert.FromBase64String(icon.PngBase64);
                Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
                // The IHDR chunk's width and height.
                Assert.Equal((size, size), (BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20))));
            }
            var unknown = Assert.IsType<ErrorReply>(await client.RequestAsync(new GetIconRequest("path:0000000000000000", 16)));
            Assert.Equal(ErrorCodes.NotFound, unknown.Code);

            Assert.IsType<OkReply>(await client.RequestAsync(new CloseListingRequest(opened.ListingId)));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task A_cmd_session_echoes_through_the_byte_pump()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-term");
        try
        {
            await using var core = await StartCoreAsync(coreExe, root);
            var client = core.Client;
            await client.HelloAsync();

            var opened = await client.RequestAsync<TerminalOpenedReply>(new TerminalOpenRequest(100, 30) { Profile = "cmd", Cwd = root });
            Assert.StartsWith(@"\\.\pipe\cabinetos-term-", opened.Pipe);
            var output = new OutputCoalescer(() => Environment.TickCount64);
            var seen = new StringBuilder();
            await using (var pipe = await TerminalPipe.ConnectAsync(opened.Pipe, output, TimeSpan.FromSeconds(5)))
            {
                await pipe.WriteAsync("echo hello-from-pane\r"u8.ToArray());
                // What the page would get: base64 chunks, decoded here.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (CountOf(seen.ToString(), "hello-from-pane") < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(OutputCoalescer.FrameMilliseconds);
                    foreach (var chunk in output.Flush())
                    {
                        seen.Append(Encoding.UTF8.GetString(Convert.FromBase64String(chunk)));
                    }
                }
            }
            // The typed line comes back once as echo and once as the command's output.
            Assert.True(CountOf(seen.ToString(), "hello-from-pane") >= 2, seen.ToString());

            Assert.IsType<OkReply>(await client.RequestAsync(new TerminalCloseRequest(opened.SessionId)));
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }

        static int CountOf(string text, string part) => text.Split(part).Length - 1;
    }

    [Fact]
    public async Task Choosing_Nord_in_the_picker_brings_theme_changed_and_the_mapper_reports_its_accent()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-theme");
        try
        {
            await using var core = await StartCoreAsync(coreExe, root);
            var client = core.Client;
            await client.HelloAsync();
            var first = await client.RequestAsync(new GetThemeRequest());
            if (first is ErrorReply { Code: ErrorCodes.UnknownRequest })
            {
                Assert.Skip("This core has no themes yet: build the core again.");
            }
            Assert.Equal("default", Assert.IsType<ThemeReply>(first).Theme.Id);

            // The window's way: the picker lists the themes and applies one with set_value ui.theme.
            var picker = new ThemePickerModel(client);
            Assert.True(await picker.LoadAsync("default"));
            // Every theme this core shipped into the empty themes folder, however many it ships (five since 9e4ce98).
            Assert.Equal(ShippedThemeFiles(Path.Combine(root, "themes")), picker.Rows.Select(r => r.Info.Id).Order());
            Assert.Contains("nord", picker.Rows.Select(r => r.Info.Id));
            Assert.Equal("#2E3440", picker.Rows.Single(r => r.Info.Id == "nord").Tint);
            Assert.True(await picker.ApplyAsync(picker.Rows.ToList().FindIndex(r => r.Info.Id == "nord")));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ThemeChangedEvent? changed = null;
            while (changed is null)
            {
                changed = await client.Events.ReadAsync(timeout.Token) as ThemeChangedEvent;
            }
            var look = ThemeMapper.Map(changed.Theme, ThemeMapper.Shades(new Argb(0xFF, 0x60, 0xCD, 0xFF), light: false));
            Assert.Equal(("nord", "#FF88C0D0"), (look.Id, look.Accent.ToString()));
            Assert.Equal(("#FF2E3440", 0.88), (look.Mica!.Tint.ToString(), look.Mica.Opacity));
            Assert.Equal(16, look.Terminal.Ansi.Count);

            // The choice is the configuration's: a restart starts with Nord.
            Assert.Equal("nord", Assert.IsType<ThemeReply>(await client.RequestAsync(new GetThemeRequest())).Theme.Id);
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("nord", File.ReadAllText(Path.Combine(root, "config", "cabinetos.json")));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_marketplace_reads_the_local_index_installs_a_reviewed_plugin_that_starts_and_removes_it()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-market");
        try
        {
            var index = Path.Combine(root, "index");
            BuildLocalIndex(index);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "cabinetos.json"),
                new JsonObject { ["marketplace"] = new JsonObject { ["index"] = index } }.ToJsonString());

            await using var core = await StartCoreAsync(coreExe, root);
            await core.Client.HelloAsync();
            if (await core.Client.RequestAsync(new ListToolsRequest()) is ErrorReply { Code: ErrorCodes.UnknownRequest })
            {
                Assert.Skip("This core has no marketplace yet: build the core again.");
            }

            using var ui = new UiThread();
            var events = new List<CoreEvent>();
            await ui.RunAsync(async () =>
            {
                var market = new MarketplaceModel(core.Client, _ => Task.CompletedTask);
                _ = PumpAsync(core.Client, market, events);

                Assert.True(await market.RefreshAsync(), market.Notice?.Detail);
                Assert.StartsWith(index, market.Source, StringComparison.OrdinalIgnoreCase);
                // build-index.ps1 packs every shipped theme of sdk/themes (five since 9e4ce98).
                Assert.Equal(ShippedThemeFiles(Path.Combine(Repo.Root, "sdk", "themes")).Count, market.CountOf(MarketTabs.Themes));
                // The shipped themes are in the themes folder already, but not from the marketplace:
                // shown as there, never replaced (trust rule 7), and not on the Installed tab.
                Assert.All(market.All.Where(i => i.Kind == ExtensionKinds.Theme), theme =>
                {
                    Assert.True(market.IsPresent(theme), theme.Id);
                    Assert.Null(theme.InstalledVersion);
                });
                Assert.Equal(0, market.CountOf(MarketTabs.Installed));
                var hello = market.Find("hello")!;
                Assert.Equal(MarketAction.Install, market.ActionFor(hello));
                // The core adds each capability's level for the review.
                Assert.All(hello.Capabilities!, capability => Assert.NotNull(capability.Level));

                await market.QueryChangedAsync("hel");
                Assert.Equal("hello", market.Items[0].Id);

                var review = PermissionReview.ForInstall(hello);
                var installed = await market.InstallAndGrantAsync("hello", review.ToGrant);
                Assert.True(installed.Ok, installed.Error);
                Assert.Equal(MarketAction.Installed, market.ActionFor(hello));
                Assert.True(File.Exists(Path.Combine(root, "plugins", "hello", "plugin.wasm")));
                await WaitUntilAsync(() => events.OfType<PluginStateChangedEvent>().Any(e => e.PluginId == "hello" && e.State.Type == PluginState.Active),
                    "hello to become active");
                Assert.Contains(events, e => e is InstallProgressEvent { ExtensionId: "hello" } progress && progress.Bytes == progress.Total);
                Assert.Contains(events, e => e is InstallFinishedEvent { ExtensionId: "hello", Ok: true });
                // The core's own record agrees with what the model concluded (protocol 11).
                Assert.True(await market.RefreshAsync());
                Assert.Equal(hello.Version, market.Find("hello")!.InstalledVersion);
                Assert.Equal((hello.Version, 1), (market.InstalledVersionOf(hello), market.CountOf(MarketTabs.Installed)));

                Assert.True((await market.UninstallAsync("hello")).Ok);
                Assert.False(market.IsInstalled(hello));
                Assert.False(File.Exists(Path.Combine(root, "plugins", "hello", "plugin.wasm")));
                Assert.True(await market.RefreshAsync());
                Assert.Null(market.Find("hello")!.InstalledVersion);
                Assert.Equal(0, market.CountOf(MarketTabs.Installed));
                return true;
            });
            await core.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    [Fact]
    public async Task The_real_core_lists_help_about_for_the_window_and_the_router_runs_the_windows_handler()
    {
        var coreExe = FindCoreOrSkip();
        var root = Repo.NewTempFolder("e2e-about");
        try
        {
            string requestId;
            await using (var core = await StartCoreAsync(coreExe, root))
            {
                await core.Client.HelloAsync();
                var router = new CommandRouter(core.Client);
                await router.RefreshAsync();
                var about = router.Find("help.about");
                Assert.NotNull(about);
                if (about.Target != "ui")
                {
                    Assert.Skip("This core runs help.about itself (before 2c80d5f): build the core again.");
                }
                // The palette's row runs the id through the router, which runs what the window registered.
                var opened = 0;
                router.RegisterUiHandler("help.about", _ => opened++);
                var outcome = await router.ExecuteAsync("help.about", trigger: "palette");
                Assert.Equal((CommandOutcomeKind.RanInUi, 1), (outcome.Kind, opened));
                requestId = outcome.RequestId;
                await core.ShutdownAsync(TimeSpan.FromSeconds(5));
            }
            // The core was never asked to run it.
            var coreLog = Directory.GetFiles(Path.Combine(root, "logs"), "core.*.jsonl").Single();
            Assert.DoesNotContain(requestId, File.ReadAllText(coreLog));
        }
        finally
        {
            Repo.RemoveTempFolder(root);
        }
    }

    // The core's own rule for a program identifier (cabinetos-fs, readable_type_name): no whitespace,
    // and either no capital letter or a name that ends in "file".
    private static bool IsProgramIdentifier(string name) =>
        !name.Any(char.IsWhiteSpace) && (!name.Any(char.IsUpper) || name.EndsWith("file", StringComparison.OrdinalIgnoreCase));

    internal static string FindCoreOrSkip()
    {
        var coreExe = CoreLauncher.Find(Path.Combine(Repo.Root, "ui"), Environment.GetEnvironmentVariable, File.Exists);
        if (coreExe is null)
        {
            Assert.Skip("No cabinetos-core.exe: set CABINETOS_CORE_EXE, or run `cargo build -p cabinetos-core` in core/.");
        }
        return coreExe;
    }

    internal static Task<CoreConnection> StartCoreAsync(string coreExe, string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var environment = new Dictionary<string, string>
        {
            ["CABINETOS_CONFIG"] = Path.Combine(root, "config", "cabinetos.json"),
            ["CABINETOS_LOG_DIR"] = Path.Combine(root, "logs"),
            ["CABINETOS_PLUGINS_DIR"] = Path.Combine(root, "plugins"),
            ["CABINETOS_PLUGINS_DATA_DIR"] = Path.Combine(root, "plugins-data"),
            ["CABINETOS_THEMES_DIR"] = Path.Combine(root, "themes"),
            ["CABINETOS_MARKETPLACE_DIR"] = Path.Combine(root, "marketplace"),
        };
        return CoreLauncher.StartAsync(coreExe, TimeSpan.FromSeconds(10), environment);
    }

    // sdk/marketplace/build-index.ps1: the fixture plugins and the shipped themes, hashed, into one folder.
    // The theme files of a themes folder, by name: one per theme, named by its ID. The schema and
    // the core's own record of what it wrote (.shipped.json) are not themes.
    private static List<string> ShippedThemeFiles(string folder) =>
    [
        .. Directory.GetFiles(folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && name != "theme.schema")
            .Order(StringComparer.Ordinal),
    ];

    private static void BuildLocalIndex(string folder)
    {
        var script = Path.Combine(Repo.Root, "sdk", "marketplace", "build-index.ps1");
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-OutDir", folder])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Started from PowerShell 7, the test would hand Windows PowerShell 7's module path, where
        // Get-FileHash is not found; without it, Windows PowerShell uses its own.
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "build-index.ps1 did not finish within 2 minutes");
        Assert.True(process.ExitCode == 0 && File.Exists(Path.Combine(folder, "index.json")), output + errors.Result);
    }

    // The window's event pump, for the marketplace: every core event goes to the model on the UI thread.
    private static async Task PumpAsync(CoreClient client, MarketplaceModel market, List<CoreEvent> seen)
    {
        await foreach (var coreEvent in client.Events.ReadAllAsync())
        {
            seen.Add(coreEvent);
            market.OnEvent(coreEvent);
            (coreEvent as ICarriesSection)?.TakeSection()?.Dispose();
        }
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
