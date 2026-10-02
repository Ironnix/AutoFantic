using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoFantic.Core.Reports;

namespace AutoFantic.Core;

/// <summary>
/// The folder the user chose for the data instead of the standard one (Settings → Your data), kept
/// as data-folder.json in the standard folder of each Windows account. Two accounts that choose the
/// same folder share the fans, the calibration, the curves and the history.
/// </summary>
/// <param name="CopyFrom">Only until the next start: the folder whose data is copied to <paramref name="Folder"/> first.</param>
public sealed record DataFolderChoice(string Folder, string? CopyFrom = null)
{
    public const string FileName = "data-folder.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Null without a choice (the standard folder is used), also if the file can't be read.</summary>
    public static DataFolderChoice? Load(string path) => File.Exists(path) ? Read(path) : null;

    // kept apart: without a choice nothing of the JSON library is loaded (the watchdog asks for the folder too)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DataFolderChoice? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DataFolderChoice>(File.ReadAllText(path), Json) is { Folder.Length: > 0 } choice ? choice : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The folder in use at this start, and what happened on the way to it.</summary>
/// <param name="Root">The folder in use (a simulated PC uses its sim\ subfolder).</param>
/// <param name="Own">This Windows account's standard folder; it also holds the choice of another one.</param>
/// <param name="CopiedFrom">Set if the data was copied to <paramref name="Root"/> at this start: from here.</param>
/// <param name="Unusable">The chosen folder, if it couldn't be used at this start.</param>
/// <param name="Why">Why <paramref name="Unusable"/> couldn't be used, in Windows' words; null if the folder just isn't there.</param>
/// <param name="CopyFailed">True if it was the copy to <paramref name="Unusable"/> that failed: then the choice is undone. False if the folder
/// just can't be reached right now (a drive that isn't there): the next start tries it again.</param>
public sealed record DataFolderUse(string Root, string Own, string? CopiedFrom = null, string? Unusable = null, string? Why = null, bool CopyFailed = false)
{
    /// <summary>True if the folder in use is one the user chose.</summary>
    public bool Chosen => !DataFolder.Same(Root, Own);
}

/// <summary>What choosing another folder does (<see cref="DataFolder.Check"/>).</summary>
public enum FolderChange
{
    /// <summary>It's the folder in use already.</summary>
    None,

    /// <summary>It's inside the folder in use, or the other way round: the data can't be copied into itself.</summary>
    Nested,

    /// <summary>It has files that aren't AuFantic's: nothing is copied between somebody else's files.</summary>
    OtherFiles,

    /// <summary>It has fans or a calibration already (another Windows account's): they are used as they are, nothing is copied.</summary>
    UseAsIs,

    /// <summary>The data in use is copied there: the folder is empty, or AuFantic's without fans and calibration.</summary>
    Copy,
}

/// <summary>Where fans.json, the measurements, the calibration and the log live.</summary>
public static class DataFolder
{
    /// <summary>Set this to use another standard folder (a developer's test data, a portable copy).</summary>
    public const string OverrideVariable = "AUTOFANTIC_DATA";

    /// <summary>Written into the new folder when the data was copied over from an older one.</summary>
    public const string MigratedNote = "copied-from.txt";

    private static DataFolderUse? _use;

    /// <summary>
    /// The folder in use: the one chosen in Settings, else %LocalAppData%\AutoFantic (or
    /// <see cref="OverrideVariable"/>). A simulated PC uses its sim\ subfolder, so its made-up data
    /// never mixes with real results. The first time, the data of earlier versions (runs\ in the
    /// repo or next to the exe) is copied over; the old folder stays as it is.
    /// </summary>
    public static string Default(bool simulated = false) => Sub(Use.Root, simulated);

    /// <summary>
    /// This Windows account's standard folder, whatever folder is in use. What AuFantic downloads
    /// and then runs (an update, the driver's installer) is kept here: never in a folder that
    /// other accounts can write to.
    /// </summary>
    public static string Own(bool simulated = false) => Sub(Use.Own, simulated);

    /// <summary>Which folder is in use and why; worked out once, at the first call (so a choice made while AuFantic runs counts from the next start).</summary>
    public static DataFolderUse Use => _use ??= Resolve(OwnRoot());

