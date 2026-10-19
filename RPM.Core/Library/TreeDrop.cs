namespace RPM.Core.Library;

/// <summary>Where a row being dragged over another one would land.</summary>
public enum TreeDropWhere
{
    /// <summary>On the line above it, as the row before it under the same parent.</summary>
    Before,

    /// <summary>On the line below it, as the row after it under the same parent.</summary>
    After,

    /// <summary>Inside it. Only ever offered by a row that can hold others.</summary>
    Into,
}

/// <summary>
/// Where a drag over a tree is asking to put the row it carries.
///
/// <para>
/// This is pure logic, kept apart from the panel. The bands decide how the drag feels, and their
/// effect is hard to see from the code alone. If the whole height of a folder meant "inside", no
/// drop between two rows would be possible. A playlist could then never be placed above the first
/// folder in a list of folders. Bands that are too narrow make the line flicker between answers
/// while the pointer is still. A row that cannot hold children must never answer
/// <see cref="TreeDropWhere.Into"/>, whatever the pointer does. Otherwise the drop lands somewhere
/// the tree cannot represent.
/// </para>
/// </summary>
public static class TreeDrop
{
    /// <summary>The share of a folder's height at the top and at the bottom that drops beside it
    /// rather than inside it. A quarter each. That is enough to hit without aiming, and it leaves
    /// the middle half of the row for dropping inside.</summary>
    public const double Edge = 0.25;

    /// <summary>
    /// What a pointer at <paramref name="withinRow"/> down a row of <paramref name="rowHeight"/> is
    /// asking for.
    /// </summary>
    /// <param name="canTakeChildren">Whether this row can hold others. A folder can, a playlist
    /// cannot. A row that cannot is split in half and answers only Before or After.</param>
    public static TreeDropWhere Where(double withinRow, double rowHeight, bool canTakeChildren)
    {
        if (rowHeight <= 0)
            return TreeDropWhere.Before;

        var down = Math.Clamp(withinRow / rowHeight, 0, 1);

        if (!canTakeChildren)
            return down < 0.5 ? TreeDropWhere.Before : TreeDropWhere.After;

        if (down < Edge) return TreeDropWhere.Before;
        if (down > 1 - Edge) return TreeDropWhere.After;

        return TreeDropWhere.Into;
    }

    /// <summary>
    /// Where a row lands in its new parent's list of children, given the row it was dropped beside
    /// and which side of that row.
    ///
    /// <para>The index is counted with the carried row already removed from the list. This makes a
    /// move down within the same list land where the drop line was drawn. When the carried row leaves
    /// a slot above the target, the target moves up one place. An index worked out before the removal
    /// would be one place too far.</para>
    /// </summary>
    /// <param name="beside">Where the target sits among its siblings, with the carried row still in
    /// place.</param>
    /// <param name="carried">Where the carried row sits among those same siblings, or -1 when it comes
    /// from another parent.</param>
    public static int Slot(int beside, TreeDropWhere where, int carried)
    {
        var at = where == TreeDropWhere.After ? beside + 1 : beside;

        return carried >= 0 && carried < at ? at - 1 : at;
    }
}
