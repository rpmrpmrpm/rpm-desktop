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

    /// <summary>
    /// JT's "Oh", as LRCLIB has it and as the cloud measured it. The sheet stamps "And they used to..." at 17.28
    /// and the next line at 17.52, and the line was not measured; the measured last word of the line before runs
    /// to 17.51, and the next line's first word is measured at 17.40. The rules alone go from "You get these..."
    /// straight to "If they get out of line" at the squeezed line's stamp, 17.28: that is already within
    /// <see cref="KaraokeLines.Lead"/> of the next line's first word, and the squeezed line has no words of its own
    /// to wait for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_line_squeezed_between_its_neighbours_is_still_sung(bool asides)
    {
        var sheet = LyricSheet.Parse(string.Join('\n',
            "[00:08.55]To crown you the queen of bad bitches worldwide",
            "[00:11.98](I don't fuck with these niggas, I don't fuck with these hoes)",
            "[00:12.74]Now with that crown",
            "[00:14.31]You get these beautiful crystal slippers, baby",
            "[00:17.28]And they used to, not wear, but to crack a ho in they head",
            "[00:17.52]If they get out of line",
            "[00:22.82](I don't fuck with these niggas, I don't fuck with these hoes)",
            "[00:25.74]Pull up double-R, this that Rih and Rocky"));
        // The measured words by sheet line, as the cloud has them.
        var byLine = new Dictionary<int, (double From, double To)[]>
        {
            [0] = [(8.07, 8.13), (8.25, 8.51), (8.59, 8.71), (8.79, 8.85), (8.91, 9.45), (9.61, 9.65), (9.81, 10.07), (10.19, 10.55), (10.61, 11.57)],
            [1] = [(11.42, 11.44), (11.46, 11.56), (11.58, 11.66), (11.68, 11.76), (11.82, 11.94), (11.96, 12.10), (12.12, 12.14), (12.16, 12.40), (12.48, 12.59), (12.59, 12.70), (12.70, 12.84), (12.84, 12.98)],
            [2] = [(12.52, 12.64), (12.74, 12.90), (13.02, 13.18), (13.40, 13.92)],
            [3] = [(14.01, 14.09), (14.19, 14.31), (14.37, 14.61), (14.81, 15.31), (15.47, 15.85), (15.99, 16.43), (16.51, 17.51)],
            [5] = [(17.40, 17.90), (18.66, 18.90), (19.20, 19.32), (19.42, 19.72), (20.02, 20.24), (21.10, 21.56)],
        };
        var score = LyricScore.Of(sheet, asides);
        IReadOnlyList<WordSpan>? Measured(int part) => byLine.TryGetValue(score.Parts[part].Line, out var words)
            ? [.. words.Select((w, k) => new WordSpan(k * 6, 5, w.From, w.To))]
            : null;
        int Part(int line) => score.Parts.Select((p, i) => (p, i)).First(x => x.p.Line == line).i;
        var slippers = Part(3);
        var squeezed = Part(4);
        var outOfLine = Part(5);

        // The rules alone pass over the squeezed line.
        Assert.Equal(slippers, KaraokeLines.Held(score, 17.2, Measured));
        Assert.Equal(outOfLine, KaraokeLines.Held(score, 17.3, Measured));

        // The hand-overs give it a moment of its own, and the next line follows.
        var handovers = KaraokeLines.Handovers(score, Measured);
        Assert.Equal(slippers, handovers.At(17.2));
        Assert.Equal(squeezed, handovers.At(17.3));
        Assert.Equal(squeezed, handovers.At(17.28 + KaraokeLines.Shortest - 0.05));
        Assert.Equal(outOfLine, handovers.At(17.28 + KaraokeLines.Shortest + 0.05));
    }

    [Fact]
    public void The_hand_overs_are_the_rules_where_no_line_is_squeezed()
    {
        var handovers = KaraokeLines.Handovers(Score, Nothing);
        Assert.Equal(-1, handovers.At(9.9));
        Assert.Equal(0, handovers.At(10.0));
        Assert.Equal(1, handovers.At(14.0));
        Assert.Equal(14.0, handovers.Next(10.5)!.Value, 9);
    }
}
