using AutoFantic.Core.Hardware;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Calibration;

/// <summary>
/// "Find my fans": runs every fan output through a few speeds, fast to slow, one after another,
/// and records which RPM sensor follows it. Empty headers, fans that stop at low speed and pumps
/// show up this way. A channel is never taken lower once it looks like a pump or nothing reacted
/// at all (possibly a pump without RPM signal): pumps are never experimented with.
/// </summary>
public static class FanDiscovery
{
    private static readonly float[] Steps = [100, 60, 30];
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

    // a fan "reacted" when its RPM changed by at least this much
    private const float ReactionRpm = 150;

    // a pump runs fast and hardly changes with duty cycle
    private const float PumpMinRpm = 1800;
    private const float PumpMaxRelativeChange = 0.25f;

    /// <summary>About how long it takes, for the UI.</summary>
    public static TimeSpan Duration(FanSession session) =>
        TimeSpan.FromSeconds(session.Channels.Count * (Steps.Length + 1) * Settle.TotalSeconds / session.TimeScale);

    /// <summary>Runs the test. Null if it had to stop (Ctrl+C, too hot, sensor problem); the reason goes to <paramref name="log"/>.</summary>
    /// <param name="progress">Called with the channel being tested and the share done (0–1).</param>
    public static FanInventory? Run(FanSession session, CancellationToken cancel, Action<string>? log = null, Action<FanChannel, double>? progress = null)
    {
        var first = session.Read();
        var keys = KeySensors.Detect(first);
        var wait = new SafeWait(session, keys, cancel, log);
        if (!wait.CheckNow())
        {
            log?.Invoke(T($"Not starting: {wait.StopReason}."));
            return null;
        }

        var headers = new List<FanHeader>();
        var channels = session.Channels.ToList();
        for (int c = 0; c < channels.Count; c++)
        {
            var channel = channels[c];
            progress?.Invoke(channel, (double)c / channels.Count);
            var measured = new List<(float Percent, Snapshot Snapshot)>();
            try
            {
                foreach (var percent in Steps)
                {
                    session.SetPercent(channel, percent);
                    if (wait.Wait(Settle) != WaitEnd.Done)
                        break;
                    measured.Add((percent, wait.Last!));
                    if (measured.Count >= 2 && percent != Steps[^1] && WhyNotLower(measured, first) is { } why)
                    {
                        if (why.StartsWith("nothing", StringComparison.Ordinal))
                            log?.Invoke(T($"{channel}: not taken lower, {T(why)}"));
                        break;
                    }
                }
            }
            finally
            {
                session.RestoreDefault(channel);
            }

            if (wait.StopReason is { } stop)
            {
                log?.Invoke(T($"Find my fans stopped: {stop}. All fans are back on BIOS control."));
                return null;
            }

            var header = ToHeader(channel, measured.OrderBy(s => s.Percent).ToList(), first);
            headers.Add(header);
            log?.Invoke(header.Connected ? (header.IsPump ? T($"{channel}: looks like a pump") : T($"{channel}: fan found")) : T($"{channel}: nothing on it"));

            // let the fan spin back to its BIOS speed before the next one, so they don't mix up
            if (wait.Wait(Settle) != WaitEnd.Done)
            {
                log?.Invoke(T($"Find my fans stopped: {wait.StopReason}. All fans are back on BIOS control."));
                return null;
            }
        }

        progress?.Invoke(channels[^1], 1);
        return new FanInventory(DateTimeOffset.Now, headers);
    }

    private static List<(SensorReading Fan, List<float> Rpm)> Reacting(List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline) =>
        baseline.OfKind(SensorKind.Fan)
            .Select(fan => (Fan: fan, Rpm: steps.Select(s => s.Snapshot.Value(fan.Id) ?? 0).ToList()))
            .Where(x => x.Rpm.Max() - x.Rpm.Min() >= ReactionRpm)
            .OrderByDescending(x => x.Rpm.Max() - x.Rpm.Min())
            .ToList();

    private static string? WhyNotLower(List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline)
    {
        var fans = Reacting(steps, baseline);
        if (fans.Count == 0)
            return "nothing reacted so far (could be a pump without RPM signal)";
        if (fans.Any(f => LooksLikePump(f.Rpm)))
            return "it looks like a pump";
        return null;
    }

    private static bool LooksLikePump(List<float> rpm)
    {
        float high = rpm.Max(), low = rpm.Min();
        return high >= PumpMinRpm && low > 0 && (high - low) / high <= PumpMaxRelativeChange;
    }

    private static FanHeader ToHeader(FanChannel channel, List<(float Percent, Snapshot Snapshot)> ascending, Snapshot baseline)
    {
        var fan = ascending.Count >= 2 ? Reacting(ascending, baseline).FirstOrDefault() : default;
        var rpm = fan.Fan is null ? [] : ascending.Select((s, i) => new RpmPoint(s.Percent, fan.Rpm[i])).ToList();
        return new FanHeader(channel.Index, channel.Id, channel.Name, channel.Hardware, fan.Fan?.Id, rpm, fan.Fan is not null && LooksLikePump(fan.Rpm));
    }
}
