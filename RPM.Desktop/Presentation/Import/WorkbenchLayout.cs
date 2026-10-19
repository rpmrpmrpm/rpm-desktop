namespace RPM.Desktop.Presentation.Import;

/// <summary>How much room the workbench has, as three named states rather than a spread of pixels.</summary>
public enum WorkbenchTier
{
    /// <summary>A narrow pane. The search field is short, and the tag panel steps aside rather than
    /// squeezing the grid to nothing.</summary>
    Compact,

    /// <summary>The ordinary pane.</summary>
    Regular,

    /// <summary>The pane opened out: the search field at its full width.</summary>
    Wide,
}

/// <summary>
/// What fits, at a given width.
///
/// <para>Kept as arithmetic, away from the controls, for the same reason <see cref="DropTarget"/> is:
/// "what does the bench do when the pane is 700 wide" is a question with a right answer, and it should
/// be answerable without a window.</para>
/// </summary>
public static class WorkbenchLayout
{
    /// <summary>Under this, the bar has to give up room somewhere.</summary>
    public const double CompactBelow = 620;

    /// <summary>At and above this, everything on the bar fits with room to spare.</summary>
    public const double WideAtLeast = 960;

    /// <summary>The narrowest width at which the grid is still useful. Below it a row is only a column and
    /// a half wide, and the tag panel needs that room for the work.</summary>
    public const double MinGridWidth = 360;

    public static WorkbenchTier TierFor(double paneWidth) =>
        paneWidth < CompactBelow ? WorkbenchTier.Compact
        : paneWidth < WideAtLeast ? WorkbenchTier.Regular
        : WorkbenchTier.Wide;

    /// <summary>One key on the tool rail, as the layout sees it: how wide it is with its label folded
    /// away, and whether a group hairline stands in front of it.</summary>
    public readonly record struct RailKey(double Width, bool StartsGroup);

    /// <summary>
    /// How many of the rail's keys fit, in the order they are given. The rest go in the ⋯ menu.
    ///
    /// <para>Two things are set aside before anything is placed. The menu has to fit whenever a key did
    /// not, or the overflow would have nowhere to go. One label's width is also reserved. A key shows its
    /// name by sliding its label out beside it. If the rail filled the pane to the pixel, the last key
    /// would be pushed out of the pane as soon as the pointer moved over one. Reserving the widest label
    /// keeps every key readable where it stands.</para>
    /// </summary>
    /// <param name="keys">The keys, most worth showing first.</param>
    /// <param name="available">The room the rail has.</param>
    /// <param name="spacing">Between two neighbours.</param>
    /// <param name="divider">A group hairline, its margins included.</param>
    /// <param name="reserve">The widest label that could slide out.</param>
    /// <param name="menu">The ⋯ key.</param>
    public static int RailFits(
        IReadOnlyList<RailKey> keys, double available,
        double spacing, double divider, double reserve, double menu)
    {
        var used = 0.0;
        var fit = 0;

        for (var i = 0; i < keys.Count; i++)
        {
            var cost = keys[i].Width
                + (i > 0 ? spacing : 0)
                + (keys[i].StartsGroup && i > 0 ? divider + spacing : 0);

            // Every key after this one still needs room, so the menu counts towards the width until the
            // last key is placed.
            var overflow = i < keys.Count - 1 ? menu + spacing : 0;
            if (used + cost + overflow + reserve > available)
                break;

            used += cost;
            fit = i + 1;
        }

        return fit;
    }

    /// <summary>
    /// Whether a tag panel of <paramref name="panelWidth"/> can be open in a pane of
    /// <paramref name="paneWidth"/> without leaving the grid unreadable.
    ///
    /// <para>The panel is not closed when this is false. It is stepped aside and comes back when there is
    /// room again. The user opened it, so a resize of the window should not be read as the user changing
    /// their mind.</para>
    /// </summary>
    public static bool FitsTagPanel(double paneWidth, double panelWidth) =>
        panelWidth > 0 && paneWidth - panelWidth >= MinGridWidth;
}
