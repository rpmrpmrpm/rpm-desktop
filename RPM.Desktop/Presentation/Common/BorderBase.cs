using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The base class for the app's border-shaped controls: one child, a background, a border, a corner
/// radius and padding.
///
/// <para><b>Why it exists.</b> Border is sealed in WinUI but not in Uno. Several controls here derived
/// from Border directly. That compiles on the Skia head, but not on the WinUI head. They derive from
/// this class instead, and the build for each head chooses the base type.</para>
///
/// <para>On the Skia head, this class is a Border, so nothing changes. On the WinUI head it is a Grid. A
/// Grid has the same Background, BorderBrush, BorderThickness, CornerRadius, Padding and BackgroundSizing
/// properties, and it lays out a single child in the same way. <see cref="Child"/> is defined here, so both
/// heads use the same name for the one child. The content property is also named here, so XAML content
/// placed inside one of these controls ends up in the same place on both heads.</para>
/// </summary>
#if RPM_WINUI
[ContentProperty(Name = nameof(Child))]
public partial class BorderBase : Grid
{
    /// <summary>The single child, named the same way as in Border. Setting it replaces the current
    /// child.</summary>
    public UIElement? Child
    {
        get => Children.Count > 0 ? Children[0] : null;
        set
        {
            Children.Clear();
            if (value is not null)
                Children.Add(value);
        }
    }
}
#else
public partial class BorderBase : Border
{
}
#endif
