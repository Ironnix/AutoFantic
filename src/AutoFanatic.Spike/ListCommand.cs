using System.Text;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

internal static class ListCommand
{
    public static int Run(FanSession session, string[] args)
    {
        var options = new Options(args);
        var snapshot = session.Read();
        var report = Build(session, snapshot);

        Console.Write(report);
        if (options.Get("--out") is { } path)
        {
            File.WriteAllText(path, report, Encoding.UTF8);
            Console.WriteLine($"Report written to {Path.GetFullPath(path)}");
        }
        return 0;
    }

    private static string Build(FanSession session, Snapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine($"AutoFanatic hardware report · {snapshot.Time:yyyy-MM-dd HH:mm}");
        text.AppendLine();

        // grouped by hardware in the order the library reports it (the Super I/O chip carries the mainboard fans)
        foreach (var hardware in snapshot.Readings.GroupBy(r => (r.Hardware, r.HardwareType)))
        {
            text.AppendLine($"== {hardware.Key.Hardware} ({hardware.Key.HardwareType})");
            foreach (var reading in hardware.OrderBy(r => r.Kind))
                text.AppendLine($"   {reading.Kind,-11} {reading.Name,-28} {Format.Value(reading.Value, reading.Kind),12}   {reading.Id}");
            text.AppendLine();
        }

        var keys = KeySensors.Detect(snapshot);
        text.AppendLine("== Key sensors (what AutoFanatic will use)");
        AppendKey(text, "CPU temperature", keys.CpuTemp, snapshot);
        AppendKey(text, "CPU power", keys.CpuPower, snapshot);
        AppendKey(text, "GPU core", keys.GpuTemp, snapshot);
        AppendKey(text, "GPU hotspot", keys.GpuHotspot, snapshot);
        AppendKey(text, "GPU memory", keys.GpuMemory, snapshot);
        AppendKey(text, "GPU power", keys.GpuPower, snapshot);
        text.AppendLine();

        text.AppendLine($"== Fan controls ({session.Channels.Count})");
        foreach (var channel in session.Channels)
        {
            string mode = channel.IsSoftwareControlled ? "software" : "BIOS/driver";
            text.AppendLine($"   #{channel.Index,-2} {channel.Hardware} / {channel.Name,-22} {Format.Value(channel.Percent, SensorKind.Control),8}   " +
                            $"range {channel.MinPercent:0}–{channel.MaxPercent:0} %   {mode}   {channel.Id}");
        }

        if (session.Channels.Count == 0)
        {
            text.AppendLine("   None found. Either the mainboard's Super I/O chip isn't supported, or the PawnIO");
            text.AppendLine("   driver is missing (LibreHardwareMonitor needs it; see README → Troubleshooting).");
        }
        else if (!snapshot.Readings.Any(r => r.HardwareType == "SuperIO" && r.Kind == SensorKind.Control))
        {
            text.AppendLine("   Only GPU fans found, no mainboard headers: Super I/O chip unsupported or PawnIO missing.");
        }

        return text.ToString();
    }

    private static void AppendKey(StringBuilder text, string label, string? id, Snapshot snapshot)
    {
        var reading = snapshot.Find(id);
        text.AppendLine(reading is null
            ? $"   {label,-16} not found"
            : $"   {label,-16} {reading.Hardware} / {reading.Name} = {Format.Value(reading.Value, reading.Kind)}");
    }
}
