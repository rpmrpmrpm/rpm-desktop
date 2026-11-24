using System.Numerics;
using RPM.Core.Library;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Which kind of playlist folder a folder is, for its cover. See <see cref="FolderFace"/>.</summary>
internal enum FolderKind
{
    /// <summary>A folder somebody made, or a folder of the app's lists of more than one kind.</summary>
    Plain,

    /// <summary>The folder of the moods.</summary>
    Moods,

    /// <summary>The folder of the decades.</summary>
    Decades,

    /// <summary>One mood's folder: that mood's lists, cut by genre.</summary>
    Mood,
}

/// <summary>
/// What a playlist folder's cover shows: which of the app's own folders it is, if any.
/// </summary>
/// <param name="Kind">Which kind of folder.</param>
/// <param name="Mood">The mood, on a <see cref="FolderKind.Mood"/> folder. Not read on the others.</param>
internal readonly record struct FolderFace(FolderKind Kind, SmartPlaylistKind Mood = default)
{
    public static FolderFace Plain => new(FolderKind.Plain);

    public static FolderFace Moods => new(FolderKind.Moods);

    public static FolderFace Decades => new(FolderKind.Decades);

    public static FolderFace OfMood(SmartPlaylistKind mood) => new(FolderKind.Mood, mood);

    /// <summary>The key the folder's photograph is filed under in <see cref="FolderPhotos.Files"/>, or null
    /// for a plain folder, which has none. The phones file them under the same keys.</summary>
    public string? PhotoKey => Kind switch
    {
        FolderKind.Moods => "moods",
        FolderKind.Decades => "decades",
        FolderKind.Mood => "mood:" + Mood,
        _ => null,
    };
}

/// <summary>
/// The cover of a playlist folder: a picture, with the folder frame drawn over it and the folder's name set
/// in the bar under the folder.
///
/// <para><b>A different drawing from a list's frame</b> (<see cref="CoverOverlay"/>), so a folder does not
/// look like one more list. <c>Assets/PlaylistOverlays/FolderFrame.png</c> is a black square with a window
/// cut out of it in the shape of the app's folder icon (round corners, a tab at the top left), a soft glow
/// round the window's edge, and a black bar under it. The iPhone and Android apps ship the same file and
/// set the name by the same numbers (<c>FolderFrameMetrics</c> there); change them together.</para>
///
/// <para><b>Inked in the folder's two colours</b>, as a list's frame is: the drawing's black in the border
/// colour, its white in the lettering colour, the glow running between them (<see
/// cref="CoverOverlay.Ramp"/>). The window stays transparent. The colours are the folder's look (<see
/// cref="LookOf"/>).</para>
///
/// <para><b>What shows through the window, best first.</b></para>
/// <list type="number">
/// <item>The picture somebody chose for the folder (<see cref="PlaylistCovers.ChosenAsync"/>), when it has
/// one.</item>
/// <item>One of the app's own folders (the moods, the decades, one mood): a photograph of its own
/// (<see cref="FolderPhotos"/>), washed in the folder's colour as a list's photograph is
/// (<c>GeneratedCoverService.Washed</c>). It is never one of the lists' photographs.</item>
/// <item>Otherwise a still room in the folder's colours (<see cref="CardRoomArt"/>), as on the phones.</item>
/// </list>
///
/// <para>The picture and the frame are composed into one square, so they come in together and the frame
/// never shows over an empty window. Everything here runs on a worker: it is a file read and a few draws at
/// <see cref="PlaylistArt.Edge"/>.</para>
/// </summary>
internal static class FolderCover
{
    /// <summary>The square the cover is composed at, the same as a list's cover.</summary>
    private const int Edge = PlaylistArt.Edge;

    // Where the name goes. Every number is a share of the square, measured on the 1000-pixel drawing. The
    // window runs from 82 to 714 down. The black bar under it runs from 715 to the bottom edge.

    /// <summary>The middle of the bar under the folder, down the square: halfway between 715 and 1000.</summary>
    private const float BarCentre = (715f + 1000f) / 2f / 1000f;

