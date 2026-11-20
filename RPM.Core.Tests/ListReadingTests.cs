using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A generated list as another device shows it after a number of presses of "Generate new". The store cuts
/// these for the phones and the web app, so each press there is a new list from the pool, as it is on a
/// computer. See <see cref="LibraryDatabase.QuerySmartPlaylistReading"/>.
/// </summary>
public class ListReadingTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;
    private static readonly SmartPlaylistId Calm = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);

    public ListReadingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmreading-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));

        // One record per song, so the per-record cap does not shrink the pool, and enough songs that the calm
        // pool is several lists deep.
        for (int i = 0; i < 1200; i++)
        {
            double at = (i + 1) / 1200.0;
            string path = $"r{i:D4}/01.flac";
            _db.Upsert(new LibraryTrack
            {
                RelativePath = path,
                Folder = $"r{i:D4}",
                FileName = "01.flac",
                Extension = ".flac",
                SizeBytes = 100,
                ModifiedUtcTicks = 1,
                DurationSeconds = 200,
                Title = path,
                Genre = "Rock",
            });
            _db.SaveTrackMood(path, 100, 1, new MoodReading(at, at * 10, at * 20));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static List<string> Paths(IReadOnlyList<LibraryTrack> tracks) => [.. tracks.Select(t => t.RelativePath)];

    [Fact]
    public void Reading_zero_is_the_list_as_it_stands()
    {
        Assert.Equal(
            Paths(_db.QuerySmartPlaylist(Calm, 0, advance: false)),
            Paths(_db.QuerySmartPlaylistReading(Calm, 0, reading: 0)));
    }

    [Fact]
    public void Each_reading_is_a_new_list()
    {
        var first = Paths(_db.QuerySmartPlaylistReading(Calm, 0, reading: 0));
        var second = Paths(_db.QuerySmartPlaylistReading(Calm, 0, reading: 1));
        var third = Paths(_db.QuerySmartPlaylistReading(Calm, 0, reading: 2));

        // Different lists, not disjoint ones: how far one window moves from the last is the pool's business
        // (see MoodPool.Window).
        Assert.NotEmpty(first);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    /// <summary>The same arithmetic as a press on this computer: reading n is what n presses give here.</summary>
    [Fact]
    public void Reading_n_is_what_n_presses_give()
    {
        var atTwo = Paths(_db.QuerySmartPlaylistReading(Calm, 0, reading: 2));

        _db.QuerySmartPlaylist(Calm, 0, advance: true);
        var pressedTwice = Paths(_db.QuerySmartPlaylist(Calm, 0, advance: true));

        Assert.Equal(atTwo, pressedTwice);
    }

    /// <summary>A phone's press is the phone's. It does not move the list on this computer's screen.</summary>
    [Fact]
    public void A_reading_for_another_device_leaves_this_computers_list_alone()
    {
        var before = Paths(_db.QuerySmartPlaylist(Calm, 0, advance: false));

        _db.QuerySmartPlaylistReading(Calm, 0, reading: 3);

        Assert.Equal(before, Paths(_db.QuerySmartPlaylist(Calm, 0, advance: false)));
    }

    [Fact]
    public void A_negative_reading_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.QuerySmartPlaylistReading(Calm, 0, reading: -1));
    }
}
