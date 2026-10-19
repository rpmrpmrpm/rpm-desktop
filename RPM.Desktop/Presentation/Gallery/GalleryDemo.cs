using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Splitting;
using RPM.Desktop.Presentation.Dialogs;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> Made-up material for the two screens that otherwise need a real recording to show
/// anything: the split dialog (which needs an album in one file, in practice a 20-minute vinyl rip) and
/// the track details editor (which reads a real file's tags).
///
/// <para>The split dialog is fed through <see cref="AlbumSplitAnalyzer"/>, which takes its audio
/// through an injectable <c>EnvelopeReader</c>. So instead of decoding a file, the dialog gets a
/// <i>synthetic</i> RMS envelope: a made-up 20-minute side with six loud stretches and the quiet
/// grooves between them. The real silence detector finds real boundaries in it. There is no ffmpeg, no
/// file and no rip. The dialog fills in exactly as it would for a genuine recording.</para>
///
/// <para>The details editor is simpler. It reads everything from an in-memory <see cref="TrackRow"/>. A
/// richly filled row, including a drawn cover, shows every tab with no file behind it.</para>
/// </summary>
internal static class GalleryDemo
{
    // A believable side: six tracks, each comfortably over the 60 s minimum, ~20 minutes in all.
    private static readonly (string Title, double Seconds)[] SideTracks =
    [
        ("Kaini Industries", 212),
        ("Bocuma", 176),
        ("Roygbiv", 258),
        ("Rue the Whirl", 149),
        ("Aquarius", 231),
        ("Olson", 194),
    ];

    private const string DemoArtist = "Boards of Canada";
    private const string DemoAlbum = "Music Has the Right to Children";
    private const string DemoPath = "/Sample/Boards of Canada - Music Has the Right to Children (Side A).wav";

    // ── split dialog ──

    /// <summary>The synthetic side, envelope and all. It is the same generator that a test drives
    /// through the real silence detector, so what fills the dialog is known to resolve to six
    /// tracks.</summary>
    private static EnvelopeScan Side() =>
        DemoSide.Envelope(SideTracks.Select(t => t.Seconds).ToArray());

    public static SplitSubject SplitSubject()
    {
        var total = Side().TotalSeconds;
        var current = new TrackInfo
        {
            FilePath = DemoPath,
            Extension = ".wav",
            DurationSeconds = total,
            Album = DemoAlbum,
            AlbumArtist = DemoArtist,
            Source = MetadataSource.Unknown,
        };
        // Pass the candidate explicitly so the dialog does not go to disk looking for a sibling cue.
        var candidate = new AlbumFileCandidate(DemoPath, total, CueSheetPath: null,
            AlbumFileHint.LongDuration | AlbumFileHint.FilenameHint);
        // A vinyl context with no side joins is a single recorded side. This lights up the format
        // picker and the needle-noise trim, which are the parts unique to a record.
        return new SplitSubject(DemoPath, current, DemoAlbum, DemoArtist, candidate,
            new VinylContext(Array.Empty<double>()));
    }

    public static AlbumSplitAnalyzer SplitAnalyzer() => new(backend: null, advanced: null)
    {
        VinylRecording = true,
        // The analyser reads its audio from here, so it never decodes anything.
        EnvelopeReader = _ => Side(),
        // This is never reached, because there is no backend and recognition is off. It is stubbed so
        // that no path can start ffmpeg.
        SliceFingerprinter = (_, _, _) => ("", 0d),
    };

    // ── RPaMp player ──

    /// <summary>A playlist for the player, so it opens full rather than empty. The paths are made up
    /// and nothing plays, but the artists are real, so the list reads like a real queue.</summary>
    public static IReadOnlyList<TrackInfo> RPaMpTracks()
    {
        static TrackInfo T(string artist, string title, double dur) => new()
        {
            FilePath = $"/Demo/{artist} - {title}.flac",
            Extension = ".flac",
            Artist = artist, AlbumArtist = artist, Title = title,
            DurationSeconds = dur,
        };
        return
        [
            T("Daft Punk", "Digital Love", 301),
            T("Justice", "Genesis", 233),
            T("Boards of Canada", "Roygbiv", 152),
            T("Aphex Twin", "Xtal", 293),
            T("Röyksopp", "Eple", 218),
            T("The Chemical Brothers", "Star Guitar", 386),
            T("Moderat", "A New Error", 340),
        ];
    }

    // ── release lookup ──

