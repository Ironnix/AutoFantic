using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Monitoring;

/// <summary>
/// How the cooling does compared with the calibration, from a stretch of history.
/// </summary>
/// <param name="Minutes">Steady minutes it rests on.</param>
/// <param name="RoomShift">°C the room is warmer (or colder) than the room temperature given at the calibration.</param>
/// <param name="CpuExtra">°C the CPU runs warmer than the calibration expects at its full load (null: not enough CPU load to tell).</param>
/// <param name="GpuExtra">The same for the GPU.</param>
public sealed record HealthResult(int Minutes, double RoomShift, double? CpuExtra, double? GpuExtra)
{
    /// <summary>The larger of the two, for a one-line verdict.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? WorstExtra => CpuExtra is null && GpuExtra is null ? null : Math.Max(CpuExtra ?? double.MinValue, GpuExtra ?? double.MinValue);
}

/// <summary>One day of cooling health against the first week after its calibration (see <see cref="CoolingHealth.SinceCalibration"/>).</summary>
/// <param name="Cpu">°C the CPU runs warmer at full load than in that first week (null: not enough CPU load that day).</param>
/// <param name="RoomShift">°C the room was warmer than at the calibration.</param>
public sealed record DayChange(DateOnly Day, DateTimeOffset Calibration, double? Cpu, double? Gpu, double RoomShift, int Minutes);

/// <summary>The last 7 days against an earlier week: °C warmer at full load, and that as a share of how far the part heats up above the room.</summary>
/// <param name="CpuPercent">How much worse the CPU's cooling works, in % (see <see cref="CoolingHealth.Rise"/>); negative = better.</param>
public sealed record HealthComparison(string Label, double? Cpu, double? Gpu, double? CpuPercent, double? GpuPercent);

/// <summary>
/// "Is the cooling still as good as at the calibration?" For every steady minute of the history
/// the calibration's model says what the CPU and the GPU should be at that power and those fan
/// speeds; the difference to what they really were is split into two parts:
///
///   difference = room shift + extra °C per watt × power
///
/// A warmer room adds the same few degrees to both, at any load. Dust in the filters and the
/// heatsinks, or old thermal paste, makes the heat harder to get out: that grows with the power, so
/// it shows most at full load. The room shift is the same for CPU and GPU (one room); the extra per
/// watt is worked out for each. Pure computation: the window and the tests use the same code.
/// </summary>
public static class CoolingHealth
{
    /// <summary>Fewer steady minutes than this and there's no answer yet.</summary>
    public const int MinMinutes = 60;

    /// <summary>A part needs this many minutes at a real load (at least a third of its full power) before its extra is told.</summary>
    public const int MinLoadedMinutes = 20;

    /// <summary>Up to this much warmer at full load is normal wobble (the model fits within about 2 °C).</summary>
    public const double Fine = 3;

    /// <summary>From this much warmer at full load it's worth cleaning.</summary>
    public const double Clean = 6;

    /// <summary>One minute of history: temperatures, power, and each calibrated group's fan speed (0 = off).</summary>
    public sealed record Minute(double CpuTemp, double GpuTemp, double CpuPower, double GpuPower, IReadOnlyList<double> Speeds);

    /// <summary>
    /// The steady minutes between two times, from the history: only minutes where every fan's speed
    /// is known (set by AuFantic, or read back from a fan the user gave to the BIOS) and the power
    /// stayed about the same.
    /// </summary>
    public static IReadOnlyList<Minute> Minutes(HistoryStore store, CalibrationResult calibration, DateTimeOffset from, DateTimeOffset to)
    {
        int count = (int)Math.Max(1, (to - from).TotalMinutes) + 2;
        Dictionary<DateTimeOffset, HistoryPoint> Get(string key) =>
            store.Query(key, from, to, maxPoints: count, minSeconds: 60).ToDictionary(p => p.Time);

        var cpuT = Get(HistoryRecorder.CpuTemp.Key);
        var gpuT = Get(HistoryRecorder.GpuTemp.Key);
        var cpuW = Get(HistoryRecorder.CpuPower.Key);
        var gpuW = Get(HistoryRecorder.GpuPower.Key);
        var fans = calibration.Groups.Select(g => Get($"fan.{MeasurementStore.Key(g.ControlIds)}.percent")).ToList();

        var minutes = new List<Minute>();
        foreach (var (time, cpu) in cpuT)
        {
            if (!gpuT.TryGetValue(time, out var gpu) || !cpuW.TryGetValue(time, out var cw) || !gpuW.TryGetValue(time, out var gw))
                continue;
            if (!Steady(cw) || !Steady(gw))
                continue;
            var speeds = new List<double>();
            foreach (var fan in fans)
            {
                // a fan that changed a lot within the minute, or wasn't AuFantic's (BIOS): not a clean minute
                if (!fan.TryGetValue(time, out var f) || f.Max - f.Min > 10)
                    break;
                speeds.Add(f.Avg);
            }
            if (speeds.Count == fans.Count)
                minutes.Add(new Minute(cpu.Avg, gpu.Avg, cw.Avg, gw.Avg, speeds));
        }
        return minutes;
    }

