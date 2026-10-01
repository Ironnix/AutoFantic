using System.Diagnostics;
using System.IO;

namespace AutoFantic.App;

/// <summary>
/// "Start with Windows" through the Task Scheduler: a task that starts AuFantic at logon with
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

    /// <summary>The exe the task starts; null without a task. After moving or unpacking AuFantic again it can be another copy.</summary>
    public static string? Target()
    {
        if (Schtasks(["/Query", "/TN", TaskName, "/XML"], out string xml) != 0)
            return null;
        var command = System.Text.RegularExpressions.Regex.Match(xml, "<Command>\"?([^<\"]+)\"?</Command>");
        return command.Success ? command.Groups[1].Value.Trim() : null;
    }

    /// <summary>The exe the task starts if that's another AutoFantic.exe than this one; null if it's this one or there's no task.</summary>
    public static string? OtherCopy() =>
        Target() is { } target && !string.Equals(Path.GetFullPath(target), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? target : null;

    /// <summary>
    /// Makes the task start this copy if it starts another one (an older copy left somewhere else:
    /// that one would come back at every logon and never get this copy's updates). Returns the copy
    /// it started before; null if nothing had to change (or the task couldn't be changed).
    /// </summary>
    public static string? FollowThisCopy() => OtherCopy() is { } other && Enable() ? other : null;

    /// <summary>Removes the "AutoFanatic" task from before the rename, if it's still there.</summary>
    public static void RemoveLegacy()
    {
        if (Schtasks(["/Query", "/TN", LegacyTaskName]) == 0)
            Schtasks(["/Delete", "/TN", LegacyTaskName, "/F"]);
    }

    private static int Schtasks(string[] args) => Schtasks(args, out _);

    private static int Schtasks(string[] args, out string output)
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
        var error = process.StandardError.ReadToEndAsync();
        output = process.StandardOutput.ReadToEnd();
        error.Wait();
        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
