using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>Which line is up while karaoke words are lit, as the Android phone app's KaraokeLinesTest checks
/// it.</summary>
public class KaraokeLinesTests
{
    private static readonly LyricScore Score = LyricScore.Of(LyricSheet.Parse(
        "[00:10.00]first line here\n[00:14.00]second line\n[00:25.00]third line"));

    /// <summary>The measured words of each piece, by its place in the score.</summary>
    private static Func<int, IReadOnlyList<WordSpan>?> Measured(params (int Part, (double From, double To)[] Words)[] lines)
        => part => lines.Where(l => l.Part == part)
            .Select(l => (IReadOnlyList<WordSpan>)[.. l.Words.Select((w, k) => new WordSpan(k * 6, 5, w.From, w.To))])
            .FirstOrDefault();

    private static readonly Func<int, IReadOnlyList<WordSpan>?> Nothing = _ => null;

    [Fact]
    public void With_nothing_measured_the_stamps_decide()
    {
        Assert.Equal(-1, KaraokeLines.Held(Score, 9.9, Nothing));
        Assert.Equal(0, KaraokeLines.Held(Score, 10.0, Nothing));
        Assert.Equal(0, KaraokeLines.Held(Score, 13.9, Nothing));
        Assert.Equal(1, KaraokeLines.Held(Score, 14.0, Nothing));
    }

    [Fact]
    public void A_line_stays_up_while_its_last_measured_word_is_sung()
    {
        var measured = Measured(
            (0, [(10.0, 11.0), (11.0, 12.0), (12.0, 14.6)]),
            (1, [(14.5, 15.0), (15.0, 15.5)]));

        // The stamp of the second line has passed, but the first is still on its last word.
        Assert.Equal(0, KaraokeLines.Held(Score, 14.3, measured));
        // The second line's first word has started, so it takes over although the first is still sung.
        Assert.Equal(1, KaraokeLines.Held(Score, 14.5, measured));
    }

    [Fact]
    public void A_line_measured_late_waits_for_its_voice_but_not_for_ever()
    {
        var measured = Measured((1, [(16.0, 16.5), (16.5, 17.0)]));

        Assert.Equal(0, KaraokeLines.Held(Score, 15.0, measured));
        Assert.Equal(1, KaraokeLines.Held(Score, 16.0 - KaraokeLines.Lead, measured));

        var astray = Measured((1, [(30.0, 30.5), (30.5, 31.0)]));
        Assert.Equal(0, KaraokeLines.Held(Score, 17.9, astray));
        // Past the longest wait, the stamp decides.
        Assert.Equal(1, KaraokeLines.Held(Score, 14.0 + KaraokeLines.WaitLongest, astray));
    }

    [Fact]
    public void A_line_sung_before_its_stamp_is_shown_as_the_light_arrives()
    {
        var measured = Measured(
            (0, [(10.0, 11.0), (11.0, 12.0), (12.0, 13.0)]),
            (1, [(13.5, 13.8), (13.8, 14.5)]));

        // The voice starts the second line at 13.5, half a second before its stamp.
        Assert.Equal(0, KaraokeLines.Held(Score, 13.0, measured));
        Assert.Equal(1, KaraokeLines.Held(Score, 13.2, measured));

        // Once the stamp is reached, it stays.
        Assert.Equal(1, KaraokeLines.Held(Score, 14.1, measured));
    }

    [Fact]
    public void A_line_is_not_shown_early_while_the_one_before_is_still_sung()
    {
        var measured = Measured(
            (0, [(10.0, 11.0), (11.0, 12.0), (12.0, 13.7)]),
            (1, [(13.5, 13.8), (13.8, 14.5)]));

        // The first line's last word runs to 13.7; the second line waits for its own first word at 13.5.
        Assert.Equal(0, KaraokeLines.Held(Score, 13.3, measured));
        Assert.Equal(1, KaraokeLines.Held(Score, 13.5, measured));
    }

    [Fact]
    public void A_line_is_never_shown_more_than_a_second_before_its_stamp()
    {
        var measured = Measured(
            (0, [(10.0, 10.5), (10.5, 11.0), (11.0, 11.5)]),
            (1, [(12.0, 13.8), (13.8, 14.5)]));

        Assert.Equal(0, KaraokeLines.Held(Score, 12.5, measured));
        Assert.Equal(1, KaraokeLines.Held(Score, 14.0 - KaraokeLines.Earliest, measured));
    }

    [Fact]
    public void The_first_line_takes_over_from_the_intro_before_its_stamp()
    {
        var measured = Measured((0, [(9.6, 10.5), (10.5, 11.0), (11.0, 11.5)]));

        Assert.Equal(-1, KaraokeLines.Held(Score, 9.0, measured));
        Assert.Equal(0, KaraokeLines.Held(Score, 9.3, measured));
    }

    [Fact]
    public void A_rest_is_never_taken_early()
    {
        var score = LyricScore.Of(LyricSheet.Parse("[00:10.00]one\n[00:12.00]♪\n[00:40.00]two"));

        // A rest has no words, so the rule never asks for them.
        Assert.Equal(0, KaraokeLines.Held(score, 11.9, part => part == 1
            ? throw new InvalidOperationException("a rest is not asked about")
            : null));
    }
}
