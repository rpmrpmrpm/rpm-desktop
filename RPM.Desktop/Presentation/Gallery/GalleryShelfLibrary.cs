#if DEBUG
using RPM.Core.Library;
using RPM.Core.Lyrics;
using RPM.Core.Metadata.Providers;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> A shelf built around real sleeves: one album per picture in a folder given on the command
/// line, with a catalogue written to go with them.
///
/// <para><b>What it is for.</b> The other two harness libraries invent their covers. That is right for
/// driving screens and wrong for photographing one. A wall of drawn shapes looks like a mock-up from
/// across the room, and the shelf is what the site is trying to show. So the covers come from a folder
/// of real photographs, and the catalogue is written to fit them.</para>
///
/// <para><b>The records are not real and do not pretend to be.</b> Every artist, album and track title
/// below is invented. Naming actual releases over stock photography would put words in the mouths of
/// people who never said them. The picture on the site would then be a claim about a catalogue, not a
/// picture of an interface.</para>
///
/// <para><b>No audio.</b> As with <see cref="GalleryWallLibrary"/>, the folders and their covers are
/// real, and the rows name files that were never written. Sync stays off for a harness run, so nothing
/// goes looking for them. Everything the wall and the album page read is present.</para> </summary>
internal static class GalleryShelfLibrary
{
    /// <summary>How wide a sleeve is kept. The wall draws them a few hundred pixels across, and the
    /// album page draws them larger. This width keeps each cover small, rather than carrying a 30 MB
    /// photograph for every record.</summary>
    private const int CoverWidth = 1000;

    /// <summary>One record: who made it, what it is called, when it was made, what it sounds like, and
    /// what is on it. Track counts differ on purpose. A shelf where every album holds exactly twelve
    /// tracks looks generated.</summary>
    private sealed record Record(string Artist, string Album, uint Year, string Genre, string[] Tracks);

