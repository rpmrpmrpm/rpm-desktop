using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Migration;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The import run: the tracks of another app's library, matched against rpm by their audio, filed or
/// uploaded, with their details merged, and every step written to the record so a stop at any point
/// continues where it was.
/// </summary>
public class MigrationRunTests : IDisposable
{
    private static readonly DateTimeOffset Y2015 = new(2015, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset Y2021 = new(2021, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string _top;
    private readonly string _root;
    private readonly string _source;
    private readonly string _staging;
    private readonly LibraryDatabase _db;
    private readonly FakeCloud _cloud = new();
    private long _clock = 638_000_000_000_000_000;

    public MigrationRunTests()
    {
        _top = Path.Combine(Path.GetTempPath(), $"rpmrun-{Guid.NewGuid():N}");
        _root = Path.Combine(_top, "library");
        _source = Path.Combine(_top, "musicbee");
        _staging = Path.Combine(_top, "staging");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_source);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_top, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private FilingRules Rules => new(NamingTemplate.ArtistAlbumNumberedTitle, OrganizeMode.Copy, MergedDiscs: true, _root);

    // ── sources ──

    private SourceTrack Local(string id, string album, string title, uint track = 1, int seed = 0, int rating = 0, bool favorite = false,
        int plays = 0, DateTimeOffset? last = null, DateTimeOffset? added = null, string artist = "Band", bool write = true)
    {
        var path = Path.Combine(_source, artist, album, $"{track:D2} {title}.wav");
        if (write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, TestAudio.Wav(1, seed));
        }
        return new SourceTrack(id, new LocalFile(path), title, artist, album, artist, (int)track, 1, 1.0, 16_044, rating, favorite, plays, last, added);
    }

    private static SourceTrack Remote(string id, string album, string title, uint track = 1, int plays = 0, string artist = "Band") =>
        new(id, new RemoteFile(id, ".wav", TestAudio.Wav(1, id.GetHashCode() & 0xff).Length), title, artist, album, artist, (int)track, 1, 1.0,
            TestAudio.Wav(1, id.GetHashCode() & 0xff).Length, 0, false, plays, null, Y2015);

    private static SourceLibrary Library(SourceKind kind, params SourceTrack[] tracks) => new(kind, "lib-1", "MusicBee", tracks, [], []);

    private MigrationRun Run(SourceLibrary library, MigrationDestination destination, IMediaServer? server = null)
    {
        var record = _db.OpenMigration(library, destination);
        return new MigrationRun(_db, library, record, Rules, server, _cloud, _staging)
        {
            Hash = TestAudio.HashSamples,
            Now = () => _clock,
            Delay = (wait, ct) => { _clock += wait.Ticks; return Task.Delay(2, ct); },
            PollInterval = TimeSpan.FromMilliseconds(5),
        };
    }

    private static readonly IProgress<MigrationProgress> Quiet = new Progress<MigrationProgress>();

    private LibraryDatabase.MigrationRecord Record(SourceLibrary library) => _db.OpenMigration(library, MigrationDestination.ThisComputer);

    private LibraryTrack Row(string title) => _db.Query().Single(t => t.Title == title);

    /// <summary>A library row for a song, as if rpm had it already.</summary>
    private LibraryTrack Existing(string title, string album, string extension = ".wav", string artist = "Band", double seconds = 1.0)
    {
        var rel = $"{artist}/{album}/{title}{extension}";
        _db.Upsert(new LibraryTrack
        {
            RelativePath = rel, Folder = $"{artist}/{album}", FileName = $"{title}{extension}", Extension = extension,
            SizeBytes = 100, ModifiedUtcTicks = 1, Title = title, Artist = artist, AlbumArtist = artist, Album = album,
            DurationSeconds = seconds, RpmId = Guid.NewGuid().ToString("D"), AddedUtcTicks = Y2021.UtcTicks,
        });
        return _db.FindByRelativePath(rel)!;
    }

    // ── this computer ──

    [Fact]
    public async Task Local_source_is_copied_and_details_applied()
    {
        var one = Local("s1", "Record", "One", 1, seed: 1, rating: 8, favorite: true, plays: 5, last: Y2021, added: Y2015);
        var two = Local("s2", "Record", "Two", 2, seed: 2);
        var library = Library(SourceKind.MusicBee, one, two);

        var end = await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(new MigrationCounts(2, 2, 0, 0, 0), Record(library).Counts());
        var row = Row("One");
        Assert.StartsWith(_root, Path.Combine(_root, row.RelativePath));
        Assert.True(File.Exists(((LocalFile)one.File).Path));
        Assert.Equal(8, row.Rating);
        Assert.True(row.IsFavorite);
        Assert.Equal(5, row.Plays);
        Assert.Equal(Y2021.UtcTicks, row.LastPlayedUtcTicks);
        Assert.Equal(Y2015.UtcTicks, row.AddedUtcTicks);
        Assert.Equal([(library.DeviceId, 5)], _db.ImportPlaysToSend(10).Select(r => (r.Device, r.Plays)));
    }

    [Fact]
    public async Task Matched_track_is_merged_not_filed()
    {
        var existing = Existing("One", "Record");
        var library = Library(SourceKind.MusicBee, Local("s1", "Record", "One", rating: 6, added: Y2015));

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Single(_db.Query());
        var row = Assert.Single(Record(library).Rows(MigrationTrackState.AlreadyInRpm));
        Assert.Equal(existing.Id, row.TrackId);
        Assert.Null(row.Reason);
        Assert.Equal(6, Row("One").Rating);
        Assert.Equal(Y2015.UtcTicks, Row("One").AddedUtcTicks);
    }

    [Fact]
    public async Task Lower_quality_copy_is_reported_with_the_source_path()
    {
        Existing("One", "Record", ".mp3");
        var source = Local("s1", "Record", "One");
        var library = Library(SourceKind.MusicBee, source);

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        var row = Assert.Single(Record(library).Rows(MigrationTrackState.AlreadyInRpm));
        Assert.Equal(MigrationReason.LowerQualityInRpm, row.Reason);
        Assert.Equal(((LocalFile)source.File).Path, row.Detail);
        Assert.Single(_db.Query());
    }

    [Fact]
    public async Task Same_audio_with_other_tags_is_matched_by_its_hash()
    {
        var source = Local("s1", "Record", "One", seed: 3);
        var hash = await TestAudio.HashSamples(((LocalFile)source.File).Path, CancellationToken.None);
        var cloudCopy = Existing("Totally Different Title", "Elsewhere");
        _db.SeedSynced(cloudCopy.Id, hash, 100, 5);
        var library = Library(SourceKind.MusicBee, source);

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(cloudCopy.Id, Assert.Single(Record(library).Rows(MigrationTrackState.AlreadyInRpm)).TrackId);
        Assert.Single(_db.Query());
    }

    [Fact]
    public async Task Missing_file_is_reported_not_retried()
    {
        var gone = Local("s1", "Record", "One", write: false);
        var library = Library(SourceKind.MusicBee, gone);

        var end = await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        var row = Assert.Single(Record(library).Rows(MigrationTrackState.NotImported));
        Assert.Equal(MigrationReason.FileNotFound, row.Reason);
        Assert.Equal(((LocalFile)gone.File).Path, row.Detail);
        Assert.Empty(Record(library).Due(_clock + TimeSpan.FromDays(1).Ticks, 10));
    }

    [Fact]
    public async Task Same_file_under_two_ids_is_filed_once()
    {
        var one = Local("s1", "Record", "One", seed: 1);
        var library = Library(SourceKind.MusicBee, one, one with { SourceId = "s1-again" });

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Single(_db.Query());
        Assert.Equal(new MigrationCounts(2, 1, 1, 0, 0), Record(library).Counts());
    }

    [Fact]
    public async Task A_file_inside_the_library_is_taken_where_it_is()
    {
        var inside = Path.Combine(_root, "Band", "Record", "01 One.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, TestAudio.Wav(1, 4));
        var library = Library(SourceKind.MusicBee,
            new SourceTrack("s1", new LocalFile(inside), "One", "Band", "Record", "Band", 1, 1, 1, 16_044, 4, false, 0, null, null));

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        var row = Assert.Single(_db.Query());
        Assert.Equal("Band/Record/01 One.wav", row.RelativePath.Replace('\\', '/'));
        Assert.Equal(4, row.Rating);
        Assert.Single(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Restart_continues_where_it_stopped()
    {
        var library = Library(SourceKind.MusicBee,
            Local("s1", "Record", "One", 1, seed: 1), Local("s2", "Record", "Two", 2, seed: 2), Local("s3", "Record", "Three", 3, seed: 3));
        using var stop = new CancellationTokenSource();
        var stopper = new SyncProgress(p => { if (p.Counts.Imported >= 1) stop.Cancel(); });

        var first = await Run(library, MigrationDestination.ThisComputer).RunAsync(stopper, stop.Token);
        var second = await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Paused, first);
        Assert.Equal(RunEnd.Finished, second);
        Assert.Equal(3, _db.Query().Count);
        Assert.Equal(new MigrationCounts(3, 3, 0, 0, 0), Record(library).Counts());
    }

    [Fact]
    public async Task Second_run_imports_only_new_tracks()
    {
        var one = Local("s1", "Record", "One", 1, seed: 1, plays: 10);
        await Run(Library(SourceKind.MusicBee, one), MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        var two = Local("s2", "Record", "Two", 2, seed: 2);
        var library = Library(SourceKind.MusicBee, one with { Plays = 12 }, two);
        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(2, _db.Query().Count);
        Assert.Equal(new MigrationCounts(2, 2, 0, 0, 0), Record(library).Counts());
        // The source's current total replaces the first import's, on the source's own device.
        Assert.Equal(12, Row("One").Plays);
    }

    [Fact]
    public async Task Finished_run_adds_the_playlists_and_closes_the_record()
    {
        var one = Local("s1", "Record", "One", 1, seed: 1);
        var two = Local("s2", "Record", "Two", 2, seed: 2);
        var library = Library(SourceKind.MusicBee, one, two) with
        {
            Playlists = [new SourcePlaylist("p1", "Mix", [], false, ["s2", "s1"], [])],
        };
        var run = Run(library, MigrationDestination.ThisComputer);
        var stages = new List<MigrationStage>();

        var end = await run.RunAsync(new SyncProgress(p => stages.Add(p.Stage)), CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(new PlaylistOutcome("Mix", 2, 0, false, false), Assert.Single(run.Playlists));
        Assert.Contains(MigrationStage.Playlists, stages);
        Assert.Equal(MigrationStage.Finishing, stages[^1]);
        Assert.Empty(_db.UnfinishedMigrations());
    }

    [Fact]
    public async Task Paused_run_leaves_the_record_open()
    {
        var library = Library(SourceKind.MusicBee, Local("s1", "Record", "One", 1, seed: 1));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, stop.Token);

        Assert.Single(_db.UnfinishedMigrations());
    }

    // ── media servers ──

    [Fact]
    public async Task Server_401_pauses_and_keeps_tracks_pending()
    {
        var server = new FakeServer { Expired = true };
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);

        var end = await Run(library, MigrationDestination.ThisComputer, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.SignInExpired, end);
        Assert.Equal(MigrationTrackState.Found, Assert.Single(Record(library).Due(_clock, 10)).State);
    }

    [Fact]
    public async Task Server_track_to_this_computer_is_filed_and_the_staged_copy_is_gone()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One", plays: 3));
        server.Add(library.Tracks[0]);

        await Run(library, MigrationDestination.ThisComputer, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(3, Row("One").Plays);
        Assert.Empty(Directory.Exists(_staging) ? Directory.GetFiles(_staging) : []);
        Assert.Single(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Partial_download_is_downloaded_again()
    {
        var server = new FakeServer { ShortOnce = true };
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(Path.Combine(_staging, "leftover.wav.part"), [1, 2, 3]);

        var end = await Run(library, MigrationDestination.ThisComputer, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(2, server.Opens);
        Assert.Equal(1, Record(library).Counts().Imported);
        Assert.Empty(Directory.GetFiles(_staging));
    }

    [Fact]
    public async Task Cloud_only_deletes_only_after_synced()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"), Remote("j2", "Record", "Two", 2));
        foreach (var t in library.Tracks) server.Add(t);
        using var uploads = new FakeUploader(_db, _root, delay: TimeSpan.FromMilliseconds(20));

        var end = await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(new MigrationCounts(2, 2, 0, 0, 0), Record(library).Counts());
        Assert.All(_db.Query(), t => Assert.Equal(CloudState.CloudOnly, t.CloudState));
        Assert.Empty(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
        Assert.Equal(0, uploads.DeletedBeforeSynced);
        Assert.True(_cloud.UploadRequests > 0);
    }

    [Fact]
    public async Task Cloud_only_never_holds_more_than_three_files()
    {
        var server = new FakeServer();
        var tracks = Enumerable.Range(1, 8).Select(i => Remote($"j{i}", "Record", $"Song {i}", (uint)i)).ToArray();
        var library = Library(SourceKind.Jellyfin, tracks);
        foreach (var t in tracks) server.Add(t);
        using var uploads = new FakeUploader(_db, _root, delay: TimeSpan.FromMilliseconds(30), staging: _staging);

        await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(8, Record(library).Counts().Imported);
        Assert.InRange(uploads.MostFilesSeen, 1, 4);   // three waiting for the cloud, one being downloaded
    }

    [Fact]
    public async Task Quota_full_takes_albums_whole()
    {
        var server = new FakeServer();
        var first = new[] { Remote("a1", "First", "One", 1), Remote("a2", "First", "Two", 2) };
        var second = new[] { Remote("b1", "Second", "One", 1), Remote("b2", "Second", "Two", 2) };
        var library = Library(SourceKind.Jellyfin, [.. first, .. second]);
        foreach (var t in library.Tracks) server.Add(t);
        _cloud.Quota = (Used: 0, Quota: first.Sum(t => t.SizeBytes) + 10, Unlimited: false);
        using var uploads = new FakeUploader(_db, _root, delay: TimeSpan.FromMilliseconds(5), onSynced: size => _cloud.Quota = _cloud.Quota with { Used = _cloud.Quota.Used + size });

        var end = await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.CloudFull, end);
        var record = Record(library);
        Assert.Equal(2, record.Counts().Imported);
        Assert.Equal(["b1", "b2"], record.Due(_clock, 10).Select(r => r.SourceId));
    }

    [Fact]
    public async Task Cloud_only_upload_refused_for_good_is_not_imported_and_its_copy_is_removed()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);
        using var uploads = new FakeUploader(_db, _root, delay: TimeSpan.FromMilliseconds(5), refuse: true);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var end = await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, limit.Token);

        Assert.Equal(RunEnd.Finished, end);
        var row = Assert.Single(Record(library).Rows(MigrationTrackState.NotImported));
        Assert.Equal(MigrationReason.UploadFailed, row.Reason);
        Assert.Equal("Not a format the cloud takes", row.Detail);
        Assert.Empty(_db.Query());
        Assert.Empty(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cloud_only_stops_when_the_cloud_cannot_take_the_waiting_files()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"), Remote("j2", "Record", "Two", 2));
        foreach (var t in library.Tracks) server.Add(t);
        _cloud.Uploads = CloudUploads.Full;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var end = await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, limit.Token);

        Assert.Equal(RunEnd.CloudFull, end);
    }

    [Fact]
    public async Task Cloud_only_stops_when_uploads_are_off()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);
        _cloud.Uploads = CloudUploads.Off;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var end = await Run(library, MigrationDestination.CloudOnly, server).RunAsync(Quiet, limit.Token);

        Assert.Equal(RunEnd.CloudOff, end);
        Assert.Equal(MigrationTrackState.Downloaded, Assert.Single(Record(library).Due(_clock, 10)).State);
    }

