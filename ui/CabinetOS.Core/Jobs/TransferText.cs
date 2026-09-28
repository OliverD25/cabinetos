using System.Globalization;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Jobs;

/// <summary>
/// The words and numbers of the transfer flyout and the status-bar pill
/// (design view E), made from what the core sent: presentation only.
/// </summary>
public static class TransferText
{
    /// <summary>"Copy" and "Copying", "Move" and "Moving", "Delete" and "Deleting".</summary>
    public static (string Verb, string Gerund) Words(JobKind? kind) => kind?.Type switch
    {
        "move" => ("Move", "Moving"),
        "delete" => ("Delete", "Deleting"),
        _ => ("Copy", "Copying"),
    };

    /// <summary>"Copying 12,480 items", "Copy paused", "Copy complete", "Copy cancelled", …</summary>
    public static string Title(TransferJob job, CultureInfo? culture = null)
    {
        var (verb, gerund) = Words(job.Kind);
        var progress = job.Progress;
        return job.State.Type switch
        {
            JobState.Queued => $"Waiting to {verb.ToLowerInvariant()}",
            JobState.Scanning => $"Preparing to {verb.ToLowerInvariant()}",
            JobState.Paused => $"{verb} paused",
            JobState.Completed => $"{verb} complete",
            JobState.CompletedWithErrors => $"{verb} complete, {Number(progress?.FilesFailed ?? 0, culture)} failed",
            JobState.Cancelled => $"{verb} cancelled",
            JobState.Failed => $"{verb} failed",
            _ => progress is { FilesTotal: > 0 } ? $"{gerund} {Items(progress.FilesTotal, culture)}" : $"{gerund}…",
        };
    }

    /// <summary>"{source} → {destination}"; for a delete, where it goes; for a failed job, why.</summary>
    public static string Subtitle(TransferJob job)
    {
        if (job.State.Type == JobState.Failed && !string.IsNullOrEmpty(job.State.Message))
        {
            return job.State.Message;
        }
        var from = job.Sources.Count switch
        {
            0 => "",
            1 => job.Sources[0],
            // The sources of one command come from one folder: name the folder.
            _ => DisplayFormat.Parent(job.Sources[0]) ?? job.Sources[0],
        };
        if (job.Kind?.Type == "delete")
        {
            return job.Kind.IsPermanentDelete ? $"{from} → deleted for good" : $"{from} → Recycle Bin";
        }
        return job.Destination is null ? from : $"{from} → {job.Destination}";
    }

    /// <summary>How much is done, from 0 to 1: bytes when the job counts bytes, else items.</summary>
    public static double Fraction(TransferJob job)
    {
        if (job.Progress is not { } progress)
        {
            return 0;
        }
        if (progress.BytesTotal > 0)
        {
            return Math.Clamp((double)progress.BytesDone / progress.BytesTotal, 0, 1);
        }
        if (progress.FilesTotal > 0)
        {
            return Math.Clamp((double)progress.FilesDone / progress.FilesTotal, 0, 1);
        }
        // Nothing to count (an empty folder): done means all of it.
        return job.State.Type is JobState.Completed or JobState.CompletedWithErrors ? 1 : 0;
    }

    /// <summary>"45%", rounded down so it reads 100% only when everything is done.</summary>
    public static string PercentText(TransferJob job, CultureInfo? culture = null) =>
        string.Create(culture ?? CultureInfo.CurrentCulture, $"{(int)Math.Floor(Fraction(job) * 100)}%");

    /// <summary>"1.2 GB of 2.7 GB", or "8,412 of 10,001 items" for a job without bytes to count.</summary>
    public static string DoneText(TransferJob job, CultureInfo? culture = null)
    {
        if (job.Progress is not { } progress)
        {
            return "";
        }
        if (progress.BytesTotal > 0)
        {
            return $"{DisplayFormat.Bytes(progress.BytesDone, culture)} of {DisplayFormat.Bytes(progress.BytesTotal, culture)}";
        }
        return progress.FilesTotal > 0 ? $"{Number(progress.FilesDone, culture)} of {Items(progress.FilesTotal, culture)}" : "";
    }

