namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// One rung of the album page's width: how large the sleeve may be drawn, how much air runs
/// between the columns, and how narrow each column may get before the next rung is used instead.
/// </summary>
/// <param name="Cover">The sleeve's cap inside the album column. The sleeve is not a column of its
/// own. It stands at the head of the album panel and is drawn at the size that panel can give it, up
/// to this cap. The record behind it takes a third more of this width to the right, and the pair
/// are centred together. So this is a cap on the sleeve, not on the pair.</param>
/// <param name="Gap">Between the columns.</param>
/// <param name="Pad">The air down both sides of the page and across its top, against whatever stands
/// at each edge. It is also the air on each side of the divider at the join to the groove. Every
/// rung is written off <see cref="AlbumPageDensity.PageAir"/>, so there is one value of 28 and not
/// seven.
///
/// <para>Every edge of the page uses this one value. A column's own margin is correct for a column,
/// but a page edge is not a column. Using the column margins on the page edges gave the card four
/// different walls: the right edge and the join used the groove's margin of 12, and the top used
/// the line where the groove's name starts. Each of those is a true statement about a column.</para>
/// </param>
/// <param name="PadFoot">The air under the page. It is a number of its own because it is the one side
/// where air is paid for with the tracklist's own height. The suggestions row is pinned down there,
/// and every point spent below it is a point the list of songs loses (see <see cref="Chrome"/>).
///
/// <para>The value is the same as the other sides, <see cref="AlbumPageDensity.PageAir"/>. The
/// suggestions strip has its own scroll lane, and the artists' names under it have descenders. So a
/// smaller foot can look as large as the sides, but only while the suggestions are on the page. With
/// no suggestions, a smaller foot looks smaller than the sides. Keeping all four edges equal gives the
/// same look in both cases. Where the strip is shown, the foot reads a lane wider than the sides.</para>
/// </param>
/// <param name="AlbumMin">The floor under the column holding the sleeve, the record's name and its
/// tracks.</param>
/// <param name="AboutMin">The floor under the column holding what the encyclopedias say. Zero on a
/// rung where it has no column at all. It then stands behind a tab beside the tracks instead.</param>
/// <param name="GrooveMin">The floor under the groove. Zero on a rung where it has no column at all.
/// It then stands behind a tab beside the tracks instead.</param>
/// <param name="AlbumStar">The album column's share of the room that the two floors leave over. The
/// about column and the groove each get one share. On the widest rungs the album column gets a little
/// more, because the tracklist has the longest lines and the other two columns wrap their text.</param>
internal readonly record struct AlbumPageRung(
    double Cover,
    double Gap,
    double Pad,
    double PadFoot,
    double AlbumMin,
    double AboutMin,
    double GrooveMin,
    double AlbumStar)
{
    /// <summary>Whether the about column stands beside the album on this rung, rather than behind a
    /// tab over the tracks.</summary>
    public bool AboutHasColumn => AboutMin > 0;

    /// <summary>Whether the groove is a column on this rung, rather than a tab beside the tracks.</summary>
    public bool GrooveHasColumn => GrooveMin > 0;

    /// <summary>
    /// The air the join to the groove takes on top of the gap the grid already spends between two
    /// columns. It is zero where the groove has no column to be joined to.
    ///
    /// <para>The join is an edge of the record's page. The record's own panels end there, and the room
    /// for the record starts, under one colour. So it uses the page air on each side of its divider:
    /// <see cref="AlbumPageDensity.PageAir"/> twice, the same 28 that the card keeps on its left and
    /// its right. A join tighter than either wall it runs between looks like two panels leaning on
    /// each other. The papers panel's scroll lane pays part of the left half from inside its own
    /// column, and the grid's <see cref="Gap"/> pays what it would spend anyway. So this is the
    /// remainder. The groove carries it as a margin.</para>
    ///
    /// <para>It is part of <see cref="Demand"/> because the page cannot avoid spending this air. If it
    /// were left out, a card exactly at a rung's width would draw the groove fifty-six points under its
    /// own floor. Air that is spent but not counted turns a floor into a wish.</para>
    /// </summary>
    public double JoinAir => GrooveHasColumn
        ? AlbumPageDensity.PageAir * 2 - AlbumPageDensity.ScrollLane - Gap
        : 0;

    /// <summary>The air the page spends above and below the columns. The height ladder takes this off
    /// the card before it checks whether anything fits. It is read from the rung, not written down a
    /// second time, so changing a rung's air cannot leave the ladder assuming room the page has
    /// already used.</summary>
    public double Chrome => Pad + PadFoot;

    /// <summary>What the rung needs from the card. A rung is used when the card can hold this.
    ///
    /// <para>The sleeve is not in this sum. It is drawn inside the album column and shrinks with it, so
    /// counting it here would count the same points twice.</para></summary>
    public double Demand =>
        Pad * 2 + AlbumMin
        + (AboutHasColumn ? Gap + AboutMin : 0)
        + (GrooveHasColumn ? Gap + JoinAir + GrooveMin : 0);
}

