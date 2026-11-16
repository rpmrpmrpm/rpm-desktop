using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The badges a person can show under their name on their profile page: the id the server keeps, and the
/// string key of its name. The phones work out which badges a collection unlocks; the desktop only shows
/// them. Keep the ids and the colours the same as <c>ProfileBadges.kt</c> in rpm-android and
/// <c>ProfileBadges.swift</c> in rpm-apple.
/// </summary>
internal static class ProfileBadges
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["first-spin"] = "Rpm_Badge_FirstSpin",
        ["crate-digger"] = "Rpm_Badge_CrateDigger",
        ["record-collector"] = "Rpm_Badge_RecordCollector",
        ["music-connoisseur"] = "Rpm_Badge_Connoisseur",
        ["living-archive"] = "Rpm_Badge_LivingArchive",
        ["museum-curator"] = "Rpm_Badge_MuseumCurator",
        ["ten-thousand"] = "Rpm_Badge_TenThousand",
        ["rock"] = "Rpm_Badge_Rock",
        ["jazz"] = "Rpm_Badge_Jazz",
        ["classical"] = "Rpm_Badge_Classical",
        ["hiphop"] = "Rpm_Badge_HipHop",
        ["electronic"] = "Rpm_Badge_Electronic",
        ["metal"] = "Rpm_Badge_Metal",
        ["pop"] = "Rpm_Badge_Pop",
        ["soul"] = "Rpm_Badge_Soul",
        ["folk"] = "Rpm_Badge_Folk",
        ["blues"] = "Rpm_Badge_Blues",
        ["reggae"] = "Rpm_Badge_Reggae",
        ["latin"] = "Rpm_Badge_Latin",
        ["genre-hopper"] = "Rpm_Badge_GenreHopper",
        ["omnivore"] = "Rpm_Badge_Omnivore",
    };

    /// <summary>The badge colour when the person chose none: the first of the phones' list.</summary>
    private static readonly Color Default = Color.FromArgb(0xFF, 0x7A, 0x1F, 0xD1);

    /// <summary>The badge's name in the reader's language, or null for an id this build does not know.</summary>
    public static string? NameOf(string? id) =>
        id is { Length: > 0 } && Names.TryGetValue(id, out var key) ? Loc.Get(key) : null;

    /// <summary>What unlocks the badge, in the reader's language, or null for an id this build does not know.</summary>
    public static string? NeedOf(string? id) =>
        id is { Length: > 0 } && Names.TryGetValue(id, out var key) ? Loc.Get(key + "_Need") : null;

    /// <summary>The badge's colour from its wire value, <c>#RRGGBB</c>; the default when it has none.</summary>
    public static Color ColourOf(string? wire)
    {
        if (wire is { Length: 7 } && wire[0] == '#'
            && uint.TryParse(wire.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return Default;
    }
}
