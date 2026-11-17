namespace RPM.Core.Layout;

/// <summary>
/// How far a group of buttons in a pane heading slides left to get out from under the spinning record.
///
/// <para>The disc hangs half off the window's right edge and is drawn over the panes. It takes the clicks
/// where it is drawn, so a button under it can be neither seen nor pressed. A heading has room to the left
/// of its buttons, so they move sideways. The library rail has no such room and moves down instead (see
/// <see cref="RailDuck"/>).</para>
/// </summary>
public static class RecordDodge
{
    /// <summary>Clear space kept between a group and the disc, in px. It is generous because the reach is
    /// the sharp edge of the vinyl, and the disc's soft shadow lies past it. These are buttons, so they are
    /// kept a full thumb's width away, not only out of the overlap.</summary>
    public const double Gap = 56;

    /// <summary>
    /// The slide to the left, in px. Zero when the disc reaches nowhere near the group.
    /// </summary>
    /// <param name="reach">The disc's leftmost x at a given y, both in window coordinates. Positive infinity
    /// at a row the disc does not reach, and at every row while nothing is playing.</param>
    /// <param name="restRight">The group's right edge where it rests, with no slide.</param>
    /// <param name="top">The group's top edge.</param>
    /// <param name="height">The group's height.</param>
    public static double Reserve(Func<double, double> reach, double restRight, double top, double height)
    {
        // Low on the circle the arc moves far to the side over a few px of height. So the reach is read at
        // five rows through the group, and the leftmost of them is the one to clear.
        var recordLeft = double.PositiveInfinity;
        for (var i = 0; i <= 4; i++)
            recordLeft = Math.Min(recordLeft, reach(top + (height * i / 4)));

        return double.IsInfinity(recordLeft) ? 0 : Math.Max(0, restRight + Gap - recordLeft);
    }
}
