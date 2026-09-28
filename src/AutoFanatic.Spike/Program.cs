using System.Security.Principal;
using AutoFanatic.Core.Hardware;
using AutoFanatic.Spike;

// Phase 0 spike: prove that AutoFanatic can read the sensors and drive the fan headers directly.
// Every fan this tool changes is handed back to the BIOS when it exits, crashes or is stopped with Ctrl+C.

if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
{
    Usage.Print();
    return 0;
}

if (!IsAdministrator())
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
    using var session = new HardwareSession();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => session.RestoreAll();

    return args[0] switch
    {
        "list" => ListCommand.Run(session, args[1..]),
        "watch" => WatchCommand.Run(session, args[1..], cancel.Token),
        "set" => SetCommand.Run(session, args[1..], cancel.Token),
        _ => Usage.Unknown(args[0]),
    };
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
