namespace RPM.Core.Library;

/// <summary>A believable stand-in music collection: several artists, a couple of albums each, real track
/// titles and running times, with no files behind it. It fills the app's screens with something to show
/// while a screen is being built or demonstrated. The user does not have to point the app at their own
/// library first.
///
/// <para>The rows are seeded straight into a mirror database (<see cref="Seed"/>). That is what the grid,
/// the tree and search read from. So the demo uses the real query path, not a mock of it. The files the
/// rows name are not on disk. A sync would read them all as deleted and empty the mirror. So the gallery
/// launch path turns sync off for the run.</para>
/// </summary>
public static class DemoLibrary
{
    private sealed record DemoAlbum(string Artist, string Album, string Genre, uint Year, string Extension, string[] Tracks);

    private static readonly DemoAlbum[] Albums =
    [
        new("Daft Punk", "Discovery", "Electronic", 2001, ".flac",
        [
            "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger",
            "Crescendolls", "Nightvision", "Superheroes", "Face to Face", "Too Long",
        ]),
        new("Boards of Canada", "Music Has the Right to Children", "IDM", 1998, ".flac",
        [
            "Wildlife Analysis", "An Eagle in Your Mind", "The Color of the Fire", "Telephasic Workshop",
            "Triangles and Rhombuses", "Sixtyten", "Turquoise Hexagon Sun", "Roygbiv", "Rue the Whirl",
        ]),
        new("Aphex Twin", "Selected Ambient Works 85-92", "Ambient Techno", 1992, ".mp3",
        [
            "Xtal", "Tha", "Pulsewidth", "Ageispolis", "i", "Green Calx",
            "Heliosphan", "We Are the Music Makers", "Schottkey 7th Path",
        ]),
        new("Justice", "Cross", "Electro", 2007, ".mp3",
        [
            "Genesis", "Let There Be Light", "D.A.N.C.E.", "Newjack", "Phantom",
            "Phantom Pt. II", "Valentine", "Tthhee Ppaarrttyy", "DVNO", "Stress",
        ]),
        new("Röyksopp", "Melody A.M.", "Downtempo", 2001, ".flac",
        [
            "So Easy", "Eple", "Sparks", "In Space", "Poor Leno",
            "A Higher Place", "Röyksopp's Night Out", "Remind Me", "She's So", "40 Years Back / Come",
        ]),
        new("The Chemical Brothers", "Surrender", "Big Beat", 1999, ".mp3",
        [
            "Music: Response", "Under the Influence", "Out of Control", "Orange Wedge", "Let Forever Be",
            "The Sunshine Underground", "Asleep from Day", "Got Glint?", "Hey Boy Hey Girl",
        ]),
        // The odd one out, on purpose. The demo catalogue is otherwise all electronic records, and two
        // features in the app look for something that is not in it. The New Year greeting does not show
        // unless the library holds "Happy New Year" by ABBA (see NewYear). The mirror ball hangs for ABBA
        // by name (see MirrorBall). Without this album the gallery can show every other screen in the app,
        // but not these two. The record is real, with its real running order, filed the way a tagger would
        // file it.
        //
        // It is WAV, not FLAC like most of the others, and that is deliberate. This is the one album a test
        // harness can put a real file behind (GalleryBootstrap writes one for the New Year track). A header
        // the harness can write from nothing is one it can write correctly. Everything that reads the
        // catalogue treats WAV as the lossless format it is.
        new("ABBA", "Super Trouper", "Pop", 1980, ".wav",
        [
            "Super Trouper", "The Winner Takes It All", "On and On and On", "Andante, Andante",
            "Me and I", "Happy New Year", "Our Last Summer", "The Piper",
            "Lay All Your Love on Me", "The Way Old Friends Do",
        ]),
    ];

    /// <summary>The demo catalogue as mirror rows. Durations and sizes are plausible but invented. The
    /// folder layout ("Artist/Album") is the same one the importer creates. So the tree and the per-folder
    /// queries group the rows exactly as they group a real library.
    /// </summary>
    public static IReadOnlyList<LibraryTrack> Tracks()
    {
        var rows = new List<LibraryTrack>();
        // A fixed clock, so the seed is the same on every run. A test can assert on it, and two runs match.
        var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        foreach (var album in Albums)
        {
            var folder = $"{album.Artist}/{album.Album}";
            for (var i = 0; i < album.Tracks.Length; i++)
            {
                var trackNo = (uint)(i + 1);
                var title = album.Tracks[i];
                var fileName = $"{trackNo:00} {Sanitize(title)}{album.Extension}";
                // FLAC files get a larger size per second than the other formats. The exact
                // numbers only have to look real.
                var duration = 150 + (i * 17) % 210;      // 2.5 to 6 minutes, varied per track
                var size = (long)(duration * (album.Extension == ".flac" ? 900_000 : 130_000));

                rows.Add(new LibraryTrack
                {
                    RelativePath = $"{folder}/{fileName}",
                    Folder = folder,
                    FileName = fileName,
                    Extension = album.Extension,
                    SizeBytes = size,
                    ModifiedUtcTicks = stamp,
                    DurationSeconds = duration,
                    Title = title,
                    Artist = album.Artist,
                    AlbumArtist = album.Artist,
                    Album = album.Album,
                    Genre = album.Genre,
                    TrackNumber = trackNo,
                    DiscNumber = 1,
                    Year = album.Year,
                });
            }
        }

        return rows;
    }

