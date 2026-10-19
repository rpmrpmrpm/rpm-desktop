using RPM.Core.Library;

namespace RPM.Core.Tests;

public class LibraryDatabaseArtistArtTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseArtistArtTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static List<ArtistBackdrop> Order(params string[] urls)
        => urls.Select((u, i) => new ArtistBackdrop(u, i, 1.0 - i * 0.1)).ToList();

    [Fact]
    public void Unknown_artist_has_no_saved_order()
    {
        Assert.Null(_db.GetArtistArt("Nobody"));
    }

    [Fact]
    public void Save_then_get_round_trips_order_and_scores()
    {
        _db.SaveArtistArt("Radiohead", Order("a", "b", "c"), curated: false);

        var got = _db.GetArtistArt("Radiohead");

        Assert.NotNull(got);
        Assert.False(got!.Curated);
        Assert.Equal(["a", "b", "c"], got.Backdrops.Select(b => b.Url));
        Assert.Equal([0, 1, 2], got.Backdrops.Select(b => b.Position));
        Assert.Equal(1.0, got.Backdrops[0].Score, 3);
    }

    [Fact]
    public void The_key_is_case_and_whitespace_insensitive()
    {
        _db.SaveArtistArt("Björk", Order("x", "y"), curated: false);

        Assert.NotNull(_db.GetArtistArt("  björk  "));
    }

    [Fact]
    public void Save_replaces_the_previous_order()
    {
        _db.SaveArtistArt("A", Order("a", "b", "c"), curated: false);
        _db.SaveArtistArt("A", Order("c", "a"), curated: true);

        var got = _db.GetArtistArt("A");

        Assert.True(got!.Curated);
        Assert.Equal(["c", "a"], got.Backdrops.Select(b => b.Url));   // old "b" is gone
    }

    [Fact]
    public void Hiding_a_backdrop_persists()
    {
        _db.SaveArtistArt("A", Order("a", "b", "c"), curated: true);

        _db.SetArtistArtHidden("A", "b", hidden: true);

        var got = _db.GetArtistArt("A");
        Assert.Contains(got!.Backdrops, b => b.Url == "b" && b.Hidden);
        Assert.DoesNotContain(got.Backdrops, b => b.Url == "a" && b.Hidden);
    }

    [Fact]
    public void Delete_forgets_the_order()
    {
        _db.SaveArtistArt("A", Order("a", "b"), curated: true);

        _db.DeleteArtistArt("A");

        Assert.Null(_db.GetArtistArt("A"));
    }

    [Fact]
    public void The_order_survives_reopening_the_database()
    {
        // A save, then a brand-new connection to the same file on disk — i.e. the next run of the app.
        _db.SaveArtistArt("Portishead", Order("front", "second", "third"), curated: true);
        _db.Dispose();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var got = reopened.GetArtistArt("Portishead");

        Assert.NotNull(got);
        Assert.True(got!.Curated);
        Assert.Equal(["front", "second", "third"], got.Backdrops.Select(b => b.Url));
    }

    [Fact]
    public void Blank_artist_is_ignored()
    {
        _db.SaveArtistArt("   ", Order("a"), curated: false);   // no throw
        Assert.Null(_db.GetArtistArt("   "));
    }
}
