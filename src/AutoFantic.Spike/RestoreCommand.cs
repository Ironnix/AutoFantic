using AutoFantic.Core.Hardware;

namespace AutoFantic.Spike;

/// <summary>
/// Emergency hand-back: asks every fan channel to return to BIOS / driver control.
/// GPU fans reliably go back to the driver curve. For mainboard headers the library only knows
/// the original BIOS setting within the run that changed it, so after a hard kill of an
/// earlier run this may do nothing there; a restart always restores the BIOS setup.
/// </summary>
internal static class RestoreCommand
{
    public static int Run(FanSession session)
    {
        int failed = 0;
        foreach (var channel in session.Channels)
        {
            try
            {
                session.ForceRestore(channel);
                Console.WriteLine($"   {channel}: back to BIOS/driver control");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"   {channel}: FAILED ({ex.Message})");
            }
        }

        if (session.Channels.Count == 0)
            Console.WriteLine("No fan channels found, nothing to restore.");
        else
            Console.WriteLine("If a mainboard fan still sits at a fixed speed, restart the PC: the BIOS sets all fans up again at boot.");

        return failed == 0 ? 0 : 1;
    }
}
