#if DEBUG
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Dev;

/// <summary>Places every genre's photograph in its own head by hand, one shelf at a time.
///
/// <para><b>It is the genre page's head, at another size.</b> The band's height is the page's own
/// <c>BandShare</c> of the width. The colour's reach is <c>ScrimReach</c>. The name sits at
/// <c>NameEdge</c>. Every brush (the ground, the fall over the name, the drop, the hearth, and the
/// lettering) comes from <see cref="GenreWordmarks"/>. That is the one door the page itself goes through.
/// So there is nothing to interpret between what is arranged here and what appears on the page.
/// <see cref="PositionArtworkDialog"/>, which places an artist in the now-playing header, works the same
/// way. This page was written from it.</para>
///
/// <para><b>What is being chosen is a CUT, and the head shows less of it than there is.</b> The generator
/// writes a strip six times as wide as it is high. The head is that shape only on a narrow pane. On an
/// ordinary pane it is about seven and a half to one. So the head takes a little off the top and the foot.
/// Both are on screen: the head at a page width that can be changed, and under it the whole cut, with the
/// part that this width does not show shaded off. Placing against the head alone would mean placing a
/// subject that a wider monitor quietly crops.</para>
///
/// <para><b>It edits the repository, never the app.</b> Nothing here writes an asset. See
/// <see cref="GenrePhotoBook"/>. The numbers go into <c>scripts/genre-photos.json</c>, and
/// <c>gen_genre_photos.py</c> cuts with them. So the generator stays the only author of
/// <c>Assets/GenrePhotos</c>.</para>
///
/// <para>DEBUG only. It is opened with <c>--place-genres</c>, in a window of its own beside the app.</para>
/// </summary>
public sealed partial class GenrePlacerPage : Page
{
    private readonly GenrePhotoBook? _book;
    private GenrePhotoEntry? _shelf;

    /// <summary>The frame as it stands, UNSETTLED. A drag that has run off the edge of the photograph keeps
    /// its overshoot here. So dragging back comes back after the same distance, instead of moving at once.
    /// Everything shown and everything saved goes through <see cref="Settled"/>.</summary>
    private GenreFrame _frame = new(0.5, 0.5, 1);

    /// <summary>The decoded source, held so that the two pictures (the head and the cut strip) come from
    /// one decode. A shelf that is flipped back to is shown at once.</summary>
    private readonly Dictionary<long, (ImageSource Bitmap, int Width, int Height)> _decoded = new();

    /// <summary>True while the sliders and the picture are being brought into agreement. This stops the one
    /// being written from turning round and rewriting the other.</summary>
    private bool _syncing;

    /// <summary>Set once the photograph is up. Nothing may be dragged, zoomed or saved before
    /// then.</summary>
    private bool _ready;

    private CancellationTokenSource? _loading;

    /// <summary>How many long-edge pixels the preview decodes to. The head model is at most a couple of
    /// thousand points across, and a source is 900 to 2000 pixels. This is generous either way. It also
    /// keeps a session over thirty-six photographs from holding a few hundred megabytes.</summary>
    private const int PreviewDecodeEdge = 1600;

    public GenrePlacerPage()
    {
        InitializeComponent();

        _book = Open();
        PageSlider.Value = 1440;
        PageValue.Text = $"{PageWidth:0}";

        if (_book is null || _book.Photos.Count == 0)
        {
            Fail(_book is null
                ? "No repository above this build: scripts/genre-photos.json is not there to edit."
                : "scripts/genre-photos.json names no photographs.");
            ShelfPicker.IsEnabled = PrevButton.IsEnabled = NextButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            return;
        }

        ShelfPicker.ItemsSource = _book.Photos.Select(p => p.Genre).ToList();
        SaveNote.Text = _book.Path;
        ShelfPicker.SelectedIndex = 0;
    }

    private static GenrePhotoBook? Open()
    {
        try
        {
            return GenrePhotoBook.Open();
        }
        catch
        {
            return null;   // Unreadable or half-edited JSON shows a message. It does not throw.
        }
    }

    // ══ which shelf ══

