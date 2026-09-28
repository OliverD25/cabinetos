using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Protocol;
using CabinetOS.Services;

namespace CabinetOS;

// Core Plugins in the window: the list, the permissions review, and what the status bar says
// when a plugin's state changes (docs/ui.md, "Plugins").
public sealed partial class MainWindow
{
    private readonly PluginWatch _plugins = new();
    private bool _pluginsUnavailable;
    private int _pluginsRefresh;

    private void SetUpPlugins()
    {
        PluginsView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        ReviewView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
    }

    private void RegisterPluginCommands()
    {
        // In the palette although the core's registry does not have it yet.
        _router.RegisterWindowCommand(
            new CommandInfo("plugins.list", "Plugins", "Show Plugins", [], [], new CommandSource("window", null, null), "ui", null, false),
            _ => ShowPluginsAsync());
        _router.RegisterLocal("plugins.review", invocation =>
            CommandArgs.Text(invocation.Args, "id") is { } id ? ReviewPluginAsync(id) : Task.CompletedTask);
        _router.RegisterLocal("plugins.reload", invocation =>
            CommandArgs.Text(invocation.Args, "id") is { } id ? ReloadPluginAsync(id, invocation.RequestId) : Task.CompletedTask);
        _router.RegisterLocal("plugins.grant", GrantAsync);
    }

    private async Task ShowPluginsAsync()
    {
        FileMenu.Close();
        if (await ReadPluginsAsync() is not { } plugins)
        {
            return;
        }
        PluginsView.Show(plugins.Select(p => new PluginRow(p)).ToList(), PluginsFolder());
    }

    private async Task ReviewPluginAsync(string pluginId)
    {
        var plugins = await ReadPluginsAsync();
        if (plugins?.FirstOrDefault(p => p.Id == pluginId) is not { } plugin)
        {
            return;
        }
        if (plugin.State.Type != PluginState.NeedsReview)
        {
            ShowNotice($"{plugin.Name} does not wait for a review any more.");
            return;
        }
        _plugins.MarkReviewed(plugin.Id);
        ReviewView.Open(new PermissionReview(plugin));
    }

    private async Task GrantAsync(CommandInvocation invocation)
    {
        if (ReviewView.Review is not { } review)
        {
            return;
        }
        ReviewView.ShowBusy(true);
        ReviewView.ShowError(null);
        var granted = await review.AllowAsync(_session, invocation.RequestId);
        ReviewView.ShowBusy(false);
        if (!granted)
        {
            ReviewView.ShowError(review.Error);
            return;
        }
        Diag.Request(LogLevel.Info, invocation.RequestId, Target, "capabilities granted",
            new LogField("plugin", review.Plugin.Id), new LogField("capabilities", string.Join(",", review.ToGrant)));
        CloseReview();
        ShowNotice($"{review.Plugin.Name} is starting…");
    }

    private async Task ReloadPluginAsync(string pluginId, string requestId)
    {
        CoreReply reply;
        try
        {
            reply = await _session.RequestAsync(new ReloadPluginRequest(pluginId) { Id = requestId });
        }
        catch (IOException error)
        {
            ShowNotice($"Cannot reload {pluginId}: {error.Message}", isError: true);
            return;
        }
        if (reply is ErrorReply refused)
        {
            ShowNotice($"Cannot reload {pluginId}: {refused.Message}", isError: true);
        }
        await RefreshPluginsAsync();
    }

    private void CloseReview()
    {
        ReviewView.Close();
        if (PluginsView.IsOpen)
        {
            PluginsView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }
        else
        {
            FocusActivePane();
        }
    }

    private void ClosePlugins()
    {
        PluginsView.Close();
        FocusActivePane();
    }

    /// <summary>
    /// Reads the plugins again after a change (<c>plugin_state_changed</c>,
    /// <c>plugin_crashed</c>, a start): says in the status bar which became
    /// active or crashed, opens the review for one that newly waits, and
    /// keeps an open list up to date.
    /// </summary>
    private async Task RefreshPluginsAsync()
    {
        var refresh = ++_pluginsRefresh;
        if (await ReadPluginsAsync(quiet: true) is not { } plugins || refresh != _pluginsRefresh)
        {
            return;
        }
        var first = _plugins.IsFresh;
        var changes = _plugins.Update(plugins);
        foreach (var plugin in changes.BecameActive)
        {
            ShowNotice($"{plugin.Name} is active.");
        }
        foreach (var plugin in changes.Crashed)
        {
            ShowNotice($"{plugin.Name} crashed: {plugin.State.Message}", isError: true);
        }
        if (changes.ToReview.FirstOrDefault() is { } waiting && !ReviewView.IsOpen && !_palette.IsOpen)
        {
            ReviewView.Open(new PermissionReview(waiting));
        }
        else if (first && plugins.Where(p => p.State.Type == PluginState.NeedsReview).ToList() is { Count: > 0 } review)
        {
            // Found at start: said once, not put in front of the user.
            ShowNotice(review.Count == 1
                ? $"{review[0].Name} waits for your review: run \"Plugins: Show Plugins\" in the palette."
                : $"{review.Count} plugins wait for your review: run \"Plugins: Show Plugins\" in the palette.");
        }
        if (PluginsView.IsOpen)
        {
            PluginsView.Show(plugins.Select(p => new PluginRow(p)).ToList(), PluginsFolder());
        }
    }

    private async Task<IReadOnlyList<PluginInfo>?> ReadPluginsAsync(bool quiet = false)
    {
        if (_pluginsUnavailable)
        {
            if (!quiet)
            {
                ShowNotice("This core has no plugins yet (list_plugins).");
            }
            return null;
        }
        CoreReply reply;
        try
        {
            reply = await _session.RequestAsync(new ListPluginsRequest());
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot list the plugins", new LogField("error", error.Message));
            return null;
        }
        switch (reply)
        {
            case PluginsReply plugins:
                return plugins.Plugins;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                _pluginsUnavailable = true;
                if (!quiet)
                {
                    ShowNotice("This core has no plugins yet (list_plugins).");
                }
                return null;
            case ErrorReply error:
                Diag.Info(Target, "list_plugins failed", new LogField("code", error.Code), new LogField("error", error.Message));
                return null;
            default:
                return null;
        }
    }

    // The folder the core reads plugins from, for the empty list's hint: the variable the core also reads, else the default.
    private static string PluginsFolder() =>
        Environment.GetEnvironmentVariable("CABINETOS_PLUGINS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CabinetOS", "plugins");
}
