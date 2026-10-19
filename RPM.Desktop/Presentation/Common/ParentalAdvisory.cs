using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The parental advisory lockup, as one picture at the size it is stamped.
///
/// <para><b>Where it came from.</b> The mark as it was supplied to this app. It is a 60x38 bitmap of the
/// official label: the black plate, the white band cut out of it, ADVISORY, and the two smaller lines. It is
/// opaque to its own edges, with no transparency at all. It is pasted below unchanged, byte for byte, and it
/// is not re-cut from anything.</para>
///
/// <para><b>The height is the whole of the instruction.</b> The width follows from the picture's own shape,
/// not from a second number. The width and height below are that shape scaled up to a height of 72. The mark
/// is then taller than the bitmap holds, so it IS resampled when it is put on a cover. The resampling uses
/// Mitchell's cubic, the same soft bicubic that the rest of this app uses to scale pictures (see
/// <c>ArtScaler.SamplingFor</c>). Scaling up adds no detail. EXPLICIT CONTENT is four pixels tall in the
/// bitmap. It comes out of the stamp as soft as it went in, only larger. Anything crisper must arrive as a
/// bigger picture. It cannot be recovered here.</para>
///
/// <para>Nothing here is coloured by the playlist. The mark is black and white wherever it appears. In the
/// colours of whatever it sits on, it would look like decoration, not a notice.</para>
///
/// <para>To replace it: encode the new picture as a PNG, paste the base64 here, and set <see cref="Width"/>
/// from its own shape at the <see cref="Height"/> that is then given.</para>
/// </summary>
internal static class ParentalAdvisory
{
    /// <summary>The size the mark is stamped at, on a cover composed at <see cref="PlaylistArt.Edge"/>. The
    /// height is 72. The width is the picture's own 60x38 shape carried up to that height. So the label keeps
    /// its shape and is not fitted to a box.</summary>
    internal const int Width = 114;
    internal const int Height = 72;

    private static SKImage? _mark;

    /// <summary>
    /// The mark, decoded once and held for the life of the app.
    ///
    /// <para>It is held because a playlist grid draws eight covers at a time, and each one would otherwise
    /// decode the same few kilobytes again. The value is null only where Skia refuses the bytes. The caller
    /// then draws no mark at all, rather than half of one.</para>
    /// </summary>
    internal static SKImage? Mark
    {
        get
        {
            if (_mark is not null)
                return _mark;

            try
            {
                return _mark = SKImage.FromEncodedData(Convert.FromBase64String(Png));
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The picture itself: a 60x38 PNG, black and white and opaque to its own edges.</summary>
    private const string Png =
        """
        iVBORw0KGgoAAAANSUhEUgAAADwAAAAmCAYAAACYsfiPAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABmZVhJ
        ZklJKgAIAAAAAQBphwQAAQAAABoAAAAAAAAAAwAAkAcABAAAADAyMzABoAMAAQAAAAEAAAAFoAQAAQAAAEQAAAAAAAAAAgABAAIABAAAAFI5OAACAAcABAAA
        ADAxMDAAAAAAIvvHMbnUA7gAAA1ASURBVGhD5dpzkGXXFgfgDie2XUmqwopt25nYtm3btpOJbduZiW3b5nr1rbx13+nbtydT79/cql3n9Nbyb62zd3d1dXXF
        v6z9/TLYYIPF8MMPH+OPP35MMMEEMfLII8fggw/emmh8pJFGinHGGSdGGGGE/LvG+vTpE+OOO25MOOGEMdpoo8UQQwzRGhtyyCFjzDHHjIknnjjGG2+8GGaY
        YVpj3scaa6wcty+atS/ao48+ekw00US5dowxxui271BDDZXrNDSq37pRRhklxh577Bh22GFb/Y329wthF1988dhrr73iwAMPjPXWWy8mn3zyFgMIrrTSSrHj
        jjvGUkstlcKXQLPPPnvstNNOcfDBB8cWW2wR008/fTJn7ZRTThmbb755HHLIIbHzzjvHnHPOmWuGHnromHfeeWPrrbfONdtvv32svPLKqQD7Ut4666yTex56
        6KGx0UYbxWSTTZZ7EgoN6zbbbLOYdtppW8ahuFVXXTW23XbbWGCBBdIYHQWm/T322COefPLJePXVV+P666+PVVZZJa2AyHTTTRcnnnhiPPTQQ8kAZZSiNthg
        g7jnnnvi9ddfzydGWIfQSyyxRFx99dXxwgsv5J5rrLFGDDfccDHqqKPGlltuGXfccUc264499thkHr0ZZ5wxjj/++Hjuuedy7RlnnJHKIhjLrbXWWnHLLbfE
        TTfdFKuttloKZh1F4PP222+P7bbbLg3VUWCuQ5DXXnstPv7443j66adzAfdAhLauueaaePvtt+Oiiy6K2WabreXmrGT+559/nspiFRZiyRVXXDHuvvvueOWV
        V+LMM8+MhRZaKN0RI7vttlu8+OKL8c4776Syzj333Jhppply30kmmSR23XXX6N+/fwq9//77xxRTTJFjwoan6X/mmWfSOyjQ2Kyzzpr8Wbf33nunaw9U4Dfe
        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
        aQAAAABJRU5ErkJggg==
        """;
}
