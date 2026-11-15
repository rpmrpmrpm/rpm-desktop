using System.Globalization;
using System.Net;
using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>
/// Reads an iBroadcast library: every track with its play count, thumbs up (a favourite) and upload date,
/// the person's playlists, and the tags (each becomes a playlist in the folder <paramref name="tagsFolder"/>).
/// Files come from the streaming server: the original for a Premium account, a 128 kbps MP3 for a free
/// one. Every call only reads; nothing sent here counts as a play.
/// </summary>
/// <param name="http">For the library and streaming servers. Not disposed here.</param>
/// <param name="tagsFolder">The name of the playlist folder the tags go into, in the person's language.</param>
public sealed partial class IBroadcastServer(HttpClient http, IBroadcastSession session, IBroadcastAccount account, string tagsFolder) : IMediaServer
{
    private static readonly Uri Streaming = new("https://streaming.ibroadcast.com");

    /// <summary>A 128 kbps MP3 is 16,000 bytes a second, the size of a free account's copy.</summary>
    private const int FreeBytesPerSecond = 16_000;

    private const int MostBusyAnswers = 3;

    public SourceKind Kind => SourceKind.IBroadcast;

    /// <summary>The wait before asking again after the streaming server answered 429. Replaced in tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct)
    {
        var library = Child(await IBroadcastApi.PostAsync(http, session, IBroadcastApi.Library, "library", ct), "library");
        var tracks = Table.Of(Child(library, "tracks"));
        var albums = Table.Of(Child(library, "albums"));
        var artists = Table.Of(Child(library, "artists"));
        var inTrash = Table.Of(Child(library, "trash")).Rows.SelectMany(r => r.Ids("tracks")).ToHashSet();

        string ArtistName(long id) => artists.Find(id) is { } artist ? artist.Text("name") : "";

        var read = new List<SourceTrack>();
        var skipped = new List<SourceSkip>();
        var names = new Dictionary<long, string>();
        foreach (var row in tracks.Rows)
        {
            var album = albums.Find(row.Number("album_id"));
            var title = row.Text("title");
            var artist = ArtistName(row.Number("artist_id"));
            var what = artist.Length > 0 ? $"{artist} - {title}" : title;
            names[row.Id] = what;

            var file = row.Text("file");
            var extension = account.Premium ? ExtensionOf(null, ContainerOf(row.Text("type"))) : ".mp3";
            if (row.Flag("trashed") || album?.Flag("trashed") == true || inTrash.Contains(row.Id))
                skipped.Add(new SourceSkip(what, SkipReason.InTrash));
            else if (!file.StartsWith('/'))
                skipped.Add(new SourceSkip(what, SkipReason.NotAFile));
            else if (extension.Length == 0)
                skipped.Add(new SourceSkip(what, SkipReason.NotMusic));
            else
                read.Add(TrackOf(row, album, title, artist, album is null ? "" : ArtistName(album.Number("artist_id")), file, extension));
        }
        found.Report(read.Count);

        var ids = read.Select(t => t.SourceId).ToHashSet(StringComparer.Ordinal);
        SourcePlaylist ListOf(string sourceId, string name, IReadOnlyList<string> folder, Row row)
        {
            var entries = row.Ids("tracks").ToList();
            return new SourcePlaylist(sourceId, name, folder, Smart: false,
                [.. entries.Select(Id).Where(ids.Contains)],
                [.. entries.Where(e => !ids.Contains(Id(e))).Select(e => names.GetValueOrDefault(e) ?? Id(e))]);
        }

        var playlists = new List<SourcePlaylist>();
        // A list iBroadcast fills itself ("Most Recent Uploads", "Thumbs Up") has a type, or is marked as
        // made by the system. Only the lists the person made come along.
        foreach (var row in Table.Of(Child(library, "playlists")).Rows.Where(r => !r.Flag("system_created") && r.Text("type").Length == 0))
            playlists.Add(ListOf(Id(row.Id), row.Text("name"), [], row));
        foreach (var row in Table.Of(Child(library, "tags")).Rows.Where(r => !r.Flag("archived")))
            playlists.Add(ListOf("tag:" + Id(row.Id), row.Text("name"), [tagsFolder], row));

        return new SourceLibrary(SourceKind.IBroadcast, account.UserId, account.UserName, read, playlists, skipped);
    }

