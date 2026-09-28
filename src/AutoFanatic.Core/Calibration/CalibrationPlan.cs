namespace AutoFanatic.Core.Calibration;

/// <summary>
/// Which fan speeds to try, and in what order. Instead of changing one group at a time (slow),
/// every run changes several groups at once following a Taguchi L9 array: 9 runs, up to 4 groups,
/// 3 speeds each, arranged so that every group's effect can still be told apart afterwards.
/// Runs go from cool (fans fast) to warm, so a run that gets too hot comes last.
/// </summary>
public static class CalibrationPlan
{
    public const double High = 100;
    public const double Middle = 65;
    public const double LowForMainboardFans = 35;

    // Taguchi L9 (3^4): each column is balanced, and any two columns contain every pair of levels once
    private static readonly int[,] L9 =
    {
        { 0, 0, 0, 0 },
        { 0, 1, 1, 1 },
        { 0, 2, 2, 2 },
        { 1, 0, 1, 2 },
        { 1, 1, 2, 0 },
        { 1, 2, 0, 1 },
        { 2, 0, 2, 1 },
        { 2, 1, 0, 2 },
        { 2, 2, 1, 0 },
    };

    public const int MaxGroups = 4;

    /// <summary>The three speeds tried for a group: 100 %, 65 % and its lowest speed that still spins.</summary>
    public static double[] Levels(FanGroup group) =>
        [High, Middle, Math.Max(LowForMainboardFans, group.MinSpinning)];

    /// <summary>
    /// The runs: speed per group. With more than <see cref="MaxGroups"/> groups, the extra ones
    /// follow the last column (they move together and can't be told apart).
    /// </summary>
    public static IReadOnlyList<double[]> Runs(IReadOnlyList<FanGroup> groups)
    {
        if (groups.Count == 0)
            return [];

        var levels = groups.Select(Levels).ToList();
        var runs = new List<(double[] Speeds, int Heat)>();
        var seen = new HashSet<string>();
        for (int row = 0; row < 9; row++)
        {
            var speeds = new double[groups.Count];
            int heat = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                int level = L9[row, Math.Min(g, MaxGroups - 1)];
                speeds[g] = levels[g][level];
                heat += level;
            }
            // fewer groups than columns repeat combinations: keep each once
            if (seen.Add(string.Join(",", speeds)))
                runs.Add((speeds, heat));
        }

        return runs.OrderBy(r => r.Heat).Select(r => r.Speeds).ToList();
    }
}
