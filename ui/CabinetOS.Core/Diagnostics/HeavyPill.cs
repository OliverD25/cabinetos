using System.Globalization;

namespace CabinetOS.Core.Diagnostics;

/// <summary>What the status bar's heavy-logging pill shows: whether it is there, its text and its tooltip.</summary>
public readonly record struct HeavyPillState(bool Visible, string Text, string ToolTip)
{
    /// <summary>The pill's text while heavy mode is on and nothing was lost.</summary>
    public const string Label = "HEAVY LOG";

    /// <summary>
    /// The pill for a window whose heavy mode is <paramref name="on"/>. The text says how many
    /// lines the window dropped because its heavy queue was full (<paramref name="lostLines"/>):
    /// <c>HEAVY LOG, 1,204 lines lost</c>. <paramref name="fromEnvironment"/> means
    /// <c>CABINETOS_LOG_HEAVY</c> decided, so clicking the pill cannot turn it off.
    /// </summary>
    public static HeavyPillState For(bool on, long lostLines, bool fromEnvironment = false)
    {
        if (!on)
        {
            return new HeavyPillState(false, "", "");
        }
        var text = lostLines > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Label}, {lostLines:N0} {(lostLines == 1 ? "line" : "lines")} lost")
            : Label;
        var tip = "Heavy logging is on: every action is written to the log, even at a cost in speed. "
            + (fromEnvironment ? "The environment variable CABINETOS_LOG_HEAVY decides; unset it to turn heavy logging off." : "Click to turn it off.");
        if (lostLines > 0)
        {
            tip += " The window's log queue was full, so some lines of the window were dropped.";
        }
        return new HeavyPillState(true, text, tip);
    }
}
