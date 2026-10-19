using RPM.Core.Art;
using SkiaSharp;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Art;

/// <summary>One backdrop after ranking: its URL and the score it earned, best-first.</summary>
public sealed record RankedBackdrop(string Url, double Score);

/// <summary>
/// The art chooser's preselect. Given an artist's candidate backdrops, in the source's likes order, it downloads
/// a capped few and returns the order from <see cref="BackdropRanker"/>.
///
/// <para>It runs the ranker's two steps in the ranker's order, and that order is what keeps it affordable.
/// <b>Shape first.</b> Each candidate's width and height come out of the decode for free. The widest photographs
/// are the ones the header can use. <b>Then the faces.</b> UltraFace is a model run for each photograph, so it runs
/// only on the shortlist. A photograph that loses on shape cannot win on its face, so detecting faces in it would
/// be wasted work.</para>
///
/// <para>The heavier crop, which uses saliency and a full-resolution cut, runs only on the one photo the chooser
/// picks, in <c>ArtDisplay</c>. Everything here is best-effort. A download that fails, or an image that will not
/// decode, drops out of the ranking rather than failing it.</para>
/// </summary>
public sealed class BackdropPreselector(FaceService faces)
{
    /// <summary>
    /// How many candidates go through the face detector and the score. These are the <b>widest</b> ones, not the
    /// first ones in the list.
    ///
    /// <para>The shortlist is taken from the head of fanart's likes order. The most-liked photographs are the
    /// strong contenders at being liked. But a like is a vote on the photograph, not on this header. A gallery's
    /// ninth most-liked photograph could be the only one shaped for a strip a dozen times wider than it is tall. It
    /// could never be chosen. It is not downloaded until it is measured, so its aspect is unknown, and it never
    /// reaches the first step. The cut is made before the shape is known, so the shortlist can miss the one
    /// photograph the header needs.</para>
    ///
    /// <para>Shape is the first question everywhere else here (see <see cref="BackdropRanker.Widest"/>), and it is
    /// the first question at this gate too. Likes still count, as a term in the score. They do not decide which
    /// photographs may be scored.</para>
    /// </summary>
    private const int MaxRanked = 8;

    /// <summary>
    /// How many are downloaded to be MEASURED. This is more than are scored. The widest cannot be known without the
    /// dimensions, and the dimensions are in the file.
    ///
    /// <para>This is the real cost of asking about shape first. A header read is all that is needed, but the
    /// download brings the whole file. So an artist with a full gallery pays one download for each photograph the
    /// first time they play. The bytes are kept, so this happens once per artist and not once per play. The
    /// detector, the expensive part, still sees only <see cref="MaxRanked"/> of them.</para>
    ///
    /// <para>The cap stops an artist with a hundred photographs from causing a hundred downloads. Past the cap, the
    /// likes order decides which photographs are measured. That order is the fallback when nothing better is
    /// known.</para>
    /// </summary>
    private const int MaxMeasured = 24;

    /// <summary>The long edge of the thumbnail that the colour and openness features are read from. It is small on
    /// purpose. This is a ranking, not the picture.</summary>
    private const int ThumbLongEdge = 96;

    /// <summary>The square that the face detector reports box coordinates in. A box's centre over this value gives
    /// its position across the photo. Its height over this value gives the share of the frame it fills.</summary>
    private const int FaceMapSize = 1000;

    /// <summary>Goes through Wire like every other client that leaves this network. So this one queues at the online
    /// gate, and inside the connection budget. It ranks a screenful of photographs at once, which is exactly the
    /// caller the socket ceiling exists for.</summary>
    private static readonly HttpClient Http =
        RPM.Core.Net.Wire.Outside(RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(15));

