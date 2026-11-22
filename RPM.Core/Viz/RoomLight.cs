using System.Numerics;

namespace RPM.Core.Viz;

/// <summary>
/// How strongly a room is lit: how far each of its colours is pushed off its own grey, and how bright
/// each family of figures is.
///
/// <para><b>There are two, because a room is shown in two kinds of place.</b> In Pegasus the room fills
/// the screen behind the player and is the picture being watched. There a muted sleeve must still light
/// the room, so its colours are pushed well off grey and lifted close to full brightness. In the library
/// the room is the back of a card, a generated list's cover or a playlist's header, between album covers
/// and the app's own palette. At Pegasus's strength it is the brightest thing on the screen, so the
/// library uses <see cref="Shelf"/>, which keeps the colours near the values the rest of the app shows.</para>
///
/// <para><see cref="RoomPalette.From"/> takes one with no default. The wrong one still compiles, and the
/// mistake is only seen on screen, so every caller states which one it means.</para>
/// </summary>
/// <param name="Ground">The dark field the figures sit on, made from the dominant tone. Its lift is the
/// brightness of the tone before it is mixed with the app's near-black.</param>
/// <param name="Tint">The dominant tone, which is most of what is seen.</param>
/// <param name="Glow">The accent, which the light in the room is made of.</param>
/// <param name="Shade">The third colour, for the second and third families of figures.</param>
/// <param name="Grey">The shade used when the sleeve has no third colour. It is grey so that no hue is
/// made up (see <see cref="RoomPalette.From"/>).</param>
/// <param name="Empty">The whole palette for a sleeve with no colour to give: the app's own blue at this
/// strength.</param>
public readonly record struct RoomLight(
    RoomLight.Family Ground, RoomLight.Family Tint, RoomLight.Family Glow, RoomLight.Family Shade, Vector3 Grey,
    RoomPalette Empty)
{
    /// <summary>How one colour is treated.</summary>
    /// <param name="Saturate">How far the colour is pushed off its own grey. One leaves it as read.</param>
    /// <param name="Lift">How bright its brightest channel is made, from zero to one. The hue is
    /// kept.</param>
    public readonly record struct Family(float Saturate, float Lift);

    /// <summary>The room in Pegasus, and in its radio tuner.</summary>
    public static RoomLight Stage { get; } = new(
        Ground: new Family(1.1f, 0.16f),
        Tint: new Family(1.35f, 0.62f),
        Glow: new Family(1.5f, 0.90f),
        Shade: new Family(1.35f, 0.76f),
        Grey: new Vector3(0.70f),
        Empty: RoomPalette.Fallback);

    /// <summary>
    /// The room in the library: on the home screen's cards, on a generated list's cover, on a playlist's
    /// header.
    /// </summary>
    /// <remarks>
    /// <para>The colours are taken nearly as read, with a small push off grey so that a muted cover does
    /// not give a brown room. The brightness is set near the drawer bands and the genre cards. The tint
    /// sits below the head of a band (six tenths of a palette colour), and the glow well below the palette
    /// colour itself. The shader adds light where figures overlap, so the glow is set lower than the
    /// brightest colour wanted on screen.</para>
    ///
    /// <para>The ground is twice as bright as in <see cref="Stage"/>. So the parts of the card between
    /// the figures are a dark version of the cover's colour, as a genre card's are, and not black. Bright
    /// figures on black are what makes a room look lit from inside.</para>
    /// </remarks>
    public static RoomLight Shelf { get; } = Lit(
        ground: new Family(1.0f, 0.32f),
        tint: new Family(1.05f, 0.46f),
        glow: new Family(1.1f, 0.64f),
        shade: new Family(1.05f, 0.52f),
        grey: new Vector3(0.42f));

    /// <summary>A light whose empty palette is the app's blue lit by the same rules as every other
    /// colour.</summary>
    private static RoomLight Lit(Family ground, Family tint, Family glow, Family shade, Vector3 grey)
        => new(ground, tint, glow, shade, grey, RoomPalette.Fallback.Relit(ground, tint, glow, shade));
}
