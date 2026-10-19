using RPM.Core.Art;

namespace RPM.Core.Tests;

public class BackdropRankerTests
{
    private static BackdropFeatures Feat(
        double meanLuma = 0.45, double contrast = 0.6, double colorfulness = 0.5,
        double aspect = 1.78, double faceAcross = -1,
        int sourceMinDim = 1080, int originalIndex = 0, double openFraction = 0, double faceHeight = 0.12,
        double obstructed = 0)
        => new(meanLuma, contrast, colorfulness, aspect, faceAcross, sourceMinDim, originalIndex,
               FacesObstructedFraction: obstructed, OpenFraction: openFraction, FaceHeight: faceHeight);

    /// <summary>A face the detector found. The default height is a subject in a photograph rather than a
    /// close-up, so a test that is not about zoom gets the full face credit.</summary>
    private static FaceSpot Face(double across, double height = 0.12) => new(across, height);

    /// <summary>A header of a given shape, for the chooser to rank against.</summary>
    private static ArtScene Header(double targetAspect)
        => ArtScene.FromPanel(1600, 200, clearLeftPx: 200, clearRightPx: 1400,
                              slotWidth: targetAspect * 200);

    // ── the hard must: sixteen-to-nine or wider, on a minimal background ──
    //
    // A press shot on a plain studio background. Wide enough to fill the band without being scaled up,
    // empty enough that the title, the frosted cards and the picture's own faded edges have space. It
    // always leads: this is a tier above the score, not a term in it.

    /// <summary>A plain-backed widescreen photo takes the front from a WIDER one that is busy. Nothing
    /// below the tier gets a say while one of these is on offer.</summary>
    [Fact]
    public void A_widescreen_photograph_on_a_plain_ground_leads_a_wider_busy_one()
    {
        var busyPanorama = Feat(aspect: 3.0, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.05,
                                originalIndex: 0);
        var pressShot = Feat(aspect: 16.0 / 9, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.6,
                             originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([busyPanorama, pressShot], Header(8.0))[0]);
    }

    /// <summary>And it does so with the worse face, the worse look and the worse place in the likes
    /// order. A weight could be outweighed by enough of those; that is why this is not one.</summary>
    [Fact]
    public void The_hard_must_cannot_be_out_voted()
    {
        var everythingElse = Feat(aspect: 1.9, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.1,
                                  meanLuma: 0.45, contrast: 1, colorfulness: 1, originalIndex: 0);
        var plain = Feat(aspect: 16.0 / 9, faceAcross: 0.02, faceHeight: 0.28, openFraction: 0.5,
                         meanLuma: 0.02, contrast: 0, colorfulness: 0, sourceMinDim: 200, originalIndex: 7);

        Assert.True(BackdropRanker.Score(everythingElse) > BackdropRanker.Score(plain),
                    "the test is only meaningful if the busy one really does score higher");
        Assert.Equal(1, BackdropRanker.Rank([everythingElse, plain], Header(8.0))[0]);
    }

    /// <summary>Both halves, or neither. Shape alone and quiet alone are already rewarded by the score;
    /// the tier needs both together.</summary>
    [Theory]
    [InlineData(16.0 / 9, 0.05)]    // wide enough, but busy corner to corner
    [InlineData(4.0 / 3, 0.80)]     // beautifully quiet, and the wrong shape for a band
    [InlineData(1.60, 0.80)]        // sixteen-to-TEN: near-miss on the floor is still a miss
    public void Half_of_it_is_not_enough(double aspect, double openFraction)
        => Assert.False(BackdropRanker.MadeForTheHeader(
               Feat(aspect: aspect, openFraction: openFraction, faceAcross: 0.64, faceHeight: 0.3)));

