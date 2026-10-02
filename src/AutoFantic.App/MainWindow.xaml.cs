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
using AutoFantic.Core.Monitoring;
using AutoFantic.Core.Reports;
using AutoFantic.Core.Updates;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Path = System.IO.Path;
using static AutoFantic.Core.Texts;

namespace AutoFantic.App;

/// <summary>
/// The AuFantic window: what the fans do right now (Overview), the history of temperatures,
/// power and fans with the user's warnings (Monitor), their curves to look at and to set (Fan curves),
/// the game sessions (Reports), whether the cooling got worse (Cooling health), calibrating and why the settings are what they are (Calibration), what
/// AuFantic did (Log), and the few settings plus developer tools. Before the first
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

    // what a fan cools, also under "Your fans": as the calibration measured it, or what the user says
    private static readonly (string Label, Component? Cools)[] CoolsChoices =
        [("as measured", null), ("CPU cooler", Component.Cpu), ("case fans", Component.Warmest), ("graphics card", Component.GpuCore)];

    // how many entries the Log page shows
    private const int LogShown = 300;

    // the monitor's time ranges; its charts refresh every few seconds while it's shown
    private static readonly (string Label, TimeSpan Span)[] Ranges =
    [
        ("10 min", TimeSpan.FromMinutes(10)), ("1 hour", TimeSpan.FromHours(1)), ("6 hours", TimeSpan.FromHours(6)),
        ("24 hours", TimeSpan.FromDays(1)), ("7 days", TimeSpan.FromDays(7)), ("30 days", TimeSpan.FromDays(30)),
        ("3 months", TimeSpan.FromDays(91)), ("1 year", TimeSpan.FromDays(365)), // hourly values, kept for 400 days
    ];
    private const int MonitorEverySeconds = 5;
    private const int MonitorPoints = 600; // per line across a chart

    // the palette's next slots after CPU (blue) and GPU (orange): red and violet were too close to orange and blue
    private static readonly Color HotspotColor = Color.FromRgb(0x1b, 0xaf, 0x7a);
    private static readonly Color MemoryColor = Color.FromRgb(0xed, 0xa1, 0x00);

    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(ProgressBar Bar, TextBlock Value)> _fanRows = [];
    private readonly List<ToggleButton> _tabs = [];
    private readonly List<(ToggleButton Button, Preset Preset)> _presets = [];
    private readonly Queue<string> _log = new();
    private IReadOnlyList<string> _groupsShown = [];
    private bool _wasSetUp;
    private TimeSpan _range = TimeSpan.FromHours(1);
    private IReadOnlyList<string> _compareChoices = []; // the series the Compare card has a button for

    // the cooling health chart's time ranges: per day up to a month, per week beyond
    private static readonly (string Label, TimeSpan Span)[] HealthRanges =
        [("30 days", TimeSpan.FromDays(30)), ("3 months", TimeSpan.FromDays(91)), ("1 year", TimeSpan.FromDays(365))];
    private TimeSpan _healthRange = TimeSpan.FromDays(91);
    private int _monitorTick;

    /// <summary>What the Updates card shows while checking or downloading; null otherwise.</summary>
    private string? _updating;
    private int _selected;
    private bool _building;
    private bool _installingPawnIo;

    // asked once per start, not every time the window opens
    private static bool _pawnIoOffered;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        UiTexts.Translate(this); // the XAML's own texts, before the code sets any
        SetResourceReference(ThemeTextProperty, "TextFillColorPrimaryBrush"); // notices a switch between light and dark

        NavOverview.Checked += (_, _) => ShowOnly(OverviewPage);
        NavMonitor.Checked += (_, _) =>
        {
            ShowOnly(MonitorPage);
            RefreshMonitor();
        };
        NavCurves.Checked += (_, _) => ShowOnly(CurvesPage);
        NavReports.Checked += (_, _) =>
        {
            ShowOnly(ReportsPage);
            BuildReports();
        };
        NavHealth.Checked += (_, _) =>
        {
            ShowOnly(HealthPage);
            BuildHealth();
        };
        HealthCalibrate.Click += (_, _) => ShowPage("calibration");
        NavCalibration.Checked += (_, _) => ShowOnly(CalibrationPage);
        NavLog.Checked += (_, _) => ShowOnly(LogPage);
        NavSettings.Checked += (_, _) =>
        {
            ShowOnly(SettingsPage);
            ShowSelfUsage();
        };

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

        // drawing a point down to 0 % means "off here": that switches stopping on
        Editor.CurveEdited += curve => _app.SetCurve(_selected, curve,
            AllowStop.IsChecked == true || (curve.Count > 0 && curve[0].Percent <= 0));
        AllowStop.Click += (_, _) =>
        {
            if (_app.Effective is { } effective)
                _app.SetCurve(_selected, effective.Groups[_selected].Curve, AllowStop.IsChecked == true);
        };
        ResetCurve.Click += (_, _) => _app.ResetCurve(_selected);
        BiosBox.Click += (_, _) => _app.SetBios(_selected, BiosBox.IsChecked == true);

        StartCalibrationButton.Click += (_, _) => StartCalibration();
        StopCalibrationButton.Click += (_, _) => _app.StopCalibration();
        AnalyseUseButton.Click += (_, _) => AnalyseUse();
        TakeOverUseButton.Click += (_, _) =>
        {
            if (_useSuggestion?.Analysis.Correction is { } use)
                _app.TakeOverUse(use); // the curves change, which also clears the suggestion (BuildUse)
        };
        DiscardUseButton.Click += (_, _) => ShowUse(null);
        DropUseButton.Click += (_, _) => _app.DropUse();
        RoomTemp.Text = (_app.Recommended?.Ambient ?? 22).ToString("0", CultureInfo.InvariantCulture);
        SetupFindFans.Click += (_, _) => FindFans();
        SetupRecheck.Click += (_, _) => Recheck();
        NoticeRecheck.Click += (_, _) => Recheck();
        CopyFallback.Click += (_, _) => System.Windows.Clipboard.SetText(FallbackText.Text);
        FanStoppedBox.Click += (_, _) => SaveWarnings();
        QuietAway.Click += (_, _) => SaveQuiet();
        QuietNight.Click += (_, _) => SaveQuiet();
        ReactEarlyBox.Click += (_, _) => _app.SaveControl(_app.Control with { ReactEarly = ReactEarlyBox.IsChecked == true });
        foreach (var box in new[] { QuietAwayMinutes, QuietFrom, QuietTo })
            box.LostFocus += (_, _) => SaveQuiet();
        AddWarning.Click += (_, _) =>
        {
            SaveWarnings([.. _app.Monitor.Warnings.Rules, new WarningRule(HistoryRecorder.CpuTemp.Key, Above: true, Limit: 85, Seconds: 10)]);
            BuildWarnings();
        };

        ShowAutostart();
        AutostartBox.Click += (_, _) =>
        {
            bool ok = AutostartBox.IsChecked == true ? Autostart.Enable() : Autostart.Disable();
            if (!ok)
                MessageBox.Show(this, T("Could not change the Windows start task."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowAutostart();
        };
        OpenPage.Click += (_, _) => OpenInExplorer(_app.PagePath);
        OpenFolder.Click += (_, _) => OpenInExplorer(_app.RunsPath);
        OpenLogFile.Click += (_, _) =>
        {
            if (_app.Log.FilePath is { } log && File.Exists(log))
                OpenInExplorer(log);
        };
        ChangeFolderButton.Click += (_, _) => ChangeFolder();
        VersionText.Text = T($"AuFantic {AppVersion.Text} · early development · MIT License");
        var folder = _app.DataUse;
        DataPath.Text = _app.RunsPath
            + (folder.Chosen ? T("  (chosen by you)") : File.Exists(Path.Combine(_app.RunsPath, DataFolder.MigratedNote)) ? T("  (copied from runs\\)") : "")
            + (folder.Unusable is not { } other ? ""
                : "\n" + (folder.CopyFailed
                    ? T($"⚠ The data couldn't be copied to {other} ({folder.Why}), so this folder stays in use.")
                    : T($"⚠ {other} can't be used right now ({folder.Why ?? T("the folder isn't there")}). AuFantic tries it again at its next start.")));
        ThemeBox.SelectedIndex = (int)_app.Appearance.Theme; // the items are in the order of Theme
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (ThemeBox.SelectedIndex >= 0)
                _app.SaveAppearance(_app.Appearance with { Theme = (Theme)ThemeBox.SelectedIndex });
        };
        LanguageBox.SelectedIndex = _app.Appearance.Language switch { "en" => 1, "de" => 2, _ => 0 };
        LanguageBox.SelectionChanged += (_, _) =>
        {
            string language = LanguageBox.SelectedIndex switch { 1 => "en", 2 => "de", _ => "" };
            if (language == _app.Appearance.Language)
                return;
            _app.SaveAppearance(_app.Appearance with { Language = language });
            if (MessageBox.Show(this, Texts.T("AuFantic starts again to switch the language. The BIOS has the fans for those few seconds."),
                    "AuFantic", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return; // then at the next start
            _app.StartAgain("--open");
            System.Windows.Application.Current.Shutdown();
        };
        UpdateDailyBox.IsChecked = _app.Updates.CheckDaily;
        UpdateDailyBox.Click += (_, _) => _app.SaveUpdateSettings(new UpdateSettings(UpdateDailyBox.IsChecked == true));
        UpdateCheckButton.Click += async (_, _) =>
        {
            _updating = T("Checking …");
            ShowUpdates();
            await _app.CheckForUpdateAsync();
            _updating = null;
            ShowUpdates();
        };
        UpdateInstall.Click += async (_, _) => await InstallUpdate();
        UpdateNotes.Click += (_, _) =>
        {
            if (_app.UpdateAvailable is { PageUrl.Length: > 0 } release)
                OpenInExplorer(release.PageUrl);
        };
        FindFansButton.Click += (_, _) => FindFans();
        SensorsButton.Click += (_, _) => ShowSensors();
        ConsoleButton.Click += (_, _) => OpenConsole();

        _app.CurvesChanged += OnCurvesChanged;
        _app.CalibrationProgress += OnCalibrationProgress;
        _app.CalibrationLog += OnCalibrationLog;
        _app.CalibrationEnded += OnCalibrationEnded;
        _app.Log.Added += OnLogAdded;
        _app.HealthUpdated += OnHealthUpdated;
        _app.UpdateStateChanged += OnUpdateStateChanged;
        Closed += (_, _) =>
        {
            _app.HealthUpdated -= OnHealthUpdated;
            _app.UpdateStateChanged -= OnUpdateStateChanged;
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
        BuildRanges();
        BuildWarnings();
        BuildQuiet();
        UpdateCalibrationState();
        ShowUpdates();

        _timer.Tick += (_, _) =>
        {
            UpdateLive();
            if (SettingsPage.Visibility == Visibility.Visible && _monitorTick % 2 == 0)
                ShowSelfUsage();
            if (++_monitorTick % MonitorEverySeconds == 0)
            {
                if (MonitorPage.Visibility == Visibility.Visible)
                    RefreshMonitor();
                if (ReportsPage.Visibility == Visibility.Visible)
                    UpdateReportsNow();
            }
        };
        _timer.Start();
        UpdateLive();
        ContentRendered += (_, _) => OfferPawnIo();
    }

    /// <summary>"overview", "monitor", "curves", "reports", "health", "calibration", "log" or "settings".</summary>
    public void ShowPage(string page) =>
        (page switch
        {
            "curves" when _app.IsSetUp => NavCurves,
            "monitor" => NavMonitor,
            "reports" => NavReports,
            "health" when _app.IsSetUp => NavHealth,
            "calibration" or "setup" => NavCalibration,
            "log" or "activity" => NavLog,
            "settings" => NavSettings,
            _ => NavOverview,
        }).IsChecked = true;

    /// <summary>The page shown now (for screenshots).</summary>
    internal FrameworkElement VisiblePage => Pages.First(p => p.Visibility == Visibility.Visible);

    private FrameworkElement[] Pages => [OverviewPage, MonitorPage, CurvesPage, ReportsPage, HealthPage, CalibrationPage, LogPage, SettingsPage];

    private void ShowOnly(FrameworkElement page)
    {
        foreach (var p in Pages)
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What depends on whether AuFantic is set up and on which fan groups there are: the
    /// navigation, the set-up cards, the fan rows and the curve tabs. Rebuilt only when that changes
    /// (the fans were found, the first calibration finished, the fans changed).
    /// </summary>
    private void UpdateStructure()
    {
        bool setUp = _app.IsSetUp;
        NavCurves.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        NavHealth.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        QuietPanel.IsEnabled = setUp;
        EarlyPanel.IsEnabled = setUp;
        NavCalibration.Content = setUp ? T("Calibration") : T("Set up");
        CalibrationHeading.Text = setUp ? T("Calibration") : T("Set up AuFantic");
        SetupCards.Visibility = setUp ? Visibility.Collapsed : Visibility.Visible;
        InsightCards.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
        CalibrateTitle.Text = setUp ? T("Calibrate") : T("Step 2 · Calibrate");
        BuildFoundFans();

        if (setUp && !_wasSetUp && NavCurves.IsChecked == false && NavCalibration.IsChecked == true && IsLoaded)
            ShowPage("overview"); // just calibrated for the first time: now there's something to see
        if (!setUp && (NavCurves.IsChecked == true || NavHealth.IsChecked == true))
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
        var names = _app.GroupNames.ToList();
        if (names.Count == 0)
        {
            FanRows.Children.Add(new TextBlock { Text = T("No fans found yet."),Style = (Style)FindResource("Caption") });
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
                Text = _app.Recommended is { } r ? CalibrationReport.Role(r.Groups[g]) : T("BIOS until the first calibration"),
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
            content.Children.Add(new TextBlock { Text = T(preset.Name), FontSize = 15, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = T(preset.Description),Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) });
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
            ? (Cooling, T("Calibrating"), T("The calibration drives the fans right now."))
            : status.State switch
            {
                LoopState.NotSetUp => (SetUp, T("Not set up yet"), T("The BIOS controls your fans until the first calibration.")),
                LoopState.Paused => (Paused, _app.Sleeping ? T("Paused for sleep") : T("Paused"), T("The BIOS controls the fans.")),
                LoopState.CoolingDown => (Cooling, T("Cooling down"), T("A temperature limit was reached: all fans at 100 % until it's cool.")),
                LoopState.SensorProblem => (Problem, T("Sensor problem"), T("A sensor stopped reporting: the BIOS has the fans, retrying every minute.")),
                _ when _app.QuietReason == "away" => (Running, T("Extra quiet: you're away"), T("Every fan at its slowest, the ones that can stop are off. Normal again when you're back.")),
                _ when _app.QuietReason == "night" => (Running, T("Extra quiet for the night"), T($"Every fan at its slowest, the ones that can stop are off, until {_app.Quiet.NightTo}.")),
                _ => (Running, T("AuFantic controls your fans"), T("They follow your curves. The safety limits always stay on.")),
            };
        StateDot.Fill = brush;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        PauseButton.Content = !_app.IsSetUp ? T("Set up") : _app.UserPaused ? T("Resume") : T("Pause");
        PauseButton.IsEnabled = !_app.Calibrating;

        CpuTemp.Text = Temp(status.CpuTemp);
        GpuTemp.Text = Temp(status.GpuTemp);
        CpuPower.Text = $"{status.CpuPower:0} W";
        GpuPower.Text = $"{status.GpuPower:0} W";
        SideStatus.Text = $"{T(_app.Effective?.Profile ?? "not set up")} · {status.Time:HH:mm:ss}";

        for (int i = 0; i < _fanRows.Count; i++)
        {
            var reading = _app.Calibrating || i >= status.Fans.Count ? null : status.Fans[i];
            _fanRows[i].Bar.Value = reading?.Percent ?? reading?.BiosPercent ?? 0;
            _fanRows[i].Value.Text = _app.Calibrating ? T("test") : Speed(reading);
        }

        // the ring sits at the smoothed temperature the fan control reads the curve at
        if (_app.Effective is not { } effective || effective.Groups.Count == 0)
            return;
        var group = effective.Groups[Math.Min(_selected, effective.Groups.Count - 1)];
        var fan = _selected < status.Fans.Count ? status.Fans[_selected] : null;
        double? followed = fan?.Status?.Temperature ?? CalibrationInsights.Followed(group.Follows, status.CpuTemp, status.GpuTemp);
        Editor.Live = followed is { } t && fan is not null && !_app.Calibrating
            ? fan.Bios ? (t, fan.BiosPercent, T("the BIOS runs it")) : (t, fan.Percent, Note(fan.Status))
            : null;
        Editor.Refresh();
    }

    private static string? Note(FanStatus? status) => status switch
    {
        { Note: FanNote.SlowingDown, CurvePercent: { } c } => T($"slowing down gently to {Math.Max(0, c):0} %"),
        { Note: FanNote.SpeedingUp, CurvePercent: { } c } => T($"speeding up to {c:0} %"),
        { Note: FanNote.Slowest } => T("the slowest speed this fan turns at"),
        { Note: FanNote.Off } => T("idle and cool"),
        { Note: FanNote.Starting } => T("starting up (short push)"),
        { Note: FanNote.Quiet } => T("extra quiet"),
        { Note: FanNote.Early } => T("ahead of the temperature: the graphics card's power jumped"),
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
            ? T("Calibrate once with this version to switch presets.")
            : T("Available after the first calibration.");

        var profile = current.Profile;
        LimitValue.Text = _app.IsSetUp ? T(current.Name) : "–";
        LimitDetail.Text = !_app.IsSetUp ? T("after the first calibration")
            : profile.MaxCooling ? T("every fan up to its knee")
            : $"CPU ≤ {profile.Cpu:0} °C · GPU ≤ {profile.GpuCore:0} °C";
    }

    private void SwitchPreset(Preset preset)
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        bool ok = _app.SwitchPreset(preset);
        Cursor = null;
        if (!ok)
            MessageBox.Show(this, T("Not enough measurements to switch yet."),"AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
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

        bool custom = _app.IsCustom(_selected), bios = effective.Bios;
        CurveTitle.Text = effective.Name;
        CurveSubtitle.Text = bios
            ? T($"Controlled by the BIOS · {CalibrationReport.Role(effective)} · AuFantic's {(custom ? T("curve (yours)") : T("curve"))} is kept for later")
            : T($"Follows the {CalibrationInsights.FollowsName(effective.Follows)} · {CalibrationReport.Role(effective)} · {(custom ? T("your own curve") : T("the recommended curve"))}");
        BiosBox.IsChecked = bios;
        BiosHint.Visibility = bios ? Visibility.Visible : Visibility.Collapsed;
        Editor.IsEnabled = !bios;
        Editor.Opacity = bios ? 0.45 : 1;
        CurveHint.Visibility = bios ? Visibility.Collapsed : Visibility.Visible;
        LegendUse.Fill = SeriesBrush(color);
        LegendRecommended.Stroke = SeriesBrush(color);
        LegendRecommended.Opacity = 0x90 / 255.0;

        bool canStop = _app.CanStop(_selected), allows = _app.AllowsStop(_selected);
        double offAt = CurveController.OffTemperature(effective);
        string part = CalibrationInsights.Name(effective.Follows);
        // always the user's choice (only not while the BIOS has these fans); what was measured at 0 % is only said
        AllowStop.IsEnabled = !bios;
        AllowStop.IsChecked = allows;
        StopHint.Text = (allows
            ? T($"Off at idle up to {part} {offAt:0} °C, on again above {offAt + CurveController.Hysteresis:0} °C or under load.")
              + (_app.RecommendsStop(_selected) ? "" : T(" (Your choice: the calibration keeps them on.)"))
            : T("Or drag a point down to 0 %.") + (_app.RecommendsStop(_selected) ? T(" Recommended.") : ""))
            + (canStop ? ""
                : _app.StopMeasured(_selected) ? T(" In the test these fans kept turning at 0 %: they may only get slower, not stop.")
                : T(" Not tested yet whether these fans stand still at 0 %: Calibration → Find my fans again tests it."));
        Editor.OffBelow = allows ? offAt : null;
        ResetCurve.IsEnabled = custom && !bios;

        // the same curve for when AuFantic isn't running: 4 points for a BIOS, the whole curve for Afterburner
        bool gpu = effective.Follows == Component.GpuCore;
        var points = gpu ? effective.Curve : CalibrationResult.BiosPoints(effective.Curve);
        FallbackText.Text = (gpu ? T("Afterburner curve: ") : T("BIOS curve: "))
            + string.Join("  ·  ", points.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %"));
        FallbackText.ToolTip = gpu
            ? T("Without AuFantic: MSI Afterburner's custom fan curve for the graphics card (a BIOS can't control it).")
            : T("Without AuFantic: the mainboard's own fan curve (e.g. MSI Smart Fan). A BIOS can only follow the CPU temperature.");
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
            MessageBox.Show(this, T("Please enter the room temperature in °C, e.g. 22."),"AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _log.Clear();
        CalibrationChart.Clear();
        _app.StartCalibration(room, LoadBuiltIn.IsChecked == true);
        CalibrationStatus.Text = T("Starting …");
        UpdateCalibrationState();
    }

    private void FindFans()
    {
        var answer = MessageBox.Show(this, T("About 2 minutes, you'll hear the fans change. Start?"),
            T("Find my fans"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        _log.Clear();
        CalibrationChart.Clear();
        _app.StartFindFans();
        CalibrationStatus.Text = T("Finding fans …");
        UpdateCalibrationState();
        ShowPage("calibration");
    }

    private void UpdateCalibrationState()
    {
        bool running = _app.Calibrating;
        // the first calibration waits for step 1, so the user sees what was found first
        StartCalibrationButton.IsEnabled = !running && (_app.IsSetUp || _app.Inventory is not null);
        StartCalibrationButton.ToolTip = StartCalibrationButton.IsEnabled || running ? null : T("Find your fans first.");
        StopCalibrationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrateSetup.IsEnabled = !running;
        CalibrationLive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrationLogText.Visibility = _log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindFansButton.IsEnabled = !running;
        TogetherRows.IsEnabled = !running;
        AnalyseUseButton.IsEnabled = !running;
        SetupFindFans.IsEnabled = !running;
        foreach (var (button, _) in _presets)
            button.IsEnabled = _app.CanSwitchPreset && !running;
    }

    /// <summary>Step 1's result before the first calibration: the fan groups found, each with how fast it turns and whether it can stop.</summary>
    private void BuildFoundFans()
    {
        FoundFans.Children.Clear();
        var groups = _app.Inventory?.Groups() ?? [];
        FindFansTitle.Text = groups.Count > 0 ? T("Step 1 · Find my fans  ✓") : T("Step 1 · Find my fans");
        SetupFindFans.Content = groups.Count > 0 ? T("Find my fans again") : T("Find my fans");
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
                Text = T($"up to {top:0} rpm · {(group.CanStop ? T("can stop at 0 %") : group.StopMeasured ? T("keeps turning at 0 %") : T("0 % not tested yet"))}"),
                Style = (Style)FindResource("Caption"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            FoundFans.Children.Add(row);
        }
        if (groups.Count > 0)
            FoundFans.Children.Add(new TextBlock
            {
                Text = T("Several fans on one output, or a loud one? You can say so after calibrating."),
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
        SetupChecks.Children.Clear();
        foreach (var check in _app.Checks)
            SetupChecks.Children.Add(CheckRow(check));

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
        if (check.Fix != CheckFix.None)
        {
            bool pawnIo = check.Fix == CheckFix.GetPawnIo;
            var fix = new Button
            {
                Content = !pawnIo ? T("Restart AuFantic") : _installingPawnIo ? T("Installing PawnIO …") : T("Install PawnIO"),
                IsEnabled = !(pawnIo && _installingPawnIo),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            fix.Click += (_, _) =>
            {
                if (pawnIo)
                {
                    InstallPawnIo();
                    return;
                }
                _app.StartAgain("--open");
                System.Windows.Application.Current.Shutdown();
            };
            text.Children.Add(fix);
        }
        Place(row, text, 1);
        return row;
    }

    /// <summary>
    /// The first time the window shows while the PawnIO driver is missing: one question, so nobody
    /// has to find the driver's installer. It's a driver, so it's never installed without a yes.
    /// </summary>
    private void OfferPawnIo()
    {
        if (_pawnIoOffered || !_app.Checks.Any(c => c.Fix == CheckFix.GetPawnIo))
            return;
        _pawnIoOffered = true;
        if (MessageBox.Show(this, T("AuFantic needs the PawnIO driver to reach the mainboard's fans and to read the CPU temperature. It isn't installed on this PC.\n\nInstall it now? AuFantic downloads it from PawnIO's page on GitHub (3 MB), installs it and starts again."),
                "AuFantic", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            InstallPawnIo();
    }

    /// <summary>Installs the PawnIO driver (the user clicked or said yes) and starts AuFantic again to use it.</summary>
    private async void InstallPawnIo()
    {
        if (_installingPawnIo)
            return;
        _installingPawnIo = true;
        BuildChecks(); // the button says "Installing …"
        try
        {
            if (!await _app.InstallPawnIoAsync())
            {
                _app.StartAgain("--open");
                System.Windows.Application.Current.Shutdown();
                return;
            }
            MessageBox.Show(this, T("The PawnIO driver is installed. Windows wants a restart of the PC to finish it: restart the PC, then start AuFantic again."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (MessageBox.Show(this, T($"The PawnIO driver couldn't be installed: {ex.Message}\n\nOpen its download page to install it yourself?"), "AuFantic", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenInExplorer(SystemCheck.PawnIoUrl);
        }
        _installingPawnIo = false;
        Recheck();
    }

    private void OnCalibrationProgress(CalibrationProgress p) => Dispatcher.BeginInvoke(() =>
    {
        CalibrationStatus.Text = p.Message;
        CalibrationBar.Value = p.Done;
        if (p.Speeds is { } speeds && _app.Inventory is { } inventory)
        {
            var groups = inventory.Groups();
            CalibrationSpeeds.Text = T("Fans now: ") + string.Join(" · ", groups.Select((g, i) => i < speeds.Count ? $"{g.Name} {speeds[i]:0} %" : g.Name));
        }
        else
        {
            CalibrationSpeeds.Text = p.Stage switch
            {
                CalibrationStage.FansOff => T("Every fan stands still for up to 2½ minutes."),
                CalibrationStage.WaitingForLoad => T("The fans are on BIOS control until the load is there."),
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
        MessageBox.Show(this, outcome.Message + (outcome.Success && outcome.Result is not null ? T("\n\nThe new settings are in use.") : ""),
            "AuFantic", MessageBoxButton.OK, outcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    });

    // ── improve from everyday use ──────────────────────────────────────────────────────

    /// <summary>What the last "Analyse my use" suggested and hasn't been taken over or discarded yet.</summary>
    private AppController.UseSuggestion? _useSuggestion;

    /// <summary>Reads the history in the background (a month of minutes) and shows what it suggests.</summary>
    private async void AnalyseUse()
    {
        AnalyseUseButton.IsEnabled = false;
        ShowUse(null);
        UseSummary.Text = T("Reading the history …");
        UseResult.Visibility = Visibility.Visible;
        try
        {
            ShowUse(await Task.Run(_app.AnalyseUse));
        }
        catch (Exception ex)
        {
            UseSummary.Text = T($"The history couldn't be analysed: {ex.Message}");
        }
        finally
        {
            AnalyseUseButton.IsEnabled = !_app.Calibrating;
        }
    }

    /// <summary>Whether a correction from everyday use is counted in; a suggestion made for other curves is gone.</summary>
    private void BuildUse()
    {
        ShowUse(null);
        var use = _app.UseInUse;
        UseInUseText.Visibility = DropUseButton.Visibility = use is null ? Visibility.Collapsed : Visibility.Visible;
        if (use is not null)
            UseInUseText.Text = T($"In use: what {use.Minutes} minutes on {use.Days} days showed up to {use.Found.ToLocalTime():dd.MM. HH:mm}. It counts until the next calibration.");
    }

    /// <summary>The findings in plain words and, if the curves would change, each fan's curve now and as suggested; null clears it.</summary>
    private void ShowUse(AppController.UseSuggestion? suggestion)
    {
        _useSuggestion = suggestion;
        UseFindings.Children.Clear();
        UseCharts.Children.Clear();
        UseCurves.Visibility = Visibility.Collapsed;
        UseResult.Visibility = suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        if (suggestion is null)
            return;

        var analysis = suggestion.Analysis;
        string since = analysis.From.ToLocalTime().ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
        if (analysis.Correction is not { } use || suggestion.Curves is null)
        {
            UseSummary.Text = T($"Not enough yet: {analysis.Minutes} of the {UseLearning.MinMinutes} minutes it takes.");
            Finding(T($"Only minutes since the last calibration ({since}) count in which every fan turned and the load had been the same for a few minutes. Use the PC as usual and try again in a few days."));
            return;
        }

        bool same = UseLearning.Same(suggestion.Changes);
        UseSummary.Text = same
            ? T("Your curves already fit how the PC really runs. Nothing to improve.")
            : T("Everyday use suggests other curves.");
        Finding(T($"From {use.Minutes} minutes on {use.Days} days since the calibration ({since}):"));
        Finding(Part("CPU", use.Cpu));
        Finding(Part("GPU", use.Gpu));
        var (topCpu, topGpu) = (_app.Store.Calibrations.Max(c => c.TopCpu), _app.Store.Calibrations.Max(c => c.TopGpu));
        Finding(use.TopCpu > topCpu || use.TopGpu > topGpu
            ? T($"Loads: the PC ran at up to CPU {use.TopCpu:0} W and GPU {use.TopGpu:0} W, more than any calibration saw (CPU {topCpu:0} W, GPU {topGpu:0} W). The new curves cover that.")
            : T($"Loads: nothing heavier than the calibrations saw (CPU {topCpu:0} W, GPU {topGpu:0} W)."));
        if (same)
            return;

        for (int g = 0; g < suggestion.Changes.Count; g++)
            UseCharts.Children.Add(UseChart(g, suggestion.Changes[g]));
        UseCurves.Visibility = Visibility.Visible;

        void Finding(string text) =>
            UseFindings.Children.Add(new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        // "CPU: 10 °C warmer at 33 W, 6 °C warmer at 78 W than the calibration expects."
        static string Part(string name, PartUse? part)
        {
            if (part is null)
                return T($"{name}: runs as the calibrations expect.");
            static string Warmer(double v) => v >= 0 ? T($"{v:0} °C warmer") : T($"{-v:0} °C cooler");
            return T($"{name}: {Warmer(part.Extra(part.From))} at {part.From:0} W, {Warmer(part.Extra(part.To))} at {part.To:0} W than the calibrations expect.");
        }
    }

    /// <summary>One fan group's curve as suggested (solid) over the one in use now (dashed), with what changes in words.</summary>
    private FrameworkElement UseChart(int group, CurveChange change)
    {
        string what = Math.Abs(change.Largest) < UseLearning.Unchanged ? T("unchanged")
            : change.Largest > 0 ? T($"up to {change.Largest:0} % faster (at {change.At:0} °C)")
            : T($"up to {-change.Largest:0} % slower (at {change.At:0} °C)");
        if (change.OffAtIdleNow != change.OffAtIdleNew)
            what += " · " + (change.OffAtIdleNew ? T("off at idle") : T("no longer off at idle"));
        if (_app.IsBios(group))
            what += " · " + T("controlled by the BIOS");
        else if (_app.IsCustom(group))
            what += " · " + T("your own curve stays in use");

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Dot(group));
        title.Children.Add(new TextBlock { Text = change.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });

        var chart = new CurveEditor
        {
            Height = 190,
            Margin = new Thickness(0, 6, 0, 0),
            Color = Palette[group % Palette.Length],
            TemperatureLabel = Capitalize(CalibrationInsights.FollowsName(change.Follows)),
            IsHitTestVisible = false, // to look at, not to drag
        };
        chart.SetCurves(change.New, change.Now);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 18) };
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = what, Style = (Style)FindResource("Caption"), Margin = new Thickness(18, 2, 0, 0) });
        panel.Children.Add(chart);
        return panel;
    }

    /// <summary>"Improve from everyday use", "Why these settings", "Your fans", "What each fan cools" and "Runs".</summary>
    private void BuildInsights()
    {
        _building = true;
        try
        {
            BuildUse();
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
        WhyIntro.Text = T($"{T(preset.Name)}: what limits the cooling at each load, and why the fans run as they do.");
        foreach (var level in _app.Levels())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            head.Children.Add(new TextBlock { Text = Capitalize(T(level.Label)),FontSize = 14, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new TextBlock { Text = $"CPU {level.CpuPower:0} W · GPU {level.GpuPower:0} W", Style = (Style)FindResource("Caption") });
            head.Children.Add(Badge($"{CalibrationInsights.Name(level.Bottleneck)} {level.Temperature:0} / {level.Allowed:0} °C", level.Temperature > level.Allowed));

            row.Children.Add(head);
            Place(row, new TextBlock { Text = level.Why, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }, 1);
            WhyRows.Children.Add(row);
        }
        if (WhyRows.Children.Count == 0)
            WhyRows.Children.Add(new TextBlock { Text = T("Calibrate once with this version to see this."),Style = (Style)FindResource("Caption") });
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var count = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 10, 0) };
            for (int n = 1; n <= 6; n++)
                count.Items.Add(n == 1 ? T("1 fan") : T($"{n} fans"));
            count.SelectedIndex = Math.Clamp(group.Headers.Sum(h => h.FanCount), 1, 6) - 1;

            var loudness = new ComboBox { Width = 110 };
            foreach (var (label, _) in LoudnessChoices)
                loudness.Items.Add(T(label));
            double db = group.Headers[0].LoudnessDb;
            loudness.SelectedIndex = Array.FindIndex(LoudnessChoices, c => Math.Abs(c.Db - db) < 0.1) is >= 0 and var i ? i : 1;

            var cools = new ComboBox { Width = 150, Margin = new Thickness(10, 0, 0, 0), ToolTip = T("What these fans cool. Their curve follows that temperature.") };
            foreach (var (label, _) in CoolsChoices)
                cools.Items.Add(T(label));
            cools.SelectedIndex = Math.Max(0, Array.FindIndex(CoolsChoices, c => c.Cools == group.Cools));

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
            cools.SelectionChanged += (_, _) =>
            {
                if (_building)
                    return;
                Cursor = System.Windows.Input.Cursors.Wait;
                _app.SetCools(index, CoolsChoices[Math.Max(0, cools.SelectedIndex)].Cools);
                Cursor = null;
            };

            row.Children.Add(Dot(g));
            Place(row, new TextBlock { Text = group.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1);
            Place(row, count, 2);
            Place(row, loudness, 3);
            Place(row, cools, 4);
            LoudnessRows.Children.Add(row);
        }
        BuildTogether(groups);
    }

    /// <summary>
    /// Under "Your fans": mainboard fans on different outputs that belong together (the two fans of
    /// one CPU cooler) can run as one group with one curve, like a graphics card's fans, and be taken apart again.
    /// </summary>
    private void BuildTogether(IReadOnlyList<FanGroup> groups)
    {
        TogetherRows.Children.Clear();
        TogetherRows.IsEnabled = !_app.Calibrating;
        var mainboard = groups.Select((group, index) => (Group: group, Index: index)).Where(x => !x.Group.IsGpu).ToList();
        var together = mainboard.Where(x => x.Group.Headers.Count > 1).ToList();
        if (mainboard.Count < 2 && together.Count == 0)
            return;

        TogetherRows.Children.Add(new TextBlock
        {
            Text = T("Fans on two outputs that belong together, like the two fans of one CPU cooler? Let them run together: one speed and one curve, like the fans of a graphics card."),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 8, 0, 10),
        });

        foreach (var (group, index) in together)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Dot(index));
            row.Children.Add(new TextBlock { Text = T($"{group.Name}: these run together."), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var apart = new Button { Content = T("Take apart"), Padding = new Thickness(14, 6, 14, 6) };
            apart.Click += (_, _) =>
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                _app.RunApart(index);
                Cursor = null;
            };
            row.Children.Add(apart);
            TogetherRows.Children.Add(row);
        }

        if (mainboard.Count < 2)
            return;
        var pick = new StackPanel { Orientation = Orientation.Horizontal };
        var first = new ComboBox { Width = 210 };
        var second = new ComboBox { Width = 210 };
        foreach (var (group, _) in mainboard)
        {
            first.Items.Add(group.Name);
            second.Items.Add(group.Name);
        }
        first.SelectedIndex = 0;
        second.SelectedIndex = 1;
        var join = new Button { Content = T("Run together"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(10, 0, 0, 0) };
        void Chosen() => join.IsEnabled = first.SelectedIndex >= 0 && second.SelectedIndex >= 0 && first.SelectedIndex != second.SelectedIndex;
        first.SelectionChanged += (_, _) => Chosen();
        second.SelectionChanged += (_, _) => Chosen();
        join.Click += (_, _) =>
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            _app.RunTogether(mainboard[first.SelectedIndex].Index, mainboard[second.SelectedIndex].Index);
            Cursor = null;
        };
        pick.Children.Add(first);
        pick.Children.Add(new TextBlock { Text = T("and"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
        pick.Children.Add(second);
        pick.Children.Add(join);
        TogetherRows.Children.Add(pick);
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
            name.Children.Add(new TextBlock
            {
                Text = T($"{CalibrationReport.Role(group)} · follows the {CalibrationInsights.FollowsName(group.Follows)}") + (_app.CoolsChosen(g) ? T(" (your choice)") : ""),
                Style = (Style)FindResource("Caption"),
            });
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
        track.Children.Add(new Border { Background = SeriesBrush(color), CornerRadius = new CornerRadius(4) });
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
            RunRows.Children.Add(new TextBlock { Text = T("No runs stored yet."),Style = (Style)FindResource("Caption") });
            return;
        }

        var names = _app.Inventory!.Groups().Select(g => g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}").ToList();
        RunRows.Children.Add(RunRow(true, T("When"), T("Load"), string.Join(" · ", names) + " (%)", "CPU / GPU W", "CPU / GPU °C", T("Bottleneck"), false));
        string? lastSource = null;
        foreach (var run in runs)
        {
            string source = run.Source == lastSource ? "" : LoadName(run.Source) + (run.Imported ? T(" (imported)") : "");
            lastSource = run.Source;
            string bottleneck = $"{CalibrationInsights.Name(run.Bottleneck)} {run.Final.GetValueOrDefault(run.Bottleneck):0} °C, "
                + (run.Headroom >= 0 ? T($"{run.Headroom:0} below limit") : T($"{-run.Headroom:0} above limit"));
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

    // ── monitor ────────────────────────────────────────────────────────────────────────

    private void BuildRanges()
    {
        RangeChoices(RangeButtons, Ranges, _range, span =>
        {
            _range = span;
            RefreshMonitor();
        });
        RangeChoices(HealthRangeButtons, HealthRanges, _healthRange, span =>
        {
            _healthRange = span;
            BuildHealth();
        });
    }

    /// <summary>A row of time range buttons: the one in use is checked; a click checks another and calls <paramref name="chosen"/>.</summary>
    private void RangeChoices(System.Windows.Controls.Panel panel, (string Label, TimeSpan Span)[] ranges, TimeSpan selected, Action<TimeSpan> chosen)
    {
        var buttons = new List<(ToggleButton Button, TimeSpan Span)>();
        foreach (var (label, span) in ranges)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("Choice"),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new TextBlock { Text = T(label), FontSize = 14 },
                IsChecked = span == selected,
            };
            var mine = span;
            button.Click += (_, _) =>
            {
                foreach (var (b, s) in buttons)
                    b.IsChecked = s == mine;
                chosen(mine);
            };
            buttons.Add((button, span));
            panel.Children.Add(button);
        }
    }

    /// <summary>The charts and the values for the chosen time range, from the history.</summary>
    private void RefreshMonitor()
    {
        var limits = new Core.Analysis.SafetyLimits();
        var preset = _app.Preset;
        LimitsNote.Text = T($"Limits: CPU {limits.CpuMax:0} °C · GPU {limits.GpuCoreMax:0} °C · hotspot and memory {limits.GpuHotspotMax:0} °C. Above them every fan runs at 100 %.")
            + Environment.NewLine + T($"{T(preset.Name)} keeps the GPU at up to {preset.Profile.GpuCore:0} °C and the hotspot at up to {preset.Profile.GpuHotspot:0} °C.");
        var store = _app.Monitor.Store;
        var to = _app.Loop.Last?.Time ?? DateTimeOffset.Now;
        var from = to - _range;
        var series = store.AllSeries();
        // one axis for all four charts, without the time the PC was off, so the same moment is at the same place in each
        Dictionary<string, IReadOnlyList<HistoryPoint>> Read(int points) => series.ToDictionary(x => x.Key, x => store.Query(x.Key, from, to, points));
        RunningAxis Axis(Dictionary<string, IReadOnlyList<HistoryPoint>> read) => RunningAxis.Of(read.Values.SelectMany(points => points).Select(p => p.Time), from, to);
        var data = Read(MonitorPoints);
        var axis = Axis(data);
        double shown = axis.Seconds / _range.TotalSeconds;
        if (shown < 0.5)
        {
            // the PC was off most of the time: the same width shows fewer hours, so ask for finer points
            data = Read((int)Math.Min(MonitorPoints / shown, 100 * MonitorPoints));
            axis = Axis(data);
        }

        ChartLine? Line(Series x, Color color) =>
            data.TryGetValue(x.Key, out var points) && points.Count > 0 ? new ChartLine(x.Kind is SeriesKind.FanPercent or SeriesKind.FanRpm ? x.Name : T(x.Name), color, points) : null;
        IReadOnlyList<ChartLine> Lines(params ChartLine?[] lines) => [.. lines.OfType<ChartLine>()];

        TempChart.Show(Lines(Line(HistoryRecorder.CpuTemp, LiveChart.CpuColor), Line(HistoryRecorder.GpuTemp, LiveChart.GpuColor),
            Line(HistoryRecorder.GpuHotspot, HotspotColor), Line(HistoryRecorder.GpuMemory, MemoryColor)), from, to, "°C", axis: axis);
        PowerChart.Show(Lines(Line(HistoryRecorder.CpuPower, LiveChart.CpuColor), Line(HistoryRecorder.GpuPower, LiveChart.GpuColor)), from, to, "W", min: 0, axis: axis);

        // fans in the colours they have everywhere else; fans that are gone keep theirs after them
        var names = _app.GroupNames.ToList();
        Color FanColor(Series x, int order) => Palette[(names.IndexOf(x.Name) is >= 0 and var i ? i : names.Count + order) % Palette.Length];
        var percent = series.Where(x => x.Kind == SeriesKind.FanPercent).ToList();
        var rpm = series.Where(x => x.Kind == SeriesKind.FanRpm).ToList();
        FanChart.Show(Lines([.. percent.Select((x, i) => Line(x, FanColor(x, i)))]), from, to, "%", min: 0, max: 100, axis: axis);
        RpmChart.Show(Lines([.. rpm.Select((x, i) => Line(x, FanColor(x, i)))]), from, to, T("rpm"), min: 0, axis: axis);

        BuildValues(series, data);
        ShowCompare(series, data, from, to, axis);
    }

    // in the Compare chart every unit has its own scale; power and the fans start at 0, as in their own charts
    private static ChartScale ScaleOf(Series x) => x.Kind switch
    {
        SeriesKind.Temperature => new ChartScale(x.Unit),
        SeriesKind.Power or SeriesKind.FanRpm => new ChartScale(x.Unit, Min: 0),
        _ => new ChartScale(x.Unit, Min: 0, Max: 100),
    };

    /// <summary>
    /// The user's own chart: a button per series, and the chosen ones together in one chart, in
    /// the order they were picked. The buttons are built again only when the series change.
    /// </summary>
    private void ShowCompare(IReadOnlyList<Series> series, Dictionary<string, IReadOnlyList<HistoryPoint>> data, DateTimeOffset from, DateTimeOffset to, RunningAxis axis)
    {
        var known = series.OrderBy(x => x.Kind).ToList();
        if (!known.Select(x => x.Key).SequenceEqual(_compareChoices))
        {
            _compareChoices = [.. known.Select(x => x.Key)];
            CompareChoices.Children.Clear();
            foreach (var x in known)
            {
                var button = new ToggleButton
                {
                    Style = (Style)FindResource("Choice"),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Content = new TextBlock { Text = Describe(x), FontSize = 13 },
                    IsChecked = _app.Compare.Series.Contains(x.Key),
                };
                string key = x.Key;
                button.Click += (_, _) =>
                {
                    var others = _app.Compare.Series.Where(k => k != key);
                    _app.SaveCompare(new CompareSettings(button.IsChecked == true ? [.. others, key] : [.. others]));
                    RefreshMonitor();
                };
                CompareChoices.Children.Add(button);
            }
        }

        var lines = _app.Compare.Series
            .Select(key => known.FirstOrDefault(x => x.Key == key))
            .OfType<Series>()
            .Select((x, i) => new ChartLine(Describe(x), Palette[i % Palette.Length], data.GetValueOrDefault(x.Key) ?? [], ScaleOf(x)))
            .ToList();
        CompareChart.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompareChart.Show(lines, from, to, "", axis: axis);
    }

    /// <summary>Now, lowest, average and highest of every series in the range, a little like HWiNFO.</summary>
    private void BuildValues(IReadOnlyList<Series> series, Dictionary<string, IReadOnlyList<HistoryPoint>> data)
    {
        ValueRows.Children.Clear();
        ValueRows.RowDefinitions.Clear();
        ValueRows.ColumnDefinitions.Clear();
        foreach (double width in new[] { 2.4, 1, 1, 1, 1 })
            ValueRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        string range = Ranges.First(r => r.Span == _range).Label;
        ValuesIntro.Text = T($"The last {T(range.StartsWith("1 ", StringComparison.Ordinal) ? range[2..] : range)}. Hover over a chart to see its values at that time. Click a name above a chart to hide its line.");

        void Row(int row, string[] cells, bool header)
        {
            ValueRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cells.Length; c++)
            {
                var text = new TextBlock { Text = cells[c], FontSize = 13, Margin = new Thickness(0, 0, 10, 6), HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                if (header)
                    text.Style = (Style)FindResource("Caption");
                Grid.SetRow(text, row);
                Place(ValueRows, text, c);
            }
        }

        Row(0, ["", T("now"), T("lowest"), T("average"), T("highest")], header: true);
        int r = 1;
        foreach (var x in series.OrderBy(x => x.Kind))
        {
            if (!data.TryGetValue(x.Key, out var points) || points.Count == 0)
                continue;
            string V(double v) => $"{v:0} {T(x.Unit)}";
            Row(r++, [Describe(x), V(points[^1].Avg), V(points.Min(p => p.Min)), V(points.Average(p => p.Avg)), V(points.Max(p => p.Max))], header: false);
        }
        if (r == 1)
            Row(1, [T("Nothing recorded in this time yet."), "", "", "", ""], header: false);
    }

    private static string Describe(Series x) => x.Kind switch
    {
        SeriesKind.Temperature => T($"{T(x.Name)} temperature"),
        SeriesKind.Power => T($"{T(x.Name)} power"),
        SeriesKind.Load => T($"{T(x.Name)} load"),
        SeriesKind.FanPercent => T($"{x.Name} speed"),
        _ => T($"{x.Name} rpm"),
    };

    /// <summary>What a warning can watch: temperatures, power, load and the fans' rpm.</summary>
    private List<Series> WatchableSeries()
    {
        IEnumerable<Series> standard =
        [
            HistoryRecorder.CpuTemp, HistoryRecorder.GpuTemp, HistoryRecorder.GpuHotspot, HistoryRecorder.GpuMemory,
            HistoryRecorder.CpuPower, HistoryRecorder.GpuPower, HistoryRecorder.CpuLoad, HistoryRecorder.GpuLoad,
        ];
        var fans = (_app.Inventory?.Groups() ?? []).Select(g => HistoryRecorder.FanSeries(g, SeriesKind.FanRpm));
        return [.. standard.Concat(fans).Concat(_app.Monitor.Store.AllSeries()).Where(x => x.Kind != SeriesKind.FanPercent).DistinctBy(x => x.Key)];
    }

    private void BuildWarnings()
    {
        _building = true;
        try
        {
            var settings = _app.Monitor.Warnings;
            FanStoppedBox.IsChecked = settings.FanStopped;
            WarningRows.Children.Clear();
            var choices = WatchableSeries();
            for (int i = 0; i < settings.Rules.Count; i++)
                WarningRows.Children.Add(WarningRow(i, settings.Rules[i], choices));
        }
        finally
        {
            _building = false;
        }
    }

    private FrameworkElement WarningRow(int index, WarningRule rule, List<Series> choices)
    {
        if (choices.All(c => c.Key != rule.Series))
            choices = [.. choices, new Series(rule.Series, rule.Series, SeriesKind.Temperature)];
        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0) };
        var what = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var c in choices)
            what.Items.Add(Describe(c));
        what.SelectedIndex = choices.FindIndex(c => c.Key == rule.Series);
        var direction = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        direction.Items.Add(T("above"));
        direction.Items.Add(T("below"));
        direction.SelectedIndex = rule.Above ? 0 : 1;
        var limit = new TextBox { Width = 64, Text = rule.Limit.ToString("0", CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center };
        var unit = new TextBlock { Text = T(choices[Math.Max(0, what.SelectedIndex)].Unit),VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0), MinWidth = 26 };
        var seconds = new TextBox { Width = 52, Text = rule.Seconds.ToString(CultureInfo.InvariantCulture), VerticalContentAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "✕", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0), ToolTip = T("Remove") };

        void Changed()
        {
            if (_building)
                return;
            var rules = _app.Monitor.Warnings.Rules.ToList();
            if (index >= rules.Count)
                return;
            var series = choices[Math.Max(0, what.SelectedIndex)];
            unit.Text = T(series.Unit);
            rules[index] = new WarningRule(series.Key, direction.SelectedIndex == 0,
                double.TryParse(limit.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ? l : rule.Limit,
                int.TryParse(seconds.Text, out int sec) ? Math.Clamp(sec, 1, 3600) : rule.Seconds,
                enabled.IsChecked == true);
            SaveWarnings(rules);
        }
        enabled.Click += (_, _) => Changed();
        what.SelectionChanged += (_, _) => Changed();
        direction.SelectionChanged += (_, _) => Changed();
        limit.LostFocus += (_, _) => Changed();
        seconds.LostFocus += (_, _) => Changed();
        remove.Click += (_, _) =>
        {
            var rules = _app.Monitor.Warnings.Rules.ToList();
            if (index < rules.Count)
                rules.RemoveAt(index);
            SaveWarnings(rules);
            BuildWarnings();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var element in new UIElement[] { enabled, what, direction, limit, unit, new TextBlock { Text = T("for"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, seconds,
                     new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }, remove })
            row.Children.Add(element);
        return row;
    }

    private void SaveWarnings(IReadOnlyList<WarningRule>? rules = null) =>
        _app.SaveWarnings(new WarningSettings(FanStoppedBox.IsChecked == true, rules ?? _app.Monitor.Warnings.Rules));

    // ── extra quiet ────────────────────────────────────────────────────────────────────

    private void BuildQuiet()
    {
        var quiet = _app.Quiet;
        QuietAway.IsChecked = quiet.WhenAway;
        QuietAwayMinutes.Text = quiet.AwayMinutes.ToString(CultureInfo.InvariantCulture);
        QuietNight.IsChecked = quiet.AtNight;
        QuietFrom.Text = quiet.NightFrom;
        QuietTo.Text = quiet.NightTo;
        ReactEarlyBox.IsChecked = _app.Control.ReactEarly;
    }

    private void SaveQuiet()
    {
        var old = _app.Quiet;
        static string Time(string text, string fallback) => TimeOnly.TryParse(text.Replace('.', ':'), CultureInfo.InvariantCulture, out var t) ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : fallback;
        _app.SaveQuiet(new QuietSettings(
            QuietAway.IsChecked == true,
            int.TryParse(QuietAwayMinutes.Text, out int minutes) ? Math.Clamp(minutes, 1, 240) : old.AwayMinutes,
            QuietNight.IsChecked == true,
            Time(QuietFrom.Text, old.NightFrom),
            Time(QuietTo.Text, old.NightTo)));
        BuildQuiet(); // shows what was understood ("23.30" → "23:30")
        UpdateLive();
    }

    // ── reports ────────────────────────────────────────────────────────────────────────

    private void UpdateReportsNow()
    {
        var now = _app.Loop.Last?.Time ?? DateTimeOffset.Now;
        ReportsNow.Text = _app.Monitor.CurrentSession is { } current
            ? T($"Now: {Sessions.Pretty(current.Program)} for {(now - current.Start).TotalMinutes:0} min.")
            : "";
    }

    /// <summary>The last 7 days in one line ("12 h 05 min played · GPU up to 78 °C · cooling OK") and a few words more under it.</summary>
    private void BuildWeek()
    {
        var week = WeekSummary.Of(_app.Monitor.Store, _app.Loop.Last?.Time ?? DateTimeOffset.Now);
        static string Length(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.TotalMinutes:0} min";

        // the same verdict as on the Cooling health page, in two words
        double? warmer = _app.CoolingWarmer();
        var (cooling, coolingDetail) = warmer switch
        {
            null => (null, T("Cooling health has nothing to compare with yet.")),
            < CoolingHealth.Fine => (T("cooling OK"), T("Cooling as good as after the calibration.")),
            < CoolingHealth.Clean => (T("cooling a bit warmer"), T("A bit warmer than after the calibration: check the dust filters when you get to it.")),
            _ => (T("cooling clearly warmer"), T("Clearly warmer than after the calibration: clean the dust filters, fans and heatsinks, then calibrate again.")),
        };
        var headline = new List<string> { week.Sessions == 0 ? T("Nothing played") : T($"{Length(week.Played)} played") };
        if (week.GpuMax is { } gpu)
            headline.Add(T($"GPU up to {gpu:0} °C"));
        if (cooling is not null)
            headline.Add(cooling);
        WeekHeadline.Text = string.Join(" · ", headline);

        var detail = new List<string>();
        if (week.Sessions > 0)
        {
            string played = week.Sessions == 1 ? T($"1 session: {week.Mostly}") : T($"{week.Sessions} sessions, mostly {week.Mostly}");
            if (week.PlayedBefore is { } before)
            {
                var more = week.Played - before;
                played += more.Duration() < TimeSpan.FromMinutes(10) ? T(", about as long as the week before")
                    : more > TimeSpan.Zero ? T($", {Length(more)} more than the week before")
                    : T($", {Length(more.Duration())} less than the week before");
            }
            detail.Add(played + ".");
        }
        else if (week.PlayedBefore is { } before && before > TimeSpan.Zero)
            detail.Add(T($"The week before: {Length(before)} played."));
        if (week.HotspotMax is { } hotspot && week.CpuMax is { } cpu)
            detail.Add(T($"GPU hotspot up to {hotspot:0} °C, CPU up to {cpu:0} °C."));
        else if (week.CpuMax is { } cpuOnly)
            detail.Add(T($"CPU up to {cpuOnly:0} °C."));
        detail.Add(coolingDetail);
        WeekDetail.Text = string.Join(" ", detail);
    }

    private void BuildReports()
    {
        UpdateReportsNow();
        BuildWeek();
        var sessions = _app.Monitor.Store.Sessions();
        static double? Avg(GameSession s, Series x) => s.Stats.TryGetValue(x.Key, out var v) ? v.Avg : null;
        static double? Max(GameSession s, Series x) => s.Stats.TryGetValue(x.Key, out var v) ? v.Max : null;
        static double? Fans(GameSession s)
        {
            var fans = s.Stats.Where(kv => kv.Key.StartsWith("fan.", StringComparison.Ordinal) && kv.Key.EndsWith(".percent", StringComparison.Ordinal)).ToList();
            return fans.Count > 0 ? fans.Average(kv => kv.Value.Avg) : null;
        }
        static string T(double? v, string unit) => v is { } x ? $"{x:0} {unit}" : "–";
        static string Length(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.TotalMinutes:0} min";
        // weighted by how long each session was
        static double? Weighted(IEnumerable<GameSession> list, Func<GameSession, double?> pick)
        {
            var known = list.Where(s => pick(s) is not null).ToList();
            double seconds = known.Sum(s => s.Length.TotalSeconds);
            return known.Count == 0 || seconds <= 0 ? null : known.Sum(s => pick(s)!.Value * s.Length.TotalSeconds) / seconds;
        }

        var games = sessions.GroupBy(s => Sessions.Pretty(s.Program)).OrderByDescending(g => g.Sum(s => s.Length.TotalSeconds));
        FillTable(GameRows, [2, 1, 1.3, 1, 1.2, 1, 1, 1.1],
            ["", Texts.T("sessions"), Texts.T("time"), "GPU Ø", Texts.T("hotspot max"), "CPU Ø", Texts.T("fans Ø"), Texts.T("GPU power Ø")],
            games.Select(g => new[]
            {
                g.Key, $"{g.Count()}", Length(TimeSpan.FromSeconds(g.Sum(s => s.Length.TotalSeconds))),
                T(Weighted(g, s => Avg(s, HistoryRecorder.GpuTemp)), "°C"), T(g.Max(s => Max(s, HistoryRecorder.GpuHotspot)), "°C"),
                T(Weighted(g, s => Avg(s, HistoryRecorder.CpuTemp)), "°C"), T(Weighted(g, Fans), "%"), T(Weighted(g, s => Avg(s, HistoryRecorder.GpuPower)), "W"),
            }),
            Texts.T("No sessions yet: play something for a few minutes."));

        FillTable(SessionRows, [1.3, 2, 1.1, 1, 1.4, 1.4, 1.2, 1],
            [Texts.T("when"), "", Texts.T("length"), Texts.T("preset"), "GPU Ø / max", "CPU Ø / max", Texts.T("hotspot max"), Texts.T("fans Ø")],
            sessions.Take(50).Select(s => new[]
            {
                s.Start.ToLocalTime().ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture), Sessions.Pretty(s.Program), Length(s.Length), Texts.T(s.Preset),
                $"{T(Avg(s, HistoryRecorder.GpuTemp), "°C")} / {T(Max(s, HistoryRecorder.GpuTemp), "°C")}",
                $"{T(Avg(s, HistoryRecorder.CpuTemp), "°C")} / {T(Max(s, HistoryRecorder.CpuTemp), "°C")}",
                T(Max(s, HistoryRecorder.GpuHotspot), "°C"), T(Fans(s), "%"),
            }),
            Texts.T("The last 50 sessions show here."));
    }

    /// <summary>A simple table: a header row in grey, then the rows; the first column left, the others right.</summary>
    /// <param name="headerWhenEmpty">False: without rows only <paramref name="empty"/> is shown, no column titles over nothing.</param>
    private void FillTable(Grid grid, double[] widths, string[] header, IEnumerable<string[]> rows, string empty, bool headerWhenEmpty = true)
    {
        var list = rows.ToList();
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        foreach (double width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        int r = 0;
        void Row(string[] cells, bool caption)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cells.Length; c++)
            {
                var text = new TextBlock
                {
                    Text = cells[c], FontSize = 13, Margin = new Thickness(0, 0, 10, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = c == 0 || (c == 1 && header[1].Length == 0) ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                };
                if (caption)
                    text.Style = (Style)FindResource("Caption");
                Grid.SetRow(text, r);
                Place(grid, text, c);
            }
            r++;
        }
        if (list.Count > 0 || headerWhenEmpty)
            Row(header, caption: true);
        foreach (var row in list)
            Row(row, caption: false);
        if (list.Count == 0)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = empty, Style = (Style)FindResource("Caption") };
            Grid.SetRow(text, r);
            Grid.SetColumnSpan(text, widths.Length);
            grid.Children.Add(text);
        }
    }

    // ── cooling health ─────────────────────────────────────────────────────────────────

    private void OnHealthUpdated() => Dispatcher.BeginInvoke(() =>
    {
        if (HealthPage.Visibility == Visibility.Visible)
            BuildHealth();
    });

    private void BuildHealth()
    {
        HealthCalibrate.Visibility = Visibility.Collapsed;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var changes = CoolingHealth.SinceCalibration(_app.Monitor.Store.HealthDays());
        ShowHealthChart(changes, today); // every day kept, also from earlier calibrations
        BuildFanWear();

        if (_app.Recommended is not { } calibration || _app.HealthReference is not { } reference)
        {
            HealthDot.Fill = Paused;
            HealthTitle.Text = T("Calibrate first");
            HealthDetail.Text = T("Cooling health compares with the calibration.");
            HealthCompareIntro.Text = "";
            HealthCompareNote.Visibility = Visibility.Collapsed;
            HealthCompareRows.Children.Clear();
            HealthRows.Children.Clear();
            return;
        }

        var now = _app.HealthNow();
        var rise = CoolingHealth.Rise(calibration);
        var comparisons = CoolingHealth.Compare(changes, reference, today, rise);
        var first = comparisons.FirstOrDefault(c => c.Label == CoolingHealth.AfterCalibration);
        var (topCpu, topGpu) = CoolingHealth.FullLoad(calibration);
        static string Signed(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
        static string Change(double? change, double? percent) => change is not { } c ? "–"
            : $"{Signed(c)} °C" + (percent is { } p ? $" ({Signed(p)} %)" : "");

        if (first is null && now is null)
        {
            HealthDot.Fill = SetUp;
            HealthTitle.Text = T("Collecting data");
            HealthDetail.Text = T($"{_app.HealthMinutes()} of {CoolingHealth.MinMinutes} steady minutes in the last 7 days. Just use the PC as usual; a game helps.");
        }
        else if (first is null)
        {
            int since = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(reference.LocalDateTime.Date).DayNumber);
            HealthDot.Fill = SetUp;
            HealthTitle.Text = T("Learning what normal looks like");
            HealthDetail.Text = T($"The first week after the calibration is what's normal for this PC ({Math.Min(since, 7)} of 7 days); the comparison with it starts a week later.");
        }
        else
        {
            double worst = Math.Max(first.Cpu ?? 0, first.Gpu ?? 0);
            string detail = T($"At full load, compared with the first week after the calibration: CPU {Change(first.Cpu, first.CpuPercent)}, GPU {Change(first.Gpu, first.GpuPercent)}.");
            (HealthDot.Fill, HealthTitle.Text, HealthDetail.Text) = worst < CoolingHealth.Fine
                ? (Running, T("Cooling as good as after the calibration"), detail)
                : worst < CoolingHealth.Clean
                ? (Attention, T("A bit warmer than after the calibration"), detail + T(" Check the dust filters when you get to it."))
                : (Problem, T("Clearly warmer than after the calibration"), detail + T(" Clean the dust filters, fans and heatsinks, then calibrate again."));
            HealthCalibrate.Visibility = worst >= CoolingHealth.Fine ? Visibility.Visible : Visibility.Collapsed;
        }

        // the last 7 days against earlier weeks, in °C and as a share of the rise above the room
        HealthCompareIntro.Text = T("The last 7 days against earlier weeks, at the same power and fan speeds. + means warmer now.");
        FillTable(HealthCompareRows, [2.2, 1.5, 1.5],
            ["", T("CPU at full load"), T("GPU at full load")],
            comparisons.Select(c => new[] { T(c.Label), Change(c.Cpu, c.CpuPercent), Change(c.Gpu, c.GpuPercent) }),
            T("Nothing to compare yet. The first week after the calibration is what's normal; the comparisons start a week after that."),
            headerWhenEmpty: false);
        var rises = new List<string>();
        if (rise.Cpu is { } cpuRise)
            rises.Add(T($"the CPU heats up {cpuRise:0} °C above the room at {topCpu:0} W"));
        if (rise.Gpu is { } gpuRise)
            rises.Add(T($"the GPU {gpuRise:0} °C at {topGpu:0} W"));
        HealthCompareNote.Text = comparisons.Count == 0 || rises.Count == 0 ? ""
            : T($"The % is how much worse the cooling works. At full load {string.Join(T(" and "), rises)}: 1 °C more on {rise.Cpu ?? rise.Gpu:0} °C is {100 / (rise.Cpu ?? rise.Gpu ?? 100):0.0} %.");
        HealthCompareNote.Visibility = HealthCompareNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        string Extra(double? extra, string part) => extra is not { } e ? T($"not enough {part} load yet") : T($"{Signed(e)} °C than the model expects");
        FillTable(HealthRows, [1.6, 3],
            ["", ""],
            now is null ? [] :
            [
                [T("Room"), T($"about {calibration.Ambient + now.RoomShift:0} °C ({Signed(now.RoomShift)} °C against the {calibration.Ambient:0} °C given at the calibration)")],
                [T($"CPU at full load ({topCpu:0} W)"), Extra(now.CpuExtra, "CPU")],
                [T($"GPU at full load ({topGpu:0} W)"), Extra(now.GpuExtra, "GPU")],
                [T("Based on"), T($"{now.Minutes} steady minutes")],
            ],
            T("Nothing to show yet."));
    }

    /// <summary>Every fan against its first week: a row with the verdict and *Start again*, and a chart over the chosen range.</summary>
    private void BuildFanWear()
    {
        FanWearRows.Children.Clear();
        FanWearRows.RowDefinitions.Clear();
        FanWearRows.ColumnDefinitions.Clear();
        FanWearRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        FanWearRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        FanWearRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fans = _app.FanWearNow();
        var names = _app.GroupNames.ToList();
        Color ColorOf(string name, int order) => Palette[(names.IndexOf(name) is >= 0 and var i ? i : order) % Palette.Length];
        static string Signed(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);

        for (int f = 0; f < fans.Count; f++)
        {
            var (group, result, hasFirstWeek, _) = fans[f];
            FanWearRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = SeriesBrush(ColorOf(group.Name, f)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = group.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            string verdict = result switch
            {
                { Change: null, FirstWeekDays: 0 } => T("No steady running recorded yet."),
                { Learning: true } => T($"Learning what's normal ({result.FirstWeekDays} of 7 days)."),
                { Change: null } => T("Nothing to compare yet: the comparison starts a week after its first week, at the speeds it ran at then."),
                { Change: <= FanWear.Worn } => T($"⚠ {-result.Change!.Value:0.0} % slower than in its first week: probably worn or blocked. Clean it, or replace it soon."),
                { Change: <= FanWear.Check } => T($"⚠ {-result.Change!.Value:0.0} % slower than in its first week: clean it and listen for grinding or rattling."),
                { Change: >= 8 } => T($"Turns {result.Change!.Value:0.0} % faster than in its first week (a changed BIOS setting?)."),
                _ => T($"Turns as fast as in its first week ({Signed(result.Change!.Value)} %)."),
            };
            var text = new TextBlock { Text = verdict, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center };
            if (result.Change <= FanWear.Check)
                text.FontWeight = FontWeights.SemiBold;
            var again = new Button { Content = T("Start again"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = T("After cleaning or replacing it: a new first week from today."),Visibility = hasFirstWeek ? Visibility.Visible : Visibility.Hidden };
            again.Click += (_, _) =>
            {
                if (MessageBox.Show(this, T($"Start a new first week for {group.Name} from today? Do this after cleaning or replacing it."),"AuFantic", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    _app.StartFanAgain(group);
            };
            foreach (var (element, column) in new (UIElement, int)[] { (name, 0), (text, 1), (again, 2) })
            {
                Grid.SetRow(element, f);
                Place(FanWearRows, element, column);
            }
        }
        if (fans.Count == 0)
            FanWearRows.Children.Add(new TextBlock { Text = T("Find the fans first (Calibration)."),Style = (Style)FindResource("Caption") });

        bool weekly = _healthRange > TimeSpan.FromDays(45);
        var to = new DateTimeOffset(DateOnly.FromDateTime(DateTime.Now).AddDays(1).ToDateTime(TimeOnly.MinValue));
        var from = to - _healthRange;
        var first = DateOnly.FromDateTime(from.LocalDateTime);
        FanWearChart.Show([.. fans.Select((fan, f) => new ChartLine(fan.Group.Name, ColorOf(fan.Group.Name, f),
            FanWear.Points(fan.Daily.Where(d => d.Day >= first), weekly)))], from, to, "%");
    }

    /// <summary>
    /// The chart of every day (per week for longer ranges) against the first week after its
    /// calibration: 0 = as good as then; a new calibration starts again at 0.
    /// </summary>
    private void ShowHealthChart(IReadOnlyList<DayChange> changes, DateOnly today)
    {
        bool weekly = _healthRange > TimeSpan.FromDays(45);
        var to = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var from = to - _healthRange;
        var shown = changes.Where(c => c.Day >= DateOnly.FromDateTime(from.LocalDateTime)).ToList();
        HealthChartIntro.Text = T($"{(weekly ? T("Per week (the band: its coolest and warmest day)") : T("Per day"))}: how much warmer CPU and GPU run at full load than in the first week after their calibration (0 = as good as then), and the room against the calibration. A new calibration starts again at 0.");
        HealthChart.Show(
        [
            new ChartLine("CPU", LiveChart.CpuColor, CoolingHealth.Points(shown, c => c.Cpu, weekly)),
            new ChartLine("GPU", LiveChart.GpuColor, CoolingHealth.Points(shown, c => c.Gpu, weekly)),
            new ChartLine(T("room"), Color.FromRgb(0x8a, 0x89, 0x84), CoolingHealth.Points(shown, c => c.RoomShift, weekly)),
        ], from, to, "°C");
    }

    // ── activity ───────────────────────────────────────────────────────────────────────

    private void BuildLog()
    {
        LogRows.Children.Clear();
        var entries = _app.Log.Entries;
        foreach (var entry in entries.Reverse().Take(LogShown))
            LogRows.Children.Add(LogRow(entry));
        if (entries.Count == 0)
            LogRows.Children.Add(new TextBlock { Text = T("Nothing yet."),Style = (Style)FindResource("Caption") });
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
            Text = time.Date == DateTime.Today ? T($"today {time:HH:mm:ss}") : $"{time:dd.MM. HH:mm:ss}",
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
        kind.Children.Add(new TextBlock { Text = T(entry.Kind.ToString()), FontSize = 13 });
        Place(row, kind, 1);
        Place(row, new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }, 2);
        return row;
    }

    // ── settings ───────────────────────────────────────────────────────────────────────

    private void UpdateCalibrationInfo()
    {
        // the calibrations as stored (English) → in the chosen language; the result's own list only for an old result without them
        var sources = _app.Store.Calibrations.Count > 0
            ? _app.Store.Calibrations.Select(c => T($"{c.Time:dd.MM. HH:mm} · {LoadName(c.Load)} · {c.Runs} runs")).ToList()
            : _app.Recommended?.Sources ?? [];
        CalibrationInfo.Text = _app.Recommended is not { } r ? T("Not calibrated yet.")
            : sources.Count == 0 ? T($"Calibrated {r.Created:dd.MM.yyyy HH:mm}.")
            : T($"{_app.Store.Calibrations.Count} calibration(s), {_app.Store.Runs.Count} runs: {string.Join("; ", sources)}");
        OpenPage.IsEnabled = File.Exists(_app.PagePath);
    }

    /// <summary>
    /// A calibration's load as stored in English ("built-in load (CPU on all cores + NVIDIA …)",
    /// "your own load", or a game's name) in the chosen language; a game's name stays as it is.
    /// </summary>
    private static string LoadName(string stored)
    {
        const string builtIn = "built-in load (", allCores = "CPU on all cores + ";
        if (stored.StartsWith(builtIn, StringComparison.Ordinal) && stored.EndsWith(')'))
        {
            string inner = stored[builtIn.Length..^1];
            inner = inner.StartsWith(allCores, StringComparison.Ordinal) ? T($"CPU on all cores + {inner[allCores.Length..]}") : T(inner);
            return T($"built-in load ({inner})");
        }
        return stored is "your own load" or "calibration" ? T(stored) : stored;
    }

    /// <summary>
    /// "Start with Windows", and a hint if the task starts another copy (an older one, before
    /// AuFantic was unpacked somewhere else): that copy wouldn't get this one's updates.
    /// </summary>
    private void ShowAutostart()
    {
        AutostartBox.IsChecked = Autostart.IsEnabled();
        AutostartNote.Text = AutostartBox.IsChecked == true && Autostart.OtherCopy() is { } other
            ? T($"⚠ It starts another copy: {other}. Switch it off and on again to start this one.")
            : T("Starts when you log in, without an admin prompt.");
    }

    private void OnUpdateStateChanged() => Dispatcher.BeginInvoke(ShowUpdates);

    /// <summary>AuFantic's own memory and CPU, now and with the window closed (Settings → Developer).</summary>
    private void ShowSelfUsage()
    {
        var (cpu, average) = SelfUsage.Cpu();
        string closed = SelfUsage.WindowClosed is { } c
            ? T($"With the window closed: {c.Bytes / 1048576.0:0} MB (measured at {c.At:HH:mm}).")
            : T("With the window closed: not measured yet (it's measured every minute while the window is closed).");
        SelfUsageText.Text = T($"AuFantic uses {SelfUsage.Memory() / 1048576.0:0} MB of memory and {cpu:0.0} % CPU right now, with this window open. {closed} ")
            + T($"On average since the start: {average:0.00} % CPU. The watchdog, a second small AuFantic, adds about 6 MB.");
    }

    /// <summary>A fan's speed in a few characters: "45 %", "off", "BIOS", or "BIOS 45 %" for a fan the user gave to the BIOS.</summary>
    internal static string Speed(FanReading? fan) => fan switch
    {
        { Bios: true, BiosPercent: { } bios } => $"BIOS {bios:0} %",
        { Percent: 0 } => T("off"),
        { Percent: { } p } => $"{p:0} %",
        _ => "BIOS",
    };

    /// <summary>The Updates card: this version, what the last check found, and the buttons that fit.</summary>
    private void ShowUpdates()
    {
        var release = _app.UpdateAvailable;
        UpdateStatus.Text = _updating
            ?? (release is not null
                ? T($"{release.Name} is available{(release.Published is { } p ? T($" (from {p.LocalDateTime:dd.MM.yyyy})") : "")}; you have {AppVersion.Text}. ")
                  + T($"Updating downloads it ({release.ZipSize / 1e6:0} MB), closes AuFantic for a few seconds (the BIOS keeps the fans) and starts the new version. Your data stays.")
                : _app.UpdateProblem is { } problem ? T($"You have {AppVersion.Text}. {problem}")
                : _app.UpdateChecked is { } at ? T($"You have {AppVersion.Text}, the newest version (checked at {at.LocalDateTime:HH:mm}).")
                : T($"You have {AppVersion.Text}."));
        UpdateInstall.Content = release is null ? "" : T($"Update to {release.Version}");
        UpdateInstall.Visibility = UpdateNotes.Visibility = release is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstall.IsEnabled = UpdateCheckButton.IsEnabled = _updating is null;
    }

    /// <summary>Downloads and installs the new version, then AuFantic restarts into it.</summary>
    private async Task InstallUpdate()
    {
        if (_app.UpdateAvailable is not { } release)
            return;
        _updating = T($"Downloading {release.Name} …");
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        ShowUpdates();
        var progress = new Progress<double>(share =>
        {
            UpdateProgress.Value = share;
            UpdateStatus.Text = _updating = T($"Downloading {release.Name} … {share * 100:0} %");
        });
        try
        {
            await _app.InstallUpdateAsync(release, progress);
        }
        catch (Exception ex)
        {
            _updating = null;
            UpdateProgress.Visibility = Visibility.Collapsed;
            ShowUpdates();
            MessageBox.Show(this, T($"The update didn't work: {ex.Message}\n\nAuFantic {AppVersion.Text} keeps running as before."),"AuFantic", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpdateStatus.Text = T($"{release.Name} is installed. AuFantic starts again …");
        try
        {
            _app.StartNewVersion("--open");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T($"{release.Name} is installed, but it didn't start ({ex.Message}). Start AuFantic again yourself."),"AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Another data folder: one that a second Windows account on this PC chooses too, so both
    /// have the same fans, calibration and curves. AuFantic starts again for it, and the new
    /// start copies the data (then no AuFantic has the files open).
    /// </summary>
    private void ChangeFolder()
    {
        if (_app.Calibrating)
        {
            MessageBox.Show(this, T("A calibration is running: finish or stop it first."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string now = _app.DataUse.Root;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("The folder for AuFantic's data"), InitialDirectory = now };
        if (dialog.ShowDialog(this) != true)
            return;
        string folder = dialog.FolderName;

        var change = DataFolder.Check(_app.DataUse, folder);
        if (change == FolderChange.None)
            return; // the folder in use
        if (change is FolderChange.Nested or FolderChange.OtherFiles)
        {
            MessageBox.Show(this, change == FolderChange.Nested
                    ? T("Choose a folder that isn't inside the data folder in use, and doesn't contain it.")
                    : T("This folder has other files in it. Choose an empty folder (the dialog can make a new one), or one that AuFantic already uses in another Windows account."),
                "AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string question = change == FolderChange.Copy
            ? T($"AuFantic copies its data (fans, calibration, curves, history, settings) to\n{folder}\nand uses that folder from now on. The old folder stays as it is:\n{now}")
            : T($"This folder already has AuFantic's data (last changed {DataFolder.LastChanged(folder) ?? DateTime.Now:dd.MM.yyyy HH:mm}):\n{folder}\n\nAuFantic uses what is there; nothing is copied. What this Windows account has now stays where it is and isn't used any more:\n{now}");
        if (MessageBox.Show(this, question + "\n\n" + T("AuFantic starts again for this. The BIOS has the fans for those few seconds."),
                T("Data folder"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        try
        {
            _app.ChooseFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, T($"AuFantic can't write to {folder}: {ex.Message}\n\nNothing changed."), "AuFantic", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _app.StartAgain("--open", "settings");
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowSensors()
    {
        string path = Path.Combine(_app.RunsPath, "sensors.txt");
        File.WriteAllText(path, _app.SensorList());
        OpenInExplorer(path);
    }

    /// <summary>The old test console needs the fans to itself: AuFantic hands them to the BIOS and closes first.</summary>
    private void OpenConsole()
    {
        string console = Path.Combine(AppContext.BaseDirectory, "autofantic-spike.exe");
        if (!File.Exists(console))
        {
            MessageBox.Show(this, T($"The test console (autofantic-spike.exe) isn't next to AuFantic ({AppContext.BaseDirectory})."),"AuFantic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, T("AuFantic closes (the fans go back to the BIOS) and the test console opens. Start AuFantic again afterwards."),
            T("Test console"),MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
            return;

        _app.Loop.Dispose();
        Process.Start(new ProcessStartInfo(console, "test") { UseShellExecute = true })?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ── light and dark ─────────────────────────────────────────────────────────────────

    /// <summary>The theme's text colour, only so the window notices a switch between light and dark.</summary>
    private static readonly DependencyProperty ThemeTextProperty = DependencyProperty.Register(
        "ThemeText", typeof(object), typeof(MainWindow), new PropertyMetadata(null, (d, _) => ((MainWindow)d).OnThemeChanged()));

    /// <summary>One brush per series colour, shared by every dot and bar in it, so a switch re-colours them all.</summary>
    private readonly Dictionary<Color, SolidColorBrush> _seriesBrushes = [];

    /// <summary>The brush for a series colour (named by its light step), in the step of the current theme.</summary>
    private SolidColorBrush SeriesBrush(Color light)
    {
        if (!_seriesBrushes.TryGetValue(light, out var brush))
            _seriesBrushes[light] = brush = new SolidColorBrush(SeriesColors.For(light, this));
        return brush;
    }

    private void OnThemeChanged()
    {
        foreach (var (light, brush) in _seriesBrushes)
            brush.Color = SeriesColors.For(light, this);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private Ellipse Dot(int group) => new()
    {
        Width = 10,
        Height = 10,
        Fill = SeriesBrush(Palette[group % Palette.Length]),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Badge(string text, bool over)
    {
        var badge = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = (over ? "⚠ " : "") + text, FontSize = 12 },
        };
        if (over)
            badge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xe3, 0x49, 0x48));
        else
            badge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); // follows a switch to dark
        return badge;
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static void OpenInExplorer(string path)
    {
        // through Explorer, so a browser or editor doesn't inherit AuFantic's admin rights
        using var _ = Process.Start("explorer.exe", $"\"{path}\"");
    }

    private static string Temp(double? celsius) => celsius is { } c and > 0 ? $"{c:0} °C" : "–";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
