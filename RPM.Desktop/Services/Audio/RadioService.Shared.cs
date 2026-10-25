using RPM.Core.Cloud;
using RPM.Core.Radio;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The saved stations radio shows, and whether they are the listener's own to change (the TV's
/// SharedStations and SavedStations, the iPhone's SavedStations).
///
/// <para>While a collection that another account shares is open, radio shows that account's saved stations,
/// read only: no heart to press and no Add to favourites (Chivan, 2026-10-18: "my favorite radio stations
/// should still appear on shared libraries"). They are read from <c>cloud/station-favorites?owner=</c>,
/// behind the same share check as the collection's other reads, each time <see cref="ReadShared"/> is asked
/// (Pegasus asks on every visit to radio). Nothing here writes another account's stations. The listener's
/// own come back when their own collection is open again.</para>
///
/// <para>Each read is filed under the account that made it and the owner it is for, so a read that lands
/// after the listener signed out or opened another collection is not shown for another one.</para>
/// </summary>
public sealed partial class RadioService
{
    /// <summary>What was read, for which signed-in account: the owners' stations, and the owners whose read
    /// failed with nothing read for them before. Replaced as one under <see cref="_sharedGate"/>.</summary>
    private sealed record SharedRead(
        string User,
        IReadOnlyDictionary<string, IReadOnlyList<RadioStation>> ByOwner,
        IReadOnlySet<string> Failed);

    private readonly object _sharedGate = new();
    private SharedRead _shared = new("", new Dictionary<string, IReadOnlyList<RadioStation>>(), new HashSet<string>());

    /// <summary>Raised, on the thread the read finished on, when an owner's stations have been read or
    /// could not be.</summary>
    public event Action? SharedChanged;

    /// <summary>
    /// The saved stations to show now: the listener's own (<see cref="Favorites"/>), or, while somebody
    /// else's collection is open, its owner's, read only. Read straight from what is held, so it can be
    /// asked on the drawing thread.
    /// </summary>
    public SavedStations Saved
    {
        get
        {
            if (!_library.IsPeerView)
                return new SavedStations(Favorites.Stations, Mine: true);

            // A computer whose account is not known (a peer on this network that never proved one) has no
            // stations to read: not the listener's own either, because the collection is not theirs.
            if (SharedOwner() is not { } owner || App.Services.GetService<AuthService>()?.UserId is not { Length: > 0 } user)
                return new SavedStations([], Mine: false, Unreadable: true);

            var read = Volatile.Read(ref _shared);
            if (read.User != user)
                return new SavedStations([], Mine: false, Reading: true);
            if (read.ByOwner.TryGetValue(owner, out var stations))
                return new SavedStations(stations, Mine: false);
            return read.Failed.Contains(owner)
                ? new SavedStations([], Mine: false, Unreadable: true)
                : new SavedStations([], Mine: false, Reading: true);
        }
    }

    /// <summary>The account whose collection is open, where it is somebody else's and their account is
    /// known (see SharingService.CloudOwner).</summary>
    private string? SharedOwner() =>
        _library.ViewingPeer is { Length: > 0 } peer
            ? App.Services.GetService<SharingService>()?.CloudOwner(peer)
            : null;

    /// <summary>Reads the open collection's owner's saved stations again, where a shared collection is open.
    /// Nothing waits on it; <see cref="SharedChanged"/> says when it has landed.</summary>
    public void ReadShared()
    {
        if (!_library.IsPeerView || SharedOwner() is not { } owner)
            return;
        if (App.Services.GetService<AuthService>()?.UserId is not { Length: > 0 } user
            || App.Services.GetService<RpmCloudClient>() is not { } cloud)
            return;

        _ = Task.Run(() => ReadSharedAsync(cloud, user, owner));
    }

    private async Task ReadSharedAsync(RpmCloudClient cloud, string user, string owner)
    {
        IReadOnlyList<FavoriteRow> rows;
        try
        {
            rows = await cloud.ListSharedStationFavoritesAsync(CloudShelf.Of(owner));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RPM.Core.Diagnostics.Scope.Said("radio", $"saved stations of {owner} not read: {ex.GetType().Name}");
            lock (_sharedGate)
            {
                if (App.Services.GetService<AuthService>()?.UserId != user)
                    return;
                var was = _shared.User == user ? _shared : Empty(user);

                // A failure does not replace a list an earlier read found.
                if (!was.ByOwner.ContainsKey(owner))
                    Volatile.Write(ref _shared, was with { Failed = new HashSet<string>(was.Failed) { owner } });
            }
            SharedChanged?.Invoke();
            return;
        }

        // Saved ones only, oldest saved first, as the listener's own are kept.
        var stations = rows.Where(r => r.Saved && r.Station is not null)
                           .OrderBy(r => r.ChangedAt)
                           .Select(r => r.Station!)
                           .ToList();
        lock (_sharedGate)
        {
            if (App.Services.GetService<AuthService>()?.UserId != user)
                return;
            var was = _shared.User == user ? _shared : Empty(user);
            var byOwner = new Dictionary<string, IReadOnlyList<RadioStation>>(was.ByOwner) { [owner] = stations };
            var failed = new HashSet<string>(was.Failed);
            failed.Remove(owner);
            Volatile.Write(ref _shared, new SharedRead(user, byOwner, failed));
        }

        RPM.Core.Diagnostics.Scope.Said("radio", $"{stations.Count} saved stations of {owner}");
        SharedChanged?.Invoke();

        static SharedRead Empty(string user) =>
            new(user, new Dictionary<string, IReadOnlyList<RadioStation>>(), new HashSet<string>());
    }
}

/// <summary>
/// The saved stations radio shows, and whether they are the listener's own (see RadioService.Saved). On a
/// shared collection, <see cref="Reading"/> is set until its owner's stations have been read once, and
/// <see cref="Unreadable"/> when that read failed with none read before. The list is empty in both, so a
/// screen can tell "none saved" from "not known".
/// </summary>
public sealed record SavedStations(
    IReadOnlyList<RadioStation> Stations,
    bool Mine,
    bool Reading = false,
    bool Unreadable = false)
{
    /// <summary>Whether a station is among them.</summary>
    public bool Contains(string uuid) => Stations.Any(s => s.Is(uuid));
}