    /// <summary>The trap in this rule, and the reason the tier asks for a subject at all: open ground
    /// and an ABSENT subject are the same measurement. An empty stage is quieter than any press shot, so a
    /// tier based on shape and quiet alone would choose the picture that shows nobody.</summary>
    [Fact]
    public void A_quiet_empty_photograph_is_not_a_photograph_of_the_artist()
    {
        var emptyStage = Feat(aspect: 1.78, faceAcross: 0.64, faceHeight: 0.03, openFraction: 0.9,
                              originalIndex: 0);
        var pressShot = Feat(aspect: 1.78, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.5,
                             originalIndex: 1);

        Assert.False(BackdropRanker.MadeForTheHeader(emptyStage));
        Assert.Equal(1, BackdropRanker.Rank([emptyStage, pressShot], Header(8.0))[0]);
    }

    /// <summary>A landscape with nobody in it at all cannot lead on the tier either, however clean. The
    /// header is a picture OF somebody.</summary>
    [Fact]
    public void A_photograph_with_no_one_in_it_never_makes_the_tier()
        => Assert.False(BackdropRanker.MadeForTheHeader(
               Feat(aspect: 2.0, faceAcross: -1, openFraction: 0.9)));

    /// <summary>And the other end: a headshot on a plain ground is not it either. In a band a dozen times
    /// wider than it is tall a close-up is a pair of eyes across a metre of screen, with nothing either
    /// side for the cards to be read over.</summary>
    [Fact]
    public void A_headshot_on_a_plain_ground_does_not_make_the_tier()
        => Assert.False(BackdropRanker.MadeForTheHeader(
               Feat(aspect: 1.78, faceAcross: 0.64, faceHeight: 0.8, openFraction: 0.6)));

    /// <summary>A gallery states its sizes in whole pixels, and 1000×563 is a thousandth under the floor.
    /// Losing a photograph to a rounding error would be impossible to explain.</summary>
    [Fact]
    public void A_hair_under_sixteen_to_nine_still_clears_the_floor()
        => Assert.True(BackdropRanker.ShapedForHeader(1000 / 563.0));

    /// <summary>The disqualification still applies. A tier above the score must not skip it and
    /// promote the one picture whose subject is behind the album cover.</summary>
    [Fact]
    public void A_photograph_whose_artist_is_hidden_never_leads_however_well_shaped_and_plain()
    {
        var hidden = Feat(aspect: 2.0, faceAcross: 0.04, faceHeight: 0.3, openFraction: 0.9,
                          obstructed: 1.0, originalIndex: 0);
        var ordinary = Feat(aspect: 1.78, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.1,
                            originalIndex: 1);

        Assert.False(BackdropRanker.MadeForTheHeader(hidden));
        Assert.Equal(1, BackdropRanker.Rank([hidden, ordinary], Header(8.0))[0]);
    }

    /// <summary>Among photographs that all pass, the ordinary steps still order them, so wider still
    /// wins. The tier says which photographs may lead, not which of them does.</summary>
    [Fact]
    public void Between_two_that_pass_the_wider_still_leads()
    {
        var sixteenNine = Feat(aspect: 16.0 / 9, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.6,
                               originalIndex: 0);
        var panorama = Feat(aspect: 2.6, faceAcross: 0.64, faceHeight: 0.3, openFraction: 0.6,
                            originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([sixteenNine, panorama], Header(8.0))[0]);
    }

    /// <summary>And an artist with no such photograph (the ordinary case) is ranked by the ordinary steps.
    /// This picks the best of what there is; it does not refuse to show anything.</summary>
    [Fact]
    public void With_nothing_that_passes_the_old_order_stands()
    {
        var boxyGoodFace = Feat(aspect: 4.0 / 3, faceAcross: 0.64, openFraction: 0.1, originalIndex: 0);
        var boxyBadFace = Feat(aspect: 4.0 / 3, faceAcross: 0.02, openFraction: 0.1, originalIndex: 1);

        var order = BackdropRanker.Rank([boxyBadFace, boxyGoodFace], Header(8.0));

        Assert.Equal([1, 0], order);
    }

    // ── step one: the widest photographs on offer, and nothing narrower competes ──

    [Fact]
    public void A_three_to_one_photograph_leads_a_four_to_three_one_whatever_its_face()
    {
        // The header is a long horizontal band. A three-to-one photograph is made for it; a four-to-three
        // is not, and no amount of well-placed face changes the shape of either.
        var panorama = Feat(aspect: 3.0, faceAcross: -1, originalIndex: 1);
        var boxy = Feat(aspect: 4.0 / 3, faceAcross: 0.64, openFraction: 0.8, originalIndex: 0);

        Assert.Equal(0, BackdropRanker.Rank([panorama, boxy], Header(8.0))[0]);
    }

