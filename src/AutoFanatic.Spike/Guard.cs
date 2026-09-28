using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// Waits in one-second ticks while watching the safety limits. Every command that changes a fan
/// waits through this, so a crossed limit or Ctrl+C always ends the command the same way.
/// A crossed temperature limit first runs every fan at 100 % until it is safely cool again.
/// </summary>
internal sealed class Guard(FanSession session, KeySensors keys, CancellationToken cancel)
{
    private readonly SafetyLimits _limits = new();
    private readonly SensorPlausibility _plausibility = new(keys);
    private bool _limitCrossed;

    // if it somehow doesn't cool down, hand back to the BIOS anyway after this long
    private static readonly TimeSpan MaxCoolDown = TimeSpan.FromMinutes(5);

    public Snapshot? Last { get; private set; }

    /// <summary>Why the last wait ended early (safety limit or Ctrl+C); null if it ran to the end.</summary>
    public string? StopReason { get; private set; }

    public bool Stopped => StopReason is not null;

    /// <summary>True if the last stop was a crossed temperature limit (the fans then ran at 100 % until cool).</summary>
    public bool LimitCrossed => _limitCrossed;

    /// <summary>True if the last stop was Ctrl+C.</summary>
    public bool Cancelled => cancel.IsCancellationRequested;

    /// <summary>Checks the limits and the sensors once without waiting.</summary>
    public bool CheckNow()
    {
        Last = session.Read();
        string? violation = _limits.Check(Last, keys);
        _limitCrossed = violation is not null;
        StopReason = violation is not null ? $"SAFETY STOP: {violation}"
            : _plausibility.Check(Last) is { } sensorError ? $"SAFETY STOP: {sensorError}"
            : null;
        return !Stopped;
    }

    /// <summary>
    /// Samples once per session second for <paramref name="duration"/> of session time (or until
    /// <paramref name="until"/> returns true). Returns false if it had to stop early.
    /// </summary>
    public bool Wait(TimeSpan duration, Action<Snapshot>? onSample = null, Func<bool>? until = null)
    {
        // session time, not wall-clock time: in a sped-up simulation the detectors still get one sample per (simulated) second
        var tick = TimeSpan.FromSeconds(1 / session.TimeScale);
        var end = session.Now + duration;
        while (session.Now < end)
        {
            if (cancel.WaitHandle.WaitOne(tick))
            {
                StopReason = "stopped with Ctrl+C";
                return false;
            }

            if (!CheckNow())
            {
                if (_limitCrossed)
                    CoolDown(tick);
                return false;
            }

            onSample?.Invoke(Last!);
            if (until?.Invoke() == true)
                return true;
        }
        return true;
    }

    /// <summary>
    /// Every fan to 100 % until all temperatures have been safely below their limits for a few
    /// seconds (<see cref="SafetyRecovery"/>), then every fan back to the BIOS. Ctrl+C hands back early.
    /// </summary>
    private void CoolDown(TimeSpan tick)
    {
        Console.WriteLine();
        Console.WriteLine($"{StopReason}.");
        Console.WriteLine($"All fans to 100 % until every temperature has been {SafetyRecovery.Margin:0} °C below its limit for {SafetyRecovery.CoolFor.TotalSeconds:0} s …");

        foreach (var channel in session.Channels)
        {
            try
            {
                session.SetPercent(channel, 100);
            }
            catch
            {
                // keep going: every other fan still has to speed up
            }
        }

        var recovery = new SafetyRecovery(_limits, keys);
        var started = session.Now;
        bool cooled = false;
        while (session.Now - started < MaxCoolDown && !cancel.WaitHandle.WaitOne(tick))
        {
            Last = session.Read();
            if (recovery.IsRecovered(Last))
            {
                cooled = true;
                break;
            }
        }

        session.RestoreAll();
        Console.WriteLine(cooled
            ? $"Cooled down after {(session.Now - started).TotalSeconds:0} s. All fans back to BIOS control."
            : "All fans back to BIOS control.");
    }

    /// <summary>Prints a warning if other fan software is running. Measurements are worthless while it fights us.</summary>
    public static void WarnAboutOtherFanTools()
    {
        var tools = FanToolCheck.Running();
        if (tools.Count == 0)
            return;

        Console.WriteLine($"Warning: {string.Join(", ", tools)} running. Make sure its fan control is OFF,");
        Console.WriteLine("         otherwise it fights AutoFanatic over the same fans (monitoring is fine).");
        Console.WriteLine();
    }
}
