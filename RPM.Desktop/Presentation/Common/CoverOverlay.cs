using System.Numerics;
using System.Text;
using RPM.Core.Library;
using RPM.Core.Viz;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The frame that a generated playlist's cover is drawn inside, and the cover drawn inside it.
///
/// <para><b>What it is.</b> A PNG with a window cut in it. Everything outside the window is black. The
/// app's own name is in the corner, and a band runs down the right-hand side where the list's name is set.
/// So the cover uses the app's own type over a picture, and every generated list uses it.</para>
///
/// <para><b>What goes behind the window is a separate question.</b> Five lists have nothing else to put
/// there. They stand on the flat room (<see cref="CardRoomArt"/>). That is the same figures that the home
/// screen's banner cards stand on. See <see cref="Roomed"/> and <see cref="Draw"/>. Every other list
/// already has a picture, and the frame goes over that picture (<see cref="Over"/>).</para>
///
/// <para><b>The list's name is SET, not drawn into the file.</b> A name drawn into the file would fit only
/// one list ("Your mix"), and the app has five such lists. So the band is empty in the asset. The name is
/// drawn in <see cref="Letter"/>, on the line where the "rpm" logo in the corner sits. It is one size for
/// every list.</para>
///
/// <para><b>Inked in palette colours and nothing else.</b> The file is black with white lettering. Neither
/// colour survives to the cover. Both inks are entries from <c>Assets/RpmPalette.xaml</c>. The border is
/// the darkest stop of the asset's own gradient, over the list's shadow colour. The name is in whichever of
/// the list's three tints reads best on it. Nothing is mixed, rotated or lifted. So a cover cannot carry a
/// colour that the app does not own. See <see cref="Ink"/>.</para>
///
/// <para><b>One drawing, every list.</b> The frame is the same picture for all of them. What tells them
/// apart is the name in the band, the two inks, and what is behind the window.</para>
///
/// <para><b>Still here, drifting on the home screen.</b> What <see cref="Draw"/> composes is one frame. It
/// is taken at the moment that <see cref="CardRoomArt.Still"/> reads off the colours. So the square in the
/// playlists tree, the head of the list's own page, and the live card on the home screen are the same room,
/// caught at the same place. They are not two pictures of one list. The home screen's card takes the
/// lettered frame alone (<see cref="Frame"/>), and it drifts the room behind the frame live.</para>
///
/// <para><b>A missing file is an ordinary answer.</b> <see cref="Has"/> is false where the asset is missing
/// or will not decode. The caller then draws what it would have drawn anyway. The file is read once,
/// whether or not it was found.</para>
/// </summary>
internal static class CoverOverlay
{
    /// <summary>
    /// The square that the room is drawn on, before it is scaled up to the cover.
    /// </summary>
    /// <remarks> This is a fifth of the cover. Skia draws this shader on the CPU, so a frame costs its
    /// pixel count and almost nothing else. That is about 1.6 microseconds a pixel on the WinUI head.
    /// Drawing the room at the full 640 would take two thirds of a second for one square. At 128 it takes
    /// about 25 ms. Nothing that can be pointed at is lost, because there is no edge in this field narrower
    /// than the skirt of a lobe. The same reasoning as <see cref="CardRoomArt.Coarse"/> applies to the
    /// cards.
    /// </remarks>
    internal const int RoomEdge = 128;

    // Where the list's name is set. The numbers are fractions of the square's own edge, so they hold at
    // whatever size the cover is composed at. They were measured on the file, which is 800 pixels square.

    /// <summary>
    /// The middle of the band that the name is set on, across the square.
    /// </summary>
    /// <remarks> <b>The name is centred on the app's own "rpm" logo in the corner, not on the band the file
    /// shipped with.</b> The lettering that was removed from the asset had its midpoint at 0.91375. The
    /// drawn "rpm" below it runs from 685 to 768 pixels, so that centre is about four pixels to the right
    /// of the logo that the name stacks over. The value is read from the logo's ink instead, at 726.5 of
    /// 800, so the name and the logo stack.
    /// </remarks>
    private const float NameCentre = 0.90813f;

    /// <summary>Where the lettering starts, down the square.</summary>
    private const float NameTop = 0.0375f;