    private static readonly Record[] Shelf =
    [
        new("Halden Moor", "Long Way From The Coast", 1974, "Folk Rock",
            ["Salt On The Window", "Long Way From The Coast", "Ferryman's Daughter", "Ninth Of June",
             "Harrow Lane", "The Quiet Part", "Bell And Anchor", "Winter Sets In Early"]),
        new("The Ravel Sisters", "Paper Houses", 1979, "Soul",
            ["Paper Houses", "Come Back Slow", "Tuesday Money", "What The Radio Said",
             "Hold Out Your Hands", "Every Other Sunday", "Nothing To It", "Cold Coffee", "Last Call"]),
        new("Cassio Vane", "Blue Hour Arithmetic", 1986, "Synth Pop",
            ["Blue Hour", "Arithmetic", "Static On The Line", "Neon Sunday", "Telephone Weather",
             "Small Machines", "Everything Repeats", "Signal Fade"]),
        new("Orval Beck", "The Fenland Tapes", 1971, "Blues",
            ["Fenland Blues", "Trouble At The Sluice", "Mud And Money", "Nobody's Errand",
             "Dry Season", "Peat And Iron", "The Long Drain"]),
        new("Nightpost", "Heavy Weather Service", 1993, "Alternative",
            ["Heavy Weather", "Postmark", "Radio Silence", "Undertow", "Fault Line", "Glasshouse",
             "Second Front", "All Hands", "Storm Glass", "Fair To Middling"]),
        new("Ilse Krayner", "Nocturnes For A Small Room", 1988, "Modern Classical",
            ["Nocturne I", "Nocturne II", "The Small Room", "Nocturne III", "Interval",
             "Nocturne IV", "Nocturne V", "Closing The Lid"]),
        new("Brasswork", "Twelve Bar Cathedral", 1968, "Jazz",
            ["Twelve Bar Cathedral", "Uptown Sermon", "The Long Count", "Sidewinder's Rest",
             "Brass And Rain", "Late Set", "Benediction"]),
        new("Mara Oyelaran", "Field Recordings, Vol. 2", 2004, "World",
            ["Harmattan", "Market Morning", "Field Recording (Lagos)", "Two Rivers", "Dust Road",
             "Field Recording (Ibadan)", "Homecoming", "Night Bus"]),
        new("The Kilburn Set", "Saturday Night Arithmetic", 1981, "Post Punk",
            ["Saturday Night", "Arithmetic", "Concrete Garden", "Wage Packet", "The Bus Station",
             "Nothing Doing", "Closing Time", "Monday Again"]),
        new("Voss & Delune", "The Cartographers", 1997, "Ambient",
            ["First Survey", "The Cartographers", "Contour Lines", "Blank Quarter", "Magnetic North",
             "Second Survey", "Everything Charted"]),
        new("Ruby Ashgrove", "Small Hours Radio", 1976, "Country",
            ["Small Hours Radio", "Nashville Money", "The Long Drive Home", "Two Weeks Notice",
             "Whiskey And Weather", "Somebody Else's Song", "Last One Standing", "Fold The Map"]),
        new("Petrichor", "Rain Shadow", 2011, "Shoegaze",
            ["Rain Shadow", "Wash", "Low Cloud", "Petrichor", "Overcast", "Downpour",
             "Clearing", "After The Weather"]),
        new("Emil Sandoval", "Guitarra Sin Reloj", 1983, "Latin",
            ["Guitarra Sin Reloj", "Media Noche", "El Camino Corto", "Verano Tarde", "Tres Puertas",
             "Sin Reloj", "La Vuelta"]),
        new("The Hollow Coast", "Beachcomber's Almanac", 1999, "Indie",
            ["Beachcomber", "Almanac", "Tide Tables", "Driftwood Economy", "Salt Flat",
             "The Hollow Coast", "Low Season", "Everything The Sea Returns"]),
        new("Kestrel Hall", "Machine For Living In", 2008, "Electronic",
            ["Machine For Living In", "Corridor", "Service Lift", "Open Plan", "Load Bearing",
             "Fire Door", "Vacant Possession", "Demolition Notice"]),
        new("Junie Marsh", "The Second Wife", 1972, "Soul",
            ["The Second Wife", "Ask Me Again", "Half A Reason", "Sunday Best", "Told You Twice",
             "Nothing Personal", "Let The Record Play"]),
        new("Torrent Bay", "Deepwater Salvage", 1990, "Hard Rock",
            ["Deepwater", "Salvage Rights", "The Wreck Of The Ardent", "Pressure Hull", "Black Water",
             "Diver Down", "Salvage Yard", "All Hands Lost"]),
        new("Anouk Ferre", "Chansons Pour Personne", 1985, "Chanson",
            ["Chanson Pour Personne", "Le Dernier Metro", "Pluie De Mars", "Sans Adresse",
             "Les Volets Fermes", "Encore Un Hiver", "Personne"]),
        new("The Pale Assembly", "Order Of Service", 1995, "Gothic Rock",
            ["Order Of Service", "Processional", "The Pale Assembly", "Vespers", "Cold Stone",
             "Recessional", "Amen Corner", "Last Rites"]),
        new("Bobby Ilesanmi", "Highlife Revival", 1978, "Highlife",
            ["Highlife Revival", "Lagos Sunrise", "Dance For The Elders", "Palm Wine Sunday",
             "Talking Drum", "Osun River", "Come Home Dancing"]),
        new("Slate & Sorrel", "The Kitchen Sessions", 2015, "Acoustic",
            ["Kitchen Table", "Slate & Sorrel", "Bread And Butter", "The Long Kettle", "Two Chairs",
             "Sunday Dishes", "Goodnight Kitchen"]),
        new("Meridian Drift", "Transit Of Venus", 2001, "Progressive",
            ["Transit Of Venus", "Ephemeris", "The Drift", "Occultation", "Parallax",
             "Second Contact", "Egress"]),
        new("Delia Whitmore", "Hymns For The Unconvinced", 1969, "Gospel",
            ["Hymn For The Unconvinced", "Doubting Thomas", "River Wide", "Sunday Morning Coming",
             "Take My Hand", "The Longer Road", "Come Home"]),
        new("Nine Below", "Cellar Door Policy", 1992, "Punk",
            ["Cellar Door", "Policy", "Nine Below", "Fire Exit", "Last Orders", "Bouncer",
             "Kicked Out Again", "Walk Home"]),
        new("Hyacinth Rowe", "The Glasshouse Letters", 2006, "Chamber Pop",
            ["The Glasshouse", "Letters", "Botanical", "Under Glass", "Orangery",
             "Hothouse Flower", "Winter Garden", "Sealed Envelope"]),
        new("Coalpit Lane", "Working Men's Club", 1975, "Rock",
            ["Working Men's Club", "Coalpit Lane", "Shift Work", "The Foreman's Song", "Pit Head",
             "Saturday Wages", "Clocking Off"]),
        new("Sable Court", "Velvet Arithmetic", 1984, "Funk",
            ["Velvet Arithmetic", "Uptown Count", "Sable Court", "Two Step Money", "Slow Burn",
             "Midnight Ledger", "Cash On Delivery"]),
        new("Tomas Reine", "Winter Light Studies", 2018, "Neoclassical",
            ["Winter Light I", "Study For Left Hand", "Winter Light II", "Frost Pattern",
             "Study In Grey", "Winter Light III", "Thaw"]),
        new("The Argyle Room", "After Hours Trio", 1962, "Jazz",
            ["After Hours", "The Argyle Room", "Blues For Nobody", "Three At The Bar",
             "Late Trio", "One More Chorus", "Lights Down"]),
    ];

