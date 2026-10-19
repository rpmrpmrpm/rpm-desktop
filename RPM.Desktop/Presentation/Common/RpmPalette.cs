using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The app's ten accent colours, and the rule for snapping any colour to the nearest of them.
///
/// <para>The colours are read from <c>Assets/RpmPalette.xaml</c>. They are not written out again here.
/// Each of the ten is a real drawer tint. The album wall calculates these over real covers, and they
/// were chosen for a varied spread of hues. A second copy in code would become a second set of colours
/// as soon as someone changed the first.</para>
///
/// <para><b>Why snap at all.</b> A photograph's dominant tone is whatever the biggest thing in the
/// frame was lit by, and that differs for every artist. Two thousand of these laid out as a wall give
/// two thousand colours and no palette. It looks like noise, even when each box is attractive. Snapped
/// to the palette, the wall uses the app's ten colours in many arrangements, and it reads as one
/// design.</para>
/// </summary>
public static class RpmPalette
{
    /// <summary>The palette in the order the asset lists it: warm, green, cool, then the magentas and
    /// reds. It is named rather than read out of the dictionary by position. So a colour that is
    /// renamed or dropped in the asset shows up as a missing colour on the wall, and not as a silently
    /// shorter palette.
    /// </summary>
    private static readonly string[] Keys =
    [
        "RpmVermilionColor",
        "RpmRustColor",
        "RpmGoldColor",
        "RpmLimeColor",
        "RpmForestColor",
        "RpmAzureColor",
        "RpmIndigoColor",
        "RpmMagentaColor",
        "RpmCrimsonColor",
        "RpmRedColor",
    ];

    /// <summary>The palette, read on the first request that can read it, and kept from then on.
    ///
    /// <para><b>This is not a <see cref="Lazy{T}"/>. The reason is a real bug, not a preference.</b>
    /// Reading the asset means setting <c>ResourceDictionary.Source</c>. Uno resolves that against a
    /// resource scope that it keeps per thread. So the read works only on the UI thread. Off the UI
    /// thread it throws <c>Stack empty</c> from inside the resolver. Almost everything that asks for a
    /// palette colour runs on a worker: the made sleeves, the generated covers, and the artists wall's
    /// snap. A <c>Lazy</c> keeps whatever the first caller got. If that caller is a background thread,
    /// it gets an empty list, and that empty list would then be the palette for the whole
    /// session.</para>
    ///
    /// <para>So an empty read is NOT an answer, and it is never kept. A worker that asks too early is
    /// told there is no palette yet. Every caller already handles that, because a dictionary that does
    /// not load is always possible. The next request after <see cref="Prime"/> gets the real palette.
    /// The value is written once and read without a lock. It is a whole list published with one
    /// reference write, so a reader that races sees either nothing or all ten.</para>
    /// </summary>
    private static IReadOnlyList<Entry> Loaded
    {
        get
        {
            if (Volatile.Read(ref _loaded) is { Count: > 0 } have)
                return have;

            // Off the UI thread there is nothing to read, and the attempt is what throws. So it is not
            // made. Without this check the read would throw on a worker thread.
            //
            // The ASKING is also caught. On a thread with no dispatcher at all, such as a headless test
            // host, the probe does not return null. It throws out of Uno's own dispatcher first. A
            // genre card's ink is decided here too, so any test that builds one would fail with a
            // NullReferenceException from inside the framework.
            if (Ui() is null)
                return [];

            var read = Read();
            if (read.Count > 0)
                Volatile.Write(ref _loaded, read);
            return read;
        }
    }

    private static IReadOnlyList<Entry>? _loaded;

    /// <summary>The dispatcher for this thread, or null where there is none. That includes the case
    /// where asking is itself the thing that fails.</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? Ui()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the palette now, on the thread that can.
    ///
    /// <para>Called once as the app starts, before there is a library to draw anything from. Everything
    /// that asks afterwards finds the answer already there. Most of those callers run on a worker
    /// thread, so they do not have to read the palette themselves.</para>
    /// </summary>
    public static void Prime() => _ = Loaded;

