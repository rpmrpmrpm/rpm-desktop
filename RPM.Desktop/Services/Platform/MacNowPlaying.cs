using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// The Mac's own transport: the play/pause key on the keyboard row, the buttons on a pair of headphones,
/// and the Now Playing tile in Control Center and on the lock screen.
///
/// <para>The framework has two halves, and they only work as a pair. <c>MPRemoteCommandCenter</c> is where
/// the key presses arrive. A target is hung on each command, and a press comes back as a selector.
/// <c>MPNowPlayingInfoCenter</c> is where the system is told what is playing. Neither half is enough on its
/// own. Commands without a now-playing statement leave the app unknown to Control Center, so the tile keeps
/// whatever else last made a sound, and so do the keys. A statement without commands puts a record on the
/// tile whose buttons do nothing.</para>
///
/// <para>This uses the same ObjC interop as the other mac-only code here (see <see cref="MacFolderDrop"/>).
/// A class made at runtime carries the command handlers, because <c>-addTarget:action:</c> takes a plain
/// object and a selector and needs no blocks. The one block in the file is for the artwork. MediaPlayer
/// offers no other way to provide it. The block is built once, as a GLOBAL block, so nothing has to copy or
/// free it.</para>
///
/// <para>Everything here is best effort and silent on failure. A missing framework, a symbol that the OS
/// has moved, or an app that is not yet the now-playing app is not worth a message to a listener who only
/// pressed pause. The app's own transport keeps working either way.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacNowPlaying
{
    // ── the runtime ──────────────────────────────────────────────────────────────────────────────

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
    private static extern nint Send(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendUtf8(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendDoubleArg(nint receiver, nint selector, double arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendUInt(nint receiver, nint selector, nuint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendBool(nint receiver, nint selector, byte arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendBytes(nint receiver, nint selector, byte* bytes, nuint length);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendSizeBlock(nint receiver, nint selector, CGSize size, nint block);

    /// <summary>NSTimeInterval comes back in a floating-point register on both architectures, so the plain
    /// send is the right one to read it with. <c>objc_msgSend_fpret</c> is for the x87 returns, which this
    /// is not.</summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern double SendForDouble(nint receiver, nint selector);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint dlsym(nint handle, string symbol);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>An NSString for the tile to show. It is autoreleased, because the dictionary it goes into
    /// retains it, and the main run loop's pool takes the reference back.</summary>
    private static nint Str(string text) =>
        SendUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), text);

    private static nint Num(double value) =>
        SendDoubleArg(objc_getClass("NSNumber"), Sel("numberWithDouble:"), value);

    // ── the keys ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A key pressed on the keyboard row, on a pair of headphones, or in Control Center. The
    /// second number is the position in seconds for a scrub, and 0 for every other command.
    ///
    /// <para>Raised on the main thread, which AppKit uses to call the command targets. That is also the UI
    /// thread. The service that listens to this event moves the call onto the dispatcher, so nothing here
    /// depends on the thread.</para>
    /// </summary>
    internal static event Action<RemoteCommand, double>? Commanded;

    private static nint _framework;
    private static nint _target;
    private static bool _enabled;

    /// <summary>Hangs the app's handlers on the system's transport commands. This runs once per run. A
    /// second call does nothing, because the runtime class is made by name and cannot be made twice.
    /// </summary>
    internal static void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;

        try
        {
            if (!LoadFramework())
                return;

            var target = EnsureTarget();
            var center = Send(objc_getClass("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
            if (target == 0 || center == 0)
                return;

            Hang(center, "playCommand", "rpmPlay:");
            Hang(center, "pauseCommand", "rpmPause:");
            Hang(center, "togglePlayPauseCommand", "rpmToggle:");
            Hang(center, "nextTrackCommand", "rpmNext:");
            Hang(center, "previousTrackCommand", "rpmPrevious:");
            Hang(center, "changePlaybackPositionCommand", "rpmSeek:");

            void Hang(nint commandCenter, string command, string action)
            {
                var cmd = Send(commandCenter, Sel(command));
                if (cmd == 0)
                    return;
                // Enabled explicitly: a command left disabled is a button the tile draws greyed out.
                SendBool(cmd, Sel("setEnabled:"), 1);
                Send(cmd, Sel("addTarget:action:"), target, Sel(action));
            }
        }
        catch
        {
            // No media keys on this machine. The rest of the app runs as before.
        }
    }

    private static bool LoadFramework()
    {
        if (_framework != 0)
            return true;

        const int RtldLazy = 1;
        _framework = dlopen("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer", RtldLazy);
        return _framework != 0;
    }

    /// <summary>The object the commands are hung on. It is an NSObject subclass made at runtime, with one method per
    /// command. The instance is kept for the life of the app. The command centre does not retain its
    /// targets, so a target that is collected leaves a play key that stops working after the next GC.</summary>
    private static nint EnsureTarget()
    {
        if (_target != 0)
            return _target;

        var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RPMRemoteTarget", 0);
        if (cls == 0)
            return 0;

        // MPRemoteCommandHandlerStatus is an NSInteger, and the handler is called with the event: the
        // encoding says so, and only the runtime's own introspection ever reads it.
        const string handler = "q24@0:8@16";
        class_addMethod(cls, Sel("rpmPlay:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnPlay, handler);
        class_addMethod(cls, Sel("rpmPause:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnPause, handler);
        class_addMethod(cls, Sel("rpmToggle:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnToggle, handler);
        class_addMethod(cls, Sel("rpmNext:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnNext, handler);
        class_addMethod(cls, Sel("rpmPrevious:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnPrevious, handler);
        class_addMethod(cls, Sel("rpmSeek:"), (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnSeek, handler);
        objc_registerClassPair(cls);

        _target = Send(Send(cls, Sel("alloc")), Sel("init"));
        return _target;
    }

    /// <summary>MPRemoteCommandHandlerStatusSuccess. Returned for every press this class handles, which is
    /// all of them. A status of "no such content" would make the system stop sending the key.</summary>
    private const nint Success = 0;

    [UnmanagedCallersOnly]
    private static nint OnPlay(nint self, nint selector, nint e) => Raise(RemoteCommand.Play, 0);

    [UnmanagedCallersOnly]
    private static nint OnPause(nint self, nint selector, nint e) => Raise(RemoteCommand.Pause, 0);

    [UnmanagedCallersOnly]
    private static nint OnToggle(nint self, nint selector, nint e) => Raise(RemoteCommand.Toggle, 0);

    [UnmanagedCallersOnly]
    private static nint OnNext(nint self, nint selector, nint e) => Raise(RemoteCommand.Next, 0);

    [UnmanagedCallersOnly]
    private static nint OnPrevious(nint self, nint selector, nint e) => Raise(RemoteCommand.Previous, 0);

    [UnmanagedCallersOnly]
    private static nint OnSeek(nint self, nint selector, nint e)
    {
        var seconds = e == 0 ? 0 : SendForDouble(e, Sel("positionTime"));
        return Raise(RemoteCommand.SeekTo, seconds);
    }

    private static nint Raise(RemoteCommand command, double seconds)
    {
        try
        {
            Commanded?.Invoke(command, seconds);
        }
        catch
        {
            // A managed exception must never cross back into AppKit.
        }
        return Success;
    }

    // ── the tile ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tells the system what is playing, in full each time. The card carries every field the tile
    /// draws. So the dictionary is built from scratch and replaces the last one, rather than being added
    /// to.
    /// </summary>
    internal static void Show(NowPlayingCard card)
    {
        try
        {
            var center = LoadFramework() ? Center() : 0;
            if (center == 0)
                return;

            var info = Send(objc_getClass("NSMutableDictionary"), Sel("dictionary"));
            if (info == 0)
                return;

            Put(info, "MPMediaItemPropertyTitle", Str(card.Title));
            if (card.Artist is { } artist)
                Put(info, "MPMediaItemPropertyArtist", Str(artist));
            if (card.Album is { } album)
                Put(info, "MPMediaItemPropertyAlbumTitle", Str(album));

            // A station has no end to count towards. Left out entirely rather than sent as nought, which
            // the tile would draw as a scrubber pinned at the start of a track that never moves.
            if (!card.IsLive)
            {
                Put(info, "MPMediaItemPropertyPlaybackDuration", Num(card.Duration));
                Put(info, "MPNowPlayingInfoPropertyElapsedPlaybackTime", Num(card.Elapsed));
            }

            // The rate makes the tile's own clock run between our updates. It is 1 while playing and 0
            // while paused. The elapsed time above is where the clock counts from.
            Put(info, "MPNowPlayingInfoPropertyPlaybackRate", Num(card.Playing ? 1 : 0));

            if (_artwork != 0)
                Put(info, "MPMediaItemPropertyArtwork", _artwork);

            Send(center, Sel("setNowPlayingInfo:"), info);
            SendUInt(center, Sel("setPlaybackState:"), card.Playing ? Playing : Paused);
        }
        catch
        {
            // Best effort: the app's own transport is the one that matters.
        }
    }

    /// <summary>Nothing is playing. The tile is removed, so it does not keep showing a record that has
    /// stopped.</summary>
    internal static void Clear()
    {
        try
        {
            var center = LoadFramework() ? Center() : 0;
            if (center == 0)
                return;

            Send(center, Sel("setNowPlayingInfo:"), 0);
            SendUInt(center, Sel("setPlaybackState:"), Stopped);
            SetArtwork(null);
        }
        catch
        {
            // Best effort, as in Show.
        }
    }

    /// <summary>MPNowPlayingPlaybackState. The tile reads this value, not the rate. A paused app that does
    /// not set it keeps showing as playing.</summary>
    private const nuint Playing = 1;
    private const nuint Paused = 2;
    private const nuint Stopped = 3;

    private static nint Center() =>
        Send(objc_getClass("MPNowPlayingInfoCenter"), Sel("defaultCenter"));

    /// <summary>Puts one field in the dictionary, under a key that the framework exports as a symbol. The
    /// key is read from the loaded framework and is not written as a literal. A literal would be the
    /// symbol's name, but the key is the string that the symbol points to.</summary>
    private static void Put(nint info, string keySymbol, nint value)
    {
        var key = KeyFor(keySymbol);
        if (value == 0 || key == 0)
            return;
        Send(info, Sel("setObject:forKey:"), value, key);
    }

    private static readonly Dictionary<string, nint> _keys = [];

    private static nint KeyFor(string symbol)
    {
        if (_keys.TryGetValue(symbol, out var cached))
            return cached;

        var address = dlsym(_framework, symbol);
        var key = address == 0 ? 0 : Marshal.ReadIntPtr(address);
        _keys[symbol] = key;
        return key;
    }

    // ── the sleeve ───────────────────────────────────────────────────────────────────────────────

    private static nint _artImage;    // NSImage*, held: the block hands this back whenever asked
    private static nint _artwork;     // MPMediaItemArtwork*, held: it is what goes in the dictionary
    private static nint _block;

    /// <summary>The record's cover, as the tile wants it. Handing over the picture takes two steps: an
    /// NSImage of our own, and an MPMediaItemArtwork that asks for it at whatever size the system is
    /// drawing. Null removes the cover. A track with no art must not keep the previous record's sleeve,
    /// because that is worse than no sleeve.
    /// </summary>
    internal static void SetArtwork(byte[]? picture)
    {
        try
        {
            // The artwork object goes first and always: it is what the tile reads, and a title shown
            // under the last record's sleeve is worse than a title shown with none.
            Forget(ref _artwork);

            if (picture is not { Length: > 0 } bytes || !LoadFramework())
                return;

            nint image;
            fixed (byte* p = bytes)
            {
                var data = SendBytes(objc_getClass("NSData"), Sel("dataWithBytes:length:"), p, (nuint)bytes.Length);
                if (data == 0)
                    return;
                image = Send(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            }
            if (image == 0)
                return;

            // The block only ever hands back a picture that replaces the old one. It never returns nothing.
            // The system may still hold an artwork object from an earlier update, and it can ask for the
            // picture at any time. It must get the old sleeve back, not nil.
            Forget(ref _artImage);
            _artImage = image;

            var block = EnsureBlock();
            if (block == 0)
                return;

            var size = new CGSize { Width = 512, Height = 512 };
            var artwork = Send(objc_getClass("MPMediaItemArtwork"), Sel("alloc"));
            _artwork = artwork == 0
                ? 0
                : SendSizeBlock(artwork, Sel("initWithBoundsSize:requestHandler:"), size, block);
        }
        catch
        {
            _artwork = 0;
        }
    }

    /// <summary>Lets go of an object we own. The object is autoreleased, not released at once. The system
    /// may hold the artwork, or ask the block for the picture, on its own thread when a new record starts.
    /// The main run loop's pool releases the reference a turn later, and by then any user of the object has
    /// retained it.</summary>
    private static void Forget(ref nint obj)
    {
        if (obj == 0)
            return;
        try { Send(obj, Sel("autorelease")); } catch { }
        obj = 0;
    }

    /// <summary>
    /// The one block in the file: <c>MPMediaItemArtwork</c> asks for the picture through a handler and
    /// offers no other way in.
    ///
    /// <para>The block is built by hand as a GLOBAL block. The flag that marks it as global stops the ObjC
    /// runtime from copying it to the heap or ever freeing it. It has a function pointer for its body and
    /// captures nothing. Capturing nothing is the point. The picture it returns is read from a field at the
    /// moment it is asked for. So the same block serves every record the app plays, and there is one
    /// allocation for the life of the app, not one per track.</para>
    /// </summary>
    private static nint EnsureBlock()
    {
        if (_block != 0)
            return _block;

        const nint RtldDefault = -2;
        var isa = dlsym(RtldDefault, "_NSConcreteGlobalBlock");
        if (isa == 0)
            return 0;

        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);

        var literal = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        literal->Isa = isa;
        literal->Flags = 1 << 28;   // BLOCK_IS_GLOBAL
        literal->Invoke = (nint)(delegate* unmanaged<nint, CGSize, nint>)&ProvideImage;
        literal->Descriptor = (nint)descriptor;

        _block = (nint)literal;
        return _block;
    }

    [UnmanagedCallersOnly]
    private static nint ProvideImage(nint block, CGSize size) => _artImage;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }
}
