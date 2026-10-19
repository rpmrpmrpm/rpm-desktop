namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// Where a drag inside one record is putting the rows it carries.
///
/// <para>This is arithmetic only, kept away from the grid. The rule that decides what a pointer position
/// means can then be tested without a window, a pointer or a drag.</para>
///
/// <para>The question is always a GAP: a number from 0 to n that says how many of the record's rows
/// sit above the line. Rows are split at the middle. A pointer past the halfway mark of a row is in the
/// gap below it. A drop between two tracks is always a reorder. There is no "onto a record" answer here.
/// A drag in this pane only arranges one record. It never moves tracks into another record.</para>
/// </summary>
public static class QueueReorder
{
    /// <summary>
    /// The gap a pointer at <paramref name="y"/> is asking for, as a count of rows above the line.
    ///
    /// <para><paramref name="bands"/> holds the record's rows that the grid is drawing, in draw order. The
    /// answer is kept inside them. A drag that goes past the top or bottom of the record lands at the end
    /// of the record, not in whatever is beside it. The two flags say whether the drawn block reaches
    /// those ends. A record scrolled halfway has rows above the first visible one, so the line must not
    /// offer a place in front of them.</para>
    /// </summary>
    public static int SlotAt(
        IReadOnlyList<(double Top, double Bottom)> bands, bool blockStartsRecord, bool blockEndsRecord, double y)
    {
        if (bands.Count == 0)
            return 0;

        var slot = bands.Count;
        for (var i = 0; i < bands.Count; i++)
        {
            if (y < (bands[i].Top + bands[i].Bottom) / 2)
            {
                slot = i;
                break;
            }
        }

        var lowest = blockStartsRecord ? 0 : 1;
        var highest = blockEndsRecord ? bands.Count : bands.Count - 1;
        return Math.Clamp(slot, lowest, Math.Max(lowest, highest));
    }

    /// <summary>Where the insertion line is drawn for a gap: the top of the row below it, or the
    /// bottom of the last row when the gap is the end of the block.</summary>
    public static double LineAt(IReadOnlyList<(double Top, double Bottom)> bands, int slot)
    {
        if (bands.Count == 0)
            return 0;
        if (slot <= 0)
            return bands[0].Top;
        return slot >= bands.Count ? bands[^1].Bottom : bands[slot].Top;
    }

    /// <summary>
    /// The rows either side of the gap that are not themselves being carried, as indices into the
    /// block, or null for the start and the end of the record.
    ///
    /// <para>Carried rows are skipped on purpose. A row cannot be dropped next to itself, because the gap
    /// it would fill is empty.</para>
    /// </summary>
    public static (int? Above, int? Below) Neighbours(IReadOnlyList<bool> carried, int slot)
    {
        int? above = null;
        for (var i = slot - 1; i >= 0; i--)
        {
            if (!carried[i])
            {
                above = i;
                break;
            }
        }

        int? below = null;
        for (var i = slot; i < carried.Count; i++)
        {
            if (!carried[i])
            {
                below = i;
                break;
            }
        }

        return (above, below);
    }

    /// <summary>The order the block would read in after the drop, as indices into the block. The
    /// carried rows keep the order they were picked up in.</summary>
    public static List<int> Apply(IReadOnlyList<bool> carried, int slot)
    {
        var moving = new List<int>();
        var staying = new List<int>();
        for (var i = 0; i < carried.Count; i++)
            (carried[i] ? moving : staying).Add(i);

        // How many rows that stay in place sit above the line. The carried rows go at that point.
        var at = 0;
        for (var i = 0; i < slot && i < carried.Count; i++)
        {
            if (!carried[i])
                at++;
        }

        var result = new List<int>(carried.Count);
        result.AddRange(staying.Take(at));
        result.AddRange(moving);
        result.AddRange(staying.Skip(at));
        return result;
    }

    /// <summary>Whether the drop would actually move anything. A drop that puts the rows back where they
    /// were asks the store for nothing. A write costs the reader their place in the list, so it must not
    /// be spent on a drag that changed nothing.</summary>
    public static bool Moves(IReadOnlyList<bool> carried, int slot)
    {
        var after = Apply(carried, slot);
        for (var i = 0; i < after.Count; i++)
        {
            if (after[i] != i)
                return true;
        }
        return false;
    }
}
