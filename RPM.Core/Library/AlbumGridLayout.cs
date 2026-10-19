namespace RPM.Core.Library;

/// <summary>
/// The arithmetic behind the album grid's "wall of covers with an inline flip-down".
///
/// The grid is a vertical stack of ROWS, each row a fixed number of cards. Expanding a card opens a
/// full-width band under the row that owns it (the same pattern as iTunes' expander). The band is part of
/// the row, so the whole thing virtualises as an ordinary vertical list, with no custom layout. What that
/// costs is this: the flat album list has to be re-chunked into rows whenever the column count changes (a
/// resize), and the column count is a function of the panel's width. Both are pure integer and list math,
/// so they live here, where a test can pin them down, away from the control's measure pass.
/// </summary>
public static class AlbumGridLayout
{
    /// <summary>
    /// How many cards best fit across a panel <paramref name="available"/> px wide when the user's
    /// TARGET card stride is <paramref name="targetStride"/> px. This rounds to the NEAREST whole column
    /// count, not down, so the realised card lands as close to the target as a whole number of columns
    /// allows. 5.5 target-columns become 6 slightly-smaller cards, not 5 wide ones. The card is then
    /// stretched to <see cref="StrideFor"/> so the row fills exactly, with no dead gutter. There is always
    /// at least one column. A panel narrower than a single card still shows one (clipped) rather than a
    /// blank wall. An unmeasured (zero or negative) width also answers one, so the first chunk is never
    /// empty.
    /// </summary>
    /// <param name="maxStride">The widest a card may be. The count is never so low that the row's share
    /// exceeds it. That is what makes "stretched to fill exactly" true at every size.</param>
    /// <remarks>
    /// <b>Why the ceiling has to be decided HERE and not by the stretch.</b> <see cref="StrideFor"/>
    /// clamps to the same number, and a clamp is not a fit. A row of columns that each want to be wider
    /// than a card is allowed to simply stop short, and what is left over is dead panel down the right-hand
    /// side. This is worst at the TOP of the size slider, where the target and the ceiling are the same
    /// number. The rounding then decides it. Round down by a third of a column, and a third of a column of
    /// panel is empty.
    ///
    /// <para>So the count is raised until the share fits under the ceiling. The clamp in
    /// <see cref="StrideFor"/> then becomes a guard that no longer fires. What the ceiling was for (one
    /// card in a wide panel must not blow up to the panel's full width) is now served by the count
    /// instead of by a gap.</para>
    /// </remarks>
    public static int ColumnsForTarget(
        double available, double targetStride, int min = 1, double maxStride = double.PositiveInfinity)
    {
        if (targetStride <= 0 || double.IsNaN(available) || available <= 0)
            return min;

        var nearest = (int)Math.Round(available / targetStride, MidpointRounding.AwayFromZero);

        var fewest = maxStride > 0 && !double.IsNaN(maxStride) && !double.IsInfinity(maxStride)
            ? (int)Math.Ceiling(available / maxStride)
            : 1;

        return Math.Max(min, Math.Max(nearest, fewest));
    }

    /// <summary>
    /// The realised card stride once the row is split into <paramref name="columns"/> whole cards. The
    /// panel width is shared out evenly and floored, so <c>columns × stride ≤ available</c> and the last
    /// card is never pushed under the scrollbar. The result is capped at <paramref name="maxStride"/>, so
    /// a nearly empty wall (one card in a wide panel) does not blow that card up to the panel's full width.
    /// An unmeasured (zero or negative) width answers the cap, which is the widest a card is ever allowed.
    ///
    /// <para>The cap is a GUARD, not the fit. Ask <see cref="ColumnsForTarget"/> for a count with the same
    /// ceiling, and the share is already under it, so this never has to clamp. Where it does clamp, the row
    /// stops short and leaves dead panel beside it. See that method.</para>
    /// </summary>
    public static double StrideFor(double available, int columns, double maxStride)
    {
        columns = Math.Max(1, columns);
        if (double.IsNaN(available) || available <= 0)
            return maxStride;
        return Math.Clamp(Math.Floor(available / columns), 1, maxStride);
    }

