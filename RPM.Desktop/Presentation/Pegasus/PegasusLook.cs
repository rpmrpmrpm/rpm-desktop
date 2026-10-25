namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// How bright and how saturated Pegasus is drawn, chosen in the settings' Display section (Picture). The Android
/// TV app's PictureLook, with the same three steps and the same figures. It was made for HDR televisions that show
/// ordinary pictures at their own full brightness (Chivan, 2026-10-17, on the Shield); on a desktop it is for any
/// screen where the room and the covers are brighter than the room the screen is in.
///
/// <para><see cref="PictureLooks.Light"/> multiplies every colour, so white is that share of full;
/// <see cref="PictureLooks.Saturation"/> pulls colours toward their grey. Both apply to everything the view draws:
/// the room, the record, the collection and its background, the keys. The opening's cover is drawn as it is, as on
/// the TV (see PegasusPlayer.Picture).</para>
///
/// <para>Stored by name (SettingsService.PegasusPicture), so the order here may change.</para>
/// </summary>
public enum PictureLook
{
    /// <summary>Colours as designed.</summary>
    Standard,

    /// <summary>Everything 15% dimmer.</summary>
    Softer,

    /// <summary>Everything 22% dimmer, and a fifth less saturated.</summary>
    Muted,
}

/// <summary>
/// What the collection's screens are drawn on, chosen in the settings' Display section (Background). The Android
/// TV app's PageBackground: the moving room can be kept for the player alone, with the pages on a still picture.
/// The player always has the moving room.
///
/// <para><see cref="Default"/> and <see cref="Pegasus"/> are the window's backdrops of the same names
/// (<see cref="NowPlaying.Backdrop.RPaMp"/> and <see cref="NowPlaying.Backdrop.Pegasus"/>, the very files the TV
/// copied). <see cref="Dark"/> is a flat anthracite, #28282D. <see cref="Room"/> is the moving room, which is what
/// the pages were drawn on before there was a choice.</para>
///
/// <para>Stored by name (SettingsService.PegasusPageBackground), so the order here may change. The default is
/// <see cref="Default"/>, as on the TV (Chivan, 2026-10-17: the moving room belongs to the player).</para>
/// </summary>
public enum PageBackground
{
    /// <summary>RPaMp's ground, the window's own default backdrop.</summary>
    Default,

    /// <summary>The Pegasus room shot against a red record, the window's Pegasus backdrop.</summary>
    Pegasus,

    /// <summary>Flat anthracite, #28282D, drawn as it is.</summary>
    Dark,

    /// <summary>The moving room, as behind the player.</summary>
    Room,
}

/// <summary>Reading, stepping and measuring the stored <see cref="PictureLook"/> and
/// <see cref="PageBackground"/>.</summary>
public static class PictureLooks
{
    /// <summary>What every colour is multiplied by: 1, 0.85 or 0.78, as on the TV.</summary>
    public static double Light(this PictureLook look) => look switch
    {
        PictureLook.Softer => 0.85,
        PictureLook.Muted => 0.78,
        _ => 1,
    };

    /// <summary>How saturated the colours are kept: 1, 1 or 0.8, as on the TV.</summary>
    public static double Saturation(this PictureLook look) => look switch
    {
        PictureLook.Muted => 0.8,
        _ => 1,
    };

    /// <summary>The look stored under <paramref name="name"/>. A missing name, or one this build does not know, is
    /// <see cref="PictureLook.Standard"/>.</summary>
    public static PictureLook NamedPicture(string? name) =>
        Enum.TryParse<PictureLook>(name, ignoreCase: false, out var look) && Enum.IsDefined(look)
            ? look
            : PictureLook.Standard;

    /// <summary>The background stored under <paramref name="name"/>. A missing name, or one this build does not
    /// know, is <see cref="PageBackground.Default"/>.</summary>
    public static PageBackground NamedBackground(string? name) =>
        Enum.TryParse<PageBackground>(name, ignoreCase: false, out var ground) && Enum.IsDefined(ground)
            ? ground
            : PageBackground.Default;

    /// <summary>The next value, wrapping, in the order the settings row steps through them.</summary>
    public static T Next<T>(T value) where T : struct, Enum
    {
        var all = Enum.GetValues<T>();
        return all[(Array.IndexOf(all, value) + 1) % all.Length];
    }
}