    private void Shelf_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_book is null || ShelfPicker.SelectedIndex < 0)
            return;

        Keep();
        _shelf = _book.Photos[ShelfPicker.SelectedIndex];
        _ = OpenShelfAsync(_shelf);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int by)
    {
        if (_book is null || _book.Photos.Count == 0)
            return;

        var count = _book.Photos.Count;
        ShelfPicker.SelectedIndex = ((ShelfPicker.SelectedIndex + by) % count + count) % count;
    }

    /// <summary>Hands what is on screen back to the shelf that is being left. The book holds the session's
    /// work, and the save writes all of it at once. So stepping through thirty-six shelves and saving at
    /// the end is one file write, not thirty-six.</summary>
    private void Keep()
    {
        if (_ready && _shelf is { } shelf)
            shelf.Frame = Settled();
    }

    /// <summary>What is on screen was chosen, not merely shown. This is called by the drag, the wheel and
    /// the three sliders, and by nothing else. See <see cref="GenrePhotoEntry.Placed"/>, which explains why
    /// a shelf must not be placed just by being looked at.</summary>
    private void Placed()
    {
        if (!_ready || _shelf is not { } shelf)
            return;

        shelf.Placed = true;
        Keep();
    }

    private async Task OpenShelfAsync(GenrePhotoEntry shelf)
    {
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;

        _ready = false;
        ShelfName.Text = shelf.Genre;
        ShelfNote.Text = shelf.Node["alt"]?.GetValue<string>() ?? "";
        Count();
        Enable(false);

        // The head's colour, name and falls are known as soon as the shelf is. No picture is involved, so
        // they go up now, not after a download.
        Dress(shelf.Genre);

        if (!_decoded.ContainsKey(shelf.Id))
        {
            StatusText.Text = $"Reading {shelf.Genre}…";
            StatusText.Visibility = Visibility.Visible;
            Model.Visibility = Visibility.Collapsed;

            try
            {
                // The page reported the missing book on load. There is nothing to read from.
                if (_book is null)
                    return;

                if (!(await _book.SourceAsync(shelf, ct)).Answers(out var source, out var why))
                {
                    if (!ct.IsCancellationRequested)
                        Fail($"{shelf.Genre}: it is not in scripts/.photocache and the pinned source "
                           + $"could not be fetched ({why}).");
                    return;
                }

                if (source is not { Length: > 0 } bytes)
                {
                    if (!ct.IsCancellationRequested)
                        Fail($"No photograph for {shelf.Genre}: it is not in scripts/.photocache and "
                           + "the pinned source has no picture at that address.");
                    return;
                }

                var (width, height) = Services.Art.ArtScaler.BoundsOf(bytes);
                if (width < 8 || height < 8 || ct.IsCancellationRequested)
                {
                    if (!ct.IsCancellationRequested)
                        Fail($"{shelf.Genre}: that file is not a picture this can open.");
                    return;
                }

                _decoded[shelf.Id] =
                    (await RemoteImage.DecodeAsync(bytes, PreviewDecodeEdge), width, height);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail($"{shelf.Genre}: {ex.Message}");
                return;
            }
        }

        if (ct.IsCancellationRequested || _shelf != shelf)
            return;

        var (bitmap, sourceWidth, sourceHeight) = _decoded[shelf.Id];
        shelf.SourceWidth = sourceWidth;
        shelf.SourceHeight = sourceHeight;
        Photo.Source = bitmap;
        CutPhoto.Source = bitmap;

        // Whatever is in force: a frame somebody placed, else the frame in the file, else the one that the
        // app's own `focus` produces. Both of the last two reproduce what is shipped exactly. So the first
        // thing on screen is what is already on screen, and there is no jump on opening.
        _frame = shelf.Frame
              ?? shelf.Filed
              ?? GenreBand.FromFocus(shelf.Focus, sourceWidth, sourceHeight);

        _syncing = true;
        FlipBox.IsChecked = shelf.Flip;
        _syncing = false;

        _ready = true;
        StatusText.Visibility = Visibility.Collapsed;
        Model.Visibility = Visibility.Visible;
        Enable(true);

        LayoutModel();
        SyncFromFrame();
        Count();
    }

    private void Fail(string why)
    {
        _ready = false;
        StatusText.Text = why;
        StatusText.Visibility = Visibility.Visible;
        Model.Visibility = Visibility.Collapsed;
        Enable(false);
    }

    private void Enable(bool on)
    {
        ZoomSlider.IsEnabled = on;
        AcrossSlider.IsEnabled = on;
        DownSlider.IsEnabled = on;
        ResetButton.IsEnabled = on;
        WidestButton.IsEnabled = on;
        FlipBox.IsEnabled = on;
    }

    private void Count()
    {
        if (_book is null)
            return;

        var placed = _book.Photos.Count(p => p.Placed);
        var at = ShelfPicker.SelectedIndex + 1;
        ShelfCount.Text = $"{at} of {_book.Photos.Count}  ·  {placed} placed";
    }

    // ══ the head the model is built from ══
    //
    // The page's own numbers. Each is a constant on GenreDetailView, not a measurement taken off a live
    // element. A model measured off the real head would need the real head on screen. This window opens
    // without the app ever having shown a genre.

    /// <summary>How wide the genre page is imagined to be, in points. This is not part of the placement. It
    /// sets the head's SHAPE, and the shape decides how much of the six-to-one cut a reader ever
    /// sees.</summary>
    private double PageWidth => Math.Max(400, PageSlider.Value);

    private double BandHeight => GenreDetailView.BandHeight;

    /// <summary>The page under the head, so its foot has something to dissolve into. The cut strip sits
    /// under that. Both are in head points, and they scale with everything else.</summary>
    private const double PageStripHeight = 64;

    private const double CutStripGap = 18;

    private double CutStripHeight => Math.Round(PageWidth / GenreBand.Shape);

    private double ModelWidth => PageWidth;

    private double ModelHeight => BandHeight + PageStripHeight + CutStripGap + CutStripHeight;

    /// <summary>Room under the model for the line that says what the strip is. These are screen points, not
    /// model points. The caption is set at a reading size, whatever the model is scaled to. So it is the
    /// one thing down there that does not shrink with everything else.</summary>
    private const double CaptionRoom = 22;

    /// <summary>Points of the model per point of the real page. Everything below is one of the page's own
    /// measurements times this, and nothing else. That is what makes the model a model.</summary>
    private double _scale = 1;

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutModel();
        PlacePhoto();
    }

    private void Page_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        PageValue.Text = $"{PageWidth:0}";
        LayoutModel();
        PlacePhoto();
    }

    /// <summary>The genre's colour, its two falls and its name. Every one comes from
    /// <see cref="GenreWordmarks"/>. So the model's head and the page's head cannot drift apart in two
    /// files.</summary>
    private GenreWordmark? _mark;

    private void Dress(string genre)
    {
        _mark = GenreWordmarks.For(genre);
        if (_mark is not { } mark)
        {
            BandGround.Fill = BandScrim.Fill = BandDrop.Fill = BandFinish.Fill = null;
            TitleMark.Source = null;
            SubtitleBar.Fill = null;
            return;
        }

        BandDrop.Fill = GenreWordmarks.Drop(mark);
        BandFinish.Fill = GenreWordmarks.Hearth(mark);

        // What fills a gap on the left: the tone the ground already holds under the picture. The picture's
        // leading edge is designed to meet that tone. See GenreWordmarks.Head, where that agreement is
        // argued, and GenreBand.MaxGap, which is why there can be a gap at all.
        var pad = new SolidColorBrush(GenreWordmarks.Deep(mark));
        PadPlate.Fill = pad;
        CutPad.Fill = pad;

        var ink = GenreWordmarks.InkOn(mark);
        SubtitleBar.Fill = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xB4, ink.R, ink.G, ink.B));

        LayoutModel();
    }

    private void LayoutModel()
    {
        var room = Stage.ActualSize;
        if (room.X <= 1 || room.Y <= 1)
            return;

        _scale = Math.Min(room.X / ModelWidth, (room.Y - CaptionRoom) / ModelHeight);
        if (!double.IsFinite(_scale) || _scale <= 0)
            return;

        var k = _scale;
        var modelW = ModelWidth * k;
        var modelH = ModelHeight * k;
        var band = BandHeight * k;

        Model.Width = modelW;
        Model.Height = modelH;

        // The clip is the MODEL's. It keeps the head's own layers inside the band. The caption under the
        // strip is not part of the model. When the clip used the model's own height, it cut the caption in
        // half, and at a small scale the caption disappeared entirely.
        ModelClip.Rect = new Rect(0, 0, modelW, modelH + CaptionRoom);

        Put(PageStrip, 0, band, modelW, Math.Max(1, PageStripHeight * k));
        Put(BandGround, 0, 0, modelW, band);
        Put(PictureZone, 0, 0, modelW, band);

        // The colour's reach is a fixed number of POINTS, never a share of the band. What it has to carry
        // is the name, and the name is a fixed width too. It is held inside the band, as the page holds it.
        var scrim = Math.Round(Math.Min(GenreDetailView.ScrimReach, PageWidth));
        Put(BandScrim, 0, 0, Math.Max(1, scrim * k), band);
        Put(BandDrop, 0, 0, modelW, band);
        Put(BandFinish, 0, 0, modelW, band);

        if (_mark is { } mark)
        {
            // Where the ground's ramp finishes, and where the fall over the name stops being solid. Both
            // are read off the band's width, exactly as PaintHead reads them. So a shelf with no photograph
            // wears one continuous fall, not a ramp and then a step.
            var turn = PageWidth > 1 ? Math.Min(scrim / PageWidth, 1) : 1;
            BandGround.Fill = GenreWordmarks.Head(mark, turn);
            BandScrim.Fill = GenreWordmarks.OverTheBand(
                mark, scrim > 1 ? GenreDetailView.NameEdge / scrim : 1);
        }

        // Half the band dissolves. This is the page's own FadeShare.
        var depth = Math.Round(band * 0.5);
        Put(FootFade, 0, band - depth, modelW, depth);

        // The corner: 28 in, 28 down, a 32 x 28 key, then the same 28 again, then the lettering.
        Put(BackKey, 28 * k, 28 * k, 32 * k, 28 * k);
        LayoutName(k);

        Put(PlateEdge, 0, 0, modelW, band);
        LayoutCut(k, band);
    }

    /// <summary>How much of the band the name may take, and how tall it is set. These are the page's own
    /// two numbers. They are not constants on GenreDetailView that this code can reach, so they are written
    /// again here and nowhere else. A model whose name is a different size would lie about how much of the
    /// picture the word covers.</summary>
    private const double NameRoom = 330;

    private const double NameHeight = 42;

    private void LayoutName(double k)
    {
        if (_mark is not { } mark)
        {
            TitleMark.Source = null;
            Put(SubtitleBar, 0, 0, 0, 0);
            return;
        }

        var width = Math.Round(Math.Min(NameRoom, NameHeight * mark.Aspect));
        var tall = width / mark.Aspect;
        var left = 28 + 32 + 28;
        var scale = XamlRoot?.RasterizationScale ?? 1;

        // The name is rastered at the size it is SHOWN at, as the page rasters it. A drawing scaled after
        // the fact is a name at the wrong resolution. This window is where the weight of the name over the
        // picture is judged, so it must be exact.
        TitleMark.Source = GenreWordmarks.Lettering(
            mark, GenreFlush.Left,
            (int)Math.Round(width * k * scale), (int)Math.Round(tall * k * scale));
        Put(TitleMark, left * k, 28 * k, width * k, tall * k);

        // The count under the name, as a bar at the height the real line is set at.
        Put(SubtitleBar, left * k, (28 + tall + 5) * k, Math.Min(150, width) * k, Math.Max(1, 8 * k));
    }

    /// <summary>The cut that is shipped, under the head and at the same width. The part that this page
    /// width does not show is shaded off.
    ///
    /// <para>The head is wider than six to one on nearly every window. So <c>UniformToFill</c> takes a band
    /// out of the middle of the cut. The rest is height held in hand for narrower panes. That is worth
    /// keeping, and it is not free. A subject placed against the head alone can sit in the part that a wide
    /// monitor crops. Two rectangles show this.</para>
    /// </summary>
    private void LayoutCut(double k, double band)
    {
        var top = band + (PageStripHeight * k) + (CutStripGap * k);
        var width = ModelWidth * k;
        var height = CutStripHeight * k;

        Put(CutZone, 0, top, width, height);
        CutClip.Rect = new Rect(0, 0, width, height);
        Put(CutEdge, 0, top, width, height);

        // What the head shows of the cut: the band's own height, at the cut's scale, taken from the middle.
        var shown = Math.Min(height, band);
        var hidden = Math.Max(0, (height - shown) / 2);
        Put(CutTop, 0, top, width, hidden);
        Put(CutFoot, 0, top + height - hidden, width, hidden);

        Canvas.SetLeft(CutLabel, 0);
        Canvas.SetTop(CutLabel, top + height + 4);
        CutLabel.Text = height > 0
            ? $"the 6:1 cut that is shipped — the head at {PageWidth:0} points shows "
              + $"{Math.Round(shown / height * 100):0}% of its height"
            : "";

        PlaceCut(k);
    }

    private static void Put(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    // ══ where the photograph lands ══

    private int SourceWidth => _shelf?.SourceWidth ?? 0;

    private int SourceHeight => _shelf?.SourceHeight ?? 0;

    private GenreFrame Settled() => GenreBand.Settled(_frame, SourceWidth, SourceHeight);

    private GenreWindow CurrentWindow() => GenreBand.Window(_frame, SourceWidth, SourceHeight);

    /// <summary>Stands the photograph in the band.
    ///
    /// <para><b>Why this reproduces the head exactly.</b> On the page, the shipped JPEG is a six-to-one cut
    /// laid across the band with <c>UniformToFill</c>. That scales it to cover, and takes the overflow off
    /// the edges. A cut centred on the frame and then centre-cropped is the SAME box as the frame's own
    /// window, scaled to cover the band. It has the same centre and the same width. So the whole source is
    /// laid out at that one scale, and the band clips it. This is one transform, with no re-decode. A drag
    /// is then a repaint, not a re-layout.</para>
    ///
    /// <para>It is placed by layout and a margin, not by a render transform. That is how the app's own
    /// backdrop layer is placed. A preview with a different mechanism from the thing it previews can
    /// disagree with it.</para>
    /// </summary>
    private void PlacePhoto()
    {
        if (!_ready || SourceWidth <= 0 || SourceHeight <= 0 || _scale <= 0)
            return;

        var window = CurrentWindow();
        if (window.Width <= 0 || window.Height <= 0)
            return;

        var band = BandHeight * _scale;
        var width = ModelWidth * _scale;

        // Cover, both ways round. A head wider than the cut is held by its width, and a narrower one by its
        // height. That is exactly what UniformToFill does with the shipped strip.
        var perPixel = Math.Max(width / window.Width, band / window.Height);

        Photo.Width = SourceWidth * perPixel;
        Photo.Height = SourceHeight * perPixel;
        Photo.Margin = new Thickness(
            (width / 2) - ((window.X + (window.Width / 2)) * perPixel),
            (band / 2) - ((window.Y + (window.Height / 2)) * perPixel),
            0, 0);
        Mirror(PhotoFlip, Photo);

        PictureClip.Rect = new Rect(0, 0, Math.Max(0, width), Math.Max(0, band));

        PlaceCut(_scale);
    }

    /// <summary>The same window, in the strip below. This is the whole cut: no cover and no crop. The
    /// window's own six-to-one box fills the strip from end to end.</summary>
    private void PlaceCut(double k)
    {
        if (!_ready || SourceWidth <= 0 || SourceHeight <= 0 || k <= 0)
            return;

        var window = CurrentWindow();
        if (window.Width <= 0)
            return;

        var width = ModelWidth * k;
        var perPixel = width / window.Width;

        CutPhoto.Width = SourceWidth * perPixel;
        CutPhoto.Height = SourceHeight * perPixel;
        CutPhoto.Margin = new Thickness(-(window.X * perPixel), -(window.Y * perPixel), 0, 0);
        Mirror(CutFlip, CutPhoto);
    }

    /// <summary>Turns one of the two pictures round, or leaves it as it is. The turn is about the MIDDLE,
    /// not the origin. A mirror about the origin would take the picture off its own margin, and the window
    /// would then look at nothing.</summary>
    private void Mirror(ScaleTransform flip, FrameworkElement picture)
    {
        flip.ScaleX = _shelf?.Flip == true ? -1 : 1;
        flip.CenterX = picture.Width / 2;
    }

    // ══ dragging the picture ══

    private bool _dragging;
    private Point _dragFrom;
    private GenreFrame _dragStart;

    private void Stage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready)
            return;

        _dragging = true;
        _dragFrom = e.GetCurrentPoint(Stage).Position;
        _dragStart = _frame;
        Stage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !_ready)
            return;

        var at = e.GetCurrentPoint(Stage).Position;

        // The drag is measured at the zoom it STARTED at. So the picture keeps up with the pointer for the
        // whole gesture, and does not speed up as it is dragged.
        var was = GenreBand.Window(_dragStart, SourceWidth, SourceHeight);
        var band = BandHeight * _scale;
        var width = ModelWidth * _scale;
        var perPixel = was.Height <= 0 ? 0 : Math.Max(width / was.Width, band / was.Height);
        if (perPixel <= 0)
            return;

        // The picture follows the pointer, so the WINDOW moves the other way. The move is accumulated from
        // where the drag started, not from the last frame. The frame keeps whatever overshoot the drag has
        // run up past the picture's edge. So dragging back travels the same distance before it moves.
        _frame = _dragStart.Moved(
            -((at.X - _dragFrom.X) / perPixel) / SourceWidth,
            -((at.Y - _dragFrom.Y) / perPixel) / SourceHeight);

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

        // The overshoot has done its job. The frame goes back inside the picture, so the next gesture (a
        // wheel, a slider, or another drag) starts from what is actually on screen.
        _frame = Settled();
        Placed();
        SyncFromFrame();
        Count();
        e.Handled = true;
    }

    // ══ the wheel: zoom about the pointer ══

    /// <summary>One notch. It is small enough that a zoom looks continuous, and large enough that crossing
    /// the whole range does not take a minute.</summary>
    private const double WheelStep = 0.90;

    private void Stage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready)
            return;

        var notches = e.GetCurrentPoint(Stage).Properties.MouseWheelDelta / 120.0;
        if (Math.Abs(notches) < 0.01)
            return;

        // Where in the drawn picture the pointer is. That is the point the zoom holds still. It is measured
        // against the BAND, since the band is the picture being looked at.
        var band = Math.Max(1, BandHeight * _scale);
        var width = Math.Max(1, ModelWidth * _scale);
        var window = CurrentWindow();
        var perPixel = Math.Max(width / window.Width, band / window.Height);

        var inZone = e.GetCurrentPoint(PictureZone).Position;
        var drawnWidth = window.Width * perPixel;
        var drawnHeight = window.Height * perPixel;

        _frame = GenreBand.Zoomed(
            Settled(), Math.Pow(WheelStep, notches),
            drawnWidth > 0 ? (inZone.X + ((drawnWidth - width) / 2)) / drawnWidth : 0.5,
            drawnHeight > 0 ? (inZone.Y + ((drawnHeight - band) / 2)) / drawnHeight : 0.5,
            SourceWidth, SourceHeight);

        Placed();
        PlacePhoto();
        SyncFromFrame();
        Count();
        e.Handled = true;
    }

    // ══ the sliders' ends are the LIMITS, not 0 and 100 ══
    //
    // Each of these three numbers is bounded by the others and by the photograph. The bounds move as the
    // picture is worked on. How far across the window can slide depends on how wide it is, and that depends
    // on the zoom. If the sliders ran over their absolute ranges, most of each track would do nothing. Its
    // far end would be a place the knob can be put, but it would not stay there. A control that lies about
    // where it is is a bad control. So each slider runs over what is reachable NOW, and it is re-mapped
    // whenever anything moves it.

    private double ZoomToSlider(double height)
    {
        var (min, max) = (GenreBand.ClosestFor(SourceWidth, SourceHeight),
                          GenreBand.FurthestFor(SourceWidth, SourceHeight));
        if (min <= 0 || max <= min)
            return 0;

        // Logarithmic, because zoom is. A step at the far-out end covers far more of the picture than the
        // same step close in. A linear track would spend nine tenths of its travel on the last bit.
        return Math.Clamp(Math.Log(Math.Clamp(height, min, max) / max) / Math.Log(min / max), 0, 1) * 1000;
    }

    private double SliderToZoom(double value)
    {
        var (min, max) = (GenreBand.ClosestFor(SourceWidth, SourceHeight),
                          GenreBand.FurthestFor(SourceWidth, SourceHeight));
        return min <= 0 || max <= min ? max : max * Math.Pow(min / max, Math.Clamp(value, 0, 1000) / 1000);
    }

    /// <summary>Where the window may sit across the photograph at this width, and the same down it. A
    /// window as wide (or as tall) as the picture collapses these to a point, and the slider says so rather
    /// than sitting there doing nothing when it is pulled.</summary>
    private (double Min, double Max) AcrossRange()
    {
        // The far LEFT end is not the picture's edge. The window may hang off it by
        // GenreBand.MaxGap of its own width, and the slider has to reach where a drag can go. The right end
        // is the edge, because nothing covers that side of the band.
        var window = CurrentWindow();
        var half = window.Width / (2.0 * Math.Max(1, SourceWidth));
        var min = half - (GenreBand.MaxGap * window.Width / Math.Max(1, SourceWidth));
        var max = 1 - half;
        return min >= max ? (max, max) : (min, max);
    }

    private (double Min, double Max) DownRange()
    {
        var half = CurrentWindow().Height / (2.0 * Math.Max(1, SourceHeight));
        return half >= 0.5 ? (0.5, 0.5) : (half, 1 - half);
    }

    /// <summary>Where a frame's number sits on its track. The track is INVERTED, so the two tracks read as
    /// the picture, not as the window.
    ///
    /// <para>Across and down are properties of the WINDOW. A window moved right shows a part of the
    /// photograph further right. That puts the picture further LEFT in the band. Laid out the plain way
    /// round, dragging the picture right would move the slider left. The two controls for one thing would
    /// then disagree in front of somebody using both. So the track is turned over once, here. Everything
    /// above goes on speaking in window numbers.</para>
    /// </summary>
    private static double ToSlider(double value, (double Min, double Max) range)
    {
        var span = range.Max - range.Min;
        return span <= 1e-9 ? 500 : (1 - Math.Clamp((value - range.Min) / span, 0, 1)) * 1000;
    }

    private static double FromSlider(double value, (double Min, double Max) range)
        => range.Max - ((range.Max - range.Min) * Math.Clamp(value / 1000, 0, 1));

    private void SyncFromFrame()
    {
        if (!_ready)
            return;

        var settled = Settled();

        _syncing = true;
        ZoomSlider.Value = ZoomToSlider(settled.Height);
        AcrossSlider.Value = ToSlider(settled.Across, AcrossRange());
        DownSlider.Value = ToSlider(settled.Down, DownRange());
        _syncing = false;

        PlacePhoto();
        SyncReadouts();
    }

    /// <summary>The figures. This is called on every frame of a drag, so it does nothing but write
    /// text.</summary>
    private void SyncReadouts()
    {
        var settled = Settled();
        PageValue.Text = $"{PageWidth:0}";

        // Zoom reads as what it physically IS. A hundred per cent is the photograph exactly filling the
        // band, which is where the app already stands. That means something on its own. Under a hundred is
        // the picture standing smaller at the end of the band, with the genre's colour carrying what it no
        // longer covers. Across and down do not work that way. "The window's middle is 56% across the file"
        // is a number about a picture nobody has seen. What matters there is how far along the travel the
        // PICTURE sits. So those two read the slider, whose ends are where the picture will go and no
        // further.
        var widest = GenreBand.WidestFor(SourceWidth, SourceHeight);
        ZoomValue.Text = settled.Height > 0
            ? $"{Math.Round(widest / settled.Height * 100):0}%"
            : "";
        AcrossValue.Text = $"{Math.Round(ToSlider(settled.Across, AcrossRange()) / 10):0}%";

        // A gap is worth saying in the band's own terms, as how far in the photograph starts. What decides
        // whether it shows is where the colour lets go, and that is a number of POINTS.
        var gap = CurrentWindow().Gap;
        GapText.Text = gap <= 0.5
            ? ""
            : $"picture starts {Math.Round(gap / Math.Max(1, CurrentWindow().Width) * PageWidth):0} "
              + $"points in; the colour is solid to {GenreDetailView.NameEdge:0} and gone by "
              + $"{Math.Min(GenreDetailView.ScrimReach, PageWidth):0}";
        DownValue.Text = $"{Math.Round(ToSlider(settled.Down, DownRange()) / 10):0}%";

        var across = AcrossRange();
        var down = DownRange();
        AcrossSlider.IsEnabled = _ready && across.Max - across.Min > 1e-6;
        DownSlider.IsEnabled = _ready && down.Max - down.Min > 1e-6;
        ZoomSlider.IsEnabled = _ready
            && GenreBand.FurthestFor(SourceWidth, SourceHeight)
             > GenreBand.ClosestFor(SourceWidth, SourceHeight) + 1e-6;
    }

    private void Zoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        // About the middle, which is what a slider means. A slider has no pointer to hold a pixel under.
        _frame = GenreBand.Zoomed(Settled(), SliderToZoom(e.NewValue) / Settled().Height,
                                  0.5, 0.5, SourceWidth, SourceHeight);
        Placed();
        SyncFromFrame();
        Count();
    }

    private void Across_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        _frame = Settled() with { Across = FromSlider(e.NewValue, AcrossRange()) };
        Placed();
        SyncFromFrame();
        Count();
    }

    private void Down_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready)
            return;

        _frame = Settled() with { Down = FromSlider(e.NewValue, DownRange()) };
        Placed();
        SyncFromFrame();
        Count();
    }

    // ══ the two ways back ══

    /// <summary>Turns the photograph round.
    ///
    /// <para>It is applied to the SOURCE, before the window. So across and down keep their meaning on the
    /// picture as it is seen. In the model that is a scale of minus one about the picture's own middle. In
    /// the generator it is a mirror before the crop. Both are the same statement, and the window is not
    /// changed by it. That is why flipping does not move the subject out of frame.</para>
    /// </summary>
    private void Flip_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_ready || _shelf is not { } shelf)
            return;

        shelf.Flip = FlipBox.IsChecked == true;
        PlacePhoto();
        Count();
    }

    /// <summary>Back to the app's own answer for this shelf: the frame that its <c>focus</c> produces. The
    /// shelf then carries no hand placement at all. The save takes its frame out and writes the focus back.
    /// That is how a shelf placed by mistake is un-placed, rather than pinned to a copy of what the
    /// generator would have done anyway.</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _shelf is not { } shelf)
            return;

        _frame = GenreBand.FromFocus(shelf.Focus, SourceWidth, SourceHeight);
        shelf.Frame = _frame;
        shelf.Placed = false;
        SyncFromFrame();
        Count();
    }

    /// <summary>The picture exactly filling the band: a hundred per cent, and where the cut stands before
    /// anybody touches it. This is not the end of the zoom in either direction. The slider goes in from
    /// here, and out past here to a picture standing smaller at the end of a coloured band.</summary>
    private void Widest_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        _frame = GenreBand.Settled(
            Settled() with { Height = GenreBand.WidestFor(SourceWidth, SourceHeight) },
            SourceWidth, SourceHeight);
        Placed();
        SyncFromFrame();
        Count();
    }

    // ══ the save ══

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null)
            return;

        Keep();

        try
        {
            var moved = _book.Save();
            SaveNote.Text = moved == 0
                ? $"Nothing moved. {_book.Path}"
                : $"{moved} shelf{(moved == 1 ? "" : "s")} written. "
                  + "Run python3 scripts/gen_genre_photos.py to cut them.";
        }
        catch (Exception ex)
        {
            SaveNote.Text = $"Could not write it: {ex.Message}";
        }

        Count();
    }
}
#endif
