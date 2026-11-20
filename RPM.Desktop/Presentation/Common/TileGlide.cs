using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using RPM.Desktop.Presentation.Main;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Slides and fades for tiles that move to a new place in a grid: the album wall's cards and its open drawer
/// (see <c>AlbumWallView.Glide.cs</c>), and the tiles of the artists, genre and playlist grids (see
/// <see cref="TileGridMove"/>). Nothing here is layout. Each element is drawn back where its tile was, and its
/// composition visual slides it to its slot.
/// </summary>
/// <remarks>
/// <para><b>The shift is not written to the same property on the two heads.</b> On WinUI, layout owns an
/// element's visual <c>Offset</c>, so the shift goes in <c>Translation</c>, which is switched on first. On Uno's
/// Skia head, layout writes the visual's arrange offset and adds <c>Offset</c> to it, so <c>Offset</c> is free.
/// <c>Translation</c> is not safe there: a write through the visual's property set does not ask for a new frame,
/// and a slide that is stopped would stay drawn where it stopped until something else moved the element.</para>
///
/// <para><b>Scale</b> is about the element's top-left corner, which is the corner its place is measured
/// to.</para>
///
/// <para><b>Opacity.</b> On Uno an element's XAML opacity and its visual's opacity are one value, and
/// <see cref="Stop"/> sets it back to one. So only an element whose XAML opacity is one may be faded here. The
/// album card's button and the grids' tiles are.</para>
///
/// <para><b>Elements are recycled.</b> A slide belongs to a tile, but it runs on an element, and a repeater
/// hands that element to other tiles. The owner calls <see cref="Stop"/> when an element is given another tile.
/// Only elements that were given a slide or a fade are stopped: stopping an animation on a property that a
/// WinUI visual does not have throws.</para>
/// </remarks>
internal sealed class TileGlide
{
    /// <summary>How long a tile takes to reach its new place. A little shorter than a pane move, so a tile moved
    /// at the landing has arrived soon after the pane has.</summary>
    public const double SlideMs = 220;

    /// <summary>How long a tile that comes into view takes to fade in.</summary>
    public const double FadeMs = 160;

#if RPM_WINUI
    private const string Shift = "Translation";
#else
    private const string Shift = "Offset";
#endif

    private readonly HashSet<UIElement> _moving = new();

    /// <summary>Starts <paramref name="element"/> <paramref name="offset"/> away from its slot, in its parent's
    /// units, at <paramref name="size"/> times its own size, and slides it home.</summary>
    public void Slide(FrameworkElement element, Vector2 offset, float size)
    {
        Stop(element);
#if RPM_WINUI
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
#endif
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = PaneGlide.Ease(compositor);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(offset, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(SlideMs);
        visual.StartAnimation(Shift, slide);

        if (Math.Abs(size - 1) >= 0.005f)
        {
            visual.CenterPoint = Vector3.Zero;
            var grow = compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(size, size, 1));
            grow.InsertKeyFrame(1f, Vector3.One, ease);
            grow.Duration = TimeSpan.FromMilliseconds(SlideMs);
            visual.StartAnimation("Scale", grow);
        }
        _moving.Add(element);
    }

    /// <summary>Fades <paramref name="element"/> in from nothing.</summary>
    public void Fade(FrameworkElement element)
    {
        Stop(element);
#if RPM_WINUI
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
#endif
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, PaneGlide.Ease(compositor));
        fade.Duration = TimeSpan.FromMilliseconds(FadeMs);
        visual.StartAnimation("Opacity", fade);
        _moving.Add(element);
    }

    /// <summary>Puts an element that was given a slide or a fade back where layout put it, at its own size and
    /// at full opacity. Does nothing for an element that was never given one.</summary>
    public void Stop(UIElement element)
    {
        if (!_moving.Remove(element))
            return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(Shift);
#if RPM_WINUI
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
#else
        visual.Offset = Vector3.Zero;
#endif
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }
}