/// <summary>What the row of suggestions along the foot is doing, which is what it costs the page.</summary>
internal enum SimilarState
{
    /// <summary>Not there at all: this record has no suggestions.</summary>
    Gone,

    /// <summary>Shut: its heading and the seam above it, with the sleeves folded away below them. It
    /// costs one line, so it stays on the page at every height. A reader can then see that there are
    /// suggestions and open them.</summary>
    Shut,

    /// <summary>Open, with the sleeves shown.</summary>
    Open,
}

/// <summary>
/// How much of the album page fits.
///
/// <para>The page has three columns with fixed minimum widths: the sleeve with the record's name and
/// its tracks, the encyclopedia text about the record and the act that made it, and the groove. The
/// page is drawn inside the library card, which is a little over half the window. On a 1440-wide
/// screen the card has about 760 px for all three. The widest rung needs more than 1000 px. Without
/// this calculation the third column would run off the edge of the card, and the tracklist would be
/// clipped to make room for it.</para>
///
/// <para>There are two ladders, asked separately, because a window can be narrow without being short.
/// Width shrinks the columns, then folds them behind a tab over the tracks. The groove folds first,
/// then the about column. Height gives up first the review promoted to the top, then the suggestions
/// along the foot. Last, it caps the head of the album column, so the head scrolls inside its own
/// pane instead of pushing the tracklist off the card.</para>
///
/// <para><b>The groove folds before the about column.</b> This order is a deliberate choice, not a side
/// effect of the arithmetic. The text about a record is shown as soon as the page opens, and it is
/// read once. The groove is a place a reader goes to on purpose, so a tab is a reasonable way to reach
/// it. Neither one leaves the page.</para>
///
/// <para>Every number here is a literal from the page's own markup, so what a rung needs never depends
/// on the page's current state. Taking a rung does not change what any rung needs. This stops a slow
/// drag of the window from switching back and forth between two rungs.</para>
/// </summary>
internal static class AlbumPageDensity
{
    /// <summary>
    /// The lane a scrolling pane on this page keeps clear for its own bar: the papers panel and the
    /// album head on their right, and the groove's two panes on theirs.
    ///
    /// <para>Ten points, a value set in the markup. The scroll bar does not stand in this lane. It stands
    /// in the air beside the column, centred there (see <see cref="BarLift"/>). So the lane only has to
    /// keep the writing clear of the bar. A wider lane would cost six points of writing per column for
    /// no gain.</para>
    ///
    /// <para>The lane is inside the panel that keeps it, so it is already paid for out of
    /// <see cref="AlbumPageRung.AboutMin"/>. That is why <see cref="AlbumPageRung.JoinAir"/> subtracts
    /// it rather than adds it. The view reads this constant instead of keeping a second copy of the
    /// same number.</para>
    /// </summary>
    public const double ScrollLane = 10;

    /// <summary>
    /// The air the record's page keeps at every edge it has: the card's four sides, and both sides of
    /// the divider at the join to the groove. It is the value used for <see cref="AlbumPageRung.Pad"/>
    /// and <see cref="AlbumPageRung.PadFoot"/>. The rungs are written off this constant, so there is one
    /// value of 28 and not seven.
    ///
    /// <para>Every edge uses this value. The margins of the columns inside the page are smaller, but
    /// they are wrong for the outer edges. Different values on different edges gave the card four
    /// different walls. The page owns its edges, whatever they meet.</para>
    ///
    /// <para>One effect of this: the groove's name starts at the top of the page, not on the line where
    /// the other panes' names start. This screen is the only one where no other pane heading is shown
    /// beside it, so nothing is out of line.</para>
    /// </summary>
    public const double PageAir = 28;

