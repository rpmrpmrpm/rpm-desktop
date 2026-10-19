using System.Runtime.Versioning;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Where the window opens: filling the screen, every time.
///
/// <para><b>The window always opens maximised, whatever size it was closed at.</b> If it reopened at its
/// last frame, the window would jump from the size the system gave it to another size, and the page inside
/// would lay itself out twice on the way. The frame is still saved when the app exits (see <see
/// cref="Remember"/>), and nothing reads it today. It is kept because it is what the window would go back
/// to if opening small were offered again.</para>
///
/// <para><b>Two mechanisms, on purpose.</b> Windows and Linux both maximise through the presenter. Uno's
/// X11 backend implements the same presenter state and title-bar hint that the Windows path uses, so one
/// shape covers both. macOS uses neither. The managed write there is <c>AppWindow.Move</c> or
/// <c>MoveAndResize</c>, and on Uno-mac that call aborts the whole process (NSWindow.setFrame throws out of
/// managed code). So macOS uses the mechanism that Cocoa apps have always used: AppKit's own frame
/// autosave. It saves on every move, and it restores the frame within the screens that exist now. A window
/// that fills the screen there comes back filling it. That is the nearest thing that platform has to this
/// behaviour.</para>
/// </summary>
internal static class WindowPlacement
{
    /// <summary>The name that AppKit files this window's frame under. It must stay stable. If it changes,
    /// every user's window forgets where it was, once.</summary>
    private const string MacFrameKey = "rpm-main-window";

    /// <summary>
    /// A note that is written while the frame is being put back, and deleted as soon as that has worked.
    ///
    /// <para>Restoring a window frame is the one step here that can stop the app before it has a screen. On
    /// this platform that failure is an <c>abort()</c> inside AppKit, which no <c>catch</c> can see. So if
    /// this file is still there at startup, the last attempt did not come back. After two such attempts,
    /// the app stops trying and opens where the system puts it. An app that cannot start is worse than an
    /// app that forgets where its window was.</para>
    /// </summary>
    private static string GuardPath => Path.Combine(LocalData.Dir, "window-frame.attempt");

    private const int MostAttempts = 2;

    /// <summary>Opens the window filling the screen. Called once, as the window comes up. It is called only
    /// through
    /// <see cref="Presentation.Shell.SkinWindow.PlaceLibrary"/>, which holds it back while the player view
    /// has the window.</summary>
    public static void Restore(Window window)
    {
        if (OperatingSystem.IsWindows())
            RestoreWindows(window);
        else if (OperatingSystem.IsLinux())
            RestoreLinux(window);
    }

    /// <summary>
    /// macOS: hands the frame to AppKit. This is separate from <see cref="Restore"/> because it needs the
    /// NSWindow, which does not exist until one tick after the window is activated. The title-bar treatment
    /// waits for the same tick.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void RestoreMac(nint nsWindow)
    {
        if (nsWindow == 0)
            return;

        // Saving is harmless whatever happened last time. It is AppKit writing the frame to the defaults.
        // That is the half that must be on, so that there is something to come back to.
        try { MacWindowChrome.RememberFrame(nsWindow, MacFrameKey); } catch { return; }

        var attempts = ReadAttempts();
        if (attempts >= MostAttempts)
        {
            Console.WriteLine("[window] not restoring the frame: the last attempts did not come back");
            return;
        }

        WriteAttempts(attempts + 1);
        try { MacWindowChrome.RestoreFrame(nsWindow, MacFrameKey); }
        finally { ClearAttempts(); }
    }

    /// <summary>Writes the window's size and position down. This is for Windows and Linux. macOS has AppKit
    /// save its own frame. See the class remarks.</summary>
    public static void Remember(Window window, SessionService session)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        try
        {
            if (window.AppWindow is not { } appWindow)
                return;

            // A maximised window's own frame is the small one that it returns to when it is un-maximised.
            // So both are kept. The state decides which one is used. That way, un-maximising after a
            // restart still lands on the size that the user actually chose.
            var maximized = RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(appWindow) is { State: OverlappedPresenterState.Maximized };
            session.WindowMaximized = maximized;
            if (maximized)
                return;

            var position = appWindow.Position;
            var size = appWindow.Size;
            if (size.Width > 0 && size.Height > 0)
                session.WindowFrame = (position.X, position.Y, size.Width, size.Height);
        }
        catch { /* A window in the middle of teardown has no geometry to report. The last note stands. */ }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreWindows(Window window)
    {
        try
        {
            if (window.AppWindow is not { } appWindow)
                return;

            var presenter = RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(appWindow);

            // Borderless: remove the title bar and the native caption buttons. Keep the sizing border, so
            // that Windows 11 edge-resize and Snap Layouts still work. The app's own caption buttons sit
            // top-left, in ShellPage.
            presenter?.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

            // Fills the work area, whatever was stored. See the class remarks.
            presenter?.Maximize();
        }
        catch { /* The window opens at the system's own size, which is a working state. */ }
    }

    /// <summary>
    /// Linux uses the same mechanism as Windows. The native title bar is removed (the X11 backend turns the
    /// request into a Motif hint). The frosted buttons in ShellPage take the place of the caption buttons.
    /// The window opens filling the work area.
    ///
    /// <para>If opening at a stored frame is ever brought back, Linux needs more than Windows does. Linux
    /// has no user32 virtual-screen metrics, so a frame can be checked for size but must be trusted for
    /// position. A window left on a monitor that is now unplugged would come back off the side of the
    /// desktop. Reading the X root window's size would close that gap.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static void RestoreLinux(Window window)
    {
        try
        {
            if (window.AppWindow is not { } appWindow)
                return;

            var presenter = RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(appWindow);

            // Borderless, the same look as Windows: no native title bar and no native caption buttons.
            presenter?.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

            // Fills the work area, whatever was stored. See the class remarks.
            presenter?.Maximize();
        }
        catch { /* The window opens at the system's own size, which is a working state. */ }
    }

    // ── the crash guard ──

    private static int ReadAttempts()
    {
        try
        {
            return File.Exists(GuardPath) && int.TryParse(File.ReadAllText(GuardPath), out var count)
                ? count
                : 0;
        }
        catch { return 0; }
    }

    private static void WriteAttempts(int count)
    {
        try
        {
            Directory.CreateDirectory(LocalData.Dir);
            File.WriteAllText(GuardPath, count.ToString());
        }
        catch { }
    }

    private static void ClearAttempts()
    {
        try { File.Delete(GuardPath); } catch { }
    }
}
