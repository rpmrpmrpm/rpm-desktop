using System.Numerics;

namespace RPM.Core.Viz;

/// <summary>
/// The four colours a room is painted in, worked out from what the sleeve gives.
///
/// <para>A cover gives a dominant tone, a vivid accent and sometimes a third colour. None of the three
/// can be used as it stands. Covers give muted, uneven tones, and a wash painted in them as they are is
/// either mud or invisible. What is wanted is the colour that people remember the sleeve as. That is the
/// tone at the saturation that memory gives it.</para>
///
/// <para>In <see cref="Vector3"/> rather than in either head's colour type, so the phone and the desktop
/// paint the same record the same colour. Both convert on the way in.</para>
/// </summary>
public readonly record struct RoomPalette(Vector3 Ground, Vector3 Tint, Vector3 Glow, Vector3 Shade)
{
    // The app's own blue, used when a sleeve has no colour to give.
    private static readonly Vector3 TintFallback = new(0.012f, 0.443f, 0.816f);
    private static readonly Vector3 GlowFallback = new(0.02f, 0.78f, 1.0f);
    private static readonly Vector3 ShadeFallback = new(0.62f, 0.16f, 0.72f);
    private static readonly Vector3 GroundFallback = new(0.012f, 0.030f, 0.070f);

    /// <summary>The shade for a sleeve with no third colour: a neutral grey. The second family stays apart
    /// from the first without using a colour that is not on the record.</summary>
    public static readonly Vector3 ShadeGrey = new(0.70f);

    /// <summary>The room with no record in it: the app's own blue, on its own near-black.</summary>
    public static readonly RoomPalette Fallback =
        new(GroundFallback, TintFallback, GlowFallback, ShadeFallback);

    /// <summary>
    /// The room in the app's own colours and nothing else: three blues on the near-black.
    ///
    /// <para><b>Why it is not <see cref="Fallback"/>.</b> That one is also "no record", but its third
    /// family is the purple <see cref="ShadeFallback"/>. That is a counter-colour. It is there so that a
    /// room lit for a one-colour sleeve still has two families you can tell apart. When there is no
    /// record at all, a purple third of the screen is a colour that belongs to nothing. The setup
    /// wizard needs all the colours to be shades of blue, the same as the deck's own background.</para>
    ///
    /// <para>The three are far enough apart in value to stay three families: a mid blue, the app's
    /// cyan over it, and a deep royal blue under both.</para>
    /// </summary>
    public static readonly RoomPalette Blues = new(
        GroundFallback,
        TintFallback,
        GlowFallback,
        new Vector3(0.106f, 0.247f, 0.627f));

    /// <summary>
    /// The room this cover paints. Any of the three may be null. A black-and-white photograph has no
    /// colour to give. Where the first two are missing, the room keeps the app's own blue.
    /// </summary>
    /// <param name="tint">The cover's dominant tone.</param>
    /// <param name="glow">Its vivid accent.</param>
    /// <param name="shade">The next colour on it that is neither of those.</param>
    public static RoomPalette From(Vector3? tint, Vector3? glow, Vector3? shade)
    {
        // The accent is the colour worth having. The dominant tone stands in for it. Each one stands in
        // for the other when only one came back.
        var t = tint ?? glow;
        var g = glow ?? t;

        if (t is not { } baseTone || g is not { } accent)
            return Fallback;

        // Lift the colours well clear of black, and push them off their own grey. A cover's dominant tone
        // is often a muddy mid-brown. A room painted in that exact tone is a brown room.
        var outTint = Vivid(baseTone, saturate: 1.35f, lift: 0.62f);
        var outGlow = Vivid(accent, saturate: 1.5f, lift: 0.90f);

        // The sleeve's third colour, if it is a real third colour and not one of the two already in hand.
        // It is used below for whichever slot needs it more.
        Vector3? spare = shade is { } third && !Same(third, baseTone) && !Same(third, accent) ? third : null;

        // Where the dominant tone and the accent are the same hue, the sleeve's third colour becomes the
        // accent, so the large areas of colour show two colours. With no third colour, the accent stays
        // as it is. The tint and the glow then differ only in brightness. For example, a gold cover with
        // red hair: its red becomes the glow.
        if (Same(outTint, outGlow) && spare is { } instead)
        {
            outGlow = Vivid(instead, saturate: 1.5f, lift: 0.90f);
            spare = null;   // used for the glow, so the shade does not repeat it
        }

        // The second and third families use the sleeve's third colour, or grey where it has none. No hue
        // is ever turned to make one up. Turning a hue puts colours on covers that do not have them. Most
        // covers are red, orange or brown, so a turned hue is usually a colour the record does not have.
        var outShade = spare is { } left ? Vivid(left, saturate: 1.35f, lift: 0.76f) : ShadeGrey;

        // The dark ground that the figures sit on is the same hue, taken almost to black. So the empty
        // parts of the screen still belong to the record, without competing with it.
        var outGround = Vivid(baseTone, saturate: 1.1f, lift: 0.16f) * 0.55f + GroundFallback * 0.45f;

        return new RoomPalette(outGround, outTint, outGlow, outShade);
    }

    /// <summary>
    /// This palette eased toward another by one frame's step, k. The caller chooses k so that a change
    /// of record takes the same time on a slow machine as on a fast one: about a second at the rate the
    /// two heads use. That is what makes a track change a dissolve rather than a cut.
    /// </summary>
    public RoomPalette EasedTo(RoomPalette target, float k) => new(
        Ground + (target.Ground - Ground) * k,
        Tint + (target.Tint - Tint) * k,
        Glow + (target.Glow - Glow) * k,
        Shade + (target.Shade - Shade) * k);

    /// <summary>A colour pushed off its own grey and set to a working brightness, hue intact.</summary>
    private static Vector3 Vivid(Vector3 c, float saturate, float lift)
    {
        float luma = c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
        var v = new Vector3(luma) + (c - new Vector3(luma)) * saturate;
        v = Vector3.Clamp(v, Vector3.Zero, Vector3.One);

        float peak = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        if (peak > 1e-4f)
            v *= lift / peak;
        return Vector3.Clamp(v, Vector3.Zero, Vector3.One);
    }

    /// <summary>Whether two colours count as the same colour. They are compared as DIRECTIONS, so a light
    /// one and a dark one of the same hue count as one colour. That is the question being asked. Two
    /// figures painted in those colours would read as a single family, however far apart their values
    /// are.</summary>
    private static bool Same(Vector3 a, Vector3 b)
        => Vector3.Distance(Normalise(a), Normalise(b)) < 0.22f;

    private static Vector3 Normalise(Vector3 c)
    {
        float len = c.Length();
        return len > 1e-4f ? c / len : Vector3.Zero;
    }
}
