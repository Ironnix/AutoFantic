using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using Microsoft.Win32;
using AutoFantic.Core;
using AutoFantic.Core.Control;

namespace AutoFantic.App;

/// <summary>
/// The icon next to the clock. Its colour shows the state, hovering shows the temperatures;
/// double-click (or "Open AuFantic") opens the window, right-click shows every fan and the
/// quick actions. The fan control itself runs in <see cref="FanControlLoop"/>; this only shows it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _temps = new() { Enabled = false };
    private readonly List<ToolStripMenuItem> _fans = [];
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _pause = new(Texts.T("Pause (the BIOS controls the fans)"));
    private readonly ToolStripMenuItem _autostart = new(Texts.T("Start with Windows"));
    private readonly ToolStripMenuItem _update = new() { Visible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ConcurrentQueue<string> _alerts = new();
    private int _selfTick;
    private readonly Dictionary<LoopState, Icon> _icons;
    private MainWindow? _window;
    private Version? _announced;
    private bool _balloonOpensSettings;

    public TrayIcon(AppController app, bool quiet)
    {
        _app = app;
        _icons = new()
        {
            [LoopState.Running] = MakeIcon(Color.FromArgb(0x1b, 0xaf, 0x7a)),
            [LoopState.Paused] = MakeIcon(Color.FromArgb(0x8a, 0x89, 0x84)),
            [LoopState.CoolingDown] = MakeIcon(Color.FromArgb(0xeb, 0x68, 0x34)),
            [LoopState.SensorProblem] = MakeIcon(Color.FromArgb(0xe3, 0x49, 0x48)),
            [LoopState.NotSetUp] = MakeIcon(Color.FromArgb(0x2a, 0x78, 0xd6)),
        };

        var menu = _menu;
        var open = new ToolStripMenuItem(Texts.T("Open AuFantic"), null, (_, _) => OpenWindow(_app.IsSetUp ? "overview" : "calibration"));
        open.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(_temps);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_autostart);
        menu.Items.Add(_update);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Texts.T("Exit (fans back to the BIOS)"), null, (_, _) => System.Windows.Application.Current.Shutdown());
        BuildFanItems();

        _pause.Click += (_, _) =>
        {
            _app.UserPaused = !_app.UserPaused;
            Refresh();
        };
        _autostart.Checked = Autostart.IsEnabled();
        _autostart.Click += (_, _) =>
        {
            bool ok = _autostart.Checked ? Autostart.Disable() : Autostart.Enable();
            if (!ok)
                MessageBox.Show(Texts.T("Could not change the Windows start task."), "AuFantic", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _autostart.Checked = Autostart.IsEnabled();
        };

        _icon = new NotifyIcon
        {
            Icon = _icons[LoopState.Running],
            Text = "AuFantic",
            ContextMenuStrip = menu,
            Visible = !quiet,
        };
        _icon.DoubleClick += (_, _) => OpenWindow(_app.IsSetUp ? "overview" : "calibration");
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOpensSettings)
                OpenWindow("settings");
        };
        _update.Click += (_, _) => OpenWindow("settings");

        _app.Loop.Alert += message => _alerts.Enqueue(message);
        _app.Alert += message => _alerts.Enqueue(message);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (quiet)
            return;
        if (_app.UpdatedFrom is { } from)
            _icon.ShowBalloonTip(5000, Texts.T($"AuFantic updated to {Core.AppVersion.Text}"),
                Texts.T($"From {from}. What's new: Settings → Updates."), ToolTipIcon.Info);
        else if (_app.UnexpectedEnd is { } ended)
            _icon.ShowBalloonTip(10000, Texts.T("AuFantic ended unexpectedly last time"), ended, ToolTipIcon.Warning);
        else if (!_app.IsSetUp)
            _icon.ShowBalloonTip(5000, Texts.T("AuFantic isn't set up yet"),
                Texts.T("The BIOS keeps your fans until the first calibration."), ToolTipIcon.Info);
        else
            _icon.ShowBalloonTip(5000, Texts.T("AuFantic is running"),
                Texts.T("Double-click to open, right-click for quick actions."), ToolTipIcon.Info);
    }

    /// <summary>One disabled menu item per fan group, after the temperatures; rebuilt when the groups change (the first calibration).</summary>
    private void BuildFanItems()
    {
        foreach (var item in _fans)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _fans.Clear();
        int at = _menu.Items.IndexOf(_temps) + 1;
        foreach (var name in _app.GroupNames)
        {
            var item = new ToolStripMenuItem(name) { Enabled = false, Tag = name };
            _fans.Add(item);
            _menu.Items.Insert(at++, item);
        }
    }

    public void OpenWindow(string page = "overview")
    {
        if (_window is null)
        {
            _window = new MainWindow(_app);
            _window.Closed += (_, _) =>
            {
                _window = null;
                MemoryUpkeep.ReleaseSoon();
            };
        }
        _window.ShowPage(page);
        _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized)
            _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    private void Refresh()
    {
        // AuFantic's own memory without the window (Settings → Developer), once a minute while it's closed
        if (_window is null && ++_selfTick % 60 == 0)
            SelfUsage.RememberWindowClosed();

        while (_alerts.TryDequeue(out var alert))
        {
            _balloonOpensSettings = false;
            _icon.ShowBalloonTip(8000, "AuFantic", alert, ToolTipIcon.Warning);
        }

        // a newer version: said once, then a menu item until it's installed
        if (_app.UpdateAvailable is { } release && release.Version != _announced)
        {
            _announced = release.Version;
            _update.Text = Texts.T($"Update to {release.Version} …");
            _update.Visible = true;
            _balloonOpensSettings = true;
            _icon.ShowBalloonTip(8000, Texts.T($"{release.Name} is available"), Texts.T("Click here to update (Settings → Updates)."), ToolTipIcon.Info);
        }

        if (_app.Loop.Last is not { } status)
            return;

        if (!_app.GroupNames.SequenceEqual(_fans.Select(f => (string)f.Tag!)))
            BuildFanItems();

        _icon.Icon = _icons[status.State];
        string state = _app.Calibrating ? Texts.T("calibrating")
            : status.State switch
            {
                LoopState.Paused => _app.Sleeping ? Texts.T("paused for sleep") : Texts.T("paused, BIOS in control"),
                LoopState.CoolingDown => Texts.T("cooling down, all fans 100 %"),
                LoopState.SensorProblem => Texts.T("sensor problem, BIOS in control"),
                LoopState.NotSetUp => Texts.T("not set up yet, BIOS in control"),
                _ when _app.QuietReason is { } quiet => Texts.T($"extra quiet ({Texts.T(quiet)})"),
                _ => Texts.T("running"),
            };
        string temps = $"CPU {T(status.CpuTemp)} · GPU {T(status.GpuTemp)}";
        string tip = $"AuFantic · {temps}\n{state}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127];
        _temps.Text = $"{temps} · {state}";
        for (int i = 0; i < _fans.Count; i++)
        {
            var fan = i < status.Fans.Count ? status.Fans[i] : null;
            _fans[i].Text = $"{_fans[i].Tag}: {MainWindow.Speed(fan)}";
        }
        _pause.Text = _app.UserPaused ? Texts.T("Resume (AuFantic controls the fans)") : Texts.T("Pause (the BIOS controls the fans)");
        _pause.Enabled = _app.IsSetUp && !_app.Calibrating;
    }

    /// <summary>Before sleep the BIOS gets the fans; after waking AuFantic takes them back.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            _app.Sleeping = true;
        else if (e.Mode == PowerModes.Resume)
            _app.Sleeping = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _window?.Close();
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>
    /// The logo (assets\logo.svg: a fan on a rounded square, drawn in a 256 box) with the square in
    /// the state's colour; the tooltip says the state in words too.
    /// </summary>
    private static Icon MakeIcon(Color color)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);

            using var fill = new SolidBrush(color);
            using var square = new GraphicsPath();
            foreach (var (x, y, from) in new[] { (136, 8, 270), (136, 136, 0), (8, 136, 90), (8, 8, 180) })
                square.AddArc(x, y, 112, 112, from, 90);
            square.CloseFigure();
            g.FillPath(fill, square);

            using var blade = new GraphicsPath();
            blade.AddBezier(126, 102, 116, 84, 108, 62, 110, 42);
            blade.AddArc(40, 40, 176, 176, 258.2f, 68.8f);
            blade.AddBezier(202, 80, 182, 86, 164, 98, 152, 116);
            blade.CloseFigure();
            using var edge = new Pen(Color.White, 8) { LineJoin = LineJoin.Round };
            for (int i = 0; i < 3; i++)
            {
                g.FillPath(Brushes.White, blade);
                g.DrawPath(edge, blade);
                g.TranslateTransform(128, 128);
                g.RotateTransform(120);
                g.TranslateTransform(-128, -128);
            }
            g.FillEllipse(Brushes.White, 104, 104, 48, 48);
            g.FillEllipse(fill, 118, 118, 20, 20);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static string T(double? celsius) => celsius is { } c ? $"{c:0} °C" : "–";
}