    /// <summary>One palette colour. It holds the colour itself, its reading in the space that the
    /// nearest match is measured in (lightness, chroma and hue), and one brush for it. So a wall of two
    /// thousand boxes holds ten brushes, not two thousand. A null <see cref="Brush"/> marks a stand-in
    /// entry (see <see cref="Override"/>). Only a test stand-in builds one. The real asset load always
    /// sets a brush.</summary>
    private sealed record Entry(
        Color Colour, double L, double Chroma, double Hue, Brush? Brush, IReadOnlyList<Color>? Ramp);

    /// <summary>The palette a test has supplied, in place of the asset.
    ///
    /// <para>The ten colours are in <c>Assets/RpmPalette.xaml</c> and are read through a
    /// <see cref="ResourceDictionary"/>. That needs a dispatcher, and a dispatcher exists only inside a
    /// running app. So anything that draws in the app's colours could not be tested by default. The
    /// drawing is the part worth testing, not the loading.</para>
    ///
    /// <para><see cref="Colours"/>, <see cref="Nearest"/> and the snap <see cref="NearestEntry"/> does
    /// for both all read this first. So a stand-in answers for the whole palette, not for half of it.
    /// See
    /// <see cref="NearestBrush"/> for the one piece that still cannot be answered this way, and
    /// why.</para>
    ///
    /// <para>This is always null in the app. Nothing outside a test sets it, so the load runs.</para>
    /// </summary>
    internal static IReadOnlyList<Color>? Override { get; set; }

    /// <summary>The colours as the asset lists them.
    ///
    /// <para>The same list is returned every time, not a new one on each request. Everything that draws
    /// a made sleeve reads this once per record. A wall of made sleeves can hold many records with no
    /// cover of their own. Rebuilding ten colours for each one would be a small cost in a hot path. The
    /// list is kept only once it has content, for the reason given at <see
    /// cref="Loaded"/>.</para></summary>
    public static IReadOnlyList<Color> Colours
        => Override
           ?? (_colours is { Count: > 0 } have ? have : _colours = [.. Loaded.Select(e => e.Colour)]);

    private static IReadOnlyList<Color>? _colours;

    /// <summary>
    /// The palette colour nearest to this one. If there is no answer, this is the colour itself. See
    /// <see cref="NearestBrush"/> for the cases where there is no answer.
    /// </summary>
    public static Color Nearest(Color colour) => NearestEntry(colour)?.Colour ?? colour;

    /// <summary>The same answer as a brush. Boxes that land on the same colour share one brush, so a
    /// wall of two thousand boxes uses ten brushes.
    ///
    /// <para>Returns null where there is nothing to snap to. There are three cases. First, the palette
    /// may not have loaded yet. A wall in the photographs' own colours is what it looked like before
    /// the palette existed. Second, the colour may have no colour in it. A black-and-white press shot
    /// reports a grey, and a grey is not nearer one hue than another. Snapped anyway, it lands on
    /// whichever accent is least saturated, so a monochrome portrait comes back washed in dark green.
    /// Both cases keep the tone the picture gave. For a grey photograph that is a grey box.</para>
    ///
    /// <para><b>The third case is under a test's <see cref="Override"/>.</b> A stand-in supplies ten
    /// colours, not ten brushes. <see cref="SolidColorBrush"/>, like <see cref="ResourceDictionary"/>,
    /// needs the dispatcher that the test host does not have. Creating one there throws the same
    /// <c>NullReferenceException</c> that the resource load throws. So an entry built from a stand-in
    /// colour has no brush. While an override is set, this returns null for every colour. That is
    /// correct for <see cref="Nearest"/>'s callers, who never ask for a brush. For code under test that
    /// draws with a brush, it is a real gap.</para>
    /// </summary>
    public static Brush? NearestBrush(Color colour) => NearestEntry(colour)?.Brush;

