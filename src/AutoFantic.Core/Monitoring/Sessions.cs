namespace AutoFantic.Core.Monitoring;

/// <summary>The average and highest value of one series during a session.</summary>
public readonly record struct SessionStat(double Avg, double Max);

/// <summary>A stretch of time one program (a game, a render) kept the PC busy.</summary>
/// <param name="Program">The process name, e.g. "VALORANT-Win64-Shipping" (<see cref="Sessions.Pretty"/> for showing it).</param>
/// <param name="Preset">The preset the fans ran with.</param>
/// <param name="Stats">Per series key: average and highest value during the session.</param>
public sealed record GameSession(string Program, DateTimeOffset Start, DateTimeOffset End, string Preset, IReadOnlyDictionary<string, SessionStat> Stats)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// Notices sessions: a program in the foreground that keeps the PC busy (the calibration's "there
/// is load": GPU at least 40 % or CPU at least 25 %) for at least a few minutes. Short breaks (a
/// loading screen, the menu, a quick look at another window) don't end it; 90 seconds without load
/// do. Fed once per sample.
/// </summary>
public sealed class SessionTracker
{
    public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Break = TimeSpan.FromSeconds(90);

    private const double BusyGpu = 40, BusyCpu = 25;

    // AutoFantic itself (the built-in load of a calibration), the desktop and the lock screen aren't games
    private static readonly HashSet<string> NotPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutoFantic", "autofantic-spike", "explorer", "LockApp", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "dwm", "Idle",
    };

    private (string Program, DateTimeOffset Start, DateTimeOffset LastBusy)? _current;

    /// <summary>The session running now, if any (it may still turn out too short to keep).</summary>
    public (string Program, DateTimeOffset Start)? Current => _current is { } c ? (c.Program, c.Start) : null;

    /// <summary>One sample. Returns a session that just ended and was long enough to keep: program, start, end.</summary>
    public (string Program, DateTimeOffset Start, DateTimeOffset End)? Feed(DateTimeOffset time, string? foreground, double cpuLoad, double gpuLoad)
    {
        bool busy = (gpuLoad >= BusyGpu || cpuLoad >= BusyCpu) && foreground is not null && !NotPrograms.Contains(foreground);
        (string, DateTimeOffset, DateTimeOffset)? ended = null;

        if (_current is { } c)
        {
            bool same = busy && string.Equals(foreground, c.Program, StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                _current = c with { LastBusy = time };
                return null;
            }
            if (time - c.LastBusy < Break && !busy)
                return null; // a short break
            if (busy && time - c.LastBusy < Break)
                return null; // another program for a moment (alt-tab): the game may come back
            _current = null;
            if (c.LastBusy - c.Start >= MinLength)
                ended = (c.Program, c.Start, c.LastBusy);
        }

        if (busy && _current is null)
            _current = (foreground!, time, time);
        return ended;
    }

    /// <summary>At exit: the running session, if it's long enough to keep.</summary>
    public (string Program, DateTimeOffset Start, DateTimeOffset End)? Finish()
    {
        var c = _current;
        _current = null;
        return c is { } s && s.LastBusy - s.Start >= MinLength ? (s.Program, s.Start, s.LastBusy) : null;
    }
}

public static class Sessions
{
    // what engines add to a game's process name
    private static readonly string[] Suffixes = ["-Win64-Shipping", "-WinGDK-Shipping", "-Win64-Test", "_x64", "_dx12", "_dx11", "_DX12", "_DX11", "-Win64", "64"];

    /// <summary>A process name as a person would write it: "VALORANT-Win64-Shipping" → "VALORANT", "cs2" stays "cs2".</summary>
    public static string Pretty(string program)
    {
        foreach (var suffix in Suffixes)
            if (program.Length > suffix.Length + 1 && program.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return program[..^suffix.Length];
        return program;
    }
}