    /// <summary>How far past the right of the papers column the groove's divider stands: half the
    /// join, less the lane the papers keep inside their own column.</summary>
    public static double JoinEdge => PageAir - ScrollLane;

    /// <summary>
    /// The width the record's column has on a card this wide WITH THE GROOVE BESIDE IT. This is the
    /// width when nothing is folded away.
    /// </summary>
    /// <remarks>
    /// Nothing on this page may be sized from a column that a fold makes wider. If it were, pressing
    /// fold would redraw the sleeve at 223 points instead of 165, and cut each suggestion from 143 to
    /// 131 points. The page would jump, then slide. Anything that must not jump should ask this method,
    /// which does not know what the fold is doing.
    ///
    /// <para>The grid's own arithmetic. It is safe to repeat here for this column only, because the
    /// album column's floor is the one that binds first on every rung. Its minimum over its star
    /// (360/1.1) is larger than the minimum of either other column. So a pool that leaves it above its
    /// floor also leaves the other two above theirs, and one Math.Max is all the Grid would do. Check
    /// this again if a rung's minimums or stars change.</para>
    ///
    /// <para>Null where the rung gives the groove no column of its own. There the fold moves nothing.
    /// A stacked groove folds out of the stack under the record, and the columns never see it. So the
    /// caller can use the width it has. A value derived here would answer a question nobody is
    /// asking.</para>
    /// </remarks>
    public static double? AlbumRoom(double card, AlbumPageRung rung)
        => Pool(card, rung) is { } pool
            ? Math.Max(rung.AlbumMin, pool * rung.AlbumStar / (rung.AlbumStar + 2))
            : null;

    /// <summary>The papers' column at the same width, and null in the same case. The album column
    /// reaches its minimum first on every rung, so the papers' own floor never binds here. The star
    /// share is kept with that floor under it, to match the shape of <see cref="AlbumRoom"/>.</summary>
    public static double? PapersRoom(double card, AlbumPageRung rung)
        => Pool(card, rung) is { } pool
            ? Math.Max(rung.AboutMin, pool / (rung.AlbumStar + 2))
            : null;

    /// <summary>
    /// The same answer for the row of suggestions, which spans the record's column, the gap, and the
    /// papers' column. Null for the same reason <see cref="AlbumRoom"/> is.
    /// </summary>
    public static double? SimilarRoom(double card, AlbumPageRung rung)
    {
        if (Pool(card, rung) is not { } pool)
            return null;

        var album = Math.Max(rung.AlbumMin, pool * rung.AlbumStar / (rung.AlbumStar + 2));
        var papers = Math.Max(rung.AboutMin, pool / (rung.AlbumStar + 2));
        return album + rung.Gap + papers;
    }

    /// <summary>The room the three columns share: the card less the page air on each side, less the two
    /// gaps the grid puts between them. The groove's JoinAir is not subtracted. It is a margin inside
    /// the groove's own cell, so the cell still asks for its share of this room.</summary>
    private static double? Pool(double card, AlbumPageRung rung)
    {
        if (!rung.GrooveHasColumn)
            return null;

        var pool = card - rung.Pad * 2 - rung.Gap * 2;
        return pool > 0 ? pool : null;
    }

    /// <summary>The box a scrollbar is drawn in, at the right edge of the pane it belongs to. This is a
    /// fact about the framework's bar. It is written down because two things on this page place
    /// themselves against the bar rather than against the pane: the papers' lane (see
    /// <see cref="PapersBarLift"/>) and the foot the suggestions stand on (see
    /// <see cref="SimilarBarLane"/>). The thumb inside the box is eight points wide and centred, so the
    /// middle of the box is the middle of the thumb (see Themes/ScrollBars.xaml).</summary>
    public const double ScrollBarBox = 12;

