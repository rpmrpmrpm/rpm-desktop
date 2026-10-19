using System.Diagnostics;
using RPM.Core.Art;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The library's art display: the selected (or playing) track over its artist's backdrop, the album cover
/// square on the left, and a record turning on the right.
///
/// This control is the <b>stage</b>: it owns the layers and the primitives (decode, crop, blur, the
/// placement maths, the record and wave motion), but it decides nothing about <em>what</em> to show. That
/// is <see cref="ArtworkManager"/>'s job (the <see cref="IArtController"/> in <see cref="Controller"/>).
/// Every change request goes through it. It ranks them (a playing track wins), runs the fitting algorithms,
/// and drives this stage through <see cref="IArtStage"/> on a single, serialized, generation-guarded path.
/// There is no other way in.
///
/// Four rules shape the layers themselves:
///
/// * Never blank. Each slot (backdrop, cover) is two layers. A new image is decoded whole by the manager
///   and only crossfaded in by <see cref="ShowBackdrop"/>/<see cref="ShowCover"/> once it exists, so a slow
///   download, or one that fails, leaves the current art on screen instead of flashing empty.
/// * Never stale. A layer that is not the visible one is transparent <b>and empty</b>: when its fade-out
///   finishes its <c>Source</c> is nulled, so nothing older can ever be revealed behind the transparency.
///   The swap is synchronous and only the manager's current generation reaches it, so two swaps can never
///   interleave and land out of order.
/// * The record turns only when something is actually playing. At rest it is a still picture. A still
///   picture costs nothing, because an always-on animation redraws the whole window every frame.
/// * The photo stops where the record starts, and dissolves towards the left, so the waves and the text
///   keep a surface to be read on.
/// </summary>
public sealed partial class ArtDisplay : UserControl, IArtStage
{
    /// <summary>The crossfade, as asked for: two seconds, long enough to read as a dissolve.</summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(2);

    /// <summary>The timing of the stopped to playing transition. Every part that moves on that transition
    /// reads this one clock, so they do not drift apart: the shell's drawer (ShellPage), the record flight,
    /// the album wash, and the backdrop's fade in and out when it goes to or from no art. Only a switch
    /// from one picture to another keeps the slower <see cref="FadeDuration"/> photo dissolve.</summary>
    public const double TransitionSeconds = 0.42;

    /// <summary>The whole-window album wash and the backdrop fade to or from no art. They use the shared
    /// transition clock, so they move with the drawer rather than lagging behind it as a dark band across
    /// the grid.</summary>
    private static readonly TimeSpan WashDuration = TimeSpan.FromSeconds(TransitionSeconds);

    /// <summary>How long the old cut takes to leave from over the new one when the same photograph is
    /// re-cut for a new window, whether it is trimmed or re-framed. See the hand-over in
    /// <see cref="ShowBackdrop"/>. The wash in ShowWash rides the same clock.</summary>
    /// <remarks>
    /// It is short because nothing is being introduced. The new cut is already up, complete, underneath,
    /// and only the old one thinning away over it is left. A longer out-then-in sequence left more than a
    /// second in which the header showed neither picture properly. On a maximize, that is the window's new
    /// shape waiting to be filled.
    /// </remarks>
    private static readonly TimeSpan HandOverDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>How fast the album art is cleared at the top of a park, before the drawer slides up. It is
    /// kept short so the picture is gone by the time the band moves. The slower <see cref="FadeDuration"/>
    /// crossfade would otherwise stay on top of the bright rpm mark long after the drawer has
    /// parked.</summary>
    private static readonly TimeSpan ArtHideDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>Parked, the rpm mark zooms to this, as a decorative touch once the drawer has settled. It
    /// is driven as a RenderTransform scale about the mark's own centre, so it grows in place and pushes
    /// nothing down. The zoom starts only after the park slide finishes.</summary>
    private const double RpmParkedScale = 2.0;
    private static readonly TimeSpan RpmZoomDuration = TimeSpan.FromMilliseconds(320);
    private ScaleTransform? _rpmScale;

    /// <summary>
    /// Where the mark's top edge sits on Windows.
    ///
    /// <para>The XAML value of 44 is for macOS. It clears the traffic lights, which float over the top-left
    /// corner of the artwork and would otherwise sit on the letterforms. Windows has no such buttons there.
    /// Its caption buttons are at the top right (see ShellPage's WinCaptionButtons), so the mark moves up
    /// into the title bar row beside them.</para>
    ///
    /// <para>25 is the mark's own left inset. It sits the same distance from the top edge as from its side,
    /// so the corner it stands in is square. This is a little lower than the window buttons (11, which are
    /// 15 px tall and centred in the same row). The mark is a wordmark, not a control, so it sits in the
    /// corner rather than level with the buttons.</para>
    ///
    /// <para>This value is applied to the element's Margin in the constructor, not in XAML. The tagline
    /// under the mark, the cover's clearance and the parked band all read that margin live, so they follow
    /// it on their own.</para>
    /// </summary>
    private const double WinMarkTop = 25;

