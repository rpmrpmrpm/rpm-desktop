using RPM.Core.Audio;

namespace RPM.Core.Mixing;

/// <summary>
/// One turntable. It holds a record (<see cref="Track"/>), spins it from a cue point at whatever tempo it
/// has been pulled to, and hands the booth blocks of audio with its own fader level, bass shelf and filter
/// already applied. It can also loop a bar of what it is playing. See <see cref="RollOut"/> and <see
/// cref="RollIn"/>.
///
/// <para>A deck knows nothing about the other deck or about what is being mixed. It plays what it is given,
/// where it is told, at the speed it is set to. The <see cref="Dj"/> decides all three.</para>
/// </summary>
public sealed class Deck : IDisposable
{
    /// <summary>Names for the two decks. A DJ says "bring in the B deck", not "bring in source
    /// 1".</summary>
    public enum Side { A, B }

    private readonly int _sampleRate, _channels;
    private readonly Func<FfmpegPcmSource.Options, IPcmSource> _openSource;
    private readonly DeckEq _eq;
    private readonly SweepFilter _sweep;
    private readonly EchoTail _echo;
    private readonly object _gate = new();

    /// <summary>
    /// Makes sure only one reopen of this deck's record runs at a time.
    ///
    /// <para><see cref="SeekTo"/> and <see cref="SetTempo"/> work the same way. Each one reads where the
    /// needle is, opens a second decoder at that point OUTSIDE the lock (opening one blocks), and then
    /// swaps it in under the lock. If two of these overlap on one deck, both decoders believe they are the
    /// record. Each one read the needle position before the other moved it. So whichever swaps in second
    /// wins, and it puts the needle back where its own read said it was.</para>
    ///
    /// <para>This is reached in the cases where it matters. The tempo ride runs in the twelve seconds
    /// before a changeover, and the workbench scrubber can be dragged in those same twelve seconds. The
    /// ride's own guard (<c>_rideTask is { IsCompleted: false }</c>) only stops a second ride. This lock is
    /// held for the whole of each operation, so the one that runs second reads the position that the first
    /// one left.</para>
    ///
    /// <para>The audio thread never takes this lock, and it is never taken while <see cref="_gate"/> is
    /// held. It is always taken outside <see cref="_gate"/>. A block being mixed does not wait for a
    /// decoder to open.</para>
    /// </summary>
    private readonly object _reopenGate = new();

    private IPcmSource? _source;
    private long _framesPlayed;
    private double _fader, _faderApplied;

    /// <summary>A cued record is on the platter with the needle in place, and it does not move. Until the
    /// DJ starts it, the deck consumes no audio. Cueing happens up to twelve seconds before the changeover.
    /// A deck that played during that time (silently, with the fader down) would reach the mix twelve
    /// seconds past the cue point that was chosen for it.</summary>
    private bool _running;

    /// <summary>Frames of silence still to pass before the record is let go. This places the drop on an
    /// exact sample. Otherwise it would land on whichever 50 ms block the fade began in.</summary>
    private long _holdFrames;

    /// <summary>Audio already read from a decoder that was just swapped in by <see cref="SetTempo"/>. It
    /// waits to be played before the new decoder is read directly. It covers the gap that the swap would
    /// otherwise leave.</summary>
    private const int PrimeFrames = 8192;

    /// <summary>The chunk size used to wind a newly opened decoder forward to the needle. <see
    /// cref="MaxCatchUpPasses"/> is how many chunks that may take before the speed change is given up as
    /// impossible.</summary>
    private const int CatchUpFrames = 8192;
    private const int MaxCatchUpPasses = 400;
    private float[]? _primed;
    private int _primedFrames, _primedOffset;

    /// <summary>Where the rest of a block is read to, when the primed audio runs out part way through it.
    /// The decoder writes from the start of the buffer it is given, but this block is already partly
    /// full.</summary>
    private float[] _topUp = [];

    /// <summary>
    /// How long the old decoder keeps playing under the new one after a speed change. The two are
    /// crossfaded, not joined directly.
    ///
    /// <para>Putting the needle back at the same <em>place</em> in time is not the same as putting it back
    /// at the same <em>point in the waveform</em>. The stretch keeps the pitch by cutting the audio into
    /// grains and overlapping them. A decoder opened again lays out those grains from where it starts. So
    /// its output at a given second is the right music at the wrong phase. Joining the two directly makes a
    /// step in the waveform, and a step is a click. Six milliseconds is too short to hear as a fade. It is
    /// long enough to turn the step into a slope.</para>
    /// </summary>
    private const int BlendFrames = 256;
    private IPcmSource? _blendFrom;
    private int _blendLeft;
    private float[] _blendScratch = [];

    /// <summary>How many callers are inside <see cref="Read"/> on the current source. A decoder must never
    /// be disposed while the audio thread is reading it. Disposing it ends that read early. The deck would
    /// then report that the record has run out, and the set would jump tracks in the middle of
    /// one.</summary>
    private int _readers;

    /// <summary>Sources that a speed change swapped out, but could not dispose straight away because the
    /// audio thread was still reading them. They are closed when the record leaves the deck.</summary>
    private readonly List<IPcmSource> _retired = [];

    /// <param name="openSource">How this deck gets audio for a record. The application passes <see
    /// cref="FfmpegPcmSource"/>. Tests pass a generator. That is what makes the whole booth testable
    /// without any audio file.</param>
    public Deck(Side side, int sampleRate, int channels, Func<FfmpegPcmSource.Options, IPcmSource>? openSource = null)
    {
        Which = side;
        _sampleRate = sampleRate;
        _channels = channels;
        _openSource = openSource ?? (options => new FfmpegPcmSource(options));
        _eq = new DeckEq(sampleRate, channels);
        _sweep = new SweepFilter(sampleRate, channels);
        _echo = new EchoTail(sampleRate, channels);
    }

    public Side Which { get; }

    /// <summary>What is on the platter, or null for an empty deck.</summary>
    public MixTrack? Track { get; private set; }

    /// <summary>True while there is a record on it that has not run out.</summary>
    public bool IsLoaded => Track is not null && _source is not null;

    /// <summary>True once the record has been let go. Before that it is cued and standing still.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// True while the record has been let go but is still counting down to its bar line. See <see
    /// cref="Start"/>. As far as the set is concerned, the platter is turning. But the needle has not
    /// moved. The deck fills its blocks with silence, and its position is still the cue point.
    ///
    /// <para><see cref="IsRunning"/> becomes true the moment Start is called. So for the whole hold, which
    /// can last up to a bar (about two seconds at club tempo), a deck reports that it is playing while it
    /// emits nothing and its grid is parked on one sample. Anything that compares the two decks in that
    /// window is comparing a moving needle with one that is not. This property tells them apart.</para>
    /// </summary>
    public bool IsWaitingForTheBar { get { lock (_gate) return _running && _holdFrames > 0; } }

    /// <summary>True while the deck is held: the platter is stopped with the needle down, by a
    /// hand.</summary>
    public bool IsHeld { get { lock (_gate) return _held; } }

    /// <summary>
    /// Stops the platter without lifting the needle. The deck fills its blocks with silence and the
    /// position does not move. So when the record is let go, the next sample played is the one after the
    /// last one heard. This is the per-deck pause. The engine-level pause stops the whole set. This stops
    /// one record while the other plays on.
    /// </summary>
    public void Hold() { lock (_gate) _held = true; }

