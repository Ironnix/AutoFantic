using System.Globalization;
using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

internal static class SetCommand
{
    private const float LowPercentNeedsForce = 25;
    private const double MaxSeconds = 600;

    // A fan counts as "reacted" when its RPM changed by at least this much.
    private const float ReactionRpm = 150;

    public static int Run(HardwareSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--force");
        var positional = options.Positional;
        if (positional.Count != 2)
        {
            Console.Error.WriteLine("Usage: set <channel> <percent> [--seconds 60] [--force]");
            return 2;
        }

        var channel = Resolve(session, positional[0]);
        if (channel is null)
        {
            Console.Error.WriteLine($"No fan channel \"{positional[0]}\". Run \"list\" to see them.");
            return 2;
        }

        if (!float.TryParse(positional[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
        {
            Console.Error.WriteLine($"\"{positional[1]}\" is not a percentage.");
            return 2;
        }

        if (percent < LowPercentNeedsForce && !options.Has("--force"))
        {
            Console.Error.WriteLine($"Below {LowPercentNeedsForce:0} % needs --force: if a pump or a fan that stalls sits on this header, it could stop.");
            return 2;
        }

        double seconds = Math.Clamp(options.GetDouble("--seconds", 60), 5, MaxSeconds);
        var limits = new SafetyLimits();

        var before = session.Read();
        var status = new StatusLine(before, session.Channels);

        string? violation = limits.Check(before, status.Keys);
        if (violation is not null)
        {
            Console.Error.WriteLine($"Not starting: {violation}.");
            return 3;
        }

        float applied = session.SetPercent(channel, percent);
        Console.WriteLine($"{channel} → {applied:0} % for {seconds:0} s. Ctrl+C hands it back to the BIOS early.");
        Console.WriteLine(status.Header());

        var end = DateTimeOffset.Now.AddSeconds(seconds);
        Snapshot last = before;
        int exitCode = 0;

        try
        {
            while (!cancel.IsCancellationRequested && DateTimeOffset.Now < end)
            {
                cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                last = session.Read();
                Console.WriteLine(status.Render(last));

                violation = limits.Check(last, status.Keys);
                if (violation is not null)
                {
                    Console.WriteLine($"SAFETY STOP: {violation}.");
                    exitCode = 3;
                    break;
                }
            }
        }
        finally
        {
            session.RestoreDefault(channel);
            Console.WriteLine($"{channel} handed back to BIOS/driver control.");
        }

        ReportReactions(before, last);
        return exitCode;
    }

    private static FanChannel? Resolve(HardwareSession session, string text)
    {
        string trimmed = text.TrimStart('#');
        if (int.TryParse(trimmed, out int index))
            return session.Channels.FirstOrDefault(c => c.Index == index);
        return session.Channels.FirstOrDefault(c => c.Id == text);
    }

    /// <summary>Which fans changed speed while the channel was held: this maps a control to its RPM sensor.</summary>
    private static void ReportReactions(Snapshot before, Snapshot after)
    {
        var reacted = before.OfKind(SensorKind.Fan)
            .Select(b => (Fan: b, Before: b.Value ?? 0, After: after.Value(b.Id) ?? 0))
            .Where(x => Math.Abs(x.After - x.Before) >= ReactionRpm)
            .ToList();

        if (reacted.Count == 0)
        {
            Console.WriteLine("No fan RPM changed noticeably: empty header, fan without tach signal, or already at that speed.");
            return;
        }

        Console.WriteLine("Fans that reacted:");
        foreach (var (fan, b, a) in reacted)
            Console.WriteLine($"   {fan.Hardware} / {fan.Name}: {b:0} → {a:0} rpm   {fan.Id}");
    }
}
