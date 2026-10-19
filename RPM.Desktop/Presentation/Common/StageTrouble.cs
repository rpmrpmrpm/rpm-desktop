namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The text shown on the black background behind a full-screen view when its GL stage could not start,
/// or stopped drawing. Used by <c>PegasusPlayer</c> and <c>VizFullScreen</c>.
///
/// <para>The GL error detail (a missing assembly, a shader log, or the ANGLE error) is for developers
/// working on the visuals, not for listeners. A Release build shows one plain sentence. A Debug build
/// shows the detail under it. The caller always writes the detail to the diagnostic log.</para>
/// </summary>
internal static class StageTrouble
{
    /// <summary>The text for the status line, or null when there is nothing wrong.</summary>
    /// <param name="detail">The GL complaint, or null.</param>
    public static string? Line(string? detail)
    {
        if (detail is null)
            return null;

#if DEBUG
        return Loc.Get("Viz_CouldNotStart") + "\n" + detail;
#else
        return Loc.Get("Viz_CouldNotStart");
#endif
    }
}