    /// <summary>
    /// How far a scrolling pane sticks its scroller out past its own column. This puts the bar in the
    /// middle of the air beside the column, not tight against the writing it scrolls.
    ///
    /// <para>The bar is drawn at the right edge of its scroller, so the only way to move the bar is to
    /// move that edge. The writing stays where it was, because the lane inside the scroller grows by the
    /// same amount (see ApplyStacking). It is the bar's box that is centred. The eight-point thumb that a
    /// reader takes hold of is centred in the box. The two-point ghost that shows while the wheel is
    /// turning is drawn at the right of that box, three points off the centre.</para>
    ///
    /// <para>Measured from the column's right edge, with right as positive. The writing ends
    /// <see cref="ScrollLane"/> short of that edge. Whatever the pane stands beside is
    /// <paramref name="beyond"/> past it: the groove's divider for the papers
    /// (<see cref="JoinEdge"/>), or the next column's first card for the tracklist (the rung's
    /// <c>Gap</c>).</para>
    /// </summary>
    public static double BarLift(double beyond)
        => ScrollBarBox / 2 + (beyond - ScrollLane) / 2;

    // ── the release card ──

    /// <summary>The least the facts in the release card are worth: a catalogue number and the line it
    /// is written on. Under this width, a key beside them takes width from writing that has none to
    /// spare. Measured on the page, where "Catalogue number: 88697 147879" is about 180 points at 12
    /// point italic.</summary>
    private const double PressingFactsFloor = 180;

    /// <summary>The least the key is taken to be, for the check that runs before the button has been
    /// measured. Its real width depends on the translated text, so the button is asked for its width.
    /// This number is only the fallback.</summary>
    private const double PressingKeyFloor = 120;

    /// <summary>
    /// Whether the release card can hold its key beside the facts, or has to put it underneath.
    /// </summary>
    /// <param name="inner">What the card holds, inside its border and padding.</param>
    /// <param name="key">The button's width, or zero if it has not been measured yet.</param>
    /// <param name="spacing">The gap between the two, from the card's grid.</param>
    public static bool PressingKeyBeside(double inner, double key, double spacing)
        => inner - spacing - Math.Max(key, PressingKeyFloor) >= PressingFactsFloor;

    /// <summary>The space the suggestions row keeps under itself for its own bar: the whole of the
    /// bar's box. A row that overflows then draws its bar clear of the cards, not across the foot of the
    /// card under the pointer.</summary>
    public const double SimilarBarLane = ScrollBarBox;

    /// <summary>The sleeve's cap on the stacked rung, where the papers stand under the record. It is
    /// 196 times 1.5. See that rung in <see cref="Rungs"/>.</summary>
    public const double StackedCover = 196 * 1.5;

    /// <summary>Widest first. The last rung is the floor: below it the page is shown as it is.</summary>
    public static readonly IReadOnlyList<AlbumPageRung> Rungs =
    [
        // Three columns, at the largest sizes.
        new(Cover: 305, Gap: 22, Pad: PageAir, PadFoot: PageAir, AlbumMin: 360, AboutMin: 310, GrooveMin: 300, AlbumStar: 1.1),

        // Still three columns, tighter: a smaller sleeve, less air around and between them, and each
        // column can be nearer its own floor. Everything on the page is still shown.
        new(Cover: 260, Gap: 18, Pad: PageAir, PadFoot: PageAir, AlbumMin: 340, AboutMin: 296, GrooveMin: 286, AlbumStar: 1.05),

        // The tightest three columns that are worth drawing. Narrower than this, the groove would be
        // narrower than one of its own cards, and a review would be a column of two words.
        new(Cover: 224, Gap: 14, Pad: PageAir, PadFoot: PageAir, AlbumMin: 320, AboutMin: 282, GrooveMin: 272, AlbumStar: 1.0),

        // The groove gives up its column and joins the tracks behind a strip. Nothing is removed from
        // the page. The groove is read when the reader asks for it.
        new(Cover: 224, Gap: 14, Pad: PageAir, PadFoot: PageAir, AlbumMin: 320, AboutMin: 282, GrooveMin: 0, AlbumStar: 1.0),

        // The papers go behind the same strip. The sleeve, the record's name and its songs are left
        // holding the card on their own. Four tabs over one column.
        //
        // This is the one rung whose sleeve is bigger than the rung above it. Here the album column takes
        // the full width of the card, and the page scrolls. The sleeve is limited by width alone, and a
        // sleeve of 196 looked small in a wide column. So the sleeve is 50% larger, 294 points. That is
        // still under the widest rung's 305, so the decode size set by that rung does not change.
        new(Cover: StackedCover, Gap: 12, Pad: PageAir, PadFoot: PageAir, AlbumMin: 300, AboutMin: 0, GrooveMin: 0, AlbumStar: 1.0),

        // The floor: a thumbnail of a sleeve and the record under it.
        new(Cover: 150, Gap: 10, Pad: PageAir, PadFoot: PageAir, AlbumMin: 240, AboutMin: 0, GrooveMin: 0, AlbumStar: 1.0),
    ];

