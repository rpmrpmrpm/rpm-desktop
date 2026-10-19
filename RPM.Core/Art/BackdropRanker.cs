namespace RPM.Core.Art;

/// <summary>One face the detector found in a candidate, in fractions of the photograph itself:
/// <paramref name="Across"/> is where its centre sits from left (0) to right (1), <paramref name="Height"/>
/// how much of the frame's height it fills. The chooser needs both: where the artist is, and whether
/// the photograph is a close-up of them.</summary>
public readonly record struct FaceSpot(double Across, double Height);

/// <summary>
/// The cheap, measurable things about one artist backdrop that decide whether it goes first: its shape,
/// the face in it, how good it looks, and where the source put it in its own likes order. All of it is
/// read from a small thumbnail, with no model beyond the face detector and no full-resolution decode.
/// That is what makes this a <em>preselect</em>: the expensive U²-Netp crop still runs, but only on the
/// one photo this picks.
/// </summary>
public readonly record struct BackdropFeatures(
    /// <summary>Average brightness of the photo, 0 (black) .. 1 (white).</summary>
    double MeanLuma,
    /// <summary>Spread of brightness, 0 (flat) .. 1 (full range). A washed-out or murky photo is low.</summary>
    double Contrast,
    /// <summary>How much colour the photo carries, 0 (grey) .. 1 (vivid).</summary>
    double Colorfulness,
    /// <summary>The source photo's aspect (width / height). The header is a long horizontal band, so this
    /// is the first thing asked of a candidate and the one that can settle it on its own. See
    /// <see cref="BackdropRanker.Widest"/>.</summary>
    double Aspect,
    /// <summary>Where the artist's face sits across the photo, 0 (left) .. 1 (right), or -1 when there is
    /// no face. A face in the clear middle is the artist, visible; a face at the far left ends up behind
    /// the album cover.</summary>
    double FaceAcross,
    /// <summary>The shorter side of the source in pixels. It stands in for how sharp the photo will look
    /// enlarged across the strip. 0 when the size is not known.</summary>
    int SourceMinDim,
    /// <summary>Where the source listed it. fanart.tv sorts its backdrops by community likes, so a lower
    /// index is "more liked". It is the tiebreak when two photos are otherwise equal.</summary>
    int OriginalIndex,
    /// <summary>The fraction of the photo's faces that fall behind the album cover or the record, so they
    /// are out of sight either way. A photo whose faces are <em>mostly</em> hidden is disqualified from
    /// leading, so the strip never opens on a picture of the artist that cannot be seen. 0 for a
    /// faceless photo or one whose faces are all in the clear.</summary>
    double FacesObstructedFraction = 0,
    /// <summary>
    /// How much of the photo is quiet ground rather than picture, 0 (busy everywhere) .. 1 (nothing but
    /// open background). Examples are sky, a wall, a dark stage and an out-of-focus room. These are the
    /// flat areas where a header can put its text and its frosted cards, and where the picture's own
    /// edges dissolve without a visible seam. A photo with plenty of open ground blends into the app; a
    /// photo that is subject from corner to corner looks like a rectangle stuck on the window.
    /// </summary>
    double OpenFraction = 0,
    /// <summary>
    /// How much of the photo's HEIGHT the biggest face fills: 0 for no face at all, ~0.05 for someone on a
    /// stage seen whole, ~0.3 for head and shoulders, ~0.7 for a headshot.
    ///
    /// <para>It is how the chooser knows a photograph is ZOOMED IN, and nothing else can tell it. A
    /// close-up and a wide shot of the same artist have the same aspect, the same colours, and a face in
    /// the same place across the frame. The one thing separating them is how much of the frame the head
    /// takes.</para>
    /// </summary>
    double FaceHeight = 0);

