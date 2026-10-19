using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Audio;
using RPM.Core.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Platform;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// Sends the sound to a speaker on the network: finds the speakers, joins one, and returns to this computer when the
/// speaker drops.
/// </summary>
public sealed partial class RPaMpModel
{
    // ── casting ──

    /// <summary>The places the audio can go: this computer, plus every Cast device that can be seen. Rebuilt each time
    /// the user opens the menu, because speakers come and go.</summary>
    /// <remarks>Uses its own collection type, so a {Binding} can hand it to the menu under Native AOT. See
    /// <see cref="Common.CastTargetList"/>.</remarks>
    public Common.CastTargetList CastTargets { get; } = new();

    public bool IsCasting => _playback.IsCasting;

    /// <summary>True when the audio is playing here, on the sound card. The menu ticks exactly one of the two
    /// destinations, never neither.</summary>
    public bool IsPlayingLocally => !IsCasting;

    public string CastTooltip => _playback.CastDevice is { } device
        ? Strings.Loc.Format("Cast_TooltipCasting", device.Name)
        : Strings.Loc.Get("Rpm_Cast_PlayOn");

    /// <summary>Shown on the LCD ticker while casting, so it is never unclear where the sound has gone.</summary>
    public string? CastStatusText => _playback.CastDevice is { } device
        ? Strings.Loc.Format("Rpm_Cast_Playing", device.Name).ToUpper(System.Globalization.CultureInfo.CurrentCulture)
        : null;

    /// <summary>True while a scan is in flight, so the menu can say so rather than looking empty.</summary>
    public bool IsScanningForCastDevices
    {
        get => _scanningCast;
        private set
        {
            if (_scanningCast == value) return;
            _scanningCast = value;
            OnChanged();
            OnChanged(nameof(CastEmptyText));
            OnChanged(nameof(ShowCastEmptyText));
            OnChanged(nameof(ScanRecordInHeader));
            OnChanged(nameof(ScanRecordInList));
        }
    }

    /// <summary>Where the spinner turns while a scan runs. There is one place, chosen once. The menu shows a spinner
    /// turning, or it does not. A second spinner turning at another size in the same box would be a second answer to
    /// the same question.
    ///
    /// <para>This is one value, not two bindings that could disagree. "Not both" is the whole rule. Two independent
    /// bools are the shape that lets both be true. The two properties below are projections of this value, so they
    /// cannot drift apart.</para></summary>
    private enum ScanMark
    {
        /// <summary>No scan in flight: no record anywhere.</summary>
        Nowhere,

        /// <summary>Scanning, with speakers already listed. The list is where the user looks, so the spinner is small, in the
        /// band above the list.</summary>
        Header,

        /// <summary>Scanning, with nothing listed yet. The list space is empty, and the spinner fills it at the size that
        /// an empty state can hold.</summary>
        List,
    }

    private ScanMark Mark =>
        !IsScanningForCastDevices ? ScanMark.Nowhere
        : CastTargets.Count == 0 ? ScanMark.List
        : ScanMark.Header;

    /// <summary>The little record in the "Play on" band.</summary>
    public bool ScanRecordInHeader => Mark == ScanMark.Header;

    /// <summary>The big one, standing where the list would be.</summary>
    public bool ScanRecordInList => Mark == ScanMark.List;

    public string CastEmptyText => Strings.Loc.Get(IsScanningForCastDevices ? "Rpm_Cast_Searching" : "Rpm_Cast_None");

    /// <summary>The empty state stands in for the list, so it also shows while a scan runs. Otherwise the menu would be a
    /// blank box for about three seconds, and it would look broken.</summary>
    public bool ShowCastEmptyText => CastTargets.Count == 0;

    /// <summary>The last cast failure, for the menu to show. Cleared on the next successful cast.</summary>
    public string? CastError
    {
        get => _castError;
        private set { _castError = value; OnChanged(); OnChanged(nameof(HasCastError)); }
    }

