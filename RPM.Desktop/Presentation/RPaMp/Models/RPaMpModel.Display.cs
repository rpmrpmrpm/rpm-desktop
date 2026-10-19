using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Audio;
using RPM.Core.Models;
using RPM.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// What the player's LCD shows: the ticker, the times and the format line. These values are read
/// from the playback engine and are not state of their own.
/// </summary>
public sealed partial class RPaMpModel
{
    // ── now-playing / LCD display ──

    /// <summary>Sound is being produced. A paused track is loaded but not playing.</summary>
    public bool IsPlaying => _playback.PlayingPath is not null && !_playback.IsPaused;

    /// <summary>A track is loaded, playing or paused.</summary>
    public bool IsLoaded => _playback.PlayingPath is not null;

    /// <summary>Copies the samples that are going to the sound device. The visualizer uses them.</summary>
    public bool CopyPlayFrame(float[] dest) => _playback.CopyPlayFrame(dest);

    /// <summary>The file that is playing, or null. The spectrum visualizer uses it.</summary>
    public string? CurrentPath => _playback.PlayingPath;

    /// <summary>Glyph for the combined play/pause button: pause while playing, otherwise play.</summary>
    public string PlayPauseGlyph => IsPlaying ? "" : "";

    /// <summary>The scrolling ticker line: every tag of the current track. It starts with the speaker's
    /// name when the sound plays on a device other than this computer.</summary>
    public string NowPlayingText
    {
        get
        {
            var track = _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? $"{_currentIndex + 1}. {Playlist[_currentIndex].Details}"
                : "RPaMp · rpmrpmrpm.com";

            return CastStatusText is { } cast ? $"{cast} • {track}" : track;
        }
    }

    public string StateGlyph => _playback.IsPaused ? "" : IsPlaying ? "" : ""; // pause / play / stop

    /// <summary>The state glyph's point size. The play triangle and the pause bars are taller than the
    /// stop square at the same size, so they are set a little smaller. Then all three look as tall as
    /// the timer digits beside them.</summary>
    public double StateGlyphSize => _playback.IsPaused || IsPlaying ? 15 : 18;

    public double Duration
    {
        get
        {
            if (_currentIndex >= 0 && _currentIndex < Playlist.Count)
                return Math.Max(1, Playlist[_currentIndex].DurationSeconds);
            // A track can play without being in the playlist, for example a rip preview played through
            // the shared player. Take the length from the engine so the scrubber covers the real
            // track. A length of 1 would pin the playhead at the end. The position slider would then
            // jump when the view binds, and that would cause a seek the user did not ask for.
            return _playback.PlayingPath is not null ? Math.Max(1, _playback.DurationSeconds) : 1;
        }
    }

    public double Position => _playback.PositionSeconds;

    /// <summary>Where the playhead is, from 0 to 1 across the track. The shell's slim seek bar uses it.</summary>
    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>Time left, counting down. The shell seek bar shows "-mm:ss" at its right.</summary>
    public string RemainingText
    {
        get
        {
            var rem = TimeSpan.FromSeconds(Math.Max(0, Duration - Position));
            return $"-{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        }
    }

    /// <summary>The bar can be dragged, because there is something to seek into. During a DJ set the DJ
    /// decides this, not the engine: a mix has no single file to reopen at an offset, so the DJ seeks
    /// in the record that is playing.</summary>
    public bool ScrubberEnabled =>
        IsLoaded && (IsDjMixing ? DjCanSeek?.Invoke() == true : _playback.CanSeek);

    /// <summary>
    /// Something is playing that has no end, such as internet radio. This is not the same as "cannot
    /// be seeked": the stream has no length and no position, so a progress bar would stay full from
    /// the first second.
    ///
    /// <para><b>The tuner is checked first, and its answer is final.</b> A stream can report any
    /// duration. A station that reports the length of its own buffer has a duration, so a test on
    /// the stream alone would say it is not live, and the bar would show a countdown and a playhead
    /// that cannot be dragged anywhere. Whether a station is on is not a property of the audio. The
    /// app tunes the station, so the tuner knows.</para>
    ///
    /// <para>The second test covers a live stream that did not come from the tuner, for example a
    /// playlist entry that points to a shoutcast URL. Then only the shape of the audio is known:
    /// no queue entry and a duration of zero.</para>
    /// </summary>
    public bool IsLiveStream =>
        _tuner.Current is not null
        || (_currentIndex < 0 && _playback.PlayingPath is not null && _playback.DurationSeconds <= 0);

    /// <summary>The bar is shown. This is a different question from <see cref="ScrubberEnabled"/>:
    /// during a changeover in a mix, the position is worth showing but there is no single file to
    /// seek in. The bar stays visible and follows the record that is playing, but it ignores the
    /// pointer. For radio there is nothing to draw, so the bar is hidden.</summary>
    public bool ScrubberVisible => IsLoaded && !IsLiveStream;

    public string TimeText
    {
        get
        {
            var pos = TimeSpan.FromSeconds(Position);
            var durSeconds = _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex].DurationSeconds
                : (_playback.PlayingPath is not null ? _playback.DurationSeconds : 0);
            var dur = TimeSpan.FromSeconds(Math.Max(0, durSeconds));
            return $"{(int)pos.TotalMinutes}:{pos.Seconds:00} / {(int)dur.TotalMinutes}:{dur.Seconds:00}";
        }
    }

    /// <summary>The playlist row that is playing. It is null for silence, a station, a DJ set, or a file
    /// played from outside the playlist.</summary>
    private PlaylistTrack? PlayingRow =>
        IsPlaying && _currentIndex >= 0 && _currentIndex < Playlist.Count ? Playlist[_currentIndex] : null;

    /// <summary>The LCD readout of three facts about the file that is playing: its bitrate, its sample
    /// rate in whole kHz, and its channels. They are read from the file's tags. Dashes or nothing are
    /// shown when there is no file or the tags do not say.</summary>
    public string KbpsText => PlayingRow?.BitrateKbps is > 0 and { } kbps
        ? kbps.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : "---";

    public string KhzText => PlayingRow?.SampleRate is > 0 and { } hz
        ? (hz / 1000).ToString(System.Globalization.CultureInfo.CurrentCulture)
        : "--";

    public string ChannelsText => PlayingRow?.Channels switch
    {
        1 => Strings.Loc.Get("Amp_Mono"),
        2 => Strings.Loc.Get("Amp_Stereo"),
        > 2 and var n => Main.Models.TrackFieldText.Channels(n),
        _ => "",
    };

    /// <param name="why">Which control asked for the seek. It is written to the <c>queue</c> ring channel,
    /// so an unwanted seek can be traced to its source.</param>
    public void SeekTo(double seconds, string why)
    {
        if (!IsPlaying) return;

        RPM.Core.Diagnostics.Said.Line("queue",
            $"seek to {seconds:0.#}s of {Duration:0.#}s in '{Path.GetFileName(_playback.PlayingPath ?? "nothing")}': {why}");

        // During a set the bar belongs to the DJ. When the DJ cannot take the seek (during a
        // changeover), the seek is dropped. The engine must not get it, because it would seek the
        // whole mix to a position that does not exist.
        if (IsDjMixing)
        {
            DjSeek?.Invoke(seconds);
            return;
        }

        _playback.SeekTo(seconds);
    }
}
