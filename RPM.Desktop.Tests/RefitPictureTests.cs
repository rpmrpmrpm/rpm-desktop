using RPM.Core.Art;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a window dragged wider or narrower does to the header's picture: nothing, except how much of it is
/// on show. The promise this file holds is that a re-fit moves no drawn pixel. That is what lets the swap
/// happen without a dissolve (see ArtDisplay.Trims).
/// </summary>
public class RefitPictureTests
{
    // A 1600-wide window with the record taking 150 off the right, and the picture stopping 70 short of
    // the slot's edge. 900 is narrow enough that the room actually binds on the picture below; 1300 is
    // not, which is its own case (Nothing_is_trimmed_off_a_header_the_picture_still_fits).
    private const double Wide = 1600, Narrow = 900, Roomy = 1300, Inset = 150, Band = 224, Breath = 70;

    private static double Slot(double window) => window - Inset;

    /// <summary>A landscape photograph of a person, off to the right of the frame.</summary>
    private static CropFrame Frame()
        => new(new CropRect(0, 0, 1920, 1080),
               new CropRect(600, 200, 700, 700),
               new CropRect(900, 250, 400, 260),
               354);

    private static ArtDisplay.PicturePlacement Placed(double window)
        => ArtDisplay.PlacePicture(Frame(), 1080, Slot(window), Band, Breath);

    private static ArtDisplay.PicturePlacement Refit(ArtDisplay.PicturePlacement was, double window)
        => ArtDisplay.RefitPicture(was, Frame(), Slot(window), Band, Breath);

    /// <summary>How far left of the WINDOW's right edge one column of the photograph is drawn. The tile is
    /// stood with its own right edge against the window's, less the record's reach (ArtDisplay.RightEdge),
    /// so this is the whole of where a pixel lands on screen.</summary>
    private static double FromTheRight(ArtDisplay.PicturePlacement place, double window, double sourceX)
        => (Slot(window) - (place.DestX + ((sourceX - place.SrcX) * place.Scale))) + Inset;

    [Fact]
    public void A_narrower_header_shows_less_of_the_same_picture()
    {
        var was = Placed(Wide);
        var now = Refit(was, Narrow);

        // The zoom, the rows on show and the edge against the record: all held.
        Assert.Equal(was.Scale, now.Scale, 6);
        Assert.Equal(was.SrcY, now.SrcY, 6);
        Assert.Equal(was.SrcH, now.SrcH, 6);
        Assert.Equal(was.DestH, now.DestH, 3);
        Assert.Equal(was.SrcX + was.SrcW, now.SrcX + now.SrcW, 3);

        // The width is the only thing that gave way.
        Assert.True(now.DestW < was.DestW, "a narrower header drew as much picture as a wide one");
        Assert.Equal(Slot(Narrow) - Breath, now.DestX + now.DestW, 3);
    }

    [Fact]
    public void Nothing_that_is_still_on_screen_has_moved()
    {
        // The point of the whole exercise. Take three columns of the photograph inside the narrowed
        // picture, and ask where each one is drawn. Measure from the window's right edge, which is the
        // edge the record, the picture and the layers are all stood against. The positions are identical,
        // so the swap has nothing to dissolve.
        var was = Placed(Wide);
        var now = Refit(was, Narrow);

        foreach (var column in new[] { now.SrcX, now.SrcX + (now.SrcW / 2), now.SrcX + now.SrcW })
            Assert.Equal(FromTheRight(was, Wide, column), FromTheRight(now, Narrow, column), 3);
    }

    [Fact]
    public void Nothing_is_trimmed_off_a_header_the_picture_still_fits()
    {
        // A window narrowed by less than the air the picture already had beside it. The placement gave
        // width up to centre the face, so there is room to lose before any of the photograph goes, and
        // what shrinks is the wash beside it.
        var was = Placed(Wide);
        var now = Refit(was, Roomy);

        Assert.True(was.DestW < Slot(Roomy) - Breath, "the fixture's picture already fills the roomy header");
        Assert.Equal(was.SrcX, now.SrcX, 3);
        Assert.Equal(was.SrcW, now.SrcW, 3);
        Assert.Equal(was.DestW, now.DestW, 3);
    }

    [Fact]
    public void A_wider_header_shows_more_of_the_same_picture()
    {
        // The other direction. The re-fit trims only, so a photograph framed in a small window stays that
        // width when the window is made large, and sits in the middle of a header it does not fill.
        // Instead, the re-fit asks the same question a fresh placement asks (ShownWidth), and lets the
        // picture out to match, at the same zoom, from the same edge.
        var was = Placed(Narrow);
        var now = Refit(was, 2600);

        Assert.Equal(was.Scale, now.Scale, 6);
        Assert.Equal(was.SrcY, now.SrcY, 6);
        Assert.Equal(was.SrcH, now.SrcH, 6);
        Assert.Equal(was.SrcX + was.SrcW, now.SrcX + now.SrcW, 3);   // held: the edge at the record

        Assert.True(now.DestW > was.DestW, "a wider header drew no more of the photograph");
        Assert.Equal(Slot(2600) - Breath, now.DestX + now.DestW, 3);
    }

    [Fact]
    public void A_refit_is_recognised_as_one_and_a_re_framing_is_not()
    {
        var was = Placed(Wide);

        Assert.True(ArtDisplay.Trims(was, Refit(was, Narrow)));

        // A band that moved is a picture framed again: a different zoom on different rows. That one must be
        // dissolved.
        Assert.False(ArtDisplay.Trims(was, ArtDisplay.PlacePicture(Frame(), 1080, Slot(Wide), Band * 1.5, Breath)));
    }

