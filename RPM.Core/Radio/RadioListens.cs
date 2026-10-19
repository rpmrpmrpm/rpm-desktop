using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Radio;

/// <summary>One station in the listening log: the station as it was when it was last tuned, and how much of
/// it has been heard.</summary>
/// <param name="Station">The whole station, kept for the same reason that a favourite keeps the whole
/// station. The directory removes entries regularly, and a log row that held only a uuid would name
/// something that nothing can play or draw. It also carries the logo URL, which the home screen's card is
/// made from.</param>
/// <param name="HeardSeconds">Audio actually delivered, added up over every time it has been tuned. This is
/// what "most listened" is ordered by.</param>
/// <param name="Tunes">How many separate listens make up that total. This is not what the ordering uses. A
/// station left on all day is more listened to than one tuned in and dropped ten times. The count is kept
/// because it is the one number that tells those two apart.</param>
/// <param name="LastHeardUtcTicks">When the most recent listen ended. This is the tie-break, and it is the
/// only way that a station which is no longer played falls behind one that is.</param>
public sealed record RadioListen(
    RadioStation Station,
    double HeardSeconds,
    int Tunes,
    long LastHeardUtcTicks);

/// <summary>This log's own totals for one station, as they are sent to the store.</summary>
/// <param name="LastHeardAt">Unix seconds, which is what goes over the wire.</param>
public sealed record RadioListenRow(RadioStation Station, double HeardSeconds, int Tunes, long LastHeardAt);

/// <summary>Another device's totals for one station, as the store handed them over.</summary>
public sealed record ForeignRadioListen(string Device, RadioListenRow Row);

/// <summary>
/// How much each station has been listened to, kept in a JSON file of its own.
///
/// <para>Not in the library mirror, for the same reason as <see cref="RadioFavorites"/>. The mirror lives
/// inside the music folder and belongs to that folder's tracks, and a station is neither. If the app is
/// pointed at another library, the record of what was heard on the radio is still true.</para>
///
/// <para>It is kept beside the presets, not in them. A preset is a station that somebody chose. This is a
/// station that somebody played. The two often disagree. A station left on all morning is usually not
/// starred, and many starred stations have never been tuned in. Ordering the presets by play time would
/// make the star list rearrange itself while the listener reads it.</para>
///
/// <para><b>What limits the size.</b> <see cref="Kept"/> stations, ranked by seconds heard. A listener who
/// tries a hundred stations in one evening does not get a hundred rows. The file is trimmed on every write,
/// and the stations that fall off the end are the ones heard least. There is no age limit. A station played
/// for forty hours two years ago is still, truthfully, one of the most listened to.</para>
///
/// <para><b>Synced between the listener's devices.</b> This log's own totals and the other devices' totals
/// are kept apart, keyed by device. A station's total is their sum, which is worked out when it is read.
/// <see cref="Note"/> writes only this log's own rows, and <see cref="Apply"/> writes only the other
/// devices' rows. Keep it that way, or a listen is counted twice. The store on the server is
/// <c>RPM.Lambda.Api.Services.StationListenStore</c>.</para>
/// </summary>
public sealed partial class RadioListens
{
    /// <summary>How many stations the log keeps. This is far more than the home screen's card reads, so the
    /// order stays stable when a station near the bottom is played again. It is small enough that the file
    /// stays a file that somebody could open.</summary>
    public const int Kept = 200;

    /// <summary>How many of the other devices' rows are kept. Each device trims its own log to <see
    /// cref="Kept"/>, so this is a few devices' worth.</summary>
    public const int TheirsKept = Kept * 4;

    /// <summary>A listen shorter than this is not written down. Thirty seconds is the same value as <see
    /// cref="Playback.Listen.ShortestSeconds"/>, for the same reason: turning a dial through six stations
    /// is not six listens.</summary>
    public const double NoticedSeconds = 30;

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>This log's own listening, by station.</summary>
    private Dictionary<string, RadioListen> _heard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The other devices' listening, one row per station per device.</summary>
    private Dictionary<(string Uuid, string Device), RadioListen> _theirs = new(TheirKey.Comparer);

    /// <summary>What was last sent for each of this log's own stations: the seconds and the tunings, as
    /// they were SENT. A station whose totals differ from these is due to be sent.</summary>
    private Dictionary<string, (double Heard, int Tunes)> _sent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This log's name in the store. It is made the first time the log is written.</summary>
    private string _device = "";

