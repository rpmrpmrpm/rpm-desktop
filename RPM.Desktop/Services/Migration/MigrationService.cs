using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RPM.Core.Cloud;
using RPM.Core.Diagnostics;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Migration;
using RPM.Core.Models;
using RPM.Core.Net;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Cloud;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Migration;

/// <summary>
/// How to reach a source again: the library file or playlist files, or a signed-in media server. It is
/// kept in the keychain (it can hold a server token or a Subsonic password) for as long as the import
/// is unfinished, so it can be continued after the app was closed, and deleted when it finishes.
/// </summary>
public sealed record MigrationConnection(
    SourceKind Kind,
    IReadOnlyList<string> Files,
    JellyfinSession? Jellyfin = null,
    PlexServerChoice? Plex = null,
    Uri? PlexConnection = null,
    SubsonicLogin? Subsonic = null);

/// <summary>
/// Imports from other music apps on the desktop. Owns the one import that may run at a time (a
/// <see cref="SemaphoreSlim"/>, not a flag), keeps it running when its window is hidden, and connects
/// the run to the library, the cloud and the keychain.
///
/// <para>Only one import runs at a time, whatever the source, so downloads and copies do not compete
/// for the disk and the network. A second request while one runs is refused; the window shows the
/// running import instead.</para>
/// </summary>
public sealed partial class MigrationService : IDisposable
{
    private const string SecretPrefix = "migration:";
    private const string PlexClientName = "migration:plex-client";

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly CloudSyncService _cloudSync;
    private readonly RpmCloudClient _cloud;
    private readonly AuthService _auth;
    private readonly ILogger<MigrationService> _log;
    private readonly SemaphoreSlim _one = new(1, 1);

    private CancellationTokenSource? _cts;

    public MigrationService(
        LibraryService library, SettingsService settings, CloudSyncService cloudSync,
        RpmCloudClient cloud, AuthService auth, ILogger<MigrationService> log)
    {
        _library = library;
        _settings = settings;
        _cloudSync = cloudSync;
        _cloud = cloud;
        _auth = auth;
        _log = log;
    }

    /// <summary>The running import's progress or end changed. Raised on a worker thread.</summary>
    public event EventHandler? Changed;

    public bool IsRunning { get; private set; }

    /// <summary>The library being imported now, or the last one imported.</summary>
    public SourceLibrary? Current { get; private set; }

    /// <summary>The label the windows show for <see cref="Current"/>.</summary>
    public string CurrentLabel { get; private set; } = "";

    public MigrationProgress? Progress { get; private set; }

    /// <summary>How the last run ended, or null while one runs or before the first.</summary>
    public RunEnd? LastEnd { get; private set; }

    public MigrationReport? LastReport { get; private set; }

    /// <summary>The window has shown the last finished import's report. The next time it opens, it
    /// starts at the list of apps instead of showing the same report again.</summary>
    public bool ReportSeen { get; set; }

    /// <summary>What the last run needs to be continued (after a sign-in expired, for example).</summary>
    public MigrationConnection? LastConnection { get; private set; }

    public MigrationDestination LastDestination { get; private set; }

    /// <summary>Whether "Cloud only" can work: signed in, and cloud upload on (the uploads it waits for
    /// run only then).</summary>
    public bool CloudReady => _auth.IsLoggedIn && _settings.CloudMode != CloudSyncMode.None;

    public bool SignedIn => _auth.IsLoggedIn;

    /// <summary>A stable id Plex uses to know this app on this computer.</summary>
    public string PlexClientId
    {
        get
        {
            var secrets = _auth.Secrets;
            if (secrets.Read(PlexClientName) is SecretRead.Found { Bytes: var bytes })
                return Encoding.UTF8.GetString(bytes);
            var id = "rpm-" + Guid.NewGuid().ToString("N");
            secrets.Write(PlexClientName, Encoding.UTF8.GetBytes(id));
            return id;
        }
    }

    // ── network ──

    /// <summary>A client for a media server: the local sockets for an address on this network (a
    /// home server should work while the internet is down), the gated way out for anything else.
    /// Downloads can be long, so the client's own timeout only bounds the wait for an answer.</summary>
    public static HttpClient HttpFor(Uri server) => IsLocal(server)
        ? new HttpClient(Wire.LocalSockets(connectTimeout: TimeSpan.FromSeconds(10))) { Timeout = TimeSpan.FromMinutes(10) }
        : Wire.Outside(Destination.Unaddressed, TimeSpan.FromMinutes(10));

    /// <summary>The servers of this kind on the network or this computer, for the person to pick from.
    /// Waits about two seconds for answers.</summary>
    public async Task<IReadOnlyList<FoundServer>> FindServersAsync(SourceKind kind, CancellationToken ct)
    {
        using var http = new HttpClient(Wire.LocalSockets(connectTimeout: TimeSpan.FromSeconds(2))) { Timeout = TimeSpan.FromSeconds(3) };
        return await Task.Run(() => ServerDiscovery.FindAsync(kind, http, TimeSpan.FromSeconds(2), ct), ct);
    }

