namespace RPM.Core.Cloud;

/// <summary>
/// The slow fade in and out of a cloud badge while it is working. It is one shared curve, so an album
/// tile and the playlist row above it always pulse in step.
///
/// <para>
/// This is a curve and not a storyboard on purpose. Under Uno, a looping <c>DoubleAnimation</c> does
/// not reliably stop while its element stays in the tree, and stopping one snaps the property back
/// to its base value for one frame. On Opacity with no base set, that is a flash to fully opaque.
/// Both views read this curve from one shared timer (the desktop's <c>BadgeBreath</c>). They join it
/// on start and leave it on stop, which is an unconditional halt, and they reset the opacity
/// themselves. There is one timer and not one for each view: the elapsed time passed in is the same
/// for both, which keeps a tile and the row above it in step, and every write repaints the whole
/// window, so a second clock would cost a second repaint per beat for the same pulse.
/// </para>
/// </summary>
public static class CloudBadgePulse
{
    /// <summary>Slow enough to read as a gentle fade and not as blinking.</summary>
    private const double PeriodSeconds = 1.4;

    /// <summary>Never fully transparent: the badge shows the album's state as well as the fact that it is
    /// busy, and a glyph that vanishes on every cycle is harder to read than one that only dims.</summary>
    private const double Floor = 0.34;

    /// <summary>The badge's opacity at <paramref name="elapsed"/> into the work.</summary>
    public static double At(TimeSpan elapsed)
    {
        // Cosine and not a triangle wave: it eases at both ends, so the badge settles at full and at dim
        // opacity and does not turn around at a sharp corner.
        var phase = (0.5 - 0.5 * Math.Cos(elapsed.TotalSeconds * 2 * Math.PI / PeriodSeconds));
        return Floor + (1 - Floor) * phase;
    }

    /// <summary>What the opacity goes back to when the work is done.</summary>
    public const double Rest = 1.0;
}
