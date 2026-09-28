using System.Collections.ObjectModel;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CabinetOS.ViewModels;

/// <summary>A pinned folder of the sidebar.</summary>
public sealed class PinnedItem(string name, string path) : ObservableObject
{
    private bool _isActive;

    /// <summary>What the row says.</summary>
    public string Name { get; } = name;

    /// <summary>Where it goes.</summary>
    public string Path { get; } = path;

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

    /// <summary>Desktop, Downloads, Documents and the profile folder. Only paths are read, no folder.</summary>
    public ObservableCollection<PinnedItem> Pinned { get; } = [];

    /// <summary>The drives, once the core can list them.</summary>
    public ObservableCollection<DriveItem> Drives { get; } = [];

    /// <summary>Whether the Drives section is shown: the core must answer <c>list_volumes</c>.</summary>
    public bool ShowDrives
    {
        get => _showDrives;
        private set => SetProperty(ref _showDrives, value);
    }

    /// <summary>Fills the pinned folders from the user's known folders.</summary>
    public void SetPinned(string desktop, string downloads, string documents, string profile)
    {
        Pinned.Clear();
        foreach (var (name, path) in new[] { ("Desktop", desktop), ("Downloads", downloads), ("Documents", documents), (DisplayFormat.FolderName(profile), profile) })
        {
            if (!string.IsNullOrEmpty(path))
            {
                Pinned.Add(new PinnedItem(name, path));
            }
        }
    }

    /// <summary>Marks the pinned folder the active pane shows.</summary>
    public void SetActivePath(string path)
    {
        foreach (var item in Pinned)
        {
            item.IsActive = string.Equals(item.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Shows the drives of a <c>volumes</c> reply; null hides the section.</summary>
    public void SetDrives(IReadOnlyList<VolumeDetails>? volumes)
    {
        Drives.Clear();
        if (volumes is null)
        {
            ShowDrives = false;
            return;
        }
        // In the core's order: the UI does not sort (brief §1).
        foreach (var volume in volumes.Where(v => !string.IsNullOrEmpty(v.DriveLetter)))
        {
            var used = volume.TotalBytes == 0 ? 0 : 1 - ((double)volume.FreeBytes / volume.TotalBytes);
            Drives.Add(new DriveItem(
                DisplayFormat.DriveName(volume.DriveLetter, volume.Label),
                $"{volume.DriveLetter}:\\",
                $"{DisplayFormat.Bytes(volume.FreeBytes)} free",
                Math.Clamp(used, 0, 1)));
        }
        ShowDrives = Drives.Count > 0;
    }
}
