using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Audio;
using RPM.Core.Models;
using RPM.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The knobs: volume, the toggles beside it, and the equaliser curve that follows the audio between
/// devices.
/// </summary>
public sealed partial class RPaMpModel
{
    // ── volume + toggles ──

    public int Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (v == _volume) return;
            _volume = v;
            OnChanged();
            OnChanged(nameof(VolumeText));
            OnChanged(nameof(IsMuted));
            OnChanged(nameof(VolumeMarkGlyph));
            OnChanged(nameof(VolumeMarkName));
            _shapeDebounce.Start();
        }
    }

    public string VolumeText => $"{_volume}%";

    /// <summary>
    /// The level the speakers report, which is used for the slider. It is not sent back to them.
    ///
    /// <para>This does everything that the <see cref="Volume"/> setter does, except the last line. The slider, the
    /// readout and the speaker mark all move. The debounce that would write the number out again does not start. That
    /// difference is the reason this exists. A number that came FROM the speaker has nowhere to go. Returning it would
    /// make the app set the dial to where it already is. That is harmless in most cases, but a drag on the speaker
    /// would then fight a write from here.</para>
    /// </summary>
    private void AdoptVolume(int level)
    {
        var v = Math.Clamp(level, 0, 100);
        if (v == _volume) return;
        _volume = v;
        OnChanged(nameof(Volume));
        OnChanged(nameof(VolumeText));
        OnChanged(nameof(IsMuted));
        OnChanged(nameof(VolumeMarkGlyph));
        OnChanged(nameof(VolumeMarkName));
    }

    /// <summary>Silence is zero. Whichever knob the slider is on (our own gain, a Bluetooth speaker, or a cast room),
    /// taking it to zero is understood by all of them. A mute flag beside it would be a second opinion about the same
    /// thing.</summary>
    public bool IsMuted => _volume == 0;

    /// <summary>The level to return to when the mark is clicked a second time. A muted knob must not forget its level
    /// by coming up at a fixed default, which could be a sudden loud sound.</summary>
    private int _volumeBeforeMute;

    /// <summary>The speaker mark's face: crossed out while there is nothing coming out of it.</summary>
    public string VolumeMarkGlyph => IsMuted ? "\uf2e2" : "\uf028";   // volume-slash / volume-up

    /// <summary>The speaker mark's tooltip and accessible name: what a click on it does.</summary>
    public string VolumeMarkName => Strings.Loc.Get(IsMuted ? "Transport_Unmute" : "Transport_Mute");

    /// <summary>Clicking the speaker mark: down to nothing, and back to what it was.</summary>
    public void ToggleMute()
    {
        if (_volume > 0)
        {
            _volumeBeforeMute = _volume;
            Volume = 0;
            return;
        }

        // Nothing is remembered. The app opened muted, or the level was set to zero by hand. Half volume is the one
        // answer that is neither silence nor loud.
        Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 50;
    }

    /// <summary>Whose volume the slider is moving: our own gain, the Bluetooth speaker's, or the cast speaker's. Decided
    /// in <see cref="VolumeRouting"/>. It is shown beside the slider, so the listener can see that the number is leaving
    /// the app.</summary>
    public VolumeKnob VolumeKnob => _playback.VolumeHeldBy;

    public bool IsVolumeLocal => VolumeKnob == VolumeKnob.App;
    public bool IsVolumeOnBluetooth => VolumeKnob == VolumeKnob.Bluetooth;
    public bool IsVolumeOnCast => VolumeKnob == VolumeKnob.Cast;

    /// <summary>The slider's mark, after the speakers changed or the music moved to a room of its own.</summary>
    private void RaiseVolumeKnob()
    {
        OnChanged(nameof(VolumeKnob));
        OnChanged(nameof(IsVolumeLocal));
        OnChanged(nameof(IsVolumeOnBluetooth));
        OnChanged(nameof(IsVolumeOnCast));
    }

    /// <summary>
    /// Opens the volume that this endpoint was last left at, and tells the interface which knob the slider is now on.
    ///
    /// <para>There is nothing to restore when the speakers hold the number themselves. Casting is one such case. The
    /// endpoint is empty then, and the room's own level is adopted (see <see cref="CastSink"/>). A Bluetooth speaker is
    /// another. Its level is the machine's output volume, which is shared with everything else that plays. Resetting it
    /// when a record opens would make the sound too loud. RPM takes what it finds in both cases.</para>
    ///
    /// <para>Nothing to restore does not mean nothing to do. The level is on the speaker, and the slider has to show it.
    /// So the level is asked for (see <see cref="PlaybackService.SyncVolumeFromSpeakers"/>). Every launch needs this
    /// request. The player is built on first need, long after the endpoint was found. So the adoption that happens when
    /// the output is settled happens before this model exists to hear it. Without the request, the slider would show a
    /// number nobody chose while the speaker played at its own level.</para>
    /// </summary>
    private void RestoreVolumeFor(string device)
    {
        _dispatcher.TryEnqueue(RaiseVolumeKnob);

        if (_playback.IsHardwareVolume)
        {
            _playback.SyncVolumeFromSpeakers();     // comes back through RemoteVolume, onto the slider
            return;
        }

        if (_settings is null || string.IsNullOrEmpty(device)) return;

        var remembered = _settings.DeviceVolumes.FirstOrDefault(v => v.Device == device);
        if (remembered is null) return;

        _dispatcher.TryEnqueue(() => Volume = remembered.Volume);
    }

    /// <summary>Writes the volume down against the speakers it was set for. It is called from the same debounce that
    /// applies it, so a drag is one write, not fifty.</summary>
    private void RememberVolume()
    {
        if (_settings is null) return;

        // The speakers keep the level themselves. This is a room while casting, or the computer's own output volume for
        // a Bluetooth speaker. Writing a second, older value would only give the app a wrong value to overrule the
        // truth with later.
        if (_playback.IsHardwareVolume) return;

        var device = _playback.OutputDeviceName;
        if (string.IsNullOrEmpty(device)) return;

        var kept = _settings.DeviceVolumes.Where(v => v.Device != device).ToList();
        kept.Add(new OutputVolume(device, _volume));
        _settings.DeviceVolumes = kept;
    }

    /// <summary>Whether the queue plays in a shuffled order. It is saved with the queue itself, because the way the
    /// listener left the player playing is part of where they left it.</summary>
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (_session is { } s) s.Shuffle = value;
            OnChanged();
        }
    }

    public bool Repeat
    {
        get => _repeat;
        set
        {
            if (_repeat == value) return;
            _repeat = value;
            if (_session is { } s) s.Repeat = value;
            OnChanged();
        }
    }

    // ── equaliser ──

    /// <summary>The ten bands, bound by index in RPaMpView. The list has its own type, for the Native AOT build. See
    /// <see cref="Common.EqBandList"/>.</summary>
    public Common.EqBandList Bands { get; }

    public bool EqOn
    {
        get => _eq.On;
        set { if (_eq.On == value) return; _eq.On = value; OnChanged(); }
    }

    /// <summary>Pre-amp slider, -12..+12 dB.</summary>
    public double Preamp
    {
        get => _eq.Preamp;
        set { if (Math.Abs(value - _eq.Preamp) < 0.001) return; _eq.Preamp = value; OnChanged(); }
    }

    private void OnBandChanged(int index, double value)
    {
        if (_syncingEq) return;
        _eq.SetBand(index, value);
    }

    private void ResetEq() => _eq.Reset();

    public void ApplyPreset(IReadOnlyList<double> gains) => _eq.ApplyPreset(gains);

    /// <summary>The service changed the curve under us. This happens when another view changes the knobs, or when the
    /// audio moves to speakers that have their own settings. It is called from whichever thread made the change.</summary>
    private void OnEqChanged() => _dispatcher.TryEnqueue(SyncEq);

    private void SyncEq()
    {
        _syncingEq = true;
        for (int i = 0; i < Bands.Count && i < _eq.Bands.Count; i++)
            Bands[i].Gain = _eq.Bands[i];
        _syncingEq = false;

        OnChanged(nameof(EqOn));
        OnChanged(nameof(Preamp));
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _shapeDebounce.Stop();
        _eq.Changed -= OnEqChanged;
        _playback.Changed -= OnPlaybackChanged;
        _playback.TrackEnded -= OnTrackEnded;
        _playback.TrackChained -= OnTrackChained;
        _playback.Stopped -= OnStopped;
        _playback.CastChanged -= OnCastChanged;
        _playback.CastFailed -= OnCastFailed;
        _playback.PlaybackStalled -= OnPlaybackStalled;
        _playback.Stop();
        _playback.StopCasting();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A speaker offered in the cast menu.</summary>
[RpmBindable]
public sealed partial class CastTarget : INotifyPropertyChanged
{
    private bool _isCurrent;

    public CastTarget(CastDevice device, bool isCurrent)
    {
        Device = device;
        _isCurrent = isCurrent;
    }

    public CastDevice Device { get; }

    public string Name => Device.Name;

    /// <summary>The model line, such as "Google Nest Mini". Two speakers called "Speaker" can then be told apart. A group
    /// shows "Speaker group", because a group is what you have to pick when its members have been paired. If there is no
    /// model line, the address is used, which is at least unique.</summary>
    public string Detail => Device.IsGroup
        ? Loc.Get("Rpm_Cast_Group")
        : string.IsNullOrWhiteSpace(Device.Model) ? Device.Address.ToString() : Device.Model;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One equaliser band: a centre frequency + a -12..+12 dB gain bound to a vertical slider.</summary>
[RpmBindable]
public sealed partial class EqBand : INotifyPropertyChanged
{
    private readonly Action<double> _onChanged;
    private double _gain;

    public EqBand(int hz, Action<double> onChanged)
    {
        Hz = hz;
        _onChanged = onChanged;
        Label = hz >= 1000 ? $"{hz / 1000}K" : hz.ToString();
    }

    public int Hz { get; }
    public string Label { get; }

    /// <summary>Gain in dB, -12..+12.</summary>
    public double Gain
    {
        get => _gain;
        set
        {
            var v = Math.Clamp(value, -12, 12);
            if (Math.Abs(v - _gain) < 0.001) return;
            _gain = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gain)));
            _onChanged(v);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
