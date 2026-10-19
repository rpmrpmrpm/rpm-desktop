using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Converters;

/// <summary>
/// The made picture a record wears where it has no cover: bind a seed, get a brush.
///
/// <para><b>The binding is to a seed, not to the folder.</b> The surface's <c>FigureSeed</c> names the
/// record only after the loader has found that it has no artwork anywhere. Until then it is null, and
/// it stays null for a record that has a cover. If the folder were bound instead, the converter would
/// run for every card on the wall as soon as the card is realized. The <c>Visibility</c> next to it
/// would then hide the figure for records with a cover, but the picture would already have been drawn.
/// Scrolling down a collection would paint and blur a figure for every tile, and nobody would see most
/// of them.</para>
///
/// <para><b>A converter, not a property on four models.</b> The figure depends on ONE thing: the
/// record's folder. Every surface that draws a cover already binds to that folder. Adding a picture
/// property and a flag to the wall's card, the list entry, the band in the drawer, and the record on
/// the artist page would repeat the same idea four times. Each one would need a loader that fills it
/// in, and any of the four could be forgotten.</para>
///
/// <para><b>It does not change <c>HasCover</c>.</b> That property still means only that the record has
/// no artwork. Code that acts on it, such as the prompt to add artwork, the editor and the importer,
/// sees no difference. The figure is only a placeholder.</para>
///
/// <para><b>Drawn on a worker thread, and filled in when ready.</b> The brush is returned empty, and
/// its picture is written a moment later. An ImageBrush is a DependencyObject, so that write repaints
/// whatever uses the brush. If the figure were made on the caller's thread, each record with no cover
/// would need a blur inside the layout pass. On a wall of such records, that is a large amount of work
/// at once.</para>
/// </summary>
public sealed partial class SleeveFigureConverter : IValueConverter
{
    /// <summary>One brush per record, kept for the whole run. The wall reuses its containers as it scrolls,
    /// so the same folder is converted again each time its tile comes back on screen. Without this cache,
    /// each conversion would draw and decode the picture again, although it cannot change.</summary>
    private static readonly Dictionary<string, ImageBrush> Kept = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, string language)
        => For(value as string);

    /// <summary>The same brush, requested from code instead of from a binding. The album page uses this.
    /// Its cover is a named square that <c>LoadCover</c> fills in, and it is not bound. A second drawing
    /// of the same figure there would look different from the wall tile the reader just came from. Keeping
    /// them the same is the purpose of the figure.</summary>
    public static ImageBrush For(string? identity)
    {
        if (identity is not { Length: > 0 } folder)
            return new ImageBrush();

        lock (Kept)
        {
            if (Kept.TryGetValue(folder, out var kept))
                return kept;

            // UniformToFill because the shapes a cover is drawn into are not all square. The drawer's
            // and the wall tile's are square, and so is the artist page's. A list row's is not. A figure
            // has no subject that cropping could cut off.
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            Kept[folder] = brush;

            _ = FillAsync(brush, folder);
            return brush;
        }
    }

    /// <summary>
    /// The number of figures that can be made at the same time.
    ///
    /// <para>When no record on a shelf has tags, a whole screen of tiles is realized in one pass. Each
    /// tile asks for a picture. That picture is a Skia paint and a blur, or a file read once
    /// <see cref="Services.Art.FigureCache"/> has it. Without a limit, every CPU core would be busy on
    /// artwork during scrolling, which is when the scroller needs the cores most. Half the cores draw
    /// figures and the rest keep drawing the wall. A figure is only a placeholder, so it can arrive one
    /// frame late.</para>
    /// </summary>
    private static readonly SemaphoreSlim Painting = new(Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>Reads the figure from disk, or draws it on a worker thread, and passes it to the brush.
    /// Failures are ignored. The brush then stays empty, and the cover's plate shows behind it.</summary>
    private static async Task FillAsync(ImageBrush brush, string folder)
    {
        try
        {
            byte[]? figure;
            await Painting.WaitAsync();
            try
            {
                figure = await Task.Run(() => Services.Art.FigureCache.Sleeve(folder));
            }
            finally
            {
                Painting.Release();
            }

            if (figure is not { Length: > 0 })
                return;

            brush.ImageSource = await RemoteImage.DecodeAsync(figure);
        }
        catch
        {
            // No Skia, or no composition. The plate stays visible.
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