    // ── the sleeve inside the album panel ──

    /// <summary>How far the record behind the sleeve sticks out past its right edge, as a share of the
    /// sleeve's width. A third. Less than this looks like a printing error on the sleeve. More than this,
    /// and the pair looks like two objects side by side rather than a record in its sleeve.</summary>
    public const double DiscShare = 0.34;

    /// <summary>
    /// The air the sleeve and its record keep on each side of themselves inside the album panel. This is
    /// a share of the panel, not a number of points.
    ///
    /// <para>A margin next to a picture is judged against the picture. Six points beside a 305-point
    /// sleeve is a hairline, and six points beside a 150-point one is a border. As a fraction, a narrower
    /// window draws a smaller sleeve with the same spacing. It does not press the same sleeve against both
    /// edges of the panel.</para>
    /// </summary>
    public const double SleeveAir = 0.06;

    /// <summary>The smallest width the sleeve is drawn at. Below this it is a thumbnail with the tracklist
    /// under it, and the page has already given up everything else it can.</summary>
    private const double SleeveFloor = 96;

    /// <summary>The width of the back key, AlbumDetailView's CloseButton, which is 32 by 28 points in the
    /// card's top-left corner. It stands on the line where the album panel starts, so the top band of the
    /// sleeve runs beside it. The panel's own air keeps them apart.</summary>
    public const double BackKey = 32;

    /// <summary>The air between the back key and the sleeve beside it. It is a number, not a share,
    /// because the key is 32 points wide at every window width. A share of a narrow panel would be too
    /// small to clear it.</summary>
    public const double BackKeyAir = 12;

    /// <summary>
    /// How wide the sleeve is drawn in an album panel of this width: the panel less its air on each side,
    /// less what the record standing out to the right of it takes, and capped at the rung's own
    /// <see cref="AlbumPageRung.Cover"/>.
    ///
    /// <para>The pair is what has to fit, not the sleeve alone. The disc sticks out past the sleeve's
    /// right edge, so a sleeve as wide as the whole panel would push its own record off the panel.</para>
    ///
    /// <para>The air on each side is the larger of two values: the share the sleeve keeps from the panel,
    /// and the lane the back key needs in the corner it shares with the top band of the sleeve. A share of
    /// a narrow panel can be smaller than the key. Then the sleeve's left edge moves in until it touches the
    /// key. The air is counted twice because the pair is centred. Moving the left edge one point to the
    /// right makes the sleeve two points narrower. So only the sleeve gives way, and nothing else on the
    /// panel moves. The height the sleeve gives up goes to the tracklist under it.</para>
    ///
    /// <para>On the current rungs, the key's lane is the value that binds at every width this page is drawn
    /// at. The share takes over on a panel wider than about 730 points. That is wider than any rung's
    /// <see cref="AlbumPageRung.Cover"/> lets the sleeve grow anyway. It is still written as a share,
    /// because that is the rule for how a sleeve sits on a panel. The key is a fact about one corner of one
    /// page.</para>
    /// </summary>
    public static double SleeveEdge(double headWidth, double cover)
    {
        var air = Math.Max(headWidth * SleeveAir, BackKey + BackKeyAir);
        return Math.Max(SleeveFloor, Math.Min((headWidth - air * 2) / (1 + DiscShare), cover));
    }

