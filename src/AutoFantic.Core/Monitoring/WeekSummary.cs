namespace AutoFantic.Core.Monitoring;

/// <summary>
/// The last 7 days in a few numbers, for the top of the Reports page: how long was played, what
/// most, and how warm it got at most (whatever the PC did, not only in games).
/// </summary>
/// <param name="Played">The length of every session that ended in these 7 days, added up.</param>
/// <param name="Mostly">The program that was played longest (as shown, <see cref="Monitoring.Sessions.Pretty"/>); null without a session.</param>
/// <param name="PlayedBefore">The same for the 7 days before, to say "more" or "less"; null if the history doesn't reach back that far.</param>
/// <param name="GpuMax">The highest GPU temperature; null if none was recorded. The same for the hotspot and the CPU.</param>
public sealed record WeekSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Played,
    int Sessions,
    string? Mostly,
    TimeSpan? PlayedBefore,
    double? GpuMax,
    double? HotspotMax,
    double? CpuMax)
{
    public static readonly TimeSpan Length = TimeSpan.FromDays(7);

    public static WeekSummary Of(HistoryStore store, DateTimeOffset now)
    {
        var from = now - Length;
        var sessions = store.Sessions();
        var week = sessions.Where(s => s.End > from && s.End <= now).ToList();
        var before = sessions.Where(s => s.End > from - Length && s.End <= from).ToList();
        // "the week before" only counts if there was a week before: something older than these 7 days is kept
        bool reachesBack = sessions.Any(s => s.End <= from) || store.Query(HistoryRecorder.CpuTemp.Key, from - Length, from, maxPoints: 1, minSeconds: 60).Count > 0;

        double? Max(Series series) =>
            store.Query(series.Key, from, now, maxPoints: 200, minSeconds: 60) is { Count: > 0 } points ? points.Max(p => p.Max) : null;

        return new WeekSummary(from, now,
            TimeSpan.FromSeconds(week.Sum(s => s.Length.TotalSeconds)),
            week.Count,
            week.GroupBy(s => Monitoring.Sessions.Pretty(s.Program)).OrderByDescending(g => g.Sum(s => s.Length.TotalSeconds)).FirstOrDefault()?.Key,
            reachesBack ? TimeSpan.FromSeconds(before.Sum(s => s.Length.TotalSeconds)) : null,
            Max(HistoryRecorder.GpuTemp), Max(HistoryRecorder.GpuHotspot), Max(HistoryRecorder.CpuTemp));
    }
}
