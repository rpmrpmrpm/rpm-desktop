using System.Diagnostics;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Shell;

/// <summary>What is allowed to run in the background, and when.
/// <para><b>What it replaces.</b> Nothing. Without a governor, there is no answer to this question.
/// Every pass in the app started itself, and they all started at the same instant, because they all
/// hang off one event: the library opening. In one measurement on a 13,000-track collection, the
/// diagnostic ring showed:</para>
/// <list type="bullet">
/// <item>the fingerprint pass, the loudness pass, the popularity sweep, the Discogs style sweep, the
/// recording-key backfill, the cloud mirror, the cloud sync, the mood-list push, the folder sync, the
/// album index rebuild and the artist index rebuild all began within the same two seconds;</item>
/// <item>the interface, which needs the same database gate to build its left pane, waited 1,269 ms for
/// the playlist tree behind a fingerprint query and 2,735 ms for the same tree's cloud state behind
/// another;</item>
/// <item>the cloud mirror then took the WRITE gate and held it for 2,843 ms across a network call, with
/// the interface waiting behind it;</item>
/// <item>the splash came off at 12.1 seconds, which is twice its own emergency ceiling. It had no
/// animation at all. The drawing thread was never free for six turns in a row. The gate's fallback
/// ceiling ran on that same busy thread.</item>
/// </list>
/// <para><b>What it is.</b> One object that every background pass asks before each unit of work. It
/// answers out of three things and nothing else: where the startup has got to (<see cref="Phase"/>),
/// which passes have been let in so far (<see cref="Wave"/>), and what the drawing thread is actually
/// doing right now (<see cref="UiPulse"/>). The rule is <see cref="Admission"/>, which is a function
/// with no state, so the policy can be read and tested without any of this.</para>
/// <para><b>What it is not.</b> It is not a work queue and it does not own any pass. Each pass keeps
/// its own loop, its own cancellation and its own idea of what a unit of work is. That is right,
/// because a unit is a track for one of them and a page of a catalogue for another. The governor only
/// ever says yes, or says no and how long to go away for. A pass that never asks is a pass that is not
/// governed, and the way to find those is to look for work that does not go through
/// <see cref="Pass"/>.</para>
/// <para><b>What it cannot do.</b> It cannot preempt. A pass that takes the database write gate and
/// holds it for twenty seconds inside one call is not something an admission rule can reach. By then it
/// has already been admitted, and the interface is waiting behind it. Such work must be broken up at
/// the call site. See <c>LibraryDatabase.MirrorCloudLibrary</c> for an example. Admission stops the
/// pile-up. It does not stop a single long hold.</para>
/// <para><b>What arrives out of order here.</b> The phase is written by the startup sequence on the
/// drawing thread; the wave is advanced on the drawing thread from the pulse's beat; the lane counts
/// are stepped by whichever pool thread a pass happens to be on; and a pass asks from any thread at any
/// time, including during a stall. Nothing here takes a lock: the phase and the wave are single writes
/// of an enum read with <see cref="Volatile"/>, and the lane counts are <see cref="Interlocked"/>. A
/// pass can therefore be admitted on a phase that changed a microsecond later, and the cost of that is
/// one unit of work in the wrong phase. That is a unit of work, not a fault. Trading that for a lock
/// every pass in the app would contend on is not a trade worth making.</para>
/// </summary>
public sealed class Governor
{
    /// <summary>
    /// How long a wave has to have nothing in flight before the next one is let in.
    ///
    /// <para>A second and a half. It is measured as one continuous run, not as a sample. A pass releases
    /// its lane between units. So a wave is briefly empty several times a second while it works flat out.
    /// What is waited for is a wave that has stopped, not one that is pausing between units.</para>
    /// </summary>
    private static readonly TimeSpan WaveQuiet = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a wave may hold the next one back, quiet or not.
    /// <para>This is a promise, like the splash's ceiling, for the same reason. The fingerprint pass
    /// can run for hours on a first launch. A wave that opened only when things were quiet would leave
    /// the last third of the background work switched off for the whole session. Half a minute is long
    /// enough for the first wave to have the machine to itself. It is short enough that no work is
    /// forgotten.</para>
    /// </summary>
    private static readonly TimeSpan WaveCeiling = TimeSpan.FromSeconds(30);

