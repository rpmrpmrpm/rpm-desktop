using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>A Subsonic server and the account to read it with. <paramref name="Password"/> is a secret:
/// it is never sent, only a salted hash of it (see <see cref="Subsonic"/>).</summary>
public sealed record SubsonicLogin(Uri Server, string User, string Password);

/// <summary>Checks a Subsonic login before an import starts.</summary>
public static class SubsonicCheck
{
    /// <exception cref="MediaServerSignInFailed">The server refused the user name or password, with the
    /// server's own message.</exception>
    public static async Task CheckAsync(HttpClient http, SubsonicLogin login, CancellationToken ct)
    {
        try
        {
            await Subsonic.CallAsync(http, login, "ping", "", ct);
        }
        catch (SubsonicError e) when (e.Code is 40 or 41)
        {
            throw new MediaServerSignInFailed(e.ServerMessage);
        }
        catch (MediaServerSignInExpired)
        {
            throw new MediaServerSignInFailed();
        }
    }
}

/// <summary>
/// Reads a Navidrome library, or any server that speaks the Subsonic API: every song with the user's
/// rating (1 to 5 stars, doubled to rpm's 0 to 10), star (a favourite), play count, last played and
/// date added, and the user's playlists. Navidrome's smart playlists come back as ordinary playlists
/// with their current songs; the API does not say which ones are smart.
/// </summary>
public sealed class SubsonicServer(HttpClient http, SubsonicLogin login) : IMediaServer
{
    /// <summary>Songs per page. The API has no total count, so the pages are read until one comes back
    /// shorter than this.</summary>
    internal int PageSize { get; init; } = 500;

    public SourceKind Kind => SourceKind.Navidrome;

    public async Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct)
    {
        var tracks = new List<SourceTrack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<SourceSkip>();
        var first = new FirstPage();
        for (var offset = 0; ; offset += PageSize)
        {
            // An empty query lists every song, in pages. Navidrome and most other servers accept it.
            var page = await Expired(() => Subsonic.CallAsync(http, login, "search3",
                $"query=&artistCount=0&albumCount=0&songCount={PageSize}&songOffset={offset}", ct));
            var list = Child(Child(page, "searchResult3"), "song");
            if (first.Again(list))
                break;
            var songs = Items(Child(page, "searchResult3"), "song").ToList();
            foreach (var song in songs)
            {
                if (TrackOf(song) is { } track)
                {
                    if (seen.Add(track.SourceId))
                        tracks.Add(track);
                }
                else
                    skipped.Add(new SourceSkip(Str(song, "artist") is { Length: > 0 } a ? $"{a} - {Str(song, "title")}" : Str(song, "title"), SkipReason.NotMusic));
            }
            found.Report(tracks.Count);
            if (songs.Count < PageSize)
                break;
        }

        var known = tracks.Select(t => t.SourceId).ToHashSet(StringComparer.Ordinal);
        var playlists = new List<SourcePlaylist>();
        var lists = await Expired(() => Subsonic.CallAsync(http, login, "getPlaylists", "", ct));
        foreach (var playlist in Items(Child(lists, "playlists"), "playlist"))
        {
            var id = Str(playlist, "id");
            var full = await Expired(() => Subsonic.CallAsync(http, login, "getPlaylist", "id=" + Uri.EscapeDataString(id), ct));
            var trackIds = new List<string>();
            var unresolved = new List<string>();
            foreach (var entry in Items(Child(full, "playlist"), "entry"))
            {
                var entryId = Str(entry, "id");
                (known.Contains(entryId) ? trackIds : unresolved).Add(entryId);
            }
            playlists.Add(new SourcePlaylist(id, Str(playlist, "name"), [], false, trackIds, unresolved));
        }

        return new SourceLibrary(SourceKind.Navidrome, Subsonic.LibraryIdOf(login), login.Server.Authority, tracks, playlists, skipped);
    }

    public async Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct)
    {
        // "download" gives the original file. "stream" could transcode, and a server may count it as a
        // play; "download" does neither.
        using var request = new HttpRequestMessage(HttpMethod.Get, Subsonic.Url(login, "download", "id=" + Uri.EscapeDataString(file.Key)));
        var body = await StreamAsync(http, request, ct);

        // A refused download still has status 200: the body is a subsonic-response error, not the file.
        if (MediaTypeOf(body) is "application/json" or "text/xml" or "application/xml")
        {
            await using (body)
                throw await RefusalAsync(body, ct);
        }
        return body;
    }

    /// <summary>What a download answered with an error body means: error 40 or 41 is a sign-in that no
    /// longer works; anything else is a failed read of this one file.</summary>
    private static async Task<Exception> RefusalAsync(Stream body, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var error = Child(Child(document.RootElement, "subsonic-response"), "error");
            var code = (int)Num(error, "code");
            return code is 40 or 41
                ? new MediaServerSignInExpired()
                : new IOException($"The server refused the download: {code} {Str(error, "message")}".TrimEnd());
        }
        catch (JsonException)
        {
            return new IOException("The server answered the download with text instead of the file.");
        }
    }

    /// <summary>Error 40 (wrong credentials) or 41 (token sign-in not allowed) during a read means the
    /// sign-in no longer works.</summary>
    private static async Task<JsonElement> Expired(Func<Task<JsonElement>> call)
    {
        try
        {
            return await call();
        }
        catch (SubsonicError e) when (e.Code is 40 or 41)
        {
            throw new MediaServerSignInExpired();
        }
    }

    /// <summary>The track, or null when its file is not audio rpm reads.</summary>
    private static SourceTrack? TrackOf(JsonElement song)
    {
        var id = Str(song, "id");
        var extension = ExtensionOf(Str(song, "path"), Str(song, "suffix"));
        if (extension.Length == 0)
            return null;
        var size = Num(song, "size");
        return new SourceTrack(
            SourceId: id,
            File: new RemoteFile(id, extension, size),
            Title: Str(song, "title"),
            Artist: Str(song, "artist"),
            Album: Str(song, "album"),
            AlbumArtist: Str(song, "displayAlbumArtist"),
            TrackNumber: (int)Num(song, "track"),
            DiscNumber: (int)Num(song, "discNumber"),
            DurationSeconds: Num(song, "duration"),
            SizeBytes: size,
            Rating: Math.Clamp((int)Num(song, "userRating") * 2, 0, 10),
            Favorite: Str(song, "starred").Length > 0,
            Plays: (int)Num(song, "playCount"),
            LastPlayed: Date(Str(song, "played")),
            Added: Date(Str(song, "created")));
    }
}

