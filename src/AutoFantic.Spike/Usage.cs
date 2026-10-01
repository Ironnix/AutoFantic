namespace AutoFantic.Spike;

internal static class Usage
{
    public static void Print()
    {
        Console.WriteLine("""
            autofantic-spike: Phase 0 test tool (run as administrator)

              test     [--runs folder]
                  The test console (AuFantic → Settings → Developer): find my fans, calibrate, curves,
                  plus the optional tests. Asks for admin rights itself; results go to runs\.

              calibrate [--ambient 22] [--profile 80] [--hold 90] [--runs folder] [--builtin-load]
                  While you play (or with --builtin-load): 9 fan combinations, how the
                  temperatures follow the power, then the quietest fan speeds per load level
                  and a curve per fan. A fans-off test first if the PC is idle. 15-30 min.
                  Uses runs\fans.json (from "Find my fans"); runs discover first if it's missing.

              run      [--runs folder]
                  "Use my curves": AuFantic runs the fans with the calibrated curves until
                  Ctrl+C, for everything the PC does. Safety limits stay active.

              load     [--seconds 30] [--cpu 0.6] [--gpu 0.9]
                  Just the built-in load, to check it works. Changes no fans; no admin needed.

              list     [--out hardware.txt]
                  Every sensor and every controllable fan, plus the key sensors
                  AuFantic picked (CPU/GPU temperature and power).

              watch    [--csv log.csv] [--interval 1]
                  One status line per second: temperatures, power, fan RPM and %,
                  and whether CPU/GPU temperatures have settled. Read-only.
                  The CSV also records the program in the foreground (e.g. the game);
                  replay it with "analyze".

              discover [--steps 100,60,30] [--settle 8] [--skip 3,4] [--out discover.txt] [--force]
                  Runs every fan channel through a few speeds, fast to slow, one after
                  another: which control drives which fan, empty headers, fans that stop,
                  pumps. A channel that looks like a pump is never taken lower.

              set      <channel> <percent> [--seconds 60] [--force]
                  Holds one fan channel at a fixed speed, then hands it back to the BIOS.

              sweep    <channels> [--steps 100,80,60,45,30] [--ambient 22] [--window 60]
                       [--max-wait 420] [--csv sweep.csv] [--out sweep.txt] [--force]
                  Under a steady load (game, benchmark loop): steps the fan group from
                  fast to slow, waits at each step until temperatures settle, and finds
                  the knee, the speed beyond which more RPM is not worth the noise.
                  Several channels: "0,1,2" (e.g. all case fans together).

              restore
                  Emergency: hands EVERY fan back to BIOS/driver control
                  (e.g. after the tool was killed in Task Manager).

              analyze  <watch.csv> [--profile 80] [--experiment 6] [--cooldown 10] [--out analysis.txt]
                  Replays a watch log (e.g. an evening of gaming) through the experiment
                  rules: how often could AuFantic have learned, and what blocked it.
                  Reads only the file: no admin rights needed.

            Channels are the # numbers from "list" (or full ids). Below 25 % needs --force,
            because a pump could sit on that header. At CPU ≥ 90 °C, GPU core ≥ 85 °C or
            hotspot/memory ≥ 100 °C every fan goes to 100 % until it is safely cool again,
            then back to the BIOS curve.

            --simulate [--sim-speed 20] [--sim-load idle|game|session]
                Any command against a built-in simulated PC (no admin, no real fans).
                discover defaults to idle load, everything else to a steady game;
                "session" is a scripted hour of desktop, loading, play and menus.
            """);
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command \"{command}\".");
        Print();
        return 2;
    }
}