    // ── the record the shelf is photographed PLAYING ──
    //
    // A wall of sleeves says what the library holds, and nothing about what the app does with a record.
    // So the shot the site needs has one record on: the header shows the artist's photograph, the album
    // is open on its tracklist, and the words run in the pane. All of that hangs off one row. The row
    // is named here rather than picked at random, so the picture can be taken again and come out the
    // same.
    /// <summary> One record the shelf can be photographed playing: which row it is, the words to it,
    /// and what the encyclopedias have to say about it.
    ///
    /// <para><b>Every word of it is invented</b>, like the catalogue it belongs to. See the note at the
    /// top of this file. The words are here because the panes that show them are half of what the app
    /// is for. They cannot be photographed empty. A record nobody has written about opens a Track Info
    /// with three blank cards, which is an honest picture of a library nobody has heard of, and a
    /// useless picture of the pane.</para>
    ///
    /// <para>There is more than one record because the site needs more than one picture. A rebuild
    /// between two screenshots would put two versions of the app on the same page. Every record listed
    /// here gets a playable file and its papers on the same run. So any of them can be played and
    /// photographed without touching this file again.</para> </summary>
    private sealed record Feature(
        string Artist,
        string Album,
        string Track,
        string Lrc,
        ArtistProfile Facts,
        AlbumProfile Pressing,
        EncyclopediaArticle OnTheArtist,
        EncyclopediaArticle OnTheAlbum);

