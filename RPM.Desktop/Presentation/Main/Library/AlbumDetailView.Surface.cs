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
/// <para><b>What arrives when.</b> Five things arrive here on their own schedules. The ground is the
/// one they can collide over:</para>
/// <list type="number">
/// <item><b>Load</b> (the user, on the dispatcher) clears the ground and starts a cover read. It first
/// increments <see cref="_paint"/>, which lets everything below drop out of date.</item>
/// <item><b>The local sleeve</b> (worker: read, blur, average) arrives with a stamp. It is stale if the
/// reader has opened another record since. The check uses the stamp, not the folder string, because the
/// same folder opened twice is two paints, and the second paint's fade must not be cut short by the
/// first.</item>
/// <item><b>The borrowed sleeve</b> (network, <c>FillRemoteCoverAsync</c>) arrives the same way. It can
/// arrive after the local read has already reported that there is no sleeve. Both use the stamp, so the
/// last writer wins, and that is the record the reader asked for.</item>
/// <item><b>The fades</b> (dispatcher timers inside <see cref="ChromeFade"/>) are the only code that
/// writes opacity. So a peel and a rebuild cannot fight: the second call changes the target of the
/// first.</item>
/// <item><b>The tracklist's own scrolling and resizing</b> change nothing the above touches. They are
/// listed because the hems and the ground share this file, not because they interact.</item>
/// </list>
/// <para>Verified by reading: (1) against (2) and (3) by the stamp, (2) against (3) by the stamp, (4)
/// against everything by there being one writer. Not verified: what happens without a composition
/// stack, under (5). <see cref="ScrollHem.Attach"/> returns null there, and the list keeps hard
/// edges.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    // ══ The ground: the record's sleeve, blurred until it is only colour ══

    /// <summary>The ground fades in with the sleeve it came from. It fades out faster when the page is
    /// given another record, because the reader is not waiting on the exit.</summary>
    private const double GroundFadeMs = 260;
    private const double GroundPeelMs = 120;

    /// <summary>Which paint the page is on. Every read below is stamped with this value, and drops out
    /// if the reader has moved on. See the map at the top of this file.</summary>
    private int _paint;

    private ChromeFade? _groundFade;
    private ChromeFade GroundFade => _groundFade ??= new ChromeFade(PageGround);

    private ChromeFade? _washFade;
    private ChromeFade WashFade => _washFade ??= new ChromeFade(PageWash);

    /// <summary>
    /// Takes the ground down and drops any sleeve still on its way to it. Called when a record is left,
    /// so the colours of the album being closed are never the ground under the album being opened. On a
    /// page opened fresh the layers are already at zero, so this does nothing visible.
    /// </summary>
    private void PeelGround()
    {
        _paint++;
        GroundFade.To(0, GroundPeelMs);
        WashFade.To(0, GroundPeelMs);

        // The window is also showing this record's colours (see PageBackdrop), and they are not the
        // next record's. Open it with nothing on, not closed: the page is still on screen, and saying
        // it has been left would show the app's own backdrop in the gap before the new sleeve lands.
        PageBackdrop.Opened(this, null);
    }

    /// <summary>
    /// Dresses the page in the record's sleeve: its mean colour as the ground, the sleeve blurred over
    /// that, and a black veil over the blur that lowers the brightness.
    ///
    /// <para>The three layers are read by <see cref="SleeveGround"/>, which is also what the wall's
    /// flip-down drawer stands on. The two surfaces show the same ground because they ask the same
    /// question in the same place.</para>
    ///
    /// <para>If the sleeve cannot be decoded, the page keeps the card's own glass. That is a complete
    /// surface, and it is what every record without a cover looks like.</para>
    /// </summary>
    private async void ShowSleeveGroundAsync(byte[] sleeve)
    {
        var mine = ++_paint;

        try
        {
            var read = await Task.Run(() => SleeveGround.Read(sleeve));

            // The reader opened another record while this one was read from disk.
            if (mine != _paint)
                return;

            if (read is null)
                return;

            var ground = new SolidColorBrush(read.Ground);
            PageGround.Background = ground;
            PageVeil.Opacity = read.Veil;

            BitmapImage? wash = null;
            if (read.Wash is { Length: > 0 } blurred)
            {
                wash = new BitmapImage();
                var stream = Presentation.Common.BitmapBytes.Stream(blurred);
                await wash.SetSourceAsync(stream);

                // Checked again after the decode: SetSourceAsync is a second dispatcher turn, and the
                // reader can open another record during it.
                if (mine != _paint)
                    return;

                PageWashArt.ImageSource = wash;
                WashFade.To(SleeveGround.WashStrength, GroundFadeMs);
            }

            GroundFade.To(1, GroundFadeMs);

            // The same three layers, lent to the whole window. While this record's page is open and no
            // artwork covers the app, the app stands on the page's sleeve. The brush and the decoded
            // picture are passed on as they are, not read again, so there is one sleeve and two
            // surfaces that show it.
            PageBackdrop.Opened(this, new PageBackdrop.Ground(
                ground, wash, wash is null ? 0 : SleeveGround.WashStrength, read.Veil, Weight: null));
        }
        catch
        {
            // Only decoration here. The page keeps the card's own glass, and the record is not failed.
        }
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
