namespace RPM.Core.Library;

/// <summary>The sizes of the parts of the artist page: how wide the column about the artist is, how tall
/// the band across the top is drawn, and how big a sleeve on the shelves beside it is.
///
/// These are computed from the width of the panel and not written into the markup, so the page fits any
/// window size. The arithmetic is kept here rather than in the view, because it contains decisions, and a
/// decision inside a measure pass cannot be checked by a test. <see cref="AlbumGridLayout"/> is kept the
/// same way.
/// </summary>
public static class ArtistPageLayout
{
    // ── the column about them ──

    /// <summary>The share of the panel the column takes. It is a share and not a fixed number, because the
    /// user often drags the width of the panel.</summary>
    public const double AboutShare = 0.40;

    /// <summary>The narrowest the column can be. Below this, the biography is a column of four-word lines
    /// and the photo is too small to see. The page is then folded into one column.</summary>
    public const double AboutMin = 360;

    /// <summary>The widest the column can be. Past this, a line of text is too long to find the start of
    /// the next line easily. That is why newspapers use columns.</summary>
    public const double AboutMax = 520;

    /// <summary>Enough room beside the column for a shelf: one whole sleeve at a readable size, and the
    /// edge of the next one. The edge shows that the row goes on past the panel.</summary>
    public const double ShelfRoom = 340;

    /// <summary>The page's shape on a panel this wide: whether the column stands beside the shelves at
    /// all, and how wide it is when it does.
    ///
    /// <para>Both answers come from one call, not two, because they are one decision. If they were asked
    /// separately, a caller could fold the page and then set a width for a column that is not there. That
    /// would lay out the panel for a column that does not exist.</para>
    ///
    /// <para>A folded page reports a width of zero. That is a stated answer, not a missing one: the column
    /// definition is written from this value either way.</para>
    /// </summary>
    public static (bool Folded, double AboutWidth) Columns(double panel)
    {
        if (double.IsNaN(panel) || panel < AboutMin + ShelfRoom)
            return (true, 0);

        return (false, Math.Clamp(Math.Round(panel * AboutShare), AboutMin, AboutMax));
    }

    // ── the band across the top ──

    /// <summary>The band's height as a share of its own width: FOUR WIDE BY THREE TALL.
    ///
    /// <para>Photos do not arrive in this shape. A 1000-by-1000 <c>artistthumb</c>, which the page opens
    /// on, is scaled to cover the band, so it loses a quarter of its height. The whole cut is taken from
    /// the bottom. A centred cut would take an eighth off the top and cut through people's heads. A
    /// backdrop is shown only for an artist with no square image. It is scaled to the band's width, with
    /// its reflection under it (RunOff in the desktop head), so nothing is cut.</para>
    ///
    /// <para><b>The wall does not follow this.</b> A tile on the artists wall has a different shape. That
    /// shape is <see cref="TileShare"/>, and it is kept here next to this constant. Two surfaces that share
    /// a shape are easy to merge again by accident, so the two values are written side by side.</para>
    /// </summary>
    public const double BandShare = 3.0 / 4.0;

    /// <summary>A box on the artists wall, as a height over a width: SIXTEEN BY NINE. Read by
    /// ArtistTileMetrics.Shape and by ArtistsPage.ArtistBox.shape on the phone.
    ///
    /// <para>Photos arrive in this shape. fanart.tv backdrops are 1920 by 1080, and a box shows a backdrop
    /// (ArtistPortraits.ChooseAsync). So a wall of them crops very little. The artist page is four by
    /// three, and the box is not. A tile and its page frame the act in two different ways. This was
    /// accepted rather than cropping the wall's pictures to the page's shape.</para>
    /// </summary>
    public const double TileShare = 9.0 / 16.0;

    /// <summary>How tall the band stands over a column this wide. It is worked out from the band's OWN
    /// width and not the panel's. When the page is folded, the band is the panel, and there is one question
    /// that has the right answer in both states.
    ///
    /// <para>There is no floor and no ceiling, on purpose. A clamp is exactly what breaks a shape. An
    /// unfolded column is between 360 and 520 wide, so the band is between 270 and 390 tall. A floor or a
    /// ceiling over that would give some widths a band that is not four by three. The column is already
    /// clamped by <see cref="Columns"/>, so the band is bounded by that arithmetic and needs no second
    /// clamp.</para>
    ///
    /// <para>An unmeasured band is nothing tall. This is a stated answer: a band with no width has no
    /// height, and the caller has not laid out yet.</para>
    /// </summary>
    public static double BandHeight(double width)
        => double.IsNaN(width) || width <= 0 ? 0 : Math.Round(width * BandShare);

