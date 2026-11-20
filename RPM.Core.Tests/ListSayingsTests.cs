using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What the listener said about the generated lists on this computer: songs that fit or do not fit a kind of list,
/// and acts and records a kind of list must stop offering. They go to the store, so a list it cuts for a phone keeps
/// to them. See <see cref="LibraryDatabase.ListSayingsToSend"/>.
/// </summary>
public class ListSayingsTests : IDisposable
{
    private readonly string _root;
    private readonly string _elsewhere;
    private readonly LibraryDatabase _db;
    private readonly LibraryDatabase _store;
    private const long Said = 638_900_000_000_000_000;

    public ListSayingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmsay-{Guid.NewGuid():N}");
        _elsewhere = Path.Combine(Path.GetTempPath(), $"rpmsay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_elsewhere);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        _store = new LibraryDatabase(MirrorLocation.InFolder(_elsewhere));
    }

    public void Dispose()
    {
        _db.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_elsewhere, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static void Add(LibraryDatabase db, string path, string? rpmId) => db.Upsert(new LibraryTrack
    {
        RelativePath = path,
        Folder = path[..path.LastIndexOf('/')],
        FileName = path[(path.LastIndexOf('/') + 1)..],
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        DurationSeconds = 200,
        Title = path,
        RpmId = rpmId,
    });

    [Fact]
    public void Verdicts_travel_by_cloud_id_and_refusals_by_subject()
    {
        Add(_db, "a/01.flac", "rpm-a1");
        Add(_db, "b/01.flac", null);
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Said);
        _db.SetTrackVerdict("b/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Liked, Said);
        _db.SetRecommendRefusal(RecommendScope.Artist, "Nickelback", SmartPlaylistKind.Energetic, refused: true, Said);
        _db.SetRecommendRefusal(RecommendScope.Album, "x/Some Record", SmartPlaylistKind.Calm, refused: true, Said);

        var said = _db.ListSayingsToSend();

        // A song with no cloud id has nothing the store could join a verdict to.
        var verdict = Assert.Single(said.Verdicts);
        Assert.Equal(new ListVerdict("rpm-a1", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Said), verdict);
        Assert.Equal(2, said.Refusals.Count);
        Assert.Contains(new ListRefusal(RecommendScope.Artist, "Nickelback", SmartPlaylistKind.Energetic, Said), said.Refusals);
    }

    [Fact]
    public void What_another_library_said_is_taken_in_whole()
    {
        Add(_store, "x/01.flac", "rpm-a1");
        Add(_store, "x/02.flac", "rpm-a2");
        _store.SetTrackVerdict("x/02.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Said);

        _store.AcceptListSayings(new ListSayings(
            [new ListVerdict("rpm-a1", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Said),
             new ListVerdict("rpm-missing", SmartPlaylistKind.Calm, TrackVerdict.Liked, Said)],
            [new ListRefusal(RecommendScope.Artist, "Nickelback", SmartPlaylistKind.Energetic, Said)]));

        Assert.Equal(TrackVerdict.Disliked, _store.TrackVerdictOf("x/01.flac", SmartPlaylistKind.Calm));
        // Whole: a verdict the sender no longer has is taken back.
        Assert.Equal(TrackVerdict.None, _store.TrackVerdictOf("x/02.flac", SmartPlaylistKind.Calm));
        Assert.False(_store.RecommendRefusalsSaid().Allows(SmartPlaylistKind.Energetic, null, "Nickelback"));
    }

    [Fact]
    public void The_same_sayings_read_twice_are_the_same()
    {
        Add(_db, "a/01.flac", "rpm-a1");
        _db.SetTrackVerdict("a/01.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Said);

        Assert.Equal(_db.ListSayingsToSend().Fingerprint, _db.ListSayingsToSend().Fingerprint);

        _db.SetRecommendRefusal(RecommendScope.Artist, "Nickelback", SmartPlaylistKind.Energetic, refused: true, Said);
        Assert.NotEqual(ListSayings.None.Fingerprint, _db.ListSayingsToSend().Fingerprint);
    }
}
