using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// A disc found in a photograph, in the coordinates of the picture it was found in.
/// </summary>
/// <param name="CenterX">Where the disc's middle is.</param>
/// <param name="CenterY">Where the disc's middle is.</param>
/// <param name="Radius">The outer edge: the rim of a CD, or the rim of the record for a photograph of a whole
/// vinyl side.</param>
/// <param name="LabelRadius">The printed label in the middle, when the picture is of a record rather than of the
/// label alone. Equal to <paramref name="Radius"/> when no inner edge was found. That is what a straight scan of
/// a label looks like.</param>
/// <param name="HoleRadius">The spindle hole, when the background shows through it. Zero when it does not. That
/// is the case for every scan where the hole was filled in, or where the disc was photographed on itself.</param>
/// <param name="Score">How much like a disc the thing found actually is, from 0 to 1. A square sleeve scores low.
/// See <see cref="DiscStamp.Detect(byte[])"/>.</param>
public sealed record DiscFind(
    float CenterX, float CenterY, float Radius, float LabelRadius, float HoleRadius, double Score);

/// <summary>
/// A picture of a record, and whether it is a picture of the WHOLE record.
///
/// <para>The two are one answer, because they are never useful apart. Every surface that draws a disc has to know
/// both: what to draw, and whether the app's own pressing still belongs underneath it. The second question is easy
/// to forget. Without it, a label is drawn with nothing behind it, the record disappears, and only its shadow is
/// left. A catalogue's label scan (the Cover Art Archive has one for most LPs) fills only the middle third and is
/// transparent everywhere else. A CD fills the disc edge to edge.
/// </para>
/// </summary>
/// <param name="CoversRecord">The picture is the whole record, and it replaces the pressing. False for a label,
/// which is worn ON the pressing.</param>
public readonly record struct DiscPicture(byte[] Bytes, bool CoversRecord);

/// <summary>
/// Cuts the round disc out of a rectangular photograph.
///
/// <para>Disc art is a transparent PNG of the record itself. The turntable draws it over its own pressing, and the
/// whole <c>disc.png</c> arrangement assumes this. Catalogues do not always provide one. They usually provide a
/// JPEG that somebody photographed: a disc on a table, corners and all. A JPEG cannot carry transparency at all. So
/// the transparency is made here, not looked for.</para>
///
/// <para>The output is always a full-disc-sized square with an alpha channel, whichever kind of record went in.
/// There is only one kind of output downstream, and it is drawn at the size of the record. A CD fills the square
/// edge to edge, and replaces the drawn record completely. A vinyl label fills only the middle third. The rest
/// stays transparent, so the record that RPM presses itself keeps showing through, with the real label on top.
/// That is what a listener means by "put the label on it". Pasting a label over the whole record would look
/// different.</para>
/// </summary>
public static class DiscStamp
{
    /// <summary>Detection runs on a copy this big on its long side. It is small on purpose. Finding a circle means
    /// counting pixels inside and outside it. Counting every pixel of a megapixel photograph to answer a question
    /// about its shape would take about a hundred times the work for the same answer.</summary>
    private const int WorkEdge = 256;

    /// <summary>The stamped PNG's edge. This is the size of a fanart.tv cdart. It is larger than the size the disc
    /// is ever drawn at.</summary>
    private const int OutputEdge = 1000;

    /// <summary>The label's share of the record's diameter. It is 66 on a 200 pt disc. That is what
    /// <c>RecordDisc.xaml</c> draws, and it matches the label size used by the pressing template (a 100 mm label on a
    /// 302 mm record). A label stamped at any other size would sit on the drawn label rather than in it.</summary>
    private const float LabelFraction = 0.33f;

    /// <summary>A CD's hole against a CD's diameter. It is 15 mm on 120 mm. This is the hole size used when the
    /// photograph does not show the hole through.</summary>
    private const float HoleFraction = 0.125f;

    /// <summary>
    /// Below this score, what was found is not round enough to be a disc.
    ///
    /// <para>A disc scores about 0.99, and a square sleeve about 0.70. The line sits between them, with room on
    /// either side. Neither a scuffed rim nor a shadow on the table decides the result. A square cannot score much
    /// lower than 0.70 against this measure. A circle fitted to a square covers its middle perfectly, and only the
    /// ring lets it down. So most of the drop has to come from the corners alone.</para>
    ///
    /// <para>Read it together with <see cref="Score"/>.</para>
    /// </summary>
    public const double MinScore = 0.80;

    /// <summary>How far a pixel must be from the background before it counts as part of the thing in the picture.
    /// It is measured per channel, from 0 to 255.</summary>
    private const int ForegroundDistance = 38;

    // ── what the caller asks ──