    /// <summary>
    /// Both halves of the sizing in one answer: how many columns a target stride asks for, and the
    /// stride they are actually laid out at once the row is filled edge to edge.
    ///
    /// <para>
    /// The column count is clamped to <paramref name="maxColumns"/>. A hand-driven size needs this, and a
    /// slider does not. A pinch can ask for covers the size of postage stamps. A wall nine albums across on
    /// a phone is a texture, not a library. Below the clamp, the answer is exactly what the desktop's
    /// slider gives: the nearest whole column, then stretched so nothing is left over.
    /// </para>
    /// </summary>
    public static (int Columns, double Stride) Fit(
        double available, double targetStride, int maxColumns, int minColumns = 1)
    {
        minColumns = Math.Max(1, minColumns);
        maxColumns = Math.Max(minColumns, maxColumns);

        var columns = Math.Clamp(ColumnsForTarget(available, targetStride, minColumns), minColumns, maxColumns);

        // The cap is the panel itself. At one column the card is the whole width, which is the point of
        // pinching that far out.
        return (columns, StrideFor(available, columns, Math.Max(1, available)));
    }

    /// <summary>Slack allowed before the scroller counts as "at the top". A ChangeView can leave a
    /// fraction of a pixel of residue. That is not a real scroll position.</summary>
    private const double AtTopEpsilon = 1;

    /// <summary>
    /// Whether the wall's virtualising repeater has lost its place in the rows.
    ///
    /// <para>The repeater places the rows it has not realised off an ESTIMATE, and it only ever corrects
    /// that estimate against rows it HAS realised. A layout pass the wall takes without a real viewport
    /// realises nothing. Examples are a panel that glided shut, a view collapsed behind an album page, or a
    /// page that arrived unmeasured. The estimate that pass produces can put every row outside the viewport.
    /// Nothing is realised then, so nothing corrects it, and the wall cannot recover on its own. It is stuck
    /// blank, or stuck showing some row halfway down with the scrollbar already at the top.</para>
    ///
    /// <para>Three invariants describe this without knowing anything about the estimate. A wall that has
    /// rows has at least one of them realised. A wall scrolled to the top shows its first row. And a wall
    /// scrolled to the top has that first row's top edge ON the top of the viewport.
    /// <paramref name="everRealized"/> keeps a wall that simply has not been laid out yet (no rows realised,
    /// because no pass has run) from reading as broken.</para>
    ///
    /// <para><paramref name="firstRowTop"/> is where row 0 sits relative to the top of the CONTENT, or NaN
    /// when row 0 is not realised. It is the content, not the viewport. A scroller's own top padding is
    /// where the wall is supposed to start. Handing that padding in as displacement reads as a lost anchor
    /// at every rest at the top. That is answered by a re-seat, and a re-seat flashes every cover on screen.
    /// This value is what the first two invariants miss. The repeater keeps a cache of rows either side of
    /// the viewport, so row 0 can be realised while the wall reads healthy. Row 0 may be laid out ABOVE the
    /// visible area, with the scrollbar at the top. That is the wall that will not scroll to its first row:
    /// the offset cannot go below zero, and at zero the rows are still placed too high. Row 0's own position
    /// is the only thing that catches it.</para>
    /// </summary>
    public static bool AnchorLost(
        int rowCount, int firstRealizedRow, double verticalOffset, bool everRealized,
        double firstRowTop = double.NaN)
    {
        if (rowCount <= 0 || !everRealized)
            return false;
        if (firstRealizedRow < 0)
            return true;
        if (verticalOffset > AtTopEpsilon)
            return false;
        return firstRealizedRow > 0
            || (!double.IsNaN(firstRowTop) && Math.Abs(firstRowTop) > TopRowEpsilon);
    }

