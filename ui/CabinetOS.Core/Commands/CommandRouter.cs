using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Commands;

/// <summary>One run of a command, as its handler sees it.</summary>
/// <param name="CommandId">The command's ID.</param>
/// <param name="Args">Its arguments, if any.</param>
/// <param name="RequestId">The ULID that names this run in the logs.</param>
/// <param name="Trigger">What started it: <c>key</c>, <c>button</c>, <c>palette</c>, …</param>
public sealed record CommandInvocation(string CommandId, JsonElement? Args, string RequestId, string Trigger);

/// <summary>How a run of a command ended.</summary>
public enum CommandOutcomeKind
{
    /// <summary>A UI handler ran it.</summary>
    RanInUi,

    /// <summary>The core ran it and answered <c>command_result</c>.</summary>
    CoreResult,

    /// <summary>A UI command without a handler yet (its feature arrives in a later phase).</summary>
    NotAvailable,

    /// <summary>It failed: an <c>error</c> reply, a lost connection, or a handler that threw.</summary>
    Failed,
}

/// <summary>The end of one run, for the status bar and the logs.</summary>
public sealed record CommandOutcome(
    string CommandId,
    string RequestId,
    string Trigger,
    CommandOutcomeKind Kind,
    JsonElement? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// The single place every button, menu item, palette row and key binding goes
/// through (brief §5, "Command Routing"; Article 7). A command the registry
/// marks <c>target: ui</c> runs its registered handler here; any other goes to
/// the core as <c>execute_command</c>. Every run is logged with its own ULID.
/// </summary>
/// <remarks>
/// The UI also has a few navigation commands the core's registry does not
/// list yet (<see cref="RegisterLocal"/>, for example <c>go.up</c>). They run
/// only here and never reach the palette until the core registers them.
/// </remarks>
public sealed class CommandRouter(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::commands";

    private readonly Dictionary<string, Func<CommandInvocation, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CommandInvocation, Task>> _local = new(StringComparer.Ordinal);
    private Dictionary<string, CommandInfo> _byId = new(StringComparer.Ordinal);

    /// <summary>Raised on the calling thread when the command list changed.</summary>
    public event Action? CommandsChanged;

    /// <summary>Raised on the calling thread when a run ended.</summary>
    public event Action<CommandOutcome>? Completed;

    /// <summary>Every command of the core's registry, in registry order.</summary>
    public IReadOnlyList<CommandInfo> Commands { get; private set; } = [];

    /// <summary>The command with <paramref name="id"/>, if the registry has it.</summary>
    public CommandInfo? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Registers the UI's handler for a registry command with <c>target: ui</c>.</summary>
    public void RegisterUiHandler(string commandId, Func<CommandInvocation, Task> handler) => _handlers[commandId] = handler;

    /// <summary>Registers the UI's handler for a registry command with <c>target: ui</c>.</summary>
    public void RegisterUiHandler(string commandId, Action<CommandInvocation> handler) =>
        _handlers[commandId] = invocation =>
        {
            handler(invocation);
            return Task.CompletedTask;
        };

    /// <summary>Registers a UI-only command that the core's registry does not list.</summary>
    public void RegisterLocal(string commandId, Action<CommandInvocation> handler) =>
        _local[commandId] = invocation =>
        {
            handler(invocation);
            return Task.CompletedTask;
        };

    /// <summary>Registers a UI-only command that the core's registry does not list.</summary>
    public void RegisterLocal(string commandId, Func<CommandInvocation, Task> handler) => _local[commandId] = handler;

    /// <summary>Reads the command list again (<c>list_commands</c>).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var reply = await core.RequestAsync(new ListCommandsRequest(), cancellationToken);
        if (reply is not CommandsReply commands)
        {
            Diag.Warn(Target, "list_commands did not return commands", new LogField("reply", reply.GetType().Name));
            return;
        }
        SetCommands(commands.Commands);
    }

    /// <summary>
    /// Reads the command list again after an event that may change it: a new
    /// keymap (the keys shown), a configuration change, or a plugin that
    /// started, stopped or crashed (its commands). Returns whether it did.
    /// </summary>
    public async Task<bool> OnCoreEventAsync(CoreEvent coreEvent)
    {
        if (coreEvent is not (KeymapChangedEvent or ConfigChangedEvent or PluginStateChangedEvent or PluginCrashedEvent))
        {
            return false;
        }
        try
        {
            await RefreshAsync();
            return true;
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot read the command list", new LogField("error", error.Message));
            return false;
        }
    }

    /// <summary>Uses <paramref name="commands"/> as the command list.</summary>
    public void SetCommands(IReadOnlyList<CommandInfo> commands)
    {
        Commands = commands;
        _byId = commands.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        CommandsChanged?.Invoke();
    }

    /// <summary>
    /// Runs a command. Never throws: the outcome says how it ended and is
    /// also raised as <see cref="Completed"/>.
    /// </summary>
    public async Task<CommandOutcome> ExecuteAsync(string commandId, JsonElement? args = null, string trigger = "api")
    {
        var requestId = Ulid.NewId();
        var invocation = new CommandInvocation(commandId, args, requestId, trigger);
        var info = Find(commandId);
        CommandOutcome outcome;
        if (info is null && _local.TryGetValue(commandId, out var local))
        {
            Log(requestId, commandId, "ui", trigger);
            outcome = await RunHandler(local, invocation);
        }
        else if (info is { Target: "ui" })
        {
            Log(requestId, commandId, "ui", trigger);
            outcome = await RunUi(invocation);
        }
        else
        {
            Log(requestId, commandId, "core", trigger);
            outcome = await RunInCore(invocation);
        }
        Completed?.Invoke(outcome);
        return outcome;
    }

    private async Task<CommandOutcome> RunUi(CommandInvocation invocation)
    {
        if (_handlers.TryGetValue(invocation.CommandId, out var handler))
        {
            return await RunHandler(handler, invocation);
        }
        Diag.Request(LogLevel.Info, invocation.RequestId, Target, "command has no UI handler yet",
            new LogField("command", invocation.CommandId));
        return new CommandOutcome(invocation.CommandId, invocation.RequestId, invocation.Trigger, CommandOutcomeKind.NotAvailable);
    }

    private async Task<CommandOutcome> RunInCore(CommandInvocation invocation)
    {
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new ExecuteCommandRequest(invocation.CommandId)
            {
                Id = invocation.RequestId,
                Args = invocation.Args,
            });
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            return Failed(invocation, "disconnected", error.Message);
        }
        switch (reply)
        {
            case CommandResultReply result:
                Diag.Request(LogLevel.Info, invocation.RequestId, Target, "command ran in the core",
                    new LogField("command", invocation.CommandId));
                return new CommandOutcome(invocation.CommandId, invocation.RequestId, invocation.Trigger,
                    CommandOutcomeKind.CoreResult, Result: result.Result);
            case CommandRoutedReply:
                // The registry changed since the list was read: the core hands it back.
                return await RunUi(invocation);
            case ErrorReply error:
                return Failed(invocation, error.Code, error.Message);
            default:
                return Failed(invocation, "protocol", $"unexpected reply {reply.GetType().Name}");
        }
    }

    private static async Task<CommandOutcome> RunHandler(Func<CommandInvocation, Task> handler, CommandInvocation invocation)
    {
        try
        {
            await handler(invocation);
            return new CommandOutcome(invocation.CommandId, invocation.RequestId, invocation.Trigger, CommandOutcomeKind.RanInUi);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Diag.Request(LogLevel.Error, invocation.RequestId, Target, "UI command handler failed",
                new LogField("command", invocation.CommandId), new LogField("error", error.ToString()));
            return new CommandOutcome(invocation.CommandId, invocation.RequestId, invocation.Trigger,
                CommandOutcomeKind.Failed, ErrorCode: "ui_error", ErrorMessage: error.Message);
        }
    }

    private static CommandOutcome Failed(CommandInvocation invocation, string code, string message)
    {
        Diag.Request(LogLevel.Info, invocation.RequestId, Target, "command failed",
            new LogField("command", invocation.CommandId), new LogField("code", code), new LogField("error", message));
        return new CommandOutcome(invocation.CommandId, invocation.RequestId, invocation.Trigger,
            CommandOutcomeKind.Failed, ErrorCode: code, ErrorMessage: message);
    }

    private static void Log(string requestId, string commandId, string target, string trigger) =>
        Diag.Request(LogLevel.Info, requestId, Target, "command executed",
            new LogField("command", commandId), new LogField("target", target), new LogField("trigger", trigger));
}
