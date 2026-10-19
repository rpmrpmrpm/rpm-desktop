namespace RPM.Core.Art;

/// <summary>A crop window in source-image pixels.</summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height);

/// <summary>
/// The window to show, and whether it is the shape of the slot it goes in.
///
/// <see cref="FillsTarget"/> false means the content would not survive a crop of the slot's shape (for
/// example a face taller than the strip), so the window was left taller and narrower than the slot.
/// The caller fits it inside the slot and fills what is left with a blurred, zoomed copy of the same
/// photo behind it. This is the way a vertical video is shown on a wide screen. Here the fill is
/// barely seen at all, because the album cover sits on one side of it and the record on the other.
/// </summary>
public readonly record struct CropChoice(CropRect Rect, bool FillsTarget);

/// <summary>
/// A framed photograph: the window to show, and the box inside it that must not be interfered with,
/// which holds the people or the subject. <see cref="Content"/> is in the same pixels as
/// <see cref="Rect"/> and always overlaps it, though on a photograph with no room to spare the two can
/// share an edge.
///
/// <para>The caller needs the second box because a frame is not just cropped, it is BLENDED: it is
/// dissolved into whatever is beside it so it has no visible edges. A dissolve that reaches a face
/// erases the face (the band members standing at either end of a group shot fade out while the ones
/// in the middle do not), so the caller has to know how far the dissolve may run. There is no group
/// DETECTOR here and none is needed: a group is a content box that happens to be wide.</para>
///
/// <para><see cref="Keep"/> is the same idea in a stricter form: not what the frame is BUILT around but
/// what may never be lost, whatever else is. It holds faces only, from brow to chin. It does not hold
/// shoulders or chests, and it does not hold the hair: a face is recognised from the eyes and the
/// mouth, and the top of a head can be cropped without harm. It exists because a display can want to
/// zoom in past the frame it was given. A header seven times wider than it is tall, holding a square
/// photograph, is mostly empty at the zoom that keeps everybody's shoulders. The way to fill it is to
/// zoom until the FACES are at risk and stop there. <see cref="Content"/> is what a frame would like to
/// hold; Keep is what it must hold.</para>
///
/// <para><see cref="EyeLine"/> is where the eyes are, down the photograph, or zero when there are no
/// faces. A display that has zoomed in must choose which part of the frame to show, and the rule is to
/// put the eyes a little ABOVE the middle of the frame. Eyes exactly in the centre look like a passport
/// photograph. Eyes at the middle of a wide band leave the picture mostly forehead.</para>
/// </summary>
public readonly record struct CropFrame(CropRect Rect, CropRect Content, CropRect Keep, int EyeLine = 0);

/// <summary>A face, in the same pixels as the saliency map. Handed in by a face detector.</summary>
public readonly record struct FaceBox(int X, int Y, int Width, int Height)
{
    public double CentreY => Y + Height / 2.0;

    /// <summary>
    /// A face box as Apple's Vision framework reports it (normalised 0..1 with the origin at the
    /// <b>bottom</b>-left), in the pixels the crop works in, whose origin is the <b>top</b>-left.
    ///
    /// <para>
    /// This is one line of arithmetic, and it is the most important line in the iOS art path: if the
    /// flip is wrong, every face is found at its mirror image down the photo, which produces a crop
    /// that is wrong with full confidence. It lives here, with a test on it, rather than inline in a
    /// platform file that no test can reach.
    /// </para>
    /// </summary>
    public static FaceBox FromNormalizedBottomLeft(double x, double y, double width, double height,
                                                   int pixelWidth, int pixelHeight)
        => new(
            (int)Math.Round(x * pixelWidth),
            (int)Math.Round((1 - y - height) * pixelHeight),
            (int)Math.Round(width * pixelWidth),
            (int)Math.Round(height * pixelHeight));
}

