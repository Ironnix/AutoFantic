namespace AutoFanatic.Core.Simulation;

/// <summary>What the simulated PC is doing at one moment: power, load and the program in the foreground.</summary>
public sealed record SimLoad(double CpuPower, double GpuPower, double CpuLoad, double GpuLoad, string Foreground)
{
    public static SimLoad Idle { get; } = new(25, 20, 5, 3, "explorer");

    public static SimLoad Game { get; } = new(120, 280, 35, 97, "SimGame");

    /// <summary>What the built-in calibration load produces: steady, a bit below a heavy game.</summary>
    public static SimLoad Calibration { get; } = new(90, 250, 60, 95, "autofanatic-spike");

    private static readonly SimLoad Loading = new(95, 160, 55, 70, "SimGame");
    private static readonly SimLoad Fight = new(130, 320, 45, 99, "SimGame");
    private static readonly SimLoad PauseMenu = new(60, 150, 15, 55, "SimGame");

    // minute the phase starts → what happens; the hour repeats
    private static readonly (double FromMinute, SimLoad Load)[] Hour =
    [
        (0, Idle),
        (3, Loading),
        (4, Game),
        (12, Fight),
        (14, Game),
        (23, PauseMenu),
        (25, Game),
        (45, Fight),
        (46, Game),
        (52, Idle),
    ];

    /// <summary>
    /// A made-up hour at the PC: desktop, a loading screen, play with a couple of fights (more GPU
    /// power), a pause menu, more play, back to the desktop. Enough variety to see how often the
    /// load is steady enough for an experiment.
    /// </summary>
    public static SimLoad Session(TimeSpan elapsed)
    {
        double minute = elapsed.TotalMinutes % 60;
        return Hour.Last(phase => phase.FromMinute <= minute).Load;
    }
}
