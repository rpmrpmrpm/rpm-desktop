using System.Buffers.Binary;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class IcoImageTests
{
    /// <summary>An icon file that holds the given frames. Built in code rather than read from disk,
    /// so the bytes under test are visible here and a failure shows which field is wrong.</summary>
    private static byte[] Ico(params (int Width, int Height, int Depth, byte[] Data)[] frames)
    {
        var head = 6 + (16 * frames.Length);
        var file = new List<byte>(head);

        file.AddRange([0, 0, 1, 0]);                                   // reserved, type = icon
        file.AddRange([(byte)frames.Length, 0]);

        var at = head;
        foreach (var (width, height, depth, data) in frames)
        {
            file.Add((byte)(width == 256 ? 0 : width));
            file.Add((byte)(height == 256 ? 0 : height));
            file.AddRange([0, 0, 1, 0]);                               // colours, reserved, planes
            file.AddRange(Bytes16((ushort)depth));
            file.AddRange(Bytes32((uint)data.Length));
            file.AddRange(Bytes32((uint)at));
            at += data.Length;
        }

        foreach (var frame in frames)
            file.AddRange(frame.Data);

        return [.. file];
    }

    /// <summary>A bare DIB as an icon stores it: no file header, and the height written double
    /// because the format expects a mask below the image.</summary>
    private static byte[] Dib(int width, int height, int depth)
    {
        var stride = (((width * depth) + 31) / 32) * 4;
        var mask = (((width * 1) + 31) / 32) * 4 * height;
        var dib = new byte[40 + (stride * height) + mask];

        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(8), (uint)(height * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)depth);

        // A known value in the first pixel, to prove that the output is the image and not the mask.
        dib[40] = 0x11; dib[41] = 0x22; dib[42] = 0x33; dib[43] = 0x44;
        return dib;
    }

    private static byte[] Png(int bytes = 64)
    {
        var png = new byte[bytes];
        png[0] = 0x89; png[1] = (byte)'P'; png[2] = (byte)'N'; png[3] = (byte)'G';
        return png;
    }

    private static byte[] Bytes16(ushort value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] Bytes32(uint value)
        => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    private static uint Read32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    // ── the two frame formats ──

    /// <summary>A modern icon holds a PNG, which is returned unchanged.</summary>
    [Fact]
    public void A_png_frame_is_handed_over_as_it_is()
    {
        var png = Png();

        Assert.Equal(png, IcoImage.LargestFrame(Ico((32, 32, 32, png))));
    }

    /// <summary>An older icon holds a raw DIB. It needs a file header to be a valid bitmap
    /// file.</summary>
    [Fact]
    public void A_bare_dib_comes_back_as_a_bitmap_file()
    {
        var bmp = IcoImage.LargestFrame(Ico((32, 32, 32, Dib(32, 32, 32))));

        Assert.NotNull(bmp);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, Read32(bmp, 2));
    }

    /// <summary>The height is written double in an icon. If it is not corrected, the image shows a
    /// band of noise below it, because the lower half is the mask and not image data.</summary>
    [Fact]
    public void The_doubled_height_is_put_back()
    {
        var bmp = IcoImage.LargestFrame(Ico((32, 32, 32, Dib(32, 32, 32))))!;

        Assert.Equal(32u, Read32(bmp, 14 + 4));    // biWidth
        Assert.Equal(32u, Read32(bmp, 14 + 8));    // biHeight, 32 and not 64
    }

    /// <summary>
    /// A 32-bit frame is given a new header with explicit channel masks. The older header cannot say
    /// that its fourth byte is alpha, so a decoder may treat that channel as padding. The icon then
    /// shows on a black square instead of a transparent background.
    /// </summary>
    [Fact]
    public void A_32_bit_frame_is_given_a_header_that_can_say_which_channel_is_alpha()
    {
        var bmp = IcoImage.LargestFrame(Ico((32, 32, 32, Dib(32, 32, 32))))!;

        Assert.Equal(108u, Read32(bmp, 14));           // BITMAPV4HEADER
        Assert.Equal(3u, Read32(bmp, 14 + 16));        // BI_BITFIELDS
        Assert.Equal(0x00FF0000u, Read32(bmp, 14 + 40));
        Assert.Equal(0x0000FF00u, Read32(bmp, 14 + 44));
        Assert.Equal(0x000000FFu, Read32(bmp, 14 + 48));
        Assert.Equal(0xFF000000u, Read32(bmp, 14 + 52));
        Assert.Equal(122u, Read32(bmp, 10));           // pixels start after both headers
    }

    /// <summary>The image, and not the mask below it. Rows are padded to four bytes, so the image's
    /// length is computed rather than taken as "the rest of the frame".</summary>
    [Fact]
    public void The_mask_is_left_behind()
    {
        var bmp = IcoImage.LargestFrame(Ico((32, 32, 32, Dib(32, 32, 32))))!;

        Assert.Equal(32u * 32u * 4u, Read32(bmp, 14 + 20));   // biSizeImage
        Assert.Equal(122 + (32 * 32 * 4), bmp.Length);
        Assert.Equal(0x11, bmp[122]);                          // and it starts at the image
    }

    // ── which frame ──

    [Fact]
    public void The_biggest_frame_wins()
    {
        var small = Png(16);
        var big = Png(32);

        Assert.Equal(big, IcoImage.LargestFrame(Ico((16, 16, 32, small), (64, 64, 32, big))));
    }

    /// <summary>A width of zero means 256, because the field is one byte.</summary>
    [Fact]
    public void A_frame_written_as_zero_is_the_largest_there_is()
    {
        var ordinary = Png(16);
        var huge = Png(32);

        Assert.Equal(huge, IcoImage.LargestFrame(Ico((128, 128, 32, ordinary), (256, 256, 32, huge))));
    }

    [Fact]
    public void Depth_settles_two_frames_of_one_size()
    {
        var shallow = Png(16);
        var deep = Png(32);

        Assert.Equal(deep, IcoImage.LargestFrame(Ico((32, 32, 8, shallow), (32, 32, 32, deep))));
    }

    // ── input that is not an icon ──

    /// <summary>A PNG served under a .ico name is common, so it is accepted rather than
    /// refused.</summary>
    [Fact]
    public void A_png_wearing_the_wrong_name_is_still_a_png()
    {
        var png = Png();

        Assert.Equal(png, IcoImage.LargestFrame(png));
    }

    [Fact]
    public void Nothing_usable_gives_nothing()
    {
        Assert.Null(IcoImage.LargestFrame(null));
        Assert.Null(IcoImage.LargestFrame([]));
        Assert.Null(IcoImage.LargestFrame([1, 2, 3]));
        Assert.Null(IcoImage.LargestFrame(new byte[64]));   // an empty header claiming nothing
    }

    /// <summary>An entry pointing outside the file it is in is not a frame.</summary>
    [Fact]
    public void A_frame_that_runs_off_the_end_is_ignored()
    {
        var ico = Ico((32, 32, 32, Png()));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), 999_999);   // dwBytesInRes

        Assert.Null(IcoImage.LargestFrame(ico));
    }
}
