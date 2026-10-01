using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Market;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CabinetOS;

// The marketplace (design view C): the index the core reads, installs and uninstalls the core
// does, and the review before a plugin is installed (docs/ui.md, "The marketplace").
public sealed partial class MainWindow
{
    private const string MarketTarget = "cabinetos_ui::market";

    private MarketplaceModel _market = null!;
    private bool _marketRead;

    // When the window last had a key or the pointer: the marketplace view is prepared only in a quiet moment.
    private readonly InputQuiet _quiet = new(() => Environment.TickCount64);

    private void SetUpMarket()
    {
        _market = new MarketplaceModel(_session);
        MarketView.Model = _market;
        // Every key and pointer event over the window, handled or not; a move counts too, as a drag of a scroll bar is moves.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _quiet.Touch()), handledEventsToo: true);
        foreach (var pointer in new[] { UIElement.PointerPressedEvent, UIElement.PointerMovedEvent, UIElement.PointerWheelChangedEvent })
        {
            RootGrid.AddHandler(pointer, new PointerEventHandler((_, _) => _quiet.Touch()), handledEventsToo: true);
        }
        MarketView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        MarketplaceButton.Click += (_, _) => _ = _router.ExecuteAsync("marketplace.browse", trigger: "button");
        _themes.Applied += _ => _market.SetCurrentTheme(_themes.Theme?.Id);
        // A command that works on the panes shows them first: a sidebar folder, Ctrl+`, F5.
        _router.Executing += invocation =>
        {
            if (MarketView.IsOpen && MarketplaceModel.NeedsThePanes(invocation.CommandId))
            {
                CloseMarket(focusPane: false);
            }
        };
    }

    private void RegisterMarketCommands()
    {
        _router.RegisterUiHandler("marketplace.browse", _ => ToggleMarketAsync());
        // The view's own buttons, not in the core's registry.
        _router.RegisterLocal("market.refresh", invocation => _market.RefreshAsync(invocation.RequestId));
        _router.RegisterLocal("market.select", invocation => _market.Select(CommandArgs.Text(invocation.Args, "id")));
        _router.RegisterLocal("market.install", InstallFromMarketAsync);
        _router.RegisterLocal("market.uninstall", UninstallFromMarketAsync);
        _router.RegisterLocal("market.source", OpenSourceAsync);
    }

