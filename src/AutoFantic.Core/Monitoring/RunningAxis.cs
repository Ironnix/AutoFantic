namespace AutoFantic.Core.Monitoring;

/// <summary>
/// A chart's time axis that leaves out the time nothing was recorded in (the PC was off, AutoFantic
/// wasn't running): the stretches with data follow each other, with a little room at every cut.
/// Positions run from 0 (left) to 1 (right). <see cref="Whole"/> is the plain axis without cuts.
/// </summary>
public sealed class RunningAxis
{
    // room per cut as a share of the width; many cuts share RoomForCuts, so the data keeps most of it
    private const double RoomPerCut = 0.015, RoomForCuts = 0.15;

    private readonly List<(DateTimeOffset From, DateTimeOffset To, double Before)> _stretches = [];
    private readonly double _perSecond;

    private RunningAxis(IEnumerable<(DateTimeOffset From, DateTimeOffset To)> stretches)
    {
        foreach (var (from, to) in stretches)
        {
            _stretches.Add((from, to, Seconds));
            Seconds += Math.Max(1, (to - from).TotalSeconds);
        }
        int cuts = _stretches.Count - 1;
        CutRoom = cuts > 0 ? Math.Min(RoomPerCut, RoomForCuts / cuts) : 0;
        _perSecond = (1 - CutRoom * cuts) / Seconds;
    }

    /// <summary>All the time from <paramref name="from"/> to <paramref name="to"/>, whether there is data or not.</summary>
    public static RunningAxis Whole(DateTimeOffset from, DateTimeOffset to) => new([(from, to)]);

    /// <summary>
    /// Only the stretches these points lie in. A cut is where the next point comes much later than
    /// the points usually follow each other. Without points: the whole time.
    /// </summary>
    public static RunningAxis Of(IEnumerable<DateTimeOffset> times, DateTimeOffset from, DateTimeOffset to)
    {
        var sorted = times.Distinct().Order().ToList();
        if (sorted.Count == 0)
            return Whole(from, to);

        var steps = sorted.Zip(sorted.Skip(1), (a, b) => (b - a).TotalSeconds).Order().ToList();
        double usual = steps.Count > 0 ? steps[steps.Count / 2] : 5;
        var stretches = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var start = sorted[0];
        for (int i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && (sorted[i] - sorted[i - 1]).TotalSeconds <= usual * 3 + 1)
                continue;
            stretches.Add((start, sorted[i - 1] > start ? sorted[i - 1] : start.AddSeconds(usual))); // a lone point still gets some width
            if (i < sorted.Count)
                start = sorted[i];
        }
        return new RunningAxis(stretches);
    }

    /// <summary>How many seconds the axis shows: the time with data, without what was cut out.</summary>
    public double Seconds { get; }

    /// <summary>The stretches shown, one after the other; a cut lies between two of them.</summary>
    public IEnumerable<(DateTimeOffset From, DateTimeOffset To)> Stretches => _stretches.Select(s => (s.From, s.To));

    public DateTimeOffset From => _stretches[0].From;

    public DateTimeOffset To => _stretches[^1].To;

    /// <summary>How much of the width every cut takes.</summary>
    public double CutRoom { get; }

    /// <summary>Where the cuts are: the middle of each one's room.</summary>
    public IEnumerable<double> Cuts => _stretches.Skip(1).Select((_, i) => Start(i + 1) - CutRoom / 2);

    /// <summary>Where a time lies, 0–1. A time that was cut out lies on its cut.</summary>
    public double Position(DateTimeOffset time)
    {
        for (int i = 0; i < _stretches.Count; i++)
        {
            var (from, to, _) = _stretches[i];
            if (time < from)
                return i == 0 ? 0 : Start(i) - CutRoom / 2;
            if (time <= to)
                return Start(i) + (time - from).TotalSeconds * _perSecond;
        }
        return 1;
    }

    /// <summary>The time at a position (the inverse of <see cref="Position"/>); on a cut, the end of the stretch before it.</summary>
    public DateTimeOffset TimeAt(double position)
    {
        int i = _stretches.Count - 1;
        while (i > 0 && position < Start(i))
            i--;
        var (from, to, _) = _stretches[i];
        var time = from.AddSeconds(Math.Max(0, position - Start(i)) / _perSecond);
        return time < to ? time : to;
    }

    private double Start(int stretch) => stretch * CutRoom + _stretches[stretch].Before * _perSecond;
}
