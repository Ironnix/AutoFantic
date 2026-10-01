using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AutoFantic.Core.Logging;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Hardware;

/// <summary>One field of a fan chip's memory of the BIOS setup: "Byte" or "Boolean" values, one per fan output.</summary>
public sealed record ChipField(string Type, int[] Values);

/// <summary>
/// What a mainboard fan chip had set before AuFantic took a fan over (LibreHardwareMonitor keeps
/// it in the chip object: the original control mode and speed of every output it changed).
/// </summary>
/// <param name="HardwareId">The chip's identifier, e.g. "/lpc/nct6686d/0".</param>
/// <param name="ChipType">The library's class for it (Nct677X, IT87XX …): values are only put back into the same kind.</param>
public sealed record ChipState(string HardwareId, string ChipType, IReadOnlyDictionary<string, ChipField> Fields);

/// <summary>
/// fans-in-use-&lt;process id&gt;.json: exists only while AuFantic (or the test console) drives at
/// least one fan, one per process. It says which process, which fans, and what the BIOS had set on them. If that process ends
/// without handing the fans back (a crash, "End task"), the watchdog, or the next start, finds the
/// file and gives the fans back with it; otherwise a mainboard fan would stay stuck at its last
/// speed until the PC restarts, because a new process only sees the stuck setting.
/// </summary>
public sealed record HandbackFile(int ProcessId, DateTimeOffset ProcessStart, DateTimeOffset Since, IReadOnlyList<string> Channels, IReadOnlyList<ChipState> Chips)
{
    /// <summary>fans-in-use-1234.json for process 1234.</summary>
    public const string Pattern = "fans-in-use-*.json";

    public static string NameFor(int processId) => $"fans-in-use-{processId}.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path)
    {
        // written next to it and then moved over it: a crash while writing never leaves half a file
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static HandbackFile? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HandbackFile>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>For the process running now.</summary>
    public static HandbackFile ForThisProcess(IReadOnlyList<string> channels, IReadOnlyList<ChipState> chips, DateTimeOffset since)
    {
        using var self = Process.GetCurrentProcess();
        return new HandbackFile(self.Id, self.StartTime, since, channels, chips);
    }

    /// <summary>True while the process that wrote the file still runs (the same process, not a new one with a reused id).</summary>
    public bool OwnerRunning()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (process.HasExited)
                return false;
            return Math.Abs((process.StartTime - ProcessStart.LocalDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false; // no process with that id
        }
        catch (InvalidOperationException)
        {
            return false; // ended while looking
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // there is one, but it can't be looked at: better not touch its fans
        }
    }
}

/// <summary>
/// Reads and writes a fan chip's memory of the BIOS setup through reflection, because the library
/// keeps it private: every chip class stores it in fields named _initial… (the original mode and
/// speed per output) and _restoreDefault…Required (which outputs it changed). Put back into a fresh
/// chip object, the library's own hand-back then restores exactly what the BIOS had set.
/// </summary>
public static class ChipMemory
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool IsMemory(FieldInfo f) =>
        (f.Name.StartsWith("_initial", StringComparison.Ordinal) || f.Name.StartsWith("_restoreDefault", StringComparison.Ordinal))
        && (f.FieldType == typeof(byte[]) || f.FieldType == typeof(bool[]));

    /// <summary>The chip's memory; null if it has none this knows how to read.</summary>
    public static ChipState? Capture(string hardwareId, object chip)
    {
        var fields = new Dictionary<string, ChipField>();
        foreach (var field in chip.GetType().GetFields(Fields).Where(IsMemory))
        {
            switch (field.GetValue(chip))
            {
                case byte[] bytes:
                    fields[field.Name] = new ChipField("Byte", bytes.Select(b => (int)b).ToArray());
                    break;
                case bool[] flags:
                    fields[field.Name] = new ChipField("Boolean", flags.Select(b => b ? 1 : 0).ToArray());
                    break;
            }
        }
        // without the "which outputs were changed" flags a restore would do nothing
        return fields.Keys.Any(k => k.StartsWith("_restoreDefault", StringComparison.Ordinal))
            ? new ChipState(hardwareId, chip.GetType().Name, fields)
            : null;
    }

    /// <summary>
    /// Puts a captured memory back into a chip object of the same kind. False (nothing changed) if
    /// the kind or the number of outputs doesn't match.
    /// </summary>
    public static bool Inject(object chip, ChipState state)
    {
        if (chip.GetType().Name != state.ChipType)
            return false;

        var targets = chip.GetType().GetFields(Fields).Where(IsMemory).ToDictionary(f => f.Name);
        foreach (var (name, value) in state.Fields)
        {
            if (!targets.TryGetValue(name, out var field) || field.GetValue(chip) is not Array current || current.Length != value.Values.Length)
                return false;
            if ((value.Type == "Byte") != (field.FieldType == typeof(byte[])))
                return false;
        }

        foreach (var (name, value) in state.Fields)
        {
            var array = (Array)targets[name].GetValue(chip)!;
            for (int i = 0; i < value.Values.Length; i++)
                array.SetValue(value.Type == "Byte" ? (object)(byte)value.Values[i] : value.Values[i] != 0, i);
        }
        return true;
    }
}

/// <summary>
/// Gives the fans back to the BIOS after a process that drove them ended without doing it itself.
/// Used by the watchdog right after AuFantic ends, and by AuFantic and the test console when
/// they start (in case the watchdog didn't run or ended too). Only one process does it at a time.
/// </summary>
public static class Handback
{
    private const string MutexName = "AutoFantic.Handback";

    /// <summary>Where this process keeps its file (<see cref="FanSession.HandbackPath"/>).</summary>
    public static string PathIn(string folder) => Path.Combine(folder, HandbackFile.NameFor(Environment.ProcessId));

    /// <summary>True if a process left fans behind (or still drives them): a file is there.</summary>
    public static bool AnyIn(string folder) => Directory.Exists(folder) && Directory.EnumerateFiles(folder, HandbackFile.Pattern).Any();

    /// <summary>
    /// For every fans-in-use file whose process is gone: gives its fans back through
    /// <paramref name="session"/>, writes what happened to the log and removes the file. Files of
    /// processes that still run are left alone. Null if there was nothing to do; otherwise the
    /// (last) log entry.
    /// </summary>
    /// <param name="who">Who is doing it, for the log: "the watchdog", "AuFantic at its start" …</param>
    public static ActivityEntry? RecoverIfNeeded(string folder, FanSession session, ActivityLog log, string who)
    {
        if (!AnyIn(folder))
            return null;

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        try
        {
            ActivityEntry? entry = null;
            foreach (string path in Directory.GetFiles(folder, HandbackFile.Pattern))
            {
                var file = HandbackFile.Load(path);
                if (file is null)
                {
                    File.Delete(path); // unreadable: nothing it could still be used for
                    continue;
                }
                if (file.OwnerRunning())
                    continue;

                var lines = session.HandBack(file);
                File.Delete(path);
                entry = log.Add(LogKind.Watchdog,
                    T($"AuFantic ended without handing the fans back (it had driven them since {file.Since.ToLocalTime():dd.MM. HH:mm}): {who} gave them back to the BIOS. {string.Join("; ", lines)}"));
            }
            return entry;
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
        }
    }
}
