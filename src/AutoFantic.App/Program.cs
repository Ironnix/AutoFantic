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

namespace AutoFantic.App;

/// <summary>
/// AutoFantic in the background: an icon next to the clock, a window to see and set the curves,
/// the fans on the calibrated curves. Needs admin rights for the hardware driver; asks for them
/// itself, or has them when started by "Start with Windows" (Task Scheduler, highest privileges).
///
/// Before the first calibration the BIOS keeps the fans and the window opens on its set-up page.
/// A watchdog process (<see cref="Watchdog"/>) hands the fans back if AutoFantic is killed.
///
/// Options for checking a build without touching the screen (with --simulate):
///   --selftest [--seconds 5]        start everything without an icon, run 5 s, exit 0 if it controlled the fans (or, not set up, only watched)
///   --selftest-calibration          a whole calibration with the built-in load, as the window starts it (the first one finds the fans too)
///   --screenshot file.png [--page overview|curves|calibration|log|settings] [--height 2000]   render the window off-screen to a PNG
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
        return RunApp(args);
    }

    // kept apart from Main, so the watchdog never loads WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string[] args)
    {
        bool simulate = args.Contains("--simulate");
        bool selfTest = args.Contains("--selftest") || args.Contains("--selftest-calibration");
        string? screenshot = Option(args, "--screenshot");
        bool quiet = selfTest || screenshot is not null;

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

        // the version from before the rename controls the fans: two programs must never fight over them
        if (!simulate && DataFolder.LegacyBackgroundRunning())
        {
            System.Windows.MessageBox.Show("The older \"AutoFanatic\" is still running and controls the fans.\n\nExit it first (right-click its icon next to the clock → Exit), then start AutoFantic again.",
                "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        if (!simulate)
            Autostart.RemoveLegacy();

        // a simulated copy touches no hardware: it may run next to the real one
        using var single = new Mutex(initiallyOwned: true, simulate ? DataFolder.BackgroundMutex + ".Simulated" : DataFolder.BackgroundMutex, out bool first);
        if (!first)
        {
            if (!quiet)
                System.Windows.MessageBox.Show("AutoFantic is already running: look for its icon next to the clock.", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var wpf = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        wpf.ThemeMode = ThemeMode.System; // Windows 11 look, light or dark like Windows

        var app = AppController.Create(simulate, out string? problem, double.TryParse(Option(args, "--sim-speed"), out double speed) ? speed : 1);
        if (app is null)
        {
            if (!quiet)
                System.Windows.MessageBox.Show(problem, "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        using (app)
        {
            // whatever happens, the fans go back to the BIOS
            AppDomain.CurrentDomain.ProcessExit += (_, _) => app.Loop.Dispose();
            wpf.DispatcherUnhandledException += (_, e) =>
            {
                app.Loop.Dispose();
                if (!quiet)
                    System.Windows.MessageBox.Show($"AutoFantic stopped: {e.Exception.Message}\n\nThe fans are back on BIOS control.", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Error);
                wpf.Shutdown(1);
                e.Handled = true;
            };
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => app.Loop.Dispose();

            app.Loop.Start();
            MemoryUpkeep.Start();

            if (screenshot is not null)
                return Screenshot(app, screenshot, Option(args, "--page") ?? "overview", double.TryParse(Option(args, "--height"), out double h) ? h : null);

            Watchdog.Launch(simulate);

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
            else if (selfTest)
            {
                var expected = app.IsSetUp ? Core.Control.LoopState.Running : Core.Control.LoopState.NotSetUp;
                After(TimeSpan.FromSeconds(double.TryParse(Option(args, "--seconds"), out double s) ? s : 5),
                    () => wpf.Shutdown(app.Loop.Last?.State == expected ? 0 : 3));
            }
            else if (!app.IsSetUp)
                wpf.Dispatcher.BeginInvoke(() => tray.OpenWindow("calibration")); // nothing to do in the background yet: show the set-up
            else if (args.Contains("--open"))
                wpf.Dispatcher.BeginInvoke(() => tray.OpenWindow());

            return wpf.Run();
        }
    }

    /// <summary>Renders the window off-screen (nobody sees it) to a PNG, to check the look of a build.</summary>
    private static int Screenshot(AppController app, string path, string page, double? height)
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

        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // off-screen the window has no backdrop: the theme's own background, so dark mode stays readable
            var background = window.TryFindResource("ApplicationBackgroundBrush") as System.Windows.Media.Brush
                ?? window.TryFindResource("WindowBackground") as System.Windows.Media.Brush
                ?? window.Background ?? System.Windows.Media.Brushes.White;
            dc.DrawRectangle(background, null, size);
            dc.DrawRectangle(new VisualBrush(root), null, size);
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
