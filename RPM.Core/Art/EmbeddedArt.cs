namespace RPM.Core.Art;

/// <summary>
/// How big the cover embedded in a track file should be.
///
/// <para>There are two tiers, because the two copies answer different questions. The album folder's
/// <c>cover.jpg</c> is written once and kept, so it gets the scan as the archive holds it: it is the
/// picture RPM itself draws from, and the one a user opens to look at a sleeve. The copy inside the
/// track files is duplicated per track: a 12-track album at a 2500 px scan carries the same picture
/// twelve times, and past a point that is library size spent on bytes nobody sees.</para>
///
/// <para>1000 px is a sensible middle. It is sharp at any size a player draws embedded art, it is
/// what the wider tooling uses as a default, and it costs a couple of hundred kilobytes a track
/// rather than a couple of megabytes. Nothing is upscaled: a sleeve that is already smaller is
/// embedded exactly as it came, because re-encoding it could only lose quality.</para>
/// </summary>
public static class EmbeddedArt
{
    /// <summary>The longest edge a cover is embedded at. Covers at or under this are embedded
    /// untouched.</summary>
    public const int MaxEdge = 1000;

    /// <summary>JPEG quality for a cover that had to be scaled. High enough that the resize is what
    /// shows and not the encoder.</summary>
    public const int JpegQuality = 88;

    /// <summary>Whether a cover of these dimensions is worth scaling before embedding. False for
    /// anything already within <see cref="MaxEdge"/>, and for a size that could not be read.</summary>
    public static bool NeedsScaling(int width, int height) =>
        width > 0 && height > 0 && Math.Max(width, height) > MaxEdge;

    /// <summary>The size to embed a cover of these dimensions at: the same shape, longest edge
    /// <see cref="MaxEdge"/>. Returns the dimensions unchanged when they already fit.</summary>
    /// <remarks>The short edge rounds to at least 1: a very wide scan is an unusual cover, but a
    /// zero-pixel one cannot be encoded at all.</remarks>
    public static (int Width, int Height) Fit(int width, int height)
    {
        if (!NeedsScaling(width, height))
            return (width, height);

        var scale = (double)MaxEdge / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }
}
