using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Logging;

namespace AutoFanatic.Core.Tests;

public sealed class SensorLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"autofanatic-test-{Guid.NewGuid():N}.csv");

    public void Dispose() => File.Delete(_path);

    private static Snapshot At(DateTimeOffset time, float? temp, float power) => new(time,
    [
        new("/amdcpu/0/temperature/2", "AMD \"Ryzen\", 7", "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", temp),
        new("/amdcpu/0/power/0", "AMD \"Ryzen\", 7", "Cpu", SensorKind.Power, "Package", power),
    ]);

    [Fact]
    public void Round_trip_keeps_sensors_values_times_and_foreground()
    {
        var t0 = new DateTimeOffset(2026, 9, 28, 20, 0, 0, 250, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 28)));
        using (var writer = new SensorLogWriter(_path, At(t0, 70, 110)))
        {
            writer.Write(At(t0, 70.5f, 110), "Cyberpunk2077");
            writer.Write(At(t0.AddSeconds(1), null, 112.25f), "Game, with \"quotes\"");
            writer.Write(At(t0.AddSeconds(2), 71, 113), null);
        }

        var samples = SensorLogReader.Read(_path).ToList();

        Assert.Equal(3, samples.Count);
        Assert.Equal(t0, samples[0].Snapshot.Time);
        Assert.Equal("Cyberpunk2077", samples[0].Foreground);
        Assert.Equal("Game, with \"quotes\"", samples[1].Foreground);
        Assert.Null(samples[2].Foreground);

        var temp = samples[0].Snapshot.Find("/amdcpu/0/temperature/2")!;
        Assert.Equal("AMD \"Ryzen\", 7", temp.Hardware);
        Assert.Equal("Cpu", temp.HardwareType);
        Assert.Equal(SensorKind.Temperature, temp.Kind);
        Assert.Equal("Core (Tctl/Tdie)", temp.Name);
        Assert.Equal(70.5f, temp.Value);

        Assert.Null(samples[1].Snapshot.Value("/amdcpu/0/temperature/2"));
        Assert.Equal(112.25f, samples[1].Snapshot.Value("/amdcpu/0/power/0"));
        Assert.Equal(TimeSpan.FromSeconds(2), samples[2].Snapshot.Time - samples[0].Snapshot.Time);
    }

    [Fact]
    public void Old_logs_without_sensor_types_are_rejected_with_a_hint()
    {
        File.WriteAllText(_path, "time,foreground,\"AMD Ryzen | Package | /amdcpu/0/power/0\"\n2026-09-28 20:00:00,\"x\",100\n");

        var error = Assert.Throws<FormatException>(() => SensorLogReader.Read(_path).ToList());

        Assert.Contains("older version", error.Message);
    }
}