    private static readonly Feature[] Featured =
    [
        // The whole-window shot. The artist's photograph is also the window's background. See
        // Portraits.
        new("Kestrel Hall", "Machine For Living In", "Corridor",
            """
            [00:14.40]Twelfth floor, the lights come on before I do
            [00:22.80]Somebody else's morning, running early
            [00:31.20]I know this hallway better than I know the street
            [00:39.60]Every door the same door
            [00:52.00]Keep walking, keep walking
            [01:00.40]The building breathes and I breathe with it
            [01:08.80]Concrete, glass, and a light that never sleeps
            [01:17.20]Every floor the same floor
            [01:34.00]I could find my way out with my eyes closed
            [01:42.40]I have not tried it yet
            [01:55.60]Keep walking, keep walking
            [02:04.00]Corridor
            """,
            new ArtistProfile(
                Mbid: null,
                Name: "Kestrel Hall",
                Disambiguation: "British electronic producer",
                Kind: "Person",
                Country: "GB",
                Area: "United Kingdom",
                Began: "1998",
                Genres: ["Electronic", "Ambient techno", "Downtempo"],
                Links: [new ReferenceLink(ReferenceSite.Wikipedia, "https://en.wikipedia.org/wiki/Kestrel_Hall")]),
            new AlbumProfile(
                Mbid: null,
                Title: "Machine For Living In",
                Artist: "Kestrel Hall",
                FirstReleased: "2008-04",
                PrimaryType: "Album",
                Genres: ["Electronic", "Ambient techno"]),
            new EncyclopediaArticle(
                "en", "Kestrel Hall",
                """
                Kestrel Hall is the recording name of a British electronic musician who has released records since 1998. The name is taken from the tower block in south London where the first tracks were made, and the work has stayed close to that subject ever since: buildings, the rooms in them, and the hours people keep inside them.

                Machine For Living In (2008) is the best known of the records, and the one usually credited with turning a club project into a listening one. It was written over two winters in an empty office floor rented by the week.
                """,
                "https://en.wikipedia.org/wiki/Kestrel_Hall"),
            new EncyclopediaArticle(
                "en", "Machine For Living In",
                """
                Machine For Living In is the fourth studio album by the British electronic musician Kestrel Hall, released in April 2008. Its title takes Le Corbusier at his word, and every track on it is named for a part of a building.
                """,
                "https://en.wikipedia.org/wiki/Machine_For_Living_In")),

        // The Track Info pane on its own, for the section about knowing what you are listening to.
        new("Ruby Ashgrove", "Small Hours Radio", "The Long Drive Home",
            """
            [00:11.20]The radio's still on, nobody's talking
            [00:18.60]Just a map on the seat and a county to cross
            [00:26.00]Every mile marker counting me down
            [00:33.40]On the long drive home
            [00:44.80]Coffee gone cold in a paper cup
            [00:52.20]Headlights combing the dark ahead
            [00:59.60]Somebody's porch light left on for nobody
            [01:07.00]On the long drive home
            [01:18.40]I have been further out and stayed away longer
            [01:25.80]And every time the road knows the way back
            [01:33.20]Small hours radio keeping me company
            [01:40.60]On the long drive home
            """,
            new ArtistProfile(
                Mbid: null,
                Name: "Ruby Ashgrove",
                Disambiguation: "American country singer",
                Kind: "Person",
                Country: "US",
                Area: "United States",
                Began: "1948",
                Genres: ["Country", "Country rock", "Folk"],
                // The doors out. Wikipedia is NOT one of them: the links box leaves it out on purpose
                // (see TrackInfoView.ElsewhereSites), because the artist and album cards above the box
                // already link to that page. With only the encyclopedia link staged, the box has
                // nothing to show and collapses. The pane this shelf exists to photograph then had no
                // socials section in the picture.
                //
                // These are the sites that box shows, in its own order. The names are invented, like
                // the rest of the record, and the links are reachable only from the harness.
                Links: [
                    new ReferenceLink(ReferenceSite.Wikipedia, "https://en.wikipedia.org/wiki/Ruby_Ashgrove"),
                    new ReferenceLink(ReferenceSite.OfficialSite, "https://rubyashgrove.com"),
                    new ReferenceLink(ReferenceSite.Discogs, "https://www.discogs.com/artist/ruby-ashgrove"),
                    new ReferenceLink(ReferenceSite.Bandcamp, "https://rubyashgrove.bandcamp.com"),
                    new ReferenceLink(ReferenceSite.Facebook, "https://www.facebook.com/rubyashgrove"),
                    new ReferenceLink(ReferenceSite.X, "https://x.com/rubyashgrove"),
                    new ReferenceLink(ReferenceSite.Instagram, "https://www.instagram.com/rubyashgrove"),
                ]),
            new AlbumProfile(
                Mbid: null,
                Title: "Small Hours Radio",
                Artist: "Ruby Ashgrove",
                FirstReleased: "1976-09",
                PrimaryType: "Album",
                Genres: ["Country", "Country rock"]),
            new EncyclopediaArticle(
                "en", "Ruby Ashgrove",
                """
                Ruby Ashgrove (born 1948) is an American country singer and songwriter, best known for the records she made in Nashville in the middle of the 1970s. She began playing the bars along the highway out of Bakersfield at seventeen, and was signed after a session player heard her sing between sets.

                Her third album, Small Hours Radio (1976), was written almost entirely on the road and recorded in eleven days. It remains the record she is asked about, and the title song has been recorded by more than forty other singers.
                """,
                "https://en.wikipedia.org/wiki/Ruby_Ashgrove"),
            new EncyclopediaArticle(
                "en", "Small Hours Radio",
                """
                Small Hours Radio is the third studio album by the American country singer Ruby Ashgrove, released in September 1976. Written during a year of touring and recorded largely live in the studio, it was her first record to reach the country top twenty.
                """,
                "https://en.wikipedia.org/wiki/Small_Hours_Radio")),
    ];