    /// <summary> How tall the lettering is ACROSS the band. This is the size, because the name is turned on
    /// its side. It is the span that an ascender and a descender take up together. It is measured on <see
    /// cref="Gauge"/>, not on the name. See <see cref="Letter"/>.
    /// </summary>
    private const float NameCross = 0.08f;

    /// <summary>
    /// The string that the size is measured on, for every cover.
    /// </summary>
    /// <remarks> An ascender and a descender together make the tallest span that Latin text occupies.
    /// Sizing each name from its OWN ink box would make the size depend on which letters the name happens
    /// to contain. Measured on the cover's own face at a 640 cover, "Your mix" came out at 68.2 points, and
    /// "Loved and forgotten" at 51.9. The second one reaches from an f to a g, and the first does not. So
    /// five covers on one shelf appeared in five sizes of type. The size of "Loved and forgotten" is used
    /// for every cover.
    ///
    /// <para>"fg" gives that size exactly, because f and g are the two letters that set that span. It is
    /// measured in whatever face the name will be set in. So a fallback face is sized by its own letters,
    /// not by the shipped face's.</para>
    /// </remarks>
    private const string Gauge = "fg";

    /// <summary>How far the name may run down the square before it reaches the app's own name in the
    /// corner. A longer name is set smaller, rather than allowed to collide. "Your mix" has eight letters,
    /// and 你的专属歌单 has six. But a locale that nobody has checked can be anything.</summary>
    private const float NameRun = 0.8375f;

    /// <summary>The quality that a cover carrying hard black-and-white type is written at. A JPEG codec
    /// spends its bits on smooth falls and on the rings around an edge. The whole frame is made of edges.
    /// See <c>PlaylistArt.Encoded</c>, where the same figure is explained for the advisory mark.</summary>
    private const int FrameQuality = 97;

    /// <summary>The quality that the room alone is written at, for the page's wash. The wash is about to be
    /// blurred until it is no longer a picture. So anything above this figure is bits spent on detail that
    /// gets thrown away.</summary>
    private const int RoomQuality = 90;

    /// <summary>
    /// The two colours that one cover's drawing is inked in.
    /// </summary>
    /// <param name="Border">Everything that the file draws black.</param>
    /// <param name="Text">Everything that it draws white: the list's name down the band, and the app's own
    /// name in the corner.</param>
    private readonly record struct FrameInk(SKColor Border, SKColor Text);

    /// <summary>
    /// The border's colour and the lettering's. Both come straight out of the palette asset.
    /// </summary>
    /// <remarks> <b>These covers are always in the rpm palette.</b> Nothing here mixes, rotates or lifts a
    /// colour. The border is the darkest stop of the gradient that <c>Assets/RpmPalette.xaml</c> already
    /// ships beside the list's shadow tint. The name is set in whichever of the list's own three tints
    /// reads best on that border. That takes two reads of the asset and one comparison.
    ///
    /// <para><b>Colours that the app does not own are avoided.</b> A border made by turning the list's hue
    /// half way round the colour ring and darkening it toward black would be one. So would lettering made
    /// from the room's accent, lightened until it reaches a contrast of 4.5:1. A rotation away from
    /// vermilion is a cyan that is nowhere in the palette. A stepped accent is a pastel of one colour. The
    /// palette's ten colours are real drawer tints, chosen as a set. A cover made from two of them sits on
    /// a shelf beside every other picture the app makes.</para>
    ///
    /// <para><b>The target is 3:1, not 4.5:1, and that is the cost.</b> The palette has no light entries.
    /// The brightest is Lime, at 0.54 relative luminance. So a palette tint on a palette shadow cannot
    /// reach the 4.5:1 that body text needs, whichever pair is chosen. The band is set at a twelfth of the
    /// cover's edge, in bold. That is large text, and WCAG asks only 3:1 for large text. Measured over the
    /// five framed lists as they are rolled today, the worst pair is 3.15:1 (the favourite tracks list and
    /// "Loved and forgotten", Crimson on Red's foot). The best is 9.87:1 ("New to you", Lime on Forest's
    /// foot). <see cref="Readable"/> still holds the fallback below to 4.5, because that path does not pick
    /// from a fixed set of ten.</para>
    ///
    /// <para><b>Three candidates, not just the brightest.</b> The trio is sorted by luminance. That is not
    /// the same question as which of them can be read on the shadow's own foot. For the two crimson lists,
    /// the brightest is Magenta at 2.95:1. The middle one, Crimson, is 3.15:1.</para>
    /// </remarks>
    /// <param name="room">The list's room. Its <see cref="CoverRoom.ListRoom.Ink"/> is the three palette
    /// entries as the asset lists them. <see cref="CoverRoom.ListRoom.Colours"/> is those three after they
    /// have been saturated and lifted for the shader. They cannot be read back as a palette colour.</param>
    private static FrameInk Ink(CoverRoom.ListRoom room)
    {
        if (room.Ink is not { } trio)
            return Unpainted(room.Colours);

        var border = Foot(trio.Deep);

        var text = trio.Hot;
        foreach (var candidate in new[] { trio.Mid, trio.Deep })
        {
            if (Contrast(candidate, border) > Contrast(text, border))
                text = candidate;
        }

        return new FrameInk(border, text);
    }

