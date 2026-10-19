using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// What this machine knows about the covers the store keeps. A wall scrolling past forty records asks
/// forty times, so every wrong answer here is forty of something: forty listings, forty 404s, or forty
/// tiles showing a sleeve that was replaced yesterday.
/// </summary>
public class CloudArtIndexTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The store hashed its key from the file's own tags at upload time; the catalogue this machine reads
    /// may carry no album artist at all, and then the album artist and the track artist are two
    /// different keys for one record.
    /// </summary>
    [Fact]
    public void A_record_is_asked_about_under_both_spellings_of_its_key()
    {
        var keys = CloudArtIndex.KeysFor("Various Artists", "Manu Chao", "Clandestino");

        Assert.Equal(2, keys.Length);
        Assert.Equal(AlbumArt.KeyForTrack("Various Artists", "Manu Chao", "Clandestino"), keys[0]);
        Assert.Equal(AlbumArt.KeyFor("Manu Chao", "Clandestino"), keys[1]);
    }

    /// <summary>Which is most records: one artist, one key, one lookup.</summary>
    [Fact]
    public void A_record_whose_two_spellings_agree_is_asked_about_once()
    {
        var keys = CloudArtIndex.KeysFor("Manu Chao", "Manu Chao", "Clandestino");

        Assert.Single(keys);
    }

    [Fact]
    public void A_record_with_no_album_artist_still_has_a_key()
        => Assert.NotEmpty(CloudArtIndex.KeysFor(null, "Manu Chao", "Clandestino"));

    /// <summary>A cover lives at an address derived from the album, so replacing it does not move it:
    /// the stamp is the only thing that tells a cached sleeve from a corrected one.</summary>
    [Fact]
    public void A_replaced_cover_is_cached_under_a_different_name()
    {
        var key = AlbumArt.KeyFor("Manu Chao", "Clandestino");

        Assert.NotEqual(CloudArtIndex.CacheFileName(key, 1), CloudArtIndex.CacheFileName(key, 2));
    }

    /// <summary>An album key is a hash, but this is a file name being built out of a value that came
    /// off the network.</summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b\\c:d*e?f")]
    [InlineData("")]
    public void A_key_off_the_network_cannot_name_a_file_anywhere_else(string key)
    {
        var name = CloudArtIndex.CacheFileName(key, 3);

        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.EndsWith("-3.img", name, StringComparison.Ordinal);
    }

    /// <summary>Version zero is the picture recovered from a track's sidecar: it came out of a file
    /// rather than from the store, and any real stamp replaces it.</summary>
    [Fact]
    public void A_sleeve_recovered_from_a_sidecar_is_filed_under_no_version()
    {
        var key = AlbumArt.KeyFor("Manu Chao", "Clandestino");

        Assert.Equal(0, CloudArtIndex.FromSidecar);
        Assert.NotEqual(CloudArtIndex.CacheFileName(key, CloudArtIndex.FromSidecar), CloudArtIndex.CacheFileName(key, 1));
    }

    [Fact]
    public void The_listing_answers_for_whichever_key_it_knows()
    {
        var covers = new[] { new StoredCover("second-key", 7) };

        var found = CloudArtIndex.Match(covers, ["first-key", "second-key"]);

        Assert.Equal("second-key", found?.AlbumKey);
        Assert.Equal(7, found?.UpdatedAt);
    }

    [Fact]
    public void A_listing_that_knows_neither_key_answers_nothing()
        => Assert.Null(CloudArtIndex.Match([new StoredCover("other", 1)], ["mine"]));

    [Fact]
    public void An_empty_listing_answers_nothing()
        => Assert.Null(CloudArtIndex.Match([], ["mine"]));

    /// <summary>A duplicate key in the listing is not worth an exception in front of somebody's
    /// covers.</summary>
    [Fact]
    public void A_listing_that_holds_one_album_twice_still_answers()
    {
        var covers = new[] { new StoredCover("key", 1), new StoredCover("key", 9) };

        Assert.Equal(9, CloudArtIndex.Match(covers, ["key"])?.UpdatedAt);
    }

    [Fact]
    public void Keys_are_matched_whatever_case_the_store_wrote_them_in()
        => Assert.NotNull(CloudArtIndex.Match([new StoredCover("ABCDEF", 1)], ["abcdef"]));

    [Fact]
    public void The_listing_is_read_once_and_then_held()
    {
        var index = new CloudArtIndex();

        Assert.True(index.NeedsIndex(Now));           // nothing read yet
        index.NoteIndexRead(Now);

        Assert.False(index.NeedsIndex(Now + TimeSpan.FromMinutes(1)));
        Assert.True(index.NeedsIndex(Now + CloudArtIndex.IndexLife + TimeSpan.FromSeconds(1)));
    }

    /// <summary>A listing that could not be read is asked for again after the short hold, not after
    /// the listing's full life, and nothing is marked blank because of it.</summary>
    [Fact]
    public void A_failed_listing_is_asked_again_after_the_short_hold()
    {
        var index = new CloudArtIndex();
        index.NoteIndexRead(Now);
        index.NoteIndexFailed(Now);

        Assert.False(index.NeedsIndex(Now + CloudArtIndex.FailedIndexHold - TimeSpan.FromSeconds(1)));
        Assert.True(index.NeedsIndex(Now + CloudArtIndex.FailedIndexHold + TimeSpan.FromSeconds(1)));
        Assert.False(index.IsBlank("any album"));
    }

    /// <summary>Without this a wall of records with no covers is a request per tile per scroll.</summary>
    [Fact]
    public void An_album_with_nothing_stored_is_only_asked_about_once()
    {
        var index = new CloudArtIndex();
        index.NoteIndexRead(Now);

        Assert.False(index.IsBlank("key"));
        index.MarkBlank("key");

        Assert.True(index.IsBlank("key"));
    }

    /// <summary>A cover stored since the last read is no longer "nothing there".</summary>
    [Fact]
    public void Reading_the_listing_again_forgets_what_had_nothing()
    {
        var index = new CloudArtIndex();
        index.MarkBlank("key");

        index.NoteIndexRead(Now);

        Assert.False(index.IsBlank("key"));
    }

    /// <summary>
    /// An album that had no art a minute ago is remembered as blank, and one that had a different
    /// picture keeps giving out the old stamp, so choosing a sleeve here has to forget both.
    /// </summary>
    [Fact]
    public void Choosing_a_sleeve_here_forgets_the_held_answer_for_that_album()
    {
        var index = new CloudArtIndex();
        index.NoteIndexRead(Now);
        index.MarkBlank("mine");
        index.MarkBlank("somebody elses");

        index.Invalidate(["mine"]);

        Assert.False(index.IsBlank("mine"));
        Assert.True(index.IsBlank("somebody elses"));   // one album's change does not affect the others
        Assert.True(index.NeedsIndex(Now));             // and the listing is stale the moment it changes
    }

    [Fact]
    public void Signing_out_forgets_all_of_it()
    {
        var index = new CloudArtIndex();
        index.NoteIndexRead(Now);
        index.MarkBlank("key");

        index.Clear();

        Assert.False(index.IsBlank("key"));
        Assert.True(index.NeedsIndex(Now));
    }
}
