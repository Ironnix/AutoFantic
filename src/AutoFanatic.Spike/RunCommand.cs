using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Control;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

/// <summary>
/// "Use my curves": AutoFanatic controls the fans with the calibrated curves, for games and
/// everything else, until stopped. The safety limits stay active; crossing one runs every fan at
/// 100 % until it has cooled down, then control carries on.
/// </summary>
internal static class RunCommand
{
    public static int Run(FanSession session, string[] args, CancellationToken cancel, Func<bool>? stopRequested = null)
    {
        var options = new Options(args);
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default(session));
        var calibration = CalibrationResult.Load(runs.File("calibration.json"));
        var inventory = runs.Inventory();
        if (calibration is null || inventory is null)
        {
            Console.WriteLine("No calibration yet: run Calibrate first.");
            return 2;
        }

        var channels = calibration.Groups
            .Select(g => g.ControlIds.Select(id => session.Channels.FirstOrDefault(c => c.Id == id)
                ?? throw new UsageException($"Fan output {id} no longer exists: run \"Find my fans\" and Calibrate again.")).ToList())
            .ToList();

        // lowest speed each group turns at, from what "Find my fans" and the fans-off test measured
        var minSpinning = FanControlLoop.MinSpinning(calibration, inventory);

        var first = session.Read();
        var keys = KeySensors.Detect(first);
        var guard = new Guard(session, keys, cancel);
        if (!guard.CheckNow())
        {
            Console.Error.WriteLine($"Not starting: {guard.StopReason}.");
            return 3;
        }

        var controller = new CurveController(calibration, minSpinning);
        Console.WriteLine($"Using your curves ({calibration.Profile}, calibrated {calibration.Created:dd.MM. HH:mm}) for everything the PC does.");
        Console.WriteLine(stopRequested is null
            ? "Ctrl+C stops; the fans then go back to the BIOS."
            : "Press Enter to stop; the fans then go back to the BIOS. You can minimize this window meanwhile.");
        Console.WriteLine();

        int tick = 0;
        try
        {
            while (true)
            {
                bool ok = guard.Wait(TimeSpan.FromDays(7), s =>
                {
                    var temps = new Dictionary<Component, double>();
                    if (s.Value(keys.CpuTemp) is { } cpu)
                        temps[Component.Cpu] = cpu;
                    if (s.Value(keys.GpuTemp) is { } gpu)
                        temps[Component.GpuCore] = gpu;
                    double cpuW = s.Value(keys.CpuPower) ?? 0, gpuW = s.Value(keys.GpuPower) ?? 0;

                    var speeds = controller.Step(s.Time, temps, cpuW, gpuW);
                    for (int g = 0; g < channels.Count; g++)
                        foreach (var channel in channels[g])
                            session.SetPercent(channel, (float)speeds[g]);

                    if (++tick % 2 == 0)
                        Console.Write($"\r   CPU {C(temps, Component.Cpu)}  GPU {C(temps, Component.GpuCore)}  ({cpuW:0} W / {gpuW:0} W)   "
                            + string.Join("  ", calibration.Groups.Select((g, i) => $"{Short(g)} {(speeds[i] == 0 ? "off" : $"{speeds[i]:0} %")}")) + "     ");
                }, until: () => stopRequested?.Invoke() == true);
                Console.WriteLine();

                if (ok || guard.Cancelled)
                    break;
                if (!guard.LimitCrossed)
                {
                    Console.WriteLine($"{guard.StopReason}: stopped.");
                    break;
                }
                Console.WriteLine("Cooled down: carrying on with your curves.");
                controller.Reset();
            }
        }
        finally
        {
            session.RestoreAll();
        }

        Console.WriteLine("Stopped. All fans back to BIOS control.");
        return 0;
    }

    private static string C(Dictionary<Component, double> temps, Component c) =>
        temps.TryGetValue(c, out double v) ? $"{v:0} °C" : "–";

    private static string Short(CalibratedGroup g) =>
        g.Follows == Component.GpuCore && g.Name.StartsWith("GPU", StringComparison.Ordinal) ? "GPU" : $"#{g.Channels[0]}";
}