    /// <summary>
    /// The darkest stop of the gradient that the asset ships beside this palette colour.
    /// </summary>
    /// <remarks> It is read from the asset rather than worked out. That is the rule that <see
    /// cref="RpmPalette.RampFor"/> exists for. The three stops are as much a part of the curated set as the
    /// tint is. An arithmetic here would be a second answer, and it would drift the moment somebody
    /// re-curated the first.
    ///
    /// <para><see cref="Dim"/> is used where there is no ramp to read. That happens with a test's stand-in
    /// palette, which supplies ten colours and no gradients. The two agree on all ten today. That is the
    /// only reason a fallback is acceptable at all.</para>
    /// </remarks>
    private static SKColor Foot(SKColor colour)
        => RpmPalette.RampFor(Ui(colour)) is { Count: >= 2 } ramp
            ? Skia(ramp[^1])
            : Toward(colour, Dim);

    /// <summary>The brightness that the asset's own gradient takes its foot to, as a share of the tint:
    /// 0.40. It is stated here only so that <see cref="Foot"/> has an answer when there is no ramp. The
    /// asset is what is read.</summary>
    private const float Dim = 0.40f;

    /// <summary>
    /// The two inks that are used where there is no palette to take them from.
    /// </summary>
    /// <remarks> The palette is read from a <c>ResourceDictionary</c>. It is empty until
    /// <c>RpmPalette.Prime</c> has run on the UI thread, and a test host never runs it. So there is no
    /// palette colour to use. The room's own two colours are used instead. Its dominant tone is taken down
    /// to make a border. Its accent is lifted until it can be read on that border. This plain arithmetic is
    /// kept for the one case where the palette cannot be read.
    /// </remarks>
    private static FrameInk Unpainted(RoomPalette room)
    {
        var border = Toward(Skia(room.Tint), Dim);
        return new FrameInk(border, Legible(Skia(room.Glow), on: border));
    }

    /// <summary>The contrast that the lettering has to reach over the border, on the <see
    /// cref="Unpainted"/> path. That path can pick any colour, so it is held to the figure for normal text.
    /// The palette path cannot reach that figure, and it is argued at 3:1 in <see cref="Ink"/>.</summary>
    private const float Readable = 4.5f;

    /// <summary>
    /// This colour, lifted until it can be read on that one. The hue and the saturation are kept.
    /// </summary>
    /// <remarks> It is stepped rather than solved. The answer is a contrast ratio, but the value being
    /// changed is an HSL lightness. The two are not linear in each other. A hue's luminance at a given
    /// lightness depends on the hue. For example, a blue at three quarters lightness is darker than a
    /// yellow at half lightness. The loop tries fifty steps of two per cent, once per cover, on a worker.
    ///
    /// <para>It returns white where even full lightness will not reach the target. That cannot happen
    /// against a border this dark. It is there so that the loop always has an answer, rather than a last
    /// value.</para>
    /// </remarks>
    private static SKColor Legible(SKColor colour, SKColor on)
    {
        colour.ToHsl(out var hue, out var saturation, out var lightness);

        for (var light = lightness; light <= 100f; light += 2f)
        {
            var lifted = SKColor.FromHsl(hue, saturation, light);
            if (Contrast(lifted, on) >= Readable)
                return lifted;
        }

        return SKColors.White;
    }

