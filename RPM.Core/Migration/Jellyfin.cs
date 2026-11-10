using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>A signed-in Jellyfin user. <paramref name="Token"/> is the access token the server gave;
/// it is a secret.</summary>
public sealed record JellyfinSession(Uri Server, string ServerId, string UserId, string Token);

/// <summary>Signing in to Jellyfin: with a user name and password, or with Quick Connect (the person
/// approves a six-digit code in another Jellyfin app, which also works for accounts that sign in
/// through a single sign-on plugin).</summary>
public static class JellyfinSignIn
{
    /// <exception cref="MediaServerSignInFailed">The user name or password is wrong.</exception>
    public static async Task<JellyfinSession> WithPasswordAsync(
        HttpClient http, Uri server, string user, string password, CancellationToken ct)
    {
        using var request = Jellyfin.Request(HttpMethod.Post, server, "Users/AuthenticateByName", token: null);
        request.Content = JsonContent.Create(new { Username = user, Pw = password }, options: Jellyfin.AsWritten);
        return SessionOf(server, await SignInJsonAsync(http, request, ct));
    }

    /// <summary>Asks the server for a Quick Connect code. The person enters <c>Code</c> in another Jellyfin
    /// app; <c>Secret</c> is kept to ask whether it was approved.</summary>
    public static async Task<(string Code, string Secret)> StartQuickConnectAsync(HttpClient http, Uri server, CancellationToken ct)
    {
        using var request = Jellyfin.Request(HttpMethod.Post, server, "QuickConnect/Initiate", token: null);
        var answer = await SignInJsonAsync(http, request, ct);
        return (Str(answer, "Code"), Str(answer, "Secret"));
    }

    /// <summary>The session once the code is approved, or null while it is not.</summary>
    public static async Task<JellyfinSession?> FinishQuickConnectAsync(HttpClient http, Uri server, string secret, CancellationToken ct)
    {
        using (var state = Jellyfin.Request(HttpMethod.Get, server, $"QuickConnect/Connect?secret={Uri.EscapeDataString(secret)}", token: null))
        {
            if (!Flag(await SignInJsonAsync(http, state, ct), "Authenticated"))
                return null;
        }

        using var request = Jellyfin.Request(HttpMethod.Post, server, "Users/AuthenticateWithQuickConnect", token: null);
        request.Content = JsonContent.Create(new { Secret = secret }, options: Jellyfin.AsWritten);
        return SessionOf(server, await SignInJsonAsync(http, request, ct));
    }

    private static JellyfinSession SessionOf(Uri server, JsonElement answer) => new(
        Base(server),
        Str(answer, "ServerId") is { Length: > 0 } id ? id : Str(Child(answer, "User"), "ServerId"),
        Str(Child(answer, "User"), "Id"),
        Str(answer, "AccessToken"));

    private static async Task<JsonElement> SignInJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await JsonAsync(http, request, ct);
        }
        catch (MediaServerSignInExpired)
        {
            throw new MediaServerSignInFailed();
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new MediaServerSignInFailed();
        }
    }
}

/// <summary>
/// Reads a Jellyfin user's music: every audio item with the user's play count, last played and
/// favourite, and the user's playlists. Jellyfin has no star ratings, so the rating is always 0.
/// </summary>
public sealed class JellyfinServer(HttpClient http, JellyfinSession session) : IMediaServer
{
    private const int PageSize = 500;

    public SourceKind Kind => SourceKind.Jellyfin;

