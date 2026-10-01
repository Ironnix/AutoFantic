using System.Text;
using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Reports;

/// <summary>The calibration result as plain text (runs\calibration-*.txt), including why each setting was chosen.</summary>
public static class CalibrationReport
{
    public static string Text(CalibrationResult result, FanInventory inventory, MeasurementStore store, FansOffResult? fansOff,
        bool fansOffFresh, IReadOnlyList<string> skipped, string stamp)
    {
        var groups = inventory.Groups().ToList();
        var preset = Preset.For(result.Profile);
        var profile = preset.Profile;
        var observations = store.ObservationsFor(groups);
        int measured = observations.Count(o => o.Weight >= 1), imported = observations.Count - measured;
        double topCpu = store.Calibrations.Max(c => c.TopCpu), topGpu = store.Calibrations.Max(c => c.TopGpu);

        var text = new StringBuilder();
        text.AppendLine($"AuFantic calibration · {stamp} · room {result.Ambient:0} °C · {result.Profile}");
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
            : "model fits the measurements within " + string.Join(", ", (result.Rms ?? new Dictionary<Component, double>()).Select(kv => $"{CalibrationInsights.Name(kv.Key)} ±{kv.Value:0.0} °C"));
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
        text.AppendLine($"   a fan is only off while CPU ≤ {result.StopCpuWatts:0} W, GPU ≤ {result.StopGpuWatts:0} W, the part it cools ≤ {Control.CurveController.OffBelow:0} °C and nothing above {Control.CurveController.OthersBelow:0} °C");
        text.AppendLine();

        text.AppendLine($"Fan speeds for {result.Profile}, per load level");
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
                line.Append("  ← target not reachable");
            text.AppendLine(line.ToString());
        }
        text.AppendLine("   \"high\" = the highest CPU and GPU load measured, both at once (worst case); \"beyond\" = 30 % more, the model extrapolates");
        text.AppendLine("   noise: dB compared with all fans at 100 % (−10 dB sounds about half as loud)");
        text.AppendLine();

        text.AppendLine("Why these settings");
        foreach (var level in CalibrationInsights.Levels(result, inventory, profile))
            text.AppendLine($"   {level.Label,-8} {level.Why}");
        text.AppendLine();

        text.AppendLine("Runs (newest first) and the part closest to its limit in each");
        foreach (var run in CalibrationInsights.Runs(store, groups, profile))
        {
            string speeds = string.Join(" · ", run.Speeds.Select(s => $"{s.Percent:0}"));
            string limit = $"{CalibrationInsights.Name(run.Bottleneck)} {run.Final.GetValueOrDefault(run.Bottleneck):0} °C ({(run.Headroom >= 0 ? $"{run.Headroom:0} °C below" : $"{-run.Headroom:0} °C above")} its limit)";
            text.AppendLine($"   {run.Time:dd.MM. HH:mm}  {Clip(run.Source, 22),-22}  fans {speeds,-18}  {run.CpuPower,4:0} W / {run.GpuPower,3:0} W  CPU {T(run.Final, Component.Cpu),7}  GPU {T(run.Final, Component.GpuCore),7}  bottleneck: {limit}{(run.Imported ? "  (imported)" : "")}");
        }
        text.AppendLine();

        text.AppendLine("Fan curves (fan % by temperature)");
        foreach (var g in result.Groups)
        {
            string points = string.Join(",  ", g.Curve.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %"));
            string off = g.OffAt.Count > 0 ? $"off at {string.Join(" + ", g.OffAt)} load, otherwise " : "";
            text.AppendLine($"   {g.Name,-30} {off}follows {CalibrationInsights.Name(g.Follows)}:  {points}");
        }
        text.AppendLine();

        var high = result.Table.First(r => r.Label == "high");
        text.AppendLine(high.MeetsTarget
            ? $"{result.Profile} holds at the highest load measured: CPU {T(high.Temperatures, Component.Cpu)}, GPU {T(high.Temperatures, Component.GpuCore)}."
            : $"{result.Profile} is not reachable at the highest load measured; the fans reach CPU {T(high.Temperatures, Component.Cpu)}, GPU {T(high.Temperatures, Component.GpuCore)}.");
        text.AppendLine();
        return text.ToString();
    }

    public static string Role(CalibratedGroup g) =>
        g.CpuEffect < 1 && g.GpuEffect < 1 ? Texts.T("barely any effect")
        : g.CpuEffect >= 2 * g.GpuEffect ? Texts.T("cools the CPU")
        : g.GpuEffect >= 2 * g.CpuEffect ? Texts.T("cools the GPU")
        : Texts.T("case airflow, helps both");

    private static string Short(FanGroup g) => g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}";

    private static string T(IReadOnlyDictionary<Component, double> temps, Component c) =>
        temps.TryGetValue(c, out double v) ? $"{v:0.0} °C" : "–";

    private static string Signed(double v) => $"{(v >= 0 ? "+" : "")}{v:0.0} °C".PadLeft(8);

    private static string Noise(double db) => double.IsFinite(db) ? $"{Math.Round(db) + 0.0:0} dB" : "silent";

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>Works the result out from everything stored and writes it: calibration.json, the text report and the page.</summary>
public static class CalibrationFiles
{
    public const string Store = "measurements.json";
    public const string Result = "calibration.json";
    public const string Page = "calibration.html";
    public const string Inventory = "fans.json";
    public const string FansOff = "fans-off.json";
    public const string Curves = "curves.json";

    /// <summary>Null if there aren't enough stored runs for these fans.</summary>
    public static (CalibrationResult Result, string Report)? Recalculate(string folder, FanInventory inventory, MeasurementStore store, Preset preset,
        double ambient, FansOffResult? fansOff, bool fansOffFresh = false, IReadOnlyList<string>? skipped = null)
    {
        var result = CalibrationCalculator.Calculate(store, inventory, preset.Profile, ambient, fansOff);
        if (result is null)
            return null;

        string stamp = $"{DateTime.Now:yyyyMMdd-HHmm}";
        string report = CalibrationReport.Text(result, inventory, store, fansOff, fansOffFresh, skipped ?? [], stamp);
        File.WriteAllText(Path.Combine(folder, $"calibration-{stamp}.txt"), report, Encoding.UTF8);
        result.Save(Path.Combine(folder, Result));
        var groups = inventory.Groups().ToList();
        int measured = store.ObservationsFor(groups).Count(o => o.Weight >= 1);
        CalibrationPage.Write(Path.Combine(folder, Page), result, groups, fansOff, measured);
        return (result, report);
    }

    /// <summary>The stored measurements; the first time, an older saved result is imported into it.</summary>
    public static MeasurementStore LoadStore(string folder, IReadOnlyList<FanGroup> groups, Action<string>? log = null)
    {
        string path = Path.Combine(folder, Store);
        if (MeasurementStore.Load(path) is { } store)
            return store;

        if (CalibrationResult.Load(Path.Combine(folder, Result)) is { } old)
        {
            var imported = MeasurementStore.Import(old, groups, "earlier calibration");
            if (imported.Runs.Count > 0)
            {
                imported.Save(path);
                log?.Invoke(Texts.T($"Imported the calibration of {old.Created:dd.MM. HH:mm} ({imported.Runs.Count} runs, half weight) as a starting point."));
                return imported;
            }
        }
        return MeasurementStore.Empty;
    }
}