    /// <summary>The share of the band's width that the artist's own lettering (logo) may take.
    ///
    /// <para>The height cap alone cannot limit a logo, and that is why this exists. A logo is capped at a
    /// height and drawn in its own proportions, so its width depends on the artist's name. A short name is
    /// a small mark at that height. A long name, such as "kanYeWest" written as one word, fills most of the
    /// band. It then runs under the photo's own controls and leaves the picture with only a strip above the
    /// text.</para>
    ///
    /// <para>The rest of the width is not spare room. It is where the picture is still a picture. The title
    /// is a caption on the photo, not a sheet laid over it. This share sets how much of the frame the title
    /// may cover.</para>
    ///
    /// <para>The share is 0.42, which is 3/5 times 0.7. The 0.7 scale makes the lettering 30 percent
    /// smaller, with its proportions kept. The mark is limited by both a height and a width. See LogoCap in
    /// ArtistDetailView, which is scaled by the same 0.7. Shrinking only one of the two limits would change
    /// the drawn size by less than intended for some marks, and would not change the shape of any mark,
    /// because the drawing stays uniform. Shrinking both scales every mark by exactly 0.7, whichever limit
    /// it was resting on.</para>
    /// </summary>
    public const double LogoWidthShare = 0.42;

    /// <summary>The widest the lettering may be drawn on a band this wide.
    ///
    /// <para>An unmeasured band gives zero. This is the same stated answer <see cref="BandHeight"/> gives,
    /// for the same reason: the caller has not laid out yet. A width made up here would cut the logo to a
    /// guess. The caller decides what to do with zero. See LogoAsync, which falls back to the height cap
    /// alone.</para>
    /// </summary>
    public static double LogoMaxWidth(double bandWidth)
        => double.IsNaN(bandWidth) || bandWidth <= 0 ? 0 : Math.Round(bandWidth * LogoWidthShare);

    // ── a sleeve on the shelves ──

    /// <summary>The sleeve size the shelves aim for. It is not the size they draw. A whole number of
    /// sleeves has to fill the shelf, so this value is the one the rounding works around.</summary>
    public const double SleeveTarget = 210;

    /// <summary>The smallest a sleeve may get, and the largest it may grow. Without the largest, a shelf
    /// with one record in a wide panel would show that one record very large.</summary>
    public const double SleeveMin = 150;
    public const double SleeveMax = 260;

    /// <summary>What is not sleeve. Both values match the markup: the shelf scroller's padding on each side
    /// (12 and 12), and the padding around the square inside one sleeve (6 and 6). If either is changed in
    /// the markup, change it here too. A stride that leaves out its own padding puts the last sleeve under
    /// the scrollbar.</summary>
    public const double ShelfPadding = 24;
    public const double SleeveChrome = 12;

    /// <summary>The gap between one sleeve and the next. The shelf's own panel adds it, and the markup sets
    /// it as the <c>StackLayout.Spacing</c> of the repeaters. It is not part of the stride that
    /// <see cref="Sleeve"/> returns, because the stride is the space one card takes. Moving along the shelf
    /// covers both.</summary>
    public const double ShelfSpacing = 2;

    /// <summary>How far the first sleeve's left edge sits from the shelf's own left edge: the scroller's
    /// padding on that side and the padding inside a card. The drawer's triangle is placed against this, so
    /// the mark sits under the square and not under the card around it.</summary>
    public const double SleeveLeft = ShelfPadding / 2 + SleeveChrome / 2;

    /// <summary>The sleeve drawn for every record on both shelves, given the width of the scroller that
    /// holds them. It returns the stride a card takes and the square inside it.
    ///
    /// <para>The rounding is the same as the wall of covers: the nearest whole number of sleeves, then
    /// stretched to fill the row so nothing is left over. This is deliberate. A record on an artist's shelf
    /// and the same record on the wall should not be two different squares.</para>
    /// </summary>
    public static (double Stride, double Art) Sleeve(double shelfWidth)
    {
        var room = double.IsNaN(shelfWidth) ? 0 : shelfWidth - ShelfPadding;
        if (room <= 0)
            return (SleeveTarget + SleeveChrome, SleeveTarget);

        var columns = AlbumGridLayout.ColumnsForTarget(room, SleeveTarget);
        var stride = Math.Clamp(
            AlbumGridLayout.StrideFor(room, columns, SleeveMax + SleeveChrome),
            SleeveMin + SleeveChrome,
            SleeveMax + SleeveChrome);

        return (stride, stride - SleeveChrome);
    }
}
