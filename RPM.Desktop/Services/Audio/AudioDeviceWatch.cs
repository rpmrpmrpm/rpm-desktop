using System.Diagnostics;
using RPM.Core.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenAL;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// One place that knows when this machine's sound devices change. Other code subscribes to it.
///
/// <para>The change news can come from three sources. They are listed here in order of preference:</para>
/// <list type="number">
/// <item>OpenAL Soft's <c>ALC_SOFT_system_events</c> extension. It reports every event we need, from
/// any backend. It exists from OpenAL Soft 1.24.0. The natives in <c>Silk.NET.OpenAL.Soft.Native</c>
/// are 1.23.1, so a stock build does not use it. A machine with a newer library (for example a Linux
/// distribution's own build) uses it with no other change.</item>
/// <item>The platform's own notification: <c>IMMNotificationClient</c> on Windows and
/// <c>AudioObjectAddPropertyListener</c> on macOS. Both report a device arriving or leaving, and the
/// machine changing its default device. Both fire as soon as the OS knows.</item>
/// <item>An inotify watch on <c>/dev/snd</c> on Linux. It reports only devices being plugged in or
/// removed.</item>
/// </list>
///
/// <para><see cref="Complete"/> is the property a subscriber needs to check. When it is true, every
/// change is announced, and a timer is only a backup. When it is false, the subscriber must poll for
/// changes itself.</para>
///
/// <para>There can be several subscribers at once, such as the player's output and the monitor in a
/// dialog that is open. So this is a list of listeners, not a single one. A single listener would stop
/// receiving changes after the first subscriber was removed.</para>
/// </summary>
internal static unsafe class AudioDeviceWatch
{
    // The system-events extension's own tokens, which Silk.NET does not carry.
    private const int PlaybackDeviceEvent = 0x19D4;
    private const int DefaultDeviceChangedEvent = 0x19D6;
    private const int DeviceAddedEvent = 0x19D7;
    private const int DeviceRemovedEvent = 0x19D8;

    private static readonly object Gate = new();
    private static readonly List<Action> Listeners = [];

    private static bool _started;

    /// <summary>True when the machine announces every change that matters: a playback device arriving
    /// or leaving, and the machine changing its default output. False when the announcements are
    /// partial or missing. A subscriber then checks for changes on its own timer.</summary>
    internal static bool Complete { get; private set; }

    /// <summary>How the news arrived, for the log. Diagnostic only.</summary>
    internal static string Source { get; private set; } = "polling";

    /// <summary>Starts listening if it is not already running, and adds <paramref name="listener"/> to
    /// the list. The listener runs on an OS notification thread. It must only set a flag or record a
    /// note, because blocking that thread can block the audio driver.</summary>
    internal static void Add(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (Gate)
        {
            Listeners.Add(listener);
            Start();
        }
    }

    internal static void Remove(Action listener)
    {
        lock (Gate) Listeners.Remove(listener);
    }

    private static void Raise()
    {
        Action[] listeners;
        lock (Gate) listeners = [.. Listeners];
        foreach (var listener in listeners)
        {
            try { listener(); } catch { }
        }
    }

    /// <summary>Call under <see cref="Gate"/>. Wires whichever source this machine has, once.</summary>
    private static void Start()
    {
        if (_started) return;
        _started = true;

        if (WireOpenAl())
        {
            Complete = true;
            Source = "ALC_SOFT_system_events";
        }
        else if (OperatingSystem.IsWindows() && WinAudioDevices.Watch(Raise))
        {
            Complete = true;
            Source = "IMMNotificationClient";
        }
        else if (OperatingSystem.IsMacOS() && MacAudioDevices.Watch(Raise))
        {
            Complete = true;
            Source = "CoreAudio";
        }
        else if (OperatingSystem.IsLinux() && LinuxAudioDevices.Watch(Raise))
        {
            // Hotplug only: nothing here says the desktop moved its default sink.
            Complete = false;
            Source = "/dev/snd";
        }

        Said.Line("audio", $"device changes come from {Source}" +
                        (Complete ? "" : " — the watcher keeps its own clock"));
    }

    /// <summary>Connects to OpenAL Soft's <c>ALC_SOFT_system_events</c>, if the library provides it.
    ///
    /// <para>The events belong to the machine, not to one device, so the request uses the null device.
    /// The functions are looked up by address, not by extension name. They only exist in a library that
    /// has the extension, so a non-null address means the extension is present.</para>
    /// </summary>
    private static bool WireOpenAl()
    {
        try
        {
            var alc = ALContext.GetApi(soft: true);

            var control = (delegate* unmanaged[Cdecl]<int, int*, byte, void>)
                alc.GetProcAddress(null, "alcEventControlSOFT");
            var callback = (delegate* unmanaged[Cdecl]<
                delegate* unmanaged[Cdecl]<int, int, Device*, int, byte*, void*, void>, void*, void>)
                alc.GetProcAddress(null, "alcEventCallbackSOFT");

            if (control == null || callback == null) return false;

            callback(&OnSystemEvent, null);

            int* events = stackalloc int[3]
                { DefaultDeviceChangedEvent, DeviceAddedEvent, DeviceRemovedEvent };
            control(3, events, 1);

            return true;
        }
        catch { return false; }
    }

    /// <summary>The driver's notification thread. The extension forbids calling back into ALC from
    /// here, so this only passes the news on to the listeners, which only set a flag.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSystemEvent(int eventType, int deviceType, Device* device, int length,
                                      byte* message, void* user)
    {
        if (deviceType != PlaybackDeviceEvent) return;
        Raise();
    }
}