/// <summary>
/// Picks the part of a photo worth showing when it has to be squeezed into a strip.
///
/// The measure of "worth showing" is a <b>saliency map</b> (one score per pixel of how much that part
/// of the picture is what the picture is <em>about</em>) which the caller passes in. In the app that
/// map comes from U²-Netp, a salient-object detector. <see cref="ClassicalSaliency"/> is the fallback
/// for when the model cannot be loaded.
///
/// The frame is then chosen by <b>framing the subject</b>, not by looking for the densest window:
///
/// * <b>Find the subject's box.</b> Threshold the map, take the bounding box of what is left. That is
///   what the picture is of.
/// * <b>Frame it on its height.</b> The window takes the subject's height plus some padding, and the
///   slot's shape supplies the width. Height is the scarce axis in a strip five times as wide as it
///   is tall: the subject's width hardly constrains anything, while its height decides whether the
///   result shows a person or only their nose. When the subject is taller than the strip can hold
///   (nearly every standing person), the band goes at the top of them, dropped by a fraction of
///   itself so it lands on the face rather than the hair. It loses the crown of the head and the
///   chin, which is the right thing to lose: a crop can take hair and it can take a chin, but it
///   cannot take eyes.
/// * <b>No subject, no zoom.</b> A texture, a landscape, a wall of stage lights: nothing there is
///   "the subject", so there is nothing to zoom to. Show the biggest window there is, over whatever
///   saliency there was. Zooming into the busiest patch of a rock face is the mistake a density
///   search makes.
///
/// There are two entry points, for two shapes of frame. <see cref="Choose"/> is described above: a
/// window of the slot's own shape, for a display where the picture continues past the frame.
/// <see cref="Fit"/> is for a frame with a bottom edge (the desktop header), where nothing outside the
/// window is shown at all, so the content sets the window's height and the slot only says how wide to
/// open it.
/// </summary>
public static class SaliencyCrop
{
    /// <summary>
    /// How far in the crop may zoom, as a fraction of the largest window of the slot's shape that fits
    /// inside the photo.
    ///
    /// NB it is measured against that window, NOT against the whole picture. A wide slot against a
    /// tallish photo means even the largest legal window is a small slice of the image (a 5:1 strip
    /// out of a square keeps a fifth of it), so a floor expressed as "keep a third of the photo"
    /// would reject every window.
    /// </summary>
    public const double MinScale = 0.45;

    /// <summary>Saliency this strong (as a fraction of the picture's peak) is the subject itself,
    /// rather than the soft halo a detector leaves around it.</summary>
    private const double SubjectThreshold = 0.5;

    /// <summary>Below this, the "subject" is a speck (a highlight, a bird, a bit of noise) and not
    /// something to build a frame around.</summary>
    private const double MinSubjectFraction = 0.005;

    /// <summary>A subject spread wider than this is the picture itself: a landscape, a texture, a wall
    /// of stage lights. There is nothing to frame, so the crop centres on the saliency and stays wide.
    /// The limit is high on purpose: a four-piece band standing shoulder to shoulder covers most of a
    /// photo and is still the subject.</summary>
    private const double MaxSubjectFraction = 0.85;

    /// <summary>Padding around the subject, as a fraction of its height. A frame that ends exactly at
    /// the hairline looks like a mistake, and so does a subject pressed against both edges.</summary>
    private const double Padding = 0.15;

    /// <summary>How much taller than the strip's band a faceless subject has to be before the picture is
    /// pillarboxed rather than banded. Past it, a band is a slice across a head rather than a view of
    /// a person.</summary>
    private const double SubjectPillarbox = 1.6;

    /// <summary>How far below a too-tall subject's top edge the band is dropped, as a fraction of the
    /// band. Small on purpose: with no face to go on, the safe error is upwards. Losing the top of
    /// somebody's hair costs nothing; a band that has slid down onto a chest has lost the person. This
    /// is only a guess, and a detected face overrides it entirely.</summary>
    private const double FaceDrop = 0.02;

    /// <summary>How much of the widest possible frame to use, leaving the rest as room to slide the
    /// frame sideways. Full width would leave the subject wherever the photographer put them.</summary>
    private const double HorizontalRoom = 0.85;

    /// <summary>Where the subject is put across the frame. It is right of centre on purpose: the left of
    /// this strip is under the album cover and the track's title, and a face behind them cannot be
    /// seen.</summary>
    private const double FocusAcross = 0.62;

