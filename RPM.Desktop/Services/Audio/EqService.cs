using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Audio;

/// <summary>One saved curve, belonging to one output device.</summary>
/// <param name="Device">The sound driver's name for the endpoint. It is empty when the audio goes to
/// something other than this computer's own device, such as a Cast speaker.</param>
public sealed record EqProfile(string Device, bool On, double Preamp, IReadOnlyList<double> Gains);

/// <summary>
/// The equaliser: the ten band gains and the pre-amp, and which speakers they belong to.
///
/// <para>Each curve belongs to one device, because a curve is a correction, not a preference. Laptop
/// speakers have little bass and often sound harsh. A hi-fi through a USB interface needs none of that
/// lift. A Bluetooth speaker needs something else again. One global curve would have to be changed
/// every time the output changes. So each device keeps its own curve, and the curve follows the audio
/// output. Connecting a device brings its saved curve with it.</para>
///
/// <para>This class owns the curve, not the player. Two views show the same controls: the player and
/// the Sound settings page. A device change must also change the curve, even when neither view is
/// open.</para>
/// </summary>
public sealed class EqService
{
    /// <summary>The named curves that the player and the settings page offer. Each has ten values, one
    /// per band, in <see cref="PlaybackService.EqBands"/> order. The pickers use the position, so the
    /// order is part of what they show.</summary>
    public static readonly EqPreset[] Presets =
    [
        new("Eq_Flat",      [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("Eq_Rock",      [5, 4, 2, 0, -1, 0, 2, 4, 5, 5]),
        new("Eq_Pop",       [-1, 2, 4, 5, 4, 2, 0, -1, -1, -2]),
        new("Eq_Jazz",      [4, 3, 1, 2, -1, -1, 0, 1, 3, 4]),
        new("Eq_Classical", [5, 4, 3, 2, -1, -1, 0, 2, 3, 4]),
        new("Eq_BassBoost", [7, 6, 5, 3, 1, 0, 0, 0, 0, 0]),
        new("Eq_Treble",    [0, 0, 0, 0, 0, 2, 4, 6, 7, 7]),
        new("Eq_Vocal",     [-2, -1, 1, 3, 4, 4, 3, 1, 0, -1]),
    ];

    private readonly SettingsService _settings;
    private readonly PlaybackService _playback;
    private readonly Dictionary<string, EqProfile> _profiles = new(StringComparer.Ordinal);
    private readonly double[] _bands = new double[PlaybackService.EqBands.Length];

    private readonly DispatcherQueue _dispatcher;

    /// <summary>These timers combine many slider changes. Slider changes arrive very often while a
    /// curve is being set, about a hundred a second. The timers rebuild the filter once for a group of
    /// changes, and write the settings far less often than the changes arrive.</summary>
    private readonly DispatcherQueueTimer _apply;
    private readonly DispatcherQueueTimer _save;

    private string _device = "";
    private bool _on;
    private double _preamp;

    /// <summary>A curve has been turned since it was last written down.</summary>
    private bool _dirty;

    /// <summary>False until the first device is settled on, so that starting on the same one the app
    /// stopped on still loads its curve rather than reading as "no change".</summary>
    private bool _loaded;

    /// <summary>Raised when the curve or its device has changed, so any knobs on screen may be out of
    /// date. It is raised on whichever thread made the change. The audio pump can change the device, so
    /// a listener on the UI must switch to the UI thread itself.</summary>
    public event Action? Changed;

    public EqService(SettingsService settings, PlaybackService playback, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _playback = playback;
        _dispatcher = dispatcher;

        foreach (var profile in settings.DeviceEq)
            _profiles[profile.Device] = profile;

        _apply = dispatcher.CreateTimer();
        _apply.Interval = TimeSpan.FromMilliseconds(30);
        _apply.IsRepeating = false;
        _apply.Tick += (_, _) => Apply();

        _save = dispatcher.CreateTimer();
        _save.Interval = TimeSpan.FromMilliseconds(600);
        _save.IsRepeating = false;
        _save.Tick += (_, _) => Save();

        _playback.OutputDeviceChanged += UseDevice;
        UseDevice(_playback.OutputDeviceName);

        // A curve is saved 600 ms after the last slider change. If the app closes inside that time, the
        // last change would be lost. So the pending curve is written when the process exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Flush(); } catch { } };
    }

    /// <summary>The speakers the curve on the knobs belongs to, by the driver's name for
    /// them.</summary>
    public string Device => _device;

    /// <summary>Whether the curve is applied to the audio. When off, the audio is not changed. The
    /// sliders keep their values, so the switch is a bypass for comparison, not a reset.</summary>
    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; Touched(); }
    }

