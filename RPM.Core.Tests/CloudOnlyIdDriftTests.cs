using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A cloud-only row whose rpm id is not the id the account stores its audio under.
///
/// <para>A cloud-only track is played by asking the store for its id. When the row's id is not in the
/// catalogue, the store answers 404 and the player skips the track. The upload path puts such a row
/// right when it has a file to stamp; a cloud-only row has no file, so the mirror pass does it, from the
/// canonical hash in the same listing.</para>
/// </summary>
public class CloudOnlyIdDriftTests : IDisposable
{
    private const string Path1 = "ABBA/The Definitive Collection/29 - The Day Before You Came.flac";
    private const string Hash = "1eb1399a131ea5b6d4ab08248fe73cb0a2abec1e563961d108526fff95ee5579";
    private const string LocalId = "6f896f6a-944c-40bd-9018-6532513fcc99";
    private const string CloudId = "cc71ab14-905e-44f0-80ec-51ae9222f3db";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudOnlyIdDriftTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"iddrift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CloudTrack Cloud(string rpmId, string sha256, string relativePath) => new(
        RpmId: rpmId,
        Sha256: sha256,
        RelativePath: relativePath,
        OriginalSize: 69_094_013,
        Title: "The Day Before You Came",
        Artist: "ABBA",
        Album: "The Definitive Collection",
        AddedAt: 1000,
        Tags: new CloudTrackTags(DurationSeconds: 347, TrackNumber: 29, AlbumArtist: "ABBA"));

    /// <summary>A track of this library's own that was uploaded and then had its file removed: synced
    /// under <paramref name="hash"/>, now cloud-only.</summary>
    private LibraryTrack CloudOnly(string relativePath, string rpmId, string hash)
    {
        var row = Synced(relativePath, rpmId, hash);
        Assert.True(_db.MarkOffloaded(row.Id));
        return _db.FindByRpmId(rpmId)!;
    }

    private LibraryTrack Synced(string relativePath, string rpmId, string hash)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = relativePath[..slash],
            FileName = relativePath[(slash + 1)..],
            Extension = ".flac",
            SizeBytes = 69_094_013,
            ModifiedUtcTicks = 1,
            Title = "The Day Before You Came",
            Artist = "ABBA",
            AlbumArtist = "ABBA",
            Album = "The Definitive Collection",
            TrackNumber = 29,
            RpmId = rpmId,
        });
        var row = _db.FindByRpmId(rpmId)!;
        _db.SetCloudState(row.Id, CloudState.Uploading);
        Assert.True(_db.MarkSynced(row.Id, hash, 69_019_951, 1));
        return _db.FindByRpmId(rpmId)!;
    }

    [Fact]
    public void A_cloud_only_row_takes_the_id_the_store_holds_its_audio_under()
    {
        var row = CloudOnly(Path1, LocalId, Hash);

        var result = _db.MirrorCloudLibrary([Cloud(CloudId, Hash, Path1)]);

        Assert.Equal(1, result.Renamed);
        var after = Assert.Single(_db.Query());
        Assert.Equal(row.Id, after.Id);
        Assert.Equal(CloudId, after.RpmId);
        Assert.Equal(CloudState.CloudOnly, after.CloudState);
        Assert.Equal(0, _db.CountAdopted());                  // no second row for the same song
        Assert.Equal(CloudId, _db.CurrentRpmId(LocalId));     // the old id still finds the song
    }

    [Fact]
    public void The_playlists_that_named_the_old_id_follow_it()
    {
        var row = CloudOnly(Path1, LocalId, Hash);
        var playlist = _db.CreatePlaylist(null, "Mix");
        _db.AddSongsToPlaylist(playlist, [((string?)LocalId, row.RelativePath)], AlreadyInPlaylist.AddAnother);

        _db.MirrorCloudLibrary([Cloud(CloudId, Hash, Path1)]);

        Assert.Equal(CloudId, Assert.Single(_db.GetPlaylistItems(playlist)).RpmId);
    }

    [Fact]
    public void A_row_the_catalogue_lists_under_its_own_id_is_left_alone()
    {
        CloudOnly(Path1, LocalId, Hash);

        var result = _db.MirrorCloudLibrary([Cloud(LocalId, Hash, Path1), Cloud(CloudId, Hash, "Other/Path.flac")]);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(LocalId, Assert.Single(_db.Query(), t => t.CloudAdopted == false).RpmId);
    }

    [Fact]
    public void A_row_whose_audio_the_catalogue_does_not_hold_keeps_its_id()
    {
        CloudOnly(Path1, LocalId, Hash);

        var result = _db.MirrorCloudLibrary([Cloud(CloudId, "another-hash", "Other/Path.flac")]);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(LocalId, _db.FindByRpmId(LocalId)?.RpmId);
    }

    [Fact]
    public void A_row_with_its_file_here_is_left_to_the_upload_which_stamps_the_file()
    {
        Synced(Path1, LocalId, Hash);

        var result = _db.MirrorCloudLibrary([Cloud(CloudId, Hash, Path1)]);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(LocalId, _db.FindByRpmId(LocalId)?.RpmId);
    }

    [Fact]
    public void An_id_another_row_of_this_library_holds_is_not_taken()
    {
        CloudOnly(Path1, LocalId, Hash);
        Synced("ABBA/Gold/19 - The Day Before You Came.flac", CloudId, Hash);

        var result = _db.MirrorCloudLibrary([Cloud(CloudId, Hash, Path1)]);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(LocalId, _db.FindByRpmId(LocalId)?.RpmId);
    }
}