    /// <summary>The size the mark was last told to be. This is not what reading ScaleX from the transform
    /// gives, because a dependent animation is in flight for the length of the zoom, and the property keeps
    /// its old value throughout. This is the same Uno behaviour that the mark's opacity fader depends on.
    /// Anything that asks whether the mark is at parked size (the tagline) must ask this, not the
    /// transform.</summary>
    private double _rpmScaleTarget = RpmParkedScale;

    // The mark's Opacity by state (its brush is 0x8C, about 0.55). It is dim while a track's chrome is up.
    // Parked, it is raised, and the parked zoom takes it the last bit to full Opacity. So the parked mark
    // ends about 10% more solid, with the extra coming from the zoom.
    private const double RpmPlayOpacity = 0.46;       // 0.46 × 0.55 ≈ 0.25, the dimmed strength
    private const double RpmParkBaseOpacity = 0.91;   // 0.91 × 0.55 ≈ 0.50, the strength before the zoom

    /// <summary>One record turn: 1.8 s. This is the speed of a real 33 1/3 rpm LP (60 / (100 /
    /// 3)).</summary>
    private const double RevolutionSeconds = 1.8;

    /// <summary>From dead stop to full speed, for the record and the waves alike.</summary>
    private const double SpinUpSeconds = 1.0;

    /// <summary>The record's soft shadow is this much bigger than the disc (halo = record × this). Kept in
    /// step with the halo drawn in <see cref="Layout"/>.</summary>
    private const double RecordHaloScale = 1.08;

    /// <summary>The shadow is moved down by this fraction of the disc diameter. The halo is then thin along
    /// the top, where the light falls, and heavier along the bottom. A concentric halo would be a flat
    /// ring. The shift is purely vertical. The disc hangs half off the right edge, so a shadow thrown to
    /// the lower right would hide under the disc or off screen. A downward shift is the part that shows
    /// over the grids. The halo keeps the same size and is only shifted.</summary>
    private const double ShadowDropFraction = 0.03;

    /// <summary>A real pressing is never perfectly round or perfectly centred, so it wobbles once per
    /// revolution. The whole disc rides a small circular orbit of this fraction of the diameter. It is
    /// barely visible, but the gap to its shadow changes as it turns.</summary>
    private const double WobbleFraction = 0.005;

    /// <summary>Where the remaining-time readout's centre sits above the band foot. It sits inline with the
    /// album name, well up from the seek line, so the disc clearance is measured at this height.</summary>
    private const double RemainCentreFromFoot = 30;

    /// <summary>Where the seek track sits above the band foot. Its right end clears the shadow at this
    /// height, because the disc is a different width here than at the remaining-time row above.</summary>
    private const double SeekLineFromFoot = 7;

    /// <summary>Comfort gap the seek bar's right end and the remaining-time both keep to the left of the
    /// record's shadow edge.</summary>
    private const double RemainShadowGap = 14;

    /// <summary>The seek strip's height, and the slot it occupies under the album (strip + the 7px rhythm
    /// gap = the 21px bottom margin on <c>TrackText</c>). Kept in sync with that margin.</summary>
    private const double SeekStripHeight = 14;

    /// <summary>How far the parked lines sit above the seek bar's own furniture.</summary>
    private const double ParkedTextGap = 4;

    /// <summary>
    /// Where the parked strip starts: a fixed share of the header's width in from its right end.
    ///
    /// <para>It is not snapped to the album wall's card boundaries. The wall is one of three library views,
    /// and it changes its columns on every pane glide and resize. A strip that followed it would grow and
    /// shrink under the user for reasons unrelated to the player. A share of the header is a rule the strip
    /// can keep on its own, in every view, at every width.</para>
    ///
    /// <para>Between a floor and a ceiling (<see cref="ParkedColumnMin"/>, <see cref="ParkedColumnMax"/>):
    /// the share is what the strip wants, and the two bounds are what it may use.</para>
    /// </summary>
    private double ParkedLeft(double rightEdge)
    {
        var column = Math.Clamp(_size.Width * ParkedColumnFraction, ParkedColumnMin, ParkedColumnMax);
        return Math.Max(24, rightEdge - column);
    }

    /// <summary>
    /// How wide the parked strip is, as a fraction of the header's width. The lines and the bar under them
    /// share this one column.
    ///
    /// <para>Open, the bar runs from the cover across to the record, since there is a cover to start after.
    /// Parked, there is no cover, so the same rule would give the bar the whole window: a scrub line as
    /// wide as a monitor. That is a ruler, not a control, and it would not line up with the words above it.
    /// A short column, right-aligned under the text, reads as one object.</para>
    /// </summary>
    private const double ParkedColumnFraction = 0.24;

