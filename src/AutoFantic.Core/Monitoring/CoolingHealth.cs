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

    /// <summary>The steady minutes between two times, from the history: only minutes where AutoFantic drove every fan and the power stayed about the same.</summary>
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
                // a fan that changed a lot within the minute, or wasn't AutoFantic's (BIOS): not a clean minute
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
    /// The change against the first week after the calibration (the model is never exact, so the
    /// baseline is how this PC measured when it was clean), from the days worked out so far. Null
    /// until there is a baseline week and a later week.
    /// </summary>
    public static (HealthResult Baseline, HealthResult Now, double? CpuChange, double? GpuChange)? Trend(IReadOnlyList<HealthDay> days, DateTimeOffset calibration, DateOnly today)
    {
        var since = days.Where(d => d.Calibration == calibration).OrderBy(d => d.Day).ToList();
        if (since.Count == 0)
            return null;
        var first = since[0].Day;
        var baseline = Combine(since.Where(d => d.Day < first.AddDays(7)));
        var now = Combine(since.Where(d => d.Day > today.AddDays(-7) && d.Day >= first.AddDays(7)));
        if (baseline is null || now is null)
            return null;
        return (baseline, now, now.CpuExtra - baseline.CpuExtra, now.GpuExtra - baseline.GpuExtra);
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
