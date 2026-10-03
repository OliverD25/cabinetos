using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Prompts;
using CabinetOS.Core.Updates;
using CabinetOS.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// The three update settings that were reachable from the file only (the settings-three-ways skill, gap 6): update.check,
// update.autoInstall and update.channel. Each is a command in the palette, a row in the update pill's flyout and in the
// top row's "Update Settings" menu, and a key of cabinetos.json. The pill is there only while an update runs or waits, so
// the top row's menu is the control that is always there; both are drawn from UpdateSettingsMenu, and both follow the
// configuration the core last sent.
public sealed partial class MainWindow
{
    private readonly MenuFlyout _updateSettingsFlyout = new();
    private string _updateFlyoutText = "";

    private void SetUpUpdateSettings()
    {
        // A right-click on the pill (or the menu key on it): its left click stays "Restart to Update".
        UpdatePill.ContextFlyout = _updateSettingsFlyout;
        UpdateUpdateSettingsFlyout();
    }

    private void RegisterUpdateSettingsCommands()
    {
        _router.RegisterUiHandler("update.toggleCheck", _ => ToggleSettingAsync("update.check", !_settings.UpdateCheck,
            on => on ? "CabinetOS looks for updates once a day." : "CabinetOS does not look for updates by itself; Update: Check for Updates still does.",
            "The automatic check"));
        _router.RegisterUiHandler("update.toggleAutoInstall", _ => ToggleSettingAsync("update.autoInstall", !_settings.UpdateAutoInstall,
            on => on ? "A downloaded update is installed at once; you restart when you like." : "A downloaded update waits until you choose Restart now.",
            "The automatic install"));
        _router.RegisterUiHandler("update.chooseChannel", ChooseUpdateChannelAsync);
    }

    // update.channel: "Update: Channel" and the menus' channel rows (which give {"value": ...} and skip the list).
    private async Task ChooseUpdateChannelAsync(CommandInvocation invocation)
    {
        var current = UpdateSettingsMenu.NormalizeChannel(_settings.UpdateChannel);
        var chosen = CommandArgs.Text(invocation.Args, "value");
        if (chosen is null)
        {
            var rows = UpdateSettingsMenu.Channels.Select(name => new PromptRow(name, name == "preview"
                ? "Also versions with a pre-release tag, such as 0.2.0-preview.1"
                : "Released versions only")).ToList();
            var row = await PickRowAsync("Update channel", rows, rows.FindIndex(r => r.Title == current),
                "Enter chooses the channel the next check reads.");
            if (row is null)
            {
                return;
            }
            chosen = row.Title;
        }
        chosen = UpdateSettingsMenu.NormalizeChannel(chosen);
        if (chosen == current)
        {
            ShowNotice($"The {chosen} channel is the one in use already.");
            return;
        }
        await ChooseSettingAsync("update.channel", chosen, channel => $"Updates come from the {channel} channel.", "The update channel");
    }

    // The pill's flyout, drawn again at each read of the configuration so an edit of the file moves its checks.
    private void UpdateUpdateSettingsFlyout()
    {
        var rows = UpdateSettingsMenu.Rows(_settings.UpdateCheck, _settings.UpdateAutoInstall, _settings.UpdateChannel);
        _updateSettingsFlyout.Items.Clear();
        foreach (var row in rows)
        {
            if (row.Value is not null && _updateSettingsFlyout.Items.Count == 2)
            {
                _updateSettingsFlyout.Items.Add(new MenuFlyoutSeparator());
            }
            var item = new MenuFlyoutItem
            {
                Text = row.Title,
                Icon = new FontIcon { Glyph = row.Checked ? "" : "", FontSize = 12 },
            };
            AutomationProperties.SetItemStatus(item, row.Checked ? "on" : "off");
            item.Click += (_, _) => RunUpdateSettingRow(row);
            _updateSettingsFlyout.Items.Add(item);
        }
        _updateFlyoutText = UpdateSettingsMenu.Describe(rows);
    }

    private void RunUpdateSettingRow(UpdateSettingRow row) =>
        _ = _router.ExecuteAsync(row.CommandId, row.Value is null ? null : CommandArgs.With("value", row.Value), trigger: "button");

    // settings-do:update-flyout|<row title>: the flyout's row pressed through its automation peer, as a screen reader would; when
    // the peer will not press a row of a flyout that was never opened, the row's own handler runs. The pill that holds the
    // flyout is shown only while an update runs, which a test does not have.
    private bool PressUpdateFlyoutRow(string title)
    {
        foreach (var item in _updateSettingsFlyout.Items.OfType<MenuFlyoutItem>().Where(item => item.Text == title))
        {
            if (FrameworkElementAutomationPeer.CreatePeerForElement(item)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            {
                try
                {
                    invoke.Invoke();
                    return true;
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    Diag.Info(SettingsTarget, "the flyout's row would not be pressed by its peer", new LogField("title", title), new LogField("error", error.Message));
                }
            }
            var row = UpdateSettingsMenu.Rows(_settings.UpdateCheck, _settings.UpdateAutoInstall, _settings.UpdateChannel).First(r => r.Title == title);
            RunUpdateSettingRow(row);
            return true;
        }
        return false;
    }
}
