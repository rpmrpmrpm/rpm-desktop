using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// macOS ASWebAuthenticationSession, the in-app browser sheet that Apple requires for OAuth.
///
/// <para>Apple's App Review rejects OAuth flows that open the default browser with a plain
/// <c>Launcher.LaunchUriAsync</c>. An auth session keeps the flow inside the app. It shares Safari's
/// cookie jar, so a user who is already signed in to Discogs simply clicks Approve. It also hands the
/// callback URL straight back to the app, so the app does not have to keep asking whether the flow has
/// finished.</para>
///
/// <para>Uno and Skia have no managed binding for AuthenticationServices. So this drives the Objective-C
/// runtime directly, as <see cref="MacWindowChrome"/> does. Three parts are not obvious:</para>
/// <list type="number">
/// <item>AuthenticationServices is not linked into the process. So <c>objc_getClass</c> cannot find
/// ASWebAuthenticationSession until the framework is loaded with <c>dlopen</c>.</item>
/// <item>The completion handler is an Objective-C <i>block</i>, not a function pointer. The code builds a
/// global block literal (isa = <c>_NSConcreteGlobalBlock</c>) whose <c>invoke</c> points at a managed
/// static method. Global blocks are never copied or released, so there is nothing to free.</item>
/// <item>The session needs a presentation context provider. That is an object that answers
/// <c>presentationAnchorForWebAuthenticationSession:</c> with an NSWindow. Instead of creating a class at
/// runtime, the code adds that selector to NSApplication and passes sharedApplication.</item>
/// </list>
///
/// <para>Everything here must run on the UI thread. AppKit rejects a session that is started from another
/// thread.</para>
/// </summary>
public static class MacWebAuth
{
    /// <summary>
    /// The scheme that the accounts API redirects to when a connection finishes. The sheet watches for
    /// it and closes itself, so the app does not need to be told separately.
    /// </summary>
    public const string CallbackScheme = "rpm";

    /// <summary>ASWebAuthenticationSessionErrorCodeCanceledLogin. The user closed the sheet.</summary>
    private const long CanceledLogin = 1;

    /// <summary>Whether the sheet is available. Windows and Linux have no system auth sheet. They fall
    /// back to opening the user's browser.</summary>
    public static bool Applicable => OperatingSystem.IsMacOS();

    // These are kept alive for the life of the process. The Objective-C runtime holds raw pointers to both
    // the block's invoke thunk and the added anchor method. If the GC collected either one, the app would
    // crash as soon as Safari called back.
    private static CompletionBlockInvoke? _completionThunk;
    private static AnchorMethod? _anchorThunk;
    private static nint _blockLiteral;
    private static bool _frameworkLoaded;

    // Only one session at a time. This also keeps the session object alive, so the GC cannot release it.
    private static TaskCompletionSource<string?>? _pending;
    private static nint _session;

    /// <summary>
    /// Shows <paramref name="url"/> in an ASWebAuthenticationSession. It completes with the callback URL
    /// once the site redirects to <see cref="CallbackScheme"/>. It completes with null when the user
    /// closed the sheet. It throws <see cref="InvalidOperationException"/> if the session cannot start.
    /// </summary>
    public static Task<string?> AuthenticateAsync(Uri url, CancellationToken ct = default)
    {
        if (!Applicable) throw new PlatformNotSupportedException("ASWebAuthenticationSession is macOS-only.");
        if (_pending is not null) throw new InvalidOperationException("An auth session is already in flight.");

        EnsureFramework();

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        var sessionClass = objc_getClass("ASWebAuthenticationSession");
        if (sessionClass == 0) { _pending = null; throw new InvalidOperationException("ASWebAuthenticationSession unavailable."); }

        var nsUrl = objc_msgSend(objc_getClass("NSURL"), sel("URLWithString:"), CreateNSString(url.ToString()));
        _session = objc_msgSend3(
            objc_msgSend(sessionClass, sel("alloc")),
            sel("initWithURL:callbackURLScheme:completionHandler:"),
            nsUrl, CreateNSString(CallbackScheme), EnsureCompletionBlock());

        objc_msgSend_ptr(_session, sel("setPresentationContextProvider:"), EnsureAnchorProvider());
        // Not ephemeral. Sharing Safari's cookies is the point: a user who is already signed in to
        // Discogs gets an Approve button, not a login form.
        objc_msgSend_bool(_session, sel("setPrefersEphemeralWebBrowserSession:"), false);

        if (!objc_msgSend_ret_bool(_session, sel("start")))
        {
            Cleanup();
            throw new InvalidOperationException("ASWebAuthenticationSession failed to start.");
        }

        // Cancelling closes the sheet. The completion block still runs, with a canceled-login error, and it
        // completes the task. So the task is deliberately not completed here.
        if (ct.CanBeCanceled)
            ct.Register(() => { try { objc_msgSend(_session, sel("cancel")); } catch { } });

        return tcs.Task;
    }

