using System;
using System.Linq;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Genres;
using RPM.Desktop.Presentation.Main.Genres.Models;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The genres wall's arithmetic: how wide a card is, how many go on a row, and the box the name is
/// set in.
/// </summary>
public class GenreWallCardTests
{
    private static GenreWallItem Shelf(string name) => new(name, [], []);

    private static GenreCardMetrics At(double width)
    {
        var metrics = new GenreCardMetrics();
        metrics.Set(width);
        return metrics;
    }

    /// <summary>
    /// A row holds whole mirrored pairs, so the column count is even. The count is one on a pane too narrow
    /// to hold two cards.
    /// </summary>
    [Theory]
    [InlineData(400, 2)]
    [InlineData(600, 2)]
    [InlineData(700, 2)]
    [InlineData(900, 4)]
    [InlineData(1000, 4)]
    [InlineData(1400, 4)]
    [InlineData(1800, 6)]
    [InlineData(380, 1)]
    [InlineData(220, 1)]
    public void A_row_holds_whole_pairs(double width, int expected)
    {
        var metrics = At(width);

        Assert.Equal(expected, metrics.Columns);
        Assert.True(metrics.Columns == 1 || metrics.Columns % 2 == 0);
        Assert.Equal(metrics.Columns > 1, metrics.Paired);
    }

    /// <summary>
    /// And a card comes out about the size of an artist's box. This is why the layout pairs to the NEAREST
    /// pair, and does not fill the row with two.
    ///
    /// <para>The artists wall aims at 300 and does not go under 190. A genre card of about 500 would look
    /// like a different app in the same panel.</para>
    /// </summary>
    [Fact]
    public void A_card_comes_out_about_the_size_of_an_artists_box()
    {
        for (var width = 200; width <= 2600; width += 13)
        {
            var metrics = At(width);
            var card = metrics.CardWidth;

            if (!metrics.Paired)
            {
                // One card fills the pane, as the artists wall does at its narrowest too. The pane is under
                // 400, or there would be room for a pair.
                Assert.Equal(Math.Floor((double)width), card);
                continue;
            }

            // The ceiling is not the target. Around 750 points there is a narrow band of pane widths where
            // splitting the row again would put both cards under the floor. In that band the wall draws one
            // large pair rather than four small cards.
            Assert.InRange(card, 190, 394);
        }
    }

    /// <summary>The cards and the gutters fill the wall exactly. A card rounded the wrong way leaves a
    /// sliver down one edge, which makes the wall look off-centre.</summary>
    [Fact]
    public void The_cards_and_the_gutters_fill_the_wall()
    {
        for (var width = 400; width < 2600; width += 7)
        {
            var metrics = At(width);
            var spent = metrics.CardWidth * metrics.Columns
                      + GenreCardMetrics.Gutter * (metrics.Columns - 1);

            Assert.InRange(width - spent, 0, metrics.Columns);
        }
    }

    /// <summary>A pair is two cards and the gutter between them. The layout is given the pair width, so it
    /// never splits a pair across two lines.</summary>
    [Fact]
    public void A_pair_is_two_cards_wide()
    {
        var wide = At(1200);
        Assert.Equal(wide.CardWidth * 2 + GenreCardMetrics.Gutter, wide.PairWidth);

        var narrow = At(300);
        Assert.False(narrow.Paired);
        Assert.Equal(narrow.CardWidth, narrow.PairWidth);
    }

    /// <summary>The framing models are asked for the shape the photograph is really drawn in: its share of
    /// the width over the whole height. If the CARD's shape were used, the window would be framed in one
    /// shape and then cropped to another. That cuts a head in half.</summary>
    [Fact]
    public void The_photograph_is_framed_at_its_own_shape()
    {
        var metrics = At(1200);
        var drawn = metrics.PictureWidth / metrics.CardHeight;

        Assert.Equal(GenreCardMetrics.PictureShape, drawn, 1);
    }

    /// <summary>
    /// The photograph's slot is nearly square, and the framing models are what make that possible.
    ///
    /// <para>The slot is nearly square, and the framing models make that work. Press photos are often
    /// portraits. A centre cut of a portrait into a wide slot would cut across somebody's chest. The window
    /// is put round the head, not the middle, which is what makes a near-square slot safe (see
    /// MainPage.Genres and PlaylistCovers.FramedAsync). The colour must stay solid where the photograph
    /// begins, or the picture's own edge shows as a line down the card.</para>
    ///
    /// <para>So the slot and the card still cannot be changed separately. Past about five by four, the
    /// models frame a band rather than a portrait, and a head does not fit in a band.</para>
    /// </summary>
    [Fact]
    public void The_photograph_stands_in_a_near_square()
    {
        var shape = GenreCardMetrics.PictureShare / GenreCardMetrics.Shape;

        Assert.True(shape < 1.25, $"the picture is {shape:F2} wide for every one tall, which is a band");
        Assert.True(shape > 0.6, $"the picture is {shape:F2} wide for every one tall, which is a slot");
    }

    /// <summary>A card is drawn at the shape the wall asks for. It is never so short that the wall looks
    /// like stamps, or so tall that it looks like billboards.</summary>
    [Theory]
    [InlineData(700)]
    [InlineData(1000)]
    [InlineData(1400)]
    [InlineData(300)]
    public void A_card_is_drawn_at_the_walls_shape(double width)
    {
        var metrics = At(width);
        var asked = metrics.CardWidth * GenreCardMetrics.Shape;

        Assert.Equal(Math.Round(Math.Clamp(asked, 120, 300)), metrics.CardHeight);
        Assert.InRange(metrics.CardHeight, 120, 300);
    }

