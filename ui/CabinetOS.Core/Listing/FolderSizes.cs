using CabinetOS.Core.Protocol;
using CabinetOS.Core.Presentation;

namespace CabinetOS.Core.Listing;

/// <summary>
/// A folder's measured size: bytes, files and folders under it, the folders
/// that could not be read (not in the total), and whether the count is done.
/// </summary>
public sealed record FolderSize(ulong Bytes, ulong Files, ulong Folders, ulong Unreadable, bool Done);

/// <summary>
/// The measured folder sizes of one pane's folder (<c>measure_paths</c>,
/// version 12): Space on a folder, Calculate Folder Size and Calculate All
/// Folder Sizes. The core counts; this keeps its running totals and results
/// by the folder's name, for the Size column and the status bar. A pane that
/// leaves the folder forgets them (<see cref="Clear"/>), as Total Commander
/// does when it reads a panel again.
/// </summary>
public sealed class FolderSizes
{
    private readonly Dictionary<string, FolderSize> _sizes = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, List<string>> _running = [];

    /// <summary>Whether a measure of this folder still counts.</summary>
    public bool IsMeasuring => _running.Count > 0;

    /// <summary>Whether any folder has a size or is being counted.</summary>
    public bool IsEmpty => _sizes.Count == 0;

    /// <summary>The measures still counting, to cancel (<c>cancel_measure</c>).</summary>
    public IReadOnlyList<ulong> RunningIds => [.. _running.Keys];

    /// <summary>
    /// The paths of <paramref name="paths"/> worth asking about: not being
    /// counted already. Use before <c>measure_paths</c>.
    /// </summary>
    public IReadOnlyList<string> NotCounting(IReadOnlyList<string> paths) =>
        [.. paths.Where(path => !_sizes.TryGetValue(DisplayFormat.FolderName(path), out var size) || size.Done)];

    /// <summary>
    /// The paths of <paramref name="paths"/> that have no size and are not being counted: what
    /// <c>panes.folderSizes</c> asks for when a listing opens or is listed again. A folder counted
    /// already keeps its size until the pane leaves the folder; Space counts it again.
    /// </summary>
    public IReadOnlyList<string> NotMeasured(IReadOnlyList<string> paths) =>
        [.. paths.Where(path => !_sizes.ContainsKey(DisplayFormat.FolderName(path)))];

    /// <summary>
    /// A measure started (<c>measure_started</c>): its paths show as being
    /// counted. Returns the paths it took, those not counted already.
    /// </summary>
    public IReadOnlyList<string> Start(ulong measureId, IReadOnlyList<string> paths)
    {
        var taken = NotCounting(paths);
        if (taken.Count == 0)
        {
            return taken;
        }
        _running[measureId] = [.. taken];
        foreach (var path in taken)
        {
            _sizes[DisplayFormat.FolderName(path)] = new FolderSize(0, 0, 0, 0, Done: false);
        }
        return taken;
    }

    /// <summary>A running total (<c>measure_progress</c>). Returns false when the measure is not this pane's.</summary>
    public bool Apply(MeasureProgressEvent progress)
    {
        if (!_running.ContainsKey(progress.MeasureId))
        {
            return false;
        }
        _sizes[DisplayFormat.FolderName(progress.Path)] = new FolderSize(progress.Bytes, progress.Files, progress.Folders, 0, Done: false);
        return true;
    }

    /// <summary>
    /// The end of a measure (<c>measure_finished</c>): every path counted to
    /// the end has its total; the rest of a cancelled one shows nothing.
    /// Returns false when the measure is not this pane's.
    /// </summary>
    public bool Apply(MeasureFinishedEvent finished)
    {
        if (!_running.Remove(finished.MeasureId, out var paths))
        {
            return false;
        }
        foreach (var path in paths)
        {
            _sizes.Remove(DisplayFormat.FolderName(path));
        }
        foreach (var result in finished.Results)
        {
            _sizes[DisplayFormat.FolderName(result.Path)] = new FolderSize(result.Bytes, result.Files, result.Folders, result.Unreadable, Done: true);
        }
        return true;
    }

    /// <summary>The size of the folder named <paramref name="name"/>, or null when it was not measured.</summary>
    public FolderSize? Get(string name) => _sizes.GetValueOrDefault(name);

    /// <summary>The same, for a name read from the listing without making a string.</summary>
    public FolderSize? Get(ReadOnlySpan<char> name) =>
        _sizes.Count > 0 && _sizes.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var size) ? size : null;

    /// <summary>Forgets every size; returns the measures still counting, to cancel.</summary>
    public IReadOnlyList<ulong> Clear()
    {
        var running = _running.Keys.ToList();
        _running.Clear();
        _sizes.Clear();
        return running;
    }
}
