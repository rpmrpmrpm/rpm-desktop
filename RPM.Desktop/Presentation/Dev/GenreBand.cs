using RPM.Desktop.Presentation.Main.Genres;

namespace RPM.Desktop.Presentation.Dev;

/// <summary>A window into one photograph, in whole pixels of that photograph. This is what the generator
/// crops.</summary>
public readonly record struct GenreWindow(double X, double Y, double Width, double Height)
{
    /// <summary>The same box, rounded to whole pixels. A crop is written in whole pixels.
    ///
    /// <para><see cref="X"/> may be NEGATIVE, and <see cref="Width"/> may be more than the picture is wide.
    /// Both mean the same thing said two ways: a window that the photograph does not fill on its left. What
    /// lies off the left is the genre's own deep colour, not part of the photograph. See
    /// <see cref="GenreBand.MaxGap"/>. Down and to the right there is no such slack, so those two values
    /// are kept inside the picture here.</para>
    /// </summary>
    public (int X, int Y, int Width, int Height) Round(int sourceWidth, int sourceHeight)
    {
        var w = Math.Max((int)Math.Round(Width), 1);
        var h = Math.Clamp((int)Math.Round(Height), 1, Math.Max(1, sourceHeight));
        return ((int)Math.Round(X),
                Math.Clamp((int)Math.Round(Y), 0, Math.Max(0, sourceHeight - h)),
                w, h);
    }

    /// <summary>How much of the window hangs off the left of the photograph, in its pixels. It is zero for
    /// a window that lies wholly on the picture.</summary>
    public double Gap => Math.Max(0, -X);
}

/// <summary>Which part of a genre's source photograph the head shows. It is given in the photograph's own
/// units.
///
/// <para><b>Three numbers, not the header's four.</b> The now-playing header's
/// <see cref="RPM.Core.Art.ArtFraming"/> has a fourth number. It is how far the picture reaches across
/// before the record's clear air begins. That header has a record hanging off its right, and a blurred copy
/// of the picture to give the rest of the width to. A genre's head has neither. It BLEEDS: the picture IS
/// the band, and there is no edge for a picture that stops short to stop against. So the window is always
/// exactly <see cref="GenreBand.Shape"/> wide, at every height. A fourth number here would always be one,
/// and it would need explaining at every reading.</para>
/// </summary>
/// <param name="Across">Where the window's middle sits across the photograph: 0 is the left edge, 1 is the
/// right.</param>
/// <param name="Down">The same, down the photograph: 0 is the top, 1 is the foot.</param>
/// <param name="Height">The window's height over the photograph's height. This IS the magnification,
/// because the window is always cut to the band's shape. <see cref="GenreBand.WidestFor"/> is the picture
/// filling the band. A smaller value is closer in. A larger value, up to
/// <see cref="GenreBand.FurthestFor"/>, shows the picture smaller, at the end of a coloured band.</param>
public readonly record struct GenreFrame(double Across, double Down, double Height)
{
    /// <summary>The same frame moved by a fraction of the photograph. The result is not clamped on purpose.
    /// The edges are applied only in <see cref="GenreBand.Window"/>. So a drag that has gone past a side
    /// keeps its overshoot. Coming back takes the same distance it took to go.</summary>
    public GenreFrame Moved(double across, double down)
        => this with { Across = Across + across, Down = Down + down };
}

/// <summary>The band's arithmetic. It says what a frame means in pixels of the photograph.
///
/// <para><b>This is the definition. <c>scripts/gen_genre_photos.py</c> is the copy.</b> The tool places the
/// picture and the generator cuts it. If the two disagree by even a rounding, the head shows something
/// other than what was placed. The generator cannot call this code. So <see cref="Window"/> is deliberately
/// short, with every clamp visible in it. <c>GenreBandTests</c> pins the numbers that the Python is held
/// to.</para>
/// </summary>
internal static class GenreBand
{
    /// <summary>The band's shape. It is the only shape this code works in. The cut is written at this
    /// shape, whatever width the head is drawn at afterwards. See
    /// <see cref="GenrePhotos.Shape"/>.</summary>
    public const double Shape = GenrePhotos.Shape;

    /// <summary>The fewest pixels of the photograph that the window may ever cover across. The header's
    /// framing uses the same figure, for the same reason. A band is soft-edged and sits under a colour, so
    /// it forgives a great deal. But below this figure it is no longer a crop of a photograph.</summary>
    public const double MinAcross = 320;

