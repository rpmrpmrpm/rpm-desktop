using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Sets the tooltip and the accessible name of a control that shows only an icon, both to the same text.
///
/// <para>A screen reader reads <c>AutomationProperties.Name</c>, not the tooltip. So a button with only a
/// tooltip is announced just as "button". Where code changes the tooltip with state (for example a pin
/// that is on or off, or a pane that is folded or open), the name must change with it. Otherwise the
/// screen reader announces the old state. Setting both in one call keeps them the same.
/// <c>IconButtonNameTests</c> rejects a direct <c>ToolTipService.SetToolTip</c> on an icon-only button
/// declared in XAML.</para>
/// </summary>
public static class IconName
{
    public static void Set(FrameworkElement element, [Shown] string text)
    {
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetName(element, text);
    }
}
