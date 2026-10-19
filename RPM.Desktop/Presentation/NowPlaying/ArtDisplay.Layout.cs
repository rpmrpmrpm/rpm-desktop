using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using RPM.Core.Art;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;


namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// Where each layer is placed: the geometry of the wave lines, and the layout pass that sizes the cover,
/// the record and the strip to the height that the shell's drawer gives the band.
/// </summary>
public sealed partial class ArtDisplay
{
    // Waves

    /// <summary>Wavelength, amplitude (as a fraction of the display's height), and the seconds each line
    /// takes to travel one wavelength at full speed.</summary>
    private static readonly (double Wavelength, double Amplitude, double Seconds)[] WaveLayers =
    [
        (220, 0.30, 5.0),
        (150, 0.20, 3.4),
        (320, 0.40, 7.5),
    ];

    /// <summary>Rebuilds the three sine lines for the current size. They are drawn one wavelength wider
    /// than the display and slid left by at most that much, so the drift is seamless.</summary>
    private void BuildWaves(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;

        Microsoft.UI.Xaml.Shapes.Polyline[] lines = [Wave1, Wave2, Wave3];

        for (var i = 0; i < lines.Length; i++)
        {
            var (wavelength, amplitudeFraction, _) = WaveLayers[i];
            var amplitude = height * amplitudeFraction / 2;

            lines[i].Points.Clear();
            foreach (var point in SinePoints(width + wavelength, amplitude, amplitude, wavelength))
                lines[i].Points.Add(point);

            Canvas.SetLeft(lines[i], 0);
            Canvas.SetTop(lines[i], height / 2 - amplitude);
        }
    }

    /// <summary>A sine sampled into points, in the Canvas's own coordinates.</summary>
    private static IEnumerable<Windows.Foundation.Point> SinePoints(
        double width, double midY, double amplitude, double wavelength)
    {
        const double step = 6;

        for (double x = 0; x < width + step; x += step)
        {
            var at = Math.Min(x, width);
            yield return new Windows.Foundation.Point(at, midY + Math.Sin(at / wavelength * 2 * Math.PI) * amplitude);
        }
    }

    // Layout

    /// <summary>The record is this much taller than the display, and hangs half off the right
    /// edge.</summary>
    private const double RecordScale = 1.9;

    /// <summary>The cover square stops growing at this size. Past it, the strip gets taller but the cover
    /// stays the same size, so the record fills the extra height.</summary>
    private const double MaxCoverSide = 156;

    /// <summary>The gap kept between the foot of the rpm mark and the top of the now-playing cover. The art
    /// sits well under the logo, and does not touch it. The mark's position and height are read live from
    /// the element (RpmMark), so the gap still works if the mark is resized or moved.</summary>
    private const double RpmCoverGap = 16;

    /// <summary>The chrome's foot sits this far below the band. The margin of ContentLayer is (24,12). The
    /// cover keeps <see cref="CoverBandInset"/> clear inside the band, at the top and bottom.</summary>
    private const double CoverFootMargin = 12;
    private const double CoverBandInset = CoverFootMargin * 2;

    /// <summary>The spinning record stops growing at this diameter. It is the disc of a band 200 tall,
    /// times RecordScale. Past this, the band can still grow but the disc stays the same size, as the cover
    /// does. This stops a tall header from making the record fill the whole window.</summary>
    private const double MaxRecordDiameter = 380;

    /// <summary>The band height at which the record reaches <see cref="MaxRecordDiameter"/>. Everything in
    /// the header is at full size here. Past it, the band would only add empty room under a disc and a
    /// cover that have both stopped growing. So this is the header's maximum: the drawer opens to the
    /// height at which the art is whole, and no further (see <see cref="MaxHeaderHeight"/>).</summary>
    internal const double FullRecordBand = MaxRecordDiameter / RecordScale;

    /// <summary>How far short of the record's left edge the photo has finished dissolving in.</summary>
    private const double ScrimClearGap = 40;

    /// <summary>How far below the band's foot the veil takes to reach full strength. It is long on purpose.
    /// The change in brightness spreads across the middle of the window, and a short ramp would be easy to
    /// see as an edge.</summary>
    private const double VeilRampHeight = 220;

