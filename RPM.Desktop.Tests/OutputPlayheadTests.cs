using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// The playhead the lyric follows: a sink's staircase of buffer counts, read as a position that
/// moves every frame.
///
/// <para>There are two clocks. <c>said</c> is what the sink reports. It stands still for a whole
/// buffer and then jumps. <c>now</c> is the screen's own frame clock, which moves continuously. Both
/// are in seconds.</para>
/// </summary>
public class OutputPlayheadTests
{
    /// <summary>
    /// The regression this file guards against.
    ///
    /// <para>The first reading has nothing before it to compare against, so a not-a-number stood in its
    /// place. Nothing compares true against a not-a-number: <c>Math.Abs(said - NaN) &gt; eps</c> is
    /// false for every <c>said</c>. So the sentinel was never replaced, and every position returned was
    /// itself not-a-number. The binary search for "which line is being sung" then answered "none" on
    /// every frame. The sheet sat on its opening line for the whole record, and nothing looked like an
    /// arithmetic fault.</para>
    /// </summary>
    [Fact]
    public void The_first_reading_is_believed()
    {
        var head = new OutputPlayhead();

        var at = head.At(said: 12.5, now: 4.0);

        Assert.False(double.IsNaN(at));
        Assert.Equal(12.5 + OutputPlayhead.Lead, at, 6);
    }

    /// <summary>A reading of nought is a reading. The top of a record is the most ordinary first frame
    /// there is, and it must not be mistaken for "nothing said yet".</summary>
    [Fact]
    public void The_top_of_a_record_is_a_reading_like_any_other()
    {
        var head = new OutputPlayhead();

        Assert.Equal(OutputPlayhead.Lead, head.At(said: 0, now: 0), 6);
    }

    /// <summary>Between two reports from the sink, the answer keeps moving. A staircase is fine for a
    /// seek bar, but a line of text that slides past needs smooth movement.</summary>
    [Fact]
    public void The_clock_runs_on_between_readings()
    {
        var head = new OutputPlayhead();
        head.At(said: 30, now: 1.00);

        var early = head.At(said: 30, now: 1.01);
        var later = head.At(said: 30, now: 1.03);

        Assert.True(later > early, $"{later} should be past {early}");
        Assert.Equal(30 + 0.03 + OutputPlayhead.Lead, later, 6);
    }

    /// <summary>But it never goes more than one buffer past what the sink actually said. A sink that has
    /// stopped reporting has stopped. A playhead that kept going would show the song still playing while
    /// the room was silent.</summary>
    [Fact]
    public void A_sink_that_stops_reporting_stops_the_playhead()
    {
        var head = new OutputPlayhead();
        head.At(said: 30, now: 1.0);

        var stalled = head.At(said: 30, now: 9.0);

        Assert.Equal(30 + OutputPlayhead.Step + OutputPlayhead.Lead, stalled, 6);
    }

    /// <summary>A seek is taken at once, in either direction, and not eased towards. The words must be at
    /// the new position on the very next frame.</summary>
    [Theory]
    [InlineData(140.0)]
    [InlineData(2.0)]
    public void A_seek_is_believed_outright(double landed)
    {
        var head = new OutputPlayhead();
        head.At(said: 30, now: 1.0);

        Assert.Equal(landed + OutputPlayhead.Lead, head.At(said: landed, now: 1.02), 6);
    }

    /// <summary>The answer is always ahead of what the sink says. The position handed back is not yet
    /// what is being heard. A lyric that arrives after its line is the one failure a listener cannot
    /// unsee.</summary>
    [Fact]
    public void The_answer_leads_what_the_sink_said()
    {
        var head = new OutputPlayhead();

        Assert.True(head.At(said: 61, now: 0) > 61);
    }

    /// <summary>When the frame clock restarts, the moments stored here no longer mean anything, because
    /// they are on the old clock. So the next reading is taken as it is, not compared with them.</summary>
    [Fact]
    public void A_restarted_clock_forgets_what_it_was_told()
    {
        var head = new OutputPlayhead();
        head.At(said: 30, now: 88.0);

        head.Restart();

        // The same sink reading, with the clock back at zero. Without clearing the old moment, the code
        // would count 0 - 88 seconds instead of none.
        Assert.Equal(30 + OutputPlayhead.Lead, head.At(said: 30, now: 0), 6);
    }
}