    /// <summary>
    /// A header the framing cannot reach across is framed again, not re-fitted.
    /// </summary>
    /// <remarks>
    /// The fault this guards against: a window maximized from a small one ended up with a picture far too
    /// small for its room. A re-fit holds the zoom and lets the picture out only as far as the photograph
    /// goes. What the bake asks before it crops is whether the framing still carries the room
    /// (ArtDisplay.Carries, from the reach stored with the framing). This test puts that question to a
    /// real placement.
    /// </remarks>
    [Fact]
    public void A_framing_made_for_a_small_header_does_not_carry_a_maximized_one()
    {
        var frame = Frame();
        var small = ArtDisplay.PlacePicture(frame, 1080, 1116, Band, breath: 0);

        // What the bake stores as that framing's reach: everything the crop kept to the left of the
        // picture's right edge, at the zoom it was framed at.
        var reach = (small.SrcX + small.SrcW - frame.Rect.X) * small.Scale;

        Assert.True(ArtDisplay.Carries(reach, 1116), "it did not even carry the header it was framed for");
        Assert.False(ArtDisplay.Carries(reach, 2398), "a maximized header was treated as a re-fit");

        // This is not a guess about the arithmetic. The re-fit really does come out under half.
        var letOut = ArtDisplay.RefitPicture(small, frame, 2398, Band, breath: 0);
        Assert.True(letOut.DestW < 0.5 * 2398, $"the re-fit covered {letOut.DestW} of 2398 after all");

        // The answer to that is the chooser, which fills the wider header on its own terms.
        var framedAgain = ArtDisplay.PlacePicture(frame, 1080, 2398, Band, breath: 0);
        Assert.True(framedAgain.DestW >= (0.5 * 2398) - 1,
                    $"framing it again covered {framedAgain.DestW} of 2398");
    }

    /// <summary>
    /// A header narrower than the one a framing was chosen for is framed again, not re-fitted.
    /// </summary>
    /// <remarks>
    /// The other half of the fault above. A maximized header is
    /// wide enough that the floor under the cover (MinBandCover) beats the size the band wants the subject
    /// at. So the zoom it picks is a fact about a room of that width. Carrying that zoom into a restored
    /// window draws the maximized subject across a header that asks for half as much of it. Framing it
    /// again draws the right amount.
    /// </remarks>
    [Fact]
    public void A_framing_the_room_chose_is_not_carried_into_a_narrower_header()
    {
        var frame = Frame();
        var maximized = ArtDisplay.ChooseZoom(frame, 2398, Band);
        var restored = ArtDisplay.ChooseZoom(frame, 1117, Band);

        // The fixture's photograph is one of the ones this is about: the floor decides its zoom across a
        // maximized header and the band decides it across a restored one, so the two rooms disagree.
        Assert.True(maximized.ByRoom, "the floor did not decide the maximized zoom");
        Assert.False(restored.ByRoom, "the floor decided the restored zoom too, so there is nothing to tell apart");
        Assert.True(maximized.Scale > restored.Scale, "the two rooms chose the same zoom after all");

        var framed = Framed(frame, 2398, maximized.ByRoom);

        Assert.True(ArtDisplay.FramedForThisRoom(framed, 2398));
        Assert.True(ArtDisplay.FramedForThisRoom(framed, 2390), "a header a few points narrower was framed again");
        Assert.False(ArtDisplay.FramedForThisRoom(framed, 1117), "the maximized framing was re-fitted into a restored window");

        // …and it is not the reach that catches it: that question says hold, which is why this one exists.
        Assert.True(ArtDisplay.Carries(framed.Reach, 1117));

        // What holding it would have drawn, against what framing it again draws.
        var refitted = ArtDisplay.RefitPicture(framed.Place, frame, 1117, Band, breath: 0);
        var again = ArtDisplay.PlacePicture(frame, 1080, 1117, Band, breath: 0);

        Assert.True(refitted.Scale > again.Scale + 0.05,
                    $"the re-fit drew the subject at {refitted.Scale:F3} where the chooser asks {again.Scale:F3}");
        Assert.True(again.DestW >= 0.5 * 1117, $"framing it again covered {again.DestW} of 1117");
    }

    [Fact]
    public void A_framing_the_band_chose_is_held_at_any_width()
    {
        // The promise the re-fit is for, and the one the test above must not have taken away: where the
        // subject's own size decided the zoom, that number is the same in every room, so a resize is a
        // re-fit in both directions and the picture is never re-cut.
        var frame = Frame();
        var framed = Framed(frame, 1117, ArtDisplay.ChooseZoom(frame, 1117, Band).ByRoom);

        Assert.False(framed.ByRoom, "the fixture no longer goes by the band at 1117");
        Assert.True(ArtDisplay.FramedForThisRoom(framed, 900));
        Assert.True(ArtDisplay.FramedForThisRoom(framed, 2398));
    }

    /// <summary>A framing as the bake stores it: the placement for that room, what it can reach, and
    /// which rule chose its zoom.</summary>
    private static ArtDisplay.FramedPicture Framed(CropFrame frame, double slot, bool byRoom)
    {
        var place = ArtDisplay.PlacePicture(frame, 1080, slot, Band, breath: 0);
        return new ArtDisplay.FramedPicture(
            Band, slot / Band, place, "auto",
            (place.SrcX + place.SrcW - frame.Rect.X) * place.Scale, byRoom, slot);
    }

    [Fact]
    public void A_picture_that_cannot_be_measured_is_left_alone()
    {
        // Nothing to hold: a placement with no scale or no window is not re-fitted into anything, it is
        // handed back as it came. The caller's own answer (PlacePicture) is what fills that header.
        var empty = new ArtDisplay.PicturePlacement(0, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.Equal(empty, ArtDisplay.RefitPicture(empty, Frame(), Slot(Narrow), Band, Breath));
    }
}
