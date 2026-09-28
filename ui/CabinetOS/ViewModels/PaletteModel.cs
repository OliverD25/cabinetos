using System.Collections.ObjectModel;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CabinetOS.ViewModels;

/// <summary>One keycap, or the word "then" between the two halves of a chord.</summary>
public sealed record KeycapPart(string Text, bool IsSeparator);

/// <summary>One command in the palette (design view B).</summary>
public sealed class PaletteRow : ObservableObject
{
    private bool _isHighlighted;
    private bool _isRecording;
    private string _recordingText = "Press keys…";
    private string? _inlineError;

    /// <summary>Makes the row for <paramref name="info"/>.</summary>
    public PaletteRow(CommandInfo info)
    {
        Info = info;
        Keycaps = info.Keys.Count > 0 && KeySequence.TryParse(info.Keys[0], out var keys)
            ? Parts(keys.DisplayParts())
            : [];
    }

    /// <summary>The command.</summary>
    public CommandInfo Info { get; }

    /// <summary>"{Category}:" in the design's tertiary color.</summary>
    public string CategoryText => $"{Info.Category}:";

    /// <summary>The command's title.</summary>
    public string Title => Info.Title;

    /// <summary>The plugin's name for a plugin's command, else null.</summary>
    public string? Badge => Info.Source.Kind == "plugin" ? Info.Source.Name ?? Info.Source.Id : null;

    /// <summary>The first binding as keycaps; chords are joined by "then".</summary>
    public IReadOnlyList<KeycapPart> Keycaps { get; private set; }

    /// <summary>Whether the command belongs to the Immutable System Tier: a lock instead of a pencil.</summary>
    public bool IsImmutable => Info.Immutable;

    /// <summary>Whether the arrow keys or the mouse put the highlight here.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    /// <summary>Whether the row is recording new keys.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    /// <summary>"Press keys…" or the combinations pressed so far.</summary>
    public string RecordingText
    {
        get => _recordingText;
        set => SetProperty(ref _recordingText, value);
    }

    /// <summary>Why the last rebinding was refused, shown in the row.</summary>
    public string? InlineError
    {
        get => _inlineError;
        set => SetProperty(ref _inlineError, value);
    }

    private static IReadOnlyList<KeycapPart> Parts(IReadOnlyList<string> display)
    {
        var parts = new List<KeycapPart>();
        for (var i = 0; i < display.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(new KeycapPart("then", true));
            }
            parts.Add(new KeycapPart(display[i], false));
        }
        return parts;
    }
}

/// <summary>
/// The command palette (design view B). The core ranks the commands
/// (<c>search_commands</c>); the palette only shows them, runs the chosen one
/// through the <see cref="CommandRouter"/>, and records new keys.
/// </summary>
public sealed class PaletteModel(ICoreChannel core, CommandRouter router) : ObservableObject
{
    private const string Target = "cabinetos_ui::palette";

    /// <summary>How long recording waits for a second combination (keybindings.md, "Chords").</summary>
    public static readonly TimeSpan RecordingWindow = TimeSpan.FromMilliseconds(1000);

    private bool _isOpen;
    private string _query = "";
    private int _highlight = -1;
    private int _search;
    private PaletteRow? _recording;
    private readonly List<KeyCombo> _combos = [];
    private int _recordingVersion;

    /// <summary>Raised when the palette opens, so the view can focus its input.</summary>
    public event Action? Opened;

    /// <summary>Raised when the palette closes, so the view can give the focus back.</summary>
    public event Action? Closed;

    /// <summary>Raised with the core's keymap after a rebinding took effect.</summary>
    public event Action<KeymapData>? KeymapUpdated;

    /// <summary>The rows shown, best match first.</summary>
    public ObservableCollection<PaletteRow> Rows { get; } = [];

    /// <summary>Whether the palette is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>What the user typed.</summary>
    public string Query => _query;

    /// <summary>"{n} commands".</summary>
    public string CountText => Rows.Count == 1 ? "1 command" : $"{Rows.Count} commands";

    /// <summary>The highlighted row's index, or -1.</summary>
    public int HighlightIndex => _highlight;

    /// <summary>Whether a row is recording keys: then every key goes to the recorder.</summary>
    public bool IsRecording => _recording is not null;

    /// <summary>Opens the palette with an empty query.</summary>
    public void Open()
    {
        if (IsOpen)
        {
            return;
        }
        IsOpen = true;
        _query = "";
        Opened?.Invoke();
        _ = SearchAsync("");
    }

