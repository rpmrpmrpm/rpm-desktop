using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// The name and the icon of a generated playlist. The rule that decides which songs go in a list lives in
/// Core (<see cref="SmartPlaylistKind"/>). The name and the glyph belong to the interface, so they are here.
/// A new kind then needs one arm in each of two switches, and no search through the pane.
///
/// <para>Neither switch has a fallback arm. A kind added to the enum needs a name and a glyph chosen on
/// purpose. A default arm would quietly give it the favourites' glyph.</para>
/// </summary>
internal static class SmartPlaylistFace
{
    /// <summary>
    /// What one generated list is called.
    ///
    /// <para>A mood row uses the app's language. A genre row under a mood does not. It has its own title from
    /// <see cref="Titles"/>, in English in every language. See that table for why.</para>
    /// </summary>
    public static string Name(SmartPlaylistId id) =>
        // A decade is named by its decade number. This is checked first, because the fall-through below
        // ends at Name(kind), which has no arm for a list whose whole name is that number.
        id.IsWithinDecade ? DecadeName(id.Decade)
        : id.Genre is { } genre && Titles.TryGetValue((id.Kind, genre), out var title) ? title

        // A list that a GENRE page offers is named by the question it asks, not by the genre. Four cards
        // in a row under "Essential rock playlists" would otherwise all be called "Rock". That says
        // nothing, and says it four times. The heading above them already names the genre. See
        // SmartPlaylists.EssentialForAGenre, the set that page draws from.
        : id.Genre is not null && SmartPlaylists.EssentialForAGenre.Contains(id.Kind) ? Name(id.Kind)

        // A MOOD over a genre with no title written for it falls back to the genre name. That is correct
        // here. These rows sit nested under the mood's own row in the tree, so the parent already says
        // which mood it is, and the child only says which shelf.
        : id.Genre ?? Name(id.Kind);

    /// <summary>
    /// The same name, given the kind as a string. A cut from another device names its lists by the string
    /// form of <see cref="SmartPlaylistKind"/>, not by the enum value.
    ///
    /// <para>A kind this build does not know (the other end is a newer version) falls back to that string.
    /// The list is not dropped, because a list with an unfamiliar name still plays.</para>
    /// </summary>
    public static string Name(string kind, string? genre, int decade)
        => Enum.TryParse<SmartPlaylistKind>(kind, ignoreCase: false, out var parsed)
            ? Name(new SmartPlaylistId(parsed, genre, decade))
            : kind;

    /// <summary>
    /// Where a generated list sits in this app's tree, outermost first.
    ///
    /// <para><b>This is the one place that answers it, for every device that shows these lists.</b> The
    /// pane builds the same three folders below (see <c>PlaylistModel</c>). If each device worked out the
    /// arrangement at its own end, the devices could disagree. A borrowed shelf could then show the eras
    /// and the mood styles flat in one folder, and a phone could show its own shape. A cut is a shelf as
    /// its owner arranged it. The arrangement travels with the cut, and this method gives the answer that
    /// is sent.</para>
    ///
    /// <para>A mood narrowed to a genre hangs under its own mood's row, which is what the pane does with
    /// it. A decade is a sibling of the moods, not one of them. Everything that is neither (the lists
    /// about listening rather than about sound) is in the pinned folder.</para>
    /// </summary>
    public static IReadOnlyList<string> FolderChain(string kind, string? genre, int decade)
    {
        if (decade > 0)
            return [DecadesFolderName];

        if (!Enum.TryParse<SmartPlaylistKind>(kind, ignoreCase: false, out var parsed))
            return [MoodsFolderName];

        if (!SmartPlaylists.Moods.Contains(parsed))
            return [FolderName];

        return genre is { Length: > 0 }
            ? [MoodsFolderName, Name(SmartPlaylistId.WholeShelf(parsed))]
            : [MoodsFolderName];
    }