    /// <summary>
    /// Whether opening a record should move the wall first. The row glides to the top of the viewport and
    /// the drawer unfolds there, rather than unfolding where the record stands.
    ///
    /// <para>The band opens UNDER its row. A record in the bottom half of the screen has almost no room left
    /// to open into, so the drawer would come out mostly below the fold. A record in the top half has the rest
    /// of the screen under it already. Moving the wall for it would be a jump the reader did not ask for. The
    /// middle of the record is compared with the middle of the viewport. That is the whole rule.</para>
    ///
    /// <para><paramref name="rowTop"/> is the row's top edge relative to the top of the VIEWPORT (negative
    /// when it is scrolled partly off the top). <paramref name="rowHeight"/> is the height of its cards alone.
    /// A row measured with a drawer already hanging under it has a middle far below the one the reader sees.</para>
    /// </summary>
    public static bool LiftsBeforeOpening(double rowTop, double rowHeight, double viewportHeight)
    {
        if (viewportHeight <= 1 || rowHeight <= 0 || double.IsNaN(rowTop))
            return false;
        return rowTop + rowHeight / 2 > viewportHeight / 2;
    }

    /// <summary>
    /// The length to WRITE on the drawer, given the length a measure said it WANTS.
    ///
    /// <para>These are two different numbers, and the difference is the margin. A measure answers with the
    /// room the element needs in its parent, margin included. <c>Height</c> and <c>ActualHeight</c> are the
    /// box inside that margin. If a measure is handed straight to <c>Height</c>, the drawer is written with
    /// the margin too large. It holds that for the length of the tween, and loses it the instant the height
    /// goes back to Auto. The result is that the drawer opens a little past its size and then settles back.
    /// On a switch between two records with the same number of tracks, it also grows and shrinks, although
    /// nothing changed.
    /// </para>
    ///
    /// <para>The result is zero where the measure never happened, or is smaller than the margin around it.
    /// The caller reads that as nothing to unfold, and simply shows the drawer.</para>
    /// </summary>
    public static double DrawerLength(double desiredHeight, double marginTop, double marginBottom)
    {
        if (double.IsNaN(desiredHeight) || double.IsNaN(marginTop) || double.IsNaN(marginBottom))
            return 0;
        return Math.Max(0, desiredHeight - marginTop - marginBottom);
    }

