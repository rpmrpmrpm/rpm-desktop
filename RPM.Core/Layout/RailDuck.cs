namespace RPM.Core.Layout;

/// <summary>
/// How far the library rail's tabs step down to get out from under the spinning record.
///
/// <para>The rail stands on the right edge of the window, and so does the disc, which hangs half off it.
/// So there is no sideways for the tabs to move into, as the library's header chrome can. The only way
/// clear is below the arc, and that is what this measures.</para>
/// </summary>
public static class RailDuck
{
    /// <summary>
    /// The drop, in px, from the rail's own top.
    /// </summary>
    /// <param name="discBottom">The row where the disc, its halo and its wobble stop reaching, in the same
    /// coordinates as <paramref name="railTop"/>. Zero when nothing is playing and the disc is off the
    /// deck. That is the resting case: no drop at all.</param>
    /// <param name="railTop">The rail's top edge.</param>
    /// <param name="gap">Clear air kept between the arc and the first tab.</param>
    /// <param name="tabsHeight">How tall the stack of tabs is.</param>
    /// <param name="railHeight">How tall the rail is. The tabs never leave it. A rail short enough that
    /// the disc covers the whole of it gives what room it has and keeps the tabs on screen. A tab pushed
    /// out of the pane is worse than a tab under the record, so the record is the thing that moves.</param>
    public static double Drop(double discBottom, double railTop, double gap,
                              double tabsHeight, double railHeight)
    {
        var wanted = discBottom - railTop + gap;
        if (wanted <= 0)
            return 0;
        var room = Math.Max(0, railHeight - tabsHeight);
        return Math.Min(wanted, room);
    }
}
