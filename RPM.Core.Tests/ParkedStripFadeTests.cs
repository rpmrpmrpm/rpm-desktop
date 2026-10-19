using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// The mini player's show/hide state. On screen and wanted are the same except during a fade, and
/// the difficult calls arrive during a fade: a track change clears the now-playing text at the end of
/// one song and sets it again at the start of the next, both during the dissolve.
/// </summary>
public class ParkedStripFadeTests
{
    [Fact]
    public void It_lays_the_strip_out_the_first_time_the_lines_go_up()
    {
        var fade = new ParkedStripFade();

        Assert.Equal(ParkedFadeAction.FadeInAndLayout, fade.Want(true));
        Assert.True(fade.IsOnScreen);
    }

    [Fact]
    public void It_does_nothing_when_asked_for_what_it_is_already_doing()
    {
        var fade = new ParkedStripFade();
        fade.Want(true);

        Assert.Equal(ParkedFadeAction.Nothing, fade.Want(true));
        Assert.Equal(ParkedFadeAction.Nothing, new ParkedStripFade().Want(false));
    }

    [Fact]
    public void It_keeps_the_lines_on_screen_until_the_fade_out_finishes()
    {
        var fade = new ParkedStripFade();
        fade.Want(true);

        Assert.Equal(ParkedFadeAction.FadeOut, fade.Want(false));
        Assert.True(fade.IsOnScreen);        // still dissolving: the strip keeps its parked shape

        Assert.True(fade.NoteFadedOut());
        Assert.False(fade.IsOnScreen);
    }

    /// <summary>The track change. Checked against the on-screen flag, this would read "already showing"
    /// and do nothing, so the dissolve would continue to zero with nothing to bring it back: a parked
    /// player with no text and only its scrub line.</summary>
    [Fact]
    public void It_brings_the_lines_back_when_the_next_track_lands_mid_fade()
    {
        var fade = new ParkedStripFade();
        fade.Want(true);
        fade.Want(false);                    // the track ended: the lines start dissolving

        Assert.Equal(ParkedFadeAction.FadeIn, fade.Want(true));
        Assert.True(fade.IsOnScreen);
    }

    /// <summary>The fade-out's completion, arriving after that, belongs to a departure that did not
    /// happen: it must not put the strip back into the open view's shape under a live readout.</summary>
    [Fact]
    public void It_ignores_a_fade_out_that_was_overtaken()
    {
        var fade = new ParkedStripFade();
        fade.Want(true);
        fade.Want(false);
        fade.Want(true);                     // the next track arrived mid-fade

        Assert.False(fade.NoteFadedOut());
        Assert.True(fade.IsOnScreen);
        Assert.True(fade.IsWanted);
    }

    [Fact]
    public void It_can_still_leave_after_an_overtaken_fade_out()
    {
        var fade = new ParkedStripFade();
        fade.Want(true);
        fade.Want(false);
        fade.Want(true);
        fade.NoteFadedOut();                 // stale, ignored

        Assert.Equal(ParkedFadeAction.FadeOut, fade.Want(false));
        Assert.True(fade.NoteFadedOut());
        Assert.False(fade.IsOnScreen);

        // Really gone, so the strip must take its parked geometry again.
        Assert.Equal(ParkedFadeAction.FadeInAndLayout, fade.Want(true));
    }
}
