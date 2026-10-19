using Microsoft.Extensions.Logging;

using RPM.Core.Art;
using RPM.Core.Threading;

using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Art;

using SkiaSharp;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// Where an act's photograph is cut for the home screen's banner.
///
/// <para><b>Why this is not the playlist headers' method.</b> That one
/// (<see cref="PlaylistCovers.FramedAsync"/>) rejects a photograph when the faces in it are small or
/// absent, because it chooses between twenty candidates for five panels and can afford to pass. The
/// banner holds one record and one act. A band shot where every face is a twelfth of the frame is the
/// picture, and rejecting it would leave the plate with the cover blurred behind the text. A crop that
/// never rejects is needed here.</para>
///
/// <para><b>What it does instead.</b> It runs the same two models through the same shared method, then
/// asks <see cref="SaliencyCrop.Choose"/> with the PLATE's own obstruction geometry
/// (<see cref="HomeBanner.Scene"/>): the cover and the text cover the left of this plate, so the
/// subject belongs about three quarters of the way across. Choose never rejects. With no subject to
/// find it centres the widest window it can, which is the worst case, not the usual one.</para>
/// </summary>
internal static class HomeFeatureArt
{
    /// <summary>
    /// One photograph, cut to a plate <paramref name="aspect"/> wide for every one of its height.
    /// </summary>
    /// <param name="photo">The act's picture as it arrived.</param>
    /// <param name="plate">How wide the plate is drawn, for the obstruction geometry.</param>
    /// <param name="log">Where the decision is logged. The screen does not show how a crop was chosen:
    /// a plate framed by the models and one framed by the fallback both look like a photograph.</param>
    /// <returns>The cut, or the picture as it arrived when there was nothing to decide with. That is
    /// still a photograph, and the surface fills the plate with it. <see cref="Framed.Settled"/> says
    /// whether both models answered, which is the only cut worth keeping between launches.</returns>
    public static Task<Framed> FrameAsync(
        byte[] photo, double aspect, double plate, ILogger log, CancellationToken ct)
        => FrameAsync(photo, aspect, HomeBanner.Scene(plate), log, ct);

