using System.Runtime.InteropServices;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="ObjcStructReturn"/>: a method that returns a struct is sent through the entry point the
/// architecture needs, and a nil receiver returns zeros.
///
/// <para>The sends run only on this machine's architecture. On Apple silicon that is
/// <c>objc_msgSend</c> for every struct. On Intel, a rectangle needs <c>objc_msgSend_stret</c>. Using
/// the wrong entry point crashes under Rosetta in <c>-[NSScreen frame]</c>, so test the Intel path by
/// running the x64 app under Rosetta. <see cref="ObjcStructReturn.NeedsStret"/> states the rule for
/// both architectures.</para>
/// </summary>
public unsafe class ObjcStructReturnTests
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [UnmanagedCallersOnly]
    private static CGRect KnownRect(nint self, nint selector) => new(12.5, -40, 1920, 1080);

    [UnmanagedCallersOnly]
    private static CGPoint KnownPoint(nint self, nint selector) => new() { X = 300.25, Y = -7 };

    /// <summary>An instance of a new NSObject subclass whose <c>rpmRect</c> and <c>rpmPoint</c> return
    /// fixed values, so a send that reads the wrong registers or the wrong buffer shows as a wrong
    /// number.</summary>
    private static nint StandIn()
    {
        var cls = objc_allocateClassPair(objc_getClass("NSObject"), $"RPMTestStructs_{Guid.NewGuid():N}", 0);
        Assert.NotEqual(0, cls);
        Assert.True(class_addMethod(cls, sel_registerName("rpmRect"),
                                    (nint)(delegate* unmanaged<nint, nint, CGRect>)&KnownRect,
                                    "{CGRect={CGPoint=dd}{CGSize=dd}}@:"));
        Assert.True(class_addMethod(cls, sel_registerName("rpmPoint"),
                                    (nint)(delegate* unmanaged<nint, nint, CGPoint>)&KnownPoint,
                                    "{CGPoint=dd}@:"));
        objc_registerClassPair(cls);
        var obj = Send(Send(cls, sel_registerName("alloc")), sel_registerName("init"));
        Assert.NotEqual(0, obj);
        return obj;
    }

    [Fact]
    public void A_rectangle_comes_back_whole()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var rect = ObjcStructReturn.Rect(StandIn(), sel_registerName("rpmRect"));

        Assert.Equal((12.5, -40.0, 1920.0, 1080.0), (rect.X, rect.Y, rect.Width, rect.Height));
    }

    [Fact]
    public void A_point_comes_back_whole()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var point = ObjcStructReturn.Point(StandIn(), sel_registerName("rpmPoint"));

        Assert.Equal((300.25, -7.0), (point.X, point.Y));
    }

    [Fact]
    public void A_nil_receiver_returns_zeros()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var rect = ObjcStructReturn.Rect(0, sel_registerName("frame"));

        Assert.Equal(default, rect);
    }

    [Fact]
    public void The_main_screen_has_a_frame()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var screen = Send(objc_getClass("NSScreen"), sel_registerName("mainScreen"));
        if (screen == 0)
            return; // no display attached to this session

        var frame = ObjcStructReturn.Rect(screen, sel_registerName("frame"));

        Assert.True(frame.Width > 0 && frame.Height > 0, $"frame was {frame.Width} x {frame.Height}");
    }

    [Theory]
    [InlineData(Architecture.X64, 32, true)]   // NSRect: returned through a buffer the caller passes
    [InlineData(Architecture.X64, 16, false)]  // NSPoint, NSSize: two doubles in xmm0 and xmm1
    [InlineData(Architecture.Arm64, 32, false)] // arm64 has no objc_msgSend_stret at all
    [InlineData(Architecture.Arm64, 16, false)]
    public void Only_a_large_struct_on_Intel_needs_stret(Architecture arch, int size, bool expected) =>
        Assert.Equal(expected, ObjcStructReturn.NeedsStret(arch, size));
}
