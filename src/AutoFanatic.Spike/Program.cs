using System.Globalization;
using System.Security.Principal;
using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Simulation;
using AutoFanatic.Spike;

// Phase 0 spike: prove that AutoFanatic can read the sensors and drive the fan headers directly.
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

// --simulate [--sim-speed 10]: a made-up PC instead of the real hardware (no admin needed)
bool simulate = args.Contains("--simulate");
double simSpeed = 1;
if (Array.IndexOf(args, "--sim-speed") is int s and >= 0 && s + 1 < args.Length)
{
    simSpeed = double.Parse(args[s + 1], CultureInfo.InvariantCulture);
    args = [.. args[..s], .. args[(s + 2)..]];
}
args = args.Where(a => a != "--simulate").ToArray();

if (!simulate && !IsAdministrator())
{
    Console.Error.WriteLine("autofanatic-spike needs admin rights (it loads the hardware driver).");
    Console.Error.WriteLine("Open Terminal / PowerShell with \"Run as administrator\" and start it again.");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the command finish its loop and restore the fans itself
    cancel.Cancel();
};

try
{
    using FanSession session = simulate ? new SimulatedPc(timeScale: simSpeed) : new HardwareSession();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => session.RestoreAll();

    if (simulate)
        Console.WriteLine($"SIMULATION: no real fans are touched (speed ×{simSpeed:0.#}).\n");

    return args[0] switch
    {
        "list" => ListCommand.Run(session, args[1..]),
        "watch" => WatchCommand.Run(session, args[1..], cancel.Token),
        "set" => SetCommand.Run(session, args[1..], cancel.Token),
        "discover" => DiscoverCommand.Run(session, args[1..], cancel.Token),
        "sweep" => SweepCommand.Run(session, args[1..], cancel.Token),
        "restore" => RestoreCommand.Run(session),
        _ => Usage.Unknown(args[0]),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex);
    return 1;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
