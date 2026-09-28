namespace AutoFantic.Core.Calibration;

/// <param name="Final">Where the value is heading (the steady state), extrapolated from the curve so far.</param>
/// <param name="Tau">Time constant in seconds: after Tau, 63 % of the change is done.</param>
/// <param name="Rms">Typical deviation of the samples from the fitted curve.</param>
/// <param name="Reliable">True if enough of the change has happened for the extrapolation to be trusted.</param>
public sealed record StepFit(double Final, double Tau, double Rms, bool Reliable);

/// <summary>
/// After a fan change, a temperature moves towards its new steady state along an exponential
/// curve: T(t) = Final + A·e^(−t/τ). Fitting that curve lets the calibration predict the final
/// temperature after about one time constant instead of waiting 4–5 of them, which is what makes a
/// calibration in about ten minutes possible.
/// </summary>
public static class StepResponseFit
{
    /// <summary>A change smaller than this is "no change": any time constant fits, the value is simply the mean.</summary>
    private const double FlatBelow = 0.4;

    /// <param name="samples">Seconds since the change, and the value.</param>
    /// <param name="maxTau">Longest time constant to consider; bounds how far the fit may extrapolate.</param>
    public static StepFit? Fit(IReadOnlyList<(double Seconds, double Value)> samples, double minTau = 3, double maxTau = 400)
    {
        if (samples.Count < 8)
            return null;

        double span = samples[^1].Seconds - samples[0].Seconds;
        if (span <= 0)
            return null;

        (double Final, double A, double Tau, double Sse)? best = null;
        const int steps = 90;
        for (int i = 0; i < steps; i++)
        {
            double tau = minTau * Math.Pow(maxTau / minTau, i / (double)(steps - 1));
            if (FitFor(samples, tau) is { } fit && (best is null || fit.Sse < best.Value.Sse))
                best = (fit.Final, fit.A, tau, fit.Sse);
        }
        if (best is not { } b)
            return null;

        double rms = Math.Sqrt(b.Sse / samples.Count);

        // A "change" no bigger than the sensor noise is noise the curve has been bent to follow
        // (a CPU sensor easily jumps ±1 °C): then the recent average is the honest answer.
        if (Math.Abs(b.A) < Math.Max(FlatBelow, 3 * rms))
        {
            double recent = samples.Where(s => s.Seconds >= samples[0].Seconds + span / 2).Average(s => s.Value);
            return new StepFit(recent, b.Tau, rms, Reliable: true);
        }

        // The change still to come is A·e^(−span/τ). Trusted when most of it has happened.
        double remaining = Math.Abs(b.A) * Math.Exp(-span / b.Tau);
        bool reliable = b.Tau < maxTau * 0.95 && remaining <= Math.Max(1.0, 0.35 * Math.Abs(b.A));
        return new StepFit(b.Final, b.Tau, rms, reliable);
    }

    // For a fixed τ the model is linear in Final and A: ordinary least squares.
    private static (double Final, double A, double Sse)? FitFor(IReadOnlyList<(double Seconds, double Value)> samples, double tau)
    {
        double n = samples.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, y) in samples)
        {
            double x = Math.Exp(-t / tau);
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        double denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-12)
            return null;

        double a = (n * sxy - sx * sy) / denominator;
        double final = (sy - a * sx) / n;

        double sse = 0;
        foreach (var (t, y) in samples)
        {
            double r = y - (final + a * Math.Exp(-t / tau));
            sse += r * r;
        }
        return (final, a, sse);
    }
}
