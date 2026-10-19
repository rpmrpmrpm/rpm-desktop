using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// Reading a picture's size out of its first few kilobytes.
///
/// <para>The samples are real encoder output (a 1000×999 picture saved by the system's image tools
/// and by cwebp, then cut down to the header), not bytes written by hand. This is what makes the test
/// useful: a hand-built header only confirms that the parser matches what its author believed, and
/// the formats have details that are easy to get wrong (a JPEG frame after an Exif block that also
/// states the dimensions, WebP storing its size three different ways under one name).</para>
///
/// <para>999 is deliberate. A square sample would pass a parser that swaps width and height, and
/// album covers are almost always square.</para>
/// </summary>
public class ImageHeaderTests
{
    private static byte[] Sample(string base64) => Convert.FromBase64String(base64);

    // ── real files ──

    /// <summary>PNG: signature, then IHDR, which the format requires to be the first chunk.</summary>
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAA+gAAAPnCAYAAAC89WYxAAAABGdBTQ==";

    /// <summary>
    /// JPEG: JFIF, then an Exif block, then the frame header. 109 bytes. The Exif block contains
    /// PixelXDimension and PixelYDimension, so the same two numbers appear twice in this sample. A
    /// parser that searches for something that looks like a size, instead of walking the segments,
    /// finds the Exif copy. That copy is correct here but not always: after a resize, the Exif values
    /// can differ from the frame.
    /// </summary>
    private const string Jpeg =
        "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6AB"
        + "AAMAAAABAAEAAKACAAQAAAABAAAD6KADAAQAAAABAAAD5wAAAAD/wAARCAPnA+gDAQ==";

    private const string Gif = "R0lGODdh6APnA9UAAAAAAA==";

    /// <summary>WebP, lossy: a VP8 keyframe, whose dimensions are 14 bits each after the start code.</summary>
    private const string WebPLossy = "UklGRggpAABXRUJQVlA4IPwoAACQvgGdASroA+cDPpFIng==";

    /// <summary>WebP, lossless: VP8L, which packs both dimensions minus one across four bytes.</summary>
    private const string WebPLossless = "UklGRrKgAABXRUJQVlA4TKagAAAv54P5ABmHbdtIEjx7dw==";

    [Theory]
    [InlineData(Png)]
    [InlineData(Jpeg)]
    [InlineData(Gif)]
    [InlineData(WebPLossy)]
    [InlineData(WebPLossless)]
    public void A_real_picture_is_measured_from_its_header_alone(string sample)
        => Assert.Equal((1000, 999), ImageHeader.Measure(Sample(sample)));

    [Fact]
    public void A_header_is_enough_and_the_rest_of_the_file_is_not_needed()
    {
        // The reason for this parser: these samples are between 24 and 109 bytes, and the files they
        // came from are 43 KB to 130 KB. Measuring sixty results this way needs a few kilobytes;
        // downloading sixty results to measure them needs about a hundred megabytes.
        Assert.True(Sample(Png).Length < 64);
        Assert.True(Sample(Jpeg).Length < 128);
    }

    // ── refusals ──

    [Fact]
    public void Something_that_is_not_a_picture_is_not_measured()
        => Assert.Null(ImageHeader.Measure("<!doctype html><html><body>Forbidden"u8));

    [Fact]
    public void An_empty_answer_is_not_measured()
        => Assert.Null(ImageHeader.Measure([]));

    [Fact]
    public void A_picture_cut_off_before_its_size_is_not_guessed_at()
    {
        // A ranged request that returned too few bytes, or a JPEG whose frame header is past the probe
        // length. Both must return null rather than a number read from the bytes that did arrive.
        var truncated = Sample(Png)[..16];
        Assert.Null(ImageHeader.Measure(truncated));
    }

    [Fact]
    public void A_jpeg_that_is_only_its_opening_marker_is_not_measured()
        => Assert.Null(ImageHeader.Measure([0xFF, 0xD8, 0xFF, 0xE0]));

    [Fact]
    public void A_jpeg_whose_segments_do_not_lead_anywhere_terminates()
    {
        // A segment with a length of zero would make the walk loop for ever if it were trusted. The
        // test checks that the call returns.
        Assert.Null(ImageHeader.Measure([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void A_huffman_table_is_not_read_as_a_frame_header()
    {
        // 0xC4 is inside the range of start-of-frame markers but is not one. Read as a frame, it gives
        // two numbers that look correct but are the table's contents.
        ReadOnlySpan<byte> dht = [0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x03, 0xE8, 0x03, 0xE7, 0x00];
        Assert.Null(ImageHeader.Measure(dht));
    }

    [Fact]
    public void A_frame_header_is_read_height_first()
    {
        // A square test picture would not catch this: a JPEG frame stores its HEIGHT before its
        // width, unlike the other formats here.
        ReadOnlySpan<byte> sof = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x03, 0xE7, 0x03, 0xE8, 0x03];
        Assert.Equal((1000, 999), ImageHeader.Measure(sof));
    }
}
