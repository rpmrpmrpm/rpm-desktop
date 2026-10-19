using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="MacWindowCard.QuietClose"/>: the splash card's <c>performClose:</c> does nothing, every other
/// object of the same class still reaches its own <c>performClose:</c>, and the card's class is never
/// changed.
///
/// <para>Run on NSObject subclasses made for each test rather than on windows: an NSWindow may only be made
/// on the main thread, and the tests do not run there. The runtime steps are the same for any class. Each
/// test makes its own classes, so the method <c>QuietClose</c> adds never reaches another test's
/// objects.</para>
///
/// <para>The class shapes copy Uno's. <c>UNOWindow</c> does not define <c>performClose:</c>. It inherits
/// NSWindow's, so the stand-in is a subclass of a class that defines it, and it counts the calls. On a
/// borderless window, AppKit's <c>performClose:</c> plays the alert sound and leaves the window open.
/// Changing a window's class can crash the app. See the remarks on
/// <see cref="MacWindowCard.QuietClose"/>.</para>
/// </summary>
[Collection(AllocatorReuseCollection.Name)]
public unsafe class MacWindowCardTests
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint object_getClass(nint obj);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(nint receiver, nint selector, nint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendString(nint receiver, nint selector,
                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary><c>addObserver:forKeyPath:options:context:</c></summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendAddObserver(nint receiver, nint selector, nint observer, nint keyPath,
                                               nuint options, nint context);

    /// <summary><c>removeObserver:forKeyPath:context:</c></summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendRemoveObserver(nint receiver, nint selector, nint observer, nint keyPath,
                                                  nint context);

    /// <summary>Calls that reached a stand-in class's own <c>performClose:</c>, by object.</summary>
    private static readonly ConcurrentDictionary<nint, int> Closed = new();

    [UnmanagedCallersOnly]
    private static void CountClose(nint self, nint selector, nint sender) =>
        Closed.AddOrUpdate(self, 1, (_, n) => n + 1);

    private static nint Sel(string name) => sel_registerName(name);

    private static nint CountingClass(nint superclass, string role)
    {
        var cls = objc_allocateClassPair(superclass, $"RPMTest{role}_{Guid.NewGuid():N}", 0);
        Assert.NotEqual(0, cls);
        Assert.True(class_addMethod(cls, Sel("performClose:"),
                                    (nint)(delegate* unmanaged<nint, nint, nint, void>)&CountClose, "v@:@"));
        objc_registerClassPair(cls);
        return cls;
    }

    private static nint PlainClass(nint superclass, string role)
    {
        var cls = objc_allocateClassPair(superclass, $"RPMTest{role}_{Guid.NewGuid():N}", 0);
        Assert.NotEqual(0, cls);
        objc_registerClassPair(cls);
        return cls;
    }

    /// <summary>A class shaped like <c>UNOWindow</c>: <c>performClose:</c> is inherited, not its
    /// own.</summary>
    private static nint WindowClass() =>
        PlainClass(CountingClass(objc_getClass("NSObject"), "NSWindow"), "UnoWindow");

    /// <summary>A new object, with no calls counted. An earlier test's object may have had the same
    /// address.</summary>
    private static nint New(nint cls)
    {
        var obj = Send(Send(cls, Sel("alloc")), Sel("init"));
        Closed.TryRemove(obj, out _);
        return obj;
    }

    private static void Release(nint obj) => Send(obj, Sel("release"));

    /// <summary>Sent the way <c>uno_window_close</c> sends it.</summary>
    private static void PerformClose(nint obj) => SendPtr(obj, Sel("performClose:"), 0);

    private static int ClosesOf(nint obj) => Closed.TryGetValue(obj, out var n) ? n : 0;

    [Fact]
    public void The_card_keeps_its_class()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var cls = WindowClass();
        var card = New(cls);
        try
        {
            Assert.True(MacWindowCard.QuietClose(card));

            // Key-value observing finds an object's registrations through its class. A card moved into
            // another class could not have its observers removed, and NSWindow removes its own in dealloc.
            // Changing the class caused a crash at quit.
            Assert.Equal(cls, object_getClass(card));
        }
        finally
        {
            Release(card);
        }
    }

    [Fact]
    public void Only_the_card_ignores_performClose()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var cls = WindowClass();
        var card = New(cls);
        var other = New(cls);
        try
        {
            Assert.True(MacWindowCard.QuietClose(card));

            PerformClose(card);
            PerformClose(other);

            Assert.Equal(0, ClosesOf(card));
            Assert.Equal(1, ClosesOf(other));
        }
        finally
        {
            Release(card);
            Release(other);
        }
    }

    [Fact]
    public void A_class_with_its_own_performClose_keeps_it_for_other_objects()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // The other case: the method is defined on the window's class itself, which a later Uno version
        // could do. QuietClose then replaces it, and calls the replaced method for every other object.
        var cls = CountingClass(objc_getClass("NSObject"), "OwnClose");
        var card = New(cls);
        var other = New(cls);
        try
        {
            Assert.True(MacWindowCard.QuietClose(card));

            PerformClose(card);
            PerformClose(other);

            Assert.Equal(0, ClosesOf(card));
            Assert.Equal(1, ClosesOf(other));
            Assert.Equal(cls, object_getClass(card));
        }
        finally
        {
            Release(card);
            Release(other);
        }
    }

    [Fact]
    public void An_observer_added_before_is_removed_after()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // This is the order the app runs in: something observes the window before the card is dressed. If
        // the card's class were changed, this removal would crash the test host (SIGSEGV in
        // _NSKeyValueRetainedObservationInfoForObject). Key-value observing moves an object into a subclass
        // of its own, and it then expects the class it made. On the app's window, which AppKit observes
        // through a forwarded key path, the same change would throw NSRangeException from -[NSWindow
        // dealloc].
        var cls = WindowClass();
        var card = New(cls);
        var other = New(cls);
        var observer = New(objc_getClass("NSObject"));
        var keyPath = SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), "tag");
        try
        {
            SendAddObserver(card, Sel("addObserver:forKeyPath:options:context:"), observer, keyPath, 0, 0);

            Assert.True(MacWindowCard.QuietClose(card));

            PerformClose(card);
            PerformClose(other);
            Assert.Equal(0, ClosesOf(card));
            Assert.Equal(1, ClosesOf(other));

            SendRemoveObserver(card, Sel("removeObserver:forKeyPath:context:"), observer, keyPath, 0);
        }
        finally
        {
            Release(card);
            Release(other);
            Release(observer);
        }
    }

    [Fact]
    public void A_second_card_does_not_wrap_the_first()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // If the second call took the method that was installed the first time as the original, every other
        // object's performClose: would call itself until the stack ran out.
        var cls = WindowClass();
        var first = New(cls);
        var second = New(cls);
        var other = New(cls);
        try
        {
            Assert.True(MacWindowCard.QuietClose(first));
            Assert.True(MacWindowCard.QuietClose(second));
            Assert.True(MacWindowCard.QuietClose(first));

            PerformClose(first);
            PerformClose(second);
            PerformClose(other);

            Assert.Equal(0, ClosesOf(first));
            Assert.Equal(0, ClosesOf(second));
            Assert.Equal(1, ClosesOf(other));
        }
        finally
        {
            Release(first);
            Release(second);
            Release(other);
        }
    }

    [Fact]
    public void A_card_of_a_subclass_does_not_wrap_the_class_it_inherits_from()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // The subclass inherits the method that QuietClose put on its parent. Taking that method for the
        // subclass's original would loop in the same way.
        var parent = WindowClass();
        var child = PlainClass(parent, "UnoWindowChild");
        var card = New(parent);
        var childCard = New(child);
        var childOther = New(child);
        try
        {
            Assert.True(MacWindowCard.QuietClose(card));
            Assert.True(MacWindowCard.QuietClose(childCard));

            PerformClose(childCard);
            PerformClose(childOther);

            Assert.Equal(0, ClosesOf(childCard));
            Assert.Equal(1, ClosesOf(childOther));
        }
        finally
        {
            Release(card);
            Release(childCard);
            Release(childOther);
        }
    }

    [Fact]
    public void A_freed_card_leaves_nothing_at_its_address()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // A card that was freed while the app was running must leave nothing behind at its address. A card
        // kept by its address would make the next window allocated there ignore performClose: too, so that
        // window could not be closed.
        var cls = WindowClass();
        var card = New(cls);
        Assert.True(MacWindowCard.QuietClose(card));
        Release(card);

        var made = new List<nint>();
        try
        {
            nint reused = 0;
            for (var i = 0; i < 256 && reused == 0; i++)
            {
                var next = New(cls);
                made.Add(next);
                if (next == card)
                    reused = next;
            }

            // The allocator hands a freed block of this size straight back. If that ever stops, this
            // test has nothing to check and says so.
            Assert.NotEqual(0, reused);

            PerformClose(reused);
            Assert.Equal(1, ClosesOf(reused));
        }
        finally
        {
            foreach (var obj in made)
                Release(obj);
        }
    }

    [Fact]
    public void No_window_is_refused()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.False(MacWindowCard.QuietClose(0));
    }
}

/// <summary>
/// Tests that need the allocator to hand a freed block straight back. This only holds while no other test
/// allocates in between. xunit runs this collection on its own, after the parallel tests. If it runs beside
/// <see cref="ObjcStructReturnTests"/>, which makes objects of the same size on another thread,
/// <see cref="MacWindowCardTests.A_freed_card_leaves_nothing_at_its_address"/> can fail. It failed once in
/// four runs when they ran together.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocatorReuseCollection
{
    public const string Name = "Allocator reuse";
}
