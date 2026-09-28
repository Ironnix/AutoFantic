using System.Diagnostics;

namespace AutoFantic.App;

/// <summary>
/// "Start with Windows" through the Task Scheduler: a task that starts AutoFantic at logon with
/// highest privileges, so the hardware driver loads without an admin prompt every time.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "AutoFantic";

    /// <summary>The task the app made before the rename; it points to an exe that no longer exists.</summary>
    private const string LegacyTaskName = "AutoFanatic";

    public static bool IsEnabled() => Schtasks(["/Query", "/TN", TaskName]) == 0;

    public static bool Enable() =>
        Schtasks(["/Create", "/TN", TaskName, "/TR", $"\"{Environment.ProcessPath}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"]) == 0;

    public static bool Disable() => Schtasks(["/Delete", "/TN", TaskName, "/F"]) == 0;

    /// <summary>Removes the "AutoFanatic" task from before the rename, if it's still there.</summary>
    public static void RemoveLegacy()
    {
        if (Schtasks(["/Query", "/TN", LegacyTaskName]) == 0)
            Schtasks(["/Delete", "/TN", LegacyTaskName, "/F"]);
    }

    private static int Schtasks(string[] args)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
