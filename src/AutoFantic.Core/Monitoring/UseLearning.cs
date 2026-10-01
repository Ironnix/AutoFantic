using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Monitoring;

/// <summary>How one fan group's curve would change: the curve in use now and the one everyday use suggests.</summary>
/// <param name="Largest">The biggest difference between the two in % of fan speed (new minus now: negative = slower and quieter) …</param>
/// <param name="At">… and the temperature it is at.</param>
/// <param name="OffAtIdleNow">The calibration switches the fans off at idle now …</param>
/// <param name="OffAtIdleNew">… and with the new curves.</param>
public sealed record CurveChange(string Name, Component Follows, IReadOnlyList<CurvePoint> Now, IReadOnlyList<CurvePoint> New,
    double Largest, double At, bool OffAtIdleNow, bool OffAtIdleNew);

/// <summary>One minute of everyday use: the temperatures, the power, and each calibrated group's fan speed.</summary>
public sealed record UseMinute(DateTimeOffset Time, double CpuTemp, double GpuTemp, double CpuPower, double GpuPower, IReadOnlyList<double> Speeds);

/// <summary>What <see cref="UseLearning.Analyze"/> found.</summary>
/// <param name="Correction">Null: not enough use yet to tell anything.</param>
/// <param name="Minutes">Settled minutes since <paramref name="From"/> (<see cref="UseLearning.MinMinutes"/> are needed).</param>
public sealed record UseAnalysis(UseCorrection? Correction, int Minutes, DateTimeOffset From);

/// <summary>
/// "Improve from everyday use": the history knows for every minute how warm CPU and GPU were, at
/// which power and which fan speeds. A calibration is a quarter of an hour under one load; the
/// weeks after it are the PC as it is really used. From them this works out two things and
/// nothing else, so the curves only change in ways that can be explained:
///
///   how warm      °C the CPU and the GPU really run warmer or cooler than the calibrated model
///                 expects at that power and those fan speeds, as a straight line over the power
///                 (<see cref="PartUse"/>). A calibration measures at one heavy load; a browser, a
///                 light game or another room are other points on that line.
///   the loads     the highest power the PC really ran at, if that is more than any calibration saw
///
/// Only minutes count in which every fan turned (the model describes moving air, not a fan that
/// stands still) and the power had been about the same for a few minutes (the temperatures had
/// followed). What each fan does (how much faster cools how much) stays as the calibrations
/// measured it: in everyday use the fans follow the temperature, so the history can't tell a fan's
/// effect apart from the load that made it speed up. Only a calibration, which sets the fans on
/// purpose, can. The result is a <see cref="UseCorrection"/>; the curves are worked out again with
/// it by the same code as after a calibration.
/// </summary>
public static class UseLearning
{
    /// <summary>Fewer settled minutes than this and there's no answer yet.</summary>
    public const int MinMinutes = 60;

    /// <summary>As long as minute values are kept.</summary>
    public static readonly TimeSpan LooksBack = TimeSpan.FromDays(30);

    /// <summary>A minute counts once the power was about the same for this many minutes before it: then the temperatures have followed.</summary>
    public const int SettledAfter = 3;

    /// <summary>
    /// Less than this is what the measurement can't tell apart from "as calibrated" (the model fits
    /// within about 2 °C), and counts as no difference: 2 °C anywhere in the range seen, 5 % more
    /// power. Without it the curves would wobble with every analysis.
    /// </summary>
    public const double SameExtra = 2, SameLoad = 1.05;

    /// <summary>The power has to vary by at least this share of the calibrations' highest for a slope to be told; otherwise it's one offset.</summary>
    public const double MinSpread = 0.15;

    /// <summary>Curves that differ by less than this many % of fan speed are the same curve.</summary>
    public const double Unchanged = 3;

    // a fan turns: its slowest reading in the minute is above a standstill
    private const double TurningRpm = 50;

    /// <summary>From where the history counts: since the latest calibration, at most <see cref="LooksBack"/>.</summary>
    public static DateTimeOffset From(DateTimeOffset now, DateTimeOffset calibration) =>
        calibration > now - LooksBack ? calibration : now - LooksBack;

    /// <summary>
    /// The minutes between two times in which every calibrated fan group turned at a known speed
    /// that hardly changed within the minute.
    /// </summary>
    public static IReadOnlyList<UseMinute> Minutes(HistoryStore store, CalibrationResult calibration, DateTimeOffset from, DateTimeOffset to)
    {
        int count = (int)Math.Max(1, (to - from).TotalMinutes) + 2;
        Dictionary<DateTimeOffset, HistoryPoint> Get(string key) =>
            store.Query(key, from, to, maxPoints: count, minSeconds: 60).ToDictionary(p => p.Time);

        var cpuT = Get(HistoryRecorder.CpuTemp.Key);
        var gpuT = Get(HistoryRecorder.GpuTemp.Key);
        var cpuW = Get(HistoryRecorder.CpuPower.Key);
        var gpuW = Get(HistoryRecorder.GpuPower.Key);
        var fans = calibration.Groups
            .Select(g => $"fan.{MeasurementStore.Key(g.ControlIds)}")
            .Select(fan => (Percent: Get($"{fan}.percent"), Rpm: Get($"{fan}.rpm")))
            .ToList();

        var minutes = new List<UseMinute>();
        foreach (var (time, cpu) in cpuT)
        {
            if (!gpuT.TryGetValue(time, out var gpu) || !cpuW.TryGetValue(time, out var cw) || !gpuW.TryGetValue(time, out var gw))
                continue;
            var speeds = new List<double>();
            foreach (var (percent, rpm) in fans)
            {
                if (!percent.TryGetValue(time, out var p) || p.Max - p.Min > 20 || !rpm.TryGetValue(time, out var r) || r.Min < TurningRpm)
                    break;
                speeds.Add(p.Avg);
            }
            if (speeds.Count == fans.Count)
                minutes.Add(new UseMinute(time, cpu.Avg, gpu.Avg, cw.Avg, gw.Avg, speeds));
        }
        return minutes;
    }

