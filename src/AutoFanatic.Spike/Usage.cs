namespace AutoFanatic.Spike;

internal static class Usage
{
    public static void Print()
    {
        Console.WriteLine("""
            autofanatic-spike: Phase 0 test tool (run as administrator)

              list  [--out report.txt]
                  Every sensor and every controllable fan the hardware exposes,
                  plus the key sensors AutoFanatic picked (CPU/GPU temperature and power).

              watch [--csv log.csv] [--interval 1]
                  One status line per interval: temperatures, power, fan RPM, fan %,
                  and whether CPU/GPU temperatures have settled. Ctrl+C stops.

              set <channel> <percent> [--seconds 60] [--force]
                  Holds one fan channel (# from "list", or its id) at a fixed speed,
                  then hands it back to the BIOS. Prints which fans reacted.
                  Stops early and restores the BIOS curve if a safety limit is crossed.
                  Below 25 % needs --force (a pump could be on that header).
            """);
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command \"{command}\".");
        Print();
        return 2;
    }
}
