using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Platform;

/// <summary>The app being quit on macOS, as reported by the source that reports it.
///
/// <para>There are three ways to quit the app on the Mac, and they do not run the same code. The red close
/// button raises the main window's <c>Closed</c> event, which writes <see cref="Scope.Closing"/>. Cmd+Q and
/// the Dock menu's Quit go through AppKit's terminate instead. The window is never closed, and
/// <c>AppDomain.ProcessExit</c> does not run on the macOS Skia head, on either build. So nothing writes
/// that record. A run with no <c>Closing</c> is never marked as handled. The crash harvest then reads it
/// again at every launch, and it would file a native fault during shutdown as a crash.</para>
///
/// <para>This observes <c>NSApplicationWillTerminateNotification</c>. AppKit posts it on the main thread
/// before the process ends, which is Uno's UI thread on macOS.</para>
/// </summary>
internal static class MacQuit
{
    /// <summary>Records that the run is ending normally when the app quits. This covers the quit paths that
    /// never close the window. Does nothing off macOS, where <c>ProcessExit</c> runs.</summary>
    public static void SaysClosing()
    {
        if (OperatingSystem.IsMacOS())
            Observer.Install();
    }

    /// <summary>One observer object for the life of the app. It is created at runtime as an NSObject
    /// subclass with one method. This is the same approach as <see cref="AppActivation"/>. The notification
    /// centre does not keep its observers alive, so the instance is never released.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static unsafe class Observer
    {
        private static nint _observer;

        public static void Install()
        {
            if (_observer != 0)
                return;

            try
            {
                _observer = Observe();
            }
            catch (Exception e)
            {
                // The app still quits. The run is left unsettled, so the crash harvest reads it again at
                // each launch.
                Said.Line("crash", $"not told when rpm is quit: {e.Message}");
            }
        }

        private static nint Observe()
        {
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RPMTerminateObserver", 0);
            if (cls == 0)
                throw new InvalidOperationException("the observer class could not be made");

            var selector = sel_registerName("rpmWillTerminate:");
            class_addMethod(cls, selector, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnWillTerminate, "v@:@");
            objc_registerClassPair(cls);

            var observer = Send(Send(cls, sel_registerName("alloc")), sel_registerName("init"));
            var center = Send(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));

            Send(center, sel_registerName("addObserver:selector:name:object:"),
                observer, selector, WillTerminateName(), 0);
            return observer;
        }

        /// <summary>The notification's name, read from the constant AppKit exports rather than spelled
        /// here, so it is AppKit's own string object.</summary>
        private static nint WillTerminateName()
        {
            var appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(appKit, "NSApplicationWillTerminateNotification"));
        }

        [UnmanagedCallersOnly]
        private static void OnWillTerminate(nint self, nint selector, nint notification)
        {
            // Writing it twice is harmless. The harvest only checks whether the record exists. Closing the
            // window and then quitting both reach this code.
            try { Scope.Closing(); }
            catch { /* A managed exception must never cross back into AppKit. */ }
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
