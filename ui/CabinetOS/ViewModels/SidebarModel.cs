using System.Collections.ObjectModel;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CabinetOS.ViewModels;

/// <summary>A pinned folder of the sidebar.</summary>
public sealed class PinnedItem(string name, string path, bool isUserPinned = false) : ObservableObject
{
    private bool _isActive;

    /// <summary>What the row says.</summary>
    public string Name { get; } = name;

    /// <summary>Where it goes.</summary>
    public string Path { get; } = path;

    /// <summary>Whether the user pinned it (<c>ui.pinned</c>), so it can be unpinned; the known folders cannot.</summary>
    public bool IsUserPinned { get; } = isUserPinned;

    /// <summary>Whether the active pane shows this folder (the design's pill).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>A drive of the sidebar, from <c>list_volumes</c>.</summary>
public sealed record DriveItem(string Name, string Path, string FreeText, double UsedFraction)
{
    /// <summary>The used part of the 3 px usage bar.</summary>
    public Microsoft.UI.Xaml.GridLength UsedLength => new(UsedFraction, Microsoft.UI.Xaml.GridUnitType.Star);

    /// <summary>The free part of the usage bar.</summary>
    public Microsoft.UI.Xaml.GridLength FreeLength => new(1 - UsedFraction, Microsoft.UI.Xaml.GridUnitType.Star);
}

/// <summary>The sidebar: pinned folders and drives (Workspaces and Tags come with their features).</summary>
public sealed class SidebarModel : ObservableObject
{
    private bool _showDrives;
    private bool _shortFreeLabels;
    private IReadOnlyList<VolumeDetails>? _volumes;

    /// <summary>
    /// Whether a drive's free space reads "118 GB" rather than "118 GB free":
    /// the Commander Compact handout's shorter label, part of chrome <c>hairlines</c>.
    /// </summary>
    public bool ShortFreeLabels
    {
        get => _shortFreeLabels;
        set
        {
            if (SetProperty(ref _shortFreeLabels, value) && _volumes is not null)
            {
                SetDrives(_volumes);
            }
        }
    }

    /// <summary>
    /// Desktop, Downloads, Documents and the profile folder, then the folders
    /// the user pinned (<c>ui.pinned</c>). Only paths are read, no folder.
    /// </summary>
    public ObservableCollection<PinnedItem> Pinned { get; } = [];

    /// <summary>The drives, once the core can list them.</summary>
    public ObservableCollection<DriveItem> Drives { get; } = [];

    /// <summary>Whether the Drives section is shown: the core must answer <c>list_volumes</c>.</summary>
    public bool ShowDrives
    {
        get => _showDrives;
        private set => SetProperty(ref _showDrives, value);
    }

    /// <summary>Fills the pinned folders: the user's known folders, then <paramref name="userPinned"/>.</summary>
    public void SetPinned(string desktop, string downloads, string documents, string profile, IReadOnlyList<string> userPinned)
    {
        Pinned.Clear();
        foreach (var (name, path) in new[] { ("Desktop", desktop), ("Downloads", downloads), ("Documents", documents), (DisplayFormat.FolderName(profile), profile) })
        {
            if (!string.IsNullOrEmpty(path))
            {
                Pinned.Add(new PinnedItem(name, path));
            }
        }
        foreach (var path in userPinned)
        {
            if (!IsPinned(path))
            {
                Pinned.Add(new PinnedItem(DisplayFormat.FolderName(path), path, isUserPinned: true));
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> already has a row.</summary>
    public bool IsPinned(string path) =>
        Pinned.Any(item => string.Equals(item.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder the active pane shows.</summary>
    public string ActivePath { get; private set; } = "";

    /// <summary>The active pane shows another folder (or another pane became the active one): the Explorer's tree follows it.</summary>
    public event Action<string>? ActivePathChanged;

    /// <summary>The drives changed: the Explorer's tree starts from them.</summary>
    public event Action? DrivesChanged;

    /// <summary>Marks the pinned folder the active pane shows.</summary>
    public void SetActivePath(string path)
    {
        foreach (var item in Pinned)
        {
            item.IsActive = string.Equals(item.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        if (!string.Equals(ActivePath, path, StringComparison.OrdinalIgnoreCase))
        {
            ActivePath = path;
            ActivePathChanged?.Invoke(path);
        }
    }

    /// <summary>Shows the drives of a <c>volumes</c> reply; null hides the section.</summary>
    public void SetDrives(IReadOnlyList<VolumeDetails>? volumes)
    {
        _volumes = volumes;
        Drives.Clear();
        if (volumes is null)
        {
            ShowDrives = false;
            DrivesChanged?.Invoke();
            return;
        }
        // In the core's order: the UI does not sort (brief §1).
        foreach (var volume in volumes.Where(v => !string.IsNullOrEmpty(v.DriveLetter)))
        {
            var used = volume.TotalBytes == 0 ? 0 : 1 - ((double)volume.FreeBytes / volume.TotalBytes);
            Drives.Add(new DriveItem(
                DisplayFormat.DriveName(volume.DriveLetter, volume.Label),
                $"{volume.DriveLetter}:\\",
                ShortFreeLabels ? DisplayFormat.Bytes(volume.FreeBytes) : $"{DisplayFormat.Bytes(volume.FreeBytes)} free",
                Math.Clamp(used, 0, 1)));
        }
        ShowDrives = Drives.Count > 0;
        DrivesChanged?.Invoke();
    }
}
