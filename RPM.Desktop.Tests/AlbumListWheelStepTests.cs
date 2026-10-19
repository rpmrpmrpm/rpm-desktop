using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// One wheel notch over a record's tracklist moves the tracklist, and only the tracklist.
///
/// <para>The hard cases are at the ENDS: if the notch that reaches the last song is passed to the shelf
/// behind it, the record being read scrolls off the screen. Uno cannot be asked about this: it marks a
/// clamped notch UNhandled, it clamps by moving to the end anyway, and its offsets lag behind a
/// one-second animation, so they still show "room left" at the moment it clamps. So the step is computed
/// here and the notch is always marked handled. These tests check that.</para>
/// </summary>
public class AlbumListWheelStepTests
{
    /// <summary>A tracklist showing eight songs of twenty-four: 176 of viewport, 528 of songs, so 352 of
    /// room to scroll through. Room is clearly more than a viewport, so the tests below can tell the
    /// proportional step and the viewport cap apart.</summary>
    private const double Viewport = 176;
    private const double Room = 352;

    /// <summary>The Windows arithmetic, which every case below uses: a delta of 120 per detent. Named
    /// explicitly, because the same numbers mean something else on a Mac (see the macOS cases at the end
    /// of this file).</summary>
    private static double Target(int delta, double offset)
        => AlbumListView.WheelTarget(delta, offset, Room, Viewport, AlbumListView.WheelUnit.Notch);

    [Fact]
    public void A_notch_down_moves_three_songs_down()
        => Assert.Equal(66, Target(-120, 0));

    [Fact]
    public void A_notch_up_moves_three_songs_up()
        => Assert.Equal(34, Target(+120, 100));

    /// <summary>The ends matter most. A notch that asks for more room than is left stops at the end and
    /// no further, and the caller marks it handled either way, so it is not passed on to the shelf.</summary>
    [Fact]
    public void A_notch_past_the_last_song_stops_at_the_last_song()
        => Assert.Equal(Room, Target(-120, Room - 10));

    [Fact]
    public void A_notch_past_the_first_song_stops_at_the_first_song()
        => Assert.Equal(0, Target(+120, 10));

    [Fact]
    public void A_notch_at_the_bottom_goes_nowhere()
        => Assert.Equal(Room, Target(-120, Room));

    [Fact]
    public void A_notch_at_the_top_goes_nowhere()
        => Assert.Equal(0, Target(+120, 0));

    /// <summary>A fast spin arrives as one large delta rather than several small ones (for example 360 or
    /// 1080), so the step must scale with it. Twice the delta, twice the songs.</summary>
    [Fact]
    public void A_bigger_notch_travels_proportionally_further()
    {
        Assert.Equal(66, Target(-120, 0));
        Assert.Equal(132, Target(-240, 0));
    }

    /// <summary>But never more than a viewport in one notch. -1080 is nine notches, which would otherwise
    /// move a tracklist most of the way through a long record with one quick turn of the wheel.
    /// </summary>
    [Fact]
    public void No_notch_travels_further_than_a_viewport()
    {
        Assert.Equal(Viewport, Target(-1080, 0));
        Assert.Equal(Room - Viewport, Target(+1080, Room));
    }

    /// <summary>A record whose songs all fit is handled by the caller, which passes the notch to the shelf
    /// instead of calling this. If it is called anyway, the result stays at 0 and is never negative.
    /// </summary>
    [Fact]
    public void A_tracklist_with_no_room_stays_at_nothing()
    {
        Assert.Equal(0, AlbumListView.WheelTarget(-120, 0, room: 0, viewport: Viewport, AlbumListView.WheelUnit.Notch));
        Assert.Equal(0, AlbumListView.WheelTarget(+120, 0, room: 0, viewport: Viewport, AlbumListView.WheelUnit.Notch));
    }

    // ── the two platforms do not send the same number ──
    //
    // Windows sends WM_MOUSEWHEEL's GET_WHEEL_DELTA_WPARAM: 120 to a detent. macOS sends
    // NSEvent.scrollingDeltaY directly: the points to travel, and the shelf's own scroller moves by exactly
    // those. Nothing in the framework normalises the two. If one is read as the other, a tracklist moves
    // 0.55 of what the shelf beside it moves for the same gesture. These tests check both units.

    /// <summary>Deltas from a real gesture on a MacBook trackpad, taken from the app's own wheel log.
    /// None is near 120, and they rise gradually instead of repeating, so they are not detents.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(-8)]
    [InlineData(-18)]
    [InlineData(-23)]
    public void On_a_mac_the_delta_is_already_the_distance(int delta)
        => Assert.Equal(delta, AlbumListView.WheelStep(delta, AlbumListView.WheelUnit.Point));

    /// <summary>One detent of a Windows wheel is three songs of twenty-two.</summary>
    [Fact]
    public void On_windows_the_delta_is_a_hundred_and_twenty_to_the_detent()
        => Assert.Equal(66, AlbumListView.WheelStep(-120, AlbumListView.WheelUnit.Notch) * -1);

    /// <summary>The size of the error if a Mac delta is read as a notch. 120 notch units move 66 points,
    /// so a delta read as a notch moves 0.55 of the same delta read as points: short by a factor of
    /// 1.82. The tracklist still moves, but does not keep up with its own shelf.</summary>
    [Fact]
    public void A_mac_delta_read_as_a_notch_travels_a_little_over_half_as_far()
    {
        var asPoints = Math.Abs(AlbumListView.WheelStep(-18, AlbumListView.WheelUnit.Point));
        var asNotches = Math.Abs(AlbumListView.WheelStep(-18, AlbumListView.WheelUnit.Notch));

        Assert.Equal(0.55, asNotches / asPoints, 3);
    }

    /// <summary>On a Mac the tracklist moves exactly as far as the shelf behind it, so the same gesture
    /// moves the same distance wherever the pointer is. The shelf's figures are from the app's own log:
    /// a delta of -18 moved it from 13 to 31.</summary>
    [Fact]
    public void On_a_mac_a_tracklist_travels_what_the_shelf_travels()
        => Assert.Equal(31 - 13, AlbumListView.WheelStep(-18, AlbumListView.WheelUnit.Point) * -1);

    /// <summary>One real trackpad gesture from the app's log, checked against the stretch at the end of a
    /// tracklist. Both readings give a clearly visible movement, so the unit does not decide whether the
    /// stretch shows on a Mac. Eight to twenty is the same range AlbumListStretchTests uses for the
    /// Windows path, and the point reading is inside it.</summary>
    [Fact]
    public void A_whole_mac_push_gives_a_visible_band_read_either_way()
    {
        int[] push = [-1, -4, -8, -18, -23, -14, -13, -7, -5];

        var asPoints = push.Sum(d => AlbumListView.WheelStep(d, AlbumListView.WheelUnit.Point));
        var asNotches = push.Sum(d => AlbumListView.WheelStep(d, AlbumListView.WheelUnit.Notch));

        Assert.InRange(Math.Abs(AlbumListView.StretchGive(asPoints)), 8, 20);
        Assert.True(Math.Abs(AlbumListView.StretchGive(asNotches)) > 8,
            "the old reading was visible too — this was never why the band did not show");
    }
}
