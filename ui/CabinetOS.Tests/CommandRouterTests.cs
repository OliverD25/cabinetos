using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Protocol;
using CabinetOS.Tests.Support;

namespace CabinetOS.Tests;

/// <summary>The command router against a fake core.</summary>
public class CommandRouterTests
{
    private static CommandInfo Command(string id, string target, bool immutable = false) =>
        new(id, "View", id, [], [], new CommandSource("core", null, null), target, null, immutable);

    private static readonly IReadOnlyList<CommandInfo> Registry =
    [
        Command("palette.show", "ui", immutable: true),
        Command("view.toggleDualPane", "ui"),
        Command("view.toggleTerminal", "ui"),
        Command("help.about", "core"),
        Command("file.copyToOtherPane", "core"),
    ];

    private static (CommandRouter Router, FakeChannel Core) Create(Func<CoreRequest, CoreReply>? answer = null)
    {
        var core = new FakeChannel(answer ?? (request => request switch
        {
            ListCommandsRequest => new CommandsReply(Registry),
            ExecuteCommandRequest { Command: "help.about" } =>
                new CommandResultReply(JsonDocument.Parse("""{"name":"CabinetOS","protocol_version":7}""").RootElement.Clone()),
            ExecuteCommandRequest { Command: "file.copyToOtherPane" } =>
                new ErrorReply(ErrorCodes.NotImplemented, "file.copyToOtherPane is not implemented yet"),
            ExecuteCommandRequest { Command: var id } when Registry.Any(c => c.Id == id && c.Target == "ui") =>
                new CommandRoutedReply("ui"),
            ExecuteCommandRequest { Command: var id } => new ErrorReply(ErrorCodes.UnknownCommand, $"no command {id}"),
            _ => new ErrorReply(ErrorCodes.UnknownRequest, request.Type),
        }));
        var router = new CommandRouter(core);
        router.SetCommands(Registry);
        return (router, core);
    }

