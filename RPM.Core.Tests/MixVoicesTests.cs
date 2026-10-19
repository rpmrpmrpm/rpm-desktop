using System.Globalization;
using RPM.Core.Analysis;
using RPM.Core.Lyrics;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// What two records are singing during one changeover.
///
/// <para>These tests check the ordering, not the numbers: two voices at once must always score worse
/// than one voice, and one voice worse than none. A changeover is scored on its own clock, with each
/// record placed on it from its own position.</para>
/// </summary>
public class MixVoicesTests
{
    private static VocalMap Sings(params (double From, double To)[] spans)
    {
        // Built through the sheet, so the test uses the same path as the app. A line of five
        // letters at SlowestChars is one second, so a stretch is made by repeating one.
        //
        // Formatted with the invariant culture on purpose: an LRC time stamp always uses a full stop,
        // and formatting under a comma culture writes [01:40,60], which is not a valid stamp.
        var lines = new List<string>();
        foreach (var (from, to) in spans)
        {
            var opens = from + VocalMap.Margin;
            var letters = (int)Math.Round((to - from - 2 * VocalMap.Margin) * LyricWindow.SlowestChars);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{(int)opens / 60:00}:{opens % 60:00.00}] {new string('a', Math.Max(1, letters))}"));
        }
        return VocalMap.FromSheet(LyricSheet.Parse(string.Join('\n', lines)));
    }

    private static readonly VocalMap Instrumental =
        VocalMap.FromSheet(LyricSheet.Parse("[00:00.00] ♪\n[05:00.00] ♪"));

    [Fact]
    public void Two_instrumentals_cost_nothing()
    {
        var voices = MixVoices.Over(Instrumental, 100, Instrumental, 0, 30);

        Assert.Equal(0, voices.Both, 3);
        Assert.Equal(0, voices.One, 3);
        Assert.Equal(30, voices.Neither, 3);
        Assert.False(voices.Clash);
    }

    [Fact]
    public void One_voice_over_the_others_instrumental_is_what_a_mix_is()
    {
        // Ten seconds of singing on the outgoing record, nothing on the incoming one.
        var voices = MixVoices.Over(Sings((100, 110)), 100, Instrumental, 0, 30);

        Assert.Equal(0, voices.Both, 3);
        Assert.Equal(10, voices.One, 1);
        Assert.Equal(20, voices.Neither, 1);
        Assert.False(voices.Clash);
    }

    [Fact]
    public void Two_voices_at_once_is_the_fault()
    {
        var voices = MixVoices.Over(Sings((100, 110)), 100, Sings((0, 10)), 0, 30);

        Assert.Equal(10, voices.Both, 1);
        Assert.Equal(0, voices.One, 1);
        Assert.Equal(20, voices.Neither, 1);
        Assert.True(voices.Clash);
    }

    [Fact]
    public void Which_record_is_singing_alone_is_counted_separately()
    {
        // These are two different cases. The old record's voice is one the listener already hears;
        // the new record's voice arriving under it spoils the handover. Scoring them separately lets
        // the search prefer the first, so they must not be added together here.
        var oldOne = MixVoices.Over(Sings((100, 110)), 100, Instrumental, 0, 30);
        var newOne = MixVoices.Over(Instrumental, 100, Sings((0, 10)), 0, 30);

        Assert.Equal(10, oldOne.Leaving, 1);
        Assert.Equal(0, oldOne.Arriving, 1);

        Assert.Equal(0, newOne.Leaving, 1);
        Assert.Equal(10, newOne.Arriving, 1);

        Assert.Equal(oldOne.One, newOne.One, 1);       // the same amount of singing, scored differently
    }

    [Fact]
    public void Silence_beats_one_voice_beats_two()
    {
        var none = MixVoices.Over(Instrumental, 0, Instrumental, 0, 30);
        var one = MixVoices.Over(Sings((0, 10)), 0, Instrumental, 0, 30);
        var two = MixVoices.Over(Sings((0, 10)), 0, Sings((0, 10)), 0, 30);

        Assert.Equal(0, none.One, 3);
        Assert.False(none.Clash);

        Assert.Equal(10, one.One, 1);
        Assert.False(one.Clash);

        Assert.True(two.Clash);
    }

    [Fact]
    public void Each_record_is_read_at_its_own_point_not_the_same_one()
    {
        // The outgoing record sings 100 to 110, so over a changeover starting at 100 it sings for the
        // first ten seconds. The incoming record sings 10 to 20 OF ITSELF: cued at 0 that vocal
        // arrives ten seconds in and misses; cued at 10 it lands exactly on top.
        var leaving = Sings((100, 110));
        var arriving = Sings((10, 20));

        Assert.False(MixVoices.Over(leaving, 100, arriving, 0, 30).Clash);
        Assert.True(MixVoices.Over(leaving, 100, arriving, 10, 30).Clash);
    }

    [Fact]
    public void A_partial_overlap_is_split_between_both_and_one()
    {
        // Ten seconds each, overlapping for five.
        var voices = MixVoices.Over(Sings((100, 110)), 100, Sings((5, 15)), 0, 30);

        Assert.Equal(5, voices.Both, 1);
        Assert.Equal(5, voices.Leaving, 1);
        Assert.Equal(5, voices.Arriving, 1);
        Assert.Equal(15, voices.Neither, 1);
    }

    [Fact]
    public void The_three_always_add_up_to_the_changeover()
    {
        var voices = MixVoices.Over(Sings((100, 108), (115, 125)), 100, Sings((2, 6), (20, 28)), 0, 30);

        Assert.Equal(30, voices.Both + voices.Leaving + voices.Arriving + voices.Neither, 2);
    }

    [Fact]
    public void A_record_nobody_has_read_sings_nowhere_and_says_so_by_being_unknown()
    {
        // Unknown has no spans, so it scores clean. That is why the planner must never pass one: it
        // resolves an unknown against the bands before it gets here. This test shows what Unknown
        // does on its own, for anybody who changes the planner.
        var voices = MixVoices.Over(VocalMap.Unknown, 0, Sings((0, 30)), 0, 30);

        Assert.Equal(0, voices.Both, 3);
        Assert.False(VocalMap.Unknown.IsKnown);
    }

    [Fact]
    public void A_changeover_of_no_length_has_nothing_in_it()
    {
        var voices = MixVoices.Over(Sings((0, 30)), 0, Sings((0, 30)), 0, 0);

        Assert.Equal(0, voices.Both, 3);
        Assert.Equal(0, voices.One, 3);
        Assert.Equal(0, voices.Neither, 3);
    }
}