    /// <summary>
    /// The picture as a ROUND record, whatever kind of picture it arrived as. Every surface that draws a disc should
    /// ask for this, rather than deciding it for itself.
    ///
    /// <para>A scan that carries its own transparency is a real cdart. It is handed straight back. A flat one is a
    /// photograph of a disc on a table. <c>disc.jpg</c> is a file name that the folder reader accepts, and a JPEG
    /// cannot carry transparency at all. So the disc is cut out of it here. A flat picture with no disc to find in
    /// it is handed back as it came. The surfaces clip their disc round anyway, and a flat record is better than
    /// none.</para>
    ///
    /// <para>Decodes pictures. Call it off the UI thread.</para>
    /// </summary>
    /// <param name="vinyl">What kind of record this is a picture of. It decides what gets cut: a CD is the whole
    /// disc, and a record is its label. It is stated at every call. See <see cref="Stamp"/>, where the two answers
    /// give different pictures.</param>
    public static DiscPicture Round(byte[] bytes, bool vinyl)
    {
        if (bytes.Length == 0)
            return new DiscPicture(bytes, CoversRecord: true);

        var cut = HasAlpha(bytes) ? bytes : Stamp(bytes, vinyl) ?? bytes;
        return new DiscPicture(cut, Covers(cut));
    }

