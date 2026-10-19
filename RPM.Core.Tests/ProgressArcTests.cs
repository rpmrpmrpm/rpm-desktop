using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The ring around the deck's rip chip. It is pure maths on purpose: the ring is a Polyline built
/// by hand (an arc geometry built in code draws nothing under Uno-Skia), so where the arc starts
/// and which way it goes is the one thing worth pinning down without a window to look at.
/// </summary>
public class ProgressArcTests
{
    private const double Cx = 12, Cy = 12, R = 9;

    [Fact]
    public void It_starts_at_twelve_oclock()
    {
        var points = ProgressArc.Points(0.5, Cx, Cy, R);
        Assert.Equal(Cx, points[0].X, precision: 6);
        Assert.Equal(Cy - R, points[0].Y, precision: 6);
    }

    /// <summary>Clockwise, in screen coordinates: a quarter of the way round is three o'clock, the
    /// RIGHT of the ring, not the left.</summary>
    [Fact]
    public void It_travels_clockwise()
    {
        var quarter = ProgressArc.Points(0.25, Cx, Cy, R);
        var (x, y) = quarter[^1];
        Assert.Equal(Cx + R, x, precision: 6);
        Assert.Equal(Cy, y, precision: 6);

        var half = ProgressArc.Points(0.5, Cx, Cy, R);
        Assert.Equal(Cx, half[^1].X, precision: 6);
        Assert.Equal(Cy + R, half[^1].Y, precision: 6);
    }

    /// <summary>A whole circle comes back to where it started, so the ring closes and leaves no gap
    /// at the top.</summary>
    [Fact]
    public void A_full_ring_closes()
    {
        var points = ProgressArc.Points(1, Cx, Cy, R);
        Assert.Equal(points[0].X, points[^1].X, precision: 6);
        Assert.Equal(points[0].Y, points[^1].Y, precision: 6);
    }

    /// <summary>Every point is on the circle: the arc is a ring, not a spiral.</summary>
    [Fact]
    public void Every_point_sits_on_the_circle()
    {
        foreach (var (x, y) in ProgressArc.Points(0.8, Cx, Cy, R))
            Assert.Equal(R, Math.Sqrt(Math.Pow(x - Cx, 2) + Math.Pow(y - Cy, 2)), precision: 6);
    }

    /// <summary>A short arc gets its share of the segments, so it is not drawn coarser than a long
    /// one, and never fewer than the two points a line needs.</summary>
    [Fact]
    public void Short_arcs_keep_their_share_of_the_segments()
    {
        Assert.Equal(13, ProgressArc.Points(0.25, Cx, Cy, R, segments: 48).Count);  // 12 pieces + 1
        Assert.Equal(49, ProgressArc.Points(1, Cx, Cy, R, segments: 48).Count);
        Assert.Equal(2, ProgressArc.Points(0.0001, Cx, Cy, R, segments: 48).Count);
    }

    /// <summary>Progress that arrives as nonsense (or as a rounding overshoot) still draws something
    /// a ring can be: nothing, or the whole circle.</summary>
    [Fact]
    public void It_clamps_its_fraction()
    {
        var none = ProgressArc.Points(-1, Cx, Cy, R);
        Assert.Equal(2, none.Count);
        Assert.Equal(none[0], none[^1]);   // zero-length: every point at twelve o'clock

        var all = ProgressArc.Points(4, Cx, Cy, R);
        Assert.Equal(ProgressArc.Points(1, Cx, Cy, R).Count, all.Count);
    }
}
