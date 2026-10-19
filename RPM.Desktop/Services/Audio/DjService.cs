using RPM.Core.Analysis;
using RPM.Core.Lyrics;
using RPM.Desktop.Services.Lyrics;
using RPM.Core.Mixing;
using RPM.Core.Models;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The app's side of the auto-mix. It creates a <see cref="Dj"/> for a list of tracks and passes the
/// mix to the player. It keeps the two in step: the player's readout follows the record the DJ has
/// brought in, and the set ends when the DJ runs out of records.
///
/// <para>The set does not wait for analysis. It starts with what the library already knows, which is
/// usually everything, because analyses are cached. The rest is analysed in the background. Each
/// result reaches the DJ through the crate. So a record analysed while the first one plays is
/// beat-matched when its turn comes.</para>
/// </summary>
public sealed partial class DjService : IDisposable
{
    private readonly PlaybackService _playback;
    private readonly TrackAnalysisService _analysis;
    private readonly DjModelService _models;

    /// <summary>The source of the lyric sheets. The sheets show what each record sings, which keeps a
    /// changeover from having two voices at once. See VocalMap and VoicePlacement.</summary>
    private readonly LyricsService _lyrics;

    private readonly object _gate = new();
    private Dj? _dj;
    private CancellationTokenSource? _analysing;

    /// <summary>Cancels a set whose records are still being prepared. See <see cref="StartAsync"/>. It
    /// is separate from <see cref="_analysing"/>, because it applies before a DJ exists.</summary>
    private CancellationTokenSource? _opening;

    /// <summary>Identifies the start that is current. Signing a set's URLs takes a moment. If the
    /// listener changes their choice during that time, the abandoned set must not start after the new
    /// one.</summary>
    private int _generation;

    /// <summary>The records of the set as they were packed, keyed by the path each one is known by. A
    /// late analysis is merged into them. See <see cref="OnAnalyzed"/>.</summary>
    private Dictionary<string, MixTrack> _packed = new(StringComparer.Ordinal);

    public DjService(
        PlaybackService playback, TrackAnalysisService analysis, DjModelService models,
        LyricsService lyrics)
    {
        _playback = playback;
        _analysis = analysis;
        _models = models;
        _lyrics = lyrics;
        _analysis.Analyzed += OnAnalyzed;

        // The analysis throttle reads this to decide how much of the machine analysis may use. It is
        // provided as a function, not a dependency, because the analyser is a lower layer that this
        // class depends on. The dependency arrow points one way only, so the answer is given as a
        // callback.
        _analysis.Transport = () =>
            (Playing: _playback.PlayingPath is not null && !_playback.IsPaused, Mixing: IsMixing);

        // Anything that takes the player away from the mix, such as a track played from the grid, a
        // Stop, or the player being closed, ends the set. Otherwise the DJ would keep decoding into a
        // sink that is playing something else.
        _playback.Changed += OnPlaybackChanged;
    }

    /// <summary>True while a mix is playing.</summary>
    public bool IsMixing
    {
        get { lock (_gate) return _dj is { HasFinished: false }; }
    }

    /// <summary>The record the mix is on. Null when no set is running.</summary>
    public MixTrack? NowPlaying { get { lock (_gate) return _dj?.NowPlaying; } }

    /// <summary>Raised on the UI-facing side each time the set moves to another record.</summary>
    public event Action<MixTrack>? TrackChanged;

    /// <summary>Raised when the set finishes on its own (the crate ran out).</summary>
    public event Action? SetEnded;

    /// <summary>Raised when a changeover starts and again when it ends. The value is true while a
    /// changeover is in progress, and the transport controls do nothing during that time.</summary>
    public event Action<bool>? ChangingOverChanged;

    /// <summary>
    /// Raised each time the answer to "can the set move on now?" changes. The value is true while a
    /// record is left in the crate, and false when only the last record is left.
    ///
    /// <para>The value controls the next button, which is disabled while the value is false. See
    /// <c>RPaMpModel.IsNextLive</c>. Pressing next plays the next record without mixing into it.
    /// Playing a record does not need a reading, so the button stays enabled during that.</para>
    /// </summary>
    public event Action<bool>? NextReadyChanged;

