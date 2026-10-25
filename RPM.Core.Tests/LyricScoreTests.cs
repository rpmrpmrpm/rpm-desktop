using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>The score a lyric column is shown from, as the Android TV app's LyricScoreTest checks it.</summary>
public class LyricScoreTests
{
    private static LyricSheet Sheet(params (double Seconds, string Text)[] lines)
        => LyricSheet.Parse(string.Join('\n', lines.Select(l => FormattableString.Invariant(
            $"[{(int)(l.Seconds / 60):00}:{l.Seconds % 60:00.00}]{l.Text}"))));

    [Fact]
    public void A_long_wait_is_a_rest_that_closes_the_stretch_before_it()
    {
        var score = LyricScore.Of(Sheet((10, "one"), (12, "two"), (60, "three")));

        var rest = score.Parts[2];
        Assert.True(rest.IsRest);
        Assert.Equal(-1, rest.Line);
        Assert.Equal(12 + LyricScore.Sung, rest.From, 3);
        Assert.Equal(60, rest.Until, 3);
        Assert.Equal(0, rest.Section);
        Assert.Equal(1, score.Parts[3].Section);
        Assert.Equal(2, score.Parts[3].Line);
    }

    [Fact]
    public void A_wordless_line_is_a_rest()
    {
        var score = LyricScore.Of(Sheet((5, "one"), (9, "♪"), (30, "two")));

        Assert.True(score.Parts[1].IsRest);
        Assert.Equal(1, score.Parts[1].Line);
        Assert.Equal(9, score.Parts[1].From, 3);
        Assert.Equal(30, score.Parts[1].Until, 3);
        Assert.Equal(0, score.Parts[1].Section);
        Assert.Equal(1, score.Parts[2].Section);
    }

    [Fact]
    public void Before_the_first_line_there_is_no_piece()
    {
        var score = LyricScore.Of(Sheet((10, "one")));

        Assert.Equal(-1, score.IndexAt(3));
        Assert.Equal(0, score.IndexAt(10.5));
    }

    [Fact]
    public void The_outro_is_a_rest_that_never_fills()
    {
        var score = LyricScore.Of(Sheet((10, "one"), (14, "two")));

        var outro = score.Parts[^1];
        Assert.True(outro.IsRest);
        Assert.Equal(14 + LyricScore.Sung, outro.From, 3);
        Assert.True(double.IsPositiveInfinity(outro.Until));
        Assert.Equal(0, LyricScore.Through(100, outro.From, outro.Until));
        Assert.Equal(outro.Id, score.IndexAt(100));
    }

    [Fact]
    public void Lines_fade_by_their_distance_from_the_sung_one()
    {
        Assert.Equal([1.0, 0.42, 0.24, 0.12, 0.0], Enumerable.Range(0, 5).Select(LyricScore.Fade));
    }

    [Fact]
    public void A_rest_fills_across_its_length()
    {
        Assert.Equal(0.5, LyricScore.Through(15, 10, 20), 6);
        Assert.Equal(0, LyricScore.Through(5, 10, 20), 6);
        Assert.Equal(1, LyricScore.Through(25, 10, 20), 6);
    }
}
