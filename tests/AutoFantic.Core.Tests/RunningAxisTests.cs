using AutoFantic.Core.Monitoring;

namespace AutoFantic.Core.Tests;

/// <summary>The monitor's time axis without the time the PC was off.</summary>
public class RunningAxisTests
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 30, 19, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Morning = Evening.AddHours(14);

    private static IEnumerable<DateTimeOffset> Every(DateTimeOffset from, int minutes, int count) =>
        Enumerable.Range(0, count).Select(i => from.AddMinutes(i * minutes));

    // two hours in the evening, off over night, an hour in the morning: a point per minute
    private static RunningAxis TwoSittings() =>
        RunningAxis.Of([.. Every(Evening, 1, 120), .. Every(Morning, 1, 60)], Evening.AddHours(-5), Morning.AddHours(1));

    [Fact]
    public void The_time_the_pc_was_off_is_left_out()
    {
        var axis = TwoSittings();

        Assert.Equal((119 + 59) * 60, axis.Seconds, 3); // from the first to the last point of each sitting
        Assert.Equal(Evening, axis.From); // the empty hours before the first point are left out too
        Assert.Equal(0, axis.Position(Evening), 6);
        Assert.Equal(1, axis.Position(Morning.AddHours(1)), 6);

        double cut = Assert.Single(axis.Cuts);
        Assert.InRange(cut, 0.64, 0.68); // after two of the three hours
        Assert.InRange(axis.Position(Morning) - axis.Position(Evening.AddHours(2)), 0.001, 0.02); // 12 hours off take the room of a cut
    }

    [Fact]
    public void A_time_that_was_left_out_lies_on_its_cut()
    {
        var axis = TwoSittings();
        var whileOff = Evening.AddHours(6);

        Assert.Equal(axis.Cuts.Single(), axis.Position(whileOff), 6);
        Assert.Equal([(Evening, Evening.AddMinutes(119)), (Morning, Morning.AddMinutes(59))], axis.Stretches);
    }

    [Fact]
    public void A_position_leads_back_to_its_time()
    {
        var axis = TwoSittings();
        var time = Morning.AddMinutes(20);

        Assert.InRange((axis.TimeAt(axis.Position(time)) - time).TotalSeconds, -1, 1);
        Assert.InRange((axis.TimeAt(axis.Cuts.Single()) - Evening.AddMinutes(119)).TotalSeconds, -1, 1); // on the cut: the last point before the PC went off
    }

    [Fact]
    public void Many_cuts_share_their_room_so_the_data_keeps_most_of_the_width()
    {
        // an hour every evening for two months
        var axis = RunningAxis.Of(Enumerable.Range(0, 60).SelectMany(day => Every(Evening.AddDays(day), 10, 6)), Evening, Evening.AddDays(60));

        Assert.Equal(59, axis.Cuts.Count());
        Assert.Equal(0.15, axis.CutRoom * 59, 6);
        Assert.Equal(60 * 50 * 60, axis.Seconds, 3);
    }

    [Fact]
    public void Without_points_it_is_the_plain_time_axis()
    {
        var axis = RunningAxis.Of([], Evening, Evening.AddHours(1));

        Assert.Empty(axis.Cuts);
        Assert.Equal(0.5, axis.Position(Evening.AddMinutes(30)), 6);
        Assert.Equal(0.25, RunningAxis.Whole(Evening, Evening.AddHours(1)).Position(Evening.AddMinutes(15)), 6);
    }
}
