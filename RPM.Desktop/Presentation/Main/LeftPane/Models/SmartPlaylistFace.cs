using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// The name and the icon of a generated playlist. The rule that decides which songs go in a list lives in
/// Core (<see cref="SmartPlaylistKind"/>), and so does the name (<see cref="ListNames"/>), because the store
/// names the lists it builds for an account with no desktop. The glyph, the colour and the cover belong to this
/// interface, so they are here. A new kind needs one arm in the name's switch and one in the glyph's.
///
/// <para>Neither switch has a fallback arm. A kind added to the enum needs a name and a glyph chosen on
/// purpose. A default arm would quietly give it the favourites' glyph.</para>
/// </summary>
internal static class SmartPlaylistFace
{
    /// <summary>What one generated list is called, in this app's language. See
    /// <see cref="ListNames.Name(SmartPlaylistId, Func{string, string})"/>, which the store also names its lists
    /// with.</summary>
    public static string Name(SmartPlaylistId id) => ListNames.Name(id, Text);

    /// <summary>The same name, given the kind as a string, as a cut from another device names it.</summary>
    public static string Name(string kind, string? genre, int decade) => ListNames.Name(kind, genre, decade, Text);

    /// <summary>Where a generated list sits in this app's tree, outermost first. See
    /// <see cref="ListNames.FolderChain"/>.</summary>
    public static IReadOnlyList<string> FolderChain(string kind, string? genre, int decade) =>
        ListNames.FolderChain(kind, genre, decade, Text);

    /// <summary>This app's string table, as <see cref="ListNames"/> asks for it.</summary>
    internal static string Text(string key) => Loc.Get(key);




    /// <summary>The row's glyph. Each list shows a glyph that matches what it is made of. The favourites
    /// use the same heart that the songs themselves use, so the list and the thing it counts look like
    /// one.</summary>
    public static string Glyph(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.FavoriteTracks => "\uf004",      // heart
        SmartPlaylistKind.MostPlayed => "\uf06d",          // fire
        SmartPlaylistKind.RecentlyAdded => "\uf067",       // plus
        SmartPlaylistKind.RecentlyPlayed => "\uf1da",      // clock-rotate-left
        SmartPlaylistKind.NeverPlayed => "\uf2f2",         // hourglass
        SmartPlaylistKind.LovedAndForgotten => "\uf7a9",   // heart-crack
        SmartPlaylistKind.PlayedNotLoved => "\uf004",      // heart, for songs played but not loved
        SmartPlaylistKind.OnRepeat => "\uf363",            // repeat
        SmartPlaylistKind.Rediscover => "\uf002",          // magnifying-glass
        SmartPlaylistKind.LateNight => "\uf186",           // moon
        SmartPlaylistKind.FirstListens => "\uf0eb",        // lightbulb
        SmartPlaylistKind.KeepSkipping => "\uf051",        // forward-step

        // The five lists made from the audio show what the music sounds like, not what it is made of.
        // There is no glyph for a percentile, and a listener who reads this row is choosing a mood.
        SmartPlaylistKind.Calm => "\uf52d",               // feather
        SmartPlaylistKind.Energetic => "\uf0e7",          // bolt
        SmartPlaylistKind.Danceable => "\uf70c",          // person-running
        SmartPlaylistKind.Focus => "",              // bullseye
        SmartPlaylistKind.Sleep => "\uf236",              // bed

        // The six the sound model hears. Each glyph is the scene its list is for, as the five above are.
        SmartPlaylistKind.Melancholy => "\uf73d",         // cloud-rain
        SmartPlaylistKind.Happy => "\uf185",              // sun
        SmartPlaylistKind.Angry => "\uf7e4",              // fire-flame-curved
        SmartPlaylistKind.Romantic => "\uf5ce",           // wine-glass
        SmartPlaylistKind.Zen => "\uf5bb",                // spa (a lotus)
        SmartPlaylistKind.NewYearsEve => "\uf79f",        // champagne-glasses

        // Merry Christmas: a Christmas tree.
        SmartPlaylistKind.Christmas => "\uf1bb",          // tree

        // A clock. This list is the only one that depends on the time of day.
        SmartPlaylistKind.RightNow => "\uf017",           // clock

        // A chart, because a decade list holds the songs of one period.
        SmartPlaylistKind.Decade => "\uf201",            // chart-line
        SmartPlaylistKind.Classical => "\uf001",         // music
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This generated playlist has no mark yet."),
    };

    /// <summary>
    /// The colour of the mark.
    ///
    /// <para>Only the two marks that are a colour have one: the favourites' red heart (the same mark the
    /// songs themselves wear) and the most-played fire. Everything else is an ordinary mark and looks
    /// like one. A row of eight coloured glyphs would show no colour as meaningful.</para>
    ///
    /// <para>The colour is defined here, not in the tree row, because a second surface draws these marks
    /// now (the home screen). The same list must not have two colours on two screens. This switch has a
    /// fallback arm, unlike the two switches above. A new kind needs a name and a glyph chosen on
    /// purpose. An ordinary colour is a real answer, so the fallback is allowed.</para>
    /// </summary>
    public static Windows.UI.Color Ink(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.FavoriteTracks => Windows.UI.Color.FromArgb(0xFF, 0xEE, 0x07, 0x09),
        SmartPlaylistKind.MostPlayed => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8A, 0x1E),

