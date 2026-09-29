using System.Globalization;
using CabinetOS.Core.Listing;

namespace CabinetOS.Core.Presentation;

/// <summary>
/// How values look on screen. Presentation only: the numbers come from the
/// core, and each is formatted when its row becomes visible.
/// </summary>
public static class DisplayFormat
{
    /// <summary>A file size as the design writes it: <c>18 KB</c>, <c>1.4 MB</c>; empty for folders.</summary>
    public static string Size(ulong bytes, bool isFolder, CultureInfo? culture = null)
    {
        if (isFolder)
        {
            return "";
        }
        culture ??= CultureInfo.CurrentCulture;
        return bytes switch
        {
            < 1024 => string.Create(culture, $"{bytes} B"),
            // Explorer rounds kilobytes up, so a 1-byte-over file never shows as 0 KB.
            < 1024 * 1024 => string.Create(culture, $"{(bytes + 1023) / 1024:N0} KB"),
            _ => Scaled(bytes, culture),
        };
    }

    /// <summary>Space in the design's short form: <c>118 GB</c>, <c>1.4 TB</c>.</summary>
    public static string Bytes(ulong bytes, CultureInfo? culture = null) =>
        bytes < 1024 * 1024 ? Size(bytes, false, culture) : Scaled(bytes, culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// A time as the design writes it, in local time: <c>Today 09:11</c>,
    /// <c>Yesterday 16:20</c>, <c>Mon 13:40</c> within a week, else the date.
    /// </summary>
    public static string Modified(DateTime utc, DateTime nowLocal, CultureInfo? culture = null)
    {
        if (utc == DateTime.MinValue)
        {
            return "";
        }
        culture ??= CultureInfo.CurrentCulture;
        var local = utc.ToLocalTime();
        var time = local.ToString("t", culture);
        var days = (nowLocal.Date - local.Date).Days;
        return days switch
        {
            0 => $"Today {time}",
            1 => $"Yesterday {time}",
            > 1 and < 7 => $"{local.ToString("ddd", culture)} {time}",
            _ => local.ToString("d", culture) + " " + time,
        };
    }

    /// <summary>
    /// The Modified column in the Commander look (chrome <c>hairlines</c>):
    /// as <see cref="Modified"/>, but a date older than a week has no time,
    /// as the Commander Compact handout shows it (its <c>2 Sep 2026</c>, in
    /// the culture's short date here), so it fits the look's narrow column.
    /// </summary>
    public static string ModifiedShort(DateTime utc, DateTime nowLocal, CultureInfo? culture = null)
    {
        if (utc == DateTime.MinValue)
        {
            return "";
        }
        culture ??= CultureInfo.CurrentCulture;
        var local = utc.ToLocalTime();
        return (nowLocal.Date - local.Date).Days >= 7 ? local.ToString("d", culture) : Modified(utc, nowLocal, culture);
    }

    /// <summary>
    /// The Type column in the Commander look (chrome <c>hairlines</c>):
    /// <c>Folder</c>, a link's kind in a word, or the extension in capitals as
    /// Total Commander's own column shows it (<c>MD</c>, <c>EXE</c>; <c>File</c>
    /// without one). The shell's names (<c>Markdown Source File</c>) are cut
    /// short in the look's narrow column; the handout's own types are this short.
    /// </summary>
    public static string ShortType(ReadOnlySpan<char> name, EntryKind kind, bool isFolder, LinkKind link)
    {
        if (link != LinkKind.None || kind == EntryKind.Link)
        {
            return link switch
            {
                LinkKind.Junction => "Junction",
                LinkKind.MountPoint => "Mount point",
                LinkKind.SymbolicLink => "Symlink",
                _ => "Link",
            };
        }
        if (isFolder)
        {
            return "Folder";
        }
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "File" : name[(dot + 1)..].ToString().ToUpperInvariant();
    }

    /// <summary>
    /// The built-in Type column, until the core's type names come (or from a
    /// core before protocol 9): <c>Folder</c>, a link's words, or the
    /// extension in capitals with <c>File</c>.
    /// </summary>
    public static string TypeText(ReadOnlySpan<char> name, EntryKind kind, bool isFolder)
    {
        if (kind == EntryKind.Link)
        {
            return LinkTypeName(LinkKind.Unknown, isFolder);
        }
        if (isFolder)
        {
            return "Folder";
        }
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return "File";
        }
        return string.Concat(name[(dot + 1)..].ToString().ToUpperInvariant(), " File");
    }

