using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The window a GL stage draws into. Every <c>GLCanvasElement</c> has to be given it as an argument.
///
/// <para><b>Why the element cannot work it out itself.</b> Uno's GLCanvasElement takes a
/// <c>Func&lt;Window&gt;</c>. WinUI gives no way to get the Window that a FrameworkElement is in, and the
/// GL context is made against that window's handle. Uno's own hosts do not need the argument, so they
/// ignore it. Every stage in this app therefore passed <c>static () =&gt; null!</c>, and nothing looked
/// wrong. On the WinUI head that null means no window, so no handle, so no context. The stage still starts
/// and reports no error, but it draws a black frame for as long as it is on screen. For example, the
/// visualizer host opens full screen at 2560x1440 with zero lit pixels.</para>
///
/// <para><b>The one window.</b> Every GL stage in this app is in the main window. That covers the eight
/// visualizers and Pegasus. The full-screen visualizer is that window with its chrome removed (see
/// <see cref="Presentation.Shell.FullScreenWindow"/>). It is not a window of its own. The other windows
/// the app opens (the gallery, the mix lab, the genre placer) host no GL. A stage put in one of those
/// would need this to find the element's own window. There is no framework call for that. It would mean
/// the app keeping a list of its windows and matching them on XamlRoot.</para>
/// </summary>
internal static class GlHost
{
    /// <summary>The window that every GL stage belongs to. Uno calls this while the stage is in the visual
    /// tree, so the main window exists by then.</summary>
    public static Window Window() => App.AppWindow!;
}
