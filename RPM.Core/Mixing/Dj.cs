using RPM.Core.Audio;
using RPM.Core.Diagnostics;

namespace RPM.Core.Mixing;

/// <summary>
/// The DJ. It has a crate of records and a booth with two decks, and it plays a set out of them. It puts a
/// record on, watches it towards its outro, picks what follows, cues that record on the free deck pulled
/// onto the playing tempo, and rides the fader across on a bar line. Then it does the same with the deck
/// that just came free, until the crate is empty.
///
/// <para>That is the changeover a record reaches on its own. The other one is the next key. That is not a
/// mix at all. The record playing is looped out on its own last bar under a closing filter, and the next
/// one starts after it. See <see cref="SkipToNext"/>.</para>
///
/// <para>This class implements <see cref="IPcmSource"/>. So the audio engine pumps it exactly as it pumps a
/// single decoded file. A mix goes through the same equaliser, the same visualiser tap, the same volume and
/// the same sink, including a Cast speaker. The engine does not need to know that a mix is happening. The
/// set is driven from <see cref="Read"/> on the audio thread. There is no timer anywhere, so the fade
/// cannot drift from the audio it is fading.</para>
///
/// <para>The audio thread is not the only driver. The workbench is a second one. See <see cref="_plan"/>,
/// the lock where the two meet. See also <see cref="Look"/>, the one reading that everything outside the
/// audio thread is drawn from.</para>
/// </summary>
public sealed class Dj : IPcmSource, INowPlayingSource
{
    private readonly DjBooth _booth;
    private readonly DjCrate _crate;
    private readonly int _sampleRate;
    private readonly int _channels;

    /// <summary>
    /// The noise that a skip is built on. Neither one is a record, and neither is on a deck. Both are
    /// summed into the booth's output.
    ///
    /// <para>There are two, because they overlap. The riser climbs under the roll-out. It is cut off on the
    /// sample where the new record takes the floor. The faller comes down over the couple of bars after
    /// that. The cut is thirty milliseconds long, and the fall has already started by the time the cut
    /// finishes. That is what makes the two sound like one gesture, not two effects.</para>
    /// </summary>
    private readonly NoiseSweep _riser, _faller;

    /// <summary>How early the next record is put on the free deck. Opening a decoder takes long enough that
    /// starting it when the fade begins would be heard.</summary>
    private const double CueAheadSeconds = 12;

    /// <summary>
    /// The booth's own lock. The audio thread decides here, and code outside the audio thread makes
    /// requests here.
    ///
    /// <para><b>Why it exists.</b> The class comment says that the audio thread is the only driver. That
    /// was true until the workbench was added. It is not true now. <see cref="SeekTo"/>, <see
    /// cref="Replan"/>, <see cref="Reject"/>, <see cref="TakeTheDecks"/> and <see cref="AuditionCued"/> are
    /// all called from the interface. Each of them writes fields that the audio thread uses to decide a
    /// changeover: the plan held ready, the task that opens its decoder, the ride on the pitch fader, and
    /// the record on the free deck. None of these fields was guarded, and none was volatile. So the audio
    /// thread could see <c>_pending is not null</c>, have the plan set to null and the deck unloaded under
    /// it, and then begin a fade into an empty platter.</para>
    ///
    /// <para><b>What it guards.</b> <see cref="_pending"/>, <see cref="_pendingWasSkip"/>, <see
    /// cref="_cueing"/>, <see cref="_matchingIn"/>, <see cref="_riding"/> and the ride's own counters, <see
    /// cref="_playOutNoMix"/>, <see cref="_current"/>, which deck is <see cref="_live"/>, the fade's
    /// counters, the crate, and every Load or Unload of a deck made from either side. In practice that is
    /// the whole of <see cref="Read"/>. That is why <see cref="Read"/> takes the lock once at the top, and
    /// not in pieces. A decision made half inside a lock is not a decision.</para>
    ///
    /// <para><b>Why it is safe to hold on the audio thread.</b> Nothing that takes the lock does anything
    /// slow while it is held. Opening a decoder and closing one both happen somewhere else: on the cue
    /// thread, and in <c>Deck.CloseLater</c>. So a request from the interface is a few field writes and a
    /// posted task. This is the same arrangement that <c>Deck._gate</c> already uses for every block that
    /// is mixed.</para>
    ///
    /// <para><b>Ordering.</b> Always taken outside <c>Deck._gate</c> and outside <see cref="_rejected"/>,
    /// never inside either. <c>Deck</c> never calls back into the booth. No work posted to <see
    /// cref="_cue"/> takes this lock. A task that did could be waited on from inside it.</para>
    /// </summary>
    private readonly object _plan = new();

    private Deck _live;
    private TransitionPlan? _current;      // how the LIVE record came in
    private TransitionPlan? _pending;      // the changeover being set up or run
    private Task? _cueing;

    private long _fadeFrames, _fadeLength;
    private bool _fading, _started, _finished;

    /// <summary>The volume curve that the running crossfade follows, or null for the classic equal-power
    /// curve. This also records where along the curve the room changes records. It is set in BeginFade,
    /// read on every block, and cleared with the rest of the fade's state.</summary>
    private FadeArc? _arc;
    private double _arcBalance = Crossfader.Balance;

    /// <summary>True while the changeover in flight is the next key's roll-out, not a mix. Both use the
    /// same machinery but move different things. A mix crosses two whole records over each other. A
    /// roll-out crosses the last bars of a loop into the first bars of one.</summary>
    private bool _rollingOut;

    /// <summary>How many frames of a roll-out the incoming record plays over. This is the length of its own
    /// roll-in loop, taken off the end of the roll-out. It is zero when there is nothing to overlap with,
    /// which is a record with no grid of its own.</summary>
    private long _rollOverlap;

    // Added moves
    //
    // Six changeovers are supported. Three of them need something ridden a block at a time, not only
    // planned. These are a filter that climbs through the record going out, an echo thrown on the bar line
    // with the fader shut under it, and a bar of record pulled backwards. The other three are the cut on
    // the one, the bass swap and the double drop. These are changes of level. The crossfade already walks a
    // curve for levels (see TransitionPlan.Arc), so they need nothing extra here.

    /// <summary>True while the changeover in flight is a filter fade. The record playing is high-passed out
    /// from underneath, not only faded.</summary>
    private bool _filtering;

    /// <summary>True while the changeover is an echo out. <see cref="_echoThrown"/> is true once the echo
    /// has actually been thrown. That happens on the bar line, not when the changeover was armed a bar
    /// earlier.</summary>
    private bool _echoing, _echoThrown;

    /// <summary>The echo's delay, how long the echo is kept alive, and how long the channel takes to shut
    /// over it. These are taken once in <see cref="BeginFade"/> from the plan, because the plan has gone by
    /// the time the tail is still ringing.</summary>
    private double _echoDelay, _echoFor;
    private long _echoKill;

    /// <summary>True while the record playing is being pulled backwards.</summary>
    private bool _spinningBack;

    // The workbench's two loops

    /// <summary>
    /// Records that a hand has turned down for the changeover being set up, by path. See <see
    /// cref="RejectCued"/>. This is cleared when a changeover actually happens. A rejection applies to that
    /// changeover, not to the record.
    /// </summary>
    private readonly HashSet<string> _rejected = [];

    /// <summary>
    /// True while the pair are being put back on the decks for another run at the same changeover.
    ///
    /// <para>Nothing may touch a deck while this happens. One record is taken off a deck and another is put
    /// on it, on this thread. So the set puts out silence for the two or three blocks that it takes. This
    /// is set on the audio thread at the end of the fade. It is cleared on the audio thread when the load
    /// is done, so the flag has one writer at a time.</para>
    /// </summary>
    private volatile bool _rehearsing;
    private Task? _rehearsalLoad;

    /// <summary>
    /// The opening record going onto its deck, and the record itself until it is there.
    ///
    /// <para>Every record in the set reaches a deck through <see cref="_cue"/>, for the reason written on
    /// that field. Opening a decoder starts a process and waits for its first bytes, and the audio thread
    /// has fifty milliseconds to answer. The first record of a set is the same case. Loading it on the
    /// audio thread would make the device run dry for as long as ffmpeg takes to start.</para>
    ///
    /// <para>Written on the audio thread, read on the audio thread. The cue thread only completes the task.
    /// Silence is held until it does. See the hold in <see cref="Read"/>.</para>
    /// </summary>
    private Task? _openingLoad;
    private MixTrack? _openingRecord;

    /// <summary>True once the incoming record is the louder of the two. Both are audible for the whole
    /// changeover, so "what is playing" does not change when the fade starts. For the first half of it, the
    /// room is still on the old record.</summary>
    private bool _dropped;

    /// <summary>The deck being brought up to its own tempo, and how far through that it is. The record is
    /// bent onto the outgoing record's tempo for the length of the changeover. Once the old record has
    /// gone, nothing holds it there any more.</summary>
    private Deck? _riding;
    private long _rideFrames, _rideLength;
    private double _rideFrom, _rideTo = 1;
    private Task? _rideTask;

    /// <summary>The pull that the incoming record is played at through this changeover, in pieces, and
    /// which piece is next. See <see cref="TempoStep"/>. This is empty for an ordinary changeover, where
    /// the deck is pulled once before the fade and left there.</summary>
    private IReadOnlyList<TempoStep> _steps = [];
    private int _stepNext;

    /// <summary>
    /// True from the press of the next key until the record playing has reached the tempo that the
    /// changeover is to happen at.
    ///
    /// <para>The pull is ridden in, not dropped on the record in one step. A track that changes speed by
    /// several percent inside one block sounds as if it has been interfered with. The point of matching two
    /// records is that nobody hears it being done. So the skip spends <see
    /// cref="TransitionPlanner.MatchInBars"/> bars easing the tempo across, before any note of the
    /// transition is played. The transport is locked during this, because this is part of the
    /// transition.</para>
    /// </summary>
    private bool _matchingIn;

    /// <summary>How far the tempo may sit from where the ride wants it before another step is taken. Each
    /// step reopens a decoder, so steps are kept few. Eight tenths of a percent is far below what anyone
    /// hears as a record changing speed.</summary>
    private const double RideStep = 0.008;

    /// <summary>Set by <see cref="SkipToNext"/> from whatever thread the button is pressed on. The audio
    /// thread acts on it at the top of the next block. The DJ never touches a deck from two
    /// threads.</summary>
    private volatile bool _skipRequested;

    /// <summary>True when the record now cued was picked because of a skip, and not picked ahead of the
    /// outro. Without this flag, the skip would throw away its own fresh choice on the very next block and
    /// pick again, forever.</summary>
    private bool _pendingWasSkip;

    /// <summary>How long the set has been putting out silence while it waits for a record to open, and the
    /// most that is allowed. A few seconds covers a cold disk or a slow network read. After that the record
    /// is not coming, and the set moves on without it.
    ///
    /// <para>This is wall clock time, not a count of frames. The wait is for a decoder to start, which
    /// happens in real time. Frames are the wrong unit. The audio pump runs faster than real time when it
    /// is catching up a device queue. So a frame budget would expire early exactly when the machine is
    /// busy. That is when a decoder most needs the extra time.</para></summary>
    private readonly System.Diagnostics.Stopwatch _holdClock = new();
    private const double MaxHoldSeconds = 5;



    /// <summary>Raised on the audio thread when the record the set is on changes. This happens when the
    /// first record drops, and at the end of each changeover.</summary>
    public event Action<MixTrack>? TrackChanged;

    /// <summary>Raised on the audio thread once the last record has played out.</summary>
    public event Action? SetEnded;

    /// <summary>
    /// Raised on the audio thread when a changeover starts, and again when it finishes. The value is true
    /// while one is in flight. This covers the whole of it, from the press of "next" through the wait for
    /// the record to cue and the bars of the fade itself. For all of that time the transport does nothing.
    /// Another press is ignored. A button that looks live but is not is worse than one that says so.
    /// </summary>
    public event Action<bool>? ChangingOverChanged;

    /// <summary>
    /// Raised when the answer to <see cref="IsNextReady"/> changes. It is raised on the analyser's thread
    /// when a reading lands, and on the audio thread when a record leaves the crate.
    ///
    /// <para>This lets the next key be dead while there is nothing to mix into, and come back to life the
    /// moment there is something. Nothing has to poll the crate from the interface.</para>
    /// </summary>
    public event Action<bool>? NextReadyChanged;

    /// <summary>
    /// True when the set could move on to another record right now. That means there is one left in the
    /// crate.
    ///
    /// <para>No reading is needed for this. The next key plays the next record rather than mixing into it
    /// (see <see cref="SkipToNext"/>). Playing a record needs no grid. So the key is live from the first
    /// frame of a set, including on a playlist that has never been played. False means this is the last
    /// record, and there is really nothing to go to.</para>
    /// </summary>
    public bool IsNextReady => _crate.RemainingCount > 0;

    private bool _nextReady;

    /// <summary>Announces the change when the answer has changed, and does nothing when it has not. This is
    /// called from every place where the crate is touched. That is several times a second while a set's
    /// analyses are landing.</summary>
    private void NoteNextReady()
    {
        bool ready = IsNextReady;
        if (ready == _nextReady)
            return;

        _nextReady = ready;
        NextReadyChanged?.Invoke(ready);
    }

    /// <summary>True while a changeover has been asked for or is running.</summary>
    public bool IsChangingOver => _skipRequested || _fading;

    private bool _announcedChangingOver;

    /// <summary>How a record gets put on the free deck. In the app this is done off the audio thread,
    /// because opening a decoder blocks and the audio thread has a 50 ms deadline. A test passes a runner
    /// that does it inline. That way a set plays out the same way every time, and does not depend on how
    /// busy the thread pool is.</summary>
    private readonly Func<Action, Task> _cue;

    /// <summary>The source of the random choices, a value from 0 to 1, asked once per changeover. The app
    /// passes the machine's own randomness. A test passes a fixed answer, because a quarter of the time is
    /// not something a test can check.</summary>
    private readonly Func<double> _chance;

    /// <summary>
    /// The luck that the NEXT changeover is decided with. It is drawn once, and held until it is used.
    ///
    /// <para>It is drawn ahead of time, not when the plan is made. The workbench asks what the changeover
    /// will be from the moment a set starts (see <see cref="WouldPlan"/>). If the draw happened inside the
    /// planner, each ask would get a different number. The picture would then show one changeover, and the
    /// set would play another. A number that is held can be shown to both.</para>
    ///
    /// <para>There is still one draw per changeover. So how often the rationed moves come up does not
    /// change. Only the timing changes. The draw happens at the end of the previous changeover, instead of
    /// a dozen seconds before the next one.</para>
    /// </summary>
    private double _luck;