    /// <summary>The strip stops shrinking here: below it the title, the artist and the countdown have no
    /// room left to say anything, however narrow the window gets.</summary>
    private const double ParkedColumnMin = 260;

    /// <summary>And it stops growing here. A quarter of a wide window is a long way. On a big monitor the
    /// share alone pushed the bar's left end past the library's search field, clear of the header chrome
    /// that the strip shares its corner with. A scrub line that long is a ruler again. 369 lands it about
    /// level with the field on a full-screen window.</summary>
    private const double ParkedColumnMax = 369;

    /// <summary>Where the parked strip's right edge sits: the window's content margin, which is what every
    /// page in the shell is laid out to. Open, the bar stops short of the record's shadow instead. Parked,
    /// the disc has flown off to the corner, so there is nothing to dodge. Clamping to a disc that is not
    /// on screen would pull the strip in from the edge that everything else lines up with.</summary>
    private const double ParkedRightMargin = 24;

    /// <summary>Where the open strip's right end sits when there is no record to clamp it to: the page's
    /// content inset plus the two hairlines drawn inside it, the card's own 1 px border and the edge the
    /// right rail paints against it. Landing on the inset alone would leave the bar two pixels past the
    /// rail's painted edge. That is close enough to the line to look like a mistake rather than a margin.
    /// </summary>
    private const double CardEdgeInset = ContentInsetX + 2;

    /// <summary>How far left of the strip the darkening reaches. It is generous on purpose, because this is
    /// the axis the fall-off runs on. A ramp given too little room does not reach zero before it ends,
    /// which leaves a visible edge. That edge is the one thing the darkening must not have.</summary>
    private const double ParkedScrimReach = 260;

    /// <summary>How far below the band's foot the darkening keeps going before it lets go. It has to go
    /// further than the content card's top edge, so that the card's ground meets the band's shade.
    /// Otherwise the shade would end on the card's own line, and the fall-off exists to avoid drawing that
    /// line.</summary>
    private const double ParkedScrimDrop = 160;

    private const double SeekTextAllowance = 21;

    private SaliencyService? _saliency;
    private FaceService? _faces;

    /// <summary>Who decides what to show. Set once at wiring time; the flip/refresh buttons and the recrop
    /// timer forward to it, and it is the only thing that calls the Show* swaps.</summary>
    public IArtController? Controller { get; set; }

    /// <summary>Which layer of each pair is currently the visible one.</summary>
    private bool _backdropOnA, _coverOnA;

    private bool _idle = true;

    /// <summary>How far the record's arc reaches in from the right edge across the band: the backdrop's
    /// slot is everything left of it.</summary>
    private double _backdropInset;
    private double _clearLeft;

    /// <summary>The tallest the header may be: <see cref="FullRecordBand"/>, the band at which the record
    /// and the cover are both full size, and never taller than the window.
    ///
    /// <para>It does not depend on the artwork. The picture is framed to the header
    /// (<see cref="RPM.Core.Art.SaliencyCrop.Fit"/>), not the header to the picture. So this is a constant,
    /// and nothing has to be told when it changes.</para></summary>
    public double MaxHeaderHeight() => _size.Height > 0 ? Math.Min(_size.Height, FullRecordBand) : FullRecordBand;

    /// <summary>The rpm mark's foot plus the clear gap the cover must keep under it. It is read live from
    /// the element, so a moved or resized mark is still respected.</summary>
    private double RpmFoot
    {
        get
        {
            var h = double.IsNaN(RpmMark.Height) ? RpmMark.ActualHeight : RpmMark.Height;
            return RpmMark.Margin.Top + h + RpmCoverGap;
        }
    }

    /// <summary>The smallest the now-playing cover ever gets: its own title, artist and album stack plus
    /// the seek slot beneath it. A square shorter than its text looks broken.</summary>
    private double CoverFloorSide =>
        Math.Min(MaxCoverSide, (TrackText.ActualHeight > 0 ? TrackText.ActualHeight : 68) + SeekTextAllowance);

    /// <summary>The shortest header a drag may reach: the band at which the cover stops shrinking. Below
    /// it, the square holds its floor size while the band keeps rising. Its top then climbs over the rpm
    /// mark, which breaks the margin the header must keep. This is live, because the floor follows the text
    /// stack and the mark's own position.</summary>
    public double MinHeaderHeight() => CoverFloorSide + Math.Max(CoverBandInset, RpmFoot - CoverFootMargin);

