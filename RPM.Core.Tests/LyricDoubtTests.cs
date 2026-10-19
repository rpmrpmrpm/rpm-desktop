using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

public class LyricDoubtTests
{
    /// <summary>A line of words at an even pace, each a fifth of a second with a breath between them.
    /// The shape of a placement nobody has any reason to doubt.</summary>
    private static WordSpan[] Even(int words, double from = 10.0, double each = 0.20, double gap = 0.10)
    {
        var placed = new WordSpan[words];
        var at = from;
        for (var i = 0; i < words; i++)
        {
            placed[i] = new WordSpan(i * 5, 4, at, at + each);
            at += each + gap;
        }
        return placed;
    }

    [Fact]
    public void An_even_line_is_believed()
    {
        Assert.False(LyricDoubt.Parked(Even(10)));
    }

    [Fact]
    public void The_measured_failure_is_caught()
    {
        // ABBA, "SOS", the chorus line at 59.46 as the shipped weights place it. One syllable held for
        // 1.18 seconds and the last four words fitted into a tenth of a second each, ending on the
        // window's own wall. This is the placement that put the sheet four words behind the singer.
        var placed = new[]
        {
            new WordSpan(0, 4, 60.10, 61.28),     // When   1.18s
            new WordSpan(5, 6, 61.86, 62.84),     // you're
            new WordSpan(12, 5, 63.00, 63.62),    // gone,
            new WordSpan(18, 3, 63.76, 63.94),    // how
            new WordSpan(22, 3, 64.26, 64.48),    // can
            new WordSpan(26, 1, 64.82, 64.84),    // I      0.02s
            new WordSpan(28, 4, 65.22, 66.00),    // even
            new WordSpan(33, 3, 66.12, 66.48),    // try
            new WordSpan(37, 2, 66.82, 66.92),    // to     0.10s
            new WordSpan(40, 2, 67.28, 67.38),    // go     0.10s
            new WordSpan(43, 3, 67.60, 67.66),    // on?    0.06s, on the wall
        };

        Assert.True(LyricDoubt.Parked(placed));
    }

    [Fact]
    public void A_held_last_note_is_singing_and_not_a_fault()
    {
        // The other half of the rule, and the reason it is not simply "no word may run over a second".
        // A line that ends on a sustain is most of what a chorus is, and those lines place well.
        var placed = Even(9).ToList();
        var last = placed[^1];
        placed[^1] = last with { To = last.From + 1.60 };

        Assert.False(LyricDoubt.Parked(placed));
    }

    [Fact]
    public void A_hold_with_ordinary_words_after_it_is_left_alone()
    {
        // A word genuinely held mid-line, one long note, then the line carries on at its own pace.
        // Nothing is crushed, so nothing was fitted in, so there is no contradiction to read.
        var placed = Even(9).ToList();
        placed[2] = placed[2] with { To = placed[2].From + 1.30 };

        Assert.False(LyricDoubt.Parked(placed));
    }

    [Fact]
    public void A_slow_line_is_not_a_parked_one()
    {
        // Every word near a second: a slow line, not a fault. The hold is judged against the line's own
        // middle word, so a line with no word out of step with its neighbours has no long hold in it.
        var placed = Even(8, each: 0.95, gap: 0.15);

        Assert.False(LyricDoubt.Parked(placed));
    }

    [Fact]
    public void A_short_line_is_left_alone()
    {
        // Three words have no pace of their own to be out of step with, and their median is one word.
        var placed = new[]
        {
            new WordSpan(0, 2, 10.0, 11.4),
            new WordSpan(3, 2, 11.5, 11.56),
            new WordSpan(6, 2, 11.6, 11.66),
        };

        Assert.False(LyricDoubt.Parked(placed));
    }

    [Fact]
    public void One_crushed_word_after_a_hold_is_not_enough()
    {
        // A short word sung quickly after a long one is ordinary. It takes two, back to back with a word
        // held four times the line's own pace, before the placement claims something impossible.
        var placed = Even(9).ToList();
        placed[1] = placed[1] with { To = placed[1].From + 1.20 };
        placed[2] = placed[2] with { To = placed[2].From + 0.05 };

        Assert.False(LyricDoubt.Parked(placed));
    }
}
