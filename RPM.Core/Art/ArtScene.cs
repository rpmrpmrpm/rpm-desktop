namespace RPM.Core.Art;

/// <summary>
/// The obstruction geometry of the art strip: how wide the panel is, and (as fractions of the
/// backdrop's own slot) where the album cover and the title text over it stop covering the left, and
/// where the record starts covering the right. It is the "scene" that both the chooser and the crop
/// reason about, so neither has to guess where a face would be hidden.
///
/// <see cref="ClearLeftFrac"/> and <see cref="ClearRightFrac"/> bracket the <b>clear band</b>: the run
/// of the strip that is on show, between the cover on the left and the record on the right. A face
/// outside it is behind something opaque and is not seen; a face in the middle of it is the artist,
/// clearly visible. Everything the chooser (<see cref="BackdropRanker"/>) and the crop
/// (<see cref="SaliencyCrop"/>) need to know about the panel's shape is derived from those two
/// numbers. So when the player is resized or expanded, which moves the cover and the record relative
/// to the strip, the whole calculation moves with it instead of staying fixed to constants tuned for
/// one width.
/// </summary>
public readonly record struct ArtScene(
    double Width, double Height, double ClearLeftFrac, double ClearRightFrac)
{
    /// <summary>
    /// The panel constants used when no scene has been measured. The no-scene overloads (and the Core
    /// tests that pin them) use these: the face's clear band runs 0.15..0.92 across the photo, the
    /// ideal position is near 0.42..0.66, and the focus is a little right of centre.
    /// </summary>
    public static readonly ArtScene Default = new(0, 0, 0.15, 0.92);

    // ── the shape of the display, which is not always a strip ──
    //
    // Everything below defaults to the desktop strip's numbers. They are properties rather than
    // constants because the phone's now-playing screen is the same problem turned on its side: a
    // tall frame whose obstructions are the record down the RIGHT and the cover, title and transport
    // along the BOTTOM, wanting the face in the clear upper-left. It is the same geometry in the
    // opposite corner.

    /// <summary>How far in the left-hand obstruction reaches at its worst. On the strip that is the
    /// album cover and the title over it, which is why the floor is "at least centred". On a screen
    /// where nothing covers the left, it is the margin and no more.</summary>
    public double SafeLeftFloor { get; init; } = 0.50;

    /// <summary>How far in the right-hand obstruction (the record) reaches at its worst.</summary>
    public double SafeRightCap { get; init; } = 0.90;

    /// <summary>The bounds that <see cref="FocusAcross"/> is clamped into. The strip's bounds are
    /// deliberately tight and right of centre. A display obstructed on the other side needs the other
    /// half of the range, so the clamp must not be what decides which side is safe.</summary>
    public double FocusAcrossFloor { get; init; } = 0.50;

    /// <inheritdoc cref="FocusAcrossFloor"/>
    public double FocusAcrossCeiling { get; init; } = 0.78;

    /// <summary>
    /// The shape the artwork is actually framed to: the slot's width over the band's height. Zero when
    /// the caller has not measured one, and then the chooser falls back to its fixed idea of "a
    /// strip".
    ///
    /// <para>It matters to the CHOOSER more than to the crop, which is handed the number directly. Which
    /// photograph is the best one for this header depends on how wide the header is. At five to
    /// one a landscape shot covers it. At nine to one a landscape shot is only a small stamp, and the
    /// photo that wins is the one whose subject is a shallow band that it can be zoomed into. No
    /// ranking of photographs is right for both, so the shape has to travel with the question.</para>
    /// </summary>
    public double TargetAspect { get; init; }

    /// <summary>
    /// Where a subject should sit <em>down</em> the frame, 0 (top) .. 1 (bottom). It is half, dead
    /// centre, on the strip, which is barely taller than a face and has nothing sitting over it
    /// vertically. A tall frame with its controls along the bottom wants the subject high instead.
    /// </summary>
    public double FocusDown { get; init; } = 0.5;

    /// <summary>
    /// The now-playing screen on a phone: a tall frame with the record covering the right from
    /// <paramref name="clearRightFrac"/> across, and the cover, title and transport covering
    /// everything below <paramref name="clearBottomFrac"/> down. Both are fractions of the displayed
    /// picture, so they move with the screen instead of being constants tuned for one handset.
    ///
    /// <para>
    /// The result puts the subject in the clear upper-left. Nothing covers the left here (the floor
    /// is a margin, not a cover), so the safe zone runs from the edge to the record, and the focus
    /// clamp is opened up because the strip's right-of-centre range is the wrong half of the frame.
    /// </para>
    /// </summary>
    public static ArtScene ForPortrait(double width, double height, double clearRightFrac, double clearBottomFrac)
        => ForClearLeft(width, height, clearRightFrac, clearBottomFrac);

    /// <summary>
    /// A frame with nothing covering its left: the record covers the frame from
    /// <paramref name="clearRightFrac"/> across, the words and the keys cover everything below
    /// <paramref name="clearBottomFrac"/> down, and what is left (the upper-left) is where the
    /// subject goes.
    ///
    /// <para>
    /// Both phone players are this shape, which is why <see cref="ForPortrait"/> is only another name
    /// for it. It is <b>not</b> the desktop's strip: there the album cover sits hard against the left
    /// edge, so a face has to clear it (<see cref="FromPanel"/>, and the 0.50 floor on
    /// <see cref="SafeLeftFloor"/>). If a phone's landscape stage were measured that way, the left-hand
    /// column and the record would squeeze the safe zone into a slot a few percent wide right beside
    /// the record. That is exactly where a face must not be, because the record covers it.
    /// </para>
    /// </summary>
    public static ArtScene ForClearLeft(double width, double height, double clearRightFrac, double clearBottomFrac)
    {
        var right = Math.Clamp(clearRightFrac, 0.2, 1);
        var bottom = Math.Clamp(clearBottomFrac, 0.2, 1);

        return new ArtScene(width, height, 0, right)
        {
            // A face is not put against the very edge of the screen, because that looks like a
            // mistake. Nothing is covering the left, so this is the only reason for a floor.
            SafeLeftFloor = 0.04,
            SafeRightCap = right,

            FocusAcrossFloor = 0.05,
            FocusAcrossCeiling = 0.95,

            // A little above the middle of the clear band rather than at its centre: a face reads
            // better high in a frame, and it leaves room for the hair that the crop adds above the
            // face box.
            FocusDown = Math.Clamp(bottom * 0.45, 0.15, 0.5),
        };
    }

    /// <summary>
    /// Builds a scene from the panel's live pixels: its size, the left edge of the clear band (the
    /// cover's right edge plus its gap) and the right edge (the record's leftmost point), against the
    /// width of the backdrop's own slot (the space a filling backdrop actually occupies).
    /// </summary>
    public static ArtScene FromPanel(double width, double height, double clearLeftPx, double clearRightPx, double slotWidth)
    {
        var span = Math.Max(1, slotWidth);
        var left = Math.Clamp(clearLeftPx / span, 0, 1);
        var right = Math.Clamp(clearRightPx / span, 0, 1);
        if (right <= left)   // a panel too narrow to have any clear band at all: keep the edges ordered
            right = Math.Min(1, left + 0.01);
        return new ArtScene(width, height, left, right) { TargetAspect = span / Math.Max(1, height) };
    }

    // ── the face safe-zone: robust to the panel narrowing, and to the title text ──
    //
    // The clear band above is measured at the CURRENT width. But an artist's backdrop order is judged
    // once and reused at every size. A face can be "clear" on a wide strip and buried as soon as the
    // player is narrower: the cover and the record are fixed multiples of the height, so as the strip
    // narrows they cover a bigger fraction of it, and the title text runs well right of the cover's
    // edge over the left of the band. So the face's safe-zone is FLOORED on the left and CAPPED on
    // the right. A face has to sit clear of where the cover reaches on a narrow strip (which is also
    // clear of the text) and short of where the record reaches. The zone only tightens further when
    // the panel is actually narrow, because then the measured band is stricter still.

    // The strip's own floor, 0.50, is "at least centred". It clears the cover down to a strip about
    // 1000 px wide, and it clears the title text, which runs well right of the cover's own edge. A
    // filling crop of a wide photo into a narrow strip has almost no room to slide the face clear,
    // so the picture has to arrive with the face already in place, and the floor makes that so. See
    // <see cref="SafeLeftFloor"/> for the setting.

    /// <summary>The edges of the safe-zone: the measured clear band, but never more generous than a
    /// narrow strip would allow. A face at or past these is behind the cover/text or the record.</summary>
    public double FaceHardLeft => Math.Max(ClearLeftFrac, SafeLeftFloor);
    public double FaceHardRight => Math.Max(FaceHardLeft + 0.02, Math.Min(ClearRightFrac, SafeRightCap));

    private double SafeCentre => (FaceHardLeft + FaceHardRight) / 2;
    private double SafeHalf => Math.Max(0.01, (FaceHardRight - FaceHardLeft) / 2);

    /// <summary>Where a subject should be placed across the frame: the centre of the safe-zone, moved a
    /// little towards the record so the title and artist over the left of the band keep their
    /// clearance.</summary>
    public double FocusAcross => Math.Clamp(SafeCentre + SafeHalf * 0.15, FocusAcrossFloor, FocusAcrossCeiling);

    /// <summary>The best position for a face, either side of the safe-zone's centre. A face in here
    /// scores the full mark.</summary>
    public double FaceIdealLeft => SafeCentre - SafeHalf * 0.31;
    public double FaceIdealRight => SafeCentre + SafeHalf * 0.31;

    /// <summary>Whether a face at this position across the photo is hidden behind the cover/text or the
    /// record (now, or once the strip narrows). Used to disqualify a photo whose faces are mostly out of
    /// sight.</summary>
    public bool Obstructs(double across) => across <= FaceHardLeft || across >= FaceHardRight;

    /// <summary>How well a face's position across the photo suits the strip: 1 in the best zone,
    /// falling to 0 at the safe-zone's edges.</summary>
    public double FacePosition(double across)
    {
        if (across <= FaceHardLeft || across >= FaceHardRight)
            return 0;
        if (across < FaceIdealLeft)
            return (across - FaceHardLeft) / Math.Max(1e-6, FaceIdealLeft - FaceHardLeft);
        if (across > FaceIdealRight)
            return (FaceHardRight - across) / Math.Max(1e-6, FaceHardRight - FaceIdealRight);
        return 1;
    }
}
