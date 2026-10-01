using System.Text;
using AutoFantic.Core.Analysis;
using AutoFantic.Core.Logging;

namespace AutoFantic.Spike;

/// <summary>
/// Replays a "watch --csv" log (e.g. an evening of gaming) through the experiment rules: how
/// often would AuFantic have been allowed to learn, and what stopped it the rest of the time.
/// Reads only the file: no hardware, no admin rights.
/// </summary>
internal static class AnalyzeCommand
{
    // rough size of the learning job: fan groups × fan steps × repeats per cell, for the one load class games mostly have
    private const int FanGroups = 4, FanSteps = 6, RepeatsPerCell = 3;

    private const int MaxListedAttempts = 20;

    public static int Run(string[] args)
    {
        var options = new Options(args);
        if (options.Positional.Count != 1)
        {
            Console.Error.WriteLine("Usage: analyze <watch.csv> [--profile 80] [--experiment 6] [--cooldown 10] [--out analysis.txt]");
            return 2;
        }

        string path = options.Positional[0];
        var rules = new ExperimentRules
        {
            ProfileLimit = options.GetDouble("--profile", 80),
            ExperimentLength = TimeSpan.FromMinutes(Math.Clamp(options.GetDouble("--experiment", 6), 1, 30)),
            Cooldown = TimeSpan.FromMinutes(Math.Clamp(options.GetDouble("--cooldown", 10), 0, 120)),
        };

        var log = SensorLogReader.Read(path).ToList();
        if (log.Count < 2)
        {
            Console.Error.WriteLine("The log has fewer than two samples.");
            return 2;
        }

        var report = LearningOpportunities.Analyze(log, rules);
        string text = Build(path, log, report, rules);
        Console.Write(text);

        if (options.Get("--out") is { } outPath)
        {
            File.WriteAllText(outPath, text, Encoding.UTF8);
            Console.WriteLine($"Report written to {Path.GetFullPath(outPath)}");
        }
        return 0;
    }

