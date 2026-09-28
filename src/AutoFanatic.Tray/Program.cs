using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoFanatic.Core;

namespace AutoFanatic.Tray;

/// <summary>
/// AutoFanatic in the background: an icon next to the clock, a window to see and set the curves,
/// the fans on the calibrated curves. Needs admin rights for the hardware driver; asks for them
/// itself, or has them when started by "Start with Windows" (Task Scheduler, highest privileges).
///
/// Options for checking a build without touching the screen (with --simulate):
///   --selftest                      start everything without an icon, run 5 s, exit 0 if it controlled the fans
///   --screenshot file.png [--page overview|curves|settings]   render the window off-screen to a PNG
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool simulate = args.Contains("--simulate");
        bool selfTest = args.Contains("--selftest");
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

        using var single = new Mutex(initiallyOwned: true, DataFolder.BackgroundMutex, out bool first);
        if (!first)
        {
            if (!quiet)
                System.Windows.MessageBox.Show("AutoFanatic is already running: look for its icon next to the clock.", "AutoFanatic", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var wpf = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        wpf.ThemeMode = ThemeMode.System; // Windows 11 look, light or dark like Windows

        var app = AppController.Create(simulate, out string? problem);
        if (app is null)
        {
            if (!quiet)
                System.Windows.MessageBox.Show(problem, "AutoFanatic", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    System.Windows.MessageBox.Show($"AutoFanatic stopped: {e.Exception.Message}\n\nThe fans are back on BIOS control.", "AutoFanatic", MessageBoxButton.OK, MessageBoxImage.Error);
                wpf.Shutdown(1);
                e.Handled = true;
            };
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => app.Loop.Dispose();

            app.Loop.Start();

            if (screenshot is not null)
                return Screenshot(app, screenshot, Option(args, "--page") ?? "overview");

            using var tray = new TrayIcon(app, quiet: selfTest);
            if (selfTest)
                After(TimeSpan.FromSeconds(5), () => wpf.Shutdown(app.Loop.Last is { State: Core.Control.LoopState.Running } ? 0 : 3));
            else if (args.Contains("--open"))
                wpf.Dispatcher.BeginInvoke(() => tray.OpenWindow());

            return wpf.Run();
        }
    }

    /// <summary>Renders the window off-screen (nobody sees it) to a PNG, to check the look of a build.</summary>
    private static int Screenshot(AppController app, string path, string page)
    {
        var window = new MainWindow(app)
        {
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
            dc.DrawRectangle(window.Background ?? System.Windows.Media.Brushes.White, null, size);
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