/// <summary>
/// Orders an artist's backdrops best-first for the header.
///
/// <para><b>Above all of it sits one hard rule</b> (see <see cref="MadeForTheHeader"/>): a photograph
/// that is sixteen-to-nine or wider, has a quiet, minimal background, AND is plainly a photograph of
/// the artist leads, ahead of anything that is not all three. This is a tier, not a weight or a bonus.
/// It describes a press shot on a plain studio ground, which is the one kind of picture this header was
/// designed for: wide enough to fill the band without being enlarged, and empty enough that the title,
/// the frosted cards and the picture's own dissolving edges all have somewhere to sit. Everything below
/// is how the rest are ordered, and it is also what applies when an artist has no such photograph,
/// which is common.</para>
///
/// <para><b>The rest of the rule has two steps, and the order of the steps matters.</b> First the
/// SHAPE: the header is a long horizontal band, so the widest photographs on offer are the ones it can
/// use, and a narrower one does not compete however good it is (see <see cref="Widest"/>). Then, among
/// the photographs of comparable width, the FACE decides: where the artist sits across the frame, and
/// whether there is any photograph around them or the picture is just a head. The look and the source's
/// likes order settle what is left.</para>
///
/// <para>The ranking is not one weighted sum of everything at once. With a sum, enough of one thing
/// beats a little of another: a portrait with a well-placed face would out-score a landscape, because
/// the face term is large and the shape term is one number among five. Width is not a preference to be
/// traded against the others. A tall photograph in a nine-to-one band is wrong before anything else
/// about it has been considered.</para>
///
/// <para>The two steps are also the order in which the WORK happens. Shape is cheap (it is two numbers
/// from the image header), and the face detector is a model run per candidate. So the shortlist is
/// drawn first and only its members are put through the detector (see <c>BackdropPreselector</c>).</para>
///
/// <para>The scoring is pure and lives in Core so it can be pinned by tests; the app decodes the
/// thumbnails, runs the detector, and feeds the numbers in.</para>
/// </summary>
public static class BackdropRanker
{
    /// <summary>
    /// The version of the ranking algorithm. Bump it whenever the ordering would change. A saved order
    /// stamped with an older version is re-ranked on next sight, so an algorithm change reaches the
    /// artists whose order was cached under the old one (a curated, hand-arranged order is exempt, and
    /// the user's own order is kept).
    ///
    /// <para>The order in which candidates are handed in is an input too: an index in that list is the
    /// likes tiebreak. If a source changes the order it returns pictures in, bump the version as well,
    /// or artists with a saved automatic order keep the old answer until their photo set changes, which
    /// for a settled collection is never.</para>
    /// </summary>
    public const int RankVersion = 10;

    // ── the hard rule: the photograph this header was designed for ──

    /// <summary>The shape a photograph has to reach to lead: sixteen to nine, which is what a press shot,
    /// a promo still and every fanart.tv background are cut to.
    ///
    /// <para>This is a FLOOR, not a target. The band is a dozen times wider than it is tall, so nothing
    /// here is ever too wide: a two-to-one image or a panorama clears the floor the same as a 1920×1080
    /// image does, and sorts ahead of it afterwards in the ordinary shape step. The floor keeps out the
    /// boxy and the tall: four-to-three, square and portrait.</para></summary>
    public const double HeaderAspect = 16.0 / 9.0;

    /// <summary>Rounding room on <see cref="HeaderAspect"/>. A gallery states its sizes in whole pixels
    /// and many land a thousandth under sixteen-to-nine (1000×563 reads as 1.776), and a photograph
    /// that misses the floor by a rounding error is lost for no good reason. The slack is small enough
    /// that sixteen-to-TEN (1.60) is still well outside it.</summary>
    private const double HeaderAspectSlack = 0.02;

    /// <summary>How much of a photograph has to read as quiet ground before its background counts as
    /// minimal: roughly half of it. A studio backdrop, a sky or a dark stage clears this; a crowd, a lit
    /// set or a scene with detail into every corner does not. Measured by
    /// <see cref="OpenAreaFraction"/>.</summary>
    private const double PlainGround = 0.45;

    /// <summary>Whether a photograph's shape is one the header can fill outright. See
    /// <see cref="HeaderAspect"/>. It takes the aspect alone so the app can ask it right after a decode,
    /// before the detector has run on anything.</summary>
    public static bool ShapedForHeader(double aspect) => aspect >= HeaderAspect - HeaderAspectSlack;

