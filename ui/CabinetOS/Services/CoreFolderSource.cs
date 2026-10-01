using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Ipc;
using CabinetOS.Core.Listing;
using CabinetOS.Core.Protocol;
using CabinetOS.Core.Sidebar;

namespace CabinetOS.Services;

/// <summary>
/// Where the Explorer's folder tree gets a folder's sub-folders: from the
/// core, with <c>list_directory</c>, one request per call, as a pane lists a
/// folder. The listing lives in shared memory; the folder names are read from
/// it off the UI thread and the listing is closed at once. A folder that
/// the core refuses (no access, gone) is an answer with a reason, not an
/// exception, so the tree can say why the row stays closed. The hidden
/// entries are left out as <c>panes.showHidden</c> says, except for the
/// one read that asks for them (<see cref="ListIncludingHiddenAsync"/>).
/// </summary>
internal sealed class CoreFolderSource(ICoreChannel core) : IFolderSource
{
    private const string Target = "cabinetos_ui::tree";

    /// <inheritdoc/>
    public Task<FolderListing> ListAsync(string path, CancellationToken cancellationToken) => ReadAsync(path, null, cancellationToken);

    /// <inheritdoc/>
    public Task<FolderListing> ListIncludingHiddenAsync(string path, CancellationToken cancellationToken) => ReadAsync(path, true, cancellationToken);

    // includeHidden null leaves it to panes.showHidden, as a pane's listing does.
    private async Task<FolderListing> ReadAsync(string path, bool? includeHidden, CancellationToken cancellationToken)
    {
        // The tree's own order, whatever the panes are sorted by.
        var request = new ListDirectoryRequest(path) { Watch = false, Sort = new SortSpec("name", false), IncludeHidden = includeHidden };
        CoreReply reply;
        try
        {
            reply = await core.RequestAsync(request, cancellationToken);
        }
        catch (IOException error)
        {
            return FolderListing.Failed(error.Message);
        }
        switch (reply)
        {
            case ErrorReply refused:
                return FolderListing.Failed(refused.Message);
            case ListingOpenedReply opened when opened.TakeSection() is { } section:
                var listingId = opened.ListingId;
                try
                {
                    var names = await Task.Run(() => FolderNames(section, opened.SectionSize), CancellationToken.None);
                    return new FolderListing(names);
                }
                catch (Exception failure) when (failure is InvalidDataException or IOException)
                {
                    return FolderListing.Failed(failure.Message);
                }
                finally
                {
                    _ = CloseAsync(listingId);
                }
            default:
                return FolderListing.Failed($"unexpected reply {reply.GetType().Name}");
        }
    }

    private static List<string> FolderNames(System.Runtime.InteropServices.SafeHandle section, ulong size)
    {
        using var view = ListingView.Open(section, size);
        var names = new List<string>();
        for (var i = 0; i < view.Count; i++)
        {
            if (view.IsFolder(i))
            {
                names.Add(view.Name(i));
            }
        }
        return names;
    }

    private async Task CloseAsync(ulong listingId)
    {
        try
        {
            await core.RequestAsync(new CloseListingRequest(listingId));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Diag.Debug(Target, "cannot close a tree listing", new LogField("listing_id", listingId), new LogField("error", error.Message));
        }
    }
}
