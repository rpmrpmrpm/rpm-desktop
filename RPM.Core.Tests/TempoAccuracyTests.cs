using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Pins the two accuracy measures to the definitions that the published tempo tables use. A number
/// measured on any other definition cannot be compared with a published one, and being comparable
/// with published ones is the reason these measures exist.
/// </summary>
public class TempoAccuracyTests
{
    [Fact]
    public void A_reading_inside_four_percent_agrees()
    {
        // 128 ± 5.12 BPM.
        Assert.True(TempoAccuracy.Matches(128.0, 128.0));
        Assert.True(TempoAccuracy.Matches(132.0, 128.0));
        Assert.True(TempoAccuracy.Matches(124.0, 128.0));
    }

    [Fact]
    public void A_reading_outside_four_percent_does_not()
    {
        Assert.False(TempoAccuracy.Matches(134.0, 128.0));
        Assert.False(TempoAccuracy.Matches(122.0, 128.0));
    }

    [Fact]
    public void The_tolerance_is_a_share_of_the_reference_not_a_fixed_number_of_beats()
    {
        // Five BPM out passes at 128 and fails at 60: the same drift is a different error.
        Assert.True(TempoAccuracy.Matches(133.0, 128.0));
        Assert.False(TempoAccuracy.Matches(65.0, 60.0));
    }

    [Fact]
    public void Half_time_and_double_time_fail_the_strict_measure_and_pass_the_octave_one()
    {
        Assert.False(TempoAccuracy.Matches(64.0, 128.0));
        Assert.False(TempoAccuracy.Matches(256.0, 128.0));

        Assert.True(TempoAccuracy.MatchesAtAnyOctave(64.0, 128.0));
        Assert.True(TempoAccuracy.MatchesAtAnyOctave(256.0, 128.0));
    }

    [Fact]
    public void A_third_and_a_triple_are_octaves_too()
    {
        // The dotted-quarter lock the DSP is prone to: two thirds of the tempo, or a bar of four
        // counted in three.
        Assert.True(TempoAccuracy.MatchesAtAnyOctave(128.0 / 3, 128.0));
        Assert.True(TempoAccuracy.MatchesAtAnyOctave(128.0 * 3, 128.0));
    }

    [Fact]
    public void A_quarter_of_the_tempo_is_not_forgiven()
    {
        // Only the factors the field agreed on. Four times out is a wrong reading, not a way of
        // counting.
        Assert.False(TempoAccuracy.MatchesAtAnyOctave(32.0, 128.0));
    }

    [Fact]
    public void The_octave_that_matched_is_reported()
    {
        Assert.Equal(1.0, TempoAccuracy.OctaveOf(128.0, 128.0));
        Assert.Equal(0.5, TempoAccuracy.OctaveOf(64.0, 128.0));
        Assert.Equal(2.0, TempoAccuracy.OctaveOf(256.0, 128.0));
        Assert.Equal(0.0, TempoAccuracy.OctaveOf(32.0, 128.0));
    }

    [Fact]
    public void An_unread_tempo_agrees_with_nothing()
    {
        Assert.False(TempoAccuracy.Matches(0, 128.0));
        Assert.False(TempoAccuracy.MatchesAtAnyOctave(0, 128.0));
        Assert.False(TempoAccuracy.Matches(128.0, 0));
        Assert.False(TempoAccuracy.MatchesExactly(128.0, 0));
    }

    [Fact]
    public void The_whole_beat_measure_rounds_both_sides()
    {
        Assert.True(TempoAccuracy.MatchesExactly(128.4, 127.6));
        Assert.False(TempoAccuracy.MatchesExactly(128.6, 127.6));
    }
}