    public Dj(
        DjCrate crate,
        int sampleRate,
        int channels,
        Func<FfmpegPcmSource.Options, IPcmSource>? openSource = null,
        Func<Action, Task>? cueRunner = null,
        Func<double>? chance = null)
    {
        ArgumentNullException.ThrowIfNull(crate);
        _crate = crate;
        _sampleRate = sampleRate;
        _channels = channels;
        _cue = cueRunner ?? (work => Task.Run(work));
        _liveSeeks = new SeekPump(_cue);
        _cuedSeeks = new SeekPump(_cue);
        _chance = chance ?? Random.Shared.NextDouble;
        _luck = _chance();

        // The next key's state before anything has happened, so that only a change is ever announced. What
        // the transport shows at the start is read directly, not waited for. See DjService, which reads
        // IsNextReady as soon as the set is built.
        _nextReady = crate.RemainingCount > 0;
        _booth = new DjBooth(sampleRate, channels, openSource);
        _riser = new NoiseSweep(sampleRate, channels);
        _faller = new NoiseSweep(sampleRate, channels);
        _live = _booth.Left;
    }

    /// <summary>The identity of the set. It stays the same for the life of the set, because the engine uses
    /// it to tell what is playing. A label that changed with each record would look like a different thing
    /// playing every few minutes. The text to show is <see cref="NowPlaying"/>.</summary>
    public string Label { get; } = $"dj-set:{Guid.NewGuid():N}";

    /// <summary>The deck the room is listening to. It is the incoming deck from the moment it lands, and
    /// the outgoing deck until then. A changeover begins several bars before the new record drops. For
    /// those bars the old record is still what is playing.</summary>
    private Deck Front => _fading && _dropped ? _booth.Other(_live) : _live;

    /// <summary>The record the set is on. During a changeover this is the record that has been dropped in,
    /// because that is the one the room is listening to by the time anyone asks.</summary>
    public MixTrack? NowPlaying => Front.Track;

    /// <summary>How far into the current record the set is, in that record's own seconds.</summary>
    public double PositionSeconds => Front.PositionSeconds;

    /// <summary>
    /// The record the room is hearing, for the player's readout, its progress bar and the artwork. The
    /// header keeps working through a mix for this reason. Everything that shows what is playing matches on
    /// a real library path. During a changeover, this is the record coming in, which is the one the mix is
    /// handing over to.
    /// </summary>
    public NowPlayingTrack? NowPlayingTrack
    {
        get
        {
            var deck = Front;
            if (deck.Track is not { } track)
                return null;
            return new NowPlayingTrack(
                track.Path, IsRecord: true, track.Title, track.Artist, track.Album,
                deck.PositionSeconds, track.DurationSeconds);
        }
    }

    /// <summary>The tempo coming out of the speakers right now, after any pull. Zero when the record has no
    /// tempo.</summary>
    public double PlayingBpm => Front.PlayingBpm;

    /// <summary>True while two records are playing together.</summary>
    public bool IsTransitioning => _fading;

    /// <summary>True once the crate is empty and the last record has played out.</summary>
    public bool HasFinished => _finished;

    /// <summary>Why the set ended, with the state that led there, once <see cref="HasFinished"/> is true.
    /// The same line goes to the log. It is kept here as well so that a test that sees a set end too early
    /// can say why.</summary>
    public string? EndedBecause { get; private set; }

    /// <summary>The set as played so far, in order.</summary>
    public IReadOnlyList<MixTrack> Played => _crate.Played;

    /// <summary>
    /// The set as it stands. This is what has been played, in the order it was played. Then comes the rest,
    /// in the order the DJ would take it out of the crate.
    ///
    /// <para>This is what the queue shows during a set. The second half is a projection, and it changes. A
    /// record that nobody has read yet cannot be placed against the one before it. So the running order
    /// settles as the readings land. It is asked again at every changeover, because the choice is made
    /// against where the playing record actually is.</para>
    /// </summary>
    public IReadOnlyList<MixTrack> RunningOrder
    {
        get
        {
            var played = _crate.Played;

            // The record on the deck. Or, before a deck has been loaded (which is the state when a set's
            // order is first shown), the last record taken out of the crate. Projecting from nothing is how
            // an unread record ends up at the front of the queue. With nothing to follow, there is nothing
            // to choose against, so the first record offered is taken.
            var from = NowPlaying ?? (played.Count > 0 ? played[^1] : null);
            double bpm = PlayingBpm > 0 ? PlayingBpm : from?.Bpm ?? 0;

            // Without the records that a hand has turned down, so the queue shows the set that will be
            // played, not the one the chooser would have picked. The exception is when nothing else is
            // left. The crate follows the same rule, for the same reason.
            var left = _crate.Remaining;
            string[] rejected;
            lock (_rejected) rejected = [.. _rejected];
            if (rejected.Length > 0)
            {
                var kept = left.Where(t => !rejected.Contains(t.Path)).ToList();
                if (kept.Count > 0)
                    left = kept;
            }

            return [.. played, .. DjSetPlanner.Following(from, bpm, left)];
        }
    }

    /// <summary>
    /// The analyser has finished with a record. Usually the record belongs to one still in the crate. If it
    /// is the one already on a deck, the deck takes it too. An analysis that lands while the opening record
    /// plays still gives that record a proper outro and grid to mix out of.
    /// </summary>
    public void NoteAnalysis(MixTrack analyzed)
    {
        ArgumentNullException.ThrowIfNull(analyzed);

        // This comes from a pool worker, one call per record read. At the same time, the audio thread may
        // be choosing what comes next from this same crate.
        lock (_plan)
            NoteAnalysisCore(analyzed);
    }