    private static string Sub(string root, bool simulated)
    {
        string folder = simulated ? Path.Combine(root, "sim") : root;
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string OwnRoot()
    {
        string root = Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFantic");
        try
        {
            if (Legacy() is { } legacy)
                MigrateOnce(legacy, root);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // copying is a convenience: without it AuFantic starts as if new
        }
        return root;
    }

    /// <summary>
    /// Where earlier versions kept their data: runs\ in the repo (found by walking up from the
    /// exe to AutoFantic.sln), else runs\ next to the exe. Null if there is none.
    /// </summary>
    public static string? Legacy()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutoFantic.sln")))
            {
                string repoRuns = Path.Combine(dir.FullName, "runs");
                return Directory.Exists(repoRuns) ? repoRuns : null;
            }
        }
        string next = Path.Combine(AppContext.BaseDirectory, "runs");
        return Directory.Exists(next) ? next : null;
    }

    /// <summary>
    /// Copies everything from <paramref name="from"/> to <paramref name="to"/> once: only if
    /// <paramref name="to"/> has no data of its own yet (no fans.json, no calibration, no copy
    /// before). Never deletes or changes anything in <paramref name="from"/>. True if it copied.
    /// </summary>
    public static bool MigrateOnce(string from, string to)
    {
        if (!Directory.Exists(from) || Same(from, to))
            return false;
        if (File.Exists(Path.Combine(to, MigratedNote)) || HasData(to))
            return false;
        if (!Directory.EnumerateFileSystemEntries(from).Any())
            return false;

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(to, MigratedNote),
            $"On {DateTime.Now:yyyy-MM-dd HH:mm} AuFantic copied its data here from{Environment.NewLine}{from}{Environment.NewLine}That folder was left as it was; it isn't used any more.{Environment.NewLine}");
        return true;
    }

    // ── a folder the user chose ────────────────────────────────────────────────────────

    /// <summary>True if both paths name the same folder.</summary>
    public static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the folder has fans or a calibration: data worth keeping, which is never copied over.</summary>
    public static bool HasData(string folder) =>
        File.Exists(Path.Combine(folder, CalibrationFiles.Inventory)) || File.Exists(Path.Combine(folder, CalibrationFiles.Result));

    /// <summary>When the fans or the calibration in the folder were last changed; null without them.</summary>
    public static DateTime? LastChanged(string folder)
    {
        var files = new[] { CalibrationFiles.Result, CalibrationFiles.Inventory }.Select(name => Path.Combine(folder, name)).Where(File.Exists).ToList();
        return files.Count == 0 ? null : files.Max(File.GetLastWriteTime);
    }

    /// <summary>What choosing <paramref name="folder"/> would do, with <paramref name="now"/> in use.</summary>
    public static FolderChange Check(DataFolderUse now, string folder) =>
        Same(folder, now.Root) ? FolderChange.None
        : Inside(folder, now.Root) || Inside(now.Root, folder) ? FolderChange.Nested
        : HasData(folder) ? FolderChange.UseAsIs
        : IsEmpty(folder) || IsOurs(folder) ? FolderChange.Copy
        : FolderChange.OtherFiles;

    private static bool IsEmpty(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>True for a folder AuFantic ran with, set up or not: every start writes its log there.</summary>
    private static bool IsOurs(string folder) =>
        HasData(folder) || File.Exists(Path.Combine(folder, Logging.ActivityLog.FileName)) || File.Exists(Path.Combine(folder, Monitoring.HistoryStore.FileName));

    /// <summary>
    /// Keeps the user's choice of <paramref name="folder"/>; it counts from the next start
    /// (<see cref="Resolve"/>), which also copies the data while no AuFantic has it open. Throws
    /// with the reason if the folder can't be written to; then nothing changed.
    /// </summary>
    public static FolderChange Choose(DataFolderUse now, string folder)
    {
        var change = Check(now, folder);
        if (change is not (FolderChange.UseAsIs or FolderChange.Copy))
            return change;
        folder = Full(folder);
        EnsureWritable(folder);
        if (change == FolderChange.Copy)
            new DataFolderChoice(folder, CopyFrom: now.Root).Save(Path.Combine(now.Own, DataFolderChoice.FileName));
        else
            Keep(now.Own, folder);
        return change;
    }

    /// <summary>
    /// The folder to use, for the Windows account whose standard folder is <paramref name="own"/>:
    /// the one it chose (copying the data there first, if that is still to do), else its own. A
    /// chosen folder that can't be reached (a drive that isn't there, a folder that was deleted) is
    /// left out for this start only; a copy that fails undoes the choice, and what it had copied
    /// is removed again.
    /// </summary>
    public static DataFolderUse Resolve(string own)
    {
        if (DataFolderChoice.Load(Path.Combine(own, DataFolderChoice.FileName)) is not { } choice)
            return new DataFolderUse(own, own);
        try
        {
            string folder = Full(choice.Folder);
            if (choice.CopyFrom is not { } from)
            {
                // a folder in use that is gone is not made again: empty, it would look like a PC that was never set up
                if (!Directory.Exists(folder))
                    return new DataFolderUse(own, own, Unusable: choice.Folder);
                EnsureWritable(folder);
                return new DataFolderUse(folder, own);
            }
            EnsureWritable(folder);
            CopyAll(from, folder);
            Keep(own, folder);
            return new DataFolderUse(folder, own, CopiedFrom: from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (choice.CopyFrom is not { } before)
                return new DataFolderUse(own, own, Unusable: choice.Folder, Why: ex.Message);
            // the data stays where it was
            string stay = Directory.Exists(before) ? before : own;
            Keep(own, stay);
            return new DataFolderUse(stay, own, Unusable: choice.Folder, Why: ex.Message, CopyFailed: true);
        }
    }

    /// <summary>Writes down that <paramref name="folder"/> is in use from now on; the standard folder needs no note.</summary>
    private static void Keep(string own, string folder)
    {
        string file = Path.Combine(own, DataFolderChoice.FileName);
        if (Same(folder, own))
            File.Delete(file);
        else
            new DataFolderChoice(Full(folder)).Save(file);
    }

    /// <summary>
    /// Copies every file, replacing what has the same name: only called for a folder without fans
    /// and calibration, so nothing worth keeping is lost. If a file can't be copied, the ones
    /// copied so far are removed again and the error is passed on. <paramref name="from"/> stays as it is.
    /// </summary>
    private static void CopyAll(string from, string to)
    {
        var copied = new List<string>();
        try
        {
            // the list first: copying must never find its own copies
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
            {
                string relative = Path.GetRelativePath(from, file);
                if (relative is DataFolderChoice.FileName or MigratedNote)
                    continue; // those are about the old folder itself
                string target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied.Add(target);
                // a database's journal belongs to the file it was written for: never another one's
                foreach (string journal in new[] { "-wal", "-shm" })
                    if (!File.Exists(file + journal))
                        File.Delete(target + journal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (string target in copied)
            {
                try
                {
                    File.Delete(target);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    // it stays: without fans.json and calibration.json the folder still counts as empty
                }
            }
            throw;
        }
    }

    /// <summary>Makes the folder if needed and tries a file in it; throws with the reason if that doesn't work.</summary>
    private static void EnsureWritable(string folder)
    {
        Directory.CreateDirectory(folder);
        using var probe = new FileStream(Path.Combine(folder, $".aufantic-{Environment.ProcessId}.tmp"), FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private static bool Inside(string folder, string parent) =>
        Full(folder).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Full(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    // ── only one AuFantic ──────────────────────────────────────────────────────────────

    /// <summary>Name of the mutex the background app (tray icon) holds while it controls the fans.</summary>
    public const string BackgroundMutex = "AutoFantic.Background";

    /// <summary>
    /// The same for the whole PC: <see cref="BackgroundMutex"/> is seen only inside one Windows
    /// account's session, but after "Switch user" another account's AuFantic keeps running and
    /// keeps the fans (and, with a shared data folder, the same files).
    /// </summary>
    public const string PcMutex = @"Global\AutoFantic.Background";

    /// <summary>The same app from before the rename (spelled "AutoFanatic"): it must never run next to the new one.</summary>
    public const string LegacyBackgroundMutex = "AutoFanatic.Background";

    /// <summary>True while the background app runs, in any version and any Windows account: then nothing else may drive the fans.</summary>
    public static bool BackgroundRunning() => Exists(BackgroundMutex) || Exists(PcMutex) || LegacyBackgroundRunning();

    /// <summary>True while the app from before the rename runs.</summary>
    public static bool LegacyBackgroundRunning() => Exists(LegacyBackgroundMutex);

    private static bool Exists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // it exists, it just belongs to an elevated process
        }
    }
}
