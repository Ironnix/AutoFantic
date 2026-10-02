using System.Globalization;
using System.Net;
using System.Text;
using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Reports;

/// <summary>
/// The calibration result as a page to look at (runs\calibration.html): what every fan does at each
/// load level, including where it is off, each fan's curve, and the numbers for the BIOS and for
/// MSI Afterburner. One self-contained file, light and dark mode, no scripts from the internet.
/// </summary>
public static class CalibrationPage
{
    // categorical slots from the validated reference palette (first four validated for adjacent use)
    private static readonly string[] Light = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"];
    private static readonly string[] Dark = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    private const int W = 720, H = 300, Left = 48, Right = 150, Top = 20, Bottom = 56;

    /// <param name="measuredRuns">Real runs behind the result (imported ones don't count): until there are some, the model's accuracy isn't known.</param>
    public static void Write(string path, CalibrationResult result, IReadOnlyList<FanGroup> groups, FansOffResult? fansOff, int measuredRuns)
    {
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>AuFantic fan plan</title>
            <style>
            :root {
              color-scheme: light;
              --surface: #fcfcfb; --text: #0b0b0b; --text-2: #52514e; --grid: #e4e3df; --card: #ffffff; --border: #e4e3df;
            {{Series(Light)}}
            }
            @media (prefers-color-scheme: dark) {
              :root:not([data-theme="light"]) {
                color-scheme: dark;
                --surface: #1a1a19; --text: #ffffff; --text-2: #c3c2b7; --grid: #3a3a37; --card: #232321; --border: #3a3a37;
            {{Series(Dark)}}
              }
            }
            :root[data-theme="dark"] {
              color-scheme: dark;
              --surface: #1a1a19; --text: #ffffff; --text-2: #c3c2b7; --grid: #3a3a37; --card: #232321; --border: #3a3a37;
            {{Series(Dark)}}
            }
            body { margin: 0; background: var(--surface); color: var(--text); font: 15px/1.5 system-ui, "Segoe UI", sans-serif; }
            main { max-width: 900px; margin: 0 auto; padding: 24px 16px 48px; }
            h1 { font-size: 24px; margin: 0 0 4px; }
            h2 { font-size: 18px; margin: 32px 0 8px; }
            h3 { font-size: 15px; margin: 0 0 4px; }
            .muted { color: var(--text-2); }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 16px; margin: 12px 0; }
            .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; }
            svg { width: 100%; height: auto; display: block; }
            svg text { fill: var(--text-2); font: 12px system-ui, "Segoe UI", sans-serif; }
            svg .label { fill: var(--text); font-weight: 600; }
            .axis { stroke: var(--grid); stroke-width: 1; }
            table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
            th, td { text-align: right; padding: 4px 8px; border-bottom: 1px solid var(--border); white-space: nowrap; }
            th:first-child, td:first-child { text-align: left; }
            .table-wrap { overflow-x: auto; }
            .legend { display: flex; flex-wrap: wrap; gap: 6px 16px; margin: 4px 0 8px; font-size: 13px; }
            .swatch { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-right: 6px; vertical-align: -1px; }
            .note { border-left: 3px solid var(--grid); padding: 2px 10px; margin: 8px 0; }
            </style>
            </head>
            <body>
            <main>
            <h1>Your fan plan</h1>
            <p class="muted">{{E(result.Profile)}} · room {{result.Ambient:0}} °C · worked out {{result.Created:dd.MM.yyyy HH:mm}}</p>
            """);

        html.Append("<div class=\"card\"><h3>Built from</h3><ul>");
        foreach (var source in result.Sources ?? [])
            html.Append($"<li>{E(source)}</li>");
        html.Append("</ul></div>");

        AppendOffSummary(html, result, fansOff);

        html.Append("<h2>What your fans do at each load</h2>");
        html.Append("<p class=\"muted\">From idle to the heaviest load measured (\"high\") and 30 % beyond it. A hollow dot at 0 means the fan is off.</p>");
        html.Append("<div class=\"card\">");
        AppendLegend(html, result);
        AppendSpeedChart(html, result);
        AppendSpeedTable(html, result);
        html.Append("</div>");

        html.Append("<h2>Curves: fan speed by temperature</h2>");
        html.Append("<p class=\"muted\">What \"Use my curves\" runs. The dashed line is the temperature target; from there on the fan runs at 100 %.</p>");
        html.Append("<div class=\"grid\">");
        for (int g = 0; g < result.Groups.Count; g++)
            AppendCurveChart(html, result, g);
        html.Append("</div>");

        AppendExport(html, result);
        AppendEffects(html, result, measuredRuns);

        html.Append("</main></body></html>\n");
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
    }

    private static string Series(string[] colors) =>
        string.Join("\n", colors.Select((c, i) => $"  --series-{i + 1}: {c};"));

    private static string Color(int group) => $"var(--series-{group % Light.Length + 1})";

    private static void AppendOffSummary(StringBuilder html, CalibrationResult result, FansOffResult? fansOff)
    {
        html.Append("<h2>Are the fans off when the PC is idle?</h2><div class=\"card\"><ul>");
        var idle = result.Table[0];
        for (int g = 0; g < result.Groups.Count; g++)
        {
            var group = result.Groups[g];
            double speed = idle.Speeds[g];
            string state = speed == 0 ? "<strong>off</strong>" : $"{speed:0} %";
            html.Append($"<li><span class=\"swatch\" style=\"background:{Color(g)}\"></span>{E(group.Name)}: {state} at idle");
            if (group.OffAt.Count > 1)
                html.Append($" (also off at {E(string.Join(", ", group.OffAt.Skip(1)))} load)");
            html.Append("</li>");
        }
        html.Append("</ul>");

        if (fansOff is null)
        {
            html.Append("<p class=\"note\">No fans-off test yet: start a calibration once while the PC is idle, then fans can be switched off at idle.</p>");
        }
        else
        {
            var r = fansOff.Resistance(result.Ambient);
            double gpuOff = result.Ambient + fansOff.GpuPower * r.GetValueOrDefault(Component.GpuCore);
            double cpuOff = result.Ambient + fansOff.CpuPower * r.GetValueOrDefault(Component.Cpu);
            html.Append($"<p class=\"note\">With every fan off at idle the CPU would head for about {cpuOff:0} °C and the GPU for about {gpuOff:0} °C " +
                $"(measured {fansOff.Created:dd.MM. HH:mm}, CPU {fansOff.CpuPower:0} W, GPU {fansOff.GpuPower:0} W). " +
                $"A fan is only switched off at low load while the part it cools is at or below {Control.CurveController.OffBelow:0} °C (or where its curve is drawn down to 0 %) and nothing is above {Control.CurveController.OthersBelow:0} °C.</p>");
            if (fansOff.GpuPower >= Monitoring.IdlePower.High)
                html.Append($"<p class=\"note\">Your graphics card uses {fansOff.GpuPower:0} W while idle; 20 to 40 W would be normal. That keeps it too warm for its fans to stop. " +
                    "The usual cause is monitors running at different refresh rates: set them all to the same rate (AuFantic's Overview says more). " +
                    "Once idle power drops, the GPU fans can be off at idle too.</p>");
        }
        html.Append("</div>");
    }

    private static void AppendLegend(StringBuilder html, CalibrationResult result)
    {
        html.Append("<div class=\"legend\">");
        for (int g = 0; g < result.Groups.Count; g++)
            html.Append($"<span><span class=\"swatch\" style=\"background:{Color(g)}\"></span>{E(result.Groups[g].Name)}</span>");
        html.Append("</div>");
    }

    private static void AppendSpeedChart(StringBuilder html, CalibrationResult result)
    {
        var rows = result.Table;
        double plotW = W - Left - Right, plotH = H - Top - Bottom;
        double X(int i) => Left + plotW * i / Math.Max(1, rows.Count - 1);
        double Y(double percent) => Top + plotH * (1 - percent / 100);

        html.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Fan speed per load level\">");
        foreach (var p in new[] { 0, 25, 50, 75, 100 })
        {
            html.Append($"<line class=\"axis\" x1=\"{Left}\" x2=\"{Left + plotW}\" y1=\"{F(Y(p))}\" y2=\"{F(Y(p))}\"/>");
            html.Append($"<text x=\"{Left - 8}\" y=\"{F(Y(p) + 4)}\" text-anchor=\"end\">{p} %</text>");
        }
        for (int i = 0; i < rows.Count; i++)
        {
            html.Append($"<text x=\"{F(X(i))}\" y=\"{H - Bottom + 18}\" text-anchor=\"middle\" class=\"label\">{E(rows[i].Label)}</text>");
            html.Append($"<text x=\"{F(X(i))}\" y=\"{H - Bottom + 34}\" text-anchor=\"middle\">{rows[i].CpuPower:0} W · {rows[i].GpuPower:0} W</text>");
        }
        html.Append($"<text x=\"{Left}\" y=\"{H - 4}\">load: CPU W · GPU W</text>");

        // lines first, then markers on top, then direct labels at the right end
        var ends = new List<(double Y, int Group)>();
        for (int g = 0; g < result.Groups.Count; g++)
        {
            string points = string.Join(" ", rows.Select((r, i) => $"{F(X(i))},{F(Y(r.Speeds[g]))}"));
            html.Append($"<polyline points=\"{points}\" fill=\"none\" stroke=\"{Color(g)}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
            ends.Add((Y(rows[^1].Speeds[g]), g));
        }
        for (int g = 0; g < result.Groups.Count; g++)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                double s = rows[i].Speeds[g];
                string tip = $"{result.Groups[g].Name} · {rows[i].Label}: {(s == 0 ? "off" : $"{s:0} %")} · CPU {Temp(rows[i], Component.Cpu)} · GPU {Temp(rows[i], Component.GpuCore)}";
                string fill = s == 0 ? "var(--surface)" : Color(g);
                html.Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(s))}\" r=\"5\" fill=\"{fill}\" stroke=\"{(s == 0 ? Color(g) : "var(--surface)")}\" stroke-width=\"2\"><title>{E(tip)}</title></circle>");
            }
        }

        // direct labels, nudged apart so they don't overlap
        double last = double.NegativeInfinity;
        foreach (var (y, g) in ends.OrderBy(e => e.Y))
        {
            double ly = Math.Max(y + 4, last + 15);
            last = ly;
            html.Append($"<text x=\"{Left + plotW + 10}\" y=\"{F(ly)}\" class=\"label\">{E(Short(result.Groups[g]))}</text>");
        }
        html.Append("</svg>");
    }

    private static void AppendSpeedTable(StringBuilder html, CalibrationResult result)
    {
        html.Append("<details><summary>Show the numbers</summary><div class=\"table-wrap\"><table><thead><tr><th>load</th><th>CPU W</th><th>GPU W</th>");
        foreach (var g in result.Groups)
            html.Append($"<th>{E(Short(g))}</th>");
        html.Append("<th>CPU</th><th>GPU</th><th>hotspot</th></tr></thead><tbody>");
        foreach (var row in result.Table)
        {
            html.Append($"<tr><td>{E(row.Label)}</td><td>{row.CpuPower:0}</td><td>{row.GpuPower:0}</td>");
            foreach (var s in row.Speeds)
                html.Append($"<td>{(s == 0 ? "off" : $"{s:0} %")}</td>");
            html.Append($"<td>{Temp(row, Component.Cpu)}</td><td>{Temp(row, Component.GpuCore)}</td><td>{Temp(row, Component.GpuHotspot)}</td></tr>");
        }
        html.Append("</tbody></table></div></details>");
    }

    private static void AppendCurveChart(StringBuilder html, CalibrationResult result, int g)
    {
        var group = result.Groups[g];
        const int cw = 340, ch = 220, cl = 44, cr = 26, ct = 12, cb = 36;
        const double minT = 30, maxT = 100;
        double plotW = cw - cl - cr, plotH = ch - ct - cb;
        double X(double t) => cl + plotW * (Math.Clamp(t, minT, maxT) - minT) / (maxT - minT);
        double Y(double p) => ct + plotH * (1 - p / 100);

        string follows = CalibrationInsights.FollowsName(group.Follows);
        html.Append($"<div class=\"card\"><h3>{E(group.Name)}</h3><p class=\"muted\">follows the {follows}" +
            (group.OffAt.Count > 0 ? $"; off at {E(string.Join(" + ", group.OffAt))} load" : "") + "</p>");
        html.Append($"<svg viewBox=\"0 0 {cw} {ch}\" role=\"img\" aria-label=\"{E(group.Name)} curve\">");
        foreach (var p in new[] { 0, 50, 100 })
        {
            html.Append($"<line class=\"axis\" x1=\"{cl}\" x2=\"{cl + plotW}\" y1=\"{F(Y(p))}\" y2=\"{F(Y(p))}\"/>");
            html.Append($"<text x=\"{cl - 6}\" y=\"{F(Y(p) + 4)}\" text-anchor=\"end\">{p} %</text>");
        }
        foreach (var t in new[] { 40, 60, 80, 100 })
            html.Append($"<text x=\"{F(X(t))}\" y=\"{ch - cb + 16}\" text-anchor=\"middle\">{t} °C</text>");

        // the target: from there on the fan runs at 100 %
        if (group.Curve.Count > 0)
        {
            double target = group.Curve[^1].Temperature;
            html.Append($"<line x1=\"{F(X(target))}\" x2=\"{F(X(target))}\" y1=\"{ct}\" y2=\"{F(ct + plotH)}\" stroke=\"var(--text-2)\" stroke-width=\"1\" stroke-dasharray=\"4 4\"/>");
        }

        // flat before the first point and after the last, as the controller runs it
        var pts = new List<(double T, double P)>();
        if (group.Curve.Count > 0)
        {
            pts.Add((minT, group.Curve[0].Percent));
            pts.AddRange(group.Curve.Select(c => (c.Temperature, c.Percent)));
            pts.Add((maxT, group.Curve[^1].Percent));
        }
        html.Append($"<polyline points=\"{string.Join(" ", pts.Select(p => $"{F(X(p.T))},{F(Y(p.P))}"))}\" fill=\"none\" stroke=\"{Color(g)}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
        foreach (var c in group.Curve)
            html.Append($"<circle cx=\"{F(X(c.Temperature))}\" cy=\"{F(Y(c.Percent))}\" r=\"5\" fill=\"{Color(g)}\" stroke=\"var(--surface)\" stroke-width=\"2\"><title>{c.Temperature:0} °C → {c.Percent:0} %</title></circle>");
        html.Append("</svg>");
        html.Append($"<p class=\"muted\">{E(string.Join(" · ", group.Curve.Select(c => $"{c.Temperature:0} °C → {c.Percent:0} %")))}</p></div>");
    }

    private static void AppendExport(StringBuilder html, CalibrationResult result)
    {
        html.Append("<h2>Use the curves without AuFantic running</h2>");
        html.Append("<div class=\"card\"><h3>Mainboard fans: BIOS (e.g. MSI Smart Fan, 4 points, temperature source CPU)</h3><div class=\"table-wrap\"><table><thead><tr><th>fan</th><th>point 1</th><th>point 2</th><th>point 3</th><th>point 4</th></tr></thead><tbody>");
        foreach (var g in result.Groups.Where(g => g.Follows != Component.GpuCore))
        {
            html.Append($"<tr><td>{E(g.Name)}</td>");
            foreach (var p in CalibrationResult.BiosPoints(g.Curve))
                html.Append($"<td>{p.Temperature:0} °C · {p.Percent:0} %</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div>");
        html.Append("<p class=\"note\">A BIOS curve can't switch a fan off at low load unless it offers a 0 % point or a \"fan stop\" option, and it can only follow CPU or mainboard temperatures, not the GPU: a case fan curve that follows the warmer of CPU and GPU then runs by the CPU alone.</p></div>");

        html.Append("<div class=\"card\"><h3>Graphics card: MSI Afterburner custom fan curve (a BIOS can't control it)</h3><div class=\"table-wrap\"><table><thead><tr><th>fan</th><th>points (GPU temperature → speed)</th></tr></thead><tbody>");
        foreach (var g in result.Groups.Where(g => g.Follows == Component.GpuCore))
            html.Append($"<tr><td>{E(g.Name)}</td><td style=\"text-align:left\">{E(string.Join(" · ", g.Curve.Select(p => $"{p.Temperature:0} °C → {p.Percent:0} %")))}</td></tr>");
        html.Append("</tbody></table></div>");
        html.Append("<p class=\"note\">Afterburner has to run in the background for its curve to apply. Without it, the card uses its own automatic curve.</p></div>");
    }

    private static void AppendEffects(StringBuilder html, CalibrationResult result, int measuredRuns)
    {
        html.Append("<h2>What each fan cools</h2><div class=\"card\"><p class=\"muted\">How much warmer the CPU and the GPU get when this fan goes from 100 % to its lowest speed, at the highest load measured.</p>");
        html.Append("<div class=\"table-wrap\"><table><thead><tr><th>fan</th><th>CPU</th><th>GPU</th></tr></thead><tbody>");
        foreach (var g in result.Groups)
            html.Append($"<tr><td>{E(g.Name)}</td><td>+{g.CpuEffect:0.0} °C</td><td>+{g.GpuEffect:0.0} °C</td></tr>");
        html.Append("</tbody></table></div>");
        if (measuredRuns == 0)
            html.Append("<p class=\"muted\">How exact this is shows after your next calibration: so far it rests on an imported earlier result only.</p>");
        else if (result.Rms is { } rms)
            html.Append($"<p class=\"muted\">The model matches the measurements within {E(string.Join(", ", rms.Select(kv => $"{Name(kv.Key)} ±{kv.Value:0.0} °C")))}.</p>");
        html.Append("</div>");
    }

    private static string Short(CalibratedGroup g) =>
        g.Follows == Component.GpuCore && g.Name.StartsWith("GPU", StringComparison.Ordinal) ? "GPU fans" : g.Name.Split(" (")[0];

    private static string Temp(LoadRow row, Component c) =>
        row.Temperatures.TryGetValue(c, out double v) ? $"{v:0} °C" : "–";

    private static string Name(Component c) => c switch
    {
        Component.Cpu => "CPU",
        Component.GpuCore => "GPU",
        Component.GpuHotspot => "hotspot",
        _ => "GPU memory",
    };

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
