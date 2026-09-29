using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Calibration;

/// <summary>How a <see cref="SafeWait.Wait"/> ended.</summary>
internal enum WaitEnd
{
    /// <summary>The time was up.</summary>
    Done,

    /// <summary>The condition asked for was met.</summary>
    Until,

    Cancelled,

    /// <summary>A temperature limit was crossed; every fan ran at 100 % until it was safely cool, then went back to the BIOS.</summary>
    TooHot,

    /// <summary>A key temperature sensor stopped reporting or reads nonsense.</summary>
    SensorError,
}

/// <summary>
/// Waits in session seconds while watching the safety limits and the sensors, for every step of
/// a calibration that changes fans. A crossed limit runs every fan at 100 % until all
/// temperatures are safely below their limits for a few seconds, then hands them to the BIOS.
/// </summary>
internal sealed class SafeWait(FanSession session, KeySensors keys, CancellationToken cancel, Action<string>? log)
{
    private static readonly TimeSpan MaxCoolDown = TimeSpan.FromMinutes(5);

    private readonly SafetyLimits _limits = new();
    private readonly SensorPlausibility _plausibility = new(keys);

    public Snapshot? Last { get; private set; }

    /// <summary>Why the last wait ended early, in plain words.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Checks the limits once without waiting; false (with <see cref="StopReason"/>) if it's already too hot.</summary>
    public bool CheckNow()
    {
        Last = session.Read();
        StopReason = _limits.Check(Last, keys) ?? _plausibility.Check(Last);
        return StopReason is null;
    }

    public WaitEnd Wait(TimeSpan duration, Action<Snapshot>? onSample = null, Func<bool>? until = null)
    {
        var tick = TimeSpan.FromSeconds(1 / session.TimeScale);
        var end = session.Now + duration;
        while (session.Now < end)
        {
            if (cancel.WaitHandle.WaitOne(tick))
            {
                StopReason = T("stopped");
                return WaitEnd.Cancelled;
            }

            Last = session.Read();
            if (_limits.Check(Last, keys) is { } violation)
            {
                StopReason = violation;
                CoolDown();
                return WaitEnd.TooHot;
            }
            if (_plausibility.Check(Last) is { } sensorError)
            {
                StopReason = sensorError;
                return WaitEnd.SensorError;
            }

            onSample?.Invoke(Last);
            if (until?.Invoke() == true)
                return WaitEnd.Until;
        }
        StopReason = null;
        return WaitEnd.Done;
    }

    private void CoolDown()
    {
        log?.Invoke(T($"{StopReason}: all fans to 100 % until it has cooled down …"));
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
        var tick = TimeSpan.FromSeconds(1 / session.TimeScale);
        var started = session.Now;
        while (session.Now - started < MaxCoolDown && !cancel.WaitHandle.WaitOne(tick))
        {
            Last = session.Read();
            if (recovery.IsRecovered(Last))
            {
                log?.Invoke(T($"cooled down after {(session.Now - started).TotalSeconds:0} s"));
                break;
            }
        }
        session.RestoreAll();
    }
}
