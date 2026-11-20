using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// What the album page stands on: the ground under the whole card, taken from the record's own sleeve,
/// and the two hems that fade the tracklist at whichever end has songs past it.
///
/// <para>Both show what is on screen that is not text, and both are measured. The ground uses this
/// record's colours only. Each hem is as deep as the scroll has left at its end. A record whose songs
/// all fit shows neither.</para>
///
/// <para><b>What arrives when.</b> Six things arrive here on their own schedules. The ground and the
/// stage in front of it are what they can collide over:</para>
/// <list type="number">
/// <item><b>Load</b> (the user, on the dispatcher) starts a new <see cref="StageOpening"/> with a new
/// <see cref="_paint"/>, which lets everything below drop out of date. A fresh record also takes the
/// last record's sleeve, disc and ground down (<see cref="PeelStage"/>). A re-read of the same record
/// leaves them up, and what it reads replaces them in place.</item>
/// <item><b>The sleeve</b> (local: disk, then a cut and a blur on workers; borrowed: the network first)
/// arrives with the opening's stamp. The cut and the ground are read side by side and put on the page
/// in ONE turn, so the sleeve and the colours it gives the page come in together. The check uses the
/// stamp, not the folder string, because the same folder opened twice is two paints.</item>
/// <item><b>The disc</b> (disk scan, then the press in the sleeve's colour) waits for the sleeve's
/// bytes, so it is pressed once in its final colour, and then for the sleeve to be on the page, so it
/// never comes in before the sleeve it stands behind. See <see cref="StageOpening"/>.</item>
/// <item><b>Close</b> moves the stamp on too, so a read still in the air lands nowhere.</item>
/// <item><b>The fades</b> (<see cref="ChromeFade"/> on the render tick) are the only code that writes
/// these layers' opacity. So a peel and an arrival cannot fight: the second call changes the target of
/// the first.</item>
/// <item><b>The tracklist's own scrolling and resizing</b> change nothing the above touches. They are
/// listed because the hems and the ground share this file, not because they interact.</item>
/// </list>
/// <para>Verified by reading: (1) against (2), (3) and (4) by the stamp, (2) against (3) by the
/// opening's two signals, which every path completes (see <see cref="SleeveLands"/>), (5) against
/// everything by there being one writer. Not verified: what happens without a composition stack, under
/// (6). <see cref="ScrollHem.Attach"/> returns null there, and the list keeps hard edges.</para>
///
/// <para><b>Fade or set.</b> A change the reader can see is faded. A change to a page that is not on
/// screen is made at once (see <see cref="PageBackdrop.IsShowing"/>). A page left behind keeps its layers
/// while it fades out as a whole. If they were faded down on the next open, the reader would see the
/// last record leave as the new one comes up.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    // ══ The ground: the record's sleeve, blurred until it is only colour ══

    /// <summary>How long the sleeve, the disc and the ground take to come in. It matches the window's own
    /// fade (ShellGround.CrossFadeMs), so the page and the window behind it change together. They go out
    /// faster when the page is given another record, because the reader is not waiting on the
    /// exit.</summary>
    private const double GroundFadeMs = 260;
    private const double GroundPeelMs = 120;

    /// <summary>Which paint the page is on. Every read below is stamped with this value, and drops out
    /// if the reader has moved on. See the map at the top of this file.</summary>
    private int _paint;

    private ChromeFade? _groundFade;
    private ChromeFade GroundFade => _groundFade ??= new ChromeFade(PageGround);

    private ChromeFade? _washFade;
    private ChromeFade WashFade => _washFade ??= new ChromeFade(PageWash);

    /// <summary>The fade for a change the reader can see, and none for a page that is not on screen. See
    /// "Fade or set" in the map at the top of this file.</summary>
    private double Seen(double ms) => PageBackdrop.IsShowing(this) ? ms : 0;

    /// <summary>
    /// Takes the last record's sleeve, disc and ground down, so the colours of the album being left are
    /// never the ground under the album being opened. Called for a fresh record only. A re-read of the
    /// same record keeps them up.
    ///
    /// <para>On a page that is not on screen, the layers are set to nothing at once and the pictures are
    /// let go. On the page the reader is looking at, they fade out, and the next record's pictures
    /// replace them when they land.</para>
    /// </summary>
    private void PeelStage()
    {
        var seen = PageBackdrop.IsShowing(this);
        var ms = seen ? GroundPeelMs : 0;

        GroundFade.To(0, ms);
        WashFade.To(0, ms);
        SleeveFade.To(0, ms);
        DiscFade.To(0, ms);

        if (!seen)
        {
            Cover.Source = null;
            CoverDisc.Source = null;
            CoverDiscScan.Source = null;
            PageWashArt.ImageSource = null;
            foreach (var which in StagePictures)
                ForgetStage(which);
        }

        // The window is also showing this record's colours (see PageBackdrop), and they are not the
        // next record's. Open it with nothing on, not closed: the page is still on screen, and saying
        // it has been left would show the app's own backdrop in the gap before the new sleeve lands.
        PageBackdrop.Opened(this, null);
    }

    /// <summary>The ground read from one sleeve, ready to put on the page: the three layers of
    /// <see cref="SleeveGround"/>, with the colour as a brush and the blur decoded.</summary>
    private sealed record GroundDress(Brush Colour, BitmapImage? Wash, double Veil);

    /// <summary>
    /// Reads the record's ground from its sleeve: its mean colour as the ground, the sleeve blurred over
    /// that, and a black veil over the blur that lowers the brightness. Null where the sleeve cannot be
    /// decoded.
    ///
    /// <para>The three layers are read by <see cref="SleeveGround"/>, which is also what the wall's
    /// flip-down drawer stands on. The two surfaces show the same ground because they ask the same
    /// question in the same place.</para>
    ///
    /// <para>Nothing is written to the page here. The caller puts the ground on together with the
    /// sleeve (see <see cref="PutGround"/>), so the two come in on one beat.</para>
    /// </summary>
    private static async Task<GroundDress?> ReadGroundAsync(byte[] sleeve)
    {
        try
        {
            if (await Task.Run(() => SleeveGround.Read(sleeve)) is not { } read)
                return null;

            BitmapImage? wash = null;
            if (read.Wash is { Length: > 0 } blurred)
            {
                wash = new BitmapImage();
                await wash.SetSourceAsync(Presentation.Common.BitmapBytes.Stream(blurred));
            }

            return new GroundDress(new SolidColorBrush(read.Ground), wash, read.Veil);
        }
        catch
        {
            // Only decoration. The page keeps the card's own glass, and the record is not failed.
            return null;
        }
    }

    /// <summary>
    /// Puts a ground on the page and lends it to the window, or takes the ground down where the sleeve
    /// gave none. Called in the same turn as the sleeve is put on the stage.
    ///
    /// <para>If the sleeve cannot be decoded, the page keeps the card's own glass. That is a complete
    /// surface, and it is what every record without a cover looks like.</para>
    /// </summary>
    private void PutGround(GroundDress? ground)
    {
        if (ground is null)
        {
            GroundFade.To(0, Seen(GroundPeelMs));
            WashFade.To(0, Seen(GroundPeelMs));
            PageBackdrop.Opened(this, null);
            return;
        }

        PageGround.Background = ground.Colour;
        PageVeil.Opacity = ground.Veil;
        PageWashArt.ImageSource = ground.Wash;

        var strength = ground.Wash is null ? 0 : SleeveGround.WashStrength;
        GroundFade.To(1, Seen(GroundFadeMs));
        WashFade.To(strength, Seen(GroundFadeMs));

        // The same three layers, lent to the whole window. While this record's page is open and no
        // artwork covers the app, the app stands on the page's sleeve. The brush and the decoded picture
        // are passed on as they are, not read again, so there is one sleeve and two surfaces that show it.
        PageBackdrop.Opened(this, new PageBackdrop.Ground(
            ground.Colour, ground.Wash, strength, ground.Veil, Weight: null));
    }

    // ══ the corner the page shares with the pane beside it ══

    /// <summary>How long the join takes to appear or disappear when the pane beside the page opens or
    /// closes under the reader. It is slower than GroundFadeMs, because the record itself has not
    /// changed.</summary>
    private const double CornerFadeMs = 320;

    private ChromeFade? _cornerFade;
    private ChromeFade CornerFade => _cornerFade ??= new ChromeFade(PageCorner);

    /// <summary>
    /// Paints the page's bottom left corner in the left pane's colour, while that pane shows a track
    /// from this album. Otherwise it removes the paint.
    ///
    /// <para><paramref name="animate"/> sets how the change happens. Only the caller knows which case
    /// applies. If a pane opens or closes beside a page the reader is already looking at, that is a
    /// change, so it fades. If the page opens while the pane is already there, the join was never
    /// absent, so it is set at once with no fade.</para>
    /// </summary>
    private void ShowCorner(bool animate)
    {
        // Asked with this page's record in hand: the join is only true while the pane on the left shows
        // the album on screen here. For any other record there is nothing to join.
        if (PaneCorner.For(_folder) is not { } colour)
        {
            CornerFade.To(0, animate ? CornerFadeMs : 0);
            return;
        }

        // Full at the corner itself, and gone by the far stop. The join is exactly the pane's colour
        // where the two cards touch.
        CornerNear.Color = Windows.UI.Color.FromArgb(0xFF, colour.R, colour.G, colour.B);
        CornerFar.Color = Windows.UI.Color.FromArgb(0x00, colour.R, colour.G, colour.B);

        CornerFade.To(1, animate ? CornerFadeMs : 0);
    }

    /// <summary>Connects the page to the pane beside it for as long as the page is in the tree. The
    /// event is static but the view comes and goes, so the handler is removed on unload. Otherwise the
    /// next album page opened would be the second of two pages listening.</summary>
    private void WatchCorner()
    {
        Loaded += (_, _) =>
        {
            PaneCorner.Changed += OnCornerChanged;

            // Use the current state now, without a fade. The page has only just arrived, so it cannot
            // have shown anything else.
            ShowCorner(animate: false);
        };

        Unloaded += (_, _) => PaneCorner.Changed -= OnCornerChanged;
    }

    /// <summary>The pane beside the page opened, closed or changed colour. The reader is watching that,
    /// so it fades.</summary>
    private void OnCornerChanged() => ShowCorner(animate: true);

    // ══ The tracklist's two hems ══

    private ScrollHem? _trackHem;

    /// <summary>Set when the composition stack refuses the hem on this machine, so the page does not
    /// try again on every notch of the wheel.</summary>
    private bool _noTrackHem;

    /// <summary>Both hems, each as deep as the scroll has left at its end. Wired to everything that can
    /// change either answer: the scrolling itself, the panel being resized, and the lines of a record
    /// arriving, which ViewChanged does not report.</summary>
    private void ShowTrackHems()
    {
        if (_trackHem is null && !_noTrackHem)
        {
            _trackHem = ScrollHem.Attach(
                TrackScroller, TrackCut, TrackHemHead, TrackHemFoot, Orientation.Vertical, ScrollHem.Depth);
            _noTrackHem = _trackHem is null;
        }

        _trackHem?.Show();
    }

    private void WatchTrackHems()
    {
        TrackScroller.ViewChanged += (_, _) => ShowTrackHems();
        TrackScroller.SizeChanged += (_, _) => ShowTrackHems();
        TrackList.SizeChanged += (_, _) => ShowTrackHems();
        Loaded += (_, _) => ShowTrackHems();
    }
}
