using System.Globalization;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

internal static class SetCommand
{
    public const float LowPercentNeedsForce = 25;

    private const double MaxSeconds = 600;

    // A fan counts as "reacted" when its RPM changed by at least this much.
    public const float ReactionRpm = 150;

    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--force");
        var positional = options.Positional;
        if (positional.Count != 2)
        {
            Console.Error.WriteLine("Usage: set <channel> <percent> [--seconds 60] [--force]");
            return 2;
        }

        var channels = Options.ParseChannels(session, positional[0]);
        if (channels.Count != 1)
        {
            Console.Error.WriteLine("set takes exactly one channel; use sweep for several.");
            return 2;
        }
        var channel = channels[0];

        if (!float.TryParse(positional[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
        {
            Console.Error.WriteLine($"\"{positional[1]}\" is not a percentage.");
            return 2;
        }

        if (!CheckLowPercent([percent], options))
            return 2;

        double seconds = Math.Clamp(options.GetDouble("--seconds", 60), 5, MaxSeconds);

        Guard.WarnAboutOtherFanTools();
        var before = session.Read();
        var status = new StatusLine(before, session.Channels);
        var guard = new Guard(session, status.Keys, cancel);

        if (!guard.CheckNow())
        {
            Console.Error.WriteLine($"Not starting: {guard.StopReason}.");
            return 3;
        }

        float applied = session.SetPercent(channel, percent);
        Console.WriteLine($"{channel} → {applied:0} % for {seconds:0} s. Ctrl+C hands it back to the BIOS early.");
        Console.WriteLine(status.Header());

        try
        {
            guard.Wait(TimeSpan.FromSeconds(seconds), s => Console.WriteLine(status.Render(s)));
        }
        finally
        {
            session.RestoreDefault(channel);
        }

        if (guard.StopReason is { } reason)
            Console.WriteLine(reason + ".");
        Console.WriteLine($"{channel} handed back to BIOS/driver control.");

        ReportReactions(before, guard.Last ?? before);
        return guard.StopReason?.StartsWith("SAFETY", StringComparison.Ordinal) == true ? 3 : 0;
    }

    /// <summary>Very low speeds could stop a pump; they need an explicit --force.</summary>
    public static bool CheckLowPercent(IEnumerable<float> percents, Options options)
    {
        if (options.Has("--force") || percents.All(p => p >= LowPercentNeedsForce))
            return true;

        Console.Error.WriteLine($"Below {LowPercentNeedsForce:0} % needs --force: if a pump or a fan that stalls sits on this header, it could stop.");
        return false;
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
