using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The picture setting: how bright and saturated the view is drawn (see <see cref="PictureLook"/>), as the Android
/// TV app's Picture row sets it.
///
/// <para><b>How.</b> The TV lays a colour matrix on the layer its screens are drawn in, and dims its room in the
/// room's own shader. XAML has no colour matrix for a tree of elements, so here two plain layers lie over
/// everything the view draws, and the room is under them with the rest (the room's shaders are not touched):</para>
/// <list type="bullet">
/// <item>PictureVeil, black at 1 - light. A colour under 15% black is that colour times 0.85, exactly the TV's
/// multiplication, for the price of one flat rectangle.</item>
/// <item>PictureMute, for a look with less saturation (Muted): a backdrop brush reads what is under it and a
/// saturation effect pulls it toward grey, about the same luminance weights as the TV's matrix (Rec. 709). It reads
/// the whole screen on every frame, so it is only in the tree while it is wanted.</item>
/// </list>
///
/// <para>The two commute (a saturation about luminance and a uniform dimming), so their order does not matter.
/// Both are under the opening's cover, which the TV also draws as it is. On the Skia head only the veil applies:
/// Muted is dimmed there but not desaturated, as the TV's own room is not.</para>
/// </summary>
public sealed partial class PegasusPlayer
{
    /// <summary>Puts <paramref name="look"/> over the view. Standard collapses both layers.</summary>
    private void ApplyPicture(PictureLook look)
    {
        var light = look.Light();
        PictureVeil.Opacity = 1 - light;
        PictureVeil.Visibility = light < 1 ? Visibility.Visible : Visibility.Collapsed;

#if RPM_WINUI
        var saturation = (float)look.Saturation();
        if (saturation < 1)
        {
            if (PictureMute.Fill is not PictureMuteBrush brush || brush.Saturation != saturation)
                PictureMute.Fill = new PictureMuteBrush { Saturation = saturation };
            PictureMute.Visibility = Visibility.Visible;
        }
        else
        {
            // Taken out of the tree and let go of, so nothing reads the screen behind it.
            PictureMute.Visibility = Visibility.Collapsed;
            PictureMute.Fill = null;
        }
#else
        PictureMute.Visibility = Visibility.Collapsed;
#endif
    }
}

#if RPM_WINUI
/// <summary>
/// What is behind it, with less saturation: a live read of the backdrop through Win2D's saturation effect, as
/// <see cref="GlassBrush"/> reads it through a blur. The picture setting's Muted look (see
/// PegasusPlayer.Picture).
/// </summary>
internal sealed partial class PictureMuteBrush : XamlCompositionBrushBase
{
    /// <summary>How much saturation is kept, 0 to 1. Read when the brush is first drawn.</summary>
    public float Saturation { get; init; } = 1;

    protected override void OnConnected()
    {
        if (CompositionBrush is null)
        {
            var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var backdrop = compositor.CreateBackdropBrush();

            var mute = new Microsoft.Graphics.Canvas.Effects.SaturationEffect
            {
                Saturation = Saturation,
                Source = new CompositionEffectSourceParameter("backdrop"),
            };

            using var factory = compositor.CreateEffectFactory(mute);
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
#endif