    /// <summary>Every name is set at the same HEIGHT, and its width is whatever its own letters need. If
    /// the box followed the name, a short name would be set large and a long one small. That reads on the
    /// wall as emphasis, as if the collection thought more of one shelf than another. In fact it only says
    /// that one word is shorter than another. Nothing is squashed. The width is the height times the name's
    /// own proportions.</summary>
    [Theory]
    [InlineData("Pop")]
    [InlineData("Soundtrack")]
    [InlineData("Old School Hip Hop")]
    [InlineData("Skiffle")]   // not in the table: it is set at the same height too
    public void Every_name_is_set_at_the_same_height(string genre)
    {
        var metrics = At(1200);
        var card = new GenreTile(Shelf(genre), metrics, flushRight: false);

        // The lettering is the wall's height. The drawing is that height plus whatever the name hangs below
        // its baseline. The width follows the drawing's own proportions. Nothing is condensed.
        Assert.Equal(metrics.NameHeight, card.NameBoxHeight * card.NameBase, 6);
        Assert.Equal(Math.Round(card.NameBoxHeight * card.NameAspect), card.NameBoxWidth);
    }

    /// <summary>The height is chosen so that the LONGEST name in the table still fits the room a card gives
    /// it. That is what allows one height for all names, with nothing condensed to fit. Every name also
    /// hangs from the same line. That is the other half of making names the same size. A shared height
    /// alone does not give that.</summary>
    [Fact]
    public void Every_name_in_the_table_is_hung_from_one_line_and_fits_the_card()
    {
        var metrics = At(1200);
        double? top = null;

        foreach (var mark in GenreWordmarks.Everything)
        {
            var card = new GenreTile(Shelf(mark.Genre), metrics, flushRight: false);

            Assert.True(
                card.NameBoxWidth <= metrics.NameWidth + 1,
                $"{mark.Genre} runs to {card.NameBoxWidth} in room for {metrics.NameWidth}");

            Assert.Equal(metrics.NameHeight, card.NameBoxHeight * card.NameBase, 6);

            top ??= card.NameMargin.Top;
            Assert.Equal(top.Value, card.NameMargin.Top);
        }
    }

    /// <summary>The name box follows the wall. Without this, a card built at one width and then resized
    /// would keep drawing its name at the old size while it is on screen.</summary>
    [Fact]
    public void The_name_box_moves_with_the_wall()
    {
        var metrics = At(1200);
        var card = new GenreTile(Shelf("Rock"), metrics, flushRight: false);
        var was = card.NameBoxWidth;

        var told = false;
        card.PropertyChanged += (_, e) => told |= e.PropertyName == nameof(GenreTile.NameBoxWidth);

        metrics.Set(700);

        Assert.True(told);
        Assert.NotEqual(was, card.NameBoxWidth);
    }

    /// <summary>
    /// A card knows which side it is drawn from as soon as it is made.
    ///
    /// <para>What it wears for that side, such as the lettering and the fall, is asked for later. That must
    /// happen on the UI thread, which is the only thread that can make those objects. So this test checks
    /// the fact the card is built on, and the two settings it chooses between.</para>
    /// </summary>
    [Fact]
    public void A_card_is_built_for_the_side_it_stands_on()
    {
        var metrics = At(1200);

        var left = new GenreTile(Shelf("Old School Hip Hop"), metrics, flushRight: false);
        var right = new GenreTile(Shelf("Old School Hip Hop"), metrics, flushRight: true);

        Assert.False(left.FlushRight);
        Assert.True(right.FlushRight);

        // The two sides of a two-line name are two different settings of it.
        var mark = GenreWordmarks.For("Old School Hip Hop")!;
        Assert.NotEqual(mark.On(GenreFlush.Left), mark.On(GenreFlush.Right));

        // Both sides give a card of the same size, so a row of two is a straight row and not a step.
        Assert.Equal(left.NameBoxWidth, right.NameBoxWidth);
        Assert.Equal(left.NameBoxHeight, right.NameBoxHeight);
    }

    /// <summary>A genre that the table does not know still gets a card. The card has no name on it. That is
    /// visible and easy to fix. The alternative would be a wall that throws while it is scrolled.</summary>
    [Fact]
    public void A_genre_with_no_wordmark_is_still_a_card()
    {
        var card = new GenreTile(Shelf("Skiffle"), At(1200), flushRight: false);

        Assert.Null(GenreWordmarks.For("Skiffle"));
        Assert.Equal("Skiffle", card.Name);

        // It still gets the wall's height, like every other card. Nothing is painted in it. A name that
        // cannot be measured is set as a square, which is the shape of no name at all.
        Assert.Equal(1, card.NameAspect);
        Assert.True(card.NameBoxWidth > 0);
        Assert.Null(card.Lettering);
    }

    /// <summary>A row is a pair. The last row of an odd number of cards has one card, with no empty space
    /// where the second would be.</summary>
    [Fact]
    public void A_pair_says_whether_it_has_a_second_card()
    {
        var metrics = At(1200);
        var left = new GenreTile(Shelf("Rock"), metrics, flushRight: false);
        var right = new GenreTile(Shelf("Indie"), metrics, flushRight: true);

        Assert.True(new GenrePair(left, right).HasRight);
        Assert.Equal(2, new GenrePair(left, right).Both.Count());

        Assert.False(new GenrePair(left, null).HasRight);
        Assert.Single(new GenrePair(left, null).Both);
    }
}
