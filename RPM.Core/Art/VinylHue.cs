namespace RPM.Core.Art;

/// <summary>A plain colour, so the rule below does not depend on a UI type.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>0 for a grey, 1 for a fully saturated hue. This is HSV's S: the gap between the
    /// largest and smallest channel, relative to the largest.</summary>
    public double Saturation
    {
        get
        {
            double max = Math.Max(R, Math.Max(G, B));
            double min = Math.Min(R, Math.Min(G, B));
            return max <= 0 ? 0 : (max - min) / max;
        }
    }

    /// <summary>0..1, the brightest channel. HSV's V.</summary>
    public double Brightness => Math.Max(R, Math.Max(G, B)) / 255.0;
}

/// <summary>
/// Chooses the colour of the vinyl disc from the colours of its sleeve.
///
/// <para>The disc takes its colour from the album art, so the disc on the deck matches the record
/// that is playing and not a fixed set of colours. A sleeve with no colour to give (a black-and-white
/// photograph, a plain white typographic cover, a scan so dark that nothing is left) gives no colour,
/// and the disc is black.</para>
///
/// <para>The rule is kept apart from the pixel code because it must be right: "is there a colour in
/// this sleeve at all" is a judgement, and a wrong answer would give every mono sleeve a muddy brown
/// disc.</para>
/// </summary>
public static class VinylHue
{
    /// <summary>
    /// Below this a colour is a grey with a little noise, not a hue.
    ///
    /// <para>It must be above JPEG noise and the faint warm or cool cast in a scan of a black-and-white
    /// sleeve. It must be low enough to keep genuinely muted covers: a faded sleeve is still coloured,
    /// and a black disc for it would treat "pale" as "no colour".</para>
    /// </summary>
    public const double MinSaturation = 0.18;

    /// <summary>A colour taken from near-black pixels is noise, not a real hue. The tint code
    /// normalises brightness, so such a colour would come back at full strength and a very dark sleeve
    /// would give a vivid disc.</summary>
    public const double MinBrightness = 0.16;

    /// <summary>
    /// The colour for the disc, or null for black.
    ///
    /// <para>The vivid highlight is preferred over the dominant tone on purpose: the dominant colour
    /// of a sleeve is often its background, and a disc in the colour of the paper is not what people
    /// mean by "the colour of that album". The dominant colour is the fallback for a sleeve that has
    /// no usable highlight.</para>
    /// </summary>
    public static Rgb? Choose(Rgb? highlight, Rgb? dominant)
    {
        if (Usable(highlight))
            return highlight;
        if (Usable(dominant))
            return dominant;
        return null;
    }

    /// <summary>Whether a candidate is a real hue and not a grey or a very dark colour.</summary>
    public static bool Usable(Rgb? colour) =>
        colour is { } c && c.Saturation >= MinSaturation && c.Brightness >= MinBrightness;
}
