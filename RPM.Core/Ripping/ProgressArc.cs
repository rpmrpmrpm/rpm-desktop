namespace RPM.Core.Ripping;

/// <summary>
/// The ring the deck's rip chip draws around its disc: a fraction of a circle, as plain points.
///
/// Pure maths, on purpose. Under Uno-Skia a code-built arc geometry renders nothing, so the ring is a
/// Polyline whose points are worked out by hand. That makes the one thing that must be right (where the
/// arc starts, and which way it goes) something that can be checked without a window.
/// </summary>
public static class ProgressArc
{
    /// <summary>
    /// Points along <paramref name="fraction"/> of a circle, clockwise from twelve o'clock, in screen
    /// coordinates (y grows downward).
    /// </summary>
    /// <param name="segments">How many straight pieces a whole circle is drawn in. A partial arc gets
    /// its share of them, so a short arc is not drawn coarser than a long one.</param>
    public static IReadOnlyList<(double X, double Y)> Points(
        double fraction, double centreX, double centreY, double radius, int segments = 48)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int steps = Math.Max(1, (int)Math.Ceiling(segments * fraction));

        var points = new List<(double X, double Y)>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double angle = 2 * Math.PI * fraction * i / steps;
            points.Add((centreX + radius * Math.Sin(angle), centreY - radius * Math.Cos(angle)));
        }
        return points;
    }
}
