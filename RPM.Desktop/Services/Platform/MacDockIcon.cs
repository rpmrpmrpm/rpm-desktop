using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>Gives the app its own Dock icon when it runs unbundled. This is every run from bin/.
///
/// An unbundled process has no Info.plist, so AppKit has no icon to show. The Dock then uses the icon
/// that LaunchServices assigns to the executable file. That icon depends on the file type, and the type
/// depends on the suffix. The binary is named "rpm" for this reason (see AssemblyName in the csproj).
/// That types it as public.unix-executable, not as the document of some other app.
///
/// Uno's mac host sets an icon too, but only from <c>Package.Current.Logo</c>. That is null without a
/// Package.appxmanifest, and this project has none. So the icon is set here, from the file next to the
/// binary.
///
/// A packaged .app carries its own icon in the bundle. setApplicationIconImage: would replace it with
/// this flat copy, which has no appearance variants. So a bundled run is left alone.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacDockIcon
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendUtf8(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>
    /// The icon to hand the Dock: Assets/Icons/AppIcon.icns first. If that is not beside the binary, the
    /// icon set that Resizetizer generates from Assets/Icons/icon.png is used.
    ///
    /// <para>Both are the same artwork in two shapes. macOS draws app icons as a superellipse of exponent
    /// 5. The phone tile is rounder, near 4.5. So the phone tile loses more of its corners than the icons
    /// beside it in the Dock, and it looks smaller, although both fill the same 824 of 1024 pixels. The
    /// .icns is compiled by actool for macOS from icons.icon. That is Apple's own render of the design, so
    /// it has the right shape. scripts/make_app_icon.py writes it, and a machine without Xcode
    /// cannot.</para>
    /// </summary>
    private static string? IconPath
    {
        get
        {
            var icns = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "AppIcon.icns");
            if (File.Exists(icns))
                return icns;

            var png = Path.Combine(
                AppContext.BaseDirectory, "Assets.xcassets", "icon.appiconset", "icon512x512@2x.png");
            return File.Exists(png) ? png : null;
        }
    }

    /// <summary>True when the executable sits inside an .app bundle.</summary>
    private static bool Bundled =>
        Environment.ProcessPath?.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) == true;

    /// <summary>
    /// Sets the Dock icon. Call after the host is running. NSApplication must already exist. Asking for it
    /// earlier would create a plain one in place of the host's own subclass.
    /// </summary>
    public static void Apply()
    {
        try
        {
            if (Bundled || IconPath is not { } file)
                return;

            var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app == 0)
                return;

            var path = SendUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), file);
            var image = Send(Send(objc_getClass("NSImage"), Sel("alloc")),
                Sel("initWithContentsOfFile:"), path);
            if (image == 0)
                return;

            Send(app, Sel("setApplicationIconImage:"), image);
        }
        catch { /* cosmetic only */ }
    }
}