    private void NoteAnalysisCore(MixTrack analyzed)
    {
        _crate.Update(analyzed);
        _booth.Left.AdoptAnalysis(analyzed);
        _booth.Right.AdoptAnalysis(analyzed);

        // The first reading to land on a set that had none is what brings the next key back to life.
        NoteNextReady();
    }

    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_finished || frames <= 0)
            return 0;

        // Taken once, at the top. Everything below decides a changeover from state that the interface can
        // write at any moment. See _plan. This is the reason for this line.
        lock (_plan)
            return ReadCore(buffer, frames);
    }

    private int ReadCore(float[] buffer, int frames)
    {
        if (!_started)
        {
            _started = true;
            if (!DropFirstRecord())
            {
                Finish("nothing in the crate to open the set with");
                return 0;
            }
        }

        // The opening record is on its way to its deck. See DropFirstRecord. Nothing may be read until it
        // is there, so this line is held with silence. The device gets a block on time and keeps playing
        // it. Without this there would be a second of quiet at the top of a set, or the device would run
        // dry and be restarted underneath it.
        //
        // This has a limit, using the same clock as the hold further down. A decoder that never opens ends
        // the set with a reason. It does not play silence forever.
        if (_openingLoad is { } opening)
        {
            if (!opening.IsCompleted)
            {
                if (!_holdClock.IsRunning)
                    _holdClock.Restart();
                if (_holdClock.Elapsed.TotalSeconds < MaxHoldSeconds)
                {
                    Array.Clear(buffer, 0, frames * _channels);
                    return frames;
                }
            }
            else
            {
                // The record is under the needle. It is announced from here, on the audio thread, and every
                // other change of record is announced from here too.
                _openingLoad = null;
                _holdClock.Reset();
                if (_openingRecord is { } dropped)
                {
                    _openingRecord = null;
                    TrackChanged?.Invoke(dropped);
                }
            }
        }

        // Putting the pair back for another run at the same changeover. This gives silence until the record
        // is on the deck. No deck may be read while one record is being taken off and another put on. This
        // takes two or three blocks. The workbench normally pauses the engine during this anyway.
        if (_rehearsing)
        {
            if (_rehearsalLoad is null or { IsCompleted: true })
            {
                _rehearsing = false;
                _rehearsalLoad = null;
            }
            else
            {
                Array.Clear(buffer, 0, frames * _channels);
                return frames;
            }
        }

        if (_fading)
        {
            // A press during a changeover asks for the thing that is already happening. It is swallowed.
            // Otherwise it would fire again as soon as this fade lands, and skip a record that nobody asked
            // to skip.
            _skipRequested = false;
            AdvanceFade(frames);
        }
        else if (_auditioning || _handsOn)
        {
            // While a hand has the decks, the set does not move on by itself. A deck is being played from
            // wherever the hand put it. A changeover started over that would drop a record out of the
            // middle of something somebody is listening to. A next-key press is swallowed for the same
            // reason that a fade swallows one. Otherwise it would fire as soon as the hands let go, and
            // skip a record nobody asked to skip.
            _skipRequested = false;

            // What a hand may still ask for: the record coming in, on the free deck, now. The request is
            // answered here, on the audio thread. Without this, the play key on the second deck would do
            // nothing.
            CueForTheHands();
        }
        else
        {
            WatchForTheOutro();
        }

        // This goes on past the changeover that started it. A record is let back up to its own tempo over
        // eight bars. On a skip, only the first of those bars are still part of a mix.
        AdvanceRide(frames);

        if (IsChangingOver != _announcedChangingOver)
        {
            _announcedChangingOver = IsChangingOver;
            ChangingOverChanged?.Invoke(_announcedChangingOver);
        }

        int written = Mix(buffer, frames);
        if (written > 0)
        {
            _holdClock.Reset();
            return written;
        }

        // Both decks are silent. Either the fade ended exactly on the outgoing record's last block, or the
        // set is over. Mixing again after the swap covers the first case in the same block.
        if (_fading)
        {
            EndFade();
            written = Mix(buffer, frames);
            if (written > 0)
                return written;
        }

        // A hand has the decks and both are stopped, or one has been played to its end. This is a person
        // not pressing anything, not a set running out. So the booth waits. It does not reach for another
        // record or declare the set over.
        if (_handsOn)
        {
            Array.Clear(buffer, 0, frames * _channels);
            return frames;
        }

        // The record ran out before its changeover could start. Causes include a short track, a cue-out
        // point past the end of the file, or a decoder that took too long to open. Put the next record on
        // and carry on. Losing the remaining records because one of them ended early is far worse than the
        // seam this makes.
        //
        // Again when the record put on has nothing in it either: a file moved or deleted since the set was
        // built is cued and loaded like any other, and runs out on its first block. Stopping at the first
        // try would end the set there with records still in the crate. Each try takes a record out of the
        // crate, or promotes the one already taken, so this ends.
        while (RecoverFromRunOut())
        {
            written = Mix(buffer, frames);
            if (written > 0)
                return written;
            if (!_live.HasRunOut)
                break;
        }

        // A record is on its way, but its decoder has not opened yet. Hold the line with a block of silence
        // rather than ending the set on a slow disk. But only for so long. A hold that never ends is a set
        // that has silently stopped, which is worse than the seam of skipping the record.
        if (_pending is not null)
        {
            if (!_holdClock.IsRunning)
                _holdClock.Restart();
            if (_holdClock.Elapsed.TotalSeconds < MaxHoldSeconds)
            {
                Array.Clear(buffer, 0, frames * _channels);
                return frames;
            }
        }

        // The hold has run out and there is still nothing. That record is not coming. Drop it and try
        // whatever else is in the crate.
        if (_pending is not null)
        {
            _pending = null;
            _pendingWasSkip = false;
            _cueing = null;
            _matchingIn = false;
            _booth.Other(_live).Unload();
            _holdClock.Reset();
            if (RecoverFromRunOut())
            {
                written = Mix(buffer, frames);
                if (written > 0)
                    return written;
            }
        }

        // Every way of carrying on has been tried, and none of them produced a sample. This is reported out
        // loud, with the state that led here. From outside it looks the same as the engine breaking. Read
        // returns 0 from now on, the device gets nothing, and the listener hears silence. The log shows
        // thousands of underruns with no cause.
        Finish(
            $"nothing left to play: live={_live.Track?.Title ?? "none"} loaded={_live.IsLoaded} " +
            $"runOut={_live.HasRunOut} cued={_booth.Other(_live).Track?.Title ?? "none"} " +
            $"pending={_pending?.Incoming.Title ?? "none"} cueing={_cueing?.IsCompleted.ToString() ?? "none"} " +
            $"opening={_openingRecord?.Title ?? "none"} fading={_fading} handsOn={_handsOn} " +
            $"crate={_crate.Remaining.Count} left");
        return 0;
    }

    /// <summary>
    /// The booth's two decks summed, with the riser over the top.
    ///
    /// <para>The riser is not a record and is on neither deck. So it is added here, after the booth has
    /// summed and clamped. That is why the result is clamped again. It is only heard while there is
    /// something under it. A block that the decks had nothing for is the end of the set, and noise is not
    /// the thing to end a set on.</para>
    /// </summary>
    private int Mix(float[] buffer, int frames)
    {
        int written = _booth.Mix(buffer, frames);
        if (written <= 0 || (!_riser.IsRunning && !_faller.IsRunning))
            return written;

        _riser.Add(buffer, 0, written);
        _faller.Add(buffer, 0, written);

        int samples = written * _channels;
        for (int i = 0; i < samples; i++)
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);

        return written;
    }

    /// <summary>
    /// Puts the crate's next record on after the live one has run dry outside a changeover. It prefers the
    /// record already cued on the free deck, because its needle is in the right place. Otherwise it takes a
    /// fresh record and drops it straight in. Returns false when there is nothing left to play, which is
    /// the only honest end of a set.
    /// </summary>
    private bool RecoverFromRunOut()
    {
        // A record is cued and ready on the other deck. Promote it, exactly as the end of a fade does.
        var waiting = _booth.Other(_live);
        if (_pending is not null && waiting.IsLoaded && !waiting.HasRunOut)
        {
            // Straight in. There is no outgoing bar line left to land on, because the record it would have
            // been matched against has stopped. Ending the changeover here announces the change. The fade
            // that would normally do this never started.
            waiting.Start();
            EndFade();
            return true;
        }

        if (_pending is not null)
        {
            // Still opening. The caller holds the line with silence rather than ending the set.
            if (_cueing is not { IsCompleted: true })
                return false;

            // It opened, but there is nothing in it. This is a file that has been moved or deleted since
            // the playlist was built. Throw that record away and take the next one. Otherwise the set would
            // sit in silence, waiting for a deck that will never produce a sample.
            _pending = null;
            _pendingWasSkip = false;
            _cueing = null;
            _matchingIn = false;
            waiting.Unload();
        }

        var next = _crate.TakeNext(_live.Track, _live.PlayingBpm, _live.CurrentIntensity);
        NoteNextReady();                  // a record has left the crate; there may be no read one left
        if (next is null)
            return false;

        // Opening, not a changeover, for a record that is to be played out. Nothing was mixed over the new
        // record's intro, so there is no reason to have skipped past it.
        var plan = _live.Track is { } outgoing && !_playOutNoMix
            ? TransitionPlanner.Plan(outgoing, _live.PlayingBpm, next)
            : TransitionPlanner.Opening(next);

        _live.Unload();
        _live = waiting;
        _live.Load(plan.Incoming, plan.CueInSeconds, plan.TempoRatio);
        _live.Start();
        _live.Fader = 1;
        _live.Eq.SetLow(0);
        _current = plan;
        _cueing = null;
        _playOutNoMix = false;
        BeginRide(_live, plan);
        TrackChanged?.Invoke(plan.Incoming);
        return true;
    }

    /// <summary>
    /// Puts the opening record on, from the cue thread, like every other record. Returns false when the
    /// crate was empty to begin with. That is the only part of this that can be answered on the spot.
    ///
    /// <para>Taking the record out of the crate is a few field writes, so it stays here. Putting it on the
    /// deck means a decoder opening, so that goes where all the others go. See <see cref="_openingLoad"/>
    /// for why. The hold in <see cref="Read"/> is what the device hears while it happens.</para>
    /// </summary>
    private bool DropFirstRecord()
    {
        var first = _crate.TakeNext(playing: null, playingBpm: 0);
        NoteNextReady();
        if (first is null)
            return false;

        var plan = TransitionPlanner.Opening(first);
        _live = _booth.Left;
        _current = plan;
        _playOutNoMix = false;
        _openingRecord = first;

        var deck = _live;
        _openingLoad = _cue(() =>
        {
            deck.Load(first, plan.CueInSeconds, plan.TempoRatio);

            // Its first block is pulled here too, for the same reason a cued record's is. A decoder that
            // has just been started has not necessarily decoded anything yet. The audio thread waiting for
            // it is the stall this method exists to avoid.
            deck.Prime();
            deck.Fader = 1;
            deck.Eq.SetLow(0);
            deck.Start();
        });
        return true;
    }

    /// <summary>
    /// Plays the next record. This is the "next track" button in a DJ set.
    ///
    /// <para>It is not a mix. The record on the deck runs to its next bar line. That bar is looped for <see
    /// cref="TransitionPlanner.RollOutBars"/> bars while the fader comes down, and a filter takes the top
    /// and then the middle away. The new record comes in at the bar where its own instruments start. It
    /// loops its own first bar for <see cref="TransitionPlanner.RollInBars"/> bars, and rides those bars up
    /// against the last of the roll-out. So it is at full level and playing on at the sample where the old
    /// loop stops.</para>
    ///
    /// <para>The record that comes next is still chosen by the DJ. It is the one the queue is
    /// showing.</para>
    ///
    /// <para>Ignored while a changeover is already running.</para>
    /// </summary>
    public void SkipToNext() => _skipRequested = true;

    /// <summary>
    /// Puts the needle back at the start of the record playing. This is the "previous track" button, doing
    /// what it does in any player on the first press. A mix cannot be rewound past the record it is on.
    /// What has already been faded away is gone.
    /// </summary>
    public void RestartRecord()
    {
        lock (_plan)
        {
            if (_live.Track is not { } track)
                return;
            SeekToCore(_current?.CueInSeconds ?? TransitionPlanner.MixInPoint(track));
        }
    }

    /// <summary>
    /// Moves the needle on the record playing. This is the scrubber, used during a set.
    ///
    /// <para>A changeover that was cued for this record is kept when the needle lands before the point
    /// where that changeover starts. Otherwise the cued record goes back in the crate, and the choice is
    /// made again from where the needle now is.</para>
    ///
    /// <para>Scrubbed into the last <see cref="TransitionPlanner.MinRoomToMixSeconds"/> of the track, there
    /// is no room to mix out of it. So the set stops trying. The record plays out, and the next one follows
    /// it, the way a queue plays. Scrubbing back out of that stretch puts the changeover back on.</para>
    ///
    /// <para>Refused during a changeover. That is the one time when the record playing is not the only
    /// thing playing.</para>
    /// </summary>
    /// <returns>False when there is nothing to seek. That is the case with no set, or with a set in the
    /// middle of a hand-over.</returns>
    public bool SeekTo(double seconds)
    {
        lock (_plan)
            return SeekToCore(seconds);
    }

    private bool SeekToCore(double seconds)
    {
        // Refused for the whole of a changeover, not only the fade. From the press of next, the record
        // playing is already being pulled onto another record's tempo. Moving the needle under that would
        // leave the pull riding a position that no longer exists.
        if (_finished || IsChangingOver || _live.Track is not { } track)
            return false;

        // An audition ends here, properly. The seek below is about to put the cued record back in the crate
        // and take it off its deck. That deck is the one a hand is playing. If the audition is ended first,
        // it ends the normal way. If the deck is unloaded first, the booth is left believing an audition is
        // running on an empty platter, and the set never plans again.
        if (_auditioning)
            AuditionCuedCore(false);

        double end = track.DurationSeconds;
        double at = end > 0 ? Math.Clamp(seconds, 0, Math.Max(0, end - 0.05)) : Math.Max(0, seconds);

        // Whether the changeover that is being held ready survives this. It survives whenever the needle
        // lands before the point where it was to start. The plan names a second on THIS record. The record
        // will still reach that second, and everything the plan says about the pair is as true from here as
        // it was from the earlier position.
        //
        // A small move back to hear a bar again should not throw away the changeover. The choice would be
        // made again, and it could come back as a different record. The pull already ridden onto the mix
        // tempo would be dropped, and nothing on screen would show that. So the plan is kept when the
        // needle lands before its start.
        bool keeps = _pending is { } planned && at < planned.StartAtSeconds;

        // Past the start of the changeover, the plan really has gone. The point where it was to begin is
        // behind the needle. The plan goes back in the crate rather than being lost from the set. It does
        // not go back while its decoder is still opening, because that is the one moment when the free deck
        // is not ours to touch.
        if (_pending is { } stale && !keeps && _cueing is null or { IsCompleted: true })
        {
            _crate.PutBack(stale.Incoming);
            _pending = null;
            _pendingWasSkip = false;
            _cueing = null;
            _matchingIn = false;
            _booth.Other(_live).Unload();
            NoteNextReady();

            // A pull on the record also belonged to the changeover that is no longer happening, so it is
            // dropped with the plan. A record left part way onto another record's tempo with no ride to
            // finish it would not match the other deck when they meet.
            _riding = null;
        }

        // Play out instead of mixing only when the needle lands where the planned changeover can no longer
        // run. That is at or past the record's own mix-out point, in its last stretch. The last minute
        // alone is not the test. The mix-out point of nearly every record is in its last minute, and the
        // planner already budgets the fade to fit from there. If the test used the clock instead of the
        // plan, every deliberate jump to the changeover would become a cut. That is the opposite of what a
        // listener who aimed for the mix point wants to hear.
        double mixOutAt = TransitionPlanner.MixOutPoint(track, Overrides?.Policy);
        _playOutNoMix = end > 0
            && end - at <= TransitionPlanner.MinRoomToMixSeconds
            && (mixOutAt <= 0 || at >= mixOutAt - 0.5);

        // Only one decoder at a time. Dragging the bar asks for a new position several times a second, and
        // each ask opens a process. The asks collapse into the newest one, and the deck goes there once.
        _liveSeeks.Ask(_live, at);
        return true;
    }

    /// <summary>
    /// Coalesces the scrubber's requests, one per deck. It holds the newest position asked for, and the one
    /// seek that is allowed to be on its way there. This is written once, because it has to be right once.
    /// A fix to the drain (a failed open, a task race) must reach both scrubbers. Otherwise the cued deck
    /// would quietly behave differently from the live one.
    /// </summary>
    private sealed class SeekPump(Func<Action, Task> cue)
    {
        private readonly object _gate = new();
        private Task? _running;
        private double _wanted = double.NaN;

        public void Ask(Deck deck, double seconds)
        {
            lock (_gate)
            {
                _wanted = seconds;
                if (_running is { IsCompleted: false })
                    return;
                _running = cue(() => Drain(deck));
            }
        }

        /// <summary>Takes the deck to the newest position asked for. If the bar moved again while it was
        /// going, it goes to the next one too.</summary>
        private void Drain(Deck deck)
        {
            while (true)
            {
                double want;
                lock (_gate)
                {
                    if (double.IsNaN(_wanted))
                    {
                        _running = null;
                        return;
                    }
                    want = _wanted;
                    _wanted = double.NaN;
                }

                deck.SeekTo(want);
            }
        }
    }

    private readonly SeekPump _liveSeeks;
    private readonly SeekPump _cuedSeeks;

    /// <summary>
    /// True while the record playing is to be played out, not mixed out of. This is when the listener has
    /// scrubbed into its last minute. It is cleared whenever a record lands on the live deck, because it is
    /// a fact about where the needle was put, not about the record.
    /// </summary>
    private bool _playOutNoMix;

    /// <summary>Where a record stops being audible. This is the measured end where there is one. Otherwise
    /// it is the file's own end.</summary>
    private static double PlayingEnd(MixTrack track) =>
        track.Analysis?.AudibleEndSeconds ?? track.DurationSeconds;

    /// <summary>
    /// While the live record plays on its own, cue the next record up as its outro approaches. Start the
    /// changeover once the point has arrived and the record is on the deck.
    /// </summary>
    private void WatchForTheOutro()
    {
        if (_live.Track is not { } playing)
            return;

        bool skipping = _skipRequested;
        double at = _live.PositionSeconds;

        // Nothing is waited for on a skip. That means not the analyser, and not a bar line. A record that
        // has not been read has no grid to mix on. It does not need one to be played. Holding the press for
        // eight seconds while a reading lands would make the next key do nothing.

        // A skip throws away a record that was already cued, and picks again. The record on the platter was
        // chosen to follow the END of this track. Being asked to move now, from somewhere else in the
        // track, is a different question. The energy here is not the energy there. The record that suited
        // the outro may be the wrong record for a breakdown. It goes back in the crate, so it is not lost
        // from the set.
        //
        // It is also cued in the wrong place for a skip, whatever the skip is. It is cued past its intro
        // and bent onto a tempo that it is no longer going to meet. Next plays a record from the top, so
        // the deck is loaded again either way.
        if (skipping && !_pendingWasSkip && _pending is { } stale && _cueing is null or { IsCompleted: true })
        {
            _crate.PutBack(stale.Incoming);
            _pending = null;
            _cueing = null;
            _matchingIn = false;
            NoteNextReady();              // it is back in the crate, and it may be the read one
        }

        // Where this record hands over. Normally this is its outro. When the listener has scrubbed into the
        // last minute, it is the end of the record. There is no room to mix out of it there, and nothing is
        // gained by trying. See SeekTo.
        double mixOutAt = _pending?.StartAtSeconds
            ?? (_playOutNoMix
                ? Math.Max(0, PlayingEnd(playing) - TransitionPlanner.SkipCutSeconds)
                // Where the changeover could START. This is not where the outro is. For everything laid out
                // backwards from the outro, those two points are minutes apart. See
                // TransitionPlanner.ArmFrom. Using the outro here was the mistake that ArmFrom avoids.
                : TransitionPlanner.ArmFrom(playing, Overrides));

        // Nothing is lined up yet, and the outro is coming. Or the user asked for the next record now, or
        // the workbench asked for it early so that it can be auditioned. Either way, pick it and cue it on
        // the free deck. The load starts a decoder, so it happens off the audio thread.
        bool cueNow = _cueNowRequested;
        if (_pending is null && _cueing is null && (skipping || cueNow || at >= mixOutAt - CueAheadSeconds))
        {
            _cueNowRequested = false;
            PickAndCueNext(playing, at, skipping);
            return;
        }

        if (_pending is not { } pending)
            return;

        // The point has come, but the fade only starts once the record is on the deck. A decoder that is
        // slow to open delays the fade. It does not drop a silent record into the mix. A skip waives the
        // point, but never the record being ready.
        if ((!skipping && at < ArmAt(playing, pending)) || _cueing is not { IsCompleted: true })
            return;

        // Not before the record playing has reached the tempo that it will hand over at. Starting half way
        // through the pull puts the new record on a grid that is still moving. On a skip, it would also
        // loop a bar that is not the length the plan was written for.
        if (_matchingIn)
        {
            if (!RideHasLanded)
                return;
            _matchingIn = false;
        }

        _skipRequested = false;
        BeginFade(pending);
    }

    /// <summary>
    /// Answers a hand's request for the record coming in, while the decks are not the set's.
    ///
    /// <para>Cueing is not planning. A hand on the decks switches off the set's watch for an outro. Putting
    /// the next record on the free deck is what the second deck's play key asks for. It is also the only
    /// way to hear what is coming before it arrives. Nothing is armed here. <see cref="WatchForTheOutro"/>
    /// is still the only place where a changeover can begin, and it is not running.</para>
    /// </summary>
    private void CueForTheHands()
    {
        if (!_cueNowRequested || _live.Track is not { } playing)
            return;
        if (_pending is not null || _cueing is not null)
        {
            _cueNowRequested = false;
            return;
        }

        _cueNowRequested = false;
        PickAndCueNext(playing, _live.PositionSeconds, skipping: false);
    }

    /// <summary>
    /// Picks what follows the record playing, works the changeover out, and puts the record on the free
    /// deck at the point the plan named. The decoder is opened off the audio thread.
    /// </summary>
    private void PickAndCueNext(MixTrack playing, double at, bool skipping)
    {
        // The next record is chosen against where the record actually is, not against the track as a whole.
        // The energy under the needle now is what the next record has to follow. Whatever a hand has turned
        // down for this changeover goes with the request. See RejectCued. This is not a permanent opinion
        // about the record. The list is emptied when a changeover actually happens.
        string[] rejected;
        lock (_rejected) rejected = [.. _rejected];

        var next = _crate.TakeNext(playing, _live.PlayingBpm, _live.CurrentIntensity, rejected);
        NoteNextReady();
        if (next is null)
        {
            _skipRequested = false;                  // nothing to skip to
            return;                                  // last record of the set: let it play out
        }

        // A skip is not a changeover at all. The record playing is rolled out on a loop of its own last
        // bar, and this record starts after it. A record scrubbed to its end is not a changeover either. It
        // is played out, and this record follows. Only a record that is left to reach its own outro gets
        // mixed into.
        //
        // Whatever was asked for is taken here, and not before. This is the moment the changeover becomes a
        // decision. An override read any earlier would belong to a changeover that has already happened.
        var asked = Overrides is { IsEmpty: false } ? Overrides : null;

        // The luck a technique is chosen with. It is only read where a technique is going to be chosen, and
        // it is read here and nowhere else. So a set that does not turn on MixPolicy.Techniques never draws
        // a number from the machine's randomness. The roll's own luck is never shifted by one draw. Without
        // this, adding the feature would change the sound of every set that does not use it.
        //
        // The luck is held rather than drawn here. So what the workbench has been showing for the last
        // three minutes is what this plans. See _luck.
        double luck = _luck;

        var plan =
            skipping ? TransitionPlanner.SkipTo(playing, _live.PlayingBpm, next, at, asked)
            : _playOutNoMix ? TransitionPlanner.PlayOut(playing, next)
            : TransitionPlanner.Plan(playing, _live.PlayingBpm, next, asked, luck: luck);
        _pending = plan;
        _pendingWasSkip = skipping;
        var free = _booth.Other(_live);
        var live = _live;

        // From here the playing record's speed belongs to this changeover. It does not belong to whatever
        // ride brought it in. Two things moving one pitch fader would fight each other.
        _riding = null;

        // A cued deck is not a slot. It is a decoder open on one file, parked at one point, at one speed.
        // If the plan asks for those same three things, the record the DJ wants is already on the platter
        // with its needle in the right place. Reloading it would only mean killing a process to start an
        // identical one.
        bool alreadyCued =
            free.IsLoaded && !free.IsRunning && !free.HasRunOut &&
            free.Track is { } cued &&
            string.Equals(cued.Path, plan.Incoming.Path, StringComparison.Ordinal) &&
            Math.Abs(free.CueSeconds - plan.CueInSeconds) < 0.02 &&
            Math.Abs(free.TempoRatio - plan.TempoRatio) < 0.002;

        _cueing = _cue(() =>
        {
            if (!alreadyCued)
            {
                free.Load(plan.Incoming, plan.CueInSeconds, plan.TempoRatio);

                // Prime the first block here, not on the audio thread. A decoder that has just been started
                // has not necessarily decoded anything yet. The first read would wait for it, and that wait
                // is a hole in the mix exactly where the record should arrive.
                free.Prime();
            }

        });

        // Both pitch faders, not one. The record already playing takes half of the difference. It is ridden
        // across rather than dropped on the record. Several bars of it ease onto the tempo the changeover
        // will run at, before any of the changeover happens.
        //
        // A step change of several percent is heard as a record being interfered with. That is true whether
        // or not anything is mixed over it. Having twelve seconds of cue-ahead does not hide it. Nobody
        // listening for it yet is not the same as nobody able to hear it.
        if (Math.Abs(plan.OutgoingTempoRatio - 1) > 1e-6)
        {
            _matchingIn = true;

            // A skip has the ride to itself, with nothing else to be ready for. A mix has to be at its
            // tempo by the time the outro arrives. That is the cue-ahead away. So on a long bar the ride is
            // shortened to fit, rather than made to hold the changeover up.
            double over = TransitionPlanner.MatchInBars * plan.BarSeconds;
            if (!skipping)
                over = Math.Min(over, CueAheadSeconds);

            BeginRide(live, over, plan.OutgoingTempoRatio);
        }
    }

    /// <summary>
    /// Where the set starts watching for a changeover. This is a little before the point the plan named.
    ///
    /// <para>The plan's point is a bar line. <see cref="BeginFade"/> holds the new record until the
    /// outgoing one reaches its NEXT bar line. If the watch starts at the point itself, the block that
    /// catches it is always a fraction past it. So the line that the fade then waits for is the one after.
    /// Every changeover in the set would run a bar later than planned, and on a line nobody chose. The
    /// green mark on the mix editor would sit on a four while the mix arrives a bar off it. Watching from
    /// inside the bar before means the line the plan named is the line the fade lands on.</para>
    ///
    /// <para>Half a bar early, not a whole one, so that a long block cannot overshoot the point and land
    /// back where it started. This only applies when a bar line was named at all. A cut names the moment a
    /// hand pressed something. Moving that earlier would make the set jump the gun.</para>
    /// </summary>
    private static double ArmAt(MixTrack playing, TransitionPlan plan)
    {
        if (plan.Style is TransitionStyle.Cut || playing.Analysis is not { } grid)
            return plan.StartAtSeconds;

        double previous = grid.BarAtOrBefore(plan.StartAtSeconds - 1e-3);
        return previous < plan.StartAtSeconds - 1e-3
            ? (previous + plan.StartAtSeconds) / 2
            : plan.StartAtSeconds;
    }

    private void BeginFade(TransitionPlan plan)
    {
        var incoming = _booth.Other(_live);
        if (!incoming.IsLoaded)
            return;

        // Beat matching, the part that is not tempo. The incoming record is cued ON a bar line. Holding it
        // until the outgoing record reaches the line that the plan named lands the two grids on the same
        // sample. Without this, the drop happens wherever the 50 ms block boundary falls. That can be up to
        // a tenth of a beat out at club tempo, which is what a loose mix sounds like.
        //
        // The line the PLAN named is used, not just the next one on the grid. Both are bar lines, and only
        // one of them is the four that the changeover was drawn on. A mix that waits for the next line is a
        // mix that starts a bar or two into a phrase. That is what the phrase snap in TransitionPlanner
        // exists to prevent.
        //
        // A blend gets the same wait. The two tempos would not meet, so the grids walk apart from there.
        // But they walk apart from wherever they started. Starting them together is the difference between
        // a record arriving on the beat and a record arriving off it. A cut does not wait. There is no grid
        // to wait on, and the press has to be the moment.
        long barLine = 0;
        if (plan.Style != TransitionStyle.Cut)
        {
            // In heard seconds, because the hold is counted in output frames. The grid is measured in the
            // record's own seconds. A deck pulled 5% fast reaches its next line 5% sooner.
            double toThePlan = (plan.StartAtSeconds - _live.PositionSeconds) / _live.TempoRatio;

            // The line is already past. This happens when the decoder took longer to open than the record
            // had left to run. Nothing can be done about the line that has gone. The next one is still
            // better than starting between two.
            double wait = toThePlan > 1e-6 ? toThePlan : _live.SecondsToNextDownbeat ?? 0;
            barLine = (long)Math.Round(Math.Max(0, wait) * _sampleRate);
        }

        // How long the automation runs, and how long the incoming record is held. On a mix these are the
        // same thing. The new record arrives as the fade starts. On a roll-out they are not, because the
        // new record arrives as the fade ENDS.
        long automation = Math.Max(1, (long)(plan.FadeSeconds * _sampleRate));
        long hold = barLine;

        _rollOverlap = 0;

        // The arc that this crossfade follows, and where along it the room changes records. This is read
        // once, here. The fade reads these on every block. A curve is something to walk, not something to
        // work out again.
        _arc = MixTechniques.FollowsAnArc(plan.Style) ? plan.Arc : null;
        _arcBalance = _arc?.BalanceT ?? Crossfader.Balance;

        if (plan.Style == TransitionStyle.RollOut)
        {
            // The next key. The record playing runs to its bar line and rolls out from there. The length is
            // counted in frames, not worked out from the seconds twice. So the roll and the hold cannot
            // round to different samples.
            long barFrames = Math.Max(1, (long)Math.Round(plan.BarSeconds * _sampleRate));
            long roll = barFrames * plan.FadeBars;

            // Which roll. There are two, chosen one changeover at a time: the whole bar repeated, and the
            // beat roll that shortens as it goes. A set that only ever uses one of them would have a habit.
            // See Rolls.Choose.
            var style = Rolls.Choose(_chance());

            _live.RollOut(Rolls.Out(style, barFrames, plan.FadeBars), barLine);
            automation = roll;

            // The record coming in rolls in on the same idea before it plays on. It starts that roll while
            // the old roll is still going. The two run against each other, one coming up as the other goes
            // down. The new record is at full level and playing on at the exact sample where the roll it
            // arrived over stops. Nothing lands in a hole, which is what a fade to silence and then a
            // record always does.
            //
            // Both are cut to their own record's bar. By this point that is the same bar. See
            // TransitionPlanner.SkipTo. This is the reason the two can be laid over each other at all. They
            // have the same bar and the same length. Their downbeats are already together, because the
            // overlap starts a whole number of bars into the roll.
            long inBar = 0;
            if (plan.RollInBarSeconds > 0)
            {
                inBar = Math.Max(1, (long)Math.Round(plan.RollInBarSeconds * _sampleRate));

                // For exactly as many of its own bars as it will be heard over. So the schedule ends on the
                // sample where the overlap does. It is not cut off, and it does not run dry.
                int inBars = plan.OverlapBars > 0 ? plan.OverlapBars : TransitionPlanner.RollInBars;

                // One bar is captured, whatever length the slices get down to. The roll is free to tighten
                // into a stutter, which is what it is for. The record still comes out of it on its own bar
                // line, because it plays on from the capture and not from the last slice.
                incoming.RollIn(Rolls.In(style, inBar, inBars), captureFrames: inBar);
            }

            // The overlap is measured in the bar that the incoming record is actually looping, not in the
            // mix's bar. The two differ by an octave on a record that is read away from the count. See
            // TransitionPlanner.RollInBarSeconds. If the overlap were measured in the wrong one, the new
            // record would land part way through its own bar and stay there. That is the fault the roll-in
            // bar was separated out to fix.
            //
            // This is still a whole number of mix bars. The two only ever differ by a power of two, and
            // RollInBars is one. So the loops start together, whichever of them is the longer.
            long overlapBar = inBar > 0 ? inBar : barFrames;

            // Never more than the roll itself. So the new record is never asked to start before the loop it
            // comes up over has begun.
            _rollOverlap = Math.Min(overlapBar * plan.OverlapBars, roll);
            hold = barLine + roll - _rollOverlap;

            // Sometimes the noise climbs across the whole of the roll, from the bar where the loop starts.
            // It is cut off in EndFade, on the sample where the new record has the floor to itself. The cut
            // is the point of it. It has to happen at the same instant the loop stops, or it is just noise.
            //
            // Only sometimes, not every time. See TransitionPlanner.NoiseChance. The decision is taken
            // once, here. So the fall that follows the rise knows whether there was one.
            if (_chance() < (plan.Overrides?.Policy?.NoiseChance ?? TransitionPlanner.NoiseChance))
                _riser.Rise(delayFrames: barLine, lengthFrames: roll);
        }

        // One bar of the mix. Both gestures below count in whole bars from the line the record is left on.
        // This is never zero. A changeover with no bar to count in cannot use either gesture, and the
        // planner already refuses to make one. This keeps the arithmetic safe, rather than handling a case
        // that happens.
        long mixBar = plan.BarSeconds > 0
            ? Math.Max(1, (long)Math.Round(plan.BarSeconds * _sampleRate))
            : Math.Max(1, (long)(plan.FadeSeconds * _sampleRate) / 2);

        if (plan.Style == TransitionStyle.EchoOut)
        {
            // The tail has the first bar to itself, and the new record lands dry on the next bar. This is
            // the shape that most guides describe: engage the echo on the last beat of the phrase, shut the
            // channel, and start the next track on the following downbeat.
            long tail = Math.Min(mixBar, Math.Max(1, automation - 1));
            hold = barLine + tail;

            double beat = plan.BarSeconds > 0 ? plan.BarSeconds / 4 : (double)mixBar / _sampleRate / 4;
            _echoDelay = beat * TransitionPlanner.EchoBeats;

            // Never past the end of the changeover. A tail that is still ringing when the next record has
            // the room is not a technique. It is a deck that somebody forgot about. The deck it lives on is
            // unloaded at the end of the fade anyway.
            _echoFor = plan.FadeSeconds;

            // Half a beat to shut the channel. This is a hand's speed, and it is long enough not to be a
            // step in the waveform.
            _echoKill = Math.Max(1, (long)(beat * 0.5 * _sampleRate));
        }

        if (plan.Style == TransitionStyle.Spinback)
        {
            // A bar is played and kept, and then the same bar is dragged back through. The record coming in
            // is held for both, so it lands on the downbeat after the rewind has stopped. This is what a
            // wheel-up sounds like. The room gets a moment of nothing but the pull, and then the tune.
            long capture = mixBar * TransitionPlanner.SpinBars;
            long rip = mixBar * Math.Max(1, plan.FadeBars - TransitionPlanner.SpinBars);

            _live.SpinBack(barLine, capture, rip);
            automation = capture + rip;
            hold = barLine + automation;
        }

        incoming.Start(hold);

        // Every changeover reports what it did. When a mix sounds wrong, the question is always which part
        // was off: the tempo lock, the cue point, or the bar the drop was placed on. Guessing after the
        // fact does not work. The report is written from the thread pool, not from here. The audio thread
        // must not wait on a lock or on a write to the terminal, and Said does both when no debugger is
        // attached.
        var note =
            $"{plan.Style} {_live.Track?.Title} ({_live.Track?.Bpm:F1}×{_live.TempoRatio:F4} = " +
            $"{_live.PlayingBpm:F1}) → {plan.Incoming.Title} ({plan.Incoming.Bpm:F1}×{plan.TempoRatio:F4} = " +
            $"{plan.Incoming.Bpm * plan.TempoRatio:F1}) at {plan.MixBpm:F1} BPM, " +
            $"cue-in {plan.CueInSeconds:F2}s, held {barLine * 1000.0 / _sampleRate:F0} ms for the bar, " +
            $"fade {plan.FadeBars} bars of {plan.BarSeconds:F2}s = {plan.FadeSeconds:F1}s, " +
            $"released over {plan.ReleaseSeconds:F1}s";
        ThreadPool.UnsafeQueueUserWorkItem(static state => Said.Line("dj", (string)state!), note);

        // The automation waits for the bar line. Until the fade proper begins, neither fader moves.
        // Otherwise the outgoing record would already be on its way down, off the beat.
        _fadeFrames = -barLine;
        _fadeLength = automation;
        _rollingOut = plan.Style == TransitionStyle.RollOut;
        _filtering = plan.Style == TransitionStyle.FilterFade;
        _echoing = plan.Style == TransitionStyle.EchoOut;
        _echoThrown = false;
        _spinningBack = plan.Style == TransitionStyle.Spinback;
        _fading = true;
        _dropped = false;

        // The pull that the incoming record will be played at, piece by piece. See TempoStep. This is only
        // used where the new record is actually running alongside the old one for the length of the fade.
        // The gestures below hold it back and then drop it whole. So a schedule written from the start of
        // the changeover would move a deck that has not begun.
        _steps = _rollingOut || _echoing || _spinningBack ? [] : plan.TempoSteps;
        _stepNext = 1;
        _planRatio = plan.TempoRatio > 0 ? plan.TempoRatio : 1;
        _lockUntil = 0;
        _lockNeedle = -1;
        _lockLive = -1;
        _lockTurning = 0;

        // The two gestures. Neither is a crossfade. The record playing keeps its level and its bottom end
        // right up to the moment it is taken away, by the fader under an echo or by a hand on the platter.
        // The record coming in is held rather than faded, so it arrives whole on a downbeat with nothing
        // over it.
        if (_echoing || _spinningBack)
        {
            _live.Fader = 1;
            _live.Eq.SetLow(0);
            _live.Sweep.SetCutoff(SweepFilter.OpenHz);

            incoming.Fader = 1;
            incoming.Eq.SetLow(0);
            incoming.Sweep.SetCutoff(SweepFilter.OpenHz);
            return;
        }

        if (_rollingOut)
        {
            // The old record holds its level and its bottom end while the loop starts. The new one is
            // silent, because it has not begun. It is held, not faded.
            _live.Fader = 1;
            _live.Eq.SetLow(0);
            _live.Sweep.SetCutoff(SweepFilter.OpenHz);
            incoming.Eq.SetLow(0);

            if (_rollOverlap <= 0)
            {
                incoming.Fader = 1;                 // nothing to come up over: it arrives whole
                incoming.Sweep.SetCutoff(SweepFilter.OpenHz);
                return;
            }

            // It comes in as its bottom end, and opens up across the roll-in. See AdvanceRollOut.
            incoming.Fader = 0;
            incoming.Sweep.SetCutoff(SweepFilter.ClosedHz);
            return;
        }

        // Set the two records' levels for the start of the fade. The outgoing record is up and owns the
        // bass. The incoming record is silent, with its bottom end already out of the way.
        var levels = _arc?.LevelsAt(0) ?? Crossfader.At(0);
        var bass = _arc?.BassDbAt(0) ?? Crossfader.BassDb(0);
        _live.Fader = levels.Outgoing;
        incoming.Fader = levels.Incoming;
        _live.Eq.SetLow(bass.Outgoing);
        incoming.Eq.SetLow(bass.Incoming);

        // A filter fade starts with the sweep flat, and climbs it through the record from the first bar.
        // This is the other way up from the roll-out, which closes down over one. See AdvanceFade.
        if (_filtering)
            _live.Sweep.SetHighPass(SweepFilter.FlatHz);
    }

    /// <summary>Moves the fader and the bass swap on by one block. Ends the changeover when the fade runs
    /// out, or when the outgoing record does.</summary>
    private void AdvanceFade(int frames)
    {
        // Whether the fade was ALREADY over when this block began. That is not the same as whether it is
        // over now. The block in which it runs out still has the last of the outgoing record's ramp in it.
        // If the deck is taken away before that block is mixed, the ramp never plays, and the record stops
        // at whatever level it had reached instead of at zero. The difference is small, but a step is a
        // step.
        bool spent = _fadeFrames >= _fadeLength;

        _fadeFrames += frames;
        var incoming = _booth.Other(_live);

        // If the outgoing record ends while the incoming one is still held for the bar line, there would be
        // silence and no fade to finish. So the outgoing record is let go now, and the seam is taken. A
        // roll-out is not affected. The record running out is what the loop is there to survive. A rewind
        // is not affected either. The bar it drags back is captured from whatever the record had left, and
        // the gesture runs its full length regardless.
        if (_live.HasRunOut && _fadeFrames < 0 && !_rollingOut && !_spinningBack)
        {
            incoming.Start();
            _fadeFrames = 0;
        }

        // While the incoming record is still held for the bar line, the fade is scheduled but not started.
        // Both decks stay exactly where they were.
        double t = _fadeFrames <= 0 ? 0 : Math.Clamp((double)_fadeFrames / _fadeLength, 0, 1);

        AdvanceWarp(incoming);
        AdvanceLock(incoming, frames);

        if (_rollingOut)
        {
            AdvanceRollOut(t, spent);
            return;
        }

        if (_echoing)
        {
            AdvanceEchoOut(spent);
            return;
        }

        if (_spinningBack)
        {
            // Nothing to ride. The record playing is at full and stays there. A hand on a platter is not a
            // fader move. The record coming in is held until the pull has stopped. So both levels were
            // settled when the changeover began.
            if (spent)
                EndFade();
            return;
        }

        var levels = _arc?.LevelsAt(t) ?? Crossfader.At(t);
        var bass = _arc?.BassDbAt(t) ?? Crossfader.BassDb(t);
        _live.Fader = levels.Outgoing;
        incoming.Fader = levels.Incoming;
        _live.Eq.SetLow(bass.Outgoing);
        incoming.Eq.SetLow(bass.Incoming);

        // On a filter fade, the record playing is also climbed out from underneath. Its bottom goes first,
        // then its body, until there is nothing left of it but air. The fader curve above is drawn to let
        // the filter do the work. It holds its level for the first half, and only finishes what the sweep
        // started.
        if (_filtering)
            _live.Sweep.SetHighPass(HighPassAt(Math.Min(1, t / Crossfader.OutBy)));

        // The balance point. The incoming record is now the louder of the two, and this is when the room
        // has changed over. Everything that shows what is playing follows this, and not the start of the
        // fade, where the old record is still the one being heard. On the classic curve it is half way
        // through the crossing. On a drawn arc it is wherever the lines cross.
        if (!_dropped && t >= _arcBalance && _pending is { } landing)
        {
            _dropped = true;
            TrackChanged?.Invoke(landing.Incoming);
        }

        if (spent || _live.HasRunOut)
            EndFade();
    }

    /// <summary>
    /// One block of a roll-out. The fader goes down and the filter closes over the looped bar. The record
    /// coming in rises through the last bars of it.
    ///
    /// <para>The top of the old record goes first, then the middle. What is left under the last bars is a
    /// kick and a bassline, which is the sound of a record about to change. The new one arrives into that,
    /// and not into silence.</para>
    /// </summary>
    private void AdvanceRollOut(double t, bool spent)
    {
        // Equal power, the same leg that the outgoing side of a crossfade uses. It holds its level through
        // the first bars, where the loop still has to sound like the record. It does its going in the last
        // bars. It is gone before the roll is, so the new record has the last bars alone.
        _live.Fader = Crossfader.At(t).Outgoing;

        // The filter closes with the fader, on the same clock. So the record is at its most filtered
        // exactly as it disappears. The movement is geometric, because the ear hears cutoff in octaves. A
        // straight line in hertz would do all of its moving in the first bar and none after.
        _live.Sweep.SetCutoff(SweepAt(Math.Min(1, t / Crossfader.OutBy), closing: true));

        // The other end of the same crossing. The record coming in arrives over the last bars of the roll.
        // It is measured on its own clock, not the roll's, because the two are different lengths.
        if (_rollOverlap > 0)
        {
            long into = _fadeFrames - (_fadeLength - _rollOverlap);
            double u = Math.Clamp((double)into / _rollOverlap, 0, 1);
            var incoming = _booth.Other(_live);

            // It arrives as its bottom end, and opens up. The kick and the bassline come in from the first
            // beat. The middle and the top come in across the whole of the roll-in. This is how a record is
            // introduced. The room has the new groove under it for a bar before it is told what the record
            // is.
            incoming.Sweep.SetCutoff(SweepAt(u, closing: false));

            // The level itself comes up almost at once. The filter is doing the arriving, not the fader.
            // The ramp is only there so that the bottom end does not appear as a step.
            incoming.Fader = Crossfader.At(Math.Min(1, u * TransitionPlanner.RollInBars)).Incoming;
        }

        // No drop half way. What is playing does not change until the loop ends and the new record has the
        // room to itself. EndFade announces that.
        if (spent)
            EndFade();
    }

    /// <summary>
    /// One block of an echo out: the throw on the bar line, and the channel shut under it.
    ///
    /// <para>The throw happens here and not in <see cref="BeginFade"/>. A changeover is armed up to a bar
    /// before the line it lands on. An echo engaged a bar early would smear a bar of the record for no
    /// reason. From the line, the deck's output goes into the delay, and the fader goes to nothing over
    /// half a beat. The echo is taken after the fader, so what the room is left with is the last half
    /// second of the record repeating and dying, on the grid, with nothing under it.</para>
    /// </summary>
    private void AdvanceEchoOut(bool spent)
    {
        if (!_echoThrown && _fadeFrames >= 0)
        {
            _echoThrown = true;
            _live.Echo.Throw(_echoDelay, TransitionPlanner.EchoFeedback, _echoFor);
        }

        if (_echoThrown)
            _live.Fader = _echoKill > 0 ? Math.Max(0, 1 - (double)_fadeFrames / _echoKill) : 0;

        // No drop half way, and no drop at all until the tail has gone and the new record has the room.
        // EndFade announces the change then.
        if (spent)
            EndFade();
    }

    /// <summary>
    /// Where a filter fade's high pass sits, <paramref name="t"/> of the way through climbing out of a
    /// record.
    ///
    /// <para>The curve is geometric between the two ends, for the same reason as the low pass. Pitch is
    /// heard in octaves. A straight line in hertz would spend the whole changeover among the bass notes,
    /// and then cross everything else in its last moment.</para>
    /// </summary>
    private static double HighPassAt(double t) =>
        SweepFilter.FlatHz *
        Math.Pow(SweepFilter.HighestHz / SweepFilter.FlatHz, Math.Clamp(t, 0, 1));

    /// <summary>
    /// Where the filter sits, <paramref name="t"/> of the way through closing a record down or opening one
    /// up.
    ///
    /// <para>The curve is geometric between the two ends, because pitch is heard in octaves. A straight
    /// line in hertz spends nine tenths of its travel in the top octave, where there is not much left to
    /// take away. It then crosses everything that anybody would notice in its last moment.</para>
    /// </summary>
    private static double SweepAt(double t, bool closing)
    {
        double span = SweepFilter.ClosedHz / SweepFilter.OpenHz;
        return closing
            ? SweepFilter.OpenHz * Math.Pow(span, Math.Clamp(t, 0, 1))
            : SweepFilter.ClosedHz * Math.Pow(1 / span, Math.Clamp(t, 0, 1));
    }

    /// <summary>
    /// Plays the record coming in at the speed that this stretch of the changeover needs. The changeover
    /// was worked out at one speed for the whole of it. See <see cref="TempoStep"/>.
    ///
    /// <para>This is the sync button, arrived at from the other end. A deck in a booth cannot see what the
    /// record is about to do. So it measures how far out it is, and corrects that on every block for as
    /// long as sync is on. A changeover that was planned already has both grids in front of it. So there is
    /// nothing to measure and nothing to correct. It knows the speed that each stretch wants, and it plays
    /// that speed. The beats land because they were never allowed to leave.</para>
    ///
    /// <para>This runs on the same cue thread, and behind the same one-at-a-time guard as a pitch ride. The
    /// reason is the same. A speed change reopens a decoder, and two of those at once on one deck would be
    /// two decoders, each convinced that it is the record.</para>
    /// </summary>
    private void AdvanceWarp(Deck incoming)
    {
        if (_stepNext >= _steps.Count || _fadeFrames < 0)
            return;

        // A step is already in flight, or a ride is still landing. Being late is harmless. A piece boundary
        // that is put a block or two off is a thousandth of the drift that it exists to remove. A decoder
        // reopened twice at once would play the record twice.
        if (_rideTask is { IsCompleted: false })
            return;

        var step = _steps[_stepNext];
        if ((double)_fadeFrames / _sampleRate < step.AtSeconds)
            return;

        _stepNext++;

        // The plan has the last word on the pull. This is the speed that a correction returns to from here.
        // A correction that is still in flight has nothing left to put back.
        _planRatio = step.Ratio;
        _lockUntil = 0;
        _rideTask = _cue(() => incoming.SetTempo(step.Ratio));
    }

    /// <summary>
    /// How far the two grids may sit apart before the changeover corrects itself, and how long it takes to
    /// close it.
    ///
    /// <para>Half the drift budget, so the correction happens while the pair are still together rather than
    /// once they are audibly not. Over four seconds, because that turns the worst gap worth acting on into
    /// a pull of under half a percent, an order of magnitude below what anybody hears as a record changing
    /// speed, and a tenth of what the gear that does this live allows itself.</para>
    /// </summary>
    private const double LockTrigger = TransitionPlanner.MaxDriftSeconds / 2;
    private const double LockSeconds = 4;

    /// <summary>The most that the phase lock will bend a record, whatever it reads. A gap that needs more
    /// than this is not drift. It is a grid that is wrong, or a deck that has been moved by hand. Pulling
    /// the record about does not fix either one.</summary>
    private const double LockMaxNudge = 0.02;

    /// <summary>Where the correction in progress ends, in fade frames. This is zero when the decks are
    /// simply running to plan.</summary>
    private long _lockUntil;

    /// <summary>The pull that the PLAN wants on the record coming in, as of now. This is the piece of the
    /// schedule that is in force, or the changeover's one pull where there is no schedule.
    ///
    /// <para>A correction goes back to this when it has finished. The plan's speed is the right one to
    /// return to, and the deck's current speed is not. The most important fault to correct is a speed
    /// change that the deck could not make (see <see cref="Deck.SetTempo"/>, which keeps the old speed and
    /// says nothing). Putting the deck back on the speed it was on would keep that fault. Going back to the
    /// plan repairs it.</para>
    /// </summary>
    private double _planRatio = 1;

    /// <summary>The last block's position of each record. This lets the lock tell a record that is turning
    /// from one that has only started. <see cref="Deck.Start"/> takes the frames to wait before the first
    /// sample, and it reports the deck as running for the whole of that wait. So a deck held for the bar
    /// line looks exactly like a deck that is playing. Its needle is parked on its cue point while the
    /// other record moves, which looks exactly like a mix falling apart. Correcting that would reopen the
    /// decoder of a deck that has not begun. That would break the drop that the hold exists to
    /// protect.</summary>
    private double _lockNeedle = -1, _lockLive = -1;

    /// <summary>How long the record coming in has been turning, in frames. The lock waits a second before
    /// it acts. The block that a record starts in is half a block of it. So the first reading after a drop
    /// is out by a block, however well the drop was placed. A correction made from that reading would be a
    /// correction of the measurement.</summary>
    private long _lockTurning;

    /// <summary>How long the two have to have been running together before a reading is acted on.</summary>
    private const double LockSettleSeconds = 1;

    /// <summary>
    /// Watches the two grids while both records are audible, and corrects the pull when they start to come
    /// apart. This is the other half of a sync button. It is also the half that deals with what the plan
    /// could not know.
    ///
    /// <para><see cref="AdvanceWarp"/> plays the speed that the grids say each stretch of the changeover
    /// wants. That is the whole answer, as long as everything happens as written. This covers the rest: a
    /// drop that landed a block late, a needle moved by hand in the middle of a changeover, a speed change
    /// that the decoder could not make (see <see cref="Deck.SetTempo"/>, which keeps the old speed rather
    /// than breaking the record, and says nothing about it), and plain arithmetic that has been running for
    /// half a minute.</para>
    ///
    /// <para>It is slow to act, and gentle when it does. The gap has to be worth acting on (<see
    /// cref="LockTrigger"/>). The correction is spread over seconds rather than beats. Only the record
    /// coming in is ever touched, because the room is listening to the other one. The lock must never
    /// become a loop that chases its own tail. So it makes one correction, puts the pull back where the
    /// plan wanted it, and looks again.</para>
    /// </summary>
    private void AdvanceLock(Deck incoming, int frames)
    {
        // This is read every block, whatever else is true. It is a comparison with the last block, and a
        // reading that is skipped is a reading that is lost.
        //
        // Both records must be turning. This is not extra caution. A gap between a record that is moving
        // and one that is not is not a mix drifting. It is one deck waiting: for its bar line, for a
        // decoder, or for the tempo ride in front of a skip. Read as drift, the gap grows by a block every
        // block, until the correction is bigger than the fault it was meant to fix.
        double now = incoming.PositionSeconds, live = _live.PositionSeconds;
        bool turning = _lockNeedle >= 0 && now > _lockNeedle + 1e-9 &&
                       _lockLive >= 0 && live > _lockLive + 1e-9;
        _lockNeedle = now;
        _lockLive = live;
        _lockTurning = turning ? _lockTurning + frames : 0;

        // Not while a speed change is still landing. Not before the two records have been running together
        // long enough for a reading to mean anything. See _lockTurning.
        if (_lockTurning < LockSettleSeconds * _sampleRate || _fadeFrames < 0 ||
            _rideTask is { IsCompleted: false })
            return;

        // A correction in progress runs its course, and then hands the record back to the plan. It is put
        // back, rather than left where it is. The corrective pull was for the gap, not for the music. A
        // record left on it would drift the other way for the rest of the changeover.
        if (_lockUntil > 0)
        {
            if (_fadeFrames < _lockUntil)
                return;

            double back = _planRatio;
            _lockUntil = 0;
            _rideTask = _cue(() => incoming.SetTempo(back));
            return;
        }

        if (GridGap(incoming) is not { } gap || Math.Abs(gap) < LockTrigger)
            return;

        // The pull that covers the gap over LockSeconds, on top of the pull the plan wants. A positive gap
        // means the record coming in is running late, and such a record has to cover more of itself in the
        // same time. The pull is capped. A gap too big to close gently is closed over several goes, rather
        // than by visibly wrenching the record about.
        double nudge = Math.Clamp(gap / LockSeconds, -LockMaxNudge, LockMaxNudge);
        double ratio = _planRatio * (1 + nudge);

        _lockUntil = _fadeFrames + (long)(LockSeconds * _sampleRate);
        _rideTask = _cue(() => incoming.SetTempo(ratio));
    }

    /// <summary>
    /// How far the record coming in is behind the record playing, in the seconds the room hears. It is null
    /// where either grid cannot say.
    ///
    /// <para>Each deck's needle is measured against its own nearest beat. That distance is converted into
    /// the mix's seconds using that deck's own pull. A deck pulled five percent fast reaches its next beat
    /// five percent sooner. The difference is wrapped into half a beat either side. It is wrapped because a
    /// beat is a beat. Two grids a whole beat apart are two grids together. What the room hears is the
    /// distance to the nearest beat of the other record, not to the beat with the same number.</para>
    /// </summary>
    private double? GridGap(Deck incoming)
    {
        if (Toward(_live) is not { } live || Toward(incoming) is not { } cued)
            return null;

        double beat = _live.PlayingBpm > 0 ? 60.0 / _live.PlayingBpm : 0;
        if (beat <= 0)
            return null;

        double gap = cued - live;
        gap -= Math.Round(gap / beat) * beat;
        return gap;

        static double? Toward(Deck deck)
        {
            if (deck.Track?.Analysis is not { } grid || deck.TempoRatio <= 0)
                return null;

            double at = deck.PositionSeconds;
            return grid.BeatNearest(at) is { } near ? (near - at) / deck.TempoRatio : null;
        }
    }

    /// <summary>Starts letting a record that has just landed back up to its own tempo.</summary>
    private void BeginRide(Deck deck, TransitionPlan plan) =>
        BeginRide(deck, plan.ReleaseSeconds, toRatio: 1);

    /// <summary>
    /// Starts moving a deck's pitch fader to <paramref name="toRatio"/> over <paramref name="seconds"/>, in
    /// steps small enough that nobody hears the record change speed.
    ///
    /// <para>A move too small to be worth riding is still made, in one go, right away. That distinction is
    /// the whole contract of this method. <see cref="RideStep"/> says how finely to travel. It never says
    /// whether to arrive.</para>
    /// </summary>
    /// <remarks>
    /// A move that is smaller than <see cref="RideStep"/> must still be made. If it were skipped, the deck
    /// would keep its old speed while the plan assumed the new one. This is the same fault that <see
    /// cref="Audio.FfmpegPcmSource.UnstretchedRatio"/> documents, one layer up. Two records one percent
    /// apart meet in the middle at half a percent each. That is under the step, so the record playing would
    /// stay at its own speed. The record coming in would be pulled to a tempo that nothing else shares. The
    /// plan would say the two were locked, and every reader downstream would believe it. Over a sixteen-bar
    /// blend, the two kicks would walk about 160 milliseconds apart. That is not two kicks that drifted. It
    /// is two kicks.
    ///
    /// <para>The closer two records are in tempo, the smaller the pull. So this fault is most likely on the
    /// pairs that need the ride most.</para>
    /// </remarks>
    private void BeginRide(Deck deck, double seconds, double toRatio)
    {
        _riding = null;

        double move = Math.Abs(deck.TempoRatio - toRatio);
        if (move <= Audio.FfmpegPcmSource.UnstretchedRatio)
            return;                                     // already there, to the decoder's own precision

        if (seconds <= 0 || move < RideStep)
        {
            // One step, now. The cue thread does it, because a speed change reopens a decoder. _rideTask is
            // what BeginFade waits on. A changeover must not start until the tempo the plan was written for
            // is the tempo the deck is actually running at.
            _rideTask = _cue(() => deck.SetTempo(toRatio));
            return;
        }

        _riding = deck;
        _rideFrom = deck.TempoRatio;
        _rideTo = toRatio;
        _rideFrames = 0;
        _rideLength = Math.Max(1, (long)(seconds * _sampleRate));
    }

    /// <summary>True once the ride has arrived and the deck is no longer being moved by it. The next key
    /// waits for this before it starts the transition. The two records are at one tempo, or they never will
    /// be.</summary>
    private bool RideHasLanded => _riding is null && _rideTask is null or { IsCompleted: true };

    /// <summary>
    /// Moves the pitch fader one step nearer to where the ride wants it. There is one step at a time, and
    /// never two at once. A speed change reopens the decoder. That is why the change is handed to the cue
    /// thread, and why the next step waits for the last one to finish.
    /// </summary>
    private void AdvanceRide(int frames)
    {
        if (_riding is not { } deck)
            return;

        _rideFrames += frames;
        double t = Math.Clamp((double)_rideFrames / _rideLength, 0, 1);
        double target = _rideFrom + (_rideTo - _rideFrom) * t;

        if (_rideTask is { IsCompleted: false })
            return;
        if (t < 1 && Math.Abs(target - deck.TempoRatio) < RideStep)
            return;

        _rideTask = _cue(() => deck.SetTempo(target));
        if (t >= 1)
            _riding = null;
    }

    /// <summary>Takes the finished record off. The deck that was coming in becomes the live one. This is
    /// what makes the next changeover run the other way round, with no special case.</summary>
    private void EndFade()
    {
        // The pair, before either one is taken off a deck. This is only done where a changeover actually
        // ran. It is also the way out of a record that stopped early. There is nothing to hear again about
        // a fade that never began.
        var again = RehearseChangeover && _fading && !_finished
            ? (Outgoing: _live.Track, Plan: _pending ?? _current)
            : default;

        // The riser stops here, sharply. A rise is there for the moment it is taken away, and that moment
        // is this one. The loop has gone, and the record that arrived has the room.
        bool wasNoisy = _riser.IsRunning;
        _riser.Release();

        // The fall takes the rise's place over the next bars. It is the same gesture from the other side,
        // coming down over the record that the room has been waiting for. It is only used where there was a
        // rise to answer, and only after a skip. A record that reached its own outro has been handed over
        // for sixteen bars, and it does not need announcing.
        if (wasNoisy && _rollingOut && _pending is { BarSeconds: > 0 } landing)
            _faller.Fall((long)(TransitionPlanner.DropBars * landing.BarSeconds * _sampleRate));

        var incoming = _booth.Other(_live);
        _live.Unload();

        incoming.Fader = 1;
        incoming.Eq.SetLow(0);

        // The record is whole again, whatever the changeover did to it. A record that arrived through a
        // filter has the room to itself now, so the room should hear all of it.
        incoming.Sweep.SetCutoff(SweepFilter.OpenHz);
        _live = incoming;

        // A fade that ended before its halfway mark never got as far as saying that the room had moved on.
        // This happens when the outgoing record ran out early. It is announced now. The audio would be
        // fine, but everything that shows what is playing would stay on a record that has gone.
        bool announce = !_dropped && _pending is not null;

        _current = _pending;
        _pending = null;
        _pendingWasSkip = false;
        _cueing = null;
        _fading = false;

        // That changeover has used its luck. The next one draws now, so that the workbench can show what it
        // will be for the whole of the record that has just landed.
        _luck = _chance();
        _rollingOut = false;
        _filtering = false;
        _steps = [];
        _stepNext = 0;
        _lockUntil = 0;
        _lockNeedle = -1;
        _lockLive = -1;
        _lockTurning = 0;
        _echoing = false;
        _echoThrown = false;
        _spinningBack = false;
        _rollOverlap = 0;
        _matchingIn = false;
        _dropped = false;
        _fadeFrames = 0;
        _arc = null;
        _arcBalance = Crossfader.Balance;

        // This is a fact about where the listener put the needle on the record that has just gone. It is
        // not about the record that has just landed.
        _playOutNoMix = false;

        // The old record has gone, so there is nothing left for the new record's tempo to agree with. Let
        // it ride back up to its own tempo. Not earlier. Releasing it while both records were audible would
        // walk the two grids apart across the stretch that beat matching exists for.
        if (_current is { } arrived)
        {
            BeginRide(_live, arrived);
            if (announce)
                TrackChanged?.Invoke(arrived.Incoming);
        }

        // A changeover that is being rehearsed happens again. This is done last, after everything above has
        // settled, because it undoes most of what was done. The record that just arrived goes back in the
        // crate, and the one it replaced goes back on the deck, at the run-up.
        //
        // A rejection is not carried across a changeover that ran. The next changeover is a different
        // question about a different pair. The record turned down here may be the answer to it.
        lock (_rejected)
            _rejected.Clear();

        if (again is { Outgoing: { } outgoing, Plan: { } ran })
        {
            _riding = null;
            StartRehearsal(outgoing, ran);
        }
    }

    /// <summary>
    /// The set is over. From here, <see cref="Read"/> answers every block with nothing.
    /// </summary>
    /// <param name="why">What led here, for the log. A set that ends when it should not sounds like the
    /// audio device starving, not like a set ending. So the reason has to be recoverable afterwards. An
    /// underrun does not say which of a dozen states produced it.</param>
    private void Finish(string why)
    {
        if (_finished)
            return;
        EndedBecause = why;
        _finished = true;

        // This can run on the audio thread. The write is queued to the thread pool. Said does a lock and a
        // write to the terminal when no debugger is attached, and the audio thread must not wait on either.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => Said.Line("dj", (string)state!), $"set ended: {why}");
        _riser.Stop();          // a set that ran out is not one to leave noise hanging over
        _faller.Stop();
        _booth.Left.Unload();
        _booth.Right.Unload();
        SetEnded?.Invoke();
    }

    /// <summary>How the record now playing was brought in. Used for logs and tests.</summary>
    public TransitionPlan? CurrentPlan => _current;

    /// <summary>
    /// What the booth is doing, in one reading. See <see cref="BoothView"/>.
    ///
    /// <para>This is the one door the workbench asks its questions through. Everything it needs is worked
    /// out here, next to the fields that the rules are about. It is handed out as one snapshot. The
    /// snapshot says which of the states is happening, why a changeover is not going to be made where one
    /// is not, and which controls would reach the set if they were pressed.</para>
    ///
    /// <para><b>Read the whole reading, not the fields.</b> The individual properties below are still
    /// public and still true. But each one, when asked separately, is an answer given at a different
    /// instant. The audio thread can swap decks between two of those answers, and the page would then draw
    /// half of one changeover and half of the next. So everything is read here in one pass, in an order
    /// that keeps the answer consistent: the state first, then the facts that the state was decided
    /// from.</para>
    /// </summary>
    /// <param name="paused">Whether the ENGINE is stopped. The booth cannot see this. The booth is a source
    /// that is being pumped, and a source that is not being pumped has no way to know. The owner of the
    /// player passes it in.</param>
    public BoothView Look(bool paused)
    {
        lock (_plan)
            return LookCore(paused);
    }

    private BoothView LookCore(bool paused)
    {
        // Every field the answer is made of is read once, under the booth's own lock. So this is one
        // INSTANT, not merely one pass. The audio thread cannot swap a deck halfway through. Reading twice,
        // once to choose the state and once to report it, could give a picture that says "mixing" next to a
        // changeover that had already finished when the line was written.
        bool finished = _finished;
        var live = _live;
        var cued = _booth.Other(live);
        var pending = _pending;
        bool changing = IsChangingOver;
        bool hands = _handsOn;
        bool auditioning = _auditioning;
        bool rehearsing = _rehearsing;
        bool playOut = _playOutNoMix;
        bool cueBusy = _cueing is { IsCompleted: false } || _openingLoad is { IsCompleted: false };
        bool anythingLeft = _crate.RemainingCount > 0 || pending is not null;

        // Whether a changeover with a shape to it will be made from the record playing. This is not the
        // same question as whose the decks are. Hands on the decks stop the set watching for the outro, but
        // they change nothing about the mix itself. So the controls stay live through a pause.
        bool mixIsComing = !finished && live.Track is not null && !rehearsing && !playOut && anythingLeft;

        // Where the set will act. This is worked out the same way that WatchForTheOutro works it out. It is
        // the plan's own point, when a plan is held ready. Otherwise it is where the record says a
        // changeover could begin. The overrides used are the ones the BOOTH is holding. They are not
        // necessarily the ones a window is drawing with.
        double armsAt = mixIsComing && live.Track is { } on
            ? pending?.StartAtSeconds ?? TransitionPlanner.ArmFrom(on, Overrides)
            : 0;
        double liveAt = live.PositionSeconds;

        // The needle has gone past the point, with nothing cued. What was on screen will not happen. The
        // set will choose again from wherever the record now is. Until it has, no mix is coming. Saying
        // "mixing" through this would show a changeover that has already been missed. Stopping that is the
        // purpose of this reading.
        bool missed = mixIsComing && pending is null && armsAt > 0 && liveAt > armsAt + 1;
        if (missed)
            mixIsComing = false;

        var state =
            finished ? BoothState.Over
            : live.Track is null ? BoothState.Nothing
            : changing ? BoothState.HandingOver
            : hands || auditioning || !mixIsComing ? BoothState.DeckPlaying
            : BoothState.Mixing;

        // The reason, in the order a person would want to be told them. The reasons that they caused
        // themselves come first, then what the record did, then what the crate did.
        var why =
            state is not BoothState.DeckPlaying ? NoMixBecause.None
            : hands ? NoMixBecause.HandsOnTheDecks
            : auditioning ? NoMixBecause.Auditioning
            : rehearsing ? NoMixBecause.PuttingThePairBack
            : playOut ? NoMixBecause.ScrubbedPastTheMixPoint
            : !anythingLeft ? NoMixBecause.NothingLeftInTheCrate
            : missed ? NoMixBecause.TheMomentHasPassed
            : NoMixBecause.None;

        return new BoothView
        {
            State = state,
            Why = why,
            Paused = paused,
            HandsOnTheDecks = hands,

            // Turning, not merely started. A deck that has been let go but is still counting down to its
            // bar line reports itself as running, while its needle has not moved. See
            // Deck.IsWaitingForTheBar. "Deck 1 is playing" next to a needle that is standing still is the
            // one thing this line must never say.
            LiveTurning = !paused && Turning(live),
            CuedTurning = !paused && Turning(cued),
            CuedOnAPlatter = cued.IsLoaded,
            CueThreadHasADeck = cueBusy,
            MatchingIn = _matchingIn,
            MixIsComing = mixIsComing && !changing,
            ArmsAtSeconds = armsAt,
            LiveAtSeconds = liveAt,
        };

        static bool Turning(Deck deck) =>
            deck is { IsLoaded: true, IsRunning: true, IsHeld: false, HasRunOut: false } &&
            !deck.IsWaitingForTheBar;
    }

    // The booth, with the lid off
    //
    // This section exists so that a changeover can be looked at while it is being planned. Otherwise it
    // would be guessed at afterwards from how it sounded. None of it moves anything. The DJ is driven
    // entirely from Read on the audio thread. These members are read from whatever thread a window happens
    // to be on. That is safe because each one is a single field or an immutable record. The one exception
    // is Overrides. It is written from the interface and read on the audio thread, and it is volatile by
    // reference for that reason.

    /// <summary>
    /// What a person has asked for that the planner would not have chosen. It is applied to the next
    /// changeover that this set plans.
    ///
    /// <para>It is read at the moment a changeover is worked out, which is a dozen seconds before it
    /// starts. Setting it during a changeover leaves that changeover alone. By then the plan is already
    /// frames on the audio thread, and there is no honest way to change a fade that has begun. It is
    /// cleared by <see cref="Replan"/>, or by setting it to null.</para>
    /// </summary>
    public MixOverrides? Overrides { get; set; }

    /// <summary>The changeover being set up, before it starts. This is null outside the dozen seconds that
    /// a set spends holding one ready.</summary>
    public TransitionPlan? PendingPlan => _pending;

    /// <summary>
    /// The changeover out of the record playing. This is the one that is set up, where there is one.
    /// Otherwise it is the one that this booth would make if it worked it out now.
    ///
    /// <para><b>Why this is a method on the DJ, and not arithmetic that a window does for itself.</b> A
    /// plan is only held ready for the dozen seconds before it runs. For the rest of a record, a window
    /// that worked out its own plan would have a second set of inputs. Those inputs can differ from the
    /// set's in three ways. The window could use the overrides it is showing, not the ones the booth is
    /// holding. It could plan with no luck, so the two rationed moves could never come out of it. And it
    /// could choose the next record itself, instead of asking the crate. The result is marks drawn on a
    /// record well before the point the set has in mind, with nothing to say that they are different
    /// pictures.</para>
    ///
    /// <para>So there is one planner call, on one set of inputs, and this is it. What comes back is what
    /// the set will do. It is null when there is nothing to draw yet, and drawing nothing is the honest
    /// answer.</para>
    /// </summary>
    /// <param name="settled">True when the answer is the changeover the set has actually set up. False
    /// while it is the one the set would make. A plan worked out now uses no luck, so it can never come
    /// back as one of the rationed moves, however likely either one is. Anything shown from it is still a
    /// guess, and it has to say so.</param>
    public TransitionPlan? WouldPlan(out bool settled)
    {
        lock (_plan)
        {
            settled = _pending is not null;
            if (_pending is { } set)
                return set;

            if (_finished || IsChangingOver || _rehearsing || _live.Track is not { } playing)
                return null;

            string[] rejected;
            lock (_rejected) rejected = [.. _rejected];

            // The crate's own answer, not a guess at it. The record stays where it is.
            var next = _crate.PeekNext(
                playing, _live.PlayingBpm, _live.CurrentIntensity, rejected);
            if (next is null)
                return null;                     // last record of the set: nothing follows it

            // A record with no grid can have a changeover too. The cut is what comes back for one. That
            // changeover is a real choice between a cut and an echo out. See
            // TransitionPlanner.EchoOutChance. The choice is made from the grid of the record PLAYING,
            // which is always there. So it is shown.
            //
            // It is not settled, though. The record is very likely still being read. When its reading
            // lands, this becomes a beat match instead. The second deck says "Analysing" for exactly that
            // reason. The next frame shows whatever the answer has become.

            // This is the same call that PickAndCueNext makes, with the booth's own overrides, not a
            // window's.
            var asked = Overrides is { IsEmpty: false } ? Overrides : null;
            try
            {
                // This is the same luck that the changeover will really be decided with. See _luck. Without
                // it, the two rationed moves could never show up in the picture, however likely they were.
                // The echo out that replaces a cut one time in four would then be a surprise every time it
                // happened.
                return _playOutNoMix
                    ? TransitionPlanner.PlayOut(playing, next)
                    : TransitionPlanner.Plan(playing, _live.PlayingBpm, next, asked, luck: _luck);
            }
            catch
            {
                return null;                     // a picture is never worth an exception
            }
        }
    }

    /// <summary>Where the record playing is, in its own seconds. This is the deck's own clock, not the one
    /// the interface follows. During a changeover the interface follows the record coming in.</summary>
    public double LivePositionSeconds => _live.PositionSeconds;

    /// <summary>Where to draw the live deck's needle. See <see cref="Deck.NeedleSeconds"/>. This is the
    /// same number as <see cref="LivePositionSeconds"/>, except during a roll. In a roll it is the point
    /// inside the looped bar, not the file position that the loop leaves standing still.</summary>
    public double LiveNeedleSeconds => _live.NeedleSeconds;

    /// <summary>The same, for the deck that the record is coming in on.</summary>
    public double CuedNeedleSeconds => _booth.Other(_live).NeedleSeconds;

    /// <summary>The record on the live deck. This is not the same as <see cref="NowPlaying"/> once a mix
    /// has passed its halfway mark.</summary>
    public MixTrack? LiveTrack => _live.Track;

    /// <summary>How far the live deck is pulled off the record's own speed. A duration of output has to be
    /// scaled by this to become a duration of the record. See the lag correction in the mix lab.</summary>
    public double LiveTempoRatio => _live.TempoRatio;

    /// <summary>The record cued on the other deck, waiting for its changeover.</summary>
    public MixTrack? CuedTrack => _booth.Other(_live).Track;

    /// <summary>Where the needle is on the cued record.</summary>
    public double CuedPositionSeconds => _booth.Other(_live).PositionSeconds;

    /// <summary>
    /// The tempo that the cued record is running at, with its own pull included. This is the same reading
    /// as <see cref="PlayingBpm"/>, for the other deck.
    ///
    /// <para>It is worth showing beside the live deck's tempo. During a changeover the two are pulled
    /// towards each other. Whether they have met is what a DJ watches.</para>
    /// </summary>
    public double CuedBpm => _booth.Other(_live).PlayingBpm;

    /// <summary>How far the cued deck is pulled off its own speed, as a ratio.</summary>
    public double CuedTempoRatio => _booth.Other(_live).TempoRatio;

    /// <summary>
    /// The three knobs on each deck. See <see cref="DeckEq"/>. These are read, not written. The changeover
    /// turns them, and a hand reaching in from outside during a bass swap would be fighting it for the same
    /// knob.
    ///
    /// <para>This is worth showing, because the bass swap is the one move in a changeover with nothing in
    /// the waveform to see. The faders are drawn, and the tempos are drawn. But the bottom end changing
    /// hands is what makes a mix different from a crossfade. Until now that happened where nobody could
    /// watch it.</para>
    /// </summary>
    public DeckEq LiveEq => _live.Eq;

    /// <inheritdoc cref="LiveEq"/>
    public DeckEq CuedEq => _booth.Other(_live).Eq;

    /// <summary>The position of each deck's own fader, from 0 to 1. This is not a crossfader position.
    /// There is no crossfader here. There are two faders, and a changeover moves both of them.</summary>
    public double LiveFader => _live.Fader;

    /// <inheritdoc cref="LiveFader"/>
    public double CuedFader => _booth.Other(_live).Fader;

    /// <summary>How far through the changeover the set is, from 0 to 1. This is zero when none is running,
    /// or when one is still waiting for its bar line.</summary>
    public double ChangeoverProgress =>
        _fading && _fadeLength > 0 && _fadeFrames > 0
            ? Math.Clamp((double)_fadeFrames / _fadeLength, 0, 1)
            : 0;

    /// <summary>True while the record playing is being eased onto the tempo that the changeover will run
    /// at. This happens before any of the changeover itself.</summary>
    public bool IsMatchingIn => _matchingIn;

    /// <summary>What is left in the crate, in the order the DJ would take it.</summary>
    public IReadOnlyList<MixTrack> Remaining => _crate.Remaining;

    /// <summary>
    /// Turns down the record cued for the next changeover. The record goes back in the crate, the choice is
    /// made again, and this record is passed over for as long as that choice stands.
    ///
    /// <para>This is what a DJ does with a record that has been pulled out and is not wanted. The rejection
    /// lasts as long as the changeover it was about. Once a changeover has actually happened, the list is
    /// emptied. The question at the next outro is a different question, and the record may well be the
    /// right answer to it.</para>
    ///
    /// <para>This is refused during a changeover. By then the record is no longer a choice. It is frames on
    /// the audio thread.</para>
    /// </summary>
    /// <returns>False when there is nothing cued to turn down, or when a changeover is under way.</returns>
    public bool RejectCued()
    {
        lock (_plan)
            return _pending is { } turned && RejectCore(turned.Incoming);
    }

    /// <summary>
    /// Turns down a record chosen to come next, whether or not it has reached a deck yet.
    ///
    /// <para>The two are different moments, about a dozen seconds apart. A record is <em>chosen</em> as
    /// soon as anything asks what comes next. The workbench does this from the moment a set starts, and
    /// shows the result on the second deck. The record is only <em>cued</em>, on a real deck with a decoder
    /// open on it, shortly before the changeover. A key that could only turn down a cued record would do
    /// nothing for most of every record.</para>
    /// </summary>
    public bool Reject(MixTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_plan)
            return RejectCore(track);
    }

    private bool RejectCore(MixTrack track)
    {
        if (_finished || IsChangingOver)
            return false;

        bool cued = _pending is { } pending &&
                    string.Equals(pending.Incoming.Path, track.Path, StringComparison.Ordinal);

        // Not while its decoder is still opening. The free deck belongs to the cue thread until that is
        // done.
        if (cued && _cueing is not (null or { IsCompleted: true }))
            return false;

        lock (_rejected)
        {
            if (!_rejected.Add(track.Path))
                return false;                  // already turned down; nothing to do again
        }

        if (!cued)
        {
            // Chosen but not yet on a deck. Nothing to take off and nothing to put back. The record is
            // still in the crate, and the next time anything asks what comes next it will be passed over.
            NoteNextReady();
            return true;
        }

        if (_auditioning)
            AuditionCuedCore(false);

        // Put the record at the back of the crate, not the front. The front is for a record the DJ still
        // wants but cannot use here. This record has just been turned down.
        _crate.PutBack(track, atFront: false);

        _pending = null;
        _pendingWasSkip = false;
        _cueing = null;
        _matchingIn = false;
        _riding = null;
        _booth.Other(_live).Unload();
        NoteNextReady();
        return true;
    }

    /// <summary>The records turned down for the changeover that is being set up, in the order they were
    /// turned down. This is almost always empty.</summary>
    public IReadOnlyList<string> Rejected
    {
        get { lock (_rejected) return [.. _rejected]; }
    }

    /// <summary>
    /// Plays the same changeover over and over. When one finishes, the pair go back on the decks a phrase
    /// before it, and the set stops, ready to run it again.
    ///
    /// <para>This is for editing. A changeover happens about once every few minutes and lasts sixteen bars.
    /// Judging an edit to one would mean waiting out the record between attempts, and that makes it easy to
    /// tune a mix from memory. With this on, the same sixteen bars can be heard as many times as
    /// needed.</para>
    ///
    /// <para>It applies to the next key too. Whatever changeover has just run is the one that is put
    /// back.</para>
    /// </summary>
    public bool RehearseChangeover { get; set; }

    /// <summary>
    /// Raised on the audio thread once the pair are back on the decks and the set is standing at the
    /// run-up, waiting to be played again.
    ///
    /// <para>Stopping is up to the listener. The DJ has no transport of its own. It is a source that the
    /// engine pumps. So it can put the needle back and report that it has done so. Pausing belongs to the
    /// engine. Making the source hold its own breath as well would mean two pauses to undo.</para>
    /// </summary>
    public event Action? ChangeoverRehearsed;

    /// <summary>True from the end of a rehearsed changeover until the set is played again.</summary>
    public bool IsRehearsing => _rehearsing;

    /// <summary>
    /// How much record is left in front of the changeover when it is put back. This is eight bars. That is
    /// enough for the ear to have the groove before the mix starts, because a changeover judged from a
    /// standing start is judged against nothing. It is also enough room for the next record to be cued and
    /// its tempo ridden on. Both of those have to happen before the fade.
    /// </summary>
    public const int RehearseLeadInBars = 8;

    /// <summary>
    /// Puts the pair back for another run at the changeover that has just finished. The record that arrived
    /// goes back in the crate. The one it replaced goes back on a deck, a couple of phrases before the
    /// point where the changeover started.
    /// </summary>
    private void StartRehearsal(MixTrack outgoing, TransitionPlan plan)
    {
        _crate.PutBack(plan.Incoming);
        NoteNextReady();

        double bar = plan.BarSeconds > 0 ? plan.BarSeconds : 2;

        // Never less than the cue-ahead. The changeover cannot happen until the next record is on the other
        // deck. That record is put there twelve seconds out. A run-up shorter than that would reach the mix
        // point with nothing to mix into, and the set would simply play on past it.
        double lead = Math.Max(RehearseLeadInBars * bar, CueAheadSeconds + 2 * bar);
        double at = Math.Max(0, plan.StartAtSeconds - lead);
        if (outgoing.Analysis is { } grid)
            at = Math.Max(0, grid.PhraseAtOrBefore(at));

        var deck = _live;
        _rehearsing = true;
        _rehearsalLoad = _cue(() =>
        {
            deck.Unload();
            deck.Load(outgoing, at, 1);
            deck.Prime();
            deck.Fader = 1;
            deck.Eq.SetLow(0);
            deck.Start();
        });

        // The record on the deck is the one the room was listening to before the changeover. The changeover
        // it came in on is not the one that brought it here now.
        _current = TransitionPlanner.Opening(outgoing) with { CueInSeconds = at };
        _playOutNoMix = false;
        _skipRequested = false;

        TrackChanged?.Invoke(outgoing);
        ChangeoverRehearsed?.Invoke();
    }

    /// <summary>
    /// Throws away the changeover that was being set up, and works it out again.
    ///
    /// <para>This is what an edit needs. A reading moved by hand reaches the crate and both decks through
    /// <see cref="NoteAnalysis"/>. But a changeover that is already planned was planned from the old
    /// reading. It may be cued at a point that has moved, bent onto a tempo that has changed, or timed to a
    /// bar line that is no longer there. The record goes back in the crate, and the next block plans it
    /// again from what the reading now says.</para>
    ///
    /// <para>Refused while a changeover is actually running. That is the one time when the plan has stopped
    /// being a plan and has become a count of frames. A set that is paused is not running one. The audio
    /// thread is not pumping the DJ at all, so this call is accepted, and nothing happens until play is
    /// pressed. Play is the moment it takes effect.</para>
    /// </summary>
    /// <returns>False when there was nothing to replan, or a changeover was already under way.</returns>
    public bool Replan()
    {
        lock (_plan)
            return ReplanCore();
    }

    private bool ReplanCore()
    {
        if (_finished || _fading)
            return false;

        // An edit that invalidates the plan also invalidates the audition that is playing from it. The
        // record is about to leave the deck that a hand is playing. The audition ends the normal way:
        // parked and re-seated. It is not unloaded out from under the flag. That would leave the booth
        // auditioning nothing, and the set would hold its breath for ever.
        if (_auditioning)
            AuditionCuedCore(false);

        // Not while its decoder is still opening. The free deck belongs to the cue thread until that is
        // done. Taking the record off it now would put two threads on one deck.
        if (_cueing is not (null or { IsCompleted: true }))
            return false;

        if (_pending is not { } stale)
            return false;

        _crate.PutBack(stale.Incoming);
        _pending = null;
        _pendingWasSkip = false;
        _cueing = null;
        _matchingIn = false;
        _riding = null;
        _booth.Other(_live).Unload();
        NoteNextReady();
        return true;
    }

    // The workbench's transport
    //
    // The workbench drives the two decks one at a time. It holds one while the other plays. It puts the
    // next record on its deck early, plays it, scrubs it, and puts it back on its cue point. None of this
    // is for a listener. The app's transport is Play, Next and the scrubber. But a DJ deciding whether a
    // changeover is right needs to hear each side of it alone first. That needs a deck's worth of transport
    // for each record.

    /// <summary>True while the record coming in is being played by hand, not by the set. The set holds its
    /// breath for the duration. No changeover is armed, and the next key does nothing.</summary>
    public bool IsAuditioning => _auditioning;

    /// <summary>
    /// True while the hands have the decks and the set does not.
    ///
    /// <para>The booth has two states: the set is running, or the hands have the decks. A DJ set is the DJ
    /// working. The DJ watches the record for its outro, picks what follows, cues it, rides its tempo
    /// across, and mixes. The workbench is a different thing. A person plays one record to find the spot to
    /// leave it on, and plays the other to hear where it comes in. While that is happening, the set must do
    /// none of the above. Otherwise every touch of a deck would start a mix that nobody asked for.</para>
    ///
    /// <para>It does NOT stop the audio. Both decks play exactly as they always did. That is the point.
    /// Only the decision is different: who makes it. Auditioning the record coming in has always worked
    /// this way. See <see cref="AuditionCued"/>. This is the same state with the other deck in it
    /// too.</para>
    /// </summary>
    public bool HandsOn => _handsOn;

    private volatile bool _handsOn;

    /// <summary>
    /// Takes the decks off the set, or gives them back.
    ///
    /// <para>Giving them back puts the booth in order first. An audition ends the normal way, with the
    /// record coming in re-seated on the plan's cue point. A platter that somebody stopped is let go. A set
    /// that is handed a held deck would play silence and watch a needle that never moves.</para>
    /// </summary>
    /// <returns>False while a changeover is running. That is the one time when neither answer is available.
    /// A fade is choreography across both decks, and nobody may interrupt it.</returns>
    public bool TakeTheDecks(bool on)
    {
        lock (_plan)
            return TakeTheDecksCore(on);
    }

    private bool TakeTheDecksCore(bool on)
    {
        if (_finished || IsChangingOver)
            return false;

        if (on == _handsOn)
            return true;

        if (!on)
        {
            if (_auditioning)
                AuditionCuedCore(false);
            else
                ReseatCued();

            _booth.Left.Resume();
            _booth.Right.Resume();
        }

        _handsOn = on;
        return true;
    }

    /// <summary>
    /// Puts the record coming in back on the plan's cue point, and stops its platter there.
    ///
    /// <para>This is what the hands are given back to. The two decks show the changeover that is about to
    /// happen. A needle left where a hand dragged it is a changeover that starts from somewhere nobody
    /// planned. Unlike an audition, a drag leaves no state behind to say that it happened. So this runs
    /// whether or not the record was played.</para>
    /// </summary>
    private void ReseatCued()
    {
        // Not while its decoder is still opening. That deck belongs to the cue thread until it lands, and
        // it lands on the cue point anyway.
        if (_pending is not { } plan || _cueing is not (null or { IsCompleted: true }))
            return;

        var cued = _booth.Other(_live);
        if (!cued.IsLoaded)
            return;

        cued.Park();
        _ = _cue(() => cued.SeekTo(plan.CueInSeconds));
    }

    /// <summary>Whether this deck's platter is stopped by a hand. See <see cref="Deck.Hold"/>.</summary>
    public bool IsDeckHeld(DjDeck deck) => Pick(deck).IsHeld;

    /// <summary>Whether this deck is actually producing audio: loaded, started, and not held.</summary>
    public bool IsDeckPlaying(DjDeck deck) =>
        Pick(deck) is { IsLoaded: true, IsRunning: true, IsHeld: false, HasRunOut: false };

    /// <summary>Whether this deck's needle is actually moving. That means playing, and not still counting
    /// down to the bar line that it was started against. See <see cref="Deck.IsWaitingForTheBar"/>.
    /// Anything that reads a deck's position has to ask this rather than <see cref="IsDeckPlaying"/>.
    /// During the hold the two answers differ, and only this one is about the needle.</summary>
    public bool IsDeckTurning(DjDeck deck) =>
        IsDeckPlaying(deck) && !Pick(deck).IsWaitingForTheBar;

    /// <summary>
    /// Stops one platter, or lets it turn again, while the other plays on. The engine-level pause freezes
    /// the whole set. This is for the workbench, which listens to one record at a time.
    /// </summary>
    /// <returns>False when there is no record on that deck to hold, or when a changeover is running. A fade
    /// is choreography across both decks, and stopping half of it is not a pause.</returns>
    public bool HoldDeck(DjDeck deck, bool held)
    {
        lock (_plan)
            return HoldDeckCore(deck, held);
    }

    private bool HoldDeckCore(DjDeck deck, bool held)
    {
        if (_finished || IsChangingOver)
            return false;

        var target = Pick(deck);
        if (!target.IsLoaded || !target.IsRunning)
            return false;

        if (held)
            target.Hold();
        else
            target.Resume();
        return true;
    }

    /// <summary>
    /// Moves the needle on either deck. The live deck goes through <see cref="SeekTo"/> and all of its
    /// rules. The cued deck can only be scrubbed during an audition. Outside one, its needle belongs to the
    /// plan that cued it.
    /// </summary>
    public bool SeekDeck(DjDeck deck, double seconds)
    {
        lock (_plan)
            return SeekDeckCore(deck, seconds);
    }

    private bool SeekDeckCore(DjDeck deck, double seconds)
    {
        if (deck == DjDeck.Live)
            return SeekToCore(seconds);

        // The record coming in may only be scrubbed while a hand has it. Outside that, its needle belongs
        // to the plan that cued it.
        if (_finished || IsChangingOver || !(_auditioning || _handsOn))
            return false;

        var cued = _booth.Other(_live);
        if (cued.Track is not { } track)
            return false;

        double end = track.DurationSeconds;
        double at = end > 0 ? Math.Clamp(seconds, 0, Math.Max(0, end - 0.05)) : Math.Max(0, seconds);

        // One decoder at a time, the same as the live scrubber. Requests collapse into the newest.
        _cuedSeeks.Ask(cued, at);
        return true;
    }

    /// <summary>
    /// Asks for the next changeover to be worked out, and its record cued now, instead of a dozen seconds
    /// before the outro. The workbench calls this before an audition, because there is nothing to audition
    /// until a record is on the deck. The plan that results is the real one. It is the same choice, with
    /// the same arithmetic, made early. A real plan has a side effect that happens here too: the record
    /// playing starts easing onto the mix tempo, exactly as it would before the changeover.
    /// </summary>
    /// <returns>False when a changeover is already planned, running, or the set is over.</returns>
    public bool CueNext()
    {
        lock (_plan)
        {
            if (_finished || IsChangingOver || _pending is not null)
                return false;

            _cueNowRequested = true;
            return true;
        }
    }

    /// <summary>
    /// Starts or ends an audition of the record coming in. While the audition runs, the cued deck plays
    /// whole, with the fader up and nothing shelved off it. It answers the workbench's transport, and the
    /// set holds its breath. Ending the audition parks the deck and puts its needle back on the plan's cue
    /// point. So a changeover that eventually runs starts exactly where the plan says.
    /// </summary>
    /// <returns>False when there is nothing cued to audition, or a changeover is running.</returns>
    public bool AuditionCued(bool on)
    {
        lock (_plan)
            return AuditionCuedCore(on);
    }

    private bool AuditionCuedCore(bool on)
    {
        if (_finished || IsChangingOver)
            return false;

        var cued = _booth.Other(_live);

        if (on)
        {
            if (_auditioning)
                return true;

            // Nothing to play, or the cue thread still owns the deck: not yet.
            if (_pending is null || !cued.IsLoaded || _cueing is not (null or { IsCompleted: true }))
                return false;

            // The flag goes up before the platter starts. So the audio thread can never see the deck
            // running while it still believes the set is watching for the outro.
            _auditioning = true;
            cued.Eq.SetLow(0);
            cued.Sweep.SetCutoff(SweepFilter.OpenHz);
            cued.Fader = 1;
            cued.Start();
            return true;
        }

        if (!_auditioning)
            return true;

        // Parked first. Then the needle is re-seated off the audio thread. Only then is the set's own watch
        // let back in. A changeover armed while the needle is still moving back to its cue point would drop
        // the record from wherever the audition left it.
        double back = _pending?.CueInSeconds ?? cued.CueSeconds;
        cued.Park();
        _ = _cue(() =>
        {
            try { cued.SeekTo(back); }
            finally { _auditioning = false; }
        });
        return true;
    }

    private Deck Pick(DjDeck deck) => deck == DjDeck.Live ? _live : _booth.Other(_live);

    private volatile bool _auditioning;
    private volatile bool _cueNowRequested;

    public void Dispose()
    {
        _finished = true;
        _riding = null;
        try { _cueing?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _rideTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        // The opening record. This is for a set stopped in the second it took to put that record on. The
        // deck is about to be disposed, and the load is still running on the cue thread. Without this wait,
        // an ffmpeg process would be left running for a set that nobody is listening to.
        try { _openingLoad?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _booth.Dispose();
    }
}

/// <summary>
/// The booth's two decks, named by role rather than by side. The roles swap at every changeover. Which
/// physical deck is which is the booth's business. The workbench's two panels are "playing" and "coming
/// in", so these are the names they ask for.
/// </summary>
public enum DjDeck
{
    /// <summary>The deck the set is playing from.</summary>
    Live,

    /// <summary>The deck the next record is (or will be) cued on.</summary>
    Cued,
}