    /// <summary>A handful of pressings of one album, as a lookup would list them: a vinyl first press,
    /// CD editions from a few countries, and a reissue. The results list is not empty when there is no
    /// network to search.</summary>
    public static IReadOnlyList<ReleaseSearchResult> LookupResults()
    {
        ReleaseSearchResult R(string id, uint year, string date, string country, string format,
            string catNo, double relevance) =>
            new(new ReleaseRef("rpm", id), "Discovery", "Daft Punk", year, date, country,
                format, TrackCount: 14, Label: "Virgin", CatalogNumber: catNo, Relevance: relevance);

        return
        [
            R("demo-1", 2001, "2001-03-12", "FR", "2×Vinyl, LP, Album", "7243 8 49606 1 8", 0.98),
            R("demo-2", 2001, "2001-03-13", "GB", "CD, Album", "CDV 2940", 0.95),
            R("demo-3", 2001, "2001-03-20", "US", "CD, Album", "7243 8 49606 2 7", 0.93),
            R("demo-4", 2001, "2001", "JP", "CD, Album, Promo", "VJCP-68225", 0.88),
            R("demo-5", 2013, "2013", "EU", "2×Vinyl, LP, Reissue, Gatefold", "5099996619414", 0.80),
        ];
    }

    // ── import queue ──

    /// <summary>A believable batch of just-dropped imports: a couple of tagged tracks (ready to file),
    /// a couple with only a file name (needing recognition), and one long album in one file that shows
    /// the split badge. Nothing has a file behind it. It only has to fill the grid.</summary>
    public static IEnumerable<TrackInfo> QueueTracks()
    {
        static TrackInfo Tagged(string file, double dur, string artist, string title, string album, uint no) => new()
        {
            FilePath = $"/Demo/Inbox/{file}",
            Extension = Path.GetExtension(file).ToLowerInvariant(),
            DurationSeconds = dur,
            Artist = artist, Title = title, Album = album, AlbumArtist = artist,
            TrackNumber = no, Year = 2011, Genre = "Electronic",
            Source = MetadataSource.Tags,
        };
        static TrackInfo Raw(string file, double dur) => new()
        {
            FilePath = $"/Demo/Inbox/{file}",
            Extension = Path.GetExtension(file).ToLowerInvariant(),
            DurationSeconds = dur,
            Source = MetadataSource.FilenameGuess,
        };

        return
        [
            Tagged("01 Midnight City.flac", 241, "M83", "Midnight City", "Hurry Up, We're Dreaming", 1),
            Tagged("02 Reunion.flac", 223, "M83", "Reunion", "Hurry Up, We're Dreaming", 2),
            Tagged("03 Wait.flac", 351, "M83", "Wait", "Hurry Up, We're Dreaming", 3),
            Raw("track_07.mp3", 187),
            Raw("track_08.mp3", 205),
            Raw("recording 2.wav", 168),
            // A long single file. AlbumFileDetector flags it, which shows the split badge and the
            // "Split album" action.
            new TrackInfo
            {
                FilePath = "/Demo/Inbox/Live at the Old Hall (full set).wav",
                Extension = ".wav",
                DurationSeconds = 1284,
                Title = "Live at the Old Hall (full set)",
                Source = MetadataSource.Unknown,
            },
        ];
    }

    // ── track details ──

    /// <summary>A fully tagged row, so every tab of the details editor has something in it: credits,
    /// lyrics, a drawn cover and all, with no file to read.</summary>
    public static TrackRow DetailRow()
    {
        var info = new TrackInfo
        {
            FilePath = "/Sample/Boards of Canada/Music Has the Right to Children/08 Roygbiv.flac",
            Extension = ".flac",
            DurationSeconds = 152,
            Title = "Roygbiv",
            Artist = DemoArtist,
            AlbumArtist = DemoArtist,
            Album = DemoAlbum,
            TrackNumber = 8,
            TrackCount = 17,
            DiscNumber = 1,
            DiscCount = 1,
            Year = 1998,
            Genre = "IDM",
            Composers = "Michael Sandison, Marcus Eoin",
            Conductor = "",
            Publisher = "Warp Records",
            Copyright = "1998 Warp Records Limited",
            RemixedBy = "",
            Grouping = "Warp / WARP CD 55",
            Bpm = 104,
            Isrc = "GBBPP9800008",
            Comment = "Demo track — gallery harness. No audio behind this file.",
            Lyrics = "(instrumental)",
            Source = MetadataSource.Tags,
        };

        var row = new TrackRow(info);
        // Give the artwork tab a cover to show: the same one the album wears in the display.
        row.SetPendingArt(GalleryArt.CoverPng(DemoArtist, DemoAlbum), "image/png");
        return row;
    }
}
