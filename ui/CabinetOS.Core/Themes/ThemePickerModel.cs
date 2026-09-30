using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>
/// One theme in the picker: what <c>list_themes</c> says, the Mica tint of
/// its swatch (null: plain Mica), and whether it is the theme in effect.
/// </summary>
public sealed record ThemeChoice(ThemeInfo Info, string? Tint, bool IsCurrent);

/// <summary>
/// The theme picker (<c>preferences.selectColorTheme</c>, docs/ui.md,
/// "Themes"): the core's themes in its order, the highlight the keys move,
/// and applying one through <c>set_value ui.theme</c>. The core then sends
/// <c>theme_changed</c>, which the window applies. <c>list_themes</c> carries
/// each theme's Mica tint since protocol 11, so opening the picker is one
/// request; with an older core every swatch shows plain Mica. While the
/// picker is open the highlighted theme is previewed: <see cref="PreviewDelay"/>
/// after the highlight stops, <c>get_theme</c> fetches it whole and
/// <see cref="Preview"/> hands it to the window, which paints it without
/// writing anything.
/// </summary>
public sealed class ThemePickerModel(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::theme";

    private int _load;
    private int _preview;
    private bool _previews;

    /// <summary>The rows, the rows' tints, the highlight or the error changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The window is to paint this theme as a preview, or, with null, the
    /// theme in effect again. Raised on the thread that moved the highlight.
    /// </summary>
    public event Action<ColorTheme?>? Preview;

    /// <summary>How long the highlight must rest on a row before its theme is fetched.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Whether the window shows a previewed theme rather than the theme in effect.</summary>
    public bool IsPreviewShown { get; private set; }

    /// <summary>The themes, in the core's order.</summary>
    public IReadOnlyList<ThemeChoice> Rows { get; private set; } = [];

    /// <summary>The highlighted row, or -1.</summary>
    public int Highlight { get; private set; } = -1;

    /// <summary>Why the last step failed, for the picker's footer; null when it did not.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether a <c>set_value</c> is on its way.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Lists the themes with <paramref name="currentId"/> marked and
    /// highlighted. False when the core has no themes (the error says so).
    /// </summary>
    public async Task<bool> LoadAsync(string? currentId)
    {
        var load = ++_load;
        Error = null;
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new ListThemesRequest());
        }
        catch (IOException error)
        {
            return Fail(error.Message);
        }
        if (load != _load)
        {
            return false;
        }
        switch (reply)
        {
            case ThemesReply themes:
                Rows = themes.Themes.Select(t => new ThemeChoice(t, t.Mica?.Tint, t.Id == currentId)).ToList();
                var current = Rows.ToList().FindIndex(r => r.IsCurrent);
                Highlight = Rows.Count == 0 ? -1 : Math.Max(0, current);
                Changed?.Invoke();
                // On the current row this sends nothing; with no current row, row 0 is shown as the highlight says.
                SchedulePreview();
                return true;
            case ErrorReply { Code: ErrorCodes.UnknownRequest }:
                return Fail("This core has no themes yet (list_themes).");
            case ErrorReply error:
                return Fail(error.Message);
            default:
                return false;
        }
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, staying on the list.</summary>
    public void Move(int delta)
    {
        if (Rows.Count > 0)
        {
            SetHighlight(Math.Clamp(Highlight + delta, 0, Rows.Count - 1));
        }
    }

    /// <summary>Puts the highlight on row <paramref name="index"/>.</summary>
    public void SetHighlight(int index)
    {
        if ((uint)index < (uint)Rows.Count && index != Highlight)
        {
            Highlight = index;
            Changed?.Invoke();
            SchedulePreview();
        }
    }

    /// <summary>The picker opened: from now on the highlighted theme is previewed.</summary>
    public void BeginPreviews() => _previews = true;

    /// <summary>
    /// The picker closed. With <paramref name="restore"/> the theme in effect
    /// is painted again if a preview was shown (Esc, a click outside, another
    /// view taking the place); without it the preview stays on screen for the
    /// <c>theme_changed</c> that makes it the theme in effect. A preview on its
    /// way is dropped either way.
    /// </summary>
    public void EndPreviews(bool restore)
    {
        _previews = false;
        _preview++;
        var shown = IsPreviewShown;
        IsPreviewShown = false;
        if (restore && shown)
        {
            Preview?.Invoke(null);
        }
    }

    /// <summary>
    /// The core's <c>theme_changed</c> made <paramref name="id"/> the theme in
    /// effect while the picker is open: the check moves there, the highlight
    /// stays. The window now shows that theme, so no preview is shown any more
    /// and one on its way is dropped.
    /// </summary>
    public void MarkCurrent(string id)
    {
        Rows = Rows.Select(r => r with { IsCurrent = r.Info.Id == id }).ToList();
        _preview++;
        IsPreviewShown = false;
        Changed?.Invoke();
    }

    private void SchedulePreview()
    {
        if (_previews && (uint)Highlight < (uint)Rows.Count)
        {
            _ = PreviewAsync(++_preview, Highlight);
        }
    }

    // The awaits stay on the caller's context: Preview is raised on the UI thread, as Changed is.
    private async Task PreviewAsync(int sequence, int row)
    {
        if (PreviewDelay > TimeSpan.Zero)
        {
            await Task.Delay(PreviewDelay);
            if (sequence != _preview)
            {
                return;
            }
        }
        var choice = Rows[row];
        if (choice.IsCurrent)
        {
            if (IsPreviewShown)
            {
                IsPreviewShown = false;
                Preview?.Invoke(null);
            }
            return;
        }
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new GetThemeRequest { ThemeId = choice.Info.Id });
        }
        catch (IOException error)
        {
            Diag.Debug(Target, "theme preview failed", new LogField("theme", choice.Info.Id), new LogField("error", error.Message));
            return;
        }
        if (sequence != _preview)
        {
            return;
        }
        switch (reply)
        {
            case ThemeReply { Theme: var theme }:
                IsPreviewShown = true;
                Preview?.Invoke(theme);
                break;
            case ErrorReply error:
                // Enter shows the core's reason in the footer; a preview only leaves the screen as it is.
                Diag.Debug(Target, "theme preview failed", new LogField("theme", choice.Info.Id), new LogField("code", error.Code),
                    new LogField("error", error.Message));
                break;
        }
    }

    /// <summary>
    /// Makes the highlighted theme (or row <paramref name="index"/>) the theme
    /// in effect. True once the core wrote <c>ui.theme</c>; its
    /// <c>theme_changed</c> follows. False with <see cref="Error"/> set.
    /// </summary>
    public async Task<bool> ApplyAsync(int? index = null, string? requestId = null)
    {
        var row = index ?? Highlight;
        if ((uint)row >= (uint)Rows.Count || IsApplying)
        {
            return false;
        }
        var id = Rows[row].Info.Id;
        IsApplying = true;
        Error = null;
        try
        {
            var reply = await core.RequestAsync(new SetValueRequest("ui.theme", Text(id)) { Id = requestId ?? "" });
            switch (reply)
            {
                case OkReply:
                    Diag.Info(Target, "theme chosen", new LogField("theme", id));
                    return true;
                case ErrorReply error:
                    return Fail(error.Code == ErrorCodes.UnknownRequest ? "This core cannot change settings yet (set_value)." : error.Message);
                default:
                    return false;
            }
        }
        catch (IOException error)
        {
            return Fail(error.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    private bool Fail(string message)
    {
        Error = message;
        Changed?.Invoke();
        return false;
    }

    private static JsonElement Text(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
