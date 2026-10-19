using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Art;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Why the frost is being taken again. This decides how long to wait for the scene to hold still
/// before it is worth photographing.</summary>
public enum FrostReason
{
    /// <summary>Something is being moved or resized: a drag, a window resize, or a page arriving. This is
    /// over in a few frames of pointer travel.</summary>
    Motion,

    /// <summary>The artwork is changing: the two-second photo dissolve, the wash moving to a new album's
    /// colour, or the drawer parking or opening.</summary>
    Artwork,
}

/// <summary>Who is holding the plate off. Each holder releases only its own hold. So a pane move that
/// finishes while Pegasus is up does not take a photograph of a backdrop that Pegasus has stopped
/// drawing.</summary>
public enum FrostHold
{
    /// <summary>A pane move is in flight (see <c>MainPage.Panes</c>).</summary>
    PaneMove,

    /// <summary>Pegasus covers the window, and the layers behind the glass are not drawn (see
    /// <c>ShellPage.Cover</c>). A photograph taken now would show nothing.</summary>
    Covered,
}

/// <summary>
/// Marks a panel as standing on the cached frost: <c>ui:Frost.Blur="30"</c> in place of a
/// <c>GlassBrush</c> of the same radius. The panel wears live glass until the plate is ready and
/// whenever the scene behind it is moving. The rest of the time it wears the plate's own slice, which is
/// almost always. See <see cref="FrostPlate"/>.
/// </summary>
public static class Frost
{
    public static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached(
        "Blur", typeof(double), typeof(Frost), new PropertyMetadata(0d, OnBlurChanged));

    public static double GetBlur(FrameworkElement element) => (double)element.GetValue(BlurProperty);

    public static void SetBlur(FrameworkElement element, double value) => element.SetValue(BlurProperty, value);

    private static void OnBlurChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is double blur)
            FrostPlate.Current.Register(element, blur);
    }
}

/// <summary>
/// The window's frost, taken once and kept.
///
/// <para>A <see cref="GlassBrush"/> is a LIVE backdrop blur. On every frame it reads back the pixels that
/// are already drawn under the panel, and it runs a Gaussian over them. A visual that carries one is
/// excluded from the renderer's picture cache. The record turns for as long as music plays, so the whole
/// tree is redrawn at the frame rate. That includes three near-fullscreen blurs at radius 30.</para>
///
/// <para>Nothing behind those panels moves. The shell draws the background picture, then the artwork, then
/// the content. The record and the seek bar are drawn ON TOP of the frosted cards. So the live blur
/// computes an identical image sixty times a second. This class takes ONE picture of the layers behind the
/// glass. It is a plate at a third of the scale, blurred at a third of the radius (<see
/// cref="FrostGeometry"/>). It cuts each panel's slice out of that picture, and gives the slice to the
/// panel as a plain <see cref="ImageBrush"/>. The panels then become still pictures, which is what the
/// subtree cache is for.</para>
///
/// <para>While the scene behind the glass IS moving (for example an artwork dissolving, the drawer being
/// dragged, or the window being resized), the panels wear their live glass again. A fresh plate is taken
/// once the scene has settled. The plate is the same blur of the same pixels, so the handover is a still
/// frame changing to an identical still frame.</para>
/// </summary>
public sealed class FrostPlate
{
    /// <summary>The main window's plate. One window has one set of layers behind its glass.</summary>
    public static FrostPlate Current { get; } = new();

    /// <summary>Everything drawn BEHIND the frosted panels: the shell's backdrop layer. This is what is
    /// photographed. The record, the seek bar and the panels themselves are drawn over it, and they must
    /// not appear in their own frost.</summary>
    private FrameworkElement? _source;

    private readonly List<Frosted> _frosted = new();

    /// <summary>One blurred plate per radius asked for. They are all 30 at present, but the brush takes a
    /// number, and the plate follows it.</summary>
    private readonly Dictionary<double, SKImage> _plates = new();

    /// <summary>The plate's own scale: what one of its pixels is worth in DIPs. It is set with the plates
    /// and read by every cut, because the picture is not always the size it was asked for.</summary>
    private FrostPlateMap _map = FrostPlateMap.Nominal(1, 1);

