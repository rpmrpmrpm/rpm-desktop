using System.Runtime.InteropServices;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="AppActivation"/> detects the app coming to the front on macOS, where Uno never raises
/// <c>Window.Activated</c> after launch.
///
/// <para>The test posts AppKit's own <c>NSApplicationDidBecomeActiveNotification</c> through the default
/// <c>NSNotificationCenter</c>, which is what AppKit does when the app is brought to the front. It
/// delivers synchronously on the posting thread, so the handler has run by the time the post returns.
/// Nothing reads the window on macOS, so none is created.</para>
/// </summary>
public class AppActivationTests
{
    [Fact]
    public void Each_return_to_the_front_calls_the_handler_on_macOS()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var calls = 0;
        AppActivation.WhenActivated(window: null!, () => calls++);

        PostBecameActive();
        PostBecameActive();

        Assert.Equal(2, calls);
    }

    private static void PostBecameActive()
    {
        var appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var name = Marshal.ReadIntPtr(NativeLibrary.GetExport(appKit, "NSApplicationDidBecomeActiveNotification"));
        var center = Send(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        Send(center, sel_registerName("postNotificationName:object:"), name, 0);
    }

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void Send(nint receiver, nint selector, nint a, nint b);
}