    /// <summary>Whether <see cref="_device"/> is on the disk. Only then does the log sync. A name that was
    /// never saved would be a different name in the next session. Rows sent under this session's name would
    /// then come back as another device's rows, and be counted twice.</summary>
    private bool _deviceWritten;

    /// <summary>The newest store stamp taken in.</summary>
    private long _cursor;

    /// <summary>False when the log is there and could not be read. See <see
    /// cref="RPM.Core.Files.StoreFile"/>.</summary>
    private bool _mayWrite = true;

    public RadioListens(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load();
    }

    /// <summary>Raised after a listen is written, or after other devices' listening is taken in, so that
    /// anything drawn from the log knows it is out of date. It may be raised on a background
    /// thread.</summary>
    public event Action? Changed;

    /// <summary>This log's name in the store, or null where the log must not sync. That is when it could
    /// not be read this session, or when its name could not be written down.</summary>
    public string? Device
    {
        get
        {
            lock (_gate)
                return _mayWrite && _deviceWritten && _device.Length > 0 ? _device : null;
        }
    }

    /// <summary>The newest store stamp this log has taken in. Zero asks the store for everything.</summary>
    public long Cursor
    {
        get
        {
            lock (_gate)
                return _cursor;
        }
    }

    /// <summary>
    /// Adds a finished listen to a station's total.
    /// </summary>
    /// <param name="heardSeconds">Audio actually delivered, not the time the station was tuned. A stream
    /// that never connected delivers nothing. A station that was reconnecting for five minutes must not
    /// rank above one that played for four.</param>
    /// <returns>False where the listen was too short to record (see <see cref="NoticedSeconds"/>). In that
    /// case nothing is written.</returns>
    public bool Note(RadioStation station, double heardSeconds)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (heardSeconds < NoticedSeconds || string.IsNullOrWhiteSpace(station.Uuid))
            return false;