    private readonly UiPulse _pulse;

    private readonly int[] _inLane = new int[4];
    private readonly int[] _inWave = new int[3];

    /// <summary>The phase and the wave are kept as ints, because an enum field cannot be passed to
    /// <see cref="Volatile"/> by reference. They must be readable from a pool thread without a lock. The
    /// properties below give them their names.</summary>
    private int _phase = (int)Phase.Cold;
    private int _wave = (int)Wave.First;

    private readonly Stopwatch _sinceWave = new();
    private readonly Stopwatch _quiet = new();

    /// <summary>
    /// A governor for a host with no interface to protect: the tests, and anything else headless.
    ///
    /// <para>Every pass is admitted at once, because the thing that admission protects is not there.
    /// The answer is stated, not left out. Otherwise a governor argument could be left off, and a pass
    /// that forgot it would run without any governing in the real app, with nothing to say so.</para>
    ///
    /// <para>It reaches <see cref="Phase.Ready"/> and opens every wave on construction, so a pass that
    /// awaits its turn in a test gets it immediately instead of waiting out a startup that will never
    /// happen.</para>
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="Lazy{T}"/> and not a field initialiser.</b> If this were written as
    /// <c>public static Governor Unattended { get; } = MakeUnattended();</c>, it would be a static field
    /// initialiser. The class would then be <c>beforefieldinit</c>, and the runtime could run the
    /// initialiser at any point before the first static field is read. That includes an app that never
    /// asks for this governor. The boot log would then show a second "phase Ready" and a second "wave
    /// First", which would be false. The same trap affected <c>App.Uptime</c>, whose stopwatch did not
    /// start when its comment said it did.
    ///
    /// <para>Constructing a <see cref="Lazy{T}"/> does not run its factory, so an early initialisation
    /// now costs an empty object and nothing else. The governor is built on the first read of
    /// <see cref="Unattended"/>, which in the app is never.</para>
    /// </remarks>
    private static readonly Lazy<Governor> TheUnattendedOne = new(MakeUnattended);

    /// <inheritdoc cref="TheUnattendedOne"/>
    public static Governor Unattended => TheUnattendedOne.Value;

    private static Governor MakeUnattended()
    {
        // Silent, before anything is reached: a governor with no interface to protect has nothing to
        // report, and a test's phases in the app's own boot track are noise at best.
        var governor = new Governor(new UiPulse(null)) { _says = false };
        governor.Reach(Phase.Ready);
        Volatile.Write(ref governor._wave, (int)Wave.Last);
        return governor;
    }

    /// <summary>Whether this governor writes what it decides into the ring. False for
    /// <see cref="Unattended"/> and true for the one the app runs on.</summary>
    private bool _says = true;

    /// <summary>Says a line, unless this is a governor nobody is watching. Internal because
    /// <see cref="Sweep"/> reports through it too, and for the same reason.</summary>
    internal void Say(string line)
    {
        if (_says)
            RPM.Core.Diagnostics.Scope.Said("gov", line);
    }

    public Governor(UiPulse pulse)
    {
        _pulse = pulse;

        // The wave bookkeeping runs on the pulse's beat and has no timer of its own. It is three
        // comparisons on the drawing thread, which is the thread being protected, so it must be cheap.
        // A second timer would ask the same thread the same question. UiPulse exists to stop exactly that.
        _pulse.Landed += _ => Consider();
    }

    /// <summary>Where the startup has got to.</summary>
    public Phase Reached => (Phase)Volatile.Read(ref _phase);

