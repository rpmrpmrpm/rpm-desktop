using RPM.Core.Art;
using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Cuts a fetched scan down to the size it is embedded at.
///
/// <para>The pixels are handled here, not in RPM.Core, because Skia is an app dependency. The decision,
/// whether to scale at all and to what size, is made by <see cref="EmbeddedArt"/>. That class can be
/// reasoned about without a decoder. This class is the adapter between the two.</para>
///
/// <para>Every failure returns the original bytes. A cover that cannot be decoded is still a cover the
/// user chose, and embedding it whole is a better result than embedding nothing.</para>
/// </summary>
public static class SleeveScaler
{
    /// <summary>Pairs a fetched scan with the copy to embed: the same picture when it already fits, or a
    /// JPEG cut to <see cref="EmbeddedArt.MaxEdge"/> when it does not.</summary>
    public static Sleeve ToSleeve(AlbumArt scan)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(scan.Bytes);
            if (bitmap is null || !EmbeddedArt.NeedsScaling(bitmap.Width, bitmap.Height))
                return Sleeve.Of(scan);

            var (width, height) = EmbeddedArt.Fit(bitmap.Width, bitmap.Height);
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var scaled = bitmap.Resize(
                info, ArtScaler.SamplingFor(bitmap.Width, width));
            if (scaled is null)
                return Sleeve.Of(scan);

            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, EmbeddedArt.JpegQuality);
            if (encoded is null || encoded.Size == 0)
                return Sleeve.Of(scan);

            var bytes = encoded.ToArray();

            // A re-encode is not always smaller. A large flat sleeve can come out heavier at this quality
            // than the archive's own file. Keeping the smaller of the two means that scaling can only
            // help, and the picture looks the same either way.
            return bytes.Length >= scan.Bytes.Length
                ? Sleeve.Of(scan)
                : new Sleeve(scan, new AlbumArt(bytes, "image/jpeg"));
        }
        catch
        {
            return Sleeve.Of(scan);
        }
    }
}
