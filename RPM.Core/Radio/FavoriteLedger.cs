namespace RPM.Core.Radio;

/// <summary>The newest change to one station's place in the saved stations: saved or not, when (Unix time
/// in milliseconds, from the clock of the device that made it), and the station itself.</summary>
public sealed record FavoriteChange(bool Saved, long ChangedAt, RadioStation Station);

/// <summary>One row of the cloud's saved-stations listing: the newest change to one station, and the
/// store's own timestamp.</summary>
public sealed record FavoriteRow(string Uuid, bool Saved, long ChangedAt, RadioStation? Station, long UpdatedAt);

/// <summary>
/// The saved stations as changes, so they can be kept in step with the listener's other devices through
/// <c>cloud/station-favorites</c>. There is one change per station, keyed by its id in lower case. The
/// change kept is the newest one made to that station on any device. The same rules apply in the API's
/// store, the Android apps and the iPhone app:
///
/// <list type="bullet">
/// <item>The newest change to a station wins, judged by the clocks of the devices that made them. An unsave
/// is kept as a change too, so it reaches the other devices instead of being undone by their copies.</item>
/// <item>The saved stations are listed in the order they were saved, the same on every device.</item>
/// <item>A device's first ledger is its list as it is, in its order (0, 1, 2...). These changes are older
/// than any real change.</item>
/// <item>A ledger belongs to one account. When another account signs in, it starts with an empty ledger.
/// The stations of the last account are stored in the cloud under that account, and are not passed on to
/// the next one.</item>
/// </list>
///
/// <para>Immutable: every change makes a new ledger. A change that changes nothing returns the same ledger,
/// which is how <see cref="RadioFavorites"/> knows whether it needs to write.</para>
/// </summary>
public sealed record FavoriteLedger(
    string? Account,
    long Cursor,
    IReadOnlyDictionary<string, FavoriteChange> Changes,
    IReadOnlyList<string> Unsent)
{
    public static FavoriteLedger Empty { get; } = new(null, 0, new Dictionary<string, FavoriteChange>(), []);

    /// <summary>A device's first ledger: its list as it is, oldest first, at its positions, with none of it
    /// marked as sent.</summary>
    public static FavoriteLedger Of(IReadOnlyList<RadioStation> list)
    {
        var changes = new Dictionary<string, FavoriteChange>();
        for (int at = 0; at < list.Count; at++)
            changes.TryAdd(Key(list[at].Uuid), new FavoriteChange(true, at, list[at]));
        return new FavoriteLedger(null, 0, changes, [.. changes.Keys]);
    }

    /// <summary>The saved stations, in the order they were saved.</summary>
    public IReadOnlyList<RadioStation> Saved =>
        [.. Changes.Where(c => c.Value.Saved)
                   .OrderBy(c => c.Value.ChangedAt)
                   .ThenBy(c => c.Key, StringComparer.Ordinal)
                   .Select(c => c.Value.Station)];

    public bool Holds(string uuid) => Changes.TryGetValue(Key(uuid), out var change) && change.Saved;

    /// <summary>The station saved or unsaved here at <paramref name="now"/>, or just after the change it
    /// replaces. That keeps the new change newer than the held one, whatever this computer's clock
    /// says.</summary>
    public FavoriteLedger Changed(RadioStation station, bool saved, long now)
    {
        var key = Key(station.Uuid);
        var at = Math.Max(now, (Changes.TryGetValue(key, out var held) ? held.ChangedAt : -1) + 1);
        var changes = new Dictionary<string, FavoriteChange>(Changes) { [key] = new(saved, at, station) };
        return this with { Changes = changes, Unsent = [.. Unsent.Where(k => k != key), key] };
    }

    /// <summary>A saved station whose details were refreshed (renamed or moved by the directory). It keeps
    /// its position and its change. This is not a change to the list, so nothing is sent.</summary>
    public FavoriteLedger Refreshed(RadioStation station)
    {
        var key = Key(station.Uuid);
        if (!Changes.TryGetValue(key, out var held) || !held.Saved || held.Station == station)
            return this;
        var changes = new Dictionary<string, FavoriteChange>(Changes) { [key] = held with { Station = station } };
        return this with { Changes = changes };
    }

    /// <summary>The ledger that <paramref name="userId"/> syncs. This is this ledger, claimed for the
    /// account, when nobody has synced it yet or the same account did. For any other account it is an empty
    /// ledger.</summary>
    public FavoriteLedger ForAccount(string userId) => Account switch
    {
        null => this with { Account = userId },
        _ when Account == userId => this,
        _ => Empty with { Account = userId },
    };

    /// <summary>Takes the rows listed by the cloud. Each row that is newer than the held change replaces
    /// it, and is no longer this device's to send. The cursor moves to the newest row that
    /// arrived.</summary>
    public FavoriteLedger Taking(IReadOnlyList<FavoriteRow> rows)
    {
        if (rows.Count == 0)
            return this;

        var changes = new Dictionary<string, FavoriteChange>(Changes);
        var waiting = new List<string>(Unsent);
        foreach (var row in rows)
        {
            if (row.Station is not { } station || string.IsNullOrWhiteSpace(row.Uuid)
                || string.IsNullOrWhiteSpace(station.StreamUrl))
                continue;
            var key = Key(row.Uuid);
            if (changes.TryGetValue(key, out var held) && held.ChangedAt >= row.ChangedAt)
                continue;
            changes[key] = new FavoriteChange(row.Saved, row.ChangedAt, station);
            waiting.Remove(key);
        }

        return this with
        {
            Changes = changes,
            Unsent = waiting,
            Cursor = Math.Max(Cursor, rows.Max(r => r.UpdatedAt)),
        };
    }

    /// <summary>What to send: the unsent changes, at most <paramref name="most"/>.</summary>
    public IReadOnlyDictionary<string, FavoriteChange> ToSend(int most) =>
        Unsent.Where(Changes.ContainsKey).Take(most).ToDictionary(k => k, k => Changes[k]);

    /// <summary>Marks <paramref name="sent"/> as accepted. Each one leaves the unsent list, unless it was
    /// changed again while it was on its way.</summary>
    public FavoriteLedger Sent(IReadOnlyDictionary<string, FavoriteChange> sent)
    {
        var waiting = Unsent.Where(k => !(sent.TryGetValue(k, out var out_)
            && Changes.TryGetValue(k, out var now) && now.ChangedAt == out_.ChangedAt)).ToList();
        return waiting.Count == Unsent.Count ? this : this with { Unsent = waiting };
    }

    private static string Key(string uuid) => uuid.ToLowerInvariant();
}
