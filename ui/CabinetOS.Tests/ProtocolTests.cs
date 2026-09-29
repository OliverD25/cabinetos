using System.Text;
using System.Text.Json;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Settings;
using CabinetOS.Tests.Support;
using Json.Schema;

namespace CabinetOS.Tests;

/// <summary>
/// The C# messages against the checked-in JSON Schemas, which the Rust types
/// generate: the schemas are the contract between the two sides.
/// </summary>
public class ProtocolTests
{
    private const string Id = "01J9ZQ4X7K3M5N8P2R6S0T1V4W";


    /// <summary>One instance of every request the UI sends.</summary>
    private static IReadOnlyList<CoreRequest> AllRequests()
    {
        using var args = JsonDocument.Parse("""{"path":"C:\\data\\a.txt"}""");
        return
        [
            new PingRequest(),
            new ShutdownRequest(),
            new HelloRequest(4242, "CabinetOS.exe"),
            new ListDirectoryRequest(@"C:\Users\me") { Watch = true },
            new ListDirectoryRequest(@"C:\Windows") { IncludeHidden = true, Sort = new SortSpec("modified", true), Watch = false },
            new CloseListingRequest(7),
            new VolumeInfoRequest(@"C:\"),
            new ListVolumesRequest(),
            new GetConfigRequest(),
            new GetKeymapRequest(),
            new ListCommandsRequest(),
            new SearchCommandsRequest("dual", 5),
            new ExecuteCommandRequest("help.about"),
            new ExecuteCommandRequest("reader.size") { Args = args.RootElement.Clone() },
            new SetKeybindingRequest("view.toggleSidebar", "ctrl+alt+b"),
            new ResetKeybindingRequest("view.toggleSidebar"),
            new GetValueRequest("ui.dualPane"),
            new SetValueRequest("ui.lastPaths", JsonDocument.Parse("""["C:\\Users\\me","D:\\work"]""").RootElement.Clone()),
            new StartJobRequest(JobKind.Copy, [@"C:\photos\a.jpg", @"C:\photos\b.jpg"]) { Destination = @"D:\backup" },
            new StartJobRequest(JobKind.Move, [@"C:\inbox"]) { Destination = @"D:\archive", Options = new JobOptions(OnConflict: "ask", Verify: true) },
            new StartJobRequest(JobKind.Delete(permanent: true), [@"C:\old"]),
            new ListJobsRequest(),
            new JobControlRequest(7, JobActions.Pause),
            new ResolveConflictRequest(7, 9, new Resolution(Resolution.RenameType, "b.txt")) { ApplyToSameKind = true },
            new ResolveConflictRequest(7, 10, new Resolution(Resolution.SkipType)),
            new TerminalOpenRequest(120, 30) { Profile = "pwsh", Cwd = @"E:\work" },
            new TerminalOpenRequest(80, 25),
            new TerminalResizeRequest(3, 100, 30),
            new TerminalCloseRequest(3),
            new TerminalSyncCwdRequest(3, @"D:\docs"),
            new TerminalListRequest(),
            new SearchRequest("budget") { Limit = 100, Root = @"C:\Users\me" },
            new SearchRequest("budget"),
            new IndexStatusRequest(),
            new ListPluginsRequest(),
            new ReloadPluginRequest("crashy"),
            new SetPluginEnabledRequest("reader", false),
            new GrantCapabilitiesRequest("reader", ["fs:read"]),
            new DescribeEntriesRequest(7, 0, 128),
            new GetIconRequest("ext:.txt", 24),
            new ListThemesRequest(),
            new GetThemeRequest(),
            new GetThemeRequest { ThemeId = "nord" },
            new ListToolsRequest(),
            new MarketplaceRefreshRequest(),
            new MarketplaceSearchRequest("nord") { Kind = ExtensionKinds.Theme },
            new MarketplaceSearchRequest(""),
            new InstallExtensionRequest("hello"),
            new InstallExtensionRequest("hello") { Version = "0.1.0" },
            new UninstallExtensionRequest("hello"),
            new ListDirectoryRequest(@"C:\photos") { Sort = new SortSpec("extension", false) },
            new CreateFileRequest(@"C:\data\New Text Document.txt"),
            new EditPathRequest(@"C:\data\build.cmd"),
            new ShowPropertiesRequest([@"C:\data\a.txt", @"C:\data\photos"]),
            new MeasurePathsRequest([@"C:\data\photos", @"C:\data\src"]),
            new CancelMeasureRequest(5),
            new MatchEntriesRequest(7, "*.txt;*.md|draft*") { FilesOnly = true },
            new MatchEntriesRequest(7, "rep*") { FirstFrom = 42 },
            new TerminalTypePathsRequest(3, [@"C:\data\a b.txt"]),
            new WindowStateRequest("left", new WindowPanesState(
                new WindowPaneState([new WindowTabState(@"C:\Users\me", false, null), new WindowTabState(@"E:\work\README.md", true, "md-preview")], 1, @"C:\Users\me\a.txt", [@"C:\Users\me\a.txt"]),
                new WindowPaneState([new WindowTabState(@"D:\", false, null)], 0, null, []))),
            new OpenPreviewRequest("preview-3"),
            new PreviewApplyRequest("preview-3"),
            new PreviewCancelRequest("preview-3"),
        ];
    }

    private const string HelloItem = """{"id":"hello","kind":"plugin","name":"Hello","author":{"name":"CabinetOS","verified":false},"version":"0.1.0","description":"The sample Core Plugin.","long":"The sample Core Plugin: a Say Hello command that answers.","size":27003,"download":{"url":"files/hello-0.1.0.zip","sha256":"2aaaf9704b4472089bf1e296437107bf9a08e5cc902acdde8c445e95df89da66"},"manifest":{"id":"hello","name":"Hello","version":"0.1.0","capabilities":[{"name":"cmd:register","reason":"Adds the Say Hello command."}]},"capabilities":[{"name":"cmd:register","reason":"Adds the Say Hello command.","level":"low"},{"name":"fs:read","reason":"Reads its folder.","roots":["%TEMP%\\hello"],"level":"medium"}],"minCoreVersion":"0.1.0","license":"MIT","installedVersion":"0.1.0"}""";

    private const string NordItem = """{"id":"nord","kind":"theme","name":"Nord","author":{"name":"Arctic Ice Studio","verified":true,"url":"https://www.nordtheme.com"},"version":"1.0.0","description":"An arctic, north-bluish palette.","size":1527,"download":{"url":"https://example.org/nord-1.0.0.json","sha256":"c1c0d89ae34d347c5e333dccee13f28e1967521f9cba349ebcf74c7afc4dd0fb"},"manifest":{"id":"nord","name":"Nord","author":"CabinetOS","version":"1.0.0","kind":"dark","accent":"#88C0D0"},"rating":{"average":4.8,"count":120},"installs":5400,"minCoreVersion":"0.1.0","license":"MIT"}""";

    private const string NordTheme = """{"id":"nord","name":"Nord","author":"CabinetOS","attribution":"Colours from the Nord palette.","version":"1.0.0","kind":"dark","accent":"#88C0D0","mica":{"tint":"#2E3440","opacity":0.88},"palette":{"textPrimary":"#ECEFF4","textSecondary":"#D8DEE9","textTertiary":"#D8DEE98B","textDisabled":"#D8DEE95D","layerFill":"#3B425280","layerStroke":"#434C5E99","layerStrokeActive":"#4C566A","controlFill":"#3B4252B3","controlFillHover":"#434C5EB3","acrylicTint":"#2E3440B8","terminalBackground":"#2E344099","folderIcon":"#EBCB8B","folderIconFront":"#F2DDB4","fileTypeColors":{"md":"#88C0D0","rs":"#D08770","toml":"#B48EAD","exe":"#A3BE8C","dll":"#A3BE8C","bin":"#BF616A","pdf":"#BF616A","zip":"#EBCB8B"},"permissionLow":"#A3BE8C","permissionMedium":"#EBCB8B","permissionHigh":"#BF616A"},"terminal":{"foreground":"#D8DEE9","background":"#2E3440","cursor":"#D8DEE9","ansi":["#3B4252","#BF616A","#A3BE8C","#EBCB8B","#81A1C1","#B48EAD","#88C0D0","#E5E9F0","#4C566A","#BF616A","#A3BE8C","#EBCB8B","#81A1C1","#B48EAD","#8FBCBB","#ECEFF4"]}}""";

    /// <summary>
    /// Requests of protocol version 8 built against the shapes the core agreed
    /// on before its schema had them: their JSON exactly as the UI sends it.
    /// </summary>
    private static IReadOnlyList<(CoreRequest Request, string Json)> AgreedRequests() =>
    [
        (new OpenPathRequest(@"C:\data\report.pdf"), $$$"""{"id":"{{{Id}}}","type":"open_path","path":"C:\\data\\report.pdf"}"""),
        (new CreateDirectoryRequest(@"C:\data\New folder"), $$$"""{"id":"{{{Id}}}","type":"create_directory","path":"C:\\data\\New folder"}"""),
        (new RenameRequest(@"C:\data\a.txt", "b.txt"), $$$"""{"id":"{{{Id}}}","type":"rename","path":"C:\\data\\a.txt","new_name":"b.txt"}"""),
        // Protocol 12, as docs/research/total-commander.md, Part 3 (b), gives them, until the core's schema has them.
        // Phase 15's log bundle, as the core's agent shaped it.
        (new SaveLogBundleRequest(10), $$$"""{"id":"{{{Id}}}","type":"save_log_bundle","minutes":10}"""),
    ];

    /// <summary>
    /// Replies and events of protocol version 12 built against the research
    /// note's shapes: (JSON, is an event, check). Checked against the core's
    /// schemas once they have the type.
    /// </summary>
    private static IReadOnlyList<(string Json, bool IsEvent, Action<object> Check)> AgreedIncoming() =>
    [
        ($$$"""{"id":"{{{Id}}}","type":"entry_matches","listing_id":7,"generation":2,"ranges":[[0,3],[10,1]]}""", false,
            b => Assert.Equal((7UL, 2U, 4UL), (((EntryMatchesReply)b).ListingId, ((EntryMatchesReply)b).Generation, Core.Listing.EntryRanges.Count(((EntryMatchesReply)b).Ranges)))),
        ($$$"""{"id":"{{{Id}}}","type":"measure_started","measure_id":5}""", false,
            b => Assert.Equal(new MeasureStartedReply(5), b)),
        ($$$"""{"id":"{{{Id}}}","type":"measure_progress","measure_id":5,"path":"C:\\data\\photos","files":120,"folders":4,"bytes":9000000}""", true,
            b => Assert.Equal(new MeasureProgressEvent(5, @"C:\data\photos", 120, 4, 9000000), b)),
        ($$$"""{"id":"{{{Id}}}","type":"measure_finished","measure_id":5,"results":[{"path":"C:\\data\\photos","files":130,"folders":4,"bytes":9500000,"unreadable":1}],"cancelled":false}""", true,
            b =>
            {
                var finished = Assert.IsType<MeasureFinishedEvent>(b);
                Assert.False(finished.Cancelled);
                Assert.Equal(new MeasureResult(@"C:\data\photos", 130, 4, 9500000, 1), finished.Results.Single());
            }),
        ($$$"""{"id":"{{{Id}}}","type":"log_bundle","path":"C:\\logs\\bundle-20260930T010203004Z.zip"}""", false,
            b => Assert.Equal(new LogBundleReply(@"C:\logs\bundle-20260930T010203004Z.zip"), b)),
    ];

    [Fact]
    public void The_agreed_version_12_replies_and_events_decode_and_follow_the_schemas_once_they_have_them()
    {
        var replies = SchemaVariants(Repo.ProtocolSchema("response.schema.json"));
        var events = SchemaVariants(Repo.ProtocolSchema("event.schema.json"));
        foreach (var (json, isEvent, check) in AgreedIncoming())
        {
            var message = MessageCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.Equal(isEvent, message.IsEvent);
            Assert.NotNull(message.Body);
            check(message.Body);
            if ((isEvent ? events : replies).ContainsKey(message.Type!))
            {
                AssertValid(isEvent ? Schemas.Event : Schemas.Response, json);
            }
        }
    }

    [Fact]
    public void Every_request_matches_the_request_schema_and_uses_only_declared_fields()
    {
        var variants = SchemaVariants(Repo.ProtocolSchema("request.schema.json"));
        var checkedTypes = new HashSet<string>();
        foreach (var request in AllRequests())
        {
            request.Id = Id;
            var json = Encoding.UTF8.GetString(MessageCodec.Encode(request));
            Assert.True(variants.TryGetValue(request.Type, out var declared), $"{request.Type} is not in the request schema");
            AssertValid(Schemas.Request, json);
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                Assert.True(property.Name == "id" || declared.Contains(property.Name),
                    $"{request.Type} sends `{property.Name}`, which the schema does not declare: {json}");
            }
            checkedTypes.Add(request.Type);
        }
        Assert.Equal(51, checkedTypes.Count);
    }

    [Fact]
    public void The_agreed_version_8_requests_follow_the_schema_once_it_has_them()
    {
        var variants = SchemaVariants(Repo.ProtocolSchema("request.schema.json"));
        foreach (var (request, expected) in AgreedRequests())
        {
            request.Id = Id;
            var json = Encoding.UTF8.GetString(MessageCodec.Encode(request));
            if (variants.TryGetValue(request.Type, out var declared))
            {
                AssertValid(Schemas.Request, json);
                using var document = JsonDocument.Parse(json);
                Assert.All(document.RootElement.EnumerateObject(), property =>
                    Assert.True(property.Name == "id" || declared.Contains(property.Name), $"{request.Type} sends `{property.Name}`: {json}"));
            }
            else
            {
                Assert.Equal(expected, json);
            }
        }
    }

    [Fact]
    public void A_job_request_leaves_its_defaults_to_the_core()
    {
        var copy = new StartJobRequest(JobKind.Copy, [@"C:\a"]) { Destination = @"D:\b", Id = Id };
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"start_job","kind":{"type":"copy"},"sources":["C:\\a"],"destination":"D:\\b"}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(copy)));
        var delete = new StartJobRequest(JobKind.Delete(permanent: false), [@"C:\a"]) { Id = Id };
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"start_job","kind":{"type":"delete","permanent":false},"sources":["C:\\a"]}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(delete)));
        var resolve = new ResolveConflictRequest(7, 9, new Resolution(Resolution.OverwriteType)) { Id = Id };
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"resolve_conflict","job_id":7,"conflict_id":9,"resolution":{"type":"overwrite"},"apply_to_same_kind":false}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(resolve)));
    }

    [Fact]
    public void A_request_puts_id_and_type_first_and_leaves_absent_options_out()
    {
        Assert.Equal($$$"""{"id":"{{{Id}}}","type":"ping"}""", Encoding.UTF8.GetString(MessageCodec.Encode(new PingRequest { Id = Id })));
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"list_directory","path":"C:\\Users","watch":true}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(new ListDirectoryRequest(@"C:\Users") { Id = Id, Watch = true })));
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"hello","client_pid":42,"client_name":"CabinetOS.exe"}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(new HelloRequest(42, "CabinetOS.exe") { Id = Id })));
        Assert.Equal(
            $$$"""{"id":"{{{Id}}}","type":"execute_command","command":"help.about"}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(new ExecuteCommandRequest("help.about") { Id = Id })));
    }

    [Fact]
    public void Sample_replies_match_the_response_schema_and_decode()
    {
        var samples = new (string Json, Action<object> Check)[]
        {
            ($$$"""{"id":"{{{Id}}}","type":"pong","protocol_version":7,"core_version":"0.1.0"}""",
                b => Assert.Equal(new PongReply(7, "0.1.0"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"ok"}""", b => Assert.IsType<OkReply>(b)),
            ($$$"""{"id":"{{{Id}}}","type":"error","code":"unknown_request","message":"unknown request type `list_volumes`"}""",
                b => Assert.Equal(new ErrorReply(ErrorCodes.UnknownRequest, "unknown request type `list_volumes`"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"welcome","protocol_version":7,"core_version":"0.1.0"}""",
                b => Assert.Equal(new WelcomeReply(7, "0.1.0"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"listing_opened","listing_id":7,"section_handle":1188,"section_size":8484488,"entry_count":100000,"generation":1,"elapsed_us":67310}""",
                b =>
                {
                    var opened = Assert.IsType<ListingOpenedReply>(b);
                    Assert.Equal((7UL, 1188UL, 8484488UL, 100000U, 1U, 67310UL),
                        (opened.ListingId, opened.SectionHandle, opened.SectionSize, opened.EntryCount, opened.Generation, opened.ElapsedUs));
                    Assert.Null(opened.Section);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"volume_info","drive_letter":"H","volume_guid_path":"\\\\?\\Volume{ff50b21c-0000-0000-0000-100000000000}\\","filesystem":"NTFS","label":"HHD","total_bytes":12000013840384,"free_bytes":10391482793984,"disk":{"device_number":0,"bus_type":"SATA","seek_penalty":true,"media_type":"HDD"}}""",
                b =>
                {
                    var volume = Assert.IsType<VolumeInfoReply>(b);
                    Assert.Equal("H", volume.DriveLetter);
                    Assert.Equal(12000013840384UL, volume.TotalBytes);
                    Assert.Equal(new DiskIdentity(0, "SATA", true, "HDD"), volume.Disk);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"volume_info","drive_letter":null,"volume_guid_path":"","filesystem":"NTFS","label":"","total_bytes":1,"free_bytes":0,"disk":null}""",
                b => Assert.Null(Assert.IsType<VolumeInfoReply>(b).Disk)),
            ("""{"id":"@ID","type":"config","path":"C:\\Users\\me\\AppData\\Roaming\\CabinetOS\\cabinetos.json","config":{"version":1,"ui":{"layout":"classic","dualPane":false,"sidebar":true,"theme":"default"},"panes":{"showHidden":true,"sort":{"key":"size","descending":true}}}}""".Replace("@ID", Id),
                b =>
                {
                    var settings = UiSettings.FromConfig(Assert.IsType<ConfigReply>(b).Config);
                    Assert.Equal(new UiSettings("classic", false, true, "default", true, "size", true), settings);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"keymap","chord_window_ms":1000,"bindings":[{"keys":"ctrl+shift+p","command":"palette.show"},{"keys":"f5","command":"file.copyToOtherPane","when":"filesView"}],"immutable":["palette.show","overlay.close","keys.open"]}""",
                b =>
                {
                    var keymap = Assert.IsType<KeymapReply>(b);
                    Assert.Equal(1000U, keymap.ChordWindowMs);
                    Assert.Equal(new KeymapBinding("f5", "file.copyToOtherPane", "filesView"), keymap.Bindings[1]);
                    Assert.Null(keymap.Bindings[0].When);
                    Assert.Equal(["palette.show", "overlay.close", "keys.open"], keymap.Immutable);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"commands","commands":[{"id":"view.toggleDualPane","category":"View","title":"Toggle Dual Pane","keys":["ctrl+shift+d"],"default_keys":["ctrl+shift+d"],"source":{"kind":"core"},"target":"ui","immutable":false},{"id":"reader.size","category":"Reader","title":"Size","keys":[],"default_keys":[],"source":{"kind":"plugin","id":"reader","name":"Reader"},"target":"core","when":"filesView","immutable":false}]}""",
                b =>
                {
                    var commands = Assert.IsType<CommandsReply>(b).Commands;
                    Assert.Equal("ui", commands[0].Target);
                    Assert.Equal(new CommandSource("core", null, null), commands[0].Source);
                    Assert.Equal(new CommandSource("plugin", "reader", "Reader"), commands[1].Source);
                    Assert.Equal("filesView", commands[1].When);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"search_results","hits":[{"id":"view.toggleDualPane","score":137}]}""",
                b => Assert.Equal(new SearchHit("view.toggleDualPane", 137), Assert.IsType<SearchResultsReply>(b).Hits.Single())),
            ($$$"""{"id":"{{{Id}}}","type":"command_routed","target":"ui"}""",
                b => Assert.Equal("ui", Assert.IsType<CommandRoutedReply>(b).Target)),
            ($$$"""{"id":"{{{Id}}}","type":"command_result","result":{"name":"CabinetOS","core_version":"0.1.0","protocol_version":7,"config_path":"C:\\x\\cabinetos.json"}}""",
                b => Assert.Equal("CabinetOS", Assert.IsType<CommandResultReply>(b).Result.GetProperty("name").GetString())),
            ($$$"""{"id":"{{{Id}}}","type":"value","value":["C:\\Users\\me"]}""",
                b => Assert.Equal(@"C:\Users\me", Assert.IsType<ValueReply>(b).Value[0].GetString())),
            ($$$"""{"id":"{{{Id}}}","type":"job_started","job_id":7}""",
                b => Assert.Equal(new JobStartedReply(7), b)),
            ($$$"""{"id":"{{{Id}}}","type":"jobs","jobs":[{"kind":{"type":"delete","permanent":true},"sources":["C:\\old"],"destination":null,"job_id":7,"state":{"type":"running"},"bytes_done":0,"bytes_total":0,"files_done":3,"files_total":9,"files_skipped":0,"files_failed":0,"conflicts_open":0,"current_path":"C:\\old\\c.txt","speed_bps":0,"elapsed_ms":120}]}""",
                b =>
                {
                    var job = Assert.IsType<JobsReply>(b).Jobs.Single();
                    Assert.True(job.Kind.IsPermanentDelete);
                    Assert.Equal([@"C:\old"], job.Sources);
                    Assert.Null(job.Destination);
                    Assert.Equal((7UL, 3UL, 9UL), (job.ToProgress().JobId, job.ToProgress().FilesDone, job.ToProgress().FilesTotal));
                    Assert.Null(job.EtaSeconds);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"entry_details","listing_id":7,"generation":1,"from":0,"details":[{"type_name":"File folder","icon_key":"folder"},{"type_name":"Application","icon_key":"path:396bbcd455199596"}]}""",
                b =>
                {
                    var details = Assert.IsType<EntryDetailsReply>(b);
                    Assert.Equal((7UL, 1U, 0U), (details.ListingId, details.Generation, details.From));
                    Assert.Equal(new EntryDetail("Application", "path:396bbcd455199596"), details.Details[1]);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"icon","key":"ext:.txt","size":32,"png_base64":"iVBORw0KGgo="}""",
                b => Assert.Equal(new IconReply("ext:.txt", 32, "iVBORw0KGgo="), b)),
            ($$$"""{"id":"{{{Id}}}","type":"terminal_opened","session_id":3,"pipe":"\\\\.\\pipe\\cabinetos-term-9f3c01a2b4d5e6f7","pid":4242}""",
                b => Assert.Equal(new TerminalOpenedReply(3, @"\\.\pipe\cabinetos-term-9f3c01a2b4d5e6f7", 4242), b)),
            ($$$"""{"id":"{{{Id}}}","type":"terminal_sessions","sessions":[{"session_id":3,"profile":"pwsh","cwd":"E:\\work","cols":120,"rows":30,"pid":4242,"state":{"type":"exited","code":3221225786},"pipe":"\\\\.\\pipe\\cabinetos-term-9f3c01a2b4d5e6f7","attached":false}]}""",
                b =>
                {
                    var session = Assert.IsType<TerminalSessionsReply>(b).Sessions.Single();
                    Assert.Equal(new TerminalState(TerminalState.Exited, 3221225786), session.State);
                    Assert.Equal((120, 30), (session.Cols, session.Rows));
                }),
            ($$$"""{"id":"{{{Id}}}","type":"file_search_results","hits":[{"path":"C:\\Users\\me\\Budget-2026.xlsx","kind":"file","frn":1407374883553540},{"path":"C:\\Users\\me\\old\\budget","kind":"directory"}],"source":"index","took_us":1210,"complete":true}""",
                b =>
                {
                    var results = Assert.IsType<FileSearchResultsReply>(b);
                    Assert.Equal((FileSearchResultsReply.FromIndex, 1210UL, true), (results.Source, results.TookUs, results.Complete));
                    Assert.False(results.Hits[0].IsFolder);
                    Assert.True(results.Hits[1].IsFolder);
                    Assert.Null(results.Hits[1].Frn);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"index_status","available":true,"volumes":[{"letter":"C","state":{"type":"ready"},"entries":1357918,"built_in_ms":3480,"journal_lag":0}]}""",
                b => Assert.Equal(("C", "ready"), (Assert.IsType<IndexStatusReply>(b).Volumes.Single().Letter, ((IndexStatusReply)b).Volumes.Single().State.Type))),
            ($$$"""{"id":"{{{Id}}}","type":"plugins","plugins":[{"id":"reader","name":"Reader","version":"0.1.0","author":"CabinetOS tests","description":"Reads the size of files in one folder.","state":{"type":"needs_review","missing":["fs:read"]},"capabilities":[{"name":"cmd:register","level":"low","granted":true,"reason":"Adds the Size command."},{"name":"fs:read","level":"medium","granted":false,"reason":"Reads the size of files in its test folder.","roots":["%TEMP%\\cabinetos-plugins-test\\reader"]}],"commands":[]}]}""",
                b =>
                {
                    var plugin = Assert.IsType<PluginsReply>(b).Plugins.Single();
                    Assert.Equal(PluginState.NeedsReview, plugin.State.Type);
                    Assert.Equal(["fs:read"], plugin.State.Missing);
                    Assert.Equal(("fs:read", "medium", false), (plugin.Capabilities[1].Name, plugin.Capabilities[1].Level, plugin.Capabilities[1].Granted));
                    Assert.Single(plugin.Capabilities[1].Roots!);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"themes","themes":[{"id":"default","name":"Default","author":"CabinetOS","version":"1.1.0","kind":"system","accent":null,"mica":null},{"id":"nord","name":"Nord","author":"CabinetOS","version":"1.0.0","kind":"dark","accent":"#88C0D0","mica":{"tint":"#2E3440","opacity":0.88}}]}""",
                b =>
                {
                    var themes = Assert.IsType<ThemesReply>(b).Themes;
                    Assert.Equal(new ThemeInfo("default", "Default", "CabinetOS", "1.1.0", ColorTheme.System), themes[0]);
                    Assert.Equal("#88C0D0", themes[1].Accent);
                    // Protocol 11: the Mica tint comes with the list.
                    Assert.Equal(new MicaTint("#2E3440", 0.88), themes[1].Mica);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"theme","theme":{{{NordTheme}}}}""",
                b =>
                {
                    var theme = Assert.IsType<ThemeReply>(b).Theme;
                    Assert.Equal(("nord", "#88C0D0", 0.88), (theme.Id, theme.Accent, theme.Mica!.Opacity));
                    Assert.Equal(("#D8DEE98B", "#F2DDB4"), (theme.Palette.TextTertiary, theme.Palette.FolderIconFront));
                    Assert.Equal("#BF616A", theme.Palette.FileTypeColors["pdf"]);
                    Assert.Equal(16, theme.Terminal.Ansi.Count);
                    Assert.False(theme.IsLight);
                    Assert.Null(theme.Metrics);
                    Assert.Null(theme.Chrome);
                }),
            // The theme format's version 2: a density preset says so in the list, and brings its sizes and chrome.
            ($$$"""{"id":"{{{Id}}}","type":"themes","themes":[{"id":"commander-compact","name":"Commander Compact","author":"CabinetOS","version":"1.0.0","kind":"system","accent":null,"mica":null,"has_metrics":true},{"id":"default","name":"Default","author":"CabinetOS","version":"1.1.0","kind":"system","accent":null,"mica":null,"has_metrics":false}]}""",
                b =>
                {
                    var themes = Assert.IsType<ThemesReply>(b).Themes;
                    Assert.Equal((true, false), (themes[0].HasMetrics, themes[1].HasMetrics));
                }),
            ($$$"""{"id":"{{{Id}}}","type":"theme","theme":{{{NordTheme[..^1]}}},"metrics":{"rowHeight":20,"lineHeight":1.3},"chrome":{"fkeyBar":true,"hairlines":false} } }""",
                b =>
                {
                    var theme = Assert.IsType<ThemeReply>(b).Theme;
                    Assert.Equal((20.0, 1.3), (theme.Metrics!["rowHeight"], theme.Metrics["lineHeight"]));
                    Assert.Equal(new ThemeChrome(FkeyBar: true, Hairlines: false), theme.Chrome);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"marketplace_index","source":"C:\\market\\index.json","fetched_at_ms":1790000000000,"items":[{{{HelloItem}}},{{{NordItem}}}]}""",
                b =>
                {
                    var index = Assert.IsType<MarketplaceIndexReply>(b);
                    Assert.Equal((@"C:\market\index.json", 1790000000000UL), (index.Source, index.FetchedAtMs));
                    var (hello, nord) = (index.Items[0], index.Items[1]);
                    Assert.Equal((ExtensionKinds.Plugin, "0.1.0", 27003UL, "MIT"), (hello.Kind, hello.MinCoreVersion, hello.Size, hello.License));
                    Assert.Equal(new MarketCapability("cmd:register", "Adds the Say Hello command.", "low"), hello.Capabilities![0]);
                    Assert.Equal(["%TEMP%\\hello"], hello.Capabilities[1].Roots);
                    Assert.Null(hello.Rating);
                    Assert.Null(hello.Author.Url);
                    Assert.Equal("files/hello-0.1.0.zip", hello.Download.Url);
                    Assert.Equal(("", 4.8, 120UL, 5400UL), (nord.Long, nord.Rating!.Average, nord.Rating.Count, nord.Installs!.Value));
                    Assert.Equal(new MarketAuthor("Arctic Ice Studio", true, "https://www.nordtheme.com"), nord.Author);
                    Assert.Equal("#88C0D0", nord.Manifest.GetProperty("accent").GetString());
                    Assert.Null(nord.Capabilities);
                    // Protocol 11: camelCase like the index's keys, absent when the marketplace installed none.
                    Assert.Equal("0.1.0", hello.InstalledVersion);
                    Assert.Null(nord.InstalledVersion);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"tools","tools":[{"id":"markdown-preview","name":"Markdown Preview","version":"1.0.0","author":"CabinetOS","description":"Shows Markdown.","dir":"C:\\Users\\me\\AppData\\Local\\CabinetOS\\tools\\markdown-preview"}]}""",
                b => Assert.Equal(("markdown-preview", "1.0.0"), (Assert.IsType<ToolsReply>(b).Tools.Single().Id, ((ToolsReply)b).Tools.Single().Version))),
            ($$$"""{"id":"{{{Id}}}","type":"preview_opened","preview":"preview-3","title":"Sort the photos","listing":{"listing_id":9,"section_handle":0,"section_size":736,"entry_count":3,"generation":1,"elapsed_us":210}}""",
                b =>
                {
                    var opened = Assert.IsType<PreviewOpenedReply>(b);
                    Assert.Equal(("preview-3", "Sort the photos", 3U, 736UL), (opened.Preview, opened.Title, opened.Listing.EntryCount, opened.SectionSize));
                }),
            ($$$"""{"id":"{{{Id}}}","type":"jobs_started","jobs":[12,13]}""",
                b => Assert.Equal([12UL, 13UL], Assert.IsType<JobsStartedReply>(b).Jobs)),
            ($$$"""{"id":"{{{Id}}}","type":"plugins","plugins":[{"id":"agent","name":"Agent","version":"0.1.0","author":"CabinetOS","description":"Works next to you.","state":{"type":"needs_review","missing":["net"]},"capabilities":[{"name":"net","level":"high","granted":false,"reason":"Asks the model provider.","hosts":["api.anthropic.com","localhost:11434"]}],"commands":[]}]}""",
                b => Assert.Equal(["api.anthropic.com", "localhost:11434"], Assert.IsType<PluginsReply>(b).Plugins.Single().Capabilities.Single().Hosts)),
        };
        foreach (var (json, check) in samples)
        {
            AssertValid(Schemas.Response, json);
            var message = MessageCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.False(message.IsEvent, json);
            Assert.Equal(Id, message.Id);
            Assert.NotNull(message.Body);
            check(message.Body);
        }
    }

    [Fact]
    public void Sample_events_match_the_event_schema_and_decode()
    {
        var samples = new (string Json, Action<object> Check)[]
        {
            ($$$"""{"id":"{{{Id}}}","type":"listing_refreshed","listing_id":7,"section_handle":1204,"section_size":8484544,"entry_count":100001,"generation":2,"reason":"changed"}""",
                b => Assert.Equal((7UL, 2U, "changed"), (((ListingRefreshedEvent)b).ListingId, ((ListingRefreshedEvent)b).Generation, ((ListingRefreshedEvent)b).Reason))),
            ($$$"""{"id":"{{{Id}}}","type":"listing_lost","listing_id":7,"message":"C:\\gone: not found"}""",
                b => Assert.Equal(new ListingLostEvent(7, @"C:\gone: not found"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"config_changed","changed":["ui.dualPane","keybindings"]}""",
                b => Assert.Equal(["ui.dualPane", "keybindings"], ((ConfigChangedEvent)b).Changed)),
            ($$$"""{"id":"{{{Id}}}","type":"config_error","line":3,"column":13,"message":"unknown field `dualPan`"}""",
                b => Assert.Equal(new ConfigErrorEvent(3, 13, "unknown field `dualPan`"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"config_error","line":null,"column":null,"message":"the file was deleted"}""",
                b => Assert.Equal(new ConfigErrorEvent(null, null, "the file was deleted"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"keymap_changed","keymap":{"chord_window_ms":1000,"bindings":[{"keys":"ctrl+alt+b","command":"view.toggleSidebar"}],"immutable":["palette.show"]}}""",
                b => Assert.Equal("ctrl+alt+b", ((KeymapChangedEvent)b).Keymap.Bindings.Single().Keys)),
            ($$$"""{"id":"{{{Id}}}","type":"job_progress","job_id":7,"state":{"type":"running"},"bytes_done":1200000000,"bytes_total":2700000000,"files_done":8412,"files_total":10001,"files_skipped":0,"files_failed":0,"conflicts_open":1,"current_path":"C:\\photos\\big.raw","speed_bps":610000000,"eta_seconds":3,"elapsed_ms":2400}""",
                b =>
                {
                    var progress = Assert.IsType<JobProgressEvent>(b);
                    Assert.Equal(new JobState("running", null), progress.State);
                    Assert.Equal(3UL, progress.EtaSeconds);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"job_progress","job_id":7,"state":{"type":"failed","message":"the disk is gone"},"bytes_done":0,"bytes_total":0,"files_done":0,"files_total":0,"files_skipped":0,"files_failed":0,"conflicts_open":0,"speed_bps":0,"elapsed_ms":1}""",
                b =>
                {
                    Assert.Equal(new JobState("failed", "the disk is gone"), ((JobProgressEvent)b).State);
                    Assert.Null(((JobProgressEvent)b).ItemsPerSecond);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"job_progress","job_id":8,"state":{"type":"running"},"bytes_done":0,"bytes_total":0,"files_done":800,"files_total":5000,"files_skipped":0,"files_failed":0,"conflicts_open":0,"current_path":"C:\\old\\a.txt","speed_bps":0,"items_per_second":412.5,"elapsed_ms":2000}""",
                b => Assert.Equal(412.5, Assert.IsType<JobProgressEvent>(b).ItemsPerSecond)),
            ($$$"""{"id":"{{{Id}}}","type":"plugin_state_changed","plugin_id":"crashy","state":{"type":"crashed","message":"wasm trap","at_ms":1790553600000}}""",
                b => Assert.Equal(new PluginState(PluginState.Crashed, Message: "wasm trap", AtMs: 1790553600000), ((PluginStateChangedEvent)b).State with { Missing = null })),
            ($$$"""{"id":"{{{Id}}}","type":"plugin_crashed","plugin_id":"crashy","message":"wasm trap: unreachable"}""",
                b => Assert.Equal(new PluginCrashedEvent("crashy", "wasm trap: unreachable"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"terminal_exited","session_id":3,"exit_code":0}""",
                b => Assert.Equal(new TerminalExitedEvent(3, 0), b)),
            ($$$"""{"id":"{{{Id}}}","type":"volumes_changed","volumes":[{"drive_letter":"F","volume_guid_path":"\\\\?\\Volume{2}\\","filesystem":"exFAT","label":"STICK","total_bytes":64000000000,"free_bytes":1000,"disk":null}]}""",
                b => Assert.Equal(("F", "STICK"), (((VolumesChangedEvent)b).Volumes.Single().DriveLetter, ((VolumesChangedEvent)b).Volumes.Single().Label))),
            ($$$"""{"id":"{{{Id}}}","type":"job_state_changed","job_id":7,"state":{"type":"completed_with_errors"}}""",
                b =>
                {
                    var changed = Assert.IsType<JobStateChangedEvent>(b);
                    Assert.Equal(new JobState(JobState.CompletedWithErrors), changed.State);
                    Assert.True(changed.State.IsFinal);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"job_conflict","conflict_id":9,"job_id":7,"kind":{"type":"file_exists","source_size":10,"source_modified":133000000000000000,"dest_size":12,"dest_modified":132000000000000000},"source":"C:\\photos\\a.jpg","destination":"D:\\backup\\photos\\a.jpg"}""",
                b =>
                {
                    var conflict = Assert.IsType<JobConflictEvent>(b);
                    Assert.Equal((9UL, 7UL), (conflict.ConflictId, conflict.JobId));
                    Assert.Equal(new ConflictKind(ConflictKind.FileExists, 10, 133000000000000000, 12, 132000000000000000), conflict.Kind);
                    Assert.Equal(@"D:\backup\photos\a.jpg", conflict.Destination);
                }),
            ($$$"""{"id":"{{{Id}}}","type":"job_conflict","conflict_id":10,"job_id":8,"kind":{"type":"recycle_bin_too_small","size":22548578304},"source":"E:\\big.iso","destination":null}""",
                b => Assert.Equal(22548578304UL, Assert.IsType<JobConflictEvent>(b).Kind.Size)),
            ($$$"""{"id":"{{{Id}}}","type":"job_conflict","conflict_id":11,"job_id":8,"kind":{"type":"io","code":23,"message":"Data error (cyclic redundancy check)."},"source":"E:\\x.bin"}""",
                b => Assert.Equal((23U, "Data error (cyclic redundancy check)."), (((JobConflictEvent)b).Kind.Code!.Value, ((JobConflictEvent)b).Kind.Message))),
            ($$$"""{"id":"{{{Id}}}","type":"theme_changed","theme":{{{NordTheme}}}}""",
                b => Assert.Equal("#2E3440", Assert.IsType<ThemeChangedEvent>(b).Theme.Mica!.Tint)),
            ($$$"""{"id":"{{{Id}}}","type":"install_progress","extension_id":"hello","bytes":13500,"total":27003}""",
                b => Assert.Equal(new InstallProgressEvent("hello", 13500, 27003), b)),
            ($$$"""{"id":"{{{Id}}}","type":"install_finished","extension_id":"hello","ok":true,"message":"installed hello 0.1.0 (plugin)"}""",
                b => Assert.Equal(new InstallFinishedEvent("hello", true, "installed hello 0.1.0 (plugin)"), b)),
            // Still protocol 11: the record's version after the install.
            ($$$"""{"id":"{{{Id}}}","type":"install_finished","extension_id":"hello","ok":false,"message":"hash mismatch","installed_version":"0.1.0"}""",
                b => Assert.Equal(new InstallFinishedEvent("hello", false, "hash mismatch", "0.1.0"), b)),
            ($$$"""{"id":"{{{Id}}}","type":"tools_changed","tools":[]}""",
                b => Assert.Empty(Assert.IsType<ToolsChangedEvent>(b).Tools)),
            ($$$"""{"id":"{{{Id}}}","type":"plugin_event","plugin_id":"agent","name":"agent.notice","payload":"{\"text\":\"Renamed 3 files\"}"}""",
                b => Assert.Equal(new PluginEventEvent("agent", "agent.notice", """{"text":"Renamed 3 files"}"""), b)),
            ($$$"""{"id":"{{{Id}}}","type":"preview_applied","preview":"preview-3","jobs":[12,13]}""",
                b => Assert.Equal([12UL, 13UL], Assert.IsType<PreviewAppliedEvent>(b).Jobs)),
            ($$$"""{"id":"{{{Id}}}","type":"preview_cancelled","preview":"preview-4"}""",
                b => Assert.Equal(new PreviewCancelledEvent("preview-4"), b)),
        };
        foreach (var (json, check) in samples)
        {
            AssertValid(Schemas.Event, json);
            var message = MessageCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.True(message.IsEvent, json);
            Assert.NotNull(message.Body);
            check(message.Body);
        }
    }

    [Fact]
    public void Every_event_type_of_the_schema_is_known_as_an_event()
    {
        var eventTypes = SchemaVariants(Repo.ProtocolSchema("event.schema.json")).Keys.ToHashSet();
        var replyTypes = SchemaVariants(Repo.ProtocolSchema("response.schema.json")).Keys.ToHashSet();
        // The version 12 events the window was built against before the core's schema had them.
        string[] agreed = ["measure_progress", "measure_finished"];
        Assert.Equal(eventTypes.Union(agreed).Order(), MessageCodec.EventTypes.Order());
        Assert.Empty(replyTypes.Intersect(MessageCodec.EventTypes));
    }

    [Fact]
    public void Unknown_types_are_ignored_not_fatal()
    {
        // A plugin's event is known since Phase 14; one whose fields do not fit is an event without a body.
        var badEvent = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"plugin_event","plugin_id":7,"name":"n","payload":"{}"}"""u8);
        Assert.True(badEvent.IsEvent);
        Assert.Null(badEvent.Body);
        Assert.NotNull(badEvent.Error);

        var newer = MessageCodec.Decode("""{"type":"listing_exploded","id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","extra":[1,{"a":2}]}"""u8);
        Assert.False(newer.IsEvent);
        Assert.Null(newer.Body);
        Assert.Equal("listing_exploded", newer.Type);
        Assert.Equal(Id, newer.Id);

        var malformed = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"pong","protocol_version":"seven"}"""u8);
        Assert.Null(malformed.Body);
        Assert.NotNull(malformed.Error);

        Assert.ThrowsAny<JsonException>(() => MessageCodec.Decode("[1,2]"u8));
    }

    [Fact]
    public void Fields_may_come_before_id_and_type()
    {
        var message = MessageCodec.Decode("""{"listing_id":9,"message":"gone","type":"listing_lost","id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W"}"""u8);
        Assert.Equal(new ListingLostEvent(9, "gone"), message.Body);
    }

    [Fact]
    public void A_future_volumes_reply_decodes_as_documented_in_part_d()
    {
        var message = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"volumes","volumes":[{"drive_letter":"C","volume_guid_path":"\\\\?\\Volume{1}\\","filesystem":"NTFS","label":"","total_bytes":1000,"free_bytes":240,"disk":null}]}"""u8);
        var volume = Assert.IsType<VolumesReply>(message.Body).Volumes.Single();
        Assert.Equal(("C", 1000UL, 240UL), (volume.DriveLetter, volume.TotalBytes, volume.FreeBytes));
    }

    [Fact]
    public void The_settings_the_window_reads_exist_in_the_config_schema()
    {
        const string firstRun = """
        {"$schema":"./cabinetos.schema.json","version":1,
         "ui":{"layout":"classic","dualPane":true,"sidebar":true,"theme":"default","dockSize":{"bottom":null,"right":null}},
         "panes":{"showHidden":false,"sort":{"key":"name","descending":false}},
         "keybindings":[],"logging":{"level":"info"},"plugins":{}}
        """;
        AssertValid(Schemas.Config, firstRun);
        AssertValid(Schemas.Config, """{"ui":{"dockSize":{"bottom":212,"right":340}}}""");
        AssertValid(Schemas.Config, """{"logging":{"heavy":true}}""");
        AssertInvalid(Schemas.Config, """{"logging":{"heavy":"yes"}}""");
        AssertInvalid(Schemas.Config, """{"ui":{"dualPan":true}}""");
        using var document = JsonDocument.Parse(firstRun);
        Assert.Equal(UiSettings.Defaults, UiSettings.FromConfig(document.RootElement));
    }

    [Fact]
    public void Missing_or_odd_settings_keep_their_defaults()
    {
        using var empty = JsonDocument.Parse("{}");
        Assert.Equal(UiSettings.Defaults, UiSettings.FromConfig(empty.RootElement));
        using var odd = JsonDocument.Parse("""{"ui":{"dualPane":"yes","layout":7},"panes":[]}""");
        Assert.Equal(UiSettings.Defaults, UiSettings.FromConfig(odd.RootElement));
    }

    /// <summary>
    /// For each <c>oneOf</c> variant of a message schema: its <c>type</c> and its
    /// declared fields, those of a <c>$ref</c> next to its <c>type</c> included
    /// (<c>start_job</c> refers to <c>JobRequest</c> for its fields).
    /// </summary>
    private static Dictionary<string, HashSet<string>> SchemaVariants(string path)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var definitions = schema.RootElement.TryGetProperty("$defs", out var defs) ? defs : default;
        var variants = new Dictionary<string, HashSet<string>>();
        foreach (var variant in schema.RootElement.GetProperty("oneOf").EnumerateArray())
        {
            if (variant.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("type", out var type)
                && type.TryGetProperty("const", out var name))
            {
                var fields = properties.EnumerateObject().Select(p => p.Name).ToHashSet();
                if (variant.TryGetProperty("$ref", out var reference))
                {
                    var definition = definitions.GetProperty(reference.GetString()!.Replace("#/$defs/", ""));
                    fields.UnionWith(definition.GetProperty("properties").EnumerateObject().Select(p => p.Name));
                }
                variants[name.GetString()!] = fields;
            }
            else if (variant.TryGetProperty("$ref", out _) || variant.TryGetProperty("allOf", out _))
            {
                throw new InvalidOperationException($"a variant of {path} is not a plain object; teach this test to follow it");
            }
        }
        return variants;
    }

    private static void AssertValid(JsonSchema schema, string json)
    {
        var result = schema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"{json}{Environment.NewLine}{Describe(result)}");
    }

    private static void AssertInvalid(JsonSchema schema, string json)
    {
        var result = schema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid, json);
    }

    private static string Describe(EvaluationResults result) =>
        string.Join(Environment.NewLine, (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Key}: {e.Value}")));
}
