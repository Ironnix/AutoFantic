using System.Text.Json;

namespace AutoFantic.Core.Monitoring;

/// <summary>A fan's average RPM at one speed step, and how many steady minutes it rests on.</summary>
public readonly record struct FanStep(double Rpm, int Minutes);

/// <summary>One day of one fan group: its average RPM per 5 % speed step, from the steady minutes. Kept for good.</summary>
/// <param name="Fan">The group's key (its control ids, see <see cref="Calibration.MeasurementStore.Key(Calibration.FanGroup)"/>).</param>
/// <param name="Steps">Per speed step (30, 35, … %).</param>
public sealed record FanDay(DateOnly Day, string Fan, IReadOnlyDictionary<int, FanStep> Steps)
{
    public int Minutes => Steps.Values.Sum(s => s.Minutes);
}

/// <summary>How a fan does against its first week.</summary>
/// <param name="Change">% it turns faster (+) or slower (−) than in its first week at the same speed setting; null without a comparison.</param>
/// <param name="FirstWeekDays">Days of the first week so far (7 = complete).</param>
/// <param name="Minutes">Steady minutes in the last 7 days.</param>
public sealed record FanWearResult(double? Change, int FirstWeekDays, int Minutes)
{
    public bool Learning => FirstWeekDays < 7;
}

/// <summary>
/// "Is a fan wearing out?" At the same speed setting a fan with a worn bearing, or dirt on its
/// blades and in its bearing, turns slower (and often louder). So for every steady minute of the
/// history (the setting and the RPM stayed the same) the RPM is kept per 5 % step, every day, and
/// compared with the fan's first week: the same steps, the same settings, only the fan changed.
/// A cleaned or new fan starts again with a new first week. Graphics card fans often hold their
/// speed themselves, so their wear shows late. Pure computation; the window and the tests use it.
/// </summary>
public static class FanWear
{
    /// <summary>% per speed step.</summary>
    public const int StepSize = 5;

    /// <summary>A step counts only with at least this many minutes in the first week (and on a day).</summary>
    public const int MinStepMinutes = 10;

    /// <summary>From this much slower (%) it's worth a look: clean it, listen to it.</summary>
    public const double Check = -8;

    /// <summary>From this much slower (%) it's probably worn or blocked.</summary>
    public const double Worn = -15;

    /// <summary>
    /// A day's steps from minute values of one fan: minutes where its setting (%) and its RPM stayed
    /// about the same and it turned. A fan that is off, speeding up or slowing down doesn't count.
    /// </summary>
    public static IReadOnlyDictionary<int, FanStep> Steps(IEnumerable<(HistoryPoint Percent, HistoryPoint Rpm)> minutes)
    {
        var sums = new Dictionary<int, (double Rpm, int Minutes)>();
        foreach (var (percent, rpm) in minutes)
        {
            if (percent.Avg < 1 || percent.Max - percent.Min > 2 || rpm.Avg < 100 || rpm.Max - rpm.Min > 0.15 * rpm.Avg)
                continue;
            int step = (int)Math.Round(percent.Avg / StepSize) * StepSize;
            var sum = sums.GetValueOrDefault(step);
            sums[step] = (sum.Rpm + rpm.Avg, sum.Minutes + 1);
        }
        return sums.ToDictionary(s => s.Key, s => new FanStep(s.Value.Rpm / s.Value.Minutes, s.Value.Minutes));
    }

    /// <summary>The steady minutes of one fan between two times, from the history.</summary>
    public static IReadOnlyDictionary<int, FanStep> Steps(HistoryStore store, Series percent, Series rpm, DateTimeOffset from, DateTimeOffset to)
    {
        int count = (int)Math.Max(1, (to - from).TotalMinutes) + 2;
        var rpms = store.Query(rpm.Key, from, to, maxPoints: count, minSeconds: 60).ToDictionary(p => p.Time);
        return Steps(store.Query(percent.Key, from, to, maxPoints: count, minSeconds: 60)
            .Where(p => rpms.ContainsKey(p.Time))
            .Select(p => (p, rpms[p.Time])));
    }

    /// <summary>
    /// The fan's first week: the first 7 days from <paramref name="since"/> on, counted from the
    /// first day it ran steadily. Per step, weighted by minutes. Empty before it ran.
    /// </summary>
    public static (IReadOnlyDictionary<int, FanStep> Steps, DateOnly? Start) FirstWeek(IEnumerable<FanDay> days, DateOnly since)
    {
        var mine = days.Where(d => d.Day >= since && d.Minutes > 0).OrderBy(d => d.Day).ToList();
        if (mine.Count == 0)
            return (new Dictionary<int, FanStep>(), null);
        var start = mine[0].Day;
        return (Combine(mine.Where(d => d.Day < start.AddDays(7))), start);
    }

