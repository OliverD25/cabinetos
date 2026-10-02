using System.Globalization;
using System.Text.Json;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Protocol;

namespace CabinetOS.Core.Updates;

/// <summary>
/// The update pill in the status bar (docs/ui.md, "Updates"): the transfer pill's look, shown
/// while a download runs ("Update · 45% · 4.2 MB/s", with the track) and while an update waits
/// for a restart ("Update ready · Restart", which a click runs).
/// </summary>
public sealed record UpdatePill(bool Visible, string Text = "", string ToolTip = "", double Fraction = 0, bool ShowsTrack = false, bool Restarts = false)
{
    /// <summary>No pill.</summary>
    public static UpdatePill Hidden { get; } = new(false);
}

/// <summary>The About dialog's "Update" row: its text, and whether it carries the dot of a waiting update.</summary>
public sealed record UpdateAboutRow(string Text, bool Dot);

/// <summary>
/// The status bar's quiet notice (docs/ui.md, "Updates"; ADR 0015): a version the core swapped in by itself
/// ("CabinetOS 0.2.0 is installed; restart to use it", with Restart now and Later), or a swap that failed and left
/// the running version as it was (with Close). <see cref="Key"/> tells one notice from another, so Later closes
/// that one only.
/// </summary>
public sealed record UpdateNoticeView(string Key, string Text, string ToolTip, bool Failed, string Version);

/// <summary>
/// What the update dialog shows (docs/ui.md, "Updates"): the version, the notes as blocks, and
/// its buttons. <see cref="RestartText"/> is null when nothing is downloaded yet; then the
/// dialog only shows the notes, and closing it snoozes nothing.
/// </summary>
public sealed record UpdateDialogView(
    string Version,
    string Title,
    string Subtitle,
    IReadOnlyList<NoteBlock> Notes,
    string? NotesMissing,
    string? NotesUrl,
    string ChangelogUrl,
    string? RestartText,
    string CloseText,
    bool SnoozesOnClose);

/// <summary>
/// The window's view of the core's updater: the last <c>update_state</c>, the download's
/// progress, which version's dialog already opened by itself in this run, and the status bar's
/// notice with the one the user closed.
/// </summary>
public sealed class UpdateModel
{
    private string? _closedNotice;

    /// <summary>The updater's state as the core sent it last; null before the first answer, or from a core before protocol 14.</summary>
    public UpdateStatus? Status { get; private set; }

    /// <summary>The last <c>update_progress</c> of the download that runs now.</summary>
    public UpdateProgressEvent? Progress { get; private set; }

    /// <summary>The version whose dialog opened by itself in this run; it does not open by itself again.</summary>
    public string? DialogShownFor { get; private set; }

    /// <summary>
    /// <c>update.autoInstall</c> (<see cref="UpdateText.AutoInstallFrom"/>): the core swaps a download in by itself,
    /// so the dialog never opens by itself. True, the default, until the configuration is read.
    /// </summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>The status bar's notice now, or null (<see cref="UpdateText.Notice"/>).</summary>
    public UpdateNoticeView? Notice { get; private set; }

    /// <summary>Takes a new state; the progress is kept only while a download runs, and the notice follows.</summary>
    public void Apply(UpdateStatus status)
    {
        var before = Status?.State;
        Status = status;
        if (status.State != UpdatePhases.Downloading)
        {
            Progress = null;
        }
        Notice = UpdateText.Notice(before, status, Notice) is { } notice && notice.Key != _closedNotice ? notice : null;
    }

    /// <summary>Takes a download's progress.</summary>
    public void Apply(UpdateProgressEvent progress) => Progress = progress;

    /// <summary>The core went away: what it said is no longer known.</summary>
    public void Forget()
    {
        Status = null;
        Progress = null;
        Notice = null;
    }

    /// <summary>Later (or Close): the notice goes until another one is due; the pill and the dot stay.</summary>
    public void CloseNotice()
    {
        _closedNotice = Notice?.Key ?? _closedNotice;
        Notice = null;
    }

    /// <summary>Remembers that the dialog of <paramref name="version"/> was shown.</summary>
    public void MarkDialogShown(string version) => DialogShownFor = version;

    /// <summary>The version that waits for a restart, or null (<see cref="UpdateText.WaitingVersion"/>).</summary>
    public string? WaitingVersion => UpdateText.WaitingVersion(Status);

    /// <summary>Whether the dialog opens by itself now (<see cref="UpdateText.OpensDialog"/>): never with <see cref="AutoInstall"/>.</summary>
    public bool OpensDialog(DateTimeOffset now) => !AutoInstall && UpdateText.OpensDialog(Status, now, DialogShownFor);

