using RPM.Core.Library;
using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Puts the mood's colour and the app's mark on a generated playlist's photograph.
///
/// <para><b>The photograph is shipped, not searched for.</b> See <see cref="Presentation.Common.PlaylistPhotos"/>,
/// which holds the photographs the build ships. Nothing is searched for at run time. A search service needs a key
/// that is not in this repository, so the covers would exist on only one machine. The choice is made once, in
/// <c>scripts/playlist-photos.json</c>, and every install draws the same covers.</para>
///
/// <para><b>An improvement on the drawn cover, never a replacement for it.</b> Every path out of here that is not a
/// picture returns null. The caller then draws the figure it would have drawn anyway. This happens when the list has
/// no photograph, when the file is missing, when the bytes will not decode, or when a surface could not be made.
/// Most generated lists have no photograph. For them, null is the normal answer, not a failure.</para>
///
/// <para><b>There is no setting.</b> A list wears its photograph wherever it has one, the same way a genre page wears
/// the photograph at its head. Nobody is asked first.</para>
///
/// <para><b>Nothing is kept.</b> There is no cache, because there is nothing to cache. The picture is a file already
/// on disk, and this adds two draws to it. A cover is the same bytes on every machine and in every session, the same
/// as the drawn figures.</para>
/// </summary>
internal static class GeneratedCoverService
{
    /// <summary>The square everything is drawn at. It matches <c>PlaylistArt.Edge</c> and the size the shipped
    /// photographs are cut to.</summary>
    private const int Edge = 640;

    /// <summary>
    /// The picture for one generated list, or null to draw the figure instead.
    /// </summary>
    /// <param name="id">Which list. It decides which photograph, if any, the list wears.</param>
    /// <param name="colour">The mood's own colour. It is laid over the picture as a hue. See <see cref="Framed"/>.</param>
    /// <param name="ct">Cancels the file read.</param>
    public static async Task<byte[]?> CoverAsync(
        SmartPlaylistId id, Windows.UI.Color colour, CancellationToken ct)
    {
        if (await Presentation.Common.PlaylistPhotos.BytesAsync(id, ct) is not { Length: > 0 } photo)
            return null;

        // Done on a worker thread, not here. This is a decode, two draws and an encode at 640 pixels. The caller is
        // composing a cover for a row that is being realised.
        return await Task.Run(() => Compose(photo, colour), ct);
    }

    /// <summary>
    /// One photograph with the mood's colour and the app's mark on it, at cover size.
    /// </summary>
    /// <remarks>
    /// <b>Split out from <see cref="CoverAsync"/> so that a test can pass it bytes.</b> There is a stronger reason
    /// too. The earlier method asked the host whether a setting was on. So it returned null in every test and under
    /// every harness, and nothing exercised the drawing. The setting is gone, and <see cref="CoverAsync"/> can now be
    /// reached from tests. That is how <c>GeneratedCoverTests</c> runs it. This method stays because bytes that are
    /// not a picture are a case no shipped file can produce on demand. It also covers the case where the decode throws.
    /// </remarks>
    /// <returns>Null where the bytes will not decode or a surface could not be made. That leaves the caller drawing the
    /// figure it would have drawn anyway.</returns>
    internal static byte[]? Compose(byte[] photo, Windows.UI.Color colour)
    {
        SKBitmap? source = null;
        try
        {
            // SKBitmap.Decode THROWS on bytes it cannot make a codec from. It throws an ArgumentNullException, from a
            // codec it did not build, instead of returning null. So the null check below does not guard against that.
            // Bytes from the disk can be bad, for example from a half-copied install or a truncated asset. Without this
            // guard, one bad file would throw out of a Task.Run in front of a row being realised, instead of the list
            // drawing its figure.
            source = SKBitmap.Decode(photo);
            return source is { Width: > 0, Height: > 0 }
                ? Framed(source, new SKColor(colour.R, colour.G, colour.B))
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>
    /// How much of the mood colour is laid over the picture.
    ///
    /// <para>Enough to make a shelf of covers look like one app. Not enough to make them look like one cover. It is
    /// applied as a HUE, not as a film of paint. The light and shade of the picture stay, and only its colour is
    /// replaced. So a photograph and a drawn figure look related, and neither looks muddy.</para>
    /// </summary>
    private const float TintStrength = 0.72f;

    /// <summary>
    /// The picture scaled up until it fills the square, wearing the mood colour.
    ///
    /// <para><b>Filled, not fitted.</b> Cropping the middle square loses the ends of every landscape picture.
    /// Fitting the whole picture into the square, and filling the rest with a blurred enlargement of itself, loses
    /// nothing. But it reads as bars. A soft margin around a small picture looks like a letterbox however it is made,
    /// and at cover size that is what a viewer sees first. So the picture is scaled until the square is covered, and
    /// whatever runs past the edges is cut off. A cover is a square, so something has to give. A cropped edge costs
    /// less than a frame around everything.</para>
    ///
    /// <para>The shipped photographs are already square. <c>gen_playlist_photos.py</c> cuts them to the same centred
    /// square at the same size. So for those, this scales one to one and crops nothing. The arithmetic stays because
    /// a picture of any other shape must still work. It is what makes it safe to add an entry to the JSON by hand.</para>
    ///
    /// <para><b>The colour is the app's own.</b> <paramref name="tint"/> is the colour each mood uses everywhere else:
    /// blue for the calm list, red for the loud one, indigo for sleep. It is laid over the picture as a hue, so the
    /// light and shade of the photograph survive and only the colour changes. That is what puts a photograph and a
    /// drawn figure on one shelf.</para>
    /// </summary>
    private static byte[]? Framed(SKBitmap source, SKColor tint)
    {
        var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
            return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        using var image = SKImage.FromBitmap(source);

        // Scaled until the SHORT side reaches the square. Both sides then cover it, and the long side overhangs.
        // Centred, because nothing here knows where the subject of a picture is. The middle is wrong less often
        // than any corner.
        var fill = Math.Max((float)Edge / source.Width, (float)Edge / source.Height);
        canvas.DrawImage(
            image, SKRect.Create(source.Width, source.Height),
            Centred(source.Width * fill, source.Height * fill),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        // SKBlendMode.Color takes hue and saturation from the paint, and lightness from what is under it. That keeps
        // a picture a picture, rather than turning it into a coloured rectangle.
        using (var wash = new SKPaint
        {
            Color = tint.WithAlpha((byte)(TintStrength * 255)),
            BlendMode = SKBlendMode.Color,
        })
            canvas.DrawRect(SKRect.Create(Edge, Edge), wash);

        // The app's mark goes last, over everything. It is a label on the picture, not part of it. It is drawn in
        // the same place, and for the same reason, as the advisory lockup on a drawn cover. It comes after the wash,
        // which gives the background the mood's hue. The mark is drawn in the opposite colour to that hue. See
        // PlaylistMark.
        canvas.Flush();
        Presentation.Common.PlaylistMark.Stamp(surface, SKRect.Create(Edge, Edge), tint);

        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data?.ToArray();
    }

    /// <summary>A box of this size, centred on the square. A box bigger than the square is the ordinary case,
    /// because filling the square means exactly that.</summary>
    private static SKRect Centred(float width, float height) =>
        SKRect.Create((Edge - width) / 2f, (Edge - height) / 2f, width, height);
}