    private async Task ToggleMarketAsync()
    {
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: true);
            return;
        }
        OpenMarket();
        if (!_marketRead)
        {
            // The core reads the index only when asked (trust rule 6): at the first look, and on Refresh.
            _marketRead = true;
            await _market.RefreshAsync();
        }
    }

    private void OpenMarket()
    {
        // The marketplace takes the main column's place: what floats over the panes or edits them ends first.
        FileMenu.Close();
        CloseOtherOverlays(opening: null);
        EndAddressEdit();
        foreach (var pane in _paneViews.Where(v => v.IsRenaming))
        {
            pane.CancelRename();
        }
        // The search field takes the keyboard before the panes collapse (see the palette).
        MarketView.Open();
        MainColumn.Visibility = Visibility.Collapsed;
        UpdateMarketButton();
        Diag.Info(MarketTarget, "marketplace shown");
    }

    private void CloseMarket(bool focusPane)
    {
        if (!MarketView.IsOpen)
        {
            return;
        }
        MainColumn.Visibility = Visibility.Visible;
        // The pane takes the keyboard before the marketplace collapses (see the palette);
        // it must be laid out to take it.
        if (focusPane || IsFocusWithin(MarketView))
        {
            MainColumn.UpdateLayout();
            FocusPaneOrEditor();
        }
        MarketView.Close();
        UpdateMarketButton();
        Diag.Info(MarketTarget, "marketplace closed");
    }

    // The marketplace view's first layout made one frame of 54 to 81 ms at its first opening (docs/log/2026-10-01/
    // speed-items-ce-report.md). It is done ahead, hidden, after the menu shapes (in their idle slot, a low-priority
    // dispatcher turn) and only once the window had no key or pointer for a moment; input in between puts it off again.
    private void PrepareMarketplaceWhenQuiet()
    {
        if (_closing || MarketView.IsOpen)
        {
            return;
        }
        if (_quiet.WaitMs is > 0 and var wait)
        {
            _ = PrepareMarketplaceLaterAsync(wait);
            return;
        }
        MarketView.PrepareLayout();
    }

    private async Task PrepareMarketplaceLaterAsync(long waitMs)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(waitMs));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PrepareMarketplaceWhenQuiet);
    }

    private void UpdateMarketButton()
    {
        MarketplaceIcon.Foreground = ThemeResources.Brush(MarketView.IsOpen ? "CbAccentBrush" : "CbTextSecondaryBrush");
        UpdateRail();
    }

    // Esc: the detail column first, then the marketplace (the design's order).
    private void CloseMarketLevel()
    {
        if (_market.SelectedId is not null)
        {
            _market.Select(null);
        }
        else
        {
            CloseMarket(focusPane: true);
        }
    }

    // marketplace.index changed: the next look reads the new index, or now when the marketplace is shown.
    private void OnMarketIndexChanged()
    {
        _marketRead = MarketView.IsOpen;
        if (MarketView.IsOpen)
        {
            _ = _market.RefreshAsync();
        }
    }

    private async Task InstallFromMarketAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "id") is not { } id || _market.Find(id) is not { } item)
        {
            return;
        }
        var update = _market.HasUpdate(item);
        MarketOutcome outcome;
        switch (item.Kind)
        {
            case ExtensionKinds.Plugin when item.Capabilities is { Count: > 0 }:
                // Trust rule 1: the user sees what it asks for before anything is downloaded; an update
                // too, since the core clears the old grants. "Allow and install" comes back through
                // plugins.grant (InstallReviewedAsync).
                ReviewView.Open(PermissionReview.ForInstall(item));
                return;
            case ExtensionKinds.Theme when update:
                // An update is not a choice of theme: it is not applied (the core applies it again if it is in effect).
                outcome = await _market.InstallAsync(id, invocation.RequestId);
                break;
            case ExtensionKinds.Theme:
                outcome = await _market.InstallAndApplyAsync(id, invocation.RequestId);
                ShowNotice(outcome.Ok ? $"{item.Name} is installed and applied." : $"Cannot install {item.Name}: {outcome.Error}", !outcome.Ok);
                return;
            default:
                // A tool, or a plugin that asks for nothing: there is nothing to review, and the core starts such a plugin at once.
                _plugins.MarkReviewed(id);
                outcome = await _market.InstallAsync(id, invocation.RequestId);
                break;
        }
        ShowNotice(outcome.Ok ? InstalledText(item, update) + "." : $"Cannot {(update ? "update" : "install")} {item.Name}: {outcome.Error}", !outcome.Ok);
    }

    private static string InstalledText(MarketItem item, bool update) =>
        update ? $"{item.Name} is updated to version {item.Version}" : $"{item.Name} is installed";

    // "Allow and install": the review closes, the detail column's button shows the download, and
    // the grant follows the install, so the plugin starts.
    private async Task InstallReviewedAsync(PermissionReview review, MarketItem item, string requestId)
    {
        CloseReview();
        var update = _market.HasUpdate(item);
        // The core installs it waiting for review; this user just reviewed it, so no second review opens.
        _plugins.MarkReviewed(item.Id);
        var outcome = await _market.InstallAndGrantAsync(item.Id, review.ToGrant, requestId);
        if (outcome.Ok)
        {
            Diag.Request(LogLevel.Info, requestId, MarketTarget, "plugin installed and granted",
                new LogField("plugin", item.Id), new LogField("version", item.Version), new LogField("capabilities", string.Join(",", review.ToGrant)));
            ShowNotice(InstalledText(item, update) + " and starting…");
        }
        else
        {
            ShowNotice($"Cannot {(update ? "update" : "install")} {item.Name}: {outcome.Error}", isError: true);
        }
    }

    private async Task UninstallFromMarketAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "id") is not { } id || _market.Find(id) is not { } item)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Uninstall {item.Name}?",
            Content = new TextBlock { Text = UninstallText(item), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }
        var outcome = await _market.UninstallAsync(id, invocation.RequestId);
        ShowNotice(outcome.Ok ? $"{item.Name} is uninstalled." : $"Cannot uninstall {item.Name}: {outcome.Error}", !outcome.Ok);
        if (outcome.Ok && item.Kind == ExtensionKinds.Plugin)
        {
            // A plugin leaves list_plugins without an event (docs/ipc.md, "The marketplace").
            await RefreshPluginsAsync();
        }
    }

    private static string UninstallText(MarketItem item) => item.Kind switch
    {
        ExtensionKinds.Plugin => "The plugin stops, and the files the marketplace installed are removed. Its own data folder and its settings in cabinetos.json stay.",
        ExtensionKinds.Theme => "The theme's file is removed from the themes folder.",
        _ => "The files the marketplace installed are removed. A file open in the tool closes.",
    };

    private async Task OpenSourceAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "id") is not { } id || _market.Find(id) is not { } item)
        {
            return;
        }
        if (MarketText.SourceUri(item) is not { } uri)
        {
            ShowNotice($"The index gives no web page for {item.Author.Name}.");
            return;
        }
        // The publisher's page in the default browser: the one thing the marketplace view does
        // itself (a launch, not file I/O), and only for http and https addresses.
        Diag.Request(LogLevel.Info, invocation.RequestId, MarketTarget, "source page opened", new LogField("url", uri.ToString()));
        if (!await Launcher.LaunchUriAsync(uri))
        {
            ShowNotice($"Windows did not open {uri}.", isError: true);
        }
    }

    // A tool installed or removed while the window runs: the tools are read again, and the
    // tabs of a tool that is gone close with its editor.
    private async Task ReloadToolsAsync()
    {
        await LoadToolsAsync();
        foreach (var gone in _strips.SelectMany(strip => strip.Tabs).Select(tab => tab.Tool).OfType<string>().Distinct()
            .Where(id => _tools.Tools.All(t => t.Manifest.Id != id)).ToList())
        {
            ForgetTool(gone);
        }
    }
}