    public bool HasCastError => !string.IsNullOrEmpty(_castError);

    // ── the firewall ──

    /// <summary>The last firewall reading the menu asked for and kept. Unknown until the menu has
    /// opened once, and always Unknown off Windows.</summary>
    private FirewallStanding _firewall = FirewallStanding.Unknown;

    /// <summary>The number of readings asked for so far. A reading is kept only if it is still the latest, so a reading
    /// started before an Allow cannot land after the reading that followed it.</summary>
    private int _firewallReads;

    /// <summary>How the last cast ended, until the next cast starts. Set and cleared on the UI thread, next to
    /// <see cref="CastError"/>.</summary>
    private CastTrouble? _castTrouble;

    /// <summary>True when the last cast ended with a speaker that took the stream and never fetched it. The firewall
    /// question at the next pick reads the firewall the same way the menu does.</summary>
    public bool LastCastNeverFetched => _castTrouble is CastTrouble.NeverFetched;

    /// <summary>The reading as the menu speaks about it: see <see cref="FirewallJudge.AfterNoFetch"/>.</summary>
    private FirewallStanding ShownFirewall =>
        LastCastNeverFetched ? FirewallJudge.AfterNoFetch(_firewall) : _firewall;

    /// <summary>Why a speaker cannot reach this computer, when Windows Firewall is the reason. Null otherwise. A blocked
    /// firewall also hides speakers from the scan, so this is shown whether or not any were found.</summary>
    public string? FirewallText => Strings.FirewallText.Line(ShownFirewall, "Firewall_CastBlocked");

    public bool HasFirewallText => FirewallText is not null;

    public string FirewallButtonText => Strings.FirewallText.Button(ShownFirewall);

    private void RaiseFirewall()
    {
        OnChanged(nameof(FirewallText));
        OnChanged(nameof(HasFirewallText));
        OnChanged(nameof(FirewallButtonText));
    }

    /// <summary>Reads the firewall again and shows the result. This is called on the UI thread. It runs when the menu
    /// opens, when a speaker never fetched the stream, and after an Allow.</summary>
    public async Task RefreshFirewallAsync()
    {
        int mine = ++_firewallReads;
        var standing = await WindowsFirewall.ReadAsync();
        if (mine != _firewallReads)
            return;

        _firewall = standing;
        RaiseFirewall();
    }

    /// <summary>The button under the firewall line. It asks Windows to let rpm through, or opens Windows Security when
    /// only the setting there can help. It reads the firewall again afterwards either way.</summary>
    public async Task FixFirewallAsync()
    {
        await WindowsFirewall.FixAsync(ShownFirewall);
        await RefreshFirewallAsync();
    }

    /// <summary>
    /// Sends the post-EQ audio to the speaker losslessly (FLAC in a live Matroska stream) instead of MP3 320. This is off
    /// by default. Google lists FLAC as a supported codec, but it is not among the formats that Google documents for a
    /// live stream, so it must be tried on the speaker first. Changing it during a track re-buffers, and nothing more
    /// happens.
    /// </summary>
    public bool CastLossless
    {
        get => ShownCastFormat == CastAudioFormat.Flac;
        set => ChooseCastFormat(value ? CastAudioFormat.Flac : CastAudioFormat.Mp3);
    }

    /// <summary>
    /// Sends AC-3 5.1 to the speaker for a surround source, instead of a stereo format. This is the only setting that
    /// keeps the six channels. It skips the stereo EQ and re-encodes the file straight to Dolby Digital, so that a Shield
    /// and an amplifier can pass it through. Whether the speaker plays it as 5.1 or folds it to stereo depends on the
    /// hardware. Try it and check the amplifier. Changing it during a track re-buffers, and nothing more happens.
    /// </summary>
    public bool CastSurround
    {
        get => ShownCastFormat == CastAudioFormat.Ac3Surround;
        set => ChooseCastFormat(value ? CastAudioFormat.Ac3Surround : CastAudioFormat.Mp3);
    }

