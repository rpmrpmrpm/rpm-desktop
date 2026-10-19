using System;
﻿using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using RPM.Core.Art;
using RPM.Core.Radio;

using RPM.Desktop.Presentation.Main.Home;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// How wide the home screen's featured banner and the two cards behind it are drawn. The rule keeps the
/// strip readable at both ends of the range this app is opened at. A plate that takes a flat share of the
/// pane is too wide in a wide window, and too narrow to hold a cover and a title in a folded one.
/// </summary>
public class HomeBannerTests
{
    [Fact]
    public void The_plate_takes_its_share_of_a_pane_that_is_neither_wide_nor_narrow()
        => Assert.Equal(1000 * HomeBanner.Share, HomeBanner.PlateWidth(1000));

    /// <summary>Past the ceiling, the cover and the text stay where they are, so the rest of a wider plate
    /// is empty photo.</summary>
    [Fact]
    public void A_wide_window_does_not_give_a_wider_plate()
        => Assert.Equal(HomeBanner.Widest, HomeBanner.PlateWidth(3000));

    /// <summary>There is no minimum plate width. Every strip narrow enough to reach a floor of 320 has
    /// already lost its neighbours. Such a strip gives the plate its whole width, which is more than 320 at
    /// every width where a floor would apply.</summary>
    [Fact]
    public void A_pane_too_narrow_for_the_share_gets_more_than_the_old_floor()
    {
        foreach (var strip in new double[] { 340, 400, 470 })
        {
            Assert.False(HomeBanner.Neighbours(strip));
            Assert.Equal(strip, HomeBanner.PlateWidth(strip));
            Assert.True(HomeBanner.PlateWidth(strip) >= Math.Min(strip, 320));
        }
    }

    /// <summary>A strip too narrow for a neighbour gives the plate ALL of it. The plate does not keep its
    /// share and stand in the middle with empty air on each side. At that width one card, edge to edge,
    /// looks right.</summary>
    [Fact]
    public void A_pane_with_no_room_for_neighbours_gives_the_plate_the_whole_strip()
    {
        Assert.False(HomeBanner.Neighbours(568));
        Assert.Equal(568, HomeBanner.PlateWidth(568));
        Assert.Equal(0, HomeBanner.PeekWidth(568));
    }

    /// <summary>The ceiling still holds where a filled strip can reach it. That is the narrow band between
    /// the widest plate and the width at which the neighbours come back.</summary>
    [Fact]
    public void A_filled_strip_is_still_capped_at_the_widest_plate()
    {
        const double strip = HomeBanner.Widest + 10;
        Assert.False(HomeBanner.Neighbours(strip));
        Assert.Equal(HomeBanner.Widest, HomeBanner.PlateWidth(strip));
    }

    /// <summary>The two widths are one answer. A plate never grows into room that a neighbour is still
    /// drawn in. The check walks across the range this app is opened at, one point at a time.</summary>
    [Fact]
    public void A_neighbour_and_a_filled_plate_are_never_both_drawn()
    {
        for (var strip = 200; strip <= 2400; strip++)
        {
            var plate = HomeBanner.PlateWidth(strip);
            var peek = HomeBanner.PeekWidth(strip);

            if (peek > 0)
                Assert.True(strip - plate >= 2 * HomeBanner.LeastPeek);
            else
                Assert.Equal(Math.Min(strip, HomeBanner.Widest), plate);
        }
    }

    /// <summary>A plate is never wider than the strip it stands in. A pane narrower than the minimum gets a
    /// plate the width of the pane, and never one that hangs over both its edges.</summary>
    [Fact]
    public void The_plate_is_never_wider_than_the_strip_it_stands_in()
        => Assert.Equal(200, HomeBanner.PlateWidth(200));

    [Fact]
    public void A_strip_with_no_width_yet_draws_nothing()
    {
        Assert.Equal(0, HomeBanner.PlateWidth(0));
        Assert.Equal(0, HomeBanner.PeekWidth(0));
    }