    // the power stayed about the same within the minute, so the temperatures had time to follow
    private static bool Steady(HistoryPoint power) => power.Max - power.Min <= 0.25 * power.Avg + 10;

    /// <summary>The answer for these minutes; null if there are too few.</summary>
    public static HealthResult? Analyze(CalibrationResult calibration, IReadOnlyList<Minute> minutes)
    {
        if (minutes.Count < MinMinutes || !calibration.Model.ContainsKey(Component.Cpu) || !calibration.Model.ContainsKey(Component.GpuCore))
            return null;
        var model = ThermalModel.FromCoefficients(calibration.Ambient, calibration.Groups.Count, calibration.Model, calibration.Rms);
        var (topCpu, topGpu) = FullLoad(calibration);

        // rows: (1, cpu power, 0) → CPU difference, (1, 0, gpu power) → GPU difference; least squares for
        // (room shift, CPU extra per W, GPU extra per W), gently held at 0 extra where there's little load
        var ata = new double[3, 3];
        var atb = new double[3];
        void Row(double x1, double x2, double y)
        {
            double[] x = [1, x1, x2];
            for (int i = 0; i < 3; i++)
            {
                atb[i] += x[i] * y;
                for (int j = 0; j < 3; j++)
                    ata[i, j] += x[i] * x[j];
            }
        }
        int cpuLoaded = 0, gpuLoaded = 0;
        foreach (var m in minutes)
        {
            Row(m.CpuPower, 0, m.CpuTemp - model.Predict(Component.Cpu, m.Speeds, m.CpuPower, m.GpuPower));
            Row(0, m.GpuPower, m.GpuTemp - model.Predict(Component.GpuCore, m.Speeds, m.CpuPower, m.GpuPower));
            if (m.CpuPower >= topCpu / 3)
                cpuLoaded++;
            if (m.GpuPower >= topGpu / 3)
                gpuLoaded++;
        }
        double hold = minutes.Count * 25; // like a few dozen minutes at 5 W saying "no extra"
        ata[1, 1] += hold;
        ata[2, 2] += hold;

        var beta = Solve(ata, atb);
        if (beta is null)
            return null;
        return new HealthResult(minutes.Count, beta[0],
            cpuLoaded >= MinLoadedMinutes ? beta[1] * topCpu : null,
            gpuLoaded >= MinLoadedMinutes ? beta[2] * topGpu : null);
    }

    /// <summary>The heaviest load the calibration measured (its "high" level): where "extra at full load" is told.</summary>
    public static (double Cpu, double Gpu) FullLoad(CalibrationResult calibration) =>
        calibration.Table.FirstOrDefault(r => r.Label == "high") is { } high ? (high.CpuPower, high.GpuPower) : (100, 250);

    /// <summary>
    /// °C above the room CPU and GPU reached at full load when calibrated (the "high" level): what
    /// "warmer" is a percentage of. 2 °C more on a rise of 50 °C means the cooling gets the same heat
    /// out 4 % worse. Null for a part the calibration has no full-load temperature for.
    /// </summary>
    public static (double? Cpu, double? Gpu) Rise(CalibrationResult calibration)
    {
        var high = calibration.Table.FirstOrDefault(r => r.Label == "high");
        double? Of(Component part) =>
            high is not null && high.Temperatures.TryGetValue(part, out double t) && t - calibration.Ambient > 5 ? t - calibration.Ambient : null;
        return (Of(Component.Cpu), Of(Component.GpuCore));
    }

    /// <summary>
    /// Every day compared with the first week after the calibration it was measured against: °C
    /// warmer at full load (0 = as good as then). A new calibration starts again at 0, so after
    /// cleaning and calibrating again the line drops back; over a year it looks like a saw, one
    /// tooth per calibration. The room stays as measured (against the calibration's room).
    /// </summary>
    public static IReadOnlyList<DayChange> SinceCalibration(IEnumerable<HealthDay> days)
    {
        var changes = new List<DayChange>();
        foreach (var calibration in days.Where(d => d.Result.Minutes > 0).GroupBy(d => d.Calibration))
        {
            var list = calibration.OrderBy(d => d.Day).ToList();
            double? cpu = FirstWeek(list, r => r.CpuExtra), gpu = FirstWeek(list, r => r.GpuExtra);
            changes.AddRange(list.Select(d => new DayChange(d.Day, d.Calibration, d.Result.CpuExtra - cpu, d.Result.GpuExtra - gpu, d.Result.RoomShift, d.Result.Minutes)));
        }
        return [.. changes.OrderBy(c => c.Day)];
    }

    // the first 7 days that know the value, weighted by their minutes: this PC's "normal" right after the calibration
    private static double? FirstWeek(IReadOnlyList<HealthDay> days, Func<HealthResult, double?> pick)
    {
        var known = days.Where(d => pick(d.Result) is not null).ToList();
        if (known.Count == 0)
            return null;
        var week = known.Where(d => d.Day < known[0].Day.AddDays(7)).ToList();
        return week.Sum(d => pick(d.Result)!.Value * d.Result.Minutes) / week.Sum(d => d.Result.Minutes);
    }