    /// <summary>The format the listener chose. It is kept in settings, so it survives a restart. It is read from settings
    /// in the constructor, and written only by the format switches. The format sent to a speaker is worked out from it
    /// at the start of each cast, by <see cref="CastFormatFor"/>. A speaker that cannot play the choice gets MP3 for that
    /// cast. The choice itself is not changed.</summary>
    private CastAudioFormat _castFormatChoice;

    /// <summary>The format the switches show. While casting, this is the format being sent. Otherwise it is the
    /// listener's choice, which is where the next cast starts.</summary>
    private CastAudioFormat ShownCastFormat => _playback.IsCasting ? _playback.CastFormat : _castFormatChoice;

    /// <summary>A format switch was flipped. The choice is stored, and a running cast restarts in the new format at the
    /// current position.</summary>
    private void ChooseCastFormat(CastAudioFormat format)
    {
        if (ShownCastFormat == format) return;
        _castFormatChoice = format;
        if (_settings is not null) _settings.CastFormat = format;
        _playback.CastFormat = format;
        OnChanged(nameof(CastLossless));   // the three formats are one choice
        OnChanged(nameof(CastSurround));
    }

    /// <summary>
    /// The format a cast is sent in: the listener's choice, unless the speaker has said before that it cannot play it,
    /// or this build does not offer it. In those cases the format is MP3, which every receiver plays. A speaker that has
    /// never been asked gets the choice, because "not told" is not the same as "cannot".
    ///
    /// <para>This is decided before the stream starts, not after a failure. A device on an optical link cannot take
    /// lossless audio. A device that has said so should not be sent it, and then be left with silence. Any other value,
    /// such as a format that a later build wrote into the settings file, is also sent as MP3.</para>
    /// </summary>
    internal static CastAudioFormat CastFormatFor(CastAudioFormat chosen, CastDeviceProfile? speaker, bool surroundOffered) =>
        chosen switch
        {
            CastAudioFormat.Flac when speaker is not { Flac: false } => CastAudioFormat.Flac,
            CastAudioFormat.Ac3Surround when surroundOffered && speaker is not { Ac3: false } => CastAudioFormat.Ac3Surround,
            _ => CastAudioFormat.Mp3,
        };

    /// <summary>
    /// Turns the television's picture off while the music plays on.
    ///
    /// <para>Written the way the switch reads, "turn off the screen", rather than as the state it sets. So the checkbox
    /// and the property say the same thing, and nobody has to invert it in their head at a call site.</para>
    ///
    /// <para>Only RPM's own receiver can be told. On Google's stock one the message goes nowhere,
    /// which is why the switch says so.</para>
    /// </summary>
    public bool CastScreenOff
    {
        // The value is limited by what the other end can do, and not only greyed out in the flyout. A speaker with no
        // screen has nothing to turn off. A switch that reads "on" for a picture that does not exist would be false.
        // The stored preference is not changed, so it comes back when the music goes to a television again.
        get => CanTurnOffScreen && !_playback.CastScreenOn;
        set
        {
            if (_playback.CastScreenOn == !value) return;
            _playback.CastScreenOn = !value;
            if (_settings is not null) _settings.CastScreenOn = !value;
            OnChanged();
        }
    }

    /// <summary>
    /// True while there is a picture to turn off. False only for a device that has said, in its greeting, that it has
    /// no screen. Examples are a speaker, a stereo pair, or a Chromecast Audio.
    ///
    /// <para>There are two sources, in this order. The first is the receiver's own answer, where there is one. The
    /// device is speaking about itself. Where there is no answer, the second source is the model that the device
    /// advertises. That is a fact that holds from the moment the device turns up in a scan. This second source is not a
    /// fallback so much as the only answer available for the case that matters. <b>A custom web receiver does not launch
    /// on an audio-only Cast device.</b> So the speakers this question is asked about are exactly the ones that can
    /// never send a <c>hello</c>. If the switch were gated on the greeting alone, it would stay available on every one
    /// of them.</para>
    ///
    /// <para>Unrecognised is not the same as false. A television that has no rule for it keeps the switch.</para>
    /// </summary>
    public bool CanTurnOffScreen =>
        CastProfile()?.Display ?? _playback.CastDevice is not { HasNoScreen: true };