    /// <summary>How far the window may hang off the LEFT of the photograph, as a share of its own width.
    ///
    /// <para><b>Off the left, never off the right. The asymmetry is the point.</b> The left of this band is
    /// not photograph in the first place. The genre's colour is laid solid over it where the name stands,
    /// and it fades out somewhere past that (<c>ScrimReach</c>). So a picture that starts a little way in
    /// loses nothing that anybody can see. The gap is filled with the tone the ground already has there
    /// (<c>GenreWordmarks.Deep</c>), and the two meet at one value. The right of the band has no such
    /// cover. A gap there would be a flat block beside the photograph.</para>
    ///
    /// <para>So a subject that sits left of centre can simply be moved right. Half the window is generous.
    /// It is not a promise that half is invisible. What is actually covered depends on how wide the page
    /// is. The tool draws the colour over it at a page width that can be changed. That is the only honest
    /// way to judge it.</para>
    /// </summary>
    public const double MaxGap = 0.5;

    /// <summary>How far in a photograph may be taken. This is a twentieth of its height, unless that would
    /// draw fewer than <see cref="MinAcross"/> pixels across. It is never past <see cref="FurthestFor"/>. A
    /// picture too small to meet the floor is shown as wide as it can be, rather than refused.</summary>
    public const double Closest = 0.05;

    /// <summary>The window whose width is the whole picture. This is where a photograph exactly fills the
    /// band.
    ///
    /// <para>It is where the app already stands. A <c>focus</c> cut keeps the source's full width and
    /// chooses only how far down to take the slice. It is also what the zoom reads as one hundred per cent.
    /// So the figure means "fills the band", not an arbitrary scale.</para>
    ///
    /// <para>It is NOT the end of the zoom. See <see cref="FurthestFor"/>.</para>
    /// </summary>
    public static double WidestFor(int sourceWidth, int sourceHeight)
        => sourceWidth <= 0 || sourceHeight <= 0
            ? 1
            : Math.Clamp(sourceWidth / (Shape * sourceHeight), 0.01, 1);

    /// <summary>How far OUT the zoom goes. That is past the point where the picture fills the band, until
    /// the slack it leaves is the whole gap that the colour can carry.
    ///
    /// <para><b>Why there is anything past filling the band at all.</b> A window wider than the photograph
    /// is one that the photograph cannot fill. On most bands that would leave a picture floating with a
    /// hole beside it. Not on this one. All the slack goes LEFT, because the window is held against the
    /// picture's right edge. The left of this band is the genre's colour, with the name standing on it. So
    /// zooming out past one hundred per cent gives a picture that begins further along a coloured band.
    /// That is useful. A photograph whose subject is small in a wide frame reads better shown small at the
    /// end of the band than blown up to fill it.</para>
    ///
    /// <para>The end is where the slack reaches <see cref="MaxGap"/> of the window. That is the same limit
    /// a drag meets, reached from the other direction. It is never a window taller than the photograph,
    /// because nothing covers the top or the foot of the band.</para>
    /// </summary>
    public static double FurthestFor(int sourceWidth, int sourceHeight)
        => Math.Min(1, WidestFor(sourceWidth, sourceHeight) / (1 - MaxGap));

    /// <inheritdoc cref="Closest"/>
    public static double ClosestFor(int sourceWidth, int sourceHeight)
        => sourceHeight <= 0
            ? Closest
            : Math.Min(Math.Max(Closest, MinAcross / (Shape * sourceHeight)),
                       FurthestFor(sourceWidth, sourceHeight));

    /// <summary>The framing the app shows today, read from the one control it has.
    ///
    /// <para><c>focus</c> takes a slice of the source's FULL WIDTH. It says only how far down to take it:
    /// zero is off the top, and one is off the foot. That is a frame: the widest there is, centred across.
    /// So the tool opens on exactly what is shipped. The first thing on screen is the thing being changed,
    /// not a jump away from it.</para>
    ///
    /// <para>A source already wider than the band is cut down its middle instead, and keeps its whole
    /// height. <c>focus</c> cannot move that. <see cref="WidestFor"/> returns one for those sources, and
    /// the centre is the middle of the picture, which says the same thing.</para>
    /// </summary>
    public static GenreFrame FromFocus(double focus, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new GenreFrame(0.5, 0.5, 1);

        var height = WidestFor(sourceWidth, sourceHeight);
        var slice = height * sourceHeight;
        var top = (sourceHeight - slice) * Math.Clamp(double.IsFinite(focus) ? focus : 0.42, 0, 1);

        return Settled(new GenreFrame(0.5, (top + (slice / 2)) / sourceHeight, height),
                       sourceWidth, sourceHeight);
    }

