using RPM.Core.Art;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the header's picture lands: how big it is drawn, which part of the photograph is on show, and
/// that moving it up the frame costs nothing. This file holds that promise.
/// </summary>
public class PicturePlacementTests
{
    // A wide header: a 1440px slot over a 224px band, with the record's breathing space at the right.
    private const double Slot = 1440, Band = 224, Breath = 70;

    /// <summary>A landscape photograph of a person: the frame is the whole of it, their head and
    /// shoulders in the middle, their face (brow to chin) inside that, and the eyes four tenths of the way
    /// down the face, which is where eyes usually are.</summary>
    private static CropFrame Frame(int eyeLine = 354)
        => new(new CropRect(0, 0, 1920, 1080),          // the frame
               new CropRect(600, 200, 700, 700),        // head and shoulders
               new CropRect(700, 250, 400, 260),        // the face, brow to chin
               eyeLine);

    /// <summary>The same photograph taken from further away: a face small enough that the header has
    /// room to spare, so the placement is free to use the eye line it prefers.</summary>
    private static CropFrame DistantFrame()
        => new(new CropRect(0, 0, 1920, 1080), new CropRect(600, 260, 700, 640),
               new CropRect(760, 300, 220, 120), 348);

    [Fact]
    public void Cutting_the_hair_does_not_buy_any_width()
    {
        // Letting the hair leave by the top of the window gives the FACE room in the part of the picture
        // that is not dissolving. If it also widened the picture, the width would come out of the same
        // budget and nothing would be gained. So the same person, photographed with a lot of hair above
        // the brow and with very little, comes out at the same size. Only the rows on show differ.
        //
        // (A frame with NO eye line would not show this. Without one, the placement does not know where
        // the face is, so it neither hangs it on the eye line nor keeps it clear of the dissolve. Those are
        // two different questions, and only this one is about the hair.)
        var bigHair = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 60, 700, 840),
                                    new CropRect(700, 250, 400, 260), 300);
        var cropped = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 230, 700, 670),
                                    new CropRect(700, 250, 400, 260), 300);

        var withHair = ArtDisplay.PlacePicture(bigHair, 1080, Slot, Band, Breath);
        var without = ArtDisplay.PlacePicture(cropped, 1080, Slot, Band, Breath);

        Assert.Equal(withHair.Scale, without.Scale, 6);
        Assert.Equal(withHair.DestW, without.DestW, 3);
        Assert.Equal(withHair.DestH, without.DestH, 3);
        Assert.Equal(withHair.SrcH, without.SrcH, 3);
    }

    [Fact]
    public void The_window_moves_down_the_photograph_to_hang_the_eyes()
    {
        // Somebody photographed low in the frame: the window slides down to bring them up the band. That is
        // the whole gesture: the hair above them leaves by the top of the window.
        var low = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 480, 700, 600),
                                new CropRect(700, 550, 400, 260), 654);   // eyes 0.4 down the face

        var place = ArtDisplay.PlacePicture(low, 1080, Slot, Band, Breath);

        Assert.True(place.SrcY > 0, "the window is still pinned to the top of the frame");
        Assert.InRange((654 - place.SrcY) / place.SrcH, 0.24, 0.44);
    }

    [Fact]
    public void A_face_too_small_to_magnify_that_far_takes_the_eye_line()
    {
        // The eye line is the rule for a face that cannot be brought up to its clearance size at all.
        // Magnifying it that far would pass MaxBackdropUpscale and blur the photograph, so the face is
        // drawn smaller. It has room to spare in the window and is hung where it reads best, eyes a little
        // above the middle. Every face that CAN reach its clearance size is placed by its chin instead (the
        // test below). That is nearly all of them.
        var speck = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 260, 700, 640),
                                  new CropRect(880, 330, 160, 30), 342);   // a face 30px tall

        var place = ArtDisplay.PlacePicture(speck, 1080, Slot, Band, Breath);

        Assert.Equal(3.0, place.Scale, 3);   // the upscale ceiling, not the clearance
        Assert.InRange((342 - place.SrcY) / place.SrcH, 0.32, 0.44);
    }

    [Fact]
    public void A_face_at_its_clearance_size_lands_its_eyes_a_third_of_the_way_down()
    {
        // The other rule, and the usual one: a single subject is zoomed until its face stands between the
        // headroom and the foot dissolve. Then the CHIN decides where the window sits, not the eye line.
        // The eyes land where they land, and that is not a number anyone chose. The arithmetic in
        // PlacePicture gives h0 + p(D - h0). For eyes four tenths down a face, that is 0.06 + 0.4 x 0.58, a
        // little under a third.
        var place = ArtDisplay.PlacePicture(DistantFrame(), 1080, Slot, Band, Breath);

        var eyesDownTheBand = (348 - place.SrcY) / place.SrcH;
        Assert.InRange(eyesDownTheBand, 0.26, 0.32);
    }

    [Fact]
    public void The_face_never_leaves_the_window()
    {
        // Whatever the eye line asks for, the brow stays in and the chin stays in.
        foreach (var eyes in new[] { 120, 300, 700, 1000 })
        {
            var place = ArtDisplay.PlacePicture(Frame(eyes), 1080, Slot, Band, Breath);
            var face = Frame(eyes).Keep;

            Assert.True(place.SrcY <= face.Y, $"the brow was cut with the eyes at {eyes}");
            Assert.True(place.SrcY + place.SrcH >= face.Y + face.Height, $"the chin was cut with the eyes at {eyes}");
        }
    }

    [Fact]
    public void The_window_never_leaves_the_photograph()
    {
        var place = ArtDisplay.PlacePicture(Frame(eyeLine: 1050), 1080, Slot, Band, Breath);

        Assert.True(place.SrcY >= 0);
        Assert.True(place.SrcY + place.SrcH <= 1080 + 0.001);
    }

    [Fact]
    public void A_big_face_makes_the_picture_narrower_so_it_clears_the_dissolve()
    {
        // A subject whose face lands in the foot dissolve goes soft. The picture always fills the band's
        // height, so a SMALLER picture is a window onto more of the photograph in the same space.
        // Everything in it rises, and the face comes up into the clear. The price is width, which is the
        // right thing to spend.
        var small = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                  new CropRect(760, 260, 220, 150), 300);   // a small face, high up
        var large = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                  new CropRect(700, 250, 400, 420), 300);   // a big one, chin far below

        var forSmall = ArtDisplay.PlacePicture(small, 1080, Slot, Band, Breath);
        var forLarge = ArtDisplay.PlacePicture(large, 1080, Slot, Band, Breath);

        Assert.True(forLarge.DestW < forSmall.DestW,
                    $"the picture did not give up any width: {forLarge.DestW} against {forSmall.DestW}");
        Assert.True(forLarge.SrcH > forSmall.SrcH, "and so it is not showing any more of the photograph");
    }

    [Fact]
    public void A_constrained_face_lands_between_the_headroom_and_the_dissolve()
    {
        // The arithmetic, end to end. For a face big enough to be the binding constraint, the scale is
        // B(D - h0)/F. The window's top is brow - h0·h. The eyes then fall about a third of the way down,
        // which is where a camera operator would usually put them.
        var brow = 250; var faceHeight = 260;                       // F
        var eyes = brow + (int)(0.4 * faceHeight);                  // p = 0.4 down the face
        var frame = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 180, 700, 720),
                                  new CropRect(700, brow, 400, faceHeight), eyes);

        var place = ArtDisplay.PlacePicture(frame, 1080, Slot, Band, Breath);

        double Down(double row) => (row - place.SrcY) / place.SrcH;
        Assert.InRange(Down(brow), 0.03, 0.10);                     // headroom, near h0 = 0.06
        Assert.InRange(Down(brow + faceHeight), 0.58, 0.66);        // the chin, clear of the dissolve
        Assert.InRange(Down(eyes), 0.24, 0.34);                     // …and the eyes on the upper third
    }

    [Fact]
    public void Narrowing_the_picture_moves_everything_towards_the_pivot()
    {
        // The communicating vessels: give up width by k and the window shows 1/k as many rows, so every
        // point moves by (its distance from the pivot) × (1 - k). Below the pivot things rise, above it
        // they come down. One gesture, both effects.
        var big = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                new CropRect(700, 250, 400, 150), 310);     // a small face: barely capped
        var bigger = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                   new CropRect(700, 250, 400, 300), 370);  // a big one: hard capped

        var wide = ArtDisplay.PlacePicture(big, 1080, Slot, Band, Breath);
        var narrow = ArtDisplay.PlacePicture(bigger, 1080, Slot, Band, Breath);

        Assert.True(narrow.DestW < wide.DestW, "the picture kept its width");
        // The chin of the constrained one is out of the dissolve. That is what the width bought. (Both
        // faces here are small enough for the subject rule to apply. A face big enough to push the picture
        // under half the header is stopped by the floor instead, and its chin is not clear of the dissolve.
        // See A_close_up_is_zoomed_until_it_covers_half_the_header.)
        var chin = bigger.Keep.Y + bigger.Keep.Height;
        Assert.True((chin - narrow.SrcY) / narrow.SrcH <= 0.66);
    }

    /// <summary>
    /// A close-up is drawn smaller than an ordinary shot, but only down to the floor. Past the floor it is
    /// zoomed until it covers half the header, and its chin goes into the dissolve.
    ///
    /// <para>The face clearance zooms a close-up OUT until the face stands between the headroom and the
    /// foot dissolve. For a face this big that leaves a picture covering a fifth of the header, with wash
    /// beside it. The floor stops that. The subject keeps the same size on every track until holding it
    /// would leave the picture under <c>MinBandCover</c>. Then the header wins.</para>
    ///
    /// <para>The chin assertion below is the price, stated rather than left implied. For a face this size
    /// the chin is IN the foot dissolve, which is what the clearance exists to prevent.</para>
    /// </summary>
    [Fact]
    public void A_close_up_is_zoomed_until_it_covers_half_the_header()
    {
        var ordinary = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                     new CropRect(700, 250, 400, 260), 354);
        var closeUp = new CropFrame(new CropRect(0, 0, 1920, 1080), new CropRect(600, 200, 700, 700),
                                    new CropRect(700, 250, 400, 500), 450);   // a face twice as tall

        var small = ArtDisplay.PlacePicture(ordinary, 1080, Slot, Band, Breath);
        var closer = ArtDisplay.PlacePicture(closeUp, 1080, Slot, Band, Breath);

        // Still the smaller zoom of the two: the subject rule is what draws the ordinary shot, and the
        // close-up only stops following it where the floor catches.
        Assert.True(closer.Scale < small.Scale, "the close-up was not zoomed out at all");
        Assert.Equal(small.DestH, closer.DestH, 3);

        // The floor, not the clearance. The clearance asks for 0.26 on a face this tall, which draws the
        // picture 499 wide in a header of 1440.
        Assert.True(closer.Scale > Band * (0.64 - 0.06) / 500,
                    "the close-up was drawn at its clearance size and left the header empty");
        Assert.True(closer.DestW >= (0.5 * Slot) - 1,
                    $"the picture covered {closer.DestW} of a {Slot} header");

        // What it costs. The headroom above the brow is kept, as ever; the chin is in the dissolve.
        Assert.InRange((closeUp.Keep.Y - closer.SrcY) / closer.SrcH, 0.03, 0.10);
        var chin = closeUp.Keep.Y + closeUp.Keep.Height;
        Assert.True((chin - closer.SrcY) / closer.SrcH > 0.66, "this face fitted after all");
    }

    [Fact]
    public void A_photograph_too_narrow_to_reach_is_zoomed_until_it_does()
    {
        // The floor exists for this case. A tight portrait has no picture on either side of its subject.
        // Holding the subject at the band's size would draw it a couple of hundred points wide in a header
        // of 1440, and the rest would be wash.
        var portrait = new CropFrame(new CropRect(0, 0, 1000, 1400), new CropRect(150, 100, 700, 1200),
                                     new CropRect(300, 200, 400, 700), 480);

        var place = ArtDisplay.PlacePicture(portrait, 1400, Slot, Band, Breath);

        Assert.True(place.Scale > Band * (0.64 - 0.06) / 700, "the subject size was held and the header was not");
        Assert.Equal(0.5 * Slot, place.DestW, 0);
        Assert.Equal(Band, place.DestH, 1);
    }

    [Fact]
    public void A_photograph_that_cannot_reach_at_any_zoom_stays_legible()
    {
        // Where the floor gives way, and the only rule that outranks it. Reaching half of a 1440 header
        // with a frame 200 points across asks for 3.6x, past MaxBackdropUpscale, and a photograph
        // magnified that far is not a picture of anybody. It is drawn at the ceiling and stops short.
        var tiny = new CropFrame(new CropRect(0, 0, 200, 150), new CropRect(20, 10, 160, 130),
                                 new CropRect(60, 20, 80, 60), 44);

        var place = ArtDisplay.PlacePicture(tiny, 150, Slot, Band, Breath);

        Assert.Equal(3.0, place.Scale, 3);
        Assert.True(place.DestW < 0.5 * Slot, "the ceiling let the floor magnify it into mush");
    }

    [Fact]
    public void The_picture_fills_the_band_and_stops_short_of_the_record()
    {
        var place = ArtDisplay.PlacePicture(Frame(), 1080, Slot, Band, Breath);

        Assert.Equal(Band, place.DestH, 1);
        Assert.True(place.DestX + place.DestW <= Slot - Breath + 0.5, "the picture ran into the record");
        Assert.True(place.DestW > 0);
    }

    // ── the same photograph in headers of two widths ──

    /// <summary>A group: five people side by side, so the keep box is wide and short. A wide subject in a
    /// narrow header must not let "fit the whole frame in the room" decide the size before the band or the
    /// faces do.</summary>
    private static CropFrame Group()
        => new(new CropRect(0, 0, 1920, 1080),
               new CropRect(200, 150, 1500, 800),
               new CropRect(300, 200, 1300, 150),
               260);   // eyes four tenths down the face, where eyes are

    [Fact]
    public void A_narrower_header_shows_less_of_the_picture_at_the_same_size()
    {
        // The subject's size follows the BAND and the faces, and a resize leaves both alone. The room
        // decides how much of the photograph is on show and nothing else. The subject must open at the same
        // size in a small window as in a maximised one.
        var wide = ArtDisplay.PlacePicture(Group(), 1080, slotWidth: 2399, band: 224, breath: 0);
        var narrow = ArtDisplay.PlacePicture(Group(), 1080, slotWidth: 1200, band: 224, breath: 0);

        Assert.Equal(wide.Scale, narrow.Scale, 3);
        Assert.Equal(wide.DestH, narrow.DestH, 3);
        Assert.True(narrow.DestW < wide.DestW, "the narrower header drew as much picture as the wide one");
        Assert.True(narrow.SrcW < wide.SrcW, "the narrower header showed as much of the photograph");
    }

    [Fact]
    public void A_header_too_narrow_for_the_faces_shows_fewer_of_them()
    {
        // What a header narrower than the subject gives up. Not the zoom. The subject's size is the thing
        // that stays the same on every track, so what gives way is the far end of the photograph. The
        // picture is still drawn inside its own room, not off the side.
        var wide = ArtDisplay.PlacePicture(Group(), 1080, slotWidth: 2399, band: 224, breath: 0);
        var tiny = ArtDisplay.PlacePicture(Group(), 1080, slotWidth: 420, band: 224, breath: 0);

        Assert.Equal(wide.Scale, tiny.Scale, 3);
        Assert.True(tiny.SrcW < wide.SrcW, "the narrow header showed as much of the photograph");
        Assert.True(tiny.DestW <= 420 + 0.5, $"the picture was drawn {tiny.DestW} wide in a room of 420");
    }

    // ── keeping a wash: whether the blurred copy still lands under the picture ──
    //
    // A 1600×900 window with the record taking 150 off the right, so the slot is 1450.

    private const double Window = 1600, Height = 900, Inset = 150;

    private static ArtDisplay.PicturePlacement PlaceIn(double window)
        => ArtDisplay.PlacePicture(Frame(), 1080, window - Inset, Band, Breath);

    /// <summary>The canvas the wash for this window is drawn on: bigger than the window, so a window that
    /// grows does not uncover the end of it (ArtDisplay.WashReach).</summary>
    private static ArtDisplay.WashCanvas Canvas => ArtDisplay.WashCanvasFor(Window, Height);

    [Fact]
    public void A_wash_registers_with_the_picture_it_was_drawn_for()
    {
        var place = PlaceIn(Window);

        Assert.True(ArtDisplay.Registers(place, Canvas, place, Window, Height));
    }

    [Fact]
    public void A_wash_survives_a_window_narrowed_under_a_refitted_picture()
    {
        // The ordinary resize, and the one worth being exact about. The picture is re-fitted rather than
        // re-framed (RefitPicture), the wash is stood at the size it was drawn with its right edge on the
        // window's, and both therefore move by the same number of points: the blurred copy lands under the
        // sharp one to the pixel, and the expensive render is skipped.
        var was = PlaceIn(Window);
        var now = ArtDisplay.RefitPicture(was, Frame(), Window - 400 - Inset, Band, Breath);

        Assert.True(ArtDisplay.Registers(was, Canvas, now, Window - 400, Height));
    }

    /// <summary>
    /// A window dragged wider is covered by the copy already on screen.
    /// </summary>
    /// <remarks>
    /// A wash is never stretched, and the copy is drawn bigger than its window (ArtDisplay.WashReach) for
    /// that reason. If the copy were drawn for the window exactly, it would end at a hard line the moment
    /// the window grew, with the flat ground colour beyond that line. For example, maximising a window from
    /// 1,084 points leaves an edge about 1,400 points into a 2,560 header for about a second.
    /// </remarks>
    [Fact]
    public void A_wash_survives_the_window_being_dragged_wider()
    {
        var wider = Window * 1.12;
        var was = PlaceIn(Window);
        var now = ArtDisplay.RefitPicture(was, Frame(), wider - Inset, Band, Breath);

        Assert.True(ArtDisplay.Registers(was, Canvas, now, wider, Height));
    }

    [Fact]
    public void A_wash_does_not_survive_a_window_past_what_it_covers()
    {
        // The reach is not unlimited, and past it the copy really does stop short: the fade at its outer
        // edges is what the window sees then (RenderWash), and a fresh copy is rendered for the new size.
        var was = PlaceIn(Window);
        var past = Canvas.Width + 10;

        Assert.False(ArtDisplay.Registers(was, Canvas, PlaceIn(past), past, Height));
        Assert.False(ArtDisplay.Registers(was, Canvas, was, Window, Canvas.Height + 10));
    }

    [Fact]
    public void A_wash_does_not_survive_the_picture_being_framed_again()
    {
        // The other half of the test: a copy that still reaches, under a picture that has been framed
        // again rather than re-fitted. The photograph is at a different size in a different place, so
        // what is under the picture's dissolving edges is the wrong part of it, and the seam that
        // registration exists to prevent is back.
        var was = PlaceIn(Window);
        var now = ArtDisplay.PlacePicture(Frame(), 1080, Window - 700 - Inset, Band, Breath);

        Assert.False(ArtDisplay.Registers(was, Canvas, now, Window - 700, Height));
    }
}