        // The third coloured mark, and the last one this row can carry. This is the list the pane offers
        // first. An uncoloured clock beside two coloured marks would look unfinished. The colour is teal,
        // not another warm tone, so the three are told apart at a glance.
        SmartPlaylistKind.RightNow => Windows.UI.Color.FromArgb(0xFF, 0x36, 0xC8, 0xC0),

        _ => Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
    };

    /// <summary>
    /// The picture a generated list wears: which of the eight families, and the seed that arranges it
    /// and picks its colours.
    ///
    /// <para><b>Chosen by meaning, not by a hash of the name.</b> Colour carries well-known associations.
    /// Reds and oranges read as urgent and arousing. Blues read as calm and slowing. Deep blue-violets
    /// read as night and rest. Greens read as fresh and new. A shelf of mood lists is a good place to use
    /// these associations. They come from general colour psychology.</para>
    ///
    /// <para><b>How the hue is steered.</b> <c>PlaylistFigure.Colourway</c> anchors on <c>seed %
    /// palette.Count</c> and takes its neighbours from the ring. A seed built as <c>variation *
    /// palette.Count + hue</c> lands on the wanted colour, and every other bit is free to arrange the
    /// picture. The drawing code does not need to know about moods.</para>
    ///
    /// <para><b>A genre row is a variation of its parent, not a picture of its own.</b> It has the same
    /// family and the same hue, with a different arrangement. So a genre under Calm looks like part of
    /// Calm, which is what the tree already shows by nesting them.</para>
    /// </summary>
    public static (int Style, uint Seed) Cover(SmartPlaylistId id)
    {
        var (style, hue) = Look(id.Kind);

        // The variation sits above the hue bits. It must reach bit 11, because the colourway reads that
        // bit to decide how far apart its three colours sit. A variation that changed only the low bits
        // would give the same picture in the same colours. A decade varies the picture the same way a
        // genre does. Otherwise every decade of one mood would wear the mood's own picture, and the
        // decades would be one cover repeated. The decade is scattered through the hash, not added as a
        // raw number. So 1960 and 1970 are not neighbours in the colourway. Ten apart in the seed would
        // give almost the same picture in almost the same colours.
        uint variation = id.Decade > 0 ? 977u + Scatter($"{id.Decade}s")
                       : id.Genre is null ? 977u
                       : 977u + Scatter(id.Genre);

        return (style, (variation * (uint)RpmPalette.Colours.Count) + hue);
    }

    /// <summary>
    /// The one colour this list wears: the anchor of its colourway, taken from the app's own colour ring.
    ///
    /// <para>It is the same number that <see cref="Cover"/> uses to steer the drawn cover, read back as a
    /// colour. A fetched picture can then be washed in it. This puts a museum photograph and a drawn
    /// figure on one shelf. They are not the same picture, but they are the same colour. A wall of covers
    /// is read by its colours long before any one cover is looked at.</para>
    ///
    /// <para>It is white when the palette has not loaded. That tints nothing, rather than inventing a
    /// colour.</para>
    /// </summary>
    public static Windows.UI.Color Tint(SmartPlaylistId id)
    {
        var ring = RpmPalette.Colours;
        return ring.Count == 0
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : ring[(int)(Cover(id).Seed % (uint)ring.Count)];
    }

    /// <summary>The family and the hue for each kind, taken from the colour ring. The ring runs warm, green,
    /// cool, magenta, red. See <see cref="RpmPalette"/>.</summary>
    private static (int Style, uint Hue) Look(SmartPlaylistKind kind) => kind switch
    {
        // The five lists made from the audio. Here the colour does real work.

        // Blue slows the reader down, which is the purpose of this list. Ripple is the stillest of the eight
        // families: concentric and even.
        SmartPlaylistKind.Calm => (6, 5),           // Ripple, Azure

        // Red is the arousing end of the spectrum, and most people read it the same way. Sweep is the family
        // with movement across it.
        SmartPlaylistKind.Energetic => (3, 9),      // Sweep, Red

        // Magenta is lively without being urgent. It suits dancing, not hurrying. Bokeh is the blur of lights
        // out of focus.
        SmartPlaylistKind.Danceable => (5, 7),      // Bokeh, Magenta

        // Forest, a family that no other list uses. Green reads as steady, not sleepy and not urgent. That is
        // what this list offers.
        SmartPlaylistKind.Focus => (4, 4),          // Ribbon, Forest

        // Indigo is night without being black. Orb is the one family that looks like a single round body seen
        // in the dark. Of the eight abstract forms it comes closest to a moon.
        SmartPlaylistKind.Sleep => (1, 6),          // Orb, Indigo

        // The six the sound model hears, each in a pair no other list uses.
        SmartPlaylistKind.Melancholy => (6, 6),     // Ripple, Indigo: rain on dark water
        SmartPlaylistKind.Happy => (0, 2),          // Halo, Gold: the sun
        SmartPlaylistKind.Angry => (2, 0),          // Mesh, Vermilion: a hot tangle
        SmartPlaylistKind.Romantic => (5, 8),       // Bokeh, Crimson: candlelight out of focus
        SmartPlaylistKind.Zen => (1, 4),            // Orb, Forest: a smooth stone
        SmartPlaylistKind.NewYearsEve => (5, 2),    // Bokeh, Gold: sparklers and champagne
        SmartPlaylistKind.Christmas => (5, 9),      // Bokeh, Red: tree lights out of focus

        // Amber, and a family of its own. A decade list is not one of the moods, so it should not share a
        // mood colour. People read a shelf by its colours first.
        SmartPlaylistKind.Decade => (3, 1),         // Strata, Amber
        SmartPlaylistKind.Classical => (0, 3),      // Halftone, Gold

        // The lists about listening. Each one takes the colour of its own glyph.
        // Drawn in the soft, rounded families (Halo, Orb, Ripple), so the shelf of them reads as one set;
        // the hard-edged ones (Prism, Panels) are left to the lists the listener makes.

        SmartPlaylistKind.FavoriteTracks => (0, 8),     // Halo, Crimson: the heart's own red
        SmartPlaylistKind.MostPlayed => (0, 0),         // Halo, Vermilion: the fire's orange
        SmartPlaylistKind.OnRepeat => (2, 1),           // Mesh, Rust
        SmartPlaylistKind.RecentlyPlayed => (6, 2),     // Ripple, Gold
        SmartPlaylistKind.RecentlyAdded => (3, 3),      // Sweep, Lime: new growth
        SmartPlaylistKind.FirstListens => (0, 4),       // Halo, Forest: new growth
        SmartPlaylistKind.LateNight => (5, 6),          // Bokeh, Indigo: night, as for Sleep
        SmartPlaylistKind.Rediscover => (6, 7),         // Ripple, Magenta
        SmartPlaylistKind.LovedAndForgotten => (1, 8),  // Orb, Crimson: the heart, dimmed
        SmartPlaylistKind.PlayedNotLoved => (2, 9),     // Mesh, Red
        SmartPlaylistKind.NeverPlayed => (1, 5),        // Orb, Azure: untouched, cool
        SmartPlaylistKind.KeepSkipping => (1, 1),       // Orb, Rust

        // Azure is the one hue left that reads as neither urgent nor sleepy. That suits a list meant for any
        // hour of the day. Ripple is the family that reads as movement without a direction.
        SmartPlaylistKind.RightNow => (6, 5),           // Ripple, Azure

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "This generated playlist has no picture yet."),
    };

    /// <summary>A stable number from a genre's name. The rock under "Calm" gets the same variation on
    /// every machine and after every restart. The hash is FNV-1a, written out here. It is not <see
    /// cref="string.GetHashCode()"/>, which is randomised per process and would redraw every genre cover
    /// on each launch.</summary>
    private static uint Scatter(string text)
    {
        uint hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash % 4096u;
    }

    /// <summary>The pinned folder's label. It is the one row in the tree that says what everything under it
    /// has in common: the app made these, not the user.</summary>
    public static string FolderName => ListNames.FolderName(Text);

    /// <summary>The decades folder's name. It is a sibling of <see cref="FolderName"/>, not a row inside
    /// it. The lists in it are the four moods asked of one decade. Nesting them under the generated
    /// playlists would put the same names twice in one tree, at two depths.</summary>
    public static string DecadesFolderName => ListNames.DecadesFolderName(Text);

    /// <summary>The moods folder's name. It is a sibling of <see cref="FolderName"/> and of <see
    /// cref="DecadesFolderName"/>, not a row inside either. The pinned folder holds two kinds of list:
    /// what the music sounds like, and what the listener has done with it. The second kind is much
    /// longer, so the moods have their own folder. What goes in here is <see
    /// cref="SmartPlaylists.Moods"/>.</summary>
    public static string MoodsFolderName => ListNames.MoodsFolderName(Text);

    /// <summary>What one decade's folder is called. It is not localized and not a resource. "1970s" is a
    /// number with a letter after it, the same in every locale the app supports. Putting it in
    /// strings.json would mean five identical rows, and five chances for one of them to be
    /// wrong.</summary>
    public static string DecadeName(int decade) => ListNames.DecadeName(decade);

    /// <summary>The decades folder's glyph: a clock. The folder is arranged by when, not by what.</summary>
    public const string DecadesFolderGlyph = "\uf017";   // clock

    /// <summary>The pinned folder's glyph: a wand. It is the one folder in the tree that a person did not
    /// make.</summary>
    public const string FolderGlyph = "\uf72b";   // wand-sparkles

    /// <summary>The moods folder's mark. It is a picture and not a glyph, because it is shaded and a font
    /// draws one colour.</summary>
    public const string MoodsFolderMark = "ms-appx:///Assets/moods-folder.png";
}
