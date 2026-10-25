namespace RPM.Core.Viz;

/// <summary>
/// The animations the room behind a full-screen player can draw. The listener picks one in the
/// player's settings ("Room animation").
///
/// <para><see cref="Original"/> is the room every rpm app draws (<see cref="RoomSource"/>). The other
/// three are drawn in the same single pass as the original, each with a program of its own
/// (<see cref="RoomStyleSource"/>), so they cost about what the original costs. Where their figures are
/// in each frame is worked out on the CPU by <see cref="RoomFigures"/>.</para>
///
/// <para>The Android TV app has the same four, with the same names and the same figures. The listener's
/// choice is stored by name, so the order here may change.</para>
/// </summary>
public enum RoomStyle
{
    /// <summary>The three families of lobes over the plane warped by the music.</summary>
    Original,

    /// <summary>Two big balls on the bass and two small ones on the air, turning about the middle with
    /// fading tails behind them, over one big blurred ball in the third colour.</summary>
    Yang,

    /// <summary>Blobs rising and sinking like a lava lamp's wax, melting into a pool at the floor and one
    /// at the top, over one huge blurred ball in the third colour that swells with the middle.</summary>
    Lava,

    /// <summary>The RPaMp skin's background picture, set moving: broad soft ribbons of light flowing
    /// across a dark ground, in the sleeve's colours.</summary>
    Alchemy,
}

/// <summary>Reading and stepping the stored <see cref="RoomStyle"/>.</summary>
public static class RoomStyles
{
    /// <summary>The style stored under <paramref name="name"/>. A missing name, or one this build does
    /// not know, is the original.</summary>
    public static RoomStyle Named(string? name)
    {
        foreach (var style in Enum.GetValues<RoomStyle>())
            if (string.Equals(style.ToString(), name, StringComparison.Ordinal))
                return style;
        return RoomStyle.Original;
    }

    /// <summary>The style after <paramref name="style"/>, wrapping, in the order the settings row steps
    /// through them: Original, Yang, Lava, Alchemy.</summary>
    public static RoomStyle Next(RoomStyle style) => style switch
    {
        RoomStyle.Original => RoomStyle.Yang,
        RoomStyle.Yang => RoomStyle.Lava,
        RoomStyle.Lava => RoomStyle.Alchemy,
        _ => RoomStyle.Original,
    };
}
