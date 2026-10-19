namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Which of Pegasus's views is on screen. One button cycles through them, in this order.
///
/// <para>They are variations of the same picture, not different pictures. Each step takes something away
/// and gives the space back to the room. Going down the list, nothing is ever added, so the button only
/// removes parts of the picture.</para>
/// </summary>
public enum PegasusView
{
    /// <summary>Everything: the record half out of its cover in the middle, the words down the left, and
    /// the spectrum running out from behind the disc to the edge of the screen.</summary>
    Spread,

    /// <summary>The spectrum and the dimmed area on the left are removed. The words move in under the
    /// cover, so the view shows the record and what is playing, but not the lyrics.</summary>
    Centred,

    /// <summary>The room alone: the figures drifting in the record's colours, and nothing else. There is no
    /// spectrum, cover, record or words. This is not a visualizer that shows the track. It shows the light
    /// the track is making, which is the purpose of this view.</summary>
    Room,

    /// <summary>
    /// The words as they are sung, scrolling against the room.
    ///
    /// <para>This is the only view that can be unavailable. It needs a synced lyric sheet for the record
    /// that is playing, and most records do not have one. Without a sheet this view is left out of the
    /// cycle, not shown empty. See <see cref="PegasusChrome.HasLyrics"/>.</para>
    /// </summary>
    Lyrics,

    /// <summary>
    /// The record and its words on the left, with the lyric as it is sung on the right. The lyric key opens
    /// this view from <see cref="Centred"/>: the record and the three lines under it move left, and the
    /// lyric fades in beside them. The seek bar and the keys stay where they are.
    ///
    /// <para>It needs a sheet, as <see cref="Lyrics"/> does, and is left out of the cycle on the same
    /// terms. From the other two views, the lyric key still opens <see cref="Lyrics"/>.</para>
    /// </summary>
    LyricsBeside,
}

/// <summary>The questions about a view that more than one place asks.</summary>
internal static class PegasusViews
{
    /// <summary>Whether the lyric is on screen in this view: alone, or beside the record.</summary>
    public static bool IsLyric(this PegasusView view) =>
        view is PegasusView.Lyrics or PegasusView.LyricsBeside;

    /// <summary>The lyric view that the lyric key opens from <paramref name="from"/>. From the record only
    /// view it is the lyric beside the record. From every other view it is the lyric alone.</summary>
    public static PegasusView LyricViewFor(PegasusView from) =>
        from == PegasusView.Centred ? PegasusView.LyricsBeside : PegasusView.Lyrics;
}

/// <summary>
/// Where Pegasus's pieces sit, as fractions of the screen rather than pixels.
///
/// <para>Shared because the composition is drawn by two parts that must match: the stage (<see
/// cref="PegasusStage"/>) paints the figures and the spectrum in GL, and the chrome (<see
/// cref="PegasusChrome"/>) lays the record, the cover and the words over it in XAML. The spectrum comes out
/// from behind the record, and the stage cannot see the record. So both parts calculate the geometry from
/// the same numbers. Two separate sets of numbers would drift apart at some window size that nobody
/// tested.</para>
/// </summary>
internal static class PegasusLayout
{
    /// <summary>The cover's side, as a fraction of the window's height. The height is used, not the width,
    /// so the cover is the same size on a wide screen as on a square one.</summary>
    public const double CoverFraction = 0.38;

    /// <summary>The cover's side is also limited to a minimum and a maximum, so it is never a small stamp
    /// on a short window or half of the picture on a narrow one.</summary>
    public const double CoverMin = 140;
    public const double CoverMaxWidthFraction = 0.34;

    /// <summary>How far right of the cover's centre the record's centre sits, as a fraction of the cover's
    /// side. The disc is the same size as the cover, so about a quarter of it shows past the cover's right
    /// edge. The rest is behind the cover.</summary>
    public const double DiscOffsetFraction = 0.312;

    /// <summary>The record's soft halo, as a multiple of the disc. There is a floor under this figure. The
    /// halo's dark inner band sits under the pressing's rim. If the halo is made too small, it covers the
    /// disc's edge, and the record looks one size smaller. The halo's radius is half of its scale, and the
    /// shadow throw (<see cref="DiscShadowThrowX"/>) moves it sideways. So a scale at or below 1 + 2 x
    /// throw would separate the shadow from the record it belongs to.</summary>
    public const double DiscHaloScale = 1.06;

    /// <summary>How far the record throws its shadow right and down, as a fraction of the disc. The light
    /// comes from the left and a little above, so the shadow falls mostly to the right. Down is the smaller
    /// of the two. A shadow that falls as far as it moves sideways looks like it comes from a light
    /// directly overhead, which is not the intended lighting.</summary>
    public const double DiscShadowThrowX = 0.025;
    public const double DiscShadowThrowY = 0.015;

    /// <summary>Where the spectrum's first column begins, measured out from the cover's right edge as a
    /// fraction of the cover's side. The start is inside the disc, so the bars are already drawn when they
    /// come out from behind the disc, and do not start at its edge.</summary>
    public const double SpectrumInset = 0.04;

    /// <summary>The words' column: its left inset and its width, as fractions of the window's
    /// width.</summary>
    public const double TextLeftFraction = 0.075;
    public const double TextWidthFraction = 0.30;


    /// <summary>
    /// The air between the record and the lyric column in <see cref="PegasusView.LyricsBeside"/>, as a
    /// fraction of the cover's side. It is measured from the edge of the record's shadow. On a 1440 pixel
    /// high screen this is about 66 points. The disc's own edge is about 30 points further in.
    ///
    /// <para>The gap is a fraction of the cover size, so it stays in proportion on any window size. The
    /// lyric column must not overlap the record.</para>
    /// </summary>
    public const double BesideGapFraction = 0.12;

    /// <summary>The minimum air on each side of the record and the lyric in <see
    /// cref="PegasusView.LyricsBeside"/>, as a fraction of the width. The two are centred as one group, so
    /// the air on each side is usually more than this minimum. A long lyric column gives up air down to
    /// this minimum (see PegasusChrome.Beside).</summary>
    public const double BesideLyricMargin = 0.05;

    /// <summary>The widest and the narrowest the lyric column is drawn beside the record. Wider than the
    /// first, a line of text becomes a paragraph. Narrower than the second, a short line does not
    /// fit.</summary>
    public const double BesideLyricMax = 1100;
    public const double BesideLyricLeast = 160;
}
