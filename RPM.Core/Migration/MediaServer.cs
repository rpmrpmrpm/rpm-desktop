using RPM.Core.Scanning;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RPM.Core.Migration;

/// <summary>A media server whose library can be imported: Jellyfin, Plex, or a Subsonic server such as
/// Navidrome. Every call only reads from the server.</summary>
public interface IMediaServer
{
    SourceKind Kind { get; }

    /// <summary>Reads the whole library: tracks, playlists. <paramref name="found"/> is told the number
    /// of tracks read so far, after each page.</summary>
    /// <exception cref="MediaServerSignInExpired">The server no longer accepts the sign-in.</exception>
    Task<SourceLibrary> ReadAsync(IProgress<int> found, CancellationToken ct);

    /// <summary>The original file of a track, as a stream to read once.</summary>
    /// <exception cref="MediaServerSignInExpired">The server no longer accepts the sign-in.</exception>
    Task<Stream> OpenAsync(RemoteFile file, CancellationToken ct);
}

/// <summary>The media server refused the sign-in that was accepted before (it answered 401, or a
/// Subsonic error 40 or 41). The import stops and asks the person to sign in again; the tracks it had
/// not reached stay waiting, they are not failures.</summary>
public sealed class MediaServerSignInExpired() : Exception("The media server no longer accepts the sign-in.");

/// <summary>The media server refused a sign-in: a wrong user name or password, or a code that was not
/// approved. <paramref name="serverMessage"/> is the server's own text, when it gives one.</summary>
public sealed class MediaServerSignInFailed(string? serverMessage = null)
    : Exception(serverMessage ?? "The media server refused the sign-in.")
{
    public string? ServerMessage { get; } = serverMessage;
}

/// <summary>The requests and JSON reading the server readers share.</summary>
internal static class MediaServerHttp
{
    /// <summary>A server address that ends with a slash, so relative paths join under it, including a
    /// server that sits under a path (<c>https://host/jellyfin/</c>).</summary>
    public static Uri Base(Uri server) =>
        server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");

    /// <summary>Sends the request and reads the JSON answer. 401 is <see cref="MediaServerSignInExpired"/>,
    /// any other failure an <see cref="HttpRequestException"/> with the status.</summary>
    public static async Task<JsonElement> JsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new MediaServerSignInExpired();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The media server answered {(int)response.StatusCode}.", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    /// <summary>Sends the request and hands back the body as a stream, which the caller disposes. The
    /// response is read as it arrives, never buffered whole: a file can be several hundred megabytes.</summary>
    public static async Task<Stream> StreamAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MediaServerSignInExpired();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The media server answered {(int)response.StatusCode}.", null, response.StatusCode);
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static long Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    public static double Real(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        Child(e, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>An ISO 8601 date, or null. Servers write up to nine digits after the second (Go's
    /// RFC 3339 with nanoseconds), which <see cref="DateTimeOffset"/> does not read, so the digits past
    /// the seventh are dropped first.</summary>
    public static DateTimeOffset? Date(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var dot = text.IndexOf('.');
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
                end++;
            if (end - dot - 1 > 7)
                text = text[..(dot + 8)] + text[end..];
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }

    /// <summary>A body stream that disposes its response with it.</summary>
    /// <summary>
    /// The file type of a server's track, as the extension rpm's library reads (".m4a"), or "" when it is
    /// not audio rpm reads. The extension of the file's own name comes first, because it is what the file
    /// is. Without a name, the server's container name is mapped: Plex says "mp4" for .m4a files, and
    /// Jellyfin may give ffprobe's list ("mov,mp4,m4a,3gp,3g2,mj2"). A file with an extension the library
    /// does not read would be dropped by the next library scan, with its plays and rating.
    /// </summary>
    public static string ExtensionOf(string? path, string? container)
    {
        if (Path.GetExtension(path ?? "").ToLowerInvariant() is { Length: > 1 } named && AudioFileScanner.AudioExtensions.Contains(named))
            return named;
        foreach (var name in (container ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var mapped = name.ToLowerInvariant() switch
            {
                "mp4" or "m4a" or "m4b" or "mov" or "alac" => ".m4a",
                "mpeg" or "mp3" => ".mp3",
                "aif" or "aiff" => ".aiff",
                "asf" or "wma" => ".wma",
                "ogg" or "oga" or "vorbis" => ".ogg",
                var other => "." + other,
            };
            if (AudioFileScanner.AudioExtensions.Contains(mapped))
                return mapped;
        }
        return "";
    }

    /// <summary>The media type of a body <see cref="StreamAsync"/> handed back, such as "audio/flac", or
    /// null when the server gave none.</summary>
    public static string? MediaTypeOf(Stream body) => (body as ResponseStream)?.MediaType;

    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public string? MediaType => response.Content.Headers.ContentType?.MediaType;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
