using RPM.Core.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Cloud;
using RPM.Desktop.Services.Library;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// Library sharing, whole: this machine on the network, the machines it shares with, and the music
/// that crosses between them.
///
/// <para>
/// Each part is simple on its own: a key, a book of peers, a beacon, a small HTTP server and a client.
/// This class combines them into the feature. It sets the order of things. The server does not start
/// before there is a library to serve. The beacon does not go out before the server has a port. Nothing
/// is served to anybody who is not in the book.
/// </para>
///
/// <para>
/// Sign-in is not required for any of this. An account adds only two things. Another of the user's own
/// computers is trusted without a question. A peer's cloud-stored records keep playing after everyone
/// has left the network. When signed out, two machines pair by hand, and they share over the network
/// while both people are present.
/// </para>
/// </summary>
public sealed partial class SharingService : IDisposable
{
    /// <summary>How often a peer's listing is asked for again. A collection is not edited most days and
    /// the answer is a few hundred bytes when nothing has changed. See <see cref="PeerCatalog.Version"/>.
    /// So this is cheap enough to be regular and slow enough not to be noise.</summary>
    private static readonly TimeSpan CatalogInterval = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly AuthService _auth;
    private readonly CloudStreamService? _stream;
    private readonly RpmCloudClient? _cloud;

    /// <summary>This machine's own album art in the cloud store. It is the last place where a cover for
    /// a borrowed record is looked for. See <see cref="CoverFor"/>.</summary>
    private readonly CloudArtService? _art;
    private readonly PeerBook _book;
    private readonly PeerLibraryStore _guests;
    private readonly PeerDiscovery _discovery;
    private readonly ILogger<SharingService> _log;

    private readonly object _gate = new();

    /// <summary>The clients in hand, one per peer, by device id. A client holds a token and the
    /// address it was granted at, so it is worth keeping and worth dropping when either changes.</summary>
    private readonly Dictionary<string, Held> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The version of each peer's listing this machine last took, so the next ask is
    /// answered with "nothing changed" instead of the whole shelf.</summary>
    private readonly Dictionary<string, string> _catalogVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every pairing this computer is part of, in both directions. The server writes requests from
    /// other computers to it and <see cref="Ask"/> writes this computer's own, so both are settled
    /// under one lock. Outlives the server: a restart ends the asked pairings
    /// (<see cref="PairingBoard.EndAsked"/>) and keeps everything else.
    /// </summary>
    private readonly PairingBoard _pairings = new(PairingTimes.Standard, () => DateTime.UtcNow);

    /// <summary>The requests this computer is following, one per computer, by device id. Whoever
    /// takes a run out of here disposes it.</summary>
    private readonly Dictionary<string, PairingRun> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ends what time ends on the board, once a second. See
    /// <see cref="PairingBoard.Sweep"/>.</summary>
    private readonly Timer _sweep;

    /// <summary>What the offer has already opened itself for this session: a question by its id, and
    /// a computer found on the network by its device id. See <see cref="TryClaimAutoOpen"/>.</summary>
    private readonly HashSet<string> _opened = new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="_served"/> alone. Its own lock rather than <see cref="_gate"/>,
    /// which is held around the clients and the beacon: a listing is served on a peer's thread and
    /// has no business waiting on either.</summary>
    private readonly object _servedGate = new();

    /// <summary>The last listing built for each audience, as what it was built out of against the
    /// version it came to. There are two entries at most. See <see cref="CatalogInputs"/> for why the
    /// audience is one of the inputs.</summary>
    private readonly Dictionary<PeerCatalogBuilder.AlbumAudience, (CatalogInputs Inputs, string Version)>
        _served = new();

    private LibraryServer? _server;
    private CancellationTokenSource? _cts;

