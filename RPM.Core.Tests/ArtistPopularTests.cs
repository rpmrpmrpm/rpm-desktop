using RPM.Core.Library;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Tests;

/// <summary>
/// An artist's popular songs: most played first, one per title, never more than half of everything of
/// theirs, and nothing at all when there are too few to be a list. Then the query that feeds it.
/// </summary>
public class ArtistPopularTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public ArtistPopularTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmpopular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<string> Ranked(int songs, int most, params (string Title, long Plays)[] played)
        => [.. ArtistPopular.Ranked(played, p => p.Title, p => p.Plays, songs, most).Select(p => p.Title)];

    // ── the ranking ──

    [Fact]
    public void The_most_played_come_first()
    {
        Assert.Equal(
            ["Big", "Middling", "Small", "Least"],
            Ranked(songs: 20, most: 32, ("Small", 10), ("Big", 900), ("Least", 2), ("Middling", 50)));
    }

    /// <summary>A live take of a song is the same song to a listener; the one played more stands for
    /// both.</summary>
    [Fact]
    public void One_song_per_title_whatever_is_in_brackets()
    {
        Assert.Equal(
            ["Hello", "A", "B", "C"],
            Ranked(songs: 20, most: 32,
                ("Hello", 900), ("hello (Live at the BBC)", 800), ("A", 70), ("B", 60), ("Hello [Remastered]", 50),
                ("C", 40)));
    }

    /// <summary>One record of ten songs gives five: the list must not be the record again.</summary>
    [Fact]
    public void Never_more_than_half_of_their_songs()
    {
        var played = Enumerable.Range(1, 10).Select(i => ($"Song {i}", (long)(100 - i))).ToArray();

        Assert.Equal(5, Ranked(songs: 10, most: 32, played).Count);
    }

    [Fact]
    public void Never_more_than_the_screen_asks_for()
    {
        var played = Enumerable.Range(1, 100).Select(i => ($"Song {i}", (long)(1000 - i))).ToArray();

        Assert.Equal(32, Ranked(songs: 400, most: 32, played).Count);
    }

    [Fact]
    public void Too_few_is_no_list_at_all()
    {
        Assert.Empty(Ranked(songs: 40, most: 32, ("A", 9), ("B", 8), ("C", 7)));

        // Enough counted songs, but too few songs in all for half of them to make a list.
        Assert.Empty(Ranked(songs: 7, most: 32, ("A", 9), ("B", 8), ("C", 7), ("D", 6)));
    }

    [Fact]
    public void A_song_counted_at_nought_is_not_popular()
    {
        Assert.Equal(
            ["A", "B", "C", "D"],
            Ranked(songs: 40, most: 32, ("A", 9), ("Z", 0), ("B", 8), ("C", 7), ("D", 6)));
    }

    [Fact]
    public void Ties_keep_the_order_they_were_given_in()
    {
        Assert.Equal(
            ["First", "Second", "Third", "Fourth"],
            Ranked(songs: 40, most: 32, ("First", 5), ("Second", 5), ("Third", 5), ("Fourth", 5)));
    }

    [Theory]
    [InlineData("Hello (Live)", "hello")]
    [InlineData("Song [Remastered 2011] (Mono)", "song")]
    [InlineData("(Intro)", "(intro)")]
    [InlineData("Unclosed (bracket", "unclosed (bracket")]
    [InlineData(null, "")]
    public void A_title_as_one_song(string? title, string key)
        => Assert.Equal(key, ArtistPopular.TitleKey(title));

    // ── the query ──

    private void Add(string folder, int at, string title)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"{folder}/{at:D2}.flac",
            Folder = folder,
            FileName = $"{at:D2}.flac",
            Extension = ".flac",
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            TrackNumber = (uint)at,
            DiscNumber = 1,
            Title = title,
            Artist = "An Act",
            Album = folder,
        });
    }

    [Fact]
    public void The_query_brings_every_counted_song_under_the_records_most_played_first()
    {
        Add("Act/One", 1, "Opener");
        Add("Act/One", 2, "Hit");
        Add("Act/One", 3, "Deep cut");
        Add("Act/Two", 1, "Other hit");
        Add("Somebody/Else", 1, "Not theirs");
        _db.SaveWorldPlays("Act/One", new Dictionary<TrackPlace, long>
        {
            [new TrackPlace(1, 1)] = 40,
            [new TrackPlace(1, 2)] = 900,
            [new TrackPlace(1, 3)] = 0,
        });
        _db.SaveWorldPlays("Act/Two", new Dictionary<TrackPlace, long> { [new TrackPlace(1, 1)] = 500 });
        _db.SaveWorldPlays("Somebody/Else", new Dictionary<TrackPlace, long> { [new TrackPlace(1, 1)] = 5000 });

        var found = _db.QueryWorldPlaysUnder(["Act/One", "Act/Two"]);

        // Two off the same record, which a genre's one-per-record list would not give; the song counted
        // at nought and the other act's record are not there.
        Assert.Equal(["Hit", "Other hit", "Opener"], found.Select(f => f.Track.Title));
        Assert.Equal([900L, 500L, 40L], found.Select(f => f.Plays));
    }

    [Fact]
    public void The_query_under_no_records_is_empty()
        => Assert.Empty(_db.QueryWorldPlaysUnder([]));
}
