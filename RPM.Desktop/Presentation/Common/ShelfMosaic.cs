using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A picture for a shelf that is not a person. The sleeves on the shelf are laid out as a
/// grid, and drawn as one image.
///
/// <para><b>Why this exists.</b> Every other name on the artists wall is a person that a catalogue can
/// be asked about, and the answer is a photograph. The compilations shelf is not a person. See
/// <c>MainPage.NobodyInParticular</c>. So there is nothing to ask and nothing to fetch. The page used
/// the made figure (<see cref="SoftFigure"/>) instead. That figure is a soft coloured shape, and at
/// four hundred points tall it looks like a picture that failed to load.</para>
///
/// <para><b>Why the sleeves.</b> A shelf of compilations has no face. It does have many covers, and
/// they are the only true picture of it. They are the records actually on the shelf, in the user's own
/// collection. Nothing is invented and nothing is fetched.</para>
///
/// <para><b>Drawn once, off the UI thread, and returned as bytes.</b> The reason is the same as for
/// <see cref="SoftFigure"/>, which explains it in full. The result is an ordinary JPEG that any surface
/// can draw. A band does not have to lay out and clip a dozen live <c>Image</c> elements.</para>
/// </summary>
public static class ShelfMosaic
{
    /// <summary>How many sleeves across the band, and how many down. The band is wider than it is tall,
    /// and each cell has to stay square. A sleeve stretched to fill an oblong is the one thing that
    /// would make this look like a mistake, rather than like a wall of records.</summary>
    private const int Columns = 4;
    private const int Rows = 3;

    /// <summary>One cell, in pixels. Kept small on purpose, like every other picture this app draws for
    /// itself. The band shows the grid a few hundred points wide, softened by a colour wash. A cell
    /// larger than a thumbnail would be pixels drawn only to be thrown away.</summary>
    private const int Cell = 160;

    /// <summary>How many sleeves the grid holds.</summary>
    public const int Cells = Columns * Rows;

    /// <summary>The fewest sleeves worth making a picture out of.
    ///
    /// <para>Below this the grid is one cover repeated across the band. That would say "this shelf
    /// holds one record" about a shelf that holds forty, with no artwork for most of them. The made
    /// figure is the better answer there, and the caller falls back to it.</para>
    /// </summary>
    public const int Least = 4;

    /// <summary>The band at the top of the shelf's page.
    /// </summary>
    /// <param name="sleeves">The covers of the records on the shelf, best first, already cut to
    /// something like a thumbnail. Fewer than <see cref="Cells"/> are repeated round to fill the grid.
    /// More are ignored. Fewer than <see cref="Least"/> gives null.</param>
    /// <returns>A JPEG, or null where there was too little to draw with or Skia would not
    /// paint.</returns>
    public static byte[]? Banner(IReadOnlyList<byte[]> sleeves)
    {
        if (sleeves.Count < Least)
            return null;

        var decoded = new List<SKImage>(sleeves.Count);
        try
        {
            foreach (var bytes in sleeves)
            {
                if (decoded.Count == Cells)
                    break;
                if (SKImage.FromEncodedData(bytes) is { Width: > 0, Height: > 0 } sleeve)
                    decoded.Add(sleeve);
            }

            if (decoded.Count < Least)
                return null;

            var info = new SKImageInfo(Columns * Cell, Rows * Cell, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;

            // The app's own dark colour is the background of the grid. It shows only where a cover does
            // not decode. The loop below leaves that cell empty, and does not move the other covers up.
            // The grid keeps the shelf's own order, and closing the gap would change that order.
            canvas.Clear(new SKColor(0x11, 0x11, 0x11, 0xFF));

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            for (var i = 0; i < Cells; i++)
            {
                // The covers repeat round the grid. So a shelf with five covers fills all twelve cells
                // with those five again. A repeated pattern looks deliberate. Five covers and seven
                // empty cells look like a picture that failed to load.
                var sleeve = decoded[i % decoded.Count];

                var cell = SKRect.Create((i % Columns) * Cell, (i / Columns) * Cell, Cell, Cell);
                canvas.DrawImage(sleeve, Square(sleeve), cell, sampling);
            }

            // And the light from the top left. It stops twelve squares looking like a contact sheet. A
            // grid drawn flat is only a grid. A grid with a gradient across it looks like a photograph
            // of a grid, which is what a band at the top of a page should look like.
            using (var fall = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(info.Width * 0.35f, info.Height),
                    [new SKColor(0xFF, 0xFF, 0xFF, 0x1A), new SKColor(0, 0, 0, 0x59)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp),
            })
                canvas.DrawRect(SKRect.Create(info.Width, info.Height), fall);

            canvas.Flush();

            using var drawn = surface.Snapshot();
            using var encoded = drawn.Encode(SKEncodedImageFormat.Jpeg, 88);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;   // Skia could not draw; the caller keeps the figure it already has
        }
        finally
        {
            foreach (var sleeve in decoded)
                sleeve.Dispose();
        }
    }

    /// <summary>The middle square of a cover, for a cell that is square when the sleeve may not be. The
    /// image is cropped, not squashed. Most covers are square, and are not changed. The few that are
    /// not square are photographs of a record, not the record itself.</summary>
    private static SKRect Square(SKImage sleeve)
    {
        var edge = Math.Min(sleeve.Width, sleeve.Height);
        return SKRect.Create((sleeve.Width - edge) / 2f, (sleeve.Height - edge) / 2f, edge, edge);
    }
}
