using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The genres' lettering, as the app reads it back.
///
/// <para>These tests guard a seam between two languages. A Python generator writes the letterforms in a
/// four-command format, and a C# reader parses them back. The compiler does not check that the two agree.
/// The failure is silent: a card can draw a blank rectangle, or letters with their counters filled in. So
/// the tests check it.</para>
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class GenreWordmarkTests
{
    /// <summary>The wall draws a card for every shelf the app files music under, plus its own word for
    /// music it cannot file. A shelf added to subgenres.json without a run of the generator would reach the
    /// wall as a card with no name on it.</summary>
    [Fact]
    public void Every_genre_the_app_files_music_under_has_a_wordmark()
    {
        foreach (var genre in Genres.All)
            Assert.True(GenreWordmarks.For(genre) is not null, $"no wordmark for {genre}");

        Assert.NotNull(GenreWordmarks.For(RPM.Core.Library.GenreWall.Unclassified));
    }

    /// <summary>The table is keyed the way the rest of the app compares genre names.</summary>
    [Fact]
    public void A_genre_is_found_however_it_is_cased()
    {
        Assert.Equal(GenreWordmarks.For("Rock"), GenreWordmarks.For("rock"));
        Assert.Null(GenreWordmarks.For("Skiffle"));
    }

    /// <summary>
    /// Every wordmark parses into a drawing with something in it.
    ///
    /// <para>The reader ignores anything it does not recognise. That makes it safe against a generator that
    /// has changed. It is also why a fully unreadable string would come back as an empty geometry that
    /// draws nothing. So the test checks the count, not just the absence of an exception.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFlush))]
    public void Every_wordmark_reads_back_as_a_drawing(string genre, GenreFlush flush)
    {
        var mark = GenreWordmarks.For(genre);
        Assert.NotNull(mark);

        var drawing = GenreWordmarks.Read(mark!.On(flush));

        // A letter is at least one closed contour, and the shortest name here is three of them.
        Assert.True(drawing.Count >= 3, $"{genre} came back with {drawing.Count} contours");
        Assert.All(drawing, contour =>
        {
            Assert.True(contour.Closed, $"{genre} has a contour that does not come back");
            Assert.NotEmpty(contour.Steps);
        });
    }

    /// <summary>
    /// Every drawing declares the non-zero fill letterforms are wound for.
    ///
    /// <para>The check reads the string, not a built geometry, because a geometry cannot be built without a
    /// window. The reader sets the fill rule because the generator promises F1, and this test checks that
    /// promise. Under even-odd, which is what a Path assumes when nobody says, the inside of every O is
    /// punched out. A script face would also perforate itself wherever it crosses its own stroke.</para>
    /// </summary>
    [Fact]
    public void Every_drawing_is_wound_the_way_letters_are_filled()
    {
        foreach (var genre in Genres.All)
        {
            var mark = GenreWordmarks.For(genre)!;
            foreach (var flush in new[] { GenreFlush.Left, GenreFlush.Right })
                Assert.StartsWith("F1 ", mark.On(flush), StringComparison.Ordinal);
        }
    }

    /// <summary>The reader knows four commands and quietly ignores everything else. This test checks the
    /// shape of that agreement.</summary>
    [Fact]
    public void The_reader_knows_four_commands()
    {
        var read = GenreWordmarks.Read("F1 M0,0 L10,0 C10,5 5,10 0,10 Z M20,20 L30,20");

        Assert.Equal(2, read.Count);

        Assert.True(read[0].Closed);
        Assert.Equal(new Windows.Foundation.Point(0, 0), read[0].Start);
        Assert.Equal(2, read[0].Steps.Count);
        Assert.False(read[0].Steps[0].Curve);
        Assert.Equal(new Windows.Foundation.Point(10, 0), read[0].Steps[0].End);
        Assert.True(read[0].Steps[1].Curve);
        Assert.Equal(new Windows.Foundation.Point(10, 5), read[0].Steps[1].One);
        Assert.Equal(new Windows.Foundation.Point(5, 10), read[0].Steps[1].Two);
        Assert.Equal(new Windows.Foundation.Point(0, 10), read[0].Steps[1].End);

        // A contour that the generator never closed is still returned. Half a letter on screen says more
        // about what went wrong than no letter at all.
        Assert.False(read[1].Closed);
        Assert.Single(read[1].Steps);
    }

    /// <summary>Negative coordinates are normal. A face whose letters overhang their own origin puts ink to
    /// the left of it, and the generator does not move the drawing to hide that.</summary>
    [Fact]
    public void The_reader_reads_signs()
    {
        var read = GenreWordmarks.Read("F1 M-4,-8 L12,-3 Z");

        Assert.Equal(new Windows.Foundation.Point(-4, -8), read[0].Start);
        Assert.Equal(new Windows.Foundation.Point(12, -3), read[0].Steps[0].End);
    }

    /// <summary>
    /// A name set on one line is one drawing however it is flushed, and the generator writes it out
    /// once. A name on two lines is two drawings, and the two columns of the wall want different ones.
    /// </summary>
    [Fact]
    public void A_name_on_more_than_one_line_is_set_both_ways()
    {
        var single = GenreWordmarks.For("Rock")!;
        Assert.Null(single.Right);
        Assert.Equal(single.Left, single.On(GenreFlush.Right));

        var wrapped = GenreWordmarks.For("Old School Hip Hop")!;
        Assert.NotNull(wrapped.Right);
        Assert.NotEqual(wrapped.On(GenreFlush.Left), wrapped.On(GenreFlush.Right));
    }

    /// <summary>
    /// The two settings of one name are the same words at the same size.
    ///
    /// <para>This is what lets both columns use the shape of the same record. They are given one
    /// <c>Aspect</c> between them. The generator measures the flush-left setting and scales both by it.
    /// This check makes sure it has not started measuring each setting on its own. If it did, one column
    /// would set its names a size larger than the other, from a number that claims to describe both.</para>
    /// </summary>
    [Fact]
    public void The_settings_of_one_name_are_one_size()
    {
        foreach (var genre in Genres.All)
        {
            var mark = GenreWordmarks.For(genre)!;
            var boxes = new[] { GenreFlush.Left, GenreFlush.Right }
                .Select(flush => Box(mark.On(flush)))
                .ToArray();

            foreach (var box in boxes)
            {
                // The height is the scale, and it is exact. Flushing a name moves its lines across each
                // other, and never up or down.
                Assert.Equal(boxes[0].Height, box.Height, 0);

                // The width is within a percent or two, and cannot be exact. The box is measured once, on
                // the flush-left setting. Each line's ink sits differently inside its own advance. So the
                // line that is widest by advance is not always the one whose ink reaches furthest. Moving
                // the lines about can move which edge of which line is the outermost, by a sidebearing.
                // This is not a setting drawn at a different size, which is what this test is here to
                // catch.
                Assert.InRange(box.Width / boxes[0].Width, 0.98, 1.02);
            }

            // The aspect on the record is roughly that shape. It is only roughly, because the two measure
            // different things. The generator measures the curve. Here the test walks the hull of the
            // handles that pull the curve, and that stands a little outside the ink on both axes. A few
            // percent comes from the handles. A wrong Aspect would put a name in a box of the wrong shape,
            // which is tens of percent out.
            Assert.InRange(boxes[0].Width / boxes[0].Height / mark.Aspect, 0.94, 1.06);
        }
    }

    /// <summary>Every drawing stands at the origin and is a thousand units tall. So a caller that wants a
    /// given height knows the width without measuring. A drawing with an offset would be set adrift of the
    /// box the card gives it, by an amount that nothing on screen explains.
    ///
    /// <para>The check is loose. The slack comes from the control points: the generator measures the curve,
    /// and this test measures the hull of the handles that pull it. On a face with steep curves, the hull
    /// reaches a few units past the ink at each edge. The test catches a drawing that is off by hundreds,
    /// not one that is off by three.</para>
    /// </summary>
    [Fact]
    public void Every_drawing_stands_at_the_origin()
    {
        const double slack = 30;

        foreach (var genre in Genres.All)
        {
            var box = Box(GenreWordmarks.For(genre)!.On(GenreFlush.Left));

            Assert.InRange(box.Left, -slack, slack);
            Assert.InRange(box.Top, -slack, slack);
            Assert.InRange(box.Height, 1000 - slack, 1000 + slack * 2);
        }
    }

    /// <summary>
    /// The name is written on the lightest tone of its shelf's own gradient. So whether it can be read is a
    /// question about two colours, and it has an answer by arithmetic. The check uses the contrast that
    /// large lettering needs, not the contrast for body text, because these names are 40 points and up.
    ///
    /// <para>The check uses the light end of the gradient, not the bare tint, because that is what the
    /// letters stand on. See <c>GenreWordmarks.Lit</c>. The gradients are read from the asset here, not
    /// through <c>RpmPalette</c>, which needs a dispatcher that no test in this project has. The file is
    /// the same source of truth that the generator reads. A copy of the ten colours in a test would be a
    /// second set of colours, which the whole arrangement exists to prevent.</para>
    /// </summary>
    [Fact]
    public void Every_name_can_be_read_off_the_tone_it_stands_on()
    {
        var ramps = Ramps();

        foreach (var genre in Genres.All)
        {
            var mark = GenreWordmarks.For(genre)!;
            var tint = GenreWordmarks.ColourOf(mark);
            var lit = ramps[Hex(tint)][0];
            var ink = Contrast(Paper, lit) >= 3.0 ? Paper : Ink;

            Assert.True(Contrast(ink, lit) >= 3.0,
                $"{genre}: {Contrast(ink, lit):F2}:1 between the name and the tone it stands on");
        }
    }

    /// <summary>
    /// Every accent in the asset ships a gradient beside it, of at least two stops, lightest first.
    ///
    /// <para>The genre wall and the head of every genre's page are drawn from these gradients, and from
    /// nothing else. The tile's ground, the fall under the name, the veil down the card, and the band
    /// across the head are the three tones of one of these ramps. An accent that is changed without its
    /// gradient, or whose gradient is written dark end first, silently drops those surfaces back to a
    /// fallback that nobody is checking.</para>
    /// </summary>
    [Fact]
    public void Every_accent_ships_a_gradient_and_it_runs_light_to_dark()
    {
        var asset = File.ReadAllText(Path.Combine(
            RepoRoot(), "RPM.Desktop", "Assets", "RpmPalette.xaml"));

        var accents = Regex.Matches(asset, @"<Color x:Key=""Rpm(\w+)Color"">#([0-9A-Fa-f]{8})</Color>");
        Assert.Equal(10, accents.Count);

        var ramps = Ramps();
        foreach (Match accent in accents)
        {
            var tint = "#" + accent.Groups[2].Value.ToUpperInvariant()[2..];
            Assert.True(ramps.ContainsKey(tint),
                $"{accent.Groups[1].Value} has no Rpm{accent.Groups[1].Value}Gradient beside it");

            var ramp = ramps[tint];
            Assert.True(ramp.Count >= 2, $"{accent.Groups[1].Value}'s gradient has one stop");

            for (var at = 1; at < ramp.Count; at++)
                Assert.True(Grey(ramp[at]) < Grey(ramp[at - 1]),
                    $"{accent.Groups[1].Value}'s gradient does not run light to dark");
        }
    }

    /// <summary>The asset's gradients, by the tint they sit beside. They are parsed rather than loaded, for
    /// the reason given in the test above.</summary>
    private static Dictionary<string, List<Windows.UI.Color>> Ramps()
    {
        var asset = File.ReadAllText(Path.Combine(
            RepoRoot(), "RPM.Desktop", "Assets", "RpmPalette.xaml"));

        var ramps = new Dictionary<string, List<Windows.UI.Color>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match block in Regex.Matches(
            asset,
            @"<Color x:Key=""Rpm(?<name>\w+)Color"">#[0-9A-Fa-f]{2}(?<tint>[0-9A-Fa-f]{6})</Color>\s*"
            + @"<LinearGradientBrush x:Key=""Rpm\k<name>Gradient""[^>]*>(?<stops>.*?)</LinearGradientBrush>",
            RegexOptions.Singleline))
        {
            var stops = Regex.Matches(block.Groups["stops"].Value, @"Color=""#[0-9A-Fa-f]{2}([0-9A-Fa-f]{6})""")
                .Select(m => Rgb(m.Groups[1].Value))
                .ToList();

            if (stops.Count > 0)
                ramps["#" + block.Groups["tint"].Value.ToUpperInvariant()] = stops;
        }

        Assert.Equal(10, ramps.Count);
        return ramps;
    }

    /// <summary>The two inks a name is ever written in. They are GenreWordmarks' own colours, copied here
    /// because the rule under test picks BETWEEN them. A test that read them from the code it checks would
    /// pass whatever they became.</summary>
    private static readonly Windows.UI.Color Paper = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Windows.UI.Color Ink = Windows.UI.Color.FromArgb(0xFF, 0x14, 0x14, 0x18);

    private static Windows.UI.Color Rgb(string hex) => Windows.UI.Color.FromArgb(
        0xFF,
        Convert.ToByte(hex[..2], 16),
        Convert.ToByte(hex[2..4], 16),
        Convert.ToByte(hex[4..], 16));

    private static string Hex(Windows.UI.Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>
    /// Every shelf wears one of the app's own accent colours, and the wall is a SPREAD of them.
    ///
    /// <para>Thirty-six shelves over ten colours means that shelves share colours, on purpose, as the
    /// artists wall shares them. What tells two shelves of one colour apart is the typeface and the finish,
    /// not the hue. So the test does not check that the colours differ. It checks that the palette has not
    /// grown a thirty-seventh entry, and that no one colour has become what the wall looks like.</para>
    ///
    /// <para>The ten colours are not written out here. They live in <c>Assets/RpmPalette.xaml</c>, and the
    /// generator resolves against that file. A copy in a test would be a second set of colours, which the
    /// whole arrangement exists to prevent. See <see cref="RPM.Desktop.Presentation.Common.RpmPalette"/>,
    /// which reads the asset for the same reason.</para>
    /// </summary>
    [Fact]
    public void Every_shelf_wears_a_palette_colour_and_no_colour_carries_the_wall()
    {
        var worn = Genres.All
            .Append(RPM.Core.Library.GenreWall.Unclassified)
            .Select(name => GenreWordmarks.For(name)!.Colour)
            .ToArray();

        var distinct = worn.Distinct().ToArray();
        Assert.InRange(distinct.Length, 2, PaletteSize);

        foreach (var colour in distinct)
        {
            Assert.Equal(0xFFu, colour >> 24);       // a palette entry is opaque; a fall adds the alpha

            var carried = worn.Count(c => c == colour);
            Assert.True(carried <= MostPerColour,
                $"{carried} shelves wear 0x{colour:X8}, and the wall wants at most {MostPerColour}");
        }
    }

    /// <summary>
    /// A shelf wears a palette entry AS IT IS, and the artists wall's own ground is what draws it.
    ///
    /// <para>The generator must not darken the palette on its way into the table. It once darkened the cold
    /// half of the palette: any colour between 60 and 300 degrees was multiplied towards black until it was
    /// no lighter than a quarter. The reason given was that the ten accents are tuned for chips and rails,
    /// and that a lime across a quarter of the screen reads as fluorescent.</para>
    ///
    /// <para>The artists wall carries the same ten accents, on the same size of tile, in the same
    /// collection. It rewrites none of them. It takes the entry as it is, and dims it when it paints,
    /// evenly, with every hue the same. If two transforms were applied to one palette, a card could come
    /// out olive beside an artist's box in lime, and neither card would show why. So the generator changes
    /// nothing, and the card asks <c>ArtistGround</c>, which is the one place that answers this
    /// question.</para>
    ///
    /// <para>The test uses the palette's own snap, not a list of colours. An entry snapped to its nearest
    /// entry is itself. Anything the generator had altered would snap back to the entry it was altered
    /// FROM. So this test fails as soon as a second transform appears, wherever it is and whatever shape it
    /// takes.</para>
    /// </summary>
    [Fact]
    public void Every_shelf_wears_its_palette_colour_unaltered()
    {
        using var palette = new PaletteStandIn();

        foreach (var mark in GenreWordmarks.Everything)
        {
            var worn = GenreWordmarks.ColourOf(mark);

            Assert.Equal(RpmPalette.Nearest(worn), worn);
        }
    }

    // ── the letters are not flat ──

    /// <summary>
    /// Light ink is lit at the top of the letters and deepened at the line they stand on, as a solid object
    /// would be. Nothing is added above it. There is nowhere above white to go, so the crown is the ink
    /// itself, and the depth is taken from the rest.
    /// </summary>
    [Fact]
    public void A_light_name_keeps_its_crown_and_deepens_to_its_foot()
    {
        var ink = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var (crown, body, foot) = GenreWordmarks.Shading(ink);

        Assert.Equal(ink, crown);
        Assert.True(Grey(body) < Grey(crown), "the body of the letter is under its crown");
        Assert.True(Grey(foot) < Grey(body), "and its foot is under the body");
    }

    /// <summary>Dark ink works the other way round. It has room above it and very little below, so the
    /// crown is a lift towards the light and the foot barely moves. A dark letter on a bright shelf loses
    /// its edges long before a light one does.</summary>
    [Fact]
    public void A_dark_name_is_lifted_at_the_crown_rather_than_crushed_at_the_foot()
    {
        var ink = Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        var (crown, body, foot) = GenreWordmarks.Shading(ink);

        Assert.True(Grey(crown) > Grey(ink), "a dark name is lit at the top rather than left flat");
        Assert.Equal(ink, body);
        Assert.True(Grey(foot) < Grey(body), "and still settles towards its foot");
    }

    /// <summary>
    /// However it is lit, it is still one colour.
    ///
    /// <para>The limit is what keeps this a shelf's name, and not a chrome bevel. Every shelf's ink is put
    /// through it. So the test is about the RULE, not about the two colours above. The moment the foot of a
    /// letter reads as a different colour from its top, the effect is no longer depth. It has become a
    /// gradient that someone applied.</para>
    /// </summary>
    [Fact]
    public void The_lighting_never_becomes_a_second_colour()
    {
        foreach (var mark in GenreWordmarks.Everything)
        {
            var ink = GenreWordmarks.InkOn(mark);
            var (crown, _, foot) = GenreWordmarks.Shading(ink);

            var span = Math.Abs(Grey(crown) - Grey(foot));
            Assert.True(span <= MostLighting,
                $"{mark.Genre}: {span:P0} between the crown and the foot of its letters, which is a bevel");

            Assert.Equal(ink.A, crown.A);
            Assert.Equal(ink.A, foot.A);
        }
    }

    /// <summary>How far apart two tones may look across one letter. A quarter of the range, and no more.
    /// That is enough for the foot of a stroke to be visibly under its crown, and little enough that nobody
    /// reads the two ends as two colours.</summary>
    private const double MostLighting = 0.25;

    /// <summary>
    /// How light a colour is, 0..1, on the sRGB values as they are written.
    ///
    /// <para>This is NOT straightened, unlike the reading that the ink CHOICE is made on. That difference
    /// is the reason this is written out here rather than shared. Straightened luminance answers "how much
    /// light is there". That is the right question for whether a name can be read off its shelf. This
    /// function asks "how far apart do these two look". On that question the straightened figure misleads
    /// at the top of the range. For example, white against white at 82% is 18% of the way down as people
    /// see it, but 36% in linear light.</para>
    /// </summary>
    private static double Grey(Windows.UI.Color colour)
        => ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255.0;

    /// <summary>The palette has ten entries, and the generator refuses more than five shelves on any one of
    /// them. Both numbers belong to the generator. These are the app's side of the same agreement. A change
    /// to either shows up here, rather than as a wall that has gone one colour.</summary>
    private const int PaletteSize = 10;
    private const int MostPerColour = 5;

    /// <summary>Both finishes are in use. A wall that drifted to all frost, or all glass, would still pass
    /// every other test here, but it would have lost half of what the finishes are for.</summary>
    [Fact]
    public void Both_finishes_are_worn()
    {
        var finishes = Genres.All
            .Append(RPM.Core.Library.GenreWall.Unclassified)
            .Select(name => GenreWordmarks.For(name)!.Finish)
            .ToArray();

        Assert.Contains(GenreFinish.Glass, finishes);
        Assert.Contains(GenreFinish.Frost, finishes);
        Assert.InRange(finishes.Count(f => f == GenreFinish.Glass), 8, finishes.Length - 8);
    }

    // ── the colour a card is drawn in ──

    /// <summary>
    /// A photograph shot in browns is drawn in purple, not in the palette's brown.
    ///
    /// <para>Rust is what warm light, wood and brick all snap to. It is also the colour that made the wall
    /// look flat. The substitution is the one place where a card's colour is not the nearest palette entry,
    /// so it is checked here. The accent is not Rust. Its three tones come from the palette's own dimming
    /// rule, applied to whatever colour it is instead. That keeps a colour that the asset does not ship
    /// from dropping the card onto a flat-colour fallback.</para>
    /// </summary>
    [Fact]
    public void A_brown_photograph_is_drawn_in_something_else()
    {
        using var palette = new PaletteStandIn();

        var mark = GenreWordmarks.For("Rock")!;
        var brown = Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5A, 0x2B);

        Assert.Equal(Windows.UI.Color.FromArgb(0xFF, 0xCC, 0x6E, 0x11), RpmPalette.Nearest(brown));

        var accent = GenreAccent.From(mark, brown);
        Assert.NotNull(accent);
        Assert.NotEqual(Windows.UI.Color.FromArgb(0xFF, 0xCC, 0x6E, 0x11), accent!.Value.Colour);
        Assert.True(accent!.Value.Colour.B > accent!.Value.Colour.R && accent!.Value.Colour.R > accent!.Value.Colour.G,
            $"the stand-in for brown is not a purple: {accent!.Value.Colour}");

        var tones = accent!.Value.Tones;
        Assert.NotNull(tones);
        Assert.Equal(3, tones!.Count);
        foreach (var (tone, by) in tones.Zip(new[] { 0.62, 0.51, 0.40 }))
            Assert.Equal(
                Windows.UI.Color.FromArgb(
                    0xFF,
                    (byte)(accent!.Value.Colour.R * by), (byte)(accent!.Value.Colour.G * by), (byte)(accent!.Value.Colour.B * by)),
                tone);
    }

    /// <summary>Every other photograph keeps the palette entry it snapped to. Brown is the only one the
    /// wall swaps. A substitution that spread to two colours would leave the wall drawing neither of them,
    /// and nothing here would show it.</summary>
    [Theory]
    [InlineData(0x21, 0x5F, 0xA8)]   // a blue backdrop
    [InlineData(0x88, 0xC0, 0x3A)]   // a green one
    [InlineData(0xD8, 0x2A, 0x24)]   // a red jacket
    public void Every_other_photograph_keeps_what_it_snapped_to(int r, int g, int b)
    {
        using var palette = new PaletteStandIn();

        var mark = GenreWordmarks.For("Rock")!;
        var shot = Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b);

        Assert.Equal(RpmPalette.Nearest(shot), GenreAccent.From(mark, shot)?.Colour);
    }

    /// <summary>A pinned shelf keeps its colour whatever its photograph shows. Country is the palette's
    /// Rust, and Latin its Red. Both are pinned, because photographs of these shelves often come back
    /// brown.</summary>
    [Theory]
    [InlineData("Country", 0xCC, 0x6E, 0x11)]
    [InlineData("Latin", 0xEE, 0x07, 0x09)]
    [InlineData("Techno", 0xAC, 0x52, 0x8D)]
    public void A_pinned_shelf_ignores_its_photograph(string genre, int r, int g, int b)
    {
        using var palette = new PaletteStandIn();

        var mark = GenreWordmarks.For(genre)!;
        var pinned = Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b);

        foreach (var shot in new[]
        {
            Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5A, 0x2B),   // brown
            Windows.UI.Color.FromArgb(0xFF, 0x21, 0x5F, 0xA8),   // blue
            Windows.UI.Color.FromArgb(0xFF, 0x88, 0xC0, 0x3A),   // green
        })
            Assert.Equal(pinned, GenreAccent.From(mark, shot)?.Colour);
    }

    /// <summary>
    /// A photograph with no colour in it does not dress the card, and the shelf keeps its own colour.
    ///
    /// <para>A white wall, a black stage and an overcast sky are common in press photos. None of them snaps
    /// to a palette accent. <c>RpmPalette.Nearest</c> returns the tone it was given, so a card that used it
    /// would come out white or grey. That is fine for an artist's box, where the colour is a veil over a
    /// drained photograph. Here the colour IS the card.</para>
    /// </summary>
    [Theory]
    [InlineData(0xF4, 0xF2, 0xEF)]   // a white studio wall
    [InlineData(0x1A, 0x1A, 0x1C)]   // a black stage
    [InlineData(0x8B, 0x8D, 0x90)]   // an overcast sky
    public void A_colourless_photograph_does_not_dress_the_card(int r, int g, int b)
    {
        using var palette = new PaletteStandIn();

        var mark = GenreWordmarks.For("Rock")!;
        var shot = Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b);

        Assert.Null(GenreAccent.From(mark, shot));
    }

    /// <summary>The finish belongs to the genre, whichever colour the card ends up in. Glass or frost says
    /// what kind of music this is, and a photograph has no say in it.</summary>
    [Fact]
    public void The_finish_is_the_genres_whatever_the_photograph_says()
    {
        using var palette = new PaletteStandIn();

        foreach (var mark in GenreWordmarks.Everything)
            Assert.Equal(
                mark.Finish,
                GenreAccent.From(mark, Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5A, 0x2B))?.Finish);
    }

    // ── the head's own height ──

    /// <summary>
    /// The genre page's header is exactly two tabs of the library rail beside it. The rail's style sets the
    /// same number as the rail's own constant.
    ///
    /// <para>There are two files and no binding between them. A XAML style setter cannot read a constant,
    /// so the test holds the agreement. A header that is nearly two tabs high would nearly line up with the
    /// rail running down the same card. That looks worse than either exact answer.</para>
    /// </summary>
    [Fact]
    public void The_head_is_two_rail_tabs_tall()
    {
        var rail = File.ReadAllText(Path.Combine(
            RepoRoot(), "RPM.Desktop", "Presentation", "Main", "RightPane", "RightRail.xaml"));

        var setter = Regex.Match(rail, @"<Setter Property=""Height"" Value=""(?<h>[0-9.]+)""/>");
        Assert.True(setter.Success, "the rail's tab style no longer sets a height");
        Assert.Equal(
            RPM.Desktop.Presentation.Main.RightPane.RightRail.OneTab,
            double.Parse(setter.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(
            RPM.Desktop.Presentation.Main.RightPane.RightRail.OneTab * 2,
            GenreDetailView.BandHeight);

        var page = File.ReadAllText(Path.Combine(
            RepoRoot(), "RPM.Desktop", "Presentation", "Main", "Genres", "GenreDetailView.xaml"));

        var band = Regex.Match(page, @"<Grid x:Name=""BannerBox""[^>]*?Height=""(?<h>[0-9.]+)""");
        Assert.True(band.Success, "the head no longer states a height in the markup");
        Assert.Equal(
            GenreDetailView.BandHeight,
            double.Parse(band.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IEnumerable<object[]> EveryFlush() =>
        from genre in Genres.All.Append(RPM.Core.Library.GenreWall.Unclassified)
        from flush in new[] { GenreFlush.Left, GenreFlush.Right }
        select new object[] { genre, flush };

    // ── measuring what came back ──

    /// <summary>The drawing's own bounds, walked by hand rather than asked for. A geometry's ComputeBounds
    /// is the renderer's job, and these tests do not start a renderer. The walk uses control points only,
    /// which is enough. A curve never leaves the hull of its own handles. The generator's own measurement
    /// is on the curve, so this gives a bound and not an equality.</summary>
    private static Windows.Foundation.Rect Box(string data)
    {
        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, foot = double.MinValue;

        void At(Windows.Foundation.Point point)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            foot = Math.Max(foot, point.Y);
        }

        foreach (var contour in GenreWordmarks.Read(data))
        {
            At(contour.Start);
            foreach (var step in contour.Steps)
            {
                if (step.Curve)
                {
                    At(step.One);
                    At(step.Two);
                }
                At(step.End);
            }
        }

        return new Windows.Foundation.Rect(left, top, right - left, foot - top);
    }

    private static double Contrast(Windows.UI.Color a, Windows.UI.Color b)
    {
        var (high, low) = (Math.Max(Lightness(a), Lightness(b)), Math.Min(Lightness(a), Lightness(b)));
        return (high + 0.05) / (low + 0.05);
    }

    private static double Lightness(Windows.UI.Color colour)
    {
        static double Straighten(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Straighten(colour.R / 255.0)
             + 0.7152 * Straighten(colour.G / 255.0)
             + 0.0722 * Straighten(colour.B / 255.0);
    }

    /// <summary>How far apart two colours look, as a plain CIE Lab distance. This is the same measure that
    /// the generator holds the palette to.</summary>
    private static double Apart(Windows.UI.Color a, Windows.UI.Color b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) Lab(Windows.UI.Color colour)
    {
        static double Straighten(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double Curve(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;

        var r = Straighten(colour.R / 255.0);
        var g = Straighten(colour.G / 255.0);
        var b = Straighten(colour.B / 255.0);

        var x = Curve((r * .4124564 + g * .3575761 + b * .1804375) / .95047);
        var y = Curve(r * .2126729 + g * .7151522 + b * .0721750);
        var z = Curve((r * .0193339 + g * .1191920 + b * .9503041) / 1.08883);

        return (116 * y - 16, 500 * (x - y), 200 * (y - z));
    }
}
