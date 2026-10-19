using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>macOS window chrome, using ObjC interop (the same approach as FolderPickerService). It is used
/// to drag a borderless window. AppWindow.Move aborts on Uno's Mac host, because NSWindow.setFrame throws
/// from managed code. setMovableByWindowBackground would take over the whole Skia surface, so the sliders
/// could not be grabbed. performWindowDragWithEvent: is the correct method. AppKit runs its own drag loop
/// from the current mouse-down, and that loop only starts where this method is called, which is the title
/// bars.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindowChrome
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsg(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMsgPtr(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsgPtrRet(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsgIndex(nint receiver, nint selector, nuint index);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMsgBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMsgLong(nint receiver, nint selector, long arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi)]
    private static extern nint SendMsgString(nint receiver, nint selector, string arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendMsgRetBool(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMsgRect(nint receiver, nint selector, CGRect rect);

    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern nint CGPathCreateMutable();

    [DllImport(CoreGraphics)]
    private static extern void CGPathAddRoundedRect(
        nint path, nint transform, CGRect rect, double cornerWidth, double cornerHeight);

    [DllImport(CoreGraphics)]
    private static extern void CGPathRelease(nint path);

    [DllImport(CoreGraphics)]
    private static extern nint CGColorCreateGenericRGB(double r, double g, double b, double a);

    [DllImport(CoreGraphics)]
    private static extern void CGColorRelease(nint color);

    /// <summary>The app's NSWindow with the given title, or 0. Matching by title is only safe because the
    /// app has one window. If two windows had the same title, the older one would be returned.
    /// </summary>
    internal static nint FindWindowHandle(string title) => FindWindow(title);

    private static nint FindWindow(string title)
    {
        var app = SendMsg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        if (app == 0) return 0;
        var windows = SendMsg(app, sel_registerName("windows"));
        if (windows == 0) return 0;
        var count = (long)SendMsg(windows, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
        {
            var win = SendMsgIndex(windows, sel_registerName("objectAtIndex:"), i);
            if (win == 0) continue;
            var t = SendMsg(win, sel_registerName("title"));
            var cstr = t == 0 ? 0 : SendMsg(t, sel_registerName("UTF8String"));
            if (cstr != 0 && Marshal.PtrToStringUTF8(cstr) == title)
                return win;
        }
        return 0;
    }

    /// <summary>Runs the window's content up under the title bar so the artwork header bleeds behind the
    /// traffic lights (macOS full-size content view + transparent, title-less bar). The buttons stay
    /// on top and clickable; only the bar's own background goes away. Best-effort.
    /// </summary>
    public static void ExtendContentUnderTitlebar(string title) => ExtendContentUnderTitlebar(FindWindow(title));

    public static void ExtendContentUnderTitlebar(nint win)
    {
        if (win == 0) return;

        // NSWindowStyleMaskFullSizeContentView = 1 << 15. Added to whatever mask the Skia host set.
        const nuint fullSizeContentView = 1 << 15;
        var mask = (nuint)SendMsg(win, sel_registerName("styleMask"));
        SendMsgIndex(win, sel_registerName("setStyleMask:"), mask | fullSizeContentView);

        SendMsgBool(win, sel_registerName("setTitlebarAppearsTransparent:"), true);
        SendMsgIndex(win, sel_registerName("setTitleVisibility:"), 1);   // NSWindowTitleHidden
    }

    /// <summary>
    /// Hands the window's size and position to AppKit's own memory, under <paramref name="key"/>.
    ///
    /// <para>This is how a Cocoa app remembers where its window was. Here it is the only way that works.
    /// The managed route (read the frame, then write it back with <c>AppWindow.Move</c> or
    /// <c>MoveAndResize</c>) aborts the process on Uno's Mac host. Uno also implements neither
    /// <c>DisplayArea.WorkArea</c> nor <c>MoveAndResize</c>, so there is no work area to check a stored
    /// frame against. AppKit provides both. It writes the frame to the defaults on every move and resize,
    /// and when it restores the frame it fits the frame to the screens that exist now. So a window last
    /// left on a monitor that is not connected, or on a screen whose resolution has changed, comes back
    /// where it can be seen and grabbed, not off the side of the desktop.</para>
    ///
    /// <para>Called once, as the window comes up: <see cref="RememberFrame"/> starts the saving,
    /// <see cref="RestoreFrame"/> applies what was saved. Nothing has to be written at exit.</para>
    /// </summary>
    public static void RememberFrame(nint win, string key)
    {
        if (win == 0)
            return;
        var name = NSString(key);
        if (name == 0)
            return;
        SendMsgPtr(win, sel_registerName("setFrameAutosaveName:"), name);
    }

    /// <summary>Puts the window back where AppKit last saw it. False when nothing is saved under that name,
    /// for example on a first run or after the defaults are cleared.</summary>
    public static bool RestoreFrame(nint win, string key)
    {
        if (win == 0)
            return false;
        var name = NSString(key);
        return name != 0 && SendMsgPtrRet(win, sel_registerName("setFrameUsingName:"), name) != 0;
    }

    /// <summary>An autoreleased NSString from a managed one. Only ever handed short ASCII keys.</summary>
    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToHGlobalAnsi(value);
        try { return SendMsgPtrRet(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8); }
        catch { return 0; }
        finally { Marshal.FreeHGlobal(utf8); }
    }

    /// <summary>Native macOS zoom. This toggles the window between its standard frame and its previous user
    /// size, the same as the green title bar button. The standard frame fills the visible area of the
    /// screen and keeps the title bar. It is not full screen. This goes through AppKit's own path, so it
    /// does not reach the managed NSWindow.setFrame call, which aborts on Uno's Mac host when
    /// AppWindow.Move or MoveAndResize is used.</summary>
    public static void ZoomWindow(string title) => ZoomWindow(FindWindow(title));

    public static void ZoomWindow(nint win)
    {
        if (win == 0) return;
        SendMsgPtr(win, sel_registerName("zoom:"), 0);   // zoom:nil
    }

    /// <summary>True while the window sits at its zoomed (maximised) frame.</summary>
    public static bool IsWindowZoomed(string title) => IsWindowZoomed(FindWindow(title));

    public static bool IsWindowZoomed(nint win)
        => win != 0 && SendMsg(win, sel_registerName("isZoomed")) != 0;

    // NSWindowStyleMaskFullScreen. Set by AppKit while the window is in its own full-screen Space.
    private const nuint FullScreenMask = 1 << 14;

    /// <summary>Native macOS full screen (the green button's "Enter Full Screen"). The window takes over
    /// its display in its own Space, with the title bar and menu bar included. AppKit runs the transition,
    /// so this does not use the managed NSWindow.setFrame path, which AppWindow.MoveAndResize aborts on.
    /// Calling it again is safe. The toggle happens only when the window is not already in the requested
    /// state.</summary>
    public static void SetFullScreen(string title, bool on) => SetFullScreen(FindWindow(title), on);

    public static void SetFullScreen(nint win, bool on)
    {
        if (win == 0 || IsFullScreen(win) == on) return;
        SendMsgPtr(win, sel_registerName("toggleFullScreen:"), 0);   // toggleFullScreen:nil
    }

    /// <summary>True while the window is in a native full-screen Space.</summary>
    public static bool IsFullScreen(string title) => IsFullScreen(FindWindow(title));

    public static bool IsFullScreen(nint win)
        => win != 0 && ((nuint)SendMsg(win, sel_registerName("styleMask")) & FullScreenMask) != 0;

    /// <summary>Best-effort: makes the NSWindow with the given title non-opaque with a clear
    /// background so only the rounded panels show (the gaps between them become see-through).
    /// No-op if the platform can't composite alpha.</summary>
    public static void MakeWindowTransparent(string title) => MakeWindowTransparent(FindWindow(title));

    public static void MakeWindowTransparent(nint win)
    {
        if (win == 0) return;

        SendMsgBool(win, sel_registerName("setOpaque:"), false);
        var clear = SendMsg(objc_getClass("NSColor"), sel_registerName("clearColor"));
        if (clear != 0) SendMsgPtr(win, sel_registerName("setBackgroundColor:"), clear);
        SendMsgBool(win, sel_registerName("setHasShadow:"), false);

        // The Skia/Metal backing layer is opaque by default. Make it non-opaque, so the clear regions
        // composite through to the desktop.
        var content = SendMsg(win, sel_registerName("contentView"));
        if (content != 0)
            ClearViewLayers(content, depth: 0);
    }

    /// <summary>Stops the window server stretching the last frame across a window that has just been made
    /// bigger.
    /// </summary>
    /// <remarks>
    /// <para>A resize is not atomic with the drawing. <c>setFrame:</c> takes
    /// effect at once, but the app cannot present a new frame until it has laid out. So for one composite
    /// the window server shows the frame it already has. By default it SCALES that frame to the new size.
    /// On the player this looks like the whole casing being pulled sideways, then snapping back when the
    /// library pane paints.</para>
    ///
    /// <para>Pinned to the top left instead, the same composite leaves the casing at its real size, and the
    /// new strip stays empty until it is drawn. That looks like a window growing, not a picture distorting.
    /// It is set on the content view's layer and on the layers under it, because the drawing surface is one
    /// of those.</para>
    /// </remarks>
    /// <param name="on">True while the player is up. False puts back what a
    /// window ordinarily does, which is to scale its last frame across a resize. That is right for the
    /// library, whose content really does stretch to the window.</param>
    public static void PinContentsTopLeft(nint win, bool on)
    {
        if (win == 0)
            return;

        var content = SendMsg(win, sel_registerName("contentView"));
        if (content == 0)
            return;

        // NSViewLayerContentsPlacementTopLeft (11), against ScaleAxesIndependently (0). This is the
        // AppKit-level setting, so a layer that the host replaces keeps it instead of getting the default.
        SendMsgLong(content, sel_registerName("setLayerContentsPlacement:"), on ? 11 : 0);

        var gravity = NsString(on ? "topLeft" : "resize");   // kCAGravityTopLeft / kCAGravityResize
        if (gravity != 0)
            PinLayers(content, 0, gravity);
    }

    private static void PinLayers(nint view, int depth, nint gravity)
    {
        if (view == 0 || depth > 4) return;

        var layer = SendMsg(view, sel_registerName("layer"));
        if (layer != 0)
            SendMsgPtr(layer, sel_registerName("setContentsGravity:"), gravity);

        var subs = SendMsg(view, sel_registerName("subviews"));
        if (subs == 0) return;
        var count = (long)SendMsg(subs, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
            PinLayers(SendMsgIndex(subs, sel_registerName("objectAtIndex:"), i), depth + 1, gravity);
    }

    private static nint NsString(string value)
    {
        var cls = objc_getClass("NSString");
        return cls == 0 ? 0 : SendMsgString(cls, sel_registerName("stringWithUTF8String:"), value);
    }

    /// <summary>Cuts the window down to the given rectangles, so everything else is not the window at all.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cut rather than transparency.</b> The window is see-through. It
    /// is not opaque, its colour is clear, and its layers are not opaque. These facts hold, and none of
    /// them changes. The problem is that the Skia surface is never cleared before a frame. Whatever the
    /// view tree does not paint keeps what the buffer held last. The gaps between the player's panels paint
    /// nothing, by design. So they show the last thing that covered the whole window, such as the splash at
    /// startup or the library after a switch. This was shown by painting the splash green, and the gaps
    /// then flickered green. Nothing inside the tree can fix this. An element's drawing is composited OVER
    /// the surface, so clearing to transparent composites to no change.</para>
    ///
    /// <para><b>Why this is written out by hand.</b> Uno's own entry point for this,
    /// <c>uno_window_clip_svg</c>, does nothing on this version. When asked to cut the window to a single
    /// 100x100 square, it left the window whole and the layer unmasked. Its code applies the mask to the
    /// layers of <c>renderingView.subviews</c>, and that collection is empty here.</para>
    ///
    /// <para>The mask goes on the content view's layer. That layer stays the same object, unlike layers the
    /// host rebuilds on each frame or resize. A CALayer mask clips its layer and every sublayer under it.
    /// That includes the Skia surface and any native view beside it.</para>
    ///
    /// <para>Implicit animation is turned off for the whole change. By default, a CALayer property set
    /// outside an explicit transaction animates over a quarter of a second. A mask that eases into place
    /// makes the gaps wipe open each time the playlist grows.</para>
    /// </remarks>
    /// <param name="panels">Rectangles in the client area, logical units, top-left origin. Empty gives
    /// the whole window back.</param>
    /// <param name="radius">The panels' corner radius, so the cut matches the
    /// drawing.</param>
    /// <param name="width">The client area's width in logical units.</param>
    /// <param name="height">Its height. Needed because a plain NSView's layer counts from the BOTTOM
    /// left, and the rectangles are stated from the top.</param>
    public static void MaskToPanels(
        nint win, IReadOnlyList<Windows.Foundation.Rect> panels, double radius, double width, double height)
    {
        if (win == 0)
            return;

        var content = SendMsg(win, sel_registerName("contentView"));
        if (content == 0)
            return;

        SendMsgBool(content, sel_registerName("setWantsLayer:"), true);
        var layer = SendMsg(content, sel_registerName("layer"));
        if (layer == 0)
            return;

        var transaction = objc_getClass("CATransaction");
        SendMsg(transaction, sel_registerName("begin"));
        SendMsgBool(transaction, sel_registerName("setDisableActions:"), true);
        try
        {
            if (panels is null || panels.Count == 0)
            {
                SendMsgPtr(layer, sel_registerName("setMask:"), 0);
                return;
            }

            // A plain NSView is not flipped, so its layer's origin is the bottom left, and the rectangles
            // must be turned over. The flipped state is asked for, not assumed. The host's own view is
            // flipped, so the answer changes if the content view is ever replaced with one of those.
            var flipped = SendMsgRetBool(content, sel_registerName("isFlipped"));

            var path = CGPathCreateMutable();
            if (path == 0)
                return;

            foreach (var p in panels)
            {
                var r = Math.Max(0, Math.Min(radius, Math.Min(p.Width, p.Height) / 2));
                var y = flipped ? p.Y : height - (p.Y + p.Height);
                CGPathAddRoundedRect(path, 0, new CGRect(p.X, y, p.Width, p.Height), r, r);
            }

            var shape = SendMsg(SendMsg(objc_getClass("CAShapeLayer"), sel_registerName("alloc")),
                                sel_registerName("init"));
            if (shape == 0)
            {
                CGPathRelease(path);
                return;
            }

            SendMsgRect(shape, sel_registerName("setFrame:"), new CGRect(0, 0, width, height));
            SendMsgPtr(shape, sel_registerName("setPath:"), path);

            var opaque = CGColorCreateGenericRGB(0, 0, 0, 1);
            if (opaque != 0)
            {
                SendMsgPtr(shape, sel_registerName("setFillColor:"), opaque);
                CGColorRelease(opaque);
            }

            SendMsgPtr(layer, sel_registerName("setMask:"), shape);

            // The layer retains the mask; this balances the alloc above.
            SendMsg(shape, sel_registerName("release"));
            CGPathRelease(path);
        }
        finally
        {
            SendMsg(transaction, sel_registerName("commit"));
        }
    }

    /// <summary>Puts back what <see cref="MakeWindowTransparent"/> removed: an opaque window with its
    /// shadow and its system background colour.
    ///
    /// <para>The main window can switch to a skin window and must come back from it (see
    /// <see cref="Presentation.Shell.SkinWindow"/>). A window that is transparent for as long as a view is
    /// shown needs this.</para>
    ///
    /// <para>The layers are made opaque again too. A CALayer that stays non-opaque looks the same, but the
    /// compositor must blend it on every frame over the whole surface. That is costly while the library
    /// scrolls a wall of covers.</para>
    /// </summary>
    public static void MakeWindowOpaque(nint win)
    {
        if (win == 0) return;

        SendMsgBool(win, sel_registerName("setOpaque:"), true);
        var back = SendMsg(objc_getClass("NSColor"), sel_registerName("windowBackgroundColor"));
        if (back != 0) SendMsgPtr(win, sel_registerName("setBackgroundColor:"), back);
        SendMsgBool(win, sel_registerName("setHasShadow:"), true);

        var content = SendMsg(win, sel_registerName("contentView"));
        if (content != 0)
            SetViewLayersOpaque(content, depth: 0);
    }

    private static void SetViewLayersOpaque(nint view, int depth)
    {
        if (view == 0 || depth > 4) return;
        var layer = SendMsg(view, sel_registerName("layer"));
        if (layer != 0) SendMsgBool(layer, sel_registerName("setOpaque:"), true);

        var subs = SendMsg(view, sel_registerName("subviews"));
        if (subs == 0) return;
        var count = (long)SendMsg(subs, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
            SetViewLayersOpaque(SendMsgIndex(subs, sel_registerName("objectAtIndex:"), i), depth + 1);
    }

    /// <summary>Hides or shows the traffic lights.
    /// </summary>
    /// <remarks>The buttons are hidden, not
    /// removed from the style mask. Removing <c>NSWindowStyleMaskTitled</c> would also remove the window's
    /// ability to be dragged, resized and made key. A borderless window on this platform is a different
    /// kind of window, and switching the app between the two often is not practical. Hiding the three
    /// buttons leaves an ordinary window with no buttons drawn on it, which is what a skin needs.
    /// </remarks>
    public static void SetTrafficLightsHidden(nint win, bool hidden)
    {
        if (win == 0) return;
        // NSWindowCloseButton = 0, NSWindowMiniaturizeButton = 1, NSWindowZoomButton = 2.
        for (nuint i = 0; i <= 2; i++)
        {
            var button = SendMsgIndex(win, sel_registerName("standardWindowButton:"), i);
            if (button != 0)
                SendMsgBool(button, sel_registerName("setHidden:"), hidden);
        }
    }

    /// <summary>Recursively forces every view's CALayer non-opaque so the Metal-backed Skia layer
    /// (a subview's layer, not the contentView's) composites its clear regions through.</summary>
    private static void ClearViewLayers(nint view, int depth)
    {
        if (view == 0 || depth > 4) return;
        SendMsgBool(view, sel_registerName("setWantsLayer:"), true);
        var layer = SendMsg(view, sel_registerName("layer"));
        if (layer != 0) SendMsgBool(layer, sel_registerName("setOpaque:"), false);

        var subs = SendMsg(view, sel_registerName("subviews"));
        if (subs == 0) return;
        var count = (long)SendMsg(subs, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
            ClearViewLayers(SendMsgIndex(subs, sel_registerName("objectAtIndex:"), i), depth + 1);
    }

    /// <summary>Begins AppKit's native window drag for the key window using the mouse event being
    /// processed right now. Call synchronously from a title-bar PointerPressed handler.</summary>
    public static void BeginWindowDrag()
    {
        var app = SendMsg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        if (app == 0) return;
        var ev = SendMsg(app, sel_registerName("currentEvent"));
        if (ev == 0) return;
        // Use the window that the mouse event belongs to, not keyWindow. A borderless NSWindow cannot
        // become key, so keyWindow is the main window, and dragging would move that one instead.
        var win = SendMsg(ev, sel_registerName("window"));
        if (win == 0) return;
        SendMsgPtr(win, sel_registerName("performWindowDragWithEvent:"), ev);
    }
}
