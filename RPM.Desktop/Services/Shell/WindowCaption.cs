using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Tells Windows which parts of the borderless window are its title bar.
///
/// <para><b>Why the system has to be told, rather than the app doing the dragging.</b> Snap layouts, snap
/// to a screen edge, drag to the top to maximise, and dragging a maximised window to restore it all run
/// inside the system's own move loop. That loop starts when a press lands on a spot that the window reports
/// as caption (<c>WM_NCHITTEST</c> answering <c>HTCAPTION</c>). A managed drag moves the window by the
/// pointer difference on every pointer move. It never enters that loop, so none of those gestures exist,
/// and the window only goes where it is put. A managed drag can also leave the window's state and its
/// position out of step. A move that is applied one tick after a maximise moves a window that is still
/// marked as maximised. That window then cannot be dragged, and it does not fill the screen that it reports
/// filling.</para>
///
/// <para><b>What handing over a region costs.</b> The system hit-tests a rectangle that is given to it,
/// before XAML sees anything. So z-order no longer protects whatever is drawn on top of it. The caller
/// therefore hands over only rectangles that nothing else needs. See <see
/// cref="Presentation.Shell.ShellPage.RefreshCaptionRegions"/> for the band this app hands over, and for
/// what it stops short of.</para>
///
/// <para>Windows only. Uno's X11 backend implements no non-client pointer source. So these calls do nothing
/// there, and Linux keeps the managed drag in ShellPage.</para>
/// </summary>
internal static class WindowCaption
{
    /// <summary>
    /// Replaces the window's caption and maximise-button regions. Both are in physical pixels, relative to
    /// the top-left of the client area. Empty arrays clear the regions. That is how the app gives the title
    /// bar back while another view owns the window.
    /// </summary>
    /// <param name="maximize">
    /// The maximise button. It is handed over separately because that is what puts the Windows 11
    /// snap-layout flyout on it. Uno sends the press and the hover back into the XAML button, so the button
    /// keeps working and keeps its own look.
    /// </param>
    public static void Apply(Window window, RectInt32[] caption, RectInt32[] maximize)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (window.AppWindow is not { } appWindow)
                return;

            var source = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, caption);
            source.SetRegionRects(NonClientRegionKind.Maximize, maximize);
        }
        catch { /* a host with no non-client pointer source: the window keeps the chrome it has */ }
    }
}