    /// <summary>A neighbour runs under the plate, so its width is what shows beside the plate, plus the
    /// bite the plate takes out of it.</summary>
    [Fact]
    public void A_neighbour_is_what_is_left_beside_the_plate_plus_the_bite()
    {
        var side = (1000 - HomeBanner.PlateWidth(1000)) / 2;
        Assert.Equal(side + HomeBanner.Bite, HomeBanner.PeekWidth(1000));
    }

    /// <summary>On a narrow pane there is no neighbour at all. A sliver of a second plate at the edge would
    /// look like a drawing fault, not like the next card.</summary>
    [Fact]
    public void A_pane_with_no_room_beside_the_plate_shows_no_neighbour()
    {
        // The share's own answer for this strip, which is what the room beside it is measured against.
        var shared = Math.Min(400 * HomeBanner.Share, HomeBanner.Widest);

        Assert.True((400 - shared) / 2 < HomeBanner.LeastPeek);
        Assert.Equal(0, HomeBanner.PeekWidth(400));
    }

    // ── where a face is allowed to land ──

    /// <summary>The purpose of asking the models is to keep the subject clear of the text. A face behind
    /// the text is a face nobody sees, and the plate then looks like a photograph that missed. This is
    /// measured in the plate's own points, which is the room the picture has now.</summary>
    [Fact]
    public void The_subject_is_placed_clear_of_the_writing()
    {
        var plate = HomeBanner.Widest;
        var subjectAt = HomeBanner.PictureLeft + (HomeBanner.Scene(plate).FocusAcross * HomeBanner.Slot(plate));

        Assert.True(subjectAt > HomeBanner.Written(plate));
    }

    /// <summary>The picture's room is the whole plate. The photo starts at the plate's left edge, so
    /// nothing covers the left of the picture. A fade from transparent there would hide anybody standing on
    /// that side.</summary>
    [Fact]
    public void The_picture_fills_the_plate()
    {
        Assert.Equal(0, HomeBanner.PictureLeft);
        Assert.Equal(HomeBanner.Widest, HomeBanner.Slot(HomeBanner.Widest));
    }

    /// <summary>A plate too narrow to have any room beside its cover still gives a slot, not nothing. The
    /// calculations after it divide by this value.</summary>
    [Fact]
    public void A_plate_narrower_than_its_own_sleeve_still_has_a_slot()
        => Assert.True(HomeBanner.Slot(100) > 0);

    /// <summary>…and short of the plate's right edge. Otherwise a face is cut in half by the
    /// edge.</summary>
    [Fact]
    public void The_subject_is_placed_short_of_the_right_edge()
        => Assert.True(HomeBanner.Scene(HomeBanner.Widest).FocusAcross < 0.95);

    /// <summary>On a narrow plate the text covers almost all of it. The correct answer is a sliver of clear
    /// band, not none. A scene with no room at all has nowhere to put the subject, so the crop falls back
    /// to the middle.</summary>
    [Fact]
    public void A_narrow_plate_keeps_a_clear_band()
    {
        var plate = 320d;   // a narrow plate, where the text takes most of the room
        Assert.True(HomeBanner.Written(plate) < plate);
        Assert.True(HomeBanner.Scene(plate).FocusAcross > 0.5);
    }

    /// <summary>The cover width is a measurement, but the title length is a guess. So the reach of the text
    /// is the cover part plus a third of the plate.</summary>
    [Fact]
    public void The_writing_reaches_past_the_sleeve_by_a_title()
        => Assert.Equal(
            HomeBanner.Margin + HomeBanner.Sleeve + HomeBanner.Margin + (HomeBanner.Widest * 0.3),
            HomeBanner.Written(HomeBanner.Widest));

    // ── the picture turned round ──

    private static FaceBox Head(int x, int size = 40) => new(x, 120, size, size);