    /// <summary>
    /// Ranks <paramref name="urls"/> best first. Returns null when the list is too short to reorder, or when nothing
    /// could be measured. In both cases the caller should keep the order it already has.
    /// </summary>
    public async Task<IReadOnlyList<RankedBackdrop>?> RankAsync(
        IReadOnlyList<string> urls, ArtScene scene, CancellationToken ct = default)
    {
        if (urls.Count < 2)
            return null;

        var measured = urls.Take(MaxMeasured).ToList();
        var remainder = urls.Skip(MaxMeasured).ToList();

        // Download and decode the contenders at the same time. Each one is independent and best-effort. The index
        // kept with each one is its position in the SOURCE's order, which is the likes order. That is what the
        // score's likes term means. The index survives every reordering below.
        var decoded = await Task.WhenAll(measured.Select((url, i) => DecodeAsync(url, i, ct)));

        var readable = decoded.Where(d => d is not null).Select(d => d!.Value).ToList();
        if (readable.Count == 0)
            return null;   // Nothing usable, so the current order stays.

        // ── the gate: the widest, not the first ──
        //
        // Every one of these has been decoded, so every one has a real aspect. The cut is made on the thing the
        // header cares about. Ties go to the likes order. Of two photographs of the same shape, the more liked is
        // the better photograph. That is the question likes answer well.
        var usable = readable
            .OrderByDescending(d => d.Aspect)
            .ThenBy(d => d.OriginalIndex)
            .Take(MaxRanked)
            .ToList();

        // ── step one: shape, off the decoded dimensions, free ──
        var wide = BackdropRanker.Widest(usable.Select(d => d.Aspect).ToList());

        // ── step two: the detector, on the shortlist only ──
        //
        // Sequential rather than concurrent. The session is single-threaded behind its own gate. So running these
        // together would only queue them somewhere else.
        var features = new List<BackdropFeatures>(usable.Count);
        for (var i = 0; i < usable.Count; i++)
        {
            var d = usable[i];

            // The shortlist, plus anything shaped for the header outright. The two are usually the same set. The
            // difference matters. An artist with a panorama has a shortlist measured against the panorama, so their
            // ordinary 16:9 photographs fall outside it. One of those can now lead on the hard requirement. Leading
            // without being detected would mean leading without anyone checking whether the artist is behind the
            // album cover. That is the one thing that disqualifies a picture from the front.
            var contends = wide.Contains(i) || BackdropRanker.ShapedForHeader(d.Aspect);
            var spots = contends ? await FacesAsync(d, ct) : [];
            features.Add(BackdropRanker.Measure(
                d.Thumb, d.Width, d.Height, d.SourceWidth, d.SourceHeight, d.OriginalIndex, spots, scene));
        }

        var order = BackdropRanker.Rank(features, scene);
        var ranked = order
            .Select(i => new RankedBackdrop(measured[features[i].OriginalIndex], BackdropRanker.Score(features[i], scene)))
            .ToList();

        Explain(order, features, wide, scene);

        // Everything else follows the ranked ones, in the source's order. So every photo is still reachable in the
        // carousel. That covers the photos too narrow to pass the gate, the photos that did not download, and the
        // tail past MaxMeasured that was never measured. These are the same case here. They are not ranked, but they
        // still belong to the artist.
        var placed = new HashSet<string>(ranked.Select(r => r.Url), StringComparer.OrdinalIgnoreCase);
        foreach (var url in measured.Concat(remainder))
            if (placed.Add(url))
                ranked.Add(new RankedBackdrop(url, 0));

        return ranked;
    }

