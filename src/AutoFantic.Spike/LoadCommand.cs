using AutoFantic.Core.Load;

namespace AutoFantic.Spike;

/// <summary>Runs the built-in calibration load on its own, to check it works. Changes no fans; no admin needed.</summary>
internal static class LoadCommand
{
    public static int Run(string[] args, CancellationToken cancel)
    {
        var options = new Options(args);
        var seconds = Math.Clamp(options.GetDouble("--seconds", 30), 1, 600);

        using var load = new TestLoad(cpuShare: Math.Clamp(options.GetDouble("--cpu", 0.6), 0, 1), gpuShare: Math.Clamp(options.GetDouble("--gpu", 0.9), 0, 1));
        Console.WriteLine($"Built-in load running for {seconds:0} s (Ctrl+C stops). Fans are not touched.");
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(1, seconds)));
        if (load.GpuError is { } error)
            Console.WriteLine($"GPU load FAILED: {error}");
        else if (load.GpuName is { } gpu)
            Console.WriteLine($"GPU load on {gpu}");
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds - 1));
        return load.GpuError is null ? 0 : 1;
    }
}
