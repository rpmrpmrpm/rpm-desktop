using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>Files and folders dragged onto the window from Finder, on macOS.
///
/// <para>On Windows, Uno's XAML drop events (AllowDrop, DragOver, Drop) work because the Win32 head ships a
/// Win32DragDropExtension. It registers the window with OLE and passes the events on to managed code. The
/// mac head has no such extension. Uno 6.6's libUnoNativeMac implements no NSDraggingDestination method and
/// never calls registerForDraggedTypes:. So AppKit refuses the drag before any managed code can see it, and
/// the cursor shows the no-drop badge. This class adds the missing part, with the same ObjC interop as the
/// other mac-only code here (see <see cref="MacKeyBeep"/>).</para>
///
/// <para>AppKit's drag protocol is informal. A view is a drop destination when it is registered for a
/// pasteboard type AND implements the methods. So the two Uno view classes get the four methods added once.
/// The window's own view is registered for file URLs. An ancestor view is a valid destination for the whole
/// window, which is the target needed.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacFolderDrop
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint object_getClass(nint obj);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint class_getName(nint cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsg(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsgPtr(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsgIndex(nint receiver, nint selector, nuint index);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendMsgUtf8(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary>NSDragOperationCopy. The files stay where they are. What the app does with them afterwards
    /// (a move into the collection, or a copy) is set by the import, not by the drag.</summary>
    private const nuint DragOperationCopy = 1;

    /// <summary>The pasteboard type that Finder puts on a dragged file or folder. It is retained, because
    /// it is read on every drop. An autoreleased string would not survive the pool it was made
    /// in.</summary>
    private static nint _fileUrlType;

    /// <summary>What a drop landed on the window, as absolute paths. Raised on the main thread, which is
    /// the UI thread. AppKit calls performDragOperation: on that thread.</summary>
    internal static event Action<IReadOnlyList<string>>? Dropped;

    /// <summary>The names of the classes that already have the four methods. class_addMethod would refuse a
    /// second add anyway. This set keeps the work out of repeated calls to Enable.</summary>
    private static readonly HashSet<string> _patched = [];

    /// <summary>Makes the window a drop target. Call once the window exists, because its view is what gets
    /// registered. Safe to call again. A class is patched only once, and registering a view again is
    /// harmless. On a host where the window has no view yet, it does nothing.
    /// </summary>
    internal static void Enable(nint window)
    {
        if (window == 0)
            return;

        var view = SendMsg(window, sel_registerName("contentView"));
        if (view == 0)
            return;

        // The window's content view need not be the Uno surface itself. The mac head puts the view behind a
        // content view controller. An ancestor is a valid destination for everything under it. So the
        // content view is the target when it is Uno's own view. Otherwise the Uno surface beneath it is
        // used. The class of the chosen view is the one that gets the methods.
        if (!IsUnoView(view) && FindUnoView(view) is { } unoView and not 0)
            view = unoView;

        PatchClassOf(view);

        _fileUrlType = _fileUrlType != 0
            ? _fileUrlType
            : SendMsg(SendMsgUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"),
                "public.file-url"), sel_registerName("retain"));

        var types = SendMsgPtr(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), _fileUrlType);
        SendMsgPtr(view, sel_registerName("registerForDraggedTypes:"), types);
    }

    /// <summary>Uno's two surfaces, the Metal one that every desktop head uses and the software fallback.
    /// These are the same pair that <see cref="MacKeyBeep"/> patches.</summary>
    private static readonly string[] Views = ["UNOMetalFlippedView", "UNOSoftView"];

    /// <summary>Gives a view's class the four NSDraggingDestination methods AppKit looks for.</summary>
    private static void PatchClassOf(nint view)
    {
        var cls = object_getClass(view);
        if (cls == 0 || ClassNameOf(view) is not { } name || !_patched.Add(name))
            return;

        // BOOL is a C99 bool on arm64 and a signed char on x86_64, and the encoding has to say which.
        // Only the runtime's own introspection reads these; a direct call does not go near them.
        string boolReturn = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "B24@0:8@16"
            : "c24@0:8@16";
        const string dragOperationReturn = "Q24@0:8@16";   // NSDragOperation is an NSUInteger

        class_addMethod(cls, sel_registerName("draggingEntered:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nuint>)&DraggingEntered, dragOperationReturn);
        class_addMethod(cls, sel_registerName("draggingUpdated:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nuint>)&DraggingEntered, dragOperationReturn);
        class_addMethod(cls, sel_registerName("prepareForDragOperation:"),
            (nint)(delegate* unmanaged<nint, nint, nint, byte>)&PrepareForDragOperation, boolReturn);
        class_addMethod(cls, sel_registerName("performDragOperation:"),
            (nint)(delegate* unmanaged<nint, nint, nint, byte>)&PerformDragOperation, boolReturn);
    }

    /// <summary>The cursor's answer while the drag is over the window: a copy where the drag carries files,
    /// and nothing where it does not. AppKit then shows the no-drop badge.</summary>
    [UnmanagedCallersOnly]
    private static nuint DraggingEntered(nint self, nint selector, nint sender)
    {
        try
        {
            return PathsFrom(sender).Count > 0 ? DragOperationCopy : 0;
        }
        catch
        {
            return 0;   // a managed exception must never cross back into AppKit
        }
    }

    [UnmanagedCallersOnly]
    private static byte PrepareForDragOperation(nint self, nint selector, nint sender) => 1;

    /// <summary>The drop. Reads the paths off the pasteboard and hands them to whoever is listening.
    /// Answering NO leaves the file in place, with the zoom-back animation. That is the right answer for a
    /// drag that carries nothing this app can use.</summary>
    [UnmanagedCallersOnly]
    private static byte PerformDragOperation(nint self, nint selector, nint sender)
    {
        try
        {
            var paths = PathsFrom(sender);
            if (paths.Count == 0)
                return 0;

            Dropped?.Invoke(paths);
            return 1;
        }
        catch
        {
            return 0;   // a managed exception must never cross back into AppKit
        }
    }

    /// <summary>The dragged items as filesystem paths.
    ///
    /// <para>Finder puts a file REFERENCE URL on the pasteboard, for example
    /// <c>file:///.file/id=15368762.17/</c>. It names the file by its inode on the volfs mount and does not
    /// say where the file is. Reading that string as a Uri gives a path that no
    /// <see cref="System.IO.Directory"/> call can use. NSURL turns it back into a place on disk.
    /// <c>filePathURL</c> is the documented way to resolve it. A URL that is already a path comes back
    /// unchanged, so ordinary drags from other apps go through the same call.</para>
    /// </summary>
    private static List<string> PathsFrom(nint sender)
    {
        var paths = new List<string>();
        if (sender == 0 || _fileUrlType == 0)
            return paths;

        var pasteboard = SendMsg(sender, sel_registerName("draggingPasteboard"));
        var items = pasteboard == 0 ? 0 : SendMsg(pasteboard, sel_registerName("pasteboardItems"));
        if (items == 0)
            return paths;

        var count = (long)SendMsg(items, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
        {
            var item = SendMsgIndex(items, sel_registerName("objectAtIndex:"), i);
            var text = item == 0 ? 0 : SendMsgPtr(item, sel_registerName("stringForType:"), _fileUrlType);
            if (text == 0)
                continue;

            var url = SendMsgPtr(objc_getClass("NSURL"), sel_registerName("URLWithString:"), text);
            if (url == 0)
                continue;

            var pathUrl = SendMsg(url, sel_registerName("filePathURL"));
            var path = SendMsg(pathUrl == 0 ? url : pathUrl, sel_registerName("path"));
            var utf8 = path == 0 ? 0 : SendMsg(path, sel_registerName("UTF8String"));
            if (utf8 != 0 && Marshal.PtrToStringUTF8(utf8) is { Length: > 0 } resolved)
                paths.Add(resolved);
        }
        return paths;
    }

    private static bool IsUnoView(nint view)
    {
        var name = ClassNameOf(view);
        return name is not null && Array.IndexOf(Views, name) >= 0;
    }

    private static string? ClassNameOf(nint obj)
    {
        var cls = obj == 0 ? 0 : object_getClass(obj);
        var name = cls == 0 ? 0 : class_getName(cls);
        return name == 0 ? null : Marshal.PtrToStringUTF8(name);
    }

    /// <summary>The first Uno surface under a view, depth first.</summary>
    private static nint FindUnoView(nint view)
    {
        var subviews = SendMsg(view, sel_registerName("subviews"));
        if (subviews == 0)
            return 0;

        var count = (long)SendMsg(subviews, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
        {
            var child = SendMsgIndex(subviews, sel_registerName("objectAtIndex:"), i);
            if (child == 0)
                continue;
            if (IsUnoView(child))
                return child;
            if (FindUnoView(child) is { } found and not 0)
                return found;
        }
        return 0;
    }
}
