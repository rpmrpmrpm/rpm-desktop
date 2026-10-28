using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>One track as <see cref="LibraryDatabase.ReadRadioCrate"/> reads it, before the mood readings are turned
/// into percentiles.</summary>
/// <param name="Artist">The credit, as <see cref="RecommendRefusals.CreditOf"/> composes it. Empty when nothing names
/// an act.</param>
/// <param name="Year">Year of release, or 0 when the tags do not say.</param>
/// <param name="Brightness">The mood meter's three readings, or null when the track has not been measured.</param>
/// <param name="Offered">Whether a radio may pick it: at least <see cref="Moods.LeastSeconds"/> long and playable from
/// this computer. A track that is not offered can still be the song a radio starts from.</param>
/// <param name="Bpm">The tempo, or null where nothing says: the analyser's measurement where it is confident, else the
/// file's tag. Whichever octave it was counted in; <see cref="SongRadio.TempoApart"/> does not care.</param>
/// <param name="Plays">How many times the listener has played it here.</param>
/// <param name="GenreTag">The file's own genre field, as written. Read for styles only: the shelf comes from
/// <see cref="MoodBands.GenreByFolder"/>, which decides it per record.</param>
/// <param name="Subgenre">The file's style field, as written, or null.</param>
/// <param name="Album">The album's name, as tagged, or null. Read to tell a live recording (see
/// <see cref="SongRadio.IsLive"/>).</param>
/// <param name="ReleaseType">The record's release type as stored (<see cref="ReleaseTypes.ToStored"/>), or null.</param>
public sealed record RadioRow(
    string RelativePath, string Folder, string Artist, string Title, int Year,
    double? Brightness, double? Onsets, double? Dynamics, bool Offered, double? Bpm,
    int Plays = 0, string? GenreTag = null, string? Subgenre = null,
    string? Album = null, string? ReleaseType = null);

/// <summary>A song's three mood readings as percentiles of the collection, each from 0 to 1. Percentiles because the
/// readings are in different units (a share of energy, events a second, decibels), and unscaled the decibels would
/// decide every comparison.</summary>
public readonly record struct RadioSound(double Brightness, double Onsets, double Dynamics);

/// <summary>One song a radio can play or start from.</summary>
/// <param name="Song">The artist and the title with editions and featured credits removed, folded to lower case. Two
/// rows with the same value are the same song on two records (an album and its remaster, or a best-of).</param>
/// <param name="Genre">The record's shelf, as <see cref="MoodBands.GenreByFolder"/> files it, or null.</param>
/// <param name="Sound">Null when the song has not been measured.</param>
/// <param name="Moods">The mood lists the song is in this session (<see cref="MoodShelf.MoodsByPath"/>). Empty for a
/// measured song in none of them, which is most songs.</param>
/// <param name="Bpm">The tempo, or null. See <see cref="RadioRow.Bpm"/>.</param>
/// <param name="Styles">The corners of the shelf the song comes from, as <see cref="Subgenres"/> names them: Trip
/// Hop, Electro, Baroque. From the file's own fields and from what the catalogues say about its record. Empty where
/// nothing names one, which is most songs.</param>
/// <param name="Plays">How many times the listener has played it. See <see cref="RadioCrate.IsFavourite"/>.</param>
/// <param name="Live">Whether it is a live recording (<see cref="SongRadio.IsLive"/>).</param>
public sealed record RadioSong(
    string Path, string Folder, string Artist, string Song, int Year, string? Genre,
    RadioSound? Sound, IReadOnlySet<SmartPlaylistKind> Moods, bool Offered, double? Bpm,
    IReadOnlySet<string> Styles, int Plays, bool Live = false);

/// <summary>
/// Every song in one collection that a song radio can pick from or start from. Read once when a radio starts and
/// held by it, so picking the next songs never reads the database.
/// </summary>
public sealed class RadioCrate
{
    private static readonly IReadOnlySet<SmartPlaylistKind> NoMoods = new HashSet<SmartPlaylistKind>();

    private static readonly IReadOnlySet<string> NoStyles = new HashSet<string>();

    /// <summary>How many times a song must have been played to count as one the listener plays most. Once or twice
    /// is a song that was tried.</summary>
    internal const int FavouriteLeastPlays = 3;

    /// <summary>How many of the most played songs count as favourites. A radio slips one in now and then
    /// (<see cref="SongRadio.FavouriteEvery"/>), and it should be a song the listener would name, not the
    /// five-hundredth most played.</summary>
    internal const int FavouritePool = 150;

    private readonly Dictionary<string, RadioSong> _byPath;
    private readonly HashSet<string> _favourites;

