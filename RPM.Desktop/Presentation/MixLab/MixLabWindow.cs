#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The mix lab in its own window, beside the running app. The main window stays a normal player. The
/// workbench sits next to it and shows what the DJ is deciding.
///
/// <para>There is only one instance. It opens when a set starts on a debug build, and it closes when
/// the set ends. Closing the main window closes it too. A debugging tool must not stay open after the
/// app it is inspecting has closed.</para>
/// </summary>
public static class MixLabWindow
{
    private static Window? _instance;

    /// <summary>Whose behaviour the open window is changing, so closing it can put it back.</summary>
    private static DjService? _held;

    public static void OpenOrFocus(DjService dj, PlaybackService playback)
    {
        ArgumentNullException.ThrowIfNull(dj);
        ArgumentNullException.ThrowIfNull(playback);

        // From here on, a set reads its first records before it plays them. See
        // DjService.HoldForAnalysis. This is set before the window exists, because the set that is
        // about to start is the one the lab was opened to look at.
        dj.HoldForAnalysis = true;
        _held = dj;

        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        var window = new Window { Title = "rpm Mix Lab" };
        var page = new MixLabPage(dj, playback);
        window.Content = page;
        page.RequestedTheme = ElementTheme.Dark;

        // The window is wide rather than tall, because everything in it is a record on a clock, and a
        // clock needs width. AppWindow.Resize uses physical pixels, so the size is scaled by the
        // display factor. Without that, the window comes up tiny on a high-density screen.
        page.Loaded += (_, _) =>
        {
            try
            {
                var scale = page.XamlRoot?.RasterizationScale ?? 1.0;
                window.AppWindow?.Resize(new Windows.Graphics.SizeInt32
                {
                    Width = (int)(1180 * scale),
                    Height = (int)(820 * scale),
                });
            }
            catch { /* sizing is cosmetic */ }
        };

        window.Closed += (_, _) => Close();
        _instance = window;
        window.Activate();
    }

    /// <summary>Closes the lab, if it is open. Called when the set ends and when the app's own window
    /// closes.</summary>
    public static void Close()
    {
        var window = _instance;
        _instance = null;

        // When the lab closes, sets go back to starting at once on whatever is known, and reading the
        // records after the music has begun. A listener wants this, and it is what every set does when
        // the lab is not open.
        if (_held is { } dj)
        {
            dj.HoldForAnalysis = false;
            _held = null;
        }

        try { window?.Close(); } catch { }
    }
}
#endif
#endif
