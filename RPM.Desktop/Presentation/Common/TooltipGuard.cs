using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Stops Uno's "rogue tooltip". This is a tooltip that opens by itself as soon as a dialog or
/// popup opens. It appears near the old mouse position, not next to the control that owns it.
///
/// Cause (found in the Uno 6.5.31 source, <c>ToolTipService.OnGotFocus</c>): a ContentDialog gives its
/// first focusable child <b>programmatic</b> focus when it opens. ToolTipService opens a tooltip on
/// <c>GotFocus</c> when the focus is Keyboard <i>or</i> UIA-programmatic. So any button with a tooltip
/// inside the dialog shows its tooltip when the dialog opens, with no pointer or keyboard action.
/// Setting Placement, or guarding on PointerEntered, has no effect, because the trigger is GotFocus.
/// Focus events cannot be cancelled with
/// <c>Handled</c>.
///
/// Fix: when an owner gets <b>Programmatic</b> focus (the dialog-open case), cancel the tooltip that
/// has just been armed. A real Keyboard tab is left alone. The tooltip is cancelled by registering it
/// again.
/// <c>SetToolTip(null)</c> runs Uno's <c>UnregisterToolTip → CloseToolTipImpl</c>, which stops the
/// pending open. The content is then restored, so hover and keyboard tooltips still work. The work is
/// deferred by one dispatcher turn, so it runs after ToolTipService's own GotFocus handler has started
/// the open timer. Because it is deferred, it works whichever handler subscribes first. So the property
/// can be applied through Styles.
/// </summary>
public static class TooltipGuard
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(TooltipGuard),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static bool _globalInstalled;

    /// <summary>
    /// Installs the guard for the whole app, through the static <see cref="FocusManager.GotFocus"/> event.
    /// One subscription sees every focus change, so no attached property or Style setter is needed on each
    /// owner. Call it once at startup.
    /// </summary>
    public static void InstallGlobal()
    {
        if (_globalInstalled) return;
        _globalInstalled = true;
        FocusManager.GotFocus += OnGlobalGotFocus;
    }

    private static void OnGlobalGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (e.NewFocusedElement is Control c) CancelSpuriousFocusToolTip(c);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control c) return;

        if ((bool)e.NewValue)
        {
            c.GotFocus += OnGotFocus;
        }
        else
        {
            c.GotFocus -= OnGotFocus;
        }
    }

    private static void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Control c) CancelSpuriousFocusToolTip(c);
    }

    /// <summary>ToolTipService opens a tooltip on GotFocus for keyboard and UIA-programmatic focus. A
    /// ContentDialog focuses a child when it opens. That focus is reported as FocusState.Pointer or
    /// Programmatic, not Keyboard, so the child's tooltip shows on open, near the old cursor. This
    /// cancels the tooltip for any non-keyboard focus, whether it comes from the dialog opening or from
    /// a click. A real keyboard tab keeps its tooltip. Hover tooltips open on PointerEntered, not
    /// GotFocus, so they are not affected.
    /// </summary>
    private static void CancelSpuriousFocusToolTip(Control c)
    {
        if (c.FocusState == FocusState.Keyboard) return;

        c.DispatcherQueue?.TryEnqueue(() =>
        {
            var tip = ToolTipService.GetToolTip(c);
            if (tip is null) return;
            // Setting the tooltip to null cancels the pending open, because Uno's SetToolTip(null) runs
            // UnregisterToolTip and CloseToolTipImpl. Setting it back keeps the tooltip working for hover
            // and keyboard focus.
            ToolTipService.SetToolTip(c, null);
            ToolTipService.SetToolTip(c, tip);
        });
    }
}