    // A head is a known shape around a face box, so the frame around it is arithmetic, not a guess.

    /// <summary>Hair and crown above the face box, as a fraction of it.</summary>
    private const double HairAbove = 0.45;

    /// <summary>Neck and shoulders below the face box, as a fraction of it.</summary>
    private const double ShouldersBelow = 0.8;

    /// <summary>Space either side of a face, as a fraction of its width.</summary>
    private const double BesideFace = 0.6;

    /// <summary>Chin and jaw below the face box, as a fraction of it. This is the head alone, where
    /// <see cref="ShouldersBelow"/> is the head AND what it stands on.</summary>
    private const double ChinBelow = 0.15;

    /// <summary>The space a head keeps at its sides when nothing else can be afforded.</summary>
    private const double BesideHead = 0.12;

    /// <summary>How much above the face box may NOT be cut: the brow and no more. The hair above it can
    /// be cut (see the note on <see cref="CropFrame.Keep"/>), and cutting it lets a portrait fill a
    /// header instead of sitting in the middle of one.</summary>
    private const double BrowAbove = 0.10;

    /// <summary>The narrowest a pillarboxed picture may get, as an aspect. Below this it is a small
    /// stamp in the middle of a wide strip and the blurred fill is doing all the work, so the frame is
    /// cropped tighter instead, and a little more of the shoulders goes.</summary>
    private const double MinPillarAspect = 2.8;

    /// <summary>How far short of the slot's shape a frame may be and still count as filling it. Cropping
    /// a sliver off a nearly-right frame is better than putting a blurred border round it.</summary>
    private const double FillTolerance = 0.12;

    /// <summary>Where the eyes sit down a face box, brow to chin.</summary>
    private const double EyesInFace = 0.42;

    /// <summary>Where the eyes go in a band too short to hold a whole face: the upper third, leaving the
    /// chin room below and spending what is left on hair. Hair and chin can be cut; eyes cannot.
    ///
    /// <para>Public because a caller that cannot pillarbox has to place its own band, and placing it by
    /// a number of its own would be the same rule tuned twice. See <see cref="EyeLineOf"/>.</para>
    /// </summary>
    public const double EyesInBand = 0.38;

    /// <summary>
    /// Where the eyes are, down the picture, for a set of faces, in whatever pixels the boxes are in.
    ///
    /// <para>This is for a caller that holds a window that will not fill its slot and cannot
    /// letterbox it. Such a caller has to give up height, and this is the line it must not give up.
    /// The value is averaged across the faces, because a band of four heads at four heights has one
    /// eye line and it is the mean of them.</para>
    /// </summary>
    public static double EyeLineOf(IReadOnlyList<FaceBox> faces)
        => faces.Count == 0 ? 0 : faces.Average(f => f.Y + f.Height * EyesInFace);