    /// <summary>
    /// The Type column of a row (and Properties' Type): a link says what kind
    /// of link it is, since the shell's name is its target's ("File folder"
    /// for a junction); any other row has the shell's name, else the
    /// built-in one.
    /// </summary>
    public static string RowType(ReadOnlySpan<char> name, EntryKind kind, bool isFolder, LinkKind link, Protocol.EntryDetail? detail)
    {
        if (link != LinkKind.None)
        {
            return LinkTypeName(link, isFolder);
        }
        return detail?.TypeName is { Length: > 0 } shellName ? shellName : TypeText(name, kind, isFolder);
    }

    /// <summary>A link's type: <c>Junction</c>, <c>Mount point</c>, <c>Symbolic link to a folder</c>, <c>Link to a file</c>, ….</summary>
    public static string LinkTypeName(LinkKind link, bool isFolder) => link switch
    {
        LinkKind.Junction => "Junction",
        LinkKind.MountPoint => "Mount point",
        LinkKind.SymbolicLink => isFolder ? "Symbolic link to a folder" : "Symbolic link to a file",
        _ => isFolder ? "Link to a folder" : "Link to a file",
    };

    /// <summary>
    /// The icon key a row asks <c>get_icon</c> for: the core's, except that a
    /// row not on this disk never asks for its file's own icon (a <c>path:</c>
    /// key, which the core reads from the file, and reading a placeholder
    /// downloads it). It gets its extension's icon instead, which the core
    /// draws for any <c>ext:</c> key without opening a file.
    /// </summary>
    public static string IconKeyFor(Protocol.EntryDetail detail, ReadOnlySpan<char> name, bool isFolder, uint attributes)
    {
        if (isFolder || !EntryFacts.IsNotOnDisk(attributes) || !detail.IconKey.StartsWith("path:", StringComparison.Ordinal))
        {
            return detail.IconKey;
        }
        // The core's own rule for an extension: from the last dot on, lower case (.gitignore has one).
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? "generic" : "ext:" + name[dot..].ToString().ToLowerInvariant();
    }

    /// <summary>
    /// How much of a name the rename box selects at first, in UTF-16 units: a
    /// file's name without its extension (after the last dot, not a leading
    /// one), as in Explorer; a folder's whole name.
    /// </summary>
    public static int RenameStem(string name, bool isFolder)
    {
        var dot = isFolder ? -1 : name.LastIndexOf('.');
        return dot > 0 ? dot : name.Length;
    }