        lock (_gate)
        {
            var was = _heard.GetValueOrDefault(station.Uuid);

            // The station as it was tuned this time, not as it was first filed. A station that was renamed
            // or moved keeps its hours, and gets its current name, URL and logo.
            _heard[station.Uuid] = new RadioListen(
                station,
                (was?.HeardSeconds ?? 0) + heardSeconds,
                (was?.Tunes ?? 0) + 1,
                DateTime.UtcNow.Ticks);

            Trim();
            Save();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The stations heard most, most first, counting every device's listening.
    /// </summary>
    /// <param name="count">How many at most. Zero or less gives nothing.</param>
    public IReadOnlyList<RadioListen> Top(int count)
    {
        if (count <= 0)
            return [];

        lock (_gate)
            return
            [
                .. Totals()
                    .OrderByDescending(l => l.HeardSeconds)
                    .ThenByDescending(l => l.LastHeardUtcTicks)
                    .Take(count),
            ];
    }

    /// <summary>What has been heard of one station on every device, or null if it has never been played
    /// long enough to count.</summary>
    public RadioListen? For(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return null;

        lock (_gate)
            return Totals().FirstOrDefault(
                l => string.Equals(l.Station.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
    }

    // ── the sync ──

    /// <summary>
    /// Takes in other devices' rows.
    /// </summary>
    /// <remarks>
    /// This log's own rows are dropped. They are already counted here, and taking one in would count it
    /// twice. Each row replaces the one it names, because that row is the device's total.
    /// </remarks>
    /// <returns>How many rows were taken in.</returns>
    public int Apply(IReadOnlyList<ForeignRadioListen> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var taken = 0;
        lock (_gate)
        {
            foreach (var (device, row) in rows)
            {
                if (string.IsNullOrWhiteSpace(device)
                    || string.Equals(device, _device, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(row.Station.Uuid)
                    || string.IsNullOrWhiteSpace(row.Station.StreamUrl))
                    continue;

                var next = new RadioListen(
                    row.Station,
                    Math.Max(0, row.HeardSeconds),
                    Math.Max(0, row.Tunes),
                    DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, row.LastHeardAt)).UtcTicks);

                // **A row already held as it is is not taken again.** The listing returns rows that changed
                // at or after the cursor. It is inclusive on purpose (see StationListenStore.ListAsync), so
                // the newest row comes back on every pass. If each returned row were counted, every pass
                // would log rows as arriving, write the file and redraw the radio list for nothing. The
                // totals are compared, not the whole record. The station holds lists, and a record compares
                // those by reference.
                if (_theirs.TryGetValue((row.Station.Uuid, device), out var held)
                    && held.HeardSeconds == next.HeardSeconds
                    && held.Tunes == next.Tunes
                    && held.LastHeardUtcTicks == next.LastHeardUtcTicks)
                    continue;

                _theirs[(row.Station.Uuid, device)] = next;
                taken++;
            }

            if (taken > 0)
            {
                TrimTheirs();
                Save();
            }
        }

        if (taken > 0)
            Changed?.Invoke();
        return taken;
    }

    /// <summary>Moves the cursor on to the newest stamp that ARRIVED, whether or not it was taken in. This
    /// stops a later pass from asking again for this log's own rows.</summary>
    public void NoteCursor(long stamp)
    {
        lock (_gate)
        {
            if (stamp <= _cursor)
                return;
            _cursor = stamp;
            Save();
        }
    }

    /// <summary>This log's own stations whose totals have changed since they were last sent, most recently
    /// heard first.</summary>
    public IReadOnlyList<RadioListenRow> Unsent(int limit)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
            return
            [
                .. _heard.Values
                    .Where(l => !_sent.TryGetValue(l.Station.Uuid, out var sent)
                                || sent.Heard != l.HeardSeconds
                                || sent.Tunes != l.Tunes)
                    .OrderByDescending(l => l.LastHeardUtcTicks)
                    .Take(limit)
                    .Select(l => new RadioListenRow(
                        l.Station, l.HeardSeconds, l.Tunes,
                        new DateTimeOffset(l.LastHeardUtcTicks, TimeSpan.Zero).ToUnixTimeSeconds())),
            ];
    }

    /// <summary>
    /// Records these rows as agreed with the store.
    /// </summary>
    /// <remarks>
    /// The values are the ones that were SENT, carried on the rows that the caller was given. They are not
    /// the log's current totals. A listen noted between <see cref="Unsent"/> and this call is still unsent,
    /// and goes in the next pass.
    /// </remarks>
    public void NoteSent(IReadOnlyList<RadioListenRow> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        lock (_gate)
        {
            foreach (var row in sent)
                _sent[row.Station.Uuid] = (row.HeardSeconds, row.Tunes);
            Save();
        }
    }

    /// <summary>
    /// The account this log synced with has been deleted. What was sent to it, and how far its listing was
    /// read, are forgotten. So a later account is sent everything again.
    /// </summary>
    /// <remarks>
    /// The listening itself stays: this log's own rows, and what the other devices reported. It is what has
    /// been heard on this computer, and the stations card is drawn from it. The device name stays too. It
    /// is a name for this log and nothing more.
    /// </remarks>
    public void ForgetAccount()
    {
        lock (_gate)
        {
            _sent.Clear();
            _cursor = 0;
            Save();
        }
    }

    /// <summary>Every station's total over this log's own rows and every other device's. Held under <see
    /// cref="_gate"/>.</summary>
    private IEnumerable<RadioListen> Totals()
    {
        var rows = _heard.Values.Concat(_theirs.Values);

        return rows
            .GroupBy(l => l.Station.Uuid, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // The station as it was most recently tuned on any device, for the name and the logo.
                var newest = g.OrderByDescending(l => l.LastHeardUtcTicks).First();
                return new RadioListen(
                    newest.Station,
                    g.Sum(l => l.HeardSeconds),
                    g.Sum(l => l.Tunes),
                    newest.LastHeardUtcTicks);
            });
    }

    /// <summary>Drops everything beyond <see cref="Kept"/>, least heard first. Held under <see
    /// cref="_gate"/>.</summary>
    private void Trim()
    {
        if (_heard.Count <= Kept)
            return;

        _heard = _heard
            .OrderByDescending(e => e.Value.HeardSeconds)
            .ThenByDescending(e => e.Value.LastHeardUtcTicks)
            .Take(Kept)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

        // A station that is no longer kept has nothing left to send, and its sent mark would only add to
        // the file.
        foreach (var uuid in _sent.Keys.Where(k => !_heard.ContainsKey(k)).ToList())
            _sent.Remove(uuid);
    }

    /// <summary>Drops the other devices' rows beyond <see cref="TheirsKept"/>, least heard first. Held
    /// under <see cref="_gate"/>.</summary>
    private void TrimTheirs()
    {
        if (_theirs.Count <= TheirsKept)
            return;

        _theirs = _theirs
            .OrderByDescending(e => e.Value.HeardSeconds)
            .ThenByDescending(e => e.Value.LastHeardUtcTicks)
            .Take(TheirsKept)
            .ToDictionary(e => e.Key, e => e.Value, TheirKey.Comparer);
    }

