using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// Turns the app's window into the skin's window, and back.
///
/// <para>RPaMp is a view and not a popout, so there is no second window for the player. The app's own
/// window becomes the player window while that view is up: no chrome, no opaque background behind the
/// gaps between the casing's panels, and the size of the casing and not of a library. Every change here
/// has a matching change that undoes it. That is the difficulty: a window made borderless and
/// see-through must come back as an ordinary window, on a platform where not all window flags can be
/// reversed.</para>
///
/// <para><b>macOS.</b> The traffic lights are HIDDEN and the style mask is not changed (see
/// <see cref="MacWindowChrome.SetTrafficLightsHidden"/>). A titled window that loses
/// <c>NSWindowStyleMaskTitled</c> also loses dragging, resizing and key status. The presenter's
/// border/title-bar switch is therefore NOT used here: on this head it changes that same mask, and the
/// way back asks for a window with no title bar, so the bit is never restored. Transparency uses the
/// <c>setOpaque:</c> and clear-colour pair, and its inverse on the way out.</para>
///
/// <para><b>Windows.</b> The presenter's own border/title-bar switch is used, and it can be reversed.
/// The window is not made see-through there: DWM composites a live backdrop only behind a FRAMED window,
/// so a window without chrome would show the gaps black and not the desktop.</para>
///
/// <para>The size and position of the window are saved on the way in and restored on the way out. They
/// are saved ONCE: if the view is entered twice without leaving, the skin's own size must not be saved
/// as the size to return to.</para>
/// </summary>
internal static class SkinWindow
{
    /// <summary>The casing's fixed width, and its usual height once it has laid out. The window is
    /// fitted to the view's measured size when there is one, and to these until then. Without them the
    /// window would open at the library's size and then change size a second time.</summary>
    private const double SkinWidth = 440, SkinHeightGuess = 648;

    private static bool _on;
    private static Windows.Graphics.RectInt32? _restore;

    /// <summary>Whether the launch placement was postponed because the skin had the window. See
    /// <see cref="PlaceLibrary"/>.</summary>
    private static bool _placeLibraryOnLeave;

    /// <summary>Whether the window is currently dressed as the skin.</summary>
    public static bool IsOn => _on;

    /// <summary>
    /// Gives the window the library's chrome and fills the work area with it
    /// (<see cref="Services.Shell.WindowPlacement.Restore"/>), or, while the window is dressed as the
    /// skin, does that when the skin is put away instead.
    /// </summary>
    /// <remarks>
    /// The launch placement must go through here and nowhere else. Two things at launch change the
    /// window, and they can arrive in either order: the session restores the player view
    /// (<c>MainPage.RestoreAppView</c>, queued on the dispatcher), and the placement is applied at the
    /// reveal. If the placement comes first, it works, as in a switch at run time. If the skin comes
    /// first and the placement is applied to the skin's window, the window gets a border and is
    /// maximized, the skin fits itself back to 440 wide inside a window still marked maximized, the cut
    /// is worked out for the wrong frame, and the window shows a title strip with nothing under it.
    ///
    /// <para>Either order is safe because this method reads <see cref="_on"/> when it is called, on the
    /// same thread that sets it. It does not depend on which of the two was queued first.</para>
    /// </remarks>
    public static void PlaceLibrary(Window window)
    {
        if (_on)
        {
            _placeLibraryOnLeave = true;
            return;
        }

        Services.Shell.WindowPlacement.Restore(window);
    }

    /// <summary>
    /// Dresses the window as the skin, or puts it back.
    /// </summary>
    /// <param name="skin">The casing, so the window can be fitted to what it actually measured. Null
    /// falls back to the nominal size above.</param>
    public static void Set(bool on, RPaMpView? skin = null)
    {
        if (on == _on)
        {
            if (on)
                Fit(skin);   // already dressed: the casing may have settled at a new height
            return;
        }
        _on = on;

        if (App.AppWindow is not { } window)
            return;

        try
        {
            if (on)
                Enter(window, skin);
            else
                Leave(window);
        }
        catch { /* best effort: a window that cannot be undressed is only a cosmetic problem */ }
    }