    /// <summary>
    /// The same answer, with the column's height included. The sleeve is also never so tall that the row
    /// of buttons under it is pushed off the bottom of the column.
    ///
    /// <para>The height ladder below always prices the head of the album column at
    /// <see cref="FactsHeight"/>: the sleeve at its floor, and the name, the stars and the buttons under it.
    /// But nothing made the sleeve that small. So a card that the ladder thought could hold the whole page
    /// would draw a 305-point sleeve into a row with room for 300, and cut the buttons in half. This
    /// method uses the row the column actually got, not an estimate. The estimate decides whether the
    /// suggestions are drawn at all. An estimate used in two places can disagree with itself.</para>
    /// </summary>
    /// <param name="headRoom">What the head of the column may stand in (see <see cref="HeadRoom"/>), or
    /// <see cref="double.PositiveInfinity"/> where the page scrolls and the column has no ceiling.</param>
    /// <param name="headRest">Everything in the head that is not the sleeve: the record's name, its line,
    /// the stars and the buttons, with the gaps between them. This is measured from the head, not assumed,
    /// because a two-line title is a different height from a one-line title.</param>
    public static double SleeveEdge(double headWidth, double headRoom, double headRest, double cover)
    {
        var byWidth = SleeveEdge(headWidth, cover);
        if (double.IsInfinity(headRoom) || headRoom <= 0 || headRest <= 0)
            return byWidth;

        return Math.Max(SleeveFloor, Math.Min(byWidth, headRoom - headRest));
    }

    // ── the record's name under the sleeve ──

    /// <summary>The air the name keeps on each side of itself under the sleeve: the close key's width on
    /// the right, where the key stands, and the same on the left so the centring is true. The name block
    /// has this margin in the XAML too. It is also written here, because the width the name is cut to is
    /// measured from the head. If the two numbers disagreed, a title would be cut to a width the block
    /// does not have.</summary>
    public const double NameAir = 26;

    /// <summary>The space the explicit badge beside the name takes, when the record has one: the badge's 16
    /// points and the 10 points between it and the last letter.</summary>
    public const double NameBadge = 26;

    /// <summary>The narrowest the name is cut to. Below this the panel has already given up everything else,
    /// and a title cut down to only an ellipsis names no record at all.</summary>
    private const double NameFloor = 80;

    /// <summary>
    /// How wide the record's name may run before it is cut short with an ellipsis.
    ///
    /// <para>The name and its badge sit in a horizontal stack. A horizontal stack measures its contents
    /// with infinite width. So a name in one neither wraps nor trims. It grows past the panel and is clipped
    /// at both ends by the centring. The width is therefore set on the name itself: the head's measured
    /// width, less the air on each side, and less what the badge takes when the record has one.</para>
    /// </summary>
    public static double NameWidth(double headWidth, bool badge)
        => Math.Max(NameFloor, headWidth - NameAir * 2 - (badge ? NameBadge : 0));

    /// <summary>The height the head of the album column may use: the row the column got, less the gap under
    /// the head and the tracklist's own floor. The list of songs is never pushed off the bottom of the card,
    /// and neither are the buttons. That is all this arithmetic does.</summary>
    public static double HeadRoom(double columnHeight)
        => columnHeight - HeadTracksGap - TracksFloor;

    /// <summary>The gap between the head of the album column and the tracklist under it. It is set in the
    /// markup. See the RowSpacing of the album column in AlbumDetailView.xaml.</summary>
    public const double HeadTracksGap = 14;

    /// <summary>
    /// The largest the sleeve is ever drawn on any rung. It is also the size the cover is decoded at.
    ///
    /// <para>This is read from the rung table, not from the rung the page is on. The two are set at different
    /// times. The sleeve is requested when the record is opened, and the rung is chosen in the card's first
    /// size pass. If the picture were decoded for a rung not yet chosen, the sleeve would stay soft, and nothing
    /// would later reload it. The widest value is correct whenever it is asked for. A narrower rung draws the
    /// same picture smaller. That costs some memory, but no sharpness. See <c>AlbumDetailView.SleeveDecodeEdge</c>.</para>
    /// </summary>
    public static double WidestCover => Rungs.Max(r => r.Cover);

    /// <summary>The widest rung this card can hold.</summary>
    public static AlbumPageRung For(double width)
    {
        var rung = 0;
        while (rung < Rungs.Count - 1 && Rungs[rung].Demand > width)
            rung++;
        return Rungs[rung];
    }

    // ── how much height there is, and what goes when there is not enough ──

