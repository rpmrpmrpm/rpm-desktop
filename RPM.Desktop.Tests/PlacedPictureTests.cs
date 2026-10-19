using RPM.Core.Art;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where a HAND-PLACED picture lands. This is the counterpart of <see cref="PicturePlacementTests"/>, with
/// the opposite rule. The automatic placement works out its own answer. This must do exactly what it is
/// told and nothing else. A person is looking at a preview while they decide, so a placement that improves
/// on their instructions would show a preview that is not true.
/// </summary>
public class PlacedPictureTests
{
    // The same header the automatic tests use: a 1440px slot over a 224px band, the record's breathing
    // space at the right. The picture's own room is 1370 across, so the shape it is cut to is ~6.1:1.
    private const double Slot = 1440, Band = 224, Breath = 70;
    private const double Room = Slot - Breath;

    private const int SourceWidth = 3000, SourceHeight = 2000;

    /// <summary>
    /// A zoom at which this photograph has room to move. This is the least obvious part of a header with
    /// this shape. A 3:2 picture cut to 6:1 uses its FULL width at the far end of the zoom, so there is
    /// nothing to pan sideways until the picture is zoomed in. See
    /// <see cref="An_ordinary_photograph_fully_zoomed_out_has_no_room_to_move_sideways"/>, which states
    /// this directly.
    /// </summary>
    private const double Roomy = 0.15;

    private static ArtDisplay.PicturePlacement Place(ArtFraming framing, int width = SourceWidth, int height = SourceHeight)
        => ArtDisplay.PlaceFramed(framing, width, height, Slot, Band, Breath);

    private static ArtFraming Covering(double centreX, double centreY, double height)
        => new(centreX, centreY, height, 1);

    // ── it fills the band, and it fills the room ──

    [Fact]
    public void The_picture_fills_the_band()
    {
        foreach (var height in new[] { 0.2, 0.5, 0.9 })
            Assert.Equal(Band, Place(Covering(0.5, 0.5, height)).DestH, 3);
    }

    [Fact]
    public void The_picture_fills_the_room_beside_the_record()
    {
        // As long as the photograph has the width for it. Past that, the reach falls away rather than the
        // zoom being overruled. The case below shows this.
        foreach (var height in new[] { 0.08, 0.15, 0.24 })
            Assert.Equal(Room, Place(Covering(0.5, 0.5, height)).DestW, 3);
    }

    [Fact]
    public void Zoomed_out_past_what_the_photograph_can_reach_across_it_stops_short()
    {
        var fill = ArtFraming.FillHeightFor(SourceWidth, SourceHeight, Room / Band);

        var atFill = Place(Covering(0.5, 0.5, fill));
        var further = Place(Covering(0.5, 0.5, fill * 1.6));

        Assert.Equal(Room, atFill.DestW, 3);
        Assert.True(further.DestW < Room);
        Assert.Equal(Band, further.DestH, 3);                        // still the full band
        Assert.Equal(Room, further.DestX + further.DestW, 3);        // still against the record
    }

    [Fact]
    public void It_is_right_aligned_against_the_record_like_the_automatic_one()
    {
        var place = Place(Covering(0.5, 0.5, 0.5));

        Assert.Equal(Room, place.DestX + place.DestW, 3);
        Assert.Equal(0, place.DestY, 3);
    }

    [Fact]
    public void A_photograph_too_narrow_to_reach_across_stops_short_and_stays_right_aligned()
    {
        // A square picture at this shape: the window is capped by its own width, so what is drawn is as
        // wide as the photograph allows and no wider. It keeps its edge against the record. The automatic
        // placement does the same with a picture it cannot widen.
        var place = Place(Covering(0.5, 0.5, 1.0), width: 1200, height: 1200);

        Assert.True(place.DestW < Room);
        Assert.Equal(Room, place.DestX + place.DestW, 3);
        Assert.Equal(Band, place.DestH, 3);
        Assert.Equal(1200, place.SrcH, 3);   // the ZOOM was honoured; it is the reach that gave way
    }

    // ── it shows what it was told to show ──