    /// <summary>The window a frame asks for, in the photograph's pixels. It is always exactly
    /// <see cref="Shape"/> wide, at every height. It never reaches past the picture's right edge. It hangs
    /// off the left by at most <see cref="MaxGap"/> of its own width.
    ///
    /// <para>Every clamp in the whole tool is here. That lets the page hold raw numbers and drag them past
    /// the edges. Whatever the page holds, the window that comes out is one the generator can cut as it
    /// stands.</para>
    /// </summary>
    public static GenreWindow Window(GenreFrame frame, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new GenreWindow(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

        // A height that is not a number falls back to the picture FILLING the band. That is where the app
        // stands when there is no frame at all. Falling back to the photograph's whole height would be wrong,
        // because the zoom goes past the picture's width. A frame with a bad division in it would then open
        // at the far end of the zoom, with the band half colour.
        var height = Math.Clamp(
            double.IsFinite(frame.Height) ? frame.Height : WidestFor(sourceWidth, sourceHeight),
            ClosestFor(sourceWidth, sourceHeight),
            FurthestFor(sourceWidth, sourceHeight)) * sourceHeight;

        // The width may be more than the picture has. That is the far end of the zoom. What holds is the
        // RIGHT edge: the window never reaches past the photograph's right edge. So all the slack is on the
        // left, where the colour is. That is why the upper bound is not floored at zero.
        var width = height * Shape;
        var x = Math.Clamp(((double.IsFinite(frame.Across) ? frame.Across : 0.5) * sourceWidth)
                           - (width / 2),
                           -(MaxGap * width),
                           Math.Max(-(MaxGap * width), sourceWidth - width));
        var y = Math.Clamp(((double.IsFinite(frame.Down) ? frame.Down : 0.5) * sourceHeight)
                           - (height / 2), 0, Math.Max(0, sourceHeight - height));

        return new GenreWindow(x, y, width, height);
    }

    /// <summary>The frame as it will really be used. The numbers are put back through the clamps in
    /// <see cref="Window"/>. This is what the sliders read and what is written to the JSON. So a figure on
    /// screen is what the picture really does, not what was last asked of it.</summary>
    public static GenreFrame Settled(GenreFrame frame, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new GenreFrame(0.5, 0.5, 1);

        var window = Window(frame, sourceWidth, sourceHeight);
        return new GenreFrame(
            (window.X + (window.Width / 2)) / sourceWidth,
            (window.Y + (window.Height / 2)) / sourceHeight,
            window.Height / sourceHeight);
    }

    /// <summary>Zooms by <paramref name="factor"/> (below one is closer in), about a point that is held
    /// still. The anchor is <paramref name="anchorAcross"/> and <paramref name="anchorDown"/>, in the
    /// window's own 0 to 1. So the pixel under the wheel stays under the wheel. Half and half zooms about
    /// the middle.
    /// </summary>
    public static GenreFrame Zoomed(GenreFrame frame, double factor,
                                    double anchorAcross, double anchorDown,
                                    int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(factor) || factor <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return frame;

        var was = Window(frame, sourceWidth, sourceHeight);
        var ax = Math.Clamp(anchorAcross, 0, 1);
        var ay = Math.Clamp(anchorDown, 0, 1);

        // The photograph pixel under the anchor is the reason for zooming about one. It is where it is
        // before, and it has to be there after. So the new window is built around that pixel, not around
        // the old centre.
        var heldX = was.X + (was.Width * ax);
        var heldY = was.Y + (was.Height * ay);

        var next = frame with
        {
            Height = Math.Clamp((was.Height / sourceHeight) * factor,
                                ClosestFor(sourceWidth, sourceHeight),
                                FurthestFor(sourceWidth, sourceHeight)),
        };
        var now = Window(next, sourceWidth, sourceHeight);

        return Settled(next with
        {
            Across = (heldX + ((0.5 - ax) * now.Width)) / sourceWidth,
            Down = (heldY + ((0.5 - ay) * now.Height)) / sourceHeight,
        }, sourceWidth, sourceHeight);
    }

    /// <summary>Whether two frames are the same, to within a hair. That is a tenth of a percent of the
    /// picture, which is under a pixel on anything here. It tells an adjustment that changed nothing from
    /// one that is worth saving.</summary>
    public static bool Same(GenreFrame a, GenreFrame b)
        => Math.Abs(a.Across - b.Across) < 1e-3
        && Math.Abs(a.Down - b.Down) < 1e-3
        && Math.Abs(a.Height - b.Height) < 1e-3;
}
