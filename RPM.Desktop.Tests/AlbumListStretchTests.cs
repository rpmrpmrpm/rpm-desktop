using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// How far a tracklist moves when scrolled past its end (the rubber-band effect), checked by the shape
/// of the curve rather than by exact numbers.
///
/// <para>A fixed movement and return on every notch, restarted by the next notch, looks like a jitter.
/// The movement must get SMALLER with each notch: there is always a little more, it is never linear, and
/// it is never a fixed step. These tests check that shape; durations or easings cannot replace it.</para>
/// </summary>
public class AlbumListStretchTests
{
    /// <summary>One notch past the end, in the units a tracklist scrolls by: three lines of 22.</summary>
    private const double Notch = 66;

    [Fact]
    public void Nothing_pushed_gives_nothing()
        => Assert.Equal(0, AlbumListView.StretchGive(0));

    /// <summary>Scrolling down the page is a negative push and moves the songs up, so the result keeps
    /// the sign of the push. With the sign reversed, the list would stretch the wrong way at both
    /// ends.</summary>
    [Fact]
    public void The_give_follows_the_direction_of_the_push()
    {
        Assert.True(AlbumListView.StretchGive(-Notch) < 0);
        Assert.True(AlbumListView.StretchGive(+Notch) > 0);
    }

    [Fact]
    public void The_two_directions_give_alike()
        => Assert.Equal(
            AlbumListView.StretchGive(+Notch),
            -AlbumListView.StretchGive(-Notch));

    /// <summary>The first notch past the end has to be plainly visible, or there is no signal at all.
    /// </summary>
    [Fact]
    public void The_first_notch_past_the_end_is_plainly_a_movement()
    {
        var give = Math.Abs(AlbumListView.StretchGive(-Notch));

        Assert.InRange(give, 8, 20);
    }

    /// <summary>Pushing harder always moves further. A curve with a flat part would look like a control
    /// that has stopped responding.</summary>
    [Fact]
    public void Pushing_further_always_gives_further()
    {
        var last = 0.0;
        for (var notches = 1; notches <= 20; notches++)
        {
            var give = Math.Abs(AlbumListView.StretchGive(-Notch * notches));
            Assert.True(give > last, $"notch {notches} gave {give:0.##}, no further than {last:0.##}");
            last = give;
        }
    }

    /// <summary>But each notch moves LESS than the one before it, which is what shows a limit. A linear
    /// movement would look like broken scrolling.</summary>
    [Fact]
    public void Each_notch_gives_less_than_the_one_before()
    {
        var previousStep = double.MaxValue;
        var last = 0.0;

        for (var notches = 1; notches <= 20; notches++)
        {
            var give = Math.Abs(AlbumListView.StretchGive(-Notch * notches));
            var step = give - last;

            Assert.True(step < previousStep, $"notch {notches} gave {step:0.##}, no less than {previousStep:0.##}");

            previousStep = step;
            last = give;
        }
    }

    /// <summary>And it never stops: however long the wheel turns there is always a little more, so the
    /// stretch never reaches a hard end of its own that would look like a second wall.</summary>
    [Fact]
    public void The_band_never_runs_out_of_give()
    {
        var far = Math.Abs(AlbumListView.StretchGive(-Notch * 400));
        var further = Math.Abs(AlbumListView.StretchGive(-Notch * 401));

        Assert.True(further > far);
    }

    /// <summary>But never so far that the tracklist looks like it scrolled: the songs stay inside their
    /// own area, whatever the wheel does.</summary>
    [Fact]
    public void No_amount_of_pushing_moves_the_songs_a_whole_area()
    {
        var hammered = Math.Abs(AlbumListView.StretchGive(-Notch * 10_000));

        // The tracklist's own height, from AlbumListTrackAreaHeight.
        Assert.True(hammered < 176 / 2.0, $"gave {hammered:0.##}");
    }
}
