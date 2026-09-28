namespace AutoFanatic.Core.Analysis;

/// <summary>What the PC is doing, from measured load, not from the program name ("How it learns → Load classes").</summary>
public enum LoadClass
{
    Idle,
    Light,
    CpuHeavy,
    GpuHeavy,
    Combined,
}

public static class LoadClassifier
{
    // starting values from the design; real gaming logs will tell whether they fit
    public const double IdleCpuBelow = 15;
    public const double IdleGpuBelow = 10;
    public const double GpuHeavyAbove = 80;
    public const double CpuHeavyAbove = 60;

    /// <param name="cpuLoad">CPU total load in %.</param>
    /// <param name="gpuLoad">GPU core load in %.</param>
    /// <returns>null if either load is unknown.</returns>
    public static LoadClass? Classify(double? cpuLoad, double? gpuLoad)
    {
        if (cpuLoad is not { } cpu || gpuLoad is not { } gpu)
            return null;

        bool cpuHeavy = cpu >= CpuHeavyAbove, gpuHeavy = gpu >= GpuHeavyAbove;
        return (cpuHeavy, gpuHeavy) switch
        {
            (true, true) => LoadClass.Combined,
            (false, true) => LoadClass.GpuHeavy,
            (true, false) => LoadClass.CpuHeavy,
            _ when cpu < IdleCpuBelow && gpu < IdleGpuBelow => LoadClass.Idle,
            _ => LoadClass.Light,
        };
    }
}
