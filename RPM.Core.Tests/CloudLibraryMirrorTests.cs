using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The rule that lets a second install show the whole collection: the cloud catalogue becomes rows,
/// except where this machine already holds the track.
/// </summary>
public class CloudLibraryMirrorTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudLibraryMirrorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static CloudTrack Cloud(
        string rpmId,
        string relativePath,
        string? title = "Song",
        string? artist = "Band",
        string? album = "Record",
        string sha = "",
        long addedAt = 1000,
        int trackNumber = 1,
        double seconds = 200)
        => new(
            RpmId: rpmId,
            Sha256: sha.Length > 0 ? sha : rpmId + "-sha",
            RelativePath: relativePath,
            OriginalSize: 5_000_000,
            Title: title,
            Artist: artist,
            Album: album,
            AddedAt: addedAt,
            Tags: new CloudTrackTags(
                DurationSeconds: seconds, TrackNumber: trackNumber, AlbumArtist: artist));

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

    /// <summary>Gives a local row the album and album artist a real one would carry: what says two
    /// differently-filed folders are the same record.</summary>
    private void SetAlbum(string relativePath, string album, string albumArtist)
    {
        var row = _db.Query().First(t =>
            string.Equals(t.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        _db.Upsert(row with { Album = album, AlbumArtist = albumArtist, Artist = albumArtist });
    }

    [Fact]
    public void AdoptsCloudTracksTheLibraryHasNever_Seen()
    {
        var result = _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Cloud("gid-2", "Band/Record/02 Two.flac", title: "Two", trackNumber: 2),
        ]);

        Assert.Equal(2, result.Adopted);
        Assert.Equal(0, result.Forgotten);

        var rows = _db.QueryUnderFolder("Band/Record");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.CloudAdopted);
            Assert.Equal(CloudState.CloudOnly, r.CloudState);
            Assert.True(r.IsOffloaded);
            Assert.True(r.CloudSelected);
            Assert.False(r.CloudPinned);
        });
        Assert.Equal(["One", "Two"], rows.OrderBy(r => r.TrackNumber).Select(r => r.Title));

        // The album is a folder with tracks in it, which is what every view is built from.
        Assert.Contains(("Band/Record", 2), _db.QueryFolderTrackCounts());
    }

    [Fact]
    public void RunningTwiceChangesNothing()
    {
        var listing = new[] { Cloud("gid-1", "Band/Record/01 One.flac") };

        Assert.Equal(1, _db.MirrorCloudLibrary(listing).Adopted);

        var again = _db.MirrorCloudLibrary(listing);
        Assert.Equal(0, again.Total);
        Assert.Equal(1, _db.CountAdopted());
    }

    [Fact]
    public void ATrackThisMachineHoldsIsNotAdoptedTwice()
    {
        // Three ways of already having it: the same id, the same canonical audio, the same path,
        // the last being the same CD ripped on two machines under one naming template.
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));
        _db.Upsert(Local("Band/Record/02 Two.flac", rpmId: "other", hash: "gid-2-sha"));
        _db.Upsert(Local("Band/Record/03 Three.flac"));

        var result = _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Elsewhere/Moved/One.flac"),
            Cloud("gid-2", "Elsewhere/Moved/Two.flac"),
            Cloud("gid-3", "Band/Record/03 Three.flac"),
        ]);

        Assert.Equal(0, result.Adopted);
        Assert.Equal(0, _db.CountAdopted());
        Assert.Equal(3, _db.Count);
    }

    [Fact]
    public void AnAlbumFiledElsewhereOnTheOtherMachineIsNotAdoptedTwice()
    {
        // The record as this machine files it: a soundtrack under its composer.
        _db.Upsert(Local("Andrew Lloyd Webber/Evita/01 One.flac", rpmId: "mine-1"));
        _db.Upsert(Local("Andrew Lloyd Webber/Evita/02 Two.flac", rpmId: "mine-2"));
        SetAlbum("Andrew Lloyd Webber/Evita/01 One.flac", "Evita", "Andrew Lloyd Webber");
        SetAlbum("Andrew Lloyd Webber/Evita/02 Two.flac", "Evita", "Andrew Lloyd Webber");

        // The same record as another install files it: a different path, no shared ids, no hashes.
        var result = _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Soundtracks/Evita/01 One.flac", album: "Evita", artist: "Andrew Lloyd Webber"),
            Cloud("gid-2", "Soundtracks/Evita/02 Two.flac", album: "Evita", artist: "Andrew Lloyd Webber"),
        ]);

        Assert.Equal(0, result.Adopted);
        Assert.Equal(0, _db.CountAdopted());
        Assert.Equal(2, _db.Count);
        Assert.Empty(_db.QueryUnderFolder("Soundtracks"));
    }

    [Fact]
    public void ADuplicateAdoptedEarlierIsTakenBackOut()
    {
        // A pass that ran before the local rows had their tags adopted the record.
        _db.MirrorCloudLibrary([Cloud("gid-1", "Soundtracks/Evita/01 One.flac", album: "Evita", artist: "Andrew Lloyd Webber")]);
        Assert.Equal(1, _db.CountAdopted());

        // Now the library has its own copy, filed its own way.
        _db.Upsert(Local("Andrew Lloyd Webber/Evita/01 One.flac", rpmId: "mine-1"));
        SetAlbum("Andrew Lloyd Webber/Evita/01 One.flac", "Evita", "Andrew Lloyd Webber");

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Soundtracks/Evita/01 One.flac", album: "Evita", artist: "Andrew Lloyd Webber")]);

        Assert.Equal(1, result.Forgotten);
        Assert.Equal(0, _db.CountAdopted());
        Assert.Single(_db.QueryUnderFolder("Andrew Lloyd Webber/Evita"));
    }

    [Fact]
    public void AnAdoptedRowIsDroppedOnceTheSameTrackIsHereByAnyRoute()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", sha: "sha-1")]);

        // The same audio, ripped here, at a path of its own and under an id of its own: the hash is
        // what says the two are one recording.
        _db.Upsert(Local("Band/Record (2019)/01 One.flac", rpmId: "mine-1", hash: "sha-1"));

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", sha: "sha-1")]);

        Assert.Equal(1, result.Forgotten);
        Assert.Equal(0, _db.CountAdopted());
    }

    [Fact]
    public void MissingTracksOfAnAlbumThatIsHereFillItIn()
    {
        // Half the record on this disk, all of it in the cloud, at the same path.
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));

        var result = _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac"),
            Cloud("gid-2", "Band/Record/02 Two.flac"),
        ]);

        // The missing track joins the album rather than starting a second one.
        Assert.Equal(1, result.Adopted);
        Assert.Equal(2, _db.QueryUnderFolder("Band/Record").Count);
    }

    [Fact]
    public void OnePathIsOneTrackWhenTwoInstallsUploadedTheSameRip()
    {
        var result = _db.MirrorCloudLibrary(
        [
            Cloud("gid-late", "Band/Record/01 One.flac", addedAt: 2000),
            Cloud("gid-first", "Band/Record/01 One.flac", addedAt: 1000),
        ]);

        Assert.Equal(1, result.Adopted);
        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal("gid-first", row.RpmId);
    }

    [Fact]
    public void ACorrectionInTheCatalogReachesTheRow()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Wrong")]);

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Right")]);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Adopted);
        Assert.Equal("Right", Assert.Single(_db.QueryUnderFolder("Band/Record")).Title);
    }

    [Fact]
    public void ARecordRefiledElsewhereMovesRatherThanDoubling()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac")]);

        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record (2019)/01 One.flac")]);

        Assert.Equal(1, _db.CountAdopted());
        Assert.Empty(_db.QueryUnderFolder("Band/Record/"));
        Assert.Single(_db.QueryUnderFolder("Band/Record (2019)"));
    }

    [Fact]
    public void AnEditMadeHereIsNotOverwrittenByTheCatalogItHasNotReachedYet()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Wrong")]);
        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));

        // The user corrects the title of a record they do not hold. It is in the mirror and nowhere
        // else yet, so the catalogue still has the old title.
        _db.Upsert(row with { Title = "Right" });
        _db.NoteMetadataEdited([row.RelativePath], DateTime.UtcNow.Ticks);

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Wrong")]);

        Assert.Equal(0, result.Updated);
        Assert.Equal("Right", Assert.Single(_db.QueryUnderFolder("Band/Record")).Title);

        // Once the store has been told, the catalogue is the newer of the two again.
        _db.NoteCloudMetadata(row.Id, DateTime.UtcNow.Ticks);
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Corrected elsewhere")]);

        Assert.Equal("Corrected elsewhere", Assert.Single(_db.QueryUnderFolder("Band/Record")).Title);
    }

    [Fact]
    public void ARowWhoseSidecarIsStillOwedKeepsItsEditAfterTheCatalogStampIsTaken()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Wrong")]);
        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        _db.Upsert(row with { Title = "Right" });
        _db.NoteMetadataEdited([row.RelativePath], DateTime.UtcNow.Ticks);
        var before = Assert.Single(_db.QueryUnderFolder("Band/Record"));

        // The catalogue push wrote the row; its sidecar has not been sent. Only the store's stamp moves.
        _db.NoteCloudStamp(row.Id, 7_000);
        _db.NoteCloudStamp(row.Id, 5_000);   // an older stamp never goes backwards

        var after = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal(7_000, after.CloudUpdatedAt);
        Assert.Equal(before.CloudSyncedUtcTicks, after.CloudSyncedUtcTicks);
        Assert.True(CloudSyncPlan.OwesSidecar(after));

        // Still the newer of the two, so a catalogue read that has not caught up does not undo the edit.
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac", title: "Wrong")]);
        Assert.Equal("Right", Assert.Single(_db.QueryUnderFolder("Band/Record")).Title);
    }

    [Fact]
    public void AnAdvisoryFlagIsFilledInOnlyOnCloudOnlyRowsThatSayNothing()
    {
        _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac"),
            Cloud("gid-2", "Band/Record/02 Two.flac"),
        ]);
        var two = _db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-2");
        _db.Upsert(two with { Advisory = RPM.Core.Models.ContentAdvisory.Clean });
        _db.Upsert(Local("Band/Other/01 Three.flac", rpmId: "gid-3"));

        var changed = _db.ReplaceCloudOnlyAdvisory(
            ["gid-1", "gid-2", "gid-3", "gid-missing"],
            RPM.Core.Models.ContentAdvisory.Unknown, RPM.Core.Models.ContentAdvisory.Explicit, 9_000);

        Assert.Equal(1, changed);
        var one = _db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-1");
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, one.Advisory);
        Assert.Equal(9_000, one.MetadataEditedUtcTicks);
        Assert.True(CloudSyncPlan.OwesSidecar(one));

        // A flag the row already carries is somebody's answer, and a track with a file keeps its flag
        // in the file.
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Clean,
            _db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-2").Advisory);
        Assert.Null(Assert.Single(_db.QueryUnderFolder("Band/Other")).Advisory);
    }

    [Fact]
    public void AnAdoptedRowTakesTheCatalogsFlagAndKeepsItWhenTheCatalogStatesNone()
    {
        static CloudTrack WithFlag(CloudTrack track, int advisory)
            => track with { Tags = track.Tags! with { Advisory = advisory } };

        _db.MirrorCloudLibrary([WithFlag(Cloud("gid-1", "Band/Record/01 One.flac"), 1)]);
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, Assert.Single(_db.QueryUnderFolder("Band/Record")).Advisory);

        // A later listing that says nothing about the lyrics is not an instruction to forget them.
        var quiet = _db.MirrorCloudLibrary([WithFlag(Cloud("gid-1", "Band/Record/01 One.flac", title: "Renamed"), 0)]);
        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal(1, quiet.Updated);
        Assert.Equal("Renamed", row.Title);
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit, row.Advisory);

        // And one that states another flag is a change made elsewhere.
        _db.MirrorCloudLibrary([WithFlag(Cloud("gid-1", "Band/Record/01 One.flac", title: "Renamed"), 2)]);
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Clean, Assert.Single(_db.QueryUnderFolder("Band/Record")).Advisory);
    }

    [Fact]
    public void AStatedFlagIsReplacedOnlyWhereTheRowStillCarriesIt()
    {
        _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac"),
            Cloud("gid-2", "Band/Record/02 Two.flac"),
        ]);
        var one = _db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-1");
        _db.Upsert(one with { Advisory = RPM.Core.Models.ContentAdvisory.Clean });

        var changed = _db.ReplaceCloudOnlyAdvisory(
            ["gid-1", "gid-2"], RPM.Core.Models.ContentAdvisory.Clean, RPM.Core.Models.ContentAdvisory.Explicit, 9_000);

        Assert.Equal(1, changed);
        Assert.Equal(RPM.Core.Models.ContentAdvisory.Explicit,
            _db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-1").Advisory);
        // Replacing Clean says nothing about a row that has no flag.
        Assert.Null(_db.QueryUnderFolder("Band/Record").Single(t => t.RpmId == "gid-2").Advisory);

        // Unknown is never set: the catalogue never sends it, so the cloud would keep the old flag.
        Assert.Throws<ArgumentException>(() => _db.ReplaceCloudOnlyAdvisory(
            ["gid-1"], RPM.Core.Models.ContentAdvisory.Explicit, RPM.Core.Models.ContentAdvisory.Unknown, 9_000));
    }

    [Fact]
    public void ATrackTakenOutOfTheCloudIsForgotten()
    {
        _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac"),
            Cloud("gid-2", "Band/Record/02 Two.flac"),
        ]);

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac")]);

        Assert.Equal(1, result.Forgotten);
        Assert.Single(_db.QueryUnderFolder("Band/Record"));
    }

    [Fact]
    public void APartialListingForgetsNothing()
    {
        _db.MirrorCloudLibrary(
        [
            Cloud("gid-1", "Band/Record/01 One.flac"),
            Cloud("gid-2", "Band/Record/02 Two.flac"),
        ]);

        var result = _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac")], prune: false);

        Assert.Equal(0, result.Forgotten);
        Assert.Equal(2, _db.CountAdopted());
    }

    [Fact]
    public void ForgettingAdoptedRowsLeavesTheLibrarysOwn()
    {
        _db.Upsert(Local("Band/Record/01 One.flac", rpmId: "gid-1"));
        _db.MirrorCloudLibrary([Cloud("gid-2", "Other/Album/01 Two.flac")]);

        Assert.Equal(1, _db.ForgetAdopted());
        Assert.Equal(1, _db.Count);
        Assert.Single(_db.QueryUnderFolder("Band/Record"));
    }

    [Fact]
    public void ARowWhoseFileArrivesIsTheLibrarysOwn()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac")]);
        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));

        _db.ClearAdopted(row.Id);

        Assert.Equal(0, _db.ForgetAdopted());
        Assert.False(Assert.Single(_db.QueryUnderFolder("Band/Record")).CloudAdopted);
    }

    [Fact]
    public void ARowWithNoPathIsStillFiledAsAnAlbum()
    {
        var track = new CloudTrack(
            RpmId: "gid-1", Sha256: "sha", RelativePath: null, OriginalSize: 10,
            Title: "One", Artist: "Band", Album: "Record", AddedAt: 1,
            Tags: new CloudTrackTags(TrackNumber: 3, AlbumArtist: "Band", Extension: ".mp3"));

        Assert.Equal("Band/Record/03 One.mp3", CloudLibraryMirror.PathFor(track));

        _db.MirrorCloudLibrary([track]);
        Assert.Single(_db.QueryUnderFolder("Band/Record"));
    }

    [Fact]
    public void ARecordRenumberedInTheCatalogMovesHereToo()
    {
        _db.MirrorCloudLibrary([
            Cloud("gid-1", "Band/Record/01 One.flac", title: "One", trackNumber: 1),
            Cloud("gid-2", "Band/Record/02 Two.flac", title: "Two", trackNumber: 2)]);

        // The first track was taken out of the account and the second renumbered into its place: a
        // path this library is still holding for a row that is about to go.
        var result = _db.MirrorCloudLibrary([
            Cloud("gid-2", "Band/Record/01 One.flac", title: "Two", trackNumber: 1)]);

        Assert.Equal(1, result.Updated);

        var row = Assert.Single(_db.QueryUnderFolder("Band/Record"));
        Assert.Equal("gid-2", row.RpmId);
        Assert.Equal("Band/Record/01 One.flac", row.RelativePath);
    }

    [Fact]
    public void AnAdoptedRowIsNotQueuedForUpload()
    {
        _db.MirrorCloudLibrary([Cloud("gid-1", "Band/Record/01 One.flac")]);

        // "Everything" means everything on this disk. A row whose bytes are already in the cloud
        // (and whose file was never here) has nothing to send.
        _db.QueueForUpload(everything: true);

        Assert.Equal(0, _db.CountByCloudState(CloudState.Queued));
    }
}