    /// <summary>
    /// The hard rule: wide enough for the band, and quiet enough to put an app on top of.
    ///
    /// <para>Shape alone is not enough as a soft judgement, because it is only judged RELATIVE to the
    /// artist's own gallery (see <see cref="Widest"/>): an artist photographed entirely in four-to-three
    /// has a "widest" too, and it would lead. Open ground alone is not enough either. It is three
    /// tenths of <see cref="Fit"/>, which is a little over half of <see cref="Score"/>, so about a
    /// sixth of the whole, and a busy photograph with a well-placed face and the source's likes behind
    /// it would beat a clean one comfortably.</para>
    ///
    /// <para>This is a tier rather than a weight, because a number added to the score can always be
    /// out-voted by enough of something else. Nothing that fails this rule can reach the front while
    /// something that passes it exists.</para>
    ///
    /// <para>The ARTIST has to be in the photograph, and be its subject. This half is not obvious and it
    /// cannot be left out: open ground and an absent subject are the same measurement. An empty stage
    /// with a figure somewhere on it is quiet across almost its whole frame and scores higher on ground
    /// than any press shot, so a tier resting on shape and quiet alone would promote exactly the
    /// pictures that are of nobody. So the face is required in the size band the header wants: big
    /// enough that there is no doubt who it is (<see cref="ClearFace"/>), small enough that there is
    /// still a photograph around them (<see cref="CrowdedFace"/>) rather than a pair of eyes across a
    /// metre of screen. A quiet ground is only worth having when there is a subject standing on it.</para>
    ///
    /// <para>Faces that are mostly hidden also disqualify a photograph here. The same rule exists in
    /// <see cref="Score"/>, but a tier above the score would skip over it and promote the one picture
    /// whose subject is behind the album cover.</para>
    ///
    /// <para>Nothing is DROPPED for failing this rule. An artist with no such photograph is the ordinary
    /// case, and the steps below then order the gallery without this tier.</para>
    /// </summary>
    public static bool MadeForTheHeader(BackdropFeatures f)
        => ShapedForHeader(f.Aspect)
           && f.OpenFraction >= PlainGround
           && f.FaceAcross >= 0                                    // somebody is in it…
           && f.FaceHeight is >= ClearFace and <= CrowdedFace      // …and it is a photograph OF them
           && f.FacesObstructedFraction <= ObstructedMajority;

    // ── step one: shape ──

    /// <summary>
    /// How much narrower than the widest photograph on offer a candidate may be and still count as the
    /// same shape for this artist.
    ///
    /// <para>This is relative, not a threshold in aspect ratios, because no width is right in the
    /// abstract: what counts is what this artist's photographs happen to be. A set that is all sixteen
    /// to nine is all one shape and the face decides every one of them. A set with a three-to-one
    /// photograph in it has a photograph made for this header, and the ordinary landscapes are the
    /// narrow ones. Four to three loses to either, which is the main thing this has to get right.</para>
    /// </summary>
    private const double ShapeSlack = 0.80;

    /// <summary>
    /// The candidates whose shape suits the header: the widest one, and everything within
    /// <see cref="ShapeSlack"/> of it. Indices into <paramref name="candidates"/>.
    ///
    /// <para>Never empty: the widest photograph is always in it, even if every photograph an artist has
    /// is a portrait. This picks the best of what there is; it does not refuse to show anything.</para>
    /// </summary>
    public static IReadOnlySet<int> Widest(IReadOnlyList<BackdropFeatures> candidates)
        => Widest(candidates.Select(c => c.Aspect).ToList());

    /// <summary>
    /// The same question asked of nothing but the shapes, so it can be asked BEFORE the expensive part.
    /// The app knows every candidate's width and height as soon as it has decoded them, and the face
    /// detector is a model run per photograph. So the shortlist is drawn here and only its members are
    /// put through the detector.
    /// </summary>
    public static IReadOnlySet<int> Widest(IReadOnlyList<double> aspects)
    {
        var widest = 0.0;
        foreach (var aspect in aspects)
            widest = Math.Max(widest, aspect);

        var enough = widest * ShapeSlack;
        var shortlist = new HashSet<int>();
        for (var i = 0; i < aspects.Count; i++)
            if (aspects[i] >= enough)
                shortlist.Add(i);

        return shortlist;
    }

    // ── step two: what separates photographs of the same shape ──
    //
    // The FACE leads: an artist header wants the artist, visible, clear of the album cover, and not
    // zoomed in so far that the picture is only a head. Open ground is the rest of it: the quiet area
    // that the app's frost and text sit on, and that the picture's edges dissolve into.
    private const double FitFace = 0.70;
    private const double FitOpen = 0.30;

