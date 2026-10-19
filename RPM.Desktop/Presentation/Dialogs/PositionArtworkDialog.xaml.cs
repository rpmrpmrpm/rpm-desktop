using RPM.Core.Art;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.NowPlaying;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>What the placement window returns. A null <see cref="Framing"/> means the app places the
/// photo itself. This happens when the user presses reset and saves. The stored framing is then removed,
/// not replaced with a copy of the chooser's answer.</summary>
public readonly record struct ArtPlacement(ArtFraming? Framing);

/// <summary>
/// Lets the user place an artist's photo in the header by hand.
///
/// <para><b>The model is the header at another size.</b> Every rectangle in it (the band, the cover
/// square, the three lines of text, and the record that hangs half off the right) is one of the app's
/// live measurements (<see cref="ArtDisplay.HeaderShape"/>) times one factor. The photo is drawn into the
/// same zone, cut to the same shape, as in the real header. So nothing has to be translated between this
/// model and the real header. It is the same picture in the same frame, only smaller.</para>
///
/// <para><b>Nothing here decodes the photo again.</b> The photo is decoded once, when the window opens.
/// Every drag and every turn of the wheel only sets the photo's size and position again. So the photo
/// follows the pointer exactly, with no new decode in between.</para>
///
/// <para><b>It is additive.</b> A photo either has a saved framing or it does not. A photo without one is
/// framed by the chooser, as before. The window opens on what is in force: a framing the user made, or
/// else the chooser's answer for this picture. So the first thing shown is what is already on screen, and
/// the view does not jump when the window opens.</para>
/// </summary>
public sealed partial class PositionArtworkDialog : ContentDialog
{
    // ── the header the model is built from ──

    private readonly ArtDisplay.HeaderShape _shape;
    private readonly ArtFramingSubject _subject;

    /// <summary>
    /// The header used when the real one has not been laid out yet. It should not be needed, because the
    /// window opens from a header that is on screen. It stops a model from being built from an empty
    /// header, which would be a blank rectangle.
    /// </summary>
    private static readonly ArtDisplay.HeaderShape PlausibleHeader = new(
        Width: 1280, Band: 200, PictureHeight: 224, SlotWidth: 1150, Breath: 60,
        CoverSide: 156, CoverLeft: 24, CoverBottom: 212,
        RecordDiameter: 380, RecordTop: -90,
        TextLeft: 194, TextBottom: 191);

    // ── the photograph ──

    private int _sourceWidth, _sourceHeight;

    /// <summary>The number of pixels on the long edge that the preview is decoded to. The model is at
    /// most about a thousand points wide, and the picture inside it is smaller, so this is enough even
    /// when zoomed in. It also stops a 24-megapixel photo from needing about 100 MB when the window
    /// opens.</summary>
    private const int PreviewDecodeEdge = 1400;

    // ── the framing being made ──

    /// <summary>
    /// The framing as it stands, not yet settled. A drag that has run past the edge of the photo keeps
    /// its overshoot here, so dragging back starts after the same distance instead of moving at once.
    /// Everything shown and everything saved goes through <see cref="Settled"/>.
    /// </summary>
    private ArtFraming _framing = ArtFraming.Whole;

    /// <summary>Whether the framing on screen was made by the user, not by the app. The reset clears it
    /// and every edit sets it. The save uses it to decide whether to write a framing or to remove the
    /// stored one.</summary>
    private bool _placed;

    /// <summary>The chooser's own framing of this photo, which the reset goes back to. It is null when
    /// the chooser has not framed this photo in this session. The reset then uses the whole picture,
    /// which is what the chooser shows for a photo it cannot frame anyway.</summary>
    private readonly ArtFraming? _automatic;

    /// <summary>True while the sliders and the picture are being brought into agreement. This stops the
    /// control being written from changing the other one again.</summary>
    private bool _syncing;

    /// <summary>Set once the photo is shown. Nothing may be dragged, zoomed or saved before
    /// then.</summary>
    private bool _ready;

