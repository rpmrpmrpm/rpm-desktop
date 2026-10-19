using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The writing under a cover, against the size of the cover.
///
/// <para>The figures below are the wall on a normal phone: about 73 points of art at four columns,
/// 105 at three, 170 at two (the size the wall opens at) and 365 at one.</para>
/// </summary>
public class AlbumCaptionTests
{
    private const double FourAcross = 73;
    private const double ThreeAcross = 105;
    private const double TwoAcross = 170;
    private const double OneAcross = 365;

    /// <summary>At the size the wall opens at, the caption has its base sizes. The other sizes scale
    /// from these, and the default must not change.</summary>
    [Fact]
    public void At_the_size_the_wall_opens_at_nothing_has_moved()
    {
        var caption = AlbumCaption.For(TwoAcross);

        Assert.Equal(15, caption.Album);
        Assert.Equal(14, caption.Artist);
        Assert.Equal(13, caption.Badge);
        Assert.Equal(20, caption.AlbumLine);
        Assert.Equal(19, caption.ArtistLine);
    }

    [Fact]
    public void A_bigger_cover_carries_bigger_writing()
        => Assert.True(AlbumCaption.For(OneAcross).Album > AlbumCaption.For(TwoAcross).Album);

    [Fact]
    public void A_smaller_cover_carries_smaller_writing()
        => Assert.True(AlbumCaption.For(FourAcross).Album < AlbumCaption.For(TwoAcross).Album);

    /// <summary>Each of the four column counts gives a different text size: three across is a
    /// different screen from four, and the text should show that.</summary>
    [Fact]
    public void Each_column_count_reads_differently_from_the_next()
    {
        double[] sizes =
        [
            AlbumCaption.For(FourAcross).Album,
            AlbumCaption.For(ThreeAcross).Album,
            AlbumCaption.For(TwoAcross).Album,
            AlbumCaption.For(OneAcross).Album,
        ];

        Assert.Equal(sizes.Length, sizes.Distinct().Count());
    }

    // ── the ends ──

    [Fact]
    public void The_writing_never_goes_below_what_can_be_read()
        => Assert.True(AlbumCaption.For(8).Album >= 11);

    [Fact]
    public void The_writing_never_grows_into_a_heading()
        => Assert.True(AlbumCaption.For(2000).Album <= 20);

    /// <summary>Clamped on the factor and not on each figure, so the artist stays under the title at
    /// both ends instead of the two meeting at the floor.</summary>
    [Fact]
    public void The_artist_stays_smaller_than_the_title_at_both_ends()
    {
        Assert.True(AlbumCaption.For(8).Artist < AlbumCaption.For(8).Album);
        Assert.True(AlbumCaption.For(2000).Artist < AlbumCaption.For(2000).Album);
    }

    /// <summary>An unmeasured card is a card the wall has not laid out yet, and it answers with the
    /// size the wall opens at rather than with nothing. A caption of zero points would give a row of
    /// no height on the pass where the layout makes its estimate.</summary>
    [Fact]
    public void A_cover_of_no_size_answers_the_size_the_wall_opens_at()
        => Assert.Equal(AlbumCaption.For(TwoAcross), AlbumCaption.For(0));

    // ── one height per row ──

    /// <summary>The main rule: cards across a row share an art edge, so they have
    /// to share a line height exactly. Whole points, so two cards cannot round apart.</summary>
    [Fact]
    public void A_line_height_is_a_whole_number_of_points()
    {
        foreach (var art in new[] { FourAcross, ThreeAcross, TwoAcross, OneAcross, 91.5, 187.3 })
        {
            var caption = AlbumCaption.For(art);

            Assert.Equal(caption.AlbumLine, Math.Round(caption.AlbumLine));
            Assert.Equal(caption.ArtistLine, Math.Round(caption.ArtistLine));
        }
    }

    [Fact]
    public void The_same_cover_size_always_gives_the_same_caption()
        => Assert.Equal(AlbumCaption.For(137.4), AlbumCaption.For(137.4));

    /// <summary>Room for the writing that sits on it, at every size.</summary>
    [Fact]
    public void A_line_is_taller_than_the_writing_on_it()
    {
        foreach (var art in new[] { 8, 73, 105, 170, 365, 2000 })
        {
            var caption = AlbumCaption.For(art);

            Assert.True(caption.AlbumLine > caption.Album);
            Assert.True(caption.ArtistLine > caption.Artist);
        }
    }

    // ── the whole card ──
    //
    // What a virtualising layout is told before it builds anything. The wall's placement depends on
    // every one of these, so they are tested here rather than checked by eye on a phone: a row height
    // that is wrong by one point moves the wall by about a line for each screen of scrolling.

    /// <summary>The sleeve, the gap, both lines, the gap between them, and the clear ground under the
    /// last one. Written out rather than computed, so the test fails when the shape changes rather
    /// than agreeing with it.</summary>
    [Fact]
    public void A_card_is_the_cover_the_two_lines_and_the_gaps()
    {
        var caption = AlbumCaption.For(TwoAcross);

        Assert.Equal(170 + 10 + 20 + 2 + 19 + 10, caption.Card);
    }

    /// <summary>The most important rule. Two cards at one art edge are one height, so a row is one height,
    /// so the layout can place every row it has not built.</summary>
    [Fact]
    public void Every_card_at_one_cover_size_is_one_height()
    {
        foreach (var art in new[] { 73, 105, 170, 365 })
            Assert.Equal(AlbumCaption.For(art).Card, AlbumCaption.For(art).Card);
    }

    /// <summary>A whole number of points wherever the art edge is one (the page makes sure of this by
    /// rounding it down). A fraction in a row height rounds differently on different rows, and the
    /// error adds up down the wall.</summary>
    [Fact]
    public void A_card_at_a_whole_cover_size_is_a_whole_number_of_points()
    {
        foreach (var art in new[] { 8, 73, 105, 170, 365, 2000 })
            Assert.Equal(AlbumCaption.For(art).Card, Math.Round(AlbumCaption.For(art).Card));
    }

    /// <summary>A bigger sleeve is a bigger card at every pinch-zoom size. Not only bigger by
    /// the sleeve: the writing grows too, so the card grows by more than the art does.</summary>
    [Fact]
    public void A_bigger_cover_is_a_taller_card()
    {
        var sizes = new[] { 73, 105, 170, 365 };

        for (var i = 1; i < sizes.Length; i++)
        {
            var smaller = AlbumCaption.For(sizes[i - 1]);
            var larger = AlbumCaption.For(sizes[i]);

            Assert.True(larger.Card > smaller.Card);
            Assert.True(larger.Card - smaller.Card > sizes[i] - sizes[i - 1]);
        }
    }

    /// <summary>A card is taller than the sleeve on it, always. The gaps and the two lines are what
    /// separates one row of records from the next, and a card that measured to its art alone would
    /// have the rows touching.</summary>
    [Fact]
    public void A_card_is_taller_than_its_cover()
    {
        foreach (var art in new[] { 8, 73, 105, 170, 365, 2000 })
            Assert.True(AlbumCaption.For(art).Card > art);
    }
}