    /// <summary>
    /// Chooses the window of a <paramref name="targetAspect"/> (width / height) to show.
    ///
    /// <paramref name="saliency"/> is one score per pixel, row-major, <paramref name="width"/> ×
    /// <paramref name="height"/>, on any positive scale. A downscaled map is enough: the caller
    /// scales the returned rect back up to the source's own pixels.
    /// </summary>
    public static CropChoice Choose(ReadOnlySpan<double> saliency, int width, int height, double targetAspect,
                                    IReadOnlyList<FaceBox>? faces = null, ArtScene? scene = null)
    {
        if (width <= 0 || height <= 0 || targetAspect <= 0 || saliency.Length < width * height)
            return new CropChoice(new CropRect(0, 0, Math.Max(width, 0), Math.Max(height, 0)), true);

        // Where the subject should sit across the frame: the centre of the real clear band when the
        // scene is known, and the default otherwise.
        var focusAcross = scene?.FocusAcross ?? FocusAcross;

        // And where it should sit DOWN the frame. Half is dead centre, which is what a strip barely
        // taller than a face wants. At the default this shift is exactly zero and the strip is
        // untouched. A tall frame with its controls along the bottom asks for a smaller number, and
        // the subject moves up by that fraction of the frame's height.
        var downShift = 0.5 - (scene?.FocusDown ?? 0.5);

        // The largest window of the slot's shape that fits inside the photo at all: the no-zoom frame.
        var (maxW, maxH) = FitInside(width, height, targetAspect);

        if (FindSubject(saliency, width, height) is not { } subject)
        {
            // Nothing to frame. Centre the widest window on whatever saliency there is, and do not
            // zoom: a photo with no subject has no detail worth enlarging.
            var (cx, cy) = Centroid(saliency, width, height);
            return new CropChoice(Place(cx, cy, maxW, maxH, width, height), true);
        }

        // The band a picture of the slot's own shape gets: the height that is actually on offer.
        var band = (int)Math.Round(Math.Clamp(maxW / targetAspect, maxH * MinScale, maxH));

        // ── faces ──
        //
        // A face box is not a hint, it is a measurement, and a head is a known shape around it: hair
        // above the brow, chin at the bottom of the box, shoulders below that. So the tightest honest
        // crop can be *derived* rather than guessed. Take the faces, grow them into heads and
        // shoulders by those proportions, and that is the smallest frame that shows everybody
        // properly.
        //
        // Zoom to it. A good crop is not "as wide as possible": a wide crop of a portrait is a strip
        // of wall with a face in it. If the resulting frame is too tall for the strip's shape, the
        // width runs out before the height does, and the picture is pillarboxed over a blurred copy
        // of itself rather than having its head cropped off to fill the box.
        if (faces is { Count: > 0 })
        {
            var faceTop = faces.Min(f => f.Y - f.Height * HairAbove);
            var faceBottom = faces.Max(f => f.Y + f.Height * (1 + ShouldersBelow));
            var faceLeft = faces.Min(f => f.X - f.Width * BesideFace);
            var faceRight = faces.Max(f => f.X + f.Width * (1 + BesideFace));

            var contentH = Math.Max(1, faceBottom - faceTop);
            var contentW = Math.Max(1, faceRight - faceLeft);

            // The slot's shape, taken by growing whichever side is short, never by cropping the other.
            var faceFrameH = Math.Max(contentH, contentW / targetAspect);
            var faceFrameW = faceFrameH * targetAspect;

            // Squeeze back inside the photo. If the width runs out first, the frame keeps its height and
            // stops being the slot's shape. That is the pillarbox case.
            if (faceFrameW > width)
            {
                // The width has run out. Keep only the height the content actually needs. Raising it
                // to the pillarbox floor here would put a blurred border around a frame that only
                // needed a sliver cropped off it.
                //
                // The height is also capped at that floor: past it the picture is a small stamp in the
                // middle of a wide strip with the fill doing all the work, so the shoulders and the
                // hair go instead (which is what the eye rule below is for).
                faceFrameW = width;
                faceFrameH = Math.Min(Math.Min(height, contentH), faceFrameW / MinPillarAspect);
            }
            if (faceFrameH > height)
            {
                faceFrameH = height;
                faceFrameW = Math.Min(width, faceFrameH * targetAspect);
            }

            // Close enough to the slot's shape counts as filling it: a frame a few percent short is
            // covered by cropping a sliver, and a sliver is better than a blurred border.
            var fillsStrip = faceFrameW >= faceFrameH * targetAspect * (1 - FillTolerance);

            // Across: the faces themselves, put right of centre when there is room. The left of this
            // strip is under the album cover and the title, and a face behind them cannot be seen.
            var faceX = (faceLeft + faceRight) / 2;
            var acrossAt = fillsStrip ? faceX + (0.5 - focusAcross) * faceFrameW : faceX;

            // Down: the eyes. Hair and chin can be cut, eyes cannot.
            var eyesAt = faces.Average(f => f.Y + f.Height * EyesInFace);
            var downAt = (faceFrameH >= contentH
                ? (faceTop + faceBottom) / 2
                : eyesAt - faceFrameH * EyesInBand + faceFrameH / 2)
                + Ride(downShift * faceFrameH, faceFrameH, contentH);

            return new CropChoice(
                Place(acrossAt, downAt, (int)Math.Round(faceFrameW), (int)Math.Round(faceFrameH), width, height),
                fillsStrip);
        }

        // No face, but a subject far taller than the strip's own band: a figure seen close, somebody
        // looking down, a back turned. Filling the strip here would give a band a fifth of the
        // photo's height across a head twice that tall, which is a giant cropped face. Pillarbox it
        // instead, exactly as a face that is too big would be, and let the blurred copy fill the
        // sides.
        if (subject.Height > band * SubjectPillarbox)
        {
            var tallH = Math.Min(height, maxW / MinPillarAspect);
            var tallW = Math.Min(width, tallH * targetAspect);

            var acrossSubject = subject.X + subject.Width / 2.0;
            var downSubject = subject.Y + tallH / 2 + tallH * FaceDrop
                              + Ride(downShift * tallH, tallH, subject.Height);

            return new CropChoice(
                Place(acrossSubject, downSubject, (int)Math.Round(tallW), (int)Math.Round(tallH), width, height),
                tallW >= tallH * targetAspect * (1 - FillTolerance));
        }

        // Frame the subject on its HEIGHT, and let the strip's shape supply the width.
        //
        // In a strip this wide, height is the scarce axis: the subject's width barely constrains
        // anything (5 heights of it fit across), while its height decides whether the result shows a
        // person or only their nose. If the frame were based on the width, a standing figure (narrow
        // and tall) would give a window a fifth as tall as it is wide, which is a slice of face.
        var frameH = subject.Height * (1 + 2 * Padding);
        var frameW = frameH * targetAspect;

        // Inside the photo, keeping the shape.
        if (frameW > width)
        {
            frameW = width;
            frameH = frameW / targetAspect;
        }
        if (frameH > height)
        {
            frameH = height;
            frameW = frameH * targetAspect;
        }

        // Leave some slack across, so the subject can be PLACED rather than merely included.
        //
        // A frame the full width of the photo has no horizontal freedom at all: the subject ends up
        // wherever the photographer put them. In this display the left of the strip is under the
        // cover art and the title, so a subject left of centre would be half hidden behind them.
        // Giving up a little width gives room to slide the frame until the subject is in the clear.
        frameW = Math.Min(frameW, maxW * HorizontalRoom);
        frameH = frameW / targetAspect;

        // Never zoom past the floor, and never zoom out past the largest window there is.
        var w = (int)Math.Round(Math.Clamp(frameW, maxW * MinScale, maxW));
        var h = (int)Math.Round(Math.Clamp(frameH, maxH * MinScale, maxH));

        // Where the subject should land across the frame: right of centre, clear of the cover and the
        // text that sit on the left of the strip.
        var focusX = faces is { Count: > 0 }
            ? faces.Average(f => f.X + f.Width / 2.0)
            : subject.X + subject.Width / 2.0;

        var subjectX = focusX + (0.5 - focusAcross) * w;

        // Vertically: when the subject is taller than the strip can hold (almost every standing
        // person, in a strip five times as wide as it is tall), the frame sits at the TOP of the
        // subject. With no face to go on, BandCentre guesses where a head sits on a body.
        var subjectY = BandCentre(subject, h) + Ride(downShift * h, h, subject.Height);

        return new CropChoice(Place(subjectX, subjectY, w, h, width, height), true);
    }