    private PositionArtworkDialog(ArtFramingSubject subject, ArtDisplay.HeaderShape shape, ArtFraming? automatic)
    {
        _subject = subject;
        _shape = shape.Measured ? shape : PlausibleHeader;
        _automatic = automatic;
        _placed = subject.Placed;
        // Use the framing in force: the user's own framing, else the chooser's answer for this photo.
        // Both match what is on screen. Only a photo the chooser has not framed this session uses the
        // default. The load then replaces it with a framing that covers the header. Whole would show the
        // whole picture as a strip.
        _framing = subject.Framing ?? automatic ?? ArtFraming.Whole;

        InitializeComponent();
        ArtistText.Text = subject.Artist;
        Shell.AttachResize(this, minWidth: 760, minHeight: 420, maxWidth: 1500,
                           defaultHeight: 560, defaultWidth: 1060);

        StatusText.Text = Loc.Get("ArtPlace_Loading");
        StatusText.Visibility = Visibility.Visible;
        Model.Visibility = Visibility.Collapsed;
        SetControlsEnabled(false);
    }

    /// <summary>What the user chose, or null if the window was closed without saving.</summary>
    public ArtPlacement? Result { get; private set; }

    /// <summary>
    /// Opens the window on the photo in the header. Returns null when the window is cancelled. It also
    /// returns null when the photo cannot be fetched. Then there is nothing to place, and a window with
    /// an empty frame is worse than no window.
    /// </summary>
    public static async Task<ArtPlacement?> ShowAsync(
        XamlRoot root, ArtFramingSubject subject, ArtDisplay.HeaderShape shape, ArtFraming? automatic)
    {
        var dialog = new PositionArtworkDialog(subject, shape, automatic) { XamlRoot = root };

        // The photo is fetched while the window is open, not before. The download is usually a cache hit
        // and instant. When it is not, a window that is open and says it is loading is better than a menu
        // item that seems to do nothing.
        _ = dialog.LoadPictureAsync();

        await DialogHost.ShowAsync(dialog);
        return dialog.Result;
    }

    private async Task LoadPictureAsync()
    {
        try
        {
            // A missing photo and a failed fetch both show "This picture could not be loaded". Both are
            // true, so nothing is stored.
            if ((await RemoteImage.BytesAsync(_subject.Url)).Match(found => found, _ => null) is not { } bytes)
            {
                Fail();
                return;
            }

            var (width, height) = Services.Art.ArtScaler.BoundsOf(bytes);
            if (width < 8 || height < 8)
            {
                Fail();
                return;
            }

            var bitmap = await RemoteImage.DecodeAsync(bytes, PreviewDecodeEdge);

            _sourceWidth = width;
            _sourceHeight = height;

            Photo.Source = bitmap;

            // A photo with no saved framing and no chooser framing this session. Open it covering the
            // header. This is the closest result to what the app would do, without running the chooser
            // again.
            if (_subject.Framing is null && _automatic is null)
                _framing = new ArtFraming(0.5, 0.5, ArtFraming.FillHeightFor(width, height, _shape.PictureAspect), 1);

            _ready = true;
            StatusText.Visibility = Visibility.Collapsed;
            Model.Visibility = Visibility.Visible;
            SetControlsEnabled(true);

            LayoutModel();
            SyncFromFraming();
        }
        catch
        {
            Fail();
        }

        void Fail()
        {
            _ready = false;
            StatusText.Text = Loc.Get("ArtPlace_NoPicture");
            StatusText.Visibility = Visibility.Visible;
            Model.Visibility = Visibility.Collapsed;
            SetControlsEnabled(false);
        }
    }

    private void SetControlsEnabled(bool on)
    {
        ZoomSlider.IsEnabled = on;
        WidthSlider.IsEnabled = on;
        AcrossSlider.IsEnabled = on;
        DownSlider.IsEnabled = on;
        AutoButton.IsEnabled = on;
        FillButton.IsEnabled = on;
        SaveButton.IsEnabled = on;
    }

    // ── the model: the header at another size ──