    /// <summary>"3 min 12 s left", "Paused", or nothing while the core has no estimate.</summary>
    public static string EtaText(TransferJob job)
    {
        if (job.State.Type == JobState.Paused)
        {
            return "Paused";
        }
        if (job.State.Type != JobState.Running || job.Progress?.EtaSeconds is not { } seconds)
        {
            return "";
        }
        return seconds switch
        {
            >= 3600 => $"{seconds / 3600} h {seconds % 3600 / 60} min left",
            >= 60 => $"{seconds / 60} min {seconds % 60} s left",
            _ => $"{seconds} s left",
        };
    }

    /// <summary>
    /// The pace over the graph with its unit: "610 MB/s", or "412 items/s" for
    /// a job without bytes to count. Nothing once the job ended, or before a
    /// job that counts items has a pace (its first record has none).
    /// </summary>
    public static string SpeedText(TransferJob job, CultureInfo? culture = null)
    {
        if (job.Progress is not { } progress || job.IsFinal)
        {
            return "";
        }
        var running = job.State.Type == JobState.Running;
        if (!job.CountsItems)
        {
            return $"{DisplayFormat.Bytes(running ? progress.SpeedBps : 0, culture)}/s";
        }
        if (progress.ItemsPerSecond is not { } items)
        {
            return "";
        }
        var pace = running ? items : 0;
        return (pace is > 0 and < 10 ? pace.ToString("0.#", culture ?? CultureInfo.CurrentCulture) : Number((ulong)Math.Round(pace), culture)) + " items/s";
    }

    /// <summary>The graph's lowest top, so a slow job does not fill it: 1 MB/s, or 10 items/s.</summary>
    public static double GraphFloor(TransferJob job) => job.CountsItems ? 10 : 1024.0 * 1024.0;

    /// <summary>The status-bar pill: "Copying · 45%", "Copy paused · 45%", or the title.</summary>
    public static string PillText(TransferJob job, CultureInfo? culture = null)
    {
        var (verb, gerund) = Words(job.Kind);
        return job.State.Type switch
        {
            JobState.Running => $"{gerund} · {PercentText(job, culture)}",
            JobState.Paused => $"{verb} paused · {PercentText(job, culture)}",
            _ => Title(job, culture),
        };
    }

    /// <summary>"Pause" or "Resume"; null when the job has ended.</summary>
    public static string? PauseLabel(TransferJob job) => job.State.Type switch
    {
        JobState.Paused => "Resume",
        _ when job.IsFinal => null,
        _ => "Pause",
    };

    /// <summary>"Cancel" while the job runs, "Close" once it has ended (the design's footer).</summary>
    public static string EndLabel(TransferJob job) => job.IsFinal ? "Close" : "Cancel";

    /// <summary>
    /// The speed graph's line over <paramref name="width"/> × <paramref name="height"/>
    /// pixels, oldest sample on the left. The scale follows the fastest sample,
    /// never below 1 MB/s, so a slow copy does not draw noise as mountains.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Graph(IReadOnlyList<double> speeds, double width, double height, double floor = 1024.0 * 1024.0)
    {
        if (speeds.Count < 2)
        {
            return [];
        }
        var top = Math.Max(speeds.Max() * 1.1, floor);
        var usable = Math.Max(0, height - 6);
        var points = new (double, double)[speeds.Count];
        for (var i = 0; i < speeds.Count; i++)
        {
            points[i] = (i * width / (speeds.Count - 1), height - (speeds[i] / top * usable));
        }
        return points;
    }

    private static string Items(ulong count, CultureInfo? culture) => count == 1 ? "1 item" : $"{Number(count, culture)} items";

    private static string Number(ulong value, CultureInfo? culture) => value.ToString("N0", culture ?? CultureInfo.CurrentCulture);
}
