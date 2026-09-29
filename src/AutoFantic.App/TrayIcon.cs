using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using Microsoft.Win32;
using AutoFantic.Core.Control;

namespace AutoFantic.App;

/// <summary>
/// The icon next to the clock. Its colour shows the state, hovering shows the temperatures;
/// double-click (or "Open AutoFantic") opens the window, right-click shows every fan and the
/// quick actions. The fan control itself runs in <see cref="FanControlLoop"/>; this only shows it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _temps = new() { Enabled = false };
    private readonly List<ToolStripMenuItem> _fans = [];
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _pause = new("Pause (the BIOS controls the fans)");
    private readonly ToolStripMenuItem _autostart = new("Start with Windows");
    private readonly ToolStripMenuItem _update = new() { Visible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ConcurrentQueue<string> _alerts = new();
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
        var open = new ToolStripMenuItem("Open AutoFantic", null, (_, _) => OpenWindow(_app.IsSetUp ? "overview" : "calibration"));
        open.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(_temps);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_autostart);
        menu.Items.Add(_update);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (fans back to the BIOS)", null, (_, _) => System.Windows.Application.Current.Shutdown());
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
                MessageBox.Show("Could not change the Windows start task.", "AutoFantic", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _autostart.Checked = Autostart.IsEnabled();
        };

        _icon = new NotifyIcon
        {
            Icon = _icons[LoopState.Running],
            Text = "AutoFantic",
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
            _icon.ShowBalloonTip(5000, $"AutoFantic updated to {Core.AppVersion.Text}",
                $"From {from}. What's new: Settings → Updates.", ToolTipIcon.Info);
        else if (_app.UnexpectedEnd is { } ended)
            _icon.ShowBalloonTip(10000, "AutoFantic ended unexpectedly last time", ended, ToolTipIcon.Warning);
        else if (!_app.IsSetUp)
            _icon.ShowBalloonTip(5000, "AutoFantic isn't set up yet",
                "The BIOS keeps your fans until the first calibration.", ToolTipIcon.Info);
        else
            _icon.ShowBalloonTip(5000, "AutoFantic is running",
                "Double-click to open, right-click for quick actions.", ToolTipIcon.Info);
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
        while (_alerts.TryDequeue(out var alert))
        {
            _balloonOpensSettings = false;
            _icon.ShowBalloonTip(8000, "AutoFantic", alert, ToolTipIcon.Warning);
        }

        // a newer version: said once, then a menu item until it's installed
        if (_app.UpdateAvailable is { } release && release.Version != _announced)
        {
            _announced = release.Version;
            _update.Text = $"Update to {release.Version} …";
            _update.Visible = true;
            _balloonOpensSettings = true;
            _icon.ShowBalloonTip(8000, $"{release.Name} is available", "Click here to update (Settings → Updates).", ToolTipIcon.Info);
        }

        if (_app.Loop.Last is not { } status)
            return;

        if (!_app.GroupNames.SequenceEqual(_fans.Select(f => (string)f.Tag!)))
            BuildFanItems();

        _icon.Icon = _icons[status.State];
        string state = _app.Calibrating ? "calibrating"
            : status.State switch
            {
                LoopState.Paused => _app.Sleeping ? "paused for sleep" : "paused, BIOS in control",
                LoopState.CoolingDown => "cooling down, all fans 100 %",
                LoopState.SensorProblem => "sensor problem, BIOS in control",
                LoopState.NotSetUp => "not set up yet, BIOS in control",
                _ when _app.QuietReason is { } quiet => $"extra quiet ({quiet})",
                _ => "running",
            };
        string temps = $"CPU {T(status.CpuTemp)} · GPU {T(status.GpuTemp)}";
        string tip = $"AutoFantic · {temps}\n{state}";
        _icon.Text = tip.Length <= 127 ? tip : tip[..127];
        _temps.Text = $"{temps} · {state}";
        for (int i = 0; i < _fans.Count; i++)
        {
            var fan = i < status.Fans.Count ? status.Fans[i] : null;
            _fans[i].Text = $"{_fans[i].Tag}: {MainWindow.Speed(fan)}";
        }
        _pause.Text = _app.UserPaused ? "Resume (AutoFantic controls the fans)" : "Pause (the BIOS controls the fans)";
        _pause.Enabled = _app.IsSetUp && !_app.Calibrating;
    }

    /// <summary>Before sleep the BIOS gets the fans; after waking AutoFantic takes them back.</summary>
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

    /// <summary>A round dot with a ring: the state is the colour; the tooltip says it in words too.</summary>
    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var ring = new Pen(Color.White, 3.5f);
            g.DrawEllipse(ring, 10, 10, 12, 12);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static string T(double? celsius) => celsius is { } c ? $"{c:0} °C" : "–";
}
