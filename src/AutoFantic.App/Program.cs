using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoFantic.Core;
using AutoFantic.Core.Logging;
using static AutoFantic.Core.Texts;

namespace AutoFantic.App;

/// <summary>
/// AuFantic in the background: an icon next to the clock, a window to see and set the curves,
/// the fans on the calibrated curves. Needs admin rights for the hardware driver; asks for them
/// itself, or has them when started by "Start with Windows" (Task Scheduler, highest privileges).
///
/// Before the first calibration the BIOS keeps the fans and the window opens on its set-up page.
/// A watchdog process (<see cref="Watchdog"/>) hands the fans back if AuFantic is killed.
///
/// Options for checking a build without touching the screen (with --simulate):
///   --selftest [--seconds 5]        start everything without an icon, run 5 s, exit 0 if it controlled the fans (or, not set up, only watched)
///   --selftest-calibration          a whole calibration with the built-in load, as the window starts it (the first one finds the fans too)
///   --selftest-update releases.json an update the way the window does it, from a local copy of GitHub's list of releases
///                                   (URL): check, download, install, restart into the new version (which runs --selftest)
///   --selftest-together             the first two mainboard fans put together and taken apart again, as "Your fans" does it (needs a calibration)
///   --screenshot file.png [--page overview|monitor|curves|calibration|log|settings] [--height 2000] [--full]   render the window off-screen to a PNG
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // the watchdog's processes: nothing of the window is loaded for them
        if (args.Contains(Watchdog.LaunchArgument))
            return Watchdog.RunLauncher(args);
        if (args.Contains(Watchdog.Argument))
            return Watchdog.Run(args);
        try
        {
            return RunApp(args);
        }
        finally
        {
            // for developers: every text shown in German that has no translation yet
            if (Environment.GetEnvironmentVariable("AUTOFANTIC_MISSING_TEXTS") is { Length: > 0 } missing)
                File.WriteAllLines(missing, Texts.Missing.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>The language chosen in Settings (or Windows'), before anything writes or shows a text.</summary>
    internal static void UseLanguage(bool simulate) =>
        Texts.Use(AppearanceSettings.Load(Path.Combine(DataFolder.Default(simulate), AppearanceSettings.FileName)).Language, typeof(Program).Assembly);

    // kept apart from Main, so the watchdog never loads WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string[] args)
    {
        bool simulate = args.Contains("--simulate");
        bool selfTest = args.Contains("--selftest") || args.Contains("--selftest-calibration") || args.Contains("--selftest-update") || args.Contains("--selftest-together");
        string? screenshot = Option(args, "--screenshot");
        bool quiet = selfTest || screenshot is not null;

        // started by an update: the old version hands the fans back and ends first
        if (int.TryParse(Option(args, AppController.WaitForArgument), out int previous))
            WaitForExit(previous);

        if (!simulate && !IsAdministrator())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = string.Join(' ', args) });
            }
            catch (Win32Exception)
            {
                // the admin prompt was declined: nothing to do
            }
            return 0;
        }
        // only now, with admin rights: this looks at the data folder, and one chosen in Settings may need them (also to copy the data there)
        UseLanguage(simulate);

        // the version from before the rename controls the fans: two programs must never fight over them
        if (!simulate && DataFolder.LegacyBackgroundRunning())
        {
            System.Windows.MessageBox.Show(T("The older \"AutoFanatic\" is still running and controls the fans.\n\nExit it first (right-click its icon next to the clock → Exit), then start AuFantic again."),
                "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        if (!simulate)
            Autostart.RemoveLegacy();

        // a simulated copy touches no hardware: it may run next to the real one
        using var single = new Mutex(initiallyOwned: true, simulate ? DataFolder.BackgroundMutex + ".Simulated" : DataFolder.BackgroundMutex, out bool first);
        if (!first)
        {
            if (!quiet)
                System.Windows.MessageBox.Show(!simulate && RunningCopy() is { } other
                    ? T($"Another copy of AuFantic is already running:\n{other}\n\nExit it first (right-click its icon next to the clock → Exit), then start this one again.")
                    : T("AuFantic is already running: look for its icon next to the clock."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        // the fans belong to the PC, not to a Windows account: after "Switch user" another account's AuFantic still has them
        using var perPc = simulate ? null : HoldForThisPc();
        if (!simulate && perPc is null)
        {
            if (!quiet)
                System.Windows.MessageBox.Show(T("AuFantic is already running in another Windows account on this PC and controls the fans from there.\n\nExit it there (right-click its icon next to the clock → Exit) or sign that account out, then start AuFantic here."),
                    "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var wpf = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        wpf.ThemeMode = ThemeMode.System; // Windows 11 look, light or dark like Windows

        var app = AppController.Create(simulate, out string? problem, double.TryParse(Option(args, "--sim-speed"), out double speed) ? speed : 1);
        if (app is null)
        {
            if (!quiet)
                System.Windows.MessageBox.Show(problem, "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        using (app)
        {
            app.Appearance.Apply(); // light, dark or like Windows, as chosen in Settings
            if (Option(args, AppController.UpdatedFromArgument) is { } from)
            {
                app.UpdatedFrom = from;
                app.Log.Add(LogKind.Info, T($"Updated from {from} to {AppVersion.Text}."));
            }

            // whatever happens, the fans go back to the BIOS
            AppDomain.CurrentDomain.ProcessExit += (_, _) => app.Loop.Dispose();
            wpf.DispatcherUnhandledException += (_, e) =>
            {
                app.Loop.Dispose();
                if (!quiet)
                    System.Windows.MessageBox.Show(T($"AuFantic stopped: {e.Exception.Message}\n\nThe fans are back on BIOS control."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Error);
                wpf.Shutdown(1);
                e.Handled = true;
            };
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => app.Loop.Dispose();

            app.Loop.Start();
            MemoryUpkeep.Start();

            if (screenshot is not null)
                return Screenshot(app, screenshot, Option(args, "--page") ?? "overview", double.TryParse(Option(args, "--height"), out double h) ? h : null, args.Contains("--full"));

            Watchdog.Launch(simulate);
            if (!quiet)
                app.StartUpdateChecks();
            if (!quiet && !simulate)
                FollowAutostart(app);

            using var tray = new TrayIcon(app, quiet: selfTest);
            if (args.Contains("--selftest-calibration"))
            {
                // a whole calibration the way the window starts it: pause, run, take over the result
                // … and a few seconds later the fan control must be running again with the new curves
                app.CalibrationEnded += outcome => wpf.Dispatcher.BeginInvoke(() =>
                    After(TimeSpan.FromSeconds(3), () =>
                        wpf.Shutdown(outcome.Success && !app.Calibrating && app.Loop.Last is { State: Core.Control.LoopState.Running } ? 0 : 4)));
                wpf.Dispatcher.BeginInvoke(() => app.StartCalibration(22, builtInLoad: true));
            }
            else if (simulate && Option(args, "--selftest-update") is { } releases)
            {
                wpf.Dispatcher.BeginInvoke(async () =>
                {
                    int code = 5; // no newer version found
                    try
                    {
                        if (await app.CheckForUpdateAsync(releases) is { } release)
                        {
                            await app.InstallUpdateAsync(release, null);
                            app.StartNewVersion("--selftest", "--seconds", "5");
                            code = 0;
                        }
                        else if (app.UpdateProblem is { } problem)
                            app.Log.Add(LogKind.Warning, $"Update self-test: {problem}");
                    }
                    catch (Exception ex)
                    {
                        app.Log.Add(LogKind.Warning, $"Update self-test: {ex}");
                        code = 6;
                    }
                    wpf.Shutdown(code);
                });
            }
            else if (args.Contains("--selftest-together"))
            {
                // two mainboard fans put together, then taken apart again: each time the fan control
                // must be running with the new groups a few seconds later
                wpf.Dispatcher.BeginInvoke(() =>
                {
                    int groups = app.Effective?.Groups.Count ?? 0;
                    bool Runs(int count) => app.Effective?.Groups.Count == count && app.Loop.Last is { State: Core.Control.LoopState.Running } status && status.Fans.Count == count;
                    bool together = app.RunTogether(0, 1);
                    After(TimeSpan.FromSeconds(3), () =>
                    {
                        bool one = together && Runs(groups - 1) && app.Effective!.Groups[0].ControlIds.Count == 2;
                        app.RunApart(0);
                        After(TimeSpan.FromSeconds(3), () => wpf.Shutdown(one && Runs(groups) ? 0 : 7));
                    });
                });
            }
            else if (selfTest)
            {
                var expected = app.IsSetUp ? Core.Control.LoopState.Running : Core.Control.LoopState.NotSetUp;
                After(TimeSpan.FromSeconds(double.TryParse(Option(args, "--seconds"), out double s) ? s : 5),
                    () => wpf.Shutdown(app.Loop.Last?.State == expected ? 0 : 3));
            }
            else if (!app.IsSetUp)
                wpf.Dispatcher.BeginInvoke(() => tray.OpenWindow("calibration")); // nothing to do in the background yet: show the set-up
            else if (args.Contains("--open"))
                wpf.Dispatcher.BeginInvoke(() => tray.OpenWindow(Option(args, "--open") ?? "overview")); // "--open settings": on that page

            return wpf.Run();
        }
    }

    /// <summary>Renders the window off-screen (nobody sees it) to a PNG, to check the look of a build.</summary>
    /// <param name="full">The whole page, also what is scrolled out of view (a window can't be taller than the screen).</param>
    private static int Screenshot(AppController app, string path, string page, double? height, bool full)
    {
        var window = new MainWindow(app)
        {
            Height = height ?? 780,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.ShowPage(page);
        window.Show();

        // a few seconds of live data, then render
        var frame = new DispatcherFrame();
        After(TimeSpan.FromSeconds(4), () => frame.Continue = false);
        Dispatcher.PushFrame(frame);

        // the whole page: everything that scrolls, under the page's fixed head if it has one (the Monitor's heading and time ranges)
        List<FrameworkElement> parts = !full ? [(FrameworkElement)window.Content] : window.VisiblePage switch
        {
            System.Windows.Controls.ScrollViewer { Content: FrameworkElement content } => [content],
            System.Windows.Controls.Panel panel when panel.Children.OfType<System.Windows.Controls.ScrollViewer>().Any() =>
                [.. panel.Children.OfType<FrameworkElement>().Select(child => child is System.Windows.Controls.ScrollViewer { Content: FrameworkElement inner } ? inner : child)],
            _ => [(FrameworkElement)window.Content],
        };
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Rect(0, 0, parts.Max(p => p.ActualWidth), parts.Sum(p => p.ActualHeight + p.Margin.Bottom));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // off-screen the window has no backdrop: the theme's own background, so dark mode stays readable
            var background = window.TryFindResource("ApplicationBackgroundBrush") as System.Windows.Media.Brush
                ?? window.TryFindResource("WindowBackground") as System.Windows.Media.Brush
                ?? window.Background ?? System.Windows.Media.Brushes.White;
            dc.DrawRectangle(background, null, size);
            double y = 0;
            foreach (var part in parts)
            {
                // not stretched: a heading's text is narrower than the room it has
                var brush = new VisualBrush(part) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                dc.DrawRectangle(brush, null, new Rect(0, y, part.ActualWidth, part.ActualHeight));
                y += part.ActualHeight + part.Margin.Bottom;
            }
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);

        window.Close();
        return 0;
    }

    private static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>
    /// "Start with Windows" follows the copy that is started: if the task still starts another one
    /// (an older copy left in another folder), that one would come back at every logon. Not for a
    /// copy that won't stay: a dev build ("0.1.9-dev"), the compiler's output, or one started
    /// straight out of the zip (Windows unpacks that into the temp folder). In the background:
    /// asking the Task Scheduler takes a moment.
    /// </summary>
    private static void FollowAutostart(AppController app)
    {
        if (AppVersion.Text.Contains('-') || !Core.Updates.UpdateInstaller.CanInstallInto(AppContext.BaseDirectory)
            || Environment.ProcessPath!.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                if (Autostart.FollowThisCopy() is { } before)
                    app.Log.Add(LogKind.Info, T($"\"Start with Windows\" started another copy ({before}). From now on it starts this one ({Environment.ProcessPath})."));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // the Task Scheduler can't be asked: Settings still says which copy starts
            }
        });
    }

    /// <summary>The mutex every Windows account on this PC sees, held until AuFantic exits; null if another account's AuFantic holds it.</summary>
    private static Mutex? HoldForThisPc()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, DataFolder.PcMutex, out bool first);
            if (first)
                return mutex;
            mutex.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // it exists, and belongs to the other account
        }
        return null;
    }

    /// <summary>The exe of the AuFantic that is already running, if it's another copy than this one; null if it's this one, or can't be told.</summary>
    private static string? RunningCopy()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!)))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } exe
                        && !string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        return exe;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // it ended just now, or Windows doesn't say where it runs from
                }
            }
        }
        return null;
    }

    /// <summary>Waits (at most a minute) until the process with that id has ended.</summary>
    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(TimeSpan.FromMinutes(1));
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
