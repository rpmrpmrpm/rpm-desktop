using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Per-album metadata (favourite / rating / review / linked pressing) and the cloud roll-up that drives the
/// album tile's badge and its Add/Remove-from-cloud menu split. An album is a folder here, so all of
/// this is keyed by folder, and an album nobody has touched has no row at all.
/// </summary>
public class AlbumMetaTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AlbumMetaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdmeta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
        };
    }

    /// <summary>Adds a track and returns its row (with the assigned id).</summary>
    private LibraryTrack Add(string relativePath)
    {
        _db.Upsert(Track(relativePath));
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    // ── favourite / rating / review ──

    [Fact]
    public void An_untouched_album_reads_back_as_default()
    {
        var meta = _db.GetAlbumMeta("Pink Floyd/The Wall");
        Assert.False(meta.IsFavorite);
        Assert.Equal(0, meta.Rating);
        Assert.Null(meta.ReviewMarkdown);
        Assert.Null(meta.DiscogsReleaseId);
        Assert.False(meta.HasReview);
    }

    [Fact]
    public void Favorite_toggles_and_persists()
    {
        const string folder = "Pink Floyd/The Wall";
        _db.SetAlbumFavorite(folder, true);
        Assert.True(_db.GetAlbumMeta(folder).IsFavorite);

        _db.SetAlbumFavorite(folder, false);
        Assert.False(_db.GetAlbumMeta(folder).IsFavorite);
    }

    [Fact]
    public void A_review_stores_stars_and_markdown()
    {
        const string folder = "Pink Floyd/The Wall";
        _db.SetAlbumReview(folder, rating: 4, reviewMarkdown: "**Comfortably Numb** carries it.", updatedUtcTicks: 123);

        var meta = _db.GetAlbumMeta(folder);
        Assert.Equal(4, meta.Rating);
        Assert.Equal("**Comfortably Numb** carries it.", meta.ReviewMarkdown);
        Assert.Equal(123, meta.ReviewUpdatedUtcTicks);
        Assert.True(meta.HasReview);
    }

    [Fact]
    public void A_rating_is_clamped_to_five_stars()
    {
        const string folder = "a/b";
        _db.SetAlbumReview(folder, rating: 9, reviewMarkdown: null, updatedUtcTicks: 1);
        Assert.Equal(5, _db.GetAlbumMeta(folder).Rating);
    }

    [Fact]
    public void Touching_one_field_leaves_the_others_alone()
    {
        const string folder = "Pink Floyd/The Wall";
        _db.SetAlbumReview(folder, rating: 5, reviewMarkdown: "classic", updatedUtcTicks: 7);
        _db.SetAlbumFavorite(folder, true);
        _db.SetAlbumPressing(folder, DiscogsJson.ProviderId, "release-42");

        // Now turn the favourite off again. The review and the pressing must survive.
        _db.SetAlbumFavorite(folder, false);

        var meta = _db.GetAlbumMeta(folder);
        Assert.False(meta.IsFavorite);
        Assert.Equal(5, meta.Rating);
        Assert.Equal("classic", meta.ReviewMarkdown);
        Assert.Equal("release-42", meta.DiscogsReleaseId);
    }

    [Fact]
    public void The_pressing_roundtrips_and_clears()
    {
        const string folder = "a/b";
        _db.SetAlbumPressing(folder, DiscogsJson.ProviderId, "249504");
        Assert.Equal("249504", _db.GetAlbumMeta(folder).DiscogsReleaseId);
        Assert.True(_db.GetAlbumMeta(folder).IsLinkedToPressing);

        _db.SetAlbumPressing(folder, DiscogsJson.ProviderId, null);
        Assert.Null(_db.GetAlbumMeta(folder).DiscogsReleaseId);
        Assert.False(_db.GetAlbumMeta(folder).IsLinkedToPressing);
    }

    /// <summary>An id belongs to the catalogue that issued it. Reading a MusicBrainz link as a Discogs
    /// one would open a Discogs page for a release number Discogs never issued.</summary>
    [Fact]
    public void A_release_id_is_only_read_by_the_catalogue_that_gave_it()
    {
        const string folder = "a/b";
        _db.SetAlbumPressing(folder, RpmMetadataProvider.ProviderId, "66278f7b-bcab-49b5-8c9a-fa76dcf51486");

        var meta = _db.GetAlbumMeta(folder);
        Assert.Null(meta.DiscogsReleaseId);
        Assert.Equal("66278f7b-bcab-49b5-8c9a-fa76dcf51486", meta.MusicBrainzReleaseId);
        Assert.True(meta.IsLinkedToPressing);
    }

    /// <summary>The disc an album was ripped from is not something a catalogue said, so it survives a
    /// pressing being linked, changed or cleared.</summary>
    [Fact]
    public void The_disc_id_outlives_the_link()
    {
        const string folder = "a/b";
        _db.SetAlbumDiscId(folder, "xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-");
        _db.SetAlbumPressing(folder, DiscogsJson.ProviderId, "430456");
        _db.SetAlbumPressing(folder, DiscogsJson.ProviderId, null);

        Assert.Equal("xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-", _db.GetAlbumMeta(folder).DiscId);
    }

    [Fact]
    public void The_rpm_album_id_roundtrips()
    {
        const string folder = "a/b";
        _db.SetAlbumRpmId(folder, "alb_7f2a1c");
        Assert.Equal("alb_7f2a1c", _db.GetAlbumMeta(folder).RpmAlbumId);
    }

    [Fact]
    public void The_barcode_is_kept_beside_the_catalogue_number()
    {
        const string folder = "a/b";
        _db.SetAlbumRelease(folder, "SRK 5203", "1977-02-04", "0724384578329");

        var meta = _db.GetAlbumMeta(folder);
        Assert.Equal("SRK 5203", meta.Catno);
        Assert.Equal("1977-02-04", meta.Released);
        Assert.Equal("0724384578329", meta.Barcode);
    }

    // ── the styles a release is filed under ──

    /// <summary>The words come back as they went in, joined on semicolons: a style may hold a comma,
    /// so a comma cannot be what separates them.</summary>
    [Fact]
    public void The_styles_of_a_release_roundtrip()
    {
        const string folder = "a/b";
        _db.SetAlbumStyles(folder, ["Deep House", "Nu-Disco"]);

        Assert.Equal("Deep House; Nu-Disco", _db.GetAlbumMeta(folder).Styles);
    }

    /// <summary>
    /// Empty and null are different answers, and the sweep depends on the difference.
    ///
    /// <para>Null is nobody having asked. Empty is Discogs filing the record under a genre and no
    /// style, which IS an answer, and it is the only thing that stops that album being offered to the
    /// pass again for ever.</para>
    /// </summary>
    [Fact]
    public void Asked_and_filed_under_no_style_is_not_the_same_as_never_asked()
    {
        Assert.Null(_db.GetAlbumMeta("never/asked").Styles);

        _db.SetAlbumStyles("asked/none", []);
        Assert.Equal("", _db.GetAlbumMeta("asked/none").Styles);
    }

    /// <summary>The pass walks the albums linked to a Discogs release that nobody has read the styles
    /// from, and stops offering one as soon as it has an answer, empty or not.</summary>
    [Fact]
    public void The_sweep_is_offered_linked_albums_until_they_have_an_answer()
    {
        _db.SetAlbumPressing("linked/unasked", DiscogsJson.ProviderId, "204216");
        _db.SetAlbumPressing("linked/asked", DiscogsJson.ProviderId, "921908");
        _db.SetAlbumPressing("linked/nostyles", DiscogsJson.ProviderId, "26797544");
        _db.SetAlbumStyles("linked/asked", ["Italo-Disco"]);
        _db.SetAlbumStyles("linked/nostyles", []);

        // Linked to a different catalogue: a Discogs release id is what this pass asks by, and an
        // MBID is not one.
        _db.SetAlbumPressing("linked/elsewhere", RpmMetadataProvider.ProviderId, "2fddb92d-24b2");

        var offered = _db.NextAlbumsWithoutStyles(40);

        Assert.Equal(["linked/unasked"], offered.Select(a => a.Folder));
        Assert.Equal("204216", offered[0].ReleaseId);
    }

    [Fact]
    public void GetAlbumMetaMany_returns_only_the_touched_albums()
    {
        _db.SetAlbumFavorite("Artist/One", true);
        _db.SetAlbumReview("Artist/Two", 3, "ok", 1);

        var map = _db.GetAlbumMetaMany(["Artist/One", "Artist/Two", "Artist/Three"]);

        Assert.True(map.ContainsKey("Artist/One"));
        Assert.True(map["Artist/One"].IsFavorite);
        Assert.Equal(3, map["Artist/Two"].Rating);
        Assert.False(map.ContainsKey("Artist/Three"));   // never touched: no row
    }

    // ── an album's tracks and its cloud roll-up ──

    [Fact]
    public void Folder_track_ids_and_rows_cover_the_whole_album()
    {
        Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");
        Add("Artist/Other/3.mp3");

        Assert.Equal(2, _db.FolderTrackIds("Artist/Album").Count);
        Assert.Equal(2, _db.FolderTracks("Artist/Album").Count);
        Assert.Single(_db.FolderTrackIds("Artist/Other"));
    }

    [Fact]
    public void An_all_local_album_shows_no_badge_and_can_only_be_added()
    {
        Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");

        var roll = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(2, roll.Total);
        Assert.Equal(0, roll.InCloud);
        Assert.Equal(AlbumCloudBadge.None, roll.Badge);
        Assert.True(roll.CanAdd);
        Assert.False(roll.CanRemove);
    }

    [Fact]
    public void A_fully_synced_album_is_full_and_can_only_be_removed()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SeedSynced(b.Id, "h2", 1, 1);

        var roll = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.True(roll.FullyInCloud);
        Assert.Equal(AlbumCloudBadge.Full, roll.Badge);
        Assert.False(roll.CanAdd);
        Assert.True(roll.CanRemove);
    }

    [Fact]
    public void A_half_synced_album_is_partial_and_offers_both()
    {
        var a = Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);

        var roll = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(1, roll.InCloud);
        Assert.Equal(AlbumCloudBadge.Partial, roll.Badge);
        Assert.True(roll.CanAdd);
        Assert.True(roll.CanRemove);
    }

    [Fact]
    public void A_chosen_but_not_yet_uploaded_album_reads_as_partial()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SetCloudSelected([a.Id, b.Id], selected: true);   // picked, nothing uploaded yet

        var roll = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(0, roll.InCloud);
        Assert.Equal(2, roll.InSet);
        Assert.Equal(AlbumCloudBadge.Partial, roll.Badge);
        Assert.False(roll.CanAdd);    // every track is already in the set
        Assert.True(roll.CanRemove);
    }

    [Fact]
    public void An_album_that_is_only_in_the_cloud_wears_its_own_badge()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SeedSynced(b.Id, "h2", 1, 1);
        _db.MarkOffloaded(a.Id);
        _db.MarkOffloaded(b.Id);

        var roll = _db.GetAlbumCloudRollup("Artist/Album");

        // Still fully in the cloud, so the menu does not change. What changes is what the tile says:
        // a record using none of this disk cannot look identical to one that is merely backed up.
        Assert.True(roll.FullyInCloud);
        Assert.Equal(2, roll.NotHere);
        Assert.Equal(AlbumCloudBadge.Offloaded, roll.Badge);
        Assert.True(roll.CanRemove);
    }

    [Fact]
    public void An_album_half_taken_off_the_disk_still_reads_as_full()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SeedSynced(b.Id, "h2", 1, 1);
        _db.MarkOffloaded(a.Id);

        // One file is still here, so the record is still taking up room and must not claim otherwise.
        var roll = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(1, roll.NotHere);
        Assert.Equal(AlbumCloudBadge.Full, roll.Badge);
    }

    [Fact]
    public void An_upload_in_flight_still_beats_the_offloaded_badge()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.MarkOffloaded(a.Id);
        _db.SetCloudState(b.Id, Cloud.CloudState.Uploading);

        // The user is waiting on something. That outranks describing where the bytes have ended up.
        Assert.Equal(AlbumCloudBadge.Working, _db.GetAlbumCloudRollup("Artist/Album").Badge);
    }

    /// <summary>
    /// The same record, with the engine stopped because there is no room: nobody is waiting on
    /// anything, because nothing is happening. The mark falls back to what the record really is, not a
    /// cloud with an arrow. That mark is the one that pulses, so this stops the wall pulsing for days
    /// over a queue that is not moving.
    /// </summary>
    [Fact]
    public void A_stopped_queue_is_not_work_in_flight()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SetCloudState(b.Id, Cloud.CloudState.Queued);

        var roll = _db.GetAlbumCloudRollup("Artist/Album");

        Assert.Equal(AlbumCloudBadge.Working, roll.BadgeWhen(uploadsStopped: false));
        Assert.Equal(AlbumCloudBadge.Partial, roll.BadgeWhen(uploadsStopped: true));
    }

    /// <summary>A record wholly up there says so either way: the stop only silences a claim about work
    /// that is not happening, and where the bytes are is not a claim about work.</summary>
    [Fact]
    public void A_stopped_queue_leaves_the_settled_records_alone()
    {
        var a = Add("Artist/Album/1.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.MarkOffloaded(a.Id);

        Assert.Equal(AlbumCloudBadge.Offloaded,
            _db.GetAlbumCloudRollup("Artist/Album").BadgeWhen(uploadsStopped: true));
    }

    // ── the same records read on somebody else's shelf ──

    /// <summary>
    /// Why this reading exists. A borrowed shelf's rows are all written as cloud rows so that they
    /// survive the lender going offline, so the ordinary reading would call every record on it
    /// offloaded: a wall of identical cyan clouds saying "using none of your disk". That is true of
    /// every record on a shelf that is not yours, so it tells the user nothing.
    /// </summary>
    [Fact]
    public void A_borrowed_record_their_computer_can_serve_is_not_read_as_offloaded()
    {
        var a = Add("Artist/Album/1.mp3");
        var b = Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SeedSynced(b.Id, "h2", 1, 1);
        _db.MarkOffloaded(a.Id);
        _db.MarkOffloaded(b.Id);

        var roll = _db.GetAlbumCloudRollup("Artist/Album");

        Assert.Equal(AlbumCloudBadge.Offloaded, roll.Badge);   // on your own shelf, unchanged
        Assert.Equal(AlbumCloudBadge.Full, roll.BadgeOnBorrowedShelf(ownerReachable: true));
    }

    /// <summary>Their computer is off, so the store is the only way the record arrives. This is the
    /// one case where cyan is correct on a borrowed shelf.</summary>
    [Fact]
    public void A_borrowed_record_reaching_you_only_through_the_store_stays_cyan()
    {
        var a = Add("Artist/Album/1.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.MarkOffloaded(a.Id);

        Assert.Equal(AlbumCloudBadge.Offloaded,
            _db.GetAlbumCloudRollup("Artist/Album").BadgeOnBorrowedShelf(ownerReachable: false));
    }

    /// <summary>Nothing of it is in the store: it plays over the link to their computer, and stops
    /// being there when that computer does.</summary>
    [Fact]
    public void A_borrowed_record_only_on_their_disk_wears_the_link()
    {
        Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");

        var roll = _db.GetAlbumCloudRollup("Artist/Album");

        Assert.Equal(AlbumCloudBadge.None, roll.Badge);        // your own: nothing to say
        Assert.Equal(AlbumCloudBadge.PeerLink, roll.BadgeOnBorrowedShelf(ownerReachable: true));
        Assert.Equal(AlbumCloudBadge.PeerLink, roll.BadgeOnBorrowedShelf(ownerReachable: false));
    }

    /// <summary>Half of it needs their computer. That is neither of the two clean answers, and the
    /// outline cloud is what the shelf already uses for "some of it".</summary>
    [Fact]
    public void A_borrowed_record_the_store_half_holds_is_partial()
    {
        var a = Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.MarkOffloaded(a.Id);

        Assert.Equal(AlbumCloudBadge.Partial,
            _db.GetAlbumCloudRollup("Artist/Album").BadgeOnBorrowedShelf(ownerReachable: true));
    }

    /// <summary>
    /// Once that computer is off, the half it was serving is not on the shelf any more. The
    /// rollup counts what the shelf shows, so the record reads as the whole thing it now is: entirely
    /// reachable through the store.
    /// </summary>
    [Fact]
    public void A_narrowed_shelf_rolls_up_only_the_rows_it_is_showing()
    {
        var a = Add("Artist/Album/1.mp3");
        Add("Artist/Album/2.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.MarkOffloaded(a.Id);

        _db.CloudRowsOnly = true;
        try
        {
            var roll = _db.GetAlbumCloudRollup("Artist/Album");
            Assert.Equal(1, roll.Total);
            Assert.Equal(AlbumCloudBadge.Offloaded, roll.BadgeOnBorrowedShelf(ownerReachable: false));
        }
        finally
        {
            _db.CloudRowsOnly = false;
        }
    }
}