    /// <summary>How far the scrim's tail continues below the band's foot before it fades out. The scrim
    /// darkens the header so that the words are readable. If it stopped at the foot, it would draw a hard
    /// line across the full width of the window. So it fades out lower down, behind the library's
    /// cards.</summary>
    private const double ScrimTailDrop = 130;

    /// <summary>Anchors the photo's dissolve to the record rather than to the display's width.</summary>
    private void SyncGradients(double width, double height, double inset, double radius)
    {
        var recordLeft = width - radius;
        var endsAt = Math.Max(1, recordLeft - ScrimClearGap);

        var scrimEnd = Math.Clamp(endsAt / Math.Max(1, width - inset), 0.25, 1.0);
        ScrimStop1.Offset = scrimEnd * 0.35;
        ScrimStop2.Offset = scrimEnd * 0.72;
        ScrimStop3.Offset = scrimEnd;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _size = e.NewSize;
        Layout();
    }

    /// <summary>
    /// Lays the strip out for the current size and header height. The chrome (cover, title, the backdrop
    /// photo and the waves) lives in the top header band (<see cref="ChromeHeight"/>, or the whole control
    /// when that is unset). The record is sized to the band and centred in it. The clip is the whole
    /// window, not the band, so the lower arc of the disc is not cut at the band's foot. The disc spills
    /// into the content below it, and its colour comes from the album's dominant tone. That colour fills
    /// everything behind the translucent content cards.
    /// </summary>
    private void Layout()
    {
        var width = _size.Width;
        var height = _size.Height;
        if (width <= 0 || height <= 0)
            return;

        // The shell's chrome height is used down to the stopped park (about 82, which is 20 px under the
        // rpm mark). A minimum of 96 would lay the art out 14 px taller than the drawer row. The ground,
        // record and gradients would then show below the drag handle as a dark band. Only the stopped state
        // goes this low. A dragged header is limited to MinHeaderHeight(), and the art is hidden in the
        // stopped state.
        var band = _chromeHeight > 0 ? Math.Clamp(_chromeHeight, 1, height) : height;
        _band = band;

        // Clip to the whole window, not the band. The record may run past the band's foot.
        RootClip.Rect = new Windows.Foundation.Rect(0, 0, width, height);

        PlaceTagline();

        // The cover and chrome sit in the header band. The cover is anchored at the bottom, at
        // CoverFootMargin + band. So as the band grows, the top of the cover rises. The side is capped so
        // that the top never goes above the foot of the rpm mark. The art shrinks under the logo rather
        // than covering it.
        var side = Math.Min(MaxCoverSide, Math.Max(0, band - CoverBandInset));
        side = Math.Min(side, Math.Max(0, (CoverFootMargin + band) - RpmFoot));
        // The side is never smaller than its own text stack. Below that, the square stays the same size and
        // its top would overlap the mark. That is why the drag floor is MinHeaderHeight(), the band where
        // these two limits meet.
        side = Math.Max(side, CoverFloorSide);
        CoverHost.Width = side;
        CoverHost.Height = side;
        IdleCoverHost.Width = side;
        IdleCoverHost.Height = side;
        _clearLeft = 16 + side + 14;

        // The backdrop host is the artwork's own zone. It runs past the chrome's band to the top edge of
        // the library's first card (see PictureBand). The scrub bar's row and the card's margin are
        // full-width strips with nothing behind them. Height is what this picture needs. The zone stops at
        // the top of the first card. If it ran to the bottom of the window, the composition would sit
        // behind the grids, and a tall window would show only a slice of the picture.
        var picture = PictureBand;
        BackdropHost.Height = picture;
        IdleChrome.Height = band;
        ContentLayer.Height = band;

        // The record keeps its proportion of the band and is centred in the band. So it hangs half off the
        // right edge, and about 0.45 band past the band's foot. It is anchored at the top, not centred on
        // the control, so it stays in place in the header while the window grows below it. The diameter is
        // zero when the record is switched off (see ShowRecord). Every measurement below is taken from this
        // number, so a zero diameter closes the gap that the disc would otherwise leave in the picture.
        var record = RecordShown ? Math.Min(MaxRecordDiameter, Math.Max(1, band * RecordScale)) : 0;
        // The disc is centred in the band only until it reaches its cap. Past that, the band keeps growing,
        // but the record stays near the top instead of moving down with the band's centre. The cap is
        // reached exactly when band = MaxRecordDiameter / RecordScale.
        var recordCenterBand = Math.Min(band, MaxRecordDiameter / RecordScale);
        var recordTop = recordCenterBand / 2 - record / 2;
        var halo = record * RecordHaloScale;
        var spread = (halo - record) / 2;

        // The disc is drawn in the shell's top overlay, so it is placed there. The overlay has the same
        // bounds as the window. The same right-aligned, top-anchored margins place the disc in the band.
        // The disc is drawn over the grids, not behind them.
        if (_record is { } rec && RecordShown)
        {
            rec.Box.VerticalAlignment = VerticalAlignment.Top;
            rec.Box.Width = record;
            rec.Box.Height = record;
            rec.Box.Margin = new Thickness(0, recordTop, -record / 2, 0);

            rec.ShadowHalo.VerticalAlignment = VerticalAlignment.Top;
            rec.ShadowHalo.Width = halo;
            rec.ShadowHalo.Height = halo;
            // Move the halo down (see ShadowDropFraction), so that the shadow is heavier along the foot.
            var shadowDrop = record * ShadowDropFraction;
            rec.ShadowHalo.Margin = new Thickness(0, recordTop - spread + shadowDrop, -record / 2 - spread, 0);
        }

        _recordRadius = record / 2;

        // The orbit is in pixels and the disc has just changed size, so the wobble is applied again. It
        // costs nothing unless the compositor has the turn. See ApplyWobble.
        ApplyWobble();
        _recordCenterY = band / 2;


        // The photo ends where the arc of the record crosses the top and bottom of the band.
        var halfBand = band / 2;
        var inset = _recordRadius > halfBand ? Math.Sqrt(_recordRadius * _recordRadius - halfBand * halfBand) : 0;
        inset = Math.Min(inset, width * 0.8);
        _backdropInset = inset;
        // The whole header is also stored as one measurement. The window for placing a photograph by hand
        // reads it. It is taken here, after every number above has been decided. So the model in that
        // window is the real header, not a drawing of it (see HeaderShape).
        _shape = new HeaderShape(
            Width: width,
            Band: band,
            PictureHeight: PictureBand,
            SlotWidth: Math.Max(1, width - inset),
            Breath: PictureBreath,
            CoverSide: side,
            CoverLeft: ContentInsetX,
            CoverBottom: ContentInsetY + band,
            RecordDiameter: record,
            RecordTop: recordTop,
            TextLeft: ContentInsetX + side + ContentColumnGap,
            TextBottom: ContentInsetY + band - TrackTextFootMargin);

        // Clear both the right end of the seek track and the remaining-time text from the record's shadow,
        // by the same gap. Each is measured at its own height. The shadow curves differently at the seek
        // line than at the time line, which is higher. The values come from the live disc geometry, so the
        // gap holds as the disc scales with the band. A fixed offset would fit only one size. (A top margin
        // of 5 plus the stack's spacing of 2 gives a 7 px box gap. That puts the visual gap of the seek
        // knob on the same rhythm as the album and artist lines.)
        var shadowRadius = _recordRadius * RecordHaloScale;
        double ShadowLeftAt(double y)
        {
            var dy = y - _recordCenterY;
            return shadowRadius > Math.Abs(dy)
                ? width - Math.Sqrt(shadowRadius * shadowRadius - dy * dy)
                : width;                                        // The disc and its halo do not reach this row.
        }

        // The track's right edge is clamped to the shadow (minus the gap) at the line's height.
        var lineRightTarget = ShadowLeftAt(12 + band - SeekLineFromFoot) - RemainShadowGap;

        // Place the seek bar over the header. Its left edge is at the text column, its foot is on the
        // chrome foot (so the knob lines up with the album rhythm), and its right edge is at the clamp.
        var seekFoot = 12 + band;                               // The text and cover foot, in window coordinates.

        // There are two seek strips, with two shapes. Neither depends on the drawer state. That is why
        // there are two. Each is laid out here in the geometry of its own view, and it fades in and out
        // with that view (SyncSeekBars). No strip changes width on screen, so a bar never appears to grow
        // or jump across the top of the window.

        // OPEN: from the cover's right edge across to the record, with the countdown on the album's row.
        var openLeft = 24 + side + 14;                          // ContentLayer margin, plus cover, plus column spacing.
        // The shadow clamp applies only while a disc is casting a shadow. With the record off, there is
        // nothing on the right to avoid. A bar stopped 14 px short of the page margin would end on no line
        // at all. It would overshoot the card and the rail on its right edge by a few pixels, which looks
        // like a mistake. So the strip ends where the other edges of the page end: level with the painted
        // edge of the rail (see CardEdgeInset). With the record on, nothing here changes.
        var openRight = RecordShown
            ? Math.Max(openLeft, Math.Max(24.0, lineRightTarget))
            : Math.Max(openLeft, width - CardEdgeInset);
        if (_seek is not null)
        {
            _seek.HorizontalAlignment = HorizontalAlignment.Left;
            _seek.VerticalAlignment = VerticalAlignment.Top;
            _seek.Width = openRight - openLeft;
            _seek.Margin = new Thickness(openLeft, seekFoot - SeekStripHeight, 0, 0);
            // The countdown ends on the same line as the track under it, by the same rule.
            var timeRightTarget = RecordShown
                ? ShadowLeftAt(seekFoot - RemainCentreFromFoot) - RemainShadowGap
                : openRight;
            _seek.SetRemainInset(openRight - timeRightTarget);
            _seek.ShowRemaining = true;
        }

        // PARKED: a short column on the right, shared with the lines above it. There is no cover beside it.
        // The bar works as a ruler and not as a control. There is no countdown. The clock and the parked
        // lines want the same row, and the lines take it. This is fixed for this bar, not switched per
        // state (see ShowRemaining).
        var parkRight = Math.Max(24.0, width - ParkedRightMargin);
        var parkLeft = ParkedLeft(parkRight);
        parkRight = Math.Max(parkLeft, parkRight);
        if (_parkSeek is not null)
        {
            _parkSeek.HorizontalAlignment = HorizontalAlignment.Left;
            _parkSeek.VerticalAlignment = VerticalAlignment.Top;
            _parkSeek.Width = parkRight - parkLeft;
            _parkSeek.Margin = new Thickness(parkLeft, seekFoot - SeekStripHeight, 0, 0);
            _parkSeek.ShowRemaining = false;
        }

        // The parked lines use the same column as the parked bar, with the same left and right edges, so
        // the text and the bar end together. They fill the band above the bar and sit on its top edge. The
        // stack is aligned to the bottom. So a track with no album keeps the other two lines next to the
        // bar, with no gap under them. The scrim reaches well past the strip's left edge. That is the axis
        // the fall-off runs along, and the scrim needs the room to fade to nothing without showing where it
        // stops. Down the other axis it is flat across the band, which is the area being darkened. Then it
        // keeps going past the foot of the band and fades. So the shade does not end on the content card's
        // top edge and draw a line along it. The tail sits under the card. There it darkens the card's
        // glass (and the frost seen through it), so that it blends into the band, instead of starting fresh
        // beneath the card.
        var scrimWidth = Math.Min(width, (parkRight - parkLeft) + ParkedScrimReach);
        ParkedScrimHost.Width = scrimWidth;
        ParkedScrimHost.Height = seekFoot + ParkedScrimDrop;
        ParkedScrim.Width = scrimWidth;
        ParkedScrim.Height = seekFoot;
        ParkedScrimTail.Width = scrimWidth;
        ParkedScrimTail.Height = ParkedScrimDrop;
        ParkedScrimTail.Margin = new Thickness(0, seekFoot, 0, 0);
        ParkedScrimTail.Background ??= BuildTail((LinearGradientBrush)ParkedScrim.Fill, null);

        ParkedChrome.Width = Math.Max(1, parkRight - parkLeft);
        ParkedChrome.Height = Math.Max(1, seekFoot - SeekStripHeight - ParkedTextGap);
        ParkedChrome.Margin = new Thickness(parkLeft, 0, 0, 0);

        // The veil comes in below the artwork. It is clear where the picture is, and full by the time the
        // rows are. Its ramp starts at the card's top edge. If any of the ramp were under the picture,
        // every edge of the picture would show a step in brightness.
        var veilFrom = picture / height;
        var veilRamp = Math.Min(VeilRampHeight, Math.Max(1, height - picture)) / height;
        VeilClear.Offset = veilFrom;
        VeilLow.Offset = veilFrom + (veilRamp * 0.25);
        VeilHalf.Offset = veilFrom + (veilRamp * 0.5);
        VeilHigh.Offset = veilFrom + (veilRamp * 0.75);
        VeilFull.Offset = Math.Min(1, veilFrom + veilRamp);

        // The header dim is flat over the picture. It fades out over the veil's ramp, stop for stop, so the
        // two change over at the same rows.
        DimFlat.Offset = VeilClear.Offset;
        DimHigh.Offset = VeilLow.Offset;
        DimHalf.Offset = VeilHalf.Offset;
        DimLow.Offset = VeilHigh.Offset;
        DimClear.Offset = VeilFull.Offset;

        BackdropHost.Margin = new Thickness(0);
        // There is no clip. The composite is the slot. It is built for exactly this rectangle, transparent
        // outside its picture and dissolved at every edge, so at rest there is nothing to cut.
        //
        // A clip fixed to the band is the wrong tool during a resize. While the window is resized, some
        // frames show the tile before the new composite is ready. The tile is stretched with the window
        // (see PlaceLayer). A clip at the band's foot would cut the foot dissolve in the middle of its
        // ramp, which draws a straight line across the window for the whole drag. So during a resize the
        // picture is allowed to run over the library. It is behind the cards and under the veil, and the
        // next bake removes it.
        PlaceBackdrop();

        // The scrim has two parts: the band's flat ramp, and a tail that carries the shade below the foot
        // and then fades out. Both are as wide as the slot. The gradient stops are computed for that width
        // (see SyncGradients). The tail's brush is the same ramp multiplied by a vertical fade. It is baked
        // once per colour.
        var scrimWide = Math.Max(1, width - inset);
        ScrimHost.Width = scrimWide;
        ScrimHost.Height = picture + ScrimTailDrop;
        ScrimRect.Width = scrimWide;
        ScrimRect.Height = picture;
        ScrimTail.Width = scrimWide;
        ScrimTail.Height = ScrimTailDrop;
        ScrimTail.Margin = new Thickness(0, picture, 0, 0);
        ScrimTail.Background ??= BuildTail(ScrimRamp, null);

        SyncGradients(width, band, inset, _recordRadius);
        BuildWaves(width, band);
        ScheduleRecrop();

        // Check whether this pass moved the artwork's geometry. That is a narrower question than whether
        // the layout ran. A load waits for this geometry to stop changing before it bakes anything
        // (SettledGeometryAsync). Most layout passes do not change it. When the drawer parks or slides
        // open, the chrome's band and the disc move, but the picture is framed for the open band height all
        // the time. So a track that starts while the drawer is parked settles at once, and does not wait
        // for an animation that does not affect it.
        var geometry = LiveGeometry;
        if (_lastGeometry.Drifted(geometry, 0.001) || _lastGeometry.Band <= 0)
        {
            _lastGeometry = geometry;
            _sinceLayout.Restart();
        }

        GeometryChanged?.Invoke();
    }