    /// <summary>Raised with the running order of the set: the records already played, then the rest in
    /// the order the DJ will take them. It is raised each time the order changes. That happens at every
    /// changeover, and when a new reading lets a record be placed.
    ///
    /// <para>The queue shows this order, so the screen shows the set that will be played, not the list
    /// it was built from.</para>
    /// </summary>
    public event Action<IReadOnlyList<string>>? RunningOrderChanged;

    /// <summary>The last order that was announced. An order that has not changed is not announced
    /// again. A reading arrives every few seconds, and most of them change nothing.</summary>
    private IReadOnlyList<string> _announcedOrder = [];

    /// <summary>Works out the running order, and raises the event if it differs from the last
    /// one.</summary>
    private void AnnounceOrder(Dj dj)
    {
        var order = dj.RunningOrder.Select(t => t.Path).ToList();

        lock (_gate)
        {
            if (!ReferenceEquals(_dj, dj) || order.SequenceEqual(_announcedOrder, StringComparer.Ordinal))
                return;

            _announcedOrder = order;
        }

        RunningOrderChanged?.Invoke(order);
    }

    /// <summary>
    /// Raised when a changeover that the workbench is testing has run and is queued again for another
    /// run. See <see cref="Dj.RehearseChangeover"/>. The set then waits at the start of the changeover.
    /// The caller pauses the engine to stop it there.
    /// </summary>
    public event Action? ChangeoverRehearsed;

    /// <summary>Whether the set repeats the same changeover, instead of moving on. This is the
    /// workbench's switch. It is off everywhere else, and it is off for any set that has not been told
    /// otherwise.</summary>
    public bool RehearseChangeover
    {
        get { lock (_gate) return _rehearse; }
        set
        {
            lock (_gate)
            {
                _rehearse = value;
                if (_dj is { } dj)
                    dj.RehearseChangeover = value;
            }
        }
    }

    /// <summary>Kept here as well as on the DJ. This way the setting works when it is turned on before
    /// a set exists, and it still applies when a new set starts after the previous one ended.</summary>
    private bool _rehearse;

    /// <summary>
    /// Rejects the record queued for the next changeover, so the set chooses another one. See
    /// <see cref="Dj.RejectCued"/>.
    /// </summary>
    /// <returns>False when no set is running, nothing is cued, or a changeover is under way.</returns>
    public bool RejectCued()
    {
        Dj? dj;
        lock (_gate) dj = _dj;
        return dj is { HasFinished: false } && dj.RejectCued();
    }

    /// <summary>Raised instead of starting when the set cannot be planned. This happens when there is
    /// no model on this machine, no model in the cloud, and no record in the set has been read. A set
    /// where every changeover is a plain blend is not a DJ set, so the listener is told instead.
    /// </summary>
    public event Action? MixUnavailable;

    /// <summary>True when the mix can move on to another record now. See
    /// <see cref="NextReadyChanged"/>. False when no set is running.</summary>
    public bool IsNextReady
    {
        get { lock (_gate) return _dj?.IsNextReady == true; }
    }

    /// <summary>
    /// Starts an auto-mixed set from <paramref name="tracks"/>, in the order the DJ decides. Any set
    /// already playing is stopped first.
    /// </summary>
    public void Start(IReadOnlyList<TrackInfo> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var playable = tracks.Where(t => !string.IsNullOrWhiteSpace(t.FilePath)).ToList();
        if (playable.Count == 0)
            return;

        _ = StartAsync(playable);
    }

    /// <summary>
    /// Packs the crate and starts the DJ.
    ///
    /// <para>The source of each record is chosen here, before the crate is packed. The choice is made
    /// by asking the player. So the DJ can mix any record the player can play, including one that is
    /// not on this disk. This must happen here, not at the deck. A cloud record's source is a signed
    /// URL, and signing it needs a network request. The deck is loaded from the audio thread, which has
    /// one block's time to fill a block and must not wait for a network.</para>
    /// </summary>
    private async Task StartAsync(IReadOnlyList<TrackInfo> tracks)
    {
        Stop();

        // A record started by hand keeps playing unless it is stopped here. Stop only ends a player
        // action when a set is running. Without this step, the record's decoder and its pump would keep
        // feeding the device while the URLs are signed and the crate is packed. The set would then take
        // the device away from it. The player is already told that the set is its queue (see
        // RPaMpService.StartDjCore). So any record still playing at this point is not shown on screen.
        if (_playback.PlayingPath is not null)
            _playback.Stop();

        CancellationTokenSource opening;
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            opening = _opening = new CancellationTokenSource();
        }

