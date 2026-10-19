using RPM.Core.Art;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Where a hand-made framing is stored. It has its own table on purpose: the saved ORDER is rewritten
/// completely every time the carousel is paged or re-ranked, and a framing stored in it would have to
/// be kept by every code path that builds a backdrop record from scratch. These tests check that the
/// order can be rewritten any number of times and the framings do not change.
/// </summary>
public class LibraryDatabaseArtistFramingTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseArtistFramingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmframe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static List<ArtistBackdrop> Order(params string[] urls)
        => urls.Select((u, i) => new ArtistBackdrop(u, i)).ToList();

    [Fact]
    public void A_photograph_nobody_framed_has_no_framing()
    {
        Assert.Null(_db.GetArtistFraming("Radiohead", "a"));
        Assert.Empty(_db.GetArtistFramings("Radiohead"));
    }

    [Fact]
    public void Save_then_get_round_trips()
    {
        _db.SaveArtistFraming("Radiohead", "a", new ArtFraming(0.42, 0.31, 0.55, 0.88));

        var got = _db.GetArtistFraming("Radiohead", "a");

        Assert.NotNull(got);
        Assert.Equal(0.42, got!.Value.CentreX, 6);
        Assert.Equal(0.31, got.Value.CentreY, 6);
        Assert.Equal(0.55, got.Value.Height, 6);
        Assert.Equal(0.88, got.Value.Cover, 6);
    }

    [Fact]
    public void Saving_again_replaces_it()
    {
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.1, 0.1, 0.9, 0.6));
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.8, 0.2, 0.3, 1));

        Assert.Equal(0.8, _db.GetArtistFraming("A", "a")!.Value.CentreX, 6);
        Assert.Single(_db.GetArtistFramings("A"));
    }

    [Fact]
    public void Null_clears_it_back_to_the_choosers_own()
    {
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.1, 0.1, 0.9, 0.6));
        _db.SaveArtistFraming("A", "a", null);

        Assert.Null(_db.GetArtistFraming("A", "a"));
        Assert.Empty(_db.GetArtistFramings("A"));
    }

    [Fact]
    public void Clearing_one_that_was_never_there_is_nothing()
    {
        _db.SaveArtistFraming("A", "never", null);   // no throw
        Assert.Null(_db.GetArtistFraming("A", "never"));
    }

    [Fact]
    public void Framings_are_per_photograph()
    {
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.2, 0.2, 0.2, 0.4));
        _db.SaveArtistFraming("A", "b", new ArtFraming(0.7, 0.7, 0.7, 0.9));

        var all = _db.GetArtistFramings("A");

        Assert.Equal(2, all.Count);
        Assert.Equal(0.2, all["a"].CentreX, 6);
        Assert.Equal(0.7, all["b"].CentreX, 6);
    }

    [Fact]
    public void The_key_is_case_and_whitespace_insensitive_like_the_order()
    {
        _db.SaveArtistFraming("Björk", "x", new ArtFraming(0.3, 0.4, 0.5, 1));

        Assert.NotNull(_db.GetArtistFraming("  björk  ", "x"));
        Assert.Single(_db.GetArtistFramings("BJÖRK"));
    }

    [Fact]
    public void A_framing_outlives_every_rewrite_of_the_order()
    {
        _db.SaveArtistArt("A", Order("a", "b"), curated: false);
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.25, 0.35, 0.45, 0.75));

        // The order changing as it does in normal use: a re-rank, a hand-made choice, a new photo.
        _db.SaveArtistArt("A", Order("b", "a"), curated: false);
        _db.SaveArtistArt("A", Order("a", "b", "c"), curated: true);
        _db.SetArtistArtHidden("A", "b", hidden: true);

        var got = _db.GetArtistFraming("A", "a");

        Assert.NotNull(got);
        Assert.Equal(0.25, got!.Value.CentreX, 6);
    }

    [Fact]
    public void Forgetting_an_artists_art_forgets_their_framings_too()
    {
        _db.SaveArtistArt("A", Order("a"), curated: true);
        _db.SaveArtistFraming("A", "a", new ArtFraming(0.5, 0.5, 0.5, 1));

        _db.DeleteArtistArt("A");

        Assert.Null(_db.GetArtistArt("A"));
        Assert.Null(_db.GetArtistFraming("A", "a"));
    }

    [Fact]
    public void It_survives_the_app_being_closed()
    {
        _db.SaveArtistFraming("Portishead", "front", new ArtFraming(0.6, 0.2, 0.4, 0.55));
        _db.Dispose();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));

        Assert.Equal(0.6, reopened.GetArtistFraming("Portishead", "front")!.Value.CentreX, 6);
    }

    [Fact]
    public void A_blank_artist_or_photograph_is_ignored()
    {
        _db.SaveArtistFraming("   ", "a", new ArtFraming(0.5, 0.5, 0.5, 1));   // no throw
        _db.SaveArtistFraming("A", "   ", new ArtFraming(0.5, 0.5, 0.5, 1));

        Assert.Null(_db.GetArtistFraming("   ", "a"));
        Assert.Null(_db.GetArtistFraming("A", "   "));
        Assert.Empty(_db.GetArtistFramings(null));
    }
}
