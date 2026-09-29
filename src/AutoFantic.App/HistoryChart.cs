using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AutoFantic.Core.Monitoring;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AutoFantic.App;

/// <summary>One line of a <see cref="HistoryChart"/>.</summary>
internal sealed record ChartLine(string Name, Color Color, IReadOnlyList<HistoryPoint> Points);

/// <summary>
/// The monitor's charts: a line per series (the average), a faint band from the lowest to the
/// highest value where points stand for longer stretches, and the values under the mouse. A gap
/// in the data (the PC was off) is a gap in the line.
/// </summary>
internal sealed class HistoryChart : FrameworkElement
{
    private const double PadLeft = 52, PadRight = 14, PadTop = 30, PadBottom = 26;

    private IReadOnlyList<ChartLine> _lines = [];
    private DateTimeOffset _from, _to;
    private string _unit = "";
    private double? _fixedMin, _fixedMax;
    private double? _hoverX;
    private string _format = "0"; // one decimal when the scale is finer than 1 (the cooling health's few °C)

    public HistoryChart()
    {
        MouseMove += (_, e) =>
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            _hoverX = null;
            InvalidateVisual();
        };
        Cursor = System.Windows.Input.Cursors.Cross;
        ThemeRedraw.Follow(this);
    }

    /// <param name="min">Fixed bottom of the scale (0 for power and fans); null = from the data.</param>
    /// <param name="max">Fixed top (100 for %); null = from the data.</param>
    public void Show(IReadOnlyList<ChartLine> lines, DateTimeOffset from, DateTimeOffset to, string unit, double? min = null, double? max = null)
    {
        _lines = lines;
        _from = from;
        _to = to;
        _unit = unit;
        _fixedMin = min;
        _fixedMax = max;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // so the mouse is seen everywhere
        var plot = new Rect(PadLeft, PadTop, Math.Max(10, ActualWidth - PadLeft - PadRight), Math.Max(10, ActualHeight - PadTop - PadBottom));
        var text = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var primary = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Black;
        var grid = new Pen(TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), 1);

        var all = _lines.SelectMany(l => l.Points).ToList();
        if (all.Count == 0)
        {
            var empty = Text("No data for this time yet.", text);
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + plot.Height / 2 - empty.Height / 2));
            return;
        }

        // the scale: round steps, a little room above and below
        double low = _fixedMin ?? all.Min(p => p.Min), high = _fixedMax ?? all.Max(p => p.Max);
        double step = NiceStep((high - low) / 4);
        _format = step < 1 ? "0.0" : "0";
        double bottom = _fixedMin ?? Math.Floor((low - step * 0.3) / step) * step;
        double top = _fixedMax ?? Math.Ceiling((high + step * 0.3) / step) * step;
        if (top <= bottom)
            top = bottom + step;
        double span = Math.Max(1, (_to - _from).TotalSeconds);
        double X(DateTimeOffset t) => plot.Left + plot.Width * (t - _from).TotalSeconds / span;
        double Y(double v) => plot.Bottom - plot.Height * (Math.Clamp(v, bottom, top) - bottom) / (top - bottom);

        for (double v = bottom; v <= top + step / 1000; v += step)
        {
            dc.DrawLine(grid, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            Label(dc, $"{Value(v)} {_unit}", new Point(plot.Left - 6, Y(v)), text, right: true);
        }
        foreach (var (time, label) in TimeTicks())
        {
            double x = X(time);
            dc.DrawLine(grid, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var ft = Text(label, text);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width), plot.Bottom + 6));
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var line in _lines)
            Draw(dc, line, X, Y);
        dc.Pop();

        // legend: the latest value of each line
        double legendX = plot.Left;
        foreach (var line in _lines)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(SeriesColors.For(line.Color, this)), null, new Rect(legendX, 8, 12, 12), 3, 3);
            string last = line.Points.Count > 0 ? $" {Value(line.Points[^1].Avg)} {_unit}" : "";
            var ft = Text(line.Name + last, primary);
            dc.DrawText(ft, new Point(legendX + 17, 14 - ft.Height / 2));
            legendX += 17 + ft.Width + 18;
        }

        if (_hoverX is { } hx && hx >= plot.Left && hx <= plot.Right)
            Hover(dc, plot, hx, span, primary);
    }

    private void Draw(DrawingContext dc, ChartLine line, Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        if (line.Points.Count == 0)
            return;
        var color = SeriesColors.For(line.Color, this);
        var brush = new SolidColorBrush(color);
        var band = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B));
        var pen = new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round };

        foreach (var piece in Pieces(line.Points))
        {
            if (piece.Count == 1)
            {
                dc.DrawEllipse(brush, null, new Point(x(piece[0].Time), y(piece[0].Avg)), 2, 2);
                continue;
            }
            if (piece.Any(p => p.Max - p.Min > 0.01))
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(x(piece[0].Time), y(piece[0].Max)), true, true);
                    ctx.PolyLineTo([.. piece.Skip(1).Select(p => new Point(x(p.Time), y(p.Max))), .. piece.AsEnumerable().Reverse().Select(p => new Point(x(p.Time), y(p.Min)))], false, false);
                }
                area.Freeze();
                dc.DrawGeometry(band, null, area);
            }
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x(piece[0].Time), y(piece[0].Avg)), false, false);
                ctx.PolyLineTo([.. piece.Skip(1).Select(p => new Point(x(p.Time), y(p.Avg)))], true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>The points split where there is a gap (the PC was off, AutoFantic wasn't running).</summary>
    private static List<List<HistoryPoint>> Pieces(IReadOnlyList<HistoryPoint> points)
    {
        var steps = points.Zip(points.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).Order().ToList();
        double usual = steps.Count > 0 ? steps[steps.Count / 2] : 5;
        var pieces = new List<List<HistoryPoint>> { new() { points[0] } };
        for (int i = 1; i < points.Count; i++)
        {
            if ((points[i].Time - points[i - 1].Time).TotalSeconds > usual * 3 + 1)
                pieces.Add([]);
            pieces[^1].Add(points[i]);
        }
        return pieces;
    }

    private void Hover(DrawingContext dc, Rect plot, double hx, double span, Brush primary)
    {
        var time = _from + TimeSpan.FromSeconds((hx - plot.Left) / plot.Width * span);
        var secondary = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        // one row per line: its name, the value (right-aligned, so the numbers line up) and, if the
        // point stands for a stretch that varied, its lowest to highest in grey
        var rows = new List<(Color Color, FormattedText Name, FormattedText Value, FormattedText? Range)>();
        foreach (var line in _lines)
        {
            if (line.Points.Count == 0)
                continue;
            var nearest = line.Points.MinBy(p => Math.Abs((p.Time - time).TotalSeconds));
            if (Math.Abs((nearest.Time - time).TotalSeconds) > span / 50 + 10)
                continue;
            var range = nearest.Max - nearest.Min > 0.5 ? Text($"{Value(nearest.Min)}–{Value(nearest.Max)}", secondary) : null;
            rows.Add((SeriesColors.For(line.Color, this), Text(line.Name, primary), Text($"{Value(nearest.Avg)} {_unit}", primary, bold: true), range));
        }
        if (rows.Count == 0)
            return;

        var guide = new Pen(secondary, 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(guide, new Point(hx, plot.Top), new Point(hx, plot.Bottom));

        const double pad = 10, swatch = 14, gap = 16, rowGap = 3;
        var header = Text(time.ToLocalTime().ToString(span > 86400 ? "dd.MM. HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture), secondary);
        double names = rows.Max(r => r.Name.Width), values = rows.Max(r => r.Value.Width);
        double ranges = rows.Max(r => r.Range?.Width ?? 0);
        double width = pad + Math.Max(header.Width, swatch + names + gap + values + (ranges > 0 ? 10 + ranges : 0)) + pad;
        double height = pad + header.Height + 4 + rows.Sum(r => r.Name.Height + rowGap) - rowGap + pad;
        double left = hx + 12 + width > plot.Right ? hx - 12 - width : hx + 12;
        var box = new Rect(left, plot.Top + 4, width, height);
        var background = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
        dc.DrawRoundedRectangle(background, new Pen(TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.Gray, 1), box, 6, 6);
        double y = box.Top + pad;
        dc.DrawText(header, new Point(box.Left + pad, y));
        y += header.Height + 4;
        double valueRight = box.Left + pad + swatch + names + gap + values;
        foreach (var (color, name, value, range) in rows)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(box.Left + pad, y + name.Height / 2 - 4, 8, 8), 2, 2);
            dc.DrawText(name, new Point(box.Left + pad + swatch, y));
            dc.DrawText(value, new Point(valueRight - value.Width, y));
            if (range is not null)
                dc.DrawText(range, new Point(valueRight + 10, y));
            y += name.Height + rowGap;
        }
    }

    /// <summary>About five labels along the time axis, at round times; months for 100 days and more.</summary>
    private IEnumerable<(DateTimeOffset, string)> TimeTicks()
    {
        double span = (_to - _from).TotalSeconds;
        if (span >= 100 * 86400)
        {
            // the 1st of every month, or of every 2nd or 3rd (Jan, Mar, May …), with the year at January and at the start
            int every = span <= 200 * 86400 ? 1 : span <= 400 * 86400 ? 2 : 3;
            var start = _from.LocalDateTime;
            var month = new DateTime(start.Year, start.Month, 1).AddMonths(1);
            while ((month.Month - 1) % every != 0)
                month = month.AddMonths(1);
            for (bool opening = true; month <= _to.LocalDateTime; month = month.AddMonths(every), opening = false)
                yield return (new DateTimeOffset(month), month.ToString(opening || month.Month == 1 ? "MMM yyyy" : "MMM", CultureInfo.InvariantCulture));
            yield break;
        }
        double[] steps = [60, 300, 600, 1800, 3600, 3 * 3600, 6 * 3600, 12 * 3600, 86400, 2 * 86400, 7 * 86400, 14 * 86400];
        double step = steps.FirstOrDefault(s => span / s <= 6, steps[^1]);
        string format = step >= 86400 ? "dd.MM." : "HH:mm";
        var local = _from.ToLocalTime();
        double offset = local.Offset.TotalSeconds;
        double first = Math.Ceiling((_from.ToUnixTimeSeconds() + offset) / step) * step - offset;
        for (double t = first; t <= _to.ToUnixTimeSeconds(); t += step)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds((long)t);
            yield return (time, time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture));
        }
    }

    private string Value(double v)
    {
        string text = v.ToString(_format, CultureInfo.CurrentCulture);
        return text.StartsWith('-') && text.Trim('-', '0', '.', ',').Length == 0 ? text[1..] : text; // not "-0"
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0)
            return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / magnitude;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * magnitude;
    }

    private void Label(DrawingContext dc, string s, Point at, Brush brush, bool right)
    {
        var ft = Text(s, brush);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : at.X, at.Y - ft.Height / 2));
    }

    private FormattedText Text(string s, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
