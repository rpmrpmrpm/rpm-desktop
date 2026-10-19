using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>A rectangle as CoreGraphics passes one: four doubles, by value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X, Y, Width, Height;

    public CGRect(double x, double y, double w, double h)
    {
        X = x; Y = y; Width = w; Height = h;
    }
}

/// <summary>A point as CoreGraphics passes one: two doubles, by value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X, Y;
}

/// <summary>Sends an Objective-C message whose method returns a struct, through the entry point that the
/// architecture needs. This is the only place in the app that may do this. RPM013 fails the build on any
/// other <c>objc_msgSend</c> import that returns a struct.
///
/// <para><b>Why.</b> On x86_64 a struct larger than 16 bytes is returned through a buffer that the caller
/// passes as a hidden first argument. The message must then go through <c>objc_msgSend_stret</c>, which
/// expects that buffer before the receiver. Plain <c>objc_msgSend</c> reads the buffer's address as the
/// receiver and crashes. On arm64 every struct comes back in registers, and <c>objc_msgSend_stret</c> does
/// not exist. A P/Invoke calls exactly the entry point it names, so one declaration cannot be right on
/// both. Without the right entry point, the Intel build crashes at launch in <c>-[NSScreen
/// frame]</c>.</para>
///
/// <para>Structs of 16 bytes or less (<see cref="CGPoint"/>, an NSSize) come back in registers on
/// both architectures and go through <c>objc_msgSend</c>. They are here too so that the size rule is
/// decided in one place, by <see cref="NeedsStret"/>.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static class ObjcStructReturn
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    /// <summary>x86_64 only. The symbol is not exported on arm64, which is fine as long as nothing
    /// calls it there: a P/Invoke is bound on its first call.</summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern CGRect SendRectStret(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGPoint SendPoint(nint receiver, nint selector);

    private static readonly bool RectNeedsStret =
        NeedsStret(RuntimeInformation.ProcessArchitecture, Marshal.SizeOf<CGRect>());

    /// <summary>Whether a struct of this size is returned through <c>objc_msgSend_stret</c> on this
    /// architecture. True only on x86_64 for a struct larger than 16 bytes.</summary>
    internal static bool NeedsStret(Architecture architecture, int size) =>
        architecture == Architecture.X64 && size > 16;

    /// <summary>A method that returns an NSRect or CGRect, such as <c>frame</c>. Zeros for a nil
    /// receiver, without sending: a message to nil fills in zeros only for a struct that comes back in
    /// registers, which a rectangle on Intel does not.</summary>
    public static CGRect Rect(nint receiver, nint selector)
    {
        if (receiver == 0)
            return default;

        return RectNeedsStret ? SendRectStret(receiver, selector) : SendRect(receiver, selector);
    }

    /// <summary>A method that returns an NSPoint or CGPoint, such as <c>+[NSEvent mouseLocation]</c>.
    /// Zeros for a nil receiver.</summary>
    public static CGPoint Point(nint receiver, nint selector) =>
        receiver == 0 ? default : SendPoint(receiver, selector);
}
