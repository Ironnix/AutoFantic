using System.ComponentModel;
using System.Diagnostics;
using AutoFantic.Core.Updates;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Hardware;

/// <summary>
/// Installs the PawnIO driver for the user, so nobody has to find and run its installer: downloads
/// the official installer of one fixed version from its GitHub release, checks it against that
/// version's SHA-256 (so only exactly that file is ever run) and runs it without a window.
/// AuFantic has admin rights already, so Windows doesn't ask again. Only after the user said yes.
/// </summary>
public static class PawnIoSetup
{
    // The version LibreHardwareMonitorLib was tried with. For a newer one: change both lines (the
    // checksum is the "digest" GitHub shows for PawnIO_setup.exe on the release's page).
    public const string Version = "2.2.0";
    public const string Sha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

    public const string Url = "https://github.com/namazso/PawnIO.Setup/releases/download/" + Version + "/" + FileName;
    private const string FileName = "PawnIO_setup.exe";

    // Windows' "done, but restart the PC" exit code
    private const int RestartNeeded = 3010;
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Downloads the installer into <paramref name="work"/> (emptied first) and checks it; returns its path.</summary>
    /// <param name="progress">0…1 while downloading.</param>
    public static async Task<string> DownloadAsync(string work, string url = Url, string sha256 = Sha256, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        string installer = Path.Combine(work, FileName);
        await UpdateInstaller.FetchAsync(url, installer, sha256, size: 0, progress, cancel);
        return installer;
    }

    /// <summary>
    /// Runs the installer without a window and waits for it. True if Windows wants a restart of the
    /// PC to finish; throws with the reason if the driver isn't installed afterwards.
    /// </summary>
    /// <param name="installed">Whether the driver is there afterwards (<see cref="SystemCheck.PawnIoInstalled"/>; a parameter for tests).</param>
    public static async Task<bool> RunAsync(string installer, Func<bool>? installed = null, CancellationToken cancel = default)
    {
        int code;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer, "-install -silent") { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException(T("The PawnIO installer didn't start."));
            await process.WaitForExitAsync(cancel).WaitAsync(InstallerTimeout, cancel);
            code = process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(T($"The PawnIO installer didn't start ({ex.Message})."), ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(T("The PawnIO installer didn't finish in time."), ex);
        }

        if (code != 0 && code != RestartNeeded)
            throw new InvalidOperationException(T($"The PawnIO installer ended with error {code}."));
        if (code == 0 && !(installed ?? SystemCheck.PawnIoInstalled)())
            throw new InvalidOperationException(T("The PawnIO installer ran, but the driver isn't there."));
        return code == RestartNeeded;
    }
}
