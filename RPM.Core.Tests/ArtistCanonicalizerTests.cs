using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class ArtistCanonicalizerTests
{
    [Theory]
    // The spelling problem this exists for: one band, four ways of writing it.
    [InlineData("guns n roses")]
    [InlineData("GUNS N' ROSES")]
    [InlineData("The Guns N' Roses")]
    [InlineData("Guns  N'  Roses")]
    public void Resolves_a_mis_spelling_to_the_spelling_the_collection_uses(string incoming)
    {
        var canon = ArtistCanonicalizer.From(["Guns N' Roses"]);
        Assert.Equal("Guns N' Roses", canon.Resolve(incoming));
    }

    [Fact]
    public void Sees_through_accents()
    {
        var canon = ArtistCanonicalizer.From(["Sigur Rós"]);
        Assert.Equal("Sigur Rós", canon.Resolve("Sigur Ros"));
    }

    /// <summary>It never invents a spelling: a band the collection has never heard of comes back
    /// exactly as it arrived, for the identification layer to have an opinion about.</summary>
    [Fact]
    public void Leaves_an_unknown_artist_alone()
    {
        var canon = ArtistCanonicalizer.From(["Muse"]);
        Assert.Equal("Portishead", canon.Resolve("Portishead"));
    }

    [Fact]
    public void Leaves_nothing_as_nothing()
    {
        var canon = ArtistCanonicalizer.From(["Muse"]);
        Assert.Null(canon.Resolve(null));
        Assert.Equal("   ", canon.Resolve("   "));
    }

    /// <summary>The most-established name wins its key: the one on the most shelves, so adopting
    /// it moves the fewest files.</summary>
    [Fact]
    public void The_first_established_spelling_wins_the_key()
    {
        var canon = ArtistCanonicalizer.From(["Céline Dion", "celine dion"]);
        Assert.Equal("Céline Dion", canon.Resolve("CELINE DION"));
    }

    /// <summary>A name that cleans away to nothing must not claim the empty key, or it would be
    /// handed back for every other unnameable track.</summary>
    [Fact]
    public void A_name_that_is_only_punctuation_establishes_nothing()
    {
        var canon = ArtistCanonicalizer.From(["???"]);
        Assert.Equal("!!!", canon.Resolve("!!!"));
    }

    [Fact]
    public void Apply_spells_both_the_artist_and_the_album_artist()
    {
        var canon = ArtistCanonicalizer.From(["Massive Attack"]);
        var track = new TrackInfo
        {
            FilePath = "/x/01.flac",
            Extension = ".flac",
            Artist = "massive attack",
            AlbumArtist = "MASSIVE ATTACK",
            Title = "Teardrop",
        };

        var applied = canon.Apply(track);

        Assert.Equal("Massive Attack", applied.Artist);
        Assert.Equal("Massive Attack", applied.AlbumArtist);
        Assert.Equal("Teardrop", applied.Title);   // nothing else is touched
    }

    /// <summary>A run agrees with ITSELF: the first file of a band the library has never held
    /// settles the spelling for the rest of the import.</summary>
    [Fact]
    public void A_run_settles_a_band_the_library_has_never_held()
    {
        var canon = ArtistCanonicalizer.From([]);
        var first = canon.Apply(Track("Sigur Rós"));
        var second = canon.Apply(Track("sigur ros"));

        Assert.Equal("Sigur Rós", first.Artist);
        Assert.Equal("Sigur Rós", second.Artist);
    }

    [Fact]
    public void An_empty_collection_has_no_opinion()
        => Assert.Equal("Anything", ArtistCanonicalizer.Empty.Resolve("Anything"));

    /// <summary>Straight off the mirror: the names the tracks table actually holds, in both the
    /// artist and album-artist columns.</summary>
    [Fact]
    public void FromLibrary_reads_the_names_the_collection_uses()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rpmcanon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));
            db.UpsertMany([
                LibraryDatabase.FromTrackInfo(
                    Track("Aphex Twin", "/x/Aphex Twin/01.flac") with { AlbumArtist = "Aphex Twin" }, "/x"),
            ]);

            var canon = ArtistCanonicalizer.FromLibrary(db);

            Assert.Equal("Aphex Twin", canon.Resolve("aphex twin"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static TrackInfo Track(string artist, string path = "/x/01.flac") => new()
    {
        FilePath = path,
        Extension = ".flac",
        Artist = artist,
    };
}