    /// <summary>The header band's height: how far down the chrome (cover, title, waves, record) reaches.
    /// The crop frames its subject to this height. The picture runs on past it (its bottom is let out to
    /// the end of the source in <see cref="RenderCropAsync"/>), spilling behind the grids. Zero means use
    /// the whole control.</summary>
    private double _band;

    /// <summary>
    /// How far down the ARTWORK reaches, which is further than the chrome does: to the top edge of the
    /// library's first card.
    ///
    /// <para>The band is where the cover, the words and the record live. Below its foot there is the scrub
    /// bar's row and then the card's own margin: a strip the width of the window with nothing behind it.
    /// The picture runs down through that strip, and its foot dissolve lands on the card's edge, where the
    /// card's own frosted surface takes over. Nothing is lost behind the scrub bar either. The bar is only
    /// a line and a knob over whatever is beneath it.</para>
    /// </summary>
    private double PictureBand
    {
        get
        {
            // Parked, the band on screen is the stopped park, and the artwork is not shown in it at all. So
            // the picture is framed for the height it will be seen at, which is the drawer's open height
            // (OpenBandHeight). Framing for the park instead would make every picture for a strip it never
            // appears in, and each would have to be made again when the drawer opens. This includes the
            // first picture of any session that starts parked.
            var band = _idle && OpenBandHeight > 0 ? OpenBandHeight
                     : _band > 0 ? _band
                     : _size.Height;
            return Math.Max(1, Math.Min(_size.Height, band + LibraryCardGap));
        }
    }

    /// <summary>The drawer's open height, given by the shell. It is where the band sits when the drawer is
    /// not parked, so it is the height every picture is framed for. Zero until the shell sets it.</summary>
    public double OpenBandHeight { get; set; }

    /// <summary>The gap between the header band's foot and the top of the library's first card, the content
    /// page's own margin (MainPage's root grid, 24). The artwork is allowed all of it.</summary>
    private const double LibraryCardGap = 24;

    // The header's own measurements, for the window that places a photograph by hand.

    /// <summary>ContentLayer's margin (24,12) and the gap between the cover and the words (its
    /// ColumnSpacing, 14), named so the model of the header can be built from the same numbers the header
    /// is. Changing the XAML and not these would make the model quietly wrong, which is the one failure a
    /// preview must not have.</summary>
    internal const double ContentInsetX = 24;
    internal const double ContentInsetY = 12;
    internal const double ContentColumnGap = 14;

    /// <summary>TrackText's bottom margin: the slot the seek bar fills under the album line.</summary>
    internal const double TrackTextFootMargin = 21;

    /// <summary>
    /// The header as it was last laid out, in its own points: the band, the picture's zone, and where the
    /// cover square, the words and the record actually sit.
    ///
    /// <para>It exists for one reason. The window where somebody places an artist's photograph by hand
    /// shows a scale model of this header. A model built from fixed constants would drift away from the
    /// header as soon as one of them changed. So the values are measured in <see cref="Layout"/>, at the
    /// moment each number is decided, and read from here.</para>
    /// </summary>
    /// <param name="PictureHeight">How far down the artwork reaches, which is past the band, see
    /// <see cref="PictureBand"/>.</param>
    /// <param name="SlotWidth">The backdrop's slot: everything left of
    /// the record's reach.</param>
    /// <param name="Breath">Clear air kept between the photograph's right edge
    /// and the disc. The picture's own room is <see cref="SlotWidth"/> less this.</param>
    public readonly record struct HeaderShape(
        double Width, double Band, double PictureHeight, double SlotWidth, double Breath,
        double CoverSide, double CoverLeft, double CoverBottom,
        double RecordDiameter, double RecordTop,
        double TextLeft, double TextBottom)
    {
        /// <summary>Where the photograph stops, short of the record. Its left edge is the header's, so this
        /// is also how wide the picture is when it fills, which a hand-placed one always does.</summary>
        public double PictureRight => Math.Max(8, SlotWidth - Breath);

        /// <summary>The shape the picture is drawn into: its room over the ARTWORK's band, which runs past
        /// the chrome's foot to the top of the library's first card. That is the height the crop is framed
        /// to (see <see cref="PictureBand"/> and the load's own use of it), so it is the number a framing's
        /// window is cut to (<see cref="ArtFraming.Window"/>). Using the chrome band here instead would
        /// make the model in the placement window a slightly different shape from the header it is a model
        /// of, and the picture would land a little off what the preview promised.</summary>
        public double PictureAspect => PictureRight / Math.Max(1, PictureHeight);

        /// <summary>Whether this was measured at all. False before the first layout pass, and the window
        /// then falls back to a plausible header rather than drawing a model of nothing.</summary>
        public bool Measured => Width > 1 && Band > 1;
    }

    private HeaderShape _shape;

