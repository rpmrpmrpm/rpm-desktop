using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// Calls a handler when the app comes to the front. The event that reports this differs by platform.
///
/// <para>WinUI and Uno raise <see cref="Window.Activated"/>. On macOS that event does not work. Uno's Mac
/// host registers no focus callback, so the window's activation state never changes after launch, and the
/// event is never raised. A handler written against it runs on Windows and never runs on the Mac. On macOS
/// this class observes <c>NSApplicationDidBecomeActiveNotification</c> instead. AppKit posts it on the main
/// thread, which is Uno's UI thread on macOS.</para>
///
/// <para>Analyzer rule RPM006 requires this class to be the only place that subscribes to
/// <see cref="Window.Activated"/>.</para>
/// </summary>
internal static class AppActivation
{
    /// <summary>Calls <paramref name="handler"/> on the UI thread each time the app comes to the front.
    /// Leaving the front is not reported.</summary>
    public static void WhenActivated(Window window, Action handler)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacObserver.Add(handler);
            return;
        }

        window.Activated += (_, e) =>
        {
            // WinUI and Uno use different types for the state. WinUI uses WindowActivationState and Uno
            // uses the WinRT core state. Both have the same values. Only Deactivated (leaving the front) is
            // ignored.
#if RPM_WINUI
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
#else
            if (e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated) return;
#endif
            handler();
        };
    }

    /// <summary>
    /// One observer object for the life of the app. It is created at runtime as an NSObject subclass with
    /// one method, the same way as the command target in <see cref="MacNowPlaying"/>. The notification
    /// centre does not keep its observers alive, so the instance is never released.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static unsafe class MacObserver
    {
        private static readonly List<Action> Handlers = [];
        private static nint _observer;

        public static void Add(Action handler)
        {
            lock (Handlers)
            {
                Handlers.Add(handler);
                if (_observer != 0)
                    return;

                try
                {
                    _observer = Observe();
                }
                catch (Exception e)
                {
                    Said.Line("window", $"not told when rpm comes to the front: {e.Message}");
                }
            }
        }

        private static nint Observe()
        {
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RPMActivationObserver", 0);
            if (cls == 0)
                throw new InvalidOperationException("the observer class could not be made");

            var selector = sel_registerName("rpmBecameActive:");
            class_addMethod(cls, selector, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnBecameActive, "v@:@");
            objc_registerClassPair(cls);

            var observer = Send(Send(cls, sel_registerName("alloc")), sel_registerName("init"));
            var center = Send(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));

            Send(center, sel_registerName("addObserver:selector:name:object:"),
                observer, selector, BecameActiveName(), 0);
            return observer;
        }

        /// <summary>The notification's name, read from the constant AppKit exports rather than spelled
        /// here, so it is AppKit's own string object.</summary>
        private static nint BecameActiveName()
        {
            var appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(appKit, "NSApplicationDidBecomeActiveNotification"));
        }

        [UnmanagedCallersOnly]
        private static void OnBecameActive(nint self, nint selector, nint notification)
        {
            Action[] handlers;
            lock (Handlers) handlers = [.. Handlers];

            foreach (var handler in handlers)
            {
                try { handler(); }
                catch { /* A managed exception must never cross back into AppKit. */ }
            }
        }

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern nint objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern nint sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern nint objc_allocateClassPair(nint super, string name, nuint extraBytes);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_registerClassPair(nint cls);

        [DllImport("/usr/lib/libobjc.dylib")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint Send(nint receiver, nint selector);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void Send(nint receiver, nint selector, nint a, nint b, nint c, nint d);
    }
}