    /// <summary>The contrast between two colours, as a ratio from 1 to 21. This is the WCAG
    /// relative-luminance measure, which anybody can check this figure against.</summary>
    private static float Contrast(SKColor a, SKColor b)
    {
        var one = Luminance(a);
        var two = Luminance(b);
        var (light, dark) = one >= two ? (one, two) : (two, one);
        return (light + 0.05f) / (dark + 0.05f);
    }

    private static float Luminance(SKColor c) =>
        (0.2126f * Channel(c.Red)) + (0.7152f * Channel(c.Green)) + (0.0722f * Channel(c.Blue));

    private static float Channel(byte value)
    {
        var v = value / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// The drawing's own black and white, read as a ramp between the two inks.
    /// </summary>
    /// <remarks> The file is greyscale. It has a black field, white lettering, and antialiased greys where
    /// they meet. So one table per channel IS a two-colour ramp. Zero maps to the border. 255 maps to the
    /// lettering. Every soft edge in the drawing lands in between. Alpha is left alone. That keeps the
    /// window a hole, and its edge as soft as it was drawn.
    /// </remarks>
    private static SKColorFilter Ramp(FrameInk ink)
    {
        var alpha = new byte[256];
        var red = new byte[256];
        var green = new byte[256];
        var blue = new byte[256];

        for (var i = 0; i < 256; i++)
        {
            var k = i / 255f;
            alpha[i] = (byte)i;
            red[i] = Mix(ink.Border.Red, ink.Text.Red, k);
            green[i] = Mix(ink.Border.Green, ink.Text.Green, k);
            blue[i] = Mix(ink.Border.Blue, ink.Text.Blue, k);
        }

        return SKColorFilter.CreateTable(alpha, red, green, blue);
    }

    private static byte Mix(byte from, byte to, float k) =>
        (byte)Math.Clamp(from + ((to - from) * k), 0, 255);

    /// <summary>A colour taken toward black. Each channel is scaled directly, not through HSL. What is
    /// wanted is the same colour with less light in it. HSL lightness pulls a saturated hue toward grey on
    /// the way.</summary>
    private static SKColor Toward(SKColor colour, float light) => new(
        (byte)Math.Clamp(colour.Red * light, 0, 255),
        (byte)Math.Clamp(colour.Green * light, 0, 255),
        (byte)Math.Clamp(colour.Blue * light, 0, 255),
        colour.Alpha);

    /// <summary>A Skia colour as the head's own colour type, which is what the palette is asked in. It is
    /// not called <c>Windows</c>. That is the namespace of the type, and a method with that name would
    /// shadow it inside this class.</summary>
    private static Windows.UI.Color Ui(SKColor c)
        => Windows.UI.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    private static SKColor Skia(Windows.UI.Color c) => new(c.R, c.G, c.B, c.A);

    private static SKColor Skia(Vector3 v) => new(
        (byte)Math.Clamp(v.X * 255f, 0, 255),
        (byte)Math.Clamp(v.Y * 255f, 0, 255),
        (byte)Math.Clamp(v.Z * 255f, 0, 255));

    /// <summary>The one drawing there is. Every generated list uses it.</summary>
    private const string Drawing = "Frame.png";

    /// <summary> Which lists stand on the drifting room behind the window, and which keep the picture they
    /// already wear.
    /// </summary>
    /// <remarks> <b>This is not the same question as whether a list is framed.</b> Every generated list is
    /// framed now. These five have nothing else to put behind the window, so the room is drawn there. The
    /// other lists already have a picture. That is a photograph, where one was kept for the list, or the
    /// figure rolled from the list's own two numbers otherwise. The frame goes over that picture (<see
    /// cref="Over"/>).
    ///
    /// <para>This is a switch with a fallback arm. So adding a list to the room is one line. Forgetting to
    /// add one does not leave a list with no cover at all.</para>
    /// </remarks>
    internal static bool Roomed(SmartPlaylistKind kind) => kind switch
    {
        // "Your mix" (Right now) comes first, then these four.
        SmartPlaylistKind.RightNow => true,
        SmartPlaylistKind.OnRepeat => true,
        SmartPlaylistKind.FavoriteTracks => true,
        SmartPlaylistKind.FirstListens => true,
        SmartPlaylistKind.LovedAndForgotten => true,
        _ => false,
    };

    /// <summary>
    /// Whether the drawing is actually there.
    /// </summary>
    /// <remarks> Both callers act on the answer. One composes a cover. The other takes a card's own mark
    /// down to make room for the frame. A card that hid its mark for a file that would not load would show
    /// an empty square.
    /// </remarks>
    internal static bool Has() => Loaded() is not null;

    private static SKBitmap? _frame;
    private static bool _read;
    private static readonly object Gate = new();

    /// <summary>
    /// The drawing itself. It is read once and kept, whether it was found or not.
    /// </summary>
    /// <remarks> It is read from the output folder, not through an <c>ms-appx:</c> URI. Every other bundled
    /// asset that this app decodes is read the same way (<see cref="PlaylistMark"/>, and <c>FaInk</c>). A
    /// cover is composed on a worker by design, and there is no XamlRoot there to resolve a URI against.
    ///
    /// <para>A miss is remembered too. If a miss were not remembered, every cover would open the file again
    /// and fail.</para>
    /// </remarks>
    private static SKBitmap? Loaded()
    {
        lock (Gate)
        {
            if (_read)
                return _frame;

            _read = true;
            try
            {
                var path = Path.Combine(
                    AppContext.BaseDirectory, "Assets", "PlaylistOverlays", Drawing);
                if (File.Exists(path))
                    _frame = SKBitmap.Decode(path);
            }
            catch
            {
                _frame = null;   // an asset that will not decode is an asset this does without
            }

            return _frame;
        }
    }

    /// <summary> The frame alone, lettered, with the window still a hole in it. This is for the home
    /// screen's card, which drifts the room behind it rather than showing a picture of the room.
    /// </summary>
    /// <remarks> It is a PNG and not a JPEG, because the window is transparent. That is the whole purpose
    /// of this output.
    /// </remarks>
    /// <param name="name">The list's name, as this locale spells it.</param>
    /// <param name="room">The list's own room. The border and the lettering are inked from it. See <see
    /// cref="Ink"/>. Only its palette trio is used. Where the drift starts does not reach the drawing, and
    /// the shader's own colours are not palette colours.</param>
    internal static byte[]? Frame(string name, CoverRoom.ListRoom room)
    {
        if (Loaded() is not { Width: > 0, Height: > 0 } frame)
            return null;

        try
        {
            var info = new SKImageInfo(
                PlaylistArt.Edge, PlaylistArt.Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            surface.Canvas.Clear(SKColors.Transparent);
            Paint(surface.Canvas, frame, name, Ink(room));
            surface.Canvas.Flush();

            using var drawn = surface.Snapshot();
            using var encoded = drawn.Encode(SKEncodedImageFormat.Png, 100);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One cover for a list that stands on the room: the room, then the lettered frame over it.
    /// </summary>
    /// <param name="name">The list's name, as this locale spells it.</param>
    /// <param name="room">The colours that the room is painted in, the palette entries that its two inks
    /// are taken from, and where in the drift it starts. These are the list's own, from <see
    /// cref="CoverRoom.Of(SmartPlaylistId)"/>. That makes the cover and the live card on the home screen
    /// one picture.</param>
    /// <param name="ground">Whether the page's wash is wanted as well. It is stated, not defaulted. Three
    /// of the four surfaces that ask for this cover show a square and nothing else. A wash that nobody
    /// reads costs an encode and a blur that nobody asked for.</param>
    /// <returns><see cref="PlaylistArt.Drawn.None"/> where there is no frame, where the shader will not
    /// compile on this machine, or where a surface could not be made. In each of those cases the caller
    /// draws the cover that it would have drawn without this.</returns>
    internal static PlaylistArt.Drawn Draw(string name, CoverRoom.ListRoom room, CoverGround ground)
    {
        if (Loaded() is not { Width: > 0, Height: > 0 } frame)
            return PlaylistArt.Drawn.None;

        try
        {
            using var pass = new CardRoomArt.Pass(RoomEdge, RoomEdge);
            // A cover looks at the room the way the card it stands for does: straight on, at the size the
            // room was tuned at. The two must be the same picture.
            if (!pass.Draw(
                    room.Colours, CardRoomArt.Still(room.Colours, room.Apart), zoom: 1f, shift: (0f, 0f)))
                return PlaylistArt.Drawn.None;

            using var drift = pass.Snapshot();
            if (drift is null)
                return PlaylistArt.Drawn.None;

            var info = new SKImageInfo(
                PlaylistArt.Edge, PlaylistArt.Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return PlaylistArt.Drawn.None;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(drift, SKRect.Create(PlaylistArt.Edge, PlaylistArt.Edge), Smooth);
            canvas.Flush();

            // The wash is taken from the room ALONE, before the frame is drawn on. What a page stands on
            // has to be a texture, not a picture. A blur of the frame would be a black field with the
            // list's name smeared across it. This is the same rule that PlaylistArt.Generated follows for
            // the app's mark.
            var wash = ground is CoverGround.Wanted
                       && Encoded(surface, RoomQuality, SKJpegEncoderDownsample.Downsample420)
                    is { Length: > 0 } bare
                ? Services.Art.ArtScaler.Wash(bare)
                : null;

            Paint(canvas, frame, name, Ink(room));
            canvas.Flush();

            return new PlaylistArt.Drawn(
                Encoded(surface, FrameQuality, SKJpegEncoderDownsample.Downsample444), wash);
        }
        catch
        {
            return PlaylistArt.Drawn.None;
        }
    }

    /// <summary>
    /// One cover for a list that already has a picture: that picture, then the lettered frame over it.
    /// </summary>
    /// <remarks> <b>The picture is not chosen again here.</b> What goes behind the window is whatever the
    /// list wears today. That is a photograph kept for it, or the figure rolled from its own two numbers
    /// otherwise. So the frame changes how a list is presented, not which picture it has. See <see
    /// cref="GeneratedCover"/>, where that order is defined.
    ///
    /// <para><b>Filled, not fitted.</b> The window is square, and nearly everything that arrives here is
    /// square too. A picture that is not square is cropped to its middle. It does not get bars down its
    /// sides. The window is a hole in a drawing, and a letterboxed picture inside it looks like a mistake
    /// in the drawing.</para>
    ///
    /// <para>It is written with <c>Downsample444</c>, like the framed cover that it is. The reason is in
    /// <see cref="Encoded"/>, where the measurement is. <b>No wash comes out of this.</b> A page's wash is
    /// a blur of the picture ALONE, and the caller already holds those bytes.</para>
    /// </remarks>
    /// <param name="picture">The list's own square, as it would be shown with no frame on it.</param>
    /// <param name="name">The list's name, as this locale spells it.</param>
    /// <param name="room">The list's room. The border and the lettering are inked from it. See <see
    /// cref="Ink"/>. Only the colours are used. Nothing behind the window comes from it here.</param>
    /// <returns>Null where the drawing did not load, where the picture would not decode, or where a surface
    /// could not be made. In each case the caller keeps the picture that it passed in.</returns>
    internal static byte[]? Over(byte[] picture, string name, CoverRoom.ListRoom room)
    {
        if (Loaded() is not { Width: > 0, Height: > 0 } frame)
            return null;

        try
        {
            using var behind = SKBitmap.Decode(picture);
            if (behind is not { Width: > 0, Height: > 0 })
                return null;

            var info = new SKImageInfo(
                PlaylistArt.Edge, PlaylistArt.Edge, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using (var image = SKImage.FromBitmap(behind))
                canvas.DrawImage(
                    image, Fill(behind.Width, behind.Height),
                    SKRect.Create(PlaylistArt.Edge, PlaylistArt.Edge), Smooth);

            Paint(canvas, frame, name, Ink(room));
            canvas.Flush();

            return Encoded(surface, FrameQuality, SKJpegEncoderDownsample.Downsample444);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The square from the middle of a picture: the largest square it holds, centred. A picture
    /// that is already square is used whole. Nearly every picture that reaches here is already
    /// square.</summary>
    private static SKRect Fill(int width, int height)
    {
        var side = Math.Min(width, height);
        return SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side);
    }

    private static SKSamplingOptions Smooth => new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Draws the drawing in this list's two inks, then the name in its band. It is one method, so
    /// that the still cover and the card's own frame cannot end up as two different frames.</summary>
    private static void Paint(SKCanvas canvas, SKBitmap frame, string name, FrameInk ink)
    {
        using (var image = SKImage.FromBitmap(frame))
        using (var paint = new SKPaint { ColorFilter = Ramp(ink) })
            canvas.DrawImage(
                image,
                SKRect.Create(frame.Width, frame.Height),
                SKRect.Create(PlaylistArt.Edge, PlaylistArt.Edge),
                Smooth,
                paint);

        Letter(canvas, name, ink.Text);
    }

    /// <summary>
    /// The list's name, turned on its side and set in the band down the right-hand edge.
    /// </summary>
    /// <remarks> <b>Sized from a gauge, not from the name.</b> Every cover sets its name at one size. That
    /// size is whatever makes <see cref="Gauge"/> (an ascender and a descender) as tall as the band.
    /// Measuring the name's own ink box instead made the size depend on which letters the name contains. It
    /// put five covers on one shelf in five sizes of type. See <see cref="Gauge"/> for the measurements.
    ///
    /// <para><b>Measured tightly.</b> What has to match the drawing is where the letters ARE. A font's
    /// ascent and descent are the room it reserves, not the room it uses. Two faces at one size put their
    /// capitals in different places. So the gauge is measured with <c>MeasureText(text, out bounds)</c>,
    /// and scaled until that box is as tall as the band.</para>
    ///
    /// <para><b>Centred on the gauge as well as sized by it.</b> That puts every cover's baseline in the
    /// same place. If the name's own box were centred, the line would move with each name. The favourite
    /// tracks name has no descender, so it would sit lower in the band than "Loved and forgotten", which
    /// does have one. The band's centre line is <see cref="NameCentre"/>. It is read off the "rpm" in the
    /// corner, so the name and the logo stack.</para>
    ///
    /// <para><b>Shrunk if it is too long.</b> This is the one thing measured off the name. A name that
    /// would run into the app's own name in the corner is set smaller, rather than allowed to collide. None
    /// of the five English names reaches that limit. A locale that nobody has checked can be
    /// anything.</para>
    ///
    /// <para><b>Turned clockwise, so the caps face the outer edge.</b> Skia counts y down. So a +90
    /// rotation sends local +x down the picture, and local -y (where the caps are) out to the right. That
    /// is the way round the original lettering read.</para>
    ///
    /// <para>The name is set in one of the list's own palette colours. See <see cref="Ink"/> for which one,
    /// and why.</para>
    ///
    /// <para>The face is the app's own Cabin Bold where it has the letters. Where it does not, it is
    /// whatever the system offers. That is what makes 你的专属歌单 legible rather than a row of boxes. Segoe UI
    /// Bold is not used, because macOS answers that request with Helvetica without saying so. See <see
    /// cref="CoverFace"/>, which both this method and <c>PlaylistArt.Write</c> use.</para>
    /// </remarks>
    private static void Letter(SKCanvas canvas, string name, SKColor colour)
    {
        if (name.Length == 0)
            return;

        // Owned says whether this one is ours to close. The app's own face is opened once for the life of
        // the process, and it is shared by every cover. Disposing it here would take it out from under the
        // next list. See CoverFace.Of.
        var (face, owned) = CoverFace.Of(name);

        try
        {
            using var font = new SKFont(face, PlaylistArt.Edge * NameCross)
            {
                Edging = SKFontEdging.SubpixelAntialias,
            };

            font.MeasureText(Gauge, out var band);

            // A face with no Latin letters measures the gauge as zero. In that case the name itself sets
            // the size. That is better than no name at all.
            if (band.Height <= 0)
                font.MeasureText(name, out band);

            if (band.Height <= 0)
                return;

            font.Size *= (PlaylistArt.Edge * NameCross) / band.Height;

            font.MeasureText(name, out var ink);
            if (ink.Width <= 0)
                return;

            if (ink.Width > PlaylistArt.Edge * NameRun)
            {
                font.Size *= (PlaylistArt.Edge * NameRun) / ink.Width;
                font.MeasureText(name, out ink);
            }

            // The band, at the size the name is actually set at. That is the gauge's size, unless the line
            // above shrank it. It is measured again rather than scaled, so the centre line matches the size
            // this font draws at.
            font.MeasureText(Gauge, out band);
            if (band.Height <= 0)
                band = ink;

            using var paint = new SKPaint { IsAntialias = true, Color = colour };

            canvas.Save();
            canvas.Translate(PlaylistArt.Edge * NameCentre, PlaylistArt.Edge * NameTop);
            canvas.RotateDegrees(90);

            // Along the band, the first letter starts where the run starts. That is the name's own left
            // bearing. Across the band, the middle of the GAUGE sits on the centre line, so every cover
            // shares it.
            canvas.DrawText(name, -ink.Left, -((band.Top + band.Bottom) / 2f), font, paint);
            canvas.Restore();
        }
        finally
        {
            if (owned)
                face.Dispose();
        }
    }

    /// <summary>
    /// The surface as a JPEG.
    /// </summary>
    /// <remarks> <b><paramref name="chroma"/> is not a tuning knob. It is what makes the name readable.</b>
    /// A JPEG stores brightness for every pixel, and colour at a quarter of that resolution, averaged over
    /// 2x2 blocks. On a photograph that is invisible. On saturated type it is ruinous. The band is one
    /// palette colour set on another. The two differ far more in colour than in brightness. So the
    /// averaging pulls the letters toward the border they stand on. In one measurement, "Your mix" on the
    /// two crimson lists was laid down as #EA154E. That is 3.17:1 on that border. The brightest pixel that
    /// survived the encode was #C22E46, at 2.54:1. The same drawing written as a PNG kept the full 3.17.
    /// Nothing about the drawing was wrong, and nothing threw an error. The cover simply came out of the
    /// encoder harder to read than it went in. <c>CoverOverlayTests</c> is what caught it.
    ///
    /// <para>Raising the quality does not fix it. At quality 100 the same pixel came out as #C52A49. The
    /// reason is that subsampling is a separate decision from quantization. <c>Downsample444</c> keeps
    /// colour at full resolution, and it is what the framed cover is written with.</para>
    ///
    /// <para>The room alone keeps <c>Downsample420</c>. It is a soft field with no type on it, and it is
    /// about to be blurred until it is no longer a picture. So full-resolution colour there would be bytes
    /// spent on something that nobody sees.</para>
    /// </remarks>
    private static byte[]? Encoded(SKSurface surface, int quality, SKJpegEncoderDownsample chroma)
    {
        using var drawn = surface.Snapshot();

        // Declared with using so that it stays alive through the encode (analyzer rule RPM012).
        // SKPixmap.Encode passes the native pixmap to Skia and keeps no reference to itself. Skia's encoder
        // reads it for every row. If the finalizer collects it first, it can be freed mid-encode. That can
        // crash the app with SIGBUS in the JPEG encoder.
        using var pixels = drawn.PeekPixels();
        if (pixels is not null)
        {
            using var stream = new SKDynamicMemoryWStream();
            if (pixels.Encode(stream, new SKJpegEncoderOptions(quality, chroma, SKJpegEncoderAlphaOption.Ignore)))
            {
                using var written = stream.DetachAsData();
                if (written is { Size: > 0 })
                    return written.ToArray();
            }
        }

        // The plain encode, for when the pixels cannot be peeked. A surface on the GPU does not hand them
        // over. This call does not control the subsampling. So the result is a correct cover, with the
        // colour loss described above, rather than no cover at all.
        using var encoded = drawn.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
    }
}

/// <summary>Whether a caller wants the page's wash as well as the square. It is stated at every call. A
/// square is what three of the four surfaces show, and the blur behind the fourth is not free.</summary>
internal enum CoverGround
{
    /// <summary>The square only.</summary>
    NotWanted,

    /// <summary>The square, and the same picture blurred for the page it opens.</summary>
    Wanted,
}