    private readonly DispatcherTimer _settle = new();

    /// <summary>Rises on every invalidation. A capture that started before the last invalidation carries an
    /// old number. It is dropped rather than painted.</summary>
    private int _version;

    private bool _capturing;
    private bool _showingPlate;
    private int _failures;

    /// <summary> When the frost may next be taken: the LATEST time that any pending reason has asked to
    /// wait until. It is not the last one asked.
    ///
    /// <para>The two reasons overlap constantly, and the longer wait has to win. A track that starts with
    /// the drawer open dissolves its artwork over two seconds. The strip re-lays out during that time (the
    /// drawer slides, the record grows), a hundred times or more. Each of those is a MOTION signal. If the
    /// shorter wait from the last signal were used, the plate would be taken a fifth of a second into the
    /// dissolve, and the half-faded frost would be held. So the deadline is kept at its maximum. It moves
    /// forward only for a reason that wants to wait even longer.</para>
    /// </summary>
    private long _deadline;

    /// <summary>How long the scene has to hold still after a move before the frost is taken again. A drag
    /// produces a hundred of these signals. Only the last one gets to spend a capture.</summary>
    private static readonly TimeSpan MotionSettle = TimeSpan.FromMilliseconds(220);

    /// <summary> The same, after an artwork change. The photo dissolves over two seconds. A plate taken
    /// during the dissolve would freeze it half-way. A page's ground is waited out for this long too, and
    /// that is the ARTWORK's number, on purpose.
    ///
    /// <para>A page keeps filling itself in long after its ground has landed. Shelves, sleeves, portraits
    /// and articles all load later. A plate taken too early would be a photograph of a half-built page. It
    /// would stay behind every frosted panel until something else moved. On screen that shows as ghosts:
    /// rows and tiles from a layout that no longer exists, drawn under the live one. So the wait must let
    /// the page settle. If the live glass that a page's ground costs needs cutting down, reduce how many
    /// times a page announces its ground. Do not take the picture sooner.</para>
    /// </summary>
    private static readonly TimeSpan ArtworkSettle = TimeSpan.FromMilliseconds(2400);

    /// <summary>How many captures may fail before the plate gives up and leaves every panel on live glass.
    /// A window with no GPU read-back will not get one on the next try either.</summary>
    private const int MaxFailures = 3;

    private FrostPlate() => _settle.Tick += OnSettled;

    /// <summary>Points the plate at the layers behind the glass, and takes a first picture of them.</summary>
    public void Attach(FrameworkElement source)
    {
        _source = source;
        Invalidate(FrostReason.Motion);
    }

    /// <summary> Puts a panel on the plate. Called by the <see cref="Frost"/> attached property. The panel
    /// wears live glass from here until the first plate is ready.
    /// </summary>
    public void Register(FrameworkElement element, double blur)
    {
        Drop(element);
        if (blur <= 0)
            return;

        var frosted = new Frosted(new WeakReference<FrameworkElement>(element), blur, new GlassBrush { BlurAmount = blur });
        _frosted.Add(frosted);
        SetBackground(element, frosted.Live);

        // A panel that changes shape reads a different part of the plate. A panel that has just loaded has a
        // place on the plate for the first time.
        element.SizeChanged += (_, _) => Invalidate(FrostReason.Motion);
        element.Loaded += (_, _) => Invalidate(FrostReason.Motion);
    }

    /// <summary> Something behind the glass is moving. Every panel goes back to live glass, and a new plate
    /// is taken once the scene has held still for as long as <paramref name="reason"/> asks for. Repeated
    /// calls push the capture further out. That is what a drag does.
    /// </summary>
    public void Invalidate(FrostReason reason)
    {
        if (_source is null || _failures >= MaxFailures)
            return;

        _version++;
        GoLive();

        var wait = reason == FrostReason.Artwork ? ArtworkSettle : MotionSettle;
        Wait(Environment.TickCount64 + (long)wait.TotalMilliseconds);
    }