    /// <summary>Everything in the head of the album column that is not the sleeve: the record's name and its
    /// line, the stars, the row of buttons, and the three gaps between them. Measured from the page as it is
    /// drawn (50 + 34 + 34, plus three gaps of 14), not estimated.</summary>
    private const double HeadRest = 160;

    /// <summary>The smallest sleeve this page will draw before it starts removing whole rows. This is not
    /// <see cref="SleeveFloor"/>, which is where the sleeve stops shrinking at all. This is the size below
    /// which the record's own picture is a thumbnail, and a suggestions strip is not worth that.
    ///
    /// <para>It is 140, not a round 150. The ten points are slack around a measurement. On a card 766 points
    /// tall, the card clears the threshold by 11 points, not by 1. So a row that is drawn does not vanish on
    /// the next point of a drag.</para></summary>
    public const double SleeveKept = 140;

    /// <summary>The head of the album column at its shortest: the sleeve at <see cref="SleeveKept"/>, with
    /// everything under it.</summary>
    private const double FactsHeight = SleeveKept + HeadRest;

    /// <summary>
    /// The space the page spends between the row of columns and whatever stands under it: one of the rung's
    /// gaps where the suggestions are drawn, and nothing where they are not.
    ///
    /// <para>The page's rows carry their own air as a margin (see ApplyStacking), so an empty row costs
    /// nothing. The grid's RowSpacing is not used for this, because it would spend air between three rows, and
    /// the middle one is usually empty. The row that is drawn costs one rung gap, and that cost is counted here.</para>
    /// </summary>
    private static double RowAir(AlbumPageRung rung, SimilarState row)
        => row == SimilarState.Gone ? 0 : rung.Gap;

    /// <summary>What the row costs the page in its current state: nothing when the record has no suggestions,
    /// a heading when it is shut, and the sleeves when it is open.</summary>
    public static double SimilarCost(AlbumPageRung rung, SimilarState row) => row switch
    {
        SimilarState.Gone => 0,
        SimilarState.Shut => RowAir(rung, row) + SimilarShut,
        _ => RowAir(rung, row) + SimilarHeight,
    };

    /// <summary>The promoted review above the tracks: its heading, the plate it sits on, the five lines of its
    /// opening paragraph, and the reviewer's name and stars along the bottom.</summary>
    private const double TopReviewHeight = 180;

    /// <summary>The least the tracklist is worth drawing: its heading and five songs below it. Measured on the
    /// page (a row is 24.5, the heading 16.5, and the gap under it 6). A floor of six rows would leave no room
    /// for the suggestions row on a card the size of a typical laptop, so the floor is five.</summary>
    private const double TracksFloor = 150;

    /// <summary>What the suggestions row is made of above its own foot lane: the rule, the heading, and a row of
    /// sleeves with their two lines of text. Measured on the page (the block is 229.5 points with a six-point lane
    /// under it). It is a floor, not an exact value. The sleeves scale with the width of the two columns above, so
    /// a wider card spends a little more space here than this value says.</summary>
    private const double SimilarContent = 223.5;

    /// <summary>The whole row: its content, and the lane its own bar stands in.</summary>
    private const double SimilarHeight = SimilarContent + SimilarBarLane;

    /// <summary>The row when shut: the seam, the air under it, and the heading the reader opens it by. Measured
    /// from the markup: SimilarBlock's spacing of 8, with a 2-point seam and a 16.5-point heading in it.</summary>
    private const double SimilarShut = 27;

    /// <summary>The least the head of the album column is worth scrolling inside. This is public because the live
    /// pass uses the same number as the floor for the row's own answer. A row too short for this leaves the head
    /// scrolling in the little space there is, never in none.</summary>
    public const double FactsFloor = 160;

