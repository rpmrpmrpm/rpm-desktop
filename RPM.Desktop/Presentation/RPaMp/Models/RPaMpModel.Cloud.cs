namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// Puts a record on when the record may not be on this disk, and removes the gap between two of them.
///
/// <para>
/// The queue passes the engine a file path. That works while every track is a file. But a track can be offloaded
/// from this computer. And because an install adopts its owner's cloud catalogue, a whole record can be here without
/// a single byte of it ever being on this drive. Those tracks play by streaming. The listener is not told which
/// tracks are streamed.
/// </para>
///
/// <para>
/// Two things make this work like a local library. The first seconds of a streamed track are already on this disk
/// (the head clip), so the sound starts at once and the stream opens behind it. The following track is opened before
/// it is needed and handed to the engine. So a record with two sides that run together still runs together. The
/// look-ahead makes the second part free: the URL has been signed by the time the change comes.
/// </para>
/// </summary>
public sealed partial class RPaMpModel
{
    /// <summary>The number of the start that is current. A listener who skips through a record can start three tracks
    /// in one second, but only the last start is wanted. The others may still be waiting for a URL signature when
    /// they finish.</summary>
    private int _startGeneration;

    /// <summary>Cancels the start that is no longer wanted. This stops a URL signature from still being fetched after
    /// the next record has started.</summary>
    private CancellationTokenSource? _starting;

    /// <summary>True while the successor is being opened, so the tick does not ask twice.</summary>
    private bool _chainArming;

    /// <summary>
    /// How close to the end of a track the next one is opened.
    ///
    /// <para>The value must be long enough to sign a URL and let ffmpeg find the first frame over a home internet
    /// connection. It must be short enough that the next track is not held open for half a record. For a stream,
    /// that would keep a connection and a decode-ahead buffer open with nothing to do.</para>
    /// </summary>
    private const double ArmSeconds = 12;

    /// <summary>How far ahead of the playhead URLs are signed and head clips fetched. About one record's worth: enough
    /// that skipping forward through an album never waits, and not so much that a queue such as "shuffle everything"
    /// asks the store for about a thousand tracks.</summary>
    private const int WarmAhead = 8;

    /// <summary>
    /// How many tracks in a row may fail to start before the queue stops moving on by itself.
    ///
    /// <para>Skipping a track that will not play is correct. A missing file should not stop the queue. But a queue of
    /// cloud tracks with no connection would otherwise skip through about a thousand of them, as fast as the failed
    /// requests return.</para>
    /// </summary>
    private const int MostSkips = 3;

    private int _skips;

    /// <summary>
    /// Starts the record on the deck, wherever it is stored. The cursor, the deck and the track numbers are updated
    /// before this runs. So the screen already shows the right track while the sound is being set up.
    /// </summary>
    /// <summary>
    /// Whether the tracks around the playing track in the queue belong to the same record.
    ///
    /// <para>A single track queued on its own counts as one record. Nothing says it is part of a mix, so the album
    /// reading is the better choice when nothing says otherwise. One neighbour is enough. The last track of a side has
    /// only one neighbour, and it still belongs to that side.</para>
    /// </summary>
    private bool NeighboursAreTheSameRecord()
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
            return true;

        var normalization = _playback.Normalization;
        if (normalization is null)
            return true;

        var here = Playlist[_currentIndex].FilePath;

        bool alone = true;
        foreach (int at in (int[])[_currentIndex - 1, _currentIndex + 1])
        {
            if (at < 0 || at >= Playlist.Count)
                continue;

            alone = false;
            if (normalization.SameAlbum(here, Playlist[at].FilePath))
                return true;
        }

