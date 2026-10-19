using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A favourite made on one machine, seen on the next: the mark is stored on the catalogue row (so a
/// phone can draw hearts without fetching a sidecar per song) and in the sidecar (so a library rebuilt
/// from the cloud gets them back).
///
/// <para>The mark is the only field in the catalogue where false is a real value. Every other field
/// treats a blank or a zero as "not known" and lets the other side keep what it has. Removing a heart
/// is as real a change as adding one, and it must be synced.</para>
/// </summary>
public class FavoriteSyncTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private static LibraryTrack Mine(
        bool favorite = false,
        long favoritedAt = 0,
        long modified = 100,
        long synced = 100,
        long edited = 0,
        long accepted = 500) => new()
        {
            Id = 1,
            RelativePath = "Cornershop/7th Time/02 Brimful of Asha.flac",
            Folder = "Cornershop/7th Time",
            FileName = "02 Brimful of Asha.flac",
            Extension = ".flac",
            RpmId = Id,
            Title = "Brimful of Asha",
            Artist = "Cornershop",
            Album = "When I Was Born for the 7th Time",
            AlbumArtist = "Cornershop",
            DurationSeconds = 318,
            TrackNumber = 2,
            DiscNumber = 1,
            Year = 1997,
            Channels = 2,
            SampleRate = 44100,
            BitsPerSample = 16,
            CloudState = CloudState.Synced,
            ModifiedUtcTicks = modified,
            CloudSyncedUtcTicks = synced,
            MetadataEditedUtcTicks = edited,
            CloudUpdatedAt = accepted,
            IsFavorite = favorite,
            FavoritedUtcTicks = favoritedAt,
        };

    private static CloudTrack Theirs(bool favorite = false, long favoritedAt = 0, long updatedAt = 500) =>
        new(Id, "sha", "Cornershop/7th Time/02 Brimful of Asha.flac", 1000,
            "Brimful of Asha", "Cornershop", "When I Was Born for the 7th Time", 0,
            new CloudTrackTags(318, 2, 0, 1, 0, 1997, null, "Cornershop", ".flac", 2, 44100, 16,
                Favorite: favorite, FavoritedAt: favoritedAt),
            updatedAt);

    // ── this machine's mark goes up ──

    [Fact]
    public void A_song_marked_here_is_pushed()
    {
        var push = CatalogReconciler.Differs(Mine(favorite: true, favoritedAt: 4242), Theirs());

        Assert.False(push.IsEmpty);
        Assert.True(push.Tags!.Favorite);
        Assert.Equal(4242, push.Tags.FavoritedAt);
    }

    [Fact]
    public void A_mark_taken_off_here_is_pushed_too()
    {
        var push = CatalogReconciler.Differs(Mine(favorite: false), Theirs(favorite: true, favoritedAt: 4242));

        Assert.False(push.IsEmpty);
        Assert.False(push.Tags!.Favorite);
    }

    [Fact]
    public void Two_sides_that_agree_about_the_mark_send_nothing()
        => Assert.True(CatalogReconciler
            .Differs(Mine(favorite: true, favoritedAt: 4242), Theirs(favorite: true, favoritedAt: 4242))
            .IsEmpty);

    /// <summary>The mark is not a tag, so nothing about the file changes when it is made. That is why
    /// the row is stamped as edited here. Without that the push side would never look at it.</summary>
    [Fact]
    public void A_mark_made_here_counts_as_an_edit_made_here()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(favorite: true, favoritedAt: 4242, edited: 900, synced: 100)],
            [Theirs(updatedAt: 500)]);

        Assert.Single(plan.Push);
        Assert.Empty(plan.Pull);
    }

    // ── another machine's mark comes down ──

    [Fact]
    public void A_song_marked_elsewhere_comes_down()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(favorite: false)],
            [Theirs(favorite: true, favoritedAt: 4242, updatedAt: 900)]);

        var pull = Assert.Single(plan.Pull);
        var applied = CloudSyncPlan.Apply(pull.Row, pull.Cloud);

        Assert.True(applied.IsFavorite);
        Assert.Equal(4242, applied.FavoritedUtcTicks);
    }

    [Fact]
    public void A_mark_taken_off_elsewhere_comes_off_here()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(favorite: true, favoritedAt: 4242)],
            [Theirs(favorite: false, updatedAt: 900)]);

        var pull = Assert.Single(plan.Pull);
        var applied = CloudSyncPlan.Apply(pull.Row, pull.Cloud);

        Assert.False(applied.IsFavorite);
        Assert.Equal(0, applied.FavoritedUtcTicks);
    }

    /// <summary>A catalogue row without the mark field has no tags at all. That does not mean "not a
    /// favourite": reading it that way would clear a library's hearts as soon as it reads an old
    /// row.</summary>
    [Fact]
    public void A_row_that_carries_no_tags_at_all_says_nothing_about_the_mark()
    {
        var old = new CloudTrack(
            Id, "sha", "Cornershop/7th Time/02 Brimful of Asha.flac", 1000,
            "Brimful of Asha", "Cornershop", "When I Was Born for the 7th Time", 0, null, 900);

        var applied = CloudSyncPlan.Apply(Mine(favorite: true, favoritedAt: 4242), old);

        Assert.True(applied.IsFavorite);
        Assert.Equal(4242, applied.FavoritedUtcTicks);
    }

    // ── a library adopted from the cloud ──

    [Fact]
    public void An_adopted_row_arrives_with_the_mark_the_catalog_holds()
    {
        var row = CloudLibraryMirror.ToTrack(Theirs(favorite: true, favoritedAt: 4242));

        Assert.True(row.IsFavorite);
        Assert.Equal(4242, row.FavoritedUtcTicks);
    }

    [Fact]
    public void An_adopted_row_whose_mark_changed_is_not_the_same_row()
    {
        var standing = CloudLibraryMirror.ToTrack(Theirs(favorite: false));
        var fresh = CloudLibraryMirror.ToTrack(Theirs(favorite: true, favoritedAt: 4242));

        Assert.False(CloudLibraryMirror.Same(standing, fresh));
    }
}
