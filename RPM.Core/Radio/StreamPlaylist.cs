namespace RPM.Core.Radio;

/// <summary>
/// Some station URLs are not a stream. They are a one-line playlist that points at one: a <c>.pls</c>
/// from a SHOUTcast server, or an <c>.m3u</c> from an Icecast server. The directory resolves most of
/// them, but not all. A station that changed its URL after the last check can hand one over at connect
/// time. This unwraps it, so the reader follows the pointer instead of trying to decode it.
/// </summary>
public static class StreamPlaylist
{
    /// <summary>True when a response looks like a playlist rather than audio. This is judged from the
    /// content type or, when the server is vague about it (many send <c>text/plain</c>), from how the
    /// body starts.</summary>
    public static bool LooksLikePlaylist(string? contentType, ReadOnlySpan<byte> head)
    {
        if (contentType is not null)
        {
            var type = contentType.Trim();
            if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("scpls", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("pls+xml", StringComparison.OrdinalIgnoreCase))
                return true;
            // An audio content type is the station saying it is a stream; believe it.
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("application/ogg", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // The header gives no help, so look at the body. A playlist is text and starts with a marker.
        // Audio is not text: an MP3 frame or an ID3 tag has a high bit set in the first couple of
        // bytes.
        Span<char> start = stackalloc char[16];
        int taken = 0;
        foreach (var b in head)
        {
            if (taken == start.Length) break;
            if (b is (< 0x20 and not (byte)'\n' and not (byte)'\r' and not (byte)'\t') or > 0x7E) return false;
            start[taken++] = (char)b;
        }

        var text = new string(start[..taken]).TrimStart();
        return text.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Reference]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first playable URL in a <c>.pls</c>, <c>.m3u</c> or ASX-style reference, or null
    /// when there is none. Nested playlists are not followed. A station gets one hop only.</summary>
    public static string? FirstUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0)
                continue;

            // A pls line looks like "File1=http://...", and an ASX-style line like "Ref1=http://...".
            // The other keys in the ini file (Title1, Length1, NumberOfEntries) have no URL, so they
            // are skipped.
            int eq = line.IndexOf('=');
            if (eq > 0 && (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("Ref", StringComparison.OrdinalIgnoreCase)))
            {
                if (Http(line[(eq + 1)..].Trim()) is { } url)
                    return url;
                continue;
            }

            // m3u: bare URLs, #EXTINF and friends commented out.
            if (line[0] == '#' || line[0] == '[')
                continue;
            if (Http(line) is { } plain)
                return plain;
        }

        return null;
    }

    /// <summary>An http(s) URL, or null. <c>mms://</c> and the other dead protocols cannot be opened by
    /// this player, so they are not returned.</summary>
    private static string? Http(string candidate)
    {
        // ASX references have "http://..." inside an attribute. Strip the quotes that a simple line
        // split leaves behind.
        var value = candidate.Trim().Trim('"', '\'');
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
    }
}
