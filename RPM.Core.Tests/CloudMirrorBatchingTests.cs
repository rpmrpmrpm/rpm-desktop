using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The mirror pass gives the write gate back partway through, and still reconciles.
///
/// <para><b>Why these exist.</b> <c>MirrorCloudLibrary</c> takes the write gate once per batch of
/// records, not once for the whole pass. One transaction for the whole pass holds the gate for about
/// 20 s on a 13,000-track catalogue, and the interface waits behind it. The tests check the boundary
/// between two batches. A catalogue that fits in one batch never crosses it, so every test below
/// uses one that does.</para>
///
/// <para>The batch size is private and these tests do not read it. They use a catalogue several
/// times larger than any likely batch size, so they stay valid if the size changes.</para>
/// </summary>
public class CloudMirrorBatchingTests : IDisposable
{
    /// <summary>Well over one batch, whatever the batch size is. Each album is its own record, so
    /// this is also the number of album groups the pass splits on.</summary>
    private const int Albums = 300;

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudMirrorBatchingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mirror-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static CloudTrack Cloud(int album, int track) => new(
        RpmId: $"gid-{album}-{track}",
        Sha256: $"sha-{album}-{track}",
        RelativePath: $"Band {album}/Record {album}/{track:00} Song.flac",
        OriginalSize: 5_000_000,
        Title: $"Song {track}",
        Artist: $"Band {album}",
        Album: $"Record {album}",
        AddedAt: 1000,
        Tags: new CloudTrackTags(DurationSeconds: 200, TrackNumber: track, AlbumArtist: $"Band {album}"));

    private static List<CloudTrack> Catalog(int albums = Albums, int perAlbum = 3)
    {
        var listing = new List<CloudTrack>(albums * perAlbum);
        for (var a = 0; a < albums; a++)
            for (var t = 1; t <= perAlbum; t++)
                listing.Add(Cloud(a, t));
        return listing;
    }

    private static LibraryTrack Local(string relativePath)
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
        };
    }

    /// <summary>The plain reconcile, across batch boundaries. A catalogue that spans many batches
    /// becomes exactly as many rows as it has tracks, and not one more.</summary>
    [Fact]
    public void A_catalog_larger_than_one_batch_is_adopted_exactly_once()
    {
        var listing = Catalog();

        var result = _db.MirrorCloudLibrary(listing);

        Assert.Equal(listing.Count, result.Adopted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Forgotten);
        Assert.Equal(listing.Count, _db.Count);
    }

    /// <summary>
    /// The property the batching most easily breaks: running the pass again must find nothing to do.
    ///
    /// <para>If the pass's sets go stale across a batch boundary, it does not see its own earlier
    /// rows and adopts some of them a second time. The duplicate shows the album twice, which the pass
    /// must never do.</para>
    /// </summary>
    [Fact]
    public void Running_it_again_over_the_same_catalog_changes_nothing()
    {
        var listing = Catalog();
        _db.MirrorCloudLibrary(listing);

        var again = _db.MirrorCloudLibrary(listing);

        Assert.Equal(0, again.Adopted);
        Assert.Equal(0, again.Updated);
        Assert.Equal(0, again.Forgotten);
        Assert.Equal(listing.Count, _db.Count);
    }

    /// <summary>A row this machine holds is still recognised as held when the pass reaches it in a
    /// late batch, not the first.</summary>
    [Fact]
    public void A_track_this_machine_holds_is_not_adopted_wherever_it_falls_in_the_pass()
    {
        var listing = Catalog();

        // One from near the end of the catalogue, so it is reached well past the first batch.
        var late = listing[^2];
        _db.Upsert(Local(late.RelativePath) with { RpmId = late.RpmId });

        var result = _db.MirrorCloudLibrary(listing);

        Assert.Equal(listing.Count - 1, result.Adopted);
        Assert.Equal(listing.Count, _db.Count);
    }

    /// <summary>When a record leaves the catalogue, exactly that record's rows are forgotten. The prune
    /// runs against the whole listing, not against its last batch.</summary>
    [Fact]
    public void A_record_that_has_left_the_cloud_is_forgotten_and_the_rest_is_not()
    {
        var listing = Catalog();
        _db.MirrorCloudLibrary(listing);

        var shorter = listing.Where(t => t.Album != "Record 7").ToList();
        var result = _db.MirrorCloudLibrary(shorter);

        Assert.Equal(listing.Count - shorter.Count, result.Forgotten);
        Assert.Equal(shorter.Count, _db.Count);
    }

    /// <summary>
    /// Another writer runs while the pass has released the gate.
    ///
    /// <para>The pass reads its three sets once, so after another write they can describe a library
    /// that has changed. <c>WriteTurns</c> handles this. A writer that lands between two batches must
    /// not produce a second row for a track it just made local.</para>
    ///
    /// <para>The interleaving is real, not simulated (a thread writes while the pass runs), so the
    /// batch it lands in is not fixed. The assertion is therefore about the END STATE, which must hold
    /// for any batch: one row per path, and no cloud row for a track this machine holds.</para>
    /// </summary>
    [Fact]
    public async Task A_write_that_lands_between_two_batches_does_not_produce_a_duplicate()
    {
        var listing = Catalog();

        // Every path the writer will claim, chosen from the back half so the pass is well past its
        // first batch by the time they start landing.
        var claimed = listing.Skip(listing.Count / 2).Take(40).ToList();

        var started = new TaskCompletionSource();

        var writer = Task.Run(() =>
        {
            started.SetResult();
            foreach (var track in claimed)
                _db.Upsert(Local(track.RelativePath) with { RpmId = track.RpmId });
        });

        await started.Task;
        _db.MirrorCloudLibrary(listing);
        await writer;

        // Whatever order those landed in, the pass must leave one row per path.
        var rows = _db.Query();
        var byPath = rows
            .GroupBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(byPath);
        Assert.Equal(listing.Count, rows.Count);
    }
}
