using System.Text.Json;
using Microsoft.Data.Sqlite;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Monitoring;

public enum SeriesKind
{
    Temperature,
    Power,
    Load,
    FanPercent,
    FanRpm,
}

/// <summary>One thing the monitor keeps a history of, e.g. "cpu.temp" (CPU, °C) or "fan./lpc/…/control/0.rpm".</summary>
public sealed record Series(string Key, string Name, SeriesKind Kind)
{
    public string Unit => Kind switch
    {
        SeriesKind.Temperature => "°C",
        SeriesKind.Power => "W",
        SeriesKind.FanRpm => T("rpm"),
        _ => "%",
    };
}

/// <summary>A stretch of time: the average, lowest and highest value in it.</summary>
public readonly record struct HistoryPoint(DateTimeOffset Time, double Avg, double Min, double Max);

/// <summary>One day of <see cref="CoolingHealth"/>: kept for a year, so a slow change (dust) shows as a trend.</summary>
/// <param name="Calibration">Which calibration it was measured against (its time): a new calibration starts a new baseline.</param>
public sealed record HealthDay(DateOnly Day, DateTimeOffset Calibration, HealthResult Result);

/// <summary>
/// The monitor's history in a small SQLite database (history.db). Every value is kept at three
/// resolutions, each point with its average, lowest and highest value, so a short spike is still
/// visible in a month's view: 5 seconds for 2 days, 1 minute for 30 days, 1 hour for about a year.
/// Older points are deleted as time goes on, so the file stays at a few tens of MB at most. Writes
/// happen when a 5-second stretch is complete (a handful of rows), never every second. Thread-safe:
/// the fan control writes, the window reads. Also keeps the game sessions (<see cref="GameSession"/>,
/// for the reports), the cooling health of every day and every fan's RPM per speed step and day
/// (<see cref="FanWear"/>), all small and kept for good.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const string FileName = "history.db";

    /// <summary>Resolution (seconds per point) and how long it's kept, finest first.</summary>
    public static readonly (int Seconds, TimeSpan Keep)[] Tiers =
    [
        (5, TimeSpan.FromDays(2)),
        (60, TimeSpan.FromDays(30)),
        (3600, TimeSpan.FromDays(400)),
    ];

    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Id, Series Series)> _series = [];
    private readonly Dictionary<(int Tier, int Id), Bucket> _open = [];
    private DateTimeOffset _prunedAt;
    private bool _disposed;

    private sealed class Bucket(long start)
    {
        public long Start = start;
        public double Sum, Min = double.PositiveInfinity, Max = double.NegativeInfinity;
        public int Count;

        public void Add(double value)
        {
            Sum += value;
            Count++;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }
    }

    /// <param name="path">The database file; null keeps it in memory (tests).</param>
    public HistoryStore(string? path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? ":memory:", Pooling = false }.ToString());
        _db.Open();
        // small cache (it runs all day next to the clock), and a journal that survives a crash
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-512;");
        Execute("""
            CREATE TABLE IF NOT EXISTS series (id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, kind INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS points (tier INTEGER NOT NULL, series INTEGER NOT NULL, t INTEGER NOT NULL,
                avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL, PRIMARY KEY (tier, series, t)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY, program TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL,
                preset TEXT NOT NULL, stats TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS health (day INTEGER PRIMARY KEY, calibration INTEGER NOT NULL, result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS fans (day INTEGER NOT NULL, fan TEXT NOT NULL, steps TEXT NOT NULL, PRIMARY KEY (day, fan)) WITHOUT ROWID;
            """);
        using var read = Command("SELECT id, key, name, kind FROM series");
        using var reader = read.ExecuteReader();
        while (reader.Read())
            _series[reader.GetString(1)] = (reader.GetInt32(0), new Series(reader.GetString(1), reader.GetString(2), (SeriesKind)reader.GetInt32(3)));
    }

    /// <summary>Every series ever recorded, in the order they first appeared.</summary>
    public IReadOnlyList<Series> AllSeries()
    {
        lock (_lock)
            return [.. _series.Values.OrderBy(s => s.Id).Select(s => s.Series)];
    }

    /// <summary>One sample of several series (about once per second). NaN and infinite values are skipped.</summary>
    public void Add(DateTimeOffset time, IEnumerable<(Series Series, double Value)> values)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            long now = time.ToUnixTimeSeconds();
            var done = new List<(int Tier, int Id, Bucket Bucket)>();
            foreach (var (series, value) in values)
            {
                if (!double.IsFinite(value))
                    continue;
                int id = IdOf(series);
                for (int tier = 0; tier < Tiers.Length; tier++)
                {
                    long start = now - (now % Tiers[tier].Seconds);
                    if (!_open.TryGetValue((tier, id), out var bucket) || bucket.Start != start)
                    {
                        if (bucket is not null)
                            done.Add((tier, id, bucket)); // its stretch is over: written once, complete
                        _open[(tier, id)] = bucket = new Bucket(start);
                    }
                    bucket.Add(value);
                }
            }
            if (done.Count > 0)
                Write(done);
            if (time - _prunedAt >= PruneEvery)
                Prune(time);
        }
    }

    /// <summary>
    /// The history of one series between two times, at most about <paramref name="maxPoints"/>
    /// points: from the finest resolution that still reaches back that far, merged further if needed.
    /// Includes the stretch that is still running.
    /// </summary>
    /// <param name="minSeconds">Points of at least this many seconds (e.g. 60: whole minutes, steadier than 5 seconds).</param>
    public IReadOnlyList<HistoryPoint> Query(string key, DateTimeOffset from, DateTimeOffset to, int maxPoints = 600, int minSeconds = 0)
    {
        lock (_lock)
        {
            if (_disposed || !_series.TryGetValue(key, out var series))
                return [];
            WriteOpen();

            int tier = Array.FindIndex(Tiers, t => t.Seconds >= minSeconds && to - t.Keep <= from);
            if (tier < 0)
                tier = Tiers.Length - 1;
            long span = Math.Max(1, (long)(to - from).TotalSeconds);
            long seconds = Tiers[tier].Seconds;
            long bucket = Math.Max(seconds, (span / maxPoints + seconds - 1) / seconds * seconds);

            using var query = Command("""
                SELECT (t / $b) * $b AS bt, AVG(avg), MIN(min), MAX(max) FROM points
                WHERE tier = $tier AND series = $series AND t >= $from AND t <= $to
                GROUP BY bt ORDER BY bt
                """);
            query.Parameters.AddWithValue("$b", bucket);
            query.Parameters.AddWithValue("$tier", tier);
            query.Parameters.AddWithValue("$series", series.Id);
            query.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds() - seconds);
            query.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var reader = query.ExecuteReader();
            var points = new List<HistoryPoint>();
            while (reader.Read())
                points.Add(new HistoryPoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
            return points;
        }
    }

    /// <summary>
    /// Gives <paramref name="to"/> the history of other series where it has none of its own: fans
    /// that run together from now on carry on from what each of them did before (their average),
    /// and fans taken apart again from what they did together. So the charts, the cooling health and
    /// the worn fan detection don't start from nothing. Series that were never recorded are skipped.
    /// </summary>
    public void Carry(IEnumerable<string> from, Series to)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var sources = from.Where(key => key != to.Key && _series.ContainsKey(key)).Select(key => _series[key].Id).ToList();
            if (sources.Count == 0)
                return;
            WriteOpen();
            using var copy = Command($"""
                INSERT OR IGNORE INTO points (tier, series, t, avg, min, max)
                SELECT tier, $to, t, AVG(avg), MIN(min), MAX(max) FROM points
                WHERE series IN ({string.Join(", ", sources)}) GROUP BY tier, t
                """);
            copy.Parameters.AddWithValue("$to", IdOf(to));
            copy.ExecuteNonQuery();
        }
    }

    /// <summary>A session that ended; its values are worked out here from the history (average and highest of every series).</summary>
    public GameSession AddSession(string program, DateTimeOffset start, DateTimeOffset end, string preset)
    {
        var stats = new Dictionary<string, SessionStat>();
        foreach (var series in AllSeries())
        {
            var points = Query(series.Key, start, end, maxPoints: 2000);
            if (points.Count > 0)
                stats[series.Key] = new SessionStat(points.Average(p => p.Avg), points.Max(p => p.Max));
        }
        var session = new GameSession(program, start, end, preset, stats);
        lock (_lock)
        {
            if (_disposed)
                return session;
            using var insert = Command("INSERT INTO sessions (program, start, end, preset, stats) VALUES ($program, $start, $end, $preset, $stats)");
            insert.Parameters.AddWithValue("$program", program);
            insert.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$end", end.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$preset", preset);
            insert.Parameters.AddWithValue("$stats", JsonSerializer.Serialize(stats));
            insert.ExecuteNonQuery();
        }
        return session;
    }

    /// <summary>Every session, newest first.</summary>
    public IReadOnlyList<GameSession> Sessions()
    {
        lock (_lock)
        {
            if (_disposed)
                return [];
            using var query = Command("SELECT program, start, end, preset, stats FROM sessions ORDER BY start DESC");
            using var reader = query.ExecuteReader();
            var sessions = new List<GameSession>();
            while (reader.Read())
                sessions.Add(new GameSession(reader.GetString(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    reader.GetString(3), JsonSerializer.Deserialize<Dictionary<string, SessionStat>>(reader.GetString(4)) ?? []));
            return sessions;
        }
    }

    public void SaveHealth(HealthDay day)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            using var upsert = Command("INSERT OR REPLACE INTO health (day, calibration, result) VALUES ($day, $calibration, $result)");
            upsert.Parameters.AddWithValue("$day", day.Day.DayNumber);
            upsert.Parameters.AddWithValue("$calibration", day.Calibration.ToUnixTimeSeconds());
            upsert.Parameters.AddWithValue("$result", JsonSerializer.Serialize(day.Result));
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>Every day worked out so far, oldest first.</summary>
    public IReadOnlyList<HealthDay> HealthDays()
    {
        lock (_lock)
        {
            if (_disposed)
                return [];
            using var query = Command("SELECT day, calibration, result FROM health ORDER BY day");
            using var reader = query.ExecuteReader();
            var days = new List<HealthDay>();
            while (reader.Read())
                if (JsonSerializer.Deserialize<HealthResult>(reader.GetString(2)) is { } result)
                    days.Add(new HealthDay(DateOnly.FromDayNumber(reader.GetInt32(0)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), result));
            return days;
        }
    }

    /// <summary>One fan's day for the worn fan detection (<see cref="FanWear"/>).</summary>
    public void SaveFanDay(FanDay day)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            using var upsert = Command("INSERT OR REPLACE INTO fans (day, fan, steps) VALUES ($day, $fan, $steps)");
            upsert.Parameters.AddWithValue("$day", day.Day.DayNumber);
            upsert.Parameters.AddWithValue("$fan", day.Fan);
            upsert.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(day.Steps));
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>Every fan's days worked out so far, oldest first.</summary>
    public IReadOnlyList<FanDay> FanDays()
    {
        lock (_lock)
        {
            if (_disposed)
                return [];
            using var query = Command("SELECT day, fan, steps FROM fans ORDER BY day");
            using var reader = query.ExecuteReader();
            var days = new List<FanDay>();
            while (reader.Read())
                days.Add(new FanDay(DateOnly.FromDayNumber(reader.GetInt32(0)), reader.GetString(1),
                    JsonSerializer.Deserialize<Dictionary<int, FanStep>>(reader.GetString(2)) ?? []));
            return days;
        }
    }

    /// <summary>Writes the stretches still running too (before closing, before reading).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_disposed)
                WriteOpen();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            WriteOpen();
            _disposed = true;
            _db.Dispose();
        }
    }

    // ── inside the lock ────────────────────────────────────────────────────────────────

    private int IdOf(Series series)
    {
        if (_series.TryGetValue(series.Key, out var known))
        {
            if (known.Series != series)
            {
                // a fan got a new name, say: keep the history, show the new name
                using var rename = Command("UPDATE series SET name = $name, kind = $kind WHERE id = $id");
                rename.Parameters.AddWithValue("$name", series.Name);
                rename.Parameters.AddWithValue("$kind", (int)series.Kind);
                rename.Parameters.AddWithValue("$id", known.Id);
                rename.ExecuteNonQuery();
                _series[series.Key] = (known.Id, series);
            }
            return known.Id;
        }
        using var insert = Command("INSERT INTO series (key, name, kind) VALUES ($key, $name, $kind) RETURNING id");
        insert.Parameters.AddWithValue("$key", series.Key);
        insert.Parameters.AddWithValue("$name", series.Name);
        insert.Parameters.AddWithValue("$kind", (int)series.Kind);
        int id = Convert.ToInt32(insert.ExecuteScalar());
        _series[series.Key] = (id, series);
        return id;
    }

    private void WriteOpen() => Write([.. _open.Select(kv => (kv.Key.Tier, kv.Key.Id, kv.Value))]);

    private void Write(IReadOnlyList<(int Tier, int Id, Bucket Bucket)> rows)
    {
        if (rows.Count == 0)
            return;
        using var transaction = _db.BeginTransaction();
        using var upsert = Command("INSERT OR REPLACE INTO points (tier, series, t, avg, min, max) VALUES ($tier, $series, $t, $avg, $min, $max)");
        upsert.Transaction = transaction;
        var tier = upsert.Parameters.Add("$tier", SqliteType.Integer);
        var series = upsert.Parameters.Add("$series", SqliteType.Integer);
        var t = upsert.Parameters.Add("$t", SqliteType.Integer);
        var avg = upsert.Parameters.Add("$avg", SqliteType.Real);
        var min = upsert.Parameters.Add("$min", SqliteType.Real);
        var max = upsert.Parameters.Add("$max", SqliteType.Real);
        foreach (var row in rows.Where(r => r.Bucket.Count > 0))
        {
            tier.Value = row.Tier;
            series.Value = row.Id;
            t.Value = row.Bucket.Start;
            avg.Value = row.Bucket.Sum / row.Bucket.Count;
            min.Value = row.Bucket.Min;
            max.Value = row.Bucket.Max;
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void Prune(DateTimeOffset now)
    {
        _prunedAt = now;
        using var delete = Command("DELETE FROM points WHERE tier = $tier AND t < $before");
        var tier = delete.Parameters.Add("$tier", SqliteType.Integer);
        var before = delete.Parameters.Add("$before", SqliteType.Integer);
        for (int i = 0; i < Tiers.Length; i++)
        {
            tier.Value = i;
            before.Value = (now - Tiers[i].Keep).ToUnixTimeSeconds();
            delete.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }
}
