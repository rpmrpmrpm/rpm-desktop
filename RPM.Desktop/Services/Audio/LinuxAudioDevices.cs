namespace RPM.Desktop.Services.Audio;

/// <summary>Reports when a sound card is added or removed on Linux. It watches the device nodes that
/// ALSA creates under <c>/dev/snd</c>. A USB interface, an HDMI monitor or a Bluetooth adapter creates
/// a <c>pcmC*D*p</c> node when it appears, and removes it when it is unplugged. So an inotify watch on
/// that directory gives the earliest and cheapest notice of a hotplug event.
///
/// <para>This covers only part of the picture. It does not report PulseAudio or PipeWire changing their
/// default output. That happens when the listener selects another output in the desktop's sound
/// settings, and no device is plugged in or out. OpenAL Soft reports that as
/// <c>ALC_SOFT_system_events</c>, in version 1.24.2 and later for the PulseAudio backend. The bundled
/// library is older. So <see cref="AudioDeviceWatch"/> does not treat this as a complete watch. The
/// sink keeps checking for itself between notifications.</para>
/// </summary>
internal static class LinuxAudioDevices
{
    private const string SoundNodes = "/dev/snd";

    private static FileSystemWatcher? _watcher;
    private static volatile Action? _onChanged;

    /// <summary>Starts watching, or points an existing watch at <paramref name="onChanged"/>. Returns
    /// false on any system that is not Linux, and on a machine with no <c>/dev/snd</c>. The caller then
    /// uses its own fallback.</summary>
    internal static bool Watch(Action onChanged)
    {
        if (!OperatingSystem.IsLinux()) return false;

        _onChanged = onChanged;
        if (_watcher is not null) return true;

        if (!Directory.Exists(SoundNodes)) return false;

        try
        {
            var watcher = new FileSystemWatcher(SoundNodes)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
            };

            watcher.Created += (_, _) => Raise();
            watcher.Deleted += (_, _) => Raise();
            watcher.Renamed += (_, _) => Raise();

            // An inotify watch can be lost because of a system limit, not because of anything this code
            // did. If the loss went unreported, the sink would stop checking for devices and assume all
            // is well. So the error is reported too.
            watcher.Error += (_, _) => Raise();

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return true;
        }
        catch { return false; }
    }

    private static void Raise()
    {
        try { _onChanged?.Invoke(); } catch { }
    }
}
