namespace CabinetOS.Core.Diagnostics;

/// <summary>Log levels, least important first, as in docs/diagnostics.md.</summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// The level filter: <c>CABINETOS_LOG</c> in the core's syntax, for example
/// <c>debug</c> or <c>info,cabinetos_ui::pipe=trace</c>. A directive without
/// <c>=</c> sets the default; <c>target=level</c> applies to that target and
/// every target below it, and the longest matching target wins.
/// </summary>
public sealed class LogFilter
{
    private readonly LogLevel _default;
    private readonly (string Prefix, LogLevel Level)[] _targets;

    private LogFilter(LogLevel defaultLevel, (string, LogLevel)[] targets)
    {
        _default = defaultLevel;
        _targets = targets;
    }

    /// <summary>The filter used when <c>CABINETOS_LOG</c> is not set.</summary>
    public static LogFilter Default { get; } = new(LogLevel.Info, []);

    /// <summary>Parses a filter; unknown parts are ignored, an empty text means the default.</summary>
    public static LogFilter Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return Default;
        }
        var level = LogLevel.Info;
        var targets = new List<(string, LogLevel)>();
        foreach (var raw in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = raw.IndexOf('=');
            if (equals < 0)
            {
                if (TryLevel(raw, out var parsed))
                {
                    level = parsed;
                }
            }
            else if (TryLevel(raw[(equals + 1)..], out var parsed))
            {
                targets.Add((raw[..equals].Trim(), parsed));
            }
        }
        targets.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return new LogFilter(level, [.. targets]);
    }

    /// <summary>Whether an event of <paramref name="level"/> from <paramref name="target"/> is written.</summary>
    public bool IsEnabled(LogLevel level, string target) => level >= LevelFor(target);

    private LogLevel LevelFor(string target)
    {
        foreach (var (prefix, level) in _targets)
        {
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (target.Length == prefix.Length || target[prefix.Length] == ':'))
            {
                return level;
            }
        }
        return _default;
    }

    private static bool TryLevel(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn": level = LogLevel.Warn; return true;
            case "error": level = LogLevel.Error; return true;
            default: level = LogLevel.Info; return false;
        }
    }
}