    public SharingService(
        SettingsService settings,
        LibraryService library,
        AuthService auth,
        PeerBook book,
        PeerLibraryStore guests,
        PeerDiscovery discovery,
        ILogger<SharingService> log,
        CloudStreamService? stream = null,
        RpmCloudClient? cloud = null,
        CloudArtService? art = null)
    {
        _art = art;
        _settings = settings;
        _library = library;
        _auth = auth;
        _book = book;
        _guests = guests;
        _discovery = discovery;
        _log = log;
        _stream = stream;
        _cloud = cloud;

        _account = AccountRegistry.Read();

        _pairings.Changed += () => Changed?.Invoke();
        _sweep = new Timer(_ => SweepPairings(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _discovery.Found += OnFound;
        _discovery.Lost += OnLost;
        _book.Changed += OnBookChanged;

        // Signing in makes this machine one of a set; signing out makes it a stranger again, and the
        // music that belonged to the set has to go with it.
        _auth.Changed += OnAccountChanged;

        // And on the way out, while there is still a token to say it with: this computer is no
        // longer one of the account's own, so its other machines must stop recognising it.
        _auth.SigningOut += UnregisterAsync;

        // A library has to be open before there is anything to serve, and opening a different one is
        // a different collection on offer.
        _library.Opened += _ => RestartSoon();
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the set of peers, what is known about them, or any pairing changes. It can be
    /// raised on any thread. Screens read <see cref="Nearby"/> and <see cref="NeedsAttention"/>
    /// again.</summary>
    public event Action? Changed;

    /// <summary>
    /// The last thing that went wrong while serving this collection to somebody, or while reading
    /// theirs. Empty when nothing has.
    ///
    /// <para>It is shown in the sharing settings on the machine where the fault happened, because the
    /// other end cannot see it. A borrower cannot tell a failing computer from one that has been shut
    /// down. Its shelf simply stops changing. This is the one place where either person can find out
    /// that something is wrong.</para>
    /// </summary>
    public SharingTrouble? LastFault { get; private set; }

    /// <summary>Puts a trouble on the settings page. The caller has already written the exception to the
    /// ring with <see cref="Faults.Note"/>.</summary>
    private void Fault(SharingTrouble trouble)
    {
        LastFault = trouble;
        Changed?.Invoke();
    }

    /// <summary>Everybody this machine shares with.</summary>
    public IReadOnlyList<TrustedPeer> Peers => _book.Peers;

    /// <summary>Machines seen on the network, whether or not they are shared with.</summary>
    public IReadOnlyList<DiscoveredPeer> Seen => _discovery.Seen;

    public bool IsServing => _server?.IsRunning == true;

    /// <summary>Puts sharing back up, off the calling thread.
    /// <para><b>Runs on a pool thread, not on the calling thread.</b> Three of the four callers run on
    /// the drawing thread: the library opening, a setting changing, and the account changing. The
    /// restart walks the peer folders on disk, moves files out of them, loads a key, binds a TCP
    /// listener, and starts and announces mDNS. On the drawing thread these steps freeze the window. So
    /// the work moves off the calling thread, and every caller gets that behaviour without having to
    /// remember it.</para>
    /// <para>Re-announcing alone costs about a second of multicast. Fixing each caller separately would
    /// leave the next caller to remember. This is the one entry point. It uses <c>Background.Run</c>,
    /// the same entry point that other services use for work that nobody is waiting for.</para>
    /// </summary>
    public void RestartSoon() => Shell.Background.Run(() =>
    {
        RestartNow();
        return Task.CompletedTask;
    });

    /// <summary>
    /// The restart itself, one at a time.
    /// </summary>
    /// <remarks>
    /// <b>Calls are serialised.</b> The drawing thread would never run two of these at once, but the
    /// pool can. Two overlapping restarts could tear down a server that the other is still building. The
    /// beacon would then announce a port that nobody is listening on. The three triggers arrive within
    /// moments of each other at start-up, which is when this would happen.
    /// </remarks>
    private void RestartNow()
    {
        lock (_restarting)
        {
            RestartCore();
        }
    }

    private readonly Lock _restarting = new();

    /// <summary>Takes sharing down and puts it back up: the server first, then the beacon that says where
    /// it is. Safe to call whenever something changed, because it restarts everything.</summary>
    private void RestartCore()
    {
        Stop();

        if (!ShouldServe)
        {
            // This computer is no longer on the network, so nothing it asked for can be answered.
            EndRuns();
            return;
        }

        // Music can end up in a peer folder if an older build swapped the open library for a borrowed
        // one. This is not a cache. It can be the user's only copy of a record, and the peer folders are
        // deleted whole when a computer is forgotten. So the music is moved to the inbox on startup, where
        // the collection can take it back in.
        if (_library.Root is { Length: > 0 } root &&
            PeerLibraryStore.Rescue(root) is > 0 and var rescued)
        {
            _log.LogWarning(
                "[Sharing] {Count} file(s) were filed into a borrowed shelf and have been moved to the inbox",
                rescued);
        }

        var identity = PeerKeyStore.Identity;

        var server = new LibraryServer(identity, _pairings)
        {
            Name = _settings.ShareName,
            Face = _settings.ShareFace,
            Trusted = () => _book.Peers,
            OnSession = Proved,
            TrackCount = () => _library.Count,
            Account = () => new PeerAccount { UserId = _auth.UserId ?? "", Name = _settings.ShareName },
            OnCatalog = (asking, have, ct) => Task.FromResult(Catalog(asking, have, ct)),
            OnTrack = TrackBytesFor,
            OnCover = CoverFor,
            OnDisc = DiscFor,
            OnArt = ArtFor,
            HasRoom = () => !_book.IsFull,
            OnPaired = Record,
            OnError = (route, ex) =>
            {
                Faults.Note("sharing", $"answering {route}", ex);
                Fault(new SharingTrouble.Serving());
            },
        };

        int port;
        try
        {
            port = server.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Sharing] Could not open a port for the library");
            server.Dispose();
            return;
        }

        lock (_gate)
        {
            _server = server;
            _cts = new CancellationTokenSource();
        }

        _beaconName = _settings.ShareName;
        _beaconFace = _settings.ShareFace;
        _discovery.Start(identity, port, _beaconName);

        var token = _cts.Token;
        _ = Task.Run(() => CatalogLoopAsync(token));
        Shell.Background.Run(() => SyncAccountAsync());
    }

    /// <summary>Whether this machine should have a beacon out at all: the user asked for it, there is a
    /// collection to offer, there is an account behind both, and this is the app rather than the design
    /// harness.
    ///
    /// <para><b>The account is needed.</b> The beacon carries an account fingerprint. The grants that
    /// let a borrower reach the cloud half of a shelf are written against the account. A borrowed
    /// record that lives only in the store is signed with the account's token. See
    /// <see cref="SharedCloudUrlAsync"/>, which returns null without one. A machine that shares without
    /// an account offers an arrangement that it cannot keep.</para>
    /// <para><b>The harness is not a collection.</b> A <c>--gallery</c> run opens a seeded temporary
    /// library. It does not get its own identity. The peer key belongs to the machine, in LocalData. So
    /// a harness and the real app running together are two servers that answer under one device id. A
    /// borrower keeps one address per device and takes whichever announced last, so either server could
    /// answer. The harness answers with a demo library, or with an empty one before the seed arrives.
    /// Read as the whole collection, that made every record look deleted. A borrowed shelf of nineteen
    /// thousand records was emptied and refilled on alternate passes for as long as both were
    /// running.</para>
    /// <para>This is turned off here, and not at the server. Everything that sharing does hangs off
    /// this one gate: the listener, the beacon, the catalogue round and the account registration. A
    /// harness that pulls would write peer mirrors into the LocalData folder that the real app is
    /// reading. <see cref="LibraryService.SyncAsync"/> already skips the same switch, for a similar
    /// reason. The harness's library is not the user's.</para>
    /// <para>This is one property, not the same terms in two places. The second place is
    /// <see cref="OnSettingsChanged"/>, which decides whether the beacon has to come down. A beacon
    /// that disagrees with the server about whether it should be up is a computer that the neighbours
    /// can see and cannot reach.</para>
    /// </summary>
    private bool ShouldServe =>
        MayServe(_settings.ShareLibrary, _library.IsOpen, _auth.IsLoggedIn, GalleryMode.Active);

    /// <summary>The rule itself, away from the settings and the session, so it can be read and
    /// tested as the sentence it is.</summary>
    /// <param name="harness">Whether this is a <c>--gallery</c> run. There is no default. A caller that
    /// forgot to pass it would share a demo library under the real machine's name.</param>
    public static bool MayServe(bool sharingOn, bool libraryOpen, bool signedIn, bool harness) =>
        sharingOn && libraryOpen && signedIn && !harness;

    public void Stop()
    {
        LibraryServer? server;
        CancellationTokenSource? cts;
        List<PeerClient> idle;

        lock (_gate)
        {
            server = _server; _server = null;
            cts = _cts; _cts = null;

            // Clients are retired here, but they are closed separately. A client that still has an
            // exchange in progress is closed by its last borrower. Closing it here would break that
            // exchange. The cancellation below makes this a short step, not a wait.
            idle = new List<PeerClient>();
            foreach (var held in _clients.Values)
            {
                held.Retired = true;
                if (held.Users <= 0)
                    idle.Add(held.Client);
            }
            _clients.Clear();
        }

        try { cts?.Cancel(); } catch { }

        // The remembered listings are cleared with the server. Each one would fail its own check once the
        // collection behind it changed. But each holds a reference to a LibraryDatabase, and a stopped
        // share must not keep a closed database alive.
        lock (_servedGate)
            _served.Clear();

        _beaconName = "";   // nothing is being said on the network now
        _beaconFace = "";
        _discovery.Stop();
        server?.Dispose();
        foreach (var client in idle)
            client.Dispose();
        try { cts?.Dispose(); } catch { }
    }

    // ── what this machine serves ──

    /// <summary>
    /// The shared listing, built from the mirror. Rows borrowed from somebody else are not in it:
    /// passing on another person's music is a chain nobody agreed to.
    ///
    /// <para>The albums go to everybody this machine is paired with. How much of each row is sent depends
    /// on the asker. Another of the owner's own computers is one collection in two rooms, so it gets
    /// everything. A guest gets what the record is: which pressing, and whether it is published as works
    /// and movements. A guest does not get what the owner made of it. See
    /// PeerCatalogBuilder.AlbumAudience, which draws that line.</para>
    ///
    /// <para>
    /// The playlists are sent to everybody. The receiver decides what to do with them. A guest mirrors the
    /// tree. Another of the owner's own machines ignores it. Playlists travel between a person's computers
    /// through their cloud, and a second route would make a second copy of every list.
    /// </para>
    ///
    /// <para>
    /// Sending playlists only to guests is wrong, because the two machines do not have to agree about who
    /// is asking. Each keeps its own book. Each works out for itself whether the other is a stranger or a
    /// second machine of the same person. The two answers can differ, for example while one side has not
    /// yet noticed that an account changed. Then the borrower opens a shelf that it believes belongs to a
    /// guest, while the lender declines to describe its playlists to a machine that it believes is its own.
    /// The result is a shelf with albums and no playlists, and no message that explains why.
    /// </para>
    ///
    /// <para>Albums work this way, and playlists follow the same model. The rows go to everybody. What
    /// varies is how much of each row is filled in. A decision made at the far end cannot turn a feature
    /// off here.</para>
    /// </summary>
    private LibraryServer.CatalogAnswer Catalog(
        TrustedPeer asking, string? have, CancellationToken cancellation)
    {
        _ = cancellation;

        // Which of the two listings this is. Not a detail of the album step: it is one of the inputs
        // below, because the owner's copy and a guest's carry different albums and so hash to
        // different versions, and one remembered version answering for both would hand a guest the
        // owner's answer.
        var audience = asking.Kind is PeerKind.Account
            ? PeerCatalogBuilder.AlbumAudience.TheOwner
            : PeerCatalogBuilder.AlbumAudience.AGuest;

        // READ FIRST, BUILT AGAINST, AND CHECKED AGAIN AT THE END.
        //
        // Taken before a row is read, so that a write landing during the build leaves the reading
        // filed under the number from before it. That is the answer that makes the next poll build
        // again rather than the one that makes it trust a listing assembled across a change.
        var inputs = Inputs(audience);

        if (have is not null && Remembered(audience) is { } last &&
            last.Inputs == inputs && string.Equals(last.Version, have, StringComparison.Ordinal))
        {
            // This is the purpose of the check. Without it, every poll would build the listing and hash
            // every row into a version. It would then compare that version and throw the listing away.
            // That happens several times an hour for each borrower, even when the collection has not
            // changed.
            Said.Line("sharing",
                $"{Who(asking.DeviceId)} already has version {Short(have)}; nothing rebuilt");
            return LibraryServer.CatalogAnswer.Unchanged;
        }

        var albums = _library.IsOpen
            ? PeerCatalogBuilder.From(_library.Db.AlbumMetaEdits(), audience)
            : [];

        var playlists = _library.IsOpen
            ? PlaylistsWithPictures(_library.Db.PlaylistTree())
            : [];

        // This is the owner's own cut. It is sent to everybody who can see the shelf, and it is the same
        // cut that the phone gets. A generated list is made from percentiles over the whole collection.
        // Neither a phone nor a borrower has that collection, so re-cutting at the far end would give lists
        // that the owner has never seen. The only difference between the two sides is what can be played.
        // On this network, the borrower can ask this machine for every track. Off the network, only the
        // tracks in the cloud can be resolved. Only ONE list from each run is sent, not the whole run. The
        // phone gets the same cut, but it receives several lists and chooses its own window when someone
        // presses refresh (see MoodListPayload.Shows). A borrowed shelf draws what it is handed as one
        // playlist. Given the whole run, it would show a Chill with two hundred songs where the owner has
        // fifty. The lists made from the owner's own listening are excluded, because those belong to the
        // owner's phone alone.
        var moodLists = _library.IsOpen
            ? PeerCatalogBuilder.MoodLists(
                PublishedMoodLists.Dressed(MoodListPush.OneListEach(
                    MoodListPush.ForOthers(_library.Db.SessionMoodLists))))
            : [];

        var catalog = PeerCatalogBuilder.Catalog(
            _library.Query(), _settings.ShareName, albums, playlists, moodLists);

        // WHAT THIS MACHINE SAID, AND FROM WHICH LIBRARY.
        //
        // An empty listing has one cause that the receiving end cannot tell apart from the others.
        // AlbumMetaEdits and PlaylistTree read album_meta and pl_node in full, with none of the narrowing
        // that the track query has. So nothing that hides rows can empty them. Zero albums and zero
        // playlists means an empty database, and the question is which database it was.
        //
        // Hence the root and the raw row counts are logged beside the counts sent. A line that says
        // "0 rows" names a different library open here. A line that says "19731 rows" with zero tracks
        // sent names a narrowing. The two cases need opposite fixes, and nothing else on either machine
        // tells them apart.
        Said.Line("sharing",
            $"served {Who(asking.DeviceId)}: {catalog.Tracks.Count} tracks, {albums.Count} albums, " +
            $"{playlists.Count} playlists and {moodLists.Count} generated lists " +
            $"(version {Short(catalog.Version)}) out of {_library.Root} — " +
            $"{_library.Db.Count} rows in it, local rows only {_library.Db.LocalRowsOnly}, " +
            $"cloud rows only {_library.Db.CloudRowsOnly}, signed in {_auth.IsLoggedIn}");

        // The listing is remembered only if nothing changed while it was being built. This covers a write
        // that landed, a library that was swapped, or the cloud switch being flipped. Any of these means
        // the version describes a collection that is no longer the one here. Remembering it would answer
        // the next poll with stale data. So nothing is stored, and the next poll builds the listing again.
        if (Inputs(audience) == inputs)
        {
            lock (_servedGate)
                _served[audience] = (inputs, catalog.Version);
        }

        return LibraryServer.CatalogAnswer.Listing(catalog);
    }

    /// <summary>Everything <see cref="Catalog"/> reads that <see cref="LibraryDatabase.WriteTurns"/>
    /// does not count, and that number with them.
    /// <para><b>Why a type and not a written-out comparison.</b> Two equal inputs are the only proof
    /// this machine can give for "nothing changed" without reading the collection. So any input that is
    /// missing from this type is a listing that can go stale without anyone noticing. A record struct
    /// compares every field, so adding a field to the type adds it to the check.</para>
    /// <para>The database is held by reference and compared by identity, and it is never called. A
    /// collection that is closed and reopened at the same folder is a new
    /// <see cref="LibraryDatabase"/>. Its turn count starts again near zero, so the count alone could
    /// match across the swap. The reference cannot match. Null means no collection is open, which is a
    /// listing of nothing.</para>
    /// <para><see cref="LibraryDatabase.CloudRowsOnly"/> and
    /// <see cref="LibraryDatabase.LocalRowsOnly"/> are here because they narrow every query in the
    /// class, and they are plain properties. Flipping either one changes what the listing holds without
    /// writing a row, so the turn count says nothing about them. The share name is here because the
    /// listing carries it. <see cref="PeerCatalog.VersionOf"/> does not hash the name. So a rename
    /// rebuilds the listing here, but the version stays the same. This is a known open point.</para>
    /// </summary>
    private readonly record struct CatalogInputs(
        LibraryDatabase? Db,
        long Turns,
        bool LocalRowsOnly,
        bool CloudRowsOnly,
        string Name,
        PeerCatalogBuilder.AlbumAudience Audience);

    private CatalogInputs Inputs(PeerCatalogBuilder.AlbumAudience audience)
    {
        var db = _library.IsOpen ? _library.Db : null;
        return new CatalogInputs(
            db,
            db?.WriteTurns ?? 0,
            db?.LocalRowsOnly ?? false,
            db?.CloudRowsOnly ?? false,
            _settings.ShareName ?? "",
            audience);
    }

    private (CatalogInputs Inputs, string Version)? Remembered(PeerCatalogBuilder.AlbumAudience audience)
    {
        lock (_servedGate)
            return _served.TryGetValue(audience, out var last) ? last : null;
    }

    /// <summary>
    /// Where a track that a peer asked for actually is: the file on this disk. If this machine has
    /// offloaded it, the cloud copy is used instead, and the borrower is sent to it directly rather than
    /// through here.
    /// </summary>
    private async Task<TrackBytes?> TrackBytesFor(TrustedPeer peer, string rpmId, CancellationToken ct)
    {
        _ = peer;

        if (!_library.IsOpen || _library.Root is not { } root)
            return null;

        if (_library.Db.FindByRpmId(rpmId) is not { } track || track.PeerAdopted)
            return null;

        var path = Path.Combine(root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return new TrackBytes(path, null);

        if (track.CloudState != CloudState.CloudOnly || _stream is null)
            return null;

        // This is the owner's own signature on the owner's own storage. A borrower cannot sign this for
        // themselves. For a record whose bytes this machine no longer keeps, this signature is what
        // "you may hear my records" must mean.
        return await _stream.UrlAsync(rpmId, ct) is { Length: > 0 } url
            ? new TrackBytes(null, url)
            : null;
    }

    /// <summary>
    /// The cover of a record that a peer is looking at, asked for through any track of it.
    ///
    /// <para>This looks in every place where this machine would find a picture of that record, in the
    /// same order as the wall here. First, the album folder's own image. Then the art inside the file
    /// that was asked for. Then the art inside any other track of the same album. Last, the cover that
    /// the owner's cloud keeps. The last two cases explain most blank tiles on the other end. One case is
    /// a folder with no <c>cover.jpg</c>, where the first track has no picture either. The other is a
    /// record whose files were all offloaded, so there is nothing on this disk to read.</para>
    ///
    /// <para>The request names a track, not an album key. A track is what the catalogue gives a borrower to
    /// point at. The answer belongs to the album, and the borrower stores it under the album.</para>
    /// </summary>
    private async Task<(byte[] Bytes, string ContentType)?> CoverFor(
        TrustedPeer peer, string rpmId, CancellationToken ct)
    {
        _ = peer;

        if (!_library.IsOpen || _library.Root is not { } root ||
            _library.Db.FindByRpmId(rpmId) is not { } track || track.PeerAdopted)
            return null;

        string Absolute(string relative) =>
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        var path = Absolute(track.RelativePath);

        // The folder comes first. A cover chosen in the album editor is written there, and this machine
        // draws the record with it. The file's own picture comes next. That is what a folder with no
        // cover from an import is left with.
        var folder = Path.GetDirectoryName(path);
        var cover = (folder is { Length: > 0 } ? AlbumArt.ForFolder(folder) : null)
                    ?? (File.Exists(path) ? AlbumArt.ForFile(path) : null);

        // Any other track of the record. In a compilation, for example, only track nine may carry the
        // artwork. That is ordinary. The borrower asked about the album, not about that one file.
        if (cover is null)
        {
            foreach (var sibling in _library.Db.FolderTracks(track.Folder))
            {
                if (ct.IsCancellationRequested)
                    return null;

                var siblingPath = Absolute(sibling.RelativePath);
                if (siblingPath == path || !File.Exists(siblingPath))
                    continue;

                if (AlbumArt.ForFile(siblingPath) is { Bytes.Length: > 0 } found)
                {
                    cover = found;
                    break;
                }
            }
        }

        if (cover is { Bytes.Length: > 0 })
            return (cover.Bytes, cover.Mime);

        // Nothing is on this disk. For a record that this machine has offloaded, the cover is part of
        // the record. The cover was uploaded with it, under this account. The borrower has no other way
        // to ask for it.
        if (_art is not { } art)
            return null;

        var stored = await art.CoverAsync(
            track.AlbumArtist, track.Artist, track.Album, track.RpmId, ct);

        return stored is { Length: > 0 } ? (stored, AlbumArt.SniffMime(stored)) : null;
    }

    /// <summary>
    /// The album's own picture of its record, which is the folder's disc.png. It is asked for through any
    /// track of the album, exactly as the cover is.
    ///
    /// <para>Only the folder is checked. Disc art is a file in the folder. It is written there at import,
    /// by the album editor, and by the cover search. There is no second place to look. A track has no disc
    /// art in its tags, and the cloud keeps covers, not discs. Most albums have no disc art. Null here is
    /// the correct answer, not a failure.</para>
    ///
    /// <para>It is sent as it is on the disk, uncut. The borrower rounds it and reads whether it is a whole
    /// record or a label (see <c>DiscStamp.Round</c>). This machine does the same with its own. The
    /// borrower's screen decides how large a picture it wants.</para>
    /// </summary>
    private async Task<(byte[] Bytes, string ContentType)?> DiscFor(
        TrustedPeer peer, string rpmId, CancellationToken ct)
    {
        _ = peer;

        if (!_library.IsOpen || _library.Root is not { } root ||
            _library.Db.FindByRpmId(rpmId) is not { } track || track.PeerAdopted)
            return null;

        var path = Path.Combine(root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Path.GetDirectoryName(path) is not { Length: > 0 } folder ||
            FolderArtWriter.FindDiscImage(folder) is not { } image)
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(image, ct);
            return bytes is { Length: > 0 } ? (bytes, AlbumArt.SniffMime(bytes)) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // a file that went while it was being read is a record with no disc art
        }
    }

    /// <summary>A picture that belongs to something other than a record. Today that is one thing: the
    /// cover of a playlist on this machine's own tree, asked for as <c>pl:12</c>.
    /// <para>It is served from the stored master, not drawn on request. That is why the master exists.
    /// See <see cref="PlaylistCoverStore"/>. Drawing a cover means flattening the list and compositing
    /// four covers. The asker is waiting on a network response.</para>
    /// <para>This is always <see cref="PlaylistShelf.Own"/>, never the peer that is asking. A borrowed
    /// shelf's masters are copies of another person's pictures. Passing them on would make this machine
    /// a second source for something it does not own. This method has no branch that reaches for a
    /// peer's segment. The only segment it can ask for is this library's own.</para>
    /// <para>The <paramref name="peer"/> parameter is ignored. That is safe only because
    /// <see cref="Catalog"/> already gives the whole <c>PlaylistTree()</c> to every trusted peer. The
    /// albums, by contrast, are split by audience in <c>PeerCatalogBuilder</c>. If playlist sharing
    /// ever becomes selective like the albums, this handler becomes an enumeration hole over
    /// <c>pl:1…pl:N</c>. A peer that asks for a node id nobody told it about would still get that
    /// picture.</para>
    /// </summary>
    private Task<(byte[] Bytes, string ContentType)?> ArtFor(
        TrustedPeer peer, string key, CancellationToken ct)
    {
        _ = peer;
        _ = ct;

        if (PlaylistNodeOf(key) is not { } node || !_library.IsOpen)
            return Task.FromResult<(byte[], string)?>(null);

        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);

        // The type is sniffed, not assumed. A picture choice can keep the user's own bytes unchanged
        // (PlaylistArt.Bleed leaves an image untouched when it is square and under the size cap). Or it
        // can re-encode as PNG when the alpha is not opaque. So the master on disk is not always a JPEG.
        // CoverFor, just above, reads the type of its own master the same way, for the same reason.
        return Task.FromResult(
            PlaylistCoverStore.Read(node, segment).Cover is { Length: > 0 } bytes
                ? (bytes, AlbumArt.SniffMime(bytes))
                : ((byte[], string)?)null);
    }

    /// <summary>This library's tree as a borrower is handed it, with a stamp on every row whose picture
    /// was chosen, so the borrower asks for that picture rather than drawing the automatic one.
    /// <para>The stamp is a hash of the bytes <see cref="ArtFor"/> would serve, read off the same
    /// master. A row with a choice and no master on the disk (the file is missing) is sent as
    /// automatic, which is the picture the borrower can draw for itself. An automatic row is never
    /// stamped: the borrower draws it out of the same records, and asking for it would be a request per
    /// list for a picture it already has.</para>
    /// <para><b>The version does not see a picture that is written after its row.</b> The listing is
    /// remembered for as long as no row has been written. The picker writes the row before the file. A
    /// listing built between those two writes carries the old picture's stamp, until the next write to
    /// the collection. The borrower then asks for the picture and is served the new file under the old
    /// stamp. What it shows is correct. It fetches once more when the stamp catches up.</para>
    /// </summary>
    private IReadOnlyList<PeerPlaylist> PlaylistsWithPictures(IReadOnlyList<PlaylistTreeRow> tree)
    {
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);
        var pictures = new Dictionary<long, string>();

        foreach (var row in tree)
        {
            if (row.Cover is PlaylistCover.Automatic)
                continue;

            try
            {
                if (PlaylistCoverStore.Read(row.Id, segment).Cover is { Length: > 0 } bytes)
                    pictures[row.Id] = Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8));
            }
            catch
            {
                // A master that cannot be read is sent as an automatic list, which the borrower can draw.
            }
        }

        return PeerCatalogBuilder.Playlists(tree, pictures);
    }

    /// <summary>
    /// The picture that a lender chose for one of their lists or folders, from the computer that holds it.
    ///
    /// <para>Kept on this disk under the owner's node id and the picture's stamp, so it is asked for once
    /// and again only when the owner changes it. Null when that computer is not on the network or has no
    /// picture for the node: the caller then draws what it would have drawn without one. A miss is not
    /// remembered, for the reason <see cref="DiscAsync"/> gives.</para>
    /// </summary>
    public async Task<byte[]?> PlaylistPictureAsync(
        string deviceId, long ownerNodeId, string stamp, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(stamp))
            return null;

        // The stamp came off the wire and is about to become part of a file name.
        var safe = new string([.. stamp.Where(char.IsAsciiLetterOrDigit).Take(32)]);
        if (safe.Length == 0)
            return null;

        var cached = Path.Combine(ArtFolder(deviceId), $"pl-{ownerNodeId}-{safe}.img");

        try
        {
            if (File.Exists(cached))
                return await File.ReadAllBytesAsync(cached, cancellation);
        }
        catch
        {
            // A half-written or locked cache file is a picture to fetch again, not a failure.
        }

        using var borrowed = Borrow(deviceId);
        if (borrowed.Client is not { } client)
            return null;

        var bytes = await client.ImageAsync(SharingProtocol.ArtRoute, $"pl:{ownerNodeId}", cancellation);
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            Directory.CreateDirectory(ArtFolder(deviceId));
            await File.WriteAllBytesAsync(cached, bytes, cancellation);
        }
        catch
        {
            // This is only a cache. A picture that could not be written is fetched again next time.
        }

        return bytes;
    }

    /// <summary>The playlist node that an art key names, or null for any other key. The parsing is kept
    /// here on its own, so that a test can check it against a key without building a
    /// <see cref="SharingService"/>.</summary>
    internal static long? PlaylistNodeOf(string key)
    {
        const string prefix = "pl:";
        return key.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(key[prefix.Length..], out var node)
            ? node
            : null;
    }

    // ── pairing ──

    /// <summary>
    /// The computers worth a row under "On this network", with any pairing on the board. Built afresh
    /// on every call; see <see cref="NearbyComputers"/>.
    /// </summary>
    /// <remarks>Called on the drawing thread on every redraw. Returns before touching the device key
    /// when there is nothing to list: the key is loaded from disk on first use, and that load belongs
    /// to <see cref="RestartNow"/> on the pool, which always runs before anything can be seen or
    /// paired.</remarks>
    public IReadOnlyList<NearbyComputer> Nearby()
    {
        var seen = _discovery.Seen;
        var pairings = _pairings.Snapshot();
        if (seen.Count == 0 && pairings.Count == 0)
            return [];

        return NearbyComputers.Build(
            seen,
            PeerKeyStore.Identity.DeviceId,
            _book.Peers,
            AccountDeviceIds(),
            pairings);
    }

    /// <summary>Whether a pairing is waiting for the person here: a code to read out, or an ending
    /// nobody has dismissed. Read from the board on every call, never kept.</summary>
    public bool NeedsAttention => _pairings.NeedsAttention;

    /// <summary>
    /// Whether the offer should open itself for <paramref name="key"/>: true once per key per
    /// session. Keys are a question's id (so each new request opens it) and a found computer's device
    /// id (so a computer on the network opens it once, not on every beacon).
    /// </summary>
    public bool TryClaimAutoOpen(string key)
    {
        lock (_gate)
            return _opened.Add(key);
    }

    /// <summary>
    /// Asks a computer on the network to share. Returns at once; the board says how it goes.
    ///
    /// <para>The pairing is on the board before the request is sent. That order is what lets the
    /// server settle two computers asking each other at the same moment
    /// (<see cref="PairingBoard.Incoming"/>).</para>
    /// </summary>
    public void Ask(string deviceId)
    {
        if (_book.IsFull)
            return;

        var peer = _discovery.Seen.FirstOrDefault(p =>
            string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
            return;

        // No display name yet: the beacon names no person. The offer brings one (PairOffer.Face).
        var (pairing, started) = _pairings.Begin(new PairingPeer(peer.DeviceId, peer.Name, "", peer.PublicKey));
        if (!started)
            return;

        // The connection is pinned to the key that the beacon carried. A machine that answers in its
        // place is refused before any word is exchanged. The beacon itself could be false. The six-digit
        // code is there for that case.
        var client = new PeerClient(PeerKeyStore.Identity, peer.Host, peer.Port, peer.PublicKey, MyAccount);
        var run = new PairingRun(_pairings, client, pairing, () => DateTime.UtcNow, Record);

        PairingRun? replaced;
        lock (_gate)
        {
            _runs.TryGetValue(peer.DeviceId, out replaced);
            _runs[peer.DeviceId] = run;
        }
        replaced?.Dispose();

        var name = _settings.ShareName;
        var face = _settings.ShareFace;

        _ = Task.Run(async () =>
        {
            try
            {
                await run.RunAsync(name, face);
            }
            catch (Exception ex)
            {
                Faults.Note("sharing", "asking a computer to share", ex);
                _pairings.Undelivered(run.DeviceId, run.Attempt);
            }
            finally
            {
                // Disposed here only if it is still the registered run. Cancel and Ask again take
                // the run out themselves and dispose it, after the withdraw has been sent.
                bool mine;
                lock (_gate)
                {
                    mine = _runs.TryGetValue(run.DeviceId, out var registered) && ReferenceEquals(registered, run);
                    if (mine)
                        _runs.Remove(run.DeviceId);
                }
                if (mine)
                    run.Dispose();
            }
        });
    }

    /// <summary>Sends the code typed here for the request to that computer.</summary>
    /// <returns>False when the other computer could not be reached.</returns>
    public async Task<bool> SubmitCodeAsync(string deviceId, string code)
    {
        // A place can be taken while the code is being typed in. For example, another of the user's own
        // computers may be trusted on sight. Sending the code then would make the other computer record a
        // pairing that this computer's book refuses. So the request is cancelled instead. That tells the
        // other computer, and the row shows that this computer is full.
        if (_book.IsFull && _book.Find(deviceId) is null)
        {
            Cancel(deviceId);
            return true;
        }

        PairingRun? run;
        lock (_gate)
            _runs.TryGetValue(deviceId, out run);

        return run is not null && await run.SubmitAsync(code);
    }

    /// <summary>Cancels this computer's request to that one, and tells it.</summary>
    public void Cancel(string deviceId)
    {
        _pairings.Withdraw(deviceId);

        PairingRun? run;
        lock (_gate)
        {
            if (_runs.Remove(deviceId, out run) is false)
                return;
        }

        Shell.Background.Run(async () =>
        {
            try { await run.CancelAsync(); }
            finally { run.Dispose(); }
        });
    }

    /// <summary>Declines that computer's request. Its status call is woken and reads Declined.</summary>
    public void Decline(string deviceId) => _pairings.Decline(deviceId);

    /// <summary>Hides an ending.</summary>
    public void Dismiss(string deviceId) => _pairings.Dismiss(deviceId);

    /// <summary>
    /// Both sides of a pairing arrive here when it is accepted, once each: the server when the right
    /// code came in, and a run when it first saw Accepted. The board hands the pairing to exactly one
    /// caller per side, which is what makes this safe to call from both the code reply and the status
    /// reply.
    /// </summary>
    private void Record(Pairing pairing)
    {
        var peer = pairing.Peer;
        Trust(peer.DeviceId, peer.PublicKey, peer.Name, peer.Face);
        Shell.Background.Run(() => GrantAsync(peer.DeviceId, peer.Name));
    }

    /// <summary>Stops following every request this computer made. The board shows each one as lost,
    /// and the other computer is told on a best-effort basis.</summary>
    private void EndRuns()
    {
        List<PairingRun> runs;
        lock (_gate)
        {
            runs = [.. _runs.Values];
            _runs.Clear();
        }

        foreach (var run in runs)
        {
            _pairings.Lose(run.DeviceId, run.Attempt);
            Shell.Background.Run(async () =>
            {
                try { await run.CancelAsync(); }
                finally { run.Dispose(); }
            });
        }
    }

    private void SweepPairings()
    {
        try { _pairings.Sweep(); }
        catch (Exception ex) { Faults.Note("sharing", "sweeping pairings", ex); }
    }

    /// <summary>The account signed in here (the user id), or empty. What this machine says it is in, inside each
    /// session proof it makes (<see cref="PeerClient"/>).</summary>
    private string MyAccount() => _auth.UserId ?? "";

    /// <summary>
    /// A borrower opened a session here and said, inside its signature, which account it is in. Written into the
    /// book so that <see cref="Restated"/> can tell when one of this account's computers has signed into another
    /// account. Called on the server's thread.
    /// </summary>
    private void Proved(TrustedPeer proved, string account) => Noted(proved, account);

    /// <summary>Records the account <paramref name="peer"/> said it is in, over a link pinned to its key, and returns
    /// the record as it now stands. Applied to the book's current record under its lock (<see cref="PeerBook.Update"/>),
    /// so it cannot put back an older copy of the rest of the record. Nothing is written when nothing changed.</summary>
    private TrustedPeer Noted(TrustedPeer peer, string account) =>
        _book.Update(peer.DeviceId, current =>
            current.AccountProvedUtcTicks > 0 && string.Equals(current.AccountUserId, account, StringComparison.Ordinal)
                ? null
                : current with { AccountUserId = account, AccountProvedUtcTicks = DateTime.UtcNow.Ticks })
        ?? peer;

    /// <summary>Writes a peer into the book, on whichever side of the question this machine was.</summary>
    private void Trust(string deviceId, byte[] publicKey, string name, string face)
    {
        // The account's own list of installs decides, and nothing the machine said about itself. Once it has
        // opened a session it has also said which account it is in, inside a signature, and Restated takes
        // that into account on every pass.
        var mine = PeerAppraisal.IsOwnInstall(deviceId, AccountDeviceIds());

        _book.Add(new TrustedPeer
        {
            DeviceId = deviceId,
            PublicKey = publicKey,
            Name = name,
            Face = face,
            Kind = mine ? PeerKind.Account : PeerKind.Guest,
            PairedUtcTicks = DateTime.UtcNow.Ticks,
            LastSeenUtcTicks = DateTime.UtcNow.Ticks,
        });

    }

    /// <summary>
    /// Puts another person's collection on screen, in place of this one's.
    ///
    /// <para>Only a guest's: another of the user's own computers has no shelf to open, because its
    /// records are already on this one.</para>
    /// </summary>
    /// <returns>False when nothing has been taken from that computer yet. That means it has never been on
    /// the network at the same time as this one.</returns>
    public bool OpenLibrary(string deviceId)
    {
        if (_book.Find(deviceId) is not { Kind: PeerKind.Guest } || !PeerLibraryStore.Exists(deviceId))
            return false;

        // A shelf whose file will not open (written by a newer build, or damaged) is not opened. The
        // store has logged the reason.
        if (_guests.Opened(deviceId) is not { } shelf)
            return false;

        // This is settled before the shelf is drawn, not when somebody first presses play. A beacon can
        // outlive the app that sent it. So whether a machine is on the network has to be asked of the
        // machine itself, not taken from the last thing it broadcast.
        Shell.Background.Run(() => ReachAsync(deviceId));

        _library.ViewPeer(deviceId, shelf);
        return true;
    }

    /// <summary>
    /// Asks a computer whether it is there, and writes down the answer.
    ///
    /// <para>One greeting, which is the cheapest thing this protocol has and needs no token. What it
    /// buys is that a shelf opens already narrowed to what can actually play, instead of looking
    /// complete until somebody presses play and waits.</para>
    /// </summary>
    private async Task ReachAsync(string deviceId, CancellationToken cancellation = default)
    {
        using var borrowed = Borrow(deviceId);
        if (borrowed.Client is not { } client)
        {
            Narrow(deviceId, cloudOnly: true);
            return;
        }

        if (await client.HelloAsync(cancellation) is null)
            Away(deviceId);
        else
            Back(deviceId);
    }

    /// <summary>Back to the user's own collection.</summary>
    public void CloseLibrary() => _library.ViewOwn();

    /// <summary>Stops sharing with one computer: it can no longer read this library, and its music
    /// leaves this one.</summary>
    public void Forget(string deviceId)
    {
        _book.Remove(deviceId);
        _server?.Revoke(deviceId);

        lock (_gate)
        {
            Retire(deviceId);
            _catalogVersions.Remove(deviceId);
        }

        if (_library.IsOpen)
        {
            _library.Db.ForgetPeer(deviceId);
            _library.NotifyChanged();
        }

        // Their shelf and their covers go with their records. Nothing that belongs to someone else is kept
        // after the arrangement that made it visible has ended. If their shelf is on screen, the view goes
        // back to this machine's own collection first.
        if (string.Equals(_library.ViewingPeer, deviceId, StringComparison.OrdinalIgnoreCase))
            _library.ViewOwn();

        _guests.Forget(deviceId, _library.IsOpen ? _library.Root : null);
    }

    // ── what this machine borrows ──

    /// <summary>
    /// Where a track's bytes are on the network, or null when no computer here holds them and can be
    /// reached right now.
    ///
    /// <para>
    /// This is what <see cref="Audio.PlaybackService"/> asks for after the disk and before the cloud. It
    /// takes an id, not a computer. The row knows which machine holds the file, so the caller does not have
    /// to carry the computer through the queue, the look-ahead and the DJ.
    /// </para>
    ///
    /// <para>A track whose computer is not on this network gets null, the same answer as a missing file.
    /// For a local-only record, that is the whole rule. It plays where the machine holding it can be
    /// reached, and nowhere else.</para>
    /// </summary>
    /// <param name="route">Whether the computer holding it may be asked at all. A track that the route has
    /// already dropped mid-song is looked for in the store and nowhere else for the rest of that song. See
    /// <see cref="RPM.Core.Playback.TrackRoute"/>. This is where "and nowhere else" is enforced, because a
    /// borrowed record's cloud copy is also reached through here.</param>
    public Task<string?> UrlForAsync(
        string rpmId, RPM.Core.Playback.TrackRoute route, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(rpmId))
            return Task.FromResult<string?>(null);

        return DeviceHolding(rpmId) is { Length: > 0 } deviceId
            ? TrackUrlAsync(deviceId, rpmId, route, cancellation)
            : Task.FromResult<string?>(null);
    }

    /// <summary>
    /// The bytes of a record stopped arriving mid-track. This writes the computer serving it off, exactly
    /// as a request that went unanswered would.
    ///
    /// <para>This is reported once, so nothing else has to find out the slow way. The shelf narrows to what
    /// the cloud holds, the band above it explains why, and the player's next attempt at the record goes
    /// straight past a computer that is not there.</para>
    /// </summary>
    public void NoteUnreachable(string rpmId)
    {
        if (DeviceHolding(rpmId) is { Length: > 0 } deviceId)
            Away(deviceId);
    }

    private string? DeviceHolding(string rpmId) =>
        DeviceHolding(
            rpmId,
            id => _library.IsOpen ? _library.Db.FindByRpmId(id)?.PeerDeviceId : null,
            _book.Peers,
            (device, id) => _guests.Find(device, id)?.PeerDeviceId);

    /// <summary>
    /// Which computer a record is on, or null when it is this collection's own.
    ///
    /// <para>The collection on screen is checked first, then the borrowed ones. A record that was played
    /// from a peer's shelf keeps playing after the user has switched back to their own collection. An
    /// account peer's rows are adopted into this machine's own mirror. A guest's rows live in the mirror
    /// kept for that computer. That is why both are checked. A guest's record is in neither the user's
    /// library nor their cloud. The streamer needs this answer before it asks for anything.</para>
    /// </summary>
    /// <param name="mine">This machine's own mirror, by track id.</param>
    /// <param name="theirs">A peer's mirror, by that computer and the track id. Given rather than
    /// reached for, so the rule can be tested without a library on a disk.</param>
    public static string? DeviceHolding(
        string rpmId,
        Func<string, string?> mine,
        IReadOnlyList<TrustedPeer> peers,
        Func<string, string, string?> theirs)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(theirs);

        if (string.IsNullOrEmpty(rpmId))
            return null;

        return mine(rpmId)
            ?? peers
                .Select(p => theirs(p.DeviceId, rpmId))
                .FirstOrDefault(id => id is { Length: > 0 });
    }

    /// <summary>The same, when the caller already knows whose machine it is.</summary>
    public async Task<string?> TrackUrlAsync(
        string deviceId, string rpmId, RPM.Core.Playback.TrackRoute route,
        CancellationToken cancellation = default)
    {
        // The computer is not asked at all when the route is closed to it. Asking would do harm. A
        // computer that answers puts the record back on the network mid-song, and that is the seam this
        // code exists to stop. A computer that does not answer costs the listener the timeout.
        if (route.PeerAllowed)
        {
            // A client is borrowed only once the route is known to allow it. So a closed route does not
            // even open a client. See the note above.
            using var borrowed = Borrow(deviceId);
            if (borrowed.Client is { } client)
            {
                var near = await client.TrackUrlAsync(rpmId, cancellation);

                // A token either comes back or it does not. The difference is whether that computer is
                // there at all. The result is recorded either way. The next track of the same record must
                // not spend another three seconds finding out, and the shelf has something to report.
                if (near is { Length: > 0 })
                {
                    Back(deviceId);
                    return near;
                }

                // This retires the client that the current line is still using. That is the normal case.
                // The client closes when this borrow ends, a few lines below.
                Away(deviceId);
            }
        }

        // That computer is not on this network. A record that it holds on its own disk is not playable
        // from here. That is the rule. A record that it has put in the cloud is different. The store has
        // the bytes, and its owner has said that this account may hear them.
        return await SharedCloudUrlAsync(deviceId, rpmId, cancellation);
    }

    /// <summary>
    /// A peer's cloud-stored record, played from the store rather than from their computer.
    ///
    /// <para>The grant made at pairing covers this. It is the only part of sharing that works away from the
    /// network where the pairing was arranged. Null for everything else: signed out, no grant, or a record
    /// whose bytes are on a disk in another person's house.</para>
    /// </summary>
    private async Task<string?> SharedCloudUrlAsync(
        string deviceId, string rpmId, CancellationToken cancellation)
    {
        if (_cloud is null || !_auth.IsLoggedIn ||
            _book.Find(deviceId) is not { AccountUserId.Length: > 0 } peer)
            return null;

        // Only for a track that the store holds a copy of. The peer's listing says which tracks these are.
        // A row that lives only on the peer's disk has nothing in the store to sign for.
        if (_guests.Find(deviceId, rpmId) is not { CloudState: CloudState.CloudOnly })
            return null;

        try
        {
            return await _cloud.GetStreamUrlAsync(
                CloudShelf.Of(peer.AccountUserId), rpmId, ct: cancellation);
        }
        catch (Exception ex)
        {
            Said.Line("sharing",
                $"Could not sign {Who(deviceId)}'s cloud copy of {rpmId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The cover of a borrowed record, from the computer that holds it.
    ///
    /// <para>Kept on this disk under the album it belongs to. So a wall that scrolls past forty borrowed
    /// records asks the other machine once per album, and never again. Null means that computer is not on
    /// the network. The card is then left blank, and this is not an error. It is the same answer as a
    /// record whose folder has no picture.</para>
    /// </summary>
    public async Task<byte[]?> CoverAsync(
        string deviceId, string rpmId, string? albumArtist, string? artist, string? album,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(rpmId))
            return null;

        var key = AlbumArt.KeyForTrack(albumArtist, artist, album);
        var cached = Path.Combine(ArtFolder(deviceId), key + ".img");

        try
        {
            if (File.Exists(cached))
                return await File.ReadAllBytesAsync(cached, cancellation);
        }
        catch
        {
            // A half-written or locked cache file is a picture to fetch again, not a failure.
        }

        using var borrowed = Borrow(deviceId);
        if (borrowed.Client is not { } client)
            return null;

        var bytes = await client.ImageAsync(SharingProtocol.CoverRoute, rpmId, cancellation);
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            Directory.CreateDirectory(ArtFolder(deviceId));
            await File.WriteAllBytesAsync(cached, bytes, cancellation);
        }
        catch
        {
            // This is only a cache. A cover that could not be written is fetched again next time.
        }

        return bytes;
    }

    /// <summary>
    /// The disc art of a borrowed album, from the computer that holds it.
    ///
    /// <para>It is cached beside the cover, with the same key and its own file name. The cover and the disc
    /// art are two pictures of one record, and a record often has one without the other. Null means that
    /// computer is not on the network. Null also means it has no disc art for the record. That is the usual
    /// case. The borrower then draws the app's own pressing, exactly as it does for its own record that has
    /// no disc.png.</para>
    ///
    /// <para>A miss is not remembered. It is one small request to a machine on the same network segment.
    /// Remembering "no" would mean that disc art added on the other machine never appears here.</para>
    /// </summary>
    public async Task<byte[]?> DiscAsync(
        string deviceId, string rpmId, string? albumArtist, string? artist, string? album,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(rpmId))
            return null;

        var key = AlbumArt.KeyForTrack(albumArtist, artist, album);
        var cached = Path.Combine(ArtFolder(deviceId), key + ".disc");

        try
        {
            if (File.Exists(cached))
                return await File.ReadAllBytesAsync(cached, cancellation);
        }
        catch
        {
            // A half-written or locked cache file is a picture to fetch again, not a failure.
        }

        using var borrowed = Borrow(deviceId);
        if (borrowed.Client is not { } client)
            return null;

        var bytes = await client.ImageAsync(SharingProtocol.DiscRoute, rpmId, cancellation);
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            Directory.CreateDirectory(ArtFolder(deviceId));
            await File.WriteAllBytesAsync(cached, bytes, cancellation);
        }
        catch
        {
            // This is only a cache. A disc that could not be written is fetched again next time.
        }

        return bytes;
    }

    /// <summary>The cover for a record on another computer, asked for by id alone.
    /// <para>For a caller that holds only an id. An example is the cast sink, which is given a track
    /// and does not know whose record it is. Which computer holds the record, and the names its cache
    /// is filed under, are both read from the row here. The caller does not guess them. A cover stored
    /// under the track artist, where the display uses the album artist, would be the same picture
    /// fetched and stored twice.</para>
    /// <para>Null for a record in this collection, which is the correct answer. There is no other
    /// computer to ask.</para>
    /// </summary>
    public async Task<byte[]?> CoverForAsync(string rpmId, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(rpmId))
            return null;

        if (DeviceHolding(rpmId) is not { Length: > 0 } deviceId)
            return null;

        // The row is read for its names only. Both mirrors are checked, in the same way as DeviceHolding.
        // An account peer's records are adopted into this machine's own mirror. A guest's records live in
        // the mirror kept for that computer.
        var row = (_library.IsOpen ? _library.Db.FindByRpmId(rpmId) : null)
                  ?? _guests.Find(deviceId, rpmId);

        return await CoverAsync(
            deviceId, rpmId, row?.AlbumArtist, row?.Artist, row?.Album, cancellation);
    }

    /// <summary>
    /// The track id this account's own cloud may be asked about, or null when the record belongs to
    /// somebody else. See <see cref="PeerTrust.OwnCloudId"/>.
    /// </summary>
    public string? OwnCloudId(string? rpmId, string? peerDeviceId) =>
        PeerTrust.OwnCloudId(_book.Peers, rpmId, peerDeviceId);

    /// <summary>
    /// The same, for a caller that holds only the id. Examples are the look-ahead and the player. They are
    /// handed a queue of ids, and they do not know whose records these are.
    /// </summary>
    public string? OwnCloudId(string? rpmId) =>
        string.IsNullOrEmpty(rpmId) ? rpmId : OwnCloudId(rpmId, DeviceHolding(rpmId));

    /// <summary>
    /// Whose cloud a record's pictures are in, when it is not this account's: the account of the
    /// person whose computer holds it. Null for the user's own records and for their own computers,
    /// whose cloud is this one.
    ///
    /// <para>This is the same grant that the music travels under. So it answers for exactly the records
    /// that keep playing after everyone has left the network. Those are the records that a shelf keeps
    /// showing.</para>
    /// </summary>
    public string? CloudOwner(string? peerDeviceId) =>
        peerDeviceId is { Length: > 0 } id
        && _book.Find(id) is { Kind: PeerKind.Guest, AccountUserId.Length: > 0 } peer
            ? peer.AccountUserId
            : null;

    /// <summary>Where one peer's covers are kept. Per peer, so forgetting a computer can take its
    /// pictures with it.</summary>
    private static string ArtFolder(string deviceId) =>
        Path.Combine(LocalData.Dir, "peers", deviceId, "art");

    /// <summary>
    /// Machines that did not answer, and when it is worth trying them again.
    ///
    /// <para>A beacon can outlive the app that sent it. A computer that was shut down sends no goodbye.
    /// So "it is on the network" can be wrong for minutes. Without this record, every track of a borrowed
    /// album would spend the connect timeout finding that out again, one after another. A queue of twenty
    /// tracks would then take a minute, and the player would look as if it had hung.</para>
    /// </summary>
    private readonly Dictionary<string, DateTime> _away = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a computer that did not answer is left alone. Short enough that a laptop
    /// coming back is noticed while the user is still looking at the record they wanted.</summary>
    private static readonly TimeSpan AwayFor = TimeSpan.FromSeconds(30);

    /// <summary>Whether a computer answered the last time this one spoke to it. False is what the
    /// shelf shows: their music cannot play until they are back.</summary>
    public bool IsReachable(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        lock (_gate)
        {
            if (_away.TryGetValue(deviceId, out var until) && DateTime.UtcNow < until)
                return false;
        }

        return _discovery.Seen.Any(p =>
            string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>That computer did not answer. This is recorded once, here. Everything that asks about it
    /// gets the same answer, and nothing has to find out the slow way.</summary>
    private void Away(string deviceId)
    {
        lock (_gate)
        {
            _away[deviceId] = DateTime.UtcNow + AwayFor;
            Retire(deviceId);
        }

        Narrow(deviceId, cloudOnly: true);
        Changed?.Invoke();
    }

    /// <summary>It answered. Whatever it was doing before, it is here now.</summary>
    private void Back(string deviceId)
    {
        bool changed;
        lock (_gate)
            changed = _away.Remove(deviceId);

        Narrow(deviceId, cloudOnly: false);

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>
    /// Shows, or stops showing, the part of a borrowed shelf that needs its owner's computer.
    ///
    /// <para>The rule for sharing is this. A record on somebody's own disk plays where that disk can be
    /// reached, and nowhere else. A record that they put in the cloud plays anywhere. Nothing is deleted.
    /// The rows all stay in place, and they come back as soon as the computer does.</para>
    /// </summary>
    private void Narrow(string deviceId, bool cloudOnly)
    {
        if (!PeerLibraryStore.Exists(deviceId))
            return;

        if (_guests.Opened(deviceId) is not { } shelf || shelf.CloudRowsOnly == cloudOnly)
            return;

        shelf.CloudRowsOnly = cloudOnly;
        _library.NotifyPeerShelfChanged(deviceId);
    }

    /// <summary>One peer's client and the count of who is inside it. Every field is read and written
    /// under <see cref="_gate"/>.</summary>
    private sealed class Held(PeerClient client)
    {
        public PeerClient Client { get; } = client;

        /// <summary>How many exchanges are in flight on it at this moment.</summary>
        public int Users;

        /// <summary>Taken out of the book: nothing new may borrow it, and it closes the moment the
        /// last exchange inside it is over. An entry still IN the book is never retired, which is
        /// what lets <see cref="Borrow"/> hand out anything it finds there.</summary>
        public bool Retired;
    }

    /// <summary>
    /// One peer's client, borrowed for the length of one exchange and given back at the end of it.
    ///
    /// <para>Must be disposed. Disposing it does not close the client. It means that this
    /// exchange is done with it. The client itself closes when the last borrower has left and
    /// something has retired it.</para>
    /// </summary>
    private readonly struct Borrowed(SharingService? owner, Held? held) : IDisposable
    {
        /// <summary>The client, or null for a machine that is not on this network right now.</summary>
        public PeerClient? Client => held?.Client;

        public void Dispose() => owner?.Return(held);
    }

    /// <summary>The client for one peer, made when it is first wanted and kept while its address holds.
    /// <para>Borrowed rather than handed over. That is the whole point of this type. A client is
    /// shared, and it can be disposed. Five separate events can decide that it should close: the peer
    /// going quiet (<see cref="Away"/>), discovery saying it is gone (<see cref="OnLost"/>), the user
    /// stopping the share (<see cref="Forget"/>), sharing being switched off (<see cref="Stop"/>), and
    /// the app closing. If any of them lands inside a catalogue pull, the HttpClient is disposed under
    /// the request that is still using it. That throws <see cref="ObjectDisposedException"/> from
    /// wherever the next call happens to be, several frames below anything that catches it. So the
    /// close waits for the last exchange to leave.</para>
    /// <para>The raw client is reachable no other way, so a caller cannot hold one past the exchange it
    /// borrowed it for without saying so in the code.</para>
    /// </summary>
    private Borrowed Borrow(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || _book.Find(deviceId) is null)
            return default;

        lock (_gate)
        {
            // Asked before the socket is: a machine that has just been found to be away is away for
            // everything, not once per track.
            if (_away.TryGetValue(deviceId, out var until) && DateTime.UtcNow < until)
                return default;

            if (_clients.TryGetValue(deviceId, out var known))
            {
                known.Users++;
                return new Borrowed(this, known);
            }
        }

        if (_discovery.Seen.FirstOrDefault(p =>
                string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) is not { } seen)
            return default;

        // The connection is pinned to the key that this peer is trusted by. That is the key it was paired
        // against. A peer that is not in the book is pinned to the key its beacon carried. The device id is
        // derived from that key. Without a key, any certificate would be accepted.
        var made = new Held(new PeerClient(
            PeerKeyStore.Identity, seen.Host, seen.Port, _book.Find(deviceId)?.PublicKey ?? seen.PublicKey, MyAccount));

        lock (_gate)
        {
            if (_clients.TryGetValue(deviceId, out var raced))
            {
                // Nobody ever borrowed this one, so there is nothing inside it to wait for.
                made.Client.Dispose();
                raced.Users++;
                return new Borrowed(this, raced);
            }

            _clients[deviceId] = made;
            made.Users++;
            return new Borrowed(this, made);
        }
    }

    /// <summary>An exchange is over. Closes the client here when it was retired while this one was
    /// inside it and this was the last one out.</summary>
    private void Return(Held? held)
    {
        if (held is null)
            return;

        bool close;
        lock (_gate)
            close = --held.Users <= 0 && held.Retired;

        if (close)
            held.Client.Dispose();
    }

    /// <summary>Takes a peer's client out of use: nothing new may borrow it, and it closes as soon as
    /// whatever is inside it has left. Call under <see cref="_gate"/>.</summary>
    private void Retire(string deviceId)
    {
        if (!_clients.Remove(deviceId, out var held))
            return;

        held.Retired = true;

        // Nobody is in it, so nobody is going to give it back.
        if (held.Users <= 0)
            held.Client.Dispose();
    }

    /// <summary>
    /// Takes each peer's listing, on a slow loop and whenever one turns up.
    ///
    /// <para>Another of the user's own computers feeds into this library, because it is the same
    /// collection. A peer that is a guest is kept apart. Its listing is read where it is, and it is not
    /// mixed into a shelf that is not theirs.</para>
    /// </summary>
    private async Task CatalogLoopAsync(CancellationToken cancellation)
    {
        // The first pass runs straight away. A machine that was already on the network when this one
        // started has nothing new to announce. Waiting for the interval would leave the shelf incomplete
        // for five minutes for no reason.
        using var timer = new PeriodicTimer(CatalogInterval);
        do
        {
            if (cancellation.IsCancellationRequested)
                return;

            // All peers are pulled at once, not one after another. A computer that is switched off costs
            // nothing here. It is not in what discovery has seen, so no socket is opened for it. But a
            // computer whose beacon is still published after its app has stopped costs the whole connect
            // timeout. In a queue, every machine behind it would wait for that time too. There are at
            // most a handful of peers, each with its own connection and mirror. So the round takes as
            // long as the slowest peer, not the sum of all of them. Nobody holds this task, so nothing
            // would look at what it returns. A failed round is only a failed round. The peers are still
            // there, the timer still ticks, and the next round tries again. But the failure must be
            // caught here. Otherwise the loop would end at the first peer that went offline during a
            // pull, and sharing would quietly stop for the rest of the session.
            try
            {
                await Task.WhenAll(_book.Peers.Select(peer => PullOnceAsync(peer, cancellation)));

                // This runs in the same round. It fails for the same reasons, and it is worth the same
                // one retry. A grant that was not made on the day of pairing means a peer's cloud album
                // will not play the first time they are away from home.
                await EnsureGrantsAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;   // the app is closing, or sharing was switched off
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[Sharing] A catalog round did not finish");
            }
        }
        while (await Cancellable.TickAsync(timer, cancellation));
    }

    /// <summary>
    /// Whether this peer is still one of the user's own installs, and the book brought up to date
    /// when the answer has changed.
    ///
    /// <para>The kind is decided when the two machines pair. But it does not stay fixed. A computer can
    /// sign out, or sign into somebody else's account. When the answer changes, the music that this machine
    /// took on the old answer is removed. That means merged rows leave the collection, or a borrowed shelf
    /// and its covers go. The next pass puts the music where it now belongs. The pairing itself stands,
    /// because the two people still agreed to share.</para>
    /// </summary>
    private bool Restated(TrustedPeer peer)
    {
        var mine = PeerAppraisal.StillOwnInstall(
            peer.DeviceId, peer.AccountUserId, peer.AccountProvedUtcTicks > 0, MyAccount(), AccountDeviceIds());

        if (mine == peer.Kind is PeerKind.Account)
            return mine;

        Said.Line("sharing",
            $"{Who(peer.DeviceId)} is {(mine ? "now" : "no longer")} one of this account's computers");

        if (mine)
        {
            // It was a guest. Its shelf and covers are another person's arrangement. This is now the same
            // collection, so the pass below fills the library from its listing.
            _guests.Forget(peer.DeviceId, _library.IsOpen ? _library.Root : null);

            if (string.Equals(_library.ViewingPeer, peer.DeviceId, StringComparison.OrdinalIgnoreCase))
                _library.ViewOwn();
        }
        else if (_library.IsOpen)
        {
            // It was one of the user's own. Its rows are in this collection, but they must not stay there
            // now.
            _library.Db.ForgetPeer(peer.DeviceId);
            _library.NotifyChanged();
        }

        _book.Add(peer with { Kind = mine ? PeerKind.Account : PeerKind.Guest });
        return mine;
    }

    /// <summary>The head of a catalogue version, which is all a log line needs of a hash: enough to
    /// tell one answer from the next, and short enough to sit beside the counts.</summary>
    private static string Short(string? version) =>
        version is { Length: > 0 } v ? v[..Math.Min(8, v.Length)] : "nothing";

    /// <summary>A peer as a ring line names it: the start of its device id, which is derived from its
    /// key. Not its name, which is the owner's own wording. The last 300 ring lines are sent with a crash
    /// report.</summary>
    private static string Who(string deviceId) => $"peer {Short(deviceId)}";

    /// <summary>One computer being read at a time, however many things ask for it.</summary>
    private readonly RPM.Core.Sharing.PullGate _pulls = new();

    /// <summary>
    /// Reads a peer's catalogue, unless it is already being read.
    ///
    /// <para><b>The only way to call <see cref="PullAsync"/>.</b> It is started from three places: the
    /// timed round, a beacon from a computer that is already known, and a computer that is trusted on
    /// sight. A beacon is not a single event. The responder re-announces, and in the first minutes after
    /// launch the same machine is found over and over. Each of those would start a whole pass over that
    /// machine's listing. A pass holds the library's one writing gate for its whole length.</para>
    ///
    /// <para>Without this, passes queued behind each other. In one measurement on a single borrowed
    /// shelf, <c>MirrorPeerLibrary</c> calls waited 57, 77 and 91 seconds behind other
    /// <c>MirrorPeerLibrary</c> calls. The user's own album edit then waited 91 seconds behind those. The
    /// work itself was not slow. Several passes of it ran, and each one held the gate that the next one
    /// needed.</para>
    ///
    /// <para>A second request is merged into one repeat rather than queued, because two passes would read
    /// the same catalogue. See <see cref="RPM.Core.Sharing.PullGate"/>.</para>
    /// </summary>
    private async Task PullOnceAsync(TrustedPeer peer, CancellationToken cancellation)
    {
        if (peer is null || !_pulls.Begin(peer.DeviceId))
            return;

        do
        {
            await PullAsync(peer, cancellation);
        }
        while (_pulls.Continue(peer.DeviceId) && !cancellation.IsCancellationRequested);
    }

    /// <summary>
    /// One peer's listing, taken and applied. A machine that is not there is not reported, because that
    /// is the normal state of a closed laptop.
    /// </summary>
    private async Task PullAsync(TrustedPeer peer, CancellationToken cancellation)
    {
        using var borrowed = Borrow(peer.DeviceId);
        if (borrowed.Client is not { } client)
            return;

        try
        {
            string? have;
            lock (_gate)
                _catalogVersions.TryGetValue(peer.DeviceId, out have);

            if (await client.CatalogAsync(have, cancellation) is not { } catalog)
                return;

            Back(peer.DeviceId);

            // The account that this peer is signed into is asked for once and written down. It is
            // deliberately not gated on this machine being signed in. The account is what the person's
            // name and face on the share button are looked up under. Someone who is signed out is still
            // entitled to see who they are borrowing from. This must run before the two writes below,
            // because both of them put back a record that was read before this line.
            //
            // It is asked on every pull, for every peer, not only once for a guest. The answer comes over the
            // link pinned to this peer's key, so it is the peer's own word, and it is what Restated uses to
            // notice a computer of the user's own that has signed into somebody else's account.
            if (await client.AccountAsync(cancellation) is { } whose)
                peer = Noted(peer, whose.UserId ?? "");

            _book.Seen(peer.DeviceId);

            // This is checked again here, and not taken from the book. It can change while a pairing
            // stands. A computer that signs into another account stops being one of the user's own at that
            // moment. Its records must then stop feeding into their collection.
            var mine = Restated(peer);

            // One of the user's own computers feeds into this library, because it is the same collection.
            // A peer that is a guest is kept in a mirror of its own, and it is read where it is. The value
            // is read once. The change below is announced for whichever database was written.
            var intoOwn = mine && _library.IsOpen;
            var result = intoOwn
                ? _library.Db.MirrorPeerLibrary(
                    peer.DeviceId,
                    PeerLibraryMirror.ToCatalogRows(catalog.Tracks),
                    prune: true,
                    cloudOnly: PeerLibraryMirror.CloudOnlyIds(catalog.Tracks))
                : _guests.Apply(peer.DeviceId, catalog);

            // The album rows are merged row by row, using whichever edit is later. They go into the same
            // database as the tracks. One of the user's own machines feeds into this collection. A
            // guest's album rows land in the mirror kept for that computer alone. A borrowed record is
            // then drawn the way its owner has it. A classical box is drawn as works and movements, and a
            // linked pressing keeps its catalogue number. None of the owner's opinions of the record
            // cross over.
            //
            // An older build sends no albums at all. That reads as nothing to merge.
            var edits = PeerCatalogBuilder.To(catalog.Albums);
            var albums = mine
                ? intoOwn
                    ? _library.Db.ApplyAlbumMetaEdits(edits, ArrivingAlbumMarks.Whole)
                    : 0
                : _guests.ApplyAlbums(peer.DeviceId, edits);

            // The playlists go into the mirror kept for that computer, and nowhere else. They never go
            // into this collection, even for one of the user's own machines. Those trees travel through
            // the cloud. A second route into the same panel would make a second copy of every list. The
            // generated lists go in beside the playlists, in the places the owner files them. On a
            // borrowed shelf, a generated list is a playlist. It is a fixed order of records with a name
            // on it, and nothing here can cut it again. So the panel already draws it once it is folded
            // in. Each row says which generated list it is, so it shows that list's picture. See
            // BorrowedMoodLists.
            var playlists = mine
                ? 0
                : _guests.ApplyPlaylists(
                    peer.DeviceId,
                    [.. PeerCatalogBuilder.To(catalog.Playlists),
                     .. BorrowedMoodLists.AsTree(
                         catalog.MoodLists,
                         SmartPlaylistFace.MoodsFolderName,
                         BorrowedMoodLists.RpmName)]);

            if (result.Total > 0 || albums > 0 || playlists > 0)
            {
                if (intoOwn)
                    _library.NotifyChanged();
                else
                    _library.NotifyPeerShelfChanged(peer.DeviceId);
            }

            // WHAT ARRIVED, AND FROM WHERE, BEFORE WHAT WAS DONE WITH IT.
            //
            // This line reports what arrived from the peer, which is the first thing to know. It also
            // shows the address that the listing came from. A shelf that fills and empties on alternate
            // passes has one of two causes. Either one machine is changing its answer, or two machines
            // are answering under one device id. Only the address tells these two apart.
            Said.Line("sharing",
                $"{Who(peer.DeviceId)} at {client.BaseUrl} sent {catalog.Tracks.Count} tracks, " +
                $"{catalog.Albums.Count} albums, {catalog.Playlists.Count} playlists and " +
                $"{catalog.MoodLists.Count} generated lists (version {Short(catalog.Version)}, " +
                $"had {Short(have)}): {result.Adopted} new, {result.Updated} changed, " +
                $"{result.Forgotten} gone, {result.Reachable} nearer, {albums} album rows and " +
                $"{playlists} playlist rows written");

            lock (_gate)
                _catalogVersions[peer.DeviceId] = catalog.Version;

            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // This is not our cancellation. The connection was closed under the request, because the
            // beacon moved this machine to another address or said it had gone. That is a normal thing
            // for a laptop on a wireless network to do. It is not a reason to end the round. If this
            // exception left this method, the loop above would read it as the app closing, and it would
            // stop for the rest of the session.
            Said.Line("sharing", $"The connection to {Who(peer.DeviceId)} went while its listing was on the way");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A machine that is not there is the normal state of a closed laptop, and it reports nothing.
            // A machine that is there but does not answer is worth showing on screen. From that moment,
            // the shelf on this side is no longer accurate, and nothing else would report it.
            if (_discovery.Seen.Any(p =>
                    string.Equals(p.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                Faults.Note("sharing", "reading a shared library", ex);
                Fault(new SharingTrouble.Reading(peer.Name));
            }
            else
                Said.Line("sharing", $"could not read a shared library (not on the network): {ex.Message}");
        }
    }

    // ── reacting to the world ──

    private void OnFound(DiscoveredPeer peer)
    {
        var verdict = PeerAppraisal.Appraise(
            peer,
            PeerKeyStore.Identity.DeviceId,
            _book.Peers,
            AccountDeviceIds());

        switch (verdict)
        {
            case PeerVerdict.Known:
                // Its address may have changed while it was away; the client is rebuilt against
                // whatever the beacon says now.
                DropClient(peer.DeviceId);
                Shell.Background.Run(() => PullOnceAsync(_book.Find(peer.DeviceId)!, CancellationToken.None));
                break;

            case PeerVerdict.AutoTrust:
                Trust(peer.DeviceId, peer.PublicKey, peer.Name, face: "");
                Shell.Background.Run(() => PullOnceAsync(_book.Find(peer.DeviceId)!, CancellationToken.None));
                break;

            // Ask: nothing to do here. It gets a row under "On this network" from Nearby, which the
            // Changed below makes the screens read again.
        }

        Changed?.Invoke();
    }

    private void OnLost(string deviceId)
    {
        DropClient(deviceId);
        Changed?.Invoke();
    }

    private void DropClient(string deviceId)
    {
        lock (_gate) Retire(deviceId);
    }

    private void OnBookChanged()
    {
        if (_server is { } server)
            server.Trusted = () => _book.Peers;

        Changed?.Invoke();
    }

    private void OnSettingsChanged()
    {
        // Every setting in the app arrives here. Re-announcing costs the calling thread about a second of
        // multicast. What the beacon says and what has merely changed are two different questions. See
        // BeaconReaction, which gives the whole answer.
        switch (BeaconReaction.For(
                    wanted: ShouldServe,
                    serving: IsServing,
                    announced: _beaconName,
                    name: _settings.ShareName,
                    announcedFace: _beaconFace,
                    face: _settings.ShareFace))
        {
            case BeaconChange.Restart:
                RestartSoon();
                break;
            case BeaconChange.Reannounce:
                Reannounce();
                break;
        }
    }

    /// <summary>What the beacon on the network currently calls this machine.
    /// <para>This is held here, and not read back from the server. It answers a question about the
    /// network. The server's own name is what it tells a caller once the caller has arrived. The
    /// beacon's name is what the neighbours' caches hold until the beacon is withdrawn.</para>
    /// </summary>
    private string _beaconName = "";

    /// <summary>The display name standing in the neighbours' caches beside <see cref="_beaconName"/>.
    /// Tracked for the same reason and compared for the same reason: renaming the person is as much a
    /// change to the record as renaming the machine.</summary>
    private string _beaconFace = "";

    /// <summary>Says this machine's record again, with whatever the settings now hold. The old record
    /// is withdrawn first, which is the expensive half and the reason nothing calls this idly.</summary>
    private void Reannounce()
    {
        if (_server is not { } server)
            return;

        _beaconName = _settings.ShareName;
        _beaconFace = _settings.ShareFace;
        _discovery.Republish(PeerKeyStore.Identity, server.Port, _beaconName);

        if (_server is { } standing)
            standing.Face = _beaconFace;
    }

    /// <summary>Signing in or out changes who this machine is to the others, so the beacon has to say
    /// so. Signing out also takes away every row that was here because of an account.</summary>
    private void OnAccountChanged()
    {
        if (!_auth.IsLoggedIn)
        {
            if (_library.IsOpen)
                _library.Db.ForgetAllPeers();

            // And the shelf on screen goes with them, if it was theirs. Leaving somebody in
            // a borrowed collection they have just been told they need an account for is the app
            // asking and refusing at the same time. Every record on it is now unplayable.
            //
            // The drop above is announced by whichever of these two runs, never by both: ViewOwn
            // raises the change itself, and a second one would rebuild the tree twice off the same
            // fact. Both come AFTER the write, so nothing redraws a collection that is half gone.
            if (_library.IsPeerView)
                _library.ViewOwn();
            else if (_library.IsOpen)
                _library.NotifyChanged();
        }

        // Sharing is arranged between accounts. So signing in or out starts or stops the whole
        // arrangement. It is not just a field that changes on a running server. This covers the beacon's
        // fingerprint, the grants, and whether a server runs at all. Restart does exactly that, and it
        // registers this device again on the way up.
        RestartSoon();

        // A machine that is not sharing never reaches the line inside Restart that does this. But the
        // account still has to know which computers are its own. That is what lets the user's other
        // machines recognise this one without being asked.
        if (!IsServing)
            Shell.Background.Run(() => SyncAccountAsync());
    }

    // ── the account's own computers ──

    /// <summary>
    /// The account's registered installs, as this machine last read them. They are kept on disk beside
    /// the key. Auto-trust has to work on a network with no route to the internet. That is often the only
    /// network that two of a person's computers share.
    /// </summary>
    private AccountRegistry _account = new("", []);

    /// <summary>
    /// The device ids that the signed-in account has registered. These make "that is one of mine" a fact
    /// and not a claim, so auto-trust rests on them.
    ///
    /// <para>The list is used only for the account that is signed in right now. A cached list that belongs
    /// to another person who used this computer is not an answer about this user's machines.</para>
    /// </summary>
    private IReadOnlyCollection<string> AccountDeviceIds() =>
        _auth.UserId is { Length: > 0 } userId && _account.UserId == userId
            ? _account.DeviceIds
            : [];

    /// <summary>Tells the account this install exists, and reads back the others.
    /// <para>Both halves are needed for what sign-in promises. The other computers cannot recognise
    /// this one until it has registered. This computer cannot recognise them until it has read the
    /// list. A failure is quiet. Without internet, the last saved list is used. That is why the list is
    /// written down.</para>
    /// </summary>
    private async Task SyncAccountAsync()
    {
        if (_cloud is null || !_auth.IsLoggedIn || _auth.UserId is not { Length: > 0 } userId)
            return;

        var identity = PeerKeyStore.Identity;

        try
        {
            await _cloud.RegisterDeviceAsync(
                identity.DeviceId,
                SharingProtocol.ToBase64Url(identity.PublicKey),
                _settings.ShareName);

            var devices = await _cloud.GetDevicesAsync();

            _account = new AccountRegistry(userId, [.. devices.Select(d => d.DeviceId)]);
            AccountRegistry.Write(_account);

            // A machine of the user's own may have been on this network, unrecognised, while the list was
            // unknown. Now that the list is known, it is recognised as one of theirs.
            foreach (var peer in _discovery.Seen)
                OnFound(peer);
        }
        catch (Exception ex)
        {
            Said.Line("sharing", $"Could not reach the account's device list: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes this install off the account's list of its own computers.
    ///
    /// <para>This runs on sign-out. Without it, the account would keep vouching for a machine that somebody
    /// else may be using. The user's other computers would trust it without any question.</para>
    /// </summary>
    private async Task UnregisterAsync()
    {
        if (_cloud is null || !_auth.IsLoggedIn)
            return;

        try
        {
            await _cloud.ForgetDeviceAsync(PeerKeyStore.Identity.DeviceId);
        }
        catch (Exception ex)
        {
            Said.Line("sharing", $"Could not take this computer off the account: {ex.Message}");
        }

        _account = new AccountRegistry("", []);
        AccountRegistry.Write(_account);
    }

    /// <summary>Makes sure that every computer this one shares with can hear its cloud-stored records.
    /// The grant that <see cref="GrantAsync"/> tries to make once, at the moment of pairing, is not
    /// enough on its own.
    /// <para>The grant is made in the seconds after two people agree to share. That happens over an
    /// internet connection that may have nothing to do with the network they are both connected to. If
    /// it fails (no connection, an expired token, or a server that does not have the route yet),
    /// nothing tries again. The failure then shows up later, as a peer's cloud album that will not play
    /// once they have gone home. So this runs again on each round.</para>
    /// <para>It is safe to repeat, and it is cheap. It makes one call to read what is already granted,
    /// and it grants only what is missing. Computers on the same account need nothing. The store lets
    /// an account read its own records wherever they are.</para>
    /// </summary>
    private async Task EnsureGrantsAsync(CancellationToken cancellation = default)
    {
        if (_cloud is null || !_auth.IsLoggedIn)
            return;

        var guests = _book.Peers.Where(p => p.Kind is PeerKind.Guest).ToList();
        if (guests.Count == 0)
            return;

        try
        {
            var shares = await _cloud.GetSharesAsync(cancellation);
            var already = new HashSet<string>(
                shares.Granted.Select(s => s.GranteeId), StringComparer.Ordinal);

            foreach (var peer in guests)
            {
                if (cancellation.IsCancellationRequested)
                    return;

                var theirs = peer.AccountUserId;

                // Whose account it is can only be asked of the machine itself, and only over a
                // connection it has agreed to. A computer that is not here is asked next time.
                if (string.IsNullOrEmpty(theirs))
                {
                    using var borrowed = Borrow(peer.DeviceId);
                    if (borrowed.Client is not { } client ||
                        await client.AccountAsync(cancellation) is not { UserId.Length: > 0 } account)
                        continue;

                    theirs = account.UserId;
                    Noted(peer, theirs);
                }

                if (already.Contains(theirs))
                    continue;

                // A guest can be signed into this machine's own account: it proved the account, but it is
                // not on the account's list of installs that this machine holds, which is what decides the
                // kind (PeerAppraisal). It needs no grant. An account always reaches its own cloud records,
                // and the server refuses a grant to itself.
                if (string.Equals(theirs, MyAccount(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (await _cloud.GrantShareAsync(theirs, peer.Name, cancellation))
                    Said.Line("sharing", $"{Who(peer.DeviceId)} may now hear this collection's cloud copies");
                else
                    Said.Line("sharing", "This collection already reaches five places");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fault(new SharingTrouble.Cloud(Faults.Note("sharing", "sharing the cloud copies", ex)));
        }
    }

    /// <summary>
    /// Records, on both accounts, that these two may hear each other's cloud-stored music.
    ///
    /// <para>This happens only for a pairing that was made on a shared network. That is the rule for who
    /// may be granted anything. It also needs both machines to be signed in. Two signed-out computers share
    /// only what is on their disks.</para>
    /// </summary>
    private async Task GrantAsync(string deviceId, string name)
    {
        if (_cloud is null || !_auth.IsLoggedIn)
            return;

        using var borrowed = Borrow(deviceId);
        if (borrowed.Client is not { } client)
            return;

        try
        {
            if (await client.AccountAsync() is not { UserId.Length: > 0 } theirs)
            {
                // They will not say yet. On this side of a pairing, that is the usual case. It is the
                // reason a grant cannot be made in one attempt at this moment.
                Shell.Background.Run(() => RetryGrantsAsync());
                return;
            }

            // This is stored with the peer. A borrowed cloud record is asked for under this account once
            // everybody has left the network. Asking again at that point is not possible. Written through
            // Noted, which changes the book's current record under its lock, so an older copy of the rest of
            // the record is not put back.
            if (_book.Find(deviceId) is { } known)
                Noted(known, theirs.UserId);

            // Signed into this machine's own account: no grant is needed, and the server refuses one. See
            // EnsureGrantsAsync.
            if (string.Equals(theirs.UserId, MyAccount(), StringComparison.OrdinalIgnoreCase))
                return;

            if (!await _cloud.GrantShareAsync(theirs.UserId, name))
                Said.Line("sharing", "This collection already reaches five places");
        }
        catch (Exception ex)
        {
            Said.Line("sharing", $"Could not grant {Who(deviceId)} the cloud half: {ex.Message}");

            // This is not the end of it. The most common reason for failure is the pairing itself. The
            // machine that was asked reaches this line before the machine that asked has written it into
            // its book. So there is no token to ask whose account it is. A few seconds later there is.
            Shell.Background.Run(() => RetryGrantsAsync());
        }
    }

    /// <summary>One more go at the grants, shortly. See <see cref="EnsureGrantsAsync"/>.</summary>
    private async Task RetryGrantsAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await EnsureGrantsAsync();
        }
        catch
        {
            // The slow loop comes round to it anyway.
        }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        Stop();
        EndRuns();
        _discovery.Dispose();
    }
}

/// <summary>
/// The account's own installs, as this machine last read them, and whose account they are.
///
/// <para>This is written down, because auto-trust must work with no route to the internet. Two of a
/// person's computers on a home network with no internet are exactly the case that sharing is for.
/// The account id is stored with the list. So the next person to sign in on this machine is not given
/// the last person's computers.</para>
/// </summary>
public sealed partial record AccountRegistry(string UserId, IReadOnlyList<string> DeviceIds)
{
    private static string Path => System.IO.Path.Combine(LocalData.Dir, "account-devices.json");

    public static AccountRegistry Read()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize(File.ReadAllText(Path), SharingServiceJson.Default.AccountRegistry) ?? Empty
                : Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static void Write(AccountRegistry registry)
    {
        try
        {
            Directory.CreateDirectory(LocalData.Dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(registry, SharingServiceJson.Default.AccountRegistry));
        }
        catch
        {
            // If the data folder cannot be written, this session still recognises the user's other
            // computers. The next launch asks the account again.
        }
    }

    private static AccountRegistry Empty => new("", []);

    /// <summary>The registry file's shape, as the source generator describes it. A published build
    /// is Native AOT, which serialises from this and never by reflection.</summary>
    [JsonSerializable(typeof(AccountRegistry))]
    private sealed partial class SharingServiceJson : JsonSerializerContext;
}