    [Fact]
    public void The_window_is_where_the_framing_put_it()
    {
        var framing = Covering(0.30, 0.65, 0.4);
        var place = Place(framing);

        var window = framing.Window(SourceWidth, SourceHeight, Room / Band);

        Assert.Equal(window.X, place.SrcX, 3);
        Assert.Equal(window.Y, place.SrcY, 3);
        Assert.Equal(window.Width, place.SrcW, 3);
        Assert.Equal(window.Height, place.SrcH, 3);
    }

    [Fact]
    public void Moving_the_framing_left_moves_the_window_left_and_nothing_else()
    {
        var was = Place(Covering(0.5, 0.5, Roomy));
        var now = Place(Covering(0.4, 0.5, Roomy));

        Assert.True(now.SrcX < was.SrcX);
        Assert.Equal(was.SrcY, now.SrcY, 3);
        Assert.Equal(was.SrcW, now.SrcW, 3);
        Assert.Equal(was.Scale, now.Scale, 6);
        Assert.Equal(was.DestX, now.DestX, 3);   // the picture does not move in the header — its content does
    }

    [Fact]
    public void Zooming_in_shows_less_of_the_photograph_at_a_larger_scale()
    {
        var out_ = Place(Covering(0.5, 0.5, 0.20));
        var in_ = Place(Covering(0.5, 0.5, 0.10));

        Assert.Equal(out_.SrcH / 2, in_.SrcH, 3);
        Assert.Equal(out_.Scale * 2, in_.Scale, 6);

        // The header is covered exactly the same either way. That is the point of cutting the picture to
        // the header's shape.
        Assert.Equal(out_.DestW, in_.DestW, 3);
        Assert.Equal(out_.DestH, in_.DestH, 3);
    }

    [Fact]
    public void A_framing_off_the_edge_of_the_photograph_is_brought_back_rather_than_cropping_air()
    {
        var place = Place(Covering(5, -3, Roomy));

        Assert.True(place.SrcX >= 0);
        Assert.True(place.SrcY >= 0);
        Assert.True(place.SrcX + place.SrcW <= SourceWidth + 0.001);
        Assert.True(place.SrcY + place.SrcH <= SourceHeight + 0.001);
    }

    [Fact]
    public void An_ordinary_photograph_fully_zoomed_out_has_no_room_to_move_sideways()
    {
        // A 16:9 photograph, as most backdrops are, into a header six times wider than it is tall. Zoomed
        // all the way out, the window is the picture's FULL width and a band of its rows. So across does
        // nothing and down does everything. That is what the sliders will show, and it is correct. It is
        // also what the automatic placement picks for the same picture. The two agree by arithmetic, not
        // because one was made to match the other.
        var whole = ArtFraming.FillHeightFor(1920, 1080, Room / Band);

        var left = ArtDisplay.PlaceFramed(Covering(0.1, 0.5, whole), 1920, 1080, Slot, Band, Breath);
        var right = ArtDisplay.PlaceFramed(Covering(0.9, 0.5, whole), 1920, 1080, Slot, Band, Breath);

        Assert.Equal(1920, left.SrcW, 3);
        Assert.Equal(left.SrcX, right.SrcX, 3);

        // Zooming in gives that freedom back.
        var closer = ArtDisplay.PlaceFramed(Covering(0.1, 0.5, whole / 2), 1920, 1080, Slot, Band, Breath);
        Assert.True(closer.SrcW < 1920);
    }

