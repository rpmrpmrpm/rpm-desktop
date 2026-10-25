using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What the collection's screens are drawn on (see <see cref="PageBackground"/>), as the Android TV app's
/// Background row chooses it (its PageBackground.kt): a still picture, a flat colour, or the moving room.
///
/// <para><b>Where it is.</b> One layer, PageGround, under the collection's three layers (the collection itself, a
/// record's page and the tuner) and over the room. It is up exactly while the collection is
/// (<see cref="LibraryUp"/>), placed or faded with it, so a record crossing with the wall does not cross the
/// background too. Each of the three layers already lays a 45% black over whatever is under it; over a picture that
/// is the TV's dim (its screens' shade laid into the picture, 1 - 0.45), so nothing is added for it.</para>
///
/// <para><b>Dark</b> is drawn as it is on the TV, #28282D with no shade. Here the shade is over it all the same,
/// so the colour under the shade is the one that comes out as #28282D through it: #494952 at 0.55 is
/// #28282D.</para>
///
/// <para><b>The room under it stops.</b> A picture or a flat colour covers the whole room, so the room would be
/// drawing for nobody. Once the background is fully up and drawn, <see cref="PageGroundCovers"/> says so and the
/// host stops the room (PegasusPlayer.OnPageGroundCovers), as the TV stops its room's surface under a still
/// backdrop. It says the opposite the moment the background starts to go, so the room is drawing again before
/// anything of it shows.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The anthracite under the collection's 45% black: #28282D through it (see the class remarks). Made
    /// on first use, on this thread.</summary>
    private SolidColorBrush? _darkGround;

    /// <summary>Raised when the background starts or stops covering the whole room. True once it is fully up and
    /// its picture is drawn; false as soon as it starts to go, or the moving room is chosen.</summary>
    public event Action<bool>? PageGroundCovers;

    private ChromeFade? _pageGroundFade;

    /// <summary>Whether the collection is up, so the background is up or on its way up.</summary>
    private bool _pageGroundUp;

    /// <summary>The picture asked for, and whether it has been decoded. A picture still decoding covers
    /// nothing: the room goes on showing until it is in, as the TV's surface does.</summary>
    private string? _pageGroundUri;
    private bool _pageGroundPictureIn;

    private bool _pageGroundCovers;

    /// <summary>The background the settings have chosen.</summary>
    private PageBackground ChosenGround => Settings?.PageBackground ?? PageBackground.Default;

    /// <summary>
    /// Brings the background up with the collection, or takes it away with it, over the same
    /// <see cref="ScreenCrossMs"/>. <paramref name="immediate"/> for a collection that is placed rather than
    /// faded in (see <see cref="ShowLibrary"/>).
    /// </summary>
    private void ShowPageGround(bool up, bool immediate)
    {
        _pageGroundFade ??= new ChromeFade(OnPageGroundFade, PageGround.Opacity);
        _pageGroundUp = up;
        if (up)
            ApplyPageBackground();
        _pageGroundFade.To(up ? 1 : 0, immediate ? 0 : ScreenCrossMs);
        PaintPageGroundCovers();
    }

    private void OnPageGroundFade(double value)
    {
        if (PageGround.Opacity != value)
            PageGround.Opacity = value;

        // Landed: fully up, or gone, and out of the tree once gone.
        if (value <= 0 && !_pageGroundUp)
            PageGround.Visibility = Visibility.Collapsed;
        if (value is <= 0 or >= 1)
            PaintPageGroundCovers();
    }

    /// <summary>
    /// Puts the chosen background in: the picture, the colour, or nothing for the moving room. Called as the
    /// collection comes up, and by the host when the setting changes, which shows at once behind the open panel. A
    /// picture already in is not loaded again.
    /// </summary>
    public void ApplyPageBackground()
    {
        var ground = ChosenGround;
        string? uri = ground switch
        {
            PageBackground.Default => Backdrop.RPaMp,
            PageBackground.Pegasus => Backdrop.Pegasus,
            _ => null,
        };

        PageGround.Background = ground == PageBackground.Dark
            ? _darkGround ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x49, 0x49, 0x52))
            : null;
        if (uri != _pageGroundUri)
        {
            _pageGroundUri = uri;
            _pageGroundPictureIn = false;
            // Decoded at the file's own size, as the window decodes the same file for its backdrop.
            PageGroundPicture.Source = uri is null ? null : new BitmapImage(new Uri(uri));
        }

        if (_pageGroundUp)
            PageGround.Visibility = ground == PageBackground.Room ? Visibility.Collapsed : Visibility.Visible;
        PaintPageGroundCovers();
    }

    private void PageGroundPicture_ImageOpened(object sender, RoutedEventArgs e)
    {
        _pageGroundPictureIn = true;
        PaintPageGroundCovers();
    }

    /// <summary>Works out whether the background covers the whole room now, and says so when that changes.</summary>
    private void PaintPageGroundCovers()
    {
        var ground = ChosenGround;
        var covers = _pageGroundUp
            && PageGround.Visibility == Visibility.Visible
            && PageGround.Opacity >= 1
            && (ground == PageBackground.Dark || (ground != PageBackground.Room && _pageGroundPictureIn));
        if (covers == _pageGroundCovers)
            return;
        _pageGroundCovers = covers;
        PageGroundCovers?.Invoke(covers);
    }
}
