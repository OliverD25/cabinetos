using System.Globalization;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Jobs;

/// <summary>
/// What the conflict card says and offers (docs/jobs.md, "Conflicts"). The
/// card offers only the decisions that fit the kind, so the core's
/// <c>invalid_resolution</c> stays a safety net rather than a message.
/// </summary>
public static class ConflictText
{
    /// <summary>The file or folder the conflict is about, by name.</summary>
    public static string Name(JobConflictEvent conflict) => DisplayFormat.FolderName(conflict.Source);

    /// <summary>"already exists", "access denied", "the Recycle Bin cannot take it (21 GB)", …</summary>
    public static string KindText(JobConflictEvent conflict, CultureInfo? culture = null) => conflict.Kind.Type switch
    {
        ConflictKind.FileExists => "already exists",
        ConflictKind.AccessDenied => "access denied",
        ConflictKind.SharingViolation => "is open in another program",
        ConflictKind.PathTooLong when IsRecycleBinDelete(conflict) => "has a path too long for the Recycle Bin",
        ConflictKind.PathTooLong => "the path is too long for the destination",
        ConflictKind.DiskFull => "the destination disk is full; the job is paused",
        ConflictKind.SourceVanished => "is gone",
        ConflictKind.RecycleBinTooSmall => $"the Recycle Bin cannot take it ({DisplayFormat.Bytes(conflict.Kind.Size ?? 0, culture)})",
        ConflictKind.Io => $"failed: {conflict.Kind.Message} (error {conflict.Kind.Code})",
        var other => other.Replace('_', ' '),
    };

    /// <summary>
    /// For <c>file_exists</c>: the new file and the one already there, each as
    /// "size · time"; null for the other kinds.
    /// </summary>
    public static (string New, string Existing)? Sides(JobConflictEvent conflict, DateTime nowLocal, CultureInfo? culture = null)
    {
        var kind = conflict.Kind;
        if (kind.Type != ConflictKind.FileExists)
        {
            return null;
        }
        return (Side(kind.SourceSize, kind.SourceModified, nowLocal, culture), Side(kind.DestSize, kind.DestModified, nowLocal, culture));
    }

    /// <summary>
    /// The decisions the card offers for a conflict, in button order. A path
    /// too long for the Recycle Bin is answered like a bin too small: the core
    /// stops such a delete before the shell's own delete could make it final
    /// without asking (docs/jobs.md, "The Recycle Bin and long paths").
    /// </summary>
    public static IReadOnlyList<string> Options(JobConflictEvent conflict) =>
        conflict.Kind.Type == ConflictKind.PathTooLong && IsRecycleBinDelete(conflict)
            ? [Resolution.DeletePermanentlyType, Resolution.SkipType, Resolution.RetryType]
            : Options(conflict.Kind);

    // A delete has no destination; a copy or a move always has one.
    private static bool IsRecycleBinDelete(JobConflictEvent conflict) => conflict.Destination is null;

    /// <summary>The decisions the card offers for a kind, in button order.</summary>
    public static IReadOnlyList<string> Options(ConflictKind kind) => kind.Type switch
    {
        ConflictKind.FileExists => [Resolution.OverwriteType, Resolution.SkipType, Resolution.RenameType, Resolution.RetryType, Resolution.CancelJobType],
        // Overwrite clears the read-only attribute first (docs/jobs.md).
        ConflictKind.AccessDenied => [Resolution.OverwriteType, Resolution.SkipType, Resolution.RetryType, Resolution.CancelJobType],
        // Only these two answer it; the job's own Cancel button stays in the footer.
        ConflictKind.RecycleBinTooSmall => [Resolution.DeletePermanentlyType, Resolution.SkipType],
        _ => [Resolution.RetryType, Resolution.SkipType, Resolution.CancelJobType],
    };

    /// <summary>A decision's button text.</summary>
    public static string Label(string resolutionType) => resolutionType switch
    {
        Resolution.OverwriteType => "Overwrite",
        Resolution.SkipType => "Skip",
        Resolution.RenameType => "Rename",
        Resolution.RetryType => "Retry",
        Resolution.DeletePermanentlyType => "Delete permanently",
        Resolution.CancelJobType => "Cancel job",
        var other => other,
    };

    private static string Side(ulong? size, long? modified, DateTime nowLocal, CultureInfo? culture)
    {
        var sizeText = DisplayFormat.Size(size ?? 0, isFolder: false, culture);
        var time = modified is long ticks && ticks > 0 && ticks <= DateTime.MaxValue.ToFileTimeUtc()
            ? DisplayFormat.Modified(DateTime.FromFileTimeUtc(ticks), nowLocal, culture)
            : "";
        return time.Length == 0 ? sizeText : $"{sizeText} · {time}";
    }
}
