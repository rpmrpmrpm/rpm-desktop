using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>A Plex sign-in code. The person opens <paramref name="AuthPage"/> in a browser and signs in
/// to Plex there; rpm never sees the Plex password.</summary>
public sealed record PlexPin(long Id, string Code, Uri AuthPage);

/// <summary>A Plex Media Server the account can reach. <paramref name="AccessToken"/> is the token for
/// this server (a secret). <paramref name="Connections"/> are its addresses, best first.</summary>
public sealed record PlexServerChoice(string Name, string MachineId, string AccessToken, IReadOnlyList<Uri> Connections);

/// <summary>
/// Signing in to Plex with a PIN: rpm asks plex.tv for a code, the person approves it on plex.tv in
/// the browser, and rpm then receives the account token. The account token lists the servers; each
/// server has its own token.
/// </summary>
public static class PlexSignIn
{
    private static readonly Uri PlexTv = new("https://plex.tv/");

    public static async Task<PlexPin> StartAsync(HttpClient http, string clientId, CancellationToken ct)
    {
        using var request = Plex.Request(HttpMethod.Post, PlexTv, "api/v2/pins?strong=true", clientId, token: null);
        var pin = await JsonAsync(http, request, ct);
        var id = Num(pin, "id");
        var code = Str(pin, "code");
        var page = new Uri("https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(clientId)
            + "&code=" + Uri.EscapeDataString(code) + "&context%5Bdevice%5D%5Bproduct%5D=rpm");
        return new PlexPin(id, code, page);
    }

    /// <summary>The account token once the person has approved the code, or null while they have not.</summary>
    public static async Task<string?> FinishAsync(HttpClient http, string clientId, PlexPin pin, CancellationToken ct)
    {
        using var request = Plex.Request(HttpMethod.Get, PlexTv,
            $"api/v2/pins/{pin.Id}?code={Uri.EscapeDataString(pin.Code)}", clientId, token: null);
        var token = Str(await JsonAsync(http, request, ct), "authToken");
        return token.Length > 0 ? token : null;
    }

    /// <summary>The servers the account can use. Connections are ordered: https before http (a web page
    /// can only use https), direct before relayed (a relay is slow), and on the home network before the
    /// internet.</summary>
    public static async Task<IReadOnlyList<PlexServerChoice>> ServersAsync(
        HttpClient http, string clientId, string accountToken, CancellationToken ct)
    {
        using var request = Plex.Request(HttpMethod.Get, PlexTv, "api/v2/resources?includeHttps=1&includeRelay=1", clientId, accountToken);
        var resources = await JsonAsync(http, request, ct);
        if (resources.ValueKind != JsonValueKind.Array)
            return [];

        return [.. resources.EnumerateArray()
            .Where(r => Str(r, "provides").Split(',').Contains("server"))
            .Select(r => new PlexServerChoice(
                Str(r, "name"),
                Str(r, "clientIdentifier"),
                Str(r, "accessToken"),
                [.. Items(r, "connections")
                    .OrderBy(c => Str(c, "protocol") == "https" ? 0 : 1)
                    .ThenBy(c => Flag(c, "relay") ? 1 : 0)
                    .ThenBy(c => Flag(c, "local") ? 0 : 1)
                    .Select(c => Uri.TryCreate(Str(c, "uri"), UriKind.Absolute, out var uri) ? uri : null)
                    .OfType<Uri>()]))];
    }

    /// <summary>The first of the server's connections that answers within a few seconds, in the order
    /// <see cref="ServersAsync"/> gives them, or null when none does.</summary>
    public static async Task<Uri?> PickConnectionAsync(HttpClient http, PlexServerChoice server, CancellationToken ct)
    {
        foreach (var connection in server.Connections)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var request = Plex.Request(HttpMethod.Get, connection, "identity", null, server.AccessToken);
                var identity = await JsonAsync(http, request, wait.Token);
                if (Str(Child(identity, "MediaContainer"), "machineIdentifier") == server.MachineId)
                    return connection;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                // Not reachable from here; try the next address.
            }
        }
        return null;
    }
}

/// <summary>
/// Reads the music on a Plex Media Server: every track in every music section, with the account's
/// rating (Plex rates out of 10, the same scale as rpm), play count, last played and date added, and
/// the account's audio playlists. Plex has no favourites.
/// </summary>
public sealed class PlexServer(HttpClient http, PlexServerChoice server, Uri connection) : IMediaServer
{
    private const int PageSize = 500;

    public SourceKind Kind => SourceKind.Plex;