    /// <summary>The left of this plate is covered by the cover and the text. A band of people standing down
    /// the left of their own photograph would be hidden. The crop's only other option is to zoom in until
    /// half of them are out of frame.</summary>
    [Fact]
    public void A_photograph_with_its_people_on_the_left_is_turned_round()
        => Assert.True(HomeFeatureArt.MoreHeadsOnTheLeft(
            [Head(20), Head(60), Head(200)], SaliencyService.Size));

    [Fact]
    public void A_photograph_with_its_people_on_the_right_is_left_alone()
        => Assert.False(HomeFeatureArt.MoreHeadsOnTheLeft(
            [Head(20), Head(200), Head(250)], SaliencyService.Size));

    /// <summary>Counted rather than weighed: four small heads down the left are four heads.</summary>
    [Fact]
    public void Heads_are_counted_not_measured()
        => Assert.True(HomeFeatureArt.MoreHeadsOnTheLeft(
            [Head(20, 20), Head(60, 20), Head(100, 20), Head(220, 120)], SaliencyService.Size));

    /// <summary>One each, and the big one on the left: a portrait with somebody else in it.</summary>
    [Fact]
    public void A_level_count_is_broken_on_which_side_carries_the_picture()
        => Assert.True(HomeFeatureArt.MoreHeadsOnTheLeft(
            [Head(60, 120), Head(240, 30)], SaliencyService.Size));

    /// <summary>…but not for a difference of a few percent. Otherwise a balanced photograph would flip back
    /// and forth between two readings of the same faces.</summary>
    [Fact]
    public void A_balanced_photograph_stays_as_it_was_taken()
        => Assert.False(HomeFeatureArt.MoreHeadsOnTheLeft(
            [Head(60, 42), Head(240, 40)], SaliencyService.Size));

    [Fact]
    public void A_photograph_with_nobody_in_it_is_never_turned()
        => Assert.False(HomeFeatureArt.MoreHeadsOnTheLeft([], SaliencyService.Size));

    /// <summary>What the crop asked for and what it got are different numbers. A window as wide as the
    /// photograph cannot slide. So a face 40% across the picture lands 40% across the window, however far
    /// right the scene wanted it. That shortfall is a second reason to turn a picture round, and this is
    /// how it is measured.</summary>
    [Fact]
    public void Where_the_faces_land_is_read_off_the_window_not_the_wish()
    {
        var window = new CropRect(0, 0, 320, 150);

        Assert.Equal(0.25, HomeFeatureArt.LandsAt(window, 80), 3);
        Assert.Equal(0.75, HomeFeatureArt.LandsAt(window, 240), 3);
    }

    /// <summary>With nobody found, the saliency's own centre of mass stands in. Otherwise the rule would
    /// fire on almost nothing, because a detector says "nobody" about a singer in profile as readily as
    /// about an empty room.</summary>
    [Fact]
    public void With_nobody_found_the_subject_is_where_the_saliency_is()
    {
        const int size = 4;
        var map = new double[size * size];
        map[(1 * size) + 3] = 1;   // one bright pixel, hard right

        Assert.Equal(3, HomeFeatureArt.SubjectAcross(map, [], size), 3);
        Assert.Equal(size / 2.0, HomeFeatureArt.SubjectAcross(new double[size * size], [], size), 3);
    }

    /// <summary>Where faces were found they are the answer: they are a measurement, the map is an
    /// estimate.</summary>
    [Fact]
    public void With_faces_found_they_are_the_subject()
        => Assert.Equal(80, HomeFeatureArt.SubjectAcross(new double[16], [Head(60, 40)], 4), 3);

    /// <summary>Read against the window's own left edge, not the photograph's: the window is what is on
    /// screen.</summary>
    [Fact]
    public void A_window_that_has_moved_moves_the_answer_with_it()
        => Assert.Equal(0.5, HomeFeatureArt.LandsAt(new CropRect(100, 0, 200, 100), 200), 3);

    [Fact]
    public void A_window_with_no_width_lands_nowhere_in_particular()
        => Assert.Equal(0.5, HomeFeatureArt.LandsAt(new CropRect(0, 0, 0, 150), 100));

