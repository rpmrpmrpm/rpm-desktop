using RPM.Core.Diagnostics;
using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Core.Threading;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// Keeps the NAS that holds this computer's library up to date: both catalogues, the trust list, and the
/// pictures of the playlists.
///
/// <para><b>It compares facts; it does not count events.</b> On every tick, and soon after anything that
/// may have changed one of them, it asks whether the NAS already holds the current catalogue versions and
/// the current trust list. If not, it uploads. A change that lands during an upload, or an event that is
/// missed, is caught by the next comparison. See the NAS section of the concurrency map.</para>
///
/// <para>One upload at a time. A trigger during an upload sets one flag; it never queues more than one
/// more run.</para>
/// </summary>
public sealed class NasPublisher : IDisposable
{
    /// <summary>How often the NAS's copy is compared with the computer's, when nothing has asked sooner.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly LibraryService _library;
    private readonly SharingService _sharing;
    private readonly NasDiscovery _nas;
    private readonly PeerBook _book;
    private readonly AuthService _auth;
    private readonly ILogger<NasPublisher> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;

    /// <summary>What the NAS holds as far as this computer knows. Used only from the loop's task.</summary>
    private readonly NasUploadState _state = new();

    public NasPublisher(
        LibraryService library, SharingService sharing, NasDiscovery nas, PeerBook book, AuthService auth,
        ILogger<NasPublisher> log)
    {
        _library = library;
        _sharing = sharing;
        _nas = nas;
        _book = book;
        _auth = auth;
        _log = log;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
                return;
            _cts = new CancellationTokenSource();
            _nas.Changed += Poke;
            _book.Changed += Poke;
            _library.Opened += OnOpened;
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }

        OnOpened("");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_cts is null)
                return;
            _nas.Changed -= Poke;
            _book.Changed -= Poke;
            _library.Opened -= OnOpened;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// The NAS machines are looked for only when the open library has a library id, which a folder on a
    /// network volume always has. First-run setup and the library settings start the search themselves.
    /// Reading the id touches the share, so it is done off the interface thread.
    /// </summary>
    private void OnOpened(string _) => Task.Run(() =>
    {
        if (_library.IsOpen && _library.Root is { Length: > 0 } root && NasLibrary.IdOf(root) is not null)
            _nas.Start();
        Poke();
    });

    /// <summary>Asks for a comparison soon. Safe from any thread, and as often as wanted.</summary>
    public void Poke()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* One wake is already waiting; that is enough. */ }
    }

    private async Task LoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(Tick, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await CompareAndUploadAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // This loop runs for the life of the app. One bad turn must not end it.
                _log.LogDebug(ex, "[Nas] An upload to the NAS failed");
            }
        }
    }

    /// <summary>One comparison, and an upload when the NAS's copy is not the current one. Public for the
    /// tests.</summary>
    public async Task CompareAndUploadAsync(CancellationToken cancellation)
    {
        if (Target() is not { } target)
            return;

        var trustList = _sharing.NasTrustList();
        var now = new NasUploadState.Snapshot(
            target.Nas.DeviceId,
            _sharing.CatalogVersion(PeerCatalogBuilder.AlbumAudience.TheOwner),
            _sharing.CatalogVersion(PeerCatalogBuilder.AlbumAudience.AGuest),
            NasUploadState.TrustKey(trustList));
        if (!_state.NeedsUpload(now, DateTime.UtcNow))
            return;

        var ownerCatalog = _sharing.CatalogFor(PeerCatalogBuilder.AlbumAudience.TheOwner);
        var guestCatalog = _sharing.CatalogFor(PeerCatalogBuilder.AlbumAudience.AGuest);
        using var client = new PeerClient(
            PeerKeyStore.Identity, target.Nas.Host, target.Nas.Port, target.Nas.PublicKey, () => _auth.UserId ?? "");
        var accepted = await client.PublishAsync(
            new NasPublish { Owner = ownerCatalog, Guest = guestCatalog, Trust = trustList }, cancellation);
        if (!accepted)
        {
            // Unreachable, refused, or this key is not in the claim file. The wait grows with each failure,
            // so a NAS that refuses is not asked every fifteen seconds.
            var wait = _state.Failed(DateTime.UtcNow);
            Said.Line("nas", $"upload to {target.Nas.Name} not taken; next try in {wait.TotalSeconds:0} s");
            return;
        }

        // The versions recorded are the ones in the catalogues just built. A write between the version
        // check and the build makes them newer than `now`, and the next comparison then sees a difference
        // and uploads again, which is the safe direction.
        _state.Uploaded(now with { OwnerVersion = ownerCatalog.Version, GuestVersion = guestCatalog.Version });
        Said.Line("nas", $"uploaded {ownerCatalog.Tracks.Count} tracks and {trustList.Count} trusted keys to {target.Nas.Name}");

        foreach (var playlist in ownerCatalog.Playlists)
        {
            if (playlist.Picture is not { Length: > 0 } stamp || !_state.PictureNeeded(playlist.Id, stamp))
                continue;
            var key = $"pl:{playlist.Id}";
            if (await _sharing.OwnArtAsync(key, cancellation) is { } art &&
                await client.PutArtAsync(key, art.Bytes, cancellation))
                _state.PictureSent(playlist.Id, stamp);
        }
    }

    private sealed record UploadTarget(DiscoveredNas Nas, string Root);

    /// <summary>The NAS to upload to: the one serving the open library, when this computer's key is in the
    /// library's claim file. Null otherwise.</summary>
    private UploadTarget? Target()
    {
        if (!_library.IsOpen || _library.Root is not { Length: > 0 } root ||
            NasLibrary.IdOf(root) is not { } libraryId ||
            _nas.LibraryServing(libraryId) is not { } nas)
            return null;
        if (NasClaim.Read(root) is not { } claim || !claim.Holds(PeerKeyStore.Identity.PublicKey))
            return null;
        return new UploadTarget(nas, root);
    }
}