    [Fact]
    public void A_framing_read_off_the_choosers_own_placement_reproduces_it_exactly()
    {
        // THE promise this whole type exists for. The placement window opens on whatever is in force and
        // shows a model of the header. If a hand-made framing cannot say exactly what the chooser said, the
        // picture jumps the moment the window opens, and every adjustment after that is made against
        // something that is not on screen.
        //
        // This is deliberately a placement the chooser really produces and a simple window cannot: TALLER
        // than the header's shape allows (it shows more rows than a filling window could) and NARROWER than
        // the room (the price it paid to centre the face).
        var frame = new CropFrame(new CropRect(0, 0, 1920, 1080),
                                  new CropRect(600, 200, 700, 700),
                                  new CropRect(700, 250, 400, 260),
                                  354);
        var chosen = ArtDisplay.PlacePicture(frame, 1080, Slot, Band, Breath);

        var framing = ArtFraming.From(chosen.SrcX, chosen.SrcY, chosen.SrcW, chosen.SrcH,
                                      1920, 1080, ArtDisplay.PlacedAspect(Slot, Band, Breath));
        var placed = ArtDisplay.PlaceFramed(framing, 1920, 1080, Slot, Band, Breath);

        Assert.Equal(chosen.SrcX, placed.SrcX, 2);
        Assert.Equal(chosen.SrcY, placed.SrcY, 2);
        Assert.Equal(chosen.SrcW, placed.SrcW, 2);
        Assert.Equal(chosen.SrcH, placed.SrcH, 2);
        Assert.Equal(chosen.Scale, placed.Scale, 4);
        Assert.Equal(chosen.DestX, placed.DestX, 2);
        Assert.Equal(chosen.DestW, placed.DestW, 2);
        Assert.Equal(chosen.DestH, placed.DestH, 2);
    }

    [Fact]
    public void How_far_the_picture_reaches_is_the_framings_to_say()
    {
        var full = Place(Covering(0.5, 0.5, Roomy));
        var short_ = Place(new ArtFraming(0.5, 0.5, Roomy, 0.6));

        Assert.Equal(Room, full.DestW, 3);
        Assert.Equal(Room * 0.6, short_.DestW, 3);
        Assert.Equal(full.Scale, short_.Scale, 6);                  // the same magnification…
        Assert.Equal(Room, short_.DestX + short_.DestW, 3);         // …still against the record
    }

    [Fact]
    public void A_picture_that_does_not_reach_across_has_room_to_move_across()
    {
        // What the person sees as "the across slider does something". A filling picture of a photograph
        // that is exactly wide enough has no freedom, and pulling back its reach is what buys it.
        var left = Place(new ArtFraming(0.2, 0.5, Roomy, 0.6));
        var right = Place(new ArtFraming(0.8, 0.5, Roomy, 0.6));

        Assert.True(left.SrcX < right.SrcX);
    }

    // ── the same framing at another header size ──

    [Fact]
    public void The_same_framing_holds_the_same_point_of_the_picture_when_the_header_narrows()
    {
        var framing = Covering(0.5, 0.35, Roomy);

        var wide = ArtDisplay.PlaceFramed(framing, SourceWidth, SourceHeight, Slot, Band, Breath);
        var narrow = ArtDisplay.PlaceFramed(framing, SourceWidth, SourceHeight, 900, Band, Breath);

        // The zoom is stated down the picture, so what the subject is standing at down the band is
        // untouched; only how much of the photograph is on show sideways changes.
        Assert.Equal(wide.SrcY, narrow.SrcY, 3);
        Assert.Equal(wide.SrcH, narrow.SrcH, 3);
        Assert.True(narrow.SrcW < wide.SrcW);

        // …and both still fill their own header.
        Assert.Equal(Band, narrow.DestH, 3);
        Assert.Equal(900 - Breath, narrow.DestW, 3);
    }

    [Fact]
    public void A_header_of_nothing_does_not_divide_by_it()
    {
        var place = Place(Covering(0.5, 0.5, 0.5));
        Assert.True(place.Scale > 0);

        var pinched = ArtDisplay.PlaceFramed(Covering(0.5, 0.5, 0.5), SourceWidth, SourceHeight, 0, 0, 0);
        Assert.True(pinched.DestW > 0);
        Assert.True(pinched.DestH > 0);
        Assert.True(double.IsFinite(pinched.Scale));
    }

    [Fact]
    public void A_source_of_nothing_does_not_place_a_picture_that_is_not_there()
    {
        var place = ArtDisplay.PlaceFramed(Covering(0.5, 0.5, 0.5), 0, 0, Slot, Band, Breath);

        Assert.True(double.IsFinite(place.Scale));
        Assert.True(place.SrcW > 0);
        Assert.True(place.SrcH > 0);
    }
}
