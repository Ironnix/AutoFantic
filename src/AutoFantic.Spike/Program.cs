using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using AutoFantic.Core;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Simulation;
using AutoFantic.Spike;

// Phase 0 spike: prove that AutoFantic can read the sensors and drive the fan headers directly.
// Every fan this tool changes is handed back to the BIOS when it exits, crashes or is stopped with Ctrl+C.

// "°C" and "→" need UTF-8; numbers always with a decimal point, matching the CSV files
Console.OutputEncoding = System.Text.Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
{
    Usage.Print();
    return 0;
}

// analyze only replays a log file: no hardware, no admin rights
if (args[0] == "analyze")
{
    try
    {
        return AnalyzeCommand.Run(args[1..]);
    }
    catch (Exception ex) when (ex is UsageException or FormatException or IOException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

// recalculate works from the stored measurements only: no hardware, no admin rights
if (args[0] == "recalculate")
{
    try
    {
        return CalibrateCommand.RecalculateCommand(args[1..]);
    }
    catch (Exception ex) when (ex is UsageException or FormatException or IOException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

// load only runs the built-in calibration load: no fans, no admin rights
if (args[0] == "load")
{
    CtrlC.Install();
    return LoadCommand.Run(args[1..], CtrlC.Token);
}

// --simulate [--sim-speed 10] [--sim-load idle|game|session]: a made-up PC instead of the real hardware (no admin needed)
bool simulate = args.Contains("--simulate");
args = args.Where(a => a != "--simulate").ToArray();
if (args.Length == 0)
{
    Usage.Print();
    return 0;
}

string? simSpeedText = TakeOption(ref args, "--sim-speed");
double simSpeed = 1;
if (simSpeedText is not null
    && (!double.TryParse(simSpeedText, NumberStyles.Float, CultureInfo.InvariantCulture, out simSpeed) || simSpeed is < 0.1 or > 100))
{
    Console.Error.WriteLine($"--sim-speed must be a number from 0.1 to 100, not \"{simSpeedText}\".");
    return 2;
}

// discover and the calibration start at idle (the calibration brings its own load), the rest under a steady game
string simLoad = TakeOption(ref args, "--sim-load") ?? (args[0] is "discover" or "calibrate" or "test" or "run" ? "idle" : "game");
Func<TimeSpan, SimLoad>? simSchedule = simLoad switch
{
    "idle" => _ => SimLoad.Idle,
    "game" => _ => SimLoad.Game,
    "session" => SimLoad.Session,
    _ => null,
};
if (simSchedule is null)
{
    Console.Error.WriteLine($"--sim-load must be idle, game or session, not \"{simLoad}\".");
    return 2;
}

// the test menu runs in its own window (AutoFantic → Settings → Developer): keep it open when something goes wrong
bool pauseOnError = args[0] == "test" && !Console.IsInputRedirected;

// only one program may drive the fans: not while AutoFantic runs in the background
if (!simulate && args[0] is "set" or "discover" or "sweep" or "calibrate" or "run" && DataFolder.BackgroundRunning())
{
    Console.Error.WriteLine("AutoFantic is running in the background (icon next to the clock) and controls the fans.");
    Console.Error.WriteLine("Right-click the icon → Exit first, then start this again.");
    return 2;
}

if (!simulate && !IsAdministrator())
{
    // the test menu asks Windows for admin rights itself, so a double-click is enough
    if (args[0] == "test" && !Console.IsInputRedirected && RestartAsAdministrator(args))
        return 0;

    Console.Error.WriteLine("autofantic-spike needs admin rights (it loads the hardware driver).");
    Console.Error.WriteLine("Open Terminal / PowerShell with \"Run as administrator\" and start it again.");
    PauseIf(pauseOnError);
    return 2;
}

// Ctrl+C cancels the running command, which then hands its fans back to the BIOS itself
CtrlC.Install();

try
{
    using FanSession session = simulate
        ? new SimulatedPc(timeScale: simSpeed, load: simSchedule)
        : new HardwareSession();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => session.RestoreAll();

    if (simulate)
        Console.WriteLine($"SIMULATION: no real fans are touched (speed ×{simSpeed:0.#}, {simLoad} load).\n");

    // fans an earlier run left behind (a crash, a hard kill) go back to the BIOS first; from now on
    // this run keeps its own fans-in-use file, so the same works for it
    if (args[0] is "set" or "discover" or "sweep" or "calibrate" or "run" or "test" or "restore")
    {
        string data = DataFolder.Default(simulate);
        if (Handback.RecoverIfNeeded(data, session, new ActivityLog(data), "the test console at its start") is { } recovered)
            Console.WriteLine(recovered.Text + "\n");
        session.HandbackPath = Handback.PathIn(data);
    }

    return args[0] switch
    {
        "list" => ListCommand.Run(session, args[1..]),
        "watch" => WatchCommand.Run(session, args[1..], CtrlC.Token),
        "set" => SetCommand.Run(session, args[1..], CtrlC.Token),
        "discover" => DiscoverCommand.Run(session, args[1..], CtrlC.Token),
        "sweep" => SweepCommand.Run(session, args[1..], CtrlC.Token),
        "restore" => RestoreCommand.Run(session),
        "test" => TestCommand.Run(session, args[1..], simulate),
        "calibrate" => CalibrateCommand.Run(session, args[1..], CtrlC.Token),
        "run" => RunCommand.Run(session, args[1..], CtrlC.Token),
        _ => Usage.Unknown(args[0]),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    PauseIf(pauseOnError);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex);
    PauseIf(pauseOnError);
    return 1;
}

static void PauseIf(bool pause)
{
    if (!pause)
        return;
    Console.Error.WriteLine();
    Console.Error.WriteLine("Press Enter to close this window.");
    Console.ReadLine();
}

// Starts this exe again with the same arguments, elevated (Windows shows its admin prompt).
static bool RestartAsAdministrator(string[] args)
{
    try
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };
        Process.Start(start);
        return true;
    }
    catch (Win32Exception)
    {
        return false; // the admin prompt was declined
    }
}

// Removes "--name value" from args and returns the value (null if absent).
static string? TakeOption(ref string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length)
        return null;
    string value = args[i + 1];
    args = [.. args[..i], .. args[(i + 2)..]];
    return value;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
