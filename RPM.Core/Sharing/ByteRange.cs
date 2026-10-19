namespace RPM.Core.Sharing;

/// <summary>
/// The piece of a file a borrower asked for.
///
/// <para>A peer's audio needs one thing that a Cast stream never did. ffmpeg does not read a track from start
/// to end. It reads the head to find out what the file is, then jumps to wherever the listener dropped the
/// needle. Each of those jumps is a <c>Range</c> request. A server that ignores Range can play music, but it
/// cannot seek.</para>
/// </summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    /// <summary>
    /// Reads a <c>Range</c> header against a file of <paramref name="length"/> bytes.
    ///
    /// <para>Returns null for a header that is absent, malformed, or multi-range. Nobody sends multi-range for
    /// audio, and answering it wrongly is worse than answering with the whole file. Null also means the header
    /// asks for nothing that this file has. The caller treats null as the whole file, which is what a client
    /// that sent no Range wanted anyway.</para>
    /// </summary>
    public static ByteRange? Parse(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header) || length <= 0)
            return null;

        var text = header.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var spec = text["bytes=".Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
            return null;

        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
            return null;

        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();

        // "-500": the last 500 bytes. Used by anything that reads a trailer. An MP4's moov atom is at the end of
        // the file when the file was not made for streaming, which is most files.
        if (from.Length == 0)
        {
            if (!long.TryParse(to, out var wanted) || wanted <= 0)
                return null;

            var start = Math.Max(0, length - wanted);
            return new ByteRange(start, length - 1);
        }

        if (!long.TryParse(from, out var begin) || begin < 0 || begin >= length)
            return null;

        if (to.Length == 0)
            return new ByteRange(begin, length - 1);

        if (!long.TryParse(to, out var end) || end < begin)
            return null;

        return new ByteRange(begin, Math.Min(end, length - 1));
    }
}
