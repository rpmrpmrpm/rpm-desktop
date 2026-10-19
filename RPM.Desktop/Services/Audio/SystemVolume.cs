using System.Diagnostics;
using RPM.Core.Audio;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The machine's own volume control for the speakers the music is coming out of. The app uses it only where
/// that is the right choice, which today means a Bluetooth speaker.
///
/// <para>The reason the app wants it is in <see cref="VolumeRouting"/>. For a Bluetooth endpoint, the
/// machine's output volume is the speaker's volume. It is sent to the speaker as AVRCP absolute volume. So
/// turning it down turns the speaker down, and no bits are thrown away before the codec. The cost is that
/// the control belongs to the whole machine. So it is taken only for an endpoint that really is Bluetooth
/// and really accepts a level. It is never taken while casting, because then this computer's output device
/// is not in the path at all.</para>
///
/// <para>Both platforms support it, and both use it. <see cref="MacSystemVolume"/> uses CoreAudio's
/// <c>kAudioDevicePropertyVolumeScalar</c>. <see cref="WinSystemVolume"/> uses <c>IAudioEndpointVolume</c>.
/// They are two ways of asking the same question. Each system answers by talking to the speaker, which is
/// why there is no Bluetooth code in this file. A machine with neither keeps the app's own gain as the
/// control.</para>
///
/// <para>This class holds the parts that are not platform work: which endpoint is held, and telling the
/// app's own writes apart from a change made by someone else on the control.</para>
/// </summary>
internal static class SystemVolume
{
    /// <summary>How long after our own write a disagreeing report counts as an echo of the level we had
    /// before, rather than someone turning the speaker. Every write comes back as a notification, and the
    /// old state can arrive after the new one. A fifth of a second is much longer than that round trip, and
    /// much shorter than the gap between two deliberate presses.</summary>
    private const long EchoMs = 250;

    private static readonly object _gate = new();
    private static readonly Stopwatch _sinceWrite = Stopwatch.StartNew();

    private static bool _hardware;
    private static int _wanted = -1;
    private static bool _listening;

    /// <summary>Raised when the volume is changed by something other than this app: the volume keys, the
    /// menu bar or tray, or the buttons on the speaker. The argument is the new percentage.
    ///
    /// <para>Raised on the platform's own notification thread. Each listener must switch threads
    /// itself.</para></summary>
    internal static event Action<int>? Changed;

    /// <summary>Whether the endpoint now held is one whose own volume the app is turning. False means
    /// nothing here is in the path, and the app's own gain is the control.</summary>
    internal static bool IsHardware { get { lock (_gate) return _hardware; } }

    /// <summary>
    /// Points this at the endpoint the sound now comes out of, using the sound library's name for it.
    /// Returns whether that endpoint's own volume is ours to turn.
    ///
    /// <para>Called on every output change, including the empty name left behind by a cast. The empty name
    /// is how the hold on the previous endpoint is released when the music leaves this machine.</para>
    /// </summary>
    internal static bool Retarget(string? device)
    {
        Listen();

        // An endpoint we are not playing to is not one to hold the volume control for. The service reports
        // an empty name while casting. That name is passed straight through, and each platform half
        // releases what it held before it looks for anything new.
        var hardware =
            OperatingSystem.IsMacOS() ? MacSystemVolume.Open(device)
            : OperatingSystem.IsWindows() ? WinSystemVolume.Open(device)
            : false;

        lock (_gate)
        {
            _hardware = hardware;
            _wanted = -1;       // A new endpoint is at its own level. We have asked it for nothing.
        }

        return hardware;
    }

    /// <summary>The endpoint's own volume as a percentage, when there is one to read.</summary>
    internal static bool TryRead(out int volume)
    {
        volume = 0;
        lock (_gate) { if (!_hardware) return false; }

        return OperatingSystem.IsMacOS() ? MacSystemVolume.TryRead(out volume)
             : OperatingSystem.IsWindows() && WinSystemVolume.TryRead(out volume);
    }

    /// <summary>Sets the endpoint's own volume, when it is ours to set. False means it is not, and the
    /// caller uses its own gain instead.</summary>
    internal static bool TryWrite(int volume)
    {
        lock (_gate)
        {
            if (!_hardware) return false;
            _wanted = Math.Clamp(volume, 0, 100);
            _sinceWrite.Restart();
        }

        return OperatingSystem.IsMacOS() ? MacSystemVolume.TryWrite(volume)
             : OperatingSystem.IsWindows() && WinSystemVolume.TryWrite(volume);
    }

    /// <summary>Subscribes once to the platform half that is present on this machine.</summary>
    private static void Listen()
    {
        lock (_gate)
        {
            if (_listening) return;
            _listening = true;
        }

        if (OperatingSystem.IsMacOS()) MacSystemVolume.Changed += OnSystemChanged;
        else if (OperatingSystem.IsWindows()) WinSystemVolume.Changed += OnSystemChanged;
    }

    /// <summary>Called when the machine reports a new level. The change can come from this app or from
    /// someone else. <see cref="VolumeRouting.ShouldAdopt"/> tells them apart, and only a change from
    /// someone else is passed on.</summary>
    private static void OnSystemChanged(int level)
    {
        int wanted;
        long since;
        lock (_gate)
        {
            if (!_hardware) return;
            wanted = _wanted;
            since = (long)_sinceWrite.Elapsed.TotalMilliseconds;
        }

        if (!VolumeRouting.ShouldAdopt(level, wanted, since, EchoMs)) return;

        Changed?.Invoke(level);
    }
}