    /// <summary>The lower-case extension without the dot, or empty.</summary>
    public static string Extension(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..].ToString().ToLowerInvariant();
    }

    /// <summary>A drive row's name: <c>Data (D:)</c>, or <c>Local Disk (C:)</c> without a label.</summary>
    public static string DriveName(string? letter, string label) =>
        $"{(string.IsNullOrWhiteSpace(label) ? "Local Disk" : label)} ({letter}:)";

    /// <summary>The last part of a path, for pane titles: <c>C:\Users\me</c> gives <c>me</c>, <c>C:\</c> gives <c>C:</c>.</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// The crumbs of a path: each part and the path up to it, the drive root
    /// with its backslash (<c>C:\</c>), as the address bar shows them.
    /// </summary>
    public static IReadOnlyList<(string Label, string Path)> Crumbs(string path)
    {
        var crumbs = new List<(string, string)>();
        // \\?\C:\… and \\?\UNC\server\share\… lift the 260-character limit; they name the same folders.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..];
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                crumbs.Add((i == 0 ? @"\\" + parts[0] : parts[i], @"\\" + string.Join('\\', parts[..(i + 1)])));
            }
            return crumbs;
        }
        var pieces = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < pieces.Length; i++)
        {
            var upTo = string.Join('\\', pieces[..(i + 1)]);
            crumbs.Add((pieces[i], i == 0 ? upTo + '\\' : upTo));
        }
        return crumbs;
    }

    /// <summary>
    /// A path for a line too narrow for it: the drive or share, an ellipsis,
    /// and as many of its last names as fit in <paramref name="maxChars"/>,
    /// each whole (<c>C:\…\names\Звіт 2026.txt</c>). The last name is kept
    /// even when it alone is longer; the view cuts its end. A path that fits
    /// is shown as it is.
    /// </summary>
    public static string ShortPath(string path, int maxChars)
    {
        if (path.Length <= maxChars)
        {
            return path;
        }
        var crumbs = Crumbs(path);
        var rootCount = path.StartsWith(@"\\", StringComparison.Ordinal) && crumbs.Count > 1 ? 2 : 1;
        if (crumbs.Count <= rootCount)
        {
            return path;
        }
        var root = crumbs[rootCount - 1].Path.TrimEnd('\\');
        var tail = crumbs[^1].Label;
        for (var i = crumbs.Count - 2; i >= rootCount; i--)
        {
            var longer = crumbs[i].Label + '\\' + tail;
            if (root.Length + 3 + longer.Length > maxChars)
            {
                break;
            }
            tail = longer;
        }
        var shown = $@"{root}\…\{tail}";
        return shown.Length < path.Length ? shown : path;
    }

    /// <summary>
    /// The root of a path (<c>go.root</c>, Ctrl+\): its drive, <c>C:\</c>, or
    /// its share, <c>\\server\share\</c>; empty for an empty path.
    /// </summary>
    public static string Root(string path)
    {
        var crumbs = Crumbs(path);
        if (crumbs.Count == 0)
        {
            return "";
        }
        if (!crumbs[0].Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return crumbs[0].Path;
        }
        return crumbs.Count > 1 ? crumbs[1].Path + '\\' : crumbs[0].Path;
    }

    /// <summary>The parent folder of a path, or null at a drive or share root.</summary>
    public static string? Parent(string path)
    {
        var crumbs = Crumbs(path);
        if (crumbs.Count < 2 || (path.StartsWith(@"\\", StringComparison.Ordinal) && crumbs.Count < 3))
        {
            return null;
        }
        return crumbs[^2].Path;
    }

    /// <summary>
    /// A search hit's folder as the results show it: from the searched
    /// folder's own name down (<c>docs\log</c> for a search of <c>C:\repo\docs</c>),
    /// so the part that tells hits apart is not cut off; the whole path when
    /// the search had no folder, was a drive, or the hit lies outside it.
    /// </summary>
    public static string FolderUnder(string folder, string? searched)
    {
        if (searched is null || folder.Length == 0 || Parent(searched) is not { } above)
        {
            return folder;
        }
        var root = searched.TrimEnd('\\');
        var inside = string.Equals(folder.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);
        return inside ? folder[(above.TrimEnd('\\').Length + 1)..] : folder;
    }

    /// <summary>A child path: <paramref name="folder"/> joined with <paramref name="name"/>.</summary>
    public static string Join(string folder, string name) =>
        folder.EndsWith('\\') ? folder + name : folder + '\\' + name;

    /// <summary>
    /// The <c>FILE_ATTRIBUTE_*</c> bits a user cares about, in words, for the
    /// Properties dialog: "Read-only, Hidden, Archive"; empty when none is set.
    /// </summary>
    public static string AttributeNames(uint attributes)
    {
        (uint Bit, string Name)[] known =
        [
            (0x1, "Read-only"),
            (0x2, "Hidden"),
            (0x4, "System"),
            (0x20, "Archive"),
            (0x100, "Temporary"),
            (0x200, "Sparse"),
            (0x400, "Link (reparse point)"),
            (0x800, "Compressed"),
            (0x1000, "Offline"),
            (0x2000, "Not indexed"),
            (0x4000, "Encrypted"),
            (0x80000, "Always kept on this device"),
            (0x400000, "Online only"),
        ];
        return string.Join(", ", known.Where(k => (attributes & k.Bit) != 0).Select(k => k.Name));
    }

    /// <summary>A size for the Properties dialog: "1.4 MB (1,468,006 bytes)".</summary>
    public static string SizeWithBytes(ulong bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var exact = bytes == 1 ? "1 byte" : string.Create(culture, $"{bytes:N0} bytes");
        return bytes < 1024 ? exact : $"{Bytes(bytes, culture)} ({exact})";
    }

    private static string Scaled(ulong bytes, CultureInfo culture)
    {
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(culture, $"{(value >= 100 ? Math.Round(value) : Math.Round(value, 1)):0.#} {units[unit]}");
    }
}
