using Microsoft.Win32;

namespace AutoFantic.Core.Hardware;

public enum CheckResult
{
    Ok,

    /// <summary>Good to know, nothing to do (no graphics card fans to control).</summary>
    Info,

    /// <summary>Works, but needs a look (another fan program running, a sensor missing).</summary>
    Warning,

    /// <summary>AutoFantic can't do its job this way (no fan outputs, no temperatures).</summary>
    Problem,
}

public sealed record SetupCheck(string Title, CheckResult Result, string Detail);

/// <summary>
/// What AutoFantic checks when it starts: can it reach the fans (a supported fan chip and the PawnIO
/// driver for it, the graphics card's fans), does it find the temperatures, and is another program
/// controlling the fans too. Shown on the set-up page, and the problems also in the log.
/// </summary>
public static class SystemCheck
{
    public const string PawnIoUrl = "https://pawnio.eu";

    /// <summary>The PawnIO driver's service is registered (LibreHardwareMonitor needs it for the mainboard's fan chip).</summary>
    public static bool PawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true; // can't tell: don't claim it's missing
        }
    }

    /// <param name="pawnIo">Whether the PawnIO driver is installed (<see cref="PawnIoInstalled"/>; a parameter for tests).</param>
    /// <param name="otherTools">Other fan programs running (<see cref="FanToolCheck.Running"/>).</param>
    public static IReadOnlyList<SetupCheck> Run(FanSession session, KeySensors keys, bool pawnIo, IReadOnlyList<string> otherTools)
    {
        var checks = new List<SetupCheck>();
        int mainboard = session.Channels.Count(c => !IsGpu(c)), gpu = session.Channels.Count(IsGpu);

        checks.Add(mainboard > 0
            ? new SetupCheck("Mainboard fans", CheckResult.Ok, $"{mainboard} fan outputs found on {session.Channels.First(c => !IsGpu(c)).Hardware}.")
            : pawnIo
            ? new SetupCheck("Mainboard fans", CheckResult.Problem, "No mainboard fan outputs found: the fan chip of this mainboard isn't supported by LibreHardwareMonitor (yet).")
            : new SetupCheck("Mainboard fans", CheckResult.Problem, $"The PawnIO driver is missing, so the mainboard's fan chip can't be reached. Install it from {PawnIoUrl}, then start AutoFantic again."));

        checks.Add(gpu > 0
            ? new SetupCheck("Graphics card fans", CheckResult.Ok, $"{gpu} fan output{(gpu == 1 ? "" : "s")} found on {session.Channels.First(IsGpu).Hardware}.")
            : new SetupCheck("Graphics card fans", CheckResult.Info, "No graphics card fans found: the card keeps its own fan curve. (NVIDIA and AMD cards are supported.)"));

        var missing = new List<string>();
        if (keys.CpuTemp is null)
            missing.Add("CPU temperature");
        if (keys.GpuTemp is null)
            missing.Add("GPU temperature");
        if (keys.CpuPower is null)
            missing.Add("CPU power");
        checks.Add(missing.Count == 0
            ? new SetupCheck("Temperatures", CheckResult.Ok, "CPU and GPU temperature and power found.")
            : new SetupCheck("Temperatures", keys.CpuTemp is null ? CheckResult.Problem : CheckResult.Warning, $"Not found: {string.Join(", ", missing)}."));

        checks.Add(otherTools.Count == 0
            ? new SetupCheck("Other fan programs", CheckResult.Ok, "None running.")
            : new SetupCheck("Other fan programs", CheckResult.Warning,
                $"{string.Join(", ", otherTools)} running. Switch its fan control off (monitoring is fine), or the two programs fight over the same fans."));
        return checks;
    }

    private static bool IsGpu(FanChannel channel) => channel.Id.Contains("gpu", StringComparison.OrdinalIgnoreCase);
}