    public async Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct)
    {
        JsonElement sections;
        using (var request = Plex.Request(HttpMethod.Get, connection, "library/sections", null, server.AccessToken))
            sections = Child(await JsonAsync(http, request, ct), "MediaContainer");

        var tracks = new List<SourceTrack>();
        var skipped = new List<SourceSkip>();
        foreach (var section in Items(sections, "Directory").Where(d => Str(d, "type") == "artist"))
        {
            await foreach (var item in PagesAsync($"library/sections/{Uri.EscapeDataString(Str(section, "key"))}/all?type=10", ct))
            {
                switch (TrackOf(item))
                {
                    case SourceTrack track:
                        tracks.Add(track);
                        break;
                    case SourceSkip skip:
                        skipped.Add(skip);
                        break;
                }
                if (tracks.Count % PageSize == 0)
                    found.Report(tracks.Count);
            }
        }
        found.Report(tracks.Count);

        var known = tracks.Select(t => t.SourceId).ToHashSet(StringComparer.Ordinal);
        var playlists = new List<SourcePlaylist>();
        JsonElement lists;
        using (var request = Plex.Request(HttpMethod.Get, connection, "playlists?playlistType=audio", null, server.AccessToken))
            lists = Child(await JsonAsync(http, request, ct), "MediaContainer");
        foreach (var playlist in Items(lists, "Metadata"))
        {
            var key = Str(playlist, "ratingKey");
            var trackIds = new List<string>();
            var unresolved = new List<string>();
            await foreach (var entry in PagesAsync($"playlists/{Uri.EscapeDataString(key)}/items", ct))
            {
                var entryKey = Str(entry, "ratingKey");
                (known.Contains(entryKey) ? trackIds : unresolved).Add(entryKey);
            }
            playlists.Add(new SourcePlaylist(key, Str(playlist, "title"), [], Flag(playlist, "smart"), trackIds, unresolved));
        }

        return new SourceLibrary(SourceKind.Plex, server.MachineId, server.Name, tracks, playlists, skipped);
    }

    public async Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct)
    {
        // The part key is a path on the server ("/library/parts/9001/1500000000/file.flac").
        var path = file.Key.TrimStart('/') + (file.Key.Contains('?') ? "&" : "?") + "download=1";
        using var request = Plex.Request(HttpMethod.Get, connection, path, null, server.AccessToken);
        return await StreamAsync(http, request, ct);
    }

    /// <summary>Every item of a paged list. Plex says how many there are (<c>totalSize</c>).</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0;
        while (true)
        {
            var separator = path.Contains('?') ? '&' : '?';
            using var request = Plex.Request(HttpMethod.Get, connection,
                $"{path}{separator}X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}", null, server.AccessToken);
            var page = Child(await JsonAsync(http, request, ct), "MediaContainer");
            var items = Items(page, "Metadata").ToList();
            foreach (var item in items)
                yield return item;
            start += items.Count;
            var total = Num(page, "totalSize");
            if (items.Count == 0 || total > 0 && start >= total || total == 0 && items.Count < PageSize)
                yield break;
        }
    }

    /// <summary>The track, or why it is not imported: no file part (a stream from an online service), or
    /// a file that is not audio rpm reads.</summary>
    private static object TrackOf(JsonElement item)
    {
        var media = Items(item, "Media").FirstOrDefault();
        var part = Items(media, "Part").FirstOrDefault();
        var key = Str(part, "key");
        var albumArtist = Str(item, "grandparentTitle");
        var artist = Str(item, "originalTitle") is { Length: > 0 } own ? own : albumArtist;
        var what = artist.Length > 0 ? $"{artist} - {Str(item, "title")}" : Str(item, "title");
        if (key.Length == 0)
            return new SourceSkip(what, SkipReason.NotAFile);

        var extension = ExtensionOf(Str(part, "file"), Str(part, "container") is { Length: > 0 } c ? c : Str(media, "container"));
        if (extension.Length == 0)
            return new SourceSkip(what, SkipReason.NotMusic);

        var size = Num(part, "size");
        var lastViewed = Num(item, "lastViewedAt");
        var added = Num(item, "addedAt");

        return new SourceTrack(
            SourceId: Str(item, "ratingKey"),
            File: new RemoteFile(key, extension, size),
            Title: Str(item, "title"),
            // originalTitle is the track's own artist when it differs from the album artist.
            Artist: artist,
            Album: Str(item, "parentTitle"),
            AlbumArtist: albumArtist,
            TrackNumber: (int)Num(item, "index"),
            DiscNumber: (int)Num(item, "parentIndex"),
            DurationSeconds: Num(item, "duration") / 1000.0,
            SizeBytes: size,
            Rating: Math.Clamp((int)Math.Round(Real(item, "userRating")), 0, 10),
            Favorite: false,
            Plays: (int)Num(item, "viewCount"),
            LastPlayed: lastViewed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastViewed) : null,
            Added: added > 0 ? DateTimeOffset.FromUnixTimeSeconds(added) : null);
    }
}

/// <summary>How every Plex request is made.</summary>
internal static class Plex
{
    public static HttpRequestMessage Request(HttpMethod method, Uri host, string path, string? clientId, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(Base(host), path));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "rpm");
        if (clientId is not null)
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientId);
        if (token is not null)
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return request;
    }
}