    /// <summary>How far below the photo's zone the model continues. The record's lower arc overlaps the
    /// library, as it does in the app, instead of being cut off at the bottom of the model. It is
    /// measured from the disc, with a little space under it.</summary>
    private double LibraryStrip
        => Math.Max(48, _shape.RecordTop + _shape.RecordDiameter - _shape.PictureHeight + 14);

    private double ModelWidth => Math.Max(1, _shape.Width);
    private double ModelHeight => Math.Max(1, _shape.PictureHeight + LibraryStrip);

    /// <summary>Model points per real header point. Everything below is one of the app's measurements
    /// times this factor and nothing else. That is what makes this a model.</summary>
    private double _scale = 1;

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutModel();
        PlacePhoto();
    }

    private void LayoutModel()
    {
        var room = Stage.ActualSize;
        if (room.X <= 1 || room.Y <= 1)
            return;

        _scale = Math.Min(room.X / ModelWidth, room.Y / ModelHeight);
        if (!double.IsFinite(_scale) || _scale <= 0)
            return;

        var k = _scale;
        var modelW = ModelWidth * k;
        var modelH = ModelHeight * k;

        Model.Width = modelW;
        Model.Height = modelH;
        ModelClip.Rect = new Rect(0, 0, modelW, modelH);

        var band = _shape.Band * k;
        var picture = _shape.PictureHeight * k;
        var pictureRight = _shape.PictureRight * k;

        // The library under the header, and the edge of the first card. The bottom of the photo fades
        // onto this in the app.
        Put(LibraryPlate, 0, picture, modelW, modelH - picture);
        Put(LibraryCard, _shape.CoverLeft * k, picture, Math.Max(1, modelW - (2 * _shape.CoverLeft * k)),
            Math.Max(1, modelH - picture));

        Put(HeaderPlate, 0, 0, modelW, picture);

        // The zone is the whole room for the picture. Only the window the framing asks for is shown
        // inside it. PlacePhoto sets that window, because it is the part that moves.
        Put(PictureZone, 0, 0, Math.Max(1, pictureRight), Math.Max(1, picture));

        // The scrim fades out where the clear gap by the record begins, as the header's scrim does.
        Put(Scrim, 0, 0, Math.Max(1, pictureRight), Math.Max(1, picture));
        var scrimEnd = Math.Clamp((_shape.SlotWidth - ScrimClearGap) / Math.Max(1, _shape.PictureRight), 0.25, 1);
        ScrimMid.Offset = scrimEnd * 0.52;
        ScrimEnd.Offset = scrimEnd;

        // The photo's own left edge, wherever the framing has put it. This is not the zone's edge. A
        // photo that does not reach across starts partway in, and that is where it fades out.
        PlaceFade(k, picture);

        var side = _shape.CoverSide * k;
        Put(CoverBox, _shape.CoverLeft * k, (_shape.CoverBottom - _shape.CoverSide) * k, side, side);
        CoverGlyph.FontSize = Math.Max(6, side * 0.30);

        LayoutText(k);
        LayoutRecord(k);

        Put(BandFoot, 0, band, modelW, 1);
        Put(PlateEdge, 0, 0, modelW, modelH);
    }

    /// <summary>How much of the drawn picture its left fade covers, as a share of its width. The header's
    /// own ramp is measured from where the subject is. This value only sets the look of the fade. It is
    /// not a rule.</summary>
    private const double FadeShare = 0.18;

    /// <summary>Fades the photo's left edge into the header's ground, over the drawn photo only. A fade
    /// in the empty part of the zone would be a dark band on a background of the same colour. That band
    /// is invisible where it belongs, and wrong everywhere else.</summary>
    private void PlaceFade(double k, double pictureHeight)
    {
        if (!_ready)
        {
            Put(PictureFade, 0, 0, 0, 0);
            return;
        }

        var window = CurrentWindow();
        var perPixel = PointsPerPixel(window);
        var drawn = window.Width * perPixel;
        if (perPixel <= 0 || drawn <= 0)
        {
            Put(PictureFade, 0, 0, 0, 0);
            return;
        }

        var left = Math.Max(0, (_shape.PictureRight * k) - drawn);
        Put(PictureFade, left, 0, Math.Max(1, drawn * FadeShare), pictureHeight);
    }

    /// <summary>The clear gap between the header's photo fade and the disc (ArtDisplay.ScrimClearGap). It
    /// is copied here, not shared, because it is a look and not a rule. The model needs the scrim to end
    /// where it ends in the real header, but the two values do not have to match exactly.</summary>
    private const double ScrimClearGap = 40;

    /// <summary>The three lines under the cover, drawn as bars. The bars use the sizes of the real text:
    /// 22, 15 and 13 points, with 2 points between the title and artist lines and 2 more before the
    /// album. A bar is about 0.7 of its point size, which is about the height a line of text
    /// covers.</summary>
    private void LayoutText(double k)
    {
        const double titleSize = 22, artistSize = 15, albumSize = 13, lineGap = 2, albumGap = 2;

        var left = _shape.TextLeft * k;
        var right = Math.Max(left + 20, _shape.PictureRight * k);
        var room = Math.Max(20, right - left);

        var album = albumSize * 0.7 * k;
        var artist = artistSize * 0.7 * k;
        var title = titleSize * 0.7 * k;

        var albumTop = (_shape.TextBottom * k) - album;
        var artistTop = albumTop - ((lineGap + albumGap) * k) - artist;
        var titleTop = artistTop - (lineGap * k) - title;

        Put(AlbumBar, left, albumTop, room * 0.30, Math.Max(1, album));
        Put(ArtistBar, left, artistTop, room * 0.42, Math.Max(1, artist));
        Put(TitleBar, left, titleTop, room * 0.62, Math.Max(1, title));
    }

    /// <summary>The record, centred on the header's right edge. The app puts it there, so half of it
    /// hangs off. The model's clip cuts off the other half, as the window does.</summary>
    private void LayoutRecord(double k)
    {
        var d = _shape.RecordDiameter * k;
        var cx = _shape.Width * k;
        var cy = (_shape.RecordTop * k) + (d / 2);

        Round(RecordShadow, cx, cy + (d * 0.03), d * 1.08);
        Round(RecordDisc, cx, cy, d);
        Round(RecordGrooveOuter, cx, cy, d * 0.78);
        Round(RecordGrooveInner, cx, cy, d * 0.56);
        Round(RecordLabel, cx, cy, d * 0.34);
        Round(RecordHole, cx, cy, Math.Max(2, d * 0.045));

        static void Round(FrameworkElement element, double centreX, double centreY, double diameter)
            => Put(element, centreX - (diameter / 2), centreY - (diameter / 2),
                   Math.Max(1, diameter), Math.Max(1, diameter));
    }

    private static void Put(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    // ── where the photo lands: set on every change, with no new decode ──

    /// <summary>The window the framing asks for, in the photo's own pixels, cut to the header's shape.
    /// This is the one place where the model and the real header must agree. They agree because both take
    /// the shape from the same measurement.</summary>
    private FrameWindow CurrentWindow()
        => _framing.Window(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    /// <summary>Model points per source pixel, at the framing's current zoom. The whole preview is drawn
    /// from this number: the photo, the tracking of a drag, and the anchor point of the wheel.</summary>
    private double PointsPerPixel(FrameWindow window)
        => window.Height <= 0 ? 0 : (_shape.PictureHeight * _scale) / window.Height;

    /// <summary>
    /// Places the photo in its zone.
    ///
    /// <para>The photo is placed the way the header places its own backdrop layer (see ArtDisplay.Stand).
    /// The scale is the same on both axes, set with Width and Height and
    /// <c>Stretch.Fill</c>. The offset is set as a margin. It is not a render transform. Nothing in this
    /// app moves a picture that way, and a preview that works differently from what it shows could
    /// disagree with it. The whole photo is laid out, and the zone clips it. So the window the framing
    /// asks for is the part that shows.</para>
    /// </summary>
    private void PlacePhoto()
    {
        if (!_ready || _sourceWidth <= 0 || _sourceHeight <= 0 || _scale <= 0)
            return;

        var window = CurrentWindow();
        var perPixel = PointsPerPixel(window);
        if (perPixel <= 0 || window.Width <= 0)
            return;

        // Right-aligned against the record, as the header's own placement is. The offset is only used
        // when the photo is too narrow to reach across at this zoom. Otherwise the photo fills the width,
        // and the offset is 0.
        var offset = Math.Max(0, (_shape.PictureRight * _scale) - (window.Width * perPixel));

        Photo.Width = _sourceWidth * perPixel;
        Photo.Height = _sourceHeight * perPixel;
        Photo.Margin = new Thickness(offset - (window.X * perPixel), -(window.Y * perPixel), 0, 0);

        // Only the window is shown. The whole photo is laid out and moved into place, so the parts
        // outside the window are still in the zone. Where the photo does not reach across, the zone has
        // room to show them. If they were not clipped, the preview would show more picture than the app
        // does. The preview exists to show what the app will do, so it must be clipped to the window.
        var drawnWidth = window.Width * perPixel;
        var drawnHeight = window.Height * perPixel;
        PictureClip.Rect = new Rect(offset, 0, Math.Max(0, drawnWidth), Math.Max(0, drawnHeight));

        // The fade moves with the photo, because the framing has just changed where the photo starts.
        PlaceFade(_scale, _shape.PictureHeight * _scale);
    }

    // ── dragging the picture ──

    private bool _dragging;
    private Point _dragFrom;
    private ArtFraming _dragStart;

    private void Stage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready)
            return;

        _dragging = true;
        _dragFrom = e.GetCurrentPoint(Stage).Position;
        _dragStart = _framing;
        Stage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !_ready)
            return;

        var at = e.GetCurrentPoint(Stage).Position;

        // The zoom used is the one the drag started at. So the picture keeps up with the pointer for the
        // whole drag, instead of speeding up as the drag goes on.
        var perPixel = PointsPerPixel(_dragStart.Window(_sourceWidth, _sourceHeight, _shape.PictureAspect));
        if (perPixel <= 0)
            return;

        // The picture follows the pointer, so the window moves the other way.
        var dx = (at.X - _dragFrom.X) / perPixel;
        var dy = (at.Y - _dragFrom.Y) / perPixel;

        // Accumulated from where the drag started, not from the last frame. The framing keeps the
        // overshoot past the photo's edge (Window is where that meets the edge). So dragging back covers
        // the same distance before the picture moves again.
        _framing = _dragStart.Moved(-dx / _sourceWidth, -dy / _sourceHeight);
        _placed = true;

        PlacePhoto();
        SyncReadouts();
        e.Handled = true;
    }

    private void Stage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Stage.ReleasePointerCapture(e.Pointer);

        // The overshoot is no longer needed. Put the framing back inside the photo, so the next gesture
        // (a wheel, a slider or another drag) starts from what is on screen.
        _framing = Settled();
        SyncFromFraming();
        e.Handled = true;
    }

    // ── the wheel: zoom about the pointer ──

    /// <summary>One notch of the wheel. It is small enough that the zoom looks smooth, and large enough
    /// that crossing the whole range does not take long.</summary>
    private const double WheelStep = 0.90;

    private void Stage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready)
            return;

        var point = e.GetCurrentPoint(Stage);
        var notches = point.Properties.MouseWheelDelta / 120.0;
        if (Math.Abs(notches) < 0.01)
            return;

        // Where the pointer is in the drawn picture. The zoom keeps this point still.
        var window = CurrentWindow();
        var zoneHeight = _shape.PictureHeight * _scale;
        var perPixel = PointsPerPixel(window);
        var drawnWidth = window.Width * perPixel;
        var offset = Math.Max(0, (_shape.PictureRight * _scale) - drawnWidth);

        var inZone = e.GetCurrentPoint(PictureZone).Position;
        var anchorX = drawnWidth > 0 ? (inZone.X - offset) / drawnWidth : 0.5;
        var anchorY = zoneHeight > 0 ? inZone.Y / zoneHeight : 0.5;

        _framing = Settled().Zoomed(Math.Pow(WheelStep, notches),
                                    Math.Clamp(anchorX, 0, 1), Math.Clamp(anchorY, 0, 1),
                                    _sourceWidth, _sourceHeight, _shape.PictureAspect);
        _placed = true;

        PlacePhoto();
        SyncFromFraming();
        e.Handled = true;
    }

    // ── the figures ──

    private ArtFraming Settled()
        => _framing.Settle(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    // ── the slider ends are the limits, not 0 and 100 ──
    //
    // Each of these four numbers is limited by the others and by the photo. The limits change as the
    // picture is worked on. For example, how far the window can slide across depends on its width, and
    // its width depends on the zoom and the reach. If each slider covered its full range, most of its
    // track would do nothing, and the far end would be a place the knob can be put but will not stay.
    // That would be a control that shows a wrong position. So each slider covers what can be reached now,
    // including both ends. It is set again whenever anything moves it (see SyncFromFraming).

    /// <summary>How far in this photo may be zoomed.</summary>
    private double ClosestZoom()
        => ArtFraming.MinHeightFor(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    /// <summary>How far out it may be zoomed, before the picture no longer covers the header at
    /// all.</summary>
    private double WidestZoom()
        => ArtFraming.WidestZoomFor(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    /// <summary>The zoom at which a full-width picture exactly fills the header. The readout calls this
    /// 100%, so the figure means "covers the header" and not an arbitrary scale.</summary>
    private double FillZoom()
        => ArtFraming.FillHeightFor(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    /// <summary>
    /// The zoom slider's scale. It is logarithmic, because zoom is. A step at the far-out end must cover
    /// more of the picture than the same step close in. A linear slider would use most of its travel on
    /// the last small part.
    /// </summary>
    private double ZoomToSlider(double height)
    {
        var (min, max) = (ClosestZoom(), WidestZoom());
        if (min <= 0 || max <= min)
            return 0;

        var t = Math.Log(Math.Clamp(height, min, max) / max) / Math.Log(min / max);
        return Math.Clamp(t, 0, 1) * 1000;
    }

    private double SliderToZoom(double value)
    {
        var (min, max) = (ClosestZoom(), WidestZoom());
        if (min <= 0 || max <= min)
            return max;

        return max * Math.Pow(min / max, Math.Clamp(value, 0, 1000) / 1000);
    }

    /// <summary>How much of the header the picture may cover at this zoom. This is 1 (all of it), unless
    /// the photo has run out of width.</summary>
    private double MostCover()
        => Settled().MaxCoverFor(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    private double CoverToSlider(double cover)
    {
        var span = MostCover() - ArtFraming.MinCover;
        return span <= 1e-6 ? 1000 : Math.Clamp((cover - ArtFraming.MinCover) / span, 0, 1) * 1000;
    }

    private double SliderToCover(double value)
        => ArtFraming.MinCover
         + (Math.Max(0, MostCover() - ArtFraming.MinCover) * Math.Clamp(value / 1000, 0, 1));

    /// <summary>Where the window may sit across the photo at this width, and the same going down. A
    /// window as wide (or as tall) as the picture has only one place, so the range collapses to a
    /// point.</summary>
    private (double Min, double Max) AcrossRange()
        => Settled().AcrossRange(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    private (double Min, double Max) DownRange()
        => Settled().DownRange(_sourceWidth, _sourceHeight, _shape.PictureAspect);

    private static double ToSlider(double value, (double Min, double Max) range)
    {
        var span = range.Max - range.Min;
        return span <= 1e-9 ? 500 : Math.Clamp((value - range.Min) / span, 0, 1) * 1000;
    }

    private static double FromSlider(double value, (double Min, double Max) range)
        => range.Min + ((range.Max - range.Min) * Math.Clamp(value / 1000, 0, 1));

    /// <summary>Sets the sliders and the readouts to match the framing. It does not write back to the
    /// framing, so the change does not loop.</summary>
    private void SyncFromFraming()
    {
        if (!_ready)
            return;

        var settled = Settled();

        _syncing = true;
        ZoomSlider.Value = ZoomToSlider(settled.Height);
        WidthSlider.Value = CoverToSlider(settled.Cover);
        AcrossSlider.Value = ToSlider(settled.CentreX, AcrossRange());
        DownSlider.Value = ToSlider(settled.CentreY, DownRange());
        _syncing = false;

        PlacePhoto();
        SyncReadouts();
    }

    /// <summary>The four figures, and the line that says whose placement is in use. It runs on every
    /// frame of a drag, so it only writes text.</summary>
    private void SyncReadouts()
    {
        var settled = Settled();

        // Zoom and width are shown as what they physically are. 100% zoom is a picture that covers the
        // header. 100% width is one that reaches the record. Both mean something on their own. Across and
        // down do not. "The window's middle is 56% across the photo" is a number about a file nobody has
        // seen. So those two show how far along the slider the window is, and the ends mean the far left
        // and the far right.
        var zoom = settled.Height > 0 ? FillZoom() / settled.Height : 1;
        ZoomValue.Text = Loc.Format("ArtPlace_Percent", (int)Math.Round(zoom * 100));
        WidthValue.Text = Loc.Format("ArtPlace_Percent", (int)Math.Round(Math.Clamp(settled.Cover, 0, 1) * 100));
        AcrossValue.Text = Loc.Format("ArtPlace_Percent", (int)Math.Round(ToSlider(settled.CentreX, AcrossRange()) / 10));
        DownValue.Text = Loc.Format("ArtPlace_Percent", (int)Math.Round(ToSlider(settled.CentreY, DownRange()) / 10));

        // A slider with nowhere to go is disabled. Otherwise it would do nothing when pulled.
        var across = AcrossRange();
        var down = DownRange();
        AcrossSlider.IsEnabled = _ready && across.Max - across.Min > 1e-6;
        DownSlider.IsEnabled = _ready && down.Max - down.Min > 1e-6;
        WidthSlider.IsEnabled = _ready && MostCover() > ArtFraming.MinCover + 1e-4;
        ZoomSlider.IsEnabled = _ready && WidestZoom() > ClosestZoom() + 1e-6;

        SourceText.Text = Loc.Get(_placed ? "ArtPlace_ByHand" : "ArtPlace_ByApp");
        AutoButton.IsEnabled = _ready && _placed;
    }

    private void Zoom_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        // Zoom about the middle of the header, where someone watching the slider is looking.
        var settled = Settled();
        var wanted = SliderToZoom(e.NewValue);
        _framing = settled.Zoomed(wanted / Math.Max(1e-9, settled.Height), 0.5, 0.5,
                                  _sourceWidth, _sourceHeight, _shape.PictureAspect);
        _placed = true;

        SyncFromFraming();
    }

    private void Width_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        _framing = Settled() with { Cover = SliderToCover(e.NewValue) };
        _placed = true;

        SyncFromFraming();
    }

    private void Across_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        _framing = Settled() with { CentreX = FromSlider(e.NewValue, AcrossRange()) };
        _placed = true;

        SyncFromFraming();
    }

    private void Down_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        _framing = Settled() with { CentreY = FromSlider(e.NewValue, DownRange()) };
        _placed = true;

        SyncFromFraming();
    }

    // ── the two shortcuts ──

    /// <summary>Gives the picture back to the app. It does not copy the app's numbers into the framing.
    /// The chooser's answer is worked out again each time, from the photo and the header. So a copy would
    /// be out of date as soon as either changed. The window shows the chooser's current answer. Nothing
    /// is saved for it (see <see cref="Save_Click"/>).</summary>
    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        _framing = _automatic ?? ArtFraming.Whole;
        _placed = false;
        SyncFromFraming();
    }

    /// <summary>As far out as this photo goes: all of its height in the band, and as much of its width as
    /// the header's shape can take.</summary>
    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        _framing = Settled() with { Height = FillZoom(), Cover = 1 };
        _placed = true;
        SyncFromFraming();
    }

    // ── leaving ──

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Hide();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Null when the app's placement is in use. The framing is removed, not saved as a copy of the
        // chooser's answer. So the picture is framed fresh each time.
        Result = new ArtPlacement(_placed ? Settled() : null);
        Hide();
    }
}
