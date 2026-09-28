using System.Globalization;
using System.Text;
using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// The core measurement: under a steady load, step a fan group through several speeds, wait at
/// each step until temperatures have settled, average the last window, and find the knee, the
/// speed beyond which more RPM stops paying off. Steps go from fast to slow, so temperatures
/// climb gradually and a crossed limit ends the sweep before the fans get any slower.
/// </summary>
internal static class SweepCommand
{
    private static readonly float[] DefaultSteps = [100, 80, 60, 45, 30];
    private const double DefaultAmbient = 22;

    // "looks idle" hint: a sweep without real load measures nothing useful
    private const double IdleCpuWatts = 45;
    private const double IdleGpuWatts = 60;

    // flag a step whose load differs this much from the first step
    private const double LoadChangeWarning = 0.20;

    // less spread than this from slowest to fastest step = the fans don't cool that component
    private const double NoEffectBelow = 0.03;

    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--force");
        if (options.Positional.Count != 1)
        {
            Console.Error.WriteLine("Usage: sweep <channels> [--steps 100,80,60,45,30] [--ambient 22] [--window 60] [--max-wait 420] [--csv sweep.csv] [--force]");
            return 2;
        }

        var channels = Options.ParseChannels(session, options.Positional[0]);
        var steps = options.GetNumbers("--steps", DefaultSteps).OrderByDescending(p => p).ToList();
        var window = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--window", 60), 20, 300));
        var maxWait = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--max-wait", 420), window.TotalSeconds + 10, 1800));
        bool ambientGiven = options.Get("--ambient") is not null;
        double ambient = options.GetDouble("--ambient", DefaultAmbient);

        if (!SetCommand.CheckLowPercent(steps, options))
            return 2;

        Guard.WarnAboutOtherFanTools();
        var first = session.Read();
        var status = new StatusLine(first, session.Channels);
        var keys = status.Keys;
        var guard = new Guard(session, keys, cancel);
        if (!guard.CheckNow())
        {
            Console.Error.WriteLine($"Not starting: {guard.StopReason}.");
            return 3;
        }

        PrintIntro(channels, steps, window, maxWait, ambient, ambientGiven, first, keys);

        var results = new List<SweepStep>();
        try
        {
            foreach (var percent in steps)
            {
                var step = MeasureStep(session, channels, percent, window, maxWait, guard, status, keys);
                if (step is null)
                    break;
                results.Add(step);
                Console.WriteLine($"   → {percent:0} %: {(step.Settled ? "settled" : "NOT settled (max wait reached)")} after {step.Duration:m\\:ss}");
                Console.WriteLine();
            }
        }
        finally
        {
            foreach (var channel in channels)
                session.RestoreDefault(channel);
        }

        if (guard.StopReason is { } reason)
            Console.WriteLine($"{reason}. Fans handed back to the BIOS; results so far:");
        else
            Console.WriteLine("Fans handed back to the BIOS.");
        Console.WriteLine();

        string report = Report(results, ambient);
        Console.Write(report);

        if (options.Get("--csv") is { } path)
        {
            WriteCsv(path, results, ambient);
            Console.WriteLine($"Steps written to {Path.GetFullPath(path)}");
        }

