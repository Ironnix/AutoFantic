using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoFantic.Core;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Simulation;
using static AutoFantic.Core.Texts;

namespace AutoFantic.App;

/// <summary>
/// A second, tiny AuFantic process that only waits for the main one to end. If it ended without
/// handing the fans back (a crash, Task Manager → End task), it hands them back with what the main
/// one kept in its fans-in-use file: the fan chip's memory of the BIOS setup. Without it a
/// mainboard fan stays at its last speed until the PC restarts.
///
/// Started through a short-lived middle process, so it isn't a child of AuFantic: "End task" on
/// AuFantic (or its whole process tree) doesn't take the watchdog with it. It doesn't load the
/// window or the hardware library until it's needed, and it gives its memory back while it waits.
/// </summary>
internal static class Watchdog
{
    public const string Argument = "--watchdog";
    public const string LaunchArgument = "--watchdog-launch";

    /// <summary>From the main process: starts the watchdog for it (through the middle process).</summary>
    public static void Launch(bool simulate)
    {
        try
        {
            Start(LaunchArgument, Environment.ProcessId, simulate)?.Dispose();
        }
        catch (Exception)
        {
            // no watchdog: the next start still hands back what a crash left behind
        }
    }

    /// <summary>The middle process: starts the watchdog and ends right away, so the watchdog has no living parent.</summary>
    public static int RunLauncher(string[] args)
    {
        if (ProcessId(args) is not { } pid)
            return 2;
        Start(Argument, pid, args.Contains("--simulate"))?.Dispose();
        return 0;
    }

    /// <summary>The watchdog: waits, then hands back whatever the main process left behind.</summary>
    public static int Run(string[] args)
    {
        if (ProcessId(args) is not { } pid)
            return 2;
        bool simulate = args.Contains("--simulate");
        // now, not after the wait: the folder the main process uses (a folder chosen in Settings meanwhile counts for the next one)
        string folder = DataFolder.Default(simulate);

        try
        {
            using var main = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(self.Handle, -1, -1); // give the start-up's memory back: from here on it only waits
            main.WaitForExit();
        }
        catch (ArgumentException)
        {
            // already gone before it could be watched: still check below
        }

        if (!Handback.AnyIn(folder))
            return 0; // it handed its fans back itself: the normal case
        Program.UseLanguage(simulate); // for its lines in the log

        try
        {
            using FanSession session = simulate ? new SimulatedPc() : new HardwareSession();
            Handback.RecoverIfNeeded(folder, session, new ActivityLog(folder), T("the watchdog"));
        }
        catch (Exception ex)
        {
            new ActivityLog(folder).Add(LogKind.Warning, T($"The watchdog couldn't hand the fans back ({ex.Message}); AuFantic tries again when it starts. A PC restart always does it."));
        }
        return 0;
    }

    private static Process? Start(string mode, int pid, bool simulate)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (simulate)
            start.ArgumentList.Add("--simulate");
        return Process.Start(start);
    }

    private static int? ProcessId(string[] args)
    {
        int i = Array.FindIndex(args, a => a is Argument or LaunchArgument);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pid) ? pid : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
