using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Radio;

/// <summary>
/// The user's saved stations, in a JSON file of their own.
///
/// <para>The favourites are not in the library mirror. The mirror lives inside the music folder and belongs
/// to that folder's tracks, and a saved station is neither. If the app is pointed at another library, the
/// favourites stay, which is what a listener expects of radio presets.</para>
///
/// <para>The whole station is stored, not just its id. The directory is run by volunteers, and stations are
/// removed from it regularly. A favourite that kept only an id would stop working as soon as its entry was
/// removed. Keeping the URL means a preset plays for as long as the station broadcasts, with or without the
/// directory.</para>
///
/// <para>The stations are kept in step with the listener's other devices
/// (<c>CloudSyncService.StationFavorites</c>). So they are held as a <see cref="FavoriteLedger"/>, which
/// records when each save and unsave was made. The file keeps the list in the form it always had, so an
/// older build can still read it, and the ledger is stored beside it. A file with no ledger yet, which is
/// every file written before sync existed, makes its first ledger from the list. The list is in the order
/// in which the stations were saved. There is no reordering, because every device has to agree on the
/// order.</para>
/// </summary>
public sealed partial class RadioFavorites
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private FavoriteLedger _ledger = FavoriteLedger.Empty;

    /// <summary>False when the presets file is there and could not be read. See <see
    /// cref="RPM.Core.Files.StoreFile"/>.</summary>
    private bool _mayWrite = true;

    /// <param name="time">The clock that stamps each save or unsave. The stamp decides which of two
    /// devices' changes to one station stands.</param>
    public RadioFavorites(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        _path = path;
        _time = time;
        Load();
    }

    /// <summary>Raised after any change, so a pane showing the presets can redraw.</summary>
    public event Action? Changed;

    /// <summary>The saved stations, in the order they were saved.</summary>
    public IReadOnlyList<RadioStation> Stations
    {
        get { lock (_gate) return _ledger.Saved; }
    }

    public bool Contains(string uuid)
    {
        lock (_gate) return _ledger.Holds(uuid);
    }

    /// <summary>Saves a station, or refreshes one that is already saved with the directory's current
    /// details. A station that was renamed or moved keeps its place in the list.</summary>
    public void Add(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        Settle(l => l.Holds(station.Uuid) ? l.Refreshed(station) : l.Changed(station, saved: true, Now()));
    }

    public void Remove(string uuid)
    {
        Settle(l => l.Changes.TryGetValue(uuid.ToLowerInvariant(), out var held) && held.Saved
            ? l.Changed(held.Station, saved: false, Now())
            : l);
    }

    /// <summary>Saves or unsaves the station, and returns whether it is now saved. This is what the star
    /// button does.</summary>
    public bool Toggle(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (Contains(station.Uuid))
        {
            Remove(station.Uuid);
            return false;
        }
        Add(station);
        return true;
    }

    // ── the sync's view ──

    /// <summary>The server timestamp that everything up to now has been taken in.</summary>
    public long Cursor
    {
        get { lock (_gate) return _ledger.Cursor; }
    }

    /// <summary>Claims the ledger for the signed-in account. The ledger of another account is started
    /// afresh. See <see cref="FavoriteLedger.ForAccount"/>.</summary>
    public void ForAccount(string userId) => Settle(l => l.ForAccount(userId));

    /// <summary>Takes in the rows that the cloud listed. See <see cref="FavoriteLedger.Taking"/>.</summary>
    public void Take(IReadOnlyList<FavoriteRow> rows) => Settle(l => l.Taking(rows));

    /// <summary>The changes that the cloud has not accepted yet, at most <paramref name="most"/>.</summary>
    public IReadOnlyDictionary<string, FavoriteChange> ToSend(int most)
    {
        lock (_gate) return _ledger.ToSend(most);
    }

    /// <summary>The changes that were sent and accepted. A change that was made again since it was sent
    /// stays unsent.</summary>
    public void NoteSent(IReadOnlyDictionary<string, FavoriteChange> sent) => Settle(l => l.Sent(sent));

    /// <summary>
    /// The account this ledger synced with has been deleted. The stations stay. The ledger starts again
    /// from them, unclaimed and unsent, as on a computer that has never synced.
    /// </summary>
    public void ForgetAccount() => Settle(l => FavoriteLedger.Of(l.Saved));

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Applies <paramref name="change"/> under the lock, and writes and announces the result where
    /// it is a different ledger.</summary>
    private void Settle(Func<FavoriteLedger, FavoriteLedger> change)
    {
        lock (_gate)
        {
            var next = change(_ledger);
            if (ReferenceEquals(next, _ledger))
                return;
            _ledger = next;
            Save();
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        // A presets file that does not parse is moved aside, not written over. A file that cannot be read
        // this time is left alone, and nothing is written to it in this session. See StoreFile.
        var read = RPM.Core.Files.StoreFile.Load(
            _path, text => JsonSerializer.Deserialize(text, RadioFavoritesJson.Default.Model));
        _mayWrite = read.MayWrite;
        _ledger = read.Value?.Sync is { } sync
            ? sync with
            {
                Changes = sync.Changes.Where(c => Playable(c.Value.Station)).ToDictionary(c => c.Key, c => c.Value),
            }
            : FavoriteLedger.Of([.. (read.Value?.Stations ?? []).Where(Playable)]);
    }

    private static bool Playable(RadioStation s) =>
        !string.IsNullOrWhiteSpace(s.Uuid) && !string.IsNullOrWhiteSpace(s.StreamUrl);

    /// <summary>Written through a temporary file. The presets are small and are rewritten on every save. A
    /// half-written file after a crash would lose all of them.</summary>
    private void Save()
    {
        if (!_mayWrite)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Model(_ledger.Saved, _ledger), RadioFavoritesJson.Default.Model));
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* read-only or full disk: the presets still work for this session */ }
    }

    /// <param name="Stations">The list, which is what an older build reads.</param>
    /// <param name="Sync">The ledger, or null in a file written before sync.</param>
    private sealed record Model(IReadOnlyList<RadioStation> Stations, FavoriteLedger? Sync);

    /// <summary>The presets file's shape, and how it is written.</summary>
    /// <remarks>The options are set on the context and not on a JsonSerializerOptions instance. A published
    /// build is Native AOT, and the call sites use the generated metadata, which carries these settings
    /// with it.</remarks>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Model))]
    private sealed partial class RadioFavoritesJson : JsonSerializerContext;
}
