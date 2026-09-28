using System.Text;
using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Spike;

/// <summary>
/// One compact line per sample: key temperatures and power, every fan RPM, every fan %,
/// and whether CPU and GPU temperatures have settled (the steady-state detector at work).
/// </summary>
internal sealed class StatusLine
{
    private const int Col = 7;

    private readonly KeySensors _keys;
    private readonly List<SensorReading> _fans;
    private readonly IReadOnlyList<FanChannel> _channels;
    private readonly SteadyStateDetector _cpuSteady = new(TimeSpan.FromSeconds(60));
    private readonly SteadyStateDetector _gpuSteady = new(TimeSpan.FromSeconds(60));

    public StatusLine(Snapshot first, IReadOnlyList<FanChannel> channels)
    {
        _keys = KeySensors.Detect(first);
        _channels = channels;
        _fans = first.OfKind(SensorKind.Fan).ToList();
    }

    public KeySensors Keys => _keys;

    public string Header()
    {
        var line = new StringBuilder("time     ");
        foreach (var name in new[] { "CPU°C", "CPU W", "GPU°C", "Hot°C", "Mem°C", "GPU W" })
            line.Append(Cell(name));
        line.Append(" |");
        foreach (var fan in _fans)
            line.Append(Cell(Short(fan.Name)));
        line.Append(" |");
        foreach (var channel in _channels)
            line.Append(Cell($"#{channel.Index} %"));
        line.Append(" | settled");
        return line.ToString();
    }

    public string Render(Snapshot s)
    {
        Track(s);

        var line = new StringBuilder($"{s.Time:HH:mm:ss} ");
        line.Append(Cell(Format.Number(s.Value(_keys.CpuTemp), SensorKind.Temperature)));
        line.Append(Cell(Format.Number(s.Value(_keys.CpuPower), SensorKind.Power)));
        line.Append(Cell(Format.Number(s.Value(_keys.GpuTemp), SensorKind.Temperature)));
        line.Append(Cell(Format.Number(s.Value(_keys.GpuHotspot), SensorKind.Temperature)));
        line.Append(Cell(Format.Number(s.Value(_keys.GpuMemory), SensorKind.Temperature)));
        line.Append(Cell(Format.Number(s.Value(_keys.GpuPower), SensorKind.Power)));
        line.Append(" |");
        foreach (var fan in _fans)
            line.Append(Cell(Format.Number(s.Value(fan.Id), SensorKind.Fan)));
        line.Append(" |");
        foreach (var channel in _channels)
            line.Append(Cell(Format.Number(s.Value(channel.Id), SensorKind.Control)));
        line.Append($" | CPU {Settled(_cpuSteady)} GPU {Settled(_gpuSteady)}");
        return line.ToString();
    }

    private void Track(Snapshot s)
    {
        if (s.Value(_keys.CpuTemp) is { } cpuT)
            _cpuSteady.Add(s.Time, cpuT, s.Value(_keys.CpuPower) ?? 0);
        if (s.Value(_keys.GpuTemp) is { } gpuT)
            _gpuSteady.Add(s.Time, gpuT, s.Value(_keys.GpuPower) ?? 0);
    }

    private static string Settled(SteadyStateDetector d) =>
        !d.IsWindowFull ? "…" : d.IsSteady ? "yes" : $"no ({d.SlopePerMinute:+0.0;-0.0}°/min)";

    private static string Cell(string text) => text.Length > Col ? text[..Col] : text.PadLeft(Col);

    private static string Short(string fanName) => fanName.Replace("Fan", "F").Replace(" ", "").Replace("#", "");
}