/// <summary>A Subsonic API error, with the server's code and message.</summary>
internal sealed class SubsonicError(int code, string serverMessage) : Exception(serverMessage)
{
    public int Code { get; } = code;
    public string ServerMessage { get; } = serverMessage;
}

/// <summary>How every Subsonic request is made.</summary>
internal static class Subsonic
{
    /// <summary>
    /// The address of an API call. The password is never sent: each call carries a new random salt and
    /// the MD5 of the password followed by that salt, as the Subsonic API defines it (version 1.13 and
    /// later). MD5 is what the protocol requires; it is not used here to keep anything secret.
    /// </summary>
    public static Uri Url(SubsonicLogin login, string method, string query)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
#pragma warning disable CA5351 // The Subsonic API defines the token as an MD5 hash.
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(login.Password + salt)));
#pragma warning restore CA5351
        var auth = $"u={Uri.EscapeDataString(login.User)}&t={token}&s={salt}&v=1.16.1&c=rpm&f=json";
        return new Uri(Base(login.Server), $"rest/{method}?{auth}{(query.Length > 0 ? "&" + query : "")}");
    }

    /// <summary>Calls the API and returns the inside of <c>subsonic-response</c>. A failed status is a
    /// <see cref="SubsonicError"/>.</summary>
    public static async Task<JsonElement> CallAsync(HttpClient http, SubsonicLogin login, string method, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(login, method, query));
        var answer = Child(await JsonAsync(http, request, ct), "subsonic-response");
        if (Str(answer, "status") != "ok")
        {
            var error = Child(answer, "error");
            throw new SubsonicError((int)Num(error, "code"), Str(error, "message"));
        }
        return answer;
    }

    /// <summary>The server address and user name, hashed: one Navidrome account is one library.</summary>
    public static string LibraryIdOf(SubsonicLogin login)
    {
        var server = login.Server.AbsoluteUri.TrimEnd('/');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{server}|{login.User}")))[..16];
    }
}
