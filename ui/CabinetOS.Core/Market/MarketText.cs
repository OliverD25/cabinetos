using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Plugins;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Themes;

namespace CabinetOS.Core.Market;

/// <summary>How a marketplace card and its detail column put an item into words (design view C).</summary>
public static class MarketText
{
    /// <summary>The kind chip: "Theme", "WASM plugin" or "Tool".</summary>
    public static string KindLabel(MarketItem item) => item.Kind switch
    {
        ExtensionKinds.Theme => "Theme",
        ExtensionKinds.Tool => "Tool",
        _ => "WASM plugin",
    };

    /// <summary>"CabinetOS · v0.1.0".</summary>
    public static string Byline(MarketItem item) => $"{item.Author.Name} · v{item.Version}";

    /// <summary>The average with one decimal, or a dash when the index gives none.</summary>
    public static string Rating(MarketItem item) =>
        item.Rating is { } rating ? rating.Average.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>The card's "(120)", or "no ratings".</summary>
    public static string RatingCount(MarketItem item) => item.Rating is { } rating ? $"({Count(rating.Count)})" : "no ratings";

    /// <summary>The stat tile's "120 ratings", or "no ratings".</summary>
    public static string RatingsLabel(MarketItem item) => item.Rating switch
    {
        null => "no ratings",
        { Count: 1 } => "1 rating",
        { Count: var count } => $"{Count(count)} ratings",
    };

    /// <summary>"5.4k", or a dash when the index does not know.</summary>
    public static string Installs(MarketItem item) => item.Installs is { } installs ? Count(installs) : "—";

    /// <summary>The card's "· 5.4k installs", or nothing when the index does not know.</summary>
    public static string InstallsLine(MarketItem item) => item.Installs is { } installs ? $"· {Count(installs)} installs" : "";

    /// <summary>The download's size: "26 KB".</summary>
    public static string Size(MarketItem item) => DisplayFormat.Bytes(item.Size, CultureInfo.InvariantCulture);

    /// <summary>A count as the design writes it: 950, 5.4k, 1.2M.</summary>
    public static string Count(ulong count) => count switch
    {
        < 1000 => count.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (count / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => (count / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>The primary button's words.</summary>
    public static string ActionText(MarketAction action) => action switch
    {
        MarketAction.InstallAndApply => "Install and apply",
        MarketAction.Update => "Update",
        MarketAction.Installing => "Installing…",
        MarketAction.Installed => "Installed",
        MarketAction.Applied => "Applied",
        _ => "Install",
    };

    /// <summary>
    /// The line under the detail column's buttons, or "": the version an
    /// update replaces, another version already installed, or why the
    /// marketplace leaves alone what it did not install (trust rule 7).
    /// </summary>
    public static string Note(MarketplaceModel model, MarketItem item) => model.InstalledVersionOf(item) switch
    {
        { } installed when model.HasUpdate(item) => item is { Kind: ExtensionKinds.Plugin, Capabilities.Count: > 0 }
            ? $"Version {installed} is installed. You review the permissions again before the update is installed."
            : $"Version {installed} is installed. The update replaces its files.",
        { } installed when installed != item.Version => $"Version {installed} is installed; the index offers {item.Version}.",
        { } => "",
        null when model.IsPresent(item) =>
            "This one is here already, but not from the marketplace: it ships with CabinetOS, or was copied in by hand. So the marketplace does not replace or remove it.",
        null => "",
    };

    /// <summary>
    /// The icon tile: 1–2 letters of the name on a colour. A theme shows its
    /// own accent; anything else a colour taken from its ID, until
    /// extensions bring icons (docs/design/README.md, "Assets").
    /// </summary>
    public static PluginTile Tile(MarketItem item)
    {
        var tile = PluginTile.For(item.Id, item.Name);
        if (item.Kind == ExtensionKinds.Theme
            && item.Manifest.ValueKind == JsonValueKind.Object
            && item.Manifest.TryGetProperty("accent", out var accent)
            && accent.ValueKind == JsonValueKind.String
            && Argb.TryParse(accent.GetString(), out var color))
        {
            return tile with { Color = $"#{color.R:X2}{color.G:X2}{color.B:X2}" };
        }
        return tile;
    }

    /// <summary>
    /// The author's web page for "Source": only an absolute http or https
    /// URL, which the browser opens. Anything else (a file, another scheme)
    /// is not opened from an index.
    /// </summary>
    public static Uri? SourceUri(MarketItem item) =>
        Uri.TryCreate(item.Author.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
}
