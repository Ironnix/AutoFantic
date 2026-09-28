using System.Globalization;
using System.Text;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// The quick calibration: with a steady built-in load, run the fans through a handful of chosen
/// combinations, predict where the temperatures settle in each, fit the thermal model, and work
/// out the quietest fan mix for every load level under the chosen profile. About 12 minutes.
/// </summary>
internal static class CalibrateCommand
{
    private static readonly Component[] Modelled = [Component.Cpu, Component.GpuCore, Component.GpuHotspot, Component.GpuMemory];

    // the first seconds after a change are fans spinning up or down, not heat moving
    private const double SkipSeconds = 3;

    private sealed record Sample(double Seconds, Dictionary<Component, double> Temps, double CpuPower, double GpuPower, double[] GroupRpm);

    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--no-load");
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default(session));
        double ambient = options.GetDouble("--ambient", 22);
        var profile = Profile.Max(options.GetDouble("--profile", 80));
        var hold = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--hold", 75), 30, 300));

        // 1. which headers really have a fan
        string inventoryPath = runs.File("fans.json");
        var inventory = FanInventory.Load(inventoryPath);
        if (inventory is null)
        {
            Console.WriteLine("First, finding out which headers have a fan on them (about 2 minutes, fans change one by one) …");
            Console.WriteLine();
            DiscoverCommand.Run(session, ["--inventory", inventoryPath, "--out", runs.File("discover.txt")], cancel);
            inventory = FanInventory.Load(inventoryPath);
            if (inventory is null)
                return 3;
            Console.WriteLine();
        }

        var groups = inventory.Groups().ToList();
        var channels = groups.Select(g => g.Headers.Select(h => Channel(session, h)).ToList()).ToList();
        if (groups.Count == 0)
        {
            Console.Error.WriteLine("No fans to calibrate: discover found no header with a fan on it.");
            return 2;
        }

        var first = session.Read();
        var keys = KeySensors.Detect(first);
        var guard = new Guard(session, keys, cancel);
        if (!guard.CheckNow())
        {
            Console.Error.WriteLine($"Not starting: {guard.StopReason}.");
            return 3;
        }
        double idleCpu = first.Value(keys.CpuPower) ?? 20, idleGpu = first.Value(keys.GpuPower) ?? 20;

        var plan = CalibrationPlan.Runs(groups);
        // each run lasts at least one hold and longer while the temperatures are still far from settled
        double minutes = (hold.TotalSeconds * 1.6 + (plan.Count - 1) * hold.TotalSeconds) / 60 / session.TimeScale;
        PrintIntro(groups, plan.Count, minutes, profile, ambient, options.Get("--ambient") is not null);

        var sensorOf = new Dictionary<Component, string?>
        {
            [Component.Cpu] = keys.CpuTemp,
            [Component.GpuCore] = keys.GpuTemp,
            [Component.GpuHotspot] = keys.GpuHotspot,
            [Component.GpuMemory] = keys.GpuMemory,
        };

        var observations = new List<Observation>();
        var skipped = new List<string>();
        var stoppedAt = new double[groups.Count];
        string loadDescription = "your own load (--no-load)";
        bool aborted = false;

        try
        {
            using var load = options.Has("--no-load") ? null : session.StartTestLoad(out loadDescription);
            Console.WriteLine($"Load: {loadDescription}");
            Console.WriteLine();

            for (int r = 0; r < plan.Count && !aborted; r++)
            {
                var speeds = plan[r];
                bool warmup = r == 0;
                for (int g = 0; g < groups.Count; g++)
                    foreach (var channel in channels[g])
                        session.SetPercent(channel, (float)speeds[g]);

                Console.WriteLine($"Run {r + 1}/{plan.Count}{(warmup ? " (warming up)" : "")} · {Describe(groups, speeds)}");

                var samples = new List<Sample>();
                var start = session.Now;
                void OnSample(Snapshot s)
                {
                    var sample = ToSample(s, start, sensorOf, keys, groups);
                    samples.Add(sample);
                    Console.Write($"\r   {TimeSpan.FromSeconds(sample.Seconds):m\\:ss}  CPU {T(sample.Temps, Component.Cpu)}  GPU {T(sample.Temps, Component.GpuCore)}  ({sample.CpuPower:0} W / {sample.GpuPower:0} W)   ");
                }

                var minHold = warmup ? hold * 1.6 : hold;
                var maxHold = warmup ? hold * 4 : hold * 2.5;
                bool ok = guard.Wait(minHold, OnSample);
                var fits = ok ? FitAll(samples) : [];
                while (ok && fits.Values.Any(f => !f.Reliable) && session.Now - start < maxHold)
                {
                    ok = guard.Wait(TimeSpan.FromSeconds(15), OnSample);
                    fits = ok ? FitAll(samples) : fits;
                }
                Console.WriteLine();

                if (!ok)
                {
                    if (guard.Cancelled)
                    {
                        Console.WriteLine("   stopped with Ctrl+C.");
                        aborted = true;
                    }
                    else if (guard.LimitCrossed)
                    {
                        skipped.Add(Describe(groups, speeds));
                        Console.WriteLine("   too hot with these speeds: skipped, carrying on with the next run.");
                    }
                    else
                    {
                        Console.WriteLine($"   {guard.StopReason}: calibration stopped.");
                        aborted = true;
                    }
                    continue;
                }

                // effective speed: a fan that stood still counts as 0 %
                var tail = samples.TakeLast(Math.Max(5, samples.Count / 4)).ToList();
                var effective = speeds.Select((speed, g) =>
                {
                    bool spinning = tail.Average(s => s.GroupRpm[g]) >= 50;
                    if (!spinning && speed > 0)
                        stoppedAt[g] = Math.Max(stoppedAt[g], speed);
                    return spinning ? speed : 0;
                }).ToList();

                var steady = samples.Where(s => s.Seconds >= samples[^1].Seconds * 0.4).ToList();
                var finals = fits.ToDictionary(kv => kv.Key, kv => kv.Value.Final);
                observations.Add(new Observation(effective, steady.Average(s => s.CpuPower), steady.Average(s => s.GpuPower), finals));

                string settled = fits.Values.All(f => f.Reliable) ? $"settles in about {TimeSpan.FromSeconds(4 * fits.Values.Max(f => f.Tau)):m\\:ss}" : "estimate less certain";
                Console.WriteLine($"   → heading for CPU {T(finals, Component.Cpu)}, GPU {T(finals, Component.GpuCore)} ({settled})");
                if (effective.Zip(speeds).Any(p => p.First == 0 && p.Second > 0))
                    Console.WriteLine("   (a fan stood still at this speed: noted as its 0-RPM range)");
            }
        }
        finally
        {
            session.RestoreAll();
        }

        Console.WriteLine();
        Console.WriteLine("Load stopped, all fans back to BIOS control.");
        Console.WriteLine();

        if (observations.Count < Math.Min(plan.Count, 3))
        {
            Console.WriteLine($"Only {observations.Count} of {plan.Count} runs finished: not enough to work out curves.");
            return aborted ? 3 : 1;
        }

        // a group that stood still at a speed needs more than that to spin
        for (int g = 0; g < groups.Count; g++)
        {
            if (stoppedAt[g] > 0)
                groups[g] = groups[g] with { SpinsFrom = (float)Math.Min(100, stoppedAt[g] + 10) };
        }

        var model = ThermalModel.Fit(observations, ambient, groups.Count);
        var optimizer = new MixOptimizer(model, groups, profile);

        double calCpu = observations.Average(o => o.CpuPower), calGpu = observations.Average(o => o.GpuPower);
        string[] labels = ["idle", "light", "medium", "heavy", "full"];
        var loads = labels.Select((_, i) => (idleCpu + (calCpu - idleCpu) * i / 4.0, idleGpu + (calGpu - idleGpu) * i / 4.0)).ToList();
        var table = optimizer.Table(loads)
            .Select((m, i) => new LoadRow(labels[i], loads[i].Item1, loads[i].Item2, m.Speeds, m.Temperatures, m.Noise, m.MeetsTarget))
            .ToList();

        var calibrated = groups.Select((g, i) => Calibrate(g, i, model, table, calCpu, calGpu)).ToList();
        var result = new CalibrationResult(
            DateTimeOffset.Now, profile.Name, ambient, calibrated, table,
            model.Components.ToDictionary(c => c, c => model.Coefficients(c).ToArray()));

        string stamp = $"{DateTime.Now:yyyyMMdd-HHmm}";
        string report = Report(result, groups, model, observations.Count, skipped, loadDescription, calCpu, calGpu, stamp);
        Console.Write(report);

        runs.Write($"calibration-{stamp}.txt", report);
        result.Save(runs.File("calibration.json"));
        Console.WriteLine($"Saved: {runs.File($"calibration-{stamp}.txt")} (and calibration.json)");
        return 0;
    }

    private static FanChannel Channel(FanSession session, FanHeader header) =>
        session.Channels.FirstOrDefault(c => c.Id == header.ControlId)
        ?? throw new UsageException($"Fan output {header.ControlId} from fans.json no longer exists: run \"Find my fans\" again.");

    private static void PrintIntro(List<FanGroup> groups, int runs, double minutes, Profile profile, double ambient, bool ambientGiven)
    {
        Console.WriteLine($"Calibrating {groups.Count} fan group(s): {string.Join(", ", groups.Select(g => g.Name))}.");
        Console.WriteLine($"{runs} runs with different fan speeds under a steady built-in load, about {minutes:0}–{minutes * 1.6:0} minutes.");
        Console.WriteLine("Please don't use the PC meanwhile: a game or video would disturb the measurement.");
        Console.WriteLine("The fans will be audible. Ctrl+C stops at any time and hands the fans back to the BIOS.");
        if (!ambientGiven)
            Console.WriteLine($"Room temperature assumed {ambient:0} °C (--ambient to change).");
        Console.WriteLine($"Goal: {profile.Name} (CPU ≤ {profile.Cpu:0} °C, GPU core ≤ {profile.GpuCore:0} °C) as quietly as possible.");
        Console.WriteLine();
    }

    private static Sample ToSample(Snapshot s, DateTimeOffset start, Dictionary<Component, string?> sensorOf, KeySensors keys, List<FanGroup> groups)
    {
        var temps = new Dictionary<Component, double>();
        foreach (var (component, id) in sensorOf)
            if (s.Value(id) is { } v)
                temps[component] = v;

        var rpm = groups.Select(g => (double)g.Headers.Average(h => s.Value(h.RpmSensorId) ?? 0)).ToArray();
        return new Sample((s.Time - start).TotalSeconds, temps, s.Value(keys.CpuPower) ?? 0, s.Value(keys.GpuPower) ?? 0, rpm);
    }

    private static Dictionary<Component, StepFit> FitAll(List<Sample> samples)
    {
        var fits = new Dictionary<Component, StepFit>();
        foreach (var component in Modelled)
        {
            var series = samples
                .Where(s => s.Seconds >= SkipSeconds && s.Temps.ContainsKey(component))
                .Select(s => (s.Seconds, s.Temps[component]))
                .ToList();
            if (StepResponseFit.Fit(series) is { } fit)
                fits[component] = fit;
        }
        return fits;
    }

    private static CalibratedGroup Calibrate(FanGroup group, int index, ThermalModel model, List<LoadRow> table, double cpuPower, double gpuPower)
    {
        double low = CalibrationPlan.Levels(group)[2];
        double Effect(Component c, double power) =>
            model.Components.Contains(c) ? power * model.Coefficients(c)[index + 1] * (ThermalModel.Basis(low) - ThermalModel.Basis(100)) : 0;

        double cpu = Effect(Component.Cpu, cpuPower), gpu = Effect(Component.GpuCore, gpuPower);
        var follows = group.IsGpu || gpu > cpu ? Component.GpuCore : Component.Cpu;
        return new CalibratedGroup(
            group.Name,
            group.Headers.Select(h => h.Channel).ToList(),
            group.Headers.Select(h => h.ControlId).ToList(),
            follows, cpu, gpu,
            CalibrationResult.CurveFor(table, index, follows));
    }

    private static string Report(CalibrationResult result, List<FanGroup> groups, ThermalModel model, int runsUsed, List<string> skipped,
        string load, double calCpu, double calGpu, string stamp)
    {
        var text = new StringBuilder();
        text.AppendLine($"AutoFanatic calibration · {stamp} · room {result.Ambient:0} °C · {result.Profile}");
        text.AppendLine($"Load: {load} → CPU {calCpu:0} W, GPU {calGpu:0} W");
        text.AppendLine();

        text.AppendLine("What each fan cools (from 100 % to its lowest speed, at the calibration load)");
        foreach (var g in result.Groups)
            text.AppendLine($"   {g.Name,-30} CPU {Signed(g.CpuEffect)}   GPU {Signed(g.GpuEffect)}   {Role(g)}");
        string fit = string.Join(", ", model.Rms.Select(kv => $"{Name(kv.Key)} ±{kv.Value:0.0} °C"));
        text.AppendLine($"   model fits the measurements within {fit}; {runsUsed} runs used" + (skipped.Count > 0 ? $", {skipped.Count} too hot and skipped" : ""));
        text.AppendLine();

        text.AppendLine($"Quietest fan speeds for {result.Profile}, per load level");
        var header = new StringBuilder($"   {"load",-8} {"CPU W",5} {"GPU W",5} ");
        foreach (var g in groups)
            header.Append($" {Short(g),8}");
        header.Append("   CPU °C  GPU °C  hotspot  noise");
        text.AppendLine(header.ToString());

        double loudest = NoiseModel.Total(groups, groups.Select(_ => 100.0).ToList());
        foreach (var row in result.Table)
        {
            var line = new StringBuilder($"   {row.Label,-8} {row.CpuPower,5:0} {row.GpuPower,5:0} ");
            foreach (var s in row.Speeds)
                line.Append($" {(s == 0 ? "0 rpm" : $"{s:0} %"),8}");
            line.Append($"   {T(row.Temperatures, Component.Cpu),6}  {T(row.Temperatures, Component.GpuCore),6}  {T(row.Temperatures, Component.GpuHotspot),7}  {Noise(row.Noise - loudest)}");
            if (!row.MeetsTarget)
                line.Append("  ← target not reachable, coolest mix");
            text.AppendLine(line.ToString());
        }
        text.AppendLine("   noise: dB compared with all fans at 100 % (−10 dB sounds about half as loud)");
        text.AppendLine();

        text.AppendLine("Fan curves (fan % by temperature), e.g. for the BIOS Smart Fan or MSI Afterburner");
        foreach (var g in result.Groups)
        {
            string points = string.Join(",  ", g.Curve.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %"));
            text.AppendLine($"   {g.Name,-30} follows {Name(g.Follows)}:  {points}");
        }
        text.AppendLine();

        var full = result.Table[^1];
        text.AppendLine(full.MeetsTarget
            ? $"{result.Profile} holds up to full load: CPU {T(full.Temperatures, Component.Cpu)}, GPU {T(full.Temperatures, Component.GpuCore)}."
            : $"{result.Profile} is not reachable at full load; the coolest mix reaches CPU {T(full.Temperatures, Component.Cpu)}, GPU {T(full.Temperatures, Component.GpuCore)}.");
        text.AppendLine();
        return text.ToString();
    }

    private static string Role(CalibratedGroup g) =>
        g.CpuEffect < 1 && g.GpuEffect < 1 ? "barely any effect"
        : g.CpuEffect >= 2 * g.GpuEffect ? "cools the CPU"
        : g.GpuEffect >= 2 * g.CpuEffect ? "cools the GPU"
        : "case airflow, helps both";

    private static string Describe(List<FanGroup> groups, IReadOnlyList<double> speeds) =>
        string.Join(" · ", groups.Select((g, i) => $"{Short(g)} {speeds[i]:0} %"));

    private static string Short(FanGroup g) =>
        g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}";

    private static string Name(Component c) => c switch
    {
        Component.Cpu => "CPU",
        Component.GpuCore => "GPU",
        Component.GpuHotspot => "hotspot",
        _ => "GPU memory",
    };

    private static string T(IReadOnlyDictionary<Component, double> temps, Component c) =>
        temps.TryGetValue(c, out double v) ? $"{v:0.0} °C" : "–";

    private static string Signed(double v) => $"{(v >= 0 ? "+" : "")}{v:0.0} °C".PadLeft(8);

    private static string Noise(double db) => double.IsFinite(db) ? $"{db:0} dB" : "silent";
}