    /// <summary> Which portrait belongs to whom. One picture per artist, taken from the folder given on
    /// the command line and matched by file name.
    ///
    /// <para>The pictures are assigned one by one, not dealt round. A photograph says who made the
    /// record. A guitarist in evening sun behind the folk album, and a red-lit DJ behind the electronic
    /// one, are the difference between a shelf and a shuffle.</para>
    ///
    /// <para>Every artist on the shelf has one. Home's banner picks its records at random on each
    /// launch. A banner record whose artist has no photograph draws an empty panel. With only some
    /// artists photographed, most launches would show a half-empty banner. The pictures are in
    /// site/portraits (sources.json there names each photographer). They are chosen for warm light,
    /// because the website's palette is warm only.</para> </summary>
    /// <remarks>The featured artist's picture is also the WINDOW'S BACKGROUND. For the record in the
    /// whole-window shot, that is what decides which picture is used. The header blurs a second copy of
    /// the photograph across the whole app (see ArtDisplay's wash), so the record being played paints
    /// everything under it. A bright outdoor picture makes the collection look pale. A black-and-white
    /// picture drains the colour from the whole interface. Both make the screenshot look like a
    /// different app. Kestrel Hall's red-lit portrait keeps the window dark and warm, which is where
    /// the app's own palette already lives.</remarks>
    private static readonly (string Artist, string Picture)[] Portraits =
    [
        ("Halden Moor", "halden-moor"),
        ("The Ravel Sisters", "the-ravel-sisters"),
        ("Cassio Vane", "cassio-vane"),
        ("Orval Beck", "orval-beck"),
        ("Nightpost", "nightpost"),
        ("Ilse Krayner", "ilse-krayner"),
        ("Brasswork", "brasswork"),
        ("Mara Oyelaran", "mara-oyelaran"),
        ("The Kilburn Set", "the-kilburn-set"),
        ("Voss & Delune", "voss-and-delune"),
        ("Ruby Ashgrove", "ruby-ashgrove"),
        ("Petrichor", "petrichor"),
        ("Emil Sandoval", "emil-sandoval"),
        ("The Hollow Coast", "the-hollow-coast"),
        ("Kestrel Hall", "kestrel-hall"),
        ("Junie Marsh", "junie-marsh"),
        ("Torrent Bay", "torrent-bay"),
        ("Anouk Ferre", "anouk-ferre"),
        ("The Pale Assembly", "the-pale-assembly"),
        ("Bobby Ilesanmi", "bobby-ilesanmi"),
        ("Slate & Sorrel", "slate-and-sorrel"),
        ("Meridian Drift", "meridian-drift"),
        ("Delia Whitmore", "delia-whitmore"),
        ("Nine Below", "nine-below"),
        ("Hyacinth Rowe", "hyacinth-rowe"),
        ("Coalpit Lane", "coalpit-lane"),
        ("Sable Court", "sable-court"),
        ("Tomas Reine", "tomas-reine"),
        ("The Argyle Room", "the-argyle-room"),
    ];

    /// <summary>How wide a portrait is kept. The header crops a band out of the middle of one and blurs
    /// a second copy across the whole window. It does that every time the window is resized. A 4000 px
    /// original from a phone would cause a visible pause in the crossfade, and the extra detail does
    /// not show in a band that tall.</summary>
    private const int PortraitWidth = 1600;

    private static string Root => Path.Combine(Path.GetTempPath(), "rpm-gallery-shelf");

