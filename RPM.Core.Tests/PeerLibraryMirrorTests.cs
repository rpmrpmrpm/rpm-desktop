using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// One collection across a user's own computers: what the laptop holds shows up on the desktop.
///
/// <para>The rules are the same as the cloud's, and that is what the tests check: a record already on
/// this shelf must not be shown twice because the other machine also has it, whether "already here"
/// means the same file, the same audio, the same path, or the same album filed under another name.
/// The extra case: a row this library shows from the cloud whose file is <em>also</em> on that
/// computer keeps its own row and only learns there is a nearer copy.</para>
/// </summary>
public class PeerLibraryMirrorTests : IDisposable
{
    private const string Laptop = "LAPTOP1";
    private const string Desktop = "DESKTOP2";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PeerLibraryMirrorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"peermirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static PeerTrack Peer(
        string rpmId, string relativePath, string? title = "Song", string? artist = "Band",
        string? album = "Record", string? hash = null, uint trackNumber = 1)
        => new()
        {
            RpmId = rpmId,
            RelativePath = relativePath,
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            TrackNumber = trackNumber,
            DurationSeconds = 200,
            SizeBytes = 5_000_000,
            ContentHash = hash,
        };

    private static IReadOnlyList<CloudTrack> Listing(params PeerTrack[] tracks) =>
        PeerLibraryMirror.ToCatalogRows(tracks);

    private static LibraryTrack Local(string relativePath, string? rpmId = null, string? hash = null)
    {
        var cut = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = cut < 0 ? "" : relativePath[..cut],
            FileName = cut < 0 ? relativePath : relativePath[(cut + 1)..],
            Extension = Path.GetExtension(relativePath).ToLowerInvariant(),
            SizeBytes = 10,
            ModifiedUtcTicks = 5,
            RpmId = rpmId,
            ContentHash = hash,
        };
    }

    [Fact]
    public void Music_on_the_other_computer_shows_up_here()
    {
        var result = _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Peer("gid-2", "Band/Record/02 Two.flac", title: "Two", trackNumber: 2)));

        Assert.Equal(2, result.Adopted);

        var rows = _db.QueryUnderFolder("Band/Record");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.PeerAdopted);
            Assert.Equal(Laptop, r.PeerDeviceId);

            // Not the cloud's row, and not offloaded: the bytes are on a computer, not in a bucket.
            // A row that claimed either would be streamed from a place that never had it.
            Assert.False(r.CloudAdopted);
            Assert.Equal(CloudState.Local, r.CloudState);
        });
        Assert.Equal(["One", "Two"], rows.OrderBy(r => r.TrackNumber).Select(r => r.Title));
    }

    [Fact]
    public void Running_twice_changes_nothing()
    {
        var listing = Listing(Peer("gid-1", "Band/Record/01 One.flac"));

        Assert.Equal(1, _db.MirrorPeerLibrary(Laptop, listing).Adopted);

        var again = _db.MirrorPeerLibrary(Laptop, listing);

        Assert.Equal(0, again.Total);
        Assert.Equal(1, _db.CountPeerAdopted());
    }

    [Fact]
    public void A_track_this_machine_already_holds_is_not_adopted()
    {
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));

        var result = _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        Assert.Equal(0, result.Adopted);
        Assert.Equal(0, _db.CountPeerAdopted());
        Assert.Single(_db.QueryUnderFolder("Band/Record"));
    }

    [Fact]
    public void The_same_audio_under_another_id_is_not_adopted()
    {
        _db.Upsert(Local("Mine/Song.flac", rpmId: "mine-1", hash: "sha-abc"));

        var result = _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("theirs-1", "Band/Record/01 One.flac", hash: "sha-abc")));

        Assert.Equal(0, result.Adopted);
    }

    [Fact]
    public void A_record_already_on_the_shelf_under_another_name_is_not_drawn_twice()
    {
        // Filed here by one naming template, there by another. Same album artist, same title.
        _db.Upsert(Local("Band - Record/01 One.flac", rpmId: "mine-1") with
        {
            Album = "Record",
            AlbumArtist = "Band",
            Artist = "Band",
        });

        var result = _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("theirs-1", "Band/Record/01 One.flac")));

        Assert.Equal(0, result.Adopted);
        Assert.Empty(_db.QueryUnderFolder("Band/Record"));
    }

    [Fact]
    public void A_row_the_cloud_already_put_here_learns_there_is_a_nearer_copy()
    {
        _db.MirrorCloudLibrary([new CloudTrack(
            RpmId: "gid-1", Sha256: "sha-1", RelativePath: "Band/Record/01 One.flac",
            OriginalSize: 5_000_000, Title: "One", Artist: "Band", Album: "Record", AddedAt: 10)]);

        var result = _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        // Nothing adopted: the row is already there. What changed is that it now knows the laptop
        // has the file, so it plays from the network instead of from the cloud.
        Assert.Equal(0, result.Adopted);
        Assert.Equal(1, result.Reachable);

        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.True(row.CloudAdopted);
        Assert.False(row.PeerAdopted);
        Assert.Equal(Laptop, row.PeerDeviceId);
        Assert.Equal(CloudState.CloudOnly, row.CloudState);
    }

    [Fact]
    public void A_record_taken_off_the_other_computer_leaves_here_too()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/01 One.flac"),
            Peer("gid-2", "Band/Record/02 Two.flac")));

        var result = _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        Assert.Equal(1, result.Forgotten);
        Assert.Equal(1, _db.CountPeerAdopted());
    }

    [Fact]
    public void A_record_renumbered_on_the_other_computer_moves_here_too()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Peer("gid-2", "Band/Record/02 Two.flac", title: "Two", trackNumber: 2)));

        // On the other computer the first track was deleted and the second renumbered into its place,
        // so the path is still held here by a row that is about to go. One of the two has to move
        // first, and the pass must work in either order.
        var result = _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-2", "Band/Record/01 One.flac", title: "Two", trackNumber: 1)));

        Assert.Equal(1, result.Updated);

        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal("gid-2", row.RpmId);
        Assert.Equal("Band/Record/01 One.flac", row.RelativePath);
    }

    [Fact]
    public void Two_records_that_swapped_names_over_there_swap_here()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Peer("gid-2", "Band/Record/02 Two.flac", title: "Two", trackNumber: 2)));

        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/02 Two.flac", title: "One", trackNumber: 2),
            Peer("gid-2", "Band/Record/01 One.flac", title: "Two", trackNumber: 1)));

        var rows = _db.QueryUnderFolder("Band/Record").OrderBy(r => r.RelativePath).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["gid-2", "gid-1"], rows.Select(r => r.RpmId));
        Assert.All(rows, r => Assert.True(r.PeerAdopted));
    }

    [Fact]
    public void A_partial_listing_does_not_read_as_a_deletion()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Band/Record/01 One.flac"),
            Peer("gid-2", "Band/Record/02 Two.flac")));

        var result = _db.MirrorPeerLibrary(
            Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")), prune: false);

        Assert.Equal(0, result.Forgotten);
        Assert.Equal(2, _db.CountPeerAdopted());
    }

    [Fact]
    public void Two_computers_keep_their_own_rows()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "One/First/01 One.flac", artist: "One", album: "First")));
        _db.MirrorPeerLibrary(Desktop, Listing(
            Peer("gid-2", "Two/Second/01 Two.flac", artist: "Two", album: "Second")));

        Assert.Equal(2, _db.CountPeerAdopted());

        // Forgetting one computer removes only its music.
        Assert.Equal(1, _db.ForgetPeer(Laptop));
        Assert.Equal(1, _db.CountPeerAdopted());
        Assert.Equal(Desktop, _db.Query().Single(t => t.PeerAdopted).PeerDeviceId);
    }

    [Fact]
    public void Forgetting_a_computer_takes_its_name_off_rows_it_only_lent_an_address_to()
    {
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));
        _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        Assert.Equal(Laptop, _db.Query().Single().PeerDeviceId);

        _db.ForgetPeer(Laptop);

        var row = _db.Query().Single();
        Assert.Null(row.PeerDeviceId);
        Assert.False(row.PeerAdopted);
    }

    [Fact]
    public void Signing_out_takes_every_computers_music_away()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "One/First/01 One.flac", artist: "One", album: "First")));
        _db.MirrorPeerLibrary(Desktop, Listing(
            Peer("gid-2", "Two/Second/01 Two.flac", artist: "Two", album: "Second")));

        Assert.Equal(2, _db.ForgetAllPeers());
        Assert.Equal(0, _db.CountPeerAdopted());
    }

    [Fact]
    public void A_file_that_turns_up_here_stops_being_the_other_computers()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        var row = _db.Query().Single();
        _db.ClearPeerAdopted(row.Id);

        var after = _db.Query().Single();
        Assert.False(after.PeerAdopted);
        Assert.Null(after.PeerDeviceId);

        // It stays: the next pass over that computer's listing must not re-adopt a track this
        // library now holds itself.
        var again = _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));
        Assert.Equal(0, again.Adopted);
    }

    [Fact]
    public void A_borrowed_row_is_not_offered_back_to_anybody()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-1", "Band/Record/01 One.flac")));

        // A sharing B's music with C is a chain nobody agreed to, and two machines pointed at each
        // other would pass the same rows back and forth for ever.
        Assert.Empty(PeerCatalogBuilder.From(_db.Query()));
    }

    [Fact]
    public void What_this_machine_holds_is_what_it_offers()
    {
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));
        _db.MirrorPeerLibrary(Laptop, Listing(Peer("gid-2", "Other/Record/01 Two.flac")));

        var offered = PeerCatalogBuilder.From(_db.Query());

        Assert.Equal("gid-1", Assert.Single(offered).RpmId);
    }

    /// <summary>
    /// A borrowed classical box is shown as works with their movements, which is only possible if the
    /// composition each track belongs to came with it. The listing has those fields; the row the
    /// mirror writes must keep them, or every borrowed box is twenty untitled tracks.
    /// </summary>
    [Fact]
    public void A_borrowed_classical_record_keeps_its_works_and_movements()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            new PeerTrack
            {
                RpmId = "gid-1",
                RelativePath = "Mozart/Keyboard Works/01 Allegro.flac",
                Title = "Sonata in B-flat, KV 333: I. Allegro",
                Artist = "Bart van Oort",
                Album = "Complete Keyboard Works",
                Work = "Sonata in B-flat, KV 333",
                MovementName = "I. Allegro",
                MovementNumber = 1,
                Composer = "Wolfgang Amadeus Mozart",
                DurationSeconds = 400,
                SizeBytes = 5_000_000,
            }));

        var row = Assert.Single(_db.QueryUnderFolder("Mozart/Keyboard Works"));
        Assert.Equal("Sonata in B-flat, KV 333", row.Work);
        Assert.Equal("I. Allegro", row.MovementName);
        Assert.Equal(1, row.MovementNumber);
        Assert.Equal("Wolfgang Amadeus Mozart", row.Composer);
    }

    /// <summary>The same for a shelf that was copied before its owner tagged the works: the next pass
    /// must notice, and a comparison that does not check the work would call the row unchanged for
    /// ever.</summary>
    [Fact]
    public void A_shelf_taken_before_the_works_were_tagged_learns_them()
    {
        _db.MirrorPeerLibrary(Laptop, Listing(
            Peer("gid-1", "Mozart/Keyboard Works/01 Allegro.flac", title: "I. Allegro")));

        _db.MirrorPeerLibrary(Laptop, Listing(
            new PeerTrack
            {
                RpmId = "gid-1",
                RelativePath = "Mozart/Keyboard Works/01 Allegro.flac",
                Title = "I. Allegro",
                Artist = "Band",
                AlbumArtist = "Band",
                Album = "Record",
                TrackNumber = 1,
                Work = "Sonata in B-flat, KV 333",
                MovementName = "I. Allegro",
                MovementNumber = 1,
                Composer = "Wolfgang Amadeus Mozart",
                DurationSeconds = 200,
                SizeBytes = 5_000_000,
            }));

        var row = Assert.Single(_db.QueryUnderFolder("Mozart/Keyboard Works"));
        Assert.Equal("Sonata in B-flat, KV 333", row.Work);
        Assert.Equal("Wolfgang Amadeus Mozart", row.Composer);
    }

    /// <summary>
    /// The same file twice on that computer, so its listing names one track id at two paths.
    ///
    /// <para>The id is stored in the file, so copying the file copies the id, and a library has no
    /// unique index on it. The pass keys the rows it already holds by id. Without care, a listing like
    /// this adopts two rows and then, on the next pass, tries to move one of them onto the other's
    /// path, which the path index refuses: "UNIQUE constraint failed: tracks.relative_path". One id is
    /// one row, the same way one path is one row.</para>
    /// </summary>
    [Fact]
    public void One_track_id_at_two_paths_is_one_row()
    {
        var listing = Listing(
            Peer("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Peer("gid-1", "Band/Record/01 One (copy).flac", title: "One", trackNumber: 1));

        _db.MirrorPeerLibrary(Laptop, listing);

        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal("gid-1", row.RpmId);

        // The pass becomes stable: nothing to report on a listing that has not changed.
        Assert.Equal(0, _db.MirrorPeerLibrary(Laptop, listing).Total);
    }

    /// <summary>A mirror written by an older build can already hold the pair. The pass must merge it,
    /// not fail on it, or that shelf never updates again: the row it keeps is one of the two, and
    /// moving it onto the other's path is the constraint failure.</summary>
    [Fact]
    public void A_shelf_that_already_holds_the_pair_is_collapsed()
    {
        var listing = Listing(
            Peer("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1));

        _db.MirrorPeerLibrary(Laptop, listing);
        CopyRowTo("Band/Record/01 One (copy).flac");

        Assert.Equal(2, _db.QueryUnderFolder("Band/Record").Count);

        var result = _db.MirrorPeerLibrary(Laptop, listing);

        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal("gid-1", row.RpmId);
        Assert.Equal("Band/Record/01 One.flac", row.RelativePath);
        Assert.Equal(1, result.Forgotten);
    }

    /// <summary>The second row an older build could write: the same adopted row under a second path,
    /// with the same id. Written directly to the database because the current mirror code cannot
    /// produce one, which is the point of the test.</summary>
    private void CopyRowTo(string relativePath)
    {
        using var connection = new SqliteConnection($"Data Source={MirrorLocation.InFolder(_root).DatabasePath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                modified_utc_ticks, duration_seconds, title, artist, album_artist,
                                album, genre, track_number, disc_number, disc_count, year,
                                rpm_id, content_hash, cloud_state, peer_adopted, peer_device_id)
            SELECT $path, folder, $name, extension, size_bytes,
                   modified_utc_ticks, duration_seconds, title, artist, album_artist,
                   album, genre, track_number, disc_number, disc_count, year,
                   rpm_id, content_hash, cloud_state, peer_adopted, peer_device_id
            FROM tracks WHERE peer_adopted = 1 ORDER BY id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.Parameters.AddWithValue("$name", relativePath[(relativePath.LastIndexOf('/') + 1)..]);
        cmd.ExecuteNonQuery();
    }
}
