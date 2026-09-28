using System.Globalization;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Spike;

/// <summary>
/// The calibration from the command line: the same <see cref="CalibrationRunner"/> the AutoFantic
/// window uses, with its progress printed to the console.
/// </summary>
internal static partial class CalibrateCommand
{
    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args, "--builtin-load");
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default(session));
        double ambient = options.GetDouble("--ambient", 22);
        var preset = Preset.For(options.Get("--profile") ?? options.Get("--preset"));
        bool builtIn = options.Has("--builtin-load");
        var calibration = new CalibrationOptions(ambient, preset, builtIn);
        if (options.Get("--hold") is not null)
            calibration = calibration with { Hold = TimeSpan.FromSeconds(Math.Clamp(options.GetDouble("--hold", 90), 30, 300)) };

        Console.WriteLine($"Calibrating for {preset.Name}: {preset.Description}");
        Console.WriteLine(builtIn
            ? "With the built-in CPU + GPU load: please don't use the PC meanwhile."
            : "With your own load: when asked, start your game and play normally.");
        Console.WriteLine("The fans will be audible. If it gets too hot, all fans go to 100 % until it has cooled down.");
        Console.WriteLine("Ctrl+C stops at any time and hands the fans back to the BIOS.");
        if (options.Get("--ambient") is null)
            Console.WriteLine($"Room temperature assumed {ambient:0} °C (--ambient to change).");
        Console.WriteLine();

        bool ticking = false;
        var runner = new CalibrationRunner(session, runs.Path, calibration);
        runner.Log += line =>
        {
            if (ticking)
                Console.WriteLine();
            ticking = false;
            Console.WriteLine(line);
        };
        runner.Progress += p =>
        {
            if (p.Sample is not { } s)
                return;
            ticking = true;
            Console.Write($"\r   {TimeSpan.FromSeconds(s.Seconds):m\\:ss}  CPU {T(s.CpuTemp)}  GPU {T(s.GpuTemp)}  ({s.CpuPower:0} W / {s.GpuPower:0} W)  {p.Message}".PadRight(100));
        };

        var outcome = runner.Run(cancel);
        if (ticking)
            Console.WriteLine();
        Console.WriteLine();
        if (outcome.Report is { } report)
            Console.Write(report);
        Console.WriteLine(outcome.Message);
        Console.WriteLine("All fans are back on BIOS control.");
        return outcome.Success ? 0 : 3;
    }

    private static string T(double? celsius) =>
        celsius is { } c ? $"{c.ToString("0.0", CultureInfo.InvariantCulture)} °C" : "–";
}
