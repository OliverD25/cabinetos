using System.Text;
using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Preview;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Tools;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>
/// The window's general parts of the AI phase (Phase 14; Article 10: nothing here
/// knows an agent): a plugin command that asks for text, the preview pane's rows
/// and keys, plugin events for tool pages, dropped paths, and the hosts a
/// <c>net</c> capability may reach.
/// </summary>
public class AgentPartsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CommandInfo PluginCommand(string id, CommandInput? input) =>
        new(id, "Agent", "Ask", ["ctrl+k ctrl+a"], [], new CommandSource("plugin", "agent", "Agent"), "core", null, false, input);

    private static (CommandRouter Router, FakeChannel Core) Create(CommandInfo command)
    {
        var core = new FakeChannel(request => request switch
        {
            ExecuteCommandRequest => new CommandResultReply(JsonDocument.Parse("""{"preview":"preview-3"}""").RootElement.Clone()),
            _ => new ErrorReply("unknown_request", request.Type),
        });
        var router = new CommandRouter(core);
        router.SetCommands([command]);
        return (router, core);
    }

    // ----- A plugin command with input -----

    [Fact]
    public void A_command_list_with_an_input_decodes_and_one_without_still_does()
    {
        var withInput = MessageCodec.Decode("""
            {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"commands","commands":[
              {"id":"agent.ask","category":"Agent","title":"Ask","keys":["ctrl+k ctrl+a"],"default_keys":[],
               "source":{"kind":"plugin","id":"agent","name":"Agent"},"target":"core","when":null,"immutable":false,
               "input":{"title":"Ask the agent","placeholder":"rename these to vacation_*"}},
              {"id":"view.toggleSidebar","category":"View","title":"Toggle Sidebar","keys":["ctrl+b"],"default_keys":["ctrl+b"],
               "source":{"kind":"core","id":null,"name":null},"target":"ui","when":null,"immutable":false}]}
            """u8);

        var commands = Assert.IsType<CommandsReply>(withInput.Body).Commands;
        Assert.Equal(new CommandInput("Ask the agent", "rename these to vacation_*"), commands[0].Input);
        Assert.Null(commands[1].Input);
    }

    [Fact]
    public async Task A_plugin_command_with_input_asks_first_and_runs_with_the_text_the_path_and_the_paths()
    {
        var (router, core) = Create(PluginCommand("agent.ask", new CommandInput("Ask the agent", "what should change?")));
        router.PluginArgs = _ => JsonDocument.Parse("""{"path":"C:\\photos\\a.jpg","paths":["C:\\photos\\a.jpg","C:\\photos\\b.jpg"]}""").RootElement.Clone();
        CommandInfo? asked = null;
        router.AskInput = command =>
        {
            asked = command;
            return Task.FromResult<string?>("rename these to vacation_*");
        };

        var outcome = await router.ExecuteAsync("agent.ask", trigger: "key");

        Assert.Equal(CommandOutcomeKind.CoreResult, outcome.Kind);
        Assert.Equal("agent.ask", asked!.Id);
        var sent = Assert.IsType<ExecuteCommandRequest>(Assert.Single(core.Requests));
        var args = sent.Args!.Value;
        Assert.Equal("rename these to vacation_*", args.GetProperty("input").GetString());
        Assert.Equal(@"C:\photos\a.jpg", args.GetProperty("path").GetString());
        Assert.Equal(2, args.GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public async Task Esc_in_the_prompt_cancels_the_command_and_nothing_reaches_the_core()
    {
        var (router, core) = Create(PluginCommand("agent.ask", new CommandInput()));
        router.AskInput = _ => Task.FromResult<string?>(null);
        CommandOutcome? completed = null;
        router.Completed += outcome => completed = outcome;

        var outcome = await router.ExecuteAsync("agent.ask", trigger: "palette");

        Assert.Equal(CommandOutcomeKind.Cancelled, outcome.Kind);
        Assert.Same(outcome, completed);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task A_caller_that_gives_input_itself_is_not_asked_and_a_command_without_input_never_is()
    {
        var (router, core) = Create(PluginCommand("agent.ask", new CommandInput()));
        var asks = 0;
        router.AskInput = _ =>
        {
            asks++;
            return Task.FromResult<string?>("typed");
        };
        using var given = JsonDocument.Parse("""{"input":"from a tool page"}""");

        await router.ExecuteAsync("agent.ask", given.RootElement.Clone(), "tool:agent-chat");
        Assert.Equal(0, asks);
        Assert.Equal("from a tool page", Assert.IsType<ExecuteCommandRequest>(core.Requests[0]).Args!.Value.GetProperty("input").GetString());

        var (plain, plainCore) = Create(PluginCommand("hello.say", null));
        plain.AskInput = _ =>
        {
            asks++;
            return Task.FromResult<string?>("typed");
        };
        await plain.ExecuteAsync("hello.say");
        Assert.Equal(0, asks);
        Assert.Null(Assert.IsType<ExecuteCommandRequest>(Assert.Single(plainCore.Requests)).Args);
    }

    [Fact]
    public void The_prompt_takes_the_commands_title_else_its_name_and_the_answer_keeps_the_other_arguments()
    {
        Assert.Equal("Ask the agent", PluginInput.Label(PluginCommand("agent.ask", new CommandInput("Ask the agent", null))));
        Assert.Equal("Ask", PluginInput.Label(PluginCommand("agent.ask", new CommandInput())));
        using var existing = JsonDocument.Parse("""{"input":"old","path":"C:\\a.txt"}""");
        var merged = PluginInput.With(existing.RootElement, "new");
        Assert.Equal("new", merged.GetProperty("input").GetString());
        Assert.Equal(@"C:\a.txt", merged.GetProperty("path").GetString());
        Assert.Equal("only", PluginInput.With(null, "only").GetProperty("input").GetString());
    }

    [Fact]
    public void A_commands_result_that_names_a_preview_is_read_as_one()
    {
        using var result = JsonDocument.Parse("""{"preview":"preview-3","note":"3 renames"}""");
        Assert.Equal("preview-3", PluginEvents.PreviewOfResult(result.RootElement));
        using var none = JsonDocument.Parse("""{"note":"nothing to do"}""");
        Assert.Null(PluginEvents.PreviewOfResult(none.RootElement));
        using var text = JsonDocument.Parse("\"preview-3\"");
        Assert.Null(PluginEvents.PreviewOfResult(text.RootElement));
    }

    // ----- The preview -----

    private static (byte[] Bytes, ListingView View) OpenPreview(params SyntheticPreviewRow[] rows)
    {
        var bytes = TestSections.BuildPreview(rows);
        var handle = TestSections.CreateSection(bytes);
        return (bytes, ListingView.Open(new SectionHandle(handle), (ulong)bytes.Length));
    }

    [Fact]
    public void Each_row_of_a_fake_listing_shows_its_change_its_item_and_its_target()
    {
        var (_, view) = OpenPreview(
            new SyntheticPreviewRow(@"C:\photos\IMG_1.jpg", 1, @"C:\photos\vacation_1.jpg", EntryKind.File),
            new SyntheticPreviewRow(@"C:\photos\IMG_2.jpg", 2, @"D:\backup", EntryKind.File),
            new SyntheticPreviewRow(@"C:\photos\IMG_3.jpg", 3, @"D:\backup", EntryKind.File),
            new SyntheticPreviewRow(@"C:\photos\old.tmp", 4, null, EntryKind.File),
            new SyntheticPreviewRow(@"C:\photos\Beach\", 5, null, EntryKind.Unknown),
            new SyntheticPreviewRow(@"C:\photos\odd", 9, null, EntryKind.File));
        using (view)
        {
            var session = PreviewSession.From("preview-3", "Sort the photos", view);

            Assert.Equal(("preview-3", "Sort the photos", "6 changes"), (session.Id, session.Title, session.Count));
            Assert.Equal("Enter applies \u00B7 Esc cancels", PreviewSession.Hint);
            Assert.Equal(
                [("Rename", @"C:\photos\", "IMG_1.jpg", "vacation_1.jpg"),
                 ("Move", @"C:\photos\", "IMG_2.jpg", @"backup\"),
                 ("Copy", @"C:\photos\", "IMG_3.jpg", @"backup\"),
                 ("Delete", @"C:\photos\", "old.tmp", ""),
                 ("Create", @"C:\photos\", @"Beach\", ""),
                 ("Change", @"C:\photos\", "odd", "")],
                session.Lines.Select(l => (l.Verb, l.Folder, l.Name, l.Target)));
            // Only a delete is red, and only a row with a target has an accent part.
            Assert.Equal([false, false, false, true, false, false], session.Lines.Select(l => l.IsDelete));
            Assert.Equal([true, true, true, false, false, false], session.Lines.Select(l => l.HasTarget));
            Assert.Equal(@"Rename C:\photos\IMG_1.jpg to C:\photos\vacation_1.jpg", session.Lines[0].Description);
            // The row shows the folder's name; its tooltip and the screen reader get the whole path.
            Assert.Equal(@"D:\backup", session.Lines[1].TargetPath);
            Assert.Equal(@"Move C:\photos\IMG_2.jpg to D:\backup", session.Lines[1].Description);
            Assert.Equal(@"Delete C:\photos\old.tmp", session.Lines[3].Description);
        }
    }

    [Fact]
    public void A_preview_of_one_row_says_change_not_changes_and_a_path_without_a_folder_keeps_its_name()
    {
        var (_, view) = OpenPreview(new SyntheticPreviewRow("loose.txt", 4, null, EntryKind.File));
        using (view)
        {
            var session = PreviewSession.From("preview-1", "One", view);
            Assert.Equal("1 change", session.Count);
            Assert.Equal(("", "loose.txt"), (session.Lines[0].Folder, session.Lines[0].Name));
        }
        Assert.Equal((@"C:\a\", "b"), PreviewLine.Split(@"C:\a\b"));
        Assert.Equal((@"C:\a\", @"b\"), PreviewLine.Split(@"C:\a\b\"));
    }

    [Fact]
    public void Enter_applies_and_Esc_cancels_and_no_other_key_is_the_previews()
    {
        Assert.Equal(PreviewKeyAction.Apply, PreviewKeys.For("enter"));
        Assert.Equal(PreviewKeyAction.Cancel, PreviewKeys.For("escape"));
        Assert.All(new[] { "ctrl+enter", "shift+escape", "down", "tab", "ctrl+shift+p", "f5", "space" }, key => Assert.Equal(PreviewKeyAction.None, PreviewKeys.For(key)));
    }

    [Fact]
    public void The_previews_requests_and_replies_follow_the_protocol_text()
    {
        Assert.Equal("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"open_preview","preview":"preview-3"}""",
            Encoding.UTF8.GetString(MessageCodec.Encode(new OpenPreviewRequest("preview-3") { Id = "01J9ZQ4X7K3M5N8P2R6S0T1V4W" })));
        Assert.Contains("\"type\":\"preview_apply\",\"preview\":\"preview-3\"", Encoding.UTF8.GetString(MessageCodec.Encode(new PreviewApplyRequest("preview-3"))));
        Assert.Contains("\"type\":\"preview_cancel\",\"preview\":\"preview-3\"", Encoding.UTF8.GetString(MessageCodec.Encode(new PreviewCancelRequest("preview-3"))));

        var opened = MessageCodec.Decode("""
            {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"preview_opened","preview":"preview-3","title":"Sort the photos",
             "listing":{"listing_id":9,"section_handle":0,"section_size":736,"entry_count":3,"generation":1,"elapsed_us":210}}
            """u8);
        var reply = Assert.IsType<PreviewOpenedReply>(opened.Body);
        Assert.Equal(("preview-3", "Sort the photos", 736UL, 3U), (reply.Preview, reply.Title, reply.SectionSize, reply.Listing.EntryCount));
        Assert.False(opened.IsEvent);

        Assert.Equal([12UL, 13UL], Assert.IsType<JobsStartedReply>(MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"jobs_started","jobs":[12,13]}"""u8).Body).Jobs);
        var applied = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"preview_applied","preview":"preview-3","jobs":[12,13]}"""u8);
        Assert.True(applied.IsEvent);
        var appliedEvent = Assert.IsType<PreviewAppliedEvent>(applied.Body);
        Assert.Equal("preview-3", appliedEvent.Preview);
        Assert.Equal([12UL, 13UL], appliedEvent.Jobs);
        Assert.Equal(new PreviewCancelledEvent("preview-4"), MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"preview_cancelled","preview":"preview-4"}"""u8).Body);
    }

    // ----- Plugin events, in the status bar and in tool pages -----

    [Fact]
    public void A_plugin_event_carries_the_plugins_id_and_any_payload()
    {
        // The core forwards the payload as the text the plugin wrote (event.schema.json).
        var message = MessageCodec.Decode("""{"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"plugin_event","plugin_id":"agent","name":"agent.notice","payload":"{\"text\":\"Renamed 3 files\"}"}"""u8);

        Assert.True(message.IsEvent);
        var received = Assert.IsType<PluginEventEvent>(message.Body);
        Assert.Equal(("agent", "agent.notice"), (received.PluginId, received.Name));
        Assert.Equal("Renamed 3 files", PluginEvents.NoticeText(received.Name, received.Payload));
    }

    [Fact]
    public void Only_an_agent_notice_with_text_is_a_notice_and_a_long_one_is_cut()
    {
        Assert.Equal("Done", PluginEvents.NoticeText("agent.notice", """{"text":"Done"}"""));
        Assert.Null(PluginEvents.NoticeText("hello.said", """{"text":"Done"}"""));
        Assert.Null(PluginEvents.NoticeText("agent.notice", """{"other":1}"""));
        Assert.Null(PluginEvents.NoticeText("agent.notice", """{"text":""}"""));
        Assert.Null(PluginEvents.NoticeText("agent.notice", "\"Done\""));
        Assert.Null(PluginEvents.NoticeText("agent.notice", "Done, but not JSON"));
        var cut = PluginEvents.NoticeText("agent.notice", $$"""{"text":"{{new string('x', 1000)}}"}""")!;
        Assert.Equal(PluginEvents.MaxNoticeLength, cut.Length);
        Assert.EndsWith("\u2026", cut);
    }

    [Fact]
    public void A_plugins_event_of_any_name_opens_the_preview_its_payload_names()
    {
        // Rule 2: the event's name does not matter (the agent plugin emits agent.preview), only the field.
        Assert.Equal("preview-7", PluginEvents.ProposedPreview("""{"preview":"preview-7"}"""));
        Assert.Equal("preview-7", PluginEvents.ProposedPreview("""{"preview":"preview-7","note":"3 renames"}"""));
        Assert.Null(PluginEvents.ProposedPreview("{}"));
        Assert.Null(PluginEvents.ProposedPreview("""{"preview":7}"""));
        Assert.Null(PluginEvents.ProposedPreview("""{"preview":""}"""));
        Assert.Null(PluginEvents.ProposedPreview("preview-7"));
    }

    [Fact]
    public void A_tool_page_subscribes_to_a_plugin_and_gets_its_events_as_plugin_event_messages()
    {
        var subscribe = ToolMessages.Parse("""{"type":"subscribe","plugin":"agent"}""");
        Assert.Equal(new ToolMessage("subscribe", null, null, null, "agent"), subscribe);
        Assert.Equal("agent", ToolMessages.Parse("""{"type":"unsubscribe","plugin":"agent"}""")!.Plugin);
        Assert.Null(ToolMessages.Parse("""{"type":"subscribe"}"""));
        Assert.Null(ToolMessages.Parse("""{"type":"subscribe","plugin":""}"""));
        Assert.Null(ToolMessages.Parse("""{"type":"subscribe","plugin":7}"""));

        using var sent = JsonDocument.Parse(ToolMessages.PluginEvent("agent", "agent.notice", """{"text":"Renamed 3 files","count":3}"""));
        Assert.Equal("plugin-event", sent.RootElement.GetProperty("type").GetString());
        Assert.Equal("agent", sent.RootElement.GetProperty("plugin").GetString());
        Assert.Equal("agent.notice", sent.RootElement.GetProperty("name").GetString());
        Assert.Equal(3, sent.RootElement.GetProperty("payload").GetProperty("count").GetInt32());
        // A payload that is not JSON reaches the page as the text it is.
        using var plain = JsonDocument.Parse(ToolMessages.PluginEvent("agent", "agent.said", "hello there"));
        Assert.Equal("hello there", plain.RootElement.GetProperty("payload").GetString());
        using var number = JsonDocument.Parse(ToolMessages.PluginEvent("agent", "agent.count", "42"));
        Assert.Equal(42, number.RootElement.GetProperty("payload").GetInt32());
    }

    [Fact]
    public void A_page_hears_only_the_plugins_it_asked_for_and_only_so_many()
    {
        var subscriptions = new ToolSubscriptions();
        Assert.False(subscriptions.Wants("agent"));
        Assert.True(subscriptions.Subscribe("agent"));
        Assert.True(subscriptions.Subscribe("agent"));
        Assert.True(subscriptions.Wants("agent"));
        Assert.False(subscriptions.Wants("hello"));
        Assert.True(subscriptions.Unsubscribe("agent"));
        Assert.False(subscriptions.Wants("agent"));

        for (var i = 0; i < ToolSubscriptions.MaxPlugins; i++)
        {
            Assert.True(subscriptions.Subscribe($"plugin-{i}"));
        }
        Assert.False(subscriptions.Subscribe("one-too-many"));
        subscriptions.Clear();
        Assert.Empty(subscriptions.Plugins);
    }

    [Fact]
    public void Rows_dropped_on_a_tool_page_arrive_as_a_paths_dropped_message()
    {
        using var sent = JsonDocument.Parse(ToolMessages.PathsDropped([@"C:\photos\a.jpg", @"C:\photos\Звіт 😀.txt"]));

        Assert.Equal("paths-dropped", sent.RootElement.GetProperty("type").GetString());
        Assert.Equal([@"C:\photos\a.jpg", @"C:\photos\Звіт 😀.txt"], sent.RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        Assert.False(sent.RootElement.TryGetProperty("truncated", out _));

        var many = Enumerable.Range(0, ToolMessages.MaxDropped + 5).Select(i => $@"C:\f\{i}.txt").ToList();
        using var cut = JsonDocument.Parse(ToolMessages.PathsDropped(many));
        Assert.Equal(ToolMessages.MaxDropped, cut.RootElement.GetProperty("paths").GetArrayLength());
        Assert.True(cut.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ----- The review dialog shows a net capability's hosts -----

    [Fact]
    public void A_net_capability_shows_its_hosts_under_its_reason_and_the_rest_show_none()
    {
        var plugin = new PluginInfo("agent", "Agent", "0.1.0", "CabinetOS", "Works next to you.",
            new PluginState(PluginState.NeedsReview, ["net"]),
            [
                new CapabilityInfo("net", "high", false, "Asks the model provider.", null, ["api.anthropic.com", "localhost:11434"], ["anthropic"]),
                new CapabilityInfo("fs:read", "medium", false, "Reads the folder you point it at.", [@"C:\photos"]),
                new CapabilityInfo("cmd:register", "low", false, "Adds the Ask command."),
            ],
            []);

        var rows = new PermissionReview(plugin).Rows;

        Assert.Equal("Can reach: api.anthropic.com, localhost:11434", rows[0].HostsText);
        Assert.Equal("Can use the stored secrets: anthropic", rows[0].SecretsText);
        Assert.Equal("Asks the model provider. Can reach: api.anthropic.com, localhost:11434 Can use the stored secrets: anthropic", rows[0].FullDetail);
        Assert.Equal("Asks the model provider.", rows[0].Detail);
        Assert.Null(rows[1].HostsText);
        Assert.Null(rows[1].SecretsText);
        Assert.Equal(@"Reads the folder you point it at. (C:\photos)", rows[1].FullDetail);
        Assert.Null(rows[2].HostsText);
    }

    [Fact]
    public void The_hosts_of_a_capability_decode_from_the_plugin_list_and_from_the_index()
    {
        var plugins = MessageCodec.Decode("""
            {"id":"01J9ZQ4X7K3M5N8P2R6S0T1V4W","type":"plugins","plugins":[{"id":"agent","name":"Agent","version":"0.1.0","author":"CabinetOS",
              "description":"","state":{"type":"needs_review","missing":["net"]},
              "capabilities":[{"name":"net","level":"high","granted":false,"reason":"Asks the provider.","hosts":["api.anthropic.com"],"secrets":["anthropic"]}],"commands":[]}]}
            """u8);
        var capability = Assert.IsType<PluginsReply>(plugins.Body).Plugins.Single().Capabilities.Single();
        Assert.Equal(["api.anthropic.com"], capability.Hosts);
        Assert.Equal(["anthropic"], capability.Secrets);

        var market = JsonSerializer.Deserialize<MarketCapability>("""{"name":"net","reason":"Asks the provider.","hosts":["localhost:11434"],"secrets":["local-key"]}""", Web);
        Assert.Equal(["localhost:11434"], market!.Hosts);
        Assert.Equal(["local-key"], market.Secrets);
        using var manifest = JsonDocument.Parse("{}");
        var item = new MarketItem("agent", ExtensionKinds.Plugin, "Agent", new MarketAuthor("CabinetOS"), "0.1.0", "Works next to you.", 1000,
            new MarketDownload("files/agent-0.1.0.zip", new string('a', 64)), manifest.RootElement.Clone(), "0.1.0", "MIT", Capabilities: [market]);
        var row = PermissionReview.ForInstall(item).Rows.Single();
        Assert.Equal("Can reach: localhost:11434", row.HostsText);
        Assert.Equal("Can use the stored secrets: local-key", row.SecretsText);
    }
}
