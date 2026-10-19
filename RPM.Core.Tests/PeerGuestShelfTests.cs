using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// A friend's collection, kept where it can be viewed without being mixed into this one.
///
/// <para>The separation is the main rule: their records are rows in their own database, so nothing
/// that reads this collection can reach one. These tests check the other half: a shelf borrowed from
/// somebody else still knows which of its records can play when nobody is on the same network, which
/// is the difference between a file on their laptop and a file they put in the cloud.</para>
/// </summary>
public class PeerGuestShelfTests : IDisposable
{
    private const string Friend = "FRIEND1";

    private readonly string _root;
    private readonly LibraryDatabase _shelf;

    public PeerGuestShelfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"guestshelf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _shelf.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <param name="cloudOnly">Their disk no longer has it.</param>
    /// <param name="inCloud">Their storage has a copy. Implied by <paramref name="cloudOnly"/>, and
    /// true on its own for the normal uploaded record that is also still on their disk.</param>
    private static PeerTrack Track(
        string rpmId, string path, bool cloudOnly = false, bool inCloud = false) => new()
    {
        RpmId = rpmId,
        RelativePath = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = "Band",
        AlbumArtist = "Band",
        Album = "Record",
        TrackNumber = 1,
        DurationSeconds = 200,
        SizeBytes = 5_000_000,
        CloudOnly = cloudOnly,
        InCloud = inCloud || cloudOnly,
    };

    [Fact]
    public void A_friends_shelf_is_theirs_from_end_to_end()
    {
        var listing = new[]
        {
            Track("gid-1", "Band/Record/01 One.flac"),
            Track("gid-2", "Band/Record/02 Two.flac"),
        };

        var result = _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        Assert.Equal(2, result.Adopted);
        Assert.All(_shelf.Query(), row =>
        {
            Assert.True(row.PeerAdopted);
            Assert.Equal(Friend, row.PeerDeviceId);
        });
    }

    [Fact]
    public void A_record_on_their_disk_and_one_they_put_in_the_cloud_are_told_apart()
    {
        var listing = new[]
        {
            Track("gid-disk", "Band/Record/01 On their disk.flac"),
            Track("gid-cloud", "Band/Record/02 In the cloud.flac", cloudOnly: true),
        };

        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        // The one on their disk plays on their network and nowhere else; the one in the cloud is the
        // only kind that keeps playing after everybody has left the network.
        Assert.Equal(CloudState.Local, _shelf.FindByRpmId("gid-disk")!.CloudState);
        Assert.Equal(CloudState.CloudOnly, _shelf.FindByRpmId("gid-cloud")!.CloudState);
    }

    [Fact]
    public void A_record_they_have_since_brought_back_down_stops_being_a_cloud_row()
    {
        var offloaded = new[] { Track("gid-1", "Band/Record/01 One.flac", cloudOnly: true) };
        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(offloaded),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(offloaded));

        Assert.Equal(CloudState.CloudOnly, _shelf.FindByRpmId("gid-1")!.CloudState);