    [Fact]
    public async Task Unreadable_quota_is_waited_out_not_read_as_full()
    {
        var server = new FakeServer();
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);
        _cloud.Quota = (0, 1_000_000_000, false);
        _cloud.QuotaFailures = 3;
        using var uploads = new FakeUploader(_db, _root, delay: TimeSpan.FromMilliseconds(5));
        var stages = new List<MigrationStage>();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var end = await Run(library, MigrationDestination.CloudOnly, server)
            .RunAsync(new SyncProgress(p => { lock (stages) stages.Add(p.Stage); }), limit.Token);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(1, Record(library).Counts().Imported);
        Assert.Contains(MigrationStage.Waiting, stages);
    }

    [Fact]
    public async Task Unreachable_server_is_waited_for_without_using_attempts()
    {
        var server = new FakeServer { Unreachable = CloudRetrySchedule.MaxAttempts + 3 };
        var library = Library(SourceKind.Jellyfin, Remote("j1", "Record", "One"));
        server.Add(library.Tracks[0]);
        var stages = new List<MigrationStage>();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var end = await Run(library, MigrationDestination.ThisComputer, server)
            .RunAsync(new SyncProgress(p => { lock (stages) stages.Add(p.Stage); }), limit.Token);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(1, Record(library).Counts().Imported);
        Assert.Contains(MigrationStage.Waiting, stages);
    }

    [Fact]
    public async Task New_run_tries_the_tracks_that_failed_before()
    {
        var track = Local("m1", "Record", "One", write: false);
        var library = Library(SourceKind.MusicBee, track);
        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);
        Assert.Equal(1, Record(library).Counts().NotImported);

        Local("m1", "Record", "One");   // the file is back
        var end = await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(RunEnd.Finished, end);
        Assert.Equal(new MigrationCounts(1, 1, 0, 0, 0), Record(library).Counts());
    }

    [Fact]
    public async Task Track_removed_from_rpm_during_the_import_is_not_brought_back()
    {
        var library = Library(SourceKind.MusicBee, Local("m1", "Record", "One"));
        var record = Record(library);
        record.AddFound(["m1"]);
        record.Note("m1", MigrationTrackState.NotImported, reason: MigrationReason.Removed);

        await Run(library, MigrationDestination.ThisComputer).RunAsync(Quiet, CancellationToken.None);

        Assert.Equal(MigrationReason.Removed, Assert.Single(Record(library).Rows(MigrationTrackState.NotImported)).Reason);
        Assert.Empty(_db.Query());
    }

    // ── fakes ──

    private sealed class SyncProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => report(value);
    }

    private sealed class FakeCloud : IMigrationCloud
    {
        public (long Used, long Quota, bool Unlimited) Quota { get; set; } = (0, 0, true);
        public int QuotaFailures { get; set; }
        public CloudUploads Uploads { get; set; } = CloudUploads.Ready;
        public int UploadRequests;
        public List<(string RpmId, long AddedAtUnix)> Added { get; } = [];
        public bool SignedIn => true;
        public Task<(long Used, long Quota, bool Unlimited)?> QuotaAsync(CancellationToken ct) =>
            Task.FromResult<(long, long, bool)?>(QuotaFailures-- > 0 ? null : Quota);
        public CloudUploads CanUpload(long bytes) => Uploads;
        public Task PutTrackAddedAsync(IReadOnlyList<(string RpmId, long AddedAtUnix)> rows, CancellationToken ct)
        {
            lock (Added) Added.AddRange(rows);
            return Task.CompletedTask;
        }
        public void RequestUpload() => Interlocked.Increment(ref UploadRequests);
    }

    private sealed class FakeServer : IMediaServer
    {
        private readonly Dictionary<string, byte[]> _files = [];
        public bool Expired { get; init; }
        public bool ShortOnce { get; set; }
        /// <summary>How many opens get no answer at all, as when the server is switched off.</summary>
        public int Unreachable { get; set; }
        public int Opens;
        public SourceKind Kind => SourceKind.Jellyfin;
        public void Add(SourceTrack track) => _files[((RemoteFile)track.File).Key] = TestAudio.Wav(1, track.SourceId.GetHashCode() & 0xff);
        public Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            if (Expired) throw new MediaServerSignInExpired();
            if (Unreachable-- > 0) throw new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(61));
            var bytes = _files[file.Key];
            if (ShortOnce)
            {
                ShortOnce = false;
                bytes = bytes[..(bytes.Length / 2)];
            }
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
    }

    /// <summary>Stands in for the cloud upload workers: marks rows chosen for the cloud as uploaded after
    /// a short wait, and watches how many audio files are on disk at once.</summary>
    private sealed class FakeUploader : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public int MostFilesSeen;
        public int DeletedBeforeSynced;

        public FakeUploader(LibraryDatabase db, string root, TimeSpan delay, string? staging = null, Action<long>? onSynced = null, bool refuse = false)
        {
            _loop = Task.Run(async () =>
            {
                var firstSeen = new Dictionary<long, DateTime>();
                while (!_stop.IsCancellationRequested)
                {
                    var files = Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories).Length
                        + (staging is not null && Directory.Exists(staging) ? Directory.GetFiles(staging).Length : 0);
                    MostFilesSeen = Math.Max(MostFilesSeen, files);
                    foreach (var t in db.Query().Where(t => t.CloudSelected && t.CloudState is CloudState.Local or CloudState.Queued))
                    {
                        if (!File.Exists(Path.Combine(root, t.RelativePath)))
                        {
                            DeletedBeforeSynced++;
                            continue;
                        }
                        if (!firstSeen.TryGetValue(t.Id, out var seen))
                            firstSeen[t.Id] = seen = DateTime.UtcNow;
                        if (DateTime.UtcNow - seen < delay)
                            continue;
                        if (refuse)
                        {
                            // The same refusal twice: final, as CloudRetrySchedule.AfterFailure rules.
                            db.MarkCloudAttemptFailed(t.Id, "Not a format the cloud takes", CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));
                            db.MarkCloudAttemptFailed(t.Id, "Not a format the cloud takes", CloudRetrySchedule.NeverAgainTicks(DateTime.UtcNow));
                            continue;
                        }
                        db.SeedSynced(t.Id, "hash-" + t.Id, t.SizeBytes, 5);
                        onSynced?.Invoke(t.SizeBytes);
                    }
                    try { await Task.Delay(2, _stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