    [Fact]
    public void A_landscape_leads_a_portrait_even_when_the_portrait_has_the_better_face()
    {
        // The case this handles: a portrait must not open the header while a wide photograph is behind
        // it in the carousel. Its face is perfectly placed and the landscape has none at all.
        var portrait = Feat(aspect: 0.7, faceAcross: 0.64, originalIndex: 0);
        var landscape = Feat(aspect: 1.78, faceAcross: -1, originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([portrait, landscape], Header(8.0))[0]);
    }

    [Fact]
    public void A_square_photo_never_leads_a_wide_one()
    {
        var wide = Feat(aspect: 1.9, originalIndex: 1);
        var square = Feat(aspect: 1.0, originalIndex: 0);

        Assert.Equal(1, BackdropRanker.Rank([square, wide], Header(8.0))[0]);
    }

    [Fact]
    public void The_widest_is_relative_so_an_artist_with_only_boxy_photos_still_gets_the_best_of_them()
    {
        // There is no width that is right in the abstract, only what this artist has. Every photograph
        // here is four-to-three; the shortlist is therefore all of them and the face decides, rather than
        // an absolute threshold rejecting the lot and leaving the likes order to pick.
        var plain = Feat(aspect: 4.0 / 3, faceAcross: -1, originalIndex: 0);
        var withFace = Feat(aspect: 4.0 / 3, faceAcross: 0.64, originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([plain, withFace], Header(8.0))[0]);
    }

    [Fact]
    public void Photographs_of_comparable_width_are_all_on_the_shortlist()
    {
        // Sixteen-to-nine and three-to-two are the same shape as far as this is concerned (the header
        // can use either), so the step-one gate must not separate them and leave the face unasked.
        var wide = BackdropRanker.Widest([1.78, 1.5, 1.6]);

        Assert.Equal(3, wide.Count);
    }

    [Fact]
    public void A_narrower_photograph_is_ranked_last_but_never_dropped()
    {
        // The arrows are a carousel over everything the sources offered. Losing on shape means never
        // leading, not being unreachable.
        var wide = Feat(aspect: 1.78, originalIndex: 1);
        var narrow = Feat(aspect: 0.8, originalIndex: 0);

        var order = BackdropRanker.Rank([narrow, wide], Header(8.0));

        Assert.Equal([1, 0], order);
    }

    [Fact]
    public void Shape_outranks_the_likes_order()
    {
        var portrait = Feat(aspect: 0.9, originalIndex: 0);
        var landscape = Feat(aspect: 1.78, originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([portrait, landscape], Header(9.0))[0]);
    }

    // ── step two: among photographs of the same shape, the face decides ──

    [Fact]
    public void Faces_decide_among_photographs_of_comparable_width()
    {
        // Both sixteen-to-nine, so step one cannot separate them and step two is the whole answer.
        var behindCover = Feat(faceAcross: 0.08, originalIndex: 0);
        var centred = Feat(faceAcross: 0.68, originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([behindCover, centred], Header(8.0))[0]);
    }

    [Fact]
    public void A_face_in_the_clear_band_fits_better_than_one_behind_the_cover()
    {
        var centred = Feat(faceAcross: 0.55);
        var behindCover = Feat(faceAcross: 0.08);

        Assert.True(BackdropRanker.Fit(centred) > BackdropRanker.Fit(behindCover));
    }

    [Fact]
    public void A_centred_face_beats_a_faceless_photograph_of_the_same_shape()
    {
        // 0.68 is the strip's best position: clear of the album cover and the title text on the left, short
        // of the record on the right. An artist header wants the artist.
        var face = Feat(faceAcross: 0.68, aspect: 1.78);
        var faceless = Feat(faceAcross: -1, aspect: 1.78, openFraction: 0.5);

        Assert.True(BackdropRanker.Fit(face) > BackdropRanker.Fit(faceless));
    }

