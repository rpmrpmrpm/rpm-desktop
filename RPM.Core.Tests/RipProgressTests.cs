using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// What a rip report reads as. The popup's bar and the deck chip's ring are two views of one rip, so
/// both take their number from here and neither does the division itself. Otherwise a popup that was
/// closed and reopened could disagree with the chip about the same disc.
/// </summary>
public class RipProgressTests
{
    [Fact]
    public void Overall_spans_the_whole_disc_not_the_track()
    {
        // Halfway through the third of four tracks.
        var p = new RipProgress(TrackDone: 2, TrackTotal: 4, CurrentTrack: "Aja", TrackFraction: 0.5);
        Assert.Equal(0.625, p.Overall, precision: 6);
    }

    [Fact]
    public void Overall_runs_zero_to_one()
    {
        Assert.Equal(0, new RipProgress(0, 11, "", 0).Overall);
        Assert.Equal(1, new RipProgress(11, 11, "", 1).Overall);
    }

    /// <summary>A rip of nothing has no progress to report, and it must not divide by its own track
    /// count.</summary>
    [Fact]
    public void An_empty_rip_reports_nothing()
    {
        Assert.Equal(0, new RipProgress(0, 0, "", 0).Overall);
        Assert.Equal(0, new RipProgress(0, 0, "", 0).TrackNow);
    }

    /// <summary>The number in "ripping 3 of 11" counts from 1, and the final report, which says every
    /// track is done, must not read as "12 of 11".</summary>
    [Fact]
    public void TrackNow_counts_from_one_and_stops_at_the_total()
    {
        Assert.Equal(1, new RipProgress(0, 11, "", 0).TrackNow);
        Assert.Equal(3, new RipProgress(2, 11, "", 0.5).TrackNow);
        Assert.Equal(11, new RipProgress(11, 11, "", 1).TrackNow);
    }

    /// <summary>A ripper that overshoots (a retry counted twice, say) still leaves a bar that can be
    /// drawn: full, not past the end of its track.</summary>
    [Fact]
    public void It_clamps_an_overshooting_report()
    {
        Assert.Equal(1, new RipProgress(12, 11, "", 1).Overall);
        Assert.Equal(11, new RipProgress(12, 11, "", 1).TrackNow);
    }
}