    // ── what "looks best" weighs ──
    // A well-exposed, contrasty, colourful, sharp photo looks better across the strip than a murky or
    // washed-out one. These sum to 1: `look` is one number in 0..1.
    private const double LookBrightness = 0.30;
    private const double LookContrast = 0.28;
    private const double LookColour = 0.22;
    private const double LookResolution = 0.20;

    // ── how fit, look and likes trade off, WITHIN a shape ──
    private const double WeightFit = 0.56;
    private const double WeightLook = 0.32;
    private const double WeightLikes = 0.12;

    /// <summary>The brightness a strip photo looks best at: dim enough that white title text can be
    /// read over it, bright enough not to be a black bar. The score falls off on either side of
    /// it.</summary>
    private const double IdealLuma = 0.45;

    /// <summary>The resolution at which a backdrop is "sharp enough". fanart.tv backdrops are 1080 tall,
    /// so they land at the top of the scale and a small TheAudioDB thumbnail lands below it.</summary>
    private const double SharpEnoughShortSide = 720.0;

    // ── how big the face should be ──
    //
    // Both ends are wrong and the middle is right, which is why this is a band and not a direction. A
    // face that is too small means a photograph the artist happens to be standing in: a figure on a
    // distant stage, recognisable to nobody. A face that is too big means a photograph with nothing
    // else in it, and in a header nine times wider than it is tall that is a pair of eyes and no
    // picture. The header wants the artist plainly as the subject, with the room around them that the
    // app blends into.

    /// <summary>Below this the artist is a speck in the scene: in the photograph, but not its subject.</summary>
    private const double TinyFace = 0.06;

    /// <summary>From here up there is no doubt who the photograph is of.</summary>
    private const double ClearFace = 0.22;

    /// <summary>Up to here the artist is still standing in a photograph rather than filling it.</summary>
    private const double CrowdedFace = 0.45;

    /// <summary>Past this the photograph IS the face.</summary>
    private const double CloseUpFace = 0.65;

    /// <summary>What a speck of a face is worth. The picture may still be a fine wide shot, so this is a
    /// discount and not a disqualification: it is simply not a picture OF anybody.</summary>
    private const double TinyCredit = 0.35;

    /// <summary>What a headshot's face is worth. Enough to keep a well-placed one ahead of a badly-placed
    /// one, not enough to put it in front of the same artist photographed with some room.</summary>
    private const double CloseUpCredit = 0.30;

    /// <summary>How flat a patch of the picture has to be to count as open ground, as a standard deviation
    /// of brightness on the 0..255 scale. A sky, a wall or an out-of-focus room is well under it; a face,
    /// a crowd or a lit stage is not.</summary>
    private const double QuietPatch = 10.0;

    /// <summary>The other test for open ground: a patch that is this much calmer than the picture's own
    /// average patch. It is the same question asked of a photograph that is busy or soft all over. See
    /// <see cref="OpenAreaFraction"/>.</summary>
    private const double QuietShare = 0.35;

    /// <summary>How much a photo whose faces are mostly hidden is knocked down: enough that it never
    /// leads, but not all the way to zero, so it still sorts sensibly among the other disqualified
    /// photos at the back of the carousel.</summary>
    private const double ObstructionPenalty = 0.15;

    /// <summary>If more than this fraction of a photo's faces are behind the cover or the record, it is
    /// disqualified from leading. A picture of the artist that cannot be seen is not the one to open
    /// on.</summary>
    private const double ObstructedMajority = 0.5;

    /// <summary>How far down the source's order the likes credit reaches. It is roughly the number of
    /// photos the chooser scores at all: past the front of the list they are all "not the favourite",
    /// and what separates them has to be the picture.</summary>
    private const double LikesSpan = 7.0;

