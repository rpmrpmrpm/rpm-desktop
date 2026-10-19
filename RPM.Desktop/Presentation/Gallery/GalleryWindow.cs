#if DEBUG
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> The screen gallery in its own window. It sits beside the running app rather than taking
/// over the app's window, so the main window can show a real screen (the seeded workbench) while the
/// gallery is used to jump between screens. There is only one instance. The <c>--gallery</c> launch
/// opens it, and closing the main window closes it too. </summary>
public static class GalleryWindow
{
    private static Window? _instance;

    public static void OpenOrFocus()
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        var window = new Window { Title = "rpm Gallery" };
        var page = new GalleryPage();
        window.Content = page;
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // A narrow panel docked to the left edge. AppWindow.Resize takes physical pixels, so scale by
        // the display factor, or the window is tiny on Retina screens. Uno on macOS has no WorkArea, so
        // an over-tall value is picked, and the OS clips it to the screen.
        page.Loaded += (_, _) =>
        {
            try
            {
                var scale = page.XamlRoot?.RasterizationScale ?? 1.0;
                window.AppWindow?.Resize(new Windows.Graphics.SizeInt32
                {
                    Width = (int)(340 * scale),
                    Height = 2000,
                });
                window.AppWindow?.Move(new Windows.Graphics.PointInt32 { X = 0, Y = 0 });
            }
            catch { /* sizing is cosmetic */ }
        };

        window.Closed += (_, _) => _instance = null;
        _instance = window;
        window.Activate();
    }

    /// <summary>Closes the gallery window if it is open. It is called when the main window closes, so
    /// the tool never outlives the app it belongs to.</summary>
    public static void Close()
    {
        var window = _instance;
        _instance = null;
        try { window?.Close(); } catch { }
    }
}
#endif
