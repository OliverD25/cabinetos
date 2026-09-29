using System.Collections;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using Microsoft.UI.Xaml.Media;

namespace CabinetOS.ViewModels;

/// <summary>What a row shows that the listing does not hold: the shell's type name and icon.</summary>
public interface IRowDetails
{
    /// <summary>Entry <paramref name="index"/>'s detail: its own, else what its extension had, else null.</summary>
    EntryDetail? Detail(int index, ReadOnlySpan<char> name, bool isFolder);

    /// <summary>The icon for <paramref name="key"/>, or null while it is on its way.</summary>
    ImageSource? Icon(string key);

    /// <summary>The measured size of the folder named <paramref name="name"/> (Space, Shift+Alt+Enter), or null.</summary>
    FolderSize? MeasuredSize(ReadOnlySpan<char> name) => null;
}

/// <summary>
/// One row of a pane: an index into a listing. Everything the row shows is
/// read from shared memory when the row becomes visible; the type name and
/// the icon come from the core a page at a time (<see cref="Details"/>).
/// </summary>
public sealed class RowItem(ListingView view, int index, IRowDetails? details = null)
{
    /// <summary>The listing the row belongs to.</summary>
    public ListingView View { get; } = view;

    /// <summary>The row's position in the listing (the core's sort order).</summary>
    public int Index { get; } = index;

    /// <summary>Where the row's type name and icon come from.</summary>
    public IRowDetails? Details { get; } = details;
}

/// <summary>
/// The rows of a listing for <c>ItemsRepeater</c>: a read-only list that
/// makes a <see cref="RowItem"/> only when the repeater asks for an index, so
/// a folder of 100,000 entries never has 100,000 objects. It implements the
/// non-generic <see cref="IList"/> because that is what WinUI reads by index.
/// </summary>
public sealed class ListingRows(ListingView view, IRowDetails? details = null) : IReadOnlyList<RowItem>, IList
{
    /// <summary>The listing behind the rows.</summary>
    public ListingView View { get; } = view;

    /// <inheritdoc/>
    public int Count => View.Count;

    /// <inheritdoc/>
    public RowItem this[int index] =>
        (uint)index < (uint)Count ? new RowItem(View, index, details) : throw new ArgumentOutOfRangeException(nameof(index));

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
            yield return new RowItem(View, i, details);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.IndexOf(object? value) => value is RowItem row && row.View == View ? row.Index : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(new RowItem(View, i, details), index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