    public async Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct)
    {
        var split = file.Key.IndexOf('|');
        if (split <= 0)
            throw new ArgumentException("Not an iBroadcast file key.", nameof(file));
        var (fileId, path) = (file.Key[..split], file.Key[(split + 1)..]);

        var token = await session.TokenAsync(ct);
        var renewed = false;
        for (var busy = 1; ; )
        {
            var url = new Uri(Streaming, path + "?Signature=" + Uri.EscapeDataString(token) + "&file_id=" + Uri.EscapeDataString(fileId)
                + "&user_id=" + Uri.EscapeDataString(account.UserId) + "&platform=rpm&version=" + Uri.EscapeDataString(IBroadcastApi.Version));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                if (renewed)
                    throw new MediaServerSignInExpired();
                token = await session.RenewAsync(token, ct);
                renewed = true;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                response.Dispose();
                if (busy++ >= MostBusyAnswers)
                    throw new HttpRequestException("The iBroadcast streaming server is busy.", null, HttpStatusCode.TooManyRequests);
                await Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 300)), ct);
                continue;
            }
            return await BodyAsync(response, ct);
        }
    }

    private SourceTrack TrackOf(Row row, Row? album, string title, string artist, string albumArtist, string file, string extension)
    {
        var id = Id(row.Id);
        var seconds = (double)row.Number("length");
        var size = account.Premium ? row.Number("size") : (long)Math.Round(seconds * FreeBytesPerSecond);
        // The first part of the path is the copy asked for: "orig", or a bitrate such as "128".
        var stream = Quality().IsMatch(file) ? Quality().Replace(file, account.Premium ? "/orig/" : "/128/", 1) : file;
        return new SourceTrack(
            SourceId: id,
            // The size is not known: the streamed file can differ from the size the library gives, so a
            // length check would refuse a good download.
            File: new RemoteFile(id + "|" + stream, extension, 0),
            Title: title,
            Artist: artist,
            Album: album?.Text("name") ?? "",
            AlbumArtist: albumArtist,
            TrackNumber: (int)row.Number("track"),
            DiscNumber: (int)(album?.Number("disc") ?? 0),
            Year: (int)(row.Number("year") is > 0 and var year ? year : album?.Number("year") ?? 0),
            Genre: row.Text("genre"),
            DurationSeconds: seconds,
            SizeBytes: size,
            Rating: 0,
            Favorite: row.Number("rating") == 5,
            Plays: (int)row.Number("plays"),
            LastPlayed: null,
            Added: Uploaded(row.Text("uploaded_on"), row.Text("uploaded_time")));
    }

    [System.Text.RegularExpressions.GeneratedRegex("^/(?:96|128|192|256|320|orig)/")]
    private static partial System.Text.RegularExpressions.Regex Quality();

    /// <summary>The upload date and time. iBroadcast does not say the zone; they are read as UTC.</summary>
    private static DateTimeOffset? Uploaded(string day, string time) =>
        DateTimeOffset.TryParseExact((day + " " + (time.Length > 0 ? time : "00:00:00")).Trim(), "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>The container name <see cref="ExtensionOf"/> reads, for a media type such as "audio/x-m4a".</summary>
    internal static string ContainerOf(string mediaType)
    {
        var sub = mediaType.Split('/') is [_, var s] ? s.Trim().ToLowerInvariant() : "";
        return sub switch
        {
            "flac" or "x-flac" => "flac",
            "mpeg" or "mp3" or "mpeg3" or "x-mpeg3" => "mp3",
            "mp4" or "x-m4a" or "m4a" => "m4a",
            "ogg" or "vorbis" => "ogg",
            "x-ms-wma" => "wma",
            "wav" or "x-wav" or "wave" => "wav",
            "aiff" or "x-aiff" => "aiff",
            "ape" or "x-ape" => "ape",
            "x-wavpack" => "wv",
            _ => sub,
        };
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One table of the library: rows keyed by id. A row is a list read through the table's <c>map</c>
    /// (field name to column), or an object read by field name. Rows are taken in the order of their ids,
    /// so the desktop and the web app list a library the same way.
    /// </summary>
    private sealed class Table
    {
        private readonly Dictionary<long, Row> _rows = [];

        public IReadOnlyList<Row> Rows { get; private init; } = [];

        public Row? Find(long id) => _rows.GetValueOrDefault(id);

        public static Table Of(JsonElement table)
        {
            if (table.ValueKind != JsonValueKind.Object)
                return new Table();
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            if (Child(table, "map") is { ValueKind: JsonValueKind.Object } map)
                foreach (var field in map.EnumerateObject())
                    if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var column))
                        columns[field.Name] = column;

            var rows = new List<Row>();
            foreach (var entry in table.EnumerateObject())
                if (long.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    && entry.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    rows.Add(new Row(id, entry.Value, columns));
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            var result = new Table { Rows = rows };
            foreach (var row in rows)
                result._rows[row.Id] = row;
            return result;
        }
    }

    private sealed class Row(long id, JsonElement row, Dictionary<string, int> columns)
    {
        public long Id => id;

        private JsonElement Field(string name)
        {
            if (row.ValueKind == JsonValueKind.Object)
                return row.TryGetProperty(name, out var value) ? value : default;
            return columns.TryGetValue(name, out var column) && column < row.GetArrayLength() ? row[column] : default;
        }

        public string Text(string name) => Field(name) is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";

        public long Number(string name) => Field(name) switch
        {
            { ValueKind: JsonValueKind.Number } n => n.TryGetInt64(out var whole) ? whole : (long)n.GetDouble(),
            { ValueKind: JsonValueKind.String } s => long.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0,
        };

        public bool Flag(string name) => Field(name) is { ValueKind: JsonValueKind.True } || Number(name) == 1 && Field(name).ValueKind == JsonValueKind.Number;

        public IEnumerable<long> Ids(string name) => Field(name) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _)).Select(e => e.GetInt64())
            : [];
    }
}
