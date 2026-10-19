namespace RPM.Core.Library;

/// <summary>Where each row of the album wall sits, and how long the wall therefore is.
///
/// <para>The wall is a stack of rows that all have exactly the same height. The card template keeps them
/// that way, even for a record with no title or artist line. The one exception is the row whose record is
/// open. That row carries its drawer underneath its covers.</para>
///
/// <para>This exception is the reason this type exists. A virtualizing <c>StackLayout</c> does not know how
/// long a collection is. It keeps the row heights it has measured, takes their mean, and computes both the
/// length of the wall and where its content starts from that one number: <c>start = firstRealizedRowTop -
/// firstRealizedIndex * mean</c>. A tall drawer on the first row makes the wall's length change when that
/// row scrolls out of the realized range. The length then drops, and a scroller cannot stand at an offset
/// past its own end. So the reader is pulled back to the top. Scroll anchoring does not fix this, because
/// the correction is clamped against the same wrong end.</para>
///
/// <para>Nothing here estimates. Given the row height, the gap, and which row carries the drawer and by how
/// much, every answer is arithmetic. The answer is the same whether the row is on screen or a thousand rows
/// away.</para>
///
/// <para>All lengths are in layout units. <paramref name="bandRow"/> is -1 when no record is open.
/// <c>bandHeight</c> is what the drawer adds to that row, not the row's total height.</para>
/// </summary>
public static class AlbumWallRows
{
    /// <summary>The top edge of <paramref name="index"/>, measured from the top of the wall.</summary>
    public static double RowTop(int index, double rowHeight, double spacing, int bandRow, double bandHeight)
    {
        if (index <= 0)
            return 0;
        var top = index * (rowHeight + spacing);
        return bandRow >= 0 && bandRow < index ? top + Math.Max(0, bandHeight) : top;
    }

    /// <summary>How tall <paramref name="index"/> is: the uniform height, plus the drawer on the one row
    /// that has it.</summary>
    public static double RowHeight(int index, double rowHeight, int bandRow, double bandHeight)
        => index >= 0 && index == bandRow ? rowHeight + Math.Max(0, bandHeight) : rowHeight;

    /// <summary>The whole wall, from the top of the first row to the bottom of the last. It is zero for no
    /// rows. An empty wall has no length, which is different from the length of one row.
    /// </summary>
    public static double WallHeight(int count, double rowHeight, double spacing, int bandRow, double bandHeight)
    {
        if (count <= 0)
            return 0;
        var height = count * rowHeight + (count - 1) * spacing;
        return bandRow >= 0 && bandRow < count ? height + Math.Max(0, bandHeight) : height;
    }

    /// <summary>Where the reader has to stand so the row under their eye stays under it, after the drawer
    /// has moved to another row or gone altogether.
    ///
    /// <para>The drawer is the one row that is not a row tall, and it can be thousands of points tall. Take
    /// it off a row ABOVE the reader and every row below moves up by its whole height. The end of the wall
    /// moves with them. A reader standing past that new end is clamped to it, and the wall would fall out
    /// from under a click on a record further down. Here the wall's length is right, but it changes while
    /// the reader is looking at it.</para>
    ///
    /// <para>So the offset moves by exactly as much as the anchor row's top edge moves. That row is at the
    /// top of the viewport, and its movement is the movement of everything the reader can see. The offset
    /// is never above the top of the wall, which is as far up as a scroller goes.</para>
    /// </summary>
    /// <param name="wasRowTop">The anchor row's top edge before the move. This is <see cref="RowTop"/> with
    /// the drawer where it was.</param>
    /// <param name="nowRowTop">The same row's top edge after the move. This is <see cref="RowTop"/> with
    /// the drawer where it now is.</param>
    public static double OffsetHolding(double offset, double wasRowTop, double nowRowTop)
        => Math.Max(0, offset + (nowRowTop - wasRowTop));

    /// <summary>The row that <paramref name="y"/> falls in, clamped to the wall at both ends.
    ///
    /// <para>This makes two straight divisions, with the drawer's own row taken out between them, rather
    /// than walking the rows. Above the open row the pitch is uniform. Below it, the whole wall is pushed
    /// down by the drawer's height. Inside the open row there is one answer.</para>
    /// </summary>
    public static int RowAt(double y, int count, double rowHeight, double spacing, int bandRow, double bandHeight)
    {
        if (count <= 0)
            return -1;
        var pitch = rowHeight + spacing;
        if (pitch <= 0)
            return 0;
        if (y <= 0)
            return 0;

        if (bandRow >= 0 && bandRow < count && bandHeight > 0)
        {
            var bandTop = bandRow * pitch;
            if (y >= bandTop)
            {
                // The drawer's own row runs to the bottom of the drawer. Everything past it is the plain
                // wall again, shifted down by exactly the height of the drawer.
                var bandBottom = bandTop + rowHeight + bandHeight + spacing;
                if (y < bandBottom)
                    return bandRow;
                y -= bandHeight;
            }
        }

        return Math.Clamp((int)(y / pitch), 0, count - 1);
    }

    /// <summary>The rows that touch the band of wall between <paramref name="top"/> and
    /// <paramref name="bottom"/>. This is what a virtualizing pass has to hold. Both ends are included, so
    /// the count is <c>Last - First + 1</c>. It returns (-1, -1) for an empty wall or a band that is upside
    /// down.</summary>
    public static (int First, int Last) RowsIn(
        double top, double bottom, int count, double rowHeight, double spacing, int bandRow, double bandHeight)
    {
        if (count <= 0 || bottom < top)
            return (-1, -1);
        var first = RowAt(top, count, rowHeight, spacing, bandRow, bandHeight);
        var last = RowAt(bottom, count, rowHeight, spacing, bandRow, bandHeight);
        return (first, Math.Max(first, last));
    }
}