    /// <summary>The status-bar pill now; none while the notice of an installed version holds Restart now.</summary>
    public UpdatePill Pill(CultureInfo? culture = null) =>
        Notice is { Failed: false } ? UpdatePill.Hidden : UpdateText.Pill(Status, Progress, culture);
}

/// <summary>The update's texts and rules (docs/ui.md, "Updates"), apart from the window so they can be tested.</summary>
public static class UpdateText
{
    /// <summary>
    /// The version that waits for a restart: the downloaded one, or the one a swap already put
    /// in place (<c>ready</c>, also after a swap by <c>cabinetos-cli update apply</c> or another
    /// window). The dot on the menu button and in About shows while there is one.
    /// </summary>
    public static string? WaitingVersion(UpdateStatus? status) => status?.State switch
    {
        UpdatePhases.Downloaded => status.Latest?.Version,
        UpdatePhases.Ready => status.Installed ?? status.Latest?.Version,
        _ => null,
    };

    /// <summary><c>update.autoInstall</c> of the core's configuration; true when it is missing or not a boolean.</summary>
    public static bool AutoInstallFrom(JsonElement config) =>
        !(config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Object
            && update.TryGetProperty("autoInstall", out var value) && value.ValueKind == JsonValueKind.False);

    /// <summary>
    /// The status bar's notice after <paramref name="status"/>, which came after a state of
    /// <paramref name="before"/> while <paramref name="shown"/> was up: a version in place that waits for a restart
    /// (<c>ready</c>: swapped in by itself, by <c>cabinetos-cli update apply</c> or by another window); a swap that
    /// failed (<c>failed</c> right after <c>applying</c>), which stays while the state stays <c>failed</c>; else none.
    /// </summary>
    public static UpdateNoticeView? Notice(string? before, UpdateStatus status, UpdateNoticeView? shown)
    {
        switch (status.State)
        {
            case UpdatePhases.Ready when WaitingVersion(status) is { } version && version != status.Current:
                return new UpdateNoticeView(
                    $"installed {version}",
                    $"CabinetOS {version} is installed; restart to use it",
                    $"CabinetOS {status.Current} runs until the restart. Click for the release notes.",
                    false,
                    version);
            case UpdatePhases.Failed when before == UpdatePhases.Applying:
                var failed = status.Latest?.Version ?? "the update";
                var reason = status.Message ?? "no reason given";
                return new UpdateNoticeView(
                    $"failed {failed} {reason}",
                    $"CabinetOS {failed} could not be installed; {status.Current} keeps running",
                    reason,
                    true,
                    failed);
            case UpdatePhases.Failed when shown is { Failed: true }:
                return shown;
            default:
                return null;
        }
    }

    /// <summary>
    /// The snooze rule: the dialog opens by itself when a version is downloaded, unless Later
    /// was chosen less than a day ago (<c>snoozed_until_ms</c> is still ahead) or its dialog
    /// already opened by itself in this run. The update commands open it at any time.
    /// With <c>update.autoInstall</c> it never opens by itself (<see cref="UpdateModel.OpensDialog"/>).
    /// </summary>
    public static bool OpensDialog(UpdateStatus? status, DateTimeOffset now, string? shownFor) =>
        status is { State: UpdatePhases.Downloaded, Latest: { } latest }
        && latest.Version != shownFor
        && !(status.SnoozedUntilMs is { } until && until > (ulong)now.ToUnixTimeMilliseconds());

    /// <summary>The status-bar pill for <paramref name="status"/> and the download's last progress.</summary>
    public static UpdatePill Pill(UpdateStatus? status, UpdateProgressEvent? progress, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        switch (status?.State)
        {
            case UpdatePhases.Downloading:
                var version = status.Latest?.Version ?? progress?.Version ?? "";
                var current = progress is { } p && (p.Version == version || version.Length == 0) ? p : null;
                var total = current?.Total ?? status.Latest?.Zip.Size ?? 0;
                var bytes = Math.Min(current?.Bytes ?? 0, total);
                var fraction = total > 0 ? (double)bytes / total : 0;
                var percent = (int)Math.Floor(fraction * 100);
                var speed = current?.BytesPerSecond ?? 0;
                var text = speed > 0
                    ? string.Create(culture, $"Update · {percent}% · {DisplayFormat.Bytes(speed, culture)}/s")
                    : string.Create(culture, $"Update · {percent}%");
                var tip = total > 0
                    ? $"Downloading CabinetOS {version}: {DisplayFormat.Bytes(bytes, culture)} of {DisplayFormat.Bytes(total, culture)}"
                    : $"Downloading CabinetOS {version}";
                return new UpdatePill(true, text, tip, fraction, ShowsTrack: true);
            case UpdatePhases.Applying:
                return new UpdatePill(true, "Update · installing", "CabinetOS moves the new version into its folder.");
            case UpdatePhases.Downloaded or UpdatePhases.Ready when WaitingVersion(status) is { } waiting:
                return new UpdatePill(true, "Update ready · Restart", $"CabinetOS {waiting} is ready. Click to restart into it.", 1, Restarts: true);
            default:
                return UpdatePill.Hidden;
        }
    }

