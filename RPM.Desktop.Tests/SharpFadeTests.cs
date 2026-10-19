using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cover's focus pull: the sharp picture coming up over the resident one on the album wall.
///
/// <para>These tests check the COST of the pull. The pull must not be a <c>while(true) { await
/// Task.Yield(); }</c> loop per tile. That loop does not wait for a frame. It posts its continuation
/// straight back to the dispatcher, which runs it and posts it again. On a live wall this came to about
/// 138,000 opacity writes a second with a single fade in flight. On Skia every one of those writes asks
/// for a repaint of the whole window. The scroll then carries about a frame's worth of latency at all
/// times, and the cover loader, which takes its turns at low priority, is starved behind a dispatcher
/// that never empties. This is the jitter.</para>
///
/// <para>The number that matters here is <see cref="SharpFade.WritesPerFade"/>. The first test fails if a
/// spin loop is put back.</para>
/// </summary>
public class SharpFadeTests
{
    /// <summary>The whole fix in one number: a fade is a handful of writes, not thousands.</summary>
    [Fact]
    public void A_fade_costs_a_handful_of_writes()
    {
        Assert.True(SharpFade.WritesPerFade > 0, "a fade that never writes never fades");
        Assert.True(SharpFade.WritesPerFade <= 8,
                    $"the pull is beaten on a shared clock; {SharpFade.WritesPerFade} is a spin come back");
    }

    [Fact]
    public void It_starts_where_the_layer_stood()
    {
        Assert.Equal(0, SharpFade.At(0, 0), 9);
        Assert.Equal(0.4, SharpFade.At(0.4, 0), 9);
    }

    /// <summary>A tile handed a second picture mid-pull restarts from where it stands, and must still land
    /// exactly on full, not on 0.6 plus the rest of a curve that was drawn for a start at zero.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(0.6)]
    [InlineData(0.99)]
    public void It_lands_on_full_from_wherever_it_began(double from)
        => Assert.Equal(1, SharpFade.At(from, 200), 9);

    /// <summary>A beat that lands late finishes the fade rather than carrying the curve past one, which
    /// would show as a tile brightening past its own picture.</summary>
    [Fact]
    public void A_late_beat_does_not_overshoot()
        => Assert.Equal(1, SharpFade.At(0, 5000), 9);

    [Fact]
    public void It_only_ever_climbs()
    {
        var last = -1.0;
        for (var ms = 0; ms <= 200; ms += 10)
        {
            var now = SharpFade.At(0, ms);
            Assert.True(now >= last, $"went backwards at {ms}ms");
            last = now;
        }
    }
}
