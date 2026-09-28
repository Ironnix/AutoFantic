using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Control;

namespace AutoFanatic.Tray;

/// <summary>
/// The AutoFanatic window: what the fans do right now (Overview), their curves to look at and to
/// set (Fan curves), and the few settings. Everything it changes goes through <see cref="AppController"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>One colour per fan group, from the validated palette; the same order as the calibration page.</summary>
    internal static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0x2a, 0x78, 0xd6), System.Windows.Media.Color.FromRgb(0xeb, 0x68, 0x34),
        System.Windows.Media.Color.FromRgb(0x1b, 0xaf, 0x7a), System.Windows.Media.Color.FromRgb(0xed, 0xa1, 0x00),
        System.Windows.Media.Color.FromRgb(0xe8, 0x7b, 0xa4), System.Windows.Media.Color.FromRgb(0x00, 0x83, 0x00),
        System.Windows.Media.Color.FromRgb(0x4a, 0x3a, 0xa7), System.Windows.Media.Color.FromRgb(0xe3, 0x49, 0x48),
    ];

    private static readonly SolidColorBrush Running = new(System.Windows.Media.Color.FromRgb(0x1b, 0xaf, 0x7a));
    private static readonly SolidColorBrush Paused = new(System.Windows.Media.Color.FromRgb(0x8a, 0x89, 0x84));
    private static readonly SolidColorBrush Cooling = new(System.Windows.Media.Color.FromRgb(0xeb, 0x68, 0x34));
    private static readonly SolidColorBrush Problem = new(System.Windows.Media.Color.FromRgb(0xe3, 0x49, 0x48));

    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(System.Windows.Controls.ProgressBar Bar, TextBlock Value)> _fanRows = [];
    private readonly List<ToggleButton> _tabs = [];
    private int _selected;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        NavOverview.Checked += (_, _) => ShowOnly(OverviewPage);
        NavCurves.Checked += (_, _) => ShowOnly(CurvesPage);
        NavSettings.Checked += (_, _) => ShowOnly(SettingsPage);

        PauseButton.Click += (_, _) =>
        {
            _app.UserPaused = !_app.UserPaused;
            UpdateLive();
        };
        Max80.Click += (_, _) => SwitchProfile(80);
        Max90.Click += (_, _) => SwitchProfile(90);

        Editor.CurveEdited += curve => _app.SetCurve(_selected, curve, AllowStop.IsChecked == true);
        AllowStop.Click += (_, _) => _app.SetCurve(_selected, _app.Effective.Groups[_selected].Curve, AllowStop.IsChecked == true);
        ResetCurve.Click += (_, _) => _app.ResetCurve(_selected);

        AutostartBox.IsChecked = Autostart.IsEnabled();
        AutostartBox.Click += (_, _) =>
        {
            bool ok = AutostartBox.IsChecked == true ? Autostart.Enable() : Autostart.Disable();
            if (!ok)
                System.Windows.MessageBox.Show(this, "Could not change the Windows start task.", "AutoFanatic", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartBox.IsChecked = Autostart.IsEnabled();
        };
        OpenPage.Click += (_, _) => OpenInExplorer(_app.PagePath);
        OpenFolder.Click += (_, _) => OpenInExplorer(_app.RunsPath);
        DataPath.Text = _app.RunsPath;

        _app.CurvesChanged += OnCurvesChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.CurvesChanged -= OnCurvesChanged;
        };

        BuildFanRows();
        BuildTabs();
        SelectCurve(0);
        UpdateProfile();
        UpdateCalibrationInfo();

        _timer.Tick += (_, _) => UpdateLive();
        _timer.Start();
        UpdateLive();
    }

    /// <summary>"overview", "curves" or "settings".</summary>
    public void ShowPage(string page) =>
        (page switch { "curves" => NavCurves, "settings" => NavSettings, _ => NavOverview }).IsChecked = true;

    private void ShowOnly(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { OverviewPage, CurvesPage, SettingsPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── overview ───────────────────────────────────────────────────────────────────────

    private void BuildFanRows()
    {
        var groups = _app.Effective.Groups;
        for (int g = 0; g < groups.Count; g++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Palette[g % Palette.Length]), VerticalAlignment = VerticalAlignment.Center };
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = groups[g].Name, FontSize = 14 });
            name.Children.Add(new TextBlock { Text = Role(groups[g]), Style = (Style)FindResource("Caption") });
            var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 16, 0) };
            var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(name, 1);
            Grid.SetColumn(bar, 2);
            Grid.SetColumn(value, 3);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(bar);
            row.Children.Add(value);
            FanRows.Children.Add(row);
            _fanRows.Add((bar, value));
        }
    }

    private void UpdateLive()
    {
        if (_app.Loop.Last is not { } status)
            return;

        var (brush, title, detail) = status.State switch
        {
            LoopState.Paused => (Paused, _app.Sleeping ? "Paused for sleep" : "Paused", "The BIOS controls the fans. Resume to hand them back to AutoFanatic."),
            LoopState.CoolingDown => (Cooling, "Cooling down", "A temperature limit was reached: all fans run at 100 % until it's safely cool again."),
            LoopState.SensorProblem => (Problem, "Sensor problem", "A temperature sensor stopped reporting: the BIOS controls the fans for now; AutoFanatic tries again every minute."),
            _ => (Running, "AutoFanatic controls your fans", "They follow your curves and are off at idle where that was found safe. The safety limits always stay on."),
        };
        StateDot.Fill = brush;
        LogoDot.Fill = brush;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        PauseButton.Content = _app.UserPaused ? "Resume" : "Pause";

        CpuTemp.Text = Temp(status.CpuTemp);
        GpuTemp.Text = Temp(status.GpuTemp);
        CpuPower.Text = $"{status.CpuPower:0} W";
        GpuPower.Text = $"{status.GpuPower:0} W";
        SideStatus.Text = $"{_app.Effective.Profile} · {status.Time:HH:mm:ss}";

        for (int i = 0; i < _fanRows.Count && i < status.Fans.Count; i++)
        {
            double? percent = status.Fans[i].Percent;
            _fanRows[i].Bar.Value = percent ?? 0;
            _fanRows[i].Value.Text = percent switch { null => "BIOS", 0 => "off", { } p => $"{p:0} %" };
        }

        var group = _app.Effective.Groups[_selected];
        double? followed = group.Follows == Component.Cpu ? status.CpuTemp : status.GpuTemp;
        Editor.Live = followed is { } t && _selected < status.Fans.Count ? (t, status.Fans[_selected].Percent) : null;
        Editor.Refresh();
    }

    private void UpdateProfile()
    {
        double limit = _app.ProfileLimit;
        Max80.IsChecked = limit < 85;
        Max90.IsChecked = limit >= 85;
        Max80.IsEnabled = Max90.IsEnabled = _app.CanSwitchProfile;
        ProfileNote.Visibility = _app.CanSwitchProfile ? Visibility.Collapsed : Visibility.Visible;
        ProfileNote.Text = "Switching needs stored measurements: run one calibration with this version first.";

        var profile = Profile.Max(limit);
        LimitValue.Text = $"Max {limit:0}";
        LimitDetail.Text = $"CPU ≤ {profile.Cpu:0} °C · GPU ≤ {profile.GpuCore:0} °C";
    }

    private void SwitchProfile(double limit)
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        bool ok = _app.SwitchProfile(limit);
        Cursor = null;
        if (!ok)
            System.Windows.MessageBox.Show(this, "The curves couldn't be worked out again: not enough stored measurements.", "AutoFanatic", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateProfile();
    }

    // ── fan curves ─────────────────────────────────────────────────────────────────────

    private void BuildTabs()
    {
        var groups = _app.Effective.Groups;
        for (int g = 0; g < groups.Count; g++)
        {
            int index = g;
            var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Palette[g % Palette.Length]), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = groups[g].Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var tab = new ToggleButton { Style = (Style)FindResource("Choice"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), Content = content };
            tab.Click += (_, _) => SelectCurve(index);
            _tabs.Add(tab);
            CurveTabs.Children.Add(tab);
        }
    }

    private void SelectCurve(int index)
    {
        _selected = index;
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsChecked = i == index;

        var effective = _app.Effective.Groups[index];
        var recommended = _app.Recommended.Groups[index];
        var color = Palette[index % Palette.Length];

        Editor.Color = color;
        Editor.TemperatureLabel = effective.Follows == Component.Cpu ? "CPU temperature" : "GPU temperature";
        Editor.SetCurves(effective.Curve, recommended.Curve);

        bool custom = _app.IsCustom(index);
        CurveTitle.Text = effective.Name;
        CurveSubtitle.Text = $"Follows the {(effective.Follows == Component.Cpu ? "CPU" : "GPU")} temperature · {Role(effective)} · "
            + (custom ? "your own curve" : "the recommended curve");
        LegendUse.Fill = new SolidColorBrush(color);
        LegendRecommended.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, color.R, color.G, color.B));

        AllowStop.IsEnabled = _app.CanStop(index);
        AllowStop.IsChecked = _app.AllowsStop(index);
        AllowStop.ToolTip = _app.CanStop(index) ? null : "The fans-off test didn't find it safe to stop this fan at idle.";
        ResetCurve.IsEnabled = custom;
    }

    private void OnCurvesChanged()
    {
        SelectCurve(_selected);
        UpdateProfile();
    }

    // ── settings ───────────────────────────────────────────────────────────────────────

    private void UpdateCalibrationInfo()
    {
        var sources = _app.Recommended.Sources ?? [];
        CalibrationInfo.Text = sources.Count == 0
            ? $"Calibrated {_app.Recommended.Created:dd.MM.yyyy HH:mm}."
            : "Built from: " + string.Join("; ", sources);
    }

    private static void OpenInExplorer(string path)
    {
        // through Explorer, so a browser doesn't inherit AutoFanatic's admin rights
        using var _ = Process.Start("explorer.exe", $"\"{path}\"");
    }

    private static string Temp(double? celsius) => celsius is { } c ? $"{c:0} °C" : "–";

    private static string Role(CalibratedGroup g) =>
        g.CpuEffect < 1 && g.GpuEffect < 1 ? "barely any effect"
        : g.CpuEffect >= 2 * g.GpuEffect ? "cools the CPU"
        : g.GpuEffect >= 2 * g.CpuEffect ? "cools the GPU"
        : "case airflow, helps both";
}
