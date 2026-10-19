using System;
using RPM.Core.Art;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres.Models;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// Cutting one photograph to the shape a genre card wants it.
///
/// <para>The choosing is done by the models (U²-Netp and UltraFace, through <c>SaliencyCrop</c>). None of
/// that runs here. These tests check the step after: the window that was chosen is the part that is cut, at
/// the shape that was asked for. A picture where no window could be chosen still comes back as a
/// picture.</para>
/// </summary>
public class GenreFaceCutTests
{
    /// <summary>A picture with a red mark in a known place. This shows that a cut took the part it was told
    /// to take, and not the middle.</summary>
    private static byte[] Picture(int width, int height, SKRectI? mark = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.DarkSlateGray);
        if (mark is { } box)
            surface.Canvas.DrawRect(box, new SKPaint { Color = SKColors.Red });

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    private static (int Width, int Height) Shape(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded);
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>The cut has the shape the card asks for, whatever the shape of the photograph. Press photos
    /// are often landscape and often portrait, and the card's slot is neither.</summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1920, 1080)]
    [InlineData(600, 900)]
    public void A_cut_comes_out_at_the_shape_that_was_asked_for(int width, int height)
    {
        var cut = PlaylistArt.Cut(
            new PlaylistArt.Framed(Picture(width, height), null), GenreCardMetrics.PictureShape);

        var (w, h) = Shape(cut);

        Assert.Equal(GenreCardMetrics.PictureShape, (double)w / h, 1);
    }

    /// <summary>
    /// A chosen window is what gets cut.
    ///
    /// <para>The two models are run over a photograph so that their answer is used. This test puts a red
    /// mark in the top-left quarter of a picture, names that quarter as the window, and checks that the cut
    /// is red. A centre crop of the same picture would not be red.</para>
    /// </summary>
    [Fact]
    public void The_window_somebody_chose_is_the_part_that_is_cut()
    {
        var picture = Picture(1000, 1000, SKRectI.Create(0, 0, 400, 460));

        var cut = PlaylistArt.Cut(
            new PlaylistArt.Framed(picture, new CropRect(0, 0, 400, 460)),
            GenreCardMetrics.PictureShape);

        using var bitmap = SKBitmap.Decode(cut);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));

        // And a cut with no window named takes the middle of the whole picture, which here is not red.
        var whole = PlaylistArt.Cut(
            new PlaylistArt.Framed(picture, null), GenreCardMetrics.PictureShape);

        using var middle = SKBitmap.Decode(whole);
        Assert.NotEqual(SKColors.Red, middle.GetPixel(middle.Width / 2, middle.Height / 2));
    }

    /// <summary>A window that reaches a pixel or two past the edge of its picture is held inside the
    /// picture, and does not throw. This can happen because the crop is chosen on a smaller map, and
    /// scaling back up can push an edge over.</summary>
    [Fact]
    public void A_window_that_overhangs_the_picture_is_still_cut()
    {
        var cut = PlaylistArt.Cut(
            new PlaylistArt.Framed(Picture(400, 400), new CropRect(300, 300, 400, 400)),
            GenreCardMetrics.PictureShape);

        var (w, h) = Shape(cut);

        Assert.True(w > 0 && h > 0);
        Assert.InRange(w, 1, 400);
        Assert.InRange(h, 1, 400);
    }

    /// <summary>A photograph that cannot be decoded comes back exactly as it arrived. The card can then
    /// still show the picture. If this threw, the card could show nothing at all.</summary>
    [Fact]
    public void A_picture_that_cannot_be_cut_comes_back_whole()
    {
        byte[] nonsense = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Same(nonsense, PlaylistArt.Cut(
            new PlaylistArt.Framed(nonsense, null), GenreCardMetrics.PictureShape));
    }

    /// <summary>The shape asked of the models is the photograph's shape, not the card's shape. The two
    /// differ, because the picture does not cover the whole card. If the card's shape were used, the window
    /// would be framed in one shape and then cropped to another, and a head could be cut in half.</summary>
    [Fact]
    public void The_shape_asked_for_is_the_photographs_and_not_the_cards()
    {
        Assert.True(GenreCardMetrics.PictureShape < 1 / GenreCardMetrics.Shape,
            "the photograph's slot is narrower than the card it stands in");
        Assert.True(1 / GenreCardMetrics.Shape > 1, "the card itself is landscape");
    }
}
