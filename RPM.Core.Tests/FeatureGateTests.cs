using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The gate's arithmetic. All of it ends up on screen as a sentence or a bar, so the edges matter
/// more than the middle: a gate that is already open must not say it has a negative amount to go,
/// and a bar must not draw past its own track.
/// </summary>
public class FeatureGateTests
{
    [Theory]
    [InlineData(0, 25, false)]
    [InlineData(24, 25, false)]
    [InlineData(25, 25, true)]
    [InlineData(26, 25, true)]
    public void OpensAtTheThresholdAndNotBefore(int have, int want, bool open)
        => Assert.Equal(open, new FeatureGate(GateNeed.Albums, have, want).Open);

    [Theory]
    [InlineData(0, 25, 25)]
    [InlineData(13, 25, 12)]
    [InlineData(25, 25, 0)]
    public void ToGoCountsDownToNought(int have, int want, int toGo)
        => Assert.Equal(toGo, new FeatureGate(GateNeed.Albums, have, want).ToGo);

    /// <summary>The sentence is "12 albums to go". Past the threshold there is nothing to go, not a
    /// negative amount of it.</summary>
    [Fact]
    public void ToGoNeverGoesNegative()
        => Assert.Equal(0, new FeatureGate(GateNeed.Albums, 900, 25).ToGo);

    [Theory]
    [InlineData(0, 10, 0.0)]
    [InlineData(5, 10, 0.5)]
    [InlineData(10, 10, 1.0)]
    public void FractionTracksTheCount(int have, int want, double fraction)
        => Assert.Equal(fraction, new FeatureGate(GateNeed.SongsInList, have, want).Fraction, 6);

    /// <summary>A bar must not overflow its track when the gate is already past.</summary>
    [Fact]
    public void FractionIsClampedAtOne()
        => Assert.Equal(1.0, new FeatureGate(GateNeed.SongsInList, 40, 10).Fraction, 6);

    /// <summary>A gate that needs nothing is open and finished, and does not divide by zero.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AGateWantingNothingIsAlreadyOpen(int want)
    {
        var gate = new FeatureGate(GateNeed.Plays, 0, want);
        Assert.True(gate.Open);
        Assert.Equal(0, gate.ToGo);
        Assert.Equal(1.0, gate.Fraction, 6);
    }

    /// <summary>The home threshold is derived from the row length, not chosen: with less than a full
    /// row, the rows on that screen cannot differ from one another. If either number changes, this
    /// assumption must be checked again.</summary>
    [Fact]
    public void TheHomeThresholdClearsAFullRow()
        => Assert.True(HomeGate.LeastAlbums > HomeShelf.RowLength);

    [Fact]
    public void HomeIsShutUnderTheThresholdAndOpenAtIt()
    {
        Assert.False(HomeGate.For(HomeGate.LeastAlbums - 1).Open);
        Assert.True(HomeGate.For(HomeGate.LeastAlbums).Open);
        Assert.Equal(GateNeed.Albums, HomeGate.For(0).Need);
    }
}