    /// <summary>How tall <see cref="Gauge"/>'s ink is, as a share of the square. This sets the type size, so
    /// every folder's name is set at one size.</summary>
    private const float NameCross = 0.095f;

    /// <summary>The widest a name may run: about as wide as the folder. A longer name is set smaller rather
    /// than cut.</summary>
    private const float NameRun = 0.84f;

    /// <summary>The string the size is measured on: an ascender and a descender, the tallest span Latin text
    /// takes. A list's frame measures the same string (see <c>CoverOverlay.Gauge</c>).</summary>
    private const string Gauge = "fg";

    /// <summary>The drawing's file, beside a list's frame.</summary>
    private const string Drawing = "FolderFrame.png";

    // The app's own three colours: its blue, its cyan light and its violet shade, the colours the room has
    // with no record in it (RoomPalette.Fallback). The folder of all the moods and every folder somebody
    // made wear them, because they hold no one mood.
    private static readonly SKColor AppHot = new(0x05, 0xC7, 0xFF);
    private static readonly SKColor AppMid = new(0x03, 0x71, 0xD0);
    private static readonly SKColor AppDeep = new(0x9E, 0x29, 0xB8);

    /// <summary>How far toward black the app's blue is taken for the border of its folders.</summary>
    private const float AppBorderLight = 0.25f;

    /// <summary>
    /// One folder's colours.
    /// </summary>
    /// <param name="Ink">The frame's border and its lettering.</param>
    /// <param name="Tint">The colour the photograph behind the window is washed in.</param>
    /// <param name="Room">The still room behind the window when there is no picture.</param>
    internal readonly record struct Look(CoverOverlay.FrameInk Ink, SKColor Tint, CoverRoom.ListRoom Room);

    /// <summary>
    /// The folder's colours.
    /// </summary>
    /// <remarks>
    /// One mood's folder wears the colours of that mood's list of the whole collection, so the folder and
    /// what is in it look alike. The folder of the decades wears the 1980s' list's colours. The folder of all
    /// the moods and every folder somebody made wear the app's own (<see cref="AppLook"/>). The phones choose
    /// the same colours (<c>FramedList.folder</c>).
    /// </remarks>
    /// <param name="name">The folder's name. A plain folder's room starts from it, so two folders do not
    /// share a pose.</param>
    internal static Look LookOf(FolderFace face, string name) => face.Kind switch
    {
        FolderKind.Decades => ListLook(new SmartPlaylistId(SmartPlaylistKind.Decade, null, 1980)),
        FolderKind.Mood => ListLook(SmartPlaylistId.WholeShelf(face.Mood)),
        _ => AppLook(name),
    };

    /// <summary>A generated list's own look, as its cover wears it.</summary>
    private static Look ListLook(SmartPlaylistId id)
    {
        var room = CoverRoom.Of(id);
        var tint = SmartPlaylistFace.Tint(id);
        return new Look(CoverOverlay.Ink(room), new SKColor(tint.R, tint.G, tint.B), room);
    }

    /// <summary>
    /// The app's own look: the border is its blue taken toward black, and the lettering is whichever of the
    /// three colours reads best on it.
    /// </summary>
    private static Look AppLook(string name)
    {
        var border = Toward(AppMid, AppBorderLight);

        var text = AppHot;
        foreach (var candidate in new[] { AppMid, AppDeep })
        {
            if (CoverOverlay.Contrast(candidate, border) > CoverOverlay.Contrast(text, border))
                text = candidate;
        }

        // Mid is most of what is seen, Hot is the light and Deep is the shadow, the order RoomPalette.From
        // takes them in.
        var colours = RoomPalette.From(Vec(AppMid), Vec(AppHot), Vec(AppDeep), RoomLight.Shelf);
        return new Look(
            new CoverOverlay.FrameInk(border, text), AppMid, new CoverRoom.ListRoom(colours, Apart(name), Ink: null));
    }

    /// <summary>Whether the drawing loaded. A folder keeps its plain mark when it did not.</summary>
    internal static bool Has() => Loaded() is not null;