    /// <summary>
    /// Frames a photo for a slot that shows the <b>whole</b> subject: the tightest window that holds
    /// what the picture is of, entire, opened out to the slot's shape with whatever photograph there
    /// is around it.
    ///
    /// <para>This is a different job from <see cref="Choose"/>, not a variant of it. Choose cuts a
    /// window of the slot's shape and accepts that a standing figure loses their legs to it: the
    /// caller shows the top of the picture and lets the rest run on below the frame. Fit is for a
    /// frame with a bottom edge: a header of fixed height with the library under it, where anything
    /// the window does not hold is not shown at all. So the window's height is set by the content,
    /// never by the slot, and the zoom is whatever fits that content in the band: the largest it can
    /// be while still being all there.</para>
    ///
    /// <para>The content is the head-and-shoulders of the faces if there are any (a head is a known
    /// shape around a face box), the salient subject plus its padding if there are none, and the whole
    /// photograph when there is no subject to speak of. The window then grows, and never crops, to the
    /// slot's shape: extra picture above, below and beside the subject costs nothing, and it is better
    /// than the blurred fill that covers whatever the photograph cannot.</para>
    /// </summary>
    public static CropFrame Fit(ReadOnlySpan<double> saliency, int width, int height, double targetAspect,
                                IReadOnlyList<FaceBox>? faces = null, ArtScene? scene = null)
    {
        if (width <= 0 || height <= 0 || targetAspect <= 0 || saliency.Length < width * height)
        {
            var all = new CropRect(0, 0, Math.Max(width, 0), Math.Max(height, 0));
            return new CropFrame(all, all, all);
        }

        var focusAcross = scene?.FocusAcross ?? FocusAcross;
        var downShift = 0.5 - (scene?.FocusDown ?? 0.5);

        // ── what has to survive whole ──
        double top, bottom, left, right;
        double keepTop, keepBottom, keepLeft, keepRight;
        double? eyes = null;

        if (faces is { Count: > 0 })
        {
            top = faces.Min(f => f.Y - f.Height * HairAbove);
            bottom = faces.Max(f => f.Y + f.Height * (1 + ShouldersBelow));
            left = faces.Min(f => f.X - f.Width * BesideFace);
            right = faces.Max(f => f.X + f.Width * (1 + BesideFace));
            eyes = faces.Average(f => f.Y + f.Height * EyesInFace);

            // The heads alone: hair above, a chin's worth below, a little space either side. Everything
            // between this and the box above (the shoulders, the chest, the room beside them) is what
            // a display may give up to fill its own shape.
            keepTop = faces.Min(f => f.Y - f.Height * BrowAbove);
            keepBottom = faces.Max(f => f.Y + f.Height * (1 + ChinBelow));
            keepLeft = faces.Min(f => f.X - f.Width * BesideHead);
            keepRight = faces.Max(f => f.X + f.Width * (1 + BesideHead));
        }
        else if (FindSubject(saliency, width, height) is { } subject)
        {
            // Padding all round, measured on the subject's height on both axes: a frame that ends at
            // the hairline looks like a mistake whichever edge it is.
            var air = subject.Height * Padding;
            top = subject.Y - air;
            bottom = subject.Y + subject.Height + air;
            left = subject.X - air;
            right = subject.X + subject.Width + air;

            // No faces to be sure about, so the subject itself is the thing that may not be lost. Only
            // its padding can be given up.
            keepTop = subject.Y;
            keepBottom = subject.Y + subject.Height;
            keepLeft = subject.X;
            keepRight = subject.X + subject.Width;
        }
        else
        {
            // A texture, a landscape, a wall of stage lights: the picture is the subject, so the whole
            // picture is what has to be seen.
            top = 0;
            bottom = height;
            left = 0;
            right = width;

            // Nothing in particular to protect, so nothing stops a display zooming: a texture cropped
            // tighter is the same texture.
            keepTop = height * 0.5;
            keepBottom = height * 0.5;
            keepLeft = width * 0.5;
            keepRight = width * 0.5;
        }

        var contentH = Math.Max(1, bottom - top);
        var contentW = Math.Max(1, right - left);

        // ── the window: the content, opened out to the slot's shape ──
        //
        // Whichever axis is scarce sets the frame and the other is grown to match. The window is never
        // cropped back to the slot's shape, because that would crop through the subject, which is
        // what this method exists to avoid. Then the window is squeezed inside the photograph: if the
        // width runs out, the frame keeps only the height the content needs (the tightest zoom that
        // still holds it) and the slot's remaining width goes to the blurred fill, since there is no
        // more photograph to give it.
        var frameH = Math.Max(contentH, contentW / targetAspect);
        var frameW = frameH * targetAspect;

        if (frameW > width)
        {
            frameW = width;
            frameH = Math.Min(height, contentH);
        }
        if (frameH > height)
        {
            frameH = height;
            frameW = Math.Min(width, frameH * targetAspect);
        }

        // ── where the content sits in it ──
        //
        // Across: only a frame that reached the slot's shape has any slack to slide in (one clamped to
        // the photo's own width has none), so the focus is applied there and skipped here.
        var contentX = (left + right) / 2;
        var fillsSlot = frameW >= frameH * targetAspect * (1 - FillTolerance);
        var acrossAt = fillsSlot ? contentX + (0.5 - focusAcross) * frameW : contentX;

        // Down: the content, centred, moved up as far as the slack allows. The eyes rule only applies
        // when the frame could not hold the whole head (a photograph shorter than the head and
        // shoulders it contains). Then hair and chin go and the eyes stay.
        var downAt = (frameH >= contentH || eyes is not { } eyesAt
            ? (top + bottom) / 2
            : eyesAt - frameH * EyesInBand + frameH / 2)
            + Ride(downShift * frameH, frameH, contentH);

        var frame = Place(acrossAt, downAt, (int)Math.Round(frameW), (int)Math.Round(frameH), width, height);

        // The content, boxed in the photo's own pixels and clipped to the frame: what the caller must
        // not dissolve through. Rounded OUTWARDS: a face half a pixel outside the box is still a face.
        var cx = (int)Math.Floor(left);
        var cy = (int)Math.Floor(top);
        var content = Clip(new CropRect(cx, cy, (int)Math.Ceiling(right) - cx, (int)Math.Ceiling(bottom) - cy), frame);

        var kx = (int)Math.Floor(keepLeft);
        var ky = (int)Math.Floor(keepTop);
        var keep = Clip(new CropRect(kx, ky, (int)Math.Ceiling(keepRight) - kx, (int)Math.Ceiling(keepBottom) - ky), frame);

        return new CropFrame(frame, content, keep, (int)Math.Round(eyes ?? 0));
    }