    /// <summary>Closes the palette; a recording in progress is dropped.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        CancelRecording();
        IsOpen = false;
        Closed?.Invoke();
    }

    /// <summary>
    /// Asks the core to rank the commands for <paramref name="query"/> and
    /// shows the result. A new query highlights the best match; a refresh
    /// keeps the highlighted command.
    /// </summary>
    public async Task SearchAsync(string query, bool keepHighlight = false)
    {
        _query = query;
        var search = ++_search;
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(new SearchCommandsRequest(query, 1000));
        }
        catch (IOException error)
        {
            Diag.Info(Target, "search failed", new LogField("error", error.Message));
            return;
        }
        if (search != _search || reply is not SearchResultsReply results)
        {
            return;
        }
        var highlightedId = _highlight >= 0 && _highlight < Rows.Count ? Rows[_highlight].Info.Id : null;
        Rows.Clear();
        var missing = false;
        foreach (var hit in results.Hits)
        {
            if (router.Find(hit.Id) is { } info)
            {
                Rows.Add(new PaletteRow(info));
            }
            else
            {
                missing = true;
            }
        }
        if (missing)
        {
            // A plugin registered a command since the list was read.
            _ = RefreshCommandsAsync();
        }
        var keep = highlightedId is null ? -1 : IndexOf(highlightedId);
        _highlight = -1;
        SetHighlight(keepHighlight && keep >= 0 ? keep : 0);
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Shows the rows again, for example after the keymap changed.</summary>
    public Task RefreshAsync() => IsOpen ? SearchAsync(_query, keepHighlight: true) : Task.CompletedTask;

    /// <summary>Moves the highlight by <paramref name="delta"/> rows.</summary>
    public void MoveHighlight(int delta)
    {
        if (Rows.Count > 0)
        {
            SetHighlight(Math.Clamp(_highlight + delta, 0, Rows.Count - 1));
        }
    }

    /// <summary>Puts the highlight on row <paramref name="index"/>.</summary>
    public void SetHighlight(int index)
    {
        if (index == _highlight || Rows.Count == 0)
        {
            _highlight = Rows.Count == 0 ? -1 : _highlight;
            return;
        }
        if (_highlight >= 0 && _highlight < Rows.Count)
        {
            Rows[_highlight].IsHighlighted = false;
        }
        _highlight = Math.Clamp(index, 0, Rows.Count - 1);
        Rows[_highlight].IsHighlighted = true;
        OnPropertyChanged(nameof(HighlightIndex));
    }

    /// <summary>Runs the highlighted command.</summary>
    public Task RunHighlightedAsync() =>
        _highlight >= 0 && _highlight < Rows.Count ? RunAsync(Rows[_highlight]) : Task.CompletedTask;

    /// <summary>Closes the palette, then runs the command through the router.</summary>
    public async Task RunAsync(PaletteRow row)
    {
        if (row.IsRecording)
        {
            return;
        }
        Close();
        await router.ExecuteAsync(row.Info.Id, trigger: "palette");
    }

    /// <summary>
    /// Starts recording new keys for <paramref name="commandId"/> (the pencil,
    /// or F2 on the highlighted row).
    /// </summary>
    public void StartRecording(string commandId)
    {
        var index = IndexOf(commandId);
        if (index < 0)
        {
            return;
        }
        var row = Rows[index];
        CancelRecording();
        SetHighlight(index);
        if (row.IsImmutable)
        {
            row.InlineError = "This shortcut belongs to the Immutable System Tier and cannot change.";
            return;
        }
        row.InlineError = null;
        row.RecordingText = "Press keys…";
        row.IsRecording = true;
        _recording = row;
        _combos.Clear();
        OnPropertyChanged(nameof(IsRecording));
        Diag.Info(Target, "recording keys", new LogField("command", row.Info.Id));
    }

    /// <summary>Stops recording without changing anything (Esc).</summary>
    public void CancelRecording()
    {
        if (_recording is null)
        {
            return;
        }
        _recording.IsRecording = false;
        _recording = null;
        _combos.Clear();
        _recordingVersion++;
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>
    /// Takes one combination while recording. A second one within the window
    /// makes a chord and commits at once; otherwise the recording commits
    /// after the window passes without a key.
    /// </summary>
    public void Record(KeyCombo combo)
    {
        if (_recording is not { } row)
        {
            return;
        }
        _combos.Add(combo);
        row.RecordingText = string.Join(" then ", _combos.Select(c => c.ToDisplay())) + " …";
        var version = ++_recordingVersion;
        if (_combos.Count >= 2)
        {
            _ = CommitAsync(version);
        }
        else
        {
            _ = CommitAfterWindowAsync(version);
        }
    }

    private async Task CommitAfterWindowAsync(int version)
    {
        await Task.Delay(RecordingWindow);
        await CommitAsync(version);
    }

    private async Task CommitAsync(int version)
    {
        if (version != _recordingVersion || _recording is not { } row || _combos.Count == 0)
        {
            return;
        }
        var keys = string.Join(' ', _combos.Take(2));
        row.IsRecording = false;
        _recording = null;
        _combos.Clear();
        _recordingVersion++;
        OnPropertyChanged(nameof(IsRecording));

        var request = new SetKeybindingRequest(row.Info.Id, keys);
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(request);
        }
        catch (IOException error)
        {
            row.InlineError = error.Message;
            return;
        }
        switch (reply)
        {
            case KeymapReply keymap:
                Diag.Request(LogLevel.Info, request.Id, Target, "keybinding changed",
                    new LogField("command", row.Info.Id), new LogField("keys", keys));
                KeymapUpdated?.Invoke(keymap.ToData());
                await RefreshCommandsAsync();
                break;
            case ErrorReply error:
                Diag.Request(LogLevel.Info, request.Id, Target, "keybinding refused",
                    new LogField("command", row.Info.Id), new LogField("keys", keys), new LogField("code", error.Code));
                row.InlineError = error.Message;
                break;
        }
    }

    private async Task RefreshCommandsAsync()
    {
        try
        {
            await router.RefreshAsync();
        }
        catch (IOException error)
        {
            Diag.Info(Target, "cannot read the command list", new LogField("error", error.Message));
            return;
        }
        await RefreshAsync();
    }

    private int IndexOf(string commandId)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Info.Id == commandId)
            {
                return i;
            }
        }
        return -1;
    }
}
