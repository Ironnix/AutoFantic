using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AutoFantic.Core.Calibration;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AutoFantic.App;

/// <summary>
/// A fan curve to look at and to set: temperature across, fan speed up. The recommended curve sits
/// faint in the background, the curve in use on top with points to drag; double-click adds a
/// point, right-click removes one. A ring shows where the fan is right now.
/// </summary>
internal sealed class CurveEditor : FrameworkElement
{
    private const double MinT = 30, MaxT = 100;
    private const double PadLeft = 46, PadRight = 18, PadTop = 14, PadBottom = 34;
    private const double HitRadius = 11;

    private List<CurvePoint> _curve = [];
    private IReadOnlyList<CurvePoint> _recommended = [];
    private int _drag = -1, _hover = -1;

    public CurveEditor()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        ThemeRedraw.Follow(this);
    }

    /// <summary>Raised when the user finished changing the curve (drag released, point added or removed).</summary>
    public event Action<IReadOnlyList<CurvePoint>>? CurveEdited;

    public Color Color { get; set; } = Color.FromRgb(0x2a, 0x78, 0xd6);

    public string TemperatureLabel { get; set; } = "CPU temperature";

    /// <summary>
    /// Where the fan is right now: the (smoothed) temperature it follows, its speed (0 = off; null =
    /// the BIOS has it), and why it isn't exactly on the curve, if it isn't.
    /// </summary>
    public (double Temperature, double? Percent, string? Note)? Live { get; set; }

    /// <summary>The fan may switch off at or below this temperature (at idle); null = it keeps turning.</summary>
    public double? OffBelow { get; set; }

    public void SetCurves(IReadOnlyList<CurvePoint> curve, IReadOnlyList<CurvePoint> recommended)
    {
        if (_drag >= 0)
            return; // don't pull the curve out from under the mouse
        _curve = curve.ToList();
        _recommended = recommended;
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    // ── geometry ───────────────────────────────────────────────────────────────────────

    private Rect Plot => new(PadLeft, PadTop, Math.Max(10, ActualWidth - PadLeft - PadRight), Math.Max(10, ActualHeight - PadTop - PadBottom));

    private double X(double t) => Plot.Left + Plot.Width * (Math.Clamp(t, MinT, MaxT) - MinT) / (MaxT - MinT);

    private double Y(double p) => Plot.Top + Plot.Height * (1 - Math.Clamp(p, 0, 100) / 100);

    private double T(double x) => MinT + (x - Plot.Left) / Plot.Width * (MaxT - MinT);

    private double P(double y) => (1 - (y - Plot.Top) / Plot.Height) * 100;

    private int HitTest(Point at)
    {
        for (int i = 0; i < _curve.Count; i++)
        {
            var c = new Point(X(_curve[i].Temperature), Y(_curve[i].Percent));
            if ((c - at).Length <= HitRadius)
                return i;
        }
        return -1;
    }

    // ── drawing ────────────────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var plot = Plot;
        var text = Theme("TextFillColorSecondaryBrush", Color.FromRgb(0x6b, 0x6a, 0x66));
        var strong = Theme("TextFillColorPrimaryBrush", Color.FromRgb(0x1b, 0x1b, 0x1b));
        var grid = new Pen(Theme("ControlStrokeColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80)), 1);
        var surface = Theme("CardBackgroundFillColorDefaultBrush", Colors.White);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole area takes the mouse

        foreach (var p in new[] { 0, 25, 50, 75, 100 })
        {
            dc.DrawLine(grid, new Point(plot.Left, Y(p)), new Point(plot.Right, Y(p)));
            Label(dc, $"{p} %", new Point(plot.Left - 8, Y(p)), text, HorizontalAlignment.Right);
        }
        for (int t = 30; t <= 100; t += 10)
        {
            dc.DrawLine(grid, new Point(X(t), plot.Bottom), new Point(X(t), plot.Bottom + 4));
            Label(dc, $"{t} °C", new Point(X(t), plot.Bottom + 14), text, HorizontalAlignment.Center);
        }
        Label(dc, TemperatureLabel, new Point(plot.Right, plot.Bottom + 28), text, HorizontalAlignment.Right, size: 11);

        var series = SeriesColors.For(Color, this);
        var color = new SolidColorBrush(series);
        var faint = new SolidColorBrush(Color.FromArgb(0x70, series.R, series.G, series.B));

        // recommended: dashed, faint
        if (_recommended.Count > 0)
            dc.DrawGeometry(null, new Pen(faint, 2) { DashStyle = new DashStyle([3, 3], 0) }, Line(_recommended));

        // in use: solid, with a soft area under it
        if (_curve.Count > 0)
        {
            var area = Line(_curve, close: true);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x1c, series.R, series.G, series.B)), null, area);
            dc.DrawGeometry(null, new Pen(color, 2.5) { LineJoin = PenLineJoin.Round }, Line(_curve));
        }

        // where the fan may be off: a band along the bottom up to the switch-off temperature
        if (OffBelow is { } off && off > MinT)
        {
            var band = new Rect(new Point(plot.Left, Y(0) - 6), new Point(X(off), Y(0)));
            var tint = new SolidColorBrush(Color.FromArgb(0x55, series.R, series.G, series.B));
            dc.DrawRectangle(tint, null, band);
            // explained below the axis, like a legend, where it can't collide with the live label
            dc.DrawRectangle(tint, null, new Rect(plot.Left, plot.Bottom + 25, 14, 6));
            Label(dc, $"off at idle up to {off:0} °C", new Point(plot.Left + 20, plot.Bottom + 28), text, HorizontalAlignment.Left, size: 11);
        }

        // where the fan is now
        if (Live is { } live)
        {
            double percent = live.Percent ?? 0;
            var at = new Point(X(live.Temperature), Y(percent));
            dc.DrawLine(new Pen(faint, 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(at.X, plot.Bottom), at);
            dc.DrawEllipse(null, new Pen(strong, 2), at, 9, 9);
            dc.DrawEllipse(strong, null, at, 3, 3);
            string now = live.Percent switch { null => "BIOS", 0 => "off", { } p => $"{p:0} %" };
            // near the right edge the label goes to the left of the ring
            bool left = at.X > plot.Right - 190;
            var anchor = left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            double x = left ? at.X - 14 : at.X + 14;
            // at the bottom both lines go above the ring, clear of the axis
            bool low = percent < 12;
            Label(dc, $"now {live.Temperature:0} °C · {now}", new Point(x, low ? at.Y - 34 : at.Y - 14), strong, anchor, size: 12, bold: true);
            if (live.Note is { } note)
                Label(dc, note, new Point(x, low ? at.Y - 18 : at.Y + 2), text, anchor, size: 11);
        }

        // the points to drag
        for (int i = 0; i < _curve.Count; i++)
        {
            var c = new Point(X(_curve[i].Temperature), Y(_curve[i].Percent));
            double r = i == _hover || i == _drag ? 8 : 6;
            dc.DrawEllipse(color, new Pen(surface, 2), c, r, r);
            if (i == _hover || i == _drag)
                Label(dc, $"{_curve[i].Temperature:0} °C · {_curve[i].Percent:0} %", new Point(c.X, c.Y - 20), strong, HorizontalAlignment.Center, size: 12, bold: true);
        }
    }

    /// <summary>The curve as the controller runs it: flat before the first point and after the last.</summary>
    private StreamGeometry Line(IReadOnlyList<CurvePoint> curve, bool close = false)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var points = new List<Point> { new(X(MinT), Y(curve[0].Percent)) };
            points.AddRange(curve.Select(c => new Point(X(c.Temperature), Y(c.Percent))));
            points.Add(new Point(X(MaxT), Y(curve[^1].Percent)));
            if (close)
            {
                ctx.BeginFigure(new Point(X(MinT), Y(0)), isFilled: true, isClosed: true);
                ctx.PolyLineTo(points, isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(X(MaxT), Y(0)), isStroked: false, isSmoothJoin: false);
            }
            else
            {
                ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
                ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void Label(DrawingContext dc, string s, Point at, Brush brush, HorizontalAlignment align, double size = 12, bool bold = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double x = align switch
        {
            HorizontalAlignment.Right => at.X - ft.Width,
            HorizontalAlignment.Center => at.X - ft.Width / 2,
            _ => at.X,
        };
        dc.DrawText(ft, new Point(x, at.Y - ft.Height / 2));
    }

    private Brush Theme(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    // ── editing ────────────────────────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this);
        int hit = HitTest(at);
        if (e.ClickCount == 2 && hit < 0 && Plot.Contains(at))
        {
            _curve.Add(new CurvePoint(Math.Round(T(at.X)), Math.Round(P(at.Y))));
            Commit();
            return;
        }
        if (hit >= 0)
        {
            _drag = hit;
            CaptureMouse();
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_drag >= 0)
        {
            // stays between its neighbours: temperatures keep rising, speeds never fall
            double lowT = _drag > 0 ? _curve[_drag - 1].Temperature + 1 : MinT;
            double highT = _drag < _curve.Count - 1 ? _curve[_drag + 1].Temperature - 1 : MaxT;
            double lowP = _drag > 0 ? _curve[_drag - 1].Percent : 0;
            double highP = _drag < _curve.Count - 1 ? _curve[_drag + 1].Percent : 100;
            _curve[_drag] = new CurvePoint(
                Math.Round(Math.Clamp(T(at.X), lowT, Math.Max(lowT, highT))),
                Math.Round(Math.Clamp(P(at.Y), lowP, Math.Max(lowP, highP))));
            InvalidateVisual();
            return;
        }

        int hover = HitTest(at);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0)
            return;
        _drag = -1;
        ReleaseMouseCapture();
        Commit();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        int hit = HitTest(e.GetPosition(this));
        if (hit >= 0 && _curve.Count > 2)
        {
            _curve.RemoveAt(hit);
            _hover = -1;
            Commit();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover >= 0 && _drag < 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    private void Commit()
    {
        var normalized = CurveOverrides.Normalized(new CurveOverride(_curve, false)).Curve;
        _curve = normalized.ToList();
        InvalidateVisual();
        CurveEdited?.Invoke(normalized);
    }
}