    /// <summary>Sets the settle timer for the later of <paramref name="until"/> and the deadline that is
    /// already set. A two-second artwork dissolve is not shortened by the layout signals that fire during
    /// it.</summary>
    private void Wait(long until)
    {
        _deadline = Math.Max(_deadline, until);

        _settle.Stop();
        _settle.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _deadline - Environment.TickCount64));
        _settle.Start();
    }

    /// <summary> Holds the plate off for <paramref name="why"/>: a pane move in flight, or Pegasus covering
    /// the window. Every frosted panel wears its live glass in the meantime. No photograph is taken until
    /// every holder has released.
    ///
    /// <para><b>Why this is needed, and a panel's own SizeChanged is not enough.</b> A compositor pane move
    /// lays the cards out ONCE. It then slides one of them with a composition <c>Translation</c> (see
    /// <c>PaneGlide</c>). So there is no motion for this class to notice. The scene reads as settled 220 ms
    /// in, which is the middle of the move. Two things then go wrong at once. First, the panel is drawn
    /// somewhere its layout does not say it is. This class crops from <c>TransformToVisual</c>, and a
    /// translation is not in that result. Second, the card that gives way is deliberately laid out wider
    /// than the window. So <see cref="FrostGeometry.CropFor"/> clamps its slice to the window's edge, and
    /// <see cref="Cut"/> then stretches that short slice across the whole card. On screen, the frost jumps
    /// and stretches for the length of every pane move, and corrects itself at the end.</para>
    /// </summary>
    public void Hold(FrostHold why)
    {
        _holds.Add(why);
        Invalidate(FrostReason.Motion);   // Live glass is used now. It is a backdrop read, so it follows the card.
    }

    /// <summary>The holder is done: the move has landed, or Pegasus has gone. The settle starts from the last
    /// release. So exactly one capture is spent on whatever was held.</summary>
    public void Release(FrostHold why)
    {
        if (_holds.Remove(why) && _holds.Count == 0)
            Invalidate(FrostReason.Motion);
    }

    /// <summary>A set rather than a flag. So one holder's release cannot undo another holder's
    /// hold.</summary>
    private readonly HashSet<FrostHold> _holds = [];

    private void OnSettled(object? sender, object e)
    {
        _settle.Stop();

        // Woken early. A longer wait was asked for after this timer was started.
        if (Environment.TickCount64 < _deadline)
        {
            Wait(_deadline);
            return;
        }

        // A pane move owns the layout right now, and that layout is not the one the panels will settle at.
        // See Hold. A plate cut here would show the frost jumping mid-animation.
        if (_holds.Count > 0)
        {
            Wait(Environment.TickCount64 + (long)MotionSettle.TotalMilliseconds);
            return;
        }

        // A capture already in flight takes this turn. Wait for it, rather than dropping this one.
        // Otherwise a scene that settles while the previous plate is still being blurred keeps its live
        // glass until something else moves.
        if (_capturing)
        {
            Wait(Environment.TickCount64 + (long)MotionSettle.TotalMilliseconds);
            return;
        }

        _deadline = 0;
        _ = CaptureAsync(_version);
    }

    /// <summary>Every panel goes back to its live blur. That is the accurate but expensive version. It is
    /// used for as long as the thing being frosted is moving.</summary>
    private void GoLive()
    {
        if (!_showingPlate)
            return;
        _showingPlate = false;

        foreach (var frosted in _frosted)
            if (frosted.Element.TryGetTarget(out var element))
                SetBackground(element, frosted.Live);
    }

    /// <summary> Photographs the layers behind the glass, blurs the result off the UI thread, and hands
    /// each panel its slice. The version guards it. If anything moves while this is in flight, it
    /// supersedes this capture. The frame that comes back is then thrown away, rather than painted over a
    /// scene that it no longer describes.
    /// </summary>
    private async Task CaptureAsync(int version)
    {
        if (_capturing || _source is not { ActualWidth: > 1, ActualHeight: > 1 } source)
            return;
        if (_frosted.Count == 0)
            return;

        _capturing = true;
        try
        {
            var (plateWidth, plateHeight) = FrostGeometry.PlateSize(source.ActualWidth, source.ActualHeight);
            var sourceWidth = source.ActualWidth;
            var sourceHeight = source.ActualHeight;

            // Rendered at plate size, not window size. Reading the picture back from the GPU is the
            // expensive part of this. A third-scale plate has a ninth of the pixels to carry across.
            var shot = new RenderTargetBitmap();
            await shot.RenderAsync(source, plateWidth, plateHeight);
            if (version != _version)
                return;

            // What comes back is not always the size that was asked for, and the difference matters. The
            // plate's arithmetic (FrostGeometry) works in units of PlateDip. So every slice that is cut
            // assumes the picture is exactly the size that was asked for. Blur copies the buffer into a
            // bitmap allocated at that size. A shot that comes back bigger would write past the end of that
            // bitmap. That corrupts the heap, and the corruption shows up later at an unrelated free, with
            // no managed exception to explain it. This happened on every Windows snap. The root element
            // still has its old shape when the plate size is worked out. The window reports a disagreement
            // at that moment, and the render that follows has the new shape.
            //
            // So the shot is checked against what was asked for, and a shot that does not match is dropped.
            // Nothing is lost by dropping it. The capture was of a scene that had already changed, and the
            // settle timer takes another as soon as the window stops moving.
            if (shot.PixelWidth != plateWidth || shot.PixelHeight != plateHeight)
                RPM.Core.Diagnostics.Scope.Said(
                    "frost",
                    $"shot came back {shot.PixelWidth}x{shot.PixelHeight}, asked {plateWidth}x{plateHeight}");

            // From here on, the SHOT is the plate, not the size that was asked for. Snapping the window to
            // the side of the screen can give back a size that is one pixel different from the ask. The
            // plate carries its own scale from here (FrostPlateMap). So a picture of another size is read
            // correctly, and it is not assumed to be three DIPs a pixel.
            plateWidth = shot.PixelWidth;
            plateHeight = shot.PixelHeight;

            var buffer = await shot.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var stream = buffer.AsStream())
                stream.ReadExactly(pixels);
            if (version != _version)
                return;

            var radii = _frosted.Select(f => f.Blur).Distinct().ToArray();
            var baked = await Task.Run(() => radii.ToDictionary(
                radius => radius,
                radius => Blur(pixels, plateWidth, plateHeight, (float)FrostGeometry.SigmaFor(radius))));

            if (version != _version)
            {
                foreach (var plate in baked.Values)
                    plate.Dispose();
                return;
            }

            foreach (var plate in _plates.Values)
                plate.Dispose();
            _plates.Clear();
            foreach (var (radius, plate) in baked)
                _plates[radius] = plate;

            // The scale of the plate's pixels, worked out from the picture that was actually taken and from
            // the window it was taken of. It is kept with the plates, because Apply runs again without a
            // new capture (for example when a panel arrives, or a page comes back). It must read the plates
            // at the same scale.
            _map = FrostPlateMap.For(plateWidth, plateHeight, sourceWidth, sourceHeight);

            Apply();
            _failures = 0;
        }
        catch
        {
            // A window that cannot be read back keeps its live glass. That is correct, just expensive.
            _failures++;
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>The plate, blurred. The whole picture goes through one Gaussian. It uses the same filter,
    /// with the same sigma in the plate's own scale, as the live glass runs over the same pixels. It is not
    /// clamped, so the window's own edges fall off into nothing, exactly as they do under the live
    /// blur.</summary>
    private static SKImage Blur(byte[] pixels, int width, int height, float sigma)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var source = new SKBitmap(info);

        // Never more than the bitmap holds. The caller checks the shot's size and drops a shot that does
        // not match. So this cannot be reached today. It is here because being wrong costs a corrupted
        // heap, not just a wrong picture. The check that prevents that lives in another method.
        var count = Math.Min(pixels.Length, info.BytesSize);
        if (count < info.BytesSize)
            source.Erase(SKColors.Transparent);
        Marshal.Copy(pixels, 0, source.GetPixels(), count);

        using var surface = SKSurface.Create(info);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawBitmap(source, SKRect.Create(width, height), paint);

        return surface.Snapshot();
    }

    /// <summary>Gives every loaded panel the part of the plate that it stands on. Panels that are not up
    /// (for example a page that has been navigated away from, or a collapsed column) are left alone. They
    /// keep their live glass, and take their slice on the next capture.</summary>
    private void Apply()
    {
        if (_source is not { } source)
            return;

        var painted = false;

        for (var i = _frosted.Count - 1; i >= 0; i--)
        {
            var frosted = _frosted[i];
            if (!frosted.Element.TryGetTarget(out var element))
            {
                _frosted.RemoveAt(i);   // the page that owned it is gone
                continue;
            }

            if (!element.IsLoaded || element.ActualWidth < 1 || element.ActualHeight < 1)
                continue;
            if (!_plates.TryGetValue(frosted.Blur, out var plate))
                continue;

            try
            {
                var origin = element.TransformToVisual(source).TransformPoint(default);
                if (FrostGeometry.CropFor(origin.X, origin.Y, element.ActualWidth, element.ActualHeight, _map)
                    is not { } crop)
                    continue;

                Cut(plate, crop, frosted);
                frosted.Plate ??= new ImageBrush { Stretch = Stretch.Fill };
                frosted.Plate.ImageSource = frosted.Copy;
                SetBackground(element, frosted.Plate);
                painted = true;
            }
            catch
            {
                // One panel that cannot be cut (it left the tree between the check and the transform)
                // simply keeps its live glass.
            }
        }

        _showingPlate |= painted;
    }

    /// <summary>Cuts one panel's slice out of the plate into its own bitmap. The last bitmap is reused when
    /// it is still the right size. A re-cut on the same geometry is then a copy, not a new
    /// allocation.</summary>
    private static void Cut(SKImage plate, FrostCrop crop, Frosted frosted)
    {
        if (frosted.Copy is not { } copy || copy.PixelWidth != crop.PixelWidth || copy.PixelHeight != crop.PixelHeight)
            frosted.Copy = copy = new WriteableBitmap(crop.PixelWidth, crop.PixelHeight);

        var info = new SKImageInfo(crop.PixelWidth, crop.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var slice = new SKBitmap(info);
        using (var canvas = new SKCanvas(slice))
        {
            canvas.Clear(SKColors.Transparent);
            // Sampled, not snapped. The source rectangle keeps the panel's true place on the plate, to a
            // fraction of a plate pixel (three DIPs). Rounding it would slide the frost under the panel by
            // a pixel and a half each time its geometry changed.
            canvas.DrawImage(plate,
                             SKRect.Create(crop.X, crop.Y, crop.Width, crop.Height),
                             SKRect.Create(crop.PixelWidth, crop.PixelHeight),
                             new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                             paint: null);
        }

        var bytes = new byte[info.BytesSize];
        Marshal.Copy(slice.GetPixels(), bytes, 0, bytes.Length);

        using var stream = copy.PixelBuffer.AsStream();
        stream.Write(bytes, 0, bytes.Length);
        copy.Invalidate();
    }

    /// <summary>Paints a panel's background, whatever kind of panel it is. The three cards are Borders. The
    /// property is open to anything with a background, so that a future card is not a special
    /// case.</summary>
    private static void SetBackground(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case Border border: border.Background = brush; break;
            case Panel panel: panel.Background = brush; break;
            case Control control: control.Background = brush; break;
            case ContentPresenter presenter: presenter.Background = brush; break;
        }
    }

    private void Drop(FrameworkElement element)
    {
        for (var i = _frosted.Count - 1; i >= 0; i--)
            if (!_frosted[i].Element.TryGetTarget(out var held) || ReferenceEquals(held, element))
                _frosted.RemoveAt(i);
    }

    /// <summary>One panel on the plate: the radius it is frosted at, the live glass it falls back to, and
    /// the slice it is wearing now.</summary>
    private sealed class Frosted(WeakReference<FrameworkElement> element, double blur, GlassBrush live)
    {
        public WeakReference<FrameworkElement> Element { get; } = element;
        public double Blur { get; } = blur;
        public GlassBrush Live { get; } = live;
        public WriteableBitmap? Copy { get; set; }
        public ImageBrush? Plate { get; set; }
    }
}