    /// <summary>
    /// Whether a picture of a record is the whole of it, or only the label in the middle.
    ///
    /// <para>Read this off the picture rather than remembering where it came from. A folder's disc.png is written
    /// by the album editor, by the cover search, by an import, or by hand. By the time it is drawn, nobody is left
    /// to ask. The alpha at four fifths of the radius answers the question whenever it is asked. A label stamp is
    /// transparent there. A CD and a whole photographed side are not.
    /// </para>
    ///
    /// <para>Returns true for anything that cannot be read. True is the default, so an unreadable picture is treated
    /// as a whole record.</para>
    /// </summary>
    public static bool Covers(byte[] bytes)
    {
        try
        {
            using var work = Work(bytes);
            if (work is null || work.AlphaType == SKAlphaType.Opaque)
                return true;

            var w = work.Width;
            var h = work.Height;
            var radius = Math.Min(w, h) / 2f * RimFraction;
            if (radius < 4)
                return true;

            var pixels = work.Pixels;
            float cx = w / 2f, cy = h / 2f;
            int opaque = 0, taken = 0;
            for (var i = 0; i < RimSamples; i++)
            {
                var angle = 2 * Math.PI * i / RimSamples;
                var x = (int)Math.Round(cx + radius * Math.Cos(angle));
                var y = (int)Math.Round(cy + radius * Math.Sin(angle));
                if (x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                taken++;
                if (pixels[y * w + x].Alpha >= 128)
                    opaque++;
            }

            return taken == 0 || opaque * 2 >= taken;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Where the vinyl is asked about: four fifths of the way out. That is past any label. A stamped label
    /// reaches a third of the radius (<see cref="LabelFraction"/>), and the widest printed label on a real record is
    /// nowhere near this far. It is also inside the rim, where a scan's own edge is soft.
    /// </summary>
    private const float RimFraction = 0.8f;

    /// <summary>How many points are read round that ring. There are enough that a picture with one torn corner is
    /// still read as the record it is.</summary>
    private const int RimSamples = 64;

    /// <summary>
    /// Whether the picture already has transparency, so it is a real cdart that needs nothing done to it. A PNG
    /// with an alpha channel that happens to be opaque everywhere does not count. It is a photograph that somebody
    /// saved as a PNG, so the pixels are read rather than the header.
    /// </summary>
    public static bool HasAlpha(byte[] bytes)
    {
        try
        {
            using var work = Work(bytes);
            if (work is null || work.AlphaType == SKAlphaType.Opaque)
                return false;

            foreach (var pixel in work.Pixels)
                if (pixel.Alpha < 250)
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The disc in a picture, or null if there is nothing round in it.
    ///
    /// <para>The background is read from the picture's own border. Everything unlike it is the subject. The
    /// subject's bounding box gives a circle to test. The score is how full that circle is, and how empty the
    /// corners around it are. A photographed disc fills its circle and leaves its corners bare. A sleeve fills the
    /// corners too, and it is rejected for exactly that.</para>
    /// </summary>
    public static DiscFind? Detect(byte[] bytes)
    {
        try
        {
            using var work = Work(bytes);
            if (work is null)
                return null;

            using var src = SKBitmap.Decode(bytes);
            if (src is null)
                return null;

            var scale = (float)Math.Max(src.Width, src.Height) / Math.Max(work.Width, work.Height);
            return Detect(work) is { } found
                ? found with
                {
                    CenterX = found.CenterX * scale,
                    CenterY = found.CenterY * scale,
                    Radius = found.Radius * scale,
                    LabelRadius = found.LabelRadius * scale,
                    HoleRadius = found.HoleRadius * scale,
                }
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The picture, stamped into the transparent PNG that the turntable needs. Returns null if the picture holds no
    /// disc worth cutting out.
    /// </summary>
    /// <param name="vinyl">This release is a record rather than a CD, so only the middle is replaced. The label is
    /// drawn at the label's own size on an otherwise transparent square, and the pressing underneath shows through
    /// everywhere else.</param>
    public static byte[]? Stamp(byte[] bytes, bool vinyl)
    {
        try
        {
            if (Detect(bytes) is not { } found || found.Score < MinScore)
                return null;

            using var src = SKBitmap.Decode(bytes);
            if (src is null)
                return null;

            // What to cut. A CD is the whole disc. A record is the label. On a photograph of a whole side, the label
            // is the inner circle. On a scan of the label alone, the label is the only circle there is. Either way,
            // the answer is LabelRadius, which says so.
            var radius = vinyl ? found.LabelRadius : found.Radius;
            if (radius <= 1)
                return null;

            // Where it lands. The stamp is always a full record's square. A CD fills it. A label takes the middle
            // third, and leaves the rest of the record showing.
            var half = OutputEdge / 2f;
            var drawn = vinyl ? half * LabelFraction : half;

            using var surface = new SKBitmap(
                new SKImageInfo(OutputEdge, OutputEdge, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(surface);
            canvas.Clear(SKColors.Transparent);

            canvas.Save();
            canvas.ClipPath(Circle(half, half, drawn), SKClipOperation.Intersect, antialias: true);
            var source = new SKRect(
                found.CenterX - radius, found.CenterY - radius,
                found.CenterX + radius, found.CenterY + radius);
            using var image = SKImage.FromBitmap(src);
            canvas.DrawImage(image, source, new SKRect(half - drawn, half - drawn, half + drawn, half + drawn),
                ArtScaler.SamplingFor(source.Width, drawn * 2));
            canvas.Restore();

            // The spindle hole, on a CD only. A record's hole is drawn by the control itself, dead centre and over
            // everything. Punching one here would only put a second hole under the first.
            //
            // Never smaller than a CD's own hole. An 8 cm single has the same 15 mm hole on a smaller disc, so no
            // disc cut here has a smaller one. A smaller measurement would be the table seen through the bottom of
            // the hole, inside the hole's wall. A camera held close above the disc sees that as a grey ring.
            if (!vinyl)
            {
                var hole = found.HoleRadius > 1 && found.HoleRadius < found.Radius * 0.4f
                    ? drawn * Math.Max(found.HoleRadius / found.Radius, HoleFraction)
                    : drawn * HoleFraction;
                using var punch = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
                canvas.DrawCircle(half, half, hole, punch);
            }

            canvas.Flush();
            using var stamp = SKImage.FromBitmap(surface);
            using var encoded = stamp.Encode(SKEncodedImageFormat.Png, 100);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static SKPath Circle(float cx, float cy, float r)
    {
        var path = new SKPath();
        path.AddCircle(cx, cy, r);
        return path;
    }

    // ── finding the circle ──

    private static DiscFind? Detect(SKBitmap work)
    {
        var w = work.Width;
        var h = work.Height;
        if (w < 16 || h < 16)
            return null;

        var pixels = work.Pixels;
        var background = Background(pixels, w, h);

        // The subject is everything unlike the border. A transparent pixel counts as background, whatever its
        // colour. A picture that already has holes in it shows where the subject is not.
        var mask = new bool[w * h];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = pixels[y * w + x];
                if (p.Alpha < 128 || Near(p, background))
                    continue;
                mask[y * w + x] = true;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        if (maxX < minX || maxY < minY)
            return null;

        var boxW = maxX - minX + 1;
        var boxH = maxY - minY + 1;
        if (Math.Min(boxW, boxH) / 2f < 8)
            return null;

        // The box is only where to START looking. It is the extent of everything that is not the background. A
        // catalogue photograph is rarely just a disc on a table. There is often the corner of a jewel case, a booklet
        // leaning against the disc, or the edge of a sleeve. Any of that widens the box on one side. A circle taken
        // straight from the box would then be off-centre by half of whatever the clutter added, and its radius would
        // be inflated to match. That makes a crooked stamp, and it still scores over the threshold, so nothing
        // catches it.
        var (cx, cy, radius) = Fit(mask, w, h,
            minX + boxW / 2f, minY + boxH / 2f, Math.Min(boxW, boxH) / 2f);
        if (radius < 8)
            return null;

        // The score is taken over the circle's own surroundings, not over the mask's box. What decides whether this
        // is a disc is what sits in the corners AROUND THE CIRCLE. Whether something else is also in the photograph
        // does not matter.
        var score = Score(mask, w, h, cx, cy, radius);

        // The score decides whether this is a disc. MinScore is set against the mask fit, so the score is taken
        // before the refinement. The refinement only decides where the cut goes.
        (cx, cy, radius) = Refine(pixels, mask, w, h, cx, cy, radius);

        return new DiscFind(
            cx, cy, radius,
            LabelRadius: LabelRadius(pixels, w, h, cx, cy, radius),
            HoleRadius: HoleRadius(pixels, w, h, cx, cy, radius, background),
            Score: score);
    }

    /// <summary>
    /// The circle that best covers the subject. It starts from the bounding box's guess and is improved by
    /// hill-climbing.
    ///
    /// <para>The measure is overlap over union, between the disc the circle claims and the mask that is really
    /// there. It is chosen because the obvious alternatives can be gamed in either direction. "How much of the circle
    /// is filled" is maximised by shrinking the circle to a speck well inside the disc. "How much of the mask is
    /// covered" is maximised by growing the circle until it swallows the whole picture. Overlap over union punishes
    /// both. So the best circle is the one actually on the disc.</para>
    ///
    /// <para>The search goes from coarse to fine. The coarse step is a share of the radius, not a fixed number of
    /// pixels. So the search reaches as far as it needs to on a big disc, and does not wander on a small one. Every
    /// step counts the whole mask. That is why detection runs on a 256 px copy.</para>
    /// </summary>
    private static (float X, float Y, float Radius) Fit(
        bool[] mask, int w, int h, float cx, float cy, float radius)
    {
        var best = Overlap(mask, w, h, cx, cy, radius);

        // The six ways to move: two along each axis, two in size.
        ReadOnlySpan<(float X, float Y, float R)> moves =
        [
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
        ];

        for (var step = Math.Max(2f, radius * 0.2f); step >= 0.5f; step /= 2f)
        {
            // Keep taking the best move at this step size until none of them is an improvement. Then halve the
            // step. The loop is bounded by the shrinking step, not by an iteration count.
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var (dx, dy, dr) in moves)
                {
                    float nx = cx + dx * step, ny = cy + dy * step, nr = radius + dr * step;
                    if (nr < 8 || nr > Math.Max(w, h))
                        continue;

                    var score = Overlap(mask, w, h, nx, ny, nr);
                    if (score <= best)
                        continue;

                    (best, cx, cy, radius) = (score, nx, ny, nr);
                    moved = true;
                }
            }
        }

        return (cx, cy, radius);
    }

    /// <summary>How much of the circle and the subject are the same thing: their overlap, divided by everything
    /// either of them covers. It is 1 when the circle is exactly the subject. It falls away when the circle is too
    /// small, too large, or in the wrong place.</summary>
    private static double Overlap(bool[] mask, int w, int h, float cx, float cy, float radius)
    {
        var r2 = radius * radius;
        int both = 0, either = 0;

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = x + 0.5f - cx;
                var dy = y + 0.5f - cy;
                var within = dx * dx + dy * dy <= r2;
                var on = mask[y * w + x];
                if (within || on) either++;
                if (within && on) both++;
            }

        return either == 0 ? 0 : (double)both / either;
    }

    // ── refining the circle on the picture's edges ──

    /// <summary>How many rays the refinement reads, evenly spaced round the circle.</summary>
    private const int EdgeRays = 180;

    /// <summary>The band each ray is searched in, as shares of the mask fit's radius. It is wide enough to cover the
    /// few percent that a shadow or a near-white rim moves the mask fit. It stays well outside a CD's hub or a
    /// record's label.</summary>
    private const float BandInner = 0.8f;
    private const float BandOuter = 1.15f;

    /// <summary>The smallest change across two pixels, in any channel from 0 to 255, that counts as an edge. A soft
    /// shadow on a table changes by less than this. The rim of a disc and the edge of its print change by several
    /// times more.</summary>
    private const float EdgeStep = 24;

    /// <summary>How far an averaged ring must rise above the profile on both sides of it, from 0 to 255, to count as
    /// a real edge. A CD's clear rim against a white table rises by about 17. The slow fall of a shadow beyond the
    /// rim, and the noise on it, rise by less than one.</summary>
    private const float RingRise = 8;

    /// <summary>How far either side of a ring the profile is looked at, to measure its rise. It is a share of the
    /// radius. It is wider than the gap between a CD's print edge and its rim. A wider window would let the print
    /// edge stand in for the rim's valley.</summary>
    private const float RingWindow = 0.03f;

    /// <summary>How far the rim can be from the mask's size, as a share of that size. The mask misses a CD's clear
    /// rim where it is the colour of the table. That is about 4% of the radius. A shadow adds about as much on its
    /// side.</summary>
    private const float RimReach = 0.05f;

    /// <summary>
    /// The mask fit, moved onto the disc's real edges.
    ///
    /// <para>The mask fit is only as good as the mask, and the mask is wrong at a disc's rim in both directions. A
    /// shadow on the table differs from the table. So it counts as disc, and pushes the circle towards it. A CD's
    /// clear plastic rim is nearly the colour of a white table. So it counts as table, and pulls the circle in on
    /// that side. On a photograph from a catalogue site, this moved the centre by nearly 4% of the radius. That cut
    /// the rim off one side and left a band of table on the other.</para>
    ///
    /// <para>Edges do not have that problem. A shadow is a soft gradient. The rim and the edge of the print are
    /// sharp, and both are circles around the same centre. So each ray takes its sharpest step near the rim. A
    /// circle is fitted to those points, with the outliers dropped, and that gives the centre. The strongest step is
    /// often the print edge, not the rim. That is why only the centre is taken from it. The radius is chosen in
    /// <see cref="Rim"/>.</para>
    ///
    /// <para>The mask fit is returned unchanged when the edges do not describe a circle near it. That happens when
    /// too few rays find an edge, when most of them disagree, or when the result moved further than the mask fit can
    /// be wrong by.</para>
    /// </summary>
    private static (float X, float Y, float Radius) Refine(
        SKColor[] pixels, bool[] mask, int w, int h, float cx, float cy, float radius)
    {
        var unchanged = (cx, cy, radius);

        // The sharpest step on each ray within the band, as a point relative to the mask fit's centre.
        var points = new List<(double X, double Y)>(EdgeRays);
        for (var i = 0; i < EdgeRays; i++)
        {
            var angle = 2 * Math.PI * i / EdgeRays;
            float ux = (float)Math.Cos(angle), uy = (float)Math.Sin(angle);
            float best = EdgeStep, at = -1;
            for (var s = radius * BandInner; s <= radius * BandOuter; s += 0.25f)
            {
                if (Step(pixels, w, h, cx, cy, ux, uy, s) is not { } step)
                    break;
                if (step > best)
                    (best, at) = (step, s);
            }
            if (at > 0)
                points.Add((at * ux, at * uy));
        }
        if (points.Count < 24)
            return unchanged;

        // Least squares, then drop the points further from the circle than three robust standard deviations (never
        // less than a pixel), and fit again.
        var keep = new bool[points.Count];
        Array.Fill(keep, true);
        (double X, double Y, double R) circle = default;
        for (var round = 0; round < 4; round++)
        {
            if (FitCircle(points, keep) is not { } fitted)
                return unchanged;
            circle = fitted;

            var residuals = new double[points.Count];
            var kept = new List<double>();
            for (var i = 0; i < points.Count; i++)
            {
                residuals[i] = Math.Abs(Math.Sqrt(
                    Math.Pow(points[i].X - circle.X, 2) + Math.Pow(points[i].Y - circle.Y, 2)) - circle.R);
                if (keep[i])
                    kept.Add(residuals[i]);
            }
            kept.Sort();
            var limit = Math.Max(1.0, 3 * 1.4826 * kept[kept.Count / 2]);
            for (var i = 0; i < points.Count; i++)
                keep[i] = residuals[i] <= limit;
        }
        if (keep.Count(k => k) * 2 < points.Count)
            return unchanged;

        float nx = cx + (float)circle.X, ny = cy + (float)circle.Y;

        // The mask fit is off by a few percent when it is off. Anything that moved much further found some other
        // circle, and the mask fit is the safer answer.
        var moved = MathF.Sqrt((nx - cx) * (nx - cx) + (ny - cy) * (ny - cy));
        if (moved > radius * 0.15f)
            return unchanged;

        var size = FitRadius(mask, w, h, nx, ny, radius);
        var rim = Rim(pixels, w, h, nx, ny, radius * BandInner, radius * BandOuter, size);
        return (nx, ny, rim);
    }

    /// <summary>The radius that best covers the mask, around a centre that is already known. It is the size half of
    /// <see cref="Fit"/>, with the centre held still.</summary>
    private static float FitRadius(bool[] mask, int w, int h, float cx, float cy, float radius)
    {
        var best = Overlap(mask, w, h, cx, cy, radius);
        for (var step = Math.Max(2f, radius * 0.2f); step >= 0.5f; step /= 2f)
        {
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var nr in (ReadOnlySpan<float>)[radius + step, radius - step])
                {
                    if (nr < 8 || nr > Math.Max(w, h))
                        continue;
                    var score = Overlap(mask, w, h, cx, cy, nr);
                    if (score <= best)
                        continue;
                    (best, radius) = (score, nr);
                    moved = true;
                }
            }
        }
        return radius;
    }

    /// <summary>
    /// The rim around a known centre: the outermost sharp ring within <see cref="RimReach"/> of
    /// <paramref name="size"/>. It is looked for between <paramref name="from"/> and <paramref name="to"/>.
    ///
    /// <para>It is read from the edge strength, averaged round the whole circle at each radius. Around the right
    /// centre, a ring that really exists is a peak in that average. A shadow is neither sharp nor centred on the
    /// disc, so it gives a low slope. A CD shows two peaks close together: the print edge and the rim. The stronger
    /// one is usually the print edge. Strength alone does not pick the rim, because a shadow over part of the rim
    /// weakens its peak. The outermost peak in the whole band does not pick it either. Something round beyond the
    /// disc, such as the tray of a jewel case, can lie further out. The mask's size limits the search to the disc. It
    /// is a few percent wrong at most, and it does not know where the edge is exactly. The peaks are exact, but they
    /// do not know which of them is the disc.</para>
    ///
    /// <para><paramref name="size"/> itself is returned when no ring stands out near it.</para>
    /// </summary>
    private static float Rim(SKColor[] pixels, int w, int h, float cx, float cy, float from, float to, float size)
    {
        var start = Math.Max(1f, from);
        var count = (int)((to - start) / 0.25f) + 1;
        if (count < 3)
            return size;

        var profile = new double[count];
        for (var k = 0; k < count; k++)
        {
            var s = start + k * 0.25f;
            double sum = 0;
            var taken = 0;
            for (var i = 0; i < EdgeRays; i++)
            {
                var angle = 2 * Math.PI * i / EdgeRays;
                if (Step(pixels, w, h, cx, cy, (float)Math.Cos(angle), (float)Math.Sin(angle), s) is not { } step)
                    continue;
                sum += step;
                taken++;
            }
            profile[k] = taken == 0 ? 0 : sum / taken;
        }

        // A ring is a local peak that rises by RingRise above the lowest point of the profile on each side of it,
        // within the window.
        var window = Math.Max(2, (int)(size * RingWindow / 0.25f));
        var rim = size;
        for (var k = 1; k < count - 1; k++)
        {
            var s = start + k * 0.25f;
            if (MathF.Abs(s - size) > size * RimReach)
                continue;

            if (profile[k] < profile[k - 1] || profile[k] < profile[k + 1])
                continue;

            double below = double.MaxValue, above = double.MaxValue;
            for (var j = Math.Max(0, k - window); j < k; j++)
                below = Math.Min(below, profile[j]);
            for (var j = k + 1; j <= Math.Min(count - 1, k + window); j++)
                above = Math.Min(above, profile[j]);
            if (profile[k] - Math.Max(below, above) < RingRise)
                continue;

            // k only grows, so the last ring kept is the outermost.
            rim = s;
        }
        return rim;
    }

    /// <summary>The circle through a set of points, by least squares (the Kåsa fit), using only the points marked
    /// in <paramref name="keep"/>. Null when there are too few points, or when they all lie on one line.</summary>
    private static (double X, double Y, double R)? FitCircle(List<(double X, double Y)> points, bool[] keep)
    {
        // x² + y² = A·x + B·y + C is linear in A, B and C. The centre is (A/2, B/2), and the radius is
        // √(C + (A/2)² + (B/2)²).
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = 0, sxz = 0, syz = 0, sz = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (!keep[i])
                continue;
            var (x, y) = points[i];
            var z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y;
            sx += x; sy += y; n++;
            sxz += x * z; syz += y * z; sz += z;
        }
        if (n < 3)
            return null;

        var det = Det(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
        if (Math.Abs(det) < 1e-9)
            return null;

        var a = Det(sxz, sxy, sx, syz, syy, sy, sz, sy, n) / det;
        var b = Det(sxx, sxz, sx, sxy, syz, sy, sx, sz, n) / det;
        var c = Det(sxx, sxy, sxz, sxy, syy, syz, sx, sy, sz) / det;
        var r2 = c + a * a / 4 + b * b / 4;
        return r2 <= 0 ? null : (a / 2, b / 2, Math.Sqrt(r2));

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }

    /// <summary>How sharply the picture changes across a point on a ray. It is the largest change in any channel,
    /// between one pixel just inside the point and one just outside it. Colour is premultiplied by alpha, so a
    /// transparent pixel reads as nothing, whatever colour it holds. Null when either side is off the picture.</summary>
    private static float? Step(SKColor[] pixels, int w, int h, float cx, float cy, float ux, float uy, float s)
    {
        if (Sample(pixels, w, h, cx + (s - 1) * ux, cy + (s - 1) * uy) is not { } inside
            || Sample(pixels, w, h, cx + (s + 1) * ux, cy + (s + 1) * uy) is not { } outside)
            return null;

        return MathF.Max(
            MathF.Max(MathF.Abs(outside.R - inside.R), MathF.Abs(outside.G - inside.G)),
            MathF.Max(MathF.Abs(outside.B - inside.B), MathF.Abs(outside.A - inside.A)));
    }

    /// <summary>The colour at a point, blended from the four pixels around it, premultiplied by alpha. Null when the
    /// point is off the picture.</summary>
    private static (float R, float G, float B, float A)? Sample(SKColor[] pixels, int w, int h, float x, float y)
    {
        // Pixel (i, j) covers i..i+1, so its centre is at i + 0.5.
        var fx = x - 0.5f;
        var fy = y - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= w || y0 + 1 >= h)
            return null;

        float ax = fx - x0, ay = fy - y0;
        float r = 0, g = 0, b = 0, a = 0;
        Add(pixels[y0 * w + x0], (1 - ax) * (1 - ay));
        Add(pixels[y0 * w + x0 + 1], ax * (1 - ay));
        Add(pixels[(y0 + 1) * w + x0], (1 - ax) * ay);
        Add(pixels[(y0 + 1) * w + x0 + 1], ax * ay);
        return (r, g, b, a);

        void Add(SKColor p, float weight)
        {
            var alpha = p.Alpha / 255f;
            r += p.Red * alpha * weight;
            g += p.Green * alpha * weight;
            b += p.Blue * alpha * weight;
            a += p.Alpha * weight;
        }
    }

    /// <summary>How far past the rim to look for whatever the disc is sitting on. It is a quarter of the radius. That
    /// is wide enough for a square's corners to fall inside it, and narrow enough that it stays about this disc, not
    /// the rest of the photograph.</summary>
    private const float SurroundFraction = 1.25f;

    /// <summary>
    /// How much like a disc the circle that was found is, from 0 to 1.
    ///
    /// <para>There are two counts. The first is how much of the circle is filled. The second is how much of the ring
    /// just outside it is empty. A disc scores near one on both. It is solid, and it has table around it. A square
    /// scores near one on the first and badly on the second. A circle fitted to a square has the square's four
    /// corners sitting in exactly that ring. That is the whole discrimination.</para>
    ///
    /// <para>The measure uses a ring, not the corners of a bounding box. A box is drawn around SOMETHING. As soon as
    /// the photograph holds anything besides the disc, such as the case, the booklet, or the sleeve behind it, the
    /// box is drawn around that too. A good disc would then be marked down for whatever lay next to it. The ring only
    /// asks about the disc's own surroundings.</para>
    /// </summary>
    private static double Score(bool[] mask, int w, int h, float cx, float cy, float radius)
    {
        var inner2 = radius * radius;
        var outer = radius * SurroundFraction;
        var outer2 = outer * outer;
        int inside = 0, insideArea = 0, ring = 0, ringArea = 0;

        var minX = Math.Max(0, (int)MathF.Floor(cx - outer));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(cx + outer));
        var minY = Math.Max(0, (int)MathF.Floor(cy - outer));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(cy + outer));

        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x + 0.5f - cx;
                var dy = y + 0.5f - cy;
                var distance = dx * dx + dy * dy;
                var on = mask[y * w + x];

                if (distance <= inner2)
                {
                    insideArea++;
                    if (on) inside++;
                }
                else if (distance <= outer2)
                {
                    ringArea++;
                    if (on) ring++;
                }
            }

        var fill = insideArea == 0 ? 0 : (double)inside / insideArea;

        // A ring that is entirely off the edge of the picture asks nothing and answers nothing. That is a disc
        // photographed to the frame's edge. It must not be marked down for the photographer having cropped tightly.
        // Nor should it be credited for that. This is why the few pixels that ARE in frame decide the answer when
        // there are any.
        var surround = ringArea == 0 ? 1 : 1 - (double)ring / ringArea;
        return fill * surround;
    }

    /// <summary>
    /// The picture's background, read from its outermost ring of pixels.
    ///
    /// <para>The median is used rather than the mean, per channel. A photograph of a disc on a white table with one
    /// dark corner has a median that is still white. A mean would be grey, and would let half the table into the
    /// subject.</para>
    /// </summary>
    private static SKColor Background(SKColor[] pixels, int w, int h)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();

        void Take(int x, int y)
        {
            var p = pixels[y * w + x];
            reds.Add(p.Red);
            greens.Add(p.Green);
            blues.Add(p.Blue);
        }

        for (var x = 0; x < w; x++)
        {
            Take(x, 0);
            Take(x, h - 1);
        }
        for (var y = 1; y < h - 1; y++)
        {
            Take(0, y);
            Take(w - 1, y);
        }

        return new SKColor(Median(reds), Median(greens), Median(blues));
    }

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values.Count == 0 ? (byte)0 : values[values.Count / 2];
    }