    /// <summary>The About dialog's "Update" row.</summary>
    public static UpdateAboutRow AboutRow(UpdateStatus? status, UpdateProgressEvent? progress, DateTime nowLocal, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (status is null)
        {
            return new UpdateAboutRow("Not reported by this core", false);
        }
        var latest = status.Latest?.Version ?? "";
        var text = status.State switch
        {
            UpdatePhases.NotUpdatable => status.Reason ?? "This install does not update itself.",
            UpdatePhases.Unchecked => $"Not checked yet ({status.Channel} channel)",
            UpdatePhases.UpToDate => status.CheckedAtMs is { } checkedAt
                ? $"Up to date on the {status.Channel} channel, checked {DisplayFormat.Modified(DateTimeOffset.FromUnixTimeMilliseconds((long)checkedAt).UtcDateTime, nowLocal, culture)}"
                : $"Up to date on the {status.Channel} channel",
            UpdatePhases.Checking => "Checking for updates…",
            UpdatePhases.Available => $"{latest} is available",
            UpdatePhases.Downloading => $"Downloading {latest}: {Pill(status, progress, culture).Text["Update · ".Length..]}",
            UpdatePhases.Downloaded => $"{latest} is ready: restart to run it",
            UpdatePhases.Applying => $"Installing {latest}…",
            UpdatePhases.Ready => $"{WaitingVersion(status)} is installed: restart to run it",
            UpdatePhases.Failed => $"The last step failed: {status.Message ?? "no reason given"}",
            _ => status.State,
        };
        return new UpdateAboutRow(text, WaitingVersion(status) is not null);
    }

    /// <summary>
    /// The dialog for the newest version the core knows, or null when it knows none yet. After
    /// a rollback (<c>ready</c> with an older version in place) the notes of that version are
    /// not at hand: the dialog says so and links the changelog.
    /// </summary>
    public static UpdateDialogView? Dialog(UpdateStatus? status)
    {
        if (status?.Latest is not { } latest)
        {
            return null;
        }
        var waiting = status.State is UpdatePhases.Downloaded or UpdatePhases.Ready;
        var version = status.State == UpdatePhases.Ready ? status.Installed ?? latest.Version : latest.Version;
        var title = status.State switch
        {
            UpdatePhases.Downloaded or UpdatePhases.Ready => $"CabinetOS {version} is ready",
            UpdatePhases.Available or UpdatePhases.Downloading => $"CabinetOS {version} is available",
            _ => $"CabinetOS {version}",
        };
        var subtitle = version == latest.Version
            ? $"You have {status.Current}. Published {latest.Published} on the {status.Channel} channel."
            : $"You have {status.Current}.";
        IReadOnlyList<NoteBlock> notes = [];
        string? missing = null;
        if (version != latest.Version)
        {
            missing = $"The release notes of {version} are in the changelog.";
        }
        else if (status.Notes is { Length: > 0 } markdown)
        {
            notes = ReleaseNotes.Parse(markdown, ReleaseNotes.BaseFor(version));
        }
        else
        {
            missing = "The release notes could not be read.";
        }
        return new UpdateDialogView(
            version,
            title,
            subtitle,
            notes,
            missing,
            version == latest.Version ? status.NotesUrl ?? latest.Notes.Url : null,
            ReleaseNotes.ChangelogUrl(version),
            waiting ? "Restart now" : null,
            waiting ? "Later" : "Close",
            status.State == UpdatePhases.Downloaded);
    }

    /// <summary>
    /// The notice after an explicit check (Update: Check for Updates), or null when the check
    /// found a newer version, which then downloads with the pill.
    /// </summary>
    public static string? CheckedNotice(UpdateStatus status) => status.State switch
    {
        UpdatePhases.UpToDate => $"CabinetOS {status.Current} is the newest version on the {status.Channel} channel.",
        UpdatePhases.NotUpdatable => status.Reason ?? "This install does not update itself.",
        UpdatePhases.Failed => $"The update check failed: {status.Message ?? "no reason given"}",
        UpdatePhases.Available or UpdatePhases.Downloading or UpdatePhases.Downloaded or UpdatePhases.Ready => null,
        _ => $"The updater is {status.State.Replace('_', ' ')}.",
    };
}
