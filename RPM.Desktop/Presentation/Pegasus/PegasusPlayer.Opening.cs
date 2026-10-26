using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The opening: what the cover over the view shows while the view stands up (see <see cref="BeginLoading"/>), and
/// the sound it hands over with. Ported from the Android TV app's opening screen (Opening.kt, OpeningSplash and
/// OpeningSound), which is the spec for this view.
///
/// <para><b>The look.</b> The rpm wordmark on black, dimmed to 0.32, with a band of light sweeping across it from
/// left to right once a second, lighting the letters it passes. The band is 0.6 of the wordmark wide, and its light
/// falls from the middle to nothing at either edge along a smootherstep, so it has no edge for the eye to find. Each
/// pass eases in and out (quadratic). The wordmark is 260 by 100 on a 1080 screen, as on the TV.</para>
///
/// <para><b>Why the compositor draws the band.</b> The cover is up exactly while this thread is busiest: the
/// collection's first read, home's first layout, the room's shaders. A band moved from this thread would stand still
/// for all of it, which is the hung look the cover is there to prevent. The TV draws its band on a thread of its own
/// for the same reason. Here, on the WinUI head, the band's two ends are animated by the system compositor and this
/// thread writes nothing per frame (as <see cref="Spin"/> does for the record). The Skia head moves them from a frame
/// tick instead, as its turning record does.</para>
///
/// <para><b>How the band lights only the letters.</b> XAML has no opacity mask, so the band is drawn with
/// composition, as the wall's foot is dissolved (PegasusChrome.TopFade): a live copy of a white wordmark
/// (OpeningLit) is painted through a mask brush whose mask is the band's gradient. The white wordmark itself is never
/// seen: its wrapper (OpeningLitCut) is clipped to nothing, and the copy does not include its wrapper.</para>
///
/// <para><b>The hand-over.</b> Once the view is ready (<see cref="EndLoading"/>), the cover waits for the end of the
/// pass the band is on, so the band is clear of the letters, then fades away over <see cref="OpeningFadeMs"/> while
/// the opening sound starts.</para>
/// </summary>
public sealed partial class PegasusPlayer
{
    /// <summary>One pass of the band across the wordmark (the TV's SWEEP_MS).</summary>
    private static readonly TimeSpan SweepPass = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long the cover takes to fade away once the view is ready (the TV's OPENING_FADE_MS).</summary>
    private const int OpeningFadeMs = 600;

    /// <summary>How long the line under the wordmark takes to come up, or go (the TV's READING_FADE_MS).</summary>
    private const int ReadingFadeMs = 300;

    /// <summary>The band's width, as a share of the wordmark's (the TV's BAND_OF_WIDTH).</summary>
    private const float BandOfWidth = 0.6f;

    /// <summary>The wordmark's size and the line's place under it, in pixels of a 1080 screen, as on the TV:
    /// 260 by 100, and the line's middle 40 below the wordmark's lower edge.</summary>
    private const double MarkWide = 260, MarkHigh = 100, ReadingGap = 40;

    /// <summary>The line's size, before the view's scale (the TV's 18.ts).</summary>
    private const double ReadingSize = 18;

    /// <summary>Counts the hand-overs. A hand-over waiting for the end of its pass is dropped when the cover goes up
    /// again in the meantime, or another hand-over is booked.</summary>
    private int _openingHandOver;

    private ChromeFade? _readingFade;

    // The band: the copy of the white wordmark, the gradient it is seen through, the sprite that paints it, and the
    // cut that hides the white wordmark itself. Built once, on the first opening.
    private CompositionVisualSurface? _sweepCopy;
    private CompositionLinearGradientBrush? _sweepBand;
    private SpriteVisual? _sweepSprite;
    private InsetClip? _sweepCut;

    /// <summary>Set when the composition pieces could not be built. The cover then shows the dimmed wordmark
    /// alone.</summary>
    private bool _noSweep;

    /// <summary>When the band started its first pass. A pass ends every <see cref="SweepPass"/> from here.</summary>
    private readonly Stopwatch _sweepClock = new();

    /// <summary>Whether the band is moved by the compositor (the WinUI head) rather than by a frame tick.</summary>
    private bool _sweepOnCompositor;
    private bool _sweepTicking;

    /// <summary>Whether this visit's opening may play the opening sound (not the gallery's still capture), and
    /// whether it did, so leaving the view silences the sound it started and no other.</summary>
    private bool _openingSoundWanted;
    private bool _openingSounded;

    private void Loading_SizeChanged(object sender, SizeChangedEventArgs e) => SizeOpening();

    /// <summary>Sizes the wordmark and places the line under it for the window's height, and fits the band to the
    /// wordmark.</summary>
    private void SizeOpening()
    {
        var high = Loading.ActualHeight > 0 ? Loading.ActualHeight : Root.ActualHeight;
        if (high <= 0)
            return;

        var px = high / 1080;
        OpeningMark.Width = MarkWide * px;
        OpeningMark.Height = MarkHigh * px;
        OpeningReading.FontSize = ReadingSize * Math.Clamp(high / 900, 1.0, 1.5);
        OpeningReadingShift.Y = (MarkHigh / 2 + ReadingGap) * px;
        FitSweep();
    }

    /// <summary>Points the copy at the whole white wordmark, sizes the sprite to it and cuts the white wordmark
    /// away. A wordmark with no size yet hides the sprite: a surface with no pixels must never reach a mask brush
    /// (see <see cref="BandSurface"/>).</summary>
    private void FitSweep()
    {
        if (_sweepCopy is null || _sweepSprite is null || _sweepCut is null)
            return;

        var wide = (float)OpeningMark.Width;
        var high = (float)OpeningMark.Height;
        if (double.IsNaN(wide) || double.IsNaN(high))
            return;
        _sweepCut.LeftInset = wide;
        _sweepCut.TopInset = high;
        BandSurface.Fit(_sweepCopy, _sweepSprite, wide, high, 0);
    }

    /// <summary>Builds the band, the first time the cover goes up.</summary>
    private void EnsureSweep()
    {
        if (_sweepSprite is not null || _noSweep)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(OpeningSweep).Compositor;

            // The white wordmark, as rendered, from its own top left corner and at its own size.
            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(OpeningLit);
            copy.SourceOffset = Vector2.Zero;
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // The band: only its alpha is read. White at the middle and nothing at either edge, flat at all three:
            // 1 - smootherstep(distance from the middle), at seventeen stops as on the TV. Outside the band the end
            // stops carry on, so the rest of the wordmark is not lit.
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
            ElementCompositionPreview.SetElementChildVisual(OpeningSweep, sprite);

            // The white wordmark is cut away where it stands. The cut is on its wrapper, which the copy does not
            // include, so the copy keeps every pixel.
            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(OpeningLitCut).Clip = cut;

            _sweepCopy = copy;
            _sweepBand = band;
            _sweepSprite = sprite;
            _sweepCut = cut;
            PlaceBand(0);
            FitSweep();
        }
        catch
        {
            // A backend without visual surfaces or mask brushes. The cover shows the dimmed wordmark alone, and the
            // white one must not show in its place. This is tried once.
            _noSweep = true;
            OpeningLitCut.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Puts the band where <paramref name="pass"/> (0 to 1, already eased) has it: from wholly before the
    /// first letter to wholly after the last, in the wordmark's own width.</summary>
    private void PlaceBand(float pass)
    {
        if (_sweepBand is not { } band)
            return;
        var from = -BandOfWidth + pass * (1 + BandOfWidth);
        band.StartPoint = new Vector2(from, 0);
        band.EndPoint = new Vector2(from + BandOfWidth, 0);
    }

    /// <summary>Starts the band sweeping, if it is not already. A band already sweeping is left on its pass, so a
    /// cover that goes up again over itself does not jump.</summary>
    private void StartSweep()
    {
        SizeOpening();
        EnsureSweep();
        if (_sweepBand is not { } band || _sweepClock.IsRunning)
            return;

        _sweepClock.Restart();

#if RPM_WINUI
        try
        {
            var compositor = band.Compositor;

            // One pass, eased in and out, repeated for as long as the cover is up. The curve is the quadratic
            // in-out the TV's pass follows, as the compositor's cubic bezier.
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.455f, 0.03f), new Vector2(0.515f, 0.955f));
            var start = compositor.CreateVector2KeyFrameAnimation();
            start.InsertKeyFrame(0f, new Vector2(-BandOfWidth, 0), compositor.CreateLinearEasingFunction());
            start.InsertKeyFrame(1f, new Vector2(1, 0), ease);
            start.Duration = SweepPass;
            start.IterationBehavior = AnimationIterationBehavior.Forever;

            // The far end rides on the near end, so the two cannot drift apart.
            var end = compositor.CreateExpressionAnimation("this.Target.StartPoint + Vector2(width, 0)");
            end.SetScalarParameter("width", BandOfWidth);

            band.StartAnimation("StartPoint", start);
            band.StartAnimation("EndPoint", end);
            _sweepOnCompositor = true;
            return;
        }
        catch
        {
            // The compositor would not take it. The frame tick below moves the band instead.
            _sweepOnCompositor = false;
        }
#endif

        if (!_sweepTicking)
        {
            _sweepTicking = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSweepFrame;
        }
    }

