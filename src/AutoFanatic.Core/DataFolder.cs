namespace AutoFanatic.Core;

/// <summary>Where fans.json, the measurements and the calibration live.</summary>
public static class DataFolder
{
    /// <summary>
    /// runs\ in the repo (found by walking up from the exe to AutoFanatic.sln), else runs\ next to
    /// the exe. A simulated PC uses runs\sim\, so its made-up data never mixes with real results.
    /// </summary>
    public static string Default(bool simulated = false)
    {
        string runs = Path.Combine(AppContext.BaseDirectory, "runs");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutoFanatic.sln")))
            {
                runs = Path.Combine(dir.FullName, "runs");
                break;
            }
        }
        return simulated ? Path.Combine(runs, "sim") : runs;
    }

    /// <summary>Name of the mutex the background app (tray icon) holds while it controls the fans.</summary>
    public const string BackgroundMutex = "AutoFanatic.Background";

    /// <summary>True while the background app runs: then nothing else may drive the fans.</summary>
    public static bool BackgroundRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(BackgroundMutex, out var mutex))
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
