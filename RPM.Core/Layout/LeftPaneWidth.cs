namespace RPM.Core.Layout;

/// <summary>
/// The left pane's width arithmetic, away from the columns it is eventually written to.
///
/// <para>Most of the pane's views take a share of the row under a ceiling. A pane that is only an index
/// INTO the library has no business being half the window. Import is the exception. It is the view that
/// is worked IN rather than read from, so it has no ceiling and a width of its own. The width is
/// dragged to whatever the job needs, and it is remembered across runs.</para>
/// </summary>
public static class LeftPaneWidth
{
    /// <summary>
    /// How wide Import stands, in px: the width it was last dragged to, or <paramref name="fallback"/>
    /// when it never has been. The answer is held inside what the row can actually give, with the
    /// library keeping <paramref name="minimum"/> for itself.
    ///
    /// <para>The clamp applies to the answer, never to what is remembered. A width dragged on a wide
    /// window is given back in full when the window is wide again. It was only borrowed from while the
    /// window was not wide. A row too narrow for both panels gives the pane the minimum and the library
    /// the remainder. The alternative is a negative width, and something has to be on screen.</para>
    /// </summary>
    /// <param name="remembered">The dragged width, or zero/negative when the user never dragged it.</param>
    /// <param name="fallback">The width to open at when nothing is remembered.</param>
    /// <param name="room">The row the two panels share, minus the splitter. Zero before it is laid out,
    /// where there is nothing to clamp against and the wanted width stands.</param>
    /// <param name="minimum">The narrowest either panel is allowed to be.</param>
    public static double ForImport(double remembered, double fallback, double room, double minimum)
    {
        var want = remembered > 0 ? remembered : fallback;
        if (room <= 0)
            return want;
        return Math.Clamp(want, minimum, Math.Max(minimum, room - minimum));
    }
}