    /// <summary>The header as it stands. Pulled when somebody opens the placement window, never pushed: the
    /// header moves for a dozen reasons and none of them are that window's business.</summary>
    public HeaderShape Shape => _shape;

    /// <summary>The record's radius and vertical centre, cached for <see cref="RecordBulgeAt"/> and the
    /// shell's menu-button placement, the disc is centred in the band, not in the control.</summary>
    private double _recordRadius, _recordCenterY;

    /// <summary>The spinning disc. The shell hosts it above the content, so it overlaps the grids.
    /// ArtDisplay still drives it (size, position, rotation, art). It is null until the shell attaches it,
    /// so every use is guarded.</summary>
    private RecordDisc? _record;

    /// <summary>
    /// Whether the disc is on the header at all. Read on every layout pass and every motion check rather
    /// than acted on once, so the header measures itself against the record that is actually there.
    /// </summary>
    public bool RecordShown { get; private set; } = true;

    /// <summary>
    /// Whether the header stands on a photograph of the artist. The view only ever reads this to grey out
    /// the menu items that are about a photograph; whether one is fetched at all is the manager's question,
    /// and it asks the setting itself.
    /// </summary>
    public bool ArtistPhotoShown { get; private set; } = true;

    /// <summary>Put the artist's photograph behind the header, or take it off. The manager is told rather
    /// than the layer cleared here: what is on the header is its answer, and it has to re-decide the
    /// current track instead of being overwritten by the view.</summary>
    public void ShowArtistPhoto(bool shown)
    {
        if (ArtistPhotoShown == shown)
            return;

        ArtistPhotoShown = shown;
        RefreshNav();
        Controller?.NotifyArtistPhotosChanged();
    }

    /// <summary>
    /// Puts the record on the header, or takes it off.
    ///
    /// <para>Taking it off is not the same as hiding a control. The disc's arc is what the photograph's
    /// slot ends at, and what the seek bar's right end avoids. So the band is laid out again with no disc,
    /// and the picture runs to the edge. The per-frame tick stops with it. The tick exists to turn this
    /// disc, and turning a disc nobody can see only repaints the app for no picture.</para>
    ///
    /// <para><b>Whether the control is on screen is not decided here.</b> It must not be. The shell hosts
    /// the disc and has two other reasons of its own to take it away: the amp view, and any page that is
    /// not the library. Three writers to one Visibility means three answers, and the last one to run wins.
    /// That is how a disc that was switched off came back as soon as the library page loaded. The shell
    /// asks <see cref="RecordShown"/> and answers it once. See ShellPage.SyncRecordShown.</para>
    /// </summary>
    public void ShowRecord(bool shown)
    {
        if (RecordShown == shown)
            return;

        RecordShown = shown;
        Layout();
        SyncMotion();
    }

    /// <summary>The shell passes in the record it hosts above the content. This takes it and lays it out at
    /// the current size.</summary>
    public void AttachRecord(RecordDisc record)
    {
        _record = record;
        _deck = new RecordDisc.Turntable(record, onDeck: !_idle);

        // The artwork pass and the shell run on different clocks, so a face may already have been set
        // before there was a record to put it on. A face can go on only while the deck is empty, and it is
        // empty here.
        if (_face != RecordFace.None)
            _deck.Wear(_face);

        if (_size.Width > 0 && _size.Height > 0)
            Layout();
    }

    /// <summary>
    /// True when the listener parked the drawer themselves, rather than it parking because nothing was
    /// playing.
    ///
    /// <para>The two look the same and mean opposite things. The automatic park is the strip getting out of
    /// the way until there is something to show, and a track arriving is the cue to come back. The
    /// commanded park means the listener asked for it to stay like this. Only the same listener saying
    /// otherwise ends it.</para>
    /// </summary>
    private bool _heldParked;

    /// <summary>Park, and stay parked: a new track or a new album will not reopen the drawer. Only
    /// <see cref="UnparkByUser"/> ends the hold.</summary>
    public void ParkByUser()
    {
        _heldParked = true;
        EnterIdle();
    }

    /// <summary>Restores a "stay parked" that was saved by a previous run. It is applied before anything
    /// plays. It only sets the hold. The display starts idle anyway, so there is nothing to animate. The
    /// hold stops the first track of the new session from opening the drawer.</summary>
    public void RestoreHeldPark() => _heldParked = true;

    /// <summary>The listener asks for the drawer back. The hold is cleared first, so the reopen is not
    /// blocked by the hold it is lifting.</summary>
    public void UnparkByUser()
    {
        _heldParked = false;
        EnterTrack();
    }

