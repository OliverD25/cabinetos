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
    public async Task A_core_command_the_ui_overrides_runs_in_the_ui_without_asking_the_core()
    {
        var (router, core) = Create();
        CommandInvocation? seen = null;
        router.RegisterUiOverride("file.copyToOtherPane", invocation =>
        {
            seen = invocation;
            return Task.CompletedTask;
        });

        var outcome = await router.ExecuteAsync("file.copyToOtherPane", trigger: "key");

        Assert.Equal(CommandOutcomeKind.RanInUi, outcome.Kind);
        Assert.Equal(outcome.RequestId, seen!.RequestId);
        Assert.Empty(core.Requests);
        Assert.Equal("core", router.Find("file.copyToOtherPane")!.Target);
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
