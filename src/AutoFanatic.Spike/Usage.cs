namespace AutoFanatic.Spike;

internal static class Usage
{
    public static void Print()
    {
        Console.WriteLine("""
            autofanatic-spike: Phase 0 test tool (run as administrator)

              list     [--out hardware.txt]
                  Every sensor and every controllable fan, plus the key sensors
                  AutoFanatic picked (CPU/GPU temperature and power).

              watch    [--csv log.csv] [--interval 1]
                  One status line per second: temperatures, power, fan RPM and %,
                  and whether CPU/GPU temperatures have settled. Read-only.
                  The CSV also records the program in the foreground (e.g. the game).

              discover [--steps 30,60,100] [--settle 8] [--skip 3,4] [--out discover.txt] [--force]
                  Runs every fan channel through a few speeds, one after another:
                  which control drives which fan, empty headers, fans that stop, pumps.

              set      <channel> <percent> [--seconds 60] [--force]
                  Holds one fan channel at a fixed speed, then hands it back to the BIOS.

              sweep    <channels> [--steps 100,80,60,45,30] [--ambient 22] [--window 60]
                       [--max-wait 420] [--csv sweep.csv] [--force]
                  Under a steady load (game, benchmark loop): steps the fan group from
                  fast to slow, waits at each step until temperatures settle, and finds
                  the knee, the speed beyond which more RPM is not worth the noise.
                  Several channels: "0,1,2" (e.g. all case fans together).

              restore
                  Emergency: hands EVERY fan back to BIOS/driver control
                  (e.g. after the tool was killed in Task Manager).

            Channels are the # numbers from "list" (or full ids). Below 25 % needs --force,
            because a pump could sit on that header. Every command that changes a fan stops
            and restores the BIOS curve at CPU ≥ 90 °C, GPU core ≥ 85 °C, hotspot/memory ≥ 100 °C.
            """);
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command \"{command}\".");
        Print();
        return 2;
    }
}