        var records = new List<MixTrack>(tracks.Count);

        // The store's name for each record, kept next to the crate. It is needed only for a record that
        // has no file on this disk. Such a record cannot be read here, so the store reads its own copy.
        var named = new List<TrackAnalysisService.SetRecord>(tracks.Count);

        try
        {
            foreach (var track in tracks)
            {
                var source = await _playback
                    .SourceForAsync(new PlaybackService.PlayRequest(
                        track.FilePath, track.DurationSeconds, track.RpmId,
                        track.Title, track.Artist, track.Album),
                        // The record is being loaded onto a deck for the first time. Every route to it
                        // is allowed, including a computer on the local network.
                        RPM.Core.Playback.TrackRoute.Anywhere,
                        opening.Token)
                    .ConfigureAwait(false);

                // A record that cannot be read is left out of the crate, so it does not fail on the
                // deck. The DJ waits several seconds on a record that will not open before it moves on
                // (see the hold clock). A set of such records would be mostly silence.
                if (source is null)
                    continue;

                records.Add(ToMixTrack(track, source));
                named.Add(new TrackAnalysisService.SetRecord(track.FilePath, track.RpmId));
            }
        }
        catch (OperationCanceledException)
        {
            return;                 // another set was chosen while this one was being prepared
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_opening, opening))
                    _opening = null;
            opening.Dispose();
        }

        lock (_gate)
            if (generation != _generation)
                return;

        if (records.Count == 0)
        {
            // No record can be read. There are no files here, and no cloud copy is available, either
            // because the user is signed out or there is nothing to sign with. The reason is logged,
            // because otherwise the player would show a set that never starts.
            Said.Line("dj", "nothing in this set can be read, here or in the cloud");
            SetEnded?.Invoke();
            return;
        }

        // Waits for the answer on where this machine's records are read. The answer is normally ready
        // long before this point, because the test runs at startup. A set started in the first seconds
        // of a session waits here, so that it is not told "no model" while the measurement is still
        // running.
        await _models.VerdictAsync().ConfigureAwait(false);

        // Can this set be mixed at all? A record that is already read can be mixed. So can a record
        // that something can read: the model files next to the app on a machine that passed the test,
        // or the store's model on a machine that did not. If neither applies, the set does not start.
        // Every changeover would be a blend, which is not what was asked for. The listener is told why.
        if (!records.Any(r => r.IsAnalyzed) && !_analysis.CanReadBeats)
        {
            Said.Line("dj", "no model here and none in the cloud, and nothing in this set has been read");
            MixUnavailable?.Invoke();
            return;
        }

        var crate = new DjCrate(records);

        var dj = new Dj(crate, AudioEngine.Rate, AudioEngine.Channels);
        lock (_gate) dj.RehearseChangeover = _rehearse;
        dj.NextReadyChanged += ready => NextReadyChanged?.Invoke(ready);
        dj.TrackChanged += track =>
        {
            // The mix has moved on to another record. The engine reads what is playing from the DJ, so
            // nothing needs to be pushed to it. The artwork and the playing-row highlight update only
            // when playback reports a change, so that change is reported here.
            _playback.NotifyNowPlayingChanged();
            TrackChanged?.Invoke(track);

            // The rest of the set is chosen again after this record starts. So the order shown in the
            // queue may no longer be the order that is played.
            AnnounceOrder(dj);
        };
        dj.SetEnded += () => SetEnded?.Invoke();
        dj.ChangingOverChanged += changing => ChangingOverChanged?.Invoke(changing);
        dj.ChangeoverRehearsed += () =>
        {
            AnnounceOrder(dj);
            ChangeoverRehearsed?.Invoke();
        };

        // Set by key rather than added, because a queue can hold the same record twice. The later entry
        // replaces the earlier one. The result needs one entry per path, so duplicates must not throw.
        var packed = new Dictionary<string, MixTrack>(StringComparer.Ordinal);
        foreach (var record in records)
            packed[record.Path] = record;

        var analysing = new CancellationTokenSource();
        lock (_gate)
        {
            if (generation != _generation)
            {
                analysing.Dispose();
                return;
            }
            _dj = dj;
            _analysing = analysing;
            _packed = packed;
        }

        // Every record in the set is passed in, with the store's name for those that have no file on
        // this disk. The analyser decides how each one is read. A file on this disk is decoded and read
        // here, either with the model files next to the app or with the store's model one window at a
        // time. A record that exists only in the cloud is read by the store, and the result is picked
        // up from the pool. In both cases the reading is saved against the row and published to the
        // pool. So each record is analysed once, and never again.
        //
        // This starts before the audio. Then even the shortest set, two records with nothing cached,
        // has its second record read before it is needed.
        _analysis.AnalyzeForSet(named, analysing.Token);

        // The lyric sheets for each record are read on the same terms. They start here, arrive while
        // the set plays, and are merged the same way as analysis results. See ReadSheetsAsync. This is
        // the one place in the app that requests a lyric sheet with nothing on screen to show it.
        _ = Task.Run(() => ReadSheetsAsync(records, analysing.Token), analysing.Token);

        // The state of the next button before any audio has played. After this the DJ reports it
        // itself. This initial state is otherwise never reported, and it is the state a set starts in
        // when it is opened from a playlist that has never been played.
        NextReadyChanged?.Invoke(dj.IsNextReady);

        // The order the set will play in, before it starts. For a library that is already analysed,
        // this is the full set in its final order. For a new playlist, it is the order the tracks
        // arrived in. The order settles as the readings arrive.
        AnnounceOrder(dj);

