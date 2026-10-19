using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RPM.Core.Audio;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The Mac's own volume knob for one output device, and whether that device is a Bluetooth speaker.
///
/// <para>This is how the app lowers the volume of a Bluetooth speaker on macOS. There is no public
/// Bluetooth API for this. IOBluetooth has no AVRCP volume call. The system does this step itself. When
/// the output device is an A2DP speaker, setting its <c>kAudioDevicePropertyVolumeScalar</c> is sent to
/// the speaker as an AVRCP absolute volume command. That is the same command the speaker's own buttons
/// send. So the app calls CoreAudio, and the speaker's volume changes.</para>
///
/// <para>A speaker that does not support absolute volume ignores the command. macOS then lowers the
/// volume in software. The app would do the same thing itself, so asking the system costs
/// nothing.</para>
///
/// <para>Every call is best effort. Each one returns false instead of throwing. A device can disappear
/// between two calls, and a Bluetooth device often does so in the middle of use.</para>
/// </summary>
internal static unsafe class MacSystemVolume
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // Four-character codes, as CoreAudio names its selectors.
    private const uint SystemObject = 1;
    private const uint DefaultOutputDevice = 0x644F7574;   // 'dOut'  kAudioHardwarePropertyDefaultOutputDevice
    private const uint Devices = 0x64657623;               // 'dev#'  kAudioHardwarePropertyDevices
    private const uint ObjectName = 0x6C6E616D;            // 'lnam'  kAudioObjectPropertyName
    private const uint TransportType = 0x7472616E;         // 'tran'  kAudioDevicePropertyTransportType
    private const uint VolumeScalar = 0x766F6C6D;          // 'volm'  kAudioDevicePropertyVolumeScalar
    private const uint Streams = 0x73746D23;               // 'stm#'  kAudioDevicePropertyStreams
    private const uint StereoChannels = 0x64636832;        // 'dch2'  kAudioDevicePropertyPreferredChannelsForStereo

    private const uint ScopeGlobal = 0x676C6F62;           // 'glob'
    private const uint ScopeOutput = 0x6F757470;           // 'outp'
    private const uint ElementMain = 0;

    private const uint TransportBluetooth = 0x626C7565;    // 'blue'  kAudioDeviceTransportTypeBluetooth
    private const uint TransportBluetoothLE = 0x626C6561;  // 'blea'  kAudioDeviceTransportTypeBluetoothLE

    private const uint Utf8 = 0x08000100;                  // kCFStringEncodingUTF8

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, void* qualifier,
        ref uint dataSize, void* data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref PropertyAddress address, uint qualifierSize, void* qualifier, out uint dataSize);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, void* qualifier,
        uint dataSize, void* data);

    [DllImport(CoreAudio)]
    private static extern byte AudioObjectHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectIsPropertySettable(
        uint objectId, ref PropertyAddress address, out byte settable);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectAddPropertyListener(
        uint objectId, ref PropertyAddress address, nint listener, nint userData);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectRemovePropertyListener(
        uint objectId, ref PropertyAddress address, nint listener, nint userData);

    [DllImport(CoreFoundation)]
    private static extern byte CFStringGetCString(nint str, byte* buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(nint str);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);

    // ── the endpoint we hold ─────────────────────────────────────────────────────────────────────────

    private static uint _held;

    /// <summary>Raised when the volume changes from outside this app, such as the volume keys or a
    /// button on the speaker. It carries the new level as a percentage. It is raised on a CoreAudio
    /// thread.</summary>
    internal static event Action<int>? Changed;

    /// <summary>
    /// Points at the device the sound is now going to, and returns whether this app may control its
    /// volume. Here that means a Bluetooth device that accepts a level.
    /// </summary>
    internal static bool Open(string? name)
    {
        var asked = EndpointNames.Bare(name);
        var device = Find(asked);
        var transport = device == 0 ? 0 : Transport(device);
        var bluetooth = transport is TransportBluetooth or TransportBluetoothLE;
        var settable = bluetooth && CanSet(device);
        var ours = device != 0 && settable;

        _held = ours ? device : 0;

        // Only followed while the device is ours. Following a device we do not control would move the
        // app's slider whenever the volume keys were used for a speaker we are not playing to.
        Follow(_held);

        RPM.Core.Diagnostics.Said.Line("volume", Say(name, device, transport, bluetooth, ours));

        return ours;
    }

    /// <summary>
    /// Describes what was decided about this device, with enough detail to act on it.
    ///
    /// <para>Three different cases leave the app with its own gain. The machine has no device with that
    /// name. The device is not on a transport whose volume reaches a speaker. Or the device has a
    /// level, but the app may not write it. Each case needs a different fix, so the log line states
    /// which one applies.</para>
    /// </summary>
    private static string Say(string? asked, uint device, uint transport, bool bluetooth, bool ours)
    {
        // The log line never contains device names. A name is chosen by the user, and this line may be
        // attached to a crash report. Find matches a name exactly, so a device that was found has the
        // asked name. A device that was not found gives the "no output device" line.
        var called = string.IsNullOrEmpty(EndpointNames.Bare(asked))
            ? "the default output"
            : "the asked output";

        if (device == 0)
            return $"{called}: no output device of that name on this machine "
                 + "— our own gain stays the slider";

        var which = $"device {device} (transport {FourCC(transport)})";

        if (!bluetooth)
            return $"{called} is {which}, not Bluetooth: macOS would scale the samples itself "
                 + "rather than send the level to a speaker — our own gain stays the slider";

        if (!ours)
            return $"{called} is {which}, Bluetooth but with no volume we may write "
                 + "(read-only, or no master element and no stereo pair publishing one) "
                 + "— our own gain stays the slider";

        return $"{called} is {which}, a Bluetooth speaker that takes a level "
             + "— the slider turns the speaker itself";
    }

    /// <summary>Prints a CoreAudio four-character code as its four characters, such as 'bltn', 'usb '
    /// or 'blue'. The code is printed, not converted to a name. The codes are what the CoreAudio
    /// headers list, and a search finds them. A transport the app does not know still prints
    /// clearly.</summary>
    private static string FourCC(uint code)
    {
        if (code == 0)
            return "none";

        Span<char> chars =
        [
            (char)((code >> 24) & 0xFF), (char)((code >> 16) & 0xFF),
            (char)((code >> 8) & 0xFF), (char)(code & 0xFF),
        ];

        foreach (var c in chars)
            if (c is < ' ' or > '~')
                return $"0x{code:X8}";

        return $"'{new string(chars)}'";
    }

    /// <summary>Reads the held device's volume as a percentage.</summary>
    internal static bool TryRead(out int volume) => TryRead(_held, out volume);

    /// <summary>Sets the held device's own volume.</summary>
    internal static bool TryWrite(int volume) => TryWrite(_held, volume);

    // ── finding the device ───────────────────────────────────────────────────────────────────────────

    /// <summary>The device the machine calls its default output, or 0.</summary>
    private static uint DefaultOutput()
    {
        var address = Address(DefaultOutputDevice, ScopeGlobal);
        uint device = 0;
        uint size = sizeof(uint);

        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, null, ref size, &device) != 0)
                return 0;
        }
        catch { return 0; }

        return device;
    }

    /// <summary>
    /// Finds the output device the sound library is playing to, by its name. Returns 0 when the machine
    /// has no device with that name.
    ///
    /// <para>The match is by name because that is the only handle the two libraries share. OpenAL
    /// Soft's CoreAudio backend takes each device name from the same property, so the two names are
    /// identical. An empty name means "the machine's default output" in the sound library, and it is
    /// handled the same way here.</para>
    ///
    /// <para>A name that matches nothing is not replaced by the default output. One case produces such
    /// a name: a device pinned in settings that the machine names differently. Using the default there
    /// would send the sound to a speaker nobody is listening to.</para>
    /// </summary>
    private static uint Find(string? name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultOutput();

        try
        {
            var address = Address(Devices, ScopeGlobal);
            if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, null, out var size) != 0)
                return 0;

            var count = (int)(size / sizeof(uint));
            if (count <= 0) return 0;

            var devices = stackalloc uint[count];
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, null, ref size, devices) != 0)
                return 0;

            for (int i = 0; i < count; i++)
            {
                // Inputs are in the same list, and an input can share a name with an output on the same
                // device.
                if (!HasOutput(devices[i])) continue;
                if (string.Equals(NameOf(devices[i]), name, StringComparison.Ordinal)) return devices[i];
            }
        }
        catch { }

        return 0;
    }

    /// <summary>Whether the device has any output streams. This tells the speaker side of an interface
    /// apart from its microphone side.</summary>
    private static bool HasOutput(uint device)
    {
        var address = Address(Streams, ScopeOutput);
        try { return AudioObjectGetPropertyDataSize(device, ref address, 0, null, out var size) == 0 && size > 0; }
        catch { return false; }
    }

    private static string NameOf(uint device)
    {
        var address = Address(ObjectName, ScopeGlobal);
        nint cf = 0;
        uint size = (uint)sizeof(nint);

        try
        {
            if (AudioObjectGetPropertyData(device, ref address, 0, null, ref size, &cf) != 0 || cf == 0)
                return "";

            // The worst case for UTF-8 is four bytes per UTF-16 unit, plus the terminator that CFString
            // needs. The size is capped, because this buffer is on the stack and a device name is a few
            // dozen characters. A longer name would never match anyway.
            var bytes = Math.Min((int)CFStringGetLength(cf) * 4 + 1, 1024);
            var buffer = stackalloc byte[bytes];
            return CFStringGetCString(cf, buffer, bytes, Utf8) != 0
                ? Marshal.PtrToStringUTF8((nint)buffer) ?? ""
                : "";
        }
        catch { return ""; }
        finally { if (cf != 0) try { CFRelease(cf); } catch { } }
    }

    /// <summary>The transport the device is connected by, as CoreAudio's four-character code. The value
    /// is 0 when the device does not report it. Bluetooth is the transport where the machine's volume
    /// is passed to the speaker, instead of being applied to the samples. That is the question this
    /// file answers. The code itself is returned, not a yes or no, so the log can show what was
    /// found.</summary>
    private static uint Transport(uint device)
    {
        if (device == 0) return 0;

        var address = Address(TransportType, ScopeGlobal);
        uint transport = 0;
        uint size = sizeof(uint);

        try
        {
            if (AudioObjectGetPropertyData(device, ref address, 0, null, ref size, &transport) != 0)
                return 0;
        }
        catch { return 0; }

        return transport;
    }

    // ── the knob ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The elements the volume is on. This is the master element when the device has one, and otherwise
    /// the left and right channels.
    ///
    /// <para>Many Bluetooth devices have no master element. The property exists only for each channel.
    /// A get on element 0 fails there and does not average anything. The stereo pair is the one the
    /// device reports (<c>kAudioDevicePropertyPreferredChannelsForStereo</c>). If the device does not
    /// report it, channels 1 and 2 are used.</para>
    /// </summary>
    private static uint[] VolumeElements(uint device)
    {
        var master = Address(VolumeScalar, ScopeOutput, ElementMain);
        try { if (AudioObjectHasProperty(device, ref master) != 0) return [ElementMain]; } catch { return []; }

        var pair = stackalloc uint[2] { 1, 2 };
        var address = Address(StereoChannels, ScopeOutput);
        uint size = sizeof(uint) * 2;
        try { AudioObjectGetPropertyData(device, ref address, 0, null, ref size, pair); } catch { }

        var elements = new List<uint>(2);
        for (int i = 0; i < 2; i++)
        {
            var channel = Address(VolumeScalar, ScopeOutput, pair[i]);
            try { if (AudioObjectHasProperty(device, ref channel) != 0) elements.Add(pair[i]); } catch { }
        }

        return [.. elements];
    }

    /// <summary>Whether the machine allows this app to change the device's volume. A device can report
    /// its volume as read-only. Examples are an aggregate device, or a driver that reports a level but
    /// does not accept changes. Such a device has no usable volume control.</summary>
    private static bool CanSet(uint device)
    {
        foreach (var element in VolumeElements(device))
        {
            var address = Address(VolumeScalar, ScopeOutput, element);
            try { if (AudioObjectIsPropertySettable(device, ref address, out var settable) == 0 && settable != 0) return true; }
            catch { }
        }

        return false;
    }

    /// <summary>The device's current volume as a rounded percentage. When the channels differ, the
    /// louder channel is used. A balance set away from the centre does not make the speaker
    /// quieter.</summary>
    private static bool TryRead(uint device, out int volume)
    {
        volume = 0;
        if (device == 0) return false;

        float loudest = -1;
        foreach (var element in VolumeElements(device))
        {
            var address = Address(VolumeScalar, ScopeOutput, element);
            float level = 0;
            uint size = sizeof(float);

            try
            {
                if (AudioObjectGetPropertyData(device, ref address, 0, null, ref size, &level) != 0) continue;
            }
            catch { continue; }

            if (level > loudest) loudest = level;
        }

        if (loudest < 0) return false;

        volume = (int)Math.Round(Math.Clamp(loudest, 0f, 1f) * 100);
        return true;
    }

    /// <summary>Sets the device's own volume to a percentage. Returns true when at least one element
    /// accepted the value.</summary>
    private static bool TryWrite(uint device, int volume)
    {
        if (device == 0) return false;

        var level = Math.Clamp(volume, 0, 100) / 100f;
        var written = false;

        foreach (var element in VolumeElements(device))
        {
            var address = Address(VolumeScalar, ScopeOutput, element);
            try
            {
                if (AudioObjectSetPropertyData(device, ref address, 0, null, sizeof(float), &level) == 0)
                    written = true;
            }
            catch { }
        }

        return written;
    }

    // ── being told ───────────────────────────────────────────────────────────────────────────────────
    //
    // The volume can change without this app: from the keyboard volume keys, the menu bar slider, or
    // the buttons on the speaker, which send the level back over AVRCP. All three arrive here as a
    // property change. That is how the app's slider shows a level the app did not set.

    private static uint _listening;
    private static uint[] _listeningOn = [];

    /// <summary>Watches this device's volume, and stops watching the device that was watched before.
    /// Device 0 stops the watching.
    ///
    /// <para>The callback runs on a CoreAudio thread and must not be held up. As in
    /// <see cref="MacAudioDevices"/>, it only reads the new level and raises the event.</para>
    /// </summary>
    private static void Follow(uint device)
    {
        if (device == _listening) return;

        var listener = (nint)(delegate* unmanaged[Cdecl]<uint, uint, PropertyAddress*, nint, int>)&OnVolumeChanged;

        foreach (var element in _listeningOn)
        {
            var address = Address(VolumeScalar, ScopeOutput, element);
            try { AudioObjectRemovePropertyListener(_listening, ref address, listener, 0); } catch { }
        }

        _listening = device;
        _listeningOn = [];
        if (device == 0) return;

        var attached = new List<uint>(2);
        foreach (var element in VolumeElements(device))
        {
            var address = Address(VolumeScalar, ScopeOutput, element);
            try { if (AudioObjectAddPropertyListener(device, ref address, listener, 0) == 0) attached.Add(element); }
            catch { }
        }

        _listeningOn = [.. attached];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnVolumeChanged(uint objectId, uint count, PropertyAddress* addresses, nint userData)
    {
        // The notification says which property changed, but not the new value. So the level is read
        // back. It is read from the device that raised the notification, not from the device currently
        // held. Otherwise a device swapped in at the same moment would report its level as the old
        // device's.
        if (objectId == _listening && TryRead(objectId, out var level)) Changed?.Invoke(level);
        return 0;   // noErr
    }

    private static PropertyAddress Address(uint selector, uint scope, uint element = ElementMain)
        => new() { Selector = selector, Scope = scope, Element = element };
}
