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
        ];
    }

    /// <summary>
    /// Requests of protocol version 8 built against the shapes the core agreed
    /// on before its schema had them: their JSON exactly as the UI sends it.
    /// </summary>
    private static IReadOnlyList<(CoreRequest Request, string Json)> AgreedRequests() =>
    [
        (new OpenPathRequest(@"C:\data\report.pdf"), $$$"""{"id":"{{{Id}}}","type":"open_path","path":"C:\\data\\report.pdf"}"""),
        (new CreateDirectoryRequest(@"C:\data\New folder"), $$$"""{"id":"{{{Id}}}","type":"create_directory","path":"C:\\data\\New folder"}"""),
        (new RenameRequest(@"C:\data\a.txt", "b.txt"), $$$"""{"id":"{{{Id}}}","type":"rename","path":"C:\\data\\a.txt","new_name":"b.txt"}"""),
    ];

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
        Assert.Equal(20, checkedTypes.Count);
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
                b => Assert.Equal(new JobState("failed", "the disk is gone"), ((JobProgressEvent)b).State)),
            ($$$"""{"id":"{{{Id}}}","type":"plugin_state_changed","plugin_id":"crashy","state":{"type":"crashed","message":"wasm trap","at_ms":1790553600000}}""",
                b => Assert.Equal("crashed", ((PluginStateChangedEvent)b).State.GetProperty("type").GetString())),
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
        Assert.Equal(eventTypes.Order(), MessageCodec.EventTypes.Order());
        Assert.Empty(replyTypes.Intersect(MessageCodec.EventTypes));
    }

    [Fact]
    public void Unknown_types_are_ignored_not_fatal()
    {
        var unknownEvent = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"plugin_event","plugin_id":"x","name":"n","payload":"{}"}"""u8);
        Assert.True(unknownEvent.IsEvent);
        Assert.Null(unknownEvent.Body);

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
         "ui":{"layout":"classic","dualPane":true,"sidebar":true,"theme":"default"},
         "panes":{"showHidden":false,"sort":{"key":"name","descending":false}},
         "keybindings":[],"logging":{"level":"info"},"plugins":{}}
        """;
        AssertValid(Schemas.Config, firstRun);
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