    /// <summary>
    /// The minutes in which the temperatures had time to follow: the minutes right before had about
    /// the same power and fan speeds. Leaves out the first minutes of a game (still warming up: it
    /// would look like better cooling) and the first after it.
    /// </summary>
    public static IReadOnlyList<UseMinute> Settled(IReadOnlyList<UseMinute> minutes)
    {
        var at = minutes.GroupBy(m => m.Time).ToDictionary(g => g.Key, g => g.First());
        static bool Close(double a, double b) => Math.Abs(a - b) <= 0.2 * Math.Max(a, b) + 10;
        return [.. minutes
            .Where(m => Enumerable.Range(1, SettledAfter).All(back =>
                at.TryGetValue(m.Time.AddMinutes(-back), out var before)
                && Close(before.CpuPower, m.CpuPower) && Close(before.GpuPower, m.GpuPower)
                && before.Speeds.Zip(m.Speeds, (a, b) => Math.Abs(a - b)).All(d => d <= 15)))
            .OrderBy(m => m.Time)];
    }

    /// <summary>
    /// What the minutes say against the calibrated model (never against an earlier correction: each
    /// analysis stands for itself and replaces the last).
    /// </summary>
    /// <param name="reference">The latest calibration's time: the correction belongs to it.</param>
    /// <param name="calibratedTop">The highest CPU and GPU power any calibration saw.</param>
    public static UseAnalysis Analyze(CalibrationResult calibration, IReadOnlyList<UseMinute> minutes, DateTimeOffset reference,
        DateTimeOffset from, DateTimeOffset now, (double Cpu, double Gpu) calibratedTop)
    {
        var settled = Settled(minutes);
        if (settled.Count < MinMinutes)
            return new UseAnalysis(null, settled.Count, from);

        // against what the calibrations measured, without any correction in use
        var model = ThermalModel.FromCoefficients(calibration.Ambient, calibration.Groups.Count, calibration.Model, calibration.Rms);
        PartUse? Part(Component part, Func<UseMinute, double> temp, Func<UseMinute, double> power, double calibrated)
        {
            if (!calibration.Model.ContainsKey(part))
                return null;
            var points = settled.Select(m => (Power: power(m), Extra: temp(m) - model.Predict(part, m.Speeds, m.CpuPower, m.GpuPower))).ToList();
            double from = Percentile(points.Select(p => p.Power), 0.02), to = Percentile(points.Select(p => p.Power), 0.98);
            double meanPower = points.Average(p => p.Power), meanExtra = points.Average(p => p.Extra);

            // a line through the minutes; with too little spread in the power it's the same everywhere in the range
            double spread = points.Sum(p => (p.Power - meanPower) * (p.Power - meanPower));
            double perWatt = to - from >= MinSpread * calibrated && spread > 0
                ? points.Sum(p => (p.Power - meanPower) * (p.Extra - meanExtra)) / spread
                : 0;
            var use = new PartUse(meanExtra - perWatt * meanPower, perWatt, from, to, calibrated);
            return Math.Max(Math.Abs(use.Extra(from)), Math.Abs(use.Extra(to))) < SameExtra ? null : use;
        }
        // a load counts as heavier than the calibrations' only if it clearly is; otherwise theirs stays
        static double Top(IEnumerable<double> power, double calibrated) =>
            Percentile(power, 0.98) is var seen && seen > calibrated * SameLoad ? seen : calibrated;

        return new UseAnalysis(new UseCorrection(now, reference, settled.Count,
            settled.Select(m => m.Time.ToLocalTime().Date).Distinct().Count(),
            Part(Component.Cpu, m => m.CpuTemp, m => m.CpuPower, calibratedTop.Cpu),
            Part(Component.GpuCore, m => m.GpuTemp, m => m.GpuPower, calibratedTop.Gpu),
            Top(settled.Select(m => m.CpuPower), calibratedTop.Cpu), Top(settled.Select(m => m.GpuPower), calibratedTop.Gpu)),
            settled.Count, from);
    }

    /// <summary>Each group's curve now and as suggested, and where they differ most (between 30 and 100 °C).</summary>
    public static IReadOnlyList<CurveChange> Compare(CalibrationResult now, CalibrationResult suggested) =>
        [.. now.Groups.Zip(suggested.Groups, (a, b) =>
        {
            double largest = 0, at = 30;
            for (int t = 30; t <= 100; t++)
            {
                double difference = CalibrationResult.Interpolate(b.Curve, t) - CalibrationResult.Interpolate(a.Curve, t);
                if (Math.Abs(difference) > Math.Abs(largest))
                    (largest, at) = (difference, t);
            }
            return new CurveChange(b.Name, b.Follows, a.Curve, b.Curve, largest, at, a.OffAt.Contains("idle"), b.OffAt.Contains("idle"));
        })];

    /// <summary>True if the suggestion is the curves in use (no curve differs by <see cref="Unchanged"/> % or more, the same fans off at idle).</summary>
    public static bool Same(IEnumerable<CurveChange> changes) =>
        changes.All(c => Math.Abs(c.Largest) < Unchanged && c.OffAtIdleNow == c.OffAtIdleNew);

    private static double Percentile(IEnumerable<double> values, double share)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)((sorted.Count - 1) * share)];
    }
}
