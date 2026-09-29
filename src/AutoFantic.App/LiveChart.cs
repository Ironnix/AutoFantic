using System.Globalization;
using System.Windows;
using System.Windows.Media;
using static AutoFantic.Core.Texts;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace AutoFantic.App;

/// <summary>CPU and GPU temperature over the last few minutes of a calibration, with a legend.</summary>
internal sealed class LiveChart : FrameworkElement
{
    private const double PadLeft = 44, PadRight = 12, PadTop = 26, PadBottom = 24;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static readonly Color CpuColor = Color.FromRgb(0x2a, 0x78, 0xd6);
    public static readonly Color GpuColor = Color.FromRgb(0xeb, 0x68, 0x34);

    private readonly List<(DateTime Time, double? Cpu, double? Gpu)> _points = [];

    public LiveChart() => ThemeRedraw.Follow(this);

    public void Add(double? cpu, double? gpu)
    {
        var now = DateTime.Now;
        _points.Add((now, cpu, gpu));
        _points.RemoveAll(p => now - p.Time > Window);
        InvalidateVisual();
    }

    public void Clear()
    {
        _points.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var plot = new Rect(PadLeft, PadTop, Math.Max(10, ActualWidth - PadLeft - PadRight), Math.Max(10, ActualHeight - PadTop - PadBottom));
        var text = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var grid = new Pen(TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // y from 20 °C, up to a bit above the hottest value (at least 80 °C)
        double top = Math.Max(80, Math.Ceiling((_points.SelectMany(p => new[] { p.Cpu ?? 0, p.Gpu ?? 0 }).DefaultIfEmpty(0).Max() + 5) / 10) * 10);
        const double bottom = 20;
        double Y(double t) => plot.Bottom - plot.Height * (Math.Clamp(t, bottom, top) - bottom) / (top - bottom);
        var now = DateTime.Now;
        double X(DateTime t) => plot.Right - plot.Width * (now - t).TotalSeconds / Window.TotalSeconds;

        for (double t = bottom; t <= top; t += 20)
        {
            dc.DrawLine(grid, new Point(plot.Left, Y(t)), new Point(plot.Right, Y(t)));
            Label(dc, $"{t:0} °C", new Point(plot.Left - 6, Y(t)), text, HorizontalAlignment.Right);
        }
        Label(dc, T("5 min ago"), new Point(plot.Left, plot.Bottom + 12), text, HorizontalAlignment.Left);
        Label(dc, T("now"), new Point(plot.Right, plot.Bottom + 12), text, HorizontalAlignment.Right);

        Color cpu = SeriesColors.For(CpuColor, this), gpu = SeriesColors.For(GpuColor, this);
        Series(dc, p => p.Cpu, cpu, X, Y);
        Series(dc, p => p.Gpu, gpu, X, Y);

        // legend with the latest values
        var last = _points.LastOrDefault();
        double x = plot.Left;
        foreach (var (name, color, value) in new[] { ("CPU", cpu, last.Cpu), ("GPU", gpu, last.Gpu) })
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(x, 6, 12, 12), 3, 3);
            var ft = Text($"{name} {(value is { } v ? $"{v:0} °C" : "–")}", TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Black, bold: true);
            dc.DrawText(ft, new Point(x + 18, 12 - ft.Height / 2));
            x += 18 + ft.Width + 20;
        }
    }

    private void Series(DrawingContext dc, Func<(DateTime Time, double? Cpu, double? Gpu), double?> value, Color color,
        Func<DateTime, double> x, Func<double, double> y)
    {
        var points = _points.Where(p => value(p) is not null).Select(p => new Point(x(p.Time), y(value(p)!.Value))).ToList();
        if (points.Count < 2)
            return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private void Label(DrawingContext dc, string s, Point at, Brush brush, HorizontalAlignment align)
    {
        var ft = Text(s, brush);
        double left = align == HorizontalAlignment.Right ? at.X - ft.Width : at.X;
        dc.DrawText(ft, new Point(left, at.Y - ft.Height / 2));
    }

    private FormattedText Text(string s, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