    private static void Enter(Window window, RPaMpView? skin)
    {
        var aw = window.AppWindow;

        // Where to come back to. Saved once only (see the class remarks).
        if (aw is not null && _restore is null)
            _restore = new Windows.Graphics.RectInt32
            {
                X = aw.Position.X,
                Y = aw.Position.Y,
                Width = aw.Size.Width,
                Height = aw.Size.Height,
            };

        // No chrome and no resizing: the casing has a fixed width, and a window that can be dragged
        // wider than the casing would leave the skin in the middle of empty space.
        if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(aw) is { } op)
        {
            // NOT on macOS. There the presenter's switch changes the window's style mask, and a window
            // that loses NSWindowStyleMaskTitled loses its traffic lights, dragging and resizing (see
            // the class remarks). They do not come back either, because the way out asks for a window
            // with a border and NO title bar, which is how this app runs, and that does not restore
            // the bit. The library would have no traffic lights and could not be dragged. The chrome
            // is removed natively instead, by hiding the three buttons.
            if (!OperatingSystem.IsMacOS())
                try { op.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false); } catch { }

            try { op.IsResizable = false; } catch { }
            try { op.IsMaximizable = false; } catch { }
        }

        // Clear the window's own fill, then the platform's. Both are needed: Uno paints a theme
        // background on the window root, and it would fill the gaps between the panels with opaque
        // black even when the NSWindow's own colour is clear.
        Services.Platform.WindowBackground.Apply(window, new SolidColorBrush(Microsoft.UI.Colors.Transparent));
        // The walk starts from the window's CONTENT and not from the skin. The skin has only just been
        // given a parent, and the walk needs a chain that certainly exists. The shell is the window's
        // content from launch.
        if (window.Content is FrameworkElement content)
            ClearRootBackground(content);

        if (OperatingSystem.IsMacOS())
        {
            var win = MacWindowChrome.FindWindowHandle(MainWindowTitle);
            if (win != 0)
            {
                MacWindowChrome.SetTrafficLightsHidden(win, true);
                MacWindowChrome.MakeWindowTransparent(win);

                // The casing grows when the library pane opens, and the window grows before the app can
                // draw. The contents are pinned to the top left and not scaled, so that one composite
                // is a wider window and not a stretched player. See MacWindowChrome.PinContentsTopLeft.
                MacWindowChrome.PinContentsTopLeft(win, true);
            }
        }

        // The compositor's own two edges, which no style flag controls: the thin line that Windows 11
        // draws round every window, and the rounded corners. On the skin the line would be a pale
        // rectangle round the casing, and the rounding would cut into the top panel's own corner.
        if (Services.Platform.NativeWindow.Handle(window) is { } edges)
            Services.Platform.WindowEdges.Square(edges, square: true, "skin");

        Fit(skin);
    }

    private static void Leave(Window window)
    {
        var aw = window.AppWindow;

        if (OperatingSystem.IsMacOS())
        {
            var win = MacWindowChrome.FindWindowHandle(MainWindowTitle);
            if (win != 0)
            {
                // The whole window comes back FIRST, for the reason given at the WinUI call below: a
                // library drawn inside the player's shape would have three rectangles missing.
                MacWindowChrome.MaskToPanels(win, Array.Empty<Windows.Foundation.Rect>(), 0, 0, 0);
                MacWindowChrome.PinContentsTopLeft(win, false);
                MacWindowChrome.MakeWindowOpaque(win);
                MacWindowChrome.SetTrafficLightsHidden(win, false);
            }
        }

        // The whole window comes back first, before the size and the fill: a library drawn inside the
        // skin's shape would have three rectangles of itself missing.
        if (Services.Platform.NativeWindow.Handle(window) is { } shape)
            Services.Platform.WindowShape.Cut(shape, Array.Empty<Windows.Foundation.Rect>(), 0, 1);

        // The edges that the compositor draws are restored with the rest of the chrome.
        if (Services.Platform.NativeWindow.Handle(window) is { } edges)
            Services.Platform.WindowEdges.Square(edges, square: false, "skin");

        // The shell's own dark background is restored BEFORE the size, so the window is never a
        // library-sized transparent hole in the desktop, even for a moment.
        Services.Platform.WindowBackground.Apply(window, new SolidColorBrush(ShellFill));
        if (window.Content is FrameworkElement root)
            RestoreRootBackground(root);

        // The app's ordinary chrome: the title bar stays removed (that is how this app runs, see
        // App.OnLaunched) and the sizing border comes back. This mirrors the guard in Enter: on macOS
        // there is nothing to restore, because nothing was removed.
        if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(aw) is { } op)
        {
            try { op.IsResizable = true; } catch { }
            try { op.IsMaximizable = true; } catch { }
            if (!OperatingSystem.IsMacOS())
                try { op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); } catch { }
        }

        if (_placeLibraryOnLeave)
        {
            // The launch placement, postponed while the skin had the window (see PlaceLibrary). The
            // size saved on the way in is from before the window was ever placed, and the library was
            // never shown at that size, so it is not used.
            _placeLibraryOnLeave = false;
            Services.Shell.WindowPlacement.Restore(window);
        }
        else if (_restore is { } bounds && aw is not null)
        {
            try
            {
                aw.Resize(new Windows.Graphics.SizeInt32 { Width = bounds.Width, Height = bounds.Height });
                aw.Move(new Windows.Graphics.PointInt32 { X = bounds.X, Y = bounds.Y });
            }
            catch { /* Uno on macOS aborts on some setFrame paths; the window keeps the skin's size */ }
        }
        _restore = null;
    }

    /// <summary>Sizes the app's window to the casing, when the window is dressed as the skin.</summary>
    private static void Fit(RPaMpView? skin)
    {
        if (_on && App.AppWindow is { } window)
            FitTo(window, skin);
    }

    /// <summary>
    /// Sizes a window to the casing it is showing.
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.Resize</c> uses PHYSICAL pixels, so the size is scaled by the display factor.
    /// Without that the window is half size on a Retina screen. This is called again whenever the casing
    /// settles at a new height (the playlist grows with the queue), which is why it is separate from
    /// <see cref="Enter"/>.
    ///
    /// <para>The <c>--rpamp</c> harness also uses this. It mounts the casing as the window's content and
    /// has the same window to fit. There is one copy of this code, so the physical pixel rule and the
    /// client area rule below are written once.</para>
    /// </remarks>
    internal static void FitTo(Window window, RPaMpView? skin)
    {
        if (window.AppWindow is not { } aw)
            return;

        // Use the CASING's size, not the control's. The control fills the window, so its own bounds
        // are the window's, and measuring that would fit the window to itself and never shrink it.
        // ContentSize is what the casing WANTS: the fixed width, and the height measured with no
        // limit. A size read from the arranged casing has already been limited by the window, and
        // feeding that back would shrink the window on every pass.
        var scale = skin?.XamlRoot?.RasterizationScale ?? 1.0;
        var measured = skin?.ContentSize ?? default;
        var width = measured.Width > 1 ? measured.Width : SkinWidth;
        var height = measured.Height > 1 ? measured.Height : SkinHeightGuess;

        var size = new Windows.Graphics.SizeInt32
        {
            Width = (int)Math.Round(width * scale),
            Height = (int)Math.Round(height * scale),
        };

        try
        {
#if RPM_WINUI
            // Set the CLIENT area, not the whole window. `Resize` sets the window RECT, and a Windows
            // window without a border still keeps 6 pixels of frame. A window resized to 440 would
            // give the casing 434 and cut off its right edge (window 440x643 gives client 434x637).
            //
            // Asking for the client size is not just a tidier way to add the frame back. The frame can
            // only be worked out by comparing the size the WINDOW reports (updated when the resize
            // message arrives) with the size the CONTENT reports (updated at the next layout). These
            // two are not from the same moment, so the size would keep changing (440, 446, 452, 440 and
            // so on, about thirty times a second), because every correction starts another resize and
            // half the readings pair a new window with an old content. This call states the number
            // that matters and reads nothing back.
            aw.ResizeClient(size);
#else
            aw.Resize(size);
#endif
        }
        catch { /* see Leave */ }

        Cut(window, skin);
    }

    /// <summary>
    /// Takes the gaps between the panels out of the window, so the desktop is behind them.
    /// </summary>
    /// <remarks>
    /// This is for the WinUI head, and <see cref="Services.Platform.WindowShape"/> explains why: that
    /// head has no transparent window, so the gaps are cut out of the window and not painted clear. The
    /// mac head clears the NSWindow's own colour instead (<see cref="MacWindowChrome"/>) and must NOT be
    /// cut as well, because a region there would remove the shadow and the rounded corners.
    ///
    /// <para>The cut is made after the resize and made again each time the casing settles, because the
    /// shape follows the casing: the playlist grows with the queue and the library opens beside the
    /// stack. The panels' rectangles are read each time and not saved, so a cut made from an old layout
    /// is replaced by the next one.</para>
    /// </remarks>
    private static void Cut(Window window, RPaMpView? skin)
    {
        if (OperatingSystem.IsMacOS())
        {
            // This head also cuts, for a different reason than the WinUI head below. The window here IS
            // see-through, but the surface is not: it is never cleared before a frame, so the gaps
            // keep the last thing that covered the whole window. See MacWindowChrome.MaskToPanels.
            var win = MacWindowChrome.FindWindowHandle(MainWindowTitle);
            if (win == 0 || skin is null)
                return;

            var size = skin.ContentSize;
            MacWindowChrome.MaskToPanels(
                win, skin.PanelRects(), skin.PanelCornerRadius, size.Width, size.Height);

            // Set again together with the mask, for the same reason: a layer that the host rebuilds
            // for a new size gets the default, and the default scales the old frame.
            MacWindowChrome.PinContentsTopLeft(win, true);
            return;
        }

#if RPM_WINUI
        if (skin is null)
            return;
        if (Services.Platform.NativeWindow.Handle(window) is not { } hwnd)
            return;

        Services.Platform.WindowShape.Cut(
            hwnd,
            skin.PanelRects(),
            skin.PanelCornerRadius,
            skin.XamlRoot?.RasterizationScale ?? 1.0);
#endif
    }

    /// <summary>The probe title the main window is created under (App.OnLaunched).</summary>
    private const string MainWindowTitle = "rpm";

    /// <summary>The shell's own background, as the page declares it. It is restored on the window root
    /// when the skin leaves.</summary>
    private static readonly Windows.UI.Color ShellFill = Windows.UI.Color.FromArgb(0xFF, 0x0E, 0x15, 0x20);

    /// <summary>
    /// Clears the fill Uno paints on the window's ROOT panel, above the app's own content.
    /// </summary>
    /// <remarks>
    /// That panel is not part of the app's tree, so no XAML can reach it. An opaque black behind the
    /// casing would fill every gap that the transparency is meant to open. The <c>--rpamp</c> design
    /// harness also uses this, because it mounts the casing as the window's content directly.
    /// </remarks>
    internal static void ClearRootBackground(FrameworkElement view)
    {
        try
        {
            DependencyObject top = view;
            while (VisualTreeHelper.GetParent(top) is { } parent)
                top = parent;
            if (top is Panel root)
                root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        catch { /* cosmetic only: an opaque backdrop is the safe fallback */ }
    }

    private static void RestoreRootBackground(FrameworkElement view)
    {
        try
        {
            DependencyObject top = view;
            while (VisualTreeHelper.GetParent(top) is { } parent)
                top = parent;
            if (top is Panel root)
                root.Background = new SolidColorBrush(ShellFill);
        }
        catch { }
    }
}