    private static bool Near(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) <= ForegroundDistance
        && Math.Abs(a.Green - b.Green) <= ForegroundDistance
        && Math.Abs(a.Blue - b.Blue) <= ForegroundDistance;

    /// <summary>
    /// The printed label inside a photographed record, or the disc's own radius when there is no inner edge to find.
    ///
    /// <para>It is read from a radial profile. That is the mean brightness of each ring, from the middle outwards.
    /// The label edge is the radius where the profile takes its biggest step. On a record, the step is the label
    /// meeting the grooves. That is the largest change of brightness anywhere on the disc. On a scan of a label
    /// alone, there is no such step, and the whole thing is the label. That answer comes out of finding no
    /// step.</para>
    /// </summary>
    private static float LabelRadius(SKColor[] pixels, int w, int h, float cx, float cy, float radius)
    {
        var bins = (int)radius;
        if (bins < 12)
            return radius;

        var profile = new double[bins];
        for (var r = 0; r < bins; r++)
        {
            double sum = 0;
            var taken = 0;
            var samples = Math.Max(24, (int)(2 * Math.PI * r));
            for (var i = 0; i < samples; i++)
            {
                var angle = 2 * Math.PI * i / samples;
                var x = (int)Math.Round(cx + r * Math.Cos(angle));
                var y = (int)Math.Round(cy + r * Math.Sin(angle));
                if (x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                var p = pixels[y * w + x];
                sum += 0.299 * p.Red + 0.587 * p.Green + 0.114 * p.Blue;
                taken++;
            }
            profile[r] = taken == 0 ? 0 : sum / taken;
        }

        // Only where a label could plausibly end. Inside a sixth of the disc is the spindle and the mirror band.
        // Past two thirds is the run-out. A step there is a rim highlight, not an edge of anything.
        var step = Math.Max(2, bins / 32);
        var from = Math.Max(step, bins / 6);
        var to = Math.Min(bins - step - 1, (int)(bins * 0.68));

        var bestAt = -1;
        double best = 0;
        for (var r = from; r <= to; r++)
        {
            var jump = Math.Abs(profile[r + step] - profile[r - step]);
            if (jump > best)
            {
                best = jump;
                bestAt = r;
            }
        }

        // A real edge, not the shading across a flat scan. Below this, the picture is a label and nothing else, so
        // the label is all of it.
        return best >= 28 && bestAt > 0 ? bestAt : radius;
    }

    /// <summary>
    /// The spindle hole, when the background shows through it. This is the usual case for a CD photographed on a
    /// table. It is zero when the hole does not show through. In that case the caller punches a hole at the real
    /// thing's proportions instead.
    /// </summary>
    private static float HoleRadius(
        SKColor[] pixels, int w, int h, float cx, float cy, float radius, SKColor background)
    {
        var limit = (int)(radius * 0.35f);
        if (limit < 3)
            return 0;

        var last = 0;
        for (var r = 1; r <= limit; r++)
        {
            var samples = Math.Max(16, (int)(2 * Math.PI * r));
            var matched = 0;
            var taken = 0;
            for (var i = 0; i < samples; i++)
            {
                var angle = 2 * Math.PI * i / samples;
                var x = (int)Math.Round(cx + r * Math.Cos(angle));
                var y = (int)Math.Round(cy + r * Math.Sin(angle));
                if (x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                taken++;
                if (Near(pixels[y * w + x], background))
                    matched++;
            }
            if (taken == 0 || matched < taken * 0.75)
                break;
            last = r;
        }
        return last;
    }

    // ── plumbing ──

    /// <summary>A copy no bigger than <see cref="WorkEdge"/>, in a colour type whose pixels can be read one by
    /// one.</summary>
    private static SKBitmap? Work(byte[] bytes)
    {
        using var src = SKBitmap.Decode(bytes);
        if (src is null || src.Width == 0 || src.Height == 0)
            return null;

        var longest = Math.Max(src.Width, src.Height);
        var k = longest <= WorkEdge ? 1.0 : (double)WorkEdge / longest;
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(src.Width * k)),
            Math.Max(1, (int)Math.Round(src.Height * k)),
            SKColorType.Rgba8888, SKAlphaType.Unpremul);

        return src.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }
}
