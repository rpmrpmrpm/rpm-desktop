using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A record's wash (the sleeve, blurred) cut down to a small grid of colours, so that the colour at one
/// point of it can be read without the picture. <see cref="SleeveGround.FlatAt"/> reads it.
///
/// <para>Small is enough. The wash is blurred far wider than one cell of this grid, so the colour between
/// two cells is the straight blend of the two, and that is what <see cref="At"/> gives.</para>
/// </summary>
public sealed class WashGrid
{
    /// <summary>The cells along the picture's longer side. The shorter side gets as many as keep the
    /// picture's shape.</summary>
    private const int LongSide = 48;

    private readonly byte[] _rgb;

    /// <param name="width">Cells across.</param>
    /// <param name="height">Cells down.</param>
    /// <param name="rgb">Three bytes per cell, red, green and blue, row after row from the top.</param>
    /// <param name="aspect">The picture's width divided by its height. The grid keeps it, because
    /// UniformToFill places the picture by its own shape.</param>
    public WashGrid(int width, int height, byte[] rgb, double aspect)
    {
        if (width < 1 || height < 1 || rgb.Length < width * height * 3 || aspect <= 0)
            throw new ArgumentException("A wash grid needs at least one cell, three bytes a cell and a shape.");
        Width = width;
        Height = height;
        _rgb = rgb;
        Aspect = aspect;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The picture's width divided by its height.</summary>
    public double Aspect { get; }

    /// <summary>Decodes a wash and cuts it down to the grid. Null where it will not decode. This blocks,
    /// so it runs where the wash is made, on a worker thread.</summary>
    public static WashGrid? Read(byte[] wash)
    {
        try
        {
            using var picture = SKBitmap.Decode(wash);
            if (picture is null || picture.Width < 1 || picture.Height < 1)
                return null;

            var aspect = (double)picture.Width / picture.Height;
            var width = aspect >= 1 ? LongSide : Math.Max(1, (int)Math.Round(LongSide * aspect));
            var height = aspect >= 1 ? Math.Max(1, (int)Math.Round(LongSide / aspect)) : LongSide;

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var small = picture.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (small is null)
                return null;

            var pixels = small.GetPixelSpan();
            var rgb = new byte[width * height * 3];
            for (int i = 0, o = 0; i + 3 < pixels.Length && o + 2 < rgb.Length; i += 4, o += 3)
            {
                rgb[o] = pixels[i];
                rgb[o + 1] = pixels[i + 1];
                rgb[o + 2] = pixels[i + 2];
            }
            return new WashGrid(width, height, rgb, aspect);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where a point of a box lands on the picture, as fractions of the picture from 0 to 1, when the
    /// picture is drawn over the box with UniformToFill and centred. The picture is scaled until it covers
    /// the box, so one of its two sides runs past the box's edges by the same amount at each end.
    /// </summary>
    public (double U, double V) Place(double x, double y, double boxWidth, double boxHeight)
    {
        var scale = Math.Max(boxWidth / Aspect, boxHeight);
        var drawnWidth = Aspect * scale;
        var drawnHeight = scale;
        var left = (boxWidth - drawnWidth) / 2;
        var top = (boxHeight - drawnHeight) / 2;
        return (Math.Clamp((x - left) / drawnWidth, 0, 1), Math.Clamp((y - top) / drawnHeight, 0, 1));
    }

    /// <summary>The colour at a point of the picture, given as fractions from 0 to 1. Between cell centres
    /// it is the blend of the four cells around the point. Past the outer centres it is the edge
    /// cell.</summary>
    public Color At(double u, double v)
    {
        var x = Math.Clamp((u * Width) - 0.5, 0, Width - 1);
        var y = Math.Clamp((v * Height) - 0.5, 0, Height - 1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;

        return Color.FromArgb(0xFF, Channel(0), Channel(1), Channel(2));

        byte Channel(int c)
        {
            double Cell(int cx, int cy) => _rgb[(((cy * Width) + cx) * 3) + c];
            var top = Cell(x0, y0) + ((Cell(x1, y0) - Cell(x0, y0)) * fx);
            var bottom = Cell(x0, y1) + ((Cell(x1, y1) - Cell(x0, y1)) * fx);
            return (byte)Math.Clamp(Math.Round(top + ((bottom - top) * fy)), 0, 255);
        }
    }
}
