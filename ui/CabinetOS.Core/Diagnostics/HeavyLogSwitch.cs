namespace CabinetOS.Core.Diagnostics;

/// <summary>What <see cref="HeavyLogSwitch.Follow"/> did.</summary>
public enum HeavyLogChange
{
    /// <summary>Nothing: heavy mode was already as the configuration says, or the environment decides.</summary>
    None,

    /// <summary>Heavy mode came on.</summary>
    TurnedOn,

    /// <summary>Heavy mode went off.</summary>
    TurnedOff,
}

/// <summary>
/// The window's own heavy mode follows <c>logging.heavy</c>, as the core's does: the window reads
/// it at start and on every <c>config_changed</c>, and the status bar's pill shows the state
/// (docs/ui.md, "Heavy logging"). <c>CABINETOS_LOG_HEAVY</c> wins over the configuration in both
/// processes, so when it is set the switch follows nothing and the pill cannot turn it off.
/// </summary>
public sealed class HeavyLogSwitch(LogWriter? writer, bool fromEnvironment)
{
    /// <summary>The switch of this process's log: <see cref="Diag.Writer"/> and the environment.</summary>
    public static HeavyLogSwitch ForProcess() => new(Diag.Writer, Diag.HeavyFromEnvironment);

    /// <summary>Whether heavy mode is on.</summary>
    public bool IsOn => writer?.HeavyEnabled ?? false;

    /// <summary>Whether <c>CABINETOS_LOG_HEAVY</c> decides, so the configuration cannot change the mode.</summary>
    public bool FromEnvironment => fromEnvironment;

    /// <summary>What the status bar's pill shows now.</summary>
    public HeavyPillState Pill => HeavyPillState.For(IsOn, writer?.HeavyLostLines ?? 0, fromEnvironment);

    /// <summary>Makes heavy mode what <c>logging.heavy</c> says (<paramref name="configured"/>).</summary>
    public HeavyLogChange Follow(bool configured)
    {
        if (writer is null || fromEnvironment || !writer.SetHeavy(configured))
        {
            return HeavyLogChange.None;
        }
        return configured ? HeavyLogChange.TurnedOn : HeavyLogChange.TurnedOff;
    }
}