    [Fact]
    public async Task A_ui_command_runs_its_handler_without_asking_the_core()
    {
        var (router, core) = Create();
        CommandInvocation? seen = null;
        router.RegisterUiHandler("view.toggleDualPane", invocation => seen = invocation);
        CommandOutcome? completed = null;
        router.Completed += outcome => completed = outcome;

        var outcome = await router.ExecuteAsync("view.toggleDualPane", trigger: "key");

        Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
        Assert.NotNull(seen);
        Assert.Equal("key", seen.Trigger);
        Assert.True(Ulid.IsValid(seen.RequestId));
        Assert.Equal(outcome.RequestId, seen.RequestId);
        Assert.Same(outcome, completed);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task A_core_command_goes_to_the_core_under_the_same_request_id()
    {
        var (router, core) = Create();
        using var args = JsonDocument.Parse("""{"verbose":true}""");

        var outcome = await router.ExecuteAsync("help.about", args.RootElement.Clone(), "palette");

        Assert.Equal(CommandOutcomeKind.CoreResult, outcome.Kind);
        Assert.Equal("CabinetOS", outcome.Result!.Value.GetProperty("name").GetString());
        var sent = Assert.IsType<ExecuteCommandRequest>(Assert.Single(core.Requests));
        Assert.Equal("help.about", sent.Command);
        Assert.Equal(outcome.RequestId, sent.Id);
        Assert.True(sent.Args!.Value.GetProperty("verbose").GetBoolean());
    }

    [Fact]
    public async Task An_error_from_the_core_is_surfaced()
    {
        var (router, _) = Create();
        var outcome = await router.ExecuteAsync("file.copyToOtherPane");
        Assert.Equal(CommandOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ErrorCodes.NotImplemented, outcome.ErrorCode);
        Assert.Contains("not implemented", outcome.ErrorMessage);
    }

    [Fact]
    public async Task A_ui_command_without_a_handler_is_reported_as_not_available()
    {
        var (router, core) = Create();
        var outcome = await router.ExecuteAsync("view.toggleTerminal");
        Assert.Equal(CommandOutcomeKind.NotAvailable, outcome.Kind);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task A_command_the_core_hands_back_runs_in_the_ui()
    {
        var (router, core) = Create();
        router.SetCommands([Command("view.toggleDualPane", "core")]);
        var ran = false;
        router.RegisterUiHandler("view.toggleDualPane", _ => ran = true);

        var outcome = await router.ExecuteAsync("view.toggleDualPane");

        Assert.True(ran);
        Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
        Assert.IsType<ExecuteCommandRequest>(Assert.Single(core.Requests));
    }

    [Fact]
    public async Task A_local_command_runs_only_in_the_ui()
    {
        var (router, core) = Create();
        string? path = null;
        router.RegisterLocal("go.up", invocation => path = invocation.Args?.GetProperty("path").GetString());
        using var args = JsonDocument.Parse("""{"path":"C:\\"}""");

        var outcome = await router.ExecuteAsync("go.up", args.RootElement.Clone());

        Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
        Assert.Equal(@"C:\", path);
        Assert.Empty(core.Requests);
        Assert.Null(router.Find("go.up"));
    }

    [Fact]
    public async Task A_window_command_runs_its_handler_before_the_registry_lists_it_and_after()
    {
        var (router, core) = Create();
        var runs = 0;
        router.RegisterUiHandler("transfer.pause", _ => runs++);

        // Before list_commands answered, or from a core older than protocol 11.
        Assert.Equal(CommandOutcomeKind.RanInUi, (await router.ExecuteAsync("transfer.pause", trigger: "button")).Kind);
        Assert.Empty(core.Requests);

        // Protocol 11 lists it with target ui: the same handler runs it.
        router.SetCommands([.. Registry, Command("transfer.pause", "ui")]);
        Assert.Equal(CommandOutcomeKind.RanInUi, (await router.ExecuteAsync("transfer.pause", trigger: "palette")).Kind);
        Assert.Equal(2, runs);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task Help_about_listed_for_the_window_opens_through_its_handler_not_on_a_core_result()
    {
        // Registry above: help.about as a core older than 2c80d5f listed it. Since then it is the window's.
        var (router, core) = Create();
        router.SetCommands([.. Registry.Where(c => c.Id != "help.about"), Command("help.about", "ui")]);
        var opened = new List<string>();
        // What MainWindow.RegisterAboutCommand registers: the About view.
        router.RegisterUiHandler("help.about", invocation =>
        {
            opened.Add(invocation.Trigger);
            return Task.CompletedTask;
        });

        var fromPalette = await router.ExecuteAsync("help.about", trigger: "palette");
        var fromMenu = await router.ExecuteAsync("help.about", trigger: "menu");

        Assert.Equal((CommandOutcomeKind.RanInUi, CommandOutcomeKind.RanInUi), (fromPalette.Kind, fromMenu.Kind));
        Assert.Null(fromPalette.Result);
        Assert.Equal(["palette", "menu"], opened);
        Assert.Empty(core.Requests);
    }

    [Fact]
    public async Task A_plugin_command_from_the_palette_gets_the_active_panes_files_and_one_from_the_menu_keeps_its_own()
    {
        var (router, core) = Create(request => request is ExecuteCommandRequest
            ? new CommandResultReply(JsonDocument.Parse("{}").RootElement.Clone())
            : new ErrorReply(ErrorCodes.UnknownRequest, request.Type));
        var size = new CommandInfo("reader.size", "Reader", "Size", [], [], new CommandSource("plugin", "reader", "Reader"), "core", "filesView", false);
        router.SetCommands([.. Registry, size]);
        using var files = JsonDocument.Parse("""{"path":"C:\\data\\a.txt","paths":["C:\\data\\a.txt","C:\\data\\b.txt"]}""");
        router.PluginArgs = _ => files.RootElement.Clone();

        await router.ExecuteAsync("reader.size", trigger: "palette");
        using var menu = JsonDocument.Parse("""{"path":"C:\\data\\c.txt"}""");
        await router.ExecuteAsync("reader.size", menu.RootElement.Clone(), "menu");
        await router.ExecuteAsync("help.about", trigger: "palette");

        var sent = core.Requests.OfType<ExecuteCommandRequest>().ToList();
        Assert.Equal(2, sent[0].Args!.Value.GetProperty("paths").GetArrayLength());
        Assert.Equal(@"C:\data\c.txt", sent[1].Args!.Value.GetProperty("path").GetString());
        // Only plugin commands get the files: a core command keeps no arguments.
        Assert.Null(sent[2].Args);
    }

    [Fact]
    public async Task While_a_dialog_is_open_only_its_own_commands_run()
    {
        var (router, core) = Create();
        var ran = new List<string>();
        router.RegisterUiHandler("view.toggleTerminal", invocation => ran.Add(invocation.CommandId));
        router.RegisterUiHandler("palette.show", invocation => ran.Add(invocation.CommandId));
        router.RegisterLocal("plugins.grant", invocation => ran.Add(invocation.CommandId));
        var executing = new List<string>();
        router.Executing += invocation => executing.Add(invocation.CommandId);
        var completed = new List<CommandOutcomeKind>();
        router.Completed += outcome => completed.Add(outcome.Kind);

        // A dialog with no commands of its own (Properties, a confirmation): nothing runs,
        // not a key's command, not the palette, not a command of the core.
        router.SetModal("Properties");
        Assert.Equal("Properties", router.Modal);
        Assert.Equal(CommandOutcomeKind.Refused, (await router.ExecuteAsync("view.toggleTerminal", trigger: "key")).Kind);
        Assert.Equal(CommandOutcomeKind.Refused, (await router.ExecuteAsync("palette.show", trigger: "key")).Kind);
        Assert.Equal(CommandOutcomeKind.Refused, (await router.ExecuteAsync("help.about", trigger: "tool:markdown-preview")).Kind);
        Assert.Empty(ran);
        Assert.Empty(executing);
        Assert.Empty(core.Requests);
        Assert.Equal([CommandOutcomeKind.Refused, CommandOutcomeKind.Refused, CommandOutcomeKind.Refused], completed);

        // The permissions review runs its own buttons, and nothing else.
        router.SetModal("permissions review", "plugins.grant", "overlay.close");
        Assert.Equal(CommandOutcomeKind.RanInUi, (await router.ExecuteAsync("plugins.grant", trigger: "button")).Kind);
        Assert.Equal(CommandOutcomeKind.Refused, (await router.ExecuteAsync("view.toggleTerminal", trigger: "key")).Kind);

        router.SetModal(null);
        Assert.Null(router.Modal);
        Assert.Equal(CommandOutcomeKind.RanInUi, (await router.ExecuteAsync("view.toggleTerminal", trigger: "key")).Kind);
        Assert.Equal(["plugins.grant", "view.toggleTerminal"], ran);
    }

    [Fact]
    public async Task A_command_nobody_knows_is_asked_of_the_core_and_fails_there()
    {
        var (router, core) = Create();
        var outcome = await router.ExecuteAsync("reader.size");
        Assert.Equal(CommandOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ErrorCodes.UnknownCommand, outcome.ErrorCode);
        Assert.Single(core.Requests);
    }

    [Fact]
    public async Task A_handler_that_throws_is_a_failed_run_not_a_crash()
    {
        var (router, _) = Create();
        router.RegisterUiHandler("view.toggleDualPane", _ => throw new InvalidOperationException("boom"));
        var outcome = await router.ExecuteAsync("view.toggleDualPane");
        Assert.Equal(CommandOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ui_error", outcome.ErrorCode);
    }

    [Fact]
    public async Task Refresh_reads_the_list_from_the_core()
    {
        var core = new FakeChannel(_ => new CommandsReply(Registry));
        var router = new CommandRouter(core);
        var changed = 0;
        router.CommandsChanged += () => changed++;

        await router.RefreshAsync();

        Assert.Equal(Registry.Select(c => c.Id), router.Commands.Select(c => c.Id));
        Assert.Equal("core", router.Find("help.about")!.Target);
        Assert.Equal(1, changed);
        Assert.IsType<ListCommandsRequest>(Assert.Single(core.Requests));
    }

    [Fact]
    public async Task The_list_is_read_again_when_keys_config_or_plugins_change()
    {
        var core = new FakeChannel(_ => new CommandsReply(Registry));
        var router = new CommandRouter(core);
        var keymap = new KeymapData(1000, [], []);

        Assert.True(await router.OnCoreEventAsync(new KeymapChangedEvent(keymap)));
        Assert.True(await router.OnCoreEventAsync(new ConfigChangedEvent(["ui.layout"])));
        Assert.True(await router.OnCoreEventAsync(new PluginCrashedEvent("crashy", "trap")));
        Assert.False(await router.OnCoreEventAsync(new ListingLostEvent(1, "gone")));
        Assert.False(await router.OnCoreEventAsync(new TerminalExitedEvent(1, 0)));

        Assert.Equal(3, core.Requests.Count(r => r is ListCommandsRequest));
        Assert.Equal(Registry.Count, router.Commands.Count);
    }

    [Fact]
    public async Task A_lost_core_is_a_failed_run()
    {
        var router = new CommandRouter(new FakeChannel(_ => throw new Core.Ipc.CoreDisconnectedException("gone")));
        router.SetCommands(Registry);
        var outcome = await router.ExecuteAsync("help.about");
        Assert.Equal(CommandOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("disconnected", outcome.ErrorCode);
    }
}