    private RadioCrate(IReadOnlyList<RadioSong> songs)
    {
        Songs = songs;
        _byPath = new Dictionary<string, RadioSong>(songs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
            _byPath[song.Path] = song;

        // The path breaks a tie, so one crate always names the same favourites.
        _favourites = new HashSet<string>(
            songs.Where(s => s.Offered && s.Plays >= FavouriteLeastPlays)
                 .OrderByDescending(s => s.Plays)
                 .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                 .Take(FavouritePool)
                 .Select(s => s.Path),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the song is one of the <see cref="FavouritePool"/> the listener plays most here.</summary>
    public bool IsFavourite(RadioSong song) => _favourites.Contains(song.Path);

    /// <summary>A collection that could not be read. A radio on it plays the song it was started from and nothing
    /// after.</summary>
    public static readonly RadioCrate Empty = new([]);

    public IReadOnlyList<RadioSong> Songs { get; }

    /// <summary>The song at a relative path, or null when the crate does not hold it.</summary>
    public RadioSong? Find(string relativePath) => _byPath.GetValueOrDefault(relativePath);

    /// <param name="genreByFolder">The shelf each record is filed under, from
    /// <see cref="MoodBands.GenreByFolder"/>.</param>
    /// <param name="moodsByPath">The mood lists each song is in, from <see cref="MoodShelf.MoodsByPath"/>.</param>
    /// <param name="stylesByFolder">The styles each record is filed under by the catalogues, in any spelling. Read
    /// through <see cref="Subgenres.Match"/>, so a style RPM has no name for is dropped.</param>
    public static RadioCrate Build(
        IReadOnlyList<RadioRow> rows,
        IReadOnlyDictionary<string, string> genreByFolder,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> moodsByPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stylesByFolder = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(genreByFolder);
        ArgumentNullException.ThrowIfNull(moodsByPath);
        stylesByFolder ??= new Dictionary<string, IReadOnlyList<string>>();

        // Percentiles are taken over the measured songs only. An unmeasured song has no place on any axis.
        var measured = new List<int>();
        for (var i = 0; i < rows.Count; i++)
            if (IsMeasured(rows[i]))
                measured.Add(i);

        var brightness = Percentiles([.. measured.Select(i => rows[i].Brightness!.Value)]);
        var onsets = Percentiles([.. measured.Select(i => rows[i].Onsets!.Value)]);
        var dynamics = Percentiles([.. measured.Select(i => rows[i].Dynamics!.Value)]);

        var sounds = new RadioSound?[rows.Count];
        for (var k = 0; k < measured.Count; k++)
            sounds[measured[k]] = new RadioSound(brightness[k], onsets[k], dynamics[k]);

        // A collection spells a few hundred genre tags and styles across many thousand rows, so each spelling is
        // read once.
        var readings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var folderStyles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var songs = new RadioSong[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (!folderStyles.TryGetValue(row.Folder, out var ofRecord))
                // Each entry is one style already, and may hold a comma ("Folk, World, & Country"), so it is matched
                // whole and not split like a file's tag.
                folderStyles[row.Folder] = ofRecord = stylesByFolder.TryGetValue(row.Folder, out var listed)
                    ? [.. listed.Select(Subgenres.Match).OfType<Subgenre>().Select(s => s.Name)
                               .Distinct(StringComparer.OrdinalIgnoreCase)]
                    : [];

            var styles = StylesOf(row, ofRecord);

            // A record no tag or catalogue files on a shelf, but whose style says which shelf it is on: a file
            // tagged only "Trip Hop" is an electronic record.
            var genre = genreByFolder.TryGetValue(row.Folder, out var filed)
                ? filed
                : styles.Count > 0 ? Subgenres.Match(styles.First())?.Genre : null;

            songs[i] = new RadioSong(
                row.RelativePath, row.Folder, row.Artist, SongOf(row), row.Year,
                genre,
                sounds[i],
                moodsByPath.TryGetValue(row.RelativePath, out var moods) ? moods : NoMoods,
                row.Offered,
                row.Bpm is > 0 ? row.Bpm : null,
                styles,
                Math.Max(row.Plays, 0),
                SongRadio.IsLive(row.Title, row.Album, row.ReleaseType));
        }

        return new RadioCrate(songs);

        IReadOnlySet<string> StylesOf(RadioRow row, IReadOnlyList<string> ofRecord)
        {
            if (ofRecord.Count == 0 && string.IsNullOrWhiteSpace(row.Subgenre) && string.IsNullOrWhiteSpace(row.GenreTag))
                return NoStyles;

            var set = new HashSet<string>(ofRecord, StringComparer.OrdinalIgnoreCase);
            set.UnionWith(StylesIn(row.Subgenre));
            set.UnionWith(StylesIn(row.GenreTag));
            return set.Count == 0 ? NoStyles : set;
        }

        // A tag may list several, as "classical, romantic classical, classical period" does. Each entry is read on
        // its own, so the second and third are not lost behind the first.
        IReadOnlyList<string> StylesIn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return [];
            if (readings.TryGetValue(text, out var known))
                return known;

            var found = text.Split(StyleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Subgenres.Match)
                .OfType<Subgenre>()
                .Select(s => s.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            readings[text] = found;
            return found;
        }
    }

    /// <summary>What separates the entries of a tag that lists several. Not the slash: "Hip-Hop / Rap" is one
    /// shelf.</summary>
    private static readonly char[] StyleSeparators = [',', ';', '|'];

    /// <summary>All three readings present and not all nought. Nought on all three is what the meter stores for a
    /// track it measured and found nothing in (see <c>LibraryDatabase.SaveTrackMood</c>), which is not a place on
    /// any axis.</summary>
    private static bool IsMeasured(RadioRow row) =>
        row is { Brightness: { } b, Onsets: { } o, Dynamics: { } d } && !(b == 0 && o == 0 && d == 0);

    /// <summary>Each value's place among the others, from 0 for the lowest to 1 for the highest. Tied values share
    /// the middle of the places they cover, so a run of equal readings does not spread across the axis.</summary>
    private static double[] Percentiles(IReadOnlyList<double> values)
    {
        var count = values.Count;
        var result = new double[count];
        if (count < 2)
        {
            Array.Fill(result, 0.5);
            return result;
        }

        var order = Enumerable.Range(0, count).ToArray();
        Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

        for (var first = 0; first < count;)
        {
            var last = first;
            while (last + 1 < count && values[order[last + 1]] == values[order[first]])
                last++;

            var place = (first + last) / 2.0 / (count - 1);
            for (var k = first; k <= last; k++)
                result[order[k]] = place;

            first = last + 1;
        }

        return result;
    }

    /// <summary>The value two copies of one song share. An untitled file gets its own path, so it is never taken
    /// for another untitled file by the same act.</summary>
    private static string SongOf(RadioRow row)
    {
        var title = TitleAsides.NameOnly(TitleAsides.WithoutCredits(row.Title)).Trim();
        return title.Length == 0
            ? row.RelativePath
            : row.Artist.Trim().ToLowerInvariant() + "\n" + title.ToLowerInvariant();
    }
}

/// <summary>
/// Picks the songs a song radio plays next: songs close to the one playing in mood, era and genre (down to the style:
/// trip hop is not electro), and, counting for less, tempo.
///
/// <para>Every <see cref="FavouriteEvery"/>th song is one the listener plays most, when one of them fits as well as
/// the closest few percent of the collection does. One that does not fit is not forced in: a radio of quiet piano
/// does not stop for a dance favourite.</para>
///
/// <para>Each song the radio plays is the seed for the songs added while it plays. The song the radio was started
/// from still counts for half as much again (<see cref="OriginWeight"/>), so a long session of radio does not drift
/// from 1970s folk to 2010s rock one small step at a time.</para>
///
/// <para>Pure: the crate, the queue and the history are handed in, and the answer is a list of relative paths.</para>
/// </summary>
public static class SongRadio
{
    /// <summary>How many songs a radio keeps queued after the one playing. Each song that starts adds songs until
    /// there are this many again, which is one song in the ordinary case.</summary>
    public const int Ahead = 10;

    /// <summary>How much the song the radio started from counts against the song playing now.</summary>
    internal const double OriginWeight = 0.5;

    /// <summary>Each pick is drawn at random from this many of the closest songs that pass the spacing rules, so two
    /// radios started from one song do not play the same list.</summary>
    internal const int Shortlist = 6;

    /// <summary>An act does not come back until this many other songs have played.</summary>
    internal const int ArtistGap = 5;

    /// <summary>A record does not come back until this many other songs have played.</summary>
    internal const int RecordGap = 12;

    /// <summary>Years apart at which two songs count as a different era. Closer than this, the era term grows in
    /// a straight line from 0.</summary>
    internal const double EraSpan = 15;

    /// <summary>What a term contributes when either song lacks the fact it compares: halfway between a match and
    /// the furthest apart. Nought would make unmeasured or undated songs look like perfect matches and push them to
    /// the front, the same reason <c>MoodFlow.UnknownKeyCost</c> gives.</summary>
    internal const double Unknown = 0.5;

    /// <summary>The genre term for two shelves in one family (<see cref="Genres.Related"/>): Rock and Metal, Jazz and
    /// Blues.</summary>
    internal const double RelatedGenre = 0.5;

    /// <summary>How much of the mood term the shared mood lists decide. The rest is the distance between the
    /// readings.</summary>
    internal const double MoodListShare = 0.25;

    /// <summary>How much tempo counts against mood, era and genre, which count 1 each. Half: tempo is a factor
    /// and not a requirement. It also covers a fifth of a real collection, where mood covers nearly all of it.</summary>
    internal const double TempoWeight = 0.5;

    /// <summary>How far apart two tempos are when they count as far apart: a fifth, as a ratio. Closer than that the
    /// term grows in a straight line on a log scale, so 100 and 110 BPM are as far apart as 140 and 154.</summary>
    internal static readonly double TempoSpan = Math.Log2(1.2);

    /// <summary>Every this many songs the radio queues, one is a favourite (<see cref="RadioCrate.IsFavourite"/>)
    /// where one fits. Counted from the song the radio started from, so the fifth, the tenth and so on.</summary>
    public const int FavouriteEvery = 5;

    /// <summary>How close a favourite must be to be played: among this share of the songs the radio could play now,
    /// closest first. Never fewer than <see cref="Shortlist"/>, so a small collection is not held to a handful.</summary>
    internal const double FavouriteFit = 0.05;

    /// <summary>The genre term for two songs on one shelf whose styles are both known and are kin
    /// (<see cref="StyleFamilies"/>): downtempo and trip hop, baroque and classical period.</summary>
    internal const double KinStyle = 0.3;

    /// <summary>The genre term for two songs on one shelf whose styles are both known and have nothing in common:
    /// lo-fi beats and electro. Further than <see cref="RelatedGenre"/>, because a shelf as broad as Electronic /
    /// Dance holds music further apart than Rock and Metal are.</summary>
    internal const double OtherStyle = 0.6;

    /// <summary>The genre term for two songs on one shelf when only one of them has a style. As far as kin: from a
    /// trip hop song, another trip hop song comes clearly before one nobody has filed, which may be anything the
    /// shelf holds, and an unfiled one still comes before electro.</summary>
    internal const double OneStyle = KinStyle;

    /// <summary>How many songs must be left, beyond the ones a pick asks for, before songs whose style clashes with
    /// the song playing are left out (see <see cref="WithoutClashes"/>). A full shortlist, so a pick still has a
    /// choice.</summary>
    internal const int ClashSpare = Shortlist;

    /// <summary>What a live recording adds to its score when the song the radio started from is not live (see
    /// <see cref="IsLive"/>). About as much as a song a decade away in era: a live song still comes when nothing
    /// in the studio is near, but a studio recording that fits comes first. Nothing is added when the radio
    /// started from a live song, so a live album goes on as a live mixtape.</summary>
    internal const double LiveApart = 0.3;

    /// <summary>
    /// Styles that sit next to one another, across the shelves they hang off. Two songs whose styles are in one family
    /// are kin (<see cref="KinStyle"/>). A style may be in more than one family.
    ///
    /// <para>The families follow how the music is played, not how it is filed. The Electronic / Dance shelf holds
    /// music for sitting down and music for a club, and no shelf name says which. The classical periods are
    /// families with their neighbours, so Bach leads to Handel before Mahler.</para>
    ///
    /// <para>Every shelf has its families, because two styles that share none clash, and a song whose style clashes
    /// with the one playing is left out (<see cref="WithoutClashes"/>). Hard rock without a family would clash with
    /// arena rock. A style with no family, Nederpop or Schlager, is kin only to itself.</para>
    /// </summary>
    internal static readonly string[][] StyleFamilies =
    [
        // Electronic music for sitting down.
        ["Downtempo", "Trip Hop", "Chillout", "Chillwave", "Lo-fi Beats", "Ambient", "New Age", "Folktronica",
         "Vaporwave", "Ambient Techno", "Ambient Classical"],
        // For listening rather than dancing, but not to sleep to.
        ["IDM", "Leftfield", "Electronica", "Progressive Electronic", "Downtempo", "Trip Hop"],
        // For a club.
        ["Electro", "Electro House", "Trance", "Psytrance", "Eurodance", "Italo Dance", "Hardstyle", "Future Bass",
         "Alternative Dance", "Deep House", "Tech House", "Progressive House", "Future House", "French House",
         "Garage House", "Tribal House", "Acid House", "Chicago House", "Afro House", "Euro House",
         "Detroit Techno", "Minimal Techno", "Acid Techno", "Hard Techno", "Industrial Techno"],
        // Breaks and bass.
        ["Drum and Bass", "Jungle", "Breakbeat", "Dubstep", "Post-Dubstep", "UK Garage", "Speed Garage", "Future Bass"],
        // The dark end.
        ["Synthwave", "Dark Wave", "Ethereal Wave", "Minimal Wave", "Witch House", "Industrial", "Post-Industrial",
         "Dark Ambient"],
        // Pop for dancing, and the dance music it shares a floor with.
        ["Dance-Pop", "Electropop", "Europop", "Synth-Pop", "Teen Pop", "Bubblegum Pop", "Hyperpop", "K-Pop", "J-Pop",
         "Eurodance", "Italo Dance", "Hi-NRG", "Italo-Disco"],
        // Pop to sit back to.
        ["Ballad", "Easy Listening", "Adult Contemporary", "Traditional Pop", "Sophisti-Pop", "Soft Rock",
         "Chanson Française"],
        // Pop for listening closely.
        ["Art Pop", "Progressive Pop", "Psychedelic Pop", "Baroque Pop", "Alternative Pop", "Sophisti-Pop"],
        // ── Rock ──
        // Classic and hard rock: big riffs, big choruses.
        ["Classic Rock", "Hard Rock", "Arena Rock", "AOR", "Blues Rock", "Southern Rock", "Glam Rock", "Heartland Rock",
         "Pub Rock", "Roots Rock", "Glam Metal", "Heavy Metal",
         // Folk sits beside it: the same records, the same years, often the same players.
         "Folk Rock", "Contemporary Folk", "Folk Pop", "Folk Revival", "Traditional Folk", "Alternative Folk",
         "Indie Folk", "Singer-Songwriter"],
        // Soft, acoustic and rootsy.
        ["Soft Rock", "Acoustic Rock", "Folk Rock", "Country Rock", "Piano Rock", "Singer-Songwriter", "Heartland Rock",
         "Roots Rock", "AOR", "Americana", "Folk Pop", "Contemporary Folk", "Folk Revival", "Traditional Folk",
         "Celtic Folk", "Alternative Folk", "Indie Folk"],
        // Rock with a pop song in it.
        ["Pop Rock", "Power Pop", "Soft Rock", "Piano Rock", "AOR", "Britpop", "Jangle Pop", "Indie Pop", "Pop Punk"],
        // Alternative and grunge.
        ["Alternative Rock", "Grunge", "Post-Grunge", "Indie Rock", "Britpop", "Garage Rock Revival", "Noise Rock",
         "Alternative Metal", "Shoegaze", "Post-Punk Revival", "Emo", "Alternative Punk"],
        // Progressive, psychedelic and art rock.
        ["Progressive Rock", "Psychedelic Rock", "Art Rock", "Space Rock", "Krautrock", "Experimental Rock", "Post-Rock",
         "Symphonic Rock", "Acid Rock", "Neo-Psychedelia", "Progressive Metal", "Avant-Prog", "Art Pop",
         "Progressive Pop"],
        // The eighties' new wave, with its synthesisers.
        ["New Wave", "New Romantic", "Synth-Pop", "Post-Punk", "Gothic Rock", "Dance-Rock", "Dark Wave", "Minimal Wave",
         "Sophisti-Pop"],
        // Rock and roll and the garage it grew up in.
        ["Rock and Roll", "Rockabilly", "Surf Rock", "Merseybeat", "Garage Rock", "Doo-Wop", "Proto-Punk"],
        // Heavy, with machines or a rapper in it.
        ["Industrial Rock", "Industrial Metal", "Nu Metal", "Rap Rock", "Alternative Metal", "Industrial", "Electropunk",
         "Funk Rock"],

        // ── Metal ──
        ["Heavy Metal", "Glam Metal", "Power Metal", "Speed Metal", "Thrash Metal", "Pop Metal", "Symphonic Metal",
         "Gothic Metal", "Folk Metal"],
        ["Death Metal", "Black Metal", "Deathcore", "Metalcore", "Thrash Metal", "Speed Metal", "Djent"],
        ["Doom Metal", "Sludge Metal", "Post-Metal", "Drone", "Post-Rock"],
        ["Nu Metal", "Alternative Metal", "Industrial Metal", "Rap Rock", "Metalcore", "Post-Grunge", "Djent"],

        // ── Punk ──
        ["Punk Rock", "Pop Punk", "Skate Punk", "Ska Punk", "Street Punk", "Hardcore Punk", "Garage Punk", "Proto-Punk",
         "Riot Grrrl", "Crust Punk", "Powerviolence"],
        ["Post-Punk", "Post-Hardcore", "Emo", "Screamo", "Alternative Punk"],
        ["Folk Punk", "Anti-Folk"],

        // ── Indie ──
        ["Indie Pop", "Bedroom Pop", "Dream Pop", "Jangle Pop", "Chamber Pop", "Lo-fi", "Indie Folk", "Indietronica",
         "Alternative Pop"],
        ["Indie Rock", "Shoegaze", "Slowcore", "Neo-Psychedelia", "Post-Punk Revival", "Garage Rock Revival", "Lo-fi",
         "Dream Pop"],

        // ── Hip-hop and rap ──
        // Thoughtful, sampled, jazzy.
        ["Conscious Hip Hop", "Alternative Hip Hop", "Jazz Rap", "Political Hip Hop", "Boom Bap", "Golden Age Hip Hop",
         "East Coast Hip Hop", "Turntablism", "Experimental Hip Hop", "Contemporary R&B"],
        // Hard, from the streets of one coast or another.
        ["Hardcore Hip Hop", "Gangsta Rap", "East Coast Hip Hop", "West Coast Hip Hop", "G-Funk", "Southern Hip Hop",
         "Crunk", "Drill", "Grime"],
        // Melodic, sung half the time, on trap drums.
        ["Pop Rap", "Cloud Rap", "Emo Rap", "Trap Soul", "Plugg", "Rage", "Phonk", "Southern Hip Hop", "Trap Latino"],
        ["Experimental Hip Hop", "Industrial Hip Hop", "Alternative Hip Hop", "Cloud Rap"],
        ["Miami Bass", "Crunk", "Jersey Club"],

        // ── R&B, soul and funk ──
        // R&B now.
        ["Contemporary R&B", "Alternative R&B", "Trap Soul", "Neo-Soul", "New Jack Swing", "Quiet Storm", "Pop Soul"],
        // Smooth, late at night.
        ["Neo-Soul", "Smooth Soul", "Quiet Storm", "Philly Soul", "Pop Soul", "Blue-Eyed Soul", "Soul Jazz", "Acid Jazz",
         "Alternative R&B", "Singer-Songwriter", "Contemporary R&B"],
        // The soul of the sixties and seventies.
        ["Motown", "Northern Soul", "Southern Soul", "Deep Soul", "Philly Soul", "Rhythm and Blues", "Doo-Wop",
         "Blue-Eyed Soul", "Psychedelic Soul", "Gospel Soul", "Pop Soul", "Contemporary R&B"],
        // Funk, and what it became in the eighties.
        ["P-Funk", "Boogie", "Synth Funk", "Minneapolis Sound", "Go-Go", "Jazz-Funk", "Psychedelic Soul", "Funk Rock",
         "Electro-Disco", "Post-Disco", "New Jack Swing", "Contemporary R&B"],

        // ── Disco and the club ──
        ["Euro-Disco", "Italo-Disco", "Hi-NRG", "Electro-Disco", "Nu Disco", "Post-Disco", "Boogie", "French House",
         "Eurodance"],
        ["Big Room", "Future Rave", "Electro House", "Progressive House", "Trance"],
        ["Electroclash", "Electro", "Electropunk", "Synth-Pop", "Electropop"],
        ["Deconstructed Club", "Jersey Club", "Hyperpop", "Bubblegum Bass", "Glitch"],

        // ── Jazz and blues ──
        // Sung, swung and easy.
        ["Vocal Jazz", "Swing", "Big Band", "Jazz Pop", "Smooth Jazz", "Traditional Pop", "Easy Listening", "Cool Jazz",
         "Bossa Nova"],
        ["Bebop", "Hard Bop", "Cool Jazz", "Modal Jazz", "Soul Jazz", "Latin Jazz"],
        ["Free Jazz", "Avant-Garde Jazz", "Free Improvisation", "Modal Jazz"],
        ["Jazz Fusion", "Jazz-Funk", "Acid Jazz", "Soul Jazz", "Smooth Jazz", "Ethio-Jazz"],
        ["Swing", "Big Band", "Gypsy Jazz", "Ragtime", "Jump Blues"],
        ["Chicago Blues", "Electric Blues", "Texas Blues", "British Blues", "Blues Rock", "Jump Blues"],
        ["Delta Blues", "Country Blues", "Piedmont Blues", "Gospel Blues", "Desert Blues"],
        ["Punk Blues", "Garage Rock", "Garage Rock Revival", "Blues Rock"],

        // ── Country and folk ──
        ["Country Pop", "Bro-Country", "Country Rock", "Americana", "Folk Pop"],
        ["Honky Tonk", "Bakersfield Sound", "Outlaw Country", "Western Swing", "Bluegrass", "Americana",
         "Alternative Country", "Gothic Country", "Southern Rock"],
        ["Singer-Songwriter", "Contemporary Folk", "Folk Pop", "Indie Folk", "Alternative Folk", "Folk Rock", "Americana",
         "Acoustic Rock", "Anti-Folk", "Freak Folk", "Chamber Pop"],
        ["Traditional Folk", "Celtic Folk", "Celtic", "Folk Revival", "Neo Folk", "Bluegrass"],

        // ── Reggae, Latin and the world ──
        ["Roots Reggae", "Rocksteady", "Lovers Rock", "Dub", "Nyabinghi", "Ska", "Two-Tone", "Ska Punk"],
        ["Dancehall", "Ragga", "Reggae Fusion", "Reggaeton", "Afrobeats"],
        ["Salsa", "Merengue", "Bachata", "Cumbia", "Son Cubano"],
        ["Bossa Nova", "Samba", "MPB", "Tango", "Latin Jazz"],
        ["Reggaeton", "Trap Latino", "Latin Pop", "Dancehall"],
        ["Mariachi", "Banda", "Norteño", "Vallenato", "Cumbia"],
        ["Latin Rock", "Latin Pop"],
        ["Afrobeat", "Highlife", "Soukous", "Afrobeats", "Ethio-Jazz", "Desert Blues"],
        ["Flamenco", "Fado", "Tango"],
        ["Klezmer", "Balkan Brass"],
        ["Bhangra", "Filmi"],

        // ── Sacred ──
        ["Gospel", "Traditional Gospel", "Contemporary Gospel", "Southern Gospel", "Gospel Soul", "Gospel Blues",
         "Spirituals", "Hymns", "Worship", "Contemporary Christian"],
        ["Gregorian Chant", "Sacred Choral", "Hymns", "Medieval", "Early Music"],
        ["Christian Rock", "Christian Hip-Hop", "Contemporary Christian", "Worship"],

        // ── The screen and the stage ──
        ["Film Score", "Orchestral Score", "Television Score", "Trailer Music", "Video Game Score", "Anime Soundtrack",
         "Synth Score"],
        ["Broadway", "Film Musical", "Show Tunes", "West End", "Original Cast Recording"],
        ["Synth Score", "Synthwave", "Chiptune"],

        // ── Out at the edges ──
        ["Electroacoustic", "Musique Concrete", "Sound Collage", "Spectral Music", "Fluxus", "Field Recording", "Drone"],
        ["Harsh Noise", "Japanoise", "Noisecore", "Power Electronics", "Noise Rock"],
        ["Audiobook", "Podcast", "Interview", "Storytelling", "Poetry", "Comedy"],

        // The classical periods, each with its neighbours.
        ["Medieval", "Renaissance", "Early Music", "Baroque"],
        ["Baroque", "Classical Period"],
        ["Classical Period", "Romantic Classical"],
        ["Romantic Classical", "Impressionist"],
        ["Impressionist", "Contemporary Classical", "Modern Classical", "Minimalism", "Post-Minimalism",
         "Ambient Classical"],
    ];

    private static readonly Dictionary<string, int[]> FamiliesOf = StyleFamilies
        .SelectMany((family, at) => family.Select(style => (style, at)))
        .GroupBy(p => p.style, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(p => p.at).ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The shelves of classical music. A classical record's year is when it was recorded, which says nothing
    /// about when the music is from, so the era term is not asked between two of them: the style says it instead
    /// (Baroque, Romantic). A 1987 Mozart is no closer to a 1987 Bach than a 2000 one is.</summary>
    private static readonly HashSet<string> ClassicalShelves =
        new(["Classical", "Neo-Classical", "Opera"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The next songs, closest first in the order they should be queued.
    /// </summary>
    /// <param name="seed">The song playing now, as a relative path.</param>
    /// <param name="origin">The song the radio was started from.</param>
    /// <param name="inQueue">Every song in the queue now, whoever put it there. Never picked.</param>
    /// <param name="history">Every song this radio has queued, oldest first. Not picked again, and the last few
    /// decide which acts and records are too recent. When the crate has fewer unplayed songs left than
    /// <paramref name="count"/>, the older half may be picked again.</param>
    /// <param name="random">Chooses within each shortlist. Stated by the caller so a test can fix it.</param>
    /// <returns>At most <paramref name="count"/> relative paths. Empty when neither the seed nor the origin is in the
    /// crate, since there is then nothing to compare against.</returns>
    public static IReadOnlyList<string> Pick(
        RadioCrate crate, string seed, string origin,
        IReadOnlySet<string> inQueue, IReadOnlyList<string> history, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(crate);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(inQueue);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(random);

        if (count <= 0 || (crate.Find(seed) ?? crate.Find(origin)) is not { } playing)
            return [];

        var scorer = new Scorer(playing, crate.Find(origin) ?? playing);

        var ranked = Ranked(crate, scorer, Blocked(crate, inQueue, history, seed, origin));
        if (ranked.Count < count && history.Count > 1)
        {
            // The radio has queued nearly everything it can reach. The older half of its history may come back;
            // the newer half and the queue stay out.
            ranked = Ranked(crate, scorer, Blocked(crate, inQueue, history.Skip(history.Count / 2).ToList(), seed, origin));
        }

        ranked = WithoutClashes(ranked, playing, count);

        var recent = history.TakeLast(RecordGap).Select(crate.Find).OfType<RadioSong>().ToList();
        recent.Add(playing);

        var picked = new List<string>(count);
        var takenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenSongs = new HashSet<string>(StringComparer.Ordinal);
        var shortlist = new List<RadioSong>(Shortlist);

        // How far down the ranking a favourite may sit and still be played.
        var fitDepth = Math.Min(ranked.Count, Math.Max(Shortlist, (int)Math.Ceiling(ranked.Count * FavouriteFit)));

        while (picked.Count < count)
        {
            shortlist.Clear();

            // The history holds the song the radio started from, so this counts that song as the first.
            var slot = history.Count + picked.Count;
            if (slot > 0 && slot % FavouriteEvery == 0)
                FillFavourites();

            if (shortlist.Count == 0)
                Fill(spaced: true);

            // Nothing passes the spacing rules (a small collection, or one act's shelf): spacing gives way rather
            // than the radio going quiet.
            if (shortlist.Count == 0)
                Fill(spaced: false);

            if (shortlist.Count == 0)
                break;

            var pick = shortlist[random.Next(shortlist.Count)];
            picked.Add(pick.Path);
            takenPaths.Add(pick.Path);
            takenSongs.Add(pick.Song);
            recent.Add(pick);
        }

        return picked;

        // The favourites among the closest songs, at most a shortlist of them. Spaced like any pick: a favourite is
        // not a reason to play the same act twice running.
        void FillFavourites()
        {
            for (var i = 0; i < fitDepth; i++)
            {
                var song = ranked[i];
                if (!crate.IsFavourite(song) || takenPaths.Contains(song.Path) || takenSongs.Contains(song.Song)
                    || !IsSpaced(song, recent))
                    continue;

                shortlist.Add(song);
                if (shortlist.Count == Shortlist)
                    return;
            }
        }

        void Fill(bool spaced)
        {
            foreach (var song in ranked)
            {
                if (takenPaths.Contains(song.Path) || takenSongs.Contains(song.Song))
                    continue;
                if (spaced && !IsSpaced(song, recent))
                    continue;

                shortlist.Add(song);
                if (shortlist.Count == Shortlist)
                    return;
            }
        }
    }

    /// <summary>How far apart two songs are in mood, era, genre and tempo, from 0 to 1. The first three count
    /// equally and tempo counts <see cref="TempoWeight"/> of one of them.</summary>
    internal static double Apart(RadioSong song, RadioSong to) =>
        Apart(song, to, RelatedTo(to.Genre));

    /// <summary>What a pick is ranked by, lowest first. See <see cref="Scorer"/>.</summary>
    internal static double Score(RadioSong song, RadioSong playing, RadioSong origin) =>
        new Scorer(playing, origin).Score(song);

    private static double Apart(RadioSong song, RadioSong to, IReadOnlySet<string> related) =>
        (MoodApart(song, to) + EraApart(song, to) + GenreApart(song, to, related)
         + (TempoWeight * TempoApart(song.Bpm, to.Bpm)))
        / (3 + TempoWeight);

    /// <summary>The era term, which is not asked between two classical records: see
    /// <see cref="ClassicalShelves"/>. Agreement rather than <see cref="Unknown"/>, so a classical radio stays with
    /// classical music and lets the style and the mood choose within it.</summary>
    private static double EraApart(RadioSong song, RadioSong to) =>
        song.Genre is { } a && to.Genre is { } b && ClassicalShelves.Contains(a) && ClassicalShelves.Contains(b)
            ? 0
            : EraApart(song.Year, to.Year);

    /// <summary>
    /// How far apart two tempos are, from 0 to 1, with a doubled or halved tempo counted as the same one.
    ///
    /// <para>The same tempo is counted 70 by one tag and 140 by another, and nothing in the audio settles which (see
    /// <c>Analysis/TempoOctave</c>). So the ratio is folded into one octave: 100 against 200 is 0, and the furthest
    /// apart are tempos half an octave apart, 100 against 141. <see cref="Unknown"/> where either has no tempo, which
    /// is most songs.</para>
    /// </summary>
    internal static double TempoApart(double? a, double? b)
    {
        if (a is not > 0 || b is not > 0)
            return Unknown;

        var octaves = Math.Abs(Math.Log2(a.Value / b.Value)) % 1;
        var folded = Math.Min(octaves, 1 - octaves);
        return Math.Min(folded / TempoSpan, 1);
    }

    /// <summary>The distance between the readings, plus whether the two share a mood list. <see cref="Unknown"/>
    /// when either is unmeasured: an unmeasured song is in no mood list because nobody has measured it, not because
    /// it sits in the middle.</summary>
    internal static double MoodApart(RadioSong a, RadioSong b)
    {
        if (a.Sound is not { } x || b.Sound is not { } y)
            return Unknown;

        var sound = Math.Sqrt(
            Squared(x.Brightness - y.Brightness) + Squared(x.Onsets - y.Onsets) + Squared(x.Dynamics - y.Dynamics))
            / Math.Sqrt(3);

        // Both in no list is agreement: both sit in the middle of their genre.
        var lists = (a.Moods.Count == 0 && b.Moods.Count == 0) || a.Moods.Overlaps(b.Moods) ? 0.0 : 1.0;

        return ((1 - MoodListShare) * sound) + (MoodListShare * lists);
    }

    internal static double EraApart(int a, int b) =>
        a > 0 && b > 0 ? Math.Min(Math.Abs(a - b) / EraSpan, 1) : Unknown;

    /// <summary>
    /// How far apart two songs' genres are: the shelves first, then the styles within them.
    ///
    /// <para>One shelf is not enough. Electronic / Dance holds lo-fi beats, trip hop and electro, and a radio
    /// started on one of them should not play the others. So on one shelf the styles decide: one in common is a
    /// match, kin styles are close, and styles with nothing in common are further apart than two related shelves.
    /// Across shelves, a style in common (or kin) brings two songs as close as kin, so Electro House on the House
    /// shelf meets Electro on the Electronic one.</para>
    /// </summary>
    internal static double GenreApart(RadioSong song, RadioSong to, IReadOnlySet<string> related)
    {
        var styles = StyleApart(song.Styles, to.Styles);

        if (song.Genre is not { } genre || to.Genre is not { } other)
            return styles is { } known && known <= KinStyle ? known : Unknown;

        if (string.Equals(genre, other, StringComparison.OrdinalIgnoreCase))
            return styles ?? (song.Styles.Count == 0 && to.Styles.Count == 0 ? 0 : OneStyle);

        var shelves = related.Contains(genre) ? RelatedGenre : 1;
        return styles is { } near && near <= KinStyle ? Math.Min(shelves, near) : shelves;
    }

    /// <summary>0 for a style in common, <see cref="KinStyle"/> for kin, <see cref="OtherStyle"/> for neither. Null
    /// when either song has no style, which is not the same as having different ones.</summary>
    internal static double? StyleApart(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return null;
        if (a.Overlaps(b))
            return 0;

        foreach (var x in a)
        {
            if (!FamiliesOf.TryGetValue(x, out var ofX))
                continue;
            foreach (var y in b)
                if (FamiliesOf.TryGetValue(y, out var ofY) && ShareOne(ofX, ofY))
                    return KinStyle;
        }

        return OtherStyle;

        // Asked once per candidate per pick, over a few thousand candidates, so no LINQ.
        static bool ShareOne(int[] x, int[] y)
        {
            foreach (var i in x)
                foreach (var j in y)
                    if (i == j)
                        return true;
            return false;
        }
    }

    private static IReadOnlySet<string> RelatedTo(string? genre) =>
        new HashSet<string>(Genres.Related(genre), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a song is a live recording: its record is filed as a live one, or its title or its album says so.
    ///
    /// <para>A title says so the way the album editor's (Live) note reads it (<see cref="TitleNote.Live"/>):
    /// "(Live)", "[Live at Wembley]", " - Unplugged". An album says so in those ways too, or by starting with the
    /// word: "Live", "Live at the BBC", "Live in Tokyo", and by "Unplugged" anywhere in it. "Live Forever" and "Long
    /// Live" are not live records, so the word alone is not enough. A "(Live Mix)" is a mix and not a concert.</para>
    /// </summary>
    public static bool IsLive(string? title, string? album, string? releaseType)
    {
        if (ReleaseTypes.FromStored(releaseType) == ReleaseType.Live)
            return true;
        if (SaysLive(title))
            return true;
        return album is { Length: > 0 } && (SaysLive(album) || LiveAlbum.IsMatch(album));

        static bool SaysLive(string? text) => TitleNote.Live.Says(text) && !LiveMix.IsMatch(text!);
    }

    private static readonly System.Text.RegularExpressions.Regex LiveMix = new(
        @"\blive\s+(?:mix|edit|dub)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex LiveAlbum = new(
        @"^\s*live\s*(?:$|[:\-–—(\[]|(?:at|in|from|on|aus|à|en|in)\b)|\bunplugged\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="ranked"/> without the songs whose style clashes with the song playing, while enough are left.
    ///
    /// <para>A style says what kind of music a song is more plainly than its mood or its year, so a clash is not
    /// weighed against the rest: a ballad does not follow dance-pop because it is from the same year. Songs that
    /// share a style with the song playing stay, so do kin styles (<see cref="StyleFamilies"/>), and so do songs
    /// with no style, which nobody has filed and which are not known to clash. A song playing with no style leaves
    /// the ranking as it is.</para>
    ///
    /// <para>Not a wall: when fewer than the pick plus <see cref="ClashSpare"/> are left, the ranking stays whole, so
    /// a small collection or a rare style still gets a radio.</para>
    /// </summary>
    internal static List<RadioSong> WithoutClashes(List<RadioSong> ranked, RadioSong playing, int count)
    {
        if (playing.Styles.Count == 0)
            return ranked;

        var kept = ranked.FindAll(song => StyleApart(song.Styles, playing.Styles) is not OtherStyle);
        return kept.Count >= count + ClashSpare ? kept : ranked;
    }

    private static double Squared(double value) => value * value;

    /// <summary>Whether the act and the record have been away long enough. An empty credit names nobody, so a shelf
    /// of untagged files is not spaced as one act.</summary>
    private static bool IsSpaced(RadioSong song, IReadOnlyList<RadioSong> recent)
    {
        for (var i = recent.Count - 1; i >= 0 && i >= recent.Count - RecordGap; i--)
        {
            var other = recent[i];
            if (string.Equals(other.Folder, song.Folder, StringComparison.OrdinalIgnoreCase))
                return false;

            var withinArtistGap = i >= recent.Count - ArtistGap;
            if (withinArtistGap && song.Artist.Length > 0
                && string.Equals(other.Artist, song.Artist, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>The paths and the songs that may not be picked: the queue, the history handed in, the seed and the
    /// origin, and any other copy of one of those songs.</summary>
    private static (HashSet<string> Paths, HashSet<string> Songs) Blocked(
        RadioCrate crate, IReadOnlySet<string> inQueue, IReadOnlyList<string> history, string seed, string origin)
    {
        var paths = new HashSet<string>(inQueue, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(history);
        paths.Add(seed);
        paths.Add(origin);

        var songs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (crate.Find(path) is { } song)
                songs.Add(song.Song);

        return (paths, songs);
    }

    /// <summary>Every song that may be picked, closest first. The path breaks a tie so one crate and one seed always
    /// rank the same way.</summary>
    private static List<RadioSong> Ranked(
        RadioCrate crate, Scorer scorer, (HashSet<string> Paths, HashSet<string> Songs) blocked)
    {
        var scored = new List<(RadioSong Song, double Score)>();
        foreach (var song in crate.Songs)
        {
            if (!song.Offered || blocked.Paths.Contains(song.Path) || blocked.Songs.Contains(song.Song))
                continue;
            scored.Add((song, scorer.Score(song)));
        }

        scored.Sort((a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : StringComparer.OrdinalIgnoreCase.Compare(a.Song.Path, b.Song.Path);
        });

        return [.. scored.Select(s => s.Song)];
    }

    /// <summary>The distance to the song playing plus <see cref="OriginWeight"/> of the distance to the song the radio
    /// started from, and <see cref="LiveApart"/> on a live recording when the radio did not start from one. The
    /// related genres are worked out once per pick rather than once per song.</summary>
    private sealed class Scorer(RadioSong playing, RadioSong origin)
    {
        private readonly IReadOnlySet<string> _playingRelated = RelatedTo(playing.Genre);
        private readonly IReadOnlySet<string> _originRelated = RelatedTo(origin.Genre);

        public double Score(RadioSong song) =>
            Apart(song, playing, _playingRelated) + (OriginWeight * Apart(song, origin, _originRelated))
            + (song.Live && !origin.Live ? LiveApart : 0);
    }
}