    /// <summary> Lays the shelf down and returns its root. There is one album per picture, in the
    /// folder's own order. Where there are more pictures than the records written above, the catalogue
    /// is walked again with the album titles numbered. So a bigger folder still makes a shelf, not
    /// duplicates. </summary>
    /// <param name="coversDir">A folder of cover pictures. Anything Skia can open.</param>
    /// <param name="artistsDir">A folder of artist portraits, named for the pictures in <see
    /// cref="Portraits"/> (site/portraits), or null for a shelf whose artists have never been
    /// photographed. The caller states this at the call site, and it is not defaulted. A shot taken
    /// without portraits shows the header standing on the record's own colours. That is a different
    /// picture, and not an obviously wrong one.</param>
    public static string Ensure(string coversDir, string? artistsDir)
    {
        var root = Root;
        Directory.CreateDirectory(root);

        var pictures = Directory.Exists(coversDir)
            ? Directory.GetFiles(coversDir)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(NumericThenName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        if (pictures.Length == 0)
        {
            Console.WriteLine($"[shelf] no pictures in {coversDir}");
            return root;
        }

        var tracks = new List<LibraryTrack>();
        var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        for (var i = 0; i < pictures.Length; i++)
        {
            var pass = i / Shelf.Length;
            var entry = Shelf[i % Shelf.Length];
            var album = pass == 0 ? entry.Album : $"{entry.Album}, Vol. {pass + 1}";
            var folder = $"{Sanitize(entry.Artist)}/{Sanitize(album)}";

            var dir = Path.Combine(root, entry.Artist is var _ ? Sanitize(entry.Artist) : "", Sanitize(album));
            Directory.CreateDirectory(dir);
            WritePicture(pictures[i], Path.Combine(dir, "cover.jpg"), CoverWidth);

            for (var n = 0; n < entry.Tracks.Length; n++)
            {
                var title = entry.Tracks[n];
                var ext = (i % 3) switch { 0 => ".flac", 1 => ".mp3", _ => ".m4a" };
                var fileName = $"{n + 1:00} {Sanitize(title)}{ext}";
                // Varied, but settled: the same album has the same length on every run, and no two
                // tracks on a record come out at the same number of seconds.
                var duration = 138 + (i * 17 + n * 29) % 190;

                tracks.Add(new LibraryTrack
                {
                    RelativePath = $"{folder}/{fileName}",
                    Folder = folder,
                    FileName = fileName,
                    Extension = ext,
                    SizeBytes = (long)(duration * (ext == ".flac" ? 900_000 : 130_000)),
                    ModifiedUtcTicks = stamp,
                    DurationSeconds = duration,
                    Title = title,
                    Artist = entry.Artist,
                    AlbumArtist = entry.Artist,
                    Album = album,
                    Genre = entry.Genre,
                    TrackNumber = (uint)(n + 1),
                    DiscNumber = 1,
                    Year = entry.Year,

                    // What the stream would have reported about itself. The album page prints the
                    // codec, the depth and the rate under the title. A row that has never been read
                    // prints a record with no format at all. On a shelf of records that nobody has
                    // opened, that is every row.
                    Channels = 2,
                    SampleRate = 44100,
                    BitsPerSample = ext == ".flac" ? 16 : (int?)null,
                    BitrateKbps = ext == ".flac" ? (int?)null : 320,
                });
            }
        }

        var rows = WithTheOneRealRecord(root, tracks);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(root)))
        {
            db.UpsertMany(rows);
            Photograph(db, root, artistsDir);
            Link(db);
        }