    /// <summary>plex.tv, for the Plex sign-in and the server list.</summary>
    public static HttpClient PlexTv() => Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(30));

    private static bool IsLocal(Uri server)
    {
        if (IPAddress.TryParse(server.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
                return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }
        return server.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !server.Host.Contains('.');
    }

    // ── reading ──

    /// <summary>Reads the source library. Library files are read off the UI thread.</summary>
    public async Task<SourceLibrary> ReadAsync(MigrationConnection connection, IProgress<int> found, CancellationToken ct)
    {
        switch (connection.Kind)
        {
            case SourceKind.MusicBee or SourceKind.ITunes:
                return await Task.Run(() =>
                {
                    using var xml = File.OpenRead(connection.Files[0]);
                    return ITunesLibraryReader.Read(xml, connection.Kind);
                }, ct);
            case SourceKind.PlaylistFiles:
                return await Task.Run(() => PlaylistFileReader.Read(
                    [.. connection.Files.Order(StringComparer.Ordinal).Select(f => new PlaylistFileInput(f, File.ReadAllBytes(f)))]), ct);
            default:
                // On a worker: the reader parses every page, and a playlist of 20,000 entries is one answer
                // of several megabytes. Its continuations must not run on the UI thread.
                var server = ServerFor(connection) ?? throw new InvalidOperationException("No server.");
                return await Task.Run(() => server.ReadAsync(found, ct), ct);
        }
    }

    /// <summary>The media server a connection names, or null for a library file.</summary>
    public static IMediaServer? ServerFor(MigrationConnection c) => c.Kind switch
    {
        SourceKind.Jellyfin when c.Jellyfin is { } j => new JellyfinServer(HttpFor(j.Server), j),
        SourceKind.Plex when c.Plex is { } p && c.PlexConnection is { } at => new PlexServer(HttpFor(at), p, at),
        SourceKind.Navidrome when c.Subsonic is { } s => new SubsonicServer(HttpFor(s.Server), s),
        _ => null,
    };

    // ── the summary ──

    public Estimate Estimate(SourceLibrary library) => MigrationEstimate.Of(library,
        [.. _library.Db.Query().Select(t => new KnownSong(t.Artist ?? "", t.Album ?? "", t.Title ?? "", t.DurationSeconds))]);

    /// <summary>Free bytes on the disk the library is on, or null when it cannot be read.</summary>
    public long? FreeDiskBytes()
    {
        try
        {
            return _library.Root is { } root ? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<(long Used, long Quota, bool Unlimited)?> CloudSpaceAsync(CancellationToken ct)
    {
        if (!_auth.IsLoggedIn)
            return null;
        try
        {
            var quota = await _cloud.GetQuotaAsync(ct);
            return (quota.UsedBytes, quota.QuotaBytes, quota.Unlimited);
        }
        catch (Exception e) when (e is HttpRequestException or CloudRejectedException or InvalidOperationException)
        {
            return null;
        }
    }

    // ── unfinished imports ──

    public IReadOnlyList<MigrationSummary> Unfinished() =>
        _library.IsOpen ? _library.Db.UnfinishedMigrations() : [];

    /// <summary>How to reach an unfinished import's source again, or null when it was not kept.</summary>
    public MigrationConnection? SavedConnection(MigrationSummary summary)
    {
        if (_auth.Secrets.Read(SecretName(summary.Kind, summary.LibraryId)) is not SecretRead.Found { Bytes: var bytes })
            return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, MigrationServiceJson.Default.MigrationConnection);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── the run ──

    /// <summary>The unfinished import of this library, or null. Read without changing anything, so the
    /// window can ask whether to continue it or start over.</summary>
    public LibraryDatabase.MigrationRecord? UnfinishedFor(SourceLibrary library) =>
        _library.IsOpen ? _library.Db.UnfinishedMigration(library) : null;

    /// <summary>The other libraries imported into this one before, for the summary's note on plays counted twice.</summary>
    public IReadOnlyList<string> OtherImportsFor(SourceLibrary library) =>
        _library.IsOpen ? _library.Db.OtherImports(library) : [];

    /// <summary>Starts the import in the background. False when one is running already.</summary>
    /// <param name="how">Continue where an earlier import of this library stopped (or start fresh when there
    /// was none), or start over and check every track again.</param>
    public bool Start(SourceLibrary library, MigrationConnection connection, MigrationDestination destination, string label, ImportStart how)
    {
        if (!_library.IsOpen || _library.Root is not { } root || !_one.Wait(0))
            return false;

        var db = _library.Db;
        var rules = new FilingRules(
            _settings.DefaultTemplate, OrganizeMode.Copy, _settings.MultiDiscMode == MultiDiscMode.MergedAlbum, root);
        var record = db.OpenMigration(library, destination);
        if (how == ImportStart.StartOver)
            record.StartOver();
        KeepConnection(library, record.LibraryKey, connection);

        var cts = new CancellationTokenSource();
        _cts = cts;
        Current = library;
        CurrentLabel = label;
        LastConnection = connection;
        LastDestination = destination;
        LastEnd = null;
        LastReport = null;
        ReportSeen = false;
        Progress = null;
        IsRunning = true;
        Raise();

        var server = ServerFor(connection);
        var run = new MigrationRun(db, library, record, rules, server, new Cloud(this), StagingRoot(root))
        {
            SourceLabel = label,
            LibraryChanged = () => _library.NotifyChanged(),
        };
        var progress = new Reporter(this);

        _ = Task.Run(async () =>
        {
            RunEnd end;
            try
            {
                end = await run.RunAsync(progress, cts.Token);
                if (run.Failure is { } failure)
                {
                    _log.LogWarning(failure, "[Migration] The import from {Source} failed", library.Name);
                    Faults.Note("migration", "importing from another app", failure);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "[Migration] The import from {Source} failed", library.Name);
                Faults.Note("migration", "importing from another app", e);
                end = RunEnd.Failed;
            }

            try
            {
                LastReport = MigrationReports.Build(library, record, run.Playlists);
                if (end == RunEnd.Finished)
                    _auth.Secrets.Delete(SecretName(library.Kind, record.LibraryKey));
                if (destination == MigrationDestination.ThisComputer || CloudReady)
                    _ = _cloudSync.KickAsync();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "[Migration] Could not finish the report for {Source}", library.Name);
            }
            finally
            {
                LastEnd = end;
                IsRunning = false;
                _cts = null;
                cts.Dispose();
                _one.Release();
                Raise();
            }
        });
        return true;
    }

    /// <summary>Pauses the running import. Continue starts it again where it stopped.</summary>
    public void Pause() => _cts?.Cancel();

    /// <summary>"Try again" from the report: the tracks that were not imported start over.</summary>
    public void Retry(SourceLibrary library, MigrationDestination destination, IEnumerable<string> sourceIds) =>
        _library.Db.OpenMigration(library, destination).Retry(sourceIds);

    /// <summary>Forgets an unfinished import: its record, and the sign-in kept to continue it. What it
    /// brought in stays in the library.</summary>
    public void Forget(MigrationSummary import)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (IsRunning || !_library.IsOpen)
            return;
        _library.Db.ForgetMigration(import);
        _auth.Secrets.Delete(SecretName(import.Kind, import.LibraryId));
        Raise();
    }

    private void KeepConnection(SourceLibrary library, string libraryKey, MigrationConnection connection)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(connection, MigrationServiceJson.Default.MigrationConnection);
        var name = SecretName(library.Kind, libraryKey);
        if (_auth.Secrets.Write(name, bytes) is not SecretWrite.Done)
            _log.LogInformation("[Migration] The sign-in for {Source} is not kept; continuing later asks again", library.Name);
    }

    private static string SecretName(SourceKind kind, string libraryId) =>
        $"{SecretPrefix}{kind.ToString().ToLowerInvariant()}:{libraryId}";

    /// <summary>Where downloads wait. Inside the library's data folder, on the same disk (so filing a
    /// download is a rename), and never in the inbox, which is watched for new music.</summary>
    private static string StagingRoot(string root) =>
        Path.Combine(root, LibraryDatabase.DataFolderName, "migration-staging");

    private void Raise()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception e) { _log.LogDebug(e, "[Migration] A listener failed"); }
    }

    // The semaphore is not disposed: the background run releases it when it ends, which can be after this.
    public void Dispose() => _cts?.Cancel();

    private sealed class Reporter(MigrationService service) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value)
        {
            service.Progress = value;
            service.Raise();
        }
    }

    /// <summary>The run's view of the cloud.</summary>
    private sealed class Cloud(MigrationService service) : IMigrationCloud
    {
        public bool SignedIn => service._auth.IsLoggedIn;

        public Task<(long Used, long Quota, bool Unlimited)?> QuotaAsync(CancellationToken ct) => service.CloudSpaceAsync(ct);

        public CloudUploads CanUpload(long bytes) =>
            !service.CloudReady ? CloudUploads.Off
            : service._cloudSync.Status.Space.Fits(bytes) ? CloudUploads.Ready
            : CloudUploads.Full;

        public Task PutTrackAddedAsync(IReadOnlyList<(string RpmId, long AddedAtUnix)> rows, CancellationToken ct) =>
            service._cloud.PutTrackAddedAsync(rows, ct);

        public void RequestUpload() => _ = service._cloudSync.KickAsync();

        public void AddPlaylist(long playlistNodeId) => _ = service._cloudSync.AddPlaylistToCloudAsync(playlistNodeId);
    }
}

/// <summary>How an import starts when the library was imported part of the way before.</summary>
public enum ImportStart
{
    /// <summary>Where it stopped: the tracks that are through are not looked at again.</summary>
    Continue,

    /// <summary>Every track is checked again. Tracks already in rpm are found and not copied twice.</summary>
    StartOver,
}

/// <summary>The connection's JSON, written at build time (the published app is Native AOT).</summary>
[JsonSerializable(typeof(MigrationConnection))]
internal sealed partial class MigrationServiceJson : JsonSerializerContext;