    /// <summary>A box cut down to the frame that holds it, never empty.</summary>
    private static CropRect Clip(CropRect box, CropRect frame)
    {
        var x = Math.Clamp(box.X, frame.X, frame.X + frame.Width - 1);
        var y = Math.Clamp(box.Y, frame.Y, frame.Y + frame.Height - 1);
        var r = Math.Clamp(box.X + box.Width, x + 1, frame.X + frame.Width);
        var b = Math.Clamp(box.Y + box.Height, y + 1, frame.Y + frame.Height);
        return new CropRect(x, y, r - x, b - y);
    }

    /// <summary>
    /// How much of the scene's requested lift the frame can actually afford.
    ///
    /// <para>
    /// A scene asks in fractions of the frame ("put them a fifth of the way up, the words are along
    /// the bottom"), but what is on offer is decided by the SUBJECT. The frame can only move up by the
    /// slack between its own height and the head-and-shoulders inside it. If it moves further than
    /// that, the frame's top edge passes the crown and the top of somebody's head is cut off.
    /// </para>
    ///
    /// <para>
    /// So this only limits a WIDE frame, and the target aspect decides it. A tall frame takes its
    /// height from the content's width (a portrait window around a head is several times taller than
    /// the head), so the slack is large and the request is met in full. The phone's landscape stage is
    /// a little over two to one: the frame comes out at the head's own height, the slack is nothing,
    /// and the lift is refused rather than paid for with the crown. The desktop strip never asks for a
    /// lift (its FocusDown is dead centre, so the shift is zero).
    /// </para>
    /// </summary>
    /// <param name="wanted">The shift the scene asked for, in pixels of the photo.</param>
    /// <param name="frameHeight">The window being placed.</param>
    /// <param name="contentHeight">What has to stay inside it: the head and shoulders, or the subject.</param>
    private static double Ride(double wanted, double frameHeight, double contentHeight)
    {
        var slack = Math.Max(0, (frameHeight - contentHeight) / 2);
        return Math.Clamp(wanted, -slack, slack);
    }