        Console.WriteLine($"[shelf] {pictures.Length} albums, {rows.Count} tracks in {root}");
        return root;
    }

    // ── the pressings the featured records are linked to ──

    /// <summary> The release each featured record is linked to. Its album page then shows a linked
    /// pressing (the date, the catalogue number and the format), instead of the "not linked" card. The
    /// release is invented like the rest of the shelf. It is linked through rpm's own catalogue, whose
    /// ids the page does not print. So no real catalogue number appears beside an invented record.
    /// </summary>
    private static readonly (string Artist, string Album, string Catno, string Released, string Format)[] Linked =
    [
        ("Kestrel Hall", "Machine For Living In", "TWR 014", "2008-04-14", "Vinyl, 2×LP, Album"),
        ("Ruby Ashgrove", "Small Hours Radio", "HRS 2031", "1976-09-13", "Vinyl, LP, Album"),
    ];

    private static void Link(LibraryDatabase db)
    {
        foreach (var (artist, album, catno, released, format) in Linked)
        {
            var folder = $"{Sanitize(artist)}/{Sanitize(album)}";
            db.SetAlbumPressing(folder, RpmMetadataProvider.ProviderId, $"gallery-{catno.Replace(' ', '-')}");
            db.SetAlbumRelease(folder, catno, released);
            db.SetAlbumEdition(folder, format);
        }
    }

    // ── the one record that is really there ──

    /// <summary>How long each featured record runs, as the catalogue above worked it out, keyed by its
    /// title. The length is kept from the lay-down, so the words are filed under the same length that
    /// the row carries. The lyric cache is keyed by artist, title, album and duration. A sheet filed
    /// under a length that nobody asks for is never seen.</summary>
    private static readonly Dictionary<string, double> Lengths = new(StringComparer.Ordinal);

    /// <summary>The entry this row is one of the featured records for, or null for the other
    /// 220.</summary>
    private static Feature? FeatureFor(LibraryTrack row)
        => Featured.FirstOrDefault(f =>
               string.Equals(row.Artist, f.Artist, StringComparison.Ordinal)
            && string.Equals(row.Album, f.Album, StringComparison.Ordinal)
            && string.Equals(row.Title, f.Track, StringComparison.Ordinal));

    /// <summary> Puts an actual playable file behind the one row that has to be played, not only
    /// listed. The row is then made to match the file.
    ///
    /// <para>Every other row on this shelf names a file that was never written. That is right for a
    /// wall of sleeves. It is fatal for a picture of the app doing its job. With nothing to open there
    /// is no header, no clock, no words and no playing row. The deck skips a file it cannot find. That
    /// is the correct answer for an offloaded track, and it leaves an empty screen to
    /// photograph.</para>
    ///
    /// <para>The row changes to a .wav file name. The clip is written as PCM, and a WAV file inside a
    /// file called .mp3 fails to decode. The extension is what the deck uses to pick a reader, so it
    /// must say what the bytes are.</para> </summary>
    private static IReadOnlyList<LibraryTrack> WithTheOneRealRecord(string root, List<LibraryTrack> rows)
    {
        var seeded = new List<LibraryTrack>(rows.Count);

        foreach (var row in rows)
        {
            if (FeatureFor(row) is null)
            {
                seeded.Add(row);
                continue;
            }

            var fileName = Path.ChangeExtension(row.FileName, ".wav");
            var relative = $"{row.Folder}/{fileName}";
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Three minutes of stereo PCM is thirty megabytes, and it is the same thirty megabytes on
            // every run, because the clip is written from a seeded generator. So it is written once and
            // then left alone. A second launch opens on a library that is already there.
            if (!File.Exists(path))
                File.WriteAllBytes(path, GalleryShoot.Clip(row.DurationSeconds));

            // The clip must be tagged. The catalogue in this file is written into the library's mirror,
            // but the header does not read the mirror. A track whose credit is blank in the mirror is
            // answered from the FILE instead. See ArtworkManager, which reads the tags itself for a
            // queue track played straight off disk. An untagged clip would play under a header with no
            // artist, no title and no photograph. That is the one thing this record exists to put on
            // screen.
            Tag(path, row);

            Lengths[row.Title ?? ""] = row.DurationSeconds;

            seeded.Add(row with
            {
                RelativePath = relative,
                FileName = fileName,
                Extension = ".wav",
                SizeBytes = new FileInfo(path).Length,
                BitsPerSample = 16,
                BitrateKbps = null,
            });
        }

        return seeded;
    }

    /// <summary>Writes the row's own catalogue into the file, so that the two agree wherever the app
    /// asks. This is best effort. An untagged clip is a header with less on it, not a shelf that
    /// fails.</summary>
    private static void Tag(string path, LibraryTrack row)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Title = row.Title;
            file.Tag.Performers = [row.Artist ?? ""];
            file.Tag.AlbumArtists = [row.AlbumArtist ?? ""];
            file.Tag.Album = row.Album;
            file.Tag.Genres = [row.Genre ?? ""];
            file.Tag.Year = row.Year;
            file.Tag.Track = row.TrackNumber;
            file.Save();
        }
        catch
        {
            // A tag writer that cannot open this file leaves the clip as it was.
        }
    }

    // ── who the artists are ──

    /// <summary> Files a photograph under each artist named in <see cref="Portraits"/>, as the person's
    /// OWN picture and as an order that was arranged by hand.
    ///
    /// <para>Both of those matter. The catalogues have never heard of anybody on this shelf, so they
    /// offer nothing for these artists. The reconciler's rule for a saved picture is "the sources still
    /// offer it, or it is gone". Under that rule, every one of these pictures would be dropped on the
    /// first track change. A file:// address is read as the listener's own (see <see
    /// cref="ArtistArtSource"/>) and is kept whatever the sources say. The curated order stops the
    /// ranker reshuffling an order of one.</para> </summary>
    private static void Photograph(LibraryDatabase db, string root, string? artistsDir)
    {
        if (string.IsNullOrWhiteSpace(artistsDir) || !Directory.Exists(artistsDir))
            return;

        var into = Path.Combine(root, ".artists");
        Directory.CreateDirectory(into);

        var taken = 0;
        foreach (var (artist, picture) in Portraits)
        {
            var source = Directory.GetFiles(artistsDir, picture + ".*")
                .FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
            if (source is null)
                continue;

            var kept = Path.Combine(into, picture + ".jpg");
            if (!File.Exists(kept))
                WritePicture(source, kept, PortraitWidth);
            if (!File.Exists(kept))
                continue;

            db.SaveArtistArt(artist, [new ArtistBackdrop(new Uri(kept).AbsoluteUri, 0)], curated: true);
            taken++;
        }

        Console.WriteLine($"[shelf] {taken} artist portrait(s)");
    }

    // ── what is written about the record ──

    /// <summary> The words to every featured song, and the encyclopedia's entries on its artist and its
    /// album. They are put where the app looks for them. All of it is invented. See <see
    /// cref="Feature"/>.
    ///
    /// <para>This is called after the host is built, not from <see cref="Ensure"/>. The dossier goes
    /// into a service that does not exist until then. It is put into that service's SESSION memory
    /// only. So nothing written here outlives the run, or is ever served to a real question.</para>
    /// </summary>
    public static void Papers()
    {
        var dossiers = App.Services.GetRequiredService<TrackInfoService>();

        foreach (var feature in Featured)
        {
            // Filed under the length the row carries, which is the only length anything asks about. A
            // record whose file was never written has no length here, and no sheet either. The words
            // are for a record that can be played, and there is nothing to play them against.
            if (!Lengths.TryGetValue(feature.Track, out var seconds))
                continue;

            LyricCache.Put(LyricsService.Key(feature.Artist, feature.Track, feature.Album, seconds),
                           LyricSheet.Parse(feature.Lrc));

            dossiers.Remember(feature.Artist, feature.Album,
                new TrackDossier(feature.Facts, feature.Pressing, feature.OnTheArtist, feature.OnTheAlbum));
        }
    }

    /// <summary>Numbered files first and in number order, so 10 follows 9 rather than 1.</summary>
    private static string NumericThenName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(stem, out var n) ? $"0{n:D6}" : $"1{stem}";
    }

    /// <summary>The picture, scaled to <paramref name="width"/> and written as a JPEG where the app
    /// looks for one: a sleeve in an album folder, or a portrait beside the library.</summary>
    private static void WritePicture(string source, string destination, int width)
    {
        try
        {
            using var original = SKBitmap.Decode(source);
            if (original is null)
                return;

            width = Math.Min(width, original.Width);
            var height = (int)Math.Round(original.Height * (double)width / original.Width);
            using var scaled = original.Resize(
                new SKImageInfo(width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (scaled is null)
                return;

            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            using var file = File.Create(destination);
            data.SaveTo(file);
        }
        catch
        {
            // A picture that will not decode is one album without a sleeve, or one artist with no face.
            // It is not a reason for the shelf to fail.
        }
    }

    /// <summary>What a folder or file may be called. The names above are written to read well, not to
    /// be paths. An apostrophe or a comma in one must not become a directory separator.</summary>
    private static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return clean.Trim().TrimEnd('.');
    }
}
#endif
