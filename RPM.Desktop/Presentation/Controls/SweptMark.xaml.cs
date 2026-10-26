using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>
/// The rpm wordmark as the Android TV app opens with it (Opening.kt, OpeningSplash and SweepSurface): dimmed
/// to 0.32 white, with a band of light sweeping across it from left to right once a second, lighting the
/// letters it passes, over and over until <see cref="Stop"/>. The startup splash card shows it.
///
/// <para><b>The band.</b> 0.6 of the wordmark wide, its light falling from the middle to nothing at either
/// edge along a smootherstep, so it has no edge for the eye to find. Each pass eases in and out (quadratic),
/// from wholly before the first letter to wholly after the last. These are the TV's numbers; Pegasus's
/// opening cover draws the same band (PegasusPlayer.Opening).</para>
///
/// <para><b>Why the compositor draws it.</b> It is shown while the drawing thread is busiest: the splash
/// card is up exactly while the library is opened and the first screen is built. A band moved from that
/// thread would stand still for all of it. On the WinUI head the band's two ends are animated by the system
/// compositor and nothing is written per frame (as <see cref="Common.Spin"/> does for the record). The Skia
/// heads move them from a frame tick instead.</para>
///
/// <para><b>How the band lights only the letters.</b> XAML has no opacity mask, so a live copy of a white
/// wordmark (<c>Lit</c>) is painted through a mask brush whose mask is the band's gradient. The white
/// wordmark itself is never seen: its wrapper is clipped to nothing, and the copy does not include its
/// wrapper. A backend that cannot build the pieces shows the dimmed wordmark alone.</para>
/// </summary>
public sealed partial class SweptMark : UserControl
{
    /// <summary>One pass of the band across the wordmark (the TV's SWEEP_MS).</summary>
    public static readonly TimeSpan Pass = TimeSpan.FromMilliseconds(1000);

    /// <summary>The band's width, as a share of the wordmark's (the TV's BAND_OF_WIDTH).</summary>
    private const float BandOfWidth = 0.6f;

    private CompositionVisualSurface? _copy;
    private CompositionLinearGradientBrush? _band;
    private SpriteVisual? _sprite;
    private InsetClip? _cut;

    /// <summary>Set when the composition pieces could not be built, so they are tried once.</summary>
    private bool _noBand;

    /// <summary>When the band started its first pass. A pass ends every <see cref="Pass"/> from here.</summary>
    private readonly Stopwatch _clock = new();

    private bool _onCompositor;
    private bool _ticking;

    public SweptMark()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();

        // A frame tick left subscribed would keep the window redrawing after the mark has gone.
        Unloaded += (_, _) => Stop();
    }

    /// <summary>How long is left of the pass the band is on. Nothing when it is not sweeping.</summary>
    public TimeSpan Left
    {
        get
        {
            if (!_clock.IsRunning || _band is null)
                return TimeSpan.Zero;
            var pass = Pass.TotalMilliseconds;
            return TimeSpan.FromMilliseconds(pass - _clock.Elapsed.TotalMilliseconds % pass);
        }
    }

    /// <summary>Starts the band sweeping, if it is not already. A band already sweeping is left on its
    /// pass.</summary>
    public void Start()
    {
        Build();
        if (_band is not { } band || _clock.IsRunning)
            return;

        _clock.Restart();

#if RPM_WINUI
        try
        {
            var compositor = band.Compositor;

            // One pass, eased in and out, repeated until stopped. The curve is the quadratic in-out the TV's
            // pass follows, as the compositor's cubic bezier.
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.455f, 0.03f), new Vector2(0.515f, 0.955f));
            var start = compositor.CreateVector2KeyFrameAnimation();
            start.InsertKeyFrame(0f, new Vector2(-BandOfWidth, 0), compositor.CreateLinearEasingFunction());
            start.InsertKeyFrame(1f, new Vector2(1, 0), ease);
            start.Duration = Pass;
            start.IterationBehavior = AnimationIterationBehavior.Forever;

            // The far end rides on the near end, so the two cannot drift apart.
            var end = compositor.CreateExpressionAnimation("this.Target.StartPoint + Vector2(width, 0)");
            end.SetScalarParameter("width", BandOfWidth);

            band.StartAnimation("StartPoint", start);
            band.StartAnimation("EndPoint", end);
            _onCompositor = true;
            return;
        }
        catch
        {
            // The compositor would not take it. The frame tick below moves the band instead.
            _onCompositor = false;
        }