    /// <summary>
    /// Whether the parked lines are up: only while the drawer is tucked away AND something is actually
    /// playing.
    ///
    /// <para>Both halves matter. Open, the full chrome shows all of this at proper size, and a second copy
    /// would be a duplicate. Stopped, there is nothing to say. The words would be the last track, and the
    /// strip would claim to be playing something it is not.</para>
    /// </summary>
    private void SyncParked()
    {
        // A LOADED track, not a sounding one. Pausing does not put the record away. The drawer is still
        // parked and there is still something on the deck to name. If this depended on _playing, the whole
        // strip would fall back to its open shape as soon as the listener paused, and the bar would grow to
        // the full window width. What ends it is playback STOPPING. The manager says so by clearing the
        // text (ArtworkManager calls SetText(null, null, null)).
        var show = _idle && !string.IsNullOrWhiteSpace(TrackTitle);

        // The artwork goes with the drawer too. Parked, the strip is a sliver of a photograph. It cannot be
        // read as one, and the parked lines have to sit on top of it. The app's own backdrop is a better
        // thing to see and to read words against. The whole artwork is one layer for this reason (see
        // ArtLayer), so nothing inside it has to know. Opening the drawer brings back exactly what was
        // there.
        Animate(ArtLayer, _idle ? 0 : 1);

        // The open bar goes out and the parked bar comes in. Neither one moves.
        SyncSeekBars(instant: false);

        // Nothing on screen turns while the drawer is shut, so nothing off screen turns either.
        SyncMotion();

        // The tagline is the other thing this band can show: parked and empty. It fades out here on its own
        // quick fade. It comes back through the same call (see SyncTagline).
        SyncTagline();

        var action = _parked.Want(show);
        if (action == ParkedFadeAction.Nothing)
            return;

        if (action == ParkedFadeAction.FadeOut)
        {
            // Leaving: the lines fade first, and the strip goes back to its open shape only once they have
            // gone. If the geometry changed as soon as the drawer opened, the expanded view's running time
            // would appear under three lines that were still fading out of the same spot. That is two
            // readouts in one place for the length of the fade. The strip stays parked until it is empty.
            // The fade is fast, because this is chrome, not artwork.
            Animate(ParkedScrimHost, 0, duration: ParkedFade);
            Animate(ParkedChrome, 0,
                onCompleted: () => { if (_parked.NoteFadedOut()) Layout(); }, duration: ParkedFade);
            return;
        }

        // Only when the lines are fully gone. If they are coming back mid-fade, the strip is still in its
        // parked geometry. Re-laying it out under a readout that nobody has stopped looking at would cause
        // a jump for no reason.
        if (action == ParkedFadeAction.FadeInAndLayout)
            Layout();                                          // the strip takes its parked geometry
        Animate(ParkedScrimHost, 1, duration: ParkedFade);
        Animate(ParkedChrome, 1, duration: ParkedFade);
    }

    /// <summary>
    /// Which of the two strips is up: the parked one while the drawer is tucked away, the open one while it
    /// is out. This is a crossfade and nothing else. The geometry of each was set in <see cref="Layout"/>
    /// and is not changed here.
    ///
    /// <para>The parked bar is part of the mini player, so it shows on the mini player's terms: the drawer
    /// is tucked away AND a track is loaded. It does not depend on <c>_idle</c> alone. When stopped, the
    /// drawer is parked but there is no mini player. A bar that depended on <c>_idle</c> would already be
    /// standing in its short column, and pressing play would show it for a moment before the manager
    /// reopens the drawer. The bar would then appear and wipe. Gating on the track means the bar is not on
    /// screen when playback starts from a stop. The drawer reopens before the title is set (ArtworkManager
    /// calls EnterTrack, then SetText), so the state the bar would need never arrives.</para>
    ///
    /// <para>Hit-testing changes at once, not when the fade ends. A bar that is fading out cannot be
    /// scrubbed, so a scrub cannot move a strip the listener cannot see.</para>
    /// </summary>
    private void SyncSeekBars(bool instant)
    {
        if (_seek is null || _parkSeek is null)
            return;

        var open = _idle ? 0 : 1;
        var park = _idle && !string.IsNullOrWhiteSpace(TrackTitle) ? 1 : 0;

        if (instant)
        {
            _seek.Opacity = open;
            _parkSeek.Opacity = park;
        }
        else
        {
            Animate(_seek, open, duration: ParkedFade);
            Animate(_parkSeek, park, duration: ParkedFade);
        }

        _seek.IsHitTestVisible = open == 1;
        _parkSeek.IsHitTestVisible = park == 1;
    }

    /// <summary>Whether the parked lines are up, and what a change of mind about that means. See
    /// <see cref="ParkedStripFade"/>, which holds the difference between on screen and wanted.</summary>
    private readonly ParkedStripFade _parked = new();

    /// <summary>How fast the parked lines come and go. This is chrome, not artwork. The slow dissolve the
    /// pictures use would be far too slow for three lines of text.</summary>
    private static readonly TimeSpan ParkedFade = TimeSpan.FromMilliseconds(180);

