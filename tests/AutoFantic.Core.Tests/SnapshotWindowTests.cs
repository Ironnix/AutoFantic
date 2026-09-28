using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

public class SnapshotWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Snapshot At(int second, float? temp) => new(Start.AddSeconds(second),
        temp is null ? [] : [new SensorReading("t", "CPU", "Cpu", SensorKind.Temperature, "Core", temp)]);

    [Fact]
    public void Averages_only_the_last_window()
    {
        var window = new SnapshotWindow(TimeSpan.FromSeconds(10));
        for (int s = 0; s <= 30; s++)
            window.Add(At(s, s < 20 ? 90 : 60));

        Assert.Equal(60, window.Mean("t")!.Value, precision: 6);
    }

    [Fact]
    public void Missing_values_are_skipped_and_unknown_sensors_give_null()
    {
        var window = new SnapshotWindow(TimeSpan.FromSeconds(60));
        window.Add(At(0, 50));
        window.Add(At(1, null));
        window.Add(At(2, 70));

        Assert.Equal(60, window.Mean("t")!.Value, precision: 6);
        Assert.Null(window.Mean("other"));
        Assert.Null(window.Mean(null));
    }
}