    /// <summary>
    /// Whether the surround switch is offered at all.
    ///
    /// <para>This is a debug-only option. The AC-3 path works and is worth keeping.
    /// But whether a given room plays it as 5.1, folds it to stereo, or refuses it can only be told by the amplifier. A
    /// switch whose result depends on a chain of hardware behind the speaker is not a setting to show everyone. So it
    /// stays where it can be tried.</para>
    /// </summary>
    public bool ShowCastSurround
    {
#if DEBUG
        get => true;
#else
        get => false;
#endif
    }

    /// <summary>
    /// True when the speaker being cast to can take these formats. Both are true when nothing is known. This covers a
    /// speaker that has not been asked, and Google's stock receiver, which cannot be asked at all. "We have not been
    /// told" is not the same as "it cannot", so the app tries.
    /// </summary>
    public bool CanCastSurround => CastProfile()?.Ac3 ?? true;

    public bool CanCastLossless => CastProfile()?.Flac ?? true;

    private CastDeviceProfile? CastProfile() => _playback.CastDevice is { } device ? ProfileOf(device) : null;

    private CastDeviceProfile? ProfileOf(CastDevice device) =>
        _settings?.CastDevices.FirstOrDefault(p => p.Id == device.Id);

    /// <summary>Records what a speaker has just said about itself. It also moves the format back to one that the speaker
    /// can play, if the setting asks for more than the speaker has.</summary>
    private void RememberCastDevice(CastDevice device, CastCapabilities capabilities)
    {
        if (_settings is not null)
        {
            var kept = _settings.CastDevices.Where(p => p.Id != device.Id).ToList();
            kept.Add(CastDeviceProfile.From(device, capabilities));
            _settings.CastDevices = kept;
        }

        // The sink falls back by itself when it starts a track. So this is about the buttons: they should stop offering
        // what this speaker has just said it cannot do.
        OnChanged(nameof(CanCastSurround));
        OnChanged(nameof(CanCastLossless));
        OnChanged(nameof(CastSurround));
        OnChanged(nameof(CastLossless));
        OnChanged(nameof(CanTurnOffScreen));
        OnChanged(nameof(CastScreenOff));
    }

    /// <summary>Rescans the network. This is cheap and quick, and takes a few seconds of mDNS.</summary>
    public async Task RefreshCastTargetsAsync()
    {
        if (IsScanningForCastDevices) return;
        IsScanningForCastDevices = true;
        try
        {
            var devices = await _playback.DiscoverCastDevicesAsync();
            var current = _playback.CastDevice;

            CastTargets.Clear();
            foreach (var device in devices)
                CastTargets.Add(new CastTarget(device, device.Id == current?.Id));

            // The speaker we are playing through always belongs in the list, whether or not it happened to answer this
            // particular sweep. mDNS is chatty and lossy, and a device dropping out of one scan is routine. Rebuilding the
            // list from the scan alone would quietly un-tick the thing the music is coming out of, which
            // reads as "not casting".
            if (current is not null && !devices.Any(d => d.Id == current.Id))
                CastTargets.Insert(0, new CastTarget(current, isCurrent: true));

            OnChanged(nameof(CastTargets));
            OnChanged(nameof(ShowCastEmptyText));
            OnChanged(nameof(ScanRecordInHeader));
            OnChanged(nameof(ScanRecordInList));
        }
        finally
        {
            IsScanningForCastDevices = false;
        }
    }