    /// <summary>
    /// Reads a value from a callback URL such as <c>rpm://discogs?status=connected</c>. It returns null
    /// when the URL does not use the app's scheme, or when the value is missing.
    /// </summary>
    public static string? ParseValue(string? callbackUrl, string key)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl)) return null;
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, CallbackScheme, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair.AsSpan(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    // ── completion block ────────────────────────────────────────────────────
    // void (^)(NSURL *callbackURL, NSError *error)
    private delegate void CompletionBlockInvoke(nint block, nint callbackUrl, nint error);

    private static void OnCompleted(nint block, nint callbackUrl, nint error)
    {
        var tcs = _pending;
        Cleanup();
        if (tcs is null) return;

        if (error != 0)
        {
            var code = (long)objc_msgSend(error, sel("code"));
            if (code == CanceledLogin) { tcs.TrySetResult(null); return; } // The user closed the sheet.
            tcs.TrySetException(new InvalidOperationException($"Sign-in failed (ASWebAuthenticationSession error {code})."));
            return;
        }

        tcs.TrySetResult(callbackUrl == 0 ? null : NSStringToString(objc_msgSend(callbackUrl, sel("absoluteString"))));
    }

    private static void Cleanup()
    {
        _pending = null;
        _session = 0;
    }

    private static nint EnsureCompletionBlock()
    {
        if (_blockLiteral != 0) return _blockLiteral;

        _completionThunk = OnCompleted;
        var invoke = Marshal.GetFunctionPointerForDelegate(_completionThunk);

        // struct Block_descriptor { unsigned long reserved; unsigned long size; }
        var descriptor = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(descriptor, 0, 0);
        Marshal.WriteIntPtr(descriptor, IntPtr.Size, Marshal.SizeOf<BlockLiteral>());

        var literal = new BlockLiteral
        {
            Isa = dlsym(RtldDefault, "_NSConcreteGlobalBlock"),
            Flags = BlockIsGlobal,
            Reserved = 0,
            Invoke = invoke,
            Descriptor = descriptor,
        };

        _blockLiteral = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        Marshal.StructureToPtr(literal, _blockLiteral, false);
        return _blockLiteral;
    }

    // ── presentation anchor ─────────────────────────────────────────────────
    // NSWindow *presentationAnchorForWebAuthenticationSession:(id)session
    private delegate nint AnchorMethod(nint self, nint cmd, nint session);

    private static nint EnsureAnchorProvider()
    {
        var app = objc_msgSend(objc_getClass("NSApplication"), sel("sharedApplication"));
        if (_anchorThunk is not null) return app;

        _anchorThunk = static (self, cmd, session) =>
        {
            var shared = objc_msgSend(objc_getClass("NSApplication"), sel("sharedApplication"));
            var window = objc_msgSend(shared, sel("keyWindow"));
            return window != 0 ? window : objc_msgSend(shared, sel("mainWindow"));
        };

        class_addMethod(objc_getClass("NSApplication"), sel("presentationAnchorForWebAuthenticationSession:"),
            Marshal.GetFunctionPointerForDelegate(_anchorThunk), "@@:@");
        return app;
    }

    private static void EnsureFramework()
    {
        if (_frameworkLoaded) return;
        // RTLD_LAZY. Without it the class is not registered, and objc_getClass returns nil.
        if (dlopen("/System/Library/Frameworks/AuthenticationServices.framework/AuthenticationServices", 1) == 0)
            throw new InvalidOperationException("Could not load AuthenticationServices.framework.");
        _frameworkLoaded = true;
    }

    // ── ObjC runtime plumbing ───────────────────────────────────────────────
    private const int BlockIsGlobal = 1 << 28;
    private static readonly nint RtldDefault = -2; // RTLD_DEFAULT

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    private static nint CreateNSString(string str)
    {
        var ns = objc_msgSend(objc_getClass("NSString"), sel("alloc"));
        var utf8 = Marshal.StringToHGlobalAnsi(str);
        ns = objc_msgSend(ns, sel("initWithUTF8String:"), utf8);
        Marshal.FreeHGlobal(utf8);
        return ns;
    }

    private static string? NSStringToString(nint nsString)
    {
        if (nsString == 0) return null;
        var utf8 = objc_msgSend(nsString, sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static nint sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libSystem.dylib")] private static extern nint dlopen(string path, int mode);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern nint dlsym(nint handle, string symbol);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend(nint target, nint selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend(nint target, nint selector, nint arg1);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend3(nint target, nint selector, nint a1, nint a2, nint a3);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_ptr(nint target, nint selector, nint arg1);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_bool(nint target, nint selector, bool arg1);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern bool objc_msgSend_ret_bool(nint target, nint selector);
}
