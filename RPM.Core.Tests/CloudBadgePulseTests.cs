using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The pulse a cloud badge shows while it is working. Pure maths on purpose: the album wall and the
/// playlist tree each drive it from their own per-frame tick, and the one thing that must hold is that
/// both get the same answer for the same elapsed time. Otherwise a tile and the row above it are seen
/// pulsing out of step.
/// </summary>
public class CloudBadgePulseTests
{
    [Fact]
    public void It_starts_dim_and_breathes_up_to_full()
    {
        Assert.Equal(0.34, CloudBadgePulse.At(TimeSpan.Zero), precision: 3);
        Assert.Equal(1.0, CloudBadgePulse.At(TimeSpan.FromSeconds(0.7)), precision: 3);   // half a period
        Assert.Equal(0.34, CloudBadgePulse.At(TimeSpan.FromSeconds(1.4)), precision: 3);  // back round
    }

    /// <summary>
    /// Never all the way out. The badge says what the album's state is as well as that it is busy, and
    /// a glyph that disappears on every pulse is harder to read than one that dims.
    /// </summary>
    [Fact]
    public void It_never_goes_fully_transparent_or_past_full()
    {
        for (var ms = 0; ms < 5_000; ms += 7)
        {
            var value = CloudBadgePulse.At(TimeSpan.FromMilliseconds(ms));
            Assert.InRange(value, 0.34, 1.0);
        }
    }

    /// <summary>Same elapsed time, same answer: this keeps two views that tick separately in step.</summary>
    [Fact]
    public void It_is_a_pure_function_of_elapsed_time()
    {
        var at = TimeSpan.FromMilliseconds(931);
        Assert.Equal(CloudBadgePulse.At(at), CloudBadgePulse.At(at));
        Assert.Equal(CloudBadgePulse.At(at), CloudBadgePulse.At(at + TimeSpan.FromSeconds(1.4)), precision: 6);
    }

    /// <summary>Rest is fully opaque: a badge that stopped working must not be left dimmed.</summary>
    [Fact]
    public void Rest_is_full_opacity() => Assert.Equal(1.0, CloudBadgePulse.Rest);
}
