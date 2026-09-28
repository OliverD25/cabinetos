using System.Collections;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.ViewModels;

/// <summary>
/// One search hit as a row: the same row template as a listing, with the
/// hit's folder where a listing shows the time. The core sends paths only, so
/// the type comes from what the core said about the extension before, and
/// there is no size or time.
/// </summary>
public sealed class SearchRowItem(FileHit hit, int index, IRowDetails? details)
{
    /// <summary>The hit.</summary>
    public FileHit Hit { get; } = hit;

    /// <summary>Its place in the core's order.</summary>
    public int Index { get; } = index;

    /// <summary>Where known type names and icons come from.</summary>
    public IRowDetails? Details { get; } = details;

    /// <summary>The name: the path's last part.</summary>
    public string Name => DisplayFormat.FolderName(Hit.Path);

    /// <summary>The folder the hit is in, or "" for a drive's root.</summary>
    public string Folder => DisplayFormat.Parent(Hit.Path) ?? "";
}

/// <summary>The hits for <c>ItemsRepeater</c>, in the core's order; the UI ranks nothing.</summary>
public sealed class SearchRows(IReadOnlyList<FileHit> hits, IRowDetails? details) : IReadOnlyList<SearchRowItem>, IList
{
    /// <summary>The hits.</summary>
    public IReadOnlyList<FileHit> Hits { get; } = hits;

    /// <inheritdoc/>
    public int Count => Hits.Count;

    /// <inheritdoc/>
    public SearchRowItem this[int index] => new(Hits[index], index, details);

    bool IList.IsFixedSize => true;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public IEnumerator<SearchRowItem> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.IndexOf(object? value) => value is SearchRowItem row && ReferenceEquals(row.Hit, Hits.ElementAtOrDefault(row.Index)) ? row.Index : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