    /// <summary>
    /// Whether a re-fed shelf is a DIFFERENT shelf (different records, or the same ones in a different
    /// order) rather than the one already on screen, told something new about itself.
    ///
    /// <para>A virtualising list cannot be trusted to work this out from the feed alone. The repeater fills
    /// the band around the offset it is standing at. If the collection is narrowed to one record, that band
    /// can be past the end of the new list. The pass then realises nothing and arranges nothing, and the
    /// blocks from the shelf before the search stay on screen. The answer is to hand the repeater a list it
    /// cannot mistake for the last one: drop the source, then feed it.</para>
    ///
    /// <para>This is why the same records in the same order must read as false. A cover arriving, a favourite
    /// marked, or an album retagged re-feeds the shelf several times a minute. Rebuilding every block for one
    /// of those would flash the whole panel and lose the reader's place for nothing.</para>
    /// </summary>
    public static bool ShelfReplaced(IReadOnlyList<string> onScreen, IReadOnlyList<string> incoming)
    {
        if (onScreen.Count != incoming.Count)
            return true;
        for (var i = 0; i < incoming.Count; i++)
        {
            if (!string.Equals(onScreen[i], incoming[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Where the middle of one card sits across a sideways-scrolling row. The result is null where that
    /// card is not on screen.
    ///
    /// <para>A drawer that opens under such a row has a triangle that points up at its record. The row
    /// scrolls sideways, but the drawer does not. So the triangle's place is not fixed by the card's index,
    /// as it is on a wall whose cards sit on a fixed stride. It depends on that index READ AGAINST the row's
    /// current offset, which has to be worked out again every time the row moves.</para>
    ///
    /// <para>Null is the second half of the answer, and not a failure. A card scrolled off either end has no
    /// place on screen for a triangle to stand. A triangle aimed at a card that is no longer there is worse
    /// than none at all. <paramref name="clearance"/> is how much room the mark needs to be drawn whole, which
    /// is half its own width. This keeps it from being cut in half by the row's edge.</para>
    ///
    /// <para>The result is measured from the row's own left edge, in the row's own space. The caller converts
    /// it to wherever its triangle lives. <paramref name="padding"/> is the space a card keeps either side of its
    /// sleeve. That is what makes the sleeve's middle sit right of the card's own left edge.</para>
    /// </summary>
    public static double? CardMiddleOnScreen(
        int index, double stride, double sleeve, double padding,
        double offset, double viewport, double clearance)
    {
        if (index < 0 || stride <= 0 || sleeve <= 0 || viewport <= 0
            || double.IsNaN(offset) || double.IsNaN(viewport))
            return null;

        var middle = (index * stride) + padding + (sleeve / 2) - offset;
        return middle < clearance || middle > viewport - clearance ? null : middle;
    }

    /// <summary>How far row 0's top edge may sit from the top of the viewport, with the scroller at rest at
    /// the top, before the wall counts as misplaced. A pixel of rounding is not a lost anchor. Half a row is.</summary>
    private const double TopRowEpsilon = 2;

    /// <summary>
    /// Cuts a flat list into rows of <paramref name="columns"/> in order. The first <c>columns</c> items are
    /// row 0, the next are row 1, and so on. The last row carries the remainder (it is not padded). Column
    /// counts below one are treated as one, so a bad width can never drop items.
    /// </summary>
    public static List<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> items, int columns)
    {
        columns = Math.Max(1, columns);
        var rows = new List<IReadOnlyList<T>>((items.Count + columns - 1) / columns);
        for (var start = 0; start < items.Count; start += columns)
        {
            var count = Math.Min(columns, items.Count - start);
            var row = new List<T>(count);
            for (var i = 0; i < count; i++)
                row.Add(items[start + i]);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Splits a track list into the band's two columns, the way iTunes lays them out: down the left column
    /// first, then the right. The left column takes the extra when the count is odd (1-6 left, 7-11 right for
    /// eleven tracks), so the two columns are never more than one row apart in height.
    ///
    /// <paramref name="isSectionStart"/> marks the entries that open a section, such as the disc headers of a
    /// multi-disc album. The break then prefers the section boundary nearest the middle. So a double album
    /// reads disc 1 down the left and disc 2 down the right, instead of cutting disc 2 in half. A boundary is
    /// only taken while it leaves each column at least <see cref="MinColumnShare"/> of the entries. An
    /// eleven-track disc next to a two-track one still splits evenly, rather than leaving one column nearly
    /// empty. If no boundary qualifies, a header is never left as the left column's last row, where it would
    /// sit with nothing under it.
    /// </summary>
    public static (IReadOnlyList<T> Left, IReadOnlyList<T> Right) SplitTwoColumns<T>(
        IReadOnlyList<T> items, Func<T, bool>? isSectionStart = null)
    {
        var leftCount = (items.Count + 1) / 2;
        if (isSectionStart is not null && items.Count > 1)
            leftCount = BreakPoint(items, isSectionStart, leftCount);

        var left = new List<T>(leftCount);
        var right = new List<T>(items.Count - leftCount);
        for (var i = 0; i < items.Count; i++)
            (i < leftCount ? left : right).Add(items[i]);
        return (left, right);
    }

    /// <summary>The least of the entries a column may keep when the break moves to a section boundary.</summary>
    private const double MinColumnShare = 0.4;

    private static int BreakPoint<T>(IReadOnlyList<T> items, Func<T, bool> isSectionStart, int balanced)
    {
        var floor = (int)Math.Ceiling(items.Count * MinColumnShare);
        var best = -1;
        for (var i = 1; i < items.Count; i++)   // index 0 opens the first section, never a break
        {
            if (!isSectionStart(items[i]) || i < floor || items.Count - i < floor)
                continue;
            if (best < 0 || Math.Abs(i - balanced) < Math.Abs(best - balanced))
                best = i;
        }
        if (best >= 0)
            return best;

        // No usable boundary: keep the balanced split, but push a header that landed on the left
        // column's last row over to the top of the right one.
        return balanced > 0 && isSectionStart(items[balanced - 1]) ? balanced - 1 : balanced;
    }
}
