using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// A host for one banner card. Collapsed whenever it holds no <see cref="HomeFeatureCard"/>.
/// </summary>
/// <remarks>
/// <b>An empty host draws every placeholder at once.</b> A ContentControl builds its ContentTemplate
/// even when Content is null. FeatureFaceTemplate binds every layer's Visibility to the card, and with
/// no card each of those bindings fails and the layer falls back to Visible: the radio mark, the
/// record mark in the cover square and the station panel all appear together. On WinUI, a host that
/// sits under the plate and is never given a card would show all of them through every card whose
/// picture has not arrived yet.
///
/// <para>So the host sets its own visibility from what it holds, on every change of Content,
/// instead of relying on each caller to collapse it before emptying it.</para>
/// </remarks>
public sealed partial class FeatureHost : ContentControl
{
    public FeatureHost()
    {
        // Content starts null and OnContentChanged is not called for the initial value.
        Visibility = Visibility.Collapsed;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Visibility = newContent is HomeFeatureCard ? Visibility.Visible : Visibility.Collapsed;
    }
}
