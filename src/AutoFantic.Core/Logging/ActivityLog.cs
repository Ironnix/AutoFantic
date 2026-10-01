using System.Globalization;
using System.Text;

namespace AutoFantic.Core.Logging;

public enum LogKind
{
    /// <summary>Started, exited, paused, resumed, a preset switched.</summary>
    Info,

    /// <summary>A fan switched off or on again.</summary>
    Fans,

    /// <summary>A temperature limit was crossed (every fan at 100 %), and when it was cool again.</summary>
    Safety,

    /// <summary>A sensor stopped reporting or read nonsense, and when it worked again.</summary>
    Sensor,

    /// <summary>Find my fans and calibrations: started, finished, stopped.</summary>
    Calibration,

    /// <summary>The fans were given back to the BIOS after AuFantic ended unexpectedly.</summary>
    Watchdog,

    /// <summary>Something the user should look at: another fan tool, a missing driver, an error.</summary>
    Warning,
}

public sealed record ActivityEntry(DateTimeOffset Time, LogKind Kind, string Text)
{
    private const string Format = "yyyy-MM-dd HH:mm:ss";

    /// <summary>One line of the log file: "2026-09-29 21:14:03 | Safety | CPU 91 °C …".</summary>
    public string ToLine() => $"{Time.ToLocalTime().ToString(Format, CultureInfo.InvariantCulture)} | {Kind} | {Text.ReplaceLineEndings(" ")}";

    public static ActivityEntry? Parse(string line)
    {
        var parts = line.Split(" | ", 3);
        if (parts.Length != 3
            || !DateTime.TryParseExact(parts[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            || !Enum.TryParse<LogKind>(parts[1], out var kind))
            return null;
        return new ActivityEntry(new DateTimeOffset(time), kind, parts[2]);
    }
}

/// <summary>
/// What AuFantic did, in plain words, newest last: safety stops, sensor problems, fans switching
/// off and on, calibrations, pauses. Kept as a text file in the data folder (activity.log, one line
/// per entry, readable in any editor) and the latest entries in memory for the window. The file is
/// started afresh at about 1 MB; the one before is kept as activity.old.log. Thread-safe: the fan
/// control, the calibration and the window all write to it.
/// </summary>
public sealed class ActivityLog
{
    public const string FileName = "activity.log";
    public const string OldFileName = "activity.old.log";

    private const int InMemory = 500;
    private const long MaxBytes = 1024 * 1024;

    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly LinkedList<ActivityEntry> _entries = new();

    /// <param name="folder">Where activity.log lives; null keeps it in memory only (tests, a simulated PC's self-test).</param>
    public ActivityLog(string? folder)
    {
        if (folder is null)
            return;
        _path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadLines(_path).TakeLast(InMemory))
                    if (ActivityEntry.Parse(line) is { } entry)
                        _entries.AddLast(entry);
        }
        catch (IOException)
        {
            // an unreadable log is no reason not to run
        }
    }

    public static ActivityLog InMemoryOnly() => new(null);

    public string? FilePath => _path;

    /// <summary>A new entry. Raised on the thread that added it.</summary>
    public event Action<ActivityEntry>? Added;

    /// <summary>The latest entries, oldest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get
        {
            lock (_lock)
                return [.. _entries];
        }
    }

    public ActivityEntry Add(LogKind kind, string text) => Add(new ActivityEntry(DateTimeOffset.Now, kind, text));

    public ActivityEntry Add(ActivityEntry entry)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > InMemory)
                _entries.RemoveFirst();
            Write(entry);
        }
        Added?.Invoke(entry);
        return entry;
    }

    private void Write(ActivityEntry entry)
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new FileInfo(_path);
            if (file.Exists && file.Length > MaxBytes)
                File.Move(_path, Path.Combine(file.DirectoryName!, OldFileName), overwrite: true);
            File.AppendAllText(_path, entry.ToLine() + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException)
        {
            // the entry stays in memory; a locked file (an editor) must never stop the fan control
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