    // Changing the record.
    //
    // When one album replaces another on a live deck, the record is changed, and it should look like that.
    // The disc lifts off, its colour, label and cd are swapped while it is away, and then it comes back
    // down. Nothing else moves. The cover square, the backdrop and the chrome carry on as they were.
    //
    // This fires on the album, not the track. The next track of the same record is the same record. A
    // playlist that moves from one album into another is therefore covered by the same rule.
    //
    // RecordChange decides when the record changes, RecordDisc.Turntable carries the change out, and this
    // file's only part is to say which record is wanted (see WearFace).

    /// <summary>The deck the record sits on. Null until the shell hands the disc over.</summary>
    private RecordDisc.Turntable? _deck;

    /// <summary>The album whose cover is being waited on. It is set when a track's chrome arrives, so the
    /// cover that arrives later can be checked against it.</summary>
    private string? _pressingAlbum;

    /// <summary>
    /// The face this display wants the record to wear.
    ///
    /// <para>It is held here because it arrives in two halves on two clocks. The sleeve carries the colour
    /// and the label, and the folder's disc.png carries the cd. The record wears one face, not two halves.
    /// Each arrival changes this value and hands the whole of it over. When any of it reaches the disc is
    /// the turntable's decision.</para>
    /// </summary>
    private RecordFace _face = RecordFace.None;

    private void WearFace(RecordFace face)
    {
        _face = face;
        _deck?.Wear(face);
    }

    private static readonly TimeSpan RecordFlyDuration = RecordDisc.Turntable.FlyDuration;

    /// <summary>Raised on the idle to track transition (nothing to show, or a track or selection is up).
    /// The shell uses it to close the art drawer and to pin the grids, in step with the record leaving and
    /// returning.</summary>
    public event Action<bool>? IdleChanged;

    /// <summary>True when nothing is playing or selected, the display is on its empty state.</summary>
    public bool IsIdle => _idle;

    /// <summary>
    /// How far the parked band clears the rpm mark's foot.
    ///
    /// <para>The gap is for the mini player as well as for the mark. The parked lines are bottom-aligned on
    /// the seek bar at the band's foot, so the band's height alone decides where their top line lands. That
    /// top line shares the window's top-right corner with the caption buttons, whose foot is at 26 (11
    /// down, 15 tall). At 20, the title started a couple of pixels under the close button, and the two
    /// looked as if they touched. The band carries the gap.</para>
    /// </summary>
    private const double StoppedBandRpmGap = 32;

    /// <summary>
    /// The shortest the parked band may be, however high the mark sits. It is 94.
    ///
    /// <para>The mark is not the only thing this band must clear. The mini player's three lines sit at the
    /// band's foot, in the window's top-right corner, where the caption buttons also are (foot at 26). At
    /// 94 the title line lands at 39, which is 13 pixels under the close button. If the band followed the
    /// mark all the way up on Windows, the lines would move up with it and end up behind those buttons. So
    /// the mark's foot raises this floor and never lowers it.</para>
    /// </summary>
    private const double ParkedLinesFloor = 94;

    /// <summary>The band height the drawer parks at when stopped: <see cref="StoppedBandRpmGap"/> under the
    /// rpm mark's foot. It is read live from the element, so a moved or resized mark is still respected. It
    /// is never below <see cref="ParkedLinesFloor"/>, which the mini player's own lines need.</summary>
    public double StoppedBandHeight
    {
        get
        {
            var rpmHeight = double.IsNaN(RpmMark.Height) ? RpmMark.ActualHeight : RpmMark.Height;
            return Math.Max(ParkedLinesFloor, RpmMark.Margin.Top + rpmHeight + StoppedBandRpmGap);
        }
    }

    private double _chromeHeight;

    /// <summary>How far down the artwork's chrome (cover, title) and the backdrop photo reach: the header
    /// band. The record and the dominant colour use the whole control below it. The shell sets it from the
    /// drag-resizable header row.</summary>
    public double ChromeHeight
    {
        get => _chromeHeight;
        set
        {
            if (Math.Abs(_chromeHeight - value) < 0.5)
                return;
            _chromeHeight = value;
            Layout();
        }
    }

    /// <summary>The display's size at the last layout pass. ActualWidth and ActualHeight still hold the old
    /// values inside a SizeChanged handler. A picture placed from them lands against the old size. That is
    /// why the composition did not recentre when the window was resized.</summary>
    private Windows.Foundation.Size _size;

    /// <summary>The scrim's ramp, in one place. The band's rectangle and the baked tail below it are the
    /// same curve, running the same way, and they must stay that way.</summary>
    private LinearGradientBrush ScrimRamp => (LinearGradientBrush)ScrimRect.Fill;