    internal static string Build(string path, List<LoggedSample> log, OpportunityReport report, ExperimentRules rules)
    {
        var text = new StringBuilder();
        var first = log[0].Snapshot.Time;
        var last = log[^1].Snapshot.Time;
        text.AppendLine($"AuFantic analysis · {Path.GetFileName(path)}");
        text.AppendLine($"Recorded {Min(report.Total)} ({log.Count} samples), {first:yyyy-MM-dd HH:mm} → {last:HH:mm}");
        text.AppendLine();

        text.AppendLine("Load classes (from CPU / GPU load)");
        foreach (var (cls, time) in report.TimeByClass.Where(kv => kv.Value > TimeSpan.Zero))
            text.AppendLine($"   {Name(cls),-12} {Min(time),10}  {Share(time, report.Total),4}");
        if (report.UnknownClassTime > TimeSpan.Zero)
            text.AppendLine($"   {"unknown",-12} {Min(report.UnknownClassTime),10}  {Share(report.UnknownClassTime, report.Total),4}   (no CPU/GPU load sensor in the log)");
        text.AppendLine();

        text.AppendLine("Programs in the foreground");
        foreach (var (name, time) in report.TimeByForeground.Take(8))
            text.AppendLine($"   {Truncate(name, 28),-28} {Min(time),10}");
        text.AppendLine();

        var t = report.UnderLoad;
        text.AppendLine($"Typical under load (median): CPU {T(t.Cpu)}, GPU core {T(t.GpuCore)}, hotspot {T(t.GpuHotspot)}, memory {T(t.GpuMemory)}");
        text.AppendLine();

        var active = report.ActiveTime;
        text.AppendLine($"Experiments, replayed with the rules from \"How it learns\":");
        text.AppendLine($"   load stable ≥ {rules.StableFor.TotalMinutes:0} min (power ±{rules.MaxPowerDeviation:0%}), {rules.Cooldown.TotalMinutes:0} min cooldown, {rules.ExperimentLength.TotalMinutes:0} min per experiment,");
        text.AppendLine($"   discarded on a load change or a power jump > {rules.AbortPowerJump:0%}. A fan is only slowed down with ≥ {rules.StartHeadroom:0} °C to spare");
        text.AppendLine($"   below Max {rules.ProfileLimit:0} and the safety limits; otherwise it is sped up (only cools, always allowed).");
        text.AppendLine();

        var completed = report.Attempts.Where(a => a.Completed).ToList();
        var discarded = report.Attempts.Where(a => !a.Completed).ToList();
        text.AppendLine($"   completed   {completed.Count}   ({completed.Count(a => a.Direction == ExperimentDirection.Slower)} slower, {completed.Count(a => a.Direction == ExperimentDirection.Faster)} faster)");
        text.AppendLine($"   discarded   {discarded.Count}" + (discarded.Count == 0 ? "" :
            "   (" + string.Join(", ", discarded.GroupBy(a => a.AbortReason).Select(g => $"{g.Key} ×{g.Count()}")) + ")"));

        double perHour = active.TotalHours > 0 ? completed.Count / active.TotalHours : 0;
        text.AppendLine($"   → {perHour:0.0} completed experiments per hour under load ({Min(active)} under load)");

        int needed = FanGroups * FanSteps * RepeatsPerCell;
        text.AppendLine(perHour > 0
            ? $"   → learning one load class ({FanGroups} fan groups × {FanSteps} steps × {RepeatsPerCell} repeats = {needed} experiments) would take about {needed / perHour:0} hours of this kind of use"
            : "   → at this rate AuFantic would never learn: see below what blocked it");
        text.AppendLine();

        if (report.Attempts.Count > 0)
        {
            text.AppendLine("   time      length  fan     class        program                      result");
            foreach (var a in report.Attempts.Take(MaxListedAttempts))
                text.AppendLine($"   {a.Start:HH:mm:ss}  {a.Length:m\\:ss}   {Name(a.Direction),-7} {Name(a.Class),-12} {Truncate(a.Foreground ?? "(unknown)", 28),-28} {a.AbortReason ?? "completed"}");
            if (report.Attempts.Count > MaxListedAttempts)
                text.AppendLine($"   … and {report.Attempts.Count - MaxListedAttempts} more");
            text.AppendLine();
        }

        text.AppendLine("What kept it from experimenting the rest of the time");
        text.AppendLine($"   {"experiment running",-22} {Min(report.Experimenting),10}  {Share(report.Experimenting, report.Total),4}");
        foreach (var (blocker, time) in report.Blocked.OrderByDescending(kv => kv.Value))
            text.AppendLine($"   {Name(blocker),-22} {Min(time),10}  {Share(time, report.Total),4}");
        text.AppendLine();
        return text.ToString();
    }

    private static string Name(LoadClass cls) => cls switch
    {
        LoadClass.CpuHeavy => "CPU-heavy",
        LoadClass.GpuHeavy => "GPU-heavy",
        _ => cls.ToString(),
    };

    private static string Name(Blocker blocker) => blocker switch
    {
        Blocker.IdleOrUnknownLoad => "idle / unknown load",
        Blocker.LoadNotStable => "load not stable",
        Blocker.AtSafetyLimit => "near a safety limit",
        Blocker.Cooldown => "cooldown",
        _ => blocker.ToString(),
    };

    private static string Name(ExperimentDirection direction) =>
        direction == ExperimentDirection.Slower ? "slower" : "faster";

    private static string T(double? celsius) => celsius is { } c ? $"{c:0} °C" : "–";

    private static string Min(TimeSpan time) => $"{time.TotalMinutes:0.0} min";

    private static string Share(TimeSpan part, TimeSpan total) =>
        total > TimeSpan.Zero ? $"{part / total:0%}" : "–";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
