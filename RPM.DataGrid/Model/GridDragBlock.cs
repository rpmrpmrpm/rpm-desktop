namespace RPM.DataGrid.Model;

/// <summary>
/// What a row drag is carrying, and how far it is allowed to go.
///
/// <para>The host supplies it when a drag begins, because only the host knows what a run of rows
/// MEANS. In the import queue a block is one record, and a drop past its last track would give those
/// files the next record's album. The grid handles the gesture; the host says where it may
/// land.</para>
/// </summary>
/// <param name="First">The first row the drop may land at or after.</param>
/// <param name="Last">The last row the drop may land at or before. Inclusive.</param>
/// <param name="Carried">Which rows are being moved. Always inside [First, Last].</param>
public sealed record GridDragBlock(int First, int Last, SpanSet Carried)
{
    /// <summary>How many rows the block spans.</summary>
    public int Count => Last - First + 1;

    /// <summary>
    /// Whether a drop at <paramref name="slot"/> would put the carried rows anywhere other than where
    /// they already are.
    ///
    /// <para>A gesture that changes nothing sends nothing to the store, because the re-read after a
    /// reorder loses the user's position in the list.</para>
    ///
    /// <para>A slot is "no move" when the carried rows are already exactly the rows on each side of
    /// it: every carried row is contiguous and the run ends at the slot or begins there.</para>
    /// </summary>
    public bool Moves(int slot)
    {
        if (Carried.IsEmpty)
            return false;

        // The rows that would sit above the line if nothing moved.
        var above = 0;
        foreach (var span in Carried.Spans)
        {
            if (span.Last < slot)
                above += span.Count;
            else if (span.First < slot)
                above += slot - span.First;
        }

        // Nothing moves when the carried run already starts where the line is, once the carried rows
        // above the line are taken out of the count.
        var settled = Carried.Min - First == slot - First - above;
        return !(settled && IsContiguous);
    }

    /// <summary>Whether the carried rows are one unbroken run.</summary>
    public bool IsContiguous => Carried.Spans.Count <= 1;

    /// <summary>
    /// The block's rows in the order a drop at <paramref name="slot"/> would leave them, as indices.
    ///
    /// <para>The carried rows come out in the order they went in, which is what makes dragging a run
    /// of three tracks keep them in their own order rather than reversing them.</para>
    /// </summary>
    public IEnumerable<int> Settled(int slot)
    {
        var carried = new List<int>();
        var rest = new List<int>();

        for (var i = First; i <= Last; i++)
        {
            if (Carried.Contains(i))
                carried.Add(i);
            else
                rest.Add(i);
        }

        // How many uncarried rows sit above the line: the slot counts every row of the block, carried
        // ones included, so the carried ones above it have to come out of the count.
        var above = slot - First;
        foreach (var i in carried)
        {
            if (i < slot)
                above--;
        }

        above = Math.Clamp(above, 0, rest.Count);

        for (var i = 0; i < above; i++)
            yield return rest[i];

        foreach (var i in carried)
            yield return i;

        for (var i = above; i < rest.Count; i++)
            yield return rest[i];
    }

    /// <summary>
    /// The rows either side of the gap that are NOT being carried, or null at the block's ends.
    ///
    /// <para>The carried rows must be skipped: a row cannot be dropped after itself, and there is no
    /// gap between a row and itself for the store to place it in.</para>
    /// </summary>
    public (int? Above, int? Below) Neighbours(int slot)
    {
        int? above = null;
        for (var i = slot - 1; i >= First; i--)
        {
            if (!Carried.Contains(i))
            {
                above = i;
                break;
            }
        }

        int? below = null;
        for (var i = slot; i <= Last; i++)
        {
            if (!Carried.Contains(i))
            {
                below = i;
                break;
            }
        }

        return (above, below);
    }
}