    /// <summary>
    /// Where the band sits when there is no face to go on: a guess, and known to be one.
    ///
    /// A face detector has already been used by the time this runs, so a photo with a face in it never
    /// gets here. This handles a back turned, a face behind a fringe, or a model that did not load. The
    /// rule is that heads are at the top of people. A band level with the subject's top shows hair; one
    /// centred on the subject shows a chest; one dropped a fraction of its own height below the top
    /// lands about where a face would be.
    /// </summary>
    private static double BandCentre(CropRect subject, int band)
    {
        if (subject.Height <= band)
            return subject.Y + subject.Height / 2.0;

        return subject.Y + band / 2.0 + band * FaceDrop;
    }

    /// <summary>A w×h window centred on a point, pushed back inside the photo's edges.</summary>
    private static CropRect Place(double centreX, double centreY, int w, int h, int width, int height)
    {
        w = Math.Clamp(w, 1, width);
        h = Math.Clamp(h, 1, height);

        var x = (int)Math.Round(Math.Clamp(centreX - w / 2.0, 0, width - w));
        var y = (int)Math.Round(Math.Clamp(centreY - h / 2.0, 0, height - h));

        return new CropRect(x, y, w, h);
    }

    /// <summary>The subject's box: the strongly salient pixels, boxed. Null when there is no subject
    /// to speak of: nothing salient at all, a speck, or saliency spread over the whole frame.</summary>
    private static CropRect? FindSubject(ReadOnlySpan<double> map, int width, int height)
    {
        var peak = 0.0;
        foreach (var value in map[..(width * height)])
            if (value > peak)
                peak = value;

        if (peak <= 0)
            return null;

        var threshold = peak * SubjectThreshold;

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        var count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (map[y * width + x] < threshold)
                    continue;

                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
            return null;

        var area = (double)count / (width * height);
        if (area < MinSubjectFraction || area > MaxSubjectFraction)
            return null;   // a speck, or the whole picture: either way, nothing to frame

        return new CropRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The saliency-weighted centre of the picture: where to point a frame that has no
    /// subject to aim at. Falls back to the middle when the map is empty.</summary>
    private static (double X, double Y) Centroid(ReadOnlySpan<double> map, int width, int height)
    {
        var total = 0.0;
        var sumX = 0.0;
        var sumY = 0.0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = map[y * width + x];
                if (v <= 0)
                    continue;

                total += v;
                sumX += v * x;
                sumY += v * y;
            }
        }