    /// <summary>Several days as one: per step, the RPM weighted by minutes.</summary>
    public static IReadOnlyDictionary<int, FanStep> Combine(IEnumerable<FanDay> days) =>
        days.SelectMany(d => d.Steps)
            .GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => new FanStep(g.Sum(s => s.Value.Rpm * s.Value.Minutes) / g.Sum(s => s.Value.Minutes), g.Sum(s => s.Value.Minutes)));

    /// <summary>
    /// % faster (+) or slower (−) than the first week, over the steps both know well enough,
    /// weighted by minutes; null if they have no step in common.
    /// </summary>
    public static double? Change(IReadOnlyDictionary<int, FanStep> steps, IReadOnlyDictionary<int, FanStep> firstWeek)
    {
        double weighted = 0, minutes = 0;
        foreach (var (step, now) in steps)
        {
            if (now.Minutes < MinStepMinutes || !firstWeek.TryGetValue(step, out var then) || then.Minutes < MinStepMinutes || then.Rpm <= 0)
                continue;
            weighted += now.Rpm / then.Rpm * now.Minutes;
            minutes += now.Minutes;
        }
        return minutes > 0 ? (weighted / minutes - 1) * 100 : null;
    }

    /// <summary>The last 7 days against the fan's first week (from <paramref name="since"/>, after a cleaning or a new fan).</summary>
    public static FanWearResult Now(IReadOnlyList<FanDay> days, DateOnly since, DateOnly today)
    {
        var (firstWeek, start) = FirstWeek(days, since);
        var last = days.Where(d => d.Day >= since && d.Day > today.AddDays(-7) && d.Day <= today).ToList();
        int minutes = last.Sum(d => d.Minutes);
        if (start is not { } first)
            return new FanWearResult(null, 0, minutes);
        int weekDays = Math.Clamp(today.DayNumber - first.DayNumber, 0, 7);
        // compared only once the last 7 days lie after the first week
        bool after = first.AddDays(7) <= today.AddDays(-6);
        return new FanWearResult(after ? Change(Combine(last), firstWeek) : null, weekDays, minutes);
    }

    /// <summary>Every day against the first week (0 = as then), for the chart: day, % change, minutes.</summary>
    public static IReadOnlyList<(DateOnly Day, double Change, int Minutes)> Daily(IReadOnlyList<FanDay> days, DateOnly since)
    {
        var (firstWeek, start) = FirstWeek(days, since);
        if (start is null)
            return [];
        return [.. days.Where(d => d.Day >= start && d.Minutes > 0).OrderBy(d => d.Day)
            .Select(d => (d.Day, Change: Change(d.Steps, firstWeek), d.Minutes))
            .Where(d => d.Change is not null)
            .Select(d => (d.Day, d.Change!.Value, d.Minutes))];
    }

    /// <summary>For a chart: one point per day, or with <paramref name="weekly"/> one per week (its average, with the lowest and highest day).</summary>
    public static IReadOnlyList<HistoryPoint> Points(IEnumerable<(DateOnly Day, double Change, int Minutes)> daily, bool weekly)
    {
        static DateTimeOffset At(DateOnly day, double hours) => new(day.ToDateTime(TimeOnly.MinValue).AddHours(hours));
        if (!weekly)
            return [.. daily.Select(d => new HistoryPoint(At(d.Day, 12), d.Change, d.Change, d.Change))];
        return [.. daily.GroupBy(d => d.Day.DayNumber / 7) // day 0 (1 Jan 0001) was a Monday
            .Select(week => new HistoryPoint(At(DateOnly.FromDayNumber(week.Key * 7), 3.5 * 24),
                week.Sum(d => d.Change * d.Minutes) / week.Sum(d => d.Minutes), week.Min(d => d.Change), week.Max(d => d.Change)))];
    }
}

/// <summary>Per fan (by key): since when its first week counts (after "Start again"), and the last warning given.</summary>
public sealed record FanWearSettings(IReadOnlyDictionary<string, DateOnly>? Since = null, IReadOnlyDictionary<string, string>? Warned = null)
{
    public const string FileName = "fan-wear.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public DateOnly SinceFor(string fan) => Since?.GetValueOrDefault(fan) ?? DateOnly.MinValue;

    /// <summary>After cleaning or replacing it: a new first week from today.</summary>
    public FanWearSettings StartAgain(string fan, DateOnly today) =>
        this with
        {
            Since = new Dictionary<string, DateOnly>(Since ?? new Dictionary<string, DateOnly>()) { [fan] = today },
            Warned = (Warned ?? new Dictionary<string, string>()).Where(w => w.Key != fan).ToDictionary(),
        };

    public FanWearSettings WithWarning(string fan, string level) =>
        this with { Warned = new Dictionary<string, string>(Warned ?? new Dictionary<string, string>()) { [fan] = level } };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static FanWearSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FanWearSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
