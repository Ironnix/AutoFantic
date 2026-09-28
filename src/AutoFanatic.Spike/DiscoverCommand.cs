using System.Text;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// Runs every fan channel through a few speeds, one after another, and records which RPM sensor
/// follows it. Answers: which control drives which fan, which headers are empty, which fans
/// stop at low speed, and which channel looks like a pump.
///
/// Steps go from fast to slow, and a channel is not taken lower once it looks like a pump or
/// nothing reacted at all (possibly a pump without RPM signal): pumps are never experimented with.
/// </summary>
internal static class DiscoverCommand
{
    private static readonly float[] DefaultSteps = [100, 60, 30];

    // A pump runs fast and hardly changes with duty cycle.
    private const float PumpMinRpm = 1800;
    private const float PumpMaxRelativeChange = 0.25f;

    /// <summary>"--inventory fans.json" saves what was found, so later steps only use headers with a fan.</summary>
    /// <param name="found">If given, receives what was found (null if discover didn't finish).</param>
    public static int Run(FanSession session, string[] args, CancellationToken cancel, Action<FanInventory>? found = null)
    {
        var options = new Options(args, "--force");
        var steps = options.GetNumbers("--steps", DefaultSteps).OrderByDescending(p => p).ToList();
        var settle = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--settle", 8), 3, 30));
        var skip = options.Get("--skip") is { } skipText ? Options.ParseChannels(session, skipText) : [];

        if (!SetCommand.CheckLowPercent(steps, options))
            return 2;

        var channels = session.Channels.Where(c => !skip.Contains(c)).ToList();
        if (channels.Count == 0)
        {
            Console.WriteLine("No fan channels to test.");
            return 0;
        }

        Guard.WarnAboutOtherFanTools();
        var first = session.Read();
        var keys = KeySensors.Detect(first);
        var guard = new Guard(session, keys, cancel);
        if (!guard.CheckNow())
        {
            Console.Error.WriteLine($"Not starting: {guard.StopReason}.");
            return 3;
        }

        var minutes = channels.Count * (steps.Count + 1) * settle.TotalSeconds / 60 / session.TimeScale;
        Console.WriteLine($"Testing {channels.Count} fan channel(s) at {string.Join(" / ", steps)} %, about {minutes:0.0} min.");
        Console.WriteLine("Fans will audibly change speed one at a time. Best at idle. Ctrl+C stops and restores.");
        Console.WriteLine();

        var report = new StringBuilder();
        var headers = new List<FanHeader>();
        foreach (var channel in channels)
        {
            var rpmByStep = new List<(float Percent, Snapshot Snapshot)>();
            string? heldBack = null;
            Console.Write($"{channel} ");
            try
            {
                foreach (var percent in steps)
                {
                    session.SetPercent(channel, percent);
                    if (!guard.Wait(settle))
                        break;
                    rpmByStep.Add((percent, guard.Last!));
                    Console.Write(".");

                    if (percent != steps[^1] && WhyNotLower(rpmByStep, first) is { } why)
                    {
                        heldBack = why;
                        break;
                    }
                }
            }
            finally
            {
                session.RestoreDefault(channel);
            }
            Console.WriteLine();

            if (guard.Stopped)
                break;

            var ascending = rpmByStep.OrderBy(s => s.Percent).ToList();
            headers.Add(ToHeader(channel, ascending, first));

            string result = Describe(channel, ascending, first, heldBack);
            report.Append(result);
            Console.Write(result);

            // let the fan spin back to its BIOS speed before the next one, so they don't mix up
            if (!guard.Wait(settle))
                break;
        }

        if (guard.StopReason is { } reason)
            Console.WriteLine($"{reason}. All fans handed back to the BIOS.");

        if (options.Get("--out") is { } path)
        {
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            Console.WriteLine($"Result written to {Path.GetFullPath(path)}");
        }

        if (!guard.Stopped)
        {
            var inventory = new FanInventory(DateTimeOffset.Now, headers);
            found?.Invoke(inventory);
            if (options.Get("--inventory") is { } inventoryPath)
                inventory.Save(inventoryPath);

            var usable = inventory.Usable.ToList();
            Console.WriteLine();
            Console.WriteLine($"Fans found on {usable.Count} of {headers.Count} outputs: {string.Join(", ", usable.Select(h => $"#{h.Channel} {h.DisplayName}"))}.");
            var empty = headers.Where(h => !h.Connected).ToList();
            if (empty.Count > 0)
                Console.WriteLine($"Nothing on {string.Join(", ", empty.Select(h => $"#{h.Channel}"))}: those are ignored from now on.");
        }

        return guard.Stopped ? 3 : 0;
    }

    /// <summary>Steps in ascending order of speed.</summary>
    private static string Describe(FanChannel channel, List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline, string? heldBack)
    {
        var text = new StringBuilder();
        if (steps.Count < 2)
            return $"   {channel}: not enough steps measured.\n\n";

        var fans = Reacting(steps, baseline);

        string header = string.Join("  ", steps.Select(s => $"{s.Percent,5:0}%"));
        text.AppendLine($"   {channel}");
        if (heldBack is not null)
            text.AppendLine($"      not taken below {steps[0].Percent:0} %: {heldBack}");

        if (fans.Count == 0)
        {
            text.AppendLine("      nothing reacted: empty header, fan without RPM signal, or a pump fixed in the BIOS");
            text.AppendLine();
            return text.ToString();
        }

        text.AppendLine($"      {"",-40} {header}");
        foreach (var (fan, rpm) in fans)
        {
            string values = string.Join("  ", rpm.Select(r => $"{r,6:0}"));
            text.AppendLine($"      {fan.Hardware + " / " + fan.Name,-40} {values}  rpm   {Hints(rpm, steps)}");
        }
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>The fan sensor that followed the channel best, and how fast it turned at each step.</summary>
    private static FanHeader ToHeader(FanChannel channel, List<(float Percent, Snapshot Snapshot)> ascending, Snapshot baseline)
    {
        var fan = ascending.Count >= 2 ? Reacting(ascending, baseline).FirstOrDefault() : default;
        var rpm = fan.Fan is null ? [] : ascending.Select((s, i) => new RpmPoint(s.Percent, fan.Rpm[i])).ToList();
        return new FanHeader(channel.Index, channel.Id, channel.Name, channel.Hardware, fan.Fan?.Id, rpm, fan.Fan is not null && LooksLikePump(fan.Rpm));
    }

    /// <summary>Fan sensors whose RPM followed the channel, biggest reaction first.</summary>
    private static List<(SensorReading Fan, List<float> Rpm)> Reacting(List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline) =>
        baseline.OfKind(SensorKind.Fan)
            .Select(fan => (Fan: fan, Rpm: steps.Select(s => s.Snapshot.Value(fan.Id) ?? 0).ToList()))
            .Where(x => x.Rpm.Max() - x.Rpm.Min() >= SetCommand.ReactionRpm)
            .OrderByDescending(x => x.Rpm.Max() - x.Rpm.Min())
            .ToList();

    /// <summary>Once two steps are measured: a reason not to take the channel any lower, or null.</summary>
    private static string? WhyNotLower(List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline)
    {
        if (steps.Count < 2)
            return null;

        var fans = Reacting(steps, baseline);
        if (fans.Count == 0)
            return "nothing reacted so far (could be a pump without RPM signal)";
        if (fans.Any(f => LooksLikePump(f.Rpm)))
            return "looks like a pump";
        return null;
    }

    private static bool LooksLikePump(List<float> rpm)
    {
        float high = rpm.Max(), low = rpm.Min();
        return high >= PumpMinRpm && low > 0 && (high - low) / high <= PumpMaxRelativeChange;
    }

    private static string Hints(List<float> rpm, List<(float Percent, Snapshot Snapshot)> steps)
    {
        var hints = new List<string>();

        if (rpm[0] < 50)
            hints.Add($"stops at {steps[0].Percent:0} %");

        if (LooksLikePump(rpm))
            hints.Add("PUMP? speed barely changes: never run it low");

        return string.Join(", ", hints);
    }
}
