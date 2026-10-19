using System.Reflection;
using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The import queue is the staging area for the library, so what a user puts on a row there has to
/// survive the trip. Every field must be written back to the store, not only some of them: works
/// and movements, sort names, soloists and ensembles, barcode, catalogue number and media, the
/// MusicBrainz ids, the advisory flag and the chosen cover. An edit triggers a rebuild, and a
/// rebuild reads from the store again, so a field that is not written back is lost at once, with no
/// error.
/// </summary>
public class QueueFidelityTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public QueueFidelityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"queuefidelity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    /// <summary>A track with every editable field filled in with something distinguishable.</summary>
    private static TrackInfo Everything(string path) => new()
    {
        FilePath = path,
        Extension = ".flac",
        DurationSeconds = 321.5,
        Title = "Allegro con brio",
        Artist = "Berliner Philharmoniker",
        AlbumArtist = "Herbert von Karajan",
        Album = "Symphony No. 5",
        TrackNumber = 1,
        TrackCount = 4,
        DiscNumber = 2,
        DiscCount = 3,
        DiscSubtitle = "The Fifth",
        Year = 1977,
        Genre = "Classical",
        Composers = "Ludwig van Beethoven",
        Conductor = "Herbert von Karajan",
        Comment = "from the 1977 cycle",
        Lyrics = "n/a",
        Grouping = "Beethoven cycle",
        Bpm = 108,
        Copyright = "© 1977",
        Publisher = "Deutsche Grammophon",
        RemixedBy = "nobody",
        Isrc = "DEA019770001",
        MusicalKey = "Cm",
        ReleaseCountry = "DE",
        Advisory = ContentAdvisory.Clean,
        TitleSort = "Allegro con brio",
        ArtistSort = "Berliner Philharmoniker",
        AlbumArtistSort = "Karajan, Herbert von",
        ComposerSort = "Beethoven, Ludwig van",
        Work = "Symphony No. 5 in C minor, Op. 67",
        MovementName = "I. Allegro con brio",
        MovementNumber = 1,
        MovementCount = 4,
        Lyricist = "none",
        Arranger = "none",
        Ensemble = "Berliner Philharmoniker",
        Soloists = "none",
        Barcode = "0289 477 8471 4",
        CatalogNumber = "477 8471",
        Media = "CD",
        OriginalYear = 1808,
        MusicBrainzRecordingId = "rec-1",
        MusicBrainzReleaseId = "rel-2",
        MusicBrainzReleaseGroupId = "rg-3",
        MusicBrainzArtistId = "art-4",
        MusicBrainzReleaseArtistId = "relart-5",
        LoudnessLufs = -14.2,
        TruePeak = -1.1,
        TagsEdited = true,
        Source = MetadataSource.Manual,
        IdentificationScore = 0.93,
    };

    private static QueueTrack Queued(TrackInfo track, TrackInfo? wanted = null) => new()
    {
        Source = "/src",
        Folder = "src",
        FileName = Path.GetFileName(track.FilePath),
        Current = track,
        Wanted = wanted ?? track,
    };

    /// <summary>Fields the queue deliberately does not carry, with the reason. Everything else must
    /// round-trip. This test fails when a new field is added and forgotten.</summary>
    private static readonly HashSet<string> NotCarried =
    [
        nameof(TrackInfo.HasArt),          // a fact about the FILE, re-read by the scan
        nameof(TrackInfo.RpmId),           // stamped at import, never edited in the queue
        nameof(TrackInfo.Availability),    // the row's own column, not the wanted side's
        nameof(TrackInfo.CodecDescription),// immutable file facts, current side only
        nameof(TrackInfo.Channels), nameof(TrackInfo.SampleRate),
        nameof(TrackInfo.BitsPerSample), nameof(TrackInfo.BitrateKbps),
        nameof(TrackInfo.PendingArt), nameof(TrackInfo.PendingArtMime),   // asserted separately
    ];

    [Fact]
    public void Every_editable_field_survives_the_store()
    {
        var track = Everything("/music/src/01.flac");
        _db.QueueInsertMany([Queued(track)]);

        var back = Assert.Single(_db.WholeQueue.CheckedRows().Concat(_db.WholeQueue.RowsInGroup("Herbert von Karajan", "Symphony No. 5")));
        AssertSameFields(track, back.Wanted);
        // The current side carries the same fields, minus the flag that only means anything on the
        // wanted one: "somebody edited this".
        AssertSameFields(track with { TagsEdited = false }, back.Current);
    }

    [Fact]
    public void Every_editable_field_survives_a_write_back()
    {
        // The case that matters: the row is edited, written back, and read again on the rebuild that
        // the edit itself triggers.
        var scanned = Everything("/music/src/01.flac");
        _db.QueueInsertMany([Queued(scanned, wanted: new TrackInfo
        {
            FilePath = scanned.FilePath,
            Extension = scanned.Extension,
        })]);
        var row = Assert.Single(_db.WholeQueue.RowsInGroup("", ""));

        var edited = Everything("/music/src/01.flac");
        _db.QueueUpdateWanted(row.Id, isChecked: true, edited);

        var back = Assert.Single(_db.WholeQueue.RowsInGroup("Herbert von Karajan", "Symphony No. 5"));
        Assert.True(back.IsChecked);
        AssertSameFields(edited, back.Wanted);
    }

    [Fact]
    public void A_chosen_cover_survives_and_is_stored_once_per_picture()
    {
        var art = new byte[] { 1, 2, 3, 4, 5 };
        var a = Everything("/music/src/01.flac") with { PendingArt = art, PendingArtMime = "image/png" };
        var b = Everything("/music/src/02.flac") with { PendingArt = art, PendingArtMime = "image/png" };
        _db.QueueInsertMany([Queued(a), Queued(b)]);

        var rows = _db.WholeQueue.RowsInGroup("Herbert von Karajan", "Symphony No. 5");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(art, r.Wanted.PendingArt);
            Assert.Equal("image/png", r.Wanted.PendingArtMime);
        });
    }

    [Fact]
    public void Taking_the_cover_off_is_a_decision_that_survives_too()
    {
        // Empty pending art means "remove the picture". It must not read back as "never chose one".
        _db.QueueInsertMany([Queued(Everything("/music/src/01.flac") with { PendingArt = [] })]);

        var back = Assert.Single(_db.WholeQueue.RowsInGroup("Herbert von Karajan", "Symphony No. 5"));
        Assert.NotNull(back.Wanted.PendingArt);
        Assert.Empty(back.Wanted.PendingArt!);
    }

    /// <summary>Compares every property of <see cref="TrackInfo"/> except the ones the queue is not
    /// meant to carry, so a field added later is caught here and not found missing in a user's
    /// collection.</summary>
    private static void AssertSameFields(TrackInfo expected, TrackInfo actual)
    {
        foreach (var p in typeof(TrackInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (NotCarried.Contains(p.Name) || !p.CanRead || p.GetIndexParameters().Length > 0)
                continue;
            Assert.Equal($"{p.Name}={p.GetValue(expected)}", $"{p.Name}={p.GetValue(actual)}");
        }
    }
}
