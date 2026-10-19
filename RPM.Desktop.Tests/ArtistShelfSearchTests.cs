using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// Two search fields over one collection.
///
/// <para>Typing a name into the field over the album wall must not remove names from the artists
/// wall. If both views were fed from one pass of the mirror, read with the album field's search, a
/// field for one view would filter another view the user is not looking at, and the artists wall's
/// own field would have nothing left to search.</para>
///
/// <para>So the artists' side of the index is the WHOLE collection, read on a pass of its own, and
/// the album views' search cannot reach it. The wall's own field filters it afterwards, in the view
/// (see ArtistWallView.SetSearch).</para>
/// </summary>
public class ArtistShelfSearchTests : IDisposable
{
    private readonly string _root;
    // No session: the mirror is opened as a signed-out app opens it. That is enough here,
    // because every row in these tests is a file on the disk.
    private readonly LibraryService _library = new(TestSessions.SignedOut());
    private readonly LibraryAlbumIndex _index;

    private const string AbbaFolder = "ABBA/Arrival";
    private const string BlondieFolder = "Blondie/Parallel Lines";

    public ArtistShelfSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"artists-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _library.Open(_root);
        _index = new LibraryAlbumIndex(_library);

        _library.Db.UpsertMany([
            Track(AbbaFolder, "Dancing Queen", "ABBA", 1976),
            Track(AbbaFolder, "Money, Money, Money", "ABBA", 1976),
            Track(BlondieFolder, "Heart of Glass", "Blondie", 1978),
        ]);
    }

    public void Dispose()
    {
        _index.Dispose();
        _library.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static LibraryTrack Track(string folder, string title, string artist, uint year) => new()
    {
        RelativePath = $"{folder}/{title}.flac",
        Folder = folder,
        FileName = $"{title}.flac",
        Extension = ".flac",
        SizeBytes = 40_000_000,
        ModifiedUtcTicks = 99,
        DurationSeconds = 200,
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = folder[(folder.IndexOf('/') + 1)..],
        Year = year,
        TrackNumber = 1,
    };

    /// <summary>The main case: searching the album wall for one act must leave every act on the
    /// artists wall.</summary>
    [Fact]
    public async Task A_search_over_the_albums_leaves_every_artist_on_the_wall()
    {
        await _index.RebuildArtistsAsync();
        await _index.RebuildAsync("abba");

        Assert.Equal([AbbaFolder], _index.Albums);
        Assert.Equal(
            ["ABBA", "Blondie"],
            _index.Artists.Acts.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>And the artist's own page, opened while that search is up, still knows what its records
    /// are: the captions come from the artists' side, not from the filtered album views.</summary>
    [Fact]
    public async Task An_artists_records_are_still_captioned_under_an_album_search()
    {
        await _index.RebuildArtistsAsync();
        await _index.RebuildAsync("abba");

        Assert.False(_index.AlbumFacts.ContainsKey(BlondieFolder));   // filtered, like the wall of covers

        Assert.Equal(1978, _index.Artists.AlbumFacts[BlondieFolder].Year);
        Assert.Equal(1, _index.Artists.TrackCounts[BlondieFolder]);
    }

    /// <summary>A search never reads the artists again, so the artists already read must survive it:
    /// the pass for each keystroke must not clear the wall.</summary>
    [Fact]
    public async Task A_keystroke_does_not_disturb_what_the_artists_were_read_at()
    {
        await _index.RebuildArtistsAsync();
        var before = _index.Artists;

        await _index.RebuildAsync("abba");
        await _index.RebuildAsync("");

        Assert.Same(before, _index.Artists);
    }

    /// <summary>A library change does update the artists: a record imported while a search is active
    /// joins the artists wall, read from the whole collection, not from the search.</summary>
    [Fact]
    public async Task An_import_during_a_search_puts_its_artist_on_the_wall()
    {
        await _index.RebuildArtistsAsync();
        await _index.RebuildAsync("abba");

        _library.Db.UpsertMany([Track("Talking Heads/Remain in Light", "Once in a Lifetime", "Talking Heads", 1980)]);
        await _index.RebuildArtistsAsync();

        Assert.Contains(_index.Artists.Acts, a => a.Name == "Talking Heads");
        Assert.Equal([AbbaFolder], _index.Albums);   // the album views stay where the search left them
    }
}