        var restored = new[] { Track("gid-1", "Band/Record/01 One.flac") };
        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(restored),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(restored));

        Assert.Equal(CloudState.Local, _shelf.FindByRpmId("gid-1")!.CloudState);
    }

    [Fact]
    public void Nothing_of_a_friends_is_offered_on_to_anybody_else()
    {
        var listing = new[] { Track("gid-1", "Band/Record/01 One.flac") };
        _shelf.MirrorPeerLibrary(Friend, PeerLibraryMirror.ToCatalogRows(listing));

        Assert.Empty(PeerCatalogBuilder.From(_shelf.Query()));
    }

    [Fact]
    public void Anything_of_this_machines_own_that_lands_on_a_borrowed_shelf_is_swept_off_it()
    {
        var listing = new[] { Track("gid-1", "Band/Record/01 One.flac") };
        _shelf.MirrorPeerLibrary(Friend, PeerLibraryMirror.ToCatalogRows(listing));

        // A pass that wrote to "the library" while this shelf was the one on screen. The passes hold
        // the user's own mirror and nothing swaps it, so this cannot happen now. But a shelf written
        // by an older build can still have these rows, and they are one person's collection in a file
        // named after somebody else.
        _shelf.UpsertMany([new LibraryTrack
        {
            RelativePath = "Mine/Album/01 Mine.flac",
            Folder = "Mine/Album",
            FileName = "01 Mine.flac",
            Extension = ".flac",
            Title = "Mine",
            Album = "Album",
            Artist = "Me",
        }]);

        Assert.Equal(2, _shelf.Query().Count);
        Assert.Equal(1, _shelf.ForgetLocalRows());

        var left = Assert.Single(_shelf.Query());
        Assert.True(left.PeerAdopted);
        Assert.Equal(Friend, left.PeerDeviceId);
    }

    [Fact]
    public void With_their_computer_away_only_what_the_cloud_holds_is_on_the_shelf()
    {
        var listing = new[]
        {
            Track("gid-disk", "Band/Record/01 On their disk.flac"),
            Track("gid-cloud", "Band/Record/02 In the cloud.flac", cloudOnly: true),
        };

        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        Assert.Equal(2, _shelf.Query().Count);

        // Their laptop is closed. What is on their disk cannot be reached from here and must no
        // longer be shown as if it could. That is the rule the sharing was agreed under.
        _shelf.CloudRowsOnly = true;

        var left = Assert.Single(_shelf.Query());
        Assert.Equal("gid-cloud", left.RpmId);
        Assert.Single(_shelf.QueryFolderTrackCounts());
        Assert.Single(_shelf.FolderTracks("Band/Record"));

        // Nothing was deleted. It all comes back when they return.
        _shelf.CloudRowsOnly = false;
        Assert.Equal(2, _shelf.Query().Count);
    }

    [Fact]
    public void A_record_they_uploaded_and_kept_is_on_the_shelf_after_their_computer_goes()
    {
        var listing = new[]
        {
            Track("gid-disk", "Band/Record/01 On their disk.flac"),
            Track("gid-synced", "Band/Record/02 Uploaded and kept.flac", inCloud: true),
            Track("gid-offloaded", "Band/Record/03 Offloaded.flac", cloudOnly: true),
        };

        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        _shelf.CloudRowsOnly = true;

        // Almost every record in a cloud collection is in this state: uploaded, and still on the disk
        // it was uploaded from. Reading only the offloaded flag would leave a borrower with the few
        // records their friend removed from their disk, which looks like the sharing is broken.
        var left = _shelf.Query().Select(t => t.RpmId).ToList();
        Assert.Equal(2, left.Count);
        Assert.Contains("gid-synced", left);
        Assert.Contains("gid-offloaded", left);
        Assert.DoesNotContain("gid-disk", left);
    }

    [Fact]
    public void An_older_peers_listing_still_keeps_what_it_could_say_was_in_the_cloud()
    {
        // A build that does not send "in the cloud": the field is absent, which reads as false, and
        // the offloaded flag is the only information left.
        var listing = new[]
        {
            new PeerTrack
            {
                RpmId = "gid-old",
                RelativePath = "Band/Record/01 One.flac",
                Album = "Record",
                Artist = "Band",
                CloudOnly = true,
            },
        };

        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        _shelf.CloudRowsOnly = true;
        Assert.Equal("gid-old", Assert.Single(_shelf.Query()).RpmId);
    }

    [Fact]
    public void A_narrowed_shelf_still_answers_a_search_the_same_way()
    {
        var listing = new[]
        {
            Track("gid-disk", "Band/Record/01 On their disk.flac"),
            Track("gid-cloud", "Band/Record/02 In the cloud.flac", cloudOnly: true),
        };

        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

        _shelf.CloudRowsOnly = true;

        // The search and the rule are combined with AND, not one instead of the other.
        Assert.Single(_shelf.Query("Band"));
        Assert.Empty(_shelf.Query("On their disk"));
    }
    /// <summary>
    /// A friend's classical box, from their library to the shelf shown here: the mark that says it is
    /// classical and the composition each track belongs to must both arrive, because if either one is
    /// missing the result is the same: a flat list of twenty nearly identical titles.
    /// </summary>
    [Fact]
    public void A_borrowed_classical_box_arrives_as_works_under_a_record_that_says_it_is_one()
    {
        var lenderRoot = Path.Combine(Path.GetTempPath(), $"lender-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lenderRoot);
        using var lender = new LibraryDatabase(MirrorLocation.InFolder(lenderRoot));

        try
        {
            lender.Upsert(new LibraryTrack
            {
                RelativePath = "Mozart/Keyboard Works/01 Allegro.flac",
                Folder = "Mozart/Keyboard Works",
                FileName = "01 Allegro.flac",
                Extension = ".flac",
                Title = "Sonata in B-flat, KV 333: I. Allegro",
                Artist = "Bart van Oort",
                AlbumArtist = "Wolfgang Amadeus Mozart",
                Album = "Complete Keyboard Works",
                Work = "Sonata in B-flat, KV 333",
                MovementName = "I. Allegro",
                MovementNumber = 1,
                Composer = "Wolfgang Amadeus Mozart",
                TrackNumber = 1,
                DurationSeconds = 440,
                SizeBytes = 5_000_000,
                RpmId = "gid-1",
            });

            lender.SetAlbumClassical("Mozart/Keyboard Works", true);

            var catalog = PeerCatalogBuilder.Catalog(
                lender.Query(), "Their laptop",
                PeerCatalogBuilder.From(
                    lender.AlbumMetaEdits(), PeerCatalogBuilder.AlbumAudience.AGuest),
                PeerCatalogBuilder.Playlists(lender.PlaylistTree(), new Dictionary<long, string>()), []);

            _shelf.MirrorPeerLibrary(
                Friend, PeerLibraryMirror.ToCatalogRows(catalog.Tracks),
                cloudOnly: PeerLibraryMirror.CloudBackedIds(catalog.Tracks));
            _shelf.ApplyAlbumMetaEdits(
                PeerCatalogBuilder.To(catalog.Albums), ArrivingAlbumMarks.NotTheHeart);

            Assert.True(_shelf.GetAlbumMeta("Mozart/Keyboard Works").Classical);

            var row = Assert.Single(_shelf.QueryUnderFolder("Mozart/Keyboard Works"));
            Assert.Equal("Sonata in B-flat, KV 333", row.Work);
            Assert.Equal("I. Allegro", row.MovementName);
            Assert.Equal("Wolfgang Amadeus Mozart", row.Composer);
        }
        finally
        {
            lender.Dispose();
            try { Directory.Delete(lenderRoot, recursive: true); } catch { }
        }
    }
}
