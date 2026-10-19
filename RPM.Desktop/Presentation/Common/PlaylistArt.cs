using RPM.Core.Art;
using RPM.Core.Library;
using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The cover of a playlist. It is drawn, not found. The wash of the page that it is opened on is drawn too.
///
/// <para><b>Why a playlist needs one at all.</b> A record has a sleeve. A playlist has nothing. The first
/// track's cover would say "this playlist is that album", about a list whose whole point is that it is not
/// one. So it gets a picture of its own. The picture is made from the only true material there is: the
/// records that the songs on it came from.</para>
///
/// <para><b>Two kinds, and they are drawn differently because they ARE different.</b> A list that somebody
/// made is a set of records. So it shows them, inset, standing on a blurred ground of their own colours. A
/// generated list is a QUERY. Nothing is in it until it is run, and its contents change with the month. A
/// quilt of four sleeves would claim a membership that does not hold. So a generated list gets the form,
/// and claims nothing.</para>
///
/// <para>The form does not carry the list's name. Every generated list is framed (<see
/// cref="CoverOverlay"/>), and the frame sets the name down its own band. Two names on one cover would make
/// one of them redundant.</para>
///
/// <para><b>Drawn once, off the thread, and handed over as bytes.</b> This is the same argument that <see
/// cref="SoftFigure"/> makes at length. What comes out is an ordinary JPEG that any surface can draw, at a
/// size that has nothing to do with the size it is shown at.</para>
/// </summary>
public static class PlaylistArt
{
    /// <summary> What one playlist is drawn as: the square at the head of it, and the picture that the
    /// whole page stands on.
    /// </summary>
    /// <param name="Cover">The square. It is a quilt of the list's records, inset on their colours. Or it
    /// is the form that a generated list's frame is composed over.</param>
    /// <param name="Wash">The SAME ground, blurred past being a picture, for the page behind the list. This
    /// is the arrangement that the artist page wears (see its PageWash). The records are not in it, and
    /// neither is the writing. What a page is read on has to be a texture, not a picture. Otherwise every
    /// line on top of it competes with a sleeve. It is null where the ground would not blur. The page then
    /// keeps whatever it had.</param>
    public readonly record struct Drawn(byte[]? Cover, byte[]? Wash)
    {
        /// <summary>Nothing could be drawn. Both surfaces keep what they show for a list with no picture: a
        /// made figure, and the pane's own background.</summary>
        public static readonly Drawn None = new(null, null);
    }

    /// <summary>The square that everything here is composed on. It is the app's own tile edge. So a
    /// playlist cover and a record's sleeve are the same picture size wherever the two are shown
    /// together.</summary>
    public const int Edge = 640;

    /// <summary>
    /// How far in from every edge the sleeves stand.
    ///
    /// <para>It is deliberately deep. The ground is not a border round a picture. It tells the viewer that
    /// this is a made cover, not an album's cover. A sleeve bled to the edges IS an album, at any size. The
    /// whole difference is that this one floats on something.</para>
    /// </summary>
    public const int Padding = 100;

    /// <summary>How many records the quilt holds. It is four, because two by two is the only arrangement
    /// that stays square at every size and reads as a set rather than as a grid.</summary>
    public const int Sleeves = 4;

    /// <summary>
    /// One artist's photograph, and the part of it that is worth showing.
    /// </summary>
    /// <param name="Picture">The photograph, encoded.</param>
    /// <param name="Window">Which pixels of it the strip should draw, in the photograph's own coordinates.
    /// <see cref="SaliencyCrop"/> chooses them from a saliency map and a face detector's boxes. It is null
    /// where neither model could answer, and the strip then falls back to a guess.
    ///
    /// <para>It is passed IN rather than worked out here. That is the point of the type. Choosing the
    /// window means decoding the picture, running two models, and scaling a map's coordinates back to the
    /// photograph's. None of that belongs in a drawing routine. The caller already does all of it, once per
    /// artist, behind its own memo.</para></param>
    public readonly record struct Framed(byte[] Picture, CropRect? Window);

