using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Windows tells us when the machine's sound devices change. A speaker is connected or removed, or the
/// listener changes Windows' own output in the volume flyout or in Settings.
///
/// <para>This uses <c>IMMNotificationClient</c>, the endpoint notification callback of the MMDevice API.
/// The volume flyout uses the same notifications to redraw itself. The callback reports a change as soon as
/// Windows knows about it, so the app does not need to poll. This is the standard way to do it. NAudio's
/// <c>RegisterEndpointNotificationCallback</c> wraps it, and the WASAPI samples use it. OpenAL Soft's own
/// Windows backend passes these events on through <c>ALC_SOFT_system_events</c>. That extension only exists
/// from OpenAL Soft 1.24.0, and the native libraries we ship are 1.23.1. So the notification is taken from
/// the layer that has it, the same way <see cref="MacAudioDevices"/> does on the Mac.</para>
///
/// <para>The client is a hand-built COM object. It is a vtable of static function pointers over a plain
/// memory allocation. There is only one client, and it lives as long as the process, so its lifetime
/// methods have no real work to do. That is why it needs no COM interop, no <c>ComWrappers</c> and no
/// marshalling. It is safe to call from the MMDevice API's own notification thread.</para>
///
/// <para>That thread must not be held up. The callbacks only raise the action given to <see cref="Watch"/>.
/// By its contract, that action only sets a flag for the audio watcher to find.</para>
/// </summary>
internal static unsafe class WinAudioDevices
{
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidMmNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private const uint ClsCtxAll = 23;
    private const uint CoinitMultithreaded = 0;

    private const int SOk = 0;
    private const int EPointer = unchecked((int)0x80004003);
    private const int ENoInterface = unchecked((int)0x80004002);

    /// <summary>The flow and the roles worth waking up for. Only output, and only the two roles that decide
    /// where music goes. <c>eCommunications</c> is left out on purpose: it is the headset a call moves to.
    /// Following it would move the album to the conference microphone's earpiece each time someone dialled
    /// in.</summary>
    private const int FlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;

    /// <summary>Where <see cref="OnDeviceStateChanged"/> and the other callbacks send their news. It is
    /// volatile because it is written by the caller of <see cref="Watch"/> and read on an MMDevice API
    /// thread.</summary>
    private static volatile Action? _onChanged;

    private static bool _watching;
    private static nint _enumerator;
    private static void** _vtable;
    private static void* _client;

    /// <summary><c>PROPERTYKEY</c>, passed by value to <c>OnPropertyValueChanged</c>. We never read it, but
    /// its shape must still be declared. On 32-bit Windows a COM method is <c>__stdcall</c>, and the callee
    /// clears the arguments off the stack. So a signature with the wrong size does not give a wrong value.
    /// It corrupts the stack.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
                                               out nint instance);

    /// <summary>Starts listening, or points an existing listener at <paramref name="onChanged"/>. Returns
    /// false on anything that is not Windows, and when the MMDevice API refuses. The caller then uses its
    /// own fallback.</summary>
    internal static bool Watch(Action onChanged)
    {
        if (!OperatingSystem.IsWindows()) return false;

        _onChanged = onChanged;
        if (_watching) return true;

        // This runs on its own thread with an apartment model that we set, because the caller's thread is
        // not ours to choose. The audio service may register from a worker thread, and the settings page
        // from the UI thread, which on WinUI is a single-threaded apartment (STA). In the MTA the
        // enumerator is free-threaded, so the callbacks arrive on the MMDevice API's own thread and never
        // need marshalling.
        var ok = false;
        var thread = new Thread(() => { try { ok = Register(); } catch { ok = false; } })
        {
            IsBackground = true,
            Name = "audio-devices-com",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(5000);

        _watching = ok;
        return ok;
    }

    private static bool Register()
    {
        // COM may already be initialised on this thread, or be set up differently. Both are fine. We only
        // need COM to be running, and this thread belongs to us.
        CoInitializeEx(0, CoinitMultithreaded);

        if (CoCreateInstance(in ClsidMmDeviceEnumerator, 0, ClsCtxAll, in IidMmDeviceEnumerator,
                             out var enumerator) < 0 || enumerator == 0)
            return false;

        _vtable = (void**)NativeMemory.Alloc(8, (nuint)sizeof(nint));
        _vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
        _vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        _vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
        _vtable[3] = (delegate* unmanaged[Stdcall]<void*, char*, uint, int>)&OnDeviceStateChanged;
        _vtable[4] = (delegate* unmanaged[Stdcall]<void*, char*, int>)&OnDeviceAdded;
        _vtable[5] = (delegate* unmanaged[Stdcall]<void*, char*, int>)&OnDeviceRemoved;
        _vtable[6] = (delegate* unmanaged[Stdcall]<void*, int, int, char*, int>)&OnDefaultDeviceChanged;
        _vtable[7] = (delegate* unmanaged[Stdcall]<void*, char*, PropertyKey, int>)&OnPropertyValueChanged;

        // A COM object is a pointer to its vtable pointer, and this one carries no state beyond that.
        _client = NativeMemory.Alloc((nuint)sizeof(nint));
        *(void***)_client = _vtable;

        // The method table of IMMDeviceEnumerator starts with IUnknown's three methods. Then come
        // EnumAudioEndpoints, GetDefaultAudioEndpoint, GetDevice and RegisterEndpointNotificationCallback,
        // which is at index 6.
        var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)enumerator)[6];
        if (register(enumerator, _client) < 0) return false;

        // Kept alive for the life of the process. Releasing the enumerator also unregisters the callback,
        // so it must not be released.
        _enumerator = enumerator;
        return true;
    }

    private static void Raise()
    {
        try { _onChanged?.Invoke(); } catch { }
    }

    // ── the COM object ─────────────────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** instance)
    {
        if (instance == null) return EPointer;
        if (iid != null && (*iid == IidUnknown || *iid == IidMmNotificationClient))
        {
            *instance = self;
            return SOk;
        }
        *instance = null;
        return ENoInterface;
    }

    /// <summary>There is one client, and it lives longer than anything that could hold it, so the count is
    /// a constant. Always answering 1 is all that is needed.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    /// <summary>An endpoint became active, was unplugged, was disabled, or is no longer present. This is
    /// the most common way a speaker leaves, and the earliest point at which the app can know it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(void* self, char* deviceId, uint state)
    {
        Raise();
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAdded(void* self, char* deviceId)
    {
        Raise();
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceRemoved(void* self, char* deviceId)
    {
        Raise();
        return SOk;
    }

    /// <summary>Windows changed its own default output. The app has to follow this one too. Nothing is
    /// disconnected, every endpoint is still there, and the music should now come out of a different
    /// one.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(void* self, int flow, int role, char* deviceId)
    {
        if (flow == FlowRender && (role == RoleConsole || role == RoleMultimedia)) Raise();
        return SOk;
    }

    /// <summary>A property of some endpoint changed, such as its name, format or policy. The app does not
    /// follow these. They fire often, so waking the watcher for each one would be noise.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(void* self, char* deviceId, PropertyKey key) => SOk;
}