    private static Entry? NearestEntry(Color colour)
    {
        var palette = Override is { } stood ? StandInEntries(stood) : Loaded;
        if (palette.Count == 0)
            return null;

        var (l, chroma, hue) = Oklch(colour);
        if (chroma < NeutralChroma)
            return null;   // no colour in it to be near anything

        Entry? best = null;
        var bestDistance = double.MaxValue;

        foreach (var entry in palette)
        {
            var distance = Distance(l, chroma, hue, entry);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = entry;
        }

        return best;
    }

    /// <summary>How much colour a tone needs before it counts as having a colour at all. This is
    /// measured as Oklab chroma. The palette's own entries run from 0.09 (Forest) to 0.17 (Azure). A
    /// warm or cool grey reads about 0.015. A photograph with any visible colour cast, such as a pale
    /// beige or a muted navy, is above 0.023. The threshold sits between the two.</summary>
    private const double NeutralChroma = 0.018;

    /// <summary>How far apart two colours are, for the question "which of these ten is this one".
    ///
    /// <para>The distance is asked as three questions in Oklch: which HUE, how strong, and how bright.
    /// It is not a straight distance in Oklab, and the choice matters. A straight distance is dominated
    /// by whichever axis is longest. For a muted colour that is always lightness. A dark navy press
    /// shot came out nearer Forest than Azure. A weak blue and a dark green have only one thing in
    /// common, which is that they are dark, and that was the largest term in the sum. Asked hue first,
    /// a blue photograph stays blue however it was lit.</para>
    ///
    /// <para>Hue is circular, so the difference is taken the short way round. Hue has the most weight.
    /// Chroma decides which of two entries of one hue a washed-out colour belongs to. Lightness is kept
    /// at half weight, so that Lime and Forest, which share a hue at very different lightness, do not
    /// collapse into one another.</para>
    /// </summary>
    private static double Distance(double l, double chroma, double hue, Entry entry)
    {
        var dh = Math.Abs(hue - entry.Hue);
        if (dh > Math.PI)
            dh = (2 * Math.PI) - dh;

        var dc = (chroma - entry.Chroma) * ChromaWeight;
        var dl = (l - entry.L) * LightnessWeight;

        return (dh * dh) + (dc * dc) + (dl * dl);
    }

    private const double ChromaWeight = 1.0;
    private const double LightnessWeight = 0.5;

    /// <summary>A colour in the form the match uses: Oklab turned to polar coordinates. These are the
    /// same three numbers a person uses. They are how bright the colour is, how much colour it has, and
    /// which colour it is.</summary>
    public static (double L, double Chroma, double Hue) Oklch(Color colour)
    {
        var (l, a, b) = Oklab(colour.R, colour.G, colour.B);
        return (l, Math.Sqrt((a * a) + (b * b)), Math.Atan2(b, a));
    }

    /// <summary>The asset, read once, into this class's own dictionary.
    ///
    /// <para>It is loaded here, and not merged into the application's resources. This matters. The
    /// accent set has a <c>RpmRedColor</c>, and so does the app's brand dictionary. They are two
    /// different reds. One is a vivid red tint, and the other is the soft #FF6B6B that a failed upload
    /// uses. Merged app-wide, the palette would load after Generic.xaml and quietly repaint every
    /// failure in the app. Kept private, the ten accents belong only to the artists wall.</para>
    ///
    /// <para>This is best effort, like all the art code. If the dictionary does not load, the wall
    /// keeps the photographs' own colours. That is how it looks without the snap.</para>
    /// </summary>
    private static IReadOnlyList<Entry> Read()
    {
        var entries = new List<Entry>(Keys.Length);

        try
        {
            var asset = new ResourceDictionary { Source = new Uri("ms-appx:///Assets/RpmPalette.xaml") };

            foreach (var key in Keys)
            {
                // A key can be missing if the dictionary has been edited. A missing key is skipped
                // rather than thrown. The cost is one accent that the wall never uses.
                if (asset.TryGetValue(key, out var value) && value is Color colour)
                {
                    var (l, chroma, hue) = Oklch(colour);
                    entries.Add(new Entry(colour, l, chroma, hue, new SolidColorBrush(colour), Ramp(asset, key)));
                }
            }
        }
        catch
        {
            return [];
        }

        return entries;
    }

