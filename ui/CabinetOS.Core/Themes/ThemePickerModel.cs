using System.Buffers;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Themes;

/// <summary>
/// One theme in the picker: what <c>list_themes</c> says, the Mica tint of
/// its swatch once its file was read (null: plain Mica, or not read yet),
/// and whether it is the theme in effect.
/// </summary>
public sealed record ThemeChoice(ThemeInfo Info, string? Tint, bool IsCurrent);

/// <summary>
/// The theme picker (<c>preferences.selectColorTheme</c>, docs/ui.md,
/// "Themes"): the core's themes in its order, the highlight the keys move,
/// and applying one through <c>set_value ui.theme</c>. The core then sends
/// <c>theme_changed</c>, which the window applies. The swatches' tints need
/// each theme's file (<c>list_themes</c> has only the accent), so every theme
/// is read once when the picker opens.
/// </summary>
public sealed class ThemePickerModel(ICoreChannel core)
{
    private const string Target = "cabinetos_ui::theme";

    private int _load;

    /// <summary>The rows, the rows' tints, the highlight or the error changed.</summary>
    public event Action? Changed;

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
    /// highlighted, then reads each theme's tint. False when the core has no
    /// themes (the error says so).
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
                Rows = themes.Themes.Select(t => new ThemeChoice(t, null, t.Id == currentId)).ToList();
                var current = Rows.ToList().FindIndex(r => r.IsCurrent);
                Highlight = Rows.Count == 0 ? -1 : Math.Max(0, current);
                Changed?.Invoke();
                // The core answers these on its own threads, in any order: the tints are set together once all came.
                var tints = await Task.WhenAll(Rows.Select(r => ReadTintAsync(r.Info.Id)));
                if (load == _load && tints.Any(t => t is not null))
                {
                    Rows = Rows.Select((row, i) => row with { Tint = tints[i] }).ToList();
                    Changed?.Invoke();
                }
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

    // The theme's Mica tint; null for plain Mica, and for a file that cannot be read (list_themes listed it anyway).
    private async Task<string?> ReadTintAsync(string id)
    {
        try
        {
            return await core.RequestAsync(new GetThemeRequest { ThemeId = id }) is ThemeReply { Theme.Mica.Tint: var tint } ? tint : null;
        }
        catch (IOException)
        {
            return null;
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