    /// <summary>
    /// Orders the candidates best-first, returning their original indices: the photographs made for this
    /// header first (<see cref="MadeForTheHeader"/>), then the widest shape on offer, each group ordered
    /// by <see cref="Score"/>. Stable: photos that score the same keep the source's order, which is the
    /// likes order.
    ///
    /// <para>The narrower photographs are not dropped. They follow, ordered among themselves the same way,
    /// so every picture stays reachable with the carousel arrows; they simply never lead.</para>
    ///
    /// <para>The <paramref name="scene"/> supplies the real panel geometry the face is judged against;
    /// if it is omitted, the defaults (<see cref="ArtScene.Default"/>) are used.</para>
    /// </summary>
    public static IReadOnlyList<int> Rank(IReadOnlyList<BackdropFeatures> candidates, ArtScene? scene = null)
    {
        var s = scene ?? ArtScene.Default;
        var wide = Widest(candidates);

        return Enumerable.Range(0, candidates.Count)
            .OrderByDescending(i => MadeForTheHeader(candidates[i]))   // the hard rule, above all of it
            .ThenByDescending(i => wide.Contains(i))      // step one: shape, and it is not negotiable
            .ThenByDescending(i => Score(candidates[i], s))
            .ThenBy(i => candidates[i].OriginalIndex)
            .ToList();
    }

    /// <summary>
    /// What separates photographs the header can equally use, 0..1: the face, the look, and the likes
    /// order, knocked down if most of its faces are hidden behind the cover or the record.
    ///
    /// <para>Shape is deliberately NOT in here. It is asked first and separately (see <see cref="Rank"/>
    /// and <see cref="Widest"/>), because a number added to this one could always be out-voted, and
    /// that could put a portrait at the front of a header.</para>
    /// </summary>
    public static double Score(BackdropFeatures f, ArtScene? scene = null)
    {
        var raw = WeightFit * Fit(f, scene) + WeightLook * Look(f) + WeightLikes * Likes(f.OriginalIndex);
        return f.FacesObstructedFraction > ObstructedMajority ? raw * ObstructionPenalty : raw;
    }

    /// <summary>
    /// How well the photo suits the header once its shape has qualified it: where the face is, how much
    /// room there is around it, and how much quiet ground the picture carries.
    /// </summary>
    public static double Fit(BackdropFeatures f, ArtScene? scene = null)
    {
        var s = scene ?? ArtScene.Default;
        var open = FitOpen * Math.Clamp(f.OpenFraction, 0, 1);

        return f.FaceAcross < 0
            ? open                                      // no face: a scene or a logo, the ground alone
            : open + FitFace * s.FacePosition(f.FaceAcross) * FacePresence(f.FaceHeight);
    }

    /// <summary>
    /// How much the face is worth given how much of the frame it fills: full marks across the band where
    /// the artist is clearly the subject and still has room around them, discounted at both ends.
    ///
    /// <para>It reads as one rule but it settles two problems. It guards against a HEADSHOT, because a
    /// header nine times wider than it is tall has nowhere to put one: a close-up there is a pair of
    /// eyes across a metre of screen, with nothing either side for the blurred fill, the picture's
    /// edges to dissolve into, or the cards to be read over. It also guards against a SPECK, because a
    /// wide shot of a stage with a figure somewhere on it is not a picture of the artist at all; it
    /// only contains them. A rule that penalised SIZE alone would fix the first problem and cause the
    /// second.</para>
    ///
    /// <para>Neither end is zero: an artist photographed only in close-up, or only from the back of a
    /// hall, must still get the best of what there is.</para>
    /// </summary>
    private static double FacePresence(double faceHeight)
    {
        if (faceHeight <= 0)
            return 1;                     // no size measured: judge it on position alone

        if (faceHeight <= TinyFace)
            return TinyCredit;
        if (faceHeight < ClearFace)       // growing into the subject of its own photograph
            return TinyCredit + (faceHeight - TinyFace) / (ClearFace - TinyFace) * (1 - TinyCredit);

        if (faceHeight <= CrowdedFace)
            return 1;                     // the band the header actually wants

        if (faceHeight >= CloseUpFace)
            return CloseUpCredit;
        return 1 - (faceHeight - CrowdedFace) / (CloseUpFace - CrowdedFace) * (1 - CloseUpCredit);
    }

    /// <summary>How good the photo looks in the abstract, 0..1.</summary>
    public static double Look(BackdropFeatures f)
        => LookBrightness * BrightnessScore(f.MeanLuma)
         + LookContrast * Math.Clamp(f.Contrast, 0, 1)
         + LookColour * Math.Clamp(f.Colorfulness, 0, 1)
         + LookResolution * ResolutionScore(f.SourceMinDim);

