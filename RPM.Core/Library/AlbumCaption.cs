namespace RPM.Core.Library;

/// <summary>
/// How big the writing under a cover is, given how big the cover is.
///
/// <para>The phone's wall is pinched between one column and four. On a normal handset, the art edge is
/// about 70 points at one end and 360 at the other. Type that does not change across that range is wrong
/// at both ends. At four columns a 15 point title competes with the sleeve it belongs to. At one column
/// the same title is a footnote under a picture five inches wide.</para>
///
/// <para>It is kept here, not in the page, because the wall must never get two different answers for
/// the same number. Every card in a row shares an art edge and therefore shares these. That is required,
/// not just tidy. The wall is a virtualising stack that ESTIMATES the rows it has not built from the ones
/// it has. So two cards that size their captions differently are two rows of different heights. Rows of
/// different heights make the wall shift a line under the finger that is reading it.</para>
/// </summary>
public readonly record struct AlbumCaption
{
    /// <summary>The art edge these sizes were drawn against: two columns on a normal handset, which is
    /// the size the wall opens at. At exactly this edge, every figure below is its base value, so only
    /// the pinched ends change size.</summary>
    private const double Anchor = 170;

    private const double AnchorAlbum = 15;
    private const double AnchorArtist = 14;
    private const double AnchorBadge = 13;
    private const double AnchorAlbumLine = 20;
    private const double AnchorArtistLine = 19;

    /// <summary>Small enough to fit four covers across, and no smaller. Below eleven points, a record's
    /// name over a photograph is hard to read at normal viewing distance.</summary>
    private const double Smallest = 11;

    /// <summary>A title, not a heading. The sleeve is the thing being looked at even when it fills the
    /// screen. Past twenty points, the writing under it competes with the sleeve.</summary>
    private const double Largest = 20;

    /// <summary>
    /// The gaps a card is built out of, above and below the writing.
    ///
    /// <para>These are here rather than in the template, for the same reason the type sizes are here. The
    /// wall's row height is the sum of these and the two line heights. A virtualising layout has to be
    /// told that height BEFORE it builds a card. Two places holding the same figure can disagree. If they
    /// disagree, the result is a row of the wrong height, which is the fault this whole class exists to
    /// prevent. The template binds to these values.</para>
    /// </summary>
    public const double ArtGap = 10;

    /// <inheritdoc cref="ArtGap"/>
    public const double LineGap = 2;

    /// <inheritdoc cref="ArtGap"/>
    public const double Below = 10;

    /// <summary>The effective art edge this caption was drawn against. This is kept so the card's whole
    /// height can be asked of the caption, rather than reassembled by whoever needs it.
    ///
    /// <para>For a cover of no size, this is the size the wall opens at, not zero. Every other figure here
    /// gives that answer too. A caption that agreed with the anchor on five numbers and disagreed on the
    /// sixth would not be the same caption. A test checks this.</para></summary>
    public double Art { get; private init; }

    /// <summary>
    /// How tall the whole card is: the sleeve, the two lines under it, and the gaps between.
    ///
    /// <para>This is the one figure a virtualising layout needs and cannot measure its way to. A layout
    /// that is told this places every card arithmetically, and it knows the length of the wall exactly.
    /// One that is not told has to average the cards it has built and estimate the rest. Every correction
    /// to that average moves the rows already on screen.</para>
    ///
    /// <para>This is a whole number of points whenever the art edge is. The page guarantees that by
    /// flooring the edge. A row height with a fraction in it lands differently on different rows.</para>
    /// </summary>
    public double Card => Art + ArtGap + AlbumLine + LineGap + ArtistLine + Below;

    /// <summary>The record's name.</summary>
    public double Album { get; private init; }

    /// <summary>Who made it, set under the name.</summary>
    public double Artist { get; private init; }

    /// <summary>The E badge. It sits beside the name and grows with it. At a fixed size, the badge would
    /// look like a mark on the title at four columns and like a speck at one.</summary>
    public double Badge { get; private init; }

    /// <summary>What the name's line measures with nothing on it, and <see cref="ArtistLine"/> the
    /// same for the line under it. Both lines keep their height empty, so that a record with no artist
    /// is not a shorter card. A shorter card would make the wall jump.</summary>
    public double AlbumLine { get; private init; }

    public double ArtistLine { get; private init; }

    /// <summary>
    /// The caption for an art edge of <paramref name="art"/> points.
    ///
    /// <para>The sizes scale by the square root of the change in edge, not by the change itself. Type and
    /// pictures do not scale together, because the distance to the reader does not change when the
    /// picture does. A cover four times the area is looked at from the same place. A title scaled with its
    /// width would be twice the size for no reason that a reader would expect. The square root keeps the
    /// writing a steady share of the card's AREA, which is what a reader judges it against.</para>
    ///
    /// <para>The factor is clamped once, not each figure. So the five numbers keep the relation they were
    /// drawn with all the way to both ends, instead of collapsing into each other at the floor.</para>
    /// </summary>
    public static AlbumCaption For(double art)
    {
        var factor = art > 0
            ? Math.Sqrt(art / Anchor)
            : 1;

        factor = Math.Clamp(factor, Smallest / AnchorAlbum, Largest / AnchorAlbum);

        return new AlbumCaption
        {
            Art = art > 0 ? art : Anchor,

            Album = Math.Round(AnchorAlbum * factor, 1),
            Artist = Math.Round(AnchorArtist * factor, 1),
            Badge = Math.Round(AnchorBadge * factor, 1),

            // Whole points, because a line height is a row height. Every card on the wall rounds the
            // same way from the same edge. A fraction of a point that lands differently on two cards
            // is the one difference this class exists to prevent.
            AlbumLine = Math.Round(AnchorAlbumLine * factor),
            ArtistLine = Math.Round(AnchorArtistLine * factor),
        };
    }
}
