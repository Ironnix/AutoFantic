using AutoFantic.Core.Analysis;
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

/// <summary>What the user can do about a check, as a button next to it.</summary>
public enum CheckFix
{
    None,

    /// <summary>Open the PawnIO driver's download page (<see cref="SystemCheck.PawnIoUrl"/>).</summary>
    GetPawnIo,

    /// <summary>Start AutoFantic again (it opens the hardware only at its start).</summary>
    Restart,
}

/// <summary>The PawnIO driver, which the mainboard's fan chip and the CPU temperature are read through.</summary>
public enum PawnIo
{
    Installed,

    Missing,

    /// <summary>Installed while AutoFantic was running: it takes a restart of AutoFantic to use it.</summary>
    InstalledSinceStart,
}

public sealed record SetupCheck(string Title, CheckResult Result, string Detail, CheckFix Fix = CheckFix.None);

/// <summary>
/// What AutoFantic checks when it starts: can it reach the fans (a supported fan chip and the PawnIO
/// driver for it, the graphics card's fans), does it read the temperatures, and is another program
/// controlling the fans too. Shown on the set-up page, and the problems also in the log.
/// </summary>
public static class SystemCheck
{
    public const string PawnIoUrl = "https://pawnio.eu";

    /// <summary>The PawnIO driver's service is registered (LibreHardwareMonitor needs it for the mainboard's fan chip and the CPU).</summary>
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

    /// <param name="pawnIo">The PawnIO driver (<see cref="PawnIoInstalled"/> now and when the hardware was opened; a parameter for tests).</param>
    /// <param name="otherTools">Other fan programs running (<see cref="FanToolCheck.Running"/>).</param>
    public static IReadOnlyList<SetupCheck> Run(FanSession session, KeySensors keys, PawnIo pawnIo, IReadOnlyList<string> otherTools)
    {
        var checks = new List<SetupCheck>();
        // a water cooler's own controller is neither: it is there without the driver, the mainboard's outputs aren't
        var mainboard = session.Channels.Where(c => c.IsMainboard).ToList();
        var gpu = session.Channels.Where(c => c.IsGpu).ToList();

        checks.Add(mainboard.Count > 0
            ? new SetupCheck(T("Mainboard fans"), CheckResult.Ok, T($"{mainboard.Count} outputs on {mainboard[0].Hardware}."))
            : pawnIo switch
            {
                PawnIo.Missing => new SetupCheck(T("Mainboard fans"), CheckResult.Problem,
                    T($"The PawnIO driver is missing: without it AutoFantic can't reach the mainboard's fans. AutoFantic's window installs it for you (Install PawnIO), or get it from {PawnIoUrl}."), CheckFix.GetPawnIo),
                PawnIo.InstalledSinceStart => new SetupCheck(T("Mainboard fans"), CheckResult.Problem,
                    T("The PawnIO driver is installed now: restart AutoFantic to use it."), CheckFix.Restart),
                _ => new SetupCheck(T("Mainboard fans"), CheckResult.Problem, T("None found: this mainboard's fan chip isn't supported yet.")),
            });

        checks.Add(gpu.Count > 0
            ? new SetupCheck(T("Graphics card fans"), CheckResult.Ok, gpu.Count == 1 ? T($"{gpu.Count} output on {gpu[0].Hardware}.") : T($"{gpu.Count} outputs on {gpu[0].Hardware}."))
            : new SetupCheck(T("Graphics card fans"), CheckResult.Info, T("None found: the card keeps its own fan curve.")));

        var missing = new List<string>();
        if (keys.CpuTemp is null)
            missing.Add(T("CPU temperature"));
        if (keys.GpuTemp is null)
            missing.Add(T("GPU temperature"));
        if (keys.CpuPower is null)
            missing.Add(T("CPU power"));
        // a sensor that is there but reads 0 °C: the CPU is read through the PawnIO driver too
        bool cpuReads = keys.CpuTemp is null || SensorPlausibility.IsPlausible(session.Read().Value(keys.CpuTemp));
        checks.Add(!cpuReads
            ? new SetupCheck(T("Temperatures"), CheckResult.Problem, pawnIo switch
            {
                PawnIo.Missing => T("The CPU temperature can't be read without the PawnIO driver."),
                PawnIo.InstalledSinceStart => T("The CPU temperature can't be read until AutoFantic is restarted."),
                _ => T("The CPU temperature can't be read (its sensor shows nothing sensible). Restart the PC; if it stays like this, this CPU isn't supported yet."),
            })
            : missing.Count == 0
            ? new SetupCheck(T("Temperatures"), CheckResult.Ok, T("CPU and GPU found."))
            : new SetupCheck(T("Temperatures"), keys.CpuTemp is null ? CheckResult.Problem : CheckResult.Warning, T($"Missing: {string.Join(", ", missing)}.")));

        checks.Add(otherTools.Count == 0
            ? new SetupCheck(T("Other fan programs"), CheckResult.Ok, T("None running."))
            : new SetupCheck(T("Other fan programs"), CheckResult.Warning,
                T($"{string.Join(", ", otherTools)} is running: switch its fan control off.")));
        return checks;
    }
}
