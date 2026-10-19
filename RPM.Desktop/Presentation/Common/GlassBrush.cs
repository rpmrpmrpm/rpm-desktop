using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Effects;
#if RPM_WINUI
// Win2D's own Gaussian blur. It has the same BlurAmount and Source, and it is already an IGraphicsEffect
// that the compositor's effect factory accepts. The Skia head uses a hand-written effect instead, which
// is defined below. See its remarks for why.
using GaussianBlurEffect = Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect;
#else
using Windows.Graphics.Effects.Interop;
#endif

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A glass brush that gives a live frost. It samples the pixels rendered behind whatever it paints
/// (a <see cref="CompositionBackdropBrush"/>, the same live-scene read that AcrylicBrush uses). It runs
/// a Gaussian blur over them through the composition effect graph. Unlike a flat veil, it updates on
/// every frame with whatever moves behind it.
///
/// This version is blur only. It is the base of the pipeline. The tint comes from the thin veil that the
/// grids already put on top. The glass bevel and rim sheen are the next step, to add once this looks
/// right. There is no refraction, because the composition effect graph has no displacement node. That
/// would need a patch to Uno.
/// </summary>
public sealed partial class GlassBrush : XamlCompositionBrushBase
{
    /// <summary>Blur radius in DIPs. A higher value gives a frostier look.</summary>
    public double BlurAmount { get; set; } = 24;

    protected override void OnConnected()
    {
        if (CompositionBrush is null)
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            var backdrop = compositor.CreateBackdropBrush();

            var blur = new GaussianBlurEffect
            {
                BlurAmount = (float)BlurAmount,
                Source = new CompositionEffectSourceParameter("backdrop"),
            };

            using var factory = compositor.CreateEffectFactory(blur);
            if (factory.CreateBrush() is { } effectBrush)
            {
                effectBrush.SetSourceParameter("backdrop", backdrop);
                CompositionBrush = effectBrush;
            }
        }

        base.OnConnected();
    }

    protected override void OnDisconnected()
    {
        CompositionBrush?.Dispose();
        CompositionBrush = null;
        base.OnDisconnected();
    }
}

/// <summary>
/// Like <see cref="GlassBrush"/>, but it frosts a fixed IMAGE instead of the live backdrop. It loads
/// <see cref="Source"/> into a composition surface and runs the same Gaussian blur over it. It is used for
/// the frosted band in the dialog header, where the frosted picture is a record photo, not the cover art.
/// </summary>
public sealed partial class ImageFrostBrush : XamlCompositionBrushBase
{
    /// <summary>The image to frost (ms-appx:/// uri).</summary>
    public Uri? Source { get; set; }

    /// <summary>Blur radius in DIPs.</summary>
    public double BlurAmount { get; set; } = 20;

    protected override void OnConnected()
    {
        if (CompositionBrush is null && Source is not null)
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(Source);
            var surfaceBrush = compositor.CreateSurfaceBrush(surface);
            surfaceBrush.Stretch = CompositionStretch.UniformToFill;

            var blur = new GaussianBlurEffect
            {
                BlurAmount = (float)BlurAmount,
                Source = new CompositionEffectSourceParameter("img"),
            };

            using var factory = compositor.CreateEffectFactory(blur);
            if (factory.CreateBrush() is { } effectBrush)
            {
                effectBrush.SetSourceParameter("img", surfaceBrush);
                CompositionBrush = effectBrush;
            }
        }

        base.OnConnected();
    }

    protected override void OnDisconnected()
    {
        CompositionBrush?.Dispose();
        CompositionBrush = null;
        base.OnDisconnected();
    }
}

#if !RPM_WINUI
/// <summary>
/// A Win2D/Direct2D Gaussian blur effect, written by hand. Uno implements the effect on Skia, but it keeps
/// the Win2D wrapper internal. So this is driven through <see cref="IGraphicsEffectD2D1Interop"/>, using
/// the D2D effect's CLSID (see the Uno "Shapes &amp; Brushes" docs). It has one property: BlurAmount.
///
/// <para>The WinUI head uses Win2D's GaussianBlurEffect instead. That is aliased at the top of this file.
/// On WinUI, Win2D is a real package, and Windows.Graphics.Effects.Interop is not projected. So there is
/// nothing to write by hand against there.</para>
/// </summary>
internal sealed class GaussianBlurEffect : IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop
{
    private static readonly Guid D2D1GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    public string Name { get; set; } = nameof(GaussianBlurEffect);
    public IGraphicsEffectSource? Source { get; set; }
    public float BlurAmount { get; set; } = 3.0f;

    public Guid GetEffectId() => D2D1GaussianBlur;

    public void GetNamedPropertyMapping(string name, out uint index, out GraphicsEffectPropertyMapping mapping)
    {
        switch (name)
        {
            case nameof(BlurAmount):
                index = 0;
                mapping = GraphicsEffectPropertyMapping.Direct;
                break;
            default:
                index = 0xFF;
                mapping = (GraphicsEffectPropertyMapping)0xFF;
                break;
        }
    }

    public object? GetProperty(uint index) => index switch
    {
        0 => BlurAmount,
        _ => null,
    };

    public uint GetPropertyCount() => 1;
    public IGraphicsEffectSource? GetSource(uint index) => Source;
    public uint GetSourceCount() => 1;
}
#endif
