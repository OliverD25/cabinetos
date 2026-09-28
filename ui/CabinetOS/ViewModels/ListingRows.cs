using System.Collections;
using CabinetOS.Core.Listing;

namespace CabinetOS.ViewModels;

/// <summary>
/// One row of a pane: an index into a listing. Everything the row shows is
/// read from shared memory when the row becomes visible.
/// </summary>
public sealed class RowItem(ListingView view, int index)
{
    /// <summary>The listing the row belongs to.</summary>
    public ListingView View { get; } = view;

    /// <summary>The row's position in the listing (the core's sort order).</summary>
    public int Index { get; } = index;
}

/// <summary>
/// The rows of a listing for <c>ItemsRepeater</c>: a read-only list that
/// makes a <see cref="RowItem"/> only when the repeater asks for an index, so
/// a folder of 100,000 entries never has 100,000 objects. It implements the
/// non-generic <see cref="IList"/> because that is what WinUI reads by index.
/// </summary>
public sealed class ListingRows(ListingView view) : IReadOnlyList<RowItem>, IList
{
    /// <summary>The listing behind the rows.</summary>
    public ListingView View { get; } = view;

    /// <inheritdoc/>
    public int Count => View.Count;

    /// <inheritdoc/>
    public RowItem this[int index] =>
        (uint)index < (uint)Count ? new RowItem(View, index) : throw new ArgumentOutOfRangeException(nameof(index));

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
    public IEnumerator<RowItem> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return new RowItem(View, i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.IndexOf(object? value) => value is RowItem row && row.View == View ? row.Index : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(new RowItem(View, i), index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
