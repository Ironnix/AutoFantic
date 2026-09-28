namespace AutoFanatic.Core.Calibration;

/// <summary>The temperatures the calibration models; each is driven by the CPU's or the GPU's power.</summary>
public enum Component
{
    Cpu,
    GpuCore,
    GpuHotspot,
    GpuMemory,
}

/// <summary>One calibration run: the fan speeds that were set and where each temperature settled.</summary>
/// <param name="Speeds">Effective speed per fan group in % (0 for a fan that stood still).</param>
/// <param name="Ambient">Room temperature during this run; null = the model's.</param>
/// <param name="Weight">How much this run counts (1 = a real measurement).</param>
public sealed record Observation(
    IReadOnlyList<double> Speeds,
    double CpuPower,
    double GpuPower,
    IReadOnlyDictionary<Component, double> Final,
    double? Ambient = null,
    double Weight = 1);

/// <summary>
/// "How it learns", compressed: for each temperature,
///   T = T_room + P · (r0 + Σ k_g / (speed_g + 10))
/// P is the power of the part that heats it (CPU or GPU). More airflow lowers the thermal
/// resistance with diminishing returns, which is exactly what makes a knee. Every k is ≥ 0: more
/// fan never makes anything hotter. A group with k ≈ 0 simply doesn't cool that part.
/// </summary>
public sealed class ThermalModel
{
    private const double Offset = 10;

    private readonly Dictionary<Component, double[]> _coefficients; // [r0, k_1 … k_G]

    private ThermalModel(double ambient, int groups, Dictionary<Component, double[]> coefficients, Dictionary<Component, double> rms)
    {
        Ambient = ambient;
        GroupCount = groups;
        _coefficients = coefficients;
        Rms = rms;
    }

    public double Ambient { get; }

    public int GroupCount { get; }

    public IEnumerable<Component> Components => _coefficients.Keys;

    /// <summary>How far the model is off from the measured runs, in °C.</summary>
    public IReadOnlyDictionary<Component, double> Rms { get; }

    public static bool IsCpu(Component c) => c == Component.Cpu;

    public static double Basis(double speed) => 1 / (Math.Max(0, speed) + Offset);

    public IReadOnlyList<double> Coefficients(Component component) => _coefficients[component];

    public double Predict(Component component, IReadOnlyList<double> speeds, double cpuPower, double gpuPower)
    {
        var c = _coefficients[component];
        double r = c[0];
        for (int g = 0; g < GroupCount; g++)
            r += c[g + 1] * Basis(speeds[g]);
        return Ambient + (IsCpu(component) ? cpuPower : gpuPower) * r;
    }

    /// <summary>
    /// Fits every component that has a value in enough observations. Runs from different
    /// calibrations (a GPU-heavy game, a CPU render …) fit together: each run is weighted by the
    /// power of the part that heats that temperature, so the CPU is learned mostly from the runs
    /// where the CPU really worked, and the GPU from those where the GPU did.
    /// </summary>
    public static ThermalModel Fit(IReadOnlyList<Observation> observations, double ambient, int groups)
    {
        var coefficients = new Dictionary<Component, double[]>();
        var rms = new Dictionary<Component, double>();

        foreach (var component in Enum.GetValues<Component>())
        {
            var rows = observations
                .Where(o => o.Final.ContainsKey(component))
                .Select(o => (o, Power: IsCpu(component) ? o.CpuPower : o.GpuPower))
                .Where(x => x.Power >= 10)
                .ToList();
            if (rows.Count < 2)
                continue;

            // y = (T − T_room) / P  =  r0 + Σ k_g · basis(speed_g), fitted in °C: rows scaled by P·√weight
            var x = rows.Select(r => new[] { 1.0 }.Concat(r.o.Speeds.Select(Basis)).ToArray()).ToList();
            var y = rows.Select(r => (r.o.Final[component] - (r.o.Ambient ?? ambient)) / r.Power).ToList();
            var scale = rows.Select(r => r.Power * Math.Sqrt(r.o.Weight)).ToList();
            var beta = NonNegativeLeastSquares(
                x.Select((row, i) => row.Select(v => v * scale[i]).ToArray()).ToList(),
                y.Select((v, i) => v * scale[i]).ToList());

            double sse = 0, weights = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                double predicted = (rows[i].o.Ambient ?? ambient) + rows[i].Power * x[i].Zip(beta, (a, b) => a * b).Sum();
                sse += rows[i].o.Weight * Math.Pow(predicted - rows[i].o.Final[component], 2);
                weights += rows[i].o.Weight;
            }

            coefficients[component] = beta;
            rms[component] = Math.Sqrt(sse / weights);
        }
        return new ThermalModel(ambient, groups, coefficients, rms);
    }

    /// <summary>
    /// Least squares with every coefficient ≥ 0, for the few parameters here: solve, drop the most
    /// negative coefficient (pin it to 0), solve again, until none is negative.
    /// </summary>
    internal static double[] NonNegativeLeastSquares(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        int p = x[0].Length;
        var active = Enumerable.Range(0, p).ToList();
        var result = new double[p];

        while (active.Count > 0)
        {
            var solution = Solve(x, y, active);
            int worst = -1;
            for (int i = 0; i < active.Count; i++)
                if (solution[i] < 0 && (worst < 0 || solution[i] < solution[worst]))
                    worst = i;

            if (worst < 0)
            {
                Array.Clear(result);
                for (int i = 0; i < active.Count; i++)
                    result[active[i]] = solution[i];
                return result;
            }
            active.RemoveAt(worst);
        }
        return result;
    }

    // normal equations with a tiny ridge, so a column that never varied can't make it singular
    private static double[] Solve(IReadOnlyList<double[]> x, IReadOnlyList<double> y, List<int> columns)
    {
        int m = columns.Count;
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
        for (int i = 0; i < m; i++)
            a[i, i] += 1e-12;

        // Gauss-Jordan with partial pivoting
        for (int col = 0; col < m; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < m; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                    pivot = r;
            for (int j = 0; j <= m; j++)
                (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);

            double d = a[col, col];
            if (Math.Abs(d) < 1e-18)
                continue;
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
