using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Reports CoreAudio device changes on the Mac: a speaker connected or disconnected, or the machine's
/// chosen output changed to another device.
///
/// <para>OpenAL Soft's <c>ALC_SOFT_system_events</c> reports this same information. It is available
/// from OpenAL Soft 1.24.0, but the bundled library is 1.23.1. So the notification is taken directly
/// from CoreAudio. Otherwise <see cref="OpenAlSink"/> would have to check every half second for a
/// change that happens only a few times a day. <see cref="WinAudioDevices"/> does the same on Windows.
/// <see cref="AudioDeviceWatch"/> chooses between them.</para>
///
/// <para>The listener runs on a CoreAudio thread, so it does almost nothing. It calls the callback,
/// which must only set a flag for the audio watcher to read.</para>
/// </summary>
internal static unsafe class MacAudioDevices
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // Four-character codes, as CoreAudio names its selectors.
    private const uint DefaultOutputDevice = 0x644F7574;   // 'dOut'
    private const uint Devices = 0x64657623;               // 'dev#'
    private const uint ScopeGlobal = 0x676C6F62;           // 'glob'
    private const uint SystemObject = 1;

    private static volatile Action? _onChanged;
    private static bool _watching;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectAddPropertyListener(
        uint objectId, ref PropertyAddress address, nint listener, nint userData);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectRemovePropertyListener(
        uint objectId, ref PropertyAddress address, nint listener, nint userData);

    /// <summary>Starts listening, or points an existing listener at <paramref name="onChanged"/>.
    /// Returns false on any system that is not a Mac, and when CoreAudio refuses the listener. The
    /// caller then uses its own fallback.</summary>
    internal static bool Watch(Action onChanged)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        _onChanged = onChanged;
        if (_watching) return true;

        try
        {
            var listener = (nint)(delegate* unmanaged[Cdecl]<uint, uint, PropertyAddress*, nint, int>)&OnPropertyChanged;

            var chosen = Address(DefaultOutputDevice);
            var listed = Address(Devices);

            // Both properties are listened to, for the same reason the sink checks two things. The
            // preferred output is what this code follows. A speaker being added or removed is usually
            // what changes that output.
            if (AudioObjectAddPropertyListener(SystemObject, ref chosen, listener, 0) != 0) return false;
            AudioObjectAddPropertyListener(SystemObject, ref listed, listener, 0);

            _watching = true;
            return true;
        }
        catch { return false; }
    }

    private static PropertyAddress Address(uint selector) =>
        new() { Selector = selector, Scope = ScopeGlobal, Element = 0 };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPropertyChanged(uint objectId, uint count, PropertyAddress* addresses, nint userData)
    {
        _onChanged?.Invoke();
        return 0;   // noErr
    }
}