    /// <inheritdoc cref="ScrimRamp"/>
    private GradientStop[] ScrimStops => [ScrimStop0, ScrimStop1, ScrimStop2, ScrimStop3];

    /// <summary>The geometry the pictures on screen were baked for, which is not necessarily the current
    /// geometry. The crop is chosen for the band's shape, and the wash is registered against the window.
    /// Both change when the drawer moves or the window is resized. A picture made for the wide strip must
    /// be made again for the narrow one, not squeezed into it. All zeroes until something is on
    /// screen.</summary>
    private BakedGeometry _bakedFor;


    public ArtDisplay()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;

        // Windows puts its window buttons at the top right and leaves the top left empty. So the mark rises
        // into the title bar to meet them (see WinMarkTop). This is set before the first layout pass, so
        // the tagline and the cover's clearance are placed against the new top, and nothing is seen moving.
        if (OperatingSystem.IsWindows())
            RpmMark.Margin = new Thickness(RpmMark.Margin.Left, WinMarkTop, RpmMark.Margin.Right, RpmMark.Margin.Bottom);

        // The rpm mark grows in place when parked. It uses a RenderTransform scale, so it zooms without
        // moving anything below it. It is anchored at its top-left corner (origin 0,0). So the zoom keeps
        // its left and top margins and grows down and right into the band, instead of spilling off the
        // window edge. It starts at the parked size when the display opens idle (a fresh library is
        // stopped), and at normal size otherwise.
        _rpmScale = new ScaleTransform();
        RpmMark.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        RpmMark.RenderTransform = _rpmScale;
        _rpmScale.ScaleX = _rpmScale.ScaleY = _rpmScaleTarget = _idle ? RpmParkedScale : 1;

        // The first crop can be made against a size the strip has not settled into yet. The import panel
        // may be up, the splitter is where it was left, and the layout that decides how wide the library
        // really is runs after this control's first pass. So the crop is checked again once it is up. The
        // mark also needs to be lit to its parked strength. Its base Opacity is the dim playing value
        // (0.46), so a library that opens stopped needs the brightening storyboard laid over it. The scale
        // is already set to parked above. HoldEnd keeps the brightness without making the base bright,
        // which would re-arm the flash.
        Loaded += (_, _) =>
        {
            ScheduleRecrop();
            if (_idle)
            {
                AnimateRpmOpacity(1.0, RpmZoomDuration);
                SyncTagline();   // a library that opens stopped opens on the parked lockup, tagline and all
            }
        };

        Unloaded += (_, _) =>
        {
            StopMotion();
            _recropTimer?.Stop();
        };
    }

    /// <summary>
    /// The now-playing seek bars: one for the open drawer, and one for the parked one.
    ///
    /// <para>Both live in the shell's TOP overlay. The art sits behind the content grid and gets no pointer
    /// input, so a bar inside the art tree could never be dragged. ArtDisplay only positions them, in
    /// <see cref="Layout"/>, the same way as the record disc.</para>
    ///
    /// <para>There are two bars, not one that changes shape. Each is laid out once per resize, into the
    /// geometry of the view it belongs to, and it does not move after that. The open bar runs from the
    /// cover across to the record, with its countdown. The parked bar sits in its short column under the
    /// mini player's lines. Opening and closing the drawer is then a crossfade between two bars that are
    /// already the right shape. One bar that changed width on the frame the state flipped would flash a
    /// window-wide bar across the top on every play. See <see cref="SyncParked"/> for the crossfade.</para>
    /// </summary>
    private SeekBar? _seek, _parkSeek;

    public void AttachSeek(SeekBar open, SeekBar parked)
    {
        _seek = open;
        _parkSeek = parked;
        SyncSeekBars(instant: true);
    }

    /// <summary>The two ONNX models the crop runs on. The host passes them in. The manager owns everything
    /// else: fetching, ranking and the stored order.</summary>
    public void AttachModels(SaliencyService saliency, FaceService faces)
    {
        _saliency = saliency;
        _faces = faces;
    }

    // The scene: what the panel's shape hides.

    /// <summary>The live obstruction geometry: how wide the panel is, and where the cover and the record
    /// cut into the strip. The manager gives this to the chooser and the crop. The tuned defaults are used
    /// until the first layout pass gives real numbers.</summary>
    public ArtScene Scene
    {
        get
        {
            var w = _size.Width;
            var h = PictureBand;   // the subject is framed to the artwork's own zone, not the chrome's
            if (w <= 0 || h <= 0)
                return ArtScene.Default;

            var slot = Math.Max(1, w - _backdropInset);
            var recordLeft = w - _backdropInset;   // the disc's leftmost reach across the band
            return ArtScene.FromPanel(w, h, _clearLeft, recordLeft, slot);
        }
    }

}
