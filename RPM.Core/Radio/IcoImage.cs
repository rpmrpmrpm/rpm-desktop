using System.Buffers.Binary;

namespace RPM.Core.Radio;

/// <summary>
/// Reads a Windows <c>.ico</c> into something an ordinary image decoder can open.
///
/// <para><b>Why this is needed.</b> A station whose only picture is its website's favicon had no picture in
/// this app. No image decoder in the stack opens an ICO file, so those addresses were rejected. Such
/// stations showed as blank rows next to a picture that does exist.</para>
///
/// <para><b>Why it is more than taking out one frame.</b> An ICO is a container, and its frames come in two
/// forms. A modern frame holds a PNG, which can be handed to a decoder as it is. An older frame holds a raw
/// DIB with no file header. Its height is written as double the real height, because the format expects a
/// monochrome mask stacked under the picture. Handed to a decoder as it is, the data is not a file. Handed
/// over with the height left uncorrected, the picture has a band of noise under it.</para>
/// </summary>
public static class IcoImage
{
    /// <summary>
    /// The largest frame, as the bytes of a file. A frame that is already a PNG is returned as it is. A
    /// bare DIB is converted to a BMP. Null when this is not an ICO or it holds nothing usable.
    /// </summary>
    public static byte[]? LargestFrame(byte[]? ico)
    {
        if (ico is null || ico.Length < Header + Entry)
            return null;

        // The reserved field must be 0 and the type must be 1. A file that is really a PNG but has an
        // .ico name is common, so it is returned as it is rather than refused.
        if (Read16(ico, 0) != 0 || Read16(ico, 2) != 1)
            return IsPng(ico, 0) ? ico : null;

        int count = Read16(ico, 4);
        if (count <= 0 || ico.Length < Header + (count * Entry))
            return null;

        var best = -1;
        var bestArea = -1;
        var bestDepth = -1;

        for (var i = 0; i < count; i++)
        {
            var at = Header + (i * Entry);

            // 0 means 256. The field is one byte, so 256 is stored as 0.
            int width = ico[at] == 0 ? 256 : ico[at];
            int height = ico[at + 1] == 0 ? 256 : ico[at + 1];
            int depth = Read16(ico, at + 6);

            var length = (int)Read32(ico, at + 8);
            var offset = (int)Read32(ico, at + 12);
            if (length <= 0 || offset < 0 || offset + length > ico.Length)
                continue;   // an entry that points outside the file

            var area = width * height;
            if (area < bestArea || (area == bestArea && depth <= bestDepth))
                continue;

            best = at;
            bestArea = area;
            bestDepth = depth;
        }

        if (best < 0)
            return null;

        var frameLength = (int)Read32(ico, best + 8);
        var frameAt = (int)Read32(ico, best + 12);
        var frame = ico.AsSpan(frameAt, frameLength);

        return IsPng(frame) ? frame.ToArray() : Bitmap(frame);
    }

    /// <summary>
    /// A bare DIB wrapped back into a .bmp: the file header that the format leaves off, and the height set
    /// back to the real height of the picture.
    /// </summary>
    /// <remarks>
    /// A 32-bit frame is re-declared as <c>BITMAPV4HEADER</c> with explicit channel masks, not left as a
    /// plain <c>BITMAPINFOHEADER</c>. The older header has no way to say that its fourth byte is alpha. A
    /// decoder reading one may treat that channel as padding. An icon whose transparency is read as padding
    /// shows its mark on a black square, which looks worse in a list than no picture at all.
    /// </remarks>
    private static byte[]? Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < InfoHeader)
            return null;

        var headerSize = (int)Read32(dib, 0);
        if (headerSize < InfoHeader || headerSize > dib.Length)
            return null;   // neither a PNG nor a DIB

        var width = (int)Read32(dib, 4);
        var stacked = (int)Read32(dib, 8);
        int depth = Read16(dib, 14);
        var compression = (int)Read32(dib, 16);

        // The height is doubled because of the mask. An ICO frame always has a mask, even a 32-bit frame
        // that does not need one, so this division always applies.
        var height = stacked / 2;
        if (width <= 0 || height <= 0)
            return null;

        var colours = (int)Read32(dib, 32);
        if (colours == 0 && depth <= 8)
            colours = 1 << depth;

        var palette = colours * 4;
        var upgrading = depth == 32 && compression == Rgb;
        var outHeader = upgrading ? V4Header : headerSize;

        // Only the picture is copied. The mask after it is left out. Rows are padded to four bytes, so
        // the picture's length is calculated from the rows, not taken as the rest of the frame. If the
        // mask were included, the decoder would read the monochrome mask as more rows of colour.
        var stride = (((width * depth) + 31) / 32) * 4;
        var pixels = Math.Min(stride * height, dib.Length - headerSize - palette);
        if (pixels <= 0)
            return null;

        var file = new byte[FileHeader + outHeader + palette + pixels];
        var span = file.AsSpan();

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        Write32(span, 2, (uint)file.Length);
        Write32(span, 10, (uint)(FileHeader + outHeader + palette));

        // The header, then the palette, then the pixels, each after the file header.
        dib[..headerSize].CopyTo(span[FileHeader..]);
        Write32(span, FileHeader + 4, (uint)width);
        Write32(span, FileHeader + 8, (uint)height);
        Write32(span, FileHeader + 20, (uint)pixels);   // biSizeImage, now that it is one picture

        if (upgrading)
        {
            Write32(span, FileHeader, V4Header);
            Write32(span, FileHeader + 16, Bitfields);
            Write32(span, FileHeader + 40, 0x00FF0000);   // red
            Write32(span, FileHeader + 44, 0x0000FF00);   // green
            Write32(span, FileHeader + 48, 0x000000FF);   // blue
            Write32(span, FileHeader + 52, 0xFF000000);   // alpha, which the old header could not describe
        }

        dib.Slice(headerSize, palette + pixels).CopyTo(span[(FileHeader + outHeader)..]);
        return file;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes, int at = 0)
        => bytes.Length >= at + 8
           && bytes[at] == 0x89 && bytes[at + 1] == 'P' && bytes[at + 2] == 'N' && bytes[at + 3] == 'G';

    private static ushort Read16(ReadOnlySpan<byte> bytes, int at)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);

    private static uint Read32(ReadOnlySpan<byte> bytes, int at)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    private static void Write32(Span<byte> bytes, int at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);

    private const int Header = 6;
    private const int Entry = 16;
    private const int FileHeader = 14;
    private const int InfoHeader = 40;
    private const int V4Header = 108;
    private const uint Rgb = 0;
    private const uint Bitfields = 3;
}
