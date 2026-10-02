using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Market;
using CabinetOS.Core.Themes;
using CabinetOS.Services;
using CabinetOS.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CabinetOS;

// The theme gallery (docs/ui.md, "The theme gallery"): the themes catalogue as colour tiles, in place of the main column as
// the Extensions page is. A selected tile is previewed on the whole window through the picker's path (ThemeApplier.Preview);
// Enter or a double-click installs and applies it; Esc closes the gallery and paints the theme in effect again.
public sealed partial class MainWindow
{
    private const string GalleryTarget = "cabinetos_ui::theme";

    private ThemeGalleryModel _gallery = null!;
    private string _previewStatusShown = "";

    private void SetUpGallery()
    {
        _gallery = new ThemeGalleryModel(_session, () => _themes.SystemIsLight, SystemAccentColor);
        GalleryView.Model = _gallery;
        GalleryView.RunCommand = (id, args, trigger) => _router.ExecuteAsync(id, args, trigger);
        // The same path as the picker's preview: nothing is written, and null paints the theme in effect again.
        _gallery.Preview += theme =>
        {
            if (theme is null)
            {
                _themes.EndPreview();
            }
            else
            {
                _themes.Preview(theme);
            }
        };
        _gallery.Changed += UpdatePreviewStatus;
        // The theme in effect, and Windows' mode and accent, which the tile of a `system` theme is painted with.
        _themes.Applied += _ =>
        {
            _gallery.Market.SetCurrentTheme(_themes.Theme?.Id);
            _gallery.Refresh();
        };
        // A command that works on the panes shows them first, as for the marketplace.
        _router.Executing += invocation =>
        {
            if (GalleryView.IsOpen && MarketplaceModel.NeedsThePanes(invocation.CommandId))
            {
                CloseGallery(restore: true, focusPane: false);
            }
        };
    }

    private void RegisterGalleryCommands()
    {
        _router.RegisterUiHandler("themes.browse", _ => OpenGalleryAsync());
        // The tiles' own actions, not in the core's registry: Enter, a double-click and the button; Delete and the overflow's Remove.
        _router.RegisterLocal("gallery.activate", ActivateGalleryTileAsync);
        _router.RegisterLocal("gallery.remove", RemoveGalleryTileAsync);
    }

    // The accent a theme without one shows on its tile: Windows' accent, in the shade the mode of Windows uses.
    private Argb SystemAccentColor()
    {
        var light = _themes.SystemIsLight;
        var shades = _themes.SystemAccent(light);
        return light ? shades.Dark1 : shades.Light2;
    }

    private async Task OpenGalleryAsync()
    {
        if (GalleryView.IsOpen)
        {
            GalleryView.FocusGrid();
            return;
        }
        // The gallery takes the main column's place: what floats over the panes or edits them ends first.
        FileMenu.Close();
        CloseOtherOverlays(opening: null);
        PrepareToCoverPanes();
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: false);
        }
        _gallery.BeginPreviews();
        GalleryView.Open();
        MainColumn.Visibility = Visibility.Collapsed;
        UpdateRail();
        Diag.Info(GalleryTarget, "gallery shown");
        await _gallery.LoadAsync(_themes.Theme?.Id);
    }

    // restore: the theme in effect is painted again if a preview is on screen (Esc, another view taking the place).
    private void CloseGallery(bool restore, bool focusPane)
    {
        if (!GalleryView.IsOpen)
        {
            return;
        }
        MainColumn.Visibility = Visibility.Visible;
        // The pane takes the keyboard before the gallery collapses (see the palette); it must be laid out to take it.
        if (focusPane || IsFocusWithin(GalleryView))
        {
            MainColumn.UpdateLayout();
            FocusPaneOrEditor();
        }
        GalleryView.Close();
        _gallery.EndPreviews(restore);
        UpdatePreviewStatus();
        UpdateRail();
        Diag.Info(GalleryTarget, "gallery closed", new LogField("restored", restore));
    }

    // The views that take the main column's place: opening one of the others, or a command for the panes, ends them.
    private void CloseCoveringViews()
    {
        if (MarketView.IsOpen)
        {
            CloseMarket(focusPane: false);
        }
        if (GalleryView.IsOpen)
        {
            CloseGallery(restore: true, focusPane: false);
        }
    }

    // The status bar says a theme is previewed, and how to get the theme in effect back.
    private void UpdatePreviewStatus()
    {
        var text = GalleryView.IsOpen ? _gallery.PreviewText ?? "" : "";
        PreviewStatusText.Text = text;
        PreviewStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text != _previewStatusShown)
        {
            _previewStatusShown = text;
            // What the status bar told the user, for the window tests and the live checks that read it.
            Diag.Info(GalleryTarget, "preview status shown", new LogField("text", text));
        }
    }

    // marketplace.themes changed: a shown gallery reads the catalogue again; a closed one does at its next opening.
    private void OnThemesCatalogueChanged()
    {
        if (GalleryView.IsOpen)
        {
            _ = _gallery.LoadAsync(_themes.Theme?.Id);
        }
    }

    private async Task ActivateGalleryTileAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "id") is not { } id || _gallery.Tiles.FirstOrDefault(t => t.Id == id) is not { } tile)
        {
            return;
        }
        var action = tile.Action;
        var outcome = await _gallery.ActivateAsync(id, invocation.RequestId);
        if (outcome is null)
        {
            return;
        }
        if (!outcome.Ok)
        {
            var verb = action switch
            {
                TileAction.Update => "update",
                TileAction.Apply => "apply",
                _ => "install",
            };
            ShowNotice($"Cannot {verb} {tile.Name}: {outcome.Error}", isError: true);
            return;
        }
        ShowNotice(action switch
        {
            TileAction.Install => $"{tile.Name} is installed and applied.",
            TileAction.Update => $"{tile.Name} is updated to version {tile.Item?.Version}.",
            _ => $"{tile.Name} is applied.",
        });
    }

    private async Task RemoveGalleryTileAsync(CommandInvocation invocation)
    {
        if (CommandArgs.Text(invocation.Args, "id") is not { } id
            || _gallery.Tiles.FirstOrDefault(t => t.Id == id) is not { CanRemove: true, Item: { } item } tile)
        {
            return;
        }
        if (!await ConfirmUninstallAsync(item))
        {
            return;
        }
        var outcome = await _gallery.RemoveAsync(id, invocation.RequestId);
        ShowNotice(outcome.Ok ? $"{tile.Name} is removed." : $"Cannot remove {tile.Name}: {outcome.Error}", !outcome.Ok);
    }

    // The snapshot aid's gallery: steps. state[:label] logs what the gallery shows; click:<id> and double:<id> are the pointer's;
    // filter:<name> is a chip; search:<text> the search field.
    private async Task RunGalleryStepAsync(string argument)
    {
        var colon = argument.IndexOf(':');
        var (verb, rest) = colon < 0 ? (argument, "") : (argument[..colon], argument[(colon + 1)..]);
        switch (verb)
        {
            case "state":
                GalleryView.LogState(rest);
                break;
            case "click" or "double":
                GalleryView.ClickForSnapshot(rest, doubleClick: verb == "double");
                break;
            case "filter" when Enum.TryParse<GalleryFilter>(rest, ignoreCase: true, out var filter):
                _gallery.SetFilter(filter);
                break;
            case "search":
                GalleryView.TypeForSnapshot(rest);
                break;
        }
        await Task.Delay(300);
    }
}
