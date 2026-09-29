namespace AutoFantic.Core;

/// <summary>Where fans.json, the measurements, the calibration and the log live.</summary>
public static class DataFolder
{
    /// <summary>Set this to use another folder (a developer's test data, a portable copy).</summary>
    public const string OverrideVariable = "AUTOFANTIC_DATA";

    /// <summary>Written into the new folder when the data was copied over from an older one.</summary>
    public const string MigratedNote = "copied-from.txt";

    /// <summary>
    /// %LocalAppData%\AutoFantic (or <see cref="OverrideVariable"/>). A simulated PC uses its sim\
    /// subfolder, so its made-up data never mixes with real results. The first time, the data of
    /// earlier versions (runs\ in the repo or next to the exe) is copied over; the old folder stays
    /// as it is.
    /// </summary>
    public static string Default(bool simulated = false)
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
            // copying is a convenience: without it AutoFantic starts as if new
        }

        string folder = simulated ? Path.Combine(root, "sim") : root;
        Directory.CreateDirectory(folder);
        return folder;
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
        if (!Directory.Exists(from) || Path.GetFullPath(from).TrimEnd('\\') == Path.GetFullPath(to).TrimEnd('\\'))
            return false;
        if (File.Exists(Path.Combine(to, MigratedNote)) || File.Exists(Path.Combine(to, "fans.json")) || File.Exists(Path.Combine(to, "calibration.json")))
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
            $"On {DateTime.Now:yyyy-MM-dd HH:mm} AutoFantic copied its data here from{Environment.NewLine}{from}{Environment.NewLine}That folder was left as it was; it isn't used any more.{Environment.NewLine}");
        return true;
    }

    /// <summary>Name of the mutex the background app (tray icon) holds while it controls the fans.</summary>
    public const string BackgroundMutex = "AutoFantic.Background";

    /// <summary>The same app from before the rename (spelled "AutoFanatic"): it must never run next to the new one.</summary>
    public const string LegacyBackgroundMutex = "AutoFanatic.Background";

    /// <summary>True while the background app runs, in any version: then nothing else may drive the fans.</summary>
    public static bool BackgroundRunning() => Exists(BackgroundMutex) || LegacyBackgroundRunning();

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
