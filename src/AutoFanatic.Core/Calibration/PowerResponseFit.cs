namespace AutoFanatic.Core.Calibration;

/// <param name="Resistance">°C per watt above room temperature once settled: the cooling value of this fan setting.</param>
/// <param name="Tau">Time constant of the slow part (the heatsink) in seconds.</param>
/// <param name="Rms">Typical deviation of the samples from the fitted curve, in °C.</param>
/// <param name="Reliable">True if enough of the slow part was seen for the result to be trusted.</param>
public sealed record PowerFit(double Resistance, double Tau, double Rms, bool Reliable);

/// <summary>
/// How a temperature follows the power, second by second, for one fan setting. A game's power
/// jumps all the time, so "wait until it settles" doesn't work; instead the whole history is used:
///
///   T = T_room + R_fast · P(now) + R_slow · (P smoothed with time constant τ) + start transient
///
/// The fast part is the chip reacting to its own power at once, the slow part the heatsink warming
/// up. R_fast + R_slow is the thermal resistance of the setting, whatever the load did meanwhile.
/// With a steady load this reduces to the plain exponential of <see cref="StepResponseFit"/>.
/// </summary>
public static class PowerResponseFit
{
    /// <param name="samples">Seconds since the fan change, temperature, power of the part that heats it.</param>
    public static PowerFit? Fit(IReadOnlyList<(double Seconds, double Temp, double Power)> samples, double ambient, double minTau = 5, double maxTau = 400)
    {
        if (samples.Count < 10)
            return null;

        double span = samples[^1].Seconds - samples[0].Seconds;
        double meanPower = samples.Average(s => s.Power);
        if (span <= 0 || meanPower < Analysis.ThermalResistance.MinPowerWatts)
            return null;

        (double R, double Tau, double Sse)? best = null;
        const int steps = 80;
        for (int i = 0; i < steps; i++)
        {
            double tau = minTau * Math.Pow(maxTau / minTau, i / (double)(steps - 1));
            if (FitFor(samples, ambient, tau) is { } fit && (best is null || fit.Sse < best.Value.Sse))
                best = (fit.R, tau, fit.Sse);
        }
        if (best is not { } b)
            return null;

        double rms = Math.Sqrt(b.Sse / samples.Count);
        bool reliable = b.Tau < maxTau * 0.95 && span >= b.Tau;
        return new PowerFit(b.R, b.Tau, rms, reliable);
    }

    // For a fixed τ the model is linear in (start transient, R_slow, R_fast): least squares with R ≥ 0.
    private static (double R, double Sse)? FitFor(IReadOnlyList<(double Seconds, double Temp, double Power)> samples, double ambient, double tau)
    {
        var x = new List<double[]>(samples.Count);
        var y = new List<double>(samples.Count);

        // heatsink part: dH/dt = (R_slow·P − H) / τ, so H = H0·decay + R_slow·filtered, with the
        // power filtered from the moment of the change (filtered starts at 0)
        double decay = 1, filtered = 0;
        for (int k = 0; k < samples.Count; k++)
        {
            if (k > 0)
            {
                double a = Math.Exp(-(samples[k].Seconds - samples[k - 1].Seconds) / tau);
                decay *= a;
                filtered = a * filtered + (1 - a) * samples[k - 1].Power;
            }
            x.Add([decay, filtered, samples[k].Power]);
            y.Add(samples[k].Temp - ambient);
        }

        var beta = SignedFirst(x, y);
        if (beta is null)
            return null;

        double sse = 0;
        for (int k = 0; k < x.Count; k++)
        {
            double r = y[k] - (beta[0] * x[k][0] + beta[1] * x[k][1] + beta[2] * x[k][2]);
            sse += r * r;
        }
        return (beta[1] + beta[2], sse);
    }

    /// <summary>
    /// The start transient may be either sign (the temperature can be rising or falling at the
    /// change); the two resistances can't be negative. Tries the full fit, then each resistance
    /// alone when one comes out negative (with a steady load the two can't be told apart anyway).
    /// </summary>
    private static double[]? SignedFirst(List<double[]> x, List<double> y)
    {
        foreach (var columns in new[] { new[] { 0, 1, 2 }, new[] { 0, 1 }, new[] { 0, 2 } })
        {
            var solution = LeastSquares(x, y, columns);
            if (solution is null)
                continue;
            var beta = new double[3];
            for (int i = 0; i < columns.Length; i++)
                beta[columns[i]] = solution[i];
            if (beta[1] >= 0 && beta[2] >= 0)
                return beta;
        }
        return null;
    }

    private static double[]? LeastSquares(List<double[]> x, List<double> y, int[] columns)
    {
        int m = columns.Length;
        var a = new double[m, m + 1];
        for (int r = 0; r < x.Count; r++)
        {
            for (int i = 0; i < m; i++)
            {
                double xi = x[r][columns[i]];
                for (int j = 0; j < m; j++)
                    a[i, j] += xi * x[r][columns[j]];
                a[i, m] += xi * y[r];
            }
        }
        // a small ridge relative to each column's size keeps collinear columns (steady load) solvable
        for (int i = 0; i < m; i++)
            a[i, i] += 1e-9 * a[i, i] + 1e-12;

        for (int col = 0; col < m; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < m; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                    pivot = r;
            for (int j = 0; j <= m; j++)
                (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);

            double d = a[col, col];
            if (Math.Abs(d) < 1e-15)
                return null;
            for (int j = col; j <= m; j++)
                a[col, j] /= d;
            for (int r = 0; r < m; r++)
            {
                if (r == col || a[r, col] == 0)
                    continue;
                double f = a[r, col];
                for (int j = col; j <= m; j++)
                    a[r, j] -= f * a[col, j];
            }
        }

        var solution = new double[m];
        for (int i = 0; i < m; i++)
            solution[i] = a[i, m];
        return solution;
    }
}