    public async Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct)
    {
        string name;
        using (var info = Jellyfin.Request(HttpMethod.Get, session.Server, "System/Info/Public", session.Token))
            name = Str(await JsonAsync(http, info, ct), "ServerName");

        var tracks = new List<SourceTrack>();
        var skipped = new List<SourceSkip>();
        await foreach (var item in PagesAsync(
            $"Users/{session.UserId}/Items?IncludeItemTypes=Audio&Recursive=true&Fields=DateCreated,MediaSources,Path&EnableUserData=true",
            count => found.Report(count), ct))
        {
            if (TrackOf(item) is { } track)
                tracks.Add(track);
            else
                skipped.Add(new SourceSkip(What(item), SkipReason.NotMusic));
        }

        var known = tracks.Select(t => t.SourceId).ToHashSet(StringComparer.Ordinal);
        var playlists = new List<SourcePlaylist>();
        await foreach (var playlist in PagesAsync($"Users/{session.UserId}/Items?IncludeItemTypes=Playlist&Recursive=true", null, ct))
        {
            var id = Str(playlist, "Id");
            var trackIds = new List<string>();
            var unresolved = new List<string>();
            await foreach (var entry in PagesAsync($"Playlists/{id}/Items?userId={session.UserId}", null, ct))
            {
                var entryId = Str(entry, "Id");
                (known.Contains(entryId) ? trackIds : unresolved).Add(entryId);
            }
            playlists.Add(new SourcePlaylist(id, Str(playlist, "Name"), [], false, trackIds, unresolved));
        }

        return new SourceLibrary(SourceKind.Jellyfin, session.ServerId, name, tracks, playlists, skipped);
    }

    public async Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(file.Key);
        try
        {
            using var request = Jellyfin.Request(HttpMethod.Get, session.Server, $"Items/{id}/Download", session.Token);
            return await StreamAsync(http, request, ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden)
        {
            // The user is not allowed to download. The static stream is the same original file,
            // without transcoding, and is allowed to every user who may play the track.
            using var request = Jellyfin.Request(HttpMethod.Get, session.Server, $"Audio/{id}/stream?static=true", session.Token);
            return await StreamAsync(http, request, ct);
        }
    }

    /// <summary>Every item of a paged list. Jellyfin says how many there are, so the pages are read
    /// until that many have arrived or a page comes back empty.</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(
        string path, Action<int>? progress, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0;
        while (true)
        {
            var separator = path.Contains('?') ? '&' : '?';
            using var request = Jellyfin.Request(HttpMethod.Get, session.Server, $"{path}{separator}StartIndex={start}&Limit={PageSize}", session.Token);
            var page = await JsonAsync(http, request, ct);
            var items = Items(page, "Items").ToList();
            foreach (var item in items)
                yield return item;
            start += items.Count;
            progress?.Invoke(start);
            if (items.Count == 0 || start >= Num(page, "TotalRecordCount"))
                yield break;
        }
    }

    private static string ArtistOf(JsonElement item) =>
        Items(item, "Artists").Select(a => a.GetString() ?? "").FirstOrDefault(a => a.Length > 0) ?? Str(item, "AlbumArtist");

    private static string What(JsonElement item) =>
        ArtistOf(item) is { Length: > 0 } artist ? $"{artist} - {Str(item, "Name")}" : Str(item, "Name");

    /// <summary>The track, or null when its file is not audio rpm reads.</summary>
    private static SourceTrack? TrackOf(JsonElement item)
    {
        var id = Str(item, "Id");
        var source = Items(item, "MediaSources").FirstOrDefault();
        var extension = ExtensionOf(
            Str(source, "Path") is { Length: > 0 } p ? p : Str(item, "Path"),
            Str(source, "Container") is { Length: > 0 } c ? c : Str(item, "Container"));
        if (extension.Length == 0)
            return null;
        var size = Num(source, "Size");
        var user = Child(item, "UserData");
        var albumArtist = Str(item, "AlbumArtist");
        var artist = ArtistOf(item);

        return new SourceTrack(
            SourceId: id,
            File: new RemoteFile(id, extension, size),
            Title: Str(item, "Name"),
            Artist: artist,
            Album: Str(item, "Album"),
            AlbumArtist: albumArtist,
            TrackNumber: (int)Num(item, "IndexNumber"),
            DiscNumber: (int)Num(item, "ParentIndexNumber"),
            DurationSeconds: Num(item, "RunTimeTicks") / 10_000_000.0,
            SizeBytes: size,
            Rating: 0,
            Favorite: Flag(user, "IsFavorite"),
            Plays: (int)Num(user, "PlayCount"),
            LastPlayed: Date(Str(user, "LastPlayedDate")),
            Added: Date(Str(item, "DateCreated")));
    }
}

/// <summary>How every Jellyfin request is made.</summary>
internal static class Jellyfin
{
    private static readonly string Version =
        typeof(Jellyfin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";

    /// <summary>Jellyfin lists each signed-in device. The id is stable for this computer, so signing in
    /// again replaces the old entry instead of adding one.</summary>
    private static readonly string DeviceId =
        "rpm-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16];

    public static HttpRequestMessage Request(HttpMethod method, Uri server, string path, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(Base(server), path));
        var authorization = $"MediaBrowser Client=\"rpm\", Device=\"{Quote(Environment.MachineName)}\", DeviceId=\"{DeviceId}\", Version=\"{Version}\"";
        if (token is not null)
            authorization += $", Token=\"{token}\"";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    /// <summary>Property names exactly as written ("Username", "Pw"), as the Jellyfin API documents
    /// them. The default for JSON content writes them in camel case.</summary>
    public static readonly JsonSerializerOptions AsWritten = new();

    private static string Quote(string value) => value.Replace("\"", "", StringComparison.Ordinal);
}
