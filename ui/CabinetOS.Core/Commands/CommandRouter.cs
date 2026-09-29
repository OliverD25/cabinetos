using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Commands;

/// <summary>One run of a command, as its handler sees it.</summary>
/// <param name="CommandId">The command's ID.</param>
/// <param name="Args">Its arguments, if any.</param>
/// <param name="RequestId">
/// The ULID that names this run in the logs. It is also the run's trace: every request
/// the run sends and every line it logs carries it (<see cref="Diag.CurrentTrace"/>).
/// </param>
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

    /// <summary>It did not run: a dialog of the window was open and does not take it (<see cref="CommandRouter.SetModal"/>).</summary>
    Refused,

    /// <summary>It did not run: the user closed the prompt the command asks for (<see cref="CommandRouter.AskInput"/>).</summary>
    Cancelled,
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
/// The UI also has commands the core's registry does not list
/// (<see cref="RegisterLocal"/>): the buttons of its own controls, such as
/// the transfer flyout's. They run only here.
/// </remarks>
public sealed class CommandRouter(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::commands";

    private readonly Dictionary<string, Func<CommandInvocation, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CommandInvocation, Task>> _local = new(StringComparer.Ordinal);
    private Dictionary<string, CommandInfo> _byId = new(StringComparer.Ordinal);
    private HashSet<string> _modalAllows = new(StringComparer.Ordinal);

    /// <summary>Raised on the calling thread when the command list changed.</summary>
    public event Action? CommandsChanged;

    /// <summary>Raised on the calling thread when a run ended.</summary>
    public event Action<CommandOutcome>? Completed;

    /// <summary>Raised on the calling thread just before a command runs, so the window can make room for what it does.</summary>
    public event Action<CommandInvocation>? Executing;

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

    /// <summary>
    /// What a plugin's command gets when it runs without arguments (from the
    /// palette or a key): the files it concerns, as <c>path</c> and
    /// <c>paths</c> (docs/plugins.md, "What the shell passes"). Null leaves
    /// the arguments out.
    /// </summary>
    public Func<CommandInfo, JsonElement?>? PluginArgs { get; set; }

    /// <summary>
    /// Asks the user for the text of a plugin command that has an <c>input</c>
    /// (<see cref="PluginInput"/>): the window's prompt box. Null when the user
    /// cancelled, and the command then does not run. A router without it runs
    /// such a command with no <c>input</c>.
    /// </summary>
    public Func<CommandInfo, Task<string?>>? AskInput { get; set; }

    /// <summary>The dialog of the window that is open, as <see cref="SetModal"/> named it, or null.</summary>
    public string? Modal { get; private set; }

    /// <summary>
    /// A modal dialog of the window opened (<paramref name="dialog"/> names it
    /// in the log) or closed (null). While one is open, only the dialog's own
    /// commands in <paramref name="allowed"/> run; any other ends as
    /// <see cref="CommandOutcomeKind.Refused"/> without running, so no key,
    /// web page or plugin acts on the window under the dialog.
    /// </summary>
    public void SetModal(string? dialog, params string[] allowed)
    {
        Modal = dialog;
        _modalAllows = new HashSet<string>(dialog is null ? [] : allowed, StringComparer.Ordinal);
    }

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
        // One run is one user action: its requests and its lines carry its ULID as their trace.
        using var trace = Diag.BeginTrace(requestId);
        if (Modal is { } dialog && !_modalAllows.Contains(commandId))
        {
            // Before Executing: nothing may make room for a command that does not run.
            Diag.Request(LogLevel.Info, requestId, Target, "command refused: a dialog is open",
                new LogField("command", commandId), new LogField("trigger", trigger), new LogField("dialog", dialog));
            var refused = new CommandOutcome(commandId, requestId, trigger, CommandOutcomeKind.Refused);
            Completed?.Invoke(refused);
            return refused;
        }
        var info = Find(commandId);
        if (args is null && info is { Source.Kind: "plugin" } && PluginArgs?.Invoke(info) is { } files)
        {
            args = files;
        }
        var invocation = new CommandInvocation(commandId, args, requestId, trigger);
        Executing?.Invoke(invocation);
        if (info is not null && AskInput is { } ask && PluginInput.Asks(info, args))
        {
            // After Executing: the palette that ran the command has made room for the prompt.
            var text = await ask(info);
            if (text is null)
            {
                Diag.Request(LogLevel.Info, requestId, Target, "command cancelled: its prompt was closed", new LogField("command", commandId));
                var cancelled = new CommandOutcome(commandId, requestId, trigger, CommandOutcomeKind.Cancelled);
                Completed?.Invoke(cancelled);
                return cancelled;
            }
            invocation = invocation with { Args = PluginInput.With(args, text) };
        }
        CommandOutcome outcome;
        // A window command the registry does not list (yet: before list_commands answers, or
        // from an older core) still runs its handler here instead of failing in the core.
        if (info is null && (_local.TryGetValue(commandId, out var local) || _handlers.TryGetValue(commandId, out local)))
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