    /// <summary>Writes the demo catalogue into <paramref name="db"/>. The database must be a fresh mirror.
    /// The rows have no RPM id and no cloud state. That is what a library just imported from disk looks
    /// like.
    /// </summary>
    public static void Seed(LibraryDatabase db) => db.UpsertMany(Tracks());

    // ── large stress catalogue ──

    /// <summary>Public so a harness can ask for a size in ALBUMS. The wall's stress library asks for
    /// exactly N whole albums' worth of tracks.</summary>
    public const int TracksPerAlbum = 12;
    private const int AlbumsPerArtist = 6;

    private static readonly string[] Adjectives =
        ["Crystal", "Velvet", "Neon", "Silent", "Golden", "Electric", "Hollow", "Cosmic", "Iron",
         "Paper", "Midnight", "Glass", "Wild", "Frozen", "Scarlet", "Lunar", "Amber", "Static",
         "Marble", "Copper", "Violet", "Distant", "Broken", "Sacred"];

    private static readonly string[] Nouns =
        ["Foxes", "Circuit", "Harbour", "Machines", "Gardens", "Signal", "Wolves", "Tide", "Empire",
         "Static", "Choir", "Comet", "Union", "Ghosts", "Method", "Parade", "Anchor", "Riot",
         "Season", "Motel", "Divers", "Orchard", "System", "Echoes"];

    private static readonly string[] AlbumAdj =
        ["First", "Slow", "Late", "Endless", "Quiet", "Bright", "Northern", "Deep", "New", "Last",
         "Hidden", "Open"];

    private static readonly string[] AlbumNoun =
        ["Light", "Weather", "Motion", "Country", "Hours", "Rooms", "Waves", "Fields", "Nights", "Signals"];

    private static readonly string[] TitleWords =
        ["Drift", "Aurora", "Lowlands", "Transit", "Halcyon", "Undertow", "Nocturne", "Meridian",
         "Afterglow", "Pilot", "Cascade", "Ember", "Vertigo", "Solstice", "Mirage", "Cobalt",
         "Tremor", "Lantern", "Fathom", "Kestrel", "Monsoon", "Anthem", "Cipher", "Dovetail"];

    private static readonly string[] Genres =
        ["Electronic", "Rock", "Jazz", "Ambient", "Hip-Hop", "Folk", "Classical", "Techno", "Soul", "Indie"];

    private static readonly string[] Extensions = [".flac", ".mp3", ".m4a"];

    /// <summary>A large catalogue made by a procedure, for stress-testing the grid and the virtualized
    /// tree. It has enough artists, albums and folders to behave like a very large library, at any size asked
    /// for. It is streamed rather than built in memory, so a very large count stays light on memory. It is
    /// deterministic: the same <paramref name="count"/> always gives the same library.
    /// </summary>
    public static IEnumerable<LibraryTrack> LargeTracks(int count)
    {
        var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var perArtist = TracksPerAlbum * AlbumsPerArtist;

        for (var i = 0; i < count; i++)
        {
            var artistIdx = i / perArtist;
            var within = i % perArtist;
            var albumIdx = within / TracksPerAlbum;
            var trackNo = (uint)(within % TracksPerAlbum + 1);

            // Artist names stay unique as the count grows. Within one block of Adjectives and Nouns, every
            // pair is distinct. Past that, a block number keeps them apart.
            var a = artistIdx % Adjectives.Length;
            var n = (artistIdx / Adjectives.Length) % Nouns.Length;
            var block = artistIdx / (Adjectives.Length * Nouns.Length);
            var artist = $"{Adjectives[a]} {Nouns[n]}";
            if (block > 0)
                artist += $" {block + 1}";

            // Distinct within an artist (albumIdx picks a distinct noun), varied across artists.
            var album = $"{AlbumAdj[(artistIdx + albumIdx) % AlbumAdj.Length]} {AlbumNoun[albumIdx % AlbumNoun.Length]}";
            var ext = Extensions[artistIdx % Extensions.Length];
            var title = $"{TitleWords[(i / 7) % TitleWords.Length]} {(char)('A' + (i % 26))}";
            var folder = $"{artist}/{album}";
            var fileName = $"{trackNo:00} {Sanitize(title)}{ext}";
            var duration = 120 + (i * 13) % 250;

            yield return new LibraryTrack
            {
                RelativePath = $"{folder}/{fileName}",
                Folder = folder,
                FileName = fileName,
                Extension = ext,
                SizeBytes = (long)(duration * (ext == ".flac" ? 900_000 : 130_000)),
                ModifiedUtcTicks = stamp,
                DurationSeconds = duration,
                Title = title,
                Artist = artist,
                AlbumArtist = artist,
                Album = album,
                Genre = Genres[artistIdx % Genres.Length],
                TrackNumber = trackNo,
                DiscNumber = 1,
                Year = (uint)(1970 + (artistIdx % 55)),
            };
        }
    }

    /// <summary>Turns a track title into a name a file system accepts. It has the same shape as the
    /// importer's own namer: no slashes and no reserved punctuation.</summary>
    private static string Sanitize(string title)
    {
        var cleaned = title;
        foreach (var bad in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            cleaned = cleaned.Replace(bad, '-');
        return cleaned;
    }
}
