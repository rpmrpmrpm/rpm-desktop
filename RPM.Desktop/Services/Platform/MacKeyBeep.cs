using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>Stops macOS beeping on every Backspace, Return and arrow key typed into the app.
///
/// Uno 6.6 gives the mac view IME support. Its NSView implements NSTextInputClient and sends
/// <c>keyDown:</c> through AppKit's <c>interpretKeyEvents:</c>. Printable keys then arrive as
/// <c>insertText:replacementRange:</c>, which the view implements. All other keys arrive as a COMMAND:
/// <c>deleteBackward:</c>, <c>insertNewline:</c>, <c>moveLeft:</c>, and so on, through
/// <c>doCommandBySelector:</c>. Uno's override there only calls <c>super</c>. NSResponder's default passes
/// the command up the chain until it reaches the end, and the end of the chain rings the system bell. The
/// key itself still works, because Uno handles it in managed code from the same keyDown. Only the sound is
/// wrong.
///
/// The fix makes the view's own <c>doCommandBySelector:</c> a no-op. That is what it already does, minus
/// the bell, because Uno acts on none of those commands. Only the view classes are changed, never
/// NSResponder. If a class does not implement the selector itself, the lookup returns the superclass's
/// method. Patching that method would silence the whole app, so the no-op is added to the class instead.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacKeyBeep
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getMetaClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getSuperclass(nint cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getInstanceMethod(nint cls, nint sel);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint method_setImplementation(nint method, nint imp);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    /// <summary>The replacement: take the command and say nothing.</summary>
    [UnmanagedCallersOnly]
    private static void Swallow(nint self, nint selector, nint command)
    {
    }

    /// <summary>Both of Uno's surfaces, the Metal one that every desktop head uses and the software
    /// fallback, carry the same override.</summary>
    private static readonly string[] Views = ["UNOMetalFlippedView", "UNOSoftView"];

    private static bool _applied;

    /// <summary>Call once the host is up. The classes are in libUnoNativeMac, which the window loads.
    /// Does nothing on a host where those classes are not registered.</summary>
    internal static void Silence()
    {
        if (_applied)
            return;
        _applied = true;

        var selector = sel_registerName("doCommandBySelector:");
        var noop = (nint)(delegate* unmanaged<nint, nint, nint, void>)&Swallow;

        foreach (var name in Views)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
                continue;

            var method = class_getInstanceMethod(cls, selector);
            var inherited = class_getInstanceMethod(class_getSuperclass(cls), selector);

            // "v@:@": returns void, takes self, _cmd and one object (the command's selector).
            if (method == 0 || method == inherited)
                class_addMethod(cls, selector, noop, "v@:@");
            else
                method_setImplementation(method, noop);
        }
    }
}
