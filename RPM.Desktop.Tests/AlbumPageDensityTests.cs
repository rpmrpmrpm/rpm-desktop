using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// How the album page fits itself to the card it is drawn in.
///
/// <para>The page is three columns inside the library card, which is a little over half the window,
/// so on an ordinary laptop a record has much less width than the widest layout wants. The layouts
/// form a ladder of rungs. These tests check that a rung is only used when the card cannot hold the
/// one above it, that every rung is narrower than the one above in every dimension, and that using a
/// rung never changes what any rung wants. The last rule stops a slow window resize from flickering
/// between two layouts.</para>
/// </summary>
public class AlbumPageDensityTests
{
    // ── the width ladder ──

    [Fact]
    public void A_card_with_room_for_everything_gets_the_widest_rung()
        => Assert.Equal(AlbumPageDensity.Rungs[0], AlbumPageDensity.For(1400));

    [Fact]
    public void A_rung_is_taken_at_exactly_what_it_asks_for()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
            Assert.True(AlbumPageDensity.For(rung.Demand).Demand <= rung.Demand);
    }

    /// <summary>One pixel less than a rung wants gives the next rung down, never a squeezed version of
    /// that rung.</summary>
    [Fact]
    public void A_card_one_pixel_short_drops_a_rung()
    {
        var widest = AlbumPageDensity.Rungs[0];
        Assert.NotEqual(widest, AlbumPageDensity.For(widest.Demand - 1));
    }

    [Fact]
    public void Every_rung_wants_less_than_the_one_above_it()
    {
        for (var i = 1; i < AlbumPageDensity.Rungs.Count; i++)
            Assert.True(
                AlbumPageDensity.Rungs[i].Demand < AlbumPageDensity.Rungs[i - 1].Demand,
                $"rung {i} does not want less than rung {i - 1}");
    }

    /// <summary>Nothing on a lower rung is bigger than on a higher one: when a card gets narrower, no
    /// column and no sleeve (cover) may grow, with one exception.
    ///
    /// <para>The exception is the rung where the papers (the About column) leave their column and move
    /// under the record. From there the album column is the card's whole width, and its sleeve cap is
    /// half as big again, because 196 left a small picture in a wide column. See
    /// <see cref="The_stacked_rungs_sleeve_is_half_as_big_again_and_under_the_widest"/>.</para></summary>
    [Fact]
    public void Nothing_grows_on_the_way_down()
    {
        for (var i = 1; i < AlbumPageDensity.Rungs.Count; i++)
        {
            var above = AlbumPageDensity.Rungs[i - 1];
            var here = AlbumPageDensity.Rungs[i];

            var papersLeftTheirColumn = above.AboutHasColumn && !here.AboutHasColumn;
            if (!papersLeftTheirColumn)
                Assert.True(here.Cover <= above.Cover, $"rung {i} widened the sleeve");
            Assert.True(here.Gap <= above.Gap, $"rung {i} widened the air");
            Assert.True(here.Pad <= above.Pad, $"rung {i} widened the padding");
            Assert.True(here.PadFoot <= above.PadFoot, $"rung {i} widened the foot");
            Assert.True(here.AlbumMin <= above.AlbumMin, $"rung {i} widened the album column");
        }
    }

    /// <summary>The rung where the papers are under the record caps the sleeve at half as big again as
    /// 196, and that is still no bigger than the widest rung's cap. The second check matters because
    /// the decode size is taken from the widest cap and must not change.</summary>
    [Fact]
    public void The_stacked_rungs_sleeve_is_half_as_big_again_and_under_the_widest()
    {
        var stacked = AlbumPageDensity.Rungs.First(r => !r.AboutHasColumn);

        Assert.Equal(294, stacked.Cover);
        Assert.Equal(AlbumPageDensity.StackedCover, stacked.Cover);
        Assert.True(stacked.Cover <= AlbumPageDensity.Rungs[0].Cover);
        Assert.Equal(AlbumPageDensity.Rungs[0].Cover, AlbumPageDensity.WidestCover);
    }

    /// <summary>The page has ONE margin (PageAir) at every edge, and the same on every rung, so the
    /// margin does not get smaller down the ladder like everything else.
    ///
    /// <para>The foot is the same as the sides even though the suggestions strip has its own scroll
    /// lane: the strip is not always on the page, and a smaller foot under a tracklist with no
    /// suggestions would look shorter than the other three sides.</para>
    /// </summary>
    [Fact]
    public void Every_edge_of_the_card_is_the_same_air()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            Assert.Equal(AlbumPageDensity.PageAir, rung.Pad);
            Assert.Equal(AlbumPageDensity.PageAir, rung.PadFoot);
        }
    }

    /// <summary>The groove leaves its column once and does not come back on a narrower rung.</summary>
    [Fact]
    public void The_groove_keeps_its_column_until_it_loses_it_for_good()
    {
        var lost = false;
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            if (!rung.GrooveHasColumn) lost = true;
            else Assert.False(lost, "the groove took a column back on a narrower rung");
        }

        Assert.True(lost, "the groove never gives up its column");
    }

    [Fact]
    public void A_card_too_narrow_for_three_columns_reads_the_groove_beside_the_tracks()
        => Assert.False(AlbumPageDensity.For(600).GrooveHasColumn);

    /// <summary>The papers keep their column for at least one rung after the groove has lost its own.
    /// The catalogue text should be visible as soon as the page opens; the groove is something a user
    /// opens on purpose, so a tab is an acceptable way to reach it.</summary>
    [Fact]
    public void The_papers_give_up_their_column_after_the_groove_does()
    {
        var grooveLost = -1;
        var papersLost = -1;

        for (var i = 0; i < AlbumPageDensity.Rungs.Count; i++)
        {
            if (grooveLost < 0 && !AlbumPageDensity.Rungs[i].GrooveHasColumn) grooveLost = i;
            if (papersLost < 0 && !AlbumPageDensity.Rungs[i].AboutHasColumn) papersLost = i;
        }

        Assert.True(grooveLost >= 0, "the groove never gives up its column");
        Assert.True(papersLost >= 0, "the papers never give up their column");
        Assert.True(papersLost > grooveLost, "the papers went behind a tab before the groove did");
    }

    /// <summary>Neither column takes its place back on a narrower rung.</summary>
    [Fact]
    public void The_papers_keep_their_column_until_they_lose_it_for_good()
    {
        var lost = false;
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            if (!rung.AboutHasColumn) lost = true;
            else Assert.False(lost, "the papers took a column back on a narrower rung");
        }

        Assert.True(lost, "the papers never give up their column");
    }

    /// <summary>The last rung is the floor. A card narrower than the last rung is drawn on the last
    /// rung, not on none.</summary>
    [Fact]
    public void A_card_narrower_than_the_floor_still_gets_the_floor()
        => Assert.Equal(AlbumPageDensity.Rungs[^1], AlbumPageDensity.For(120));

    /// <summary>What a rung wants is computed only from its own literals, so the answer is the same
    /// whatever the page looks like now. This stops a resize from switching back and forth.</summary>
    [Fact]
    public void What_a_rung_wants_is_its_own_parts_added_up()
    {
        var rung = AlbumPageDensity.Rungs[0];
        Assert.Equal(
            SideAir(rung) + rung.AlbumMin + rung.Gap + rung.AboutMin
                + rung.Gap + rung.JoinAir + rung.GrooveMin,
            rung.Demand);
    }

    /// <summary>The join to the groove is an edge of the page and has the PAGE's own air (margin) on
    /// each side of its divider, the same as the card's left and right edges. Written as the sum the
    /// user sees: the papers' scroll lane, then the grid's gap, then the groove's own air.</summary>
    [Fact]
    public void The_join_carries_the_pages_air_on_both_sides()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            if (!rung.GrooveHasColumn)
                continue;

            Assert.Equal(
                AlbumPageDensity.PageAir * 2,
                AlbumPageDensity.ScrollLane + rung.Gap + rung.JoinAir);
        }
    }

    /// <summary>If the page uses air that the rung does not ask for, the rung's demand is too small;
    /// if the rung asks for air the page does not use, the card changes rung too early. The rung asks
    /// for the grid's gap plus a remainder, and the two must add up to the join the user sees.</summary>
    [Fact]
    public void A_rung_asks_for_exactly_the_air_its_join_spends()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            if (!rung.GrooveHasColumn)
            {
                Assert.Equal(0, rung.JoinAir);
                continue;
            }

            var withoutTheJoin = SideAir(rung) + rung.AlbumMin
                + rung.Gap + rung.AboutMin + rung.Gap + rung.GrooveMin;

            Assert.Equal(rung.JoinAir, rung.Demand - withoutTheJoin, 3);

            // Never narrow enough to run the groove into the papers.
            Assert.True(AlbumPageDensity.ScrollLane + rung.Gap + rung.JoinAir > 0,
                "the groove stands on top of the papers");
        }
    }

    [Fact]
    public void A_rung_with_no_groove_column_does_not_ask_for_one()
    {
        var rung = AlbumPageDensity.Rungs[^1];
        Assert.Equal(rung.Pad * 2 + rung.AlbumMin, rung.Demand);
    }

    /// <summary>The sleeve is drawn INSIDE the album column and shrinks with it, so a rung that
    /// counted it would ask for the same points twice, and the ladder would change rung at the wrong
    /// widths. Checked on every rung: the demand is the columns and the air between them, and the
    /// sleeve is not one of the terms.</summary>
    [Fact]
    public void The_sleeve_is_not_asked_for_twice()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            var columns = SideAir(rung) + rung.AlbumMin
                + (rung.AboutHasColumn ? rung.Gap + rung.AboutMin : 0)
                + (rung.GrooveHasColumn ? rung.Gap + rung.JoinAir + rung.GrooveMin : 0);

            Assert.Equal(columns, rung.Demand);
        }
    }

    // ── the height ladder ──

    /// <summary>The sleeve a card of this height draws, computed the way the page computes it: the
    /// room the row leaves, less everything in the head that is not the sleeve.</summary>
    private static double Sleeve(double height, SimilarState row)
        => AlbumPageDensity.SleeveEdge(
            headWidth: 1000,
            headRoom: AlbumPageDensity.HeadRoom(AlbumPageDensity.ColumnRoom(height, Wide, row)),
            headRest: HeadRest,
            cover: Wide.Cover);

    /// <summary>What a rung spends left and right of its three columns: the page's own margin, twice.
    /// Written the same way the rung writes it, so these cases stay simple arithmetic.</summary>
    private static double SideAir(AlbumPageRung rung) => rung.Pad * 2;

    /// <summary>The widest rung, which is what the height cases below are measured against.</summary>
    private static readonly AlbumPageRung Wide = AlbumPageDensity.Rungs[0];

    /// <summary>Everything in the head of the album column that is not the sleeve, as the page draws
    /// it: the record's name and its line, the stars, the buttons, and the gaps between them. The
    /// ladder uses the same number; it is repeated here so a test that shrinks the sleeve computes it
    /// the same way the page does. Change both.</summary>
    private const double HeadRest = 160;

    /// <summary>The rung the height cases below use. Passed at every call site instead of assumed:
    /// what a card of a given height can hold depends on the air that rung uses, and a ladder that
    /// guessed it would promise room the page has already used.</summary>
    private static AlbumPageRung Rung(double height) => Wide;

    [Fact]
    public void A_tall_card_shows_the_suggestions_and_the_promoted_review()
    {
        Assert.True(AlbumPageDensity.HoldsOpenSimilar(1100, Rung(1100)));
        Assert.True(AlbumPageDensity.ShowsTopReview(1100, Rung(1100), SimilarState.Open));

        // And the sleeve gives up nothing: a card this tall draws it at the rung's own cap.
        Assert.Equal(Wide.Cover, Sleeve(1100, SimilarState.Open));
    }

    /// <summary>The order things are removed: first the review promoted above the ratings, then the
    /// suggestions along the foot, and the facts are capped only when that is not enough. The review is
    /// on the page twice (the copy at the top is a shortcut to the first card in the groove), and the
    /// suggestions are nowhere else, so a short card gives up the duplicate first.</summary>
    [Fact]
    public void The_promoted_review_goes_before_the_foot()
    {
        Assert.True(AlbumPageDensity.HoldsOpenSimilar(900, Rung(900)));
        Assert.False(AlbumPageDensity.ShowsTopReview(900, Rung(900), SimilarState.Open));

        // The sleeve shrinks to make room for the row, but it is still a sleeve, not a thumbnail.
        var sleeve = Sleeve(900, SimilarState.Open);
        Assert.True(sleeve < Wide.Cover, "the sleeve paid nothing for the row it kept");
        Assert.True(sleeve >= AlbumPageDensity.SleeveKept,
            $"the record was drawn at {sleeve} to keep the suggestions");
    }

    [Fact]
    public void The_foot_goes_before_the_facts_are_capped()
    {
        Assert.False(AlbumPageDensity.HoldsOpenSimilar(600, Rung(600)));
        Assert.False(AlbumPageDensity.ShowsTopReview(600, Rung(600), SimilarState.Open));

        // The height the foot gave up goes to the record: the sleeve is bigger without the row.
        Assert.True(Sleeve(600, SimilarState.Gone) > Sleeve(600, SimilarState.Open));
    }

    /// <summary>
    /// The suggestions are drawn only where the record's own page still has room for its main parts:
    /// a sleeve of a useful size, its name, and the row of buttons, with the tracklist under them.
    ///
    /// <para>The limit is at 788.5, so a card of 790 holds the row. It is written below as
    /// 766 + 22.5: 22.5 is the extra height that the equal margin on all four edges (PageAir) adds to
    /// the top and foot together (Chrome).</para>
    ///
    /// <para>Below the limit the row is removed, and the record's picture gets the height back. A
    /// short card gives up what is also shown elsewhere before it gives up what is only here.</para>
    /// </summary>
    [Fact]
    public void The_suggestions_go_before_the_record_is_a_thumbnail()
    {
        Assert.True(AlbumPageDensity.HoldsOpenSimilar(790, Rung(790)),
            "a card with room for the row could not hold it");
        Assert.False(AlbumPageDensity.HoldsOpenSimilar(700, Rung(700)));

        // The limit is exactly the margins' share above 766, and nothing else: a card of 766 plus that
        // height holds the row.
        Assert.True(AlbumPageDensity.HoldsOpenSimilar(766 + 22.5, Rung(766 + 22.5)));
    }

    /// <summary>What the ladder promises, the page can hold: wherever the suggestions are drawn, the
    /// space left still holds the head with its sleeve at least at the smallest size this page
    /// draws. The estimate that decides the row and the room the row leaves use the same
    /// arithmetic.</summary>
    [Fact]
    public void A_card_that_shows_the_suggestions_can_still_hold_the_head()
    {
        for (var height = 300d; height <= 1600; height += 5)
        {
            if (!AlbumPageDensity.HoldsOpenSimilar(height, Rung(height)))
                continue;

            var room = AlbumPageDensity.HeadRoom(AlbumPageDensity.ColumnRoom(height, Rung(height), SimilarState.Open));
            var sleeve = AlbumPageDensity.SleeveEdge(1000, room, HeadRest, Wide.Cover);

            Assert.True(sleeve >= AlbumPageDensity.SleeveKept,
                $"a {height}-point card drew the suggestions over a {sleeve}-point sleeve");
            Assert.True(sleeve + HeadRest <= room + 0.001, $"the head ran off a {height}-point card");
        }
    }

    /// <summary>On a card too short to show the facts in full, they scroll inside what is left once
    /// the tracklist has its minimum height. The list of songs is never pushed off the bottom.</summary>
    [Fact]
    public void A_short_card_caps_the_facts_and_keeps_the_tracklist()
    {
        var cap = AlbumPageDensity.FactsCap(500, Rung(500), SimilarState.Gone);

        Assert.True(double.IsFinite(cap));
        Assert.True(cap < 500, "the cap is not a cap");
        Assert.True(cap >= 160, "the facts were capped below what is worth scrolling");
    }

    /// <summary>However short the card, the facts keep a minimum height of their own: a pane two
    /// pixels tall would be only a scrollbar.</summary>
    [Fact]
    public void The_facts_never_fall_below_their_own_floor()
        => Assert.Equal(160, AlbumPageDensity.FactsCap(100, Rung(100), SimilarState.Gone));

    /// <summary>Height is decided on its own. A wide card can be short and a narrow one tall, and
    /// neither ladder affects the other.</summary>
    [Fact]
    public void The_two_ladders_are_asked_separately()
    {
        Assert.Equal(AlbumPageDensity.Rungs[0], AlbumPageDensity.For(1400));
        Assert.False(AlbumPageDensity.HoldsOpenSimilar(520, Rung(520)));
    }

    // ── the sleeve inside the album panel ──

    /// <summary>The reason for the share: a narrower panel draws a smaller sleeve, rather than the same
    /// sleeve pressed against the panel's two edges.</summary>
    [Fact]
    public void A_narrower_panel_draws_a_smaller_sleeve()
    {
        var cover = AlbumPageDensity.Rungs[0].Cover;

        Assert.True(
            AlbumPageDensity.SleeveEdge(360, cover) < AlbumPageDensity.SleeveEdge(440, cover),
            "the sleeve did not give way with the panel");
    }

    /// <summary>And it is the picture that shrinks, never the margins: below the cap the air is the
    /// same at both edges, and it is never less than the share OR than the lane the back key sits
    /// in.</summary>
    [Fact]
    public void The_air_either_side_is_the_greater_of_the_share_and_the_keys_lane()
    {
        var cover = AlbumPageDensity.Rungs[0].Cover;

        foreach (var head in new double[] { 240, 320, 400, 440 })
        {
            var stage = AlbumPageDensity.SleeveEdge(head, cover) * (1 + AlbumPageDensity.DiscShare);
            var air = (head - stage) / 2;

            Assert.Equal(
                Math.Max(head * AlbumPageDensity.SleeveAir, AlbumPageDensity.BackKey + AlbumPageDensity.BackKeyAir),
                air, 3);
        }
    }

    /// <summary>The key's lane is a MINIMUM for the air, not a replacement for the share: a panel wide
    /// enough that its share already clears the key uses the share.</summary>
    [Fact]
    public void A_wide_panel_spends_its_share_rather_than_the_keys_lane()
    {
        const double head = 1200;

        var stage = AlbumPageDensity.SleeveEdge(head, double.PositiveInfinity)
                    * (1 + AlbumPageDensity.DiscShare);

        Assert.Equal(AlbumPageDensity.SleeveAir * 2, (head - stage) / head, 3);
    }

    /// <summary>The overlap this rule prevents: the back key sits in the same corner as the top of the
    /// sleeve, and on a compact card the sleeve's left edge could reach it. On every rung, at the
    /// narrowest panel that rung allows, there is space between the two.</summary>
    [Fact]
    public void The_sleeve_stands_clear_of_the_back_key_on_every_rung()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            var head = rung.AlbumMin;
            var left = (head - AlbumPageDensity.SleeveEdge(head, rung.Cover) * (1 + AlbumPageDensity.DiscShare)) / 2;

            Assert.True(
                left >= AlbumPageDensity.BackKey + AlbumPageDensity.BackKeyAir - 0.001,
                $"a {head}-wide panel drew the sleeve {left} in, over a {AlbumPageDensity.BackKey}-wide key");
        }
    }

    /// <summary>The pair must fit: the sleeve, with the record sticking out to its right, never reaches
    /// the panel's edges.</summary>
    [Fact]
    public void The_record_behind_the_sleeve_stays_on_the_panel()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            var head = rung.AlbumMin;
            var stage = AlbumPageDensity.SleeveEdge(head, rung.Cover) * (1 + AlbumPageDensity.DiscShare);

            Assert.True(stage < head, $"the pair ran off a {head}-wide panel");
        }
    }

    [Fact]
    public void The_sleeve_never_passes_the_rungs_cap()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
            Assert.True(AlbumPageDensity.SleeveEdge(4000, rung.Cover) <= rung.Cover);
    }

    /// <summary>A panel narrower than the smallest useful sleeve does not shrink it to nothing: at that
    /// point the page has already given up everything else it can.</summary>
    [Fact]
    public void The_sleeve_has_a_floor()
        => Assert.Equal(96, AlbumPageDensity.SleeveEdge(40, 305));

    /// <summary>The sleeve never grows as the page narrows (cap and share together), except at the one
    /// width where the papers leave their column: from there the album column is the card's whole
    /// width and its cap is half as big again (see <see cref="Nothing_grows_on_the_way_down"/>). It
    /// grows there once, and shrinks steadily again below it.</summary>
    [Fact]
    public void The_sleeve_never_grows_as_the_page_narrows_except_where_the_papers_stack()
    {
        var last = double.MaxValue;
        var aboveHadPapers = AlbumPageDensity.For(900).AboutHasColumn;
        var grew = 0;

        for (var head = 900d; head >= 200; head -= 5)
        {
            var rung = AlbumPageDensity.For(head);
            var edge = AlbumPageDensity.SleeveEdge(head, rung.Cover);

            if (aboveHadPapers && !rung.AboutHasColumn)
            {
                if (edge > last)
                    grew++;
            }
            else
            {
                Assert.True(edge <= last, $"the sleeve grew at {head}");
            }

            aboveHadPapers = rung.AboutHasColumn;
            last = edge;
        }

        Assert.True(grew <= 1, "the sleeve grew more than once where the papers stack");
    }

    // ── where the bars stand ──

    /// <summary>
    /// A pane's scroll bar sits in the middle of the space beside it, not against the text it scrolls.
    ///
    /// <para>Measured from the right edge of the pane's column, with right as positive: the text ends
    /// one lane before that edge and the neighbour is <paramref name="beyond"/> after it. The
    /// papers' neighbour is the groove's divider; the tracklist's is the first encyclopedia card, one
    /// column gap away.</para>
    /// </summary>
    [Theory]
    [InlineData(2)]    // the papers, against the groove's line
    [InlineData(22)]   // the tracklist, against the next column
    [InlineData(14)]
    public void A_bar_stands_in_the_middle_of_the_air_beside_it(double beyond)
    {
        var writing = -AlbumPageDensity.ScrollLane;
        var bar = AlbumPageDensity.BarLift(beyond) - AlbumPageDensity.ScrollBarBox / 2;

        Assert.Equal((writing + beyond) / 2, bar, 3);
    }

    /// <summary>Each of the three panes on this page uses its own neighbour: the papers have the
    /// groove's line, the songs have the next column, and the groove has the card's own edge, which is
    /// the page's margin away (both sides of the card use the same margin).</summary>
    [Fact]
    public void Each_pane_answers_to_its_own_neighbour()
    {
        Assert.Equal(10, AlbumPageDensity.BarLift(AlbumPageDensity.JoinEdge), 3);
        Assert.Equal(12, AlbumPageDensity.BarLift(Wide.Gap), 3);
        Assert.Equal(15, AlbumPageDensity.BarLift(Wide.Pad), 3);
    }

    /// <summary>Whatever the lane and whatever the neighbour, the bar's box never overlaps the text it
    /// scrolls.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(22)]
    public void A_bar_never_stands_on_the_writing(double beyond)
    {
        var writing = -AlbumPageDensity.ScrollLane;
        var box = AlbumPageDensity.BarLift(beyond) - AlbumPageDensity.ScrollBarBox;

        Assert.True(box >= writing, $"the bar crossed the writing by {writing - box}");
    }

    // ── the frame the record's page stands in ──

    /// <summary>The join inside the card uses the page's air too, so the divider has the same space
    /// around it as the card's edges.</summary>
    [Fact]
    public void The_join_wears_the_pages_air_as_well()
        // In from the divider: the papers' lane and the divider's own offset make up the left half.
        => Assert.Equal(
            AlbumPageDensity.PageAir, AlbumPageDensity.ScrollLane + AlbumPageDensity.JoinEdge);

    /// <summary>The card's two sides use one margin on every rung, whether or not the groove has a
    /// column on the right. Different margins on the two sides would make the page look shifted to one
    /// side.</summary>
    [Fact]
    public void The_page_carries_the_same_margin_down_both_sides()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            // Checked against what the rung DEMANDS, not the helper above, which would only check the
            // arithmetic against itself.
            var columns = rung.AlbumMin
                + (rung.AboutHasColumn ? rung.Gap + rung.AboutMin : 0)
                + (rung.GrooveHasColumn ? rung.Gap + rung.JoinAir + rung.GrooveMin : 0);

            Assert.Equal(rung.Pad * 2, rung.Demand - columns, 3);
        }
    }

    /// <summary>The divider is in the middle of the join on every rung: the cards stop the same
    /// distance before the line as the groove starts after it.</summary>
    [Fact]
    public void The_divider_stands_in_the_middle_of_the_join()
    {
        foreach (var rung in AlbumPageDensity.Rungs)
        {
            if (!rung.GrooveHasColumn)
                continue;

            var cardsToLine = AlbumPageDensity.ScrollLane + AlbumPageDensity.JoinEdge;
            var lineToGroove = rung.Gap + rung.JoinAir - AlbumPageDensity.JoinEdge;

            Assert.Equal(cardsToLine, lineToGroove, 3);
            Assert.Equal(AlbumPageDensity.PageAir, lineToGroove, 3);
        }
    }

    /// <summary>The suggestions keep the whole of a bar's box under them, so an overflowing row draws
    /// its bar clear of the cards rather than across the foot of the one under the pointer.</summary>
    [Fact]
    public void The_suggestions_keep_a_whole_bar_under_them()
        => Assert.Equal(AlbumPageDensity.ScrollBarBox, AlbumPageDensity.SimilarBarLane);

    /// <summary>A sleeve is never so tall that the buttons under it are pushed off the bottom of the
    /// column. The buttons are the main controls for the record.</summary>
    [Fact]
    public void A_short_column_shrinks_the_sleeve_rather_than_cutting_the_buttons()
    {
        var edge = AlbumPageDensity.SleeveEdge(headWidth: 500, headRoom: 400, headRest: HeadRest, cover: 305);

        Assert.Equal(240, edge);
        Assert.True(edge + HeadRest <= 400);
    }

    /// <summary>A column with no height limit (the page is scrolling, and the column is as tall as its
    /// content) uses only the width.</summary>
    [Fact]
    public void A_column_with_no_ceiling_asks_only_the_width()
        => Assert.Equal(
            AlbumPageDensity.SleeveEdge(500, 305),
            AlbumPageDensity.SleeveEdge(500, double.PositiveInfinity, HeadRest, 305));

    /// <summary>A head that has not been measured yet does not affect the sleeve: a rest of zero means
    /// "not measured", not "the head takes nothing".</summary>
    [Fact]
    public void An_unmeasured_head_does_not_shrink_the_sleeve()
        => Assert.Equal(
            AlbumPageDensity.SleeveEdge(500, 305),
            AlbumPageDensity.SleeveEdge(500, 400, headRest: 0, cover: 305));

    /// <summary>At its minimum size the sleeve stops shrinking, and the head scrolls inside its cap
    /// instead: a picture thirty points tall would be too small for the record's page.</summary>
    [Fact]
    public void The_sleeve_stops_at_its_floor_however_short_the_column()
        => Assert.Equal(96, AlbumPageDensity.SleeveEdge(500, 200, HeadRest, 305));

    // ── the row folded away ──

    /// <summary>When shut, the row takes the height of a heading, not a shelf of sleeves. That is why it
    /// can be shut: the height goes back to the record's own picture.</summary>
    [Fact]
    public void A_shut_row_costs_a_heading_and_gives_the_sleeve_back()
    {
        var open = AlbumPageDensity.SimilarCost(Wide, SimilarState.Open);
        var shut = AlbumPageDensity.SimilarCost(Wide, SimilarState.Shut);

        Assert.True(shut < open / 4, "a shut row is not much cheaper than an open one");
        Assert.True(Sleeve(766, SimilarState.Shut) > Sleeve(766, SimilarState.Open));
    }

    /// <summary>A record with no suggestions uses no height for them, not even the air above a row that
    /// is not there.</summary>
    [Fact]
    public void A_record_with_no_suggestions_spends_nothing_on_them()
        => Assert.Equal(0, AlbumPageDensity.SimilarCost(Wide, SimilarState.Gone));

    /// <summary>The shut row fits where the open one does not: a card too short for the sleeves can
    /// still show that there ARE suggestions. A removed row would not show that.</summary>
    [Fact]
    public void A_card_too_short_for_the_open_row_still_holds_it_shut()
    {
        const double height = 700;   // under the open row's threshold, well over the shut one's

        Assert.False(AlbumPageDensity.HoldsOpenSimilar(height, Wide));

        var room = AlbumPageDensity.HeadRoom(AlbumPageDensity.ColumnRoom(height, Wide, SimilarState.Shut));
        Assert.True(room >= AlbumPageDensity.SleeveKept + HeadRest,
            $"the shut row left only {room} for the head");
    }

    // ── the release card ──

    /// <summary>The key sits beside the facts while there is room for both, and moves below them when
    /// the facts would be narrower than a catalogue number.</summary>
    [Fact]
    public void The_release_key_stands_beside_the_facts_while_there_is_room()
    {
        Assert.True(AlbumPageDensity.PressingKeyBeside(inner: 411, key: 155, spacing: 10));
        Assert.False(AlbumPageDensity.PressingKeyBeside(inner: 248, key: 155, spacing: 10));
    }

    /// <summary>A key that has not been measured yet counts as its minimum width, not zero. Otherwise
    /// the first pass draws it beside facts it does not fit next to, and the card jumps as soon as the
    /// button has a width.</summary>
    [Fact]
    public void An_unmeasured_key_is_taken_at_its_floor()
        => Assert.False(AlbumPageDensity.PressingKeyBeside(inner: 290, key: 0, spacing: 10));

    /// <summary>A longer label for the same key (a translation, or "Change release" instead of "Find
    /// release") moves the point at which the card changes layout, so the key is measured, not
    /// assumed.</summary>
    [Fact]
    public void A_wider_key_drops_under_the_facts_sooner()
    {
        Assert.True(AlbumPageDensity.PressingKeyBeside(inner: 320, key: 108, spacing: 10));
        Assert.False(AlbumPageDensity.PressingKeyBeside(inner: 320, key: 155, spacing: 10));
    }
}