    private void Load()
    {
        // Nothing in the log can be rebuilt from anywhere else. So a log that does not parse is moved
        // aside, not written over. A log that cannot be read this time is left alone, and is not written in
        // this session. See StoreFile.
        var read = RPM.Core.Files.StoreFile.Load(
            _path, text => JsonSerializer.Deserialize(text, RadioListensJson.Default.Model));
        _mayWrite = read.MayWrite;

        var model = read.Value;
        _heard = (model?.Heard ?? [])
            .Where(Playable)
            .GroupBy(l => l.Station.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _theirs = new Dictionary<(string, string), RadioListen>(TheirKey.Comparer);
        foreach (var row in model?.Theirs ?? [])
            if (!string.IsNullOrWhiteSpace(row.Device) && row.Listen is { } listen && Playable(listen))
                _theirs[(listen.Station.Uuid, row.Device)] = listen;

        _sent = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model?.Sent ?? [])
            if (!string.IsNullOrWhiteSpace(row.Uuid))
                _sent[row.Uuid] = (row.HeardSeconds, row.Tunes);

        _cursor = model?.Cursor ?? 0;
        _device = model?.Device ?? "";
        _deviceWritten = _device.Length > 0;

        // A log that has never had a name gets one now. It is written straight away, so the name is on the
        // disk before anything is sent under it.
        if (_mayWrite && _device.Length == 0)
        {
            _device = Guid.NewGuid().ToString("N");
            _deviceWritten = Save();
        }
    }

    private static bool Playable(RadioListen l) =>
        l.Station is { } s && !string.IsNullOrWhiteSpace(s.Uuid) && !string.IsNullOrWhiteSpace(s.StreamUrl);

    /// <summary>Written through a temporary file, like the presets beside it. The whole log is rewritten on
    /// every listen. A half-written file after a crash would lose all of it.</summary>
    /// <returns>Whether the log is now on the disk.</returns>
    private bool Save()
    {
        if (!_mayWrite)
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            var model = new Model(
                [.. _heard.Values],
                _device.Length > 0 ? _device : null,
                _cursor,
                [.. _sent.Select(e => new SentRow(e.Key, e.Value.Heard, e.Value.Tunes))],
                [.. _theirs.Select(e => new TheirRow(e.Key.Device, e.Value))]);
            File.WriteAllText(temp, JsonSerializer.Serialize(model, RadioListensJson.Default.Model));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch
        {
            // Read-only or full disk. The session's listening still counts in memory.
            return false;
        }
    }

    /// <summary>The generated metadata that this file is read and written through.</summary>
    /// <remarks>A source-generated context is used, not a JsonSerializerOptions instance, because a
    /// published build is Native AOT, and reflection-based serialization is switched off there. Serializing
    /// through options would use reflection, which throws in that build. Load catches every exception, so
    /// the result would be a listening history that never loaded or saved, with no message
    /// anywhere.</remarks>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Model))]
    private sealed partial class RadioListensJson : JsonSerializerContext;

    /// <param name="Heard">This log's own listening. This was the only field before sync existed. So a log
    /// written by an older build reads as this log's own listening and nothing else.</param>
    private sealed record Model(
        IReadOnlyList<RadioListen> Heard,
        string? Device = null,
        long Cursor = 0,
        IReadOnlyList<SentRow>? Sent = null,
        IReadOnlyList<TheirRow>? Theirs = null);

    private sealed record SentRow(string Uuid, double HeardSeconds, int Tunes);

    private sealed record TheirRow(string Device, RadioListen? Listen);

    /// <summary>Station ids compare without case, as everywhere else in this log. Device names compare
    /// exactly.</summary>
    private sealed class TheirKey : IEqualityComparer<(string Uuid, string Device)>
    {
        public static readonly TheirKey Comparer = new();

        public bool Equals((string Uuid, string Device) x, (string Uuid, string Device) y) =>
            string.Equals(x.Uuid, y.Uuid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Device, y.Device, StringComparison.Ordinal);

        public int GetHashCode((string Uuid, string Device) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Uuid),
                StringComparer.Ordinal.GetHashCode(key.Device));
    }
}
