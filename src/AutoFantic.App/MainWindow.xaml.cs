using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Reports;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using ComboBox = System.Windows.Controls.ComboBox;
using Path = System.IO.Path;

namespace AutoFantic.App;

/// <summary>
/// The AutoFantic window: what the fans do right now (Overview), their curves to look at and to
/// set (Fan curves), calibrating and why the settings are what they are (Calibration), and the
/// few settings plus developer tools. Everything it changes goes through <see cref="AppController"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>One colour per fan group, from the validated palette; the same order as the calibration page.</summary>
    internal static readonly Color[] Palette =
    [
        Color.FromRgb(0x2a, 0x78, 0xd6), Color.FromRgb(0xeb, 0x68, 0x34), Color.FromRgb(0x1b, 0xaf, 0x7a), Color.FromRgb(0xed, 0xa1, 0x00),
        Color.FromRgb(0xe8, 0x7b, 0xa4), Color.FromRgb(0x00, 0x83, 0x00), Color.FromRgb(0x4a, 0x3a, 0xa7), Color.FromRgb(0xe3, 0x49, 0x48),
    ];

    private static readonly SolidColorBrush Running = new(Color.FromRgb(0x1b, 0xaf, 0x7a));
    private static readonly SolidColorBrush Paused = new(Color.FromRgb(0x8a, 0x89, 0x84));
    private static readonly SolidColorBrush Cooling = new(Color.FromRgb(0xeb, 0x68, 0x34));
    private static readonly SolidColorBrush Problem = new(Color.FromRgb(0xe3, 0x49, 0x48));

    // loudness choices for "Your fans": label → dB correction
    private static readonly (string Label, double Db)[] LoudnessChoices = [("quiet", -5), ("normal", 0), ("loud", 5)];

    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(ProgressBar Bar, TextBlock Value)> _fanRows = [];
    private readonly List<ToggleButton> _tabs = [];
    private readonly List<(ToggleButton Button, Preset Preset)> _presets = [];
    private readonly Queue<string> _log = new();
    private int _selected;
    private bool _building;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        NavOverview.Checked += (_, _) => ShowOnly(OverviewPage);
        NavCurves.Checked += (_, _) => ShowOnly(CurvesPage);
        NavCalibration.Checked += (_, _) => ShowOnly(CalibrationPage);
        NavSettings.Checked += (_, _) => ShowOnly(SettingsPage);

        PauseButton.Click += (_, _) =>
        {
            _app.UserPaused = !_app.UserPaused;
            UpdateLive();
        };

        Editor.CurveEdited += curve => _app.SetCurve(_selected, curve, AllowStop.IsChecked == true);
        AllowStop.Click += (_, _) => _app.SetCurve(_selected, _app.Effective.Groups[_selected].Curve, AllowStop.IsChecked == true);
        ResetCurve.Click += (_, _) => _app.ResetCurve(_selected);

        StartCalibrationButton.Click += (_, _) => StartCalibration();
        StopCalibrationButton.Click += (_, _) => _app.StopCalibration();
        RoomTemp.Text = _app.Recommended.Ambient.ToString("0", CultureInfo.InvariantCulture);

        AutostartBox.IsChecked = Autostart.IsEnabled();
        AutostartBox.Click += (_, _) =>
        {
            bool ok = AutostartBox.IsChecked == true ? Autostart.Enable() : Autostart.Disable();
            if (!ok)
                MessageBox.Show(this, "Could not change the Windows start task.", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartBox.IsChecked = Autostart.IsEnabled();
        };
        OpenPage.Click += (_, _) => OpenInExplorer(_app.PagePath);
        OpenFolder.Click += (_, _) => OpenInExplorer(_app.RunsPath);
        DataPath.Text = _app.RunsPath;
        FindFansButton.Click += (_, _) => FindFans();
        SensorsButton.Click += (_, _) => ShowSensors();
        ConsoleButton.Click += (_, _) => OpenConsole();

        _app.CurvesChanged += OnCurvesChanged;
        _app.CalibrationProgress += OnCalibrationProgress;
        _app.CalibrationLog += OnCalibrationLog;
        _app.CalibrationEnded += OnCalibrationEnded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.CurvesChanged -= OnCurvesChanged;
            _app.CalibrationProgress -= OnCalibrationProgress;
            _app.CalibrationLog -= OnCalibrationLog;
            _app.CalibrationEnded -= OnCalibrationEnded;
        };

        BuildFanRows();
        BuildTabs();
        BuildPresets();
        SelectCurve(0);
        UpdatePresets();
        UpdateCalibrationInfo();
        BuildInsights();
        UpdateCalibrationState();

        _timer.Tick += (_, _) => UpdateLive();
        _timer.Start();
        UpdateLive();
    }

    /// <summary>"overview", "curves", "calibration" or "settings".</summary>
    public void ShowPage(string page) =>
        (page switch { "curves" => NavCurves, "calibration" => NavCalibration, "settings" => NavSettings, _ => NavOverview }).IsChecked = true;

    private void ShowOnly(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { OverviewPage, CurvesPage, CalibrationPage, SettingsPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── overview ───────────────────────────────────────────────────────────────────────

    private void BuildFanRows()
    {
        FanRows.Children.Clear();
        _fanRows.Clear();
        var groups = _app.Effective.Groups;
        for (int g = 0; g < groups.Count; g++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = groups[g].Name, FontSize = 14 });
            name.Children.Add(new TextBlock { Text = CalibrationReport.Role(groups[g]), Style = (Style)FindResource("Caption") });
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 16, 0) };
            var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

            row.Children.Add(Dot(g));
            Place(row, name, 1);
            Place(row, bar, 2);
            Place(row, value, 3);
            FanRows.Children.Add(row);
            _fanRows.Add((bar, value));
        }
    }

    private void BuildPresets()
    {
        foreach (var preset in Preset.All)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = preset.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = preset.Description, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) });
            var button = new ToggleButton
            {
                Style = (Style)FindResource("Choice"),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 10, 0),
                Content = content,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            var chosen = preset;
            button.Click += (_, _) => SwitchPreset(chosen);
            _presets.Add((button, preset));
            PresetButtons.Children.Add(button);
        }
    }

    private void UpdateLive()
    {
        if (_app.Loop.Last is not { } status)
            return;

        var (brush, title, detail) = _app.Calibrating
            ? (Cooling, "Calibrating", "The calibration drives the fans right now. See the Calibration page.")
            : status.State switch
            {
                LoopState.Paused => (Paused, _app.Sleeping ? "Paused for sleep" : "Paused", "The BIOS controls the fans. Resume to hand them back to AutoFantic."),
                LoopState.CoolingDown => (Cooling, "Cooling down", "A temperature limit was reached: all fans run at 100 % until it's safely cool again."),
                LoopState.SensorProblem => (Problem, "Sensor problem", "A temperature sensor stopped reporting: the BIOS controls the fans for now; AutoFantic tries again every minute."),
                _ => (Running, "AutoFantic controls your fans", "They follow your curves and are off at idle where that was found safe. The safety limits always stay on."),
            };
        StateDot.Fill = brush;
        LogoDot.Fill = brush;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        PauseButton.Content = _app.UserPaused ? "Resume" : "Pause";
        PauseButton.IsEnabled = !_app.Calibrating;

        CpuTemp.Text = Temp(status.CpuTemp);
        GpuTemp.Text = Temp(status.GpuTemp);
        CpuPower.Text = $"{status.CpuPower:0} W";
        GpuPower.Text = $"{status.GpuPower:0} W";
        SideStatus.Text = $"{_app.Effective.Profile} · {status.Time:HH:mm:ss}";

        for (int i = 0; i < _fanRows.Count && i < status.Fans.Count; i++)
        {
            double? percent = _app.Calibrating ? null : status.Fans[i].Percent;
            _fanRows[i].Bar.Value = percent ?? 0;
            _fanRows[i].Value.Text = _app.Calibrating ? "test" : percent switch { null => "BIOS", 0 => "off", { } p => $"{p:0} %" };
        }

        var group = _app.Effective.Groups[Math.Min(_selected, _app.Effective.Groups.Count - 1)];
        double? followed = group.Follows == Component.Cpu ? status.CpuTemp : status.GpuTemp;
        Editor.Live = followed is { } t && _selected < status.Fans.Count ? (t, status.Fans[_selected].Percent) : null;
        Editor.Refresh();
    }

    private void UpdatePresets()
    {
        var current = _app.Preset;
        foreach (var (button, preset) in _presets)
        {
            button.IsChecked = preset.Id == current.Id;
            button.IsEnabled = _app.CanSwitchPreset && !_app.Calibrating;
        }
        ProfileNote.Visibility = _app.CanSwitchPreset ? Visibility.Collapsed : Visibility.Visible;
        ProfileNote.Text = "Switching needs stored measurements: run one calibration with this version first.";

        var profile = current.Profile;
        LimitValue.Text = current.Name;
        LimitDetail.Text = profile.MaxCooling ? "every fan up to its knee" : $"CPU ≤ {profile.Cpu:0} °C · GPU ≤ {profile.GpuCore:0} °C";
    }

    private void SwitchPreset(Preset preset)
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        bool ok = _app.SwitchPreset(preset);
        Cursor = null;
        if (!ok)
            MessageBox.Show(this, "The settings couldn't be worked out again: not enough stored measurements.", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdatePresets();
    }

    // ── fan curves ─────────────────────────────────────────────────────────────────────

    private void BuildTabs()
    {
        CurveTabs.Children.Clear();
        _tabs.Clear();
        var groups = _app.Effective.Groups;
        for (int g = 0; g < groups.Count; g++)
        {
            int index = g;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Dot(g));
            content.Children.Add(new TextBlock { Text = groups[g].Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var tab = new ToggleButton { Style = (Style)FindResource("Choice"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), Content = content };
            tab.Click += (_, _) => SelectCurve(index);
            _tabs.Add(tab);
            CurveTabs.Children.Add(tab);
        }
    }

    private void SelectCurve(int index)
    {
        _selected = Math.Min(index, _app.Effective.Groups.Count - 1);
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsChecked = i == _selected;

        var effective = _app.Effective.Groups[_selected];
        var recommended = _app.Recommended.Groups[_selected];
        var color = Palette[_selected % Palette.Length];

        Editor.Color = color;
        Editor.TemperatureLabel = effective.Follows == Component.Cpu ? "CPU temperature" : "GPU temperature";
        Editor.SetCurves(effective.Curve, recommended.Curve);

        bool custom = _app.IsCustom(_selected);
        CurveTitle.Text = effective.Name;
        CurveSubtitle.Text = $"Follows the {(effective.Follows == Component.Cpu ? "CPU" : "GPU")} temperature · {CalibrationReport.Role(effective)} · "
            + (custom ? "your own curve" : "the recommended curve");
        LegendUse.Fill = new SolidColorBrush(color);
        LegendRecommended.Stroke = new SolidColorBrush(Color.FromArgb(0x90, color.R, color.G, color.B));

        AllowStop.IsEnabled = _app.CanStop(_selected);
        AllowStop.IsChecked = _app.AllowsStop(_selected);
        AllowStop.ToolTip = _app.CanStop(_selected) ? null : "The fans-off test didn't find it safe to stop this fan at idle.";
        ResetCurve.IsEnabled = custom;
    }

    private void OnCurvesChanged() => Dispatcher.BeginInvoke(() =>
    {
        SelectCurve(_selected);
        UpdatePresets();
        UpdateCalibrationInfo();
        BuildInsights();
    });

    // ── calibration ────────────────────────────────────────────────────────────────────

    private void StartCalibration()
    {
        if (!double.TryParse(RoomTemp.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double room) || room is < 5 or > 45)
        {
            MessageBox.Show(this, "Please enter the room temperature in °C (a guess is fine, e.g. 22).", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _log.Clear();
        CalibrationChart.Clear();
        _app.StartCalibration(room, LoadBuiltIn.IsChecked == true);
        CalibrationStatus.Text = "Starting …";
        UpdateCalibrationState();
    }

    private void FindFans()
    {
        var answer = MessageBox.Show(this, "Every fan output runs through a few speeds, one after another (about 2 minutes). Best while the PC is idle. Start?",
            "Find my fans", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        _log.Clear();
        CalibrationChart.Clear();
        _app.StartFindFans();
        CalibrationStatus.Text = "Finding fans …";
        UpdateCalibrationState();
        ShowPage("calibration");
    }

    private void UpdateCalibrationState()
    {
        bool running = _app.Calibrating;
        StartCalibrationButton.IsEnabled = !running;
        StopCalibrationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrateSetup.IsEnabled = !running;
        CalibrationLive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrationLogText.Visibility = _log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindFansButton.IsEnabled = !running;
        foreach (var (button, _) in _presets)
            button.IsEnabled = _app.CanSwitchPreset && !running;
    }

    private void OnCalibrationProgress(CalibrationProgress p) => Dispatcher.BeginInvoke(() =>
    {
        CalibrationStatus.Text = p.Message;
        CalibrationBar.Value = p.Done;
        if (p.Speeds is { } speeds)
        {
            var groups = _app.Inventory.Groups();
            CalibrationSpeeds.Text = "Fans now: " + string.Join(" · ", groups.Select((g, i) => i < speeds.Count ? $"{g.Name} {speeds[i]:0} %" : g.Name));
        }
        else
        {
            CalibrationSpeeds.Text = p.Stage switch
            {
                CalibrationStage.FansOff => "Every fan stands still for up to 2½ minutes.",
                CalibrationStage.WaitingForLoad => "The fans are on BIOS control until the load is there.",
                _ => "",
            };
        }
        if (p.Sample is { } s)
            CalibrationChart.Add(s.CpuTemp, s.GpuTemp);
    });

    private void OnCalibrationLog(string line) => Dispatcher.BeginInvoke(() =>
    {
        _log.Enqueue(line);
        while (_log.Count > 8)
            _log.Dequeue();
        CalibrationLogText.Text = string.Join(Environment.NewLine, _log);
        CalibrationLogText.Visibility = Visibility.Visible;
    });

    private void OnCalibrationEnded(CalibrationOutcome outcome) => Dispatcher.BeginInvoke(() =>
    {
        UpdateCalibrationState();
        UpdatePresets();
        BuildInsights();
        MessageBox.Show(this, outcome.Message + (outcome.Success && outcome.Result is not null ? "\n\nThe new settings are in use; the Calibration page explains them." : ""),
            "AutoFantic", MessageBoxButton.OK, outcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    });

    /// <summary>"Why these settings", "Your fans", "What each fan cools" and "Runs".</summary>
    private void BuildInsights()
    {
        _building = true;
        try
        {
            BuildWhy();
            BuildLoudness();
            BuildEffects();
            BuildRuns();
        }
        finally
        {
            _building = false;
        }
    }

    private void BuildWhy()
    {
        WhyRows.Children.Clear();
        var preset = _app.Preset;
        WhyIntro.Text = $"{preset.Name}: {preset.Description} For each load level, from idle to 30 % beyond the heaviest load measured, the part that limits the cooling and why the fans run as they do.";
        foreach (var level in _app.Levels())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            head.Children.Add(new TextBlock { Text = Capitalize(level.Label), FontSize = 14, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new TextBlock { Text = $"CPU {level.CpuPower:0} W · GPU {level.GpuPower:0} W", Style = (Style)FindResource("Caption") });
            head.Children.Add(Badge($"{CalibrationInsights.Name(level.Bottleneck)} {level.Temperature:0} / {level.Allowed:0} °C", level.Temperature > level.Allowed));

            row.Children.Add(head);
            Place(row, new TextBlock { Text = level.Why, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }, 1);
            WhyRows.Children.Add(row);
        }
        if (WhyRows.Children.Count == 0)
            WhyRows.Children.Add(new TextBlock { Text = "No explanation yet: calibrate once with this version.", Style = (Style)FindResource("Caption") });
    }

    private void BuildLoudness()
    {
        LoudnessRows.Children.Clear();
        var groups = _app.Inventory.Groups();
        for (int g = 0; g < groups.Count; g++)
        {
            int index = g;
            var group = groups[g];
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var count = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 10, 0) };
            for (int n = 1; n <= 6; n++)
                count.Items.Add(n == 1 ? "1 fan" : $"{n} fans");
            count.SelectedIndex = Math.Clamp(group.Headers.Sum(h => h.FanCount), 1, 6) - 1;

            var loudness = new ComboBox { Width = 110 };
            foreach (var (label, _) in LoudnessChoices)
                loudness.Items.Add(label);
            double db = group.Headers[0].LoudnessDb;
            loudness.SelectedIndex = Array.FindIndex(LoudnessChoices, c => Math.Abs(c.Db - db) < 0.1) is >= 0 and var i ? i : 1;

            void Changed()
            {
                if (_building)
                    return;
                Cursor = System.Windows.Input.Cursors.Wait;
                _app.SetLoudness(index, count.SelectedIndex + 1, LoudnessChoices[Math.Max(0, loudness.SelectedIndex)].Db);
                Cursor = null;
            }
            count.SelectionChanged += (_, _) => Changed();
            loudness.SelectionChanged += (_, _) => Changed();

            row.Children.Add(Dot(g));
            Place(row, new TextBlock { Text = group.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, 1);
            Place(row, count, 2);
            Place(row, loudness, 3);
            LoudnessRows.Children.Add(row);
        }
    }

    private void BuildEffects()
    {
        EffectRows.Children.Clear();
        var groups = _app.Recommended.Groups;
        double max = Math.Max(1, groups.Max(g => Math.Max(g.CpuEffect, g.GpuEffect)));
        foreach (var (group, g) in groups.Select((x, i) => (x, i)))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bars = new StackPanel();
            bars.Children.Add(EffectBar("CPU", group.CpuEffect, max, LiveChart.CpuColor));
            bars.Children.Add(EffectBar("GPU", group.GpuEffect, max, LiveChart.GpuColor));

            row.Children.Add(Dot(g));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = group.Name, FontSize = 14 });
            name.Children.Add(new TextBlock { Text = CalibrationReport.Role(group), Style = (Style)FindResource("Caption") });
            Place(row, name, 1);
            Place(row, bars, 2);
            EffectRows.Children.Add(row);
        }
    }

    private FrameworkElement EffectBar(string label, double value, double max, Color color)
    {
        var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, value / max), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - value / max), GridUnitType.Star) });
        track.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(4) });
        line.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center });
        Place(line, track, 1);
        Place(line, new TextBlock { Text = $"+{value:0.0} °C", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }, 2);
        return line;
    }

    private void BuildRuns()
    {
        RunRows.Children.Clear();
        var runs = _app.Runs();
        if (runs.Count == 0)
        {
            RunRows.Children.Add(new TextBlock { Text = "No runs stored yet.", Style = (Style)FindResource("Caption") });
            return;
        }

        var names = _app.Inventory.Groups().Select(g => g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}").ToList();
        RunRows.Children.Add(RunRow(true, "When", "Load", string.Join(" · ", names) + " (%)", "CPU / GPU W", "CPU / GPU °C", "Bottleneck", false));
        string? lastSource = null;
        foreach (var run in runs)
        {
            string source = run.Source == lastSource ? "" : run.Source + (run.Imported ? " (imported)" : "");
            lastSource = run.Source;
            string bottleneck = $"{CalibrationInsights.Name(run.Bottleneck)} {run.Final.GetValueOrDefault(run.Bottleneck):0} °C, "
                + (run.Headroom >= 0 ? $"{run.Headroom:0} below limit" : $"{-run.Headroom:0} above limit");
            RunRows.Children.Add(RunRow(false, $"{run.Time:dd.MM. HH:mm}", source, string.Join(" · ", run.Speeds.Select(s => $"{s.Percent:0}")),
                $"{run.CpuPower:0} / {run.GpuPower:0}", $"{Temp(run.Final.GetValueOrDefault(Component.Cpu))} / {Temp(run.Final.GetValueOrDefault(Component.GpuCore))}",
                bottleneck, run.Headroom < 0));
        }
    }

    private FrameworkElement RunRow(bool header, string when, string source, string speeds, string power, string temps, string bottleneck, bool over)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, header ? 8 : 4) };
        foreach (double w in new[] { 90, 200, 150, 100, 110 })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] cells = [when, source, speeds, power, temps, bottleneck];
        for (int c = 0; c < cells.Length; c++)
        {
            var text = new TextBlock { Text = cells[c], FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
            if (header)
                text.Style = (Style)FindResource("Caption");
            if (c == 5 && over)
                text.FontWeight = FontWeights.SemiBold;
            Place(row, text, c);
        }
        return row;
    }

    // ── settings ───────────────────────────────────────────────────────────────────────

    private void UpdateCalibrationInfo()
    {
        var sources = _app.Recommended.Sources ?? [];
        CalibrationInfo.Text = sources.Count == 0
            ? $"Calibrated {_app.Recommended.Created:dd.MM.yyyy HH:mm}."
            : $"{_app.Store.Calibrations.Count} calibration(s), {_app.Store.Runs.Count} runs: " + string.Join("; ", sources);
    }

    private void ShowSensors()
    {
        string path = Path.Combine(_app.RunsPath, "sensors.txt");
        File.WriteAllText(path, _app.SensorList());
        OpenInExplorer(path);
    }

    /// <summary>The old test console needs the fans to itself: AutoFantic hands them to the BIOS and closes first.</summary>
    private void OpenConsole()
    {
        string console = Path.Combine(AppContext.BaseDirectory, "autofantic-spike.exe");
        if (!File.Exists(console))
        {
            MessageBox.Show(this, $"The test console (autofantic-spike.exe) isn't next to AutoFantic ({AppContext.BaseDirectory}).", "AutoFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, "AutoFantic closes (the fans go back to the BIOS) and the test console opens. Start AutoFantic again afterwards.",
            "Test console", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
            return;

        _app.Loop.Dispose();
        Process.Start(new ProcessStartInfo(console, "test") { UseShellExecute = true })?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private static Ellipse Dot(int group) => new()
    {
        Width = 10,
        Height = 10,
        Fill = new SolidColorBrush(Palette[group % Palette.Length]),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Badge(string text, bool over) => new()
    {
        Margin = new Thickness(0, 6, 0, 0),
        Padding = new Thickness(8, 2, 8, 3),
        CornerRadius = new CornerRadius(10),
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = over ? new SolidColorBrush(Color.FromArgb(0x30, 0xe3, 0x49, 0x48)) : (TryFindResource("SubtleFillColorSecondaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gainsboro),
        Child = new TextBlock { Text = (over ? "⚠ " : "") + text, FontSize = 12 },
    };

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static void OpenInExplorer(string path)
    {
        // through Explorer, so a browser or editor doesn't inherit AutoFantic's admin rights
        using var _ = Process.Start("explorer.exe", $"\"{path}\"");
    }

    private static string Temp(double? celsius) => celsius is { } c and > 0 ? $"{c:0} °C" : "–";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