        return alone;
    }

    /// <param name="startSeconds">Where to start playback, in seconds. It is not zero only when the app has just opened
    /// on a track that was playing when the app closed. See <see cref="RestoreQueue"/>.</param>
    private async Task StartTrackAsync(PlaylistTrack track, double startSeconds = 0)
    {
        var generation = ++_startGeneration;

        // The volume levelling uses the album gain when a record is played straight through. It uses each track's own
        // gain when the queue was put together by hand. Shuffle decides it outright. Otherwise the check is whether the
        // neighbouring tracks in the queue are on the same record.
        _playback.InAlbumOrder = !_shuffle && NeighboursAreTheSameRecord();

        // Whatever was queued behind the track that was playing is not what follows this one.
        _playback.ClearChain();
        _chainArming = false;

        _starting?.Cancel();
        _starting?.Dispose();
        var cts = _starting = new CancellationTokenSource();

        bool started;
        string? failure = null;
        try
        {
            // A resumed track starts where it stopped, but never past its end. A file that was re-ripped shorter since
            // then would otherwise open at a position it no longer has.
            var from = track.DurationSeconds > 0 && startSeconds >= track.DurationSeconds - 2 ? 0 : startSeconds;
            started = await _playback.StartAsync(Request(track), from, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            started = false;
            failure = $"{ex.GetType().Name}: {ex.Message}";
            RPM.Core.Diagnostics.Faults.Note("queue", $"starting '{Path.GetFileName(track.FilePath)}'", ex);
        }

        if (generation != _startGeneration)
            return;   // superseded while it was being opened

        if (started)
        {
            _skips = 0;
            _ = WarmAheadAsync();
            return;
        }

        // Nothing can play the track: there is no file on this disk, and no cloud copy can be reached. Move on, so the
        // queue does not stop on a silent record. The number of failures in a row is limited (see MostSkips). A queue
        // of records from a computer that has left the network fails the same way on every track.
        if (++_skips > MostSkips)
        {
            _skips = 0;
            _playback.NoteSourceLost();
            return;
        }

        var at = _currentIndex;
        if (at >= 0 && at + 1 < Playlist.Count)
            PlayIndex(at + 1, $"the start of row {at + 1} failed ({failure ?? "nothing to play it from"})");
    }

    private PlaybackService.PlayRequest Request(PlaylistTrack track) =>
        new(track.FilePath, track.DurationSeconds, track.RpmId, track.Title, track.Artist, track.Album);

    /// <summary>
    /// Runs once per tick. It checks whether the playing track is close enough to its end that the next track should
    /// be opened and handed over.
    ///
    /// <para>Every reason not to hand over returns early. All of those checks are cheap reads. Shuffle and repeat are
    /// among them. With shuffle, the next track is not known ahead of time. With repeat, the next track is the same
    /// track again. A gapless changeover cannot serve that from a source that is still being played.</para>
    /// </summary>
    private void ChainTick()
    {
        if (_chainArming || IsDjMixing || _shuffle || _repeat) return;
        if (_playback.PlayingPath is null || _playback.IsPaused || _playback.HasChain) return;

        var duration = _playback.DurationSeconds;
        if (duration <= 0) return;

        var remaining = duration - _playback.PositionSeconds;
        if (remaining <= 0 || remaining > ArmSeconds) return;

        if (_currentIndex < 0 || _currentIndex + 1 >= Playlist.Count) return;

        _chainArming = true;

        // The position this was decided on. Arming far too early is the trace a seek nobody meant leaves.
        RPM.Core.Diagnostics.Said.Line("queue",
            $"opening row {_currentIndex + 2} to follow, at {_playback.PositionSeconds:0.#}s of {duration:0.#}s");
        _ = ArmAsync(Playlist[_currentIndex + 1], _startGeneration);
    }

    /// <param name="generation">The start that this successor was opened behind. Opening it takes a URL signature and a
    /// decoder, which can take a second or more, and the listener may start another record in that time.
    /// <see cref="StartTrackAsync"/> clears the chain before this one has landed. Without the generation, the stale
    /// successor would be installed behind the record just chosen. The changeover would then run off the end of that
    /// record into a track from the queue that was replaced.</param>
    private async Task ArmAsync(PlaylistTrack next, int generation)
    {
        bool armed = false;
        try
        {
            armed = await _playback.ChainAsync(Request(next));
        }
        catch
        {
            // The successor could not be opened. Nothing is broken. The track ends in the normal way, and the queue moves
            // on as usual, with the usual gap.
        }

        // Both answers are the model's own state. They are read on the thread that owns that state, not on the thread
        // where the open finished.
        _dispatcher.TryEnqueue(() =>
        {
            bool stale = generation != _startGeneration;

            // It arrived too late. Another record started while this one was being opened, and that start cleared the
            // chain before this arrived. If it were left in place, the playback pump would read past the end of the
            // record the listener just chose, into a track from the queue they replaced.
            if (stale && armed)
                _playback.ClearChain();

            // Only the current arm may say that arming is finished. A replaced arm saying so would let the tick open a
            // second successor behind the same record.
            if (!stale)
                _chainArming = false;
        });
    }

    /// <summary>
    /// The queued successor took over without the output device being touched. No track ended, so nothing else moves
    /// the cursor. This is where the queue catches up with the track that is already playing.
    /// </summary>
    private void OnTrackChained() => _dispatcher.TryEnqueue(() =>
    {
        if (_currentIndex < 0 || _currentIndex + 1 >= Playlist.Count) return;

        // Ask the engine which record is sounding. Do not count from where the cursor was. The changeover is announced
        // by the pump, and it reaches this code one dispatcher turn later. A listener can pick a record inside that turn,
        // and then the queue is different. Counting from the old cursor position would move the cursor one track past
        // the track they just clicked. The record they chose would play, while the screen named the next one, and the
        // end of it would skip a track.
        if (!string.Equals(_playback.PlayingPath, Playlist[_currentIndex + 1].FilePath, StringComparison.Ordinal))
        {
            RPM.Core.Diagnostics.Said.Line("queue",
                $"changeover not followed: the engine is on '{Path.GetFileName(_playback.PlayingPath ?? "nothing")}', " +
                $"the row after the cursor is '{Path.GetFileName(Playlist[_currentIndex + 1].FilePath)}'");
            return;
        }

        _currentIndex++;
        RPM.Core.Diagnostics.Said.Line("queue",
            $"row {_currentIndex + 1} of {Playlist.Count}, '{Path.GetFileName(Playlist[_currentIndex].FilePath)}': gapless changeover");
        SetDeck(Playlist[_currentIndex]);
        Renumber();
        RaiseNowPlaying();

        _ = WarmAheadAsync();
    });

    /// <summary>
    /// Signs the URLs and fetches the opening seconds of the cloud tracks just ahead of the playhead. Reaching one of
    /// them is then a local file open, and the stream is already addressed.
    /// </summary>
    private Task WarmAheadAsync()
    {
        if (_currentIndex < 0) return Task.CompletedTask;

        var ids = Playlist
            .Skip(_currentIndex)
            .Take(WarmAhead)
            .Where(t => t.RpmId is { Length: > 0 } && !File.Exists(t.FilePath))
            .Select(t => t.RpmId!)
            .ToList();

        return ids.Count == 0 ? Task.CompletedTask : _playback.WarmAsync(ids);
    }
}