    /// <summary>
    /// The same cut against another surface's obstruction geometry: what covers the picture and where
    /// its subject should go. The head of Pegasus's artist page calls this with its own scene (the name
    /// in its lower left, the foot fading out).
    /// </summary>
    public static async Task<Framed> FrameAsync(
        byte[] photo, double aspect, ArtScene scene, ILogger log, CancellationToken ct)
    {
        try
        {
            using var source = SKBitmap.Decode(photo);
            if (source is null || source.Width < 8 || source.Height < 8)
                return Framed.AsItCame(photo);

            // The faces come first. They are a hint, not a verdict: the crop uses them to frame heads
            // and shoulders when they are found, and frames the subject as a whole when they are not.
            // No photograph is rejected on this answer.
            var scan = await PlaylistCovers.FacesAsync(source, SaliencyService.Size, ct);
            if (ct.IsCancellationRequested)
                return Framed.AsItCame(photo);

            // U²-Netp when the model is installed, local contrast when it is not. The fallback finds
            // detail rather than subject, so it is weaker, but still better than a crop chosen on
            // nothing.
            var saliency = await PlaylistCovers.SaliencyReadAsync(source, ct);
            if (saliency.Map is not { } map || ct.IsCancellationRequested)
                return Framed.AsItCame(photo);

            // The map is the photograph SQUASHED INTO A SQUARE, so shapes change: a window that is
            // 3.8:1 in the photograph is 3.8 x height / width : 1 in the map. On a 640 x 360 press
            // shot, a plate of 3.8 asks the map for 2.1.
            var inMap = aspect * source.Height / source.Width;

            var faces = scan.Faces;
            var mirror = false;

            var chosen = SaliencyCrop.Choose(
                map, SaliencyService.Size, SaliencyService.Size, inMap, faces, scene);

            if (chosen.Rect.Width <= 0 || chosen.Rect.Height <= 0)
                return Framed.AsItCame(photo);

            // ── the picture mirrored ──
            //
            // A press shot with its people on the left puts them on the wrong side of this plate,
            // because the cover and the text are on the left. The crop asks for them at
            // scene.FocusAcross, but it can only slide the window over material that is THERE. A face
            // 40% across a photograph cannot be put at 76% across a window nearly as wide as the
            // photograph, because the window's left edge would be off the picture. The window is
            // clamped back inside, and the face stays where it was: behind the title.
            //
            // Mirrored, the same photograph needs no zoom and no sliding. It is tried when the heads
            // are on the left, or when they LANDED behind the text. It is used only if it really puts
            // them further across, so a photograph that does not improve stays as it was taken.
            //
            // Everything measured so far is mirrored, not measured again (the map is an array and the
            // faces are boxes). The picture itself is re-encoded only once the mirrored framing wins.
            //
            // Where the subject IS, in the map's own pixels: the faces when anybody was found, and the
            // centre of mass of the saliency when nobody was. The detector finding nobody is common (a
            // singer in profile, a figure at a distance, a machine without the model), and a rule that
            // only works on faces would rarely apply.
            var subjectAt = SubjectAcross(map, faces, SaliencyService.Size);
            var landed = LandsAt(chosen.Rect, subjectAt);

            if (MoreHeadsOnTheLeft(faces, SaliencyService.Size)
                || subjectAt < SaliencyService.Size / 2.0
                || landed < scene.FaceHardLeft)
            {
                var turnedMap = MirrorMap(map, SaliencyService.Size);
                var turnedFaces = (IReadOnlyList<FaceBox>)[.. faces.Select(box => new FaceBox(
                    SaliencyService.Size - (box.X + box.Width), box.Y, box.Width, box.Height))];

                var other = SaliencyCrop.Choose(
                    turnedMap, SaliencyService.Size, SaliencyService.Size, inMap, turnedFaces, scene);

                var otherLanded = LandsAt(other.Rect, SaliencyService.Size - subjectAt);

                if (other.Rect.Width > 0 && other.Rect.Height > 0 && otherLanded > landed)
                {
                    mirror = true;
                    faces = turnedFaces;
                    chosen = other;

                    log.LogInformation(
                        "[home] turned the picture round: the subject lands at {Was:0.00} as taken, {Now:0.00} mirrored",
                        landed, otherLanded);
                }
            }

            // Back to the photograph's own pixels. The map is a square resize of it, so the two axes
            // scale by different amounts. One scale for both would shift the window sideways on every
            // picture that is not square.
            var across = (double)source.Width / SaliencyService.Size;
            var down = (double)source.Height / SaliencyService.Size;

            var window = new CropRect(
                (int)Math.Round(chosen.Rect.X * across),
                (int)Math.Round(chosen.Rect.Y * down),
                (int)Math.Round(chosen.Rect.Width * across),
                (int)Math.Round(chosen.Rect.Height * down));

            log.LogInformation(
                "[home] framing {Source}: faces={Faces} looked={Looked} focus={Focus:0.00} across, window {Window}, fills={Fills}",
                $"{source.Width}x{source.Height}", faces.Count, scan.Looked,
                scene.FocusAcross,
                $"{window.Width}x{window.Height} at {window.X},{window.Y}", chosen.FillsTarget);

            // Also written to the diagnostics scope, next to the "cut <act> for a plate of <n>" line the
            // caller writes: that line says WHICH picture was cut and this one says what happened to it.
            // The ILogger line above goes to the host's logging, which on the WinUI head cannot be read
            // back, so a plate that cut off somebody's head is checked here.
            // The window the crop chose is not the plate's shape; this one is (see Squared).
            var cut = Squared(window, aspect, source.Width, source.Height);

            RPM.Core.Diagnostics.Scope.Said(
                "home",
                $"framed: photo {source.Width}x{source.Height}, faces {faces.Count}, asked {aspect:F2}:1, "
              + $"window {window.Width}x{window.Height} at {window.X},{window.Y} "
              + $"({(double)window.Width / Math.Max(1, window.Height):F2}:1), "
              + $"cut {cut.Width}x{cut.Height} at {cut.X},{cut.Y}, fills {chosen.FillsTarget}");

            // Cut and mirrored in ONE pass over the bitmap that is already decoded, and encoded once.
            // Separate steps would mean three PNG encodes and three decodes of the same photograph per
            // card, each a large object of a megabyte or two, three cards at a time. The resulting
            // garbage collection stalls the frames that step the banner.
            return Composed(source, cut, mirror) is { } composed
                ? new Framed(composed, Settled: scan.Looked && saliency.FromModel)
                : Framed.AsItCame(photo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A picture that cannot be decoded can still be shown whole on the plate.
            log.LogInformation("[home] framing failed: {Why}", ex.Message);
            return Framed.AsItCame(photo);
        }
    }

    /// <summary>
    /// A photograph cut for the plate, and whether the cut is the models' answer.
    /// </summary>
    /// <param name="Bytes">What the plate draws.</param>
    /// <param name="Settled">True when the face detector looked and U²-Netp drew the saliency map. False
    /// for the picture as it arrived and for a crop chosen with the local-contrast fallback. Both are
    /// fine to show, but neither is the answer this machine would give with its models working, so
    /// neither is kept between launches.</param>
    internal readonly record struct Framed(byte[] Bytes, bool Settled)
    {
        public static Framed AsItCame(byte[] photo) => new(photo, Settled: false);
    }

    /// <summary>
    /// Where the subject is across the picture, in the map's own pixels: the faces when anybody was
    /// found, and the centre of mass of the saliency when nobody was.
    ///
    /// <para>The second case is what makes this work on an ordinary shelf. A face detector finds nobody
    /// in a photograph of a singer in profile, a figure at a distance or a person seen from behind, and
    /// in every picture on a machine where the model is not installed. A rule that only works on faces
    /// would rarely apply.</para>
    /// </summary>
    internal static double SubjectAcross(double[] map, IReadOnlyList<FaceBox> faces, int size)
    {
        if (faces.Count > 0)
            return faces.Average(face => face.X + (face.Width / 2.0));

        double total = 0, weighted = 0;
        for (var row = 0; row < size; row++)
        {
            var at = row * size;
            for (var column = 0; column < size; column++)
            {
                var strength = map[at + column];
                if (strength <= 0)
                    continue;

                total += strength;
                weighted += strength * column;
            }
        }

        return total <= 0 ? size / 2.0 : weighted / total;
    }

    /// <summary>
    /// Where the subject lands across a chosen window, 0 (its left edge) .. 1 (its right).
    ///
    /// <para>What the crop ASKED for is <see cref="ArtScene.FocusAcross"/>; this is what it got. The two
    /// differ whenever the photograph runs out before the window can slide that far, which is common on
    /// a plate this wide. This is the only measurement that shows whether the subject is actually clear
    /// of the text.</para>
    /// </summary>
    internal static double LandsAt(CropRect window, double subjectAcross)
        => window.Width <= 0
            ? 0.5
            : Math.Clamp((subjectAcross - window.X) / window.Width, 0, 1);

    /// <summary>
    /// Whether the people in a photograph are on its left.
    ///
    /// <para>Faces are counted, not weighed: a band of four on the left is four heads whatever their
    /// size. When the counts are equal, the face area decides, and only when one side is clearly heavier.
    /// A single large face against one small one is a portrait with somebody else in it, and mirroring
    /// the photograph for a margin of a few percent would flip balanced pictures back and forth.</para>
    /// </summary>
    internal static bool MoreHeadsOnTheLeft(IReadOnlyList<FaceBox> faces, int size)
    {
        if (faces.Count == 0)
            return false;

        var middle = size / 2.0;
        int left = 0, right = 0;
        double leftMass = 0, rightMass = 0;

        foreach (var face in faces)
        {
            var mass = (double)Math.Max(1, face.Width) * Math.Max(1, face.Height);
            if (face.X + (face.Width / 2.0) < middle)
            {
                left++;
                leftMass += mass;
            }
            else
            {
                right++;
                rightMass += mass;
            }
        }

        return left > right || (left == right && leftMass > rightMass * LopsidedBy);
    }

    /// <summary>How much heavier one side must be before equal counts are decided by face area.</summary>
    private const double LopsidedBy = 1.25;

    /// <summary>
    /// The finished background for one plate: the chosen window of the photograph, mirrored when the
    /// subject was on the wrong side, drawn once and encoded once.
    ///
    /// <para><b>The photograph is fully opaque to its left edge.</b> It fills the whole card with no
    /// fade, so a person standing at the left of a press shot is drawn at full strength. Behind it is
    /// the same photograph blurred, so there is no visible edge.</para>
    ///
    /// <para><b>One pass, to avoid repeated work.</b> Cutting in separate steps and mirroring into a
    /// second surface would mean several encodes and decodes of the same picture for every card, and the
    /// banner builds three cards at a time. Each encode is a large-object allocation, and the resulting
    /// garbage collection stalls the frames that step the banner. The pixels here come from the bitmap
    /// this method is given, which the models have already decoded.</para>
    ///
    /// <para>It is given the CUT, not the crop's own window: squaring the window to the plate is a
    /// framing decision (see <see cref="Squared"/>), it is logged with the rest of the framing, and this
    /// method draws what was decided.</para>
    /// </summary>
    private static byte[]? Composed(SKBitmap source, CropRect cut, bool mirror)
    {
        try
        {
            if (cut.Width < 8 || cut.Height < 8)
                return null;

            // The window is in the MIRRORED picture's pixels when the subject was on the left, but the
            // source is the photograph as it arrived. So the window is taken the same distance from the
            // other edge.
            var from = mirror
                ? SKRect.Create(source.Width - (cut.X + cut.Width), cut.Y, cut.Width, cut.Height)
                : SKRect.Create(cut.X, cut.Y, cut.Width, cut.Height);

            var info = new SKImageInfo(cut.Width, cut.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;

            // Black, not transparent: the draw below covers the whole surface, and the output is a
            // JPEG, which has no alpha channel anyway.
            canvas.Clear(SKColors.Black);

            using var image = SKImage.FromBitmap(source);
            if (image is null)
                return null;

            var into = SKRect.Create(0, 0, cut.Width, cut.Height);

            if (mirror)
            {
                canvas.Save();
                canvas.Scale(-1, 1);
                canvas.Translate(-cut.Width, 0);
            }

            canvas.DrawImage(image, from, into, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            if (mirror)
                canvas.Restore();

            using var drawn = surface.Snapshot();

            // JPEG, because every pixel is opaque. A 760 x 352 plate is about 60 KB as a JPEG against a
            // megabyte as a PNG, and the card decodes one of these per act on a screen that draws three
            // at a time.
            using var data = drawn.Encode(SKEncodedImageFormat.Jpeg, 92);
            return data is { Size: > 0 } ? data.ToArray() : null;
        }
        catch
        {
            return null;   // the plate then shows the photograph whole
        }
    }

    /// <summary>
    /// The chosen window squared to the plate's shape, inside the photograph: as much of the window's
    /// WIDTH as the plate can carry, the rows to match, and both centred on the window the crop chose.
    /// </summary>
    /// <remarks>
    /// <para>The crop comes back a few percent off the shape it was asked for, because its edges are
    /// pushed inside the photograph. Sometimes it is far off: asked for 2.16:1 on a 1920x1080 press
    /// shot, it can answer with a band 1920x385, five to one, because that is where the heads are. The
    /// window must be given the plate's shape.</para>
    ///
    /// <para><b>The rows change, not the columns.</b> A window wider than the plate takes more of the
    /// photograph above and below it, up to what the photograph has. Width is given up only when the
    /// rows run out. Squaring that 1920x385 band by keeping the middle 832 columns would zoom in to 43%
    /// of the width and re-centre on the middle of the photograph, away from the people the crop placed
    /// three quarters of the way across, and the heads would go off the top of the card. Grown instead,
    /// the same band becomes 1920x889: the whole width, with everybody in it.</para>
    ///
    /// <para>A window TALLER than the plate is trimmed from the middle. This is the same rule seen from
    /// the other side: the crop chose those columns on purpose.</para>
    /// </remarks>
    internal static CropRect Squared(CropRect window, double aspect, int sourceWidth, int sourceHeight)
    {
        if (window.Width <= 0 || window.Height <= 0 || aspect <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return window;

        // The widest window of the plate's shape the photograph can hold at all.
        var widest = (int)Math.Round(Math.Min(sourceWidth, sourceHeight * aspect));
        if (widest < 8)
            return window;

        var cols = Math.Min(window.Width, widest);
        var rows = (int)Math.Round(cols / aspect);
        if (rows < 8 || cols < 8)
            return window;

        // Centred on the window the crop chose, in both axes, and then pushed inside the photograph.
        var x = (int)Math.Round(window.X + (window.Width / 2.0) - (cols / 2.0));
        var y = (int)Math.Round(window.Y + (window.Height / 2.0) - (rows / 2.0));

        return new CropRect(
            Math.Clamp(x, 0, Math.Max(0, sourceWidth - cols)),
            Math.Clamp(y, 0, Math.Max(0, sourceHeight - rows)),
            cols, rows);
    }

    /// <summary>The saliency map reversed, row by row: the same answer about the same picture, read from
    /// the other end. About a thousand times cheaper than asking the model again.</summary>
    internal static double[] MirrorMap(double[] map, int size)
    {
        var turned = new double[map.Length];
        for (var row = 0; row < size; row++)
        {
            var at = row * size;
            for (var column = 0; column < size; column++)
                turned[at + column] = map[at + size - 1 - column];
        }

        return turned;
    }
}