    /// <summary>The artwork geometry as of the last layout pass, for spotting when it actually
    /// moves.</summary>
    private BakedGeometry _lastGeometry;

    /// <summary>Raised whenever the strip is laid out again (on resize). The shell attaches its menu button
    /// to the edge of the record, and moves it when the record grows.</summary>
    public event Action? GeometryChanged;

    // The position of the P's descender inside the rpm mark's path, as a fraction of the path's own
    // bounding box. The letters run from x 18 to 525 and from y -143 to 52 (see the Data in the XAML). The
    // descender is the stem at x 132. It reaches the foot of the box, so the bottom edge of the mark is the
    // bottom of the descender. Fractions are used, not pixels, so the tagline still hangs off the letter if
    // the mark is resized.
    private const double RpmDescenderLeftFraction = (132 - 18) / (525.0 - 18);

    /// <summary>How far under the descender's foot the tagline's line box starts. One pixel: the line is
    /// meant to hang off the letter, not to be spaced away from it.</summary>
    private const double TaglineDescenderGap = 1;

    /// <summary>Places the tagline so that it hangs off the P. The d of "re-discover" stands on the stem of
    /// the descender, and the top of the line is the foot of that descender. Both values come from the
    /// mark's live margin and size at its parked scale. The tagline is only shown when the mark is parked
    /// and zoomed (see <see cref="SyncTagline"/>). The darker head ("re-") starts to the LEFT of the stem.
    /// So the word begins before the letter it lines up with, and the d lands on the stem. The width of the
    /// head is measured, not counted, so the hyphen moves the line back by the right amount.</summary>
    private void PlaceTagline()
    {
        var markWidth = double.IsNaN(RpmMark.Width) ? RpmMark.ActualWidth : RpmMark.Width;
        var markHeight = double.IsNaN(RpmMark.Height) ? RpmMark.ActualHeight : RpmMark.Height;
        if (markWidth <= 0 || markHeight <= 0)
            return;

        var left = RpmMark.Margin.Left + RpmParkedScale * markWidth * RpmDescenderLeftFraction - HeadWidth();
        var top = RpmMark.Margin.Top + RpmParkedScale * markHeight + TaglineDescenderGap;
        ParkedTagline.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>How wide the tagline's darker head ("re-") is, measured in the tagline's own font. It is
    /// measured, not guessed, because the alignment is offset by this width. The width changes with the
    /// translation. A language that does not start with those letters has no head and no offset.</summary>
    private double HeadWidth()
    {
        if (TaglineHead.Text.Length == 0)
            return 0;

        var probe = new TextBlock
        {
            Text = TaglineHead.Text,
            FontSize = ParkedTagline.FontSize,
            FontFamily = ParkedTagline.FontFamily,
            FontWeight = ParkedTagline.FontWeight,
            FontStyle = ParkedTagline.FontStyle,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>How far the record reaches in from the RIGHT edge, at a given height down the strip. The
    /// disc is centred vertically and hangs off the right edge. Its leftmost reach is widest at the middle,
    /// and it narrows towards the top and the bottom. A control placed clear of the disc at height
    /// <paramref name="yFromTop"/> needs a right margin of at least this. Zero when there is no
    /// strip.</summary>
    public double RecordBulgeAt(double yFromTop)
    {
        if (_recordRadius <= 0)
            return 0;
        // Use the worst case over one revolution. The disc orbits because of the eccentric-pressing wobble,
        // so its edge reaches a wobble amplitude further left, and its centre a wobble higher, than in the
        // resting position. Callers that avoid the disc must clear where it reaches, not where it rests.
        var amp = _recordRadius * 2 * WobbleFraction;
        var radius = _recordRadius + amp;
        var dy = Math.Max(0, Math.Abs(yFromTop - _recordCenterY) - amp);
        var bulge = radius > dy ? Math.Sqrt(radius * radius - dy * dy) : 0;
        return Math.Min(bulge, _size.Width * 0.8);
    }

    /// <summary>
    /// The tail of the parked scrim: the band's own ramp, faded out down its rows, baked into a small
    /// bitmap.
    ///
    /// <para>The shade of the tail is the horizontal ramp multiplied by a vertical fade. No XAML brush can
    /// multiply two gradients. Stacking two translucent layers adds them instead. That would put a dark
    /// band across the left half of the window, where the horizontal ramp should be nothing. So the product
    /// is computed once here, into 256 x 64 pixels, which are then stretched to the size of the strip. A
    /// ramp stretches well.</para>
    ///
    /// <para>The ramp is read from the band's own brush, not written out a second time. There is one curve,
    /// declared in the XAML next to the rectangle it fills. The tail follows whatever that curve
    /// says.</para>
    /// </summary>
    private static ImageBrush BuildTail(LinearGradientBrush ramp, Windows.UI.Color? colour)
    {
        const int width = 256, height = 64;
        var stops = ramp.GradientStops.OrderBy(s => s.Offset).ToArray();

        // The direction of the ramp is read from the brush, not assumed. The two scrims in this file run in
        // opposite directions. The parked scrim is dark at the RIGHT, where its words are, in the
        // right-hand corner. The open scrim is dark at the LEFT, beside the cover. A tail baked the wrong
        // way round is not just wrong. It would be clear exactly where the band above it is darkest, so the
        // join it exists to hide would become the hardest edge in the window.
        var rightToLeft = ramp.StartPoint.X > ramp.EndPoint.X;

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            // A flipped smoothstep. It leaves the band's flat shade with no slope, and it reaches zero with
            // no slope too. So neither end of the tail has an edge that can be seen. A plain linear fade
            // would reach zero with its slope still on it, and the eye sees that as an edge (a Mach band).
            var t = (y + 0.5) / height;
            var fade = 1 - (t * t * (3 - 2 * t));

            for (var x = 0; x < width; x++)
            {
                var along = (x + 0.5) / width;
                var sample = SampleRamp(stops, rightToLeft ? 1 - along : along);
                // The shape of the ramp is its alpha values. The caller may override its colour. The open
                // scrim uses the album's colour, which changes from record to record. The stops keep the
                // shade that the ramp was drawn with.
                var tone = colour ?? sample;
                var alpha = sample.A / 255.0 * fade;

                // Premultiplied BGRA: what a WriteableBitmap's buffer holds.
                var i = (y * width + x) * 4;
                pixels[i] = (byte)Math.Round(tone.B * alpha);
                pixels[i + 1] = (byte)Math.Round(tone.G * alpha);
                pixels[i + 2] = (byte)Math.Round(tone.R * alpha);
                pixels[i + 3] = (byte)Math.Round(255 * alpha);
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();

        // Fill, not Uniform: this is a ramp being stretched to the strip's shape, and Uniform would
        // letterbox it against one edge.
        return new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Fill };
    }

    /// <summary>What a gradient's stops say at <paramref name="offset"/>, straight
    /// (un-premultiplied).</summary>
    private static Windows.UI.Color SampleRamp(GradientStop[] stops, double offset)
    {
        if (stops.Length == 0)
            return Windows.UI.Color.FromArgb(0, 0, 0, 0);
        if (offset <= stops[0].Offset)
            return stops[0].Color;

        for (var i = 1; i < stops.Length; i++)
        {
            if (offset > stops[i].Offset)
                continue;

            var (from, to) = (stops[i - 1], stops[i]);
            var span = to.Offset - from.Offset;
            var k = span <= 0 ? 1 : (offset - from.Offset) / span;
            static byte Mix(byte a, byte b, double k) => (byte)Math.Round(a + ((b - a) * k));
            return Windows.UI.Color.FromArgb(Mix(from.Color.A, to.Color.A, k),
                                             Mix(from.Color.R, to.Color.R, k),
                                             Mix(from.Color.G, to.Color.G, k),
                                             Mix(from.Color.B, to.Color.B, k));
        }

        return stops[^1].Color;
    }

    /// <summary>Like <see cref="RecordBulgeAt"/>, but it also clears the disc's shadow and halo, as well as
    /// its wobble. The soft edge reaches a little further left than the vinyl. A control that must not sit
    /// on the shading (the seek bar, or the window's caption buttons) should use this method, not
    /// <see cref="RecordBulgeAt"/>.</summary>
    public double RecordShadowBulgeAt(double yFromTop)
    {
        if (_recordRadius <= 0)
            return 0;
        var amp = _recordRadius * 2 * WobbleFraction;
        var radius = _recordRadius * RecordHaloScale + amp;   // The halo, plus the wobble's travel.
        var dy = Math.Max(0, Math.Abs(yFromTop - _recordCenterY) - amp);
        var bulge = radius > dy ? Math.Sqrt(radius * radius - dy * dy) : 0;
        return Math.Min(bulge, _size.Width * 0.85);
    }

    /// <summary>
    /// The row below which the disc, its halo and its wobble reach nothing at all. It is the same circle
    /// that <see cref="RecordShadowBulgeAt"/> measures, taken where the circle ends.
    ///
    /// <para>For a control that cannot move out of the disc's way sideways. The library rail is on the
    /// window's right edge, as the record is, so there is no room to its left. The only clear space is
    /// below.</para>
    /// </summary>
    public double RecordShadowBottom
    {
        get
        {
            if (_recordRadius <= 0)
                return 0;
            var amp = _recordRadius * 2 * WobbleFraction;
            return _recordCenterY + amp + (_recordRadius * RecordHaloScale) + amp;
        }
    }
}