    /// <summary>
    /// The title a mood uses over one genre. For example, "Slow Burn" rather than "Chill / Rock".
    ///
    /// <para>Each pair is written out by hand. The combined form read as a mood above a category. "Chill"
    /// above "Electronic / Dance" is a mood above a taxonomy, and the second half does the job of a shelf
    /// label, not a playlist name. A formula would be less typing, but no formula can produce "Four to the
    /// Floor" from a genre string.</para>
    ///
    /// <para>The titles are in English in every locale, on purpose. They are names, not interface text. A
    /// song's name is not translated either. A name translated into five languages would be five weaker
    /// names. The genres wall already shows <see cref="Genres.All"/> untranslated, so these titles match
    /// it. The titles are kept here, not in <c>strings.json</c>, because that file holds interface text
    /// only.</para>
    ///
    /// <para>Some titles follow their records. Some genres were split out of broader shelves, and their
    /// records moved with them. So "Funk Fever" is the loud end of Funk, where the funk records now are.
    /// "Soul Jams", "House Party" and "Rap Core" follow the soul, house and rap records. A square that a
    /// title has left has no line here. See MoodSets.Absorbed.</para>
    ///
    /// <para>Half the answer. <see cref="MoodSets"/> is the other half. This table says what a list is
    /// called. MoodSets says which squares of the grid each list draws from, and which squares are no
    /// longer lists at all because another list took them over. A square that MoodSets marks as taken
    /// over has no line here. If it had one, the name could still be reached through a shared cut, and it
    /// would read as a list this app does not have. A test checks the two tables against each
    /// other.</para>
    ///
    /// <para>The titles are in the order of <see cref="Genres.All"/>, so this table can be read down
    /// against the list it answers. Every genre has a line under every mood, except the squares that
    /// MoodSets has taken over. A test checks this, and checks that no two titles are the same.</para>
    ///
    /// <para>A pair with no title falls back to the genre name. A genre added to <see cref="Genres.All"/>
    /// later reads as itself, not as nothing.</para>
    /// </summary>
    private static readonly Dictionary<(SmartPlaylistKind Kind, string Genre), string> Titles = new()
    {
        // Chill: the quiet end of each kind of music
        [(SmartPlaylistKind.Calm, "Rock")] = "Slow Burn",
        [(SmartPlaylistKind.Calm, "Indie")] = "Bedroom Tapes",
        [(SmartPlaylistKind.Calm, "Pop")] = "Soft Pop",
        [(SmartPlaylistKind.Calm, "Disco")] = "After the Party",
        [(SmartPlaylistKind.Calm, "Funk")] = "Soft Groove",
        [(SmartPlaylistKind.Calm, "Soul")] = "Quiet Storm",
        [(SmartPlaylistKind.Calm, "R&B")] = "Soft Velvet",
        [(SmartPlaylistKind.Calm, "Rap")] = "Low End",
        [(SmartPlaylistKind.Calm, "Trap")] = "Night Drive",
        [(SmartPlaylistKind.Calm, "Old School Hip Hop")] = "Corner Store",
        [(SmartPlaylistKind.Calm, "Hip-Hop / Rap")] = "Just the Bars",
        [(SmartPlaylistKind.Calm, "House")] = "Sunrise Set",
        [(SmartPlaylistKind.Calm, "Techno")] = "Slow Machine",
        [(SmartPlaylistKind.Calm, "Club")] = "Closing Time",
        [(SmartPlaylistKind.Calm, "Electronic / Dance")] = "Ambient Drift",
        [(SmartPlaylistKind.Calm, "Jazz")] = "Jazz Club",
        [(SmartPlaylistKind.Calm, "Blues")] = "Slow Blues",
        [(SmartPlaylistKind.Calm, "Country")] = "Porch Light",
        [(SmartPlaylistKind.Calm, "Folk")] = "Hearthside",
        [(SmartPlaylistKind.Calm, "Classical")] = "Adagio",
        [(SmartPlaylistKind.Calm, "Neo-Classical")] = "Felt Piano",
        [(SmartPlaylistKind.Calm, "Opera")] = "Aria",
        [(SmartPlaylistKind.Calm, "Metal")] = "The Calm Before",
        [(SmartPlaylistKind.Calm, "Punk")] = "Comedown",
        // The last two shelves, in the order Genres.All keeps them. The fallback would hide a missing
        // title on screen, so a test checks that every genre has a title of its own.
        [(SmartPlaylistKind.Calm, "Noise")] = "Low Hum",
        [(SmartPlaylistKind.Calm, "Avant-Garde")] = "Long Tones",
        [(SmartPlaylistKind.Calm, "Reggae")] = "Island Time",
        [(SmartPlaylistKind.Calm, "Latin")] = "Bossa Hours",
        [(SmartPlaylistKind.Calm, "World")] = "Distant Shores",
        [(SmartPlaylistKind.Calm, "Religious")] = "Sanctuary",
        [(SmartPlaylistKind.Calm, "Soundtrack")] = "End Credits",
        [(SmartPlaylistKind.Calm, "Video Game")] = "Save Point",
        [(SmartPlaylistKind.Calm, "Musical")] = "Curtain Down",
        [(SmartPlaylistKind.Calm, "Spoken Word")] = "Quiet Words",

        // Adrenaline: the loud, busy end
        [(SmartPlaylistKind.Energetic, "Rock")] = "Amplified",
        // No line for (Energetic, "Indie"): that square is taken over by another list (see
        // MoodSets.Absorbed).
        [(SmartPlaylistKind.Energetic, "Pop")] = "Pop Fever",
        [(SmartPlaylistKind.Energetic, "Disco")] = "Hustle",

        // No line for (Energetic, "Funk"): that square is taken over by another list (see
        // MoodSets.Absorbed). A square that nothing offers must have no name. Otherwise the name could be
        // reached through a shared cut and read as a list this app does not have.
        [(SmartPlaylistKind.Energetic, "Soul")] = "Funkadelica",
        [(SmartPlaylistKind.Energetic, "R&B")] = "Shout",

        // Records tagged "Rap" are on this square.
        [(SmartPlaylistKind.Energetic, "Rap")] = "Rap Core",   // no other list takes this square
        [(SmartPlaylistKind.Energetic, "Trap")] = "808s",
        [(SmartPlaylistKind.Energetic, "Old School Hip Hop")] = "Boom Bap",
        [(SmartPlaylistKind.Energetic, "Hip-Hop / Rap")] = "Rapsody",

        // House records are not filed under Electronic / Dance.
        [(SmartPlaylistKind.Energetic, "House")] = "House Party",
        [(SmartPlaylistKind.Energetic, "Techno")] = "Berlin Nights",
        [(SmartPlaylistKind.Energetic, "Club")] = "Big Night",
        [(SmartPlaylistKind.Energetic, "Electronic / Dance")] = "Main Floor",
        [(SmartPlaylistKind.Energetic, "Jazz")] = "Hard Bop",
        [(SmartPlaylistKind.Energetic, "Blues")] = "Juke Joint",
        [(SmartPlaylistKind.Energetic, "Country")] = "Tailgate",
        // No line for (Energetic, "Folk"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Energetic, "Classical")] = "Fortissimo",
        [(SmartPlaylistKind.Energetic, "Neo-Classical")] = "Strings Rising",
        [(SmartPlaylistKind.Energetic, "Opera")] = "Grand Finale",
        [(SmartPlaylistKind.Energetic, "Metal")] = "Wall of Sound",
        [(SmartPlaylistKind.Energetic, "Punk")] = "Fast and Loud",
        [(SmartPlaylistKind.Energetic, "Noise")] = "Blown Speakers",
        [(SmartPlaylistKind.Energetic, "Avant-Garde")] = "Free Blowing",
        [(SmartPlaylistKind.Energetic, "Reggae")] = "Dancehall Heat",
        [(SmartPlaylistKind.Energetic, "Latin")] = "Fuego",
        [(SmartPlaylistKind.Energetic, "World")] = "Carnival",
        [(SmartPlaylistKind.Energetic, "Religious")] = "Hands Up",
        [(SmartPlaylistKind.Energetic, "Soundtrack")] = "Chase Scene",
        [(SmartPlaylistKind.Energetic, "Video Game")] = "Boss Fight",
        [(SmartPlaylistKind.Energetic, "Musical")] = "Show Stopper",
        [(SmartPlaylistKind.Energetic, "Spoken Word")] = "Full Voice",

        // Dancefloor: busy over a level that holds still
        [(SmartPlaylistKind.Danceable, "Rock")] = "Rock Anthems",

        // A club night. The name fits the indie records on this shelf.
        [(SmartPlaylistKind.Danceable, "Indie")] = "Indie Disco",
        [(SmartPlaylistKind.Danceable, "Pop")] = "Club Pop",
        [(SmartPlaylistKind.Danceable, "Disco")] = "Disco Fever",
        [(SmartPlaylistKind.Danceable, "Funk")] = "Get Down",

        // The soul records are on this shelf, so the name follows them.
        [(SmartPlaylistKind.Danceable, "Soul")] = "Soul Jams",
        [(SmartPlaylistKind.Danceable, "R&B")] = "Two Step",
        // No line for (Danceable, "Rap"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Danceable, "Trap")] = "Hi Hats",
        [(SmartPlaylistKind.Danceable, "Old School Hip Hop")] = "Cardboard Floor",
        [(SmartPlaylistKind.Danceable, "Hip-Hop / Rap")] = "Block Party",
        [(SmartPlaylistKind.Danceable, "House")] = "Warehouse",
        [(SmartPlaylistKind.Danceable, "Techno")] = "Steady Pulse",
        [(SmartPlaylistKind.Danceable, "Club")] = "Guest List",
        [(SmartPlaylistKind.Danceable, "Electronic / Dance")] = "Four to the Floor",
        [(SmartPlaylistKind.Danceable, "Jazz")] = "A Moment for the Lovers",
        [(SmartPlaylistKind.Danceable, "Blues")] = "Shuffle",
        [(SmartPlaylistKind.Danceable, "Country")] = "Line Dance",
        [(SmartPlaylistKind.Danceable, "Folk")] = "Barn Dance",
        [(SmartPlaylistKind.Danceable, "Classical")] = "Ballroom",
        [(SmartPlaylistKind.Danceable, "Neo-Classical")] = "Minimal Motion",
        [(SmartPlaylistKind.Danceable, "Opera")] = "Overture",
        [(SmartPlaylistKind.Danceable, "Metal")] = "Pit Floor",
        [(SmartPlaylistKind.Danceable, "Punk")] = "Pogo",
        [(SmartPlaylistKind.Danceable, "Noise")] = "Machine Rhythm",
        [(SmartPlaylistKind.Danceable, "Avant-Garde")] = "Cut and Splice",
        [(SmartPlaylistKind.Danceable, "Reggae")] = "Soundsystem",
        [(SmartPlaylistKind.Danceable, "Latin")] = "Salsa Night",
        [(SmartPlaylistKind.Danceable, "World")] = "Global Floor",
        [(SmartPlaylistKind.Danceable, "Religious")] = "Clap and Stomp",
        [(SmartPlaylistKind.Danceable, "Soundtrack")] = "Title Sequence",
        [(SmartPlaylistKind.Danceable, "Video Game")] = "Bonus Stage",
        [(SmartPlaylistKind.Danceable, "Musical")] = "Big Number",
        [(SmartPlaylistKind.Danceable, "Spoken Word")] = "Spoken Groove",

        // Focus: the same still level with little happening over it
        //
        // Focus records are never the same as the Dancefloor records above. One wants busy and the other
        // sparse, so the two cuts do not meet. The names go two ways. Some say what a record is doing
        // ("Instrumentals", "Held Note", "One Piano"). Some say when you would play it ("Golden Hour",
        // "Midnight Mood"). Together they are the two things a listener chooses between when they pick
        // this block over the one above.
        [(SmartPlaylistKind.Focus, "Rock")] = "Golden Hour",
        // No line for (Focus, "Indie"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Pop")] = "Pop Lounge",
        [(SmartPlaylistKind.Focus, "Disco")] = "Long Intro",
        [(SmartPlaylistKind.Focus, "Funk")] = "Slow Jamz",
        [(SmartPlaylistKind.Focus, "Soul")] = "Midnight Mood",
        [(SmartPlaylistKind.Focus, "R&B")] = "Smooth Sail",
        // No line for (Focus, "Rap"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Trap")] = "Loop Tape",
        [(SmartPlaylistKind.Focus, "Old School Hip Hop")] = "Beat Tape",
        [(SmartPlaylistKind.Focus, "Hip-Hop / Rap")] = "Rap Life",
        // No line for (Focus, "House"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Techno")] = "Loop Room",
        [(SmartPlaylistKind.Focus, "Club")] = "Warm Up",
        [(SmartPlaylistKind.Focus, "Electronic / Dance")] = "9-to-5",
        [(SmartPlaylistKind.Focus, "Jazz")] = "The Lounge",
        [(SmartPlaylistKind.Focus, "Blues")] = "Slow Hand",
        [(SmartPlaylistKind.Focus, "Country")] = "Open Road",
        [(SmartPlaylistKind.Focus, "Folk")] = "Folk Off",
        [(SmartPlaylistKind.Focus, "Classical")] = "Andante",
        [(SmartPlaylistKind.Focus, "Neo-Classical")] = "One Piano",
        [(SmartPlaylistKind.Focus, "Opera")] = "Recitative",
        [(SmartPlaylistKind.Focus, "Metal")] = "Drone Riff",
        [(SmartPlaylistKind.Focus, "Punk")] = "Rehearsal Room",
        [(SmartPlaylistKind.Focus, "Noise")] = "Flat Line",
        [(SmartPlaylistKind.Focus, "Avant-Garde")] = "Held Note",
        [(SmartPlaylistKind.Focus, "Reggae")] = "Easy Skank",
        [(SmartPlaylistKind.Focus, "Latin")] = "Nylon Strings",
        [(SmartPlaylistKind.Focus, "World")] = "Long Journey",
        [(SmartPlaylistKind.Focus, "Religious")] = "Vespers",
        [(SmartPlaylistKind.Focus, "Soundtrack")] = "Underscore",
        [(SmartPlaylistKind.Focus, "Video Game")] = "Overworld",
        [(SmartPlaylistKind.Focus, "Musical")] = "Between Numbers",
        [(SmartPlaylistKind.Focus, "Spoken Word")] = "Low Murmur",
    };

