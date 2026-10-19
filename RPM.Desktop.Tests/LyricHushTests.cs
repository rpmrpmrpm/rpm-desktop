using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// When the lyric sheet takes itself away between lines, and when it comes back.
///
/// <para>Times are in seconds, read as a playhead would read them. <c>from</c> is the line just sung, or
/// the top of the record before the first line. <c>until</c> is the line being waited for. The values are
/// eased before they reach the screen. The easing never goes backwards and fixes both ends, so these tests
/// check the raw curve.</para>
/// </summary>
public class LyricHushTests
{
    /// <summary>The common case, which must never flicker: lines follow one another at singing pace. A
    /// sheet that dipped between the verses of a slow song would be worse than one that stays still. In the
    /// middle of a song the sheet gives a long benefit of the doubt, because an LRC file never says how
    /// long the line on screen takes to sing.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(9.5)]
    [InlineData(20.0)]
    public void A_wait_short_enough_to_be_singing_never_moves_the_sheet(double gap)
    {
        for (double t = 0; t <= gap; t += 0.1)
            Assert.Equal(1.0, LyricHush.Show(t, from: 0, until: gap, intro: false));
    }

    /// <summary>The two waits have different limits, on purpose. A twelve second rest in the middle of a
    /// song is probably a long note or a slow singer, so the words stay. The same twelve seconds before
    /// anything has been sung is an intro. A sheet of unsung lines over an intro says nothing useful, so it
    /// is hidden sooner.</summary>
    [Fact]
    public void An_intro_is_given_far_less_rope_than_a_break_mid_song()
    {
        Assert.Equal(1.0, LyricHush.Show(2, from: 0, until: 12, intro: false));
        Assert.Equal(0.0, LyricHush.Show(2, from: 0, until: 12, intro: true));
    }

    /// <summary>Each threshold is checked on both sides. At exactly the threshold nothing happens, and just
    /// past it the sheet starts to hide. The boundary is the whole meaning of the figure.</summary>
    [Theory]
    [InlineData(false, LyricHush.AfterBreak)]
    [InlineData(true, LyricHush.AfterIntro)]
    public void The_threshold_is_where_the_hush_begins(bool intro, double after)
    {
        var atIt = LyricHush.Show(LyricHush.Out, from: 0, until: after, intro: intro);
        var pastIt = LyricHush.Show(LyricHush.Out, from: 0, until: after + 0.5, intro: intro);

        Assert.Equal(1.0, atIt);
        Assert.True(pastIt < 1.0, $"a wait past the threshold should have begun to go, but showed {pastIt}");
    }

    /// <summary>During a real break the words go, stay gone, and are back at full exactly when the next
    /// line lands, not after it. A reader who missed the start of a line cannot go back for it.</summary>
    [Fact]
    public void Through_a_long_break_the_words_go_and_are_back_before_the_singer()
    {
        const double until = 60;

        Assert.Equal(1.0, LyricHush.Show(0, 0, until, intro: false));                       // just sung
        Assert.Equal(0.0, LyricHush.Show(LyricHush.Out, 0, until, intro: false));           // gone
        Assert.Equal(0.0, LyricHush.Show(30, 0, until, intro: false));                      // still gone
        Assert.Equal(0.0, LyricHush.Show(until - LyricHush.In, 0, until, intro: false));    // the turn
        Assert.Equal(1.0, LyricHush.Show(until, 0, until, intro: false));                   // full, as it lands
    }

    /// <summary>The return has a deadline, so the test checks its shape, not only its ends. The value must
    /// never fall, and it must be part way up in the middle. It must not jump at one end of the two
    /// seconds.</summary>
    [Fact]
    public void The_return_climbs_all_the_way_and_never_falls_back()
    {
        const double until = 60;

        Assert.InRange(LyricHush.Show(until - LyricHush.In / 2, 0, until, intro: false), 0.4, 0.6);

        var previous = 0.0;
        for (double t = until - LyricHush.In; t <= until; t += 0.05)
        {
            var now = LyricHush.Show(t, 0, until, intro: false);
            Assert.True(now >= previous, $"the return dipped at {t:0.00}s: {now} after {previous}");
            previous = now;
        }
    }

    /// <summary>The intro is measured from the top of the record, because there is no line before the first
    /// one. The sheet lifts for the first line over the same two seconds as any other line. This is the
    /// case where the card in the middle of the screen is shown.</summary>
    [Fact]
    public void A_long_intro_hushes_and_lifts_for_the_first_line()
    {
        Assert.Equal(0.0, LyricHush.Show(6, from: 0, until: 25, intro: true));
        Assert.Equal(0.0, LyricHush.Show(25 - LyricHush.In, 0, 25, intro: true));
        Assert.InRange(LyricHush.Show(25 - LyricHush.In / 2, 0, 25, intro: true), 0.4, 0.6);
        Assert.Equal(1.0, LyricHush.Show(25, 0, 25, intro: true));
    }

    /// <summary>A short intro leaves the sheet alone, and no card appears for it. The card is the opposite
    /// of this figure.</summary>
    [Fact]
    public void A_short_intro_is_left_alone()
    {
        for (double t = 0; t <= LyricHush.AfterIntro; t += 0.1)
            Assert.Equal(1.0, LyricHush.Show(t, from: 0, until: LyricHush.AfterIntro, intro: true));
    }

    /// <summary>After the last line there is no next line. The wait never ends, and the words stay hidden
    /// through the outro. They do not stay lit on a line that nobody is singing.</summary>
    [Fact]
    public void After_the_last_line_the_sheet_stays_away()
    {
        Assert.Equal(1.0, LyricHush.Show(100, 100, double.PositiveInfinity, intro: false));
        Assert.Equal(0.0, LyricHush.Show(100 + LyricHush.Out, 100, double.PositiveInfinity, intro: false));
        Assert.Equal(0.0, LyricHush.Show(100_000, 100, double.PositiveInfinity, intro: false));
    }

    /// <summary>The figure depends only on the playhead position, and nothing remembered. This is what
    /// makes a seek land correctly. The same moment always gives the same answer, however the playhead got
    /// there.</summary>
    [Fact]
    public void The_same_moment_always_gives_the_same_answer()
    {
        Assert.Equal(
            LyricHush.Show(12, from: 4, until: 90, intro: false),
            LyricHush.Show(12, from: 4, until: 90, intro: false));
    }
}