    /// <summary>The furthest wave that has been let in.</summary>
    public Wave Opened => (Wave)Volatile.Read(ref _wave);

    /// <summary>The interface's own reading, for anything that wants to show it.</summary>
    public UiPulse Pulse => _pulse;

    /// <summary>
    /// Says the startup has reached a phase. Called by the startup sequence, on the drawing thread, and
    /// by nothing else.
    /// </summary>
    /// <remarks>
    /// Only ever forwards. A phase that could go backwards would let a pass be admitted under
    /// <see cref="Phase.Ready"/> and then find itself running under <see cref="Phase.Opening"/>, and
    /// there is no phase of this app's life where that is a thing that happened.
    /// </remarks>
    public void Reach(Phase phase)
    {
        if ((int)phase <= Volatile.Read(ref _phase))
            return;

        Volatile.Write(ref _phase, (int)phase);
        Say($"phase {phase}");

        if (phase != Phase.Ready)
            return;

        // The first wave opens with the interface, not before it: everything in it is work the screen
        // is waiting on, and the screen does not exist until now.
        _sinceWave.Restart();
        _quiet.Reset();
        Say("wave First");
    }

    /// <summary>A pass, which is the only way to reach the answer.</summary>
    /// <param name="name">What it is called on a governor line. Keep it short, because the line is
    /// shared.</param>
    /// <param name="lane">What it competes for.</param>
    /// <param name="wave">Where it comes in the stated order.</param>
    /// <param name="notBefore">The earliest phase it may run in at all. Most passes want
    /// <see cref="Phase.Ready"/>; a pass the first screen is waiting for wants
    /// <see cref="Phase.Loading"/>.</param>
    /// <remarks>Every argument is required, and none has a default. A pass that forgot its wave would
    /// be in the first wave, running while the user waits for the screen, and nothing would say so.
    /// Preventing that failure is the purpose of this object. The compiler asks for every argument.
    /// </remarks>
    public Sweep Pass(string name, Lane lane, Wave wave, Phase notBefore) =>
        new(this, name, lane, wave, notBefore);

    /// <summary>The rule, asked with this governor's own state. Public so a test can drive the object
    /// rather than only the function.</summary>
    internal Verdict Ask(Wave wave, Lane lane, Phase notBefore) =>
        Admission.Ask(
            _pulse.LateMs,
            _pulse.HeldForMs,
            _pulse.Calm,
            Reached,
            Opened,
            notBefore,
            wave,
            lane,
            Volatile.Read(ref _inLane[(int)lane]));

    /// <summary>Books a unit of work into its lane and its wave. Paired with <see cref="Done"/> by
    /// <see cref="Sweep.Slot"/>, which is the only thing that calls either.</summary>
    internal void Took(Wave wave, Lane lane)
    {
        Interlocked.Increment(ref _inLane[(int)lane]);
        Interlocked.Increment(ref _inWave[(int)wave]);
    }

    /// <inheritdoc cref="Took"/>
    internal void Done(Wave wave, Lane lane)
    {
        Interlocked.Decrement(ref _inLane[(int)lane]);
        Interlocked.Decrement(ref _inWave[(int)wave]);
    }

    /// <summary>
    /// Whether the open wave has stopped, and therefore whether the next may come in.
    ///
    /// <para>Runs on the drawing thread, sixty times a second. Everything in it is a read of an int and
    /// a comparison of two longs.</para>
    /// </summary>
    private void Consider()
    {
        var opened = Opened;
        if (opened == Wave.Last || Reached != Phase.Ready)
            return;

        // Work still running in the open wave, or in an earlier wave, keeps the next wave shut. An
        // earlier-wave pass that is still running is exactly what the next wave waits for.
        var working = 0;
        for (var w = 0; w <= (int)opened; w++)
            working += Volatile.Read(ref _inWave[w]);

        if (working > 0 || !_pulse.Free)
        {
            _quiet.Reset();
        }
        else if (!_quiet.IsRunning)
        {
            _quiet.Restart();
        }

        var settled = _quiet.IsRunning && _quiet.Elapsed >= WaveQuiet;
        var overdue = _sinceWave.IsRunning && _sinceWave.Elapsed >= WaveCeiling;

        if (!settled && !overdue)
            return;

        var next = (Wave)((int)opened + 1);
        Volatile.Write(ref _wave, (int)next);
        _sinceWave.Restart();
        _quiet.Reset();

        Say($"wave {next}" + (overdue && !settled ? " (CEILING, the one before it is still working)" : ""));
    }
}

