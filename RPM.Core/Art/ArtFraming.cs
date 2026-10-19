namespace RPM.Core.Art;

/// <summary>A window into the photograph in its own pixels, kept in doubles because it is on its way to
/// a placement that is drawn at sub-pixel scale. <see cref="CropRect"/> is the same box rounded off, for
/// the places that compare or cache one.</summary>
public readonly record struct FrameWindow(double X, double Y, double Width, double Height)
{
    /// <summary>The same window in whole pixels, kept inside <paramref name="sourceWidth"/> ×
    /// <paramref name="sourceHeight"/> after the rounding.</summary>
    public CropRect Round(int sourceWidth, int sourceHeight)
    {
        var w = Math.Clamp((int)Math.Round(Width), 1, Math.Max(1, sourceWidth));
        var h = Math.Clamp((int)Math.Round(Height), 1, Math.Max(1, sourceHeight));
        var x = Math.Clamp((int)Math.Round(X), 0, Math.Max(0, sourceWidth - w));
        var y = Math.Clamp((int)Math.Round(Y), 0, Math.Max(0, sourceHeight - h));
        return new CropRect(x, y, w, h);
    }
}

/// <summary>
/// A framing chosen by hand: which part of one photograph the header shows, how close in, and how much
/// of the header it covers.
///
/// <para>The automatic path (<see cref="SaliencyCrop"/> and the placement built on it) answers the same
/// question by looking at the picture: where the subject is, where the faces are, what the cover and
/// the record are covering. This is the answer when somebody has looked at the result and disagreed.
/// It does not compete with the chooser and it does not replace it. A photograph either has one of
/// these saved against it or it does not, and the pictures that do not are framed by the chooser.</para>
///
/// <para><b>It must be able to say exactly what the chooser said.</b> The shape of this type is built
/// for that. The placement window opens on whatever is already in force and shows a model of the
/// header. If the model cannot express the chooser's own answer, the picture JUMPS the moment the
/// window opens (a subject that was whole is suddenly cropped), and every adjustment after that is
/// made against something that is not what is on screen. A window that could only ever be the header's
/// exact shape could not do it: the chooser's window is routinely a little taller than that shape, and
/// a little narrower than the room. Hence three numbers about the window and a fourth about the room,
/// below.</para>
///
/// <para><b>Written down in the photograph's units, because the header has no fixed size.</b> It is a
/// different width on every monitor and a different height again while the drawer is opening, so a
/// framing saved as "these pixels of the strip" would only hold at the size it was made at.
/// <see cref="CentreX"/>/<see cref="CentreY"/> are where the window's middle sits across and down the
/// picture, and <see cref="Height"/> is how tall the window is against the picture's own height. Those
/// three stay true at any size. <see cref="Cover"/> is about the HEADER rather than the picture, on
/// purpose: see its own note.</para>
/// </summary>
/// <param name="CentreX">Where the window's middle sits across the photograph: 0 the left edge, 1 the
/// right. Clamped on use so the window can never hang off the picture.</param>
/// <param name="CentreY">The same down the photograph: 0 the top, 1 the bottom.</param>
/// <param name="Height">The window's height over the photograph's. This IS the magnification, since
/// the window is always stood full-height in the band. 1 is the whole picture top to bottom; smaller is
/// closer in.</param>
/// <param name="Cover">
/// How much of the picture's room in the header the drawn photograph covers, 0..1. One is edge to edge,
/// from the header's left to where the record's clear area begins. Less than one and the picture stops
/// short of the record: it is right-aligned against it, with the blurred copy of itself filling the
/// rest. The chooser does this whenever centring a face costs it width.
///
/// <para>Stated about the HEADER, not the photograph, because that is the right invariant: "this
/// picture covers the whole header" should stay true on a wider monitor, where the same number of
/// source pixels would cover less of it. It makes the window's width follow the header instead of
/// being pinned to the shape it was made at.</para>
/// </param>
public readonly record struct ArtFraming(double CentreX, double CentreY, double Height, double Cover)
{
    /// <summary>How far in the framing may be zoomed: a twentieth of the picture's height. Past that a
    /// header is a magnified detail of somebody's cheek, and the source has no pixels left to give it.
    /// It is a floor on the STORED number. What a given photograph can actually reach is
    /// <see cref="MinHeightFor"/>, which also refuses to blow a small picture up too far.</summary>
    public const double MinHeight = 0.05;

    /// <summary>The least of the header a hand-placed picture may cover. Below this the header is mostly
    /// the blurred copy with a small stamp of the photograph beside the record, which removes the
    /// artwork rather than placing it.</summary>
    public const double MinCover = 0.30;

    /// <summary>Pixels of the photograph that the window width must never fall below. This is roughly
    /// a quarter of a wide header. It is soft, but a backdrop is feathered and sits under a scrim, so
    /// it forgives a great deal (the same argument as the crop's own upscale ceiling).</summary>
    public const double MinSourcePixels = 320;

    /// <summary>The whole photograph, covering the whole header: what a framing starts as when there is
    /// nothing better to start it at. The chooser's own answer is the usual seed (see
    /// <see cref="From"/>); this is the fallback for a picture that never went through it.</summary>
    public static readonly ArtFraming Whole = new(0.5, 0.5, 1.0, 1.0);

    /// <summary>
    /// Reads an existing window back as a framing: how the header is already showing this photograph,
    /// so a person opening the window to adjust it starts from what they can see rather than from the
    /// middle of the picture. <paramref name="aspect"/> is the room's width over the band, the same
    /// number <see cref="Window"/> is given, and it is what turns the window's width into
    /// <see cref="Cover"/>.
    /// </summary>
    public static ArtFraming From(double x, double y, double width, double height,
                                  int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0 || aspect <= 0)
            return Whole;

        return new ArtFraming(
            (x + (width / 2)) / sourceWidth,
            (y + (height / 2)) / sourceHeight,
            height / sourceHeight,
            // The drawn picture is the window scaled so its height is the band. Its width against the
            // room is therefore the window's width against (its height × the room's shape), and the
            // band cancels out. That is why this can be read from the window without knowing the size
            // of anything.
            Math.Clamp(width / (height * aspect), 0, 1));
    }

    /// <inheritdoc cref="From(double,double,double,double,int,int,double)"/>
    public static ArtFraming From(CropRect window, int sourceWidth, int sourceHeight, double aspect)
        => From(window.X, window.Y, window.Width, window.Height, sourceWidth, sourceHeight, aspect);

    /// <summary>
    /// The furthest out a FILLING picture can be taken: the window at which its width is exactly the
    /// photograph's, as a fraction of the photograph's height. Past it a covering picture would need to
    /// be wider than the photograph is, so the cover falls away instead.
    ///
    /// <para>This is not a ceiling on <see cref="Height"/> (that ceiling is the whole photograph, 1),
    /// and not a ceiling on anything the chooser produces. It is where the "out" end of the zoom
    /// slider goes.</para>
    /// </summary>
    public static double FillHeightFor(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0)
            return 1;
        return Math.Clamp(sourceWidth / (aspect * sourceHeight), 0.01, 1);
    }

    /// <summary>
    /// How far in this photograph may be zoomed. <see cref="MinHeight"/> unless that would draw fewer
    /// pixels across than <see cref="MinSourcePixels"/>. A picture 400 wide magnified twenty times is
    /// not a close crop, it is a smear, and the person should be stopped at the limit of what the file
    /// can give rather than left to find it.
    /// </summary>
    public static double MinHeightFor(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0)
            return MinHeight;

        // A filling window's width in source pixels is height × aspect, so a floor on the width is a
        // floor on the height. Never above 1: a photograph too small to satisfy it at all is shown
        // whole rather than refused.
        var floor = MinSourcePixels / (aspect * sourceHeight);
        return Math.Clamp(Math.Max(MinHeight, floor), 0, 1);
    }

    /// <summary>
    /// How far OUT the zoom can usefully go: the point at which the picture, taken as wide as it will
    /// go, has shrunk to <see cref="MinCover"/> of the header. Past there it is a small stamp beside
    /// the record with the blurred copy filling everything else, which does not place the artwork.
    ///
    /// <para>The limit depends on the photograph, like every other limit here. A wide panorama never
    /// reaches it (the whole picture is the limit); a tall portrait reaches it early. It is where the
    /// "out" end of a zoom slider belongs, so that the end of the track is a real limit rather than a
    /// stretch of travel that does nothing.</para>
    /// </summary>
    public static double WidestZoomFor(int sourceWidth, int sourceHeight, double aspect)
        => Math.Clamp(FillHeightFor(sourceWidth, sourceHeight, aspect) / MinCover,
                      MinHeightFor(sourceWidth, sourceHeight, aspect), 1);

    /// <summary>
    /// The range <see cref="CentreX"/> can actually reach at this zoom: from the window standing
    /// against the photograph's left edge to standing against its right. Outside it the window would
    /// hang off the picture and <see cref="Window"/> pulls it back, so a slider laid over 0..1 would
    /// waste both of its ends.
    ///
    /// <para>It collapses to a single point (<c>Min == Max</c>) for a window as wide as the
    /// photograph, which has nowhere to go and should say so rather than being pulled at.</para>
    /// </summary>
    public (double Min, double Max) AcrossRange(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0)
            return (0.5, 0.5);

        var half = Window(sourceWidth, sourceHeight, aspect).Width / (2.0 * sourceWidth);
        return half >= 0.5 ? (0.5, 0.5) : (half, 1 - half);
    }

    /// <inheritdoc cref="AcrossRange"/>
    public (double Min, double Max) DownRange(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceHeight <= 0)
            return (0.5, 0.5);

        var half = Window(sourceWidth, sourceHeight, aspect).Height / (2.0 * sourceHeight);
        return half >= 0.5 ? (0.5, 0.5) : (half, 1 - half);
    }

    /// <summary>The most of the header this photograph can cover at this zoom: one, unless the picture
    /// runs out of width first.</summary>
    public double MaxCoverFor(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0)
            return 1;

        var h = Math.Clamp(Height, MinHeightFor(sourceWidth, sourceHeight, aspect), 1) * sourceHeight;
        return Math.Clamp(sourceWidth / (h * aspect), 0, 1);
    }

    /// <summary>
    /// The window this framing asks for, in the photograph's pixels. <paramref name="aspect"/> is the
    /// picture's room in the header over the band it stands in.
    ///
    /// <para>Every clamp lives here and nowhere else. That lets the dialog, the placement and the
    /// store all use raw numbers: whatever they hold, the window that comes out is inside the
    /// photograph, no smaller than the file can stand, and no wider than the header can draw.</para>
    /// </summary>
    public FrameWindow Window(int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0)
            return new FrameWindow(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

        var floor = MinHeightFor(sourceWidth, sourceHeight, aspect);
        var fraction = double.IsFinite(Height) ? Math.Clamp(Height, floor, 1) : 1;
        var h = fraction * sourceHeight;

        // A covering window is height × the room's shape. It is never wider than the photograph; in
        // that case the cover falls away on its own and the picture stops short of the record.
        var full = Math.Min(sourceWidth, h * aspect);
        var cover = double.IsFinite(Cover) ? Math.Clamp(Cover, MinCover, 1) : 1;
        var w = Math.Clamp(cover * h * aspect, Math.Min(full, MinSourcePixels), full);

        var cx = double.IsFinite(CentreX) ? CentreX : 0.5;
        var cy = double.IsFinite(CentreY) ? CentreY : 0.5;

        var x = Math.Clamp((cx * sourceWidth) - (w / 2), 0, Math.Max(0, sourceWidth - w));
        var y = Math.Clamp((cy * sourceHeight) - (h / 2), 0, Math.Max(0, sourceHeight - h));

        return new FrameWindow(x, y, w, h);
    }

    /// <summary>
    /// This framing as it will actually be used: the numbers put back through <see cref="Window"/>'s
    /// clamps. The window to draw comes from <see cref="Window"/>. This method is for the dialog,
    /// whose sliders and figures have to report what the picture is really doing rather than what was
    /// last asked of it: if the photograph is dragged off its own edge, the reading has to come back.
    /// </summary>
    public ArtFraming Settle(int sourceWidth, int sourceHeight, double aspect)
        => From(Window(sourceWidth, sourceHeight, aspect), sourceWidth, sourceHeight, aspect);

    private static ArtFraming From(FrameWindow window, int sourceWidth, int sourceHeight, double aspect)
        => From(window.X, window.Y, window.Width, window.Height, sourceWidth, sourceHeight, aspect);

    /// <summary>The same framing moved by <paramref name="acrossFraction"/> / <paramref name="downFraction"/>
    /// of the photograph. Unclamped on purpose: <see cref="Window"/> is where a framing meets the edges
    /// of the picture, and clamping here as well would let a drag that ran off one side lose its place.</summary>
    public ArtFraming Moved(double acrossFraction, double downFraction)
        => this with { CentreX = CentreX + acrossFraction, CentreY = CentreY + downFraction };

    /// <summary>
    /// Zoomed by <paramref name="factor"/> (below 1 is closer in) about a point held still:
    /// <paramref name="anchorX"/>/<paramref name="anchorY"/> in the window's own 0..1, so the pixel under
    /// the mouse wheel stays under it. Pass 0.5, 0.5 to zoom about the middle.
    ///
    /// <para>How much of the header the picture covers is left alone: zooming is about the subject's
    /// size, not about how far the photograph reaches across. (It can still fall away at the far end,
    /// where the picture runs out of width. <see cref="Window"/> holds that line.)</para>
    /// </summary>
    public ArtFraming Zoomed(double factor, double anchorX, double anchorY,
                             int sourceWidth, int sourceHeight, double aspect)
    {
        if (!double.IsFinite(factor) || factor <= 0 || sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0)
            return this;

        var was = Window(sourceWidth, sourceHeight, aspect);

        // The photograph pixel under the anchor must be at the same place after the zoom as before
        // it, so the new window is built around that pixel rather than around the old centre.
        var ax = Math.Clamp(anchorX, 0, 1);
        var ay = Math.Clamp(anchorY, 0, 1);
        var heldX = was.X + (was.Width * ax);
        var heldY = was.Y + (was.Height * ay);

        var floor = MinHeightFor(sourceWidth, sourceHeight, aspect);
        var fraction = Math.Clamp((was.Height / sourceHeight) * factor, floor, 1);

        var next = this with { Height = fraction };
        var now = next.Window(sourceWidth, sourceHeight, aspect);

        var centreX = heldX + ((0.5 - ax) * now.Width);
        var centreY = heldY + ((0.5 - ay) * now.Height);

        return next with { CentreX = centreX / sourceWidth, CentreY = centreY / sourceHeight };
    }

    /// <summary>Whether two framings are the same to within a tiny tolerance: a tenth of a percent of
    /// the picture, which is under a pixel on anything the header will ever be handed. Used to tell an
    /// adjustment that changed nothing from one worth re-baking the artwork for.</summary>
    public bool Matches(ArtFraming other)
        => Math.Abs(CentreX - other.CentreX) < 1e-3
        && Math.Abs(CentreY - other.CentreY) < 1e-3
        && Math.Abs(Height - other.Height) < 1e-3
        && Math.Abs(Cover - other.Cover) < 1e-3;

    /// <summary>A short, stable stamp for cache keys: two framings that round the same here draw the
    /// same picture. Four figures is a tenth of a pixel on a 1000px photograph.</summary>
    public string Stamp() => $"{CentreX:F4},{CentreY:F4},{Height:F4},{Cover:F4}";
}