    /// <summary>The three tones the asset already draws this accent as, lightest first.
    ///
    /// <para>The palette ships with more than ten colours. Beside each <c>Rpm&lt;Name&gt;Color</c>
    /// there is an
    /// <c>Rpm&lt;Name&gt;Gradient</c>. That is the drawer's own ramp over that tint, and it is part of
    /// the curated set, just as the tint is. A surface that needs the app's colours, and not a
    /// calculation based on them, reads this. Nothing is computed, so nothing can drift from the
    /// asset.</para>
    ///
    /// <para>Null where the entry has no gradient beside it. This happens with a re-curated dictionary,
    /// or with a test's stand-in, which gives ten colours and no brushes.</para>
    /// </summary>
    private static IReadOnlyList<Color>? Ramp(ResourceDictionary asset, string colourKey)
    {
        var key = colourKey[..^"Color".Length] + "Gradient";
        if (!asset.TryGetValue(key, out var value) || value is not LinearGradientBrush ramp)
            return null;

        var stops = ramp.GradientStops
            .OrderBy(stop => stop.Offset)
            .Select(stop => stop.Color)
            .ToList();

        return stops.Count >= 2 ? stops : null;
    }

    /// <summary>The asset's own ramp for whichever accent a colour snaps to, lightest first.
    ///
    /// <para>Ordered by lightness, not by the offsets it was written at. Every caller wants lightest
    /// first, and the asset's order is a convention, not a promise. In the current asset the two orders
    /// agree for all ten.</para>
    ///
    /// <para>Null where there is nothing to snap to, or where that entry has no gradient. See
    /// <see cref="NearestBrush"/>. It returns null in the same two cases, for the same reasons.</para>
    /// </summary>
    public static IReadOnlyList<Color>? RampFor(Color colour)
    {
        if (NearestEntry(colour)?.Ramp is not { Count: >= 2 } ramp)
            return null;

        return Lightness(ramp[0]) >= Lightness(ramp[^1]) ? ramp : [.. ramp.Reverse()];
    }

    /// <summary>How light a colour is, for putting a ramp the right way round and nothing else.</summary>
    private static double Lightness(Color colour)
        => (0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B);

    /// <summary>A stand-in's colours, read into the same shape that <see cref="Read"/> builds from the
    /// asset. Oklch is worked out fresh on each call, not once. A stand-in is set and cleared far more
    /// often than the real asset loads, and it is only ten colours deep. It has no brush. See <see
    /// cref="NearestBrush"/> for why one cannot be built here.
    /// </summary>
    private static IReadOnlyList<Entry> StandInEntries(IReadOnlyList<Color> colours)
        => colours.Select(colour =>
        {
            var (l, chroma, hue) = Oklch(colour);
            return new Entry(colour, l, chroma, hue, Brush: null, Ramp: null);
        }).ToList();

    /// <summary>sRGB to Oklab. The space is built so that the distance between two colours is roughly
    /// how different they look. Plain RGB distance does not do this. It puts two dark colours close
    /// together however different their hues are, and puts two bright colours far apart however similar
    /// they are.
    ///
    /// <para>Björn Ottosson's matrices, unaltered, with the sRGB transfer curve undone first. They are
    /// written out here because the calculation is nine multiplications and a cube root. The app has no
    /// colour-management dependency to use instead.</para>
    /// </summary>
    public static (double L, double A, double B) Oklab(byte red, byte green, byte blue)
    {
        double r = Linear(red), g = Linear(green), b = Linear(blue);

        var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));

        return (
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>One channel, with the sRGB transfer curve undone. The stored byte is a display
    /// encoding. Averaging or comparing colours in that encoding compares the encoding, not the
    /// light.</summary>
    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
