using System.Globalization;
using System.Text;
using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Calibration;

namespace AutoFanatic.Spike;

/// <summary>
/// The second half of the calibration: from every stored measurement to the result. Needs no
/// hardware, so it can run again at any time, e.g. after another calibration or for another limit.
/// </summary>
internal static partial class CalibrateCommand
{
    private const string StoreFile = "measurements.json";

    /// <summary>"recalculate": the result again from the stored measurements (no admin rights needed).</summary>
    public static int RecalculateCommand(string[] args)
    {
        var options = new Options(args);
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default());
        var inventory = runs.Inventory();
        if (inventory is null)
        {
            Console.WriteLine("No fans.json yet: run \"Find my fans\" first.");
            return 2;
        }

        var groups = inventory.Groups().ToList();
        var store = LoadStore(runs, groups);
        if (store.Calibrations.Count == 0)
        {
            Console.WriteLine("No measurements yet: run a calibration first.");
            return 2;
        }

        var previous = CalibrationResult.Load(runs.File("calibration.json"));
        double limit = options.GetDouble("--profile", previous is null ? 80 : double.Parse(previous.Profile.Split(' ')[^1], CultureInfo.InvariantCulture));
        double ambient = options.GetDouble("--ambient", store.Calibrations[^1].Ambient);
        return Recalculate(runs, inventory, store, Profile.Max(limit), ambient, FansOffResult.Load(runs.File("fans-off.json")), fansOffFresh: false);
    }

    /// <summary>The stored measurements; the first time, an older saved result is imported into it.</summary>
    private static MeasurementStore LoadStore(RunsFolder runs, IReadOnlyList<FanGroup> groups)
    {
        string path = runs.File(StoreFile);
        if (MeasurementStore.Load(path) is { } store)
            return store;

        if (CalibrationResult.Load(runs.File("calibration.json")) is { } old)
        {
            string load = runs.Latest("calibration-*.txt") is { } report
                ? File.ReadLines(report.FullName).FirstOrDefault(l => l.StartsWith("Load: ", StringComparison.Ordinal))?[6..].Split(',')[0] ?? "earlier calibration"
                : "earlier calibration";
            var imported = MeasurementStore.Import(old, groups, load);
            if (imported.Runs.Count > 0)
            {
                imported.Save(path);
                Console.WriteLine($"Imported the calibration of {old.Created:dd.MM. HH:mm} ({imported.Runs.Count} runs, half weight) as a starting point.");
                return imported;
            }
        }
        return MeasurementStore.Empty;
    }

    /// <summary>Fits the model to all stored runs and writes the result: text report, calibration.json and the page.</summary>
    private static int Recalculate(RunsFolder runs, FanInventory inventory, MeasurementStore store, Profile profile, double ambient,
        FansOffResult? fansOff, bool fansOffFresh, IReadOnlyList<string>? skipped = null)
    {
        var groups = inventory.Groups().ToList();
        var observations = store.ObservationsFor(groups);
        var result = CalibrationCalculator.Calculate(store, inventory, profile, ambient, fansOff);
        if (result is null)
        {
            Console.WriteLine($"Only {observations.Count} stored runs match your fans: not enough to work out curves.");
            return 1;
        }
        double topCpu = store.Calibrations.Max(c => c.TopCpu), topGpu = store.Calibrations.Max(c => c.TopGpu);

        string stamp = $"{DateTime.Now:yyyyMMdd-HHmm}";
        int measured = observations.Count(o => o.Weight >= 1), imported = observations.Count - measured;
        string report = Report(result, groups, measured, imported, skipped ?? [], topCpu, topGpu, stamp, fansOff, fansOffFresh);
        Console.Write(report);

        runs.Write($"calibration-{stamp}.txt", report);
        result.Save(runs.File("calibration.json"));
        string page = runs.File("calibration.html");
        CalibrationPage.Write(page, result, groups, fansOff, measured);
        Console.WriteLine($"Saved: calibration-{stamp}.txt, calibration.json and the page {page}");
        return 0;
    }

    private static string Report(CalibrationResult result, List<FanGroup> groups, int measured, int imported, IReadOnlyList<string> skipped,
        double topCpu, double topGpu, string stamp, FansOffResult? fansOff, bool fansOffFresh)
    {
        var text = new StringBuilder();
        text.AppendLine($"AutoFanatic calibration · {stamp} · room {result.Ambient:0} °C · {result.Profile}");
        text.AppendLine("Built from:");
        foreach (var source in result.Sources ?? [])
            text.AppendLine($"   {source}");
        text.AppendLine($"Highest load measured: CPU {topCpu:0} W, GPU {topGpu:0} W");
        text.AppendLine();

        text.AppendLine("What each fan cools (from 100 % to its lowest speed, at the highest load)");
        foreach (var g in result.Groups)
            text.AppendLine($"   {g.Name,-30} CPU {Signed(g.CpuEffect)}   GPU {Signed(g.GpuEffect)}   {Role(g)}");
        string fit = measured == 0
            ? "accuracy shows after your next calibration (only imported runs so far)"
            : "model fits the measurements within " + string.Join(", ", (result.Rms ?? new Dictionary<Component, double>()).Select(kv => $"{Name(kv.Key)} ±{kv.Value:0.0} °C"));
        text.AppendLine($"   {fit}; {measured} measured runs" + (imported > 0 ? $" + {imported} imported" : "")
            + (skipped.Count > 0 ? $", {skipped.Count} too hot and skipped" : ""));
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
        text.AppendLine("   \"high\" = the highest CPU and GPU load measured, both at once (worst case); \"beyond\" = 30 % more, the model extrapolates");
        text.AppendLine("   noise: dB compared with all fans at 100 % (−10 dB sounds about half as loud)");
        text.AppendLine();

        text.AppendLine("Fan curves (fan % by temperature), what \"Use my curves\" runs");
        foreach (var g in result.Groups)
        {
            string points = string.Join(",  ", g.Curve.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %"));
            string off = g.OffAt.Count > 0 ? $"off at {string.Join(" + ", g.OffAt)} load, otherwise " : "";
            text.AppendLine($"   {g.Name,-30} {off}follows {Name(g.Follows)}:  {points}");
        }
        text.AppendLine();

        text.AppendLine("For the BIOS (e.g. MSI Smart Fan: 4 points, temperature source CPU)");
        foreach (var g in result.Groups.Where(g => g.Follows == Component.Cpu))
            text.AppendLine($"   {g.Name,-30} {string.Join("   ", CalibrationResult.BiosPoints(g.Curve).Select(p => $"{p.Temperature:0} °C {p.Percent:0} %"))}"
                + (g.OffAt.Count > 0 ? "   (at idle it may stop: set the first point to 0 % if the BIOS allows it)" : ""));
        text.AppendLine("For the graphics card (a BIOS can't control it; e.g. MSI Afterburner's custom fan curve)");
        foreach (var g in result.Groups.Where(g => g.Follows == Component.GpuCore))
            text.AppendLine($"   {g.Name,-30} {string.Join("   ", g.Curve.Select(p => $"{p.Temperature:0} °C {p.Percent:0} %"))}");
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
}