    /// <summary>Sends the audio to a speaker, or back to this computer when <paramref name="target"/> is null. Playback
    /// carries on from where it is. The sink swap restarts the decoder at the current position, so the queue and the EQ
    /// do not notice.</summary>
    public async Task CastToAsync(CastTarget? target)
    {
        CastError = null;
        _castTrouble = null;
        RaiseFirewall();

        if (target is null)
        {
            _playback.StopCasting();
            return;
        }

        // The format is worked out from the choice for every cast. So MP3 sent to one speaker that cannot play the choice
        // does not carry over to the next speaker.
        await _playback.CastToAsync(target.Device,
            () => CastFormatFor(_castFormatChoice, ProfileOf(target.Device), ShowCastSurround));   // CastFailed says why if it did not take
    }

    private void OnCastChanged() => _dispatcher.TryEnqueue(() =>
    {
        var current = _playback.CastDevice;
        foreach (var t in CastTargets)
            t.IsCurrent = t.Device.Id == current?.Id;

        OnChanged(nameof(IsCasting));
        OnChanged(nameof(IsPlayingLocally));
        OnChanged(nameof(CastTooltip));
        OnChanged(nameof(CastStatusText));
        OnChanged(nameof(ScrubberEnabled));   // a live stream has nothing to seek back into
        OnChanged(nameof(ScrubberVisible));
        OnChanged(nameof(IsLiveStream));
        OnChanged(nameof(NowPlayingText));

        // A different speaker means a different set of answers: what it can decode, and whether it has a picture at all.
        // These are asked again here, because the profile is looked up by device id.
        OnChanged(nameof(CanCastSurround));
        OnChanged(nameof(CanCastLossless));
        OnChanged(nameof(CanTurnOffScreen));
        OnChanged(nameof(CastScreenOff));

        // The switches show the format being sent while casting, and the stored choice otherwise. The format can change
        // without a switch being flipped: a speaker that refuses surround drops the cast to MP3.
        OnChanged(nameof(CastLossless));
        OnChanged(nameof(CastSurround));

        // The volume went with it: the slider is the room's knob now, and says so beside itself.
        RaiseVolumeKnob();

        // The television is also told about the favourite state. It knows nothing about that state until it is told.
        SyncCastFavorite();
    });

    // ── the heart, at the television ──

    private static Services.Library.LibraryService Library =>
        App.Services.GetRequiredService<Services.Library.LibraryService>();

    /// <summary>
    /// Tells the television whether the record that is playing is one the listener has favourited.
    ///
    /// <para>The value is read from the library at the moment it is asked, not remembered from when it last changed.
    /// The mark can be set from the album view, from a track line, from the full-screen player, or by the record simply
    /// moving on. A value read when the consumer needs it is correct, whoever set it.</para>
    ///
    /// <para><b>Null is not the same as false.</b> A file played from outside the collection has no row for the mark to
    /// be stored on. The television then removes the favourite button from the screen, rather than showing a button that
    /// cannot be pressed.</para>
    ///
    /// <para>Nothing is read at all while the music plays here. A library read for a screen that does not exist is a
    /// cost with no reader.</para>
    /// </summary>
    private void SyncCastFavorite()
    {
        if (!_playback.IsCasting)
            return;

        var path = _playback.PlayingPath;
        var library = Library;

        _playback.CastFavorite = path is not null && library.HoldsTrack(path)
            ? library.IsTrackFavorite(path)
            : null;
    }

    /// <summary>
    /// The favourite button was pressed on the television.
    ///
    /// <para>The value is written through <see cref="Services.Library.LibraryService.SetTrackFavorite"/>. That is the
    /// same route that the app's own favourite button uses. So a song marked from the sofa is marked on its own line in
    /// the library, by the time anybody goes back to it.</para>
    ///
    /// <para>A write that did not happen is not reported as one that did. If the library returns false, there was
    /// nowhere to store the mark, and the screen keeps the mark it had.</para>
    /// </summary>
    private void ToggleCastFavorite() => _dispatcher.TryEnqueue(() =>
    {
        if (_playback.PlayingPath is not { } path) return;

        var library = Library;
        if (!library.HoldsTrack(path))
        {
            _playback.CastFavorite = null;
            return;
        }

        if (!library.SetTrackFavorite(path, !library.IsTrackFavorite(path)))
            return;

        SyncCastFavorite();
    });

