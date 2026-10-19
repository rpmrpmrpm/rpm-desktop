using RPM.Core.Import;

namespace RPM.Desktop.Strings;

/// <summary>
/// The built-in file naming templates' names in the user's language, for the setup wizard and
/// Settings, Library.
///
/// <para>The name in <see cref="NamingTemplate"/> is also the identity the settings file stores for
/// the chosen template, so it stays English and does not change with the language. Only what the
/// screen shows is translated. A template the user made has the name they typed, and is shown as
/// typed.</para>
/// </summary>
internal static class NamingText
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        [NamingTemplate.ArtistAlbumNumberedTitle.Name] = "Naming_ArtistAlbumNumberedTitle",
        [NamingTemplate.ArtistAlbumArtistTitle.Name] = "Naming_ArtistAlbumArtistTitle",
        [NamingTemplate.ArtistNumberedTitle.Name] = "Naming_ArtistNumberedTitle",
        [NamingTemplate.FlatArtistTitle.Name] = "Naming_FlatArtistTitle",
        [NamingTemplate.ArtistAlbumWorkMovement.Name] = "Naming_ArtistAlbumWorkMovement",
    };

    /// <summary>The string key of a built-in template's name, or null for one the user made.</summary>
    public static string? Key(string name) => Keys.GetValueOrDefault(name);

    /// <summary>What the screen shows for a template called <paramref name="name"/>.</summary>
    public static string Label(string name) => Key(name) is { } key ? Loc.Get(key) : name;
}
