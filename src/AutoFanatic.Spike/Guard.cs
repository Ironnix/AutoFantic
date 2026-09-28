using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// Waits in one-second ticks while watching the safety limits. Every command that changes a fan
/// waits through this, so a crossed limit or Ctrl+C always ends the command the same way.
/// </summary>
internal sealed class Guard(FanSession session, KeySensors keys, CancellationToken cancel)
{
    private readonly SafetyLimits _limits = new();

    public Snapshot? Last { get; private set; }

    /// <summary>Why the last wait ended early (safety limit or Ctrl+C); null if it ran to the end.</summary>
    public string? StopReason { get; private set; }

    public bool Stopped => StopReason is not null;

    /// <summary>Checks the limits once without waiting.</summary>
    public bool CheckNow()
    {
        Last = session.Read();
        StopReason = _limits.Check(Last, keys) is { } violation ? $"SAFETY STOP: {violation}" : null;
        return !Stopped;
    }

    /// <summary>
    /// Samples once per second for <paramref name="duration"/> (or until <paramref name="until"/> returns true).
    /// Returns false if it had to stop early.
    /// </summary>
    public bool Wait(TimeSpan duration, Action<Snapshot>? onSample = null, Func<bool>? until = null)
    {
        var end = DateTimeOffset.Now + duration;
        while (DateTimeOffset.Now < end)
        {
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
            {
                StopReason = "stopped with Ctrl+C";
                return false;
            }

            if (!CheckNow())
                return false;

            onSample?.Invoke(Last!);
            if (until?.Invoke() == true)
                return true;
        }
        return true;
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