#endif

        if (!_ticking)
        {
            _ticking = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Stops the band and puts it back before the first letter.</summary>
    public void Stop()
    {
        _clock.Reset();

        if (_ticking)
        {
            _ticking = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
        }

#if RPM_WINUI
        if (_onCompositor && _band is { } band)
        {
            try
            {
                band.StopAnimation("EndPoint");
                band.StopAnimation("StartPoint");
            }
            catch { }
        }
#endif
        _onCompositor = false;

        Place(0);
    }

    /// <summary>One frame of the band, where the compositor does not move it.</summary>
    private void OnFrame(object? sender, object e)
    {
        var into = _clock.Elapsed.TotalMilliseconds % Pass.TotalMilliseconds / Pass.TotalMilliseconds;
        Place((float)(into < 0.5 ? 2 * into * into : 1 - 2 * (1 - into) * (1 - into)));
    }

    /// <summary>Puts the band where <paramref name="pass"/> (0 to 1, already eased) has it, in the
    /// wordmark's own width.</summary>
    private void Place(float pass)
    {
        if (_band is not { } band)
            return;
        var from = -BandOfWidth + pass * (1 + BandOfWidth);
        band.StartPoint = new Vector2(from, 0);
        band.EndPoint = new Vector2(from + BandOfWidth, 0);
    }

    /// <summary>Builds the band, the first time it is asked for.</summary>
    private void Build()
    {
        if (_sprite is not null || _noBand)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(Band).Compositor;

            // The white wordmark, as rendered, from its own top left corner and at its own size.
            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(Lit);
            copy.SourceOffset = Vector2.Zero;
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Only the band's alpha is read: 1 - smootherstep(distance from the middle), at seventeen stops as
            // on the TV. Outside the band the end stops carry on, so the rest of the wordmark is not lit.
            var band = compositor.CreateLinearGradientBrush();
            band.MappingMode = CompositionMappingMode.Relative;
            for (var i = 0; i <= 16; i++)
            {
                var at = i / 16f;
                var x = Math.Abs(2 * at - 1);
                var light = 1 - x * x * x * (x * (x * 6 - 15) + 10);
                band.ColorStops.Add(compositor.CreateColorGradientStop(
                    at, Windows.UI.Color.FromArgb((byte)(255 * light), 255, 255, 255)));
            }

            var mask = compositor.CreateMaskBrush();
            mask.Source = pixels;
            mask.Mask = band;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(Band, sprite);

            // The white wordmark is cut away where it stands. The cut is on its wrapper, which the copy does
            // not include, so the copy keeps every pixel.
            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(LitCut).Clip = cut;

            _copy = copy;
            _band = band;
            _sprite = sprite;
            _cut = cut;
            Place(0);
            Fit();
            LitCut.Visibility = Visibility.Visible;
        }
        catch
        {
            // A backend without visual surfaces or mask brushes: the dimmed wordmark alone, and the white one
            // stays collapsed.
            _noBand = true;
        }
    }

    /// <summary>Points the copy at the whole white wordmark, sizes the sprite to it and cuts the white
    /// wordmark away. A mark with no size yet hides the sprite: a surface with no pixels must never reach a
    /// mask brush (see <see cref="BandSurface"/>).</summary>
    private void Fit()
    {
        if (_copy is null || _sprite is null || _cut is null)
            return;

        var wide = (float)ActualWidth;
        var high = (float)ActualHeight;
        _cut.LeftInset = wide;
        _cut.TopInset = high;
        BandSurface.Fit(_copy, _sprite, wide, high, 0);
    }
}
