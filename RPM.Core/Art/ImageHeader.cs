namespace RPM.Core.Art;

/// <summary>
/// How big a picture is, read out of the first few kilobytes of it.
///
/// <para>Every image format states its dimensions in a header near the front, which means the size of
/// a picture can be known without fetching the picture. That is the whole point of this: a cover
/// search shows sixty results and the useful question about each is "how big is it", and sixty full
/// downloads to answer it would be tens of megabytes to fill in a caption.</para>
///
/// <para>So the caller fetches a prefix (a ranged request for the first part of the file) and
/// hands it here. Anything unrecognised, truncated, or not a picture at all comes back null, and the
/// caption simply stays blank: an unmeasured picture is a normal outcome, not an error.</para>
/// </summary>
public static class ImageHeader
{
    /// <summary>How much of a file is worth asking for. PNG and WebP state their size in the first
    /// thirty bytes. JPEG puts it after any number of comment, colour-profile and thumbnail segments
    /// that the encoder wrote first, and 64 KB is enough for these in practice (cameras and scanners)
    /// while still being a small part of the picture.</summary>
    public const int ProbeBytes = 64 * 1024;

    /// <summary>The picture's dimensions, or null when this is not a picture whose header can be read
    /// from what was given.</summary>
    public static (int Width, int Height)? Measure(ReadOnlySpan<byte> bytes)
    {
        if (Png(bytes) is { } png) return png;
        if (Gif(bytes) is { } gif) return gif;
        if (WebP(bytes) is { } webp) return webp;
        if (Jpeg(bytes) is { } jpeg) return jpeg;
        return null;
    }

    // ── PNG ──

    /// <summary>The signature, then IHDR, whose first eight bytes are the two dimensions. Always at
    /// the same offset: the standard requires IHDR to be the first chunk.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static (int, int)? Png(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24 || !b[..8].SequenceEqual(PngSignature))
            return null;
        if (!b.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        return Valid(Big32(b, 16), Big32(b, 20));
    }

    // ── GIF ──

    private static (int, int)? Gif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || !(b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)))
            return null;
        return Valid(Little16(b, 6), Little16(b, 8));
    }

    // ── WebP ──

    /// <summary>
    /// Three formats under one name, and they state their size in three different ways.
    ///
    /// <para>WebP is handled rather than skipped because web image search results are often WebP, and
    /// those results are the ones that most need a size shown.</para>
    /// </summary>
    private static (int, int)? WebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        var chunk = b.Slice(12, 4);

        // Lossy: a keyframe header, with 14-bit dimensions after the start code.
        if (chunk.SequenceEqual("VP8 "u8))
            return Valid(Little16(b, 26) & 0x3FFF, Little16(b, 28) & 0x3FFF);

        // Lossless: 14 bits each, minus one, packed across the four bytes after the signature byte.
        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = (uint)(b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24));
            return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        // Extended: the canvas size, as two 24-bit values minus one.
        if (chunk.SequenceEqual("VP8X"u8))
            return Valid(Little24(b, 24) + 1, Little24(b, 27) + 1);

        return null;
    }

    // ── JPEG ──

    /// <summary>
    /// The only one that has to be walked.
    ///
    /// <para>A JPEG is a chain of segments. The one that carries the dimensions (a start-of-frame)
    /// sits after any number of others that the encoder wrote first: comments, an Exif block, a colour
    /// profile, and often an embedded thumbnail that is itself a whole JPEG. So the segments are
    /// stepped through by their stated lengths until a frame header turns up.</para>
    ///
    /// <para>The walk stops at the start of scan. After that comes entropy-coded image data, in which
    /// bytes that look like segment markers are not segment markers, and walking into it would read
    /// noise as structure.</para>
    /// </summary>
    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
            return null;

        var at = 2;
        while (at + 4 <= b.Length)
        {
            // Markers may be preceded by any number of fill bytes.
            if (b[at] != 0xFF)
            {
                at++;
                continue;
            }
            while (at < b.Length && b[at] == 0xFF)
                at++;
            if (at >= b.Length)
                return null;

            var marker = b[at++];

            // Standalone markers: no length, nothing to skip.
            if (marker is 0x01 or >= 0xD0 and <= 0xD9)
                continue;

            // Start of scan, or end of image. The dimensions were before this or are not coming.
            if (marker is 0xDA or 0xD9)
                return null;

            if (at + 2 > b.Length)
                return null;
            var length = Big16(b, at);
            if (length < 2)
                return null;

            // Any start-of-frame: baseline, progressive, lossless, arithmetic-coded. They differ in how
            // the image is compressed and have exactly the same frame header layout. Three markers in
            // the range are not frames (0xC4 is a Huffman table, 0xC8 is reserved, 0xCC is an
            // arithmetic-coding table). Reading one as a frame gives two plausible numbers that are
            // not the picture's size.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // [length:2][precision:1][height:2][width:2]
                if (at + 7 > b.Length)
                    return null;
                return Valid(Big16(b, at + 5), Big16(b, at + 3));
            }

            at += length;
        }
        return null;
    }

    // ── reading numbers, and refusing nonsense ──

    /// <summary>A measurement is only reported when both halves are positive and neither is absurd.
    /// Garbage read out of a truncated or misidentified file tends to come back as zero or as tens of
    /// millions, and a caption saying a sleeve is 4 million pixels across is worse than no caption.</summary>
    private static (int, int)? Valid(int width, int height)
        => width is > 0 and <= 100_000 && height is > 0 and <= 100_000 ? (width, height) : null;

    private static int Big16(ReadOnlySpan<byte> b, int at) => (b[at] << 8) | b[at + 1];

    private static int Big32(ReadOnlySpan<byte> b, int at)
        => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static int Little16(ReadOnlySpan<byte> b, int at) => b[at] | (b[at + 1] << 8);

    private static int Little24(ReadOnlySpan<byte> b, int at)
        => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16);
}
