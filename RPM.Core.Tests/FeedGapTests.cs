using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// How the pump's missing time is split into its causes.
///
/// <para>Tested because the line is only useful if it adds up. A remainder that could be negative
/// would let a long read cancel out a long changeover and show a gap as explained when it was not.
/// The only reader of that line is somebody trying to find out why the music stopped.</para>
/// </summary>
public class FeedGapTests
{
    private static FeedGap Gap(
        double total, double read = 0, double changeover = 0, double playhead = 0,
        double collector = 0, double? ring = null)
        => new(total, read, changeover, playhead, collector, ring);

    // ── the remainder is stated, never omitted ──

    [Fact]
    public void What_no_part_explains_is_left_over()
    {
        var gap = Gap(total: 1000, read: 100, changeover: 50, playhead: 25);

        Assert.Equal(825, gap.ElsewhereMs);
    }

    [Fact]
    public void A_gap_its_parts_cover_has_nothing_left_over()
    {
        var gap = Gap(total: 175, read: 100, changeover: 50, playhead: 25);

        Assert.Equal(0, gap.ElsewhereMs);
    }

    [Fact]
    public void Parts_that_overrun_the_whole_do_not_become_credit()
    {
        // Timestamps taken on a thread that can be suspended between any two of them.
        var gap = Gap(total: 100, read: 400, changeover: 0, playhead: 0);

        Assert.Equal(0, gap.ElsewhereMs);
    }

    [Fact]
    public void A_negative_part_is_not_carried()
    {
        var gap = Gap(total: 1000, read: -500, changeover: 0, playhead: 0);

        Assert.Equal(0, gap.ReadMs);
        Assert.Equal(1000, gap.ElsewhereMs);
    }

    [Fact]
    public void The_collector_overlaps_the_parts_rather_than_adding_to_them()
    {
        // A collection freezes whichever part was running, so it must not be subtracted a second time.
        var gap = Gap(total: 1000, read: 900, collector: 800);

        Assert.Equal(100, gap.ElsewhereMs);
        Assert.Equal(800, gap.CollectorMs);
    }

    // ── when to write a line ──

    [Fact]
    public void A_gap_inside_the_threshold_is_not_late()
    {
        Assert.False(Gap(total: 100).IsLate(100));
    }

    [Fact]
    public void A_gap_past_the_threshold_is_late()
    {
        Assert.True(Gap(total: 101).IsLate(100));
    }

    [Fact]
    public void A_read_that_is_late_makes_the_whole_gap_late()
    {
        // A slow read alone is still caught.
        Assert.True(Gap(total: 4062, read: 4000).IsLate(100));
    }

    [Fact]
    public void A_gap_no_part_explains_is_still_late()
    {
        // Nothing measured was slow, but the pump was absent for four seconds. This is caught too.
        var gap = Gap(total: 4062);

        Assert.True(gap.IsLate(100));
        Assert.Equal(4062, gap.ElsewhereMs);
    }

    // ── the ring ──

    [Fact]
    public void A_source_with_no_ring_says_so_rather_than_reporting_none()
    {
        var gap = Gap(total: 200, ring: null);

        Assert.Null(gap.RingMs);
        Assert.Contains("no ring to ask", gap.Describe());
    }

    [Fact]
    public void An_empty_ring_is_a_reading_and_not_a_missing_one()
    {
        var gap = Gap(total: 200, ring: 0);

        Assert.Equal(0, gap.RingMs);
        Assert.DoesNotContain("no ring to ask", gap.Describe());
    }

    // ── the line itself ──

    [Fact]
    public void Every_part_is_named_in_the_line()
    {
        var line = Gap(total: 1000, read: 100, changeover: 50, playhead: 25, collector: 5, ring: 480)
            .Describe();

        Assert.Contains("read", line);
        Assert.Contains("changeover", line);
        Assert.Contains("playhead", line);
        Assert.Contains("elsewhere", line);
        Assert.Contains("ring", line);
        Assert.Contains("collector", line);
    }
}
