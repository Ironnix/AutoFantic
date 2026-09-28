using System.Text;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// Runs every fan channel through a few speeds, one after another, and records which RPM sensor
/// follows it. Answers: which control drives which fan, which headers are empty, which fans
/// stop at low speed, and which channel looks like a pump.
/// </summary>
internal static class DiscoverCommand
{
    private static readonly float[] DefaultSteps = [30, 60, 100];

    // A pump runs fast and hardly changes with duty cycle.
    private const float PumpMinRpm = 1800;
    private const float PumpMaxRelativeChange = 0.25f;

    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--force");
        var steps = options.GetNumbers("--steps", DefaultSteps).OrderBy(p => p).ToList();
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

        var minutes = channels.Count * (steps.Count + 1) * settle.TotalSeconds / 60;
        Console.WriteLine($"Testing {channels.Count} fan channel(s) at {string.Join(" / ", steps)} %, about {minutes:0.0} min.");
        Console.WriteLine("Fans will audibly change speed one at a time. Best at idle. Ctrl+C stops and restores.");
        Console.WriteLine();

        var report = new StringBuilder();
        foreach (var channel in channels)
        {
            var rpmByStep = new List<(float Percent, Snapshot Snapshot)>();
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
                }
            }
            finally
            {
                session.RestoreDefault(channel);
            }
            Console.WriteLine();

            if (guard.Stopped)
                break;

            string result = Describe(channel, rpmByStep, first);
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

        return guard.Stopped ? 3 : 0;
    }

    private static string Describe(FanChannel channel, List<(float Percent, Snapshot Snapshot)> steps, Snapshot baseline)
    {
        var text = new StringBuilder();
        if (steps.Count < 2)
            return $"   {channel}: not enough steps measured.\n\n";

        var fans = baseline.OfKind(SensorKind.Fan)
            .Select(fan => (Fan: fan, Rpm: steps.Select(s => s.Snapshot.Value(fan.Id) ?? 0).ToList()))
            .Where(x => x.Rpm.Max() - x.Rpm.Min() >= SetCommand.ReactionRpm)
            .OrderByDescending(x => x.Rpm.Max() - x.Rpm.Min())
            .ToList();

        string header = string.Join("  ", steps.Select(s => $"{s.Percent,5:0}%"));
        text.AppendLine($"   {channel}");

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

    private static string Hints(List<float> rpm, List<(float Percent, Snapshot Snapshot)> steps)
    {
        var hints = new List<string>();

        if (rpm[0] < 50)
            hints.Add($"stops at {steps[0].Percent:0} %");

        float low = rpm[0], high = rpm[^1];
        if (high >= PumpMinRpm && low > 0 && (high - low) / high <= PumpMaxRelativeChange)
            hints.Add("PUMP? speed barely changes: never run it low");

        return string.Join(", ", hints);
    }
}
