#if DEBUG
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Dev;

/// <summary>The genre photograph placer, in a window of its own beside the running app. It works like the
/// gallery, for the same reason: the main window goes on being the app while this tool is used to work
/// through the shelves. There is only one instance. <c>--place-genres</c> opens it, and closing the main
/// window closes it too.
/// </summary>
public static class GenrePlacerWindow
{
    private static Window? _instance;

    public static void OpenOrFocus()
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        var window = new Window { Title = "rpm — place genre photographs" };
        var page = new GenrePlacerPage();
        window.Content = page;
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // The window is wide and not very tall, because that is what it holds: a band seven times as wide
        // as it is high, the cut under it, and two rows of sliders. AppWindow.Resize uses physical pixels.
        // So it is scaled by the display factor, or it is a postage stamp on a high-density screen. The
        // window is not moved. On this platform that call often aborts the process, so the opening position
        // is not worth it.
        page.Loaded += (_, _) =>
        {
            try
            {
                var scale = page.XamlRoot?.RasterizationScale ?? 1.0;
                window.AppWindow?.Resize(new Windows.Graphics.SizeInt32
                {
                    Width = (int)(1180 * scale),
                    Height = (int)(720 * scale),
                });
            }
            catch
            {
                // Sizing is cosmetic. The window is usable at whatever size the shell gives it.
            }
        };

        window.Closed += (_, _) => _instance = null;
        _instance = window;
        window.Activate();
    }

    /// <summary>Closes the placer if it is open. This is called when the main window closes, so the tool
    /// never outlives the app it belongs to.</summary>
    public static void Close()
    {
        var window = _instance;
        _instance = null;
        try { window?.Close(); } catch { }
    }
}
#endif
