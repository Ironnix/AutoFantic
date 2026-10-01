using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

/// <summary>
/// Installing the PawnIO driver for the user. The real installer is never run here: the download
/// comes from a server on this PC, and a small script stands in for the installer.
/// </summary>
public sealed class PawnIoSetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("autofantic-pawnio-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // serves one file once, like GitHub does for a release's file
    private static (string Url, Task Served) Serve(byte[] content)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var served = Task.Run(async () =>
        {
            var context = await server.GetContextAsync();
            context.Response.ContentLength64 = content.Length;
            await context.Response.OutputStream.WriteAsync(content);
            context.Response.Close();
            server.Close();
        });
        return ($"http://localhost:{port}/PawnIO_setup.exe", served);
    }

    private string Installer(int exitCode)
    {
        string script = Path.Combine(_root, $"installer-{exitCode}.cmd");
        File.WriteAllText(script, $"@exit /b {exitCode}");
        return script;
    }

    [Fact]
    public async Task The_installer_is_kept_only_if_it_has_the_expected_checksum()
    {
        byte[] content = Encoding.UTF8.GetBytes("the installer");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        string work = Path.Combine(_root, "work");

        var (url, served) = Serve(content);
        string installer = await PawnIoSetup.DownloadAsync(work, url, sha256);
        await served;
        Assert.Equal(content, File.ReadAllBytes(installer));

        (url, served) = Serve(Encoding.UTF8.GetBytes("something else"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PawnIoSetup.DownloadAsync(work, url, sha256));
        await served;
        Assert.Empty(Directory.GetFiles(work)); // never left behind to be run
    }

    [Fact]
    public void The_official_installer_is_fetched_by_its_version_and_checksum()
    {
        Assert.Contains($"/{PawnIoSetup.Version}/", PawnIoSetup.Url);
        Assert.StartsWith("https://github.com/namazso/PawnIO.Setup/releases/download/", PawnIoSetup.Url);
        Assert.Matches("^[0-9a-f]{64}$", PawnIoSetup.Sha256);
    }

    [Fact]
    public async Task A_finished_install_needs_no_restart_of_the_pc_unless_windows_says_so()
    {
        Assert.False(await PawnIoSetup.RunAsync(Installer(0), installed: () => true));
        Assert.True(await PawnIoSetup.RunAsync(Installer(3010), installed: () => false)); // the driver shows up after the restart
    }

    [Fact]
    public async Task A_failed_install_says_why()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PawnIoSetup.RunAsync(Installer(5), installed: () => false));
        Assert.Contains("5", error.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PawnIoSetup.RunAsync(Installer(0), installed: () => false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PawnIoSetup.RunAsync(Path.Combine(_root, "missing.exe"), installed: () => true));
    }
}
