using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Controls;

// The app's three buttons. They are the frosted-glass look in control form. The look itself is set in
// Themes/Generic.xaml (GlassButtonBaseStyle, plus one face brush for each button). These types let a screen
// say which button it wants, so it does not repeat a Style= on every call site.
//
// They differ by material weight, not by colour. A brighter frost reads as the primary button. A thin,
// see-through one recedes. The exception is danger, where red has a meaning and is not just decoration.

/// <summary>The default button: a frosted ghost pill. Used for secondary and cancel actions.</summary>
public partial class RpmButton : Button
{
    public RpmButton() => DefaultStyleKey = typeof(RpmButton);
}

/// <summary>The one committing action on a screen. It uses the same glass, with a brighter frost.</summary>
public partial class RpmPrimaryButton : Button
{
    public RpmPrimaryButton() => DefaultStyleKey = typeof(RpmPrimaryButton);
}

/// <summary>Destructive actions. The glass takes a red wash, because a delete needs a clear warning, not
/// just emphasis.</summary>
public partial class RpmDangerButton : Button
{
    public RpmDangerButton() => DefaultStyleKey = typeof(RpmDangerButton);
}