    /// <summary>The cover, composed on a worker. Null where the drawing did not load or a surface could not be
    /// made; the folder then keeps its plain mark.</summary>
    /// <param name="face">Which folder this is.</param>
    /// <param name="name">The folder's name, as this locale spells it.</param>
    /// <param name="chosen">The picture somebody chose for the folder, or null.</param>
    internal static Task<byte[]?> DrawAsync(FolderFace face, string name, byte[]? chosen, CancellationToken ct)
        => Task.Run(() => Draw(face, name, chosen), ct);

    /// <summary>The cover, as a JPEG at <see cref="Edge"/>. See <see cref="DrawAsync"/>.</summary>
    internal static byte[]? Draw(FolderFace face, string name, byte[]? chosen)
    {
        if (Loaded() is not { Width: > 0, Height: > 0 } frame)
            return null;

        try
        {
            var look = LookOf(face, name);
            var info = new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            Behind(canvas, face, look, chosen);

            using (var image = SKImage.FromBitmap(frame))
            using (var paint = new SKPaint { ColorFilter = CoverOverlay.Ramp(look.Ink) })
                canvas.DrawImage(
                    image, SKRect.Create(frame.Width, frame.Height), SKRect.Create(Edge, Edge),
                    CoverOverlay.Smooth, paint);

            Letter(canvas, name, look.Ink.Text);
            canvas.Flush();

            // Written as a framed list's cover is, with the colour at full resolution, so the name keeps its
            // contrast (see CoverOverlay.Encoded).
            return CoverOverlay.Encoded(surface, CoverOverlay.FrameQuality, SKJpegEncoderDownsample.Downsample444);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What shows through the window, in the order the class summary gives. A picture that will not
    /// decode falls to the next answer; the last one, the room, leaves the black field where the room cannot
    /// be drawn on this machine.</summary>
    private static void Behind(SKCanvas canvas, FolderFace face, Look look, byte[]? chosen)
    {
        if (chosen is { Length: > 0 } && Decoded(chosen) is { } own)
        {
            using (own)
            {
                using var image = SKImage.FromBitmap(own);
                canvas.DrawImage(image, Fill(own.Width, own.Height), SKRect.Create(Edge, Edge), CoverOverlay.Smooth);
            }
            return;
        }

        if (face.PhotoKey is { } key && FolderPhotos.PathFor(key) is { } path
            && File.Exists(path) && Decoded(File.ReadAllBytes(path)) is { } photo)
        {
            using (photo)
                Services.Art.GeneratedCoverService.Washed(canvas, photo, look.Tint, Edge);
            return;
        }

        using var pass = new CardRoomArt.Pass(CoverOverlay.RoomEdge, CoverOverlay.RoomEdge);
        var colours = look.Room.Colours;
        if (!pass.Draw(colours, CardRoomArt.Still(colours, look.Room.Apart), zoom: 1f, shift: (0f, 0f)))
            return;

        using var drift = pass.Snapshot();
        if (drift is not null)
            canvas.DrawImage(drift, SKRect.Create(Edge, Edge), CoverOverlay.Smooth);
    }

    /// <summary>
    /// The folder's name, centred in the bar under the folder, on one line.
    /// </summary>
    /// <remarks>
    /// <para><b>Sized by <see cref="Gauge"/>, not by the name</b>, so every folder's name is one size: the size
    /// at which the gauge's ink is <see cref="NameCross"/> of the square tall. A name wider than <see
    /// cref="NameRun"/> of the square is set smaller to fit.</para>
    ///
    /// <para><b>Centred across by its own ink</b>, so the letters and not the advance widths are in the
    /// middle.</para>
    ///
    /// <para><b>Centred down by the body of the letters</b>: the part from the top of the gauge's ink (the top
    /// of the tall letters) to the baseline. Hanging tails such as the "g"'s are left out, so the name looks
    /// centred in the bar and every folder's name sits on one line, whatever letters it has. Skia measures
    /// the ink's top from the baseline, so it is negative, and the baseline goes half of it below the bar's
    /// middle.</para>
    ///
    /// <para>The face is the app's Cabin Bold where it has the letters, and whatever the system offers where
    /// it does not (<see cref="CoverFace"/>), as on a list's frame.</para>
    /// </remarks>
    private static void Letter(SKCanvas canvas, string name, SKColor colour)
    {
        if (name.Length == 0)
            return;

        // Owned says whether this face is ours to close. See CoverFace.Of.
        var (face, owned) = CoverFace.Of(name);

        try
        {
            var cross = Edge * NameCross;
            using var font = new SKFont(face, cross) { Edging = SKFontEdging.SubpixelAntialias };

            font.MeasureText(Gauge, out var gauge);

            // A face with no Latin letters measures the gauge as nothing. The name sets the size then.
            if (gauge.Height <= 0)
                font.MeasureText(name, out gauge);
            if (gauge.Height <= 0)
                return;

            font.Size *= cross / gauge.Height;

            font.MeasureText(name, out var ink);
            if (ink.Width <= 0)
                return;

            if (ink.Width > Edge * NameRun)
            {
                font.Size *= (Edge * NameRun) / ink.Width;
                font.MeasureText(name, out ink);
            }

            // The gauge at the size the name is actually set at, for the line.
            font.MeasureText(Gauge, out var line);
            if (line.Height <= 0)
                line = ink;

            using var paint = new SKPaint { IsAntialias = true, Color = colour };
            canvas.DrawText(
                name, ((Edge - ink.Width) / 2f) - ink.Left, (Edge * BarCentre) - (line.Top / 2f), font, paint);
        }
        finally
        {
            if (owned)
                face.Dispose();
        }
    }

    /// <summary>A plain folder's place along the room's drift, from its name: a stable number, so a folder has
    /// the same pose every time, and two folders seldom share one.</summary>
    private static int Apart(string name)
    {
        // FNV-1a over the UTF-16 units. string.GetHashCode changes from one run to the next.
        var hash = 2166136261u;
        foreach (var unit in name)
            hash = unchecked((hash ^ unit) * 16777619u);
        return (int)(hash % 997u);
    }

    /// <summary>The bytes as a picture, or null where they are not one. SKBitmap.Decode throws on bytes it
    /// cannot make a codec from.</summary>
    private static SKBitmap? Decoded(byte[] bytes)
    {
        try
        {
            var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is { Width: > 0, Height: > 0 })
                return bitmap;
            bitmap?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The largest square in the middle of a picture. The window is a square hole, so a picture of
    /// another shape is cropped to its middle rather than given bars.</summary>
    private static SKRect Fill(int width, int height)
    {
        var side = Math.Min(width, height);
        return SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side);
    }

    /// <summary>A colour taken toward black, channel by channel, rounded. The phones round the same
    /// way.</summary>
    private static SKColor Toward(SKColor colour, float light) => new(
        (byte)Math.Clamp(MathF.Round(colour.Red * light), 0, 255),
        (byte)Math.Clamp(MathF.Round(colour.Green * light), 0, 255),
        (byte)Math.Clamp(MathF.Round(colour.Blue * light), 0, 255));

    private static Vector3 Vec(SKColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);

    private static SKBitmap? _frame;
    private static bool _read;
    private static readonly object Gate = new();

    /// <summary>
    /// The drawing, read once from beside the binary and kept, whether it was found or not. It is read from
    /// the output folder and not through an <c>ms-appx:</c> URI, as a list's frame is (see
    /// <c>CoverOverlay.Loaded</c>): a cover is composed on a worker, where there is no XamlRoot.
    /// </summary>
    private static SKBitmap? Loaded()
    {
        lock (Gate)
        {
            if (_read)
                return _frame;

            _read = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PlaylistOverlays", Drawing);
                if (File.Exists(path))
                    _frame = SKBitmap.Decode(path);
            }
            catch
            {
                _frame = null;   // a drawing that will not decode is one the folders do without
            }

            return _frame;
        }
    }
}