    /// <summary>The label of a list that covers a whole shelf, in the app's language. It is not stored
    /// anywhere, because a generated list is not something a person names.</summary>
    private static string Name(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.FavoriteTracks => Loc.Get("Playlists_FavoriteTracks"),
        SmartPlaylistKind.MostPlayed => Loc.Get("Playlists_MostPlayed"),
        SmartPlaylistKind.RecentlyAdded => Loc.Get("Playlists_RecentlyAdded"),
        SmartPlaylistKind.RecentlyPlayed => Loc.Get("Playlists_RecentlyPlayed"),
        SmartPlaylistKind.NeverPlayed => Loc.Get("Playlists_NeverPlayed"),
        SmartPlaylistKind.LovedAndForgotten => Loc.Get("Playlists_LovedAndForgotten"),
        SmartPlaylistKind.PlayedNotLoved => Loc.Get("Playlists_PlayedNotLoved"),
        SmartPlaylistKind.OnRepeat => Loc.Get("Playlists_OnRepeat"),
        SmartPlaylistKind.Rediscover => Loc.Get("Playlists_Rediscover"),
        SmartPlaylistKind.LateNight => Loc.Get("Playlists_LateNight"),
        SmartPlaylistKind.FirstListens => Loc.Get("Playlists_FirstListens"),
        SmartPlaylistKind.KeepSkipping => Loc.Get("Playlists_KeepSkipping"),
        SmartPlaylistKind.Calm => Loc.Get("Playlists_Calm"),
        SmartPlaylistKind.Energetic => Loc.Get("Playlists_Energetic"),
        SmartPlaylistKind.Danceable => Loc.Get("Playlists_Danceable"),
        SmartPlaylistKind.Focus => Loc.Get("Playlists_Focus"),
        SmartPlaylistKind.Sleep => Loc.Get("Playlists_Sleep"),
        SmartPlaylistKind.RightNow => Loc.Get("Playlists_RightNow"),

        // Name(SmartPlaylistId) answers a decade by its number first, so this arm is not reached through
        // it. It is written anyway: a missing arm is what the throw at the bottom of this switch is for.
        SmartPlaylistKind.Decade => Loc.Get("Playlists_DecadesFolder"),
        SmartPlaylistKind.Classical => Loc.Get("Playlists_Classical"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This generated playlist has no name yet."),
    };

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

        // Amber, and a family of its own. A decade list is not one of the moods, so it should not share a
        // mood colour. People read a shelf by its colours first.
        SmartPlaylistKind.Decade => (3, 1),         // Strata, Amber
        SmartPlaylistKind.Classical => (0, 3),      // Halftone, Gold

        // The lists about listening. Each one takes the colour of its own glyph.

        SmartPlaylistKind.FavoriteTracks => (0, 8),     // Halo, Crimson: the heart's own red
        SmartPlaylistKind.MostPlayed => (7, 0),         // Prism, Vermilion: the fire's orange
        SmartPlaylistKind.OnRepeat => (2, 1),           // Mesh, Rust
        SmartPlaylistKind.RecentlyPlayed => (4, 2),     // Panels, Gold
        SmartPlaylistKind.RecentlyAdded => (3, 3),      // Sweep, Lime: new growth
        SmartPlaylistKind.FirstListens => (0, 4),       // Halo, Forest: new growth
        SmartPlaylistKind.LateNight => (5, 6),          // Bokeh, Indigo: night, as for Sleep
        SmartPlaylistKind.Rediscover => (6, 7),         // Ripple, Magenta
        SmartPlaylistKind.LovedAndForgotten => (1, 8),  // Orb, Crimson: the heart, dimmed
        SmartPlaylistKind.PlayedNotLoved => (2, 9),     // Mesh, Red
        SmartPlaylistKind.NeverPlayed => (4, 5),        // Panels, Azure: untouched, cool
        SmartPlaylistKind.KeepSkipping => (7, 1),       // Prism, Rust

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
    public static string FolderName => Loc.Get("Playlists_SmartFolder");

    /// <summary>The decades folder's name. It is a sibling of <see cref="FolderName"/>, not a row inside
    /// it. The lists in it are the four moods asked of one decade. Nesting them under the generated
    /// playlists would put the same names twice in one tree, at two depths.</summary>
    public static string DecadesFolderName => Loc.Get("Playlists_DecadesFolder");

    /// <summary>The moods folder's name. It is a sibling of <see cref="FolderName"/> and of <see
    /// cref="DecadesFolderName"/>, not a row inside either. The pinned folder holds two kinds of list:
    /// what the music sounds like, and what the listener has done with it. The second kind is much
    /// longer, so the moods have their own folder. What goes in here is <see
    /// cref="SmartPlaylists.Moods"/>.</summary>
    public static string MoodsFolderName => Loc.Get("Playlists_MoodsFolder");

    /// <summary>What one decade's folder is called. It is not localized and not a resource. "1970s" is a
    /// number with a letter after it, the same in every locale the app supports. Putting it in
    /// strings.json would mean five identical rows, and five chances for one of them to be
    /// wrong.</summary>
    public static string DecadeName(int decade) => $"{decade}s";

    /// <summary>The decades folder's glyph: a clock. The folder is arranged by when, not by what.</summary>
    public const string DecadesFolderGlyph = "\uf017";   // clock

    /// <summary>The pinned folder's glyph: a wand. It is the one folder in the tree that a person did not
    /// make.</summary>
    public const string FolderGlyph = "\uf72b";   // wand-sparkles
}