        return guard.StopReason?.StartsWith("SAFETY", StringComparison.Ordinal) == true ? 3 : 0;
    }

    private static SweepStep? MeasureStep(
        FanSession session,
        IReadOnlyList<FanChannel> channels,
        float percent,
        TimeSpan window,
        TimeSpan maxWait,
        Guard guard,
        StatusLine status,
        KeySensors keys)
    {
        foreach (var channel in channels)
            session.SetPercent(channel, percent);

        Console.WriteLine($"Step {percent:0} %: waiting for temperatures to settle (window {window.TotalSeconds:0} s, max {maxWait.TotalMinutes:0} min)");
        Console.WriteLine(status.Header());

        var cpu = new SteadyStateDetector(window);
        var gpu = new SteadyStateDetector(window);
        var samples = new SnapshotWindow(window);
        var started = DateTimeOffset.Now;
        int tick = 0;

        bool Settled() =>
            (keys.CpuTemp is null || cpu.IsSteady) && (keys.GpuTemp is null || gpu.IsSteady);

        bool completed = guard.Wait(maxWait, s =>
        {
            samples.Add(s);
            if (s.Value(keys.CpuTemp) is { } cpuTemp)
                cpu.Add(s.Time, cpuTemp, s.Value(keys.CpuPower) ?? 0);
            if (s.Value(keys.GpuTemp) is { } gpuTemp)
                gpu.Add(s.Time, gpuTemp, s.Value(keys.GpuPower) ?? 0);

            string line = status.Render(s);
            if (++tick % 5 == 0)
                Console.WriteLine(line);
        }, until: Settled);

        if (!completed)
            return null;

        return new SweepStep(
            percent,
            samples.Mean(keys.CpuTemp),
            samples.Mean(keys.CpuPower),
            samples.Mean(keys.GpuTemp),
            samples.Mean(keys.GpuHotspot),
            samples.Mean(keys.GpuMemory),
            samples.Mean(keys.GpuPower),
            Settled(),
            DateTimeOffset.Now - started);
    }

    private static void PrintIntro(
        IReadOnlyList<FanChannel> channels, List<float> steps, TimeSpan window, TimeSpan maxWait,
        double ambient, bool ambientGiven, Snapshot first, KeySensors keys)
    {
        Console.WriteLine($"Sweeping {string.Join(", ", channels)} through {string.Join(" → ", steps)} %.");
        Console.WriteLine($"Each step waits until CPU and GPU temperatures have settled (±{0.2} °C/min over {window.TotalSeconds:0} s), at most {maxWait.TotalMinutes:0} min.");
        Console.WriteLine($"Worst case about {steps.Count * maxWait.TotalMinutes:0} min. Ctrl+C stops and hands the fans back to the BIOS.");
        if (!ambientGiven)
            Console.WriteLine($"Room temperature assumed {ambient:0} °C. Pass --ambient <°C> for better numbers.");

        double cpuW = first.Value(keys.CpuPower) ?? 0, gpuW = first.Value(keys.GpuPower) ?? 0;
        if (cpuW < IdleCpuWatts && gpuW < IdleGpuWatts)
        {
            Console.WriteLine();
            Console.WriteLine($"Note: the PC looks idle (CPU {cpuW:0} W, GPU {gpuW:0} W). Start a steady load first:");
            Console.WriteLine("      a game standing still in one spot, a benchmark in a loop, or a stress test.");
        }
        Console.WriteLine();
    }

    private static string Report(List<SweepStep> steps, double ambient)
    {
        var text = new StringBuilder();
        if (steps.Count == 0)
        {
            text.AppendLine("No step completed.");
            return text.ToString();
        }

        double? cpuW0 = steps[0].CpuPower, gpuW0 = steps[0].GpuPower;

        text.AppendLine($"Results (room {ambient:0} °C, R = °C above room per watt; lower is better)");
        text.AppendLine("  fan %  CPU °C  CPU W   R CPU  GPU °C  Hot °C  Mem °C  GPU W   R GPU   R hot  settled  time   note");
        foreach (var s in steps.OrderByDescending(s => s.FanPercent))
        {
            string note = Changed(s.CpuPower, cpuW0) || Changed(s.GpuPower, gpuW0) ? "load changed" : "";
            text.AppendLine(
                $"  {s.FanPercent,5:0}  {N(s.CpuTemp),6}  {N(s.CpuPower, "0"),5}  {R(s.CpuResistance(ambient))}  " +
                $"{N(s.GpuTemp),6}  {N(s.GpuHotspot),6}  {N(s.GpuMemory),6}  {N(s.GpuPower, "0"),5}  " +
                $"{R(s.GpuResistance(ambient))}  {R(s.HotspotResistance(ambient))}  {(s.Settled ? "yes" : "NO"),7}  {s.Duration:m\\:ss}  {note}");
        }
        text.AppendLine();

        text.AppendLine("Knee (fan speed beyond which more RPM is not worth the noise):");
        AppendKnee(text, "CPU", steps, s => s.CpuResistance(ambient) ?? Rise(s.CpuTemp, ambient));
        AppendKnee(text, "GPU core", steps, s => s.GpuResistance(ambient) ?? Rise(s.GpuTemp, ambient));
        AppendKnee(text, "GPU hotspot", steps, s => s.HotspotResistance(ambient) ?? Rise(s.GpuHotspot, ambient));
        text.AppendLine("   quiet = < 5 %, balanced = < 3 %, performance = < 1.5 % improvement per +10 % fan speed");
        text.AppendLine();
        return text.ToString();
    }

    private static void AppendKnee(StringBuilder text, string label, List<SweepStep> steps, Func<SweepStep, double?> metric)
    {
        var points = steps
            .Select(s => (s.FanPercent, Value: metric(s)))
            .Where(p => p.Value.HasValue)
            .Select(p => (p.FanPercent, p.Value!.Value))
            .ToList();

        if (points.Count < 2)
        {
            text.AppendLine($"   {label,-12} not enough data");
            return;
        }

        // e.g. the CPU fan doesn't change GPU temperatures: that's "no effect", not "knee at 30 %"
        double best = points.Min(p => p.Item2), worst = points.Max(p => p.Item2);
        if ((worst - best) / worst < NoEffectBelow)
        {
            text.AppendLine($"   {label,-12} not affected by these fans (< {NoEffectBelow:0%} difference across all steps)");
            return;
        }

        string Knee(double threshold)
        {
            var result = KneeFinder.Find(points, threshold);
            return result.KneePercent is not { } k ? "–"
                : result.StillImprovingAtMax ? $"> {k:0} %"
                : $"{k:0} %";
        }

        text.AppendLine($"   {label,-12} quiet {Knee(KneeFinder.QuietMinGain),-7} balanced {Knee(KneeFinder.BalancedMinGain),-7} performance {Knee(KneeFinder.PerformanceMinGain)}");
    }

    private static void WriteCsv(string path, List<SweepStep> steps, double ambient)
    {
        var csv = new StringBuilder("fan_percent,cpu_temp,cpu_power,cpu_r,gpu_temp,gpu_hotspot,gpu_memory,gpu_power,gpu_r,hotspot_r,settled,seconds,ambient\n");
        foreach (var s in steps)
        {
            csv.AppendLine(string.Join(',',
                C(s.FanPercent), C(s.CpuTemp), C(s.CpuPower), C(s.CpuResistance(ambient)),
                C(s.GpuTemp), C(s.GpuHotspot), C(s.GpuMemory), C(s.GpuPower),
                C(s.GpuResistance(ambient)), C(s.HotspotResistance(ambient)),
                s.Settled ? "1" : "0", C(s.Duration.TotalSeconds), C(ambient)));
        }
        File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
    }

    private static bool Changed(double? value, double? reference) =>
        value is { } v && reference is { } r && r > 1 && Math.Abs(v - r) / r > LoadChangeWarning;

    private static double? Rise(double? temp, double ambient) => temp - ambient;

    private static string N(double? value, string format = "0.0") =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "–";

    private static string R(double? value) =>
        (value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "–").PadLeft(6);

    private static string C(double? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
}