    /// <summary>Peaks at <see cref="IdealLuma"/> and falls to 0 at black and at near-white. A strip
    /// photo should be mid-dark: not a black bar and not a glare under the text.</summary>
    private static double BrightnessScore(double meanLuma)
    {
        var d = Math.Abs(Math.Clamp(meanLuma, 0, 1) - IdealLuma);
        var reach = Math.Max(IdealLuma, 1 - IdealLuma);   // the further edge, so both ends reach 0
        return Math.Clamp(1 - d / reach, 0, 1);
    }

    private static double ResolutionScore(int shortSide)
        => shortSide <= 0 ? 0.5 : Math.Clamp(shortSide / SharpEnoughShortSide, 0, 1);

    /// <summary>
    /// The likes prior: the source's favourite scores 1, and the credit fades away over the next
    /// <see cref="LikesSpan"/> photos.
    ///
    /// <para>The span is fixed rather than spread across the list. That is the difference between a
    /// prior and a verdict. If it were normalised by the list's own length, a two-photo list would read
    /// as "the first is the most liked photograph there is and the second is the least", and would
    /// spend the whole likes weight on that. Where a photo sits in the source's order is a property of
    /// the photo, not of how many others happen to be beside it.</para>
    /// </summary>
    private static double Likes(int originalIndex)
        => Math.Clamp(1 - originalIndex / LikesSpan, 0, 1);

    /// <summary>
    /// Reads a thumbnail's features. <paramref name="rgba"/> is row-major, 4 bytes a pixel,
    /// <paramref name="width"/> × <paramref name="height"/>. A small decode is enough, because this is
    /// only a ranking. <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/> are the real
    /// photo's pixels (for aspect and sharpness); pass 0 for either if unknown and they fall back to
    /// the thumbnail's own shape. <paramref name="faceAcross"/> is where a face sits across the photo
    /// (0..1), or -1 for none. The app runs the detector, so this method needs no model.
    /// </summary>
    public static BackdropFeatures Measure(
        ReadOnlySpan<byte> rgba, int width, int height,
        int sourceWidth, int sourceHeight, int originalIndex, double faceAcross = -1)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            return new BackdropFeatures(0, 0, 0, 1, faceAcross, 0, originalIndex);

        var luma = new double[width * height];
        double sum = 0, sumSq = 0;
        double sumRg = 0, sumRgSq = 0, sumYb = 0, sumYbSq = 0;

        for (var i = 0; i < luma.Length; i++)
        {
            double r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            var l = 0.299 * r + 0.587 * g + 0.114 * b;
            luma[i] = l;
            sum += l;
            sumSq += l * l;

            // Hasler & Süsstrunk's opponent-channel colourfulness, the cheap version.
            var rg = r - g;
            var yb = 0.5 * (r + g) - b;
            sumRg += rg; sumRgSq += rg * rg;
            sumYb += yb; sumYbSq += yb * yb;
        }

        var n = luma.Length;
        var meanLuma = sum / n;
        var lumaStd = Math.Sqrt(Math.Max(0, sumSq / n - meanLuma * meanLuma));

        var rgStd = Std(sumRg, sumRgSq, n);
        var ybStd = Std(sumYb, sumYbSq, n);
        var rgMean = sumRg / n;
        var ybMean = sumYb / n;
        var colorfulness = Math.Sqrt(rgStd * rgStd + ybStd * ybStd)
                         + 0.3 * Math.Sqrt(rgMean * rgMean + ybMean * ybMean);

