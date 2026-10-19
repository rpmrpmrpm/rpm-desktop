using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Windows' own volume control for one output endpoint. It also tells whether that endpoint is a Bluetooth
/// speaker.
///
/// <para>This is the Windows version of what <see cref="MacSystemVolume"/> does, and it works the same way.
/// The endpoint volume of an A2DP speaker is sent to the speaker as AVRCP absolute volume. So
/// <c>IAudioEndpointVolume::SetMasterVolumeLevelScalar</c> changes the speaker itself. It does not reduce
/// our samples before the codec. No Bluetooth API is used on this side either.</para>
///
/// <para>The interfaces are called through their vtables, not through COM interop. <see
/// cref="WinAudioDevices"/> does the same. It needs only a few slots, no <c>ComWrappers</c> and no
/// marshalling layer. It also keeps this file easy to check against the MMDevice headers.</para>
///
/// <para>Every COM call happens on one MTA thread that this class owns. See <see cref="Ask"/>. The knob is
/// turned from the interface thread and from the audio thread. A pointer obtained in one apartment must not
/// be called from another. Owning a thread costs a few microseconds per call, and it avoids the
/// problem.</para>
///
/// <para>This class is Windows only, and that is stated on the type. Every caller already checks
/// <c>OperatingSystem.IsWindows()</c> (see <see cref="SystemVolume"/>). Stating it here too lets the
/// compiler check the COM worker's apartment, instead of warning that the code might not run on
/// Windows.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static unsafe class WinSystemVolume
{
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IidAudioEndpointVolumeCallback = new("657804FA-D6AD-4496-8A60-352752AF4F89");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>The device and driver property set. <c>PKEY_Device_FriendlyName</c> is the endpoint's name
    /// as everything shows it, including the sound library. That is how the two lists are matched.
    /// <c>PKEY_Device_EnumeratorName</c> is the bus the endpoint came in on. That is how a Bluetooth
    /// speaker is told apart from a sound card.</summary>
    private static readonly Guid DeviceProperties = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint PidFriendlyName = 14;
    private const uint PidEnumeratorName = 24;

    private const uint ClsCtxAll = 23;
    private const uint CoinitMultithreaded = 0;

    private const int SOk = 0;
    private const int EPointer = unchecked((int)0x80004003);
    private const int ENoInterface = unchecked((int)0x80004002);

    private const int FlowRender = 0;
    private const int RoleMultimedia = 1;
    private const uint DeviceStateActive = 1;

    private const ushort VtLpwstr = 31;

    /// <summary>The value of <c>PKEY_Device_EnumeratorName</c> for a speaker that arrived over Bluetooth.
    /// Classic A2DP enumerates under <c>BTHENUM</c>, hands-free under <c>BTHHFENUM</c>, and LE Audio under
    /// <c>BTHLEENUM</c>. The rule checks the prefix, not the three names, so a new Bluetooth bus that
    /// Microsoft adds later is still covered without an update.</summary>
    private const string BluetoothBus = "BTH";

    // ── vtable slots, as the MMDevice headers order them ─────────────────────────────────────────────
    //
    // Every interface starts with IUnknown's three (QueryInterface, AddRef, Release), so the first method
    // of each interface proper is slot 3.

    private const int MmEnumeratorEnumAudioEndpoints = 3;
    private const int MmEnumeratorGetDefaultAudioEndpoint = 4;
    private const int MmCollectionGetCount = 3;
    private const int MmCollectionItem = 4;
    private const int MmDeviceActivate = 3;
    private const int MmDeviceOpenPropertyStore = 4;
    private const int PropertyStoreGetValue = 5;
    private const int EndpointVolumeRegisterNotify = 3;
    private const int EndpointVolumeUnregisterNotify = 4;
    private const int EndpointVolumeSetMasterScalar = 7;
    private const int EndpointVolumeGetMasterScalar = 9;
    private const int UnknownRelease = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>A <c>PROPVARIANT</c>, as far as we need it: the tag, its three reserved words and the
    /// union. Only the first pointer of the union is ever read, for a <c>VT_LPWSTR</c>. The struct has the
    /// same size as the real one, so <c>PropVariantClear</c> gets what it expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Value;
        public nint Value2;
    }

    /// <summary>The volume notification the endpoint sends, up to the field we want. The channel array that
    /// follows it is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeNotification
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
                                               out nint instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);

    // ── the one thread that talks to COM ─────────────────────────────────────────────────────────────

    private static readonly BlockingCollection<Action> _jobs = new();
    private static Thread? _worker;

    /// <summary>Runs a piece of COM work on this class's own MTA thread and waits for the answer.
    ///
    /// <para>The thread starts on first use and lives as long as the process. Waiting is what makes the
    /// knob usable from anywhere without an apartment check. The caller blocks for the length of one or two
    /// MMDevice calls, which take microseconds. Volume changes are already merged by the player's own
    /// debounce before they get here.</para></summary>
    private static T Ask<T>(Func<T> work, T fallback)
    {
        if (!OperatingSystem.IsWindows()) return fallback;

        StartWorker();

        var done = new ManualResetEventSlim(false);
        var answer = fallback;

        _jobs.Add(() =>
        {
            try { answer = work(); }
            catch { answer = fallback; }
            finally { done.Set(); }
        });

        // The wait has a time limit, so a COM call into a driver that has stopped answering cannot hang the
        // interface thread. The knob simply does not move during that time.
        return done.Wait(2000) ? answer : fallback;
    }

    private static void StartWorker()
    {
        if (_worker is not null) return;

        lock (_jobs)
        {
            if (_worker is not null) return;

            _worker = new Thread(Work) { IsBackground = true, Name = "audio-volume-com" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
    }

    private static void Work()
    {
        // COM may already be initialised on this thread, or be set up differently. Both are fine. We only
        // need COM running, and this thread belongs to us.
        CoInitializeEx(0, CoinitMultithreaded);

        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); } catch { }
        }
    }

    // ── the endpoint we hold ─────────────────────────────────────────────────────────────────────────

    /// <summary><c>IAudioEndpointVolume</c> for the endpoint the music is on, or 0. Only ever touched on
    /// the worker thread.</summary>
    private static nint _volume;

    private static void** _callbackVtable;
    private static void* _callback;
    private static bool _registered;

    /// <summary>Raised when the volume is changed by something other than this app: the volume keys, the
    /// tray slider, or the buttons on the speaker, which send the level back over AVRCP. The argument is
    /// the new percentage. It is raised on an MMDevice API thread.</summary>
    internal static event Action<int>? Changed;

    /// <summary>
    /// Points at the endpoint the sound is now coming out of. Returns whether its own volume is ours to
    /// turn. That is true only for a Bluetooth speaker that gives us an <c>IAudioEndpointVolume</c>.
    /// </summary>
    internal static bool Open(string? name) => Ask(() => OpenOnWorker(EndpointNames.Bare(name)), false);

    private static bool OpenOnWorker(string name)
    {
        ReleaseHeld();

        var device = FindDevice(name);
        if (device == 0)
        {
            Said.Line("volume", "no active render endpoint has the asked name, keeping our own gain");
            return false;
        }

        try
        {
            // The bus name is read from the device at run time, because it cannot be worked out from the
            // headers. If the rule does not match, the knob is silently never used. This line logs the
            // reason in one place.
            var bus = StringProperty(device, PidEnumeratorName);
            if (!bus.StartsWith(BluetoothBus, StringComparison.OrdinalIgnoreCase))
            {
                Said.Line("volume", $"the endpoint is on the '{bus}' bus, not Bluetooth, keeping our own gain");
                return false;
            }

            fixed (Guid* iid = &IidAudioEndpointVolume)
            {
                var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)
                    (*(void***)device)[MmDeviceActivate];

                nint volume = 0;
                var hr = activate(device, iid, ClsCtxAll, 0, &volume);
                if (hr < 0 || volume == 0)
                {
                    Said.Line("volume", $"the endpoint is Bluetooth but gave no endpoint volume (0x{hr:X8})");
                    return false;
                }

                _volume = volume;
            }
        }
        finally { ReleaseCom(device); }

        Register();
        Said.Line("volume", "the endpoint is a Bluetooth speaker, its own volume is the knob now" +
                        (_registered ? "" : ", though it refused to report changes back"));
        return true;
    }

    /// <summary>The held endpoint's volume as a percentage.</summary>
    internal static bool TryRead(out int volume)
    {
        var level = Ask(() =>
        {
            if (_volume == 0) return -1f;

            float scalar = 0;
            var get = (delegate* unmanaged[Stdcall]<nint, float*, int>)
                (*(void***)_volume)[EndpointVolumeGetMasterScalar];

            return get(_volume, &scalar) < 0 ? -1f : scalar;
        }, -1f);

        volume = level < 0 ? 0 : Percent(level);
        return level >= 0;
    }

    /// <summary>Turns the held endpoint's own volume, which is the speaker's own volume.</summary>
    internal static bool TryWrite(int volume) => Ask(() =>
    {
        if (_volume == 0) return false;

        var set = (delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)
            (*(void***)_volume)[EndpointVolumeSetMasterScalar];

        // Pass a null event context, so our own change comes back through the callback like any other
        // change. Telling them apart is the job of the echo guard. It uses the level and the timing (see
        // VolumeRouting.ShouldAdopt). That also covers a case the event context cannot: another process
        // moving the same endpoint.
        return set(_volume, Math.Clamp(volume, 0, 100) / 100f, null) >= 0;
    }, false);

    private static int Percent(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);

    // ── finding the endpoint ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The active render endpoint that the sound library is playing to, found by the name the library uses,
    /// or 0.
    ///
    /// <para>The match is on <c>PKEY_Device_FriendlyName</c>, the string that OpenAL Soft's WASAPI backend
    /// builds its own device names from. See <see cref="EndpointNames"/> for the one difference between
    /// them. An empty name means the machine's default, and the default is returned. A name that matches
    /// nothing returns 0, not the default. That happens when an endpoint is pinned in settings, and using
    /// the default there would move a speaker that nobody is listening to.</para>
    /// </summary>
    private static nint FindDevice(string name)
    {
        if (CoCreateInstance(in ClsidMmDeviceEnumerator, 0, ClsCtxAll, in IidMmDeviceEnumerator,
                             out var enumerator) < 0 || enumerator == 0)
            return 0;

        try
        {
            if (name.Length == 0)
            {
                var getDefault = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)
                    (*(void***)enumerator)[MmEnumeratorGetDefaultAudioEndpoint];

                nint device = 0;
                return getDefault(enumerator, FlowRender, RoleMultimedia, &device) < 0 ? 0 : device;
            }

            var enumerate = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)
                (*(void***)enumerator)[MmEnumeratorEnumAudioEndpoints];

            nint collection = 0;
            if (enumerate(enumerator, FlowRender, DeviceStateActive, &collection) < 0 || collection == 0)
                return 0;

            try
            {
                var count = (delegate* unmanaged[Stdcall]<nint, uint*, int>)
                    (*(void***)collection)[MmCollectionGetCount];
                var item = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)
                    (*(void***)collection)[MmCollectionItem];

                uint total = 0;
                if (count(collection, &total) < 0) return 0;

                for (uint i = 0; i < total; i++)
                {
                    nint device = 0;
                    if (item(collection, i, &device) < 0 || device == 0) continue;

                    if (string.Equals(StringProperty(device, PidFriendlyName), name, StringComparison.Ordinal))
                        return device;

                    ReleaseCom(device);
                }
            }
            finally { ReleaseCom(collection); }
        }
        finally { ReleaseCom(enumerator); }

        return 0;
    }

    private static string StringProperty(nint device, uint propertyId)
    {
        var open = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)
            (*(void***)device)[MmDeviceOpenPropertyStore];

        nint store = 0;
        if (open(device, 0 /* STGM_READ */, &store) < 0 || store == 0) return "";

        try
        {
            var get = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)
                (*(void***)store)[PropertyStoreGetValue];

            var key = new PropertyKey { FormatId = DeviceProperties, PropertyId = propertyId };
            var value = default(PropVariant);

            if (get(store, &key, &value) < 0) return "";

            try
            {
                return value.Type == VtLpwstr && value.Value != 0
                    ? Marshal.PtrToStringUni(value.Value) ?? ""
                    : "";
            }
            finally { PropVariantClear(ref value); }
        }
        finally { ReleaseCom(store); }
    }

    private static void ReleaseCom(nint instance)
    {
        if (instance == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)instance)[UnknownRelease];
        release(instance);
    }

    private static void ReleaseHeld()
    {
        if (_volume == 0) return;

        if (_registered)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, void*, int>)
                (*(void***)_volume)[EndpointVolumeUnregisterNotify];
            unregister(_volume, _callback);
            _registered = false;
        }

        ReleaseCom(_volume);
        _volume = 0;
    }

    // ── being told ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Registers our callback with the endpoint. The callback object is a vtable and one
    /// allocation. It is built once and reused for every endpoint. There is only one of it, and it lives
    /// longer than anything that could hold it, so its lifetime methods do nothing useful.</summary>
    private static void Register()
    {
        if (_volume == 0) return;

        if (_callback == null)
        {
            _callbackVtable = (void**)NativeMemory.Alloc(4, (nuint)sizeof(nint));
            _callbackVtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
            _callbackVtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
            _callbackVtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            _callbackVtable[3] = (delegate* unmanaged[Stdcall]<void*, VolumeNotification*, int>)&OnNotify;

            _callback = NativeMemory.Alloc((nuint)sizeof(nint));
            *(void***)_callback = _callbackVtable;
        }

        var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)
            (*(void***)_volume)[EndpointVolumeRegisterNotify];

        _registered = register(_volume, _callback) >= 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** instance)
    {
        if (instance == null) return EPointer;
        if (iid != null && (*iid == IidUnknown || *iid == IidAudioEndpointVolumeCallback))
        {
            *instance = self;
            return SOk;
        }
        *instance = null;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    /// <summary>The endpoint's volume changed. The new level is in the notification itself, so this does
    /// not ask COM anything. That matters, because this runs on the MMDevice API's own thread. Calling back
    /// into that API from inside a notification makes the thread deadlock.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNotify(void* self, VolumeNotification* data)
    {
        if (data != null) Changed?.Invoke(Percent(data->MasterVolume));
        return SOk;
    }
}