    /// <summary>The map is mirrored, not measured again. So it must give the same answer read from the
    /// other end: mirrored twice gives back the start.</summary>
    [Fact]
    public void A_map_mirrored_twice_is_the_map()
    {
        const int size = 4;
        double[] map = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        Assert.Equal(map, HomeFeatureArt.MirrorMap(HomeFeatureArt.MirrorMap(map, size), size));
        Assert.Equal([4, 3, 2, 1], HomeFeatureArt.MirrorMap(map, size).Take(4));
    }

    // ── the shape a photograph is cut to ──

    /// <summary>The cut is asked for in the plate's own shape, so the surface does not crop it a second
    /// time on the way to the screen.</summary>
    [Fact]
    public void A_photograph_is_cut_to_the_room_it_will_stand_in()
    {
        var plate = HomeBanner.PlateWidth(1000);
        Assert.Equal(HomeBanner.Slot(plate) / HomeBanner.Height, HomeBanner.Aspect(plate));
    }

    /// <summary>A plate that has not been measured yet is treated as the widest one, which is the shape
    /// most windows give it. The alternative would be a division by zero, or a cut to a shape that no plate
    /// has.</summary>
    [Fact]
    public void An_unmeasured_plate_is_cut_as_the_widest_one()
        => Assert.Equal((HomeBanner.Widest - HomeBanner.PictureLeft) / HomeBanner.Height, HomeBanner.Aspect(0));

    // ── what a step carries ──

    [Fact]
    public void A_step_carries_the_strip_a_share_of_the_plate()
        => Assert.Equal(HomeBanner.PlateWidth(600) * HomeBanner.SlideShare, HomeBanner.Slide(600));

    /// <summary>A step of almost nothing reads as the cards snapping rather than as the strip
    /// moving.</summary>
    [Fact]
    public void A_narrow_pane_still_carries_the_strip_far_enough_to_see()
        => Assert.Equal(HomeBanner.LeastSlide, HomeBanner.Slide(150));

    /// <summary>A step is capped. A step of most of a plate would be a slide that the reader has to wait
    /// out.</summary>
    [Fact]
    public void A_wide_window_does_not_make_the_step_a_journey()
        => Assert.Equal(HomeBanner.MostSlide, HomeBanner.Slide(3000));

    /// <summary>The neighbours are shorter than the plate, which puts them behind it. They are the same
    /// card at the same width. Level with the plate, they would read as three banners in a row.</summary>
    [Fact]
    public void A_neighbour_is_drawn_shorter_than_the_plate()
        => Assert.Equal(HomeBanner.Height - HomeBanner.PeekDrop, HomeBanner.PeekHeight);

    // ── the photograph coming up ──

    /// <summary>A picture for the test. It is an ImageSource that is never really constructed, because
    /// creating one needs a dispatcher and a headless test has none. Nothing here asks what it looks like.
    /// <see cref="CoverFillTests"/> uses the same trick, for the same reason.</summary>
    private static ImageSource APicture => (ImageSource)System.Runtime.CompilerServices.RuntimeHelpers
        .GetUninitializedObject(typeof(Microsoft.UI.Xaml.Media.Imaging.BitmapImage));

    /// <summary>A card with no photograph draws no picture layer, and has nothing to come up.</summary>
    [Fact]
    public void A_card_with_no_photograph_has_nothing_to_draw()
    {
        var card = new HomeFeatureCard();

        Assert.Null(card.Portrait);
        Assert.Equal(Visibility.Collapsed, card.PortraitShown);
        Assert.Equal(0, card.PortraitOn);
    }