        return total <= 0
            ? (width / 2.0, height / 2.0)
            : (sumX / total, sumY / total);
    }

    /// <summary>
    /// The fallback map, for when no model is available: local contrast (a Sobel gradient).
    ///
    /// It finds <em>detail</em>, which is a poor stand-in for <em>subject</em> (a brick wall scores
    /// as high as a face). It exists so the crop degrades to something reasonable instead of failing,
    /// not because it is any good. <paramref name="rgba"/> is row-major, 4 bytes per pixel.
    /// </summary>
    public static double[] ClassicalSaliency(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var map = new double[Math.Max(0, width * height)];
        if (width <= 2 || height <= 2 || rgba.Length < width * height * 4)
            return map;

        var luma = new double[width * height];
        for (var i = 0; i < luma.Length; i++)
            luma[i] = 0.299 * rgba[i * 4] + 0.587 * rgba[i * 4 + 1] + 0.114 * rgba[i * 4 + 2];

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;

                var gx = -luma[i - width - 1] - 2 * luma[i - 1] - luma[i + width - 1]
                         + luma[i - width + 1] + 2 * luma[i + 1] + luma[i + width + 1];
                var gy = -luma[i - width - 1] - 2 * luma[i - width] - luma[i - width + 1]
                         + luma[i + width - 1] + 2 * luma[i + width] + luma[i + width + 1];

                map[i] = Math.Sqrt(gx * gx + gy * gy);
            }
        }

        return map;
    }

    /// <summary>The largest w×h of the given aspect that fits inside width×height.</summary>
    private static (int W, int H) FitInside(int width, int height, double aspect)
    {
        var w = width;
        var h = (int)Math.Round(width / aspect);

        if (h > height)
        {
            h = height;
            w = (int)Math.Round(height * aspect);
        }

        return (Math.Clamp(w, 1, width), Math.Clamp(h, 1, height));
    }
}
