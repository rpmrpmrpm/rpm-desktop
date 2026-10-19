using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// What AppKit must be told to turn an Uno window into a card: no frame, see-through, glass behind it,
/// centred on the screen, above everything, and an alpha that can be faded.
///
/// <para><b>Why this is not <see cref="MacWindowChrome"/>.</b> That class dresses the app's main window,
/// and each call there is about the window the user is working in. This class is for a window that is not
/// the app. It takes no input and closes after about two seconds. Half of what it does here
/// (<c>NSWindowStyleMaskBorderless</c>, a floating level) would be wrong for the main window.</para>
///
/// <para><b>The Windows head's card is <see cref="Presentation.Startup.SplashWindow"/>.</b> Most of what it
/// does has an equivalent here. The layered window's alpha becomes <c>NSWindow.alphaValue</c>, which Core
/// Animation animates rather than stepping it. The region cut becomes a corner radius on the content view's
/// layer. The style-bit changes become one borderless mask.</para>
///
/// <para><b>The glass is the exception, and the card draws it itself.</b> Desktop acrylic has a counterpart
/// in <c>NSVisualEffectView</c>, but it cannot be reached from here. The window can be made see-through,
/// and so can its layers, but the Skia surface drawn into them still is not. With an effect view behind the
/// card, every layer still reports non-opaque, and the card samples as (0,0,0) at every point. This is the
/// problem that <see cref="MacWindowChrome.MaskToPanels"/> describes. What the view tree does not paint is
/// not transparent. It shows whatever the buffer held. So <c>SplashCard.DrawOwnGlass</c> paints a plate
/// instead, and nothing here asks AppKit for glass.</para>
///
/// <para><b>The plate imitates the Windows card, not the desktop behind it.</b> It cannot copy the desktop,
/// for the reason above. It does copy the Windows card. Thin acrylic in dark theme is a light neutral pane,
/// and the plate is painted to sit beside one. The colours, and the one number to adjust, are in
/// <c>SplashCard.xaml</c>, on the <c>Plate</c> grid.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacWindowCard
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint object_getClass(nint obj);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getSuperclass(nint cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    /// <summary>The implementation an instance of the class runs for the selector, inherited or its
    /// own.</summary>
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getMethodImplementation(nint cls, nint sel);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getInstanceMethod(nint cls, nint sel);

    /// <summary>Returns the implementation it replaced.</summary>
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint method_setImplementation(nint method, nint imp);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_setAssociatedObject(nint obj, nint key, nint value, nuint policy);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getAssociatedObject(nint obj, nint key);

    /// <summary>OBJC_ASSOCIATION_RETAIN_NONATOMIC.</summary>
    private const nuint RetainNonatomic = 1;

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendPtrRet(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendLong(nint receiver, nint selector, long arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendULong(nint receiver, nint selector, nuint arg);

    /// <summary>A CGFloat argument. It has its own entry point because a double does not travel in the same
    /// register as a pointer on arm64. Passed through the integer overload, it arrives as
    /// garbage.</summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(nint receiver, nint selector, double arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendRectBool(nint receiver, nint selector, CGRect rect,
                                            [MarshalAs(UnmanagedType.I1)] bool display);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendIndex(nint receiver, nint selector, nuint index);

    // NSWindowStyleMaskBorderless. This is not a bit to clear. Its value is 0. Setting the mask to 0
    // removes the title bar, the buttons, the resize edges and the window frame in one call.
    private const nuint Borderless = 0;

    // NSFloatingWindowLevel. The main window is activated after this one. Without this level, the main
    // window could be ordered in front of the card.
    private const long FloatingLevel = 3;

    /// <summary>Dresses the window as the card: borderless, see-through, rounded, centred, floating, and
    /// invisible.
    /// </summary>
    /// <remarks><b>Invisible on the way in, and this matters.</b> A window is drawn
    /// as soon as it is activated. A card that is dressed after it has been seen would first show as a
    /// rectangle, then change into a card. So the alpha is set to 0 here, before anything else, and
    /// <see cref="Fade"/> brings it up.
    /// </remarks>
    /// <returns>False when there is no window to dress, which the
    /// caller must read as "show no card".</returns>
    public static bool Dress(nint window, double width, double height, double radius)
    {
        if (window == 0)
            return false;

        try
        {
            // Alpha goes to 0 first. Each change below would otherwise be visible as a frame.
            SendDouble(window, sel_registerName("setAlphaValue:"), 0.0);

            SendULong(window, sel_registerName("setStyleMask:"), Borderless);

            // The mask above takes the close button away, and Uno closes a window by pressing it.
            // See QuietClose.
            if (!QuietClose(window))
                RPM.Core.Diagnostics.Scope.Said("boot", "splash close will beep: performClose: not replaced");

            SendLong(window, sel_registerName("setLevel:"), FloatingLevel);

            // This window takes no mouse input. A floating window in the centre of the screen is where the
            // pointer often is, and this window is above everything, so any click it took would go nowhere.
            // The setting does not depend on the window's alpha.
            SendBool(window, sel_registerName("setIgnoresMouseEvents:"), true);

            // The window itself is see-through. This is what makes the rounded corners work. The card's
            // shape is cut into the content view's layer, and everything outside that shape would otherwise
            // show the window's background colour instead of the desktop. The layers under the content view
            // are deliberately left alone. See the note on this class.
            SendBool(window, sel_registerName("setOpaque:"), false);
            var clear = Send(objc_getClass("NSColor"), sel_registerName("clearColor"));
            if (clear != 0)
                SendPtr(window, sel_registerName("setBackgroundColor:"), clear);

            // Keeps the card out of the way of normal window behaviour. It is not a document, it does not
            // belong to a single Space, and it must not appear in Mission Control or be picked by a
            // window-cycling gesture.
            SendULong(window, sel_registerName("setCollectionBehavior:"),
                      // CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary
                      1 | 16 | 64 | 256);

            Round(Send(Send(window, sel_registerName("contentView")), sel_registerName("layer")), radius);
            Place(window, width, height);

            SendBool(window, sel_registerName("setHasShadow:"), true);
            return true;
        }
        catch
        {
            // Nothing shown so far matters, because the card is simply not made. The caller decides whether
            // an undressed window is worth showing. It is not, so the answer is no.
            return false;
        }
    }

    /// <summary>
    /// The key of the associated object that marks a window as a card. A selector, because the runtime
    /// gives each name one address for the life of the process, which is what a key has to be.
    /// </summary>
    private const string CardMark = "rpmQuietCloseCard";

    /// <summary>Guards <see cref="ClosesBefore"/>.</summary>
    private static readonly object QuietLock = new();

    /// <summary>For each class <see cref="QuietClose"/> put its method on, the <c>performClose:</c> the
    /// class had before. Every window of the class that is not a card still runs it.</summary>
    private static readonly Dictionary<nint, nint> ClosesBefore = [];

    /// <summary>Makes <c>performClose:</c> do nothing on this one window, instead of playing the alert
    /// sound.
    /// </summary>
    /// <remarks>
    /// <para>Uno's <c>Window.Close</c> ends in <c>uno_window_close</c>, which
    /// is <c>[window performClose:nil]</c>: a press of the window's close button. A borderless window has
    /// no close button. AppKit then plays the alert sound and leaves the window open. A window with the
    /// closable bit set but no title bar beeps the same way, so the style mask cannot prevent it.</para>
    ///
    /// <para>The method goes on the window's class, <c>UNOWindow</c>, and checks which window it was
    /// sent to: a card gets nothing, every other window gets the <c>performClose:</c> the class had
    /// before. The app's own window still closes from its close button and Cmd-W, after two
    /// lookups: the mark below, and the class's previous method.</para>
    ///
    /// <para>The window's class is never changed. Moving the card into a subclass with
    /// <c>object_setClass</c> is unsafe. Key-value observing finds an observer through the object's class.
    /// If an observer was registered before the change, <c>-[NSWindow dealloc]</c> cannot find it under the
    /// new class. That throws NSRangeException, and nothing catches it. This was reproduced with a
    /// standalone AppKit program.</para>
    ///
    /// <para>The method does nothing, rather than closing the window. A native close of this window has
    /// crashed the app (see <c>SplashWindow.Close</c>). So the window's lifetime stays as it is: it is left
    /// at alpha 0 and ignores the mouse. Uno's managed close still runs before the native call and detaches
    /// the card's content.</para>
    ///
    /// <para>A card is marked on the window object, with an associated object, and not by its address. The
    /// runtime drops the mark when the window is freed. So a window created later at the same address is
    /// not mistaken for a card. A card can be freed while the app is still running, for example while it
    /// handles an Apple event, so a card does not always live as long as the app.</para>
    /// </remarks>
    /// <returns>False when the method could not be put on the window's class. The card then beeps on
    /// close.</returns>
    internal static bool QuietClose(nint window)
    {
        if (window == 0)
            return false;

        // [window class] rather than object_getClass: an observed window has already been moved into
        // a subclass that key-value observing made for it, and [window class] still answers the class
        // Uno made. Methods put on that class reach the observing subclass too.
        var cls = Send(window, sel_registerName("class"));
        if (cls == 0)
            return false;

        var selector = sel_registerName("performClose:");
        var quiet = (nint)(delegate* unmanaged<nint, nint, nint, void>)&PerformClose;

        // Marked before the method is put on the class, so there is no moment when the method is
        // there and the card is not yet known to it.
        var mark = Send(objc_getClass("NSNull"), sel_registerName("null"));
        if (mark == 0)
            return false;
        objc_setAssociatedObject(window, sel_registerName(CardMark), mark, RetainNonatomic);

        lock (QuietLock)
        {
            var before = class_getMethodImplementation(cls, selector);

            // Already this method, on this class or on one it inherits from. Kept as the class's own
            // it would make every other window's performClose: call itself.
            if (before != quiet)
            {
                // "v@:@": returns void, takes self, _cmd and the sender. Refused when the class defines
                // performClose: itself rather than inheriting it, and that one is replaced instead.
                if (!class_addMethod(cls, selector, quiet, "v@:@"))
                {
                    var own = class_getInstanceMethod(cls, selector);
                    if (own == 0)
                        return false;

                    before = method_setImplementation(own, quiet);
                }

                ClosesBefore[cls] = before;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>performClose:</c> for every window of a class <see cref="QuietClose"/> was given: nothing for
    /// a card, the class's previous <c>performClose:</c> for any other window.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void PerformClose(nint self, nint selector, nint sender)
    {
        if (objc_getAssociatedObject(self, sel_registerName(CardMark)) != 0)
            return;

        nint before = 0;
        lock (QuietLock)
        {
            // Up from the object's own class, which for an observed window is the subclass key-value
            // observing made; the method was put on one of its ancestors.
            for (var cls = object_getClass(self); cls != 0 && before == 0; cls = class_getSuperclass(cls))
                ClosesBefore.TryGetValue(cls, out before);
        }

        if (before != 0)
            ((delegate* unmanaged<nint, nint, nint, void>)before)(self, selector, sender);
    }

    /// <summary>Sizes the card and puts it dead centre of the display the pointer is on.
    /// </summary>
    /// <remarks>
    /// <para>One call sets both size and position. With two calls, the window server could
    /// composite the card at a wrong place between them. <c>setContentSize:</c> is not used either. On a
    /// borderless window the frame is the content area.</para>
    ///
    /// <para><b>The pointer's display, and all of it.</b> The Windows card uses the same rule. A person
    /// with two screens is looking at the one under the pointer, so the card is centred on the whole
    /// SCREEN, not on its work area. The work area would place it slightly too high. If the pointer's
    /// display cannot be found, the main display is used.</para>
    /// </remarks>
    private static void Place(nint window, double width, double height)
    {
        var screen = ScreenUnderPointer();
        SendRectBool(
            window, sel_registerName("setFrame:display:"),
            new CGRect(
                screen.X + (screen.Width - width) / 2,
                screen.Y + (screen.Height - height) / 2,
                width,
                height),
            false);
    }

    /// <summary>The frame of the display under the pointer, in AppKit's own coordinates. The origin is
    /// bottom left, and all screens share one coordinate space.</summary>
    private static CGRect ScreenUnderPointer()
    {
        var cls = objc_getClass("NSScreen");
        var main = cls == 0 ? 0 : Send(cls, sel_registerName("mainScreen"));
        var fallback = main == 0
            ? new CGRect(0, 0, 1280, 800)
            : ObjcStructReturn.Rect(main, sel_registerName("frame"));

        try
        {
            var mouse = ObjcStructReturn.Point(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
            var screens = Send(cls, sel_registerName("screens"));
            if (screens == 0)
                return fallback;

            var count = (long)Send(screens, sel_registerName("count"));
            for (nuint i = 0; i < (nuint)count; i++)
            {
                var screen = SendIndex(screens, sel_registerName("objectAtIndex:"), i);
                if (screen == 0)
                    continue;

                var frame = ObjcStructReturn.Rect(screen, sel_registerName("frame"));
                if (mouse.X >= frame.X && mouse.X < frame.X + frame.Width
                    && mouse.Y >= frame.Y && mouse.Y < frame.Y + frame.Height)
                    return frame;
            }
        }
        catch { /* If the pointer cannot be located, the main display is used. */ }

        return fallback;
    }

    private static void Round(nint layer, double radius)
    {
        if (layer == 0)
            return;

        SendDouble(layer, sel_registerName("setCornerRadius:"), radius);
        SendBool(layer, sel_registerName("setMasksToBounds:"), true);
    }

    /// <summary>Takes the window's alpha to <paramref name="to"/> over <paramref name="ms"/>, on AppKit's
    /// own clock.
    /// </summary>
    /// <remarks>
    /// <para><b>Core Animation runs the fade, not this app.</b> The drawing
    /// thread builds the interface for as long as this card is shown. A fade stepped from that thread can
    /// stall. A 730 ms fade has been seen to take 6,660 ms (see <c>SplashFade</c>). Here one instruction is
    /// handed over, and the window server draws every frame.</para>
    ///
    /// <para><b>Main thread only.</b> Every AppKit call must run there, and this method is reached from the
    /// dispatcher, which is the main thread on this head. There is deliberately no completion handler. That
    /// would need a block. The caller needs to close the window when the fade ends, and it can do that with
    /// a timer it already has.</para>
    /// </remarks>
    public static void Fade(nint window, double to, int ms)
    {
        if (window == 0)
            return;

        try
        {
            var context = objc_getClass("NSAnimationContext");
            if (context == 0)
            {
                SendDouble(window, sel_registerName("setAlphaValue:"), to);
                return;
            }

            SendVoid(context, sel_registerName("beginGrouping"));

            var current = Send(context, sel_registerName("currentContext"));
            if (current != 0)
                SendDouble(current, sel_registerName("setDuration:"), ms / 1000.0);

            // The animator proxy: messages sent to it animate instead of taking effect at once. If it is
            // not available, the window changes in one step, with no fade.
            var animator = Send(window, sel_registerName("animator"));
            SendDouble(animator == 0 ? window : animator, sel_registerName("setAlphaValue:"), to);

            SendVoid(context, sel_registerName("endGrouping"));
        }
        catch
        {
            try { SendDouble(window, sel_registerName("setAlphaValue:"), to); } catch { }
        }
    }

    /// <summary>Sets the alpha with no animation at all.</summary>
    public static void SetAlpha(nint window, double alpha)
    {
        if (window == 0)
            return;

        try { SendDouble(window, sel_registerName("setAlphaValue:"), alpha); } catch { }
    }

    /// <summary>Sets whether clicks pass through the window to whatever is behind it.</summary>
    public static void IgnoreMouse(nint window, bool ignore)
    {
        if (window == 0)
            return;

        try { SendBool(window, sel_registerName("setIgnoresMouseEvents:"), ignore); } catch { }
    }

    /// <summary>Brings the window to the front of its level, and makes it the window that receives keyboard
    /// input. Uncloaking does not do this by itself. The window has been invisible since it was activated,
    /// so nothing has raised it, and other windows may be above it.</summary>
    public static void Raise(nint window)
    {
        if (window == 0)
            return;

        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != 0)
                SendBool(app, sel_registerName("activateIgnoringOtherApps:"), true);

            SendPtrRet(window, sel_registerName("makeKeyAndOrderFront:"), 0);
        }
        catch { /* the window stays where it is in the stack */ }
    }
}