    /// <summary>Lets a held platter turn again.</summary>
    public void Resume() { lock (_gate) _held = false; }

    /// <summary>
    /// Makes a running deck a cued one again. The platter stops, and the deck goes back to returning
    /// nothing from <see cref="Read"/>, as it does between <see cref="Load"/> and <see cref="Start"/>. The
    /// needle stays where it was. The caller decides where it goes next, usually with <see cref="SeekTo"/>
    /// back to the cue point that an audition moved away from.
    /// </summary>
    public void Park()
    {
        lock (_gate)
        {
            _running = false;
            _held = false;
            _holdFrames = 0;
            _fader = 0;
            _faderApplied = 0;
        }
    }

    private bool _held;

    /// <summary>
    /// Lets the record go after <paramref name="delayFrames"/> more frames of silence. This is how a
    /// changeover is beat-matched. The incoming record is held until the outgoing one reaches its bar line.
    /// So the two grids meet on the same sample, not within a block of each other.
    /// </summary>
    public void Start(long delayFrames = 0)
    {
        lock (_gate)
        {
            _running = true;
            _holdFrames = Math.Max(0, delayFrames);
        }
    }

    /// <summary>True once the record has played to its end (the source ran dry).</summary>
    public bool HasRunOut { get; private set; }

    /// <summary>Where the needle is, in seconds of the ORIGINAL record. The stretch is undone here, so a
    /// cue point measured by the analyser still means the same thing on a deck pulled 4% fast.</summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
                return CueSeconds + (double)_framesPlayed / _sampleRate * TempoRatio;
        }
    }

    /// <summary>
    /// Where the needle is for a picture to draw. This is <see cref="PositionSeconds"/>, except while a bar
    /// of the record is being looped. In that case it is the point inside that bar that the loop has
    /// reached.
    ///
    /// <para>A loop plays a copy of a stretch that the deck has already passed. So the file's own position
    /// stops for as long as the roll lasts. That is the true position of the record, but it is useless to
    /// look at. A needle parked for eight bars in the middle of the sound the room is hearing looks like a
    /// picture that has frozen. What the room hears is one bar repeating. So this shows where in that bar
    /// the loop has got to.</para>
    ///
    /// <para>This is not <see cref="PositionSeconds"/> itself, on purpose. That number is what a changeover
    /// is scheduled against: the plan's point, the next bar line, whether the record has room left. Each of
    /// those questions is about the file. This value is only used for display.</para>
    ///
    /// <para>The few milliseconds that the capture was turned by, so that its seam lands on the hit (see
    /// <see cref="TurnOntoTheHit"/>), are not undone here. At any zoom where a bar is visible, that is
    /// under one pixel. Undoing it would need a second copy of the turn.</para>
    /// </summary>
    public double NeedleSeconds
    {
        get
        {
            double at = PositionSeconds;

            // A rewind. The needle walks backwards through the bar it is dragging. This uses the same
            // arithmetic as a loop, but the read head is the point that the rip has reached, not the loop's
            // point.
            if (_spinning && _captureFrames > 0 && _loopFilled >= _captureFrames)
            {
                double dragged = (_captureFrames - Math.Clamp(_spinAt, 0, _captureFrames))
                                 / (double)_sampleRate * TempoRatio;
                return Math.Max(0, at - dragged);
            }

            // This is read without the lock. The audio thread writes these ints one at a time. A stale
            // value moves the needle by one block at most. Taking the deck's lock on every frame to be
            // exact about a picture would cost too much.
            if (!_rolling || _captureFrames <= 0 || _loopFilled < _captureFrames || _loopFrames <= 0)
                return at;

            double back = (_captureFrames - Math.Clamp(_loopAt, 0, _captureFrames))
                          / (double)_sampleRate * TempoRatio;
            return Math.Max(0, at - back);
        }
    }

    /// <summary>Where the needle was dropped, in original seconds.</summary>
    public double CueSeconds { get; private set; }

    /// <summary>How fast this deck runs. 1 is as recorded, and 1.04 is pulled 4% up. Pitch is kept (see
    /// <see cref="FfmpegPcmSource"/>), so a stretched deck stays in key.</summary>
    public double TempoRatio { get; private set; } = 1;

    /// <summary>
    /// The tempo the record is actually playing at, after the stretch.
    ///
    /// <para>This is read at the needle, not from the whole record. A record whose grid bends is at one
    /// tempo at the top and another at the outro (see <see cref="TrackAnalysis.BpmAt"/>). This is the
    /// number the room hears, so it is the number the other deck has to be matched to. It is also handed to
    /// <see cref="TransitionPlanner"/> as the playing tempo. The tempo both records meet at is worked out
    /// from it. A changeover planned from the table's average tempo would be planned around a tempo that
    /// the record is not playing at either end of itself.</para>
    ///
    /// <para>This uses <see cref="PositionSeconds"/> and not <see cref="NeedleSeconds"/>. The question is
    /// where the file has got to, because that is what the grid is measured against. A loop parks the
    /// needle inside one bar, but the record keeps moving through its own tempo.</para>
    /// </summary>
    public double PlayingBpm =>
        Track?.Analysis is { Bpm: > 0 } analysis
            ? analysis.BpmAt(PositionSeconds) * TempoRatio
            : (Track?.Bpm ?? 0) * TempoRatio;

    /// <summary>The channel fader, from 0 to 1. The <see cref="Crossfader"/> sets it once per block. The
    /// deck ramps to the new value across the frames of the block rather than stepping. A fader that jumps
    /// once every 50 ms can be heard moving.</summary>
    public double Fader
    {
        get => _fader;
        set => _fader = Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// The deck's three knobs: bass, mid and high. They are flat while this is the only record playing. The
    /// bass is pulled down while the other deck owns the bottom end. They are also set for whatever else a
    /// changeover or a hand asks for. The bass swap is still the most common use. See <see cref="DeckEq"/>.
    /// </summary>
    public DeckEq Eq => _eq;

    /// <summary>The deck's low pass. It is swept down over a roll-out, to take the top off the record
    /// before the bottom. It does nothing while it is open, which is at all other times.</summary>
    public SweepFilter Sweep => _sweep;

    /// <summary>
    /// The deck's echo, applied after the fader. See <see cref="EchoTail"/>. It is silent and has no effect
    /// until a changeover sends the record into it. This is what lets the record leave while its sound
    /// stays in the room.
    /// </summary>
    public EchoTail Echo => _echo;

    // Rolling
    //
    // A slice of the record, played over and over. It is the last bar on the way out of a track, or the
    // first bar or first beat on the way into one. The deck does not seek back to it, because a decoder is
    // a pipe and a pipe cannot be rewound. But a bar is under two seconds of audio. So the deck keeps a
    // copy of it as it goes past, and plays that copy.
    //
    // A roll is a schedule, not one length. The interesting rolls get shorter as they go: half a bar, then
    // a beat, then half a beat. Each step is a prefix of the same capture. So only the longest slice is
    // kept, and every step after the first is a shorter read of it.

    private float[] _loop = [];
    private RollStep[] _steps = [];
    private int _step;                // which step of the schedule is playing
    private long _stepLeft;           // frames of it still to come
    private int _captureFrames;       // the longest slice, and so how much is kept
    private int _loopFrames;          // the slice being played now
    private int _loopFilled;          // how much has been captured
    private int _loopAt;              // where in the slice the next block starts
    private long _rollWait;           // frames to play normally before the capture starts
    private bool _rolling;
    private bool _playOnAfterLoop;    // rolling IN: the record carries on where the capture left off

    /// <summary>
    /// How far the seam of a loop may be moved to land it on the hit.
    ///
    /// <para>The grid is a ruler. <see cref="Analysis.BeatTracker"/> makes the beats it tracked into an
    /// even line before anything downstream uses them, because a mix needs that. A drummer does not play
    /// evenly. So the bar line that a loop is cut on can sit a few milliseconds away from the hi-hat, the
    /// crash or the kick that was actually struck there. A loop whose seam falls in the middle of a hit
    /// cuts off the tail of that hit and joins it to a decay. This happens every time round the
    /// loop.</para>
    ///
    /// <para>Twenty-five milliseconds. This is wider than any ruler is wrong by, and much narrower than a
    /// sixteenth note at any tempo. So the seam can never be moved onto the wrong hit.</para>
    /// </summary>
    private const double NudgeSeconds = 0.025;

    /// <summary>The working buffer for a turn. It lets a slice be rotated without a second buffer the size
    /// of the first.</summary>
    private float[] _turn = [];

    /// <summary>True while this deck is rolling, rather than simply playing the record.</summary>
    public bool IsLooping { get { lock (_gate) return _rolling; } }

    /// <summary>
    /// Rolls the record out on <paramref name="steps"/>. It plays normally for <paramref
    /// name="afterFrames"/>, which is the wait for the next bar line. Then it captures the longest slice
    /// the schedule asks for, and plays the schedule out. After that the deck falls silent, and the record
    /// is finished with.
    ///
    /// <para>The deck only makes the sound. The fader and the filter that go over it are ridden by the <see
    /// cref="Dj"/> one block at a time, exactly as in a crossfade. So the roll cannot drift from the audio
    /// it is rolling.</para>
    /// </summary>
    public void RollOut(IReadOnlyList<RollStep> steps, long afterFrames) =>
        Roll(steps, afterFrames, playOn: false);

    /// <summary>
    /// Rolls the record in. It captures while it plays, repeats the schedule, and then carries on with the
    /// record from where the capture stopped. So a record looped in on its first bar plays its second bar
    /// next, not its fifth.
    /// </summary>
    /// <param name="captureFrames">
    /// How much of the record to take before the looping starts. This only matters when it must be more
    /// than the longest slice the schedule asks for. Zero takes exactly the longest slice, which is what a
    /// roll going out wants.
    ///
    /// <para>The capture does two jobs. It is the material that every step loops a prefix of. It is also,
    /// for a roll that plays on, how much of the record the roll uses up, because the deck carries on from
    /// where the capture stopped. These two need different lengths. A beat roll loops one beat. If the
    /// record then plays on from there, it carries on from its second beat, three beats after the downbeat
    /// that the whole changeover was built to land on. Capturing a bar and looping a beat of it gives both.
    /// The roll sounds as tight as it did, and the record still comes out on a bar line.</para>
    /// </param>
    public void RollIn(IReadOnlyList<RollStep> steps, long captureFrames = 0) =>
        Roll(steps, afterFrames: 0, playOn: true, captureFrames);

    // Rewind
    //
    // The other thing a captured bar is good for. A roll plays the slice forwards, over and over. A
    // spinback plays it once, backwards, starting fast and slowing to a stop. That is what a hand pushing a
    // platter the wrong way sounds like. This is why the pitch falls as it goes, instead of staying where
    // the record was. Nothing here keeps the pitch, on purpose. A spinback that kept its key would not
    // sound like a spinback.

    private bool _spinning;
    private long _spinLeft;      // frames of the rip still to produce
    private long _spinLength;    // how long the whole rip is
    private double _spinAt;      // where in the capture it has got back to, in frames

    /// <summary>
    /// How hard the platter is pushed. The rip moves back through the capture as <c>(1 - u)^Drag</c>. At
    /// <c>Drag</c> = 2.5 it starts at two and a half times speed and comes to a standstill. Most of the bar
    /// is crossed in the first third of the gesture. A real push also spends most of its speed early.
    /// </summary>
    private const double Drag = 2.5;

    /// <summary>The last part of the rip is faded out. By the end, a record that has been spun to a halt
    /// keeps reading the same sample. That makes a DC step when it stops. A twelfth of the gesture is too
    /// short to hear as a fade, and long enough to turn that step into a slope.</summary>
    private const double SpinTail = 0.08;

    /// <summary>True while this deck is being pulled backwards.</summary>
    public bool IsSpinning { get { lock (_gate) return _spinning; } }

    /// <summary>
    /// The rewind. It plays normally for <paramref name="afterFrames"/>, which is the wait for the bar
    /// line. While it plays, it keeps a copy of the next <paramref name="captureFrames"/>. Then it rips
    /// back through that copy over <paramref name="spinFrames"/>, slowing as it goes. After that the deck
    /// falls silent, and the record is finished with.
    ///
    /// <para>The bar is heard before it is pulled back. That is the whole shape of the gesture. The phrase
    /// finishes, and then the room hears the last of it dragged away.</para>
    /// </summary>
    public void SpinBack(long afterFrames, long captureFrames, long spinFrames)
    {
        int room = int.MaxValue / Math.Max(1, _channels);
        int capture = (int)Math.Clamp(captureFrames, 1, room);

        lock (_gate)
        {
            if (_loop.Length < capture * _channels)
                _loop = new float[capture * _channels];

            ResetRoll();
            _captureFrames = capture;
            _loopFilled = 0;
            _rollWait = Math.Max(0, afterFrames);
            _spinLength = Math.Max(1, spinFrames);
            _spinLeft = _spinLength;
            _spinAt = capture;
            _spinning = true;
        }
    }

    /// <summary>
    /// Fills the block from a rewind. First the record itself, until the bar line. Then the bar being kept
    /// as it plays. Then that bar backwards and slowing. Then silence.
    /// </summary>
    private int SpinInto(float[] buffer, int offset, int frames, IPcmSource? source)
    {
        int done = 0;

        while (done < frames)
        {
            int want = frames - done;
            long wait;
            lock (_gate) wait = _rollWait;

            // Before the bar line the record plays untouched. So the gesture starts where the record's own
            // bar starts, not where the changeover was armed.
            if (wait > 0)
            {
                int take = (int)Math.Min(want, wait);
                int got = source is null ? 0 : Decode(buffer, offset + done, take, source);
                if (got <= 0) { RanOutMidSpin(); continue; }
                lock (_gate) _rollWait -= got;
                done += got;
                continue;
            }

            // The bar that is about to be pulled back. It is played and kept at the same time.
            if (_loopFilled < _captureFrames)
            {
                int take = Math.Min(want, _captureFrames - _loopFilled);
                int got = source is null ? 0 : Decode(buffer, offset + done, take, source);
                if (got <= 0) { RanOutMidSpin(); continue; }

                Array.Copy(
                    buffer, (offset + done) * _channels,
                    _loop, _loopFilled * _channels, got * _channels);
                _loopFilled += got;
                done += got;
                continue;
            }

            long left;
            lock (_gate) left = _spinLeft;

            // The gesture is over. The rest of the block is silence, not a short read. So the changeover
            // can land on a sample of its own choosing.
            if (left <= 0)
            {
                Array.Clear(buffer, (offset + done) * _channels, want * _channels);
                done += want;
                break;
            }

            int rip = (int)Math.Min(want, left);
            Rip(buffer, offset + done, rip);
            done += rip;
        }

        return done;
    }

    /// <summary>
    /// One block of the rip. The captured bar is read backwards, at a speed that falls to nothing. Samples
    /// between two frames are interpolated. The read head is almost always between two frames. Stepping to
    /// the nearest frame instead is the grit that makes a cheap reverse sound cheap.
    /// </summary>
    private void Rip(float[] buffer, int at, int frames)
    {
        int capture, channels = _channels;
        long left, length;
        lock (_gate) { capture = _captureFrames; left = _spinLeft; length = _spinLength; }

        // Nothing was captured. The record ended before the bar line came round. The gesture still runs its
        // full length in silence. The record on the other deck has already been held for exactly that many
        // frames.
        if (capture <= 0)
        {
            Array.Clear(buffer, at * channels, frames * channels);
            lock (_gate) _spinLeft = Math.Max(0, left - frames);
            return;
        }

        double last = 0;
        for (int f = 0; f < frames; f++)
        {
            double u = Math.Clamp(1 - (double)(left - f) / length, 0, 1);
            double pos = capture * Math.Pow(1 - u, Drag);

            int a = Math.Clamp((int)pos, 0, capture - 1);
            int b = Math.Min(a + 1, capture - 1);
            var share = (float)(pos - a);

            // The very end of the gesture. The platter has stopped, and the read head sits on one sample.
            // The level is faded to zero here, instead of leaving a step.
            var gain = (float)(u > 1 - SpinTail ? Math.Max(0, (1 - u) / SpinTail) : 1);

            int i = (at + f) * channels;
            for (int c = 0; c < channels; c++)
            {
                float one = _loop[a * channels + c], two = _loop[b * channels + c];
                buffer[i + c] = (one + (two - one) * share) * gain;
            }

            last = pos;
        }

        lock (_gate)
        {
            _spinLeft = Math.Max(0, left - frames);
            _spinAt = last;
        }
    }

    /// <summary>The record ended before the bar it was to be pulled back through was complete. Whatever was
    /// captured is what gets ripped, and the gesture still runs its full length.</summary>
    private void RanOutMidSpin()
    {
        lock (_gate)
        {
            _rollWait = 0;
            _captureFrames = _loopFilled;
            _spinAt = _captureFrames;
        }
    }

    private void Roll(IReadOnlyList<RollStep> steps, long afterFrames, bool playOn, long captureFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(steps);

        // A schedule can only be played if every slice is a prefix of the first one. That is what lets one
        // capture serve all of them.
        var wanted = steps
            .Where(step => step.SliceFrames > 0 && step.LengthFrames > 0)
            .ToArray();

        if (wanted.Length == 0)
            return;

        int room = int.MaxValue / Math.Max(1, _channels);
        int longest = (int)Math.Min(wanted.Max(step => step.SliceFrames), room);

        // Never less than the longest slice, because a step cannot read more of the record than was taken.
        // Never more than the first step. The capture plays as it is taken, and it must be complete before
        // the looping that it feeds can start.
        int capture = longest;
        if (captureFrames > capture)
            capture = (int)Math.Min(Math.Min(captureFrames, wanted[0].LengthFrames), room);
        if (capture < longest)
            capture = longest;

        lock (_gate)
        {
            if (_loop.Length < capture * _channels)
                _loop = new float[capture * _channels];

            _steps = wanted;
            _step = 0;
            _stepLeft = wanted[0].LengthFrames;
            _captureFrames = capture;
            _loopFrames = (int)Math.Min(wanted[0].SliceFrames, capture);
            _loopFilled = 0;
            _loopAt = 0;
            _rollWait = Math.Max(0, afterFrames);
            _playOnAfterLoop = playOn;
            _rolling = true;
        }
    }

    /// <summary>
    /// Removes every trace of a roll: the schedule, the capture's bookkeeping, and the wait for the bar. It
    /// is one method because it is one state. The fields span nine variables. If a tenth is added, it must
    /// be cleared everywhere a roll can be abandoned (a load, a seek, an unload). Otherwise a deck that is
    /// sought in the middle of a roll replays a bar of the previous record. Callers hold whatever lock they
    /// already hold. The caller is responsible for guarding the fields.
    /// </summary>
    private void ResetRoll()
    {
        _rolling = false;
        _playOnAfterLoop = false;
        _rollWait = _stepLeft = 0;
        _steps = [];
        _step = 0;
        _captureFrames = _loopFrames = _loopFilled = _loopAt = 0;

        // The rewind has its own three fields. It is captured from the same buffer and waits for the same
        // bar line. So it is cleared in the same place, and for the same reason.
        _spinning = false;
        _spinLeft = _spinLength = 0;
        _spinAt = 0;
    }

    /// <summary>How many frames of the roll are left to play.</summary>
    public long LoopFramesLeft
    {
        get
        {
            lock (_gate)
            {
                if (!_rolling)
                    return 0;
                long left = _rollWait + _stepLeft;
                for (int i = _step + 1; i < _steps.Length; i++)
                    left += _steps[i].LengthFrames;
                return left;
            }
        }
    }

    /// <summary>
    /// Puts a record on. It opens at <paramref name="cueSeconds"/>, stretched by
    /// <paramref name="tempoRatio"/>, with the fader down. Whatever was on the deck is taken off.
    /// </summary>
    public void Load(MixTrack track, double cueSeconds, double tempoRatio)
    {
        ArgumentNullException.ThrowIfNull(track);
        Unload();

        Track = track;
        CueSeconds = Math.Max(0, cueSeconds);
        TempoRatio = Math.Clamp(tempoRatio, 0.5, 2.0);
        HasRunOut = false;
        _running = false;
        _held = false;
        _holdFrames = 0;
        _primed = null;
        _primedFrames = _primedOffset = 0;
        _framesPlayed = 0;
        _fader = 0;
        _faderApplied = 0;
        _eq.Reset();
        _eq.SetGains(0, 0, 0);
        _sweep.Reset();
        _echo.Stop();
        ResetRoll();

        _source = _openSource(new FfmpegPcmSource.Options
        {
            Source = track.Source,
            SampleRate = _sampleRate,
            Channels = _channels,
            StartSeconds = CueSeconds,
            TempoRatio = TempoRatio,
            Label = track.Display,
        });

        // The roll's capture buffer is sized here, on the cue thread. A roll is cut to a bar: the mix's bar
        // or the record's own, whichever the plan chose. Allocating a buffer of nearly a megabyte inside
        // the audio callback would make the collector pause on exactly the sample where a changeover
        // starts. A bar and a half of this record covers every bar that a plan can cut. A deck that rolls
        // twice reuses the same buffer.
        if (track.Bpm > 0)
        {
            double barSeconds = Math.Max(1, track.BeatsPerBar) * 60.0 / (track.Bpm * TempoRatio);
            long generous = (long)Math.Ceiling(barSeconds * 1.5 * _sampleRate) * _channels;
            if (generous > 0 && generous <= 16 * 1024 * 1024 && _loop.Length < generous)
                _loop = new float[generous];
        }
    }

    /// <summary>Plays out the audio pulled during a speed change, before the new decoder is read directly.
    /// Returns -1 when there is none waiting.</summary>
    private int TakePrimed(float[] buffer, int offset, int frames)
    {
        lock (_gate)
        {
            if (_primed is not { } primed || _primedOffset >= _primedFrames)
                return -1;

            int take = Math.Min(frames, _primedFrames - _primedOffset);
            Array.Copy(primed, _primedOffset * _channels, buffer, offset * _channels, take * _channels);
            _primedOffset += take;
            if (_primedOffset >= _primedFrames)
                _primed = null;
            return take;
        }
    }

    /// <summary>
    /// Picks the needle up and puts it down somewhere else on the same record. This is the scrubber.
    ///
    /// <para>It works like <see cref="SetTempo"/>, and for the same reason. The decoder is opened at the
    /// new place and read from before the old one is let go. So the deck never waits for a process to
    /// start. The source it was reading is retired, not killed under the audio thread. The last few
    /// milliseconds of where the record was are faded out under where it now is. Two unrelated points in a
    /// waveform joined directly make a click.</para>
    ///
    /// <para>Called from the cue thread, never the audio thread, because opening a decoder blocks.</para>
    /// </summary>
    /// <returns>False when the record could not be opened there. In that case the needle has not moved, and
    /// the deck plays on from where it was.</returns>
    public bool SeekTo(double seconds)
    {
        lock (_reopenGate)
            return SeekToCore(seconds);
    }

    private bool SeekToCore(double seconds)
    {
        MixTrack? track;
        double ratio;
        lock (_gate)
        {
            track = Track;
            if (track is null || _source is null)
                return false;
            ratio = TempoRatio;
        }

        double at = Math.Max(0, seconds);

        var fresh = _openSource(new FfmpegPcmSource.Options
        {
            Source = track.Source,
            SampleRate = _sampleRate,
            Channels = _channels,
            StartSeconds = at,
            TempoRatio = ratio,
            Label = track.Display,
        });

        // The first block is pulled here, not on the audio thread, the same way a cue does it.
        var primed = new float[PrimeFrames * _channels];
        int ready = fresh.Read(primed, PrimeFrames);
        if (ready <= 0)
        {
            fresh.Dispose();          // nothing there (past the end, or the file would not reopen)
            return false;
        }

        IPcmSource? old;
        lock (_gate)
        {
            if (!ReferenceEquals(Track, track) || _source is null)
            {
                fresh.Dispose();      // the record changed while the decoder was opening
                return false;
            }

            _primed = primed;
            _primedFrames = ready;
            _primedOffset = 0;
            CueSeconds = at;
            _framesPlayed = 0;
            HasRunOut = false;        // a record sought back from its end has plenty left again

            // A roll cut from somewhere else is not this record any more.
            ResetRoll();

            old = _source;
            _source = fresh;

            if (_blendFrom is null)
            {
                _blendFrom = old;
                _blendLeft = BlendFrames;
                old = null;
            }
        }

        RetireSource(old);
        return true;
    }

    /// <summary>
    /// Changes the speed of the record while it is playing. This is riding the pitch fader. The needle
    /// stays where it is. The decoder is reopened at the current position with the new stretch, and the new
    /// one is read from only once it has audio ready, so the swap leaves no gap.
    ///
    /// <para>Called from the cue thread, never the audio thread, because opening a decoder blocks. It is
    /// done during the twelve seconds before a changeover for exactly that reason.</para>
    /// </summary>
    public void SetTempo(double ratio)
    {
        lock (_reopenGate)
            SetTempoCore(ratio);
    }

    private void SetTempoCore(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.5, 2.0);
        MixTrack? track;
        double at;
        long playedAtOpen;
        double ratioAtOpen;
        lock (_gate)
        {
            track = Track;

            // The same floor as the decoder uses, for the same reason. A change of a twentieth of a percent
            // that is refused here is a twentieth of a percent that the planner has already counted on.
            // This is the last step of a ride, where the deck is put exactly on the tempo that the
            // changeover was worked out for. Refusing it because it is nearly there is how a changeover
            // ends up nearly beat-matched.
            if (track is null || _source is null ||
                Math.Abs(ratio - TempoRatio) < Audio.FfmpegPcmSource.UnstretchedRatio)
                return;
            playedAtOpen = _framesPlayed;
            ratioAtOpen = TempoRatio;
            at = CueSeconds + (double)playedAtOpen / _sampleRate * ratioAtOpen;
        }

        var fresh = _openSource(new FfmpegPcmSource.Options
        {
            Source = track.Source,
            SampleRate = _sampleRate,
            Channels = _channels,
            StartSeconds = at,
            TempoRatio = ratio,
            Label = track.Display,
        });

        // The record kept playing while that decoder was opening, and the new one was opened at the place
        // where the needle was. So wind it forward to where the needle is now. Otherwise its first samples
        // would replay what was heard in between. That repeated sound is the flam that gives a speed change
        // away.
        long freshRead = 0;
        var scratch = new float[CatchUpFrames * _channels];
        for (int pass = 0; pass < MaxCatchUpPasses; pass++)
        {
            long behind = WantedFrames(playedAtOpen, ratioAtOpen, ratio, at) - freshRead;
            if (behind <= 0)
                break;

            int n = fresh.Read(scratch, (int)Math.Min(CatchUpFrames, behind));
            if (n <= 0)
            {
                fresh.Dispose();   // ran off the end of the record while catching up. Keep the old speed.
                return;
            }
            freshRead += n;
        }

        // Prime the next block, so that the audio thread never waits for a decoder at all.
        var primed = new float[PrimeFrames * _channels];
        int ready = fresh.Read(primed, PrimeFrames);
        if (ready <= 0)
        {
            fresh.Dispose();       // could not reopen. Stay on the old speed rather than break the record.
            return;
        }
        long primedStart = freshRead;
        freshRead += ready;

        IPcmSource? old;
        lock (_gate)
        {
            if (!ReferenceEquals(Track, track) || _source is null)
            {
                fresh.Dispose();   // the record changed while the decoder was opening
                return;
            }

            // Where the needle is at this instant, as an offset into what was primed.
            long offset = WantedFrames(playedAtOpen, ratioAtOpen, ratio, at) - primedStart;
            if (offset < 0)
                offset = 0;

            if (offset >= ready)
            {
                // It moved again while the lock was being taken. A block of hesitation is better than a
                // block of the record played twice.
                _primed = null;
                _primedFrames = _primedOffset = 0;
                CueSeconds = at + freshRead * ratio / _sampleRate;
            }
            else
            {
                _primed = primed;
                _primedFrames = ready;
                _primedOffset = (int)offset;
                CueSeconds = at + (primedStart + offset) * ratio / _sampleRate;
            }

            old = _source;
            _source = fresh;
            TempoRatio = ratio;
            _framesPlayed = 0;

            // The old decoder is kept for a few milliseconds, so that the swap can be crossfaded and not
            // joined directly. From here on only the audio thread reads it, so nothing else can be inside
            // it. A ride of the pitch fader makes a dozen of these steps in a row. If one in five left a
            // click, a mix would crackle its way back up to tempo.
            if (_blendFrom is null)
            {
                _blendFrom = old;
                _blendLeft = BlendFrames;
                old = null;
            }
        }

        RetireSource(old);      // only when a blend was already in progress. That blend is the one that fades.
    }

    /// <summary>How many frames the new decoder should have produced by now to be level with the needle.
    /// This is the record-seconds played since the swap began, converted to frames at the new
    /// speed.</summary>
    private long WantedFrames(long playedAtOpen, double ratioAtOpen, double newRatio, double at)
    {
        long played;
        lock (_gate) played = _framesPlayed;
        double consumed = (played - playedAtOpen) / (double)_sampleRate * ratioAtOpen;
        return (long)Math.Round(consumed * _sampleRate / newRatio);
    }

    /// <summary>
    /// Fades out the decoder that a speed change replaced, across the first few milliseconds after the
    /// swap. Without this, the two streams are joined directly at whatever value each one has. That step is
    /// the click.
    ///
    /// <para>Runs on the audio thread, on audio the deck has just produced. It runs before the shelf and
    /// the fader touch that audio. Once the old decoder is handed over, it is read here and nowhere else.
    /// So there is nothing to race with.</para>
    /// </summary>
    private void BlendOutTheOldDecoder(float[] buffer, int offset, int frames)
    {
        if (_blendFrom is null || _blendLeft <= 0 || frames <= 0)
            return;

        int take = Math.Min(frames, _blendLeft);
        if (_blendScratch.Length < take * _channels)
            _blendScratch = new float[take * _channels];

        int got = _blendFrom.Read(_blendScratch, take);
        if (got > 0)
        {
            // The two gains add up to one, not to one in power. The two streams are the same music. Where
            // they agree, they add together directly. Equal-power curves would sum to 1.4 in the middle,
            // and that trades the click for a thump.
            //
            // A raised cosine is used, not a straight line. A straight fade is continuous, but its slope is
            // not. It has a corner where the crossfade starts and another where it ends. A corner in the
            // gain is a corner in the waveform, which is heard as a smaller version of the click. The
            // raised cosine keeps the level steady at both ends and does its moving in the middle.
            int done = BlendFrames - _blendLeft;
            for (int f = 0; f < got; f++)
            {
                var rising = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (done + f + 1) / BlendFrames));
                float falling = 1 - rising;
                int at = (offset + f) * _channels, from = f * _channels;
                for (int c = 0; c < _channels; c++)
                    buffer[at + c] = buffer[at + c] * rising + _blendScratch[from + c] * falling;
            }
            _blendLeft -= got;
        }
        else
        {
            _blendLeft = 0;          // the old decoder has nothing left. The new one carries on alone.
        }

        if (_blendLeft > 0)
            return;

        var finished = _blendFrom;
        _blendFrom = null;
        CloseLater(finished);
    }

    /// <summary>
    /// Closes a decoder on a thread pool thread, not here.
    ///
    /// <para>Closing one kills an ffmpeg process. Killing a process can take long enough to miss the audio
    /// thread's deadline. Then the block being mixed arrives late, and the speakers get a gap in it. That
    /// is a crackle. It happens on exactly the actions that close decoders: pressing next (the record cued
    /// for the outro is thrown away), the end of every changeover, and every step of the pitch ride.
    /// Nothing waits for the close, so there is no reason for it to run here.</para>
    /// </summary>
    private static void CloseLater(IPcmSource? source)
    {
        if (source is null)
            return;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            try { ((IPcmSource)state!).Dispose(); } catch { }
        }, source);
    }

    /// <summary>
    /// Pulls the first block off a freshly cued record, so the audio thread never waits for a decoder that
    /// has not produced a sample yet. This is called from the cue thread. A started ffmpeg has to read and
    /// decode before it puts anything on its pipe. If the first read blocks until then, the wait is heard
    /// as a gap at the drop.
    /// </summary>
    public void Prime()
    {
        IPcmSource? source;
        lock (_gate)
        {
            source = _source;
            if (source is null || _primed is not null)
                return;
        }

        var primed = new float[PrimeFrames * _channels];
        int ready = source.Read(primed, PrimeFrames);
        if (ready <= 0)
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(_source, source) || _primed is not null)
                return;                    // the record changed during this read, so it is not ours to keep
            _primed = primed;
            _primedFrames = ready;
            _primedOffset = 0;
        }
    }

    /// <summary>
    /// Closes a source that the deck has finished with, but never while the audio thread is inside it.
    /// Disposing kills the ffmpeg behind it. A read that dies in the middle of a block looks exactly like
    /// the end of the record, and that would take the whole set with it. This waits briefly for the reader
    /// to come out. A source that is still busy after that is parked, and it is closed when the record
    /// leaves the deck.
    /// </summary>
    private void RetireSource(IPcmSource? source)
    {
        if (source is null)
            return;

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _readers) > 0 && waited.ElapsedMilliseconds < 500)
            Thread.Sleep(2);

        if (Volatile.Read(ref _readers) == 0)
        {
            CloseLater(source);
            return;
        }

        lock (_gate) _retired.Add(source);
    }

    /// <summary>
    /// The analyser has caught up with the record already playing here. This keeps the analysis, without
    /// touching the audio. Nothing about playback changes, and the needle stays where it is. From now on
    /// the DJ can see this record's outro and grid. An analysis for any other record is ignored.
    /// </summary>
    public void AdoptAnalysis(MixTrack analyzed)
    {
        ArgumentNullException.ThrowIfNull(analyzed);
        lock (_gate)
        {
            if (Track is not { } track || analyzed.Analysis is null)
                return;
            if (!string.Equals(track.Path, analyzed.Path, StringComparison.Ordinal))
                return;

            // Which readings may replace which. The background sweep never overwrites a record that the
            // deck already knows. Its answer is not newer, only later. A reading moved by a hand always
            // replaces the old one. So does the measured reading that a hand puts back. Refusing either one
            // would make every edit in the lab silently do nothing.
            bool fresh = !track.IsAnalyzed;
            bool handMade = analyzed.Analysis.IsEdited;
            bool revert = track.Analysis?.IsEdited == true;
            if (!fresh && !handMade && !revert)
                return;

            Track = track with { Analysis = analyzed.Analysis };
        }
    }

    /// <summary>Takes the record off and closes its decoder.</summary>
    public void Unload()
    {
        IPcmSource? source, blending;
        IPcmSource[] retired;
        lock (_gate)
        {
            source = _source;
            _source = null;
            blending = _blendFrom;
            _blendFrom = null;
            _blendLeft = 0;
            Track = null;
            _framesPlayed = 0;
            ResetRoll();
            retired = [.. _retired];
            _retired.Clear();
        }
        _sweep.Reset();
        _echo.Stop();
        CloseLater(source);
        CloseLater(blending);
        foreach (var stale in retired)
            CloseLater(stale);
        _fader = 0;
        _faderApplied = 0;
        lock (_gate) { _running = false; _holdFrames = 0; }
    }

    /// <summary>
    /// Reads one block off this deck into <paramref name="buffer"/>, with the bass shelf and the fader
    /// applied. Returns the frames written. Returns 0 when the deck is empty or the record has run out.
    /// Frames that the record did not fill are zeroed, so the booth can always mix a full block.
    /// </summary>
    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        long hold;
        lock (_gate)
        {
            if (!_running)
                return 0;              // cued: the needle is down, but the platter is not turning

            // Held: a full block of silence, not a short one. A short block is how a record signals that it
            // has run out. A held record has not gone anywhere. Nothing advances, not the position and not
            // the bar-line hold. So letting go picks up the exact sample where the hand stopped.
            if (_held)
            {
                if (frames > 0)
                    Array.Clear(buffer, 0, frames * _channels);
                return Math.Max(0, frames);
            }

            hold = _holdFrames;
        }
        if (frames <= 0)
            return 0;

        if (hold <= 0)
            return Play(buffer, 0, frames);

        // Waiting for the outgoing record's bar line. This is silence, and the record itself is untouched.
        // So the first sample it plays is still the one at its cue point.
        int held = (int)Math.Min(frames, hold);
        Array.Clear(buffer, 0, held * _channels);
        lock (_gate) _holdFrames -= held;

        if (held >= frames)
            return held;               // the whole block is still before the line

        // The bar line arrives part way through this block. This is the normal case, because a wait
        // measured in samples almost never lands on a 50 ms boundary. So the rest of this block is the
        // record, starting on the sample where the line fell.
        //
        // Handing the block back short would be wrong. That means silence now, and the record from the top
        // of the next block. That is the error the hold exists to prevent. It puts every drop on the block
        // edge after the bar line, up to 50 ms late. At club tempo that is a tenth of a beat. A mix that is
        // not quite together sounds like that.
        int played = Play(buffer, held, frames - held);
        return played > 0 ? held + played : held;
    }

    /// <summary>
    /// Reads <paramref name="frames"/> of the record itself into <paramref name="buffer"/>, with the bass
    /// shelf, the sweep and the fader applied.
    /// </summary>
    /// <param name="offset">Frames into <paramref name="buffer"/> to write at. Non-zero only in the block
    /// where a bar-line hold ran out part way through.</param>
    private int Play(float[] buffer, int offset, int frames)
    {
        IPcmSource? source;
        bool rolling, spinning;
        lock (_gate)
        {
            source = _source;
            rolling = _rolling;
            spinning = _spinning;
        }
        if (source is null && !rolling && !spinning)
            return 0;

        int read = spinning
            ? SpinInto(buffer, offset, frames, source)
            : rolling
                ? LoopInto(buffer, offset, frames, source)
                : Decode(buffer, offset, frames, source!);

        if (read <= 0)
            return 0;

        _eq.Process(buffer, offset, read);
        _sweep.Process(buffer, offset, read);

        int start = offset * _channels;
        double from = _faderApplied, to = _fader;
        double stepPerFrame = read > 1 ? (to - from) / (read - 1) : 0;
        for (int f = 0; f < read; f++)
        {
            var gain = (float)(from + stepPerFrame * f);
            int at = start + f * _channels;
            for (int c = 0; c < _channels; c++)
                buffer[at + c] *= gain;
        }
        _faderApplied = to;

        // The echo comes last, after the fader. This is the only place where it can go and still be an echo
        // out. See EchoTail. Shutting the channel must take the record away and leave its sound behind.
        _echo.Process(buffer, offset, read);

        return read;
    }

    /// <summary>
    /// Fills the block from a loop. The record plays normally until the bar line. Then one bar of it is
    /// captured on its way past, and that bar repeats. After that there are two cases. When rolling out,
    /// the deck goes silent, because the record is finished with. When rolling in, the rest of the record
    /// plays.
    /// </summary>
    private int LoopInto(float[] buffer, int offset, int frames, IPcmSource? source)
    {
        int done = 0;

        while (done < frames)
        {
            int want = frames - done;
            long wait, left;
            bool playOn;
            lock (_gate) { wait = _rollWait; left = _stepLeft; playOn = _playOnAfterLoop; }

            // Before the bar line the record plays untouched. So the roll starts where the record's own bar
            // starts, not where the button was pressed.
            if (wait > 0)
            {
                int take = (int)Math.Min(want, wait);
                int got = source is null ? 0 : Decode(buffer, offset + done, take, source);
                if (got <= 0) { RanOutMidRoll(); continue; }
                lock (_gate) _rollWait -= got;
                done += got;
                continue;
            }

            // This step has played its full length. Move on to the next one. That is a shorter read of the
            // same capture, and it starts at the top of it.
            if (left <= 0 && NextStep())
                continue;

            // The schedule has run out.
            if (left <= 0)
            {
                // Rolling in: the record carries on from where the capture stopped. That is its second bar,
                // not its fifth. From here, this deck is a normal deck again.
                if (playOn && source is not null)
                {
                    lock (_gate) _rolling = false;
                    int got = Decode(buffer, offset + done, want, source);
                    return got > 0 ? done + got : done;
                }

                // Rolling out: the changeover lands on this very sample. So the rest of the block is
                // silence, not a short read. The DJ is ending the fade in it.
                Array.Clear(buffer, (offset + done) * _channels, want * _channels);
                done += want;
                break;
            }

            int room = (int)Math.Min(want, left);

            // Capturing. The capture plays as it is taken. So the first time round, the roll is the record
            // itself. That is why the seam at the end of the capture is the only seam there is. Only the
            // longest slice is kept. Each later step is a shorter read of it.
            if (_loopFilled < _captureFrames)
            {
                int take = Math.Min(room, _captureFrames - _loopFilled);
                int got = source is null ? 0 : Decode(buffer, offset + done, take, source);
                if (got <= 0) { RanOutMidRoll(); continue; }

                Array.Copy(
                    buffer, (offset + done) * _channels,
                    _loop, _loopFilled * _channels, got * _channels);
                _loopFilled += got;

                // The capture is complete. Turn it so that its seam falls on the hit, not where the ruler
                // put it.
                if (_loopFilled >= _captureFrames)
                    TurnOntoTheHit();
                lock (_gate) _stepLeft -= got;
                done += got;
                continue;
            }

            // Play the slice again.
            if (_loopFrames <= 0)
            {
                Array.Clear(buffer, (offset + done) * _channels, room * _channels);
                lock (_gate) _stepLeft -= room;
                done += room;
                continue;
            }

            int fromLoop = Math.Min(room, _loopFrames - _loopAt);
            Array.Copy(
                _loop, _loopAt * _channels,
                buffer, (offset + done) * _channels, fromLoop * _channels);
            _loopAt = (_loopAt + fromLoop) % _loopFrames;
            lock (_gate) _stepLeft -= fromLoop;
            done += fromLoop;
        }

        return done;
    }

    /// <summary>
    /// Moves on to the next stage of the schedule. This is a shorter slice of the same capture, read from
    /// its top. Returns false when the schedule is finished.
    /// </summary>
    private bool NextStep()
    {
        lock (_gate)
        {
            if (_step + 1 >= _steps.Length)
                return false;

            _step++;
            _stepLeft = _steps[_step].LengthFrames;

            // Never longer than what was actually captured. A record that ended in the middle of a roll has
            // less of itself than the schedule asked for.
            _loopFrames = (int)Math.Min(_steps[_step].SliceFrames, Math.Max(0, _loopFilled));
            _loopAt = 0;
            return true;
        }
    }

    /// <summary>
    /// Turns the captured slice so that it starts on the hit.
    ///
    /// <para>A loop is a circle. What plays at the seam is whatever the slice starts with. So turning the
    /// slice moves the seam, and the loop itself stays the same. This means nothing has to be kept from
    /// before the bar line. The same music is at the other end of the capture. The first time round is the
    /// record itself, and it is not changed by any of this. Only the repeats have a seam at all.</para>
    ///
    /// <para>Every step of a roll reads a prefix of this same slice. So turning it once puts the shorter
    /// reads of a beat roll on the hit as well.</para>
    /// </summary>
    private void TurnOntoTheHit()
    {
        int by = TheHit();
        if (by <= 0 || by >= _captureFrames)
            return;

        if (_turn.Length < by * _channels)
            _turn = new float[by * _channels];

        // Turn left by "by". The sample at by moves to the top, and the samples in front of it go round to
        // the back. That is where a circle says they already were.
        Array.Copy(_loop, 0, _turn, 0, by * _channels);
        Array.Copy(_loop, by * _channels, _loop, 0, (_captureFrames - by) * _channels);
        Array.Copy(_turn, 0, _loop, (_captureFrames - by) * _channels, by * _channels);
    }

    /// <summary>
    /// The hit nearest the seam, as frames into the captured slice. Returns zero when no hit is worth
    /// turning to. That check matters as much as the move. A stretch with nothing struck in it has no
    /// better place to be cut than the one the grid named. Turning on the strength of a pad swelling would
    /// be worse than not turning at all.
    /// </summary>
    /// <remarks>
    /// A hit is read as a rise in energy from one short window to the next. That is what a hi-hat, a crash
    /// or a kick is. A held note is not a rise. Only the windows on either side of the seam are checked.
    /// They are checked as a circle, because that is what they are. The window before the first one is the
    /// last one.
    /// </remarks>
    private int TheHit()
    {
        const int Window = 64;
        const double Rise = 3.0;      // how much louder a window has to be than the one before it

        int slice = _captureFrames;
        int windows = slice / Window;
        if (windows < 4)
            return 0;

        int reach = Math.Min((int)(NudgeSeconds * _sampleRate) / Window, windows / 2 - 1);
        if (reach < 1)
            return 0;

        // What this slice normally carries. A hit has to be loud compared with the body of the loop. It is
        // not enough to be louder than the moment before it. The start of any sound that rises out of near
        // silence is several times that silence. None of it is a drum being struck.
        double body = 0;
        for (int w = 0; w < windows; w++)
            body += Energy(w * Window, Window);
        body /= windows;

        double best = 0;
        int found = 0;

        for (int step = -reach; step <= reach; step++)
        {
            int w = ((step % windows) + windows) % windows;
            double here = Energy(w * Window, Window);
            double before = Energy(((w - 1 + windows) % windows) * Window, Window);

            if (here < body || here < before * Rise)
                continue;

            double rise = here / Math.Max(before, 1e-12);
            if (rise <= best)
                continue;

            best = rise;
            found = w * Window;
        }

        return found;
    }

    /// <summary>The energy of one window of the captured slice.</summary>
    private double Energy(int from, int frames)
    {
        double sum = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < _channels; c++)
            {
                float sample = _loop[(from + f) * _channels + c];
                sum += sample * sample;
            }
        }
        return sum / (frames * _channels);
    }

    /// <summary>
    /// The record ended in the middle of its own roll. This happens when a skip is pressed in the last bars
    /// of a track, or when a record is shorter than the four bars it was to be rolled in over. Whatever was
    /// captured is looped. If nothing was captured, the rest of the roll is silence. Either way, the roll
    /// runs its full length, because the record on the other deck has already been held for exactly that
    /// many frames.
    /// </summary>
    private void RanOutMidRoll()
    {
        lock (_gate)
        {
            _rollWait = 0;
            _captureFrames = _loopFilled;   // whatever was got. No more of it is coming.
            _loopFrames = Math.Min(_loopFrames, _loopFilled);   // possibly zero, which reads as silence
            _loopAt = 0;
            _playOnAfterLoop = false;       // there is nothing left to play on into
        }
    }

    /// <summary>Reads <paramref name="frames"/> off the decoder, primed audio first. This is the record as
    /// it comes, before the shelf, the sweep or the fader.</summary>
    private int Decode(float[] buffer, int offset, int frames, IPcmSource source)
    {
        int start = offset * _channels;

        // Anything primed by a speed change plays first. It is the audio that follows the samples already
        // handed to the sink, and the new decoder has been read past it.
        int read = Math.Max(0, TakePrimed(buffer, offset, frames));

        // The primed audio almost never ends on a block boundary. So most of the time it runs out part way
        // through a block. The rest of the block has to come from the decoder now. If a short block were
        // returned instead, the frames after it would be silent, because the booth mixes what each deck
        // gives it and moves on. That leaves a hole of up to one block in the middle of a record that is
        // playing at full level. That is the click at every changeover and at every step of the pitch ride,
        // one PrimeFrames after each of them.
        if (read < frames)
        {
            int want = frames - read;
            int at = start + read * _channels;

            // The decoder writes from the start of whatever it is given. So it can only be given the
            // caller's buffer when this block is still empty. That means no part is filled by primed audio,
            // and no part is filled by the silence of a hold.
            bool inPlace = read == 0 && offset == 0;
            if (!inPlace && _topUp.Length < want * _channels)
                _topUp = new float[want * _channels];

            // This is counted for the whole read. A speed change on another thread waits for the read to
            // finish before it closes the decoder.
            Interlocked.Increment(ref _readers);
            int got;
            try
            {
                got = source.Read(inPlace ? buffer : _topUp, want);
            }
            finally
            {
                Interlocked.Decrement(ref _readers);
            }

            if (got > 0)
            {
                if (!inPlace)
                    Array.Copy(_topUp, 0, buffer, at, got * _channels);
                read += got;
            }

            // A record that ends inside its own roll-out has not run out, as far as the set is concerned.
            // The loop carries on without it, and the changeover still lands where it was always going to.
            // See RanOutMidRoll. RanOutMidSpin says the same thing about a record that ends part way into
            // the bar that a rewind was going to drag back.
            bool rolling;
            lock (_gate) rolling = _rolling || _spinning;

            if (read <= 0)
            {
                HasRunOut = !rolling;
                return 0;
            }
            if (read < frames)
            {
                HasRunOut = !rolling;
                Array.Clear(buffer, start + read * _channels, (frames - read) * _channels);
            }
        }

        BlendOutTheOldDecoder(buffer, offset, read);

        lock (_gate) _framesPlayed += read;
        return read;
    }

    /// <summary>
    /// How long until this record's next bar line, in heard seconds. The stretch is applied, so this is
    /// time on the clock, not time on the record. Null when the deck has no grid to read. In that case a
    /// changeover falls back to a plain blend.
    /// </summary>
    public double? SecondsToNextDownbeat
    {
        get
        {
            if (Track?.Analysis is not { } analysis)
                return null;
            if (analysis.NextDownbeatAfter(PositionSeconds) is not { } next)
                return null;
            return Math.Max(0, (next - PositionSeconds) / TempoRatio);
        }
    }

    /// <summary>
    /// How hard the record on this deck is hitting right now. This is the energy where the needle is, not
    /// the track's average. The next record is chosen against this.
    /// </summary>
    public double? CurrentIntensity =>
        Track?.Analysis is { } analysis ? analysis.IntensityAt(PositionSeconds) : null;

    /// <summary>How far the record has left to run, in original seconds. Positive infinity for an empty
    /// deck, so that comparisons of which record runs out first are safe.</summary>
    public double RemainingSeconds =>
        Track is { } track && track.DurationSeconds > 0
            ? Math.Max(0, track.DurationSeconds - PositionSeconds)
            : double.PositiveInfinity;

    public void Dispose() => Unload();
}