    [Fact]
    public void A_face_behind_the_cover_earns_no_face_bonus()
    {
        // A face at the far left is as good as no face at all for fit: it is hidden either way.
        var behindCover = Feat(faceAcross: 0.05);
        var faceless = Feat(faceAcross: -1);

        Assert.Equal(BackdropRanker.Fit(faceless), BackdropRanker.Fit(behindCover), 6);
    }

    // ── how big the face is: a band, not a direction ──

    [Fact]
    public void A_face_you_can_see_beats_both_a_headshot_and_a_speck()
    {
        // Three photographs identical but for how much of the frame the head fills. The one in the middle
        // is the artist, clearly, with room around them, which is all that the header wants.
        var speck = Feat(faceAcross: 0.64, faceHeight: 0.03);          // a figure on a distant stage
        var subject = Feat(faceAcross: 0.64, faceHeight: 0.30);        // head and shoulders in a room
        var headshot = Feat(faceAcross: 0.64, faceHeight: 0.72);       // nothing but a face
        var header = Header(8.0);

        Assert.True(BackdropRanker.Fit(subject, header) > BackdropRanker.Fit(speck, header));
        Assert.True(BackdropRanker.Fit(subject, header) > BackdropRanker.Fit(headshot, header));
    }

    [Fact]
    public void A_bigger_face_wins_until_it_starts_crowding_the_frame()
    {
        // Growing, over the range where growing means "you can tell who this is".
        var header = Header(8.0);
        double Size(double h) => BackdropRanker.Fit(Feat(faceAcross: 0.64, faceHeight: h), header);

        Assert.True(Size(0.05) < Size(0.12));
        Assert.True(Size(0.12) < Size(0.20));
        Assert.True(Size(0.30) > Size(0.72));   // …and falling again once the face is the photograph
    }

    [Fact]
    public void A_wide_shot_with_a_visible_artist_beats_a_close_up()
    {
        // A head-and-shoulders shot does not lead a photograph of the same artist taken with some room
        // around them.
        var closeUp = Feat(faceAcross: 0.64, faceHeight: 0.70, originalIndex: 0);
        var wideShot = Feat(faceAcross: 0.64, faceHeight: 0.25, originalIndex: 1);
        var header = Header(8.0);

        Assert.True(BackdropRanker.Fit(wideShot, header) > BackdropRanker.Fit(closeUp, header));
        Assert.Equal(1, BackdropRanker.Rank([closeUp, wideShot], header)[0]);
    }

    [Fact]
    public void A_photograph_the_artist_is_merely_standing_in_does_not_lead_one_they_are_the_subject_of()
    {
        // Penalising face SIZE alone would mean the smaller the better, and the winner would be a stage
        // shot with a tiny figure on it.
        var speck = Feat(faceAcross: 0.64, faceHeight: 0.03, openFraction: 0.7, originalIndex: 0);
        var subject = Feat(faceAcross: 0.64, faceHeight: 0.28, openFraction: 0.4, originalIndex: 1);
        var header = Header(8.0);

        Assert.Equal(1, BackdropRanker.Rank([speck, subject], header)[0]);
    }

    [Fact]
    public void A_close_up_still_beats_a_close_up_whose_face_is_behind_the_cover()
    {
        // The zoom penalty is not a disqualification: an artist photographed only in close-up must still
        // get the best of what there is.
        var placed = Feat(faceAcross: 0.64, faceHeight: 0.62);
        var buried = Feat(faceAcross: 0.05, faceHeight: 0.62);

        Assert.True(BackdropRanker.Fit(placed) > BackdropRanker.Fit(buried));
    }

    [Fact]
    public void A_face_whose_size_was_never_measured_keeps_its_full_credit()
    {
        // A caller that gives the ranker a position without a size (the phone, an older caller) keeps
        // the face signal: no size means full credit, the same as a face of the right size.
        var unmeasured = Feat(faceAcross: 0.64, faceHeight: 0);
        var wellSized = Feat(faceAcross: 0.64, faceHeight: 0.30);

        Assert.Equal(BackdropRanker.Fit(wellSized), BackdropRanker.Fit(unmeasured), 6);
    }