    /// <summary>Pre-amp, from -12 to +12 dB. It sets the headroom for the whole curve. Without it, a
    /// boosted band clips.</summary>
    public double Preamp
    {
        get => _preamp;
        set
        {
            var v = Math.Clamp(value, -12, 12);
            if (Math.Abs(v - _preamp) < 0.001) return;
            _preamp = v;
            Touched();
        }
    }

    /// <summary>The band gains in dB, in <see cref="PlaybackService.EqBands"/> order.</summary>
    public IReadOnlyList<double> Bands => _bands;

    public void SetBand(int index, double db)
    {
        if (index < 0 || index >= _bands.Length) return;

        var v = Math.Clamp(db, -12, 12);
        if (Math.Abs(v - _bands[index]) < 0.001) return;

        _bands[index] = v;
        Touched();
    }

    /// <summary>Saves a pending curve now, without waiting for the timer. This is used when the app
    /// closes, because there is no later chance to save.</summary>
    public void Flush()
    {
        if (!_dirty) return;

        _save.Stop();
        Save();
    }

    /// <summary>Flat, and the pre-amp with it. Leaves the on/off switch where it was.</summary>
    public void Reset()
    {
        Array.Clear(_bands);
        _preamp = 0;
        Touched();
    }

    /// <summary>One of <see cref="Presets"/>, or anything else ten values long.</summary>
    public void ApplyPreset(IReadOnlyList<double> gains)
    {
        for (int i = 0; i < _bands.Length; i++)
            _bands[i] = Math.Clamp(i < gains.Count ? gains[i] : 0, -12, 12);

        Touched();
    }

    /// <summary>The audio has moved to another output device. That device's own curve is shown on the
    /// sliders and applied to the audio. A device seen for the first time starts flat and switched off,
    /// which is the same as having no equaliser.</summary>
    public void UseDevice(string device)
    {
        device ??= "";
        if (_loaded && device == _device) return;

        // A pending change belongs to the device being left. The writer uses the current device name,
        // so the change is filed under the old device now, before the name changes. Filing only updates
        // memory. The settings file is written later by the timer, on another thread.
        if (_dirty) Record();

        _loaded = true;
        _device = device;

        if (_profiles.TryGetValue(device, out var profile))
        {
            _on = profile.On;
            _preamp = profile.Preamp;
            for (int i = 0; i < _bands.Length; i++)
                _bands[i] = i < profile.Gains.Count ? profile.Gains[i] : 0;
        }
        else
        {
            _on = false;
            _preamp = 0;
            Array.Clear(_bands);
        }

        Apply();
        Changed?.Invoke();
    }

    private void Touched()
    {
        _dirty = true;

        // The curve can change on the audio pump, because a device that arrives brings its own curve.
        // The pump has a deadline. It must not wait for a timer it cannot start, and it must not write
        // settings.json. So the filters are set here, and the save is requested on the UI thread.
        if (!_dispatcher.HasThreadAccess)
        {
            Apply();
            _dispatcher.TryEnqueue(() => _save.Start());
        }
        else
        {
            _apply.Start();
            _save.Start();
        }

        _dirty = true;
        Changed?.Invoke();
    }

    private void Apply() => _playback.ApplyEqualizer(_on ? _preamp : 0, _on ? _bands : new double[_bands.Length]);

    private void Save()
    {
        Record();
        _settings.DeviceEq = [.. _profiles.Values];
    }

    /// <summary>Files the curve on the sliders under the device it belongs to. This is in memory only.
    /// It is cheap enough for any thread. Writing to disk in <see cref="Save"/> is not.</summary>
    private void Record()
    {
        _dirty = false;
        _profiles[_device] = new EqProfile(_device, _on, _preamp, [.. _bands]);
    }
}

/// <summary>
/// One named equalizer curve. This is a class, not a tuple or a positional record, and it has no
/// deconstructor. A picker therefore cannot take the key out by position and show it on screen. The
/// picker shows <see cref="Label"/>.
/// </summary>
public sealed class EqPreset(string key, double[] gains)
{
    /// <summary>The string key of the name.</summary>
    public string Key { get; } = key;

    /// <summary>Ten gains in dB, one a band, in <see cref="PlaybackService.EqBands"/> order.</summary>
    public double[] Gains { get; } = gains;

    /// <summary>The name in the user's language.</summary>
    public string Label => Strings.Loc.Get(Key);
}