    /// <summary>The picture goes in invisible. The first frame that has a photograph draws none of it, and
    /// the fade brings it up from there. The other order would show an abrupt jump, which this
    /// avoids.</summary>
    [Fact]
    public void A_photograph_arrives_before_it_is_drawn()
    {
        var card = new HomeFeatureCard();
        var said = new List<string>();
        card.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");

        // This is the state half. The fade that follows rides the composition tick, and a headless test has
        // none. See HomeFeatureCard.PortraitIn.
        card.PortraitIn(APicture);

        Assert.NotNull(card.Portrait);
        Assert.Equal(Visibility.Visible, card.PortraitShown);
        Assert.Equal(0, card.PortraitOn);
        Assert.Contains(nameof(HomeFeatureCard.Portrait), said);
        Assert.Contains(nameof(HomeFeatureCard.PortraitShown), said);
    }

    // ── the spinner while a card's picture loads ──

    private static RadioStation AStation => new()
    {
        Uuid = "station-uuid",
        Name = "Station",
        StreamUrl = "https://example.com/stream",
    };

    /// <summary>A record's plate shows the spinner while its cover is being fetched, and only
    /// then.</summary>
    [Fact]
    public void A_record_card_spins_while_its_ground_is_fetched()
    {
        var card = new HomeFeatureCard { RoomShown = true };
        Assert.False(card.PlateWaiting);

        card.GroundFetching = true;
        Assert.True(card.PlateWaiting);

        card.Ground = APicture;
        Assert.False(card.PlateWaiting);
    }

    /// <summary>A fetch that ends with no picture (no artwork, or a failure) stops the spinner. Otherwise
    /// it would keep turning on a plate that nothing is coming for.</summary>
    [Fact]
    public void A_record_card_stops_spinning_when_the_fetch_ends_without_a_picture()
    {
        var card = new HomeFeatureCard { RoomShown = true, GroundFetching = true };

        card.GroundFetching = false;

        Assert.False(card.PlateWaiting);
    }

    /// <summary>The spinner's timer keeps running behind a collapsed screen. So the spinner turns only
    /// while the screen is being looked at.</summary>
    [Fact]
    public void No_spinner_turns_on_a_screen_nobody_is_looking_at()
    {
        var record = new HomeFeatureCard { GroundFetching = true };
        var station = new HomeFeatureCard { Station = AStation, StationMarkFetching = true };

        Assert.False(record.PlateWaiting);
        Assert.False(station.MarkWaiting);

        var said = new List<string>();
        record.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");
        record.RoomShown = true;

        Assert.True(record.PlateWaiting);
        Assert.Contains(nameof(HomeFeatureCard.PlateWaiting), said);
    }

    /// <summary>A station's card shows the spinner where its mark goes while the lookup runs. The radio
    /// mark is shown only once the lookup reports that there is none.</summary>
    [Fact]
    public void A_station_card_spins_while_its_mark_is_looked_up()
    {
        var card = new HomeFeatureCard { Station = AStation, RoomShown = true };

        card.StationMarkFetching = true;
        Assert.True(card.MarkWaiting);
        Assert.Equal(Visibility.Collapsed, card.StationMarkShown);
        Assert.False(card.PlateWaiting);

        card.StationMarkMissing = true;
        card.StationMarkFetching = false;
        Assert.False(card.MarkWaiting);
        Assert.Equal(Visibility.Visible, card.StationMarkShown);
    }

    /// <summary>The mark arriving stops the spinner, even before the lookup's finally block has
    /// run.</summary>
    [Fact]
    public void A_station_card_stops_spinning_when_its_mark_arrives()
    {
        var card = new HomeFeatureCard { Station = AStation, RoomShown = true, StationMarkFetching = true };

        card.StationLogo = APicture;

        Assert.False(card.MarkWaiting);
    }

    /// <summary>A generated list's plate stands on its room from the start, so it never spins.</summary>
    [Fact]
    public void A_list_card_never_spins()
    {
        var card = new HomeFeatureCard
        {
            Door = new HomeListDoor(null, 1),
            RoomShown = true,
            GroundFetching = true,
        };

        Assert.False(card.PlateWaiting);
        Assert.False(card.MarkWaiting);
    }
}