#if DEBUG
        // When the lab is open, the set waits for its first readings before any audio plays. Normally a
        // set starts with whatever is known, and improves as the analysis arrives. That suits
        // listening, but not study. The first changeover is the one most worth studying, and it is the
        // one most likely to have been planned from a partial reading.
        if (HoldForAnalysis)
            await HoldForReadingsAsync(records, analysing.Token).ConfigureAwait(false);

        lock (_gate)
            if (generation != _generation)
                return;
#endif

        double setLength = records.Sum(r => r.DurationSeconds);
        if (!_playback.PlaySource(dj, setLength, title: dj.NowPlaying?.Title ?? Strings.Loc.Get("Dj_SetTitle"), artist: dj.NowPlaying?.Artist))
        {
            // The sound output refused the set, and the player has reported why. The set is stopped
            // here. Otherwise the set and its analysis would keep running for a set nobody can hear.
            Stop();
        }
    }

    /// <summary>"Next track" during a set. The playing record is looped for eight bars under a closing
    /// filter. Then the next record starts. This is not a mix. See <see cref="Dj.SkipToNext"/>.
    /// </summary>
    /// <returns>False when no set is running, so the caller can fall back to its normal
    /// transport.</returns>
    public bool SkipToNext()
    {
        Dj? dj;
        lock (_gate) dj = _dj;
        if (dj is null || dj.HasFinished)
            return false;
        dj.SkipToNext();
        return true;
    }

    /// <summary>"Previous track" during a set. It moves the needle back to the start of the record that
    /// is playing. A mix cannot go back further, because the audio that was faded out is
    /// gone.</summary>
    /// <returns>False when no set is running.</returns>
    public bool RestartRecord()
    {
        Dj? dj;
        lock (_gate) dj = _dj;
        if (dj is null || dj.HasFinished)
            return false;
        dj.RestartRecord();
        return true;
    }

    /// <summary>The seek bar during a set. It moves the needle on the record that is playing. See
    /// <see cref="Dj.SeekTo"/>.</summary>
    /// <returns>False when no set is running, or when a changeover
    /// is in progress. The caller then leaves the bar where it is.</returns>
    public bool SeekTo(double seconds)
    {
        Dj? dj;
        lock (_gate) dj = _dj;
        return dj is { HasFinished: false } && dj.SeekTo(seconds);
    }

    /// <summary>True while a set is running and the playing record can be scrubbed. This is any time
    /// except during a handover to the next record.</summary>
    public bool CanSeek
    {
        get { lock (_gate) return _dj is { HasFinished: false, IsChangingOver: false }; }
    }

    /// <summary>Ends the set. The engine disposes the DJ together with the rest of its source. So this
    /// method only stops the audio and releases the set.</summary>
    public void Stop()
    {
        Dj? dj;
        CancellationTokenSource? analysing;
        CancellationTokenSource? opening;
        lock (_gate)
        {
            dj = _dj;
            analysing = _analysing;
            opening = _opening;
            _dj = null;
            _analysing = null;
            _packed = new(StringComparer.Ordinal);

            // A set that is still being prepared is no longer wanted. The generation number is what
            // stops it. The cancel only saves the remaining requests. The signing may already be
            // finished, and the DJ may be on its way to being created.
            _generation++;
        }
        // The analysis is cancelled before the early return below. The set and its analysis are
        // separate. The set can already be over, because a DJ who has run out of records clears its
        // field while the analysis behind it keeps running. Stop must still cancel that analysis.
        try { analysing?.Cancel(); } catch { }
        analysing?.Dispose();

        // Not disposed here. The start that owns it is still inside an await, and it disposes the
        // source when it finishes.
        try { opening?.Cancel(); } catch (ObjectDisposedException) { }

        if (dj is null)
            return;

        _playback.Stop();       // the engine disposes the DJ along with its source
    }

    /// <param name="source">Where the bytes come from: the file itself, or the signed URL of a record
    /// that is only in the cloud. <see cref="MixTrack.Path"/> stays the library path in both cases,
    /// because the crate and the playing-row highlight compare against it.</param>
    private MixTrack ToMixTrack(TrackInfo track, string source) => new()
    {
        Source = source,
        Path = track.FilePath,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        TaggedDuration = track.DurationSeconds,
        Analysis = _analysis.Cached(track.FilePath),
    };


    /// <summary>
    /// Reads the lyric sheets of the crate, and merges each sheet into the record it belongs to.
    ///
    /// <para><b>Why only the DJ asks.</b> Elsewhere in the app a sheet is fetched when something on
    /// screen is about to show its words. See <see cref="LyricsService"/>, which is asked and never
    /// fetches on its own. The DJ is the only caller that wants a sheet with nothing to display. It
    /// needs to know where the singing is, so that a changeover avoids two voices at once. The record
    /// it decides about is several tracks away, so the question must be asked well before that record
    /// reaches the deck.</para>
    ///
    /// <para><b>Runs behind the set.</b> A crate of a hundred tracks needs a hundred lookups. Most are
    /// answered from the user's own folder in a millisecond, but the first play of a set is not. So the
    /// set starts with what it has, and the sheets arrive while it plays. They are merged through the
    /// same path as a late analysis. A changeover planned before the sheets arrive is planned the same
    /// way it would have been without this feature.</para>
    ///
    /// <para>The lookups run one at a time. The service is free, has no key, and the answers are needed
    /// in crate order. The first changeover is minutes away, so parallel connections are not
    /// needed.</para>
    /// </summary>
    private async Task ReadSheetsAsync(IReadOnlyList<MixTrack> records, CancellationToken ct)
    {
        foreach (var record in records)
        {
            if (ct.IsCancellationRequested)
                return;

            LyricSheet sheet;
            try
            {
                sheet = await _lyrics
                    .ForAsync(record.Artist, record.Title, record.Album, record.DurationSeconds,
                              record.Path, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Dj? dj;
            MixTrack? packed;
            lock (_gate)
            {
                dj = _dj;
                _packed.TryGetValue(record.Path, out packed);
            }

            // The set was put away, or this record is no longer in it. Either way there is nobody to
            // give the answer to, and the sheet is in the cache for next time regardless.
            if (dj is null || packed is null)
                return;

            // An empty sheet is also an answer, and it is the most common one. It means nobody has
            // written this record down. VocalMap treats it as "unknown", not as "nobody sings".
            var told = packed with { Sheet = sheet };
            lock (_gate) _packed[record.Path] = told;
            dj.NoteAnalysis(told);
        }
    }

    /// <summary>
    /// A background analysis has finished. It is passed to the DJ, which uses it if the record is still
    /// in the crate or on the deck.
    ///
    /// <para>The analysis is merged into the record as it was packed, instead of creating a new record.
    /// The crate replaces its copy with the one it is given (see <see cref="DjCrate.Update"/>). An
    /// analysis does not contain the other fields of a record, such as its title and its source. For a
    /// cloud record the source is a signed URL, which is not the path the analysis is named
    /// after.</para>
    /// </summary>
    private void OnAnalyzed(TrackAnalysis analysis)
    {
        Dj? dj;
        MixTrack? packed;
        lock (_gate)
        {
            dj = _dj;
            _packed.TryGetValue(analysis.FilePath, out packed);
        }

        // Not part of this set: it is the result of another analysis run, or the set has since been
        // stopped.
        if (dj is null || packed is null)
            return;

        dj.NoteAnalysis(packed with { Analysis = analysis });

        // A record that could not be placed before can be placed now. So the rest of the set may be
        // rearranged around it.
        AnnounceOrder(dj);
    }

    /// <summary>The player moved on to something other than this mix, such as a grid preview, a stop,
    /// or a queued track. The set is over. The DJ is dropped, so nothing keeps decoding in the
    /// background.</summary>
    private void OnPlaybackChanged()
    {
        Dj? dj;
        lock (_gate) dj = _dj;
        if (dj is null)
            return;

        // The check compares object identity, not paths. During a mix, PlayingPath is the record that
        // is playing, and it changes at every changeover. Comparing paths would treat each handover as
        // the player moving on, and the set would be stopped in the middle of the mix.
        if (ReferenceEquals(_playback.PlayingSource, dj))
            return;                                  // the mix is still playing

        lock (_gate)
        {
            if (!ReferenceEquals(_dj, dj))
                return;
            _dj = null;
            _packed = new(StringComparer.Ordinal);
            try { _analysing?.Cancel(); } catch { }
            _analysing?.Dispose();
            _analysing = null;

            // A start may still be waiting inside its own awaits. The DJ exists as soon as it is built,
            // which is before the audio is passed to the engine. When the lab is open, that gap can
            // last up to ninety seconds while the first readings are awaited. Any player action in that
            // time must also cancel the start. The generation number does this. StartAsync checks it
            // after every await, so it does not hand the audio to the engine after the set was dropped.
            _generation++;
        }

        // Announce the end. The DJ cannot see every way a set can end, such as a stop or the player
        // moving on to a file. It reports only the end that it reaches itself. Without this event, the
        // transport would keep showing a changeover that never lands, and the queue would stay in set
        // mode.
        SetEnded?.Invoke();
    }

    public void Dispose()
    {
        _analysis.Analyzed -= OnAnalyzed;
        _playback.Changed -= OnPlaybackChanged;
        Stop();
    }

#if DEBUG
    // ── the mix lab ───────────────────────────────────────────────────────────
    //
    // Debug builds only. This exposes the working state of a set while it is being planned: the DJ, the
    // reading each record was packed with, and a way to put an edited reading back. This tool exists to
    // find where the planner is wrong, so this access is needed. It is not safe for a release.

    /// <summary>The DJ that runs this set. The lab reads it, and uses it to apply
    /// corrections.</summary>
    public Dj? Booth { get { lock (_gate) return _dj; } }

    /// <summary>
    /// Set while the lab is open. The set holds its first sample until the first records are read.
    ///
    /// <para>This is off for an ordinary set. A listener wants the music to start, and the readings to
    /// catch up after it. A set that waited while a playlist was read would be a poor player. The
    /// workbench needs the opposite. It needs the first changeover planned from a complete reading. A
    /// plan made from part of a reading is not useful, and that changeover is the one most likely to be
    /// studied.</para>
    /// </summary>
    public bool HoldForAnalysis { get; set; }

    /// <summary>How many records of a set must be read before it starts. These are the record that
    /// plays first and the one after it. Together they make up the whole first changeover.</summary>
    private const int HoldForRecords = 2;

    /// <summary>
    /// The progress of the wait for the first readings: how many of the first records have come back,
    /// and how many are expected. Both are zero when no wait is in progress.
    ///
    /// <para>The lab shows this. The wait happens while a DJ exists but no record is on either deck. On
    /// screen that is an empty workbench with no reason shown. On a cold library it can last about a
    /// minute, because the model reads both records before any audio plays.</para>
    /// </summary>
    public (int Read, int Total) ReadingAhead
    {
        get { lock (_gate) return (_readAhead, _readAheadOf); }
    }

    private int _readAhead, _readAheadOf;

    /// <summary>The longest time the wait may take. A record that cannot be read at all must not hold a
    /// set indefinitely. After this limit the set starts with what it has, which is what every other
    /// set does.</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Reads the first records of the set before any audio of it plays. The records are requested by
    /// name, instead of waiting for the sweep to reach them.
    ///
    /// <para>Waiting alone is not enough. The set's sweep is a <c>Parallel.ForEachAsync</c> over every
    /// record. The order in which readings arrive depends on the number of slots and the length of each
    /// file. In a set of twenty tracks, the first two answers can be tracks seven and thirteen. A wait
    /// that only counts answers would let the set start while its own first record is still unread.
    /// That is the changeover the lab was opened to study.</para>
    ///
    /// <para>So the first records are requested directly. <see cref="TrackAnalysisService.GetAsync"/>
    /// shares one job per file with the sweep. Requesting a record here does not decode it twice. It
    /// moves the record to the front of the queue and waits for that job.</para>
    /// </summary>
    private async Task HoldForReadingsAsync(IReadOnlyList<MixTrack> records, CancellationToken ct)
    {
        // The first records of the crate. With nothing playing, these are the records in the order the
        // DJ takes them: the record that fades out, and the one it hands over to.
        var head = records.Take(HoldForRecords).ToList();
        if (head.Count == 0)
            return;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HoldLimit);

        lock (_gate)
        {
            _readAhead = 0;
            _readAheadOf = head.Count;
        }

        try
        {
            // Each record is counted as it arrives, not all at once. The lab can then show which
            // records are still missing, not only that some are.
            await Task.WhenAll(head.Select(async r =>
            {
                await _analysis.GetAsync(r.Path, limit.Token).ConfigureAwait(false);
                lock (_gate) _readAhead++;
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the set was dropped, or a record was not read within the limit. Both end the same
            // way: the set starts with what is known, as every set does when nobody is studying it.
            Said.Line("dj", "the lab's wait for the first readings ended early");
        }
        catch (Exception ex)
        {
            Said.Line("dj", $"the lab's wait for the first readings failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _readAhead = 0;
                _readAheadOf = 0;
            }
        }
    }

    /// <summary>True while the set is paused. The lab only changes readings while paused. The audio
    /// thread is not feeding the DJ at that time, so a reading can be changed without racing a
    /// changeover that is already counting frames.</summary>
    public bool IsPaused => _playback.IsPaused;

    /// <summary>The record as the set packed it: its title, the source it is read from, and the reading
    /// it was put into the crate with.</summary>
    public MixTrack? Packed(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate) return _packed.GetValueOrDefault(path);
    }

    /// <summary>Puts an edited reading into the running set. The crate and both decks take it. The
    /// changeover that was being prepared is discarded, so the next one is planned from the new
    /// reading.</summary>
    /// <remarks>The reading is not saved to the library and not published to the pool. It is one
    /// person's view of how their set should sound. <see cref="TrackAnalysis.IsEdited"/> keeps it out
    /// of both. See <see cref="RPM.Core.Cloud.AnalysisFacts.From"/>.</remarks>
    /// <returns>False when no set is running, or when the record is not in it.</returns>
    /// <param name="replan">False while a drag is in progress. A drag sends many pointer moves.
    /// Replanning on each one would put the cued record back in the crate and start a decoder for
    /// every pixel. The reading is still applied to the set at once. The caller replans once, when the
    /// drag ends.</param>
    public bool ApplyEdit(TrackAnalysis edited, bool replan = true)
    {
        ArgumentNullException.ThrowIfNull(edited);

        Dj? dj;
        MixTrack? packed;
        lock (_gate)
        {
            dj = _dj;
            _packed.TryGetValue(edited.FilePath, out packed);
        }

        if (dj is null || packed is null)
            return false;

        dj.NoteAnalysis(packed with { Analysis = edited });
        if (replan)
        {
            dj.Replan();
            AnnounceOrder(dj);
        }
        return true;
    }
#endif
}