    /// <summary>The faces in one candidate, as fractions of the photograph. The detector's boxes are normalised to
    /// the frame it was given. That is the whole photo squashed to 320 by 240. So a box's height over the map size IS
    /// its share of the photograph's height, whatever shape the photograph is.</summary>
    private async Task<IReadOnlyList<FaceSpot>> FacesAsync(Decoded d, CancellationToken ct)
    {
        try
        {
            // The boxes alone. A candidate with nobody in it, and a candidate nobody looked at, both come back as an
            // empty list. This ranking treats an empty list as "no faces".
            var boxes = (await faces.DetectAsync(d.Face, FaceMapSize, ct)).Faces;
            return boxes
                .Select(b => new FaceSpot((b.X + b.Width / 2.0) / FaceMapSize, b.Height / (double)FaceMapSize))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];   // A detector that will not run leaves the photo judged on its shape and its look.
        }
    }

    private async Task<Decoded?> DecodeAsync(string url, int originalIndex, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var bytes = await Http.GetByteArrayAsync(uri, ct);

            return await Task.Run(() => Decode(bytes, originalIndex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // A photo that will not download simply does not compete.
        }
    }

    /// <summary>
    /// Prints why each candidate landed where it did. It shows whether its shape made the shortlist, the open ground,
    /// the face and its size, and the parts of the score. A chooser that picks the wrong photograph can then be
    /// checked, not guessed at. This is off unless <c>RPM_ART_DIAG=1</c> is set in the environment.
    /// </summary>
    private static void Explain(
        IReadOnlyList<int> order, IReadOnlyList<BackdropFeatures> features, IReadOnlySet<int> wide, ArtScene scene)
    {
        if (RPM.Core.Diagnostics.DevSwitch.Read("RPM_ART_DIAG") is not ("1" or "true"))
            return;

        Said.Line("art", $"header {scene.TargetAspect:F1}:1, clear band {scene.FaceHardLeft:F2}..{scene.FaceHardRight:F2}");
        foreach (var (i, place) in order.Select((i, place) => (i, place)))
        {
            var f = features[i];
            Said.Line("art",
                $"#{place} src{f.OriginalIndex} " +
                $"{(BackdropRanker.MadeForTheHeader(f) ? "MADE" : "----")} " +
                $"{(wide.Contains(i) ? "WIDE" : "narrow")} " +
                $"aspect={f.Aspect:F2} score={BackdropRanker.Score(f, scene):F3} " +
                $"fit={BackdropRanker.Fit(f, scene):F3} look={BackdropRanker.Look(f):F3} " +
                $"open={f.OpenFraction:F2} " +
                $"face={(f.FaceAcross < 0 ? "none" : $"{f.FaceAcross:F2}×{f.FaceHeight:F2}")} " +
                $"hidden={f.FacesObstructedFraction:F2}");
        }
    }

    private readonly record struct Decoded(
        byte[] Thumb, int Width, int Height, int SourceWidth, int SourceHeight, byte[] Face, int OriginalIndex)
    {
        /// <summary>The photograph's own shape. It comes from the real pixels when the codec gives them, and from the
        /// thumbnail otherwise. Step one is decided on this value.</summary>
        public double Aspect => SourceWidth > 0 && SourceHeight > 0
            ? (double)SourceWidth / SourceHeight
            : (double)Width / Math.Max(1, Height);
    }

    /// <summary>Decodes the two views each candidate needs. One is an aspect-preserved thumbnail, for the colour and
    /// openness features. The other is a 320 by 240 RGBA image, for the face detector. The source's real dimensions
    /// are read too. Runs on a worker thread.</summary>
    private static Decoded? Decode(byte[] bytes, int originalIndex)
    {
        int sourceWidth = 0, sourceHeight = 0;
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes));
            if (codec is not null)
            {
                sourceWidth = codec.Info.Width;
                sourceHeight = codec.Info.Height;
            }
        }
        catch { /* Fall back to the thumbnail's own shape for aspect. */ }

        using var src = SKBitmap.Decode(bytes);
        if (src is null || src.Width < 2 || src.Height < 2)
            return null;

        var longEdge = Math.Max(src.Width, src.Height);
        var scale = Math.Min(1.0, (double)ThumbLongEdge / longEdge);
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));

        var thumbInfo = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var thumb = src.Resize(thumbInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (thumb is null)
            return null;

        var (fw, fh) = FaceService.InputSize;
        var faceInfo = new SKImageInfo(fw, fh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var faceBitmap = src.Resize(faceInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (faceBitmap is null)
            return null;

        return new Decoded(
            thumb.GetPixelSpan().ToArray(), w, h, sourceWidth, sourceHeight,
            faceBitmap.GetPixelSpan().ToArray(), originalIndex);
    }
}