    /// <param name="chrome">The air the page keeps above and below its columns (see
    /// <see cref="AlbumPageRung.Chrome"/>). It is passed in, not assumed. A height ladder that guessed this value
    /// could promise room the page has already used. The air above and below does not change with the height, so
    /// the rung's own value is used at every call site.</param>
    /// <summary>
    /// What the row of columns gets on a card of this height: the card, less the air above and below, less the
    /// cost of the suggestions row where it is drawn, including its gap.
    ///
    /// <para>The page does not use this value. The album column measures the row it actually got (see
    /// AlbumDetailView.FitAlbumColumn), because a row's height is a fact and this is only an estimate of it. This
    /// method exists so that the ladder can be checked against its own arithmetic. A rung that says the suggestions
    /// fit is promising that what is left over still holds a sleeve, a name and a row of buttons.</para>
    /// </summary>
    public static double ColumnRoom(double height, AlbumPageRung rung, SimilarState row)
        => height - rung.Chrome - SimilarCost(rung, row);

    /// <summary>
    /// Whether a card this tall can show the suggestions open and still be the record's page: a sleeve worth
    /// looking at, the record's name, its buttons, and a tracklist under them.
    ///
    /// <para>This is the answer for a reader who has not chosen either way. Once the reader has opened or shut
    /// the row, that choice is the answer. A short card then shrinks the sleeve instead (see
    /// AlbumPageDensity.SleeveEdge, and the setting behind it, SettingsService.AlbumSimilarOpen).</para>
    ///
    /// <para>The suggestions row outlasts the promoted review, which is the other thing this page can give up. The
    /// review appears twice: the copy in the papers column is a shortcut to the first card in the groove. The
    /// suggestions appear nowhere else. So a short window drops the duplicate before it drops the only copy.</para>
    /// </summary>
    public static bool HoldsOpenSimilar(double height, AlbumPageRung rung)
        => HeadRoom(ColumnRoom(height, rung, SimilarState.Open)) >= FactsHeight;

    /// <summary>How much of the screen the window must take before the groove opens by itself.</summary>
    public const double GrooveWindowShare = 2.0 / 3.0;

    /// <summary>
    /// Whether the window takes enough of the screen that the groove is simply part of the page: open, with no
    /// fold offered.
    /// </summary>
    /// <remarks>
    /// <b>True is a rule, not a default.</b> Above the share there is no key and no folding, whatever the reader
    /// chose in a smaller window. Their choice is kept, and it applies again as soon as they give the screen back.
    /// Below the share, the reader's choice decides, and this arithmetic only says what an unanswered page does.
    /// See AlbumDetailView.GrooveShutWanted and ShowGrooveKeys, which are the two readers of it, and
    /// SettingsService.AlbumGrooveOpen for what is kept.
    ///
    /// <para><b>The window is compared with the screen, not the card with itself.</b> Every other measure on this
    /// page is a width in points inside the library card, and that decides how much fits. This one is not about
    /// fitting. A window that takes two thirds of the screen is one the reader has given to the app. A window that
    /// takes less has been placed beside something else, so the reader is looking at the record rather than at
    /// what other people said about it.</para>
    ///
    /// <para>Both widths are in the same units, physical pixels: the window's own <c>AppWindow.Size</c>, and the
    /// <c>DisplayArea.WorkArea</c> of the screen it is on. A screen width of zero means the reading failed. In that
    /// case the groove opens, rather than letting a display the app could not measure decide for the reader.</para>
    /// </remarks>
    public static bool GrooveOpensItself(double windowWidth, double screenWidth)
        => screenWidth <= 0 || windowWidth >= screenWidth * GrooveWindowShare;

    /// <summary>Whether the promoted review keeps its place in the papers column. It does only on a card with room
    /// for the whole page, suggestions included.</summary>
    public static bool ShowsTopReview(double height, AlbumPageRung rung, SimilarState row)
        => HeadRoom(ColumnRoom(height, rung, row)) >= FactsHeight + TopReviewHeight;

    /// <summary>
    /// How tall the head of the album column may be before it scrolls inside its own pane: what is left of the card
    /// once the suggestions, the row's own air and the tracklist's floor have their share.
    ///
    /// <para>This is an estimate of a row the page has not been given yet. The page uses the lower of this value and
    /// the row it actually got (see AlbumDetailView.FitAlbumColumn). The sleeve, the record's name and its play
    /// button are what the page is, so they are never the part that goes. But they may not push the list of songs
    /// off the bottom of the card either.</para>
    /// </summary>
    public static double FactsCap(double height, AlbumPageRung rung, SimilarState row)
        => Math.Max(FactsFloor, HeadRoom(ColumnRoom(height, rung, row)));
}