    private void OnCastFailed(CastTrouble trouble) => _dispatcher.TryEnqueue(() =>
    {
        CastError = Strings.Loc.Format(trouble.StringKey, trouble.Device);
        _castTrouble = trouble;

        // From this computer, a speaker that took the stream and never fetched it looks the same as a blocked firewall.
        // The firewall line goes under the error, so the menu says why.
        if (trouble is CastTrouble.NeverFetched)
            _ = RefreshFirewallAsync();
    });

    /// <summary>The speakers went away and nothing replaced them, so playback was paused rather than held open
    /// indefinitely. It uses the cast error's line, because it is the same kind of news: the music stopped for a reason
    /// outside the app, and the reason is worth saying.</summary>
    private void OnPlaybackStalled()
        => _dispatcher.TryEnqueue(() => CastError = Strings.Loc.Get("Player_NoSpeakers"));

    private string? _lastTimeText;
    private string? _lastRemainingText;
    private double _lastProgress = -1;
    private int _lastSecond = -1;

    /// <summary>Only announces what has actually changed. The tick runs 2.5 times a second, but the two clock strings
    /// have one-second resolution. So most of those notifications would re-run their bindings, measure the text again,
    /// and start a full layout pass, which reaches the track grid, for no visible difference. Skipping the no-ops keeps
    /// the visualiser at a steady frame rate while playing.</summary>
    private void RaisePosition()
    {
        // This runs on the same tick as the clock. The next track is opened a few seconds before it is needed. This is
        // the only part of the player that runs while a record plays.
        ChainTick();

        // Position drives the amp's slider. That slider snaps to whole seconds (StepFrequency="1"). Announcing the position
        // 2.5 times a second would re-lay-out the slider for values it cannot show. One update a second is all it can use.
        // The shell's seek bar uses Progress below, which stays smooth.
        int second = (int)Position;
        if (second != _lastSecond)
        {
            _lastSecond = second;
            OnChanged(nameof(Position));
        }

        double progress = Progress;
        if (Math.Abs(progress - _lastProgress) > 0.0005)
        {
            _lastProgress = progress;
            OnChanged(nameof(Progress));
        }

        var time = TimeText;
        if (time != _lastTimeText)
        {
            _lastTimeText = time;
            OnChanged(nameof(TimeText));
        }

        var remaining = RemainingText;
        if (remaining != _lastRemainingText)
        {
            _lastRemainingText = remaining;
            OnChanged(nameof(RemainingText));
        }
    }

    private void RaiseNowPlaying()
    {
        // A new track reads everything below again. So the change check above must not keep the previous values.
        _lastTimeText = null;
        _lastRemainingText = null;
        _lastProgress = -1;
        _lastSecond = -1;

        OnChanged(nameof(IsPlaying));
        OnChanged(nameof(IsTransportBusy));
        OnChanged(nameof(NowPlayingText));
        OnChanged(nameof(StateGlyph));
        OnChanged(nameof(StateGlyphSize));
        OnChanged(nameof(PlayPauseGlyph));
        OnChanged(nameof(Duration));
        OnChanged(nameof(Position));
        OnChanged(nameof(ScrubberEnabled));
        OnChanged(nameof(ScrubberVisible));
        OnChanged(nameof(IsLiveStream));
        OnChanged(nameof(TimeText));
        OnChanged(nameof(Progress));
        OnChanged(nameof(RemainingText));
        OnChanged(nameof(KbpsText));
        OnChanged(nameof(KhzText));
        OnChanged(nameof(ChannelsText));

        // A new record has its own verdict, so the keys must not keep showing the previous record's verdict.
        RaiseVerdict();
    }
}
