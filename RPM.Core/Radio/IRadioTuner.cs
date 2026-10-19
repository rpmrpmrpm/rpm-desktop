namespace RPM.Core.Radio;

/// <summary>
/// Tunes a station in, whatever is behind the speaker.
///
/// <para>
/// The directory, the presets and the model that drives the pane are the same on every device. They are
/// HTTP and a list. The steps underneath are the same too. The ICY socket is opened in <see
/// cref="IcyConnection"/>, and a station reaches the speakers as an <see cref="Audio.IPcmSource"/> through
/// the same engine that plays a mix, with the equaliser, the visualiser and the Cast sink attached.
/// </para>
///
/// <para>Only the decoder at the bottom differs: ffmpeg on the desktop, and AudioToolbox on the phones,
/// where ffmpeg is not available. This is the point where the two versions part. Nothing above it needs to
/// know which decoder is in use.</para>
/// </summary>
public interface IRadioTuner
{
    /// <summary>The station directory, Radio Browser, which is run by volunteers.</summary>
    RadioBrowserClient Directory { get; }

    /// <summary>The user's presets, saved as whole stations so they still play when the directory has
    /// changed.</summary>
    RadioFavorites Favorites { get; }

    /// <summary>The station playing, or null.</summary>
    RadioStation? Current { get; }

    /// <summary>True while the station is unreachable and the tuner is retrying.</summary>
    bool IsReconnecting { get; }

    /// <summary>The track the station says is playing. Null when it sends none (an HLS station never does)
    /// or has not sent one yet.</summary>
    string? NowPlayingTitle { get; }

    /// <summary>Raised when the tuned station changes, when it announces another track, and when it drops
    /// or comes back. It may be raised on a background thread.</summary>
    event Action? Changed;

    /// <summary>Tunes <paramref name="station"/> in, replacing whatever was playing. It returns at once. A
    /// station that takes five seconds to answer must not block the interface.</summary>
    void Tune(RadioStation station);

    /// <summary>Switches the radio off. Silent when nothing is tuned.</summary>
    void Stop();

    /// <summary>True when this station is the one playing.</summary>
    bool IsTuned(string uuid);
}
