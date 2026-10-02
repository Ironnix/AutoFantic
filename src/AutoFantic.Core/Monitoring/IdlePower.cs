using AutoFantic.Core.Analysis;

namespace AutoFantic.Core.Monitoring;

/// <summary>What the graphics card draws while the PC does nothing.</summary>
/// <param name="Watts">The middle (median) of its power in the idle minutes looked at.</param>
/// <param name="Minutes">How many idle minutes that rests on.</param>
public sealed record IdlePowerFinding(double Watts, int Minutes)
{
    /// <summary>Far more than a card needs for a desktop: worth telling the user what helps.</summary>
    public bool High => Watts >= IdlePower.High;
}

/// <summary>What most likely keeps the graphics card from idling, judged by the monitors' refresh rates.</summary>
public enum IdleCause
{
    /// <summary>The monitors run at different rates: the card can't slow its memory down between their pictures.</summary>
    DifferentRates,

    /// <summary>The same rate everywhere (or one monitor), but a very high one.</summary>
    HighRate,

    /// <summary>The rates look fine, or Windows doesn't say them: something else keeps the card busy.</summary>
    Unknown,
}

/// <summary>
/// "Does the graphics card rest when the PC does?" A card draws 20 to 40 W for a desktop. With
/// monitors at different refresh rates (or one very fast one) many cards never slow their memory
/// down and draw 100 W and more while doing nothing: warmer, louder, and too warm for their fans
/// to stand still. This reads the idle power from the monitor's history, so it's known right after
/// a start and follows what the user changes. Pure computation: the window and the tests use the same code.
/// </summary>
public static class IdlePower
{
    /// <summary>From this many watts at idle on, it's worth a hint (a healthy card stays well below, also with several monitors).</summary>
    public const double High = 60;

    /// <summary>Fewer idle minutes than this and there's no answer yet.</summary>
    public const int MinMinutes = 20;

    /// <summary>Only the latest idle minutes count, so the answer changes within the hour after the monitors were set differently.</summary>
    public const int LatestMinutes = 60;

    /// <summary>How far back idle minutes are looked for (a PC that is mostly used for games has few).</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(7);

    /// <summary>A rate above this can keep a card busy all by itself (144 Hz and less usually don't).</summary>
    public const int FastAbove = 144;

    /// <summary>
    /// The graphics card's power in the latest idle minutes before <paramref name="to"/> (CPU and GPU
    /// load both low, as <see cref="LoadClassifier"/> calls idle); null if there are too few yet.
    /// </summary>
    public static IdlePowerFinding? Gpu(HistoryStore store, DateTimeOffset to)
    {
        var from = to - LookBack;
        int count = (int)LookBack.TotalMinutes + 2;
        Dictionary<DateTimeOffset, HistoryPoint> Get(Series series) =>
            store.Query(series.Key, from, to, maxPoints: count, minSeconds: 60).ToDictionary(p => p.Time);

        var cpuLoad = Get(HistoryRecorder.CpuLoad);
        var gpuLoad = Get(HistoryRecorder.GpuLoad);
        var idle = store.Query(HistoryRecorder.GpuPower.Key, from, to, maxPoints: count, minSeconds: 60)
            .Where(power => cpuLoad.TryGetValue(power.Time, out var cpu) && gpuLoad.TryGetValue(power.Time, out var gpu)
                && LoadClassifier.Classify(cpu.Avg, gpu.Avg) is LoadClass.Idle)
            .TakeLast(LatestMinutes)
            .Select(power => power.Avg)
            .Order()
            .ToList();
        if (idle.Count < MinMinutes)
            return null;
        double median = idle.Count % 2 == 1 ? idle[idle.Count / 2] : (idle[idle.Count / 2 - 1] + idle[idle.Count / 2]) / 2;
        return new IdlePowerFinding(median, idle.Count);
    }

    /// <summary>What the monitors' refresh rates (Hz, one per monitor in use) say about the cause.</summary>
    public static IdleCause Cause(IReadOnlyList<int> refreshRates)
    {
        if (refreshRates.Count == 0)
            return IdleCause.Unknown;
        // Windows gives whole numbers: 59.94 Hz is 59 on one monitor and 60 on the next
        if (refreshRates.Max() - refreshRates.Min() > 1)
            return IdleCause.DifferentRates;
        return refreshRates.Max() > FastAbove ? IdleCause.HighRate : IdleCause.Unknown;
    }
}
