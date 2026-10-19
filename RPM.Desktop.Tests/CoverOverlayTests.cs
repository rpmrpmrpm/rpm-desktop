using RPM.Core.Library;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// That a framed cover is a room behind a window rather than a black square, that its two inks are palette
/// colours, and that the name in its band is the same size and on the same line on every one.
///
/// <para>Every way this fails is silent. If the frame file did not ship, the cover claims a frame it does
/// not have. A frame drawn the wrong way round is an opaque black square with no room in it. A room that
/// did not draw is a window onto nothing. A name sized by its own letters gives five covers in five sizes
/// of type. None of these throws an error, and all of them reach the shelf as a picture.</para>
///
/// <para><b>Drawn with the palette standing in.</b> The inks are entries from
/// <c>Assets/RpmPalette.xaml</c>. A suite with no palette would test <c>CoverOverlay.Unpainted</c>, the
/// fallback, and would say nothing about what ships. See <see cref="PaletteStandIn"/>. The
/// <see cref="PaletteStandInCollection"/> attribute is required. See that class for why.</para>
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class CoverOverlayTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    public void Dispose()
    {
        _palette.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The list's name, as the band is set with it. English here; the app asks
    /// <c>SmartPlaylistFace.Name</c>, which answers in whatever the app is running in.</summary>
    private const string Name = "Your mix";

    /// <summary>The five lists that are framed, in a fixed order.</summary>
    public static TheoryData<SmartPlaylistKind> Framed =>
    [
        SmartPlaylistKind.RightNow,
        SmartPlaylistKind.OnRepeat,
        SmartPlaylistKind.FavoriteTracks,
        SmartPlaylistKind.FirstListens,
        SmartPlaylistKind.LovedAndForgotten,
    ];

    /// <summary>The names those five lists wear in English, which is what the band is set with. Only the
    /// spread matters here. They run from eight letters to nineteen, and from a name with a descender to a
    /// name with none.</summary>
    private static readonly string[] EnglishNames =
    [
        "Your mix",
        "On repeat",
        "Favorite tracks",
        "New to you",
        "Loved and forgotten",
    ];

    /// <summary>The room a list is drawn in. It is rolled from the list's own seed, against the stood-in
    /// palette. This is the same call the app makes.</summary>
    private static CoverRoom.ListRoom Room(SmartPlaylistKind kind)
        => CoverRoom.Of(SmartPlaylistId.WholeShelf(kind));

    /// <summary>
    /// The frame drawing that every list uses is present.
    /// </summary>
    /// <remarks>
    /// <c>Has</c> reports whether the asset is there, so this test checks that the PNG is in the project
    /// and reaches the output folder. Without the frame, the app looks the same as before, and nobody
    /// notices the failure.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Framed))]
    public void A_framed_list_has_its_drawing(SmartPlaylistKind kind)
    {
        Assert.True(
            CoverOverlay.Has(),
            "Assets/PlaylistOverlays/Frame.png did not reach the output folder");

        Assert.NotNull(CoverOverlay.Frame(Name, Room(kind)));
    }

    /// <summary>
    /// Two lists with the same colourway are not one picture.
    /// </summary>
    /// <remarks>
    /// "Favorite tracks" and "Loved and forgotten" are both Crimson at seed 9,778. They differ only
    /// in which family their figure is drawn as. A room has no families. Without a place along the drift,
    /// they would be the same picture in the same pose, with two names on it.
    /// </remarks>
    [Fact]
    public void Two_lists_in_one_colourway_start_at_different_moments()
    {
        var one = Room(SmartPlaylistKind.FavoriteTracks);
        var two = Room(SmartPlaylistKind.LovedAndForgotten);

        Assert.Equal(one.Ink, two.Ink);
        Assert.NotEqual(one.Apart, two.Apart);
        Assert.NotEqual(
            CardRoomArt.Still(one.Colours, one.Apart),
            CardRoomArt.Still(two.Colours, two.Apart));
    }

    /// <summary>
    /// A list that is not one of the five is framed over the picture it already wears.
    /// </summary>
    /// <remarks>
    /// A list that is not one of the five does not stand on the room, but the frame still goes on. The test
    /// checks two facts, both read off the composed square. There is a border at the foot in the list's own
    /// colour. The picture that was passed in still shows through the window.
    ///
    /// <para>The picture behind is flat white, which no border in the palette is.</para>
    /// </remarks>
    [Fact]
    public void A_list_that_is_not_roomed_is_framed_over_its_own_picture()
    {
        Assert.False(CoverOverlay.Roomed(SmartPlaylistKind.Calm));

        var room = Room(SmartPlaylistKind.Calm);
        Assert.NotNull(room.Ink);

        var framed = CoverOverlay.Over(Flat(SKColors.White), Name, room);
        Assert.NotNull(framed);

        using var cover = SKBitmap.Decode(framed);
        Assert.NotNull(cover);

        var window = cover.GetPixel((int)(cover.Width * 0.30), (int)(cover.Height * 0.55));
        Assert.True(
            window.Red > 230 && window.Green > 230 && window.Blue > 230,
            $"the picture passed in is not showing through the window: {window}");

        var deep = room.Ink!.Value.Deep;
        var wanted = new SKColor(
            (byte)(deep.Red * 0.40), (byte)(deep.Green * 0.40), (byte)(deep.Blue * 0.40));
        var foot = cover.GetPixel((int)(cover.Width * 0.35), (int)(cover.Height * 0.985));

        Assert.True(
            Math.Abs(foot.Red - wanted.Red) <= 8
            && Math.Abs(foot.Green - wanted.Green) <= 8
            && Math.Abs(foot.Blue - wanted.Blue) <= 8,
            $"the border is {foot} where the palette's foot over {deep} is {wanted}");
    }

    /// <summary>A square of one colour, encoded, for a test that wants to see what the window lets
    /// through.</summary>
    private static byte[] Flat(SKColor colour)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(PlaylistArt.Edge, PlaylistArt.Edge, SKColorType.Rgba8888, SKAlphaType.Premul));
        Assert.NotNull(surface);
        surface.Canvas.Clear(colour);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// A Simplified Chinese name is set in a Simplified Chinese face, in bold, with no character missing.
    /// </summary>
    /// <remarks>
    /// <b>When this fails, the cover still looks like a cover.</b> If the language is not given, the
    /// platform offers Yu Gothic UI, a Japanese face. That face has four of these six characters, and it
    /// lacks 专 and 单. The face with the most characters was Microsoft JhengHei UI, which is Traditional. If
    /// the weight is not given, the platform answers 400. Then the only name on the shelf that is not Latin
    /// is not bold. Neither case throws an error or draws a box. See <c>CoverFace.Matched</c>.
    ///
    /// <para>The face itself belongs to the platform, and the test does not check it by name. The test
    /// checks that the request is made in the right terms, and that the face returned can draw the string.
    /// A host with no Simplified face installed has nothing to offer, so the test is skipped rather than
    /// failed.</para>
    /// </remarks>
    [Fact]
    public void A_simplified_chinese_name_is_set_in_a_simplified_chinese_face()
    {
        // "Your mix" as zh-Hans spells it.
        const string Chinese = "\u4f60\u7684\u4e13\u5c5e\u6b4c\u5355";
        var letters = Chinese.EnumerateRunes().Select(r => r.Value).Distinct().ToArray();

        // This goes through the app's own lookup, not the font manager's. Asking the manager directly would
        // test the platform, not RPM. macOS answers a character match with the family's regular member,
        // however the request was worded. CoverFace is where that is dealt with.
        var faces = letters.Select(letter => CoverFace.FaceFor(letter, "zh-Hans")).ToArray();

        if (faces.Any(f => f is null))
            return;   // no Simplified face on this host: a missing language pack is not a fault here

        var best = faces.OrderByDescending(f => letters.Count(f!.ContainsGlyph)).First()!;

        var missing = letters.Where(l => !best.ContainsGlyph(l))
                             .Select(char.ConvertFromUtf32)
                             .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{best.FamilyName} cannot draw {string.Join("", missing)} of {Chinese}");

        Assert.True(
            best.FontWeight >= (int)SKFontStyleWeight.SemiBold,
            $"{best.FamilyName} came back at weight {best.FontWeight}, where the Latin names are bold");
    }

    /// <summary>
    /// The frame is on the outside and the room is inside the window.
    /// </summary>
    /// <remarks>
    /// <para>The check reads the composed picture, not the two halves. A foot that is not dark means the
    /// frame did not land. A window with no light anywhere means the room did not draw, or the frame went
    /// over the whole square. The window's bounds come from the file: 36,31 to 646,764 of 800. Both patches
    /// sampled are well inside their own side of that. They are clear of the lettering and of the edge that
    /// a JPEG rings around.</para>
    /// </remarks>
    [Fact]
    public void The_border_is_a_border_and_the_window_is_the_room()
    {
        var drawn = CoverOverlay.Draw(
            Name, Room(SmartPlaylistKind.RightNow), CoverGround.NotWanted);
        Assert.NotNull(drawn.Cover);

        using var cover = SKBitmap.Decode(drawn.Cover);
        Assert.NotNull(cover);
        Assert.Equal(PlaylistArt.Edge, cover.Width);
        Assert.Equal(PlaylistArt.Edge, cover.Height);

        // The foot, below the window and left of the app's own name. It must still be dark, because it is a
        // border and not a second picture.
        var foot = Patch(cover, 0.20, 0.50, 0.975, 0.995);
        Assert.True(foot.Most < 0.45, $"the border is not a border: the foot came out at {foot.Most:F3}");

        // The window is the room. The test takes the brightest pixel, not the average. The room is a dark
        // field with light in it. An average would ask for a bright picture rather than for a picture at
        // all.
        var window = Patch(cover, 0.15, 0.65, 0.20, 0.80);
        Assert.True(
            window.Most > 0.05,
            $"the window came out black: the room did not draw under the frame ({window.Most:F3})");
    }

    /// <summary>
    /// The border is the asset's own darkest stop over the list's shadow tint.
    /// </summary>
    /// <remarks>
    /// <b>These covers must always use the rpm palette.</b> A failure here is silent. A border worked out
    /// from a hue, instead of read off the asset, still draws and still looks like a border. The colour
    /// itself is what shows the failure.
    ///
    /// <para>The colour is read from the composed JPEG, so the test measures what lands on the shelf. The
    /// codec allows eight levels of slack per channel, because it shifts a flat field slightly.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Framed))]
    public void The_border_is_the_lists_own_shadow_at_the_assets_foot(SmartPlaylistKind kind)
    {
        var room = Room(kind);
        Assert.NotNull(room.Ink);

        var drawn = CoverOverlay.Draw(Name, room, CoverGround.NotWanted);
        Assert.NotNull(drawn.Cover);

        using var cover = SKBitmap.Decode(drawn.Cover);
        Assert.NotNull(cover);

        var deep = room.Ink!.Value.Deep;
        var wanted = new SKColor(
            (byte)(deep.Red * 0.40), (byte)(deep.Green * 0.40), (byte)(deep.Blue * 0.40));
        var drawnFoot = cover.GetPixel((int)(cover.Width * 0.35), (int)(cover.Height * 0.985));

        Assert.True(
            Math.Abs(drawnFoot.Red - wanted.Red) <= 8
            && Math.Abs(drawnFoot.Green - wanted.Green) <= 8
            && Math.Abs(drawnFoot.Blue - wanted.Blue) <= 8,
            $"{kind}: the border is {drawnFoot} where the palette's foot over {deep} is {wanted}");
    }

    /// <summary>The wash is the room WITHOUT the frame. It is only made when it is asked for. A blur of the
    /// frame would be a dark field with the list's name smeared across it, and a page cannot be read on
    /// that.</summary>
    [Fact]
    public void The_wash_is_asked_for_and_is_not_the_frame()
    {
        var room = Room(SmartPlaylistKind.RightNow);

        Assert.Null(
            CoverOverlay.Draw(Name, room, CoverGround.NotWanted).Wash);

        var drawn = CoverOverlay.Draw(Name, room, CoverGround.Wanted);
        Assert.NotNull(drawn.Wash);

        using var wash = SKBitmap.Decode(drawn.Wash);
        Assert.NotNull(wash);

        // At the foot, where the cover has its border. The wash is the room across the whole square, so
        // there is light there.
        var foot = Patch(wash, 0.20, 0.50, 0.975, 0.995);
        Assert.True(
            foot.Most > 0.05,
            $"the wash was drawn from the frame, not the room ({foot.Most:F3})");
    }

    /// <summary>
    /// The list's name is set into the band. The asset file does not contain the name.
    /// </summary>
    /// <remarks>
    /// The band is read where the lettering runs down, in the upper half. The same band is read on a cover
    /// drawn with no name. The border is a colour and not black. So "there is light in the band" cannot
    /// answer the question alone. The name is set if the band is lighter with a name than without one.
    /// </remarks>
    [Fact]
    public void The_name_is_set_in_the_band()
    {
        var room = Room(SmartPlaylistKind.RightNow);

        var drawn = CoverOverlay.Draw(Name, room, CoverGround.NotWanted);
        Assert.NotNull(drawn.Cover);

        using var cover = SKBitmap.Decode(drawn.Cover);
        Assert.NotNull(cover);

        var band = Patch(cover, 0.87, 0.96, 0.05, 0.45);

        var bare = CoverOverlay.Draw("", room, CoverGround.NotWanted);
        Assert.NotNull(bare.Cover);

        using var empty = SKBitmap.Decode(bare.Cover);
        Assert.NotNull(empty);

        var blank = Patch(empty, 0.87, 0.96, 0.05, 0.45);

        Assert.True(
            band.Most > blank.Most + 0.2,
            $"the band has no lettering in it ({band.Most:F3} against {blank.Most:F3} with no name)");
    }

    /// <summary>
    /// The lettering can be read on the border it stands on.
    /// </summary>
    /// <remarks>
    /// <b>This test checks for a fault, not for a feature.</b> Two palette colours on one cover say nothing
    /// about whether either can be read. People tell colours apart by lightness first. Without this check,
    /// "Your mix" could measure 1.9:1 against the 4.5:1 that text needs. Nothing would throw, and both
    /// covers would still look like covers. Only a number catches it.
    ///
    /// <para><b>The threshold is 3:1, not 4.5:1.</b> Every ink is an entry from the palette. The palette
    /// has no light entries. The brightest is Lime, at 0.54 relative luminance, so no pair of them reaches
    /// 4.5:1. The band is large bold text, and WCAG asks only 3:1 for that. The worst of the five, as they
    /// are rolled today, is 3.15:1, on the two Crimson lists. See <c>CoverOverlay.Ink</c>.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Framed))]
    public void The_name_can_be_read_on_the_border(SmartPlaylistKind kind)
    {
        var drawn = CoverOverlay.Draw(Name, Room(kind), CoverGround.NotWanted);
        Assert.NotNull(drawn.Cover);

        using var cover = SKBitmap.Decode(drawn.Cover);
        Assert.NotNull(cover);

        var border = cover.GetPixel((int)(cover.Width * 0.35), (int)(cover.Height * 0.985));

        SKColor? lightest = null;
        var most = -1.0;

        for (var y = (int)(cover.Height * 0.05); y < cover.Height * 0.45; y++)
        {
            for (var x = (int)(cover.Width * 0.87); x < cover.Width * 0.96; x++)
            {
                var pixel = cover.GetPixel(x, y);
                if (Luma(pixel) > most)
                {
                    most = Luma(pixel);
                    lightest = pixel;
                }
            }
        }

        Assert.NotNull(lightest);

        var apart = Contrast(lightest.Value, border);
        Assert.True(
            apart >= 3.0,
            $"{kind}: the name is {apart:F2}:1 against its border, which is unreadable");
    }

    /// <summary>
    /// Every name is set at one size, whatever letters it happens to contain.
    /// </summary>
    /// <remarks>
    /// <b>The fault this catches.</b> If the size is set so that the name's own ink box is as tall as the
    /// band, the size depends on the letters. For example, on a 640 px cover in Segoe UI Bold, "Your mix"
    /// would come out at 68.2 points and "Loved and forgotten" at 51.9. The second name reaches from an f
    /// to a g, and the first does not. Five covers on one shelf would then have five sizes of type, and
    /// none of them looks wrong on its own.
    ///
    /// <para><b>Read back through the font, not off the picture.</b> A name's run down the band is its
    /// width, and that is a different number for every name. Divided by the name's width at one point, it
    /// gives the size the name was set at. That should be the same number for all five.</para>
    /// </remarks>
    [Fact]
    public void Every_name_is_set_at_the_same_size()
    {
        var sizes = new List<(string Name, double Size)>();

        foreach (var name in EnglishNames)
        {
            var drawn = CoverOverlay.Draw(
                name, Room(SmartPlaylistKind.RightNow), CoverGround.NotWanted);
            Assert.NotNull(drawn.Cover);

            using var cover = SKBitmap.Decode(drawn.Cover);
            Assert.NotNull(cover);

            var ink = BandInk(cover);
            Assert.True(ink.Run > 0, $"{name}: nothing was set in the band");

            sizes.Add((name, ink.Run / WidthAtOnePoint(name)));
        }

        // These are called on Enumerable, not as extension methods. Uno.Extensions declares its own MinBy
        // and MaxBy, and this project has both namespaces in its implicit usings. So the extension form is
        // ambiguous and does not compile. Naming the class picks System.Linq's.
        var smallest = Enumerable.MinBy(sizes, s => s.Size);
        var largest = Enumerable.MaxBy(sizes, s => s.Size);

        Assert.True(
            largest.Size <= smallest.Size * 1.04,
            $"the names are set at different sizes: {smallest.Name} at {smallest.Size:F1} "
            + $"and {largest.Name} at {largest.Size:F1}");
    }

    /// <summary>
    /// The band's lettering is centred on the "rpm" in the corner, which it stacks over.
    /// </summary>
    /// <remarks>
    /// The band and the logo share the right-hand column of the cover, so their centre lines must be the
    /// same line. The band is centred on the rpm logo at the bottom right.
    ///
    /// <para>The check uses "Loved and forgotten", which reaches both an ascender and a descender. Its ink
    /// spans the whole band, so the midpoint of that span is the line the lettering is centred on. A name
    /// with no descender sits on the same baseline, but its ink does not span the band, so its midpoint is
    /// not used.</para>
    /// </remarks>
    [Fact]
    public void The_name_is_centred_on_the_logo()
    {
        var drawn = CoverOverlay.Draw(
            "Loved and forgotten", Room(SmartPlaylistKind.RightNow), CoverGround.NotWanted);
        Assert.NotNull(drawn.Cover);

        using var cover = SKBitmap.Decode(drawn.Cover);
        Assert.NotNull(cover);

        var band = BandInk(cover);
        var logo = LogoInk(cover);

        Assert.True(logo.Across > 0, "the app's own name did not draw in the corner");

        var apart = Math.Abs(band.Centre - logo.Centre);
        Assert.True(
            apart <= 3.0,
            $"the band is centred at {band.Centre:F1} and the logo at {logo.Centre:F1}, "
            + $"{apart:F1} pixels apart");
    }

    /// <summary>
    /// The name's width at one point, in the face the cover sets it in.
    /// </summary>
    /// <remarks>
    /// The width is asked of <see cref="CoverFace"/>, which is where the cover asks for its face. Segoe UI
    /// Bold is a Windows font. If a name were asked for by Windows font name, macOS would hand both this
    /// test and the cover Helvetica, without telling either of them. The test would still pass, because
    /// both sides would be wrong in the same way. The size rule would fail, because Helvetica is wider and
    /// runs a long name past the room the cover gives it.
    ///
    /// <para>Asking the same place the cover asks means this measures whatever ships, on whichever head the
    /// suite runs on. It is no longer a second statement of which face that is.</para>
    /// </remarks>
    private static double WidthAtOnePoint(string name)
    {
        // Owned says whether it is ours to close. The shipped face is shared and outlives any one
        // measurement. See CoverFace.Of.
        var (face, owned) = CoverFace.Of(name);

        try
        {
            using var font = new SKFont(face, 100f);
            font.MeasureText(name, out var ink);
            return ink.Width / 100f;
        }
        finally
        {
            if (owned)
                face.Dispose();
        }
    }

    /// <summary>
    /// The lettering in the band: how far it runs down the cover, where its centre line sits across it, and
    /// how wide across the band it is.
    /// </summary>
    /// <remarks>
    /// The band is dark, with one colour set on it. So "lettering" is anything well clear of the darkest
    /// thing in the strip. The threshold is two fifths of the way from the darkest pixel to the brightest.
    /// That is above the grey a JPEG leaves around an edge, and below the body of a letter.
    ///
    /// <para><b>The window goes down to the logo, not half way.</b> If the window stopped at 0.50, the
    /// longest name would run past it. For example, "Loved and forgotten" inks from y 25 to y 445 on a 640
    /// px cover. A window ending at 0.50 would report a run of 295 instead of 420. The name would then look
    /// as if it were set at two thirds of the size of the others. The window is too short in that case, not
    /// the cover. The window stops at 0.92 because the app's own name starts at 0.93 in the same column. A
    /// window that reached it would measure the two as one run.</para>
    /// </remarks>
    private static (double Run, double Centre, double Across) BandInk(SKBitmap cover)
        => Ink(cover, 0.855, 0.965, 0.02, 0.92);

    /// <summary>
    /// The app's own name in the corner, below the window and below the band.
    /// </summary>
    /// <remarks>
    /// <b>The window starts at 0.86, not at 0.80.</b> The room's own bottom corner reaches into this strip,
    /// at the left of the wider window. It is brighter than the border. So if the window started at 0.80,
    /// the lit box would stick to the window's own left edge. For example, x[512-613] with 512 at that edge
    /// would put the "centre" at 562.5. Narrowed to the column where the logo stands, the window measures
    /// x[550-613], with centre 581.5. That is half a pixel from the band's 581.0, which is correct.
    /// <see cref="The_name_is_centred_on_the_logo"/> depends on this window.
    /// </remarks>
    private static (double Run, double Centre, double Across) LogoInk(SKBitmap cover)
        => Ink(cover, 0.86, 1.00, 0.93, 0.99);

    private static (double Run, double Centre, double Across) Ink(
        SKBitmap cover, double left, double right, double top, double bottom)
    {
        var x0 = (int)(cover.Width * left);
        var x1 = Math.Min(cover.Width, (int)(cover.Width * right));
        var y0 = (int)(cover.Height * top);
        var y1 = Math.Min(cover.Height, (int)(cover.Height * bottom));

        double darkest = 1, brightest = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var luma = Luma(cover.GetPixel(x, y));
                darkest = Math.Min(darkest, luma);
                brightest = Math.Max(brightest, luma);
            }
        }

        var lit = darkest + ((brightest - darkest) * 0.4);

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (Luma(cover.GetPixel(x, y)) < lit)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX
            ? (0, 0, 0)
            : ((maxY - minY) + 1, (minX + maxX) / 2.0, (maxX - minX) + 1);
    }

    private static double Contrast(SKColor a, SKColor b)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(SKColor c) =>
            (0.2126 * Channel(c.Red)) + (0.7152 * Channel(c.Green)) + (0.0722 * Channel(c.Blue));

        var one = Luminance(a);
        var two = Luminance(b);
        return one >= two ? (one + 0.05) / (two + 0.05) : (two + 0.05) / (one + 0.05);
    }

    /// <summary>The same list is the same picture on every machine and after every restart. The moment the
    /// room is caught at is read from its own colours. That is what lets the still cover and the drifting
    /// card on the home screen be one picture.</summary>
    [Fact]
    public void The_moment_is_read_off_the_colours()
    {
        Assert.Equal(CardRoomArt.Still(RoomPalette.Fallback), CardRoomArt.Still(RoomPalette.Fallback));
        Assert.NotEqual(CardRoomArt.Still(RoomPalette.Fallback), CardRoomArt.Still(RoomPalette.Blues));
    }

    /// <summary>The brightest pixel and the average of one patch of a picture. The patch is given in
    /// fractions of the picture's own edges, so a wash drawn at another size is read at the same
    /// place.</summary>
    private static (double Most, double Mean) Patch(
        SKBitmap picture, double left, double right, double top, double bottom)
    {
        var x0 = (int)(picture.Width * left);
        var x1 = Math.Max(x0 + 1, (int)(picture.Width * right));
        var y0 = (int)(picture.Height * top);
        var y1 = Math.Max(y0 + 1, (int)(picture.Height * bottom));

        double most = 0, total = 0;
        var taken = 0;

        for (var y = y0; y < Math.Min(y1, picture.Height); y++)
        {
            for (var x = x0; x < Math.Min(x1, picture.Width); x++)
            {
                var luma = Luma(picture.GetPixel(x, y));
                most = Math.Max(most, luma);
                total += luma;
                taken++;
            }
        }

        return taken == 0 ? (0, 0) : (most, total / taken);
    }

    private static double Luma(SKColor c) =>
        ((0.2126 * c.Red) + (0.7152 * c.Green) + (0.0722 * c.Blue)) / 255.0;
}
