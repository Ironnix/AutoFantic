using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoFantic.Core;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
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
/// set (Fan curves), calibrating and why the settings are what they are (Calibration), what
/// AutoFantic did (Activity), and the few settings plus developer tools. Before the first
/// calibration the Calibration page is the set-up (checks, find my fans, calibrate) and there are
/// no curves yet. Everything it changes goes through <see cref="AppController"/>.
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
    private static readonly SolidColorBrush SetUp = new(Color.FromRgb(0x2a, 0x78, 0xd6));
    private static readonly SolidColorBrush Attention = new(Color.FromRgb(0xed, 0xa1, 0x00));

    // loudness choices for "Your fans": label → dB correction
    private static readonly (string Label, double Db)[] LoudnessChoices = [("quiet", -5), ("normal", 0), ("loud", 5)];

    // how many log entries the Activity page shows
    private const int LogShown = 300;

    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(ProgressBar Bar, TextBlock Value)> _fanRows = [];
    private readonly List<ToggleButton> _tabs = [];
    private readonly List<(ToggleButton Button, Preset Preset)> _presets = [];
    private readonly Queue<string> _log = new();
    private IReadOnlyList<string> _groupsShown = [];
    private bool _wasSetUp;
    private int _selected;
    private bool _building;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        NavOverview.Checked += (_, _) => ShowOnly(OverviewPage);
        NavCurves.Checked += (_, _) => ShowOnly(CurvesPage);
        NavCalibration.Checked += (_, _) => ShowOnly(CalibrationPage);
        NavLog.Checked += (_, _) => ShowOnly(LogPage);
        NavSettings.Checked += (_, _) => ShowOnly(SettingsPage);

        PauseButton.Click += (_, _) =>
        {
            if (!_app.IsSetUp)
            {
                ShowPage("calibration");
                return;
            }
            _app.UserPaused = !_app.UserPaused;
            UpdateLive();
        };

        // drawing a point down to 0 % means "off here": that switches stopping on where the fan can stop
        Editor.CurveEdited += curve => _app.SetCurve(_selected, curve,
            AllowStop.IsChecked == true || (curve.Count > 0 && curve[0].Percent <= 0 && _app.CanStop(_selected)));
        AllowStop.Click += (_, _) =>
        {
            if (_app.Effective is { } effective)
                _app.SetCurve(_selected, effective.Groups[_selected].Curve, AllowStop.IsChecked == true);
        };
        ResetCurve.Click += (_, _) => _app.ResetCurve(_selected);

        StartCalibrationButton.Click += (_, _) => StartCalibration();
        StopCalibrationButton.Click += (_, _) => _app.StopCalibration();
        RoomTemp.Text = (_app.Recommended?.Ambient ?? 22).ToString("0", CultureInfo.InvariantCulture);
        SetupFindFans.Click += (_, _) => FindFans();
        SetupRecheck.Click += (_, _) => Recheck();
        SettingsRecheck.Click += (_, _) => Recheck();

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
        OpenLogFile.Click += (_, _) =>
        {
            if (_app.Log.FilePath is { } log && File.Exists(log))
                OpenInExplorer(log);
        };
        DataPath.Text = _app.RunsPath + (File.Exists(Path.Combine(_app.RunsPath, DataFolder.MigratedNote))
            ? "  ·  earlier versions kept it in runs\\ next to the program; it was copied here once"
            : "");
        FindFansButton.Click += (_, _) => FindFans();
        SensorsButton.Click += (_, _) => ShowSensors();
        ConsoleButton.Click += (_, _) => OpenConsole();

        _app.CurvesChanged += OnCurvesChanged;
        _app.CalibrationProgress += OnCalibrationProgress;
        _app.CalibrationLog += OnCalibrationLog;
        _app.CalibrationEnded += OnCalibrationEnded;
        _app.Log.Added += OnLogAdded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.CurvesChanged -= OnCurvesChanged;
            _app.CalibrationProgress -= OnCalibrationProgress;
            _app.CalibrationLog -= OnCalibrationLog;
            _app.CalibrationEnded -= OnCalibrationEnded;
            _app.Log.Added -= OnLogAdded;
        };

        BuildPresets();
        UpdateStructure();
        UpdatePresets();
        UpdateCalibrationInfo();
        BuildInsights();
        BuildChecks();
        BuildLog();
        UpdateCalibrationState();

        _timer.Tick += (_, _) => UpdateLive();
        _timer.Start();
        UpdateLive();
    }

    /// <summary>"overview", "curves", "calibration", "log" or "settings".</summary>
    public void ShowPage(string page) =>
        (page switch
        {
            "curves" when _app.IsSetUp => NavCurves,
            "calibration" or "setup" => NavCalibration,
            "log" or "activity" => NavLog,
            "settings" => NavSettings,
            _ => NavOverview,
        }).IsChecked = true;

    private void ShowOnly(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { OverviewPage, CurvesPage, CalibrationPage, LogPage, SettingsPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What depends on whether AutoFantic is set up and on which fan groups there are: the
    /// navigation, the set-up cards, the fan rows and the curve tabs. Rebuilt only when that changes
    /// (the fans were found, the first calibration finished, the fans changed).
    /// </summary>
    private void UpdateStructure()
    {
        bool setUp = _app.IsSetUp;
        NavCurves.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        NavCalibration.Content = setUp ? "Calibration" : "Set up";
        CalibrationHeading.Text = setUp ? "Calibration" : "Set up AutoFantic";
        SetupCards.Visibility = setUp ? Visibility.Collapsed : Visibility.Visible;
        InsightCards.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        CalibrateTitle.Text = setUp ? "Calibrate" : "Step 2 · Calibrate";
        BuildFoundFans();

        if (setUp && !_wasSetUp && NavCurves.IsChecked == false && NavCalibration.IsChecked == true && IsLoaded)
            ShowPage("overview"); // just calibrated for the first time: now there's something to see
        if (!setUp && NavCurves.IsChecked == true)
            ShowPage("calibration");
        _wasSetUp = setUp;

        var groups = _app.GroupNames;
        if (groups.SequenceEqual(_groupsShown))
            return;
        _groupsShown = groups;
        BuildFanRows();
        BuildTabs();
        if (setUp)
            SelectCurve(_selected);
    }

    // ── overview ───────────────────────────────────────────────────────────────────────

    private void BuildFanRows()
    {
        FanRows.Children.Clear();
        _fanRows.Clear();
        var names = _app.GroupNames;
        if (names.Count == 0)
        {
            FanRows.Children.Add(new TextBlock { Text = "No fans found yet: Set up → Find my fans.", Style = (Style)FindResource("Caption") });
            return;
        }
        for (int g = 0; g < names.Count; g++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = names[g], FontSize = 14 });
            name.Children.Add(new TextBlock
            {
                Text = _app.Recommended is { } r ? CalibrationReport.Role(r.Groups[g]) : "the BIOS controls it until the first calibration",
                Style = (Style)FindResource("Caption"),
            });
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
                LoopState.NotSetUp => (SetUp, "Not set up yet", "The BIOS controls your fans until AutoFantic knows your PC: find your fans and calibrate once (about 20 minutes)."),
                LoopState.Paused => (Paused, _app.Sleeping ? "Paused for sleep" : "Paused", "The BIOS controls the fans. Resume to hand them back to AutoFantic."),
                LoopState.CoolingDown => (Cooling, "Cooling down", "A temperature limit was reached: all fans run at 100 % until it's safely cool again."),
                LoopState.SensorProblem => (Problem, "Sensor problem", "A temperature sensor stopped reporting: the BIOS controls the fans for now; AutoFantic tries again every minute."),
                _ => (Running, "AutoFantic controls your fans", "They follow your curves and switch off when the PC is idle and cool, where allowed. The safety limits always stay on."),
            };
        StateDot.Fill = brush;
        LogoDot.Fill = brush;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        PauseButton.Content = !_app.IsSetUp ? "Set up" : _app.UserPaused ? "Resume" : "Pause";
        PauseButton.IsEnabled = !_app.Calibrating;

        CpuTemp.Text = Temp(status.CpuTemp);
        GpuTemp.Text = Temp(status.GpuTemp);
        CpuPower.Text = $"{status.CpuPower:0} W";
        GpuPower.Text = $"{status.GpuPower:0} W";
        SideStatus.Text = $"{(_app.Effective?.Profile ?? "not set up")} · {status.Time:HH:mm:ss}";

        for (int i = 0; i < _fanRows.Count; i++)
        {
            double? percent = _app.Calibrating || i >= status.Fans.Count ? null : status.Fans[i].Percent;
            _fanRows[i].Bar.Value = percent ?? 0;
            _fanRows[i].Value.Text = _app.Calibrating ? "test" : percent switch { null => "BIOS", 0 => "off", { } p => $"{p:0} %" };
        }

        // the ring sits at the smoothed temperature the fan control reads the curve at
        if (_app.Effective is not { } effective || effective.Groups.Count == 0)
            return;
        var group = effective.Groups[Math.Min(_selected, effective.Groups.Count - 1)];
        var fan = _selected < status.Fans.Count ? status.Fans[_selected] : null;
        double? followed = fan?.Status?.Temperature ?? CalibrationInsights.Followed(group.Follows, status.CpuTemp, status.GpuTemp);
        Editor.Live = followed is { } t && fan is not null && !_app.Calibrating ? (t, fan.Percent, Note(fan.Status)) : null;
        Editor.Refresh();
    }

    private static string? Note(FanStatus? status) => status switch
    {
        { Note: FanNote.SlowingDown, CurvePercent: { } c } => $"slowing down gently to {Math.Max(0, c):0} %",
        { Note: FanNote.SpeedingUp, CurvePercent: { } c } => $"speeding up to {c:0} %",
        { Note: FanNote.Slowest } => "the slowest speed this fan turns at",
        { Note: FanNote.Off } => "idle and cool",
        { Note: FanNote.Starting } => "starting up (short push)",
        _ => null,
    };

    private void UpdatePresets()
    {
        var current = _app.Preset;
        foreach (var (button, preset) in _presets)
        {
            button.IsChecked = _app.IsSetUp && preset.Id == current.Id;
            button.IsEnabled = _app.CanSwitchPreset && !_app.Calibrating;
        }
        ProfileNote.Visibility = _app.CanSwitchPreset ? Visibility.Collapsed : Visibility.Visible;
        ProfileNote.Text = _app.IsSetUp
            ? "Switching needs stored measurements: run one calibration with this version first."
            : $"The presets work once AutoFantic is calibrated. The first calibration uses {current.Name}; you can switch any time afterwards.";

        var profile = current.Profile;
        LimitValue.Text = _app.IsSetUp ? current.Name : "–";
        LimitDetail.Text = !_app.IsSetUp ? "after the first calibration"
            : profile.MaxCooling ? "every fan up to its knee"
            : $"CPU ≤ {profile.Cpu:0} °C · GPU ≤ {profile.GpuCore:0} °C";
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
        var groups = _app.Effective?.Groups ?? [];
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
        if (_app.Effective is not { } all || _app.Recommended is not { } recommendedAll || all.Groups.Count == 0)
            return;
        _selected = Math.Clamp(index, 0, all.Groups.Count - 1);
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsChecked = i == _selected;

        var effective = all.Groups[_selected];
        var recommended = recommendedAll.Groups[_selected];
        var color = Palette[_selected % Palette.Length];

        Editor.Color = color;
        Editor.TemperatureLabel = Capitalize(CalibrationInsights.FollowsName(effective.Follows));
        Editor.SetCurves(effective.Curve, recommended.Curve);

        bool custom = _app.IsCustom(_selected);
        CurveTitle.Text = effective.Name;
        CurveSubtitle.Text = $"Follows the {CalibrationInsights.FollowsName(effective.Follows)} · {CalibrationReport.Role(effective)} · "
            + (custom ? "your own curve" : "the recommended curve");
        LegendUse.Fill = new SolidColorBrush(color);
        LegendRecommended.Stroke = new SolidColorBrush(Color.FromArgb(0x90, color.R, color.G, color.B));

        bool canStop = _app.CanStop(_selected), allows = _app.AllowsStop(_selected);
        double offAt = CurveController.OffTemperature(effective);
        string part = CalibrationInsights.Name(effective.Follows);
        AllowStop.IsEnabled = canStop;
        AllowStop.IsChecked = allows;
        StopHint.Text = !canStop
            ? "These fans kept turning at 0 % when AutoFantic measured them, so they can't be switched off."
            : allows
            ? $"Off while the PC is idle and the {part} is at most {offAt:0} °C; on again above {offAt + CurveController.Hysteresis:0} °C or when load comes. Drag the first points down to 0 % to choose the temperature."
              + (_app.RecommendsStop(_selected) ? "" : " The calibration keeps these fans turning at idle: switching them off is your choice.")
            : "Can be switched off when the PC is idle and cool (dragging a point to 0 % does it too)."
              + (_app.RecommendsStop(_selected) ? " The calibration recommends it." : "");
        Editor.OffBelow = allows ? offAt : null;
        ResetCurve.IsEnabled = custom;
    }

    private void OnCurvesChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateStructure();
        SelectCurve(_selected);
        UpdatePresets();
        UpdateCalibrationInfo();
        BuildInsights();
    });

    // ── calibration and set-up ─────────────────────────────────────────────────────────

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
        // the first calibration waits for step 1, so the user sees what was found first
        StartCalibrationButton.IsEnabled = !running && (_app.IsSetUp || _app.Inventory is not null);
        StartCalibrationButton.ToolTip = StartCalibrationButton.IsEnabled || running ? null : "Find your fans first (step 1).";
        StopCalibrationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrateSetup.IsEnabled = !running;
        CalibrationLive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrationLogText.Visibility = _log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindFansButton.IsEnabled = !running;
        SetupFindFans.IsEnabled = !running;
        foreach (var (button, _) in _presets)
            button.IsEnabled = _app.CanSwitchPreset && !running;
    }

    /// <summary>Step 1's result before the first calibration: the fan groups found, each with how fast it turns and whether it can stop.</summary>
    private void BuildFoundFans()
    {
        FoundFans.Children.Clear();
        var groups = _app.Inventory?.Groups() ?? [];
        FindFansTitle.Text = groups.Count > 0 ? "Step 1 · Find my fans  ✓" : "Step 1 · Find my fans";
        SetupFindFans.Content = groups.Count > 0 ? "Find my fans again" : "Find my fans";
        FoundFans.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            float top = group.Headers.Max(h => h.Rpm.Count > 0 ? h.Rpm.Max(p => p.Rpm) : 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(Dot(g));
            row.Children.Add(new TextBlock { Text = group.Name, FontSize = 14, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock
            {
                Text = $"up to {top:0} rpm · " + (group.CanStop ? "can stop at 0 %" : "keeps turning at 0 %"),
                Style = (Style)FindResource("Caption"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            FoundFans.Children.Add(row);
        }
        if (groups.Count > 0)
            FoundFans.Children.Add(new TextBlock
            {
                Text = "Several fans on one output (a hub or splitter) or a loud fan? Say so on the Calibration page after calibrating, under \"Your fans\".",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 6, 0, 0),
            });
    }

    private void Recheck()
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        _app.RefreshChecks();
        Cursor = null;
        BuildChecks();
    }

    /// <summary>The start-up checks, on the set-up page and in Settings; the ones that need a look also on the Overview.</summary>
    private void BuildChecks()
    {
        foreach (var host in new[] { SetupChecks, SettingsChecks })
        {
            host.Children.Clear();
            foreach (var check in _app.Checks)
                host.Children.Add(CheckRow(check));
        }

        NoticeRows.Children.Clear();
        foreach (var check in _app.Checks.Where(c => c.Result >= CheckResult.Warning))
            NoticeRows.Children.Add(CheckRow(check));
        NoticeCard.Visibility = NoticeRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement CheckRow(SetupCheck check)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyph, brush) = check.Result switch
        {
            CheckResult.Ok => ("✓", Running),
            CheckResult.Info => ("i", Paused),
            CheckResult.Warning => ("!", Attention),
            _ => ("✕", Problem),
        };
        row.Children.Add(new TextBlock { Text = glyph, Foreground = brush, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = check.Title, FontSize = 14 });
        text.Children.Add(new TextBlock { Text = check.Detail, Style = (Style)FindResource("Caption") });
        Place(row, text, 1);
        return row;
    }

    private void OnCalibrationProgress(CalibrationProgress p) => Dispatcher.BeginInvoke(() =>
    {
        CalibrationStatus.Text = p.Message;
        CalibrationBar.Value = p.Done;
        if (p.Speeds is { } speeds && _app.Inventory is { } inventory)
        {
            var groups = inventory.Groups();
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
        UpdateStructure();
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
        var groups = _app.Inventory?.Groups() ?? [];
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
        var groups = _app.Recommended?.Groups ?? [];
        if (groups.Count == 0)
            return;
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
            name.Children.Add(new TextBlock { Text = $"{CalibrationReport.Role(group)} · follows the {CalibrationInsights.FollowsName(group.Follows)}", Style = (Style)FindResource("Caption") });
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

        var names = _app.Inventory!.Groups().Select(g => g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}").ToList();
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

    // ── activity ───────────────────────────────────────────────────────────────────────

    private void BuildLog()
    {
        LogRows.Children.Clear();
        var entries = _app.Log.Entries;
        foreach (var entry in entries.Reverse().Take(LogShown))
            LogRows.Children.Add(LogRow(entry));
        if (entries.Count == 0)
            LogRows.Children.Add(new TextBlock { Text = "Nothing yet.", Style = (Style)FindResource("Caption") });
    }

    private void OnLogAdded(ActivityEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        if (LogRows.Children.Count == 1 && LogRows.Children[0] is TextBlock)
            LogRows.Children.Clear(); // "Nothing yet."
        LogRows.Children.Insert(0, LogRow(entry));
        while (LogRows.Children.Count > LogShown)
            LogRows.Children.RemoveAt(LogRows.Children.Count - 1);
    });

    private FrameworkElement LogRow(ActivityEntry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var time = entry.Time.ToLocalTime();
        row.Children.Add(new TextBlock
        {
            Text = time.Date == DateTime.Today ? $"today {time:HH:mm:ss}" : $"{time:dd.MM. HH:mm:ss}",
            Style = (Style)FindResource("Caption"),
        });
        var brush = entry.Kind switch
        {
            LogKind.Safety or LogKind.Watchdog => Cooling,
            LogKind.Sensor => Problem,
            LogKind.Warning => Attention,
            LogKind.Fans => Running,
            LogKind.Calibration => SetUp,
            _ => Paused,
        };
        var kind = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        kind.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        kind.Children.Add(new TextBlock { Text = entry.Kind.ToString(), FontSize = 13 });
        Place(row, kind, 1);
        Place(row, new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }, 2);
        return row;
    }

    // ── settings ───────────────────────────────────────────────────────────────────────

    private void UpdateCalibrationInfo()
    {
        var sources = _app.Recommended?.Sources ?? [];
        CalibrationInfo.Text = _app.Recommended is not { } r ? "Not calibrated yet."
            : sources.Count == 0 ? $"Calibrated {r.Created:dd.MM.yyyy HH:mm}."
            : $"{_app.Store.Calibrations.Count} calibration(s), {_app.Store.Runs.Count} runs: " + string.Join("; ", sources);
        OpenPage.IsEnabled = File.Exists(_app.PagePath);
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