/// <summary>
/// One background pass, and the only way to ask the governor anything.
///
/// <para>Used like this, and the shape is the point:</para>
/// <code>
/// while (!ct.IsCancellationRequested)
/// {
///     using var slot = await _sweep.Next(ct);
///     if (!slot.Go)
///         return;          // the app is closing
///
///     await OneTrack();    // the lane is held for exactly this
/// }
/// </code>
///
/// <para>The <c>using</c> statement holds the lane and releases it. So a pass cannot be admitted and
/// then forget to report that it has finished. That would leave its lane full for the rest of the
/// session, and every other pass in that lane would be shut out with no error.</para>
/// </summary>
public sealed class Sweep
{
    private readonly Governor _governor;
    private readonly Wave _wave;
    private readonly Lane _lane;
    private readonly Phase _notBefore;

    private Held _lastSaid = Held.None;

    internal Sweep(Governor governor, string name, Lane lane, Wave wave, Phase notBefore)
    {
        _governor = governor;
        Name = name;
        _lane = lane;
        _wave = wave;
        _notBefore = notBefore;
    }

    /// <summary>What this pass is called.</summary>
    public string Name { get; }

    /// <summary>
    /// Waits until the interface can afford this pass's next unit of work, and hands back the lane.
    /// </summary>
    /// <returns>A slot whose <see cref="Slot.Go"/> is true, held until it is disposed. False only when
    /// <paramref name="ct"/> fired, which means the app is closing or the library was swapped. In that
    /// case nothing was taken, and the caller returns.</returns>
    public async Task<Slot> Next(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var verdict = _governor.Ask(_wave, _lane, _notBefore);
            if (verdict.Go)
            {
                _lastSaid = Held.None;
                _governor.Took(_wave, _lane);
                return new Slot(_governor, _wave, _lane);
            }

            // Logged once per reason, not once per ask. A pass held for four seconds asks a hundred
            // times, and those lines would all say the same thing. The useful line is the one where the
            // reason changes. That is the pass moving from "not my turn" to "the interface is busy" to
            // "in".
            if (verdict.Why != _lastSaid)
            {
                _lastSaid = verdict.Why;
                _governor.Say($"{Name} held: {verdict.Why}");
            }

            if (!await Cancellable.DelayAsync(TimeSpan.FromMilliseconds(verdict.WaitMs), ct)
                    .ConfigureAwait(false))
                break;
        }

        return default;
    }

    /// <summary>
    /// A lane, held for one unit of work.
    /// </summary>
    /// <remarks>
    /// A struct, and default-constructible on purpose: <c>default</c> is the refusal, it carries no
    /// governor, and disposing it does nothing. That is what lets the call site be one <c>using</c>
    /// and one test of <see cref="Go"/> rather than a null check around a block.
    /// </remarks>
    public readonly struct Slot : IDisposable
    {
        private readonly Governor? _governor;
        private readonly Wave _wave;
        private readonly Lane _lane;

        internal Slot(Governor governor, Wave wave, Lane lane)
        {
            _governor = governor;
            _wave = wave;
            _lane = lane;
        }

        /// <summary>Whether the pass may do its work. False means the token fired and nothing was
        /// taken.</summary>
        public bool Go => _governor is not null;

        public void Dispose() => _governor?.Done(_wave, _lane);
    }
}