    [Fact]
    public void Measure_keeps_the_biggest_faces_height()
    {
        // A crowd with one head in the foreground is a close-up of that head, whatever the others are.
        var f = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0,
                                       new[] { Face(0.6, 0.08), Face(0.7, 0.55) }, ArtScene.Default);

        Assert.Equal(0.55, f.FaceHeight, 6);
    }

    // ── open ground: what the app blends into ──

    [Fact]
    public void A_wide_photo_with_open_ground_leads_one_that_is_picture_edge_to_edge()
    {
        // Two landscapes of the same shape. One is busy in every corner; the other has the artist in the
        // middle and open air either side, which is what the wash blurs into and the cards are read over.
        var open = Feat(openFraction: 0.7, faceAcross: 0.64, originalIndex: 1);
        var crowded = Feat(openFraction: 0.05, faceAcross: 0.64, originalIndex: 0);
        var header = Header(8.0);

        Assert.True(BackdropRanker.Fit(open, header) > BackdropRanker.Fit(crowded, header));
        Assert.Equal(0, BackdropRanker.Rank([open, crowded], header)[0]);   // …despite being second-liked
    }

    [Fact]
    public void Measure_reads_open_ground_off_the_picture()
    {
        const int w = 32, h = 32;
        var half = BackdropRanker.Measure(BandOfDetail(w, h, 0, h / 2), w, h, w, h, 0);
        var busy = BackdropRanker.Measure(BandOfDetail(w, h, 0, h), w, h, w, h, 0);

        Assert.True(half.OpenFraction > 0.4, $"half the frame is flat grey, read as {half.OpenFraction:F2}");
        Assert.True(busy.OpenFraction < 0.1, $"nothing here is flat, read as {busy.OpenFraction:F2}");
    }

    // ── look and likes settle what is left ──

    [Fact]
    public void Rank_puts_the_better_photo_first_even_when_it_is_less_liked()
    {
        // Index 0 is the most-liked, but it is dark, flat and square; index 1 is wide and looks good.
        var dull = Feat(meanLuma: 0.05, contrast: 0.1, colorfulness: 0.05, aspect: 1.0,
                        sourceMinDim: 200, originalIndex: 0);
        var good = Feat(meanLuma: 0.45, contrast: 0.8, colorfulness: 0.7, aspect: 1.78,
                        sourceMinDim: 1080, originalIndex: 1);

        var order = BackdropRanker.Rank([dull, good]);

        Assert.Equal(1, order[0]);   // the good one wins despite being lower in the likes order
    }

    [Fact]
    public void Rank_breaks_a_tie_by_likes()
    {
        // Three photos that fit and look identical: the more-liked (lower original index) goes first.
        var order = BackdropRanker.Rank([Feat(originalIndex: 0), Feat(originalIndex: 1), Feat(originalIndex: 2)]);

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void The_most_liked_photograph_does_not_win_a_two_photo_carousel_on_likes_alone()
    {
        // The likes prior is fixed and small, not spread across the list. Spread across the list, an
        // artist with exactly two photos would have "the most liked" and "the least liked", and the full
        // weight of the tiebreak would go to a choice between two. A real difference in the picture
        // still decides.
        var crowded = Feat(openFraction: 0.05, faceAcross: 0.64, originalIndex: 0);
        var open = Feat(openFraction: 0.7, faceAcross: 0.64, originalIndex: 1);

        Assert.Equal(1, BackdropRanker.Rank([crowded, open], Header(8.0))[0]);
    }

    [Fact]
    public void A_dark_flat_photo_scores_below_a_bright_contrasty_one()
    {
        var good = Feat(meanLuma: 0.45, contrast: 0.8, colorfulness: 0.7);
        var dark = Feat(meanLuma: 0.03, contrast: 0.1, colorfulness: 0.05);

        Assert.True(BackdropRanker.Look(good) > BackdropRanker.Look(dark));
    }

    // ── scene-aware faces: the best-placeable one, and disqualifying the hidden ──

    [Fact]
    public void Measure_carries_the_face_position_through()
    {
        var f = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0, faceAcross: 0.6);
        Assert.Equal(0.6, f.FaceAcross, 6);
    }

    [Fact]
    public void Measure_keeps_the_best_placeable_face_not_the_average()
    {
        // Two faces at 0.1 and 0.85 average to a middling 0.48, but the ranker must keep the one that
        // can actually be placed clear (0.85), not the mean of them.
        var f = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0,
                                       new[] { Face(0.1), Face(0.85) }, ArtScene.Default);

        Assert.True(f.FaceAcross > 0.6, $"kept {f.FaceAcross:F2}, which looks like the average");
    }

    [Fact]
    public void A_centred_face_fits_better_than_two_faces_at_the_edges()
    {
        var scene = ArtScene.Default;
        var centred = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0, new[] { Face(0.62) }, scene);
        var spread = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 1, new[] { Face(0.1), Face(0.9) }, scene);

        Assert.True(BackdropRanker.Fit(centred, scene) > BackdropRanker.Fit(spread, scene));
    }

    [Fact]
    public void A_photo_with_most_faces_hidden_is_demoted_below_a_clean_one()
    {
        var scene = ArtScene.Default;
        var clean = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0, new[] { Face(0.55) }, scene);
        var hidden = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 1, new[] { Face(0.05), Face(0.95) }, scene);

        Assert.True(hidden.FacesObstructedFraction > 0.5, "both faces are behind the cover/record");
        Assert.True(BackdropRanker.Score(clean, scene) > BackdropRanker.Score(hidden, scene));

        var order = BackdropRanker.Rank([hidden, clean], scene);
        Assert.Equal(1, order[0]);   // the clean photo leads despite being the less-liked (index 1)
    }

    [Fact]
    public void One_hidden_face_among_visible_ones_is_not_demoted()
    {
        // A minority behind the cover is not a disqualification: the photo is still mostly of people
        // you can see.
        var f = BackdropRanker.Measure(Solid(8, 8, 128, 128, 128), 8, 8, 1920, 1080, 0,
                                       new[] { Face(0.05), Face(0.6), Face(0.7) }, ArtScene.Default);

        Assert.False(f.FacesObstructedFraction > 0.5);
    }

    // ── Measure: reading features off a thumbnail ──

    [Fact]
    public void Measure_reads_a_bright_image_as_bright()
    {
        var dark = BackdropRanker.Measure(Solid(16, 16, 10, 10, 10), 16, 16, 1920, 1080, 0);
        var light = BackdropRanker.Measure(Solid(16, 16, 200, 200, 200), 16, 16, 1920, 1080, 0);

        Assert.True(light.MeanLuma > dark.MeanLuma);
        Assert.True(dark.MeanLuma < 0.1);
    }

    [Fact]
    public void Measure_takes_aspect_and_sharpness_from_the_source_not_the_thumbnail()
    {
        // A square thumbnail, but a 16:9 1080p source: the ranking must judge the real photo, and shape
        // is the first thing it is judged on.
        var f = BackdropRanker.Measure(Solid(32, 32, 128, 128, 128), 32, 32, 1920, 1080, 0);

        Assert.Equal(1920.0 / 1080, f.Aspect, 3);
        Assert.Equal(1080, f.SourceMinDim);
    }

    [Fact]
    public void Measure_survives_a_degenerate_image()
    {
        var f = BackdropRanker.Measure([], 0, 0, 0, 0, 3);
        Assert.Equal(3, f.OriginalIndex);   // no throw; index preserved
    }

    // ── synthetic images ──

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    /// <summary>A flat grey image with detail (alternating black/white columns) only in rows
    /// [bandTop, bandBottom). Everything outside that band is flat, which is what open ground is.</summary>
    private static byte[] BandOfDetail(int w, int h, int bandTop, int bandBottom)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                byte v = y >= bandTop && y < bandBottom
                    ? (byte)(x % 2 == 0 ? 0 : 255)   // busy: alternating columns
                    : (byte)128;                     // flat grey
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        }
        return px;
    }
}
