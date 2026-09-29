using Microsoft.Win32;
using static AutoFantic.Core.Texts;

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
            ? new SetupCheck(T("Mainboard fans"), CheckResult.Ok, T($"{mainboard} outputs on {session.Channels.First(c => !IsGpu(c)).Hardware}."))
            : pawnIo
            ? new SetupCheck(T("Mainboard fans"), CheckResult.Problem, T("None found: this mainboard's fan chip isn't supported yet."))
            : new SetupCheck(T("Mainboard fans"), CheckResult.Problem, T($"The PawnIO driver is missing. Install it from {PawnIoUrl}, then restart AutoFantic.")));

        checks.Add(gpu > 0
            ? new SetupCheck(T("Graphics card fans"), CheckResult.Ok, gpu == 1 ? T($"{gpu} output on {session.Channels.First(IsGpu).Hardware}.") : T($"{gpu} outputs on {session.Channels.First(IsGpu).Hardware}."))
            : new SetupCheck(T("Graphics card fans"), CheckResult.Info, T("None found: the card keeps its own fan curve.")));

        var missing = new List<string>();
        if (keys.CpuTemp is null)
            missing.Add(T("CPU temperature"));
        if (keys.GpuTemp is null)
            missing.Add(T("GPU temperature"));
        if (keys.CpuPower is null)
            missing.Add(T("CPU power"));
        checks.Add(missing.Count == 0
            ? new SetupCheck(T("Temperatures"), CheckResult.Ok, T("CPU and GPU found."))
            : new SetupCheck(T("Temperatures"), keys.CpuTemp is null ? CheckResult.Problem : CheckResult.Warning, T($"Missing: {string.Join(", ", missing)}.")));

        checks.Add(otherTools.Count == 0
            ? new SetupCheck(T("Other fan programs"), CheckResult.Ok, T("None running."))
            : new SetupCheck(T("Other fan programs"), CheckResult.Warning,
                T($"{string.Join(", ", otherTools)} is running: switch its fan control off.")));
        return checks;
    }

    private static bool IsGpu(FanChannel channel) => channel.Id.Contains("gpu", StringComparison.OrdinalIgnoreCase);
}