    /// <summary>
    /// A list that somebody made: its records, inset, on a blurred ground of their colours.
    /// </summary>
    /// <param name="sleeves">The covers of the DISTINCT records on the list, best first. Each is already
    /// cut to something like a thumbnail. Four or more gives the quilt. One to three gives only that first
    /// record. Three squares and a hole look like a picture that failed to load. An empty list draws
    /// nothing, because a list whose records have no artwork has nothing to draw from.</param>
    /// <param name="identity">What the ground's arrangement is drawn from. The playlist's name is used. So
    /// the list is the same cover every time, on every machine.</param>
    public static Drawn Made(IReadOnlyList<byte[]> sleeves, string identity)
    {
        if (sleeves.Count == 0)
            return Drawn.None;

        var decoded = new List<SKImage>(Sleeves);
        try
        {
            foreach (var bytes in sleeves)
            {
                if (decoded.Count == Sleeves)
                    break;
                if (SKImage.FromEncodedData(bytes) is { Width: > 0, Height: > 0 } sleeve)
                    decoded.Add(sleeve);
            }

            if (decoded.Count == 0)
                return Drawn.None;

            if (Ink(sleeves, decoded.Count) is not { Count: > 0 } ink)
                return Drawn.None;

            // Painted ONCE and used twice. It is the border round the quilt, and, blurred, the ground that
            // the whole page stands on. Painting one arrangement twice would do the same work twice, for
            // two pictures that have to agree anyway.
            var ground = SoftFigure.Ground(ink, identity, Edge);

            var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return Drawn.None;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            // The ground first, whole, behind everything. If it will not draw, the cover still works. What
            // shows through the inset is the black above, which is the app's own dark.
            if (ground is { Length: > 0 })
            {
                using var painted = SKImage.FromEncodedData(ground);
                if (painted is not null)
                    canvas.DrawImage(painted, SKRect.Create(Edge, Edge), sampling);
            }

            var inner = new SKRect(Padding, Padding, Edge - Padding, Edge - Padding);

            // The lift off the ground. Without it, the sleeves look like a hole cut in the blur, not like
            // records lying on it. The inset would then mean nothing.
            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 0x66),
                ImageFilter = SKImageFilter.CreateBlur(18, 18, SKShaderTileMode.Decal),
            })
                canvas.DrawRoundRect(inner, Corner, Corner, shadow);

            // Clipped once, round the whole block. The quilt's four squares share the corners rather than
            // each rounding its own. That is what makes four records read as one cover.
            canvas.Save();
            using (var clip = new SKRoundRect(inner, Corner, Corner))
                canvas.ClipRoundRect(clip, antialias: true);

            if (decoded.Count >= Sleeves)
            {
                var half = inner.Width / 2f;
                for (var i = 0; i < Sleeves; i++)
                {
                    var cell = SKRect.Create(
                        inner.Left + ((i % 2) * half), inner.Top + ((i / 2) * half), half, half);
                    canvas.DrawImage(decoded[i], Square(decoded[i]), cell, sampling);
                }
            }
            else
            {
                canvas.DrawImage(decoded[0], Square(decoded[0]), inner, sampling);
            }

            canvas.Restore();
            canvas.Flush();

            return new Drawn(Encoded(surface, SmoothQuality), Blurred(ground));
        }
        catch
        {
            return Drawn.None;   // No Skia to draw with. Both surfaces keep what they had.
        }
        finally
        {
            foreach (var sleeve in decoded)
                sleeve.Dispose();
        }
    }

    /// <summary>
    /// A generated list: the form, with nothing written on it.
    ///
    /// <para><b>The name is not painted across this.</b> Generated lists can be told apart without it. Each
    /// mood picks its own colour for what that colour means. Each family differs. And every row carries its
    /// own title beside the picture. A caption baked into the cover would repeat the title that the row
    /// already shows.</para>
    ///
    /// <para>The page's wash is the same form, blurred.</para>
    /// </summary>
    /// <param name="style">Which of the eight families this list wears. It is the same set that a user
    /// picks from for their own playlists. So the generated lists belong to the same shelf, rather than to
    /// a form of their own.</param>
    /// <param name="seed">The arrangement AND the colourway. It is chosen, not hashed off the name. What a
    /// generated list is about is known, and its colour can say it (see <c>SmartPlaylistFace.Cover</c>). A
    /// hash of the name would put "Calm" in whatever hue its letters happened to land on. A set of mood
    /// covers must not do that.</param>
    /// <param name="mood">The colour of the list that this is a cover for. The app's mark is drawn in its
    /// complement. It is stated at every call. This overload is only ever used for a generated list. If it
    /// could omit the colour, a list of the app's own would wear no mark, while the list beside it wore
    /// one. See <see cref="PlaylistMark"/>.</param>
    public static Drawn Generated(int style, uint seed, Color mood)
    {
        try
        {
            if (FigureBytes(style, seed) is not { Length: > 0 } figure)
                return Drawn.None;

            var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return Drawn.None;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using (var painted = SKImage.FromEncodedData(figure))
            {
                if (painted is null)
                    return Drawn.None;
                canvas.DrawImage(painted, SKRect.Create(Edge, Edge),
                                 new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            canvas.Flush();

            // The app's mark, over the finished figure. It goes on last, because what decides its lightness
            // is the surface it will actually sit on. It is also a label stuck to the picture, not part of
            // the composition. That is where Figure puts the advisory lockup too.
            PlaylistMark.Stamp(surface, SKRect.Create(Edge, Edge), Skia(mood));
            canvas.Flush();

            // The WASH is taken from the figure as drawn, without the mark. It is a blur of the picture for
            // a page to stand on. A mark blurred into it would be a smear that nobody put there.
            return new Drawn(Encoded(surface, SmoothQuality), Blurred(figure));
        }
        catch
        {
            return Drawn.None;
        }
    }

    /// <summary>How many pictures a generated cover can be drawn as. The picker offers exactly this many
    /// tiles: one per family, all in one colourway. It does not offer a page of every family times every
    /// roll.</summary>
    public const int Styles = PlaylistFigure.Families;

    /// <summary> A cover that the user picked out of a set, rather than one drawn from the list's contents.
    /// It is one of eight soft pictures, in colours from the app's own palette.
    ///
    /// <para><b>Why a playlist may want one.</b> The quilt says what is in the list. That is right for a
    /// list that is about its records, and wrong for a list that is about an occasion. Four sleeves are a
    /// poor picture of "Sunday morning", and no arrangement of them gets better.</para>
    ///
    /// <para>The two numbers are the whole of the picture. So the same pair gives the same cover on every
    /// machine, and after every restart. Nothing is read from the disk, and nothing is asked of the
    /// network.</para>
    ///
    /// <para><b>No ground under it.</b> It does not paint <see cref="SoftFigure.Ground"/> first and lay the
    /// geometry over the top. The families draw their own field (see <see cref="PlaylistFigure"/>). A
    /// second soft form beneath a soft picture is only a colour that it has to fight. That leaves the
    /// page's wash with nothing of its own to be made from. So it is made from the cover, blurred past
    /// being a picture, in the same way as a record's own sleeve.</para>
    /// </summary>
    /// <param name="style">Which family, wrapped into range.</param>
    /// <param name="seed">The roll that arranges it, and which entries of the palette ring it is drawn
    /// in.</param>
    /// <param name="mark">Whether the advisory lockup is stamped in the corner. It is stated at every call
    /// and never defaulted. See <see cref="CoverMark"/>, where the reason is written out.</param>
    /// <param name="mood">The colour of the list that this is a cover FOR. It is null where the cover is
    /// for something that is not a generated list. It is not the colour that the figure is drawn in. The
    /// colourway decides that from the seed. It is the colour that the app's mark is complemented from.
    ///
    /// <para>It is stated at every call and never defaulted, by the same rule that <paramref name="mark"/>
    /// follows. Null means "this is not a generated list, do not stamp it". A default would put the app's
    /// mark on a cover that was drawn for a playlist somebody made and named.</para></param>
    public static Drawn Figure(int style, uint seed, CoverMark mark, Color? mood)
    {
        try
        {
            // The app's own ring, whole and in its own order. Which three of them this cover wears is
            // decided by the colourway alone. See PlaylistFigure.Colourway.
            var palette = RpmPalette.Colours
                                    .Select(colour => new SKColor(colour.R, colour.G, colour.B))
                                    .ToList();

            var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return Drawn.None;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Bled to the edges, unlike the quilt. The quilt's inset stops four sleeves reading as one
            // album's. There is no album here to be mistaken for.
            var inner = SKRect.Create(Edge, Edge);

            canvas.Save();
            using (var clip = new SKRoundRect(inner, Corner, Corner))
                canvas.ClipRoundRect(clip, antialias: true);

            var painted = PlaylistFigure.Paint(canvas, inner, palette, style, seed);

            // Inside the clip, so that a mark in the corner is cut by the same rounding as the picture. It
            // goes on last, over everything. It is not part of the composition. It is a label stuck to it.
            if (painted && mark is CoverMark.Explicit)
                Advisory(canvas, inner);

            canvas.Restore();
            canvas.Flush();

            // And the app's mark, for a cover that IS one of the app's own lists. It is drawn outside the
            // clip's Save and Restore, because it is centred and never reaches the rounding. It is drawn
            // after the flush, so that its lightness is measured against the finished figure.
            if (painted && mood is { } colour)
                PlaylistMark.Stamp(surface, inner, Skia(colour));

            canvas.Flush();

            // The palette had not loaded. A black rounded square is not a cover. A surface that keeps what
            // it had is a better answer than one that shows the app's own dark as a picture.
            if (!painted)
                return Drawn.None;

            var cover = Encoded(surface, mark is CoverMark.Explicit ? MarkedQuality : SmoothQuality);
            return new Drawn(cover, Blurred(cover));
        }
        catch
        {
            return Drawn.None;   // No Skia to draw with. The caller keeps what it had.
        }
    }

    /// <summary>
    /// The parental advisory lockup, in the bottom-right corner of a made cover.
    ///
    /// <para>The picture is in <see cref="ParentalAdvisory"/>, which holds it, says where it came from, and
    /// gives the size it is stamped at. This method does not decide what the mark looks like or how big it
    /// is. It decides only which corner, and how far from the edges.</para>
    ///
    /// <para>The size is one stated value, not a fraction of anything. That is the point. The mark is three
    /// lines of type, and the smallest line is a twelfth of the mark's height. A mark that scaled with the
    /// cover would be a different piece of type on every surface, and unreadable on most of them. So it is
    /// one size, stated once, and stamped exactly. That is 114 by 72 on a cover composed at 640.</para>
    ///
    /// <para>If the picture will not decode, no mark is drawn at all. It is not drawn as a box, or as half
    /// of one. A cover that is missing its mark is something that somebody can see and fix. A broken lockup
    /// is a claim made badly.</para>
    /// </summary>
    private static void Advisory(SKCanvas canvas, SKRect box)
    {
        if (ParentalAdvisory.Mark is not { } mark)
            return;

        var edge = box.Width;
        var margin = edge * MarkMargin;

        // The stated size, where the cover is composed at 640. It is scaled if the cover is a different
        // size. Mitchell's cubic is used, because the picture handed in is smaller than the stamp and has
        // to be scaled up to it. The result is soft rather than blocky. That is the better of the two
        // outcomes.
        var scale = edge / Edge;
        var wide = ParentalAdvisory.Width * scale;
        var tall = ParentalAdvisory.Height * scale;

        canvas.DrawImage(
            mark,
            SKRect.Create(box.Right - margin - wide, box.Bottom - margin - tall, wide, tall),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>How far the mark's corner stands off the cover's edges, as a fraction of the cover. Its
    /// size is not a fraction of anything. See <see cref="Advisory"/>.</summary>
    private const float MarkMargin = 0.05f;

    /// <summary> A picture off somebody's own disk, cut to a square around the centre. It is cut for the
    /// space it is about to sit in.
    ///
    /// <para>It is cropped rather than kept in its own shape. Every square that this app draws a cover in
    /// (the well here, the wall, and the header) crops with <c>UniformToFill</c>. Which part of a landscape
    /// or portrait photograph is kept should be decided once, by whoever chose the picture. It should not
    /// be a different guess, made fresh by each surface that draws it later.</para>
    ///
    /// <para>It is scaled down only when it is bigger than <paramref name="maxEdge"/>. It is never scaled
    /// up. Only a picture that is ALREADY square and no bigger than the cap comes back untouched, with the
    /// same bytes. Anything else is cropped and re-encoded, even where no scaling is needed. A centred
    /// square is the only shape that this cover has room for, and there is no way to hand one back without
    /// drawing it.</para>
    ///
    /// <para>The same fallback as everything else in this file applies. If anything goes wrong, the bytes
    /// that were passed in come back unchanged. The caller then keeps the picture that the user actually
    /// chose, rather than losing it to a failed crop.</para>
    /// </summary>
    public static byte[] Bleed(byte[] bytes, int maxEdge)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            var edge = bitmap is null ? 0 : Math.Min(bitmap.Width, bitmap.Height);
            if (bitmap is null || edge <= 0)
                return bytes;

            // Already square and no bigger than the cap. A crop or a re-encode here could only lose quality.
            if (bitmap.Width == bitmap.Height && edge <= maxEdge)
                return bytes;

            using var image = SKImage.FromBitmap(bitmap);
            var crop = SKRect.Create((bitmap.Width - edge) / 2f, (bitmap.Height - edge) / 2f, edge, edge);

            var target = Math.Min(edge, maxEdge);
            var info = new SKImageInfo(target, target, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return bytes;

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            surface.Canvas.DrawImage(image, crop, SKRect.Create(target, target), sampling);
            surface.Canvas.Flush();

            using var snapped = surface.Snapshot();
            var alpha = bitmap.AlphaType != SKAlphaType.Opaque;
            using var encoded = snapped.Encode(
                alpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, alpha ? 100 : 90);
            return encoded is null || encoded.Size == 0 ? bytes : encoded.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary> One photograph, cut to the shape that a surface wants, around whatever somebody decided
    /// was worth showing.
    ///
    /// <para>This is the genres wall's route to the framing that the strip's panels get. It takes the same
    /// <see cref="Framed"/> that the models produced, and it applies the same <see cref="Fill"/> rule. So a
    /// face on a genre card is framed by exactly the judgement that framed the same face on a playlist's
    /// header. There is one answer about one photograph, and it is used twice.</para>
    ///
    /// <para>The picture is cut rather than drawn into a canvas of the target's own size. What comes back
    /// is the photograph's own pixels, at the resolution it arrived in, and the surface does the scaling. A
    /// card on a wall is a different size on every pane and at every display scale. A picture baked to one
    /// of those sizes would be resampled twice.</para>
    /// </summary>
    /// <param name="framed">The photograph, and the window somebody chose in it, if anybody did.</param>
    /// <param name="aspect">How wide the surface is for every one of its height.</param>
    /// <returns>The cut, encoded. Or the picture as it came, where it cannot be decoded. A photograph
    /// framed by nobody is still a photograph.</returns>
    public static byte[] Cut(Framed framed, double aspect)
    {
        try
        {
            using var picture = SKImage.FromEncodedData(framed.Picture);
            if (picture is null || picture.Width < 8 || picture.Height < 8)
                return framed.Picture;

            var window = Fill(picture, SKRect.Create((float)aspect, 1f), framed.Window);
            var rect = SKRectI.Round(window);
            if (rect.Width < 8 || rect.Height < 8)
                return framed.Picture;

            using var cut = picture.Subset(rect);
            if (cut is null)
                return framed.Picture;

            using var data = cut.Encode(SKEncodedImageFormat.Png, 92);
            return data?.ToArray() ?? framed.Picture;
        }
        catch
        {
            // A picture that will not cut is a picture that the surface can still show whole.
            return framed.Picture;
        }
    }

    /// <summary>
    /// The part of a picture that fills a panel.
    ///
    /// <para><b>Whatever <see cref="Framed.Window"/> names is used first.</b> That is where somebody has
    /// looked at the photo and said what matters. It is the saliency crop, which had a face detector's
    /// answer to work from. It was chosen for this panel's shape, so squaring it up below usually does
    /// nothing.</para>
    ///
    /// <para><b>Otherwise it is a guess.</b> The guess is the middle across, and the top third down. The
    /// guess is only right by luck. A hooded jacket has no shoulders to find. A seated singer has a
    /// silhouette twice as tall as her head. That is the problem the detector exists to solve. So this path
    /// is a fallback for a missing model, and not the ordinary path.</para>
    ///
    /// <para>The crop is made to the panel's shape, not squashed into it. A face squashed sideways is the
    /// one thing here that would look like a fault, rather than a deliberate treatment.</para>
    /// </summary>
    private static SKRect Fill(SKImage picture, SKRect into, CropRect? window)
    {
        var chosen = window is { Width: > 0, Height: > 0 } named
            ? Inside(named, picture)
            : SKRect.Create(picture.Width, picture.Height);

        var wanted = into.Width / into.Height;
        var has = chosen.Width / chosen.Height;

        if (has > wanted)
        {
            var width = chosen.Height * wanted;
            return SKRect.Create(chosen.Left + ((chosen.Width - width) / 2f), chosen.Top,
                                 width, chosen.Height);
        }

        var height = chosen.Width / wanted;

        // Down a CHOSEN window, from the middle. It was picked for this shape, and its edges are already
        // where somebody wanted them. Down the whole picture, from the top third, for the reason above.
        // With nothing to go on, the safer error is upwards.
        var down = window is null ? 3f : 2f;
        return SKRect.Create(chosen.Left, chosen.Top + ((chosen.Height - height) / down),
                             chosen.Width, height);
    }

    /// <summary>A chosen window, kept inside the picture that it is a window on. The crop is worked out on
    /// a map of the photograph, not on the photograph itself. Scaling back up can put an edge a pixel or
    /// two past where there is anything to draw.</summary>
    private static SKRect Inside(CropRect window, SKImage picture)
    {
        var left = Math.Clamp(window.X, 0, picture.Width - 1);
        var top = Math.Clamp(window.Y, 0, picture.Height - 1);
        return SKRect.Create(
            left, top,
            Math.Min(window.Width, picture.Width - left),
            Math.Min(window.Height, picture.Height - top));
    }

    /// <summary>
    /// The colours a list's ground is drawn in: one tone from each of its records.
    ///
    /// <para>The tones come from the very sleeves that are about to cover most of the ground. That is what
    /// makes the border read as this playlist's own, and not as decoration. A list of four records gives a
    /// ground of four colours that meet. A single record gives a single colour. One colour is the flat
    /// rectangle that the figure exists to replace. So the other two readings are asked for as well. They
    /// disagree with the dominant colour on exactly the sleeves that are worth drawing from.</para>
    /// </summary>
    private static List<Color> Ink(IReadOnlyList<byte[]> sleeves, int records)
    {
        var ink = new List<Color>(Sleeves + 2);

        foreach (var bytes in sleeves.Take(records))
            if (CoverColor.Dominant(bytes) is { } dominant)
                ink.Add(Full(dominant));

        if (ink.Count < 2)
        {
            if (CoverColor.Highlight(sleeves[0]) is { } highlight) ink.Add(Full(highlight));
            if (CoverColor.Average(sleeves[0]) is { } mean) ink.Add(Full(mean));
        }

        return ink;
    }

    /// <summary>The app's own colour, as Skia writes it. Alpha is dropped. Every caller here means a hue,
    /// not a film of paint. How solid the mark is drawn is PlaylistMark's own number.</summary>
    private static SKColor Skia(Color colour) => new(colour.R, colour.G, colour.B);

    /// <summary>
    /// One of the eight families, painted on its own, as encoded bytes.
    ///
    /// <para>Bytes are used rather than a canvas, because <see cref="Generated"/> needs the picture twice:
    /// once as the cover itself, and once for the page's wash. Painting it twice would give the same
    /// picture from the same seed. That would only cost time.</para>
    /// </summary>
    private static byte[]? FigureBytes(int style, uint seed)
    {
        var palette = RpmPalette.Colours
                                .Select(colour => new SKColor(colour.R, colour.G, colour.B))
                                .ToList();

        var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
            return null;

        surface.Canvas.Clear(SKColors.Black);
        if (!PlaylistFigure.Paint(surface.Canvas, SKRect.Create(Edge, Edge), palette, style, seed))
            return null;

        surface.Canvas.Flush();
        return Encoded(surface, SmoothQuality);
    }

    /// <summary>The ground, blurred past being a picture, for the page to stand on. This is the app's own
    /// wash. It is cut small and blurred hard, on a worker, never on the surface (see ArtScaler.Wash, where
    /// the reason is written out). It is null where the ground would not blur, exactly as a photograph's
    /// wash is.</summary>
    private static byte[]? Blurred(byte[]? ground)
        => ground is { Length: > 0 } ? Services.Art.ArtScaler.Wash(ground) : null;

    /// <summary> A colour at full strength. Its hue and saturation are kept, and its brightest channel is
    /// taken to the top.
    ///
    /// <para><b>Why this is here and not in <see cref="SoftFigure"/>.</b> Every other figure in the app is
    /// a background with something laid over it. That might be a photograph, a colour veil, or a sheet of
    /// black. So it is held back on purpose (see the figure's own <c>Lit</c>), and it comes out bright
    /// enough once the layer above it is on. This ground has nothing over it. What is seen of it is the
    /// hundred points round the edge, on their own. Held back in the usual way, that border would come out
    /// as one dark smudge rather than the record's colour. So the colours are taken to full strength BEFORE
    /// the figure dims them. What is left after it dims is the tone that the sleeve actually is.</para>
    ///
    /// <para>The brightest channel is used, rather than all three. Scaling every channel by the same amount
    /// keeps the hue. Clamping each channel separately would walk the colour toward white.</para>
    /// </summary>
    private static Color Full(Color colour)
    {
        var top = Math.Max(colour.R, Math.Max(colour.G, colour.B));
        if (top is 0 or 0xFF)
            return colour;

        var lift = 255.0 / top;
        return Color.FromArgb(
            0xFF,
            (byte)Math.Min(255, colour.R * lift),
            (byte)Math.Min(255, colour.G * lift),
            (byte)Math.Min(255, colour.B * lift));
    }

    /// <summary>The middle square of a cover. It is for a cell that is square, and a sleeve that may not
    /// be. It is cropped rather than squashed. Most covers are square already, and this does nothing to
    /// them.</summary>
    private static SKRect Square(SKImage sleeve)
    {
        var edge = Math.Min(sleeve.Width, sleeve.Height);
        return SKRect.Create((sleeve.Width - edge) / 2f, (sleeve.Height - edge) / 2f, edge, edge);
    }

    /// <param name="quality">What the JPEG is written at. It is 90 for a picture made of gradients, which
    /// is what all of these are. It is much higher for a picture carrying the advisory mark. A codec built
    /// for photographs spends its bits on smooth falls, and it rings badly around a hard black-and-white
    /// edge. The whole mark is made of hard black-and-white edges. At 90, the ringing round the letters is
    /// the first thing anybody notices. The cost is a file two to three times the size, on the one cover in
    /// a hundred that wears a mark.</param>
    private static byte[]? Encoded(SKSurface surface, int quality)
    {
        using var drawn = surface.Snapshot();
        using var encoded = drawn.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
    }

    /// <summary>What a cover with no hard edges in it is written at, and what one carrying the mark is
    /// written at. See <see cref="Encoded"/>.</summary>
    private const int SmoothQuality = 90;
    private const int MarkedQuality = 97;

    /// <summary>How round the block of sleeves is. It is the app's own radius of 6 at card size, scaled to
    /// the size that this is composed at. A cover is drawn at 640 and shown at anything from 52 pixels up.
    /// A radius in raw pixels would be a hairline at one end and a pill at the other.</summary>
    private const float Corner = Edge * 0.028f;
}