    /// <summary>One frame of the band, where the compositor does not move it.</summary>
    private void OnSweepFrame(object? sender, object e)
    {
        var into = _sweepClock.Elapsed.TotalMilliseconds % SweepPass.TotalMilliseconds / SweepPass.TotalMilliseconds;
        PlaceBand((float)ChromeEase(into));
    }

    /// <summary>The quadratic ease-in-out every fade in this view uses (see ChromeFade).</summary>
    private static double ChromeEase(double t) => t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);

    /// <summary>Stops the band and puts it back before the first letter, for the next opening.</summary>
    private void StopSweep()
    {
        _sweepClock.Reset();

        if (_sweepTicking)
        {
            _sweepTicking = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSweepFrame;
        }

#if RPM_WINUI
        if (_sweepOnCompositor && _sweepBand is { } band)
        {
            try
            {
                band.StopAnimation("EndPoint");
                band.StopAnimation("StartPoint");
            }
            catch { }
        }
#endif
        _sweepOnCompositor = false;

        PlaceBand(0);
    }

    /// <summary>How long is left of the pass the band is on, so the cover hands over with the band clear of the
    /// letters. Nothing when the band is not sweeping.</summary>
    private TimeSpan SweepLeft()
    {
        if (!_sweepClock.IsRunning || _sweepBand is null)
            return TimeSpan.Zero;
        var pass = SweepPass.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(pass - _sweepClock.Elapsed.TotalMilliseconds % pass);
    }

    /// <summary>Brings the line under the wordmark up, or takes it away, over <see cref="ReadingFadeMs"/>.</summary>
    private void ShowOpeningReading(bool on)
    {
        _readingFade ??= new ChromeFade(OpeningReading);
        if (on)
            OpeningReading.Visibility = Visibility.Visible;
        _readingFade.To(on ? 1 : 0, ReadingFadeMs);
    }

    /// <summary>
    /// The opening sound, as the cover fades away: Pegasus's own opener, <c>rpm-open-pegasus</c>
    /// (<see cref="AppSound.PlayPegasusOpen"/>), at the level the window's opening sound plays at (0.7 of the
    /// player's volume), through the same player.
    ///
    /// <para>It plays only when nothing is playing: not over music that is already playing, and not over the
    /// window's own opening sound still sounding, so the two never overlap. It stops as soon as music starts:
    /// whoever puts a record on has said what the speakers are for. Once per opening; leaving the view silences it
    /// (see <see cref="StopOpening"/>).</para>
    /// </summary>
    private void PlayOpeningSound()
    {
        if (!IsShowing || !_openingSoundWanted)
            return;
        _openingSoundWanted = false;

        try
        {
            var playback = App.Services.GetRequiredService<PlaybackService>();
            bool Playing() => playback.PlayingPath is not null && !playback.IsPaused;
            if (Playing() || AppSound.IsPlaying)
                return;

            _openingSounded = true;
            AppSound.PlayPegasusOpen(playback.Volume, stop: Playing);
        }
        catch { /* a flourish: it must never hold up the view */ }
    }

    /// <summary>The view is being left: the band stops, and the opening sound this visit started stops with
    /// it.</summary>
    private void StopOpening()
    {
        StopSweep();
        if (_openingSounded)
        {
            _openingSounded = false;
            AppSound.Silence();
        }
    }
}
