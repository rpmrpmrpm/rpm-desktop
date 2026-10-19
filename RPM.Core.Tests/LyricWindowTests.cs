using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

public class LyricWindowTests
{
    private const string Line = "When you're gone, how can I even try to go on?";

    [Fact]
    public void The_far_wall_is_the_next_line_s_stamp_and_a_hair_more()
    {
        // The window is not a LENGTH that gains a slack at each end afterwards: with that, an overrun of
        // 1.5 would reach a wall 2.1 seconds past the next stamp, and forced alignment, which must
        // consume every frame it is handed, would spread the tail of the line out into it.
        var window = LyricWindow.For(Line, 59.46, 67.42);

        Assert.Equal(67.42 + LyricWindow.Overrun, window.Closes, 6);
    }

    [Fact]
    public void Nothing_is_added_to_the_wall_after_it_is_decided()
    {
        // Said as the property that has to hold rather than as an arithmetic: whatever the line, the
        // window may not reach past the next stamp by more than the overrun. This assertion holds
        // however the sum is written.
        foreach (var text in new[] { "Oh", Line, new string('a', 400) })
            foreach (var gap in new[] { 0.4, 1.0, 3.0, 8.0, 30.0 })
            {
                var window = LyricWindow.For(text, 10.0, 10.0 + gap);
                Assert.True(
                    window.Closes <= 10.0 + gap + LyricWindow.Overrun + 1e-9,
                    $"a {text.Length} character line with a {gap}s gap reached {window.Closes:F2}, " +
                    $"past {10.0 + gap + LyricWindow.Overrun:F2}");
            }
    }

    [Fact]
    public void A_line_before_a_long_instrumental_is_bounded_by_its_own_words_instead()
    {
        // The neighbour is half a minute away, so it says nothing useful; what stops the search running
        // out into the guitar is how long these words could take to sing.
        var window = LyricWindow.For(Line, 59.46, 90.0);

        Assert.Equal(59.46 + Line.Length / LyricWindow.SlowestChars, window.Closes, 6);
    }

    [Fact]
    public void The_last_line_of_a_sheet_has_no_neighbour_and_is_bounded_the_same_way()
    {
        var infinite = LyricWindow.For(Line, 59.46, double.PositiveInfinity);
        var enormous = LyricWindow.For(Line, 59.46, double.MaxValue);

        Assert.Equal(59.46 + Line.Length / LyricWindow.SlowestChars, infinite.Closes, 6);
        Assert.Equal(infinite.Closes, enormous.Closes, 6);   // both spellings of "there is no next line"
    }

    [Fact]
    public void The_window_opens_before_the_stamp_because_a_singer_comes_in_early()
    {
        var window = LyricWindow.For(Line, 59.46, 67.42);

        Assert.Equal(59.46 - LyricWindow.Slack, window.Opens, 6);
    }

    [Fact]
    public void The_first_line_of_a_record_cannot_open_before_the_record_does()
    {
        var window = LyricWindow.For("Uh", 0.42, 0.99);

        Assert.Equal(0, window.Opens, 6);
    }

    [Fact]
    public void A_line_whose_neighbour_is_a_breath_away_still_gets_something_to_read()
    {
        // Two stamps a fifth of a second apart would otherwise leave the model less audio than it will
        // accept, and a window it refuses outright leaves the line with no answer rather than a short one.
        var window = LyricWindow.For("Uh", 30.0, 30.2);

        Assert.True(window.Length >= LyricWindow.LeastWindow);
    }

    [Fact]
    public void The_length_is_the_two_moments_and_nothing_else()
    {
        var window = LyricWindow.For(Line, 59.46, 67.42);

        Assert.Equal(window.Closes - window.Opens, window.Length, 9);
    }
}
