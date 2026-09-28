using System.Text;
using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// The calibration: while the PC works (a game, or the built-in load), run the fans through a
/// handful of chosen combinations, work out from how the temperatures follow the power what each
/// setting is worth (°C per watt), fit the thermal model, and compute the quietest fan speeds for
/// every load level, from idle to beyond what was measured. The result applies to every task.
/// </summary>
internal static class CalibrateCommand
{
    private static readonly Component[] Modelled = [Component.Cpu, Component.GpuCore, Component.GpuHotspot, Component.GpuMemory];

    // the first seconds after a change are fans spinning up or down, not heat moving
    private const double SkipSeconds = 3;

    // "there is load": the GPU or the CPU is clearly busy
    private const double LoadedGpuPercent = 40;
    private const double LoadedCpuPercent = 25;

    private const int RetriesWithoutLoad = 2;

    private sealed record Sample(double Seconds, Dictionary<Component, double> Temps, double CpuPower, double GpuPower,
        double CpuLoad, double GpuLoad, double[] GroupRpm, string? Foreground);

    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--builtin-load");
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default(session));
        double ambient = options.GetDouble("--ambient", 22);
        var profile = Profile.Max(options.GetDouble("--profile", 80));
        bool builtIn = options.Has("--builtin-load");
        var hold = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--hold", builtIn ? 75 : 90), 30, 300));

        // which headers really have a fan
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
            Console.Error.WriteLine("No fans to calibrate: no header with a fan on it was found.");
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

        var sensorOf = new Dictionary<Component, string?>
        {
            [Component.Cpu] = keys.CpuTemp,
            [Component.GpuCore] = keys.GpuTemp,
            [Component.GpuHotspot] = keys.GpuHotspot,
            [Component.GpuMemory] = keys.GpuMemory,
        };

        bool idleNow = LoadClassifier.Classify(first.Value(keys.CpuLoad), first.Value(keys.GpuLoad)) is LoadClass.Idle;
        var plan = CalibrationPlan.Runs(groups);
        PrintIntro(groups, plan.Count, hold, profile, ambient, options.Get("--ambient") is not null, builtIn, idleNow, session.TimeScale);

        var observations = new List<Observation>();
        var loaded = new List<Sample>();
        var skipped = new List<string>();
        var stoppedAt = new double[groups.Count];
        string loadDescription = "your own load";
        bool aborted = false;
        string fansOffPath = runs.File("fans-off.json");
        var fansOff = FansOffResult.Load(fansOffPath);
        double? idleCpu = null, idleGpu = null;

        try
        {
            // 1. fans off: only possible while the PC is idle; otherwise the last result is reused
            if (idleNow)
            {
                idleCpu = first.Value(keys.CpuPower);
                idleGpu = first.Value(keys.GpuPower);
                var test = FansOffTest(session, guard, inventory, groups, channels, sensorOf, keys);
                aborted = test.Aborted;
                if (test.Result is not null)
                {
                    fansOff = test.Result;
                    fansOff.Save(fansOffPath);
                }
                if (test.Inventory != inventory)
                {
                    inventory = test.Inventory;
                    inventory.Save(inventoryPath);
                    groups = inventory.Groups().ToList(); // same groups, now knowing which fans stop at 0 %
                }
                session.RestoreAll(); // BIOS again until the load is there
                Console.WriteLine();
            }
            else if (fansOff is not null)
            {
                idleCpu = fansOff.CpuPower;
                idleGpu = fansOff.GpuPower;
            }

            // 2. the load: the built-in one, or whatever the user runs (a game)
            using var load = aborted || !builtIn ? null : session.StartTestLoad(out loadDescription);
            if (builtIn)
            {
                Console.WriteLine($"Load: {loadDescription}");
                Console.WriteLine();
            }
            else if (!aborted && !WaitForLoad(session, guard, sensorOf, keys, groups))
            {
                aborted = true;
            }

            // 3. the runs
            for (int r = 0; r < plan.Count && !aborted; r++)
            {
                var speeds = plan[r];
                bool warmup = r == 0;
                for (int attempt = 0; ; attempt++)
                {
                    for (int g = 0; g < groups.Count; g++)
                        foreach (var channel in channels[g])
                            session.SetPercent(channel, (float)speeds[g]);

                    Console.WriteLine($"Run {r + 1}/{plan.Count}{(warmup ? " (warming up)" : "")} · {Describe(groups, speeds)}");
                    var outcome = MeasureRun(session, guard, sensorOf, keys, groups, ambient, hold, warmup);
                    if (outcome.Stop is { } stop)
                    {
                        if (stop == RunStop.TooHot)
                        {
                            skipped.Add(Describe(groups, speeds));
                            Console.WriteLine("   too hot with these speeds: skipped, carrying on with the next run.");
                        }
                        else
                        {
                            Console.WriteLine(stop == RunStop.Cancelled ? "   stopped with Ctrl+C." : $"   {guard.StopReason}: calibration stopped.");
                            aborted = true;
                        }
                        break;
                    }

                    var samples = outcome.Samples;
                    if (!builtIn && !HasLoad(samples))
                    {
                        if (attempt < RetriesWithoutLoad)
                        {
                            Console.WriteLine("   hardly any load during this run (game paused or closed?): repeating it.");
                            continue;
                        }
                        Console.WriteLine("   still no load: run skipped.");
                        break;
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

                    double cpuW = samples.Average(s => s.CpuPower), gpuW = samples.Average(s => s.GpuPower);
                    var finals = outcome.Fits.ToDictionary(
                        kv => kv.Key,
                        kv => ambient + (ThermalModel.IsCpu(kv.Key) ? cpuW : gpuW) * kv.Value.Resistance);
                    observations.Add(new Observation(effective, cpuW, gpuW, finals));
                    loaded.AddRange(samples);

                    Console.WriteLine($"   → at {cpuW:0} W / {gpuW:0} W this setting holds CPU at {T(finals, Component.Cpu)}, GPU at {T(finals, Component.GpuCore)}"
                        + (outcome.Fits.Values.All(f => f.Reliable) ? "" : " (less certain)"));
                    if (effective.Zip(speeds).Any(p => p.First == 0 && p.Second > 0))
                        Console.WriteLine("   (a fan stood still at this speed: noted as its 0-RPM range)");
                    break;
                }
            }
        }
        finally
        {
            session.RestoreAll();
        }

        Console.WriteLine();
        Console.WriteLine(builtIn ? "Load stopped, all fans back to BIOS control." : "All fans back to BIOS control.");
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

        // load levels from what was measured: idle, up to the highest load seen, and beyond it
        double topCpu = Percentile(loaded.Select(s => s.CpuPower), 0.95), topGpu = Percentile(loaded.Select(s => s.GpuPower), 0.95);
        double lowCpu = idleCpu ?? Percentile(loaded.Select(s => s.CpuPower), 0.05);
        double lowGpu = idleGpu ?? Percentile(loaded.Select(s => s.GpuPower), 0.05);
        string[] labels = ["idle", "light", "medium", "high", "beyond"];
        double[] shares = [0, 0.33, 0.66, 1, 1.3];
        var loads = shares.Select(f => (lowCpu + (topCpu - lowCpu) * f, lowGpu + (topGpu - lowGpu) * f)).ToList();

        // fans may be off at idle-like power only (and while it's cool, see MixOptimizer)
        double stopCpu = Math.Max(lowCpu * 1.25, topCpu / 2), stopGpu = Math.Max(lowGpu * 1.25, topGpu / 2);
        var optimizer = new MixOptimizer(model, groups, profile)
        {
            StopOnlyUpTo = (stopCpu, stopGpu),
            AllOffResistance = fansOff?.Resistance(ambient),
        };
        var table = optimizer.Table(loads)
            .Select((m, i) => new LoadRow(labels[i], loads[i].Item1, loads[i].Item2, m.Speeds, m.Temperatures, m.Noise, m.MeetsTarget))
            .ToList();

        var calibrated = groups.Select((g, i) => Calibrate(g, i, model, table, topCpu, topGpu, profile)).ToList();
        var result = new CalibrationResult(
            DateTimeOffset.Now, profile.Name, ambient, calibrated, table,
            model.Components.ToDictionary(c => c, c => model.Coefficients(c).ToArray()),
            stopCpu, stopGpu);

        if (!builtIn)
            loadDescription = MostlyRunning(loaded) is { } program ? $"your own load (mostly {program})" : "your own load";

        string stamp = $"{DateTime.Now:yyyyMMdd-HHmm}";
        string report = Report(result, groups, model, observations.Count, skipped, loadDescription, topCpu, topGpu, stamp, fansOff, idleNow);
        Console.Write(report);

        runs.Write($"calibration-{stamp}.txt", report);
        result.Save(runs.File("calibration.json"));
        Console.WriteLine($"Saved: {runs.File($"calibration-{stamp}.txt")} (and calibration.json)");
        return 0;
    }

    private static FanChannel Channel(FanSession session, FanHeader header) =>
        session.Channels.FirstOrDefault(c => c.Id == header.ControlId)
        ?? throw new UsageException($"Fan output {header.ControlId} from fans.json no longer exists: run \"Find my fans\" again.");

    private static void PrintIntro(List<FanGroup> groups, int runs, TimeSpan hold, Profile profile, double ambient, bool ambientGiven,
        bool builtIn, bool idleNow, double timeScale)
    {
        double minutes = runs * hold.TotalMinutes / timeScale;
        Console.WriteLine($"Calibrating {groups.Count} fan group(s): {string.Join(", ", groups.Select(g => g.Name))}.");
        if (idleNow)
            Console.WriteLine("The PC is idle, so first a fans-off test (up to 2½ minutes).");
        Console.WriteLine(builtIn
            ? $"Then {runs} runs with different fan speeds under the built-in load, about {minutes:0}–{minutes * 1.8:0} minutes."
            : $"Then {runs} runs with different fan speeds while you play or work, about {minutes:0}–{minutes * 1.8:0} minutes.");
        Console.WriteLine("The fans will be audible. If it gets too hot, all fans go to 100 % until it has cooled down.");
        Console.WriteLine("Ctrl+C stops at any time and hands the fans back to the BIOS.");
        if (!ambientGiven)
            Console.WriteLine($"Room temperature assumed {ambient:0} °C (--ambient to change).");
        Console.WriteLine($"Goal: {profile.Name} (CPU ≤ {profile.Cpu:0} °C, GPU core ≤ {profile.GpuCore:0} °C) as quietly as possible.");
        Console.WriteLine();
    }

    /// <summary>Fans on BIOS control until the user's game (or anything) keeps the PC busy for 20 s.</summary>
    private static bool WaitForLoad(FanSession session, Guard guard, Dictionary<Component, string?> sensorOf, KeySensors keys, List<FanGroup> groups)
    {
        Console.WriteLine("Now start your game (or anything that makes the PC work) and play normally.");
        Console.WriteLine("The calibration starts by itself once there is load. Waiting up to 15 minutes …");

        var start = session.Now;
        int loadedFor = 0;
        bool ok = guard.Wait(TimeSpan.FromMinutes(15), s =>
        {
            var sample = ToSample(s, start, sensorOf, keys, groups, session);
            loadedFor = IsLoaded(sample.CpuLoad, sample.GpuLoad) ? loadedFor + 1 : 0;
            Console.Write($"\r   waiting {TimeSpan.FromSeconds(sample.Seconds):m\\:ss}  CPU {sample.CpuLoad:0} %  GPU {sample.GpuLoad:0} %  in front: {sample.Foreground ?? "?"}".PadRight(80));
        }, until: () => loadedFor >= 20);
        Console.WriteLine();

        if (!ok)
            return false;
        if (loadedFor < 20)
        {
            Console.WriteLine("No load came up: calibration stopped. Start it again while a game runs.");
            return false;
        }
        Console.WriteLine("Load detected, starting.");
        Console.WriteLine();
        return true;
    }

    private static bool IsLoaded(double cpuLoad, double gpuLoad) =>
        gpuLoad >= LoadedGpuPercent || cpuLoad >= LoadedCpuPercent;

    private static bool HasLoad(List<Sample> samples) =>
        IsLoaded(samples.Average(s => s.CpuLoad), samples.Average(s => s.GpuLoad));

    private enum RunStop
    {
        Cancelled,
        TooHot,
        SensorError,
    }

    private sealed record RunOutcome(List<Sample> Samples, Dictionary<Component, PowerFit> Fits, RunStop? Stop);

    /// <summary>
    /// Holds the current fan setting at least <paramref name="hold"/> (longer for the warm-up, and
    /// longer while the result is still uncertain) and fits how each temperature follows the power.
    /// </summary>
    private static RunOutcome MeasureRun(FanSession session, Guard guard, Dictionary<Component, string?> sensorOf, KeySensors keys,
        List<FanGroup> groups, double ambient, TimeSpan hold, bool warmup)
    {
        var samples = new List<Sample>();
        var start = session.Now;
        void OnSample(Snapshot s)
        {
            var sample = ToSample(s, start, sensorOf, keys, groups, session);
            samples.Add(sample);
            Console.Write($"\r   {TimeSpan.FromSeconds(sample.Seconds):m\\:ss}  CPU {T(sample.Temps, Component.Cpu)}  GPU {T(sample.Temps, Component.GpuCore)}  ({sample.CpuPower:0} W / {sample.GpuPower:0} W)   ");
        }

        var minHold = warmup ? hold * 1.5 : hold;
        var maxHold = warmup ? hold * 3.5 : hold * 2.5;
        bool ok = guard.Wait(minHold, OnSample);
        var fits = ok ? FitAll(samples, ambient) : [];
        while (ok && (fits.Count == 0 || fits.Values.Any(f => !f.Reliable)) && session.Now - start < maxHold)
        {
            ok = guard.Wait(TimeSpan.FromSeconds(15), OnSample);
            fits = ok ? FitAll(samples, ambient) : fits;
        }
        Console.WriteLine();

        RunStop? stop = ok ? null
            : guard.Cancelled ? RunStop.Cancelled
            : guard.LimitCrossed ? RunStop.TooHot
            : RunStop.SensorError;
        return new RunOutcome(samples, fits, stop);
    }

    private sealed record OffTest(FansOffResult? Result, FanInventory Inventory, bool Aborted);

    // the fans-off test ends as soon as the CPU or GPU core passes this
    private const double OffTestWarmAbove = 62;

    /// <summary>
    /// Fans-off test at idle: every fan at 0 % for up to 2½ minutes, ending early once the CPU or
    /// GPU core passes 62 °C. Finds out which fans really stop at 0 % (some keep turning slowly) and
    /// how warm CPU and GPU get without any fan at idle power: the data behind "fans off when cool".
    /// </summary>
    private static OffTest FansOffTest(FanSession session, Guard guard, FanInventory inventory, List<FanGroup> groups,
        List<List<FanChannel>> channels, Dictionary<Component, string?> sensorOf, KeySensors keys)
    {
        Console.WriteLine($"Fans-off test: all fans stop for up to 2½ minutes (ends early above {OffTestWarmAbove:0} °C) …");
        foreach (var channel in channels.SelectMany(c => c))
            session.SetPercent(channel, 0);

        var samples = new List<Sample>();
        var start = session.Now;
        bool tooWarm = false, loadCameUp = false;
        bool ok = guard.Wait(TimeSpan.FromSeconds(150), s =>
        {
            var sample = ToSample(s, start, sensorOf, keys, groups, session);
            samples.Add(sample);
            tooWarm = sample.Temps.Any(kv => kv.Key is Component.Cpu or Component.GpuCore && kv.Value > OffTestWarmAbove);
            loadCameUp = IsLoaded(sample.CpuLoad, sample.GpuLoad);
            Console.Write($"\r   {TimeSpan.FromSeconds(sample.Seconds):m\\:ss}  CPU {T(sample.Temps, Component.Cpu)}  GPU {T(sample.Temps, Component.GpuCore)}  ({sample.CpuPower:0} W / {sample.GpuPower:0} W)   ");
        }, until: () => tooWarm || loadCameUp);
        Console.WriteLine();

        if (!ok)
            return new OffTest(null, inventory, Aborted: guard.Cancelled || !guard.LimitCrossed);
        if (loadCameUp)
        {
            // warmer because of the load, not because the fans stopped: says nothing about idle
            Console.WriteLine("   the PC got busy during the test (game started?): fans-off result not used this time.");
            return new OffTest(null, inventory, Aborted: false);
        }

        var summary = new List<string>();
        var last = guard.Last!;
        var elapsed = session.Now - start;

        // fans need a few seconds to spin down: only a long enough test says which ones stop at 0 %
        var updated = inventory;
        if (elapsed >= TimeSpan.FromSeconds(20))
        {
            var rpmAtZero = inventory.Usable.ToDictionary(h => h.Channel, h => last.Value(h.RpmSensorId) ?? 0f);
            updated = inventory.WithRpmAtZero(rpmAtZero);
            foreach (var group in updated.Groups())
            {
                float rpm = group.Headers.Average(h => rpmAtZero.GetValueOrDefault(h.Channel));
                summary.Add(rpm < 50 ? $"{group.Name} stops at 0 %" : $"{group.Name} keeps turning at 0 % (about {rpm:0} rpm), so it can't be switched off");
            }
        }

        var finals = new Dictionary<Component, double>();
        foreach (var component in Modelled)
        {
            var series = samples.Where(s => s.Seconds >= SkipSeconds && s.Temps.ContainsKey(component)).Select(s => (s.Seconds, s.Temps[component])).ToList();
            if (StepResponseFit.Fit(series) is { } fit)
                finals[component] = fit.Final;
        }

        double cpuW = samples.Average(s => s.CpuPower), gpuW = samples.Average(s => s.GpuPower);
        if (tooWarm)
        {
            // stopped early while still rising: at least this warm, likely more
            foreach (var c in new[] { Component.Cpu, Component.GpuCore })
                if (finals.TryGetValue(c, out double v))
                    finals[c] = Math.Max(v, OffTestWarmAbove + 3);
            string which = samples[^1].Temps.Where(kv => kv.Key is Component.Cpu or Component.GpuCore && kv.Value > OffTestWarmAbove)
                .Select(kv => kv.Key == Component.Cpu ? "CPU" : "GPU").FirstOrDefault() ?? "CPU/GPU";
            summary.Add($"without fans at idle (CPU {cpuW:0} W, GPU {gpuW:0} W) the {which} passed {OffTestWarmAbove:0} °C after {elapsed:m\\:ss}: too warm to switch everything off at this idle power");
        }
        else
        {
            summary.Add($"without fans at idle (CPU {cpuW:0} W, GPU {gpuW:0} W): CPU heads for {T(finals, Component.Cpu)}, GPU for {T(finals, Component.GpuCore)}");
        }

        Console.WriteLine($"   → {summary[^1]}");
        return new OffTest(new FansOffResult(DateTimeOffset.Now, cpuW, gpuW, finals, summary), updated, Aborted: false);
    }

    private static Sample ToSample(Snapshot s, DateTimeOffset start, Dictionary<Component, string?> sensorOf, KeySensors keys, List<FanGroup> groups, FanSession session)
    {
        var temps = new Dictionary<Component, double>();
        foreach (var (component, id) in sensorOf)
            if (s.Value(id) is { } v)
                temps[component] = v;

        var rpm = groups.Select(g => (double)g.Headers.Average(h => s.Value(h.RpmSensorId) ?? 0)).ToArray();
        return new Sample((s.Time - start).TotalSeconds, temps, s.Value(keys.CpuPower) ?? 0, s.Value(keys.GpuPower) ?? 0,
            s.Value(keys.CpuLoad) ?? 0, s.Value(keys.GpuLoad) ?? 0, rpm, session.Foreground());
    }

    /// <summary>How each temperature follows the power of the part that heats it.</summary>
    private static Dictionary<Component, PowerFit> FitAll(List<Sample> samples, double ambient)
    {
        var fits = new Dictionary<Component, PowerFit>();
        foreach (var component in Modelled)
        {
            var series = samples
                .Where(s => s.Seconds >= SkipSeconds && s.Temps.ContainsKey(component))
                .Select(s => (s.Seconds, s.Temps[component], ThermalModel.IsCpu(component) ? s.CpuPower : s.GpuPower))
                .ToList();
            if (PowerResponseFit.Fit(series, ambient) is { } fit)
                fits[component] = fit;
        }
        return fits;
    }

    private static CalibratedGroup Calibrate(FanGroup group, int index, ThermalModel model, List<LoadRow> table, double cpuPower, double gpuPower, Profile profile)
    {
        double low = CalibrationPlan.Levels(group)[2];
        double Effect(Component c, double power) =>
            model.Components.Contains(c) ? power * model.Coefficients(c)[index + 1] * (ThermalModel.Basis(low) - ThermalModel.Basis(100)) : 0;

        double cpu = Effect(Component.Cpu, cpuPower), gpu = Effect(Component.GpuCore, gpuPower);
        var follows = group.IsGpu || gpu > cpu ? Component.GpuCore : Component.Cpu;

        // just above the target everything runs flat out: covers any task hotter than the calibration
        var limits = new SafetyLimits();
        double safety = follows == Component.Cpu ? limits.CpuMax : limits.GpuCoreMax;
        double fullSpeedAt = Math.Min(profile.Target(follows) + 2, safety - 3);

        return new CalibratedGroup(
            group.Name,
            group.Headers.Select(h => h.Channel).ToList(),
            group.Headers.Select(h => h.ControlId).ToList(),
            follows, cpu, gpu,
            CalibrationResult.CurveFor(table, index, follows, fullSpeedAt),
            table.Where(r => r.Speeds[index] == 0).Select(r => r.Label).ToList());
    }

    private static double Percentile(IEnumerable<double> values, double share)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)Math.Round((sorted.Count - 1) * share)];
    }

    private static string? MostlyRunning(List<Sample> samples) =>
        samples.Where(s => s.Foreground is not null).GroupBy(s => s.Foreground).MaxBy(g => g.Count())?.Key;

    private static string Report(CalibrationResult result, List<FanGroup> groups, ThermalModel model, int runsUsed, List<string> skipped,
        string load, double topCpu, double topGpu, string stamp, FansOffResult? fansOff, bool fansOffFresh)
    {
        var text = new StringBuilder();
        text.AppendLine($"AutoFanatic calibration · {stamp} · room {result.Ambient:0} °C · {result.Profile}");
        text.AppendLine($"Load: {load}, up to CPU {topCpu:0} W, GPU {topGpu:0} W");
        text.AppendLine();

        text.AppendLine("What each fan cools (from 100 % to its lowest speed, at that load)");
        foreach (var g in result.Groups)
            text.AppendLine($"   {g.Name,-30} CPU {Signed(g.CpuEffect)}   GPU {Signed(g.GpuEffect)}   {Role(g)}");
        string fit = string.Join(", ", model.Rms.Select(kv => $"{Name(kv.Key)} ±{kv.Value:0.0} °C"));
        text.AppendLine($"   model fits the measurements within {fit}; {runsUsed} runs used" + (skipped.Count > 0 ? $", {skipped.Count} too hot and skipped" : ""));
        text.AppendLine();

        text.AppendLine("Fans off (0-RPM)");
        if (fansOff is null)
        {
            text.AppendLine("   no fans-off test yet: start a calibration once while the PC is idle, then fans can be switched off at idle");
        }
        else
        {
            if (!fansOffFresh)
                text.AppendLine($"   (from the fans-off test of {fansOff.Created:dd.MM. HH:mm})");
            foreach (var line in fansOff.Summary)
                text.AppendLine($"   {line}");
        }
        text.AppendLine($"   a fan is only off while CPU ≤ {result.StopCpuWatts:0} W, GPU ≤ {result.StopGpuWatts:0} W and both stay ≤ {MixOptimizer.StopOnlyBelow:0} °C");
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
                line.Append($" {(s == 0 ? "off" : $"{s:0} %"),8}");
            line.Append($"   {T(row.Temperatures, Component.Cpu),6}  {T(row.Temperatures, Component.GpuCore),6}  {T(row.Temperatures, Component.GpuHotspot),7}  {Noise(row.Noise - loudest)}");
            if (!row.MeetsTarget)
                line.Append("  ← target not reachable, coolest mix");
            text.AppendLine(line.ToString());
        }
        text.AppendLine("   \"beyond\" is 30 % more than the highest load measured, e.g. a render; the model extrapolates there");
        text.AppendLine("   noise: dB compared with all fans at 100 % (−10 dB sounds about half as loud)");
        text.AppendLine();

        text.AppendLine("Fan curves (fan % by temperature): what \"Use my curves\" runs, also usable in the BIOS or MSI Afterburner");
        foreach (var g in result.Groups)
        {
            string points = string.Join(",  ", g.Curve.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %"));
            string off = g.OffAt.Count > 0 ? $"off at {string.Join(" + ", g.OffAt)} load, otherwise " : "";
            text.AppendLine($"   {g.Name,-30} {off}follows {Name(g.Follows)}:  {points}");
        }
        text.AppendLine();

        var high = result.Table.First(r => r.Label == "high");
        text.AppendLine(high.MeetsTarget
            ? $"{result.Profile} holds at the highest load measured: CPU {T(high.Temperatures, Component.Cpu)}, GPU {T(high.Temperatures, Component.GpuCore)}."
            : $"{result.Profile} is not reachable at the highest load measured; the coolest mix reaches CPU {T(high.Temperatures, Component.Cpu)}, GPU {T(high.Temperatures, Component.GpuCore)}.");
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

    private static string Noise(double db) => double.IsFinite(db) ? $"{Math.Round(db) + 0.0:0} dB" : "silent";
}
