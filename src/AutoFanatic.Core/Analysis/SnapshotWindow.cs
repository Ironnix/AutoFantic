using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Analysis;

/// <summary>Keeps the snapshots of the last <see cref="Window"/> and averages any sensor over them.</summary>
public sealed class SnapshotWindow(TimeSpan window)
{
    private readonly Queue<Snapshot> _snapshots = new();

    public TimeSpan Window { get; } = window;

    public int Count => _snapshots.Count;

    public void Add(Snapshot snapshot)
    {
        _snapshots.Enqueue(snapshot);
        while (_snapshots.Count > 0 && snapshot.Time - _snapshots.Peek().Time > Window)
            _snapshots.Dequeue();
    }

    public void Clear() => _snapshots.Clear();

    /// <summary>Mean of the sensor over the window, ignoring samples where it had no value.</summary>
    public double? Mean(string? sensorId)
    {
        if (sensorId is null)
            return null;

        var values = _snapshots
            .Select(s => s.Value(sensorId))
            .Where(v => v.HasValue)
            .Select(v => (double)v!.Value)
            .ToList();

        return values.Count == 0 ? null : values.Average();
    }
}