        return new BackdropFeatures(
            MeanLuma: meanLuma / 255.0,
            // 64 is roughly the spread of a well-exposed photo. It lets a good photo land near 1
            // without a hard clip flattening everything above it.
            Contrast: Math.Clamp(lumaStd / 64.0, 0, 1),
            Colorfulness: Math.Clamp(colorfulness / 110.0, 0, 1),
            Aspect: sourceWidth > 0 && sourceHeight > 0
                ? (double)sourceWidth / sourceHeight
                : (double)width / height,
            FaceAcross: faceAcross,
            SourceMinDim: sourceWidth > 0 && sourceHeight > 0 ? Math.Min(sourceWidth, sourceHeight) : 0,
            OriginalIndex: originalIndex,
            OpenFraction: OpenAreaFraction(luma, width, height));
    }

    /// <summary>
    /// Reads a thumbnail's features against a real <paramref name="scene"/>, from every face the detector
    /// found rather than one averaged value.
    ///
    /// The face carried into <see cref="BackdropFeatures.FaceAcross"/> is the <b>best-placeable</b> one:
    /// the face nearest the clear band, not the average of them all. Averaging gives wrong answers: two
    /// faces at 0.1 and 0.9 average to a perfect-looking 0.5 while both are hidden. Alongside it,
    /// <see cref="BackdropFeatures.FacesObstructedFraction"/> records how many faces are out of sight, so
    /// a photo whose faces are mostly hidden can be disqualified from leading, and
    /// <see cref="BackdropFeatures.FaceHeight"/> takes the BIGGEST face, which decides whether this
    /// reads as a close-up or as a photograph with somebody in it.
    /// </summary>
    public static BackdropFeatures Measure(
        ReadOnlySpan<byte> rgba, int width, int height,
        int sourceWidth, int sourceHeight, int originalIndex,
        IReadOnlyList<FaceSpot> faces, ArtScene scene)
    {
        double bestAcross = -1, bestPos = -1, biggest = 0;
        var obstructed = 0;
        foreach (var face in faces)
        {
            if (scene.Obstructs(face.Across))
                obstructed++;
            var pos = scene.FacePosition(face.Across);
            if (pos > bestPos)
            {
                bestPos = pos;
                bestAcross = face.Across;
            }
            biggest = Math.Max(biggest, face.Height);
        }

        var faceAcross = faces.Count > 0 ? bestAcross : -1;
        var obstructedFraction = faces.Count > 0 ? (double)obstructed / faces.Count : 0;

        return Measure(rgba, width, height, sourceWidth, sourceHeight, originalIndex, faceAcross)
            with { FacesObstructedFraction = obstructedFraction, FaceHeight = biggest };
    }

    private static double Std(double sum, double sumSq, int n)
    {
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    /// <summary>
    /// The fraction of the photo that is open ground: patches flat enough
    /// (<see cref="QuietPatch"/>) to be sky, a wall, a dark stage or an out-of-focus room, rather
    /// than picture.
    ///
    /// <para>A close-up cropped to a letterbox has no ground at all, because every pixel of it is face.
    /// It lands as a rectangle of somebody's eyes with the app's frost and text sitting on top of it. A
    /// wide shot of the same artist on a stage has the subject in the middle and open dark either side.
    /// The picture's feathered edges dissolve into that open dark, the blurred fill matches it, and the
    /// cards are read over it.</para>
    ///
    /// <para>It is measured in patches rather than per pixel, because what matters is whether there is a
    /// CONTINUOUS piece of quiet somewhere, and a per-pixel count would call the smooth interior of any
    /// large object quiet. The patch is a fraction of the photo's short side, so a thumbnail of any size
    /// is asked the same question about the photograph.</para>
    ///
    /// <para>A patch counts as open if it is flat outright, OR if it is far calmer than the rest of this
    /// picture. The second test makes the answer mean the same thing on every photograph. On a
    /// thumbnail, a dense scene has been averaged down until its patches look smooth by any fixed
    /// standard, while a studio shot on a lit backdrop is calmer still. What separates them is the
    /// CONTRAST between the busy part and the rest, and that survives the scaling.</para>
    /// </summary>
    private static double OpenAreaFraction(double[] luma, int width, int height)
    {
        if (width < 2 || height < 2)
            return 0;

        var patch = Math.Max(2, Math.Min(width, height) / 8);

        var stds = new List<double>();
        for (var top = 0; top + patch <= height; top += patch)
        {
            for (var left = 0; left + patch <= width; left += patch)
            {
                double sum = 0, sumSq = 0;
                for (var y = top; y < top + patch; y++)
                {
                    var row = y * width;
                    for (var x = left; x < left + patch; x++)
                    {
                        var l = luma[row + x];
                        sum += l;
                        sumSq += l * l;
                    }
                }

                stds.Add(Std(sum, sumSq, patch * patch));
            }
        }

        if (stds.Count == 0)
            return 0;

        var busy = stds.Average() * QuietShare;
        return (double)stds.Count(s => s < QuietPatch || s < busy) / stds.Count;
    }
}