    /// <summary>The earlier weeks the last 7 days are compared with, and how far back each is.</summary>
    private static readonly (string Label, int Days)[] Earlier = [("A week ago", 7), ("4 weeks ago", 28), ("3 months ago", 91), ("A year ago", 364)];

    public const string AfterCalibration = "The first week after the calibration";

    /// <summary>
    /// The last 7 days against earlier weeks, all measured against the calibration in use (days of an
    /// older one aren't comparable: its model was another): a week, 4 weeks, 3 months and a year ago,
    /// as far as there are days, and the first week after the calibration. Empty without a last week.
    /// </summary>
    public static IReadOnlyList<HealthComparison> Compare(IReadOnlyList<DayChange> changes, DateTimeOffset calibration, DateOnly today, (double? Cpu, double? Gpu) rise)
    {
        var mine = changes.Where(c => c.Calibration == calibration).ToList();
        if (Week(mine, today.AddDays(-7), today) is not { } now)
            return [];

        HealthComparison Against(string label, double? cpu, double? gpu)
        {
            double? cpuChange = now.Cpu - cpu, gpuChange = now.Gpu - gpu;
            return new HealthComparison(label, cpuChange, gpuChange, cpuChange / rise.Cpu * 100, gpuChange / rise.Gpu * 100);
        }
        var list = new List<HealthComparison>();
        foreach (var (label, back) in Earlier)
            if (Week(mine, today.AddDays(-7 - back), today.AddDays(-back)) is { } then)
                list.Add(Against(label, then.Cpu, then.Gpu));
        // the first week is 0 by definition; told once the last 7 days are all after it
        if (mine.Count > 0 && mine[0].Day.AddDays(7) <= today.AddDays(-6))
            list.Add(Against(AfterCalibration, 0, 0));
        return list;
    }

    // the days in (from, to] as one, weighted by their minutes; null if there's none
    private static (double? Cpu, double? Gpu)? Week(IReadOnlyList<DayChange> changes, DateOnly from, DateOnly to)
    {
        var days = changes.Where(c => c.Day > from && c.Day <= to).ToList();
        if (days.Count == 0)
            return null;
        double? Mean(Func<DayChange, double?> pick)
        {
            var known = days.Where(d => pick(d) is not null).ToList();
            return known.Count == 0 ? null : known.Sum(d => pick(d)!.Value * d.Minutes) / known.Sum(d => d.Minutes);
        }
        return (Mean(d => d.Cpu), Mean(d => d.Gpu));
    }

    /// <summary>
    /// For a chart: one point per day, or with <paramref name="weekly"/> one per week (Monday to
    /// Sunday, in its middle: the average weighted by minutes, with the lowest and the highest day).
    /// </summary>
    public static IReadOnlyList<HistoryPoint> Points(IEnumerable<DayChange> changes, Func<DayChange, double?> pick, bool weekly)
    {
        var known = changes.Where(c => pick(c) is not null).ToList();
        static DateTimeOffset At(DateOnly day, double hours) => new(day.ToDateTime(TimeOnly.MinValue).AddHours(hours));
        if (!weekly)
            return [.. known.Select(c => new HistoryPoint(At(c.Day, 12), pick(c)!.Value, pick(c)!.Value, pick(c)!.Value))];
        return [.. known.GroupBy(c => c.Day.DayNumber / 7) // day 0 (1 Jan 0001) was a Monday
            .Select(week => new HistoryPoint(At(DateOnly.FromDayNumber(week.Key * 7), 3.5 * 24),
                week.Sum(c => pick(c)!.Value * c.Minutes) / week.Sum(c => c.Minutes), week.Min(c => pick(c)!.Value), week.Max(c => pick(c)!.Value)))];
    }

    /// <summary>Several days as one: weighted by their minutes.</summary>
    public static HealthResult? Combine(IEnumerable<HealthDay> days)
    {
        var list = days.Select(d => d.Result).Where(r => r.Minutes > 0).ToList();
        if (list.Count == 0)
            return null;
        double total = list.Sum(r => r.Minutes);
        double? Mean(Func<HealthResult, double?> pick)
        {
            var known = list.Where(r => pick(r) is not null).ToList();
            return known.Count == 0 ? null : known.Sum(r => pick(r)!.Value * r.Minutes) / known.Sum(r => r.Minutes);
        }
        return new HealthResult((int)total, list.Sum(r => r.RoomShift * r.Minutes) / total, Mean(r => r.CpuExtra), Mean(r => r.GpuExtra));
    }

    // Gaussian elimination with pivoting; null if singular
    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(m[r, col]));
            if (Math.Abs(m[pivot, col]) < 1e-12)
                return null;
            for (int j = 0; j <= n; j++)
                (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (int r = 0; r < n; r++)
            {
                if (r == col)
                    continue;
                double f = m[r, col] / m[col, col];
                for (int j = col; j <= n; j++)
                    m[r, j] -= f * m[col, j];
            }
        }
        return Enumerable.Range(0, n).Select(i => m[i, n] / m[i, i]).ToArray();
    }
}
