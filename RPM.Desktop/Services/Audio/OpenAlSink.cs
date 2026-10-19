using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;
using Silk.NET.OpenAL;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The local sound device: a streaming OpenAL Soft source fed by a fixed set of small buffers.
///
/// <para>The buffer queue is the backpressure. There are only <see cref="BufferCount"/> buffers. Once
/// they are all with the device, <see cref="Enqueue"/> has no free buffer for the next block. It waits
/// until the device returns one, which happens at the speed the audio is played. So the engine needs no
/// timer. Pausing stops the device from returning buffers, so the decoder simply waits where it
/// is.</para>
/// </summary>
public sealed unsafe partial class OpenAlSink : IAudioSink
{
    /// <summary>The number of buffers, about 500 ms of audio at the engine's 50 ms blocks. This is the
    /// whole margin the pump has. A scheduling gap, a GC pause or a device stall is absorbed by it.
    /// When the margin runs out, the source runs dry and the listener hears a break. A deeper queue
    /// makes live EQ and volume changes take effect later. So the queue is kept as deep as possible
    /// without making the controls feel slow.</summary>
    private const int BufferCount = 10;

    /// <summary>How many buffers must be queued before the source may start. If the source started on
    /// the first 50 ms block, the device would play it while the second block was still being decoded.
    /// On a cold file the decoder would fall behind, and the track would open with a break. A short
    /// pre-roll adds about a tenth of a second of silence, which nobody notices, and it prevents that
    /// break.</summary>
    private const int PreRollBuffers = 3;

    /// <summary>The length of one buffer in seconds. Everything here is counted in buffers of this
    /// length.</summary>
    private const double BufferSeconds = 0.05;

    /// <summary>The number of buffers that may be with the device at once, out of the
    /// <see cref="BufferCount"/> that exist. This sets how deep the queue is. All the buffers still
    /// exist. This value is the point where <see cref="Enqueue"/> starts to wait instead of handing
    /// over another buffer.
    ///
    /// <para>It can be changed, because it sets the second half of how late a live change takes effect.
    /// See <see cref="KeepAhead"/>. It is never below two. With one buffer, the device must play it
    /// fully before the next one arrives, and that is not a queue.</para>
    /// </summary>
    private volatile int _inFlight = BufferCount;

    /// <summary>When the pump last handed over a block. An underrun uses this to say how long the
    /// device had no audio. A device runs dry for one reason: it was not given audio in time. There are
    /// two possible causes. The pump may have taken too long to produce the next block, which shows as
    /// a gap of hundreds of milliseconds. Or the device may have stopped playing the audio it already
    /// had. That shows as a normal gap, and the watcher handles it.</summary>
    private long _fedAt;

    /// <summary>The time the runtime spent pausing the process for garbage collection, as of the last
    /// time the pump fed the device. A managed thread cannot avoid a collection, whatever priority it
    /// asks for. <see cref="RPM.Core.Audio.AudioThread"/> gives the pump a performance core, but it
    /// does not stop the pump from being paused. So a collection longer than the queue is an underrun
    /// with no late block in it. That looks the same as a decoder that could not keep up, but the two
    /// need different fixes. So the gap is split into parts.</summary>
    private TimeSpan _gcAtFeed;

    /// <summary>How long the OpenAL library took for the last block. This counts every call into the
    /// library made while taking that block. The calls are the claim, any device move on the way in,
    /// the reclaiming of buffers, and the two calls that hand the audio over. The value is in
    /// microseconds when all is well. When it is large, the block was late because the driver held it,
    /// and the cause is not upstream. In that case, look first at this sink's own watcher. It asks the
    /// machine for the device list twice a second, on a thread that shares OpenAL's lock.
    ///
    /// <para>The value must include every call into the library, not only the buffer fill and the
    /// queue call. A device that stops returning buffers while still reporting that it is playing holds
    /// the pump inside <c>alSourceUnqueueBuffers</c>, and that time must be counted.</para>
    /// </summary>
    private double _alMs;

    /// <inheritdoc/>
    public void KeepAhead(double seconds)
    {
        int buffers = (int)Math.Round(seconds / BufferSeconds);
        _inFlight = Math.Clamp(buffers, 2, BufferCount);
    }

    /// <summary>The shortest time between two attempts by the watcher. It is also the time the watcher
    /// waits while a held track waits. The pauses between looks are
    /// <see cref="DeviceWatch.SleepMs"/>.</summary>
    private const int LostRetryMs = DeviceWatch.HeldTrackMs;

    // ALC tokens that Silk.NET does not provide. One is the enumerate-all extension's name for every
    // endpoint, not only the ones that the plain enumeration lists. The other is the disconnect
    // extension's flag. Its companion, ALC_DEFAULT_ALL_DEVICES_SPECIFIER, is not defined here. See
    // AvailableDevices.
    private const int AllDevicesSpecifier = 0x1013;
    private const int Connected = 0x313;

    private readonly object _gate = new();
    private readonly Queue<int> _queuedFrames = new();   // frames per buffer handed to the device, FIFO
    private readonly Stack<uint> _free = new();

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _source;
    private uint[] _buffers = Array.Empty<uint>();

    /// <summary>The shortest time between two device moves. A move causes an audible seam in the audio.
    /// A change that does not settle must not cause a series of moves.</summary>
    private const int MoveCooldownMs = 2000;

    private long _nextDeviceMove;
    private long _lostSince;

    /// <summary>When the device last returned a buffer. A working device does this every block. A dead
    /// device stops doing it, and that is how the stop is detected without asking anything.</summary>
    private long _lastProgress;

    /// <summary>The real time the source has spent playing. It stops while paused and restarts on each
    /// track. A device's claims are checked against this value. The device cannot have played more
    /// audio than this.</summary>
    private readonly Stopwatch _played = new();

    /// <summary>Wakes the watcher. This happens when the driver reports a change, when a track starts,
    /// or when the listener picks a device. The watcher's wait is the only clock it has.</summary>
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>The endpoint the pump should move this sink to at its next block. The watcher sets it.
    /// It is null the rest of the time, so the pump only pays for the check.</summary>
    private volatile string? _moveTo;

    private volatile bool _closing;

    /// <summary>The reason the sink cannot play, or null when it can. The player reads it when a track
    /// is refused, on a different thread from the one that writes it. The whole reference is replaced
    /// at once. It is cleared before <see cref="IsAvailable"/> becomes true, and set after it becomes
    /// false. So a reader that finds the sink unavailable also finds the reason, or finds nothing
    /// during a rebuild.</summary>
    private volatile OutputTrouble? _trouble;

    /// <summary>The machine's endpoints when the last open failed, one per line. It is null after a
    /// successful open. The watcher reopens an idle sink by itself only when this list has changed, or
    /// when a refused play asked for another try (<see cref="TryAgain"/>).</summary>
    private volatile string? _failedOn;

    /// <summary>A refused play asked for another try, whatever the endpoints are. The watcher's next
    /// idle check clears it.</summary>
    private volatile bool _retryAsked;

    /// <summary>1 while a thread is inside <see cref="MoveTo"/>. The pump moves the device during a
    /// track, and the watcher moves it while no track is playing. The two can overlap briefly. For
    /// example, a stop can arrive while a held pump is in the middle of a move. Two rebuilds at once
    /// would tear down each other's device. So <see cref="MoveTo"/> sets this flag first, and a second
    /// caller leaves without moving.</summary>
    private int _moving;

    /// <summary>The endpoint this sink was last asked to use, as the device enumeration names it. This
    /// is not the name the device reports after opening. See <see cref="Look"/> for why the two must
    /// never be compared.</summary>
    private volatile string _intended = "";

    /// <summary>The source was stopped by a device move, not by a missed deadline.
    /// <see cref="KeepPlaying"/> reads and clears this flag. Without it, every move would be counted
    /// and logged as an underrun.</summary>
    private volatile bool _moved;

    /// <summary>The device the sound was going to has disappeared, and nothing has replaced it yet.
    ///
    /// <para>While this is set, everything the pump does with the device is suspended. A stopped OpenAL
    /// source reports its whole queue as processed. So a device that dies in the middle of a track
    /// returns every buffer at once. If that were believed, about half a second of audio would be
    /// counted as heard, the buffers freed, and the rest of the track pushed into a dead device in a
    /// few milliseconds. The track would be thrown away. Waiting costs only the silence that is already
    /// happening.</para>
    /// </summary>
    private volatile bool _deviceLost;

    /// <summary>How long the sound may be missing before playback is paused. Swapping a speaker takes a
    /// few seconds. A device that has not returned by then will not return by itself. Holding a track
    /// open forever is worse than stopping it where it is.</summary>
    private const int StallGiveUpMs = 10_000;

    /// <summary>How long a device may return no buffers before it is presumed dead. A device returns a
    /// buffer every 50 ms. This limit is fifteen missed buffers in a row. It is far longer than a
    /// scheduling gap, and shorter than the five seconds the machine can take to report that a speaker
    /// is gone.
    ///
    /// <para>The time is measured in <see cref="_silence"/>, which counts only the waiting that this
    /// loop does. Wall clock time would also count the time the thread was paused. A long pause in the
    /// process would then look like a dead speaker.</para>
    /// </summary>
    private const int NoProgressMs = 750;

    /// <summary>The length of each turn of the wait. The stall is counted in these turns, so the turn
    /// must be short enough that the count is accurate.</summary>
    private const int StallPollMs = 5;

    /// <summary>How long the pump has watched the device return nothing while the device owed audio.
    /// Only the waits the pump actually performed are counted. Time when the thread was paused is not
    /// counted, and time spent paused is not counted either. See <see cref="DeviceSilence"/>.</summary>
    private readonly DeviceSilence _silence = new();

    /// <summary><c>alcReopenDeviceSOFT</c>, when the driver provides it. It moves an open device to
    /// another endpoint, and keeps every context, source and buffer. When it is null, a rebuild is used
    /// instead.</summary>
    private delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte> _reopen;

    /// <summary><c>alcSetThreadContext</c>, from <c>ALC_EXT_thread_local_context</c>. It sets the
    /// current context for the calling thread only, and not for the whole process.
    ///
    /// <para>This is needed because the app has more than one sink at a time. The opening sound plays
    /// through its own sink while the player starts. The vinyl monitor uses another sink for the input.
    /// A cast that returns to this computer creates a new sink next to the old one.
    /// <c>alcMakeContextCurrent</c> is one slot shared by all of them. Every AL call in this file acts
    /// on whatever context is in that slot. A source is numbered within its own context. So if one sink
    /// stops, empties or deletes its source while another sink holds the slot, it does those things to
    /// the other sink's source. This is not a rare race. It is the normal case. The opening sound is
    /// stopped when a record starts, which is when the player takes the slot. The record then goes
    /// silent after about a second, and pauses itself ten seconds later.</para>
    ///
    /// <para>This is static because the entry point belongs to the library, not to a device. It is
    /// looked up once. It is null when the extension is missing, and then the process-wide slot is the
    /// only option. OpenAL Soft has had the extension since version 1.9, so that path is kept for
    /// completeness only.</para>
    /// </summary>
    private static delegate* unmanaged[Cdecl]<Context*, byte> _setThreadContext;

    private static bool _threadContextLookedUp;

    /// <summary>Whether each thread has its own current context on this machine. This keeps two sinks
    /// from using each other's sources. When it is false, the single process-wide slot is used, and the
    /// sinks can only take turns. The value is set after a sink is built. It is used for diagnostics,
    /// because the fault it guards against looks like a speaker that stopped working.</summary>
    public static bool ContextsArePerThread => _setThreadContext != null;

    /// <summary>This sink's listener in <see cref="AudioDeviceWatch"/>, kept so it can be removed
    /// later. It is a field, not a lambda written inline at each use. Removing a delegate needs the
    /// same instance that was added. A sink that stays subscribed after disposal would keep a dead
    /// object woken for the life of the process.</summary>
    private readonly Action _onDevicesChanged;

    private short[] _scratch = Array.Empty<short>();
    private long _framesConsumed;           // frames the device has finished playing
    private int _volume = 100;
    private int _channels = 2;
    private int _rate = 44100;

    private volatile bool _paused;
    private volatile bool _stopped = true;

    /// <summary>Whether a pump is attached. This is true from <see cref="Begin"/> until the pump's
    /// last call into this sink. That call is a <see cref="Drain"/> at the end of a track, an
    /// <see cref="Enqueue"/> that returns false, or a <see cref="Stop"/>. This is not the same as
    /// <see cref="_stopped"/>. A track that plays to its end is drained, and its pump is gone. The sink
    /// is not stopped until the next play.
    ///
    /// <para>This decides who carries a move out (<see cref="Carrier"/>). The engine's worker and the
    /// pump write it, and the watcher reads it.</para>
    /// </summary>
    private volatile bool _pumpAttached;

    /// <summary>Who carries the next move out. See <see cref="DeviceWatch"/>.</summary>
    private MoveCarrier Carrier => _pumpAttached ? MoveCarrier.Pump : MoveCarrier.Watcher;
    private bool _started;                  // the source has been told to play at least once in this track
    private long _underruns;

    /// <summary>The source was found stopped with audio still queued, and has not played since. One
    /// break makes one log entry and one count, however many times the pump checks during it. See
    /// <see cref="KeepPlaying"/>.</summary>
    private bool _dry;

    /// <summary>The endpoint the listener picked in settings, by the driver's name for it. It is null
    /// to use whatever the machine calls its default.
    ///
    /// <para>This is static because the choice belongs to the app, not to one sink. The player's sink
    /// and the vinyl monitor are different objects, and both play through the same chosen speakers. A
    /// name the machine does not have right now is not an error. For example, an unplugged USB
    /// interface is still the listener's choice. The sink then uses the default, and switches back when
    /// the device returns.</para>
    /// </summary>
    public static string? PreferredDevice { get; set; }

    /// <summary>Every playback endpoint the machine has, with the names the picker shows and that
    /// <see cref="PreferredDevice"/> uses. The machine's default output is first in the list.
    ///
    /// <para>This list is the only reliable way to find the default output. OpenAL Soft's
    /// <c>ALC_DEFAULT_ALL_DEVICES_SPECIFIER</c> looks like the right choice, but it is not. It probes
    /// the machine only when its cached list is empty. Otherwise it returns the first name from its
    /// last enumeration. If it is read once at startup, it keeps returning the same endpoint, even
    /// after the listener changes the system output. This method probes every time. The library puts
    /// the default first in the list. Each backend does this. WASAPI adds
    /// <c>GetDefaultAudioEndpoint</c> before it walks the collection, and CoreAudio adds
    /// <c>kAudioHardwarePropertyDefaultOutputDevice</c> first.</para>
    /// </summary>
    public static IReadOnlyList<string> AvailableDevices()
    {
        try
        {
            var alc = SharedApi();
            if (alc is null) return [];

            // Silk's typed string getter stops at the first NUL. This property is a list of strings
            // that ends with an empty string, so the pointer is walked by hand.
            var getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)
                alc.GetProcAddress(null, "alcGetString");
            if (getString == null) return [];

            var names = new List<string>();
            for (var p = getString(null, AllDevicesSpecifier); p != null && *p != 0;)
            {
                int len = 0;
                while (p[len] != 0) len++;
                names.Add(Encoding.UTF8.GetString(p, len));
                p += len + 1;
            }

            return names;
        }
        catch { return []; }
    }

    /// <summary>Where news of device changes comes from on this machine, and whether it covers every
    /// change. This is for diagnostics. "It did not follow the change" and "it was never told" are
    /// different faults.</summary>
    public static string DeviceNews => AudioDeviceWatch.Complete
        ? AudioDeviceWatch.Source
        : $"{AudioDeviceWatch.Source}, and a look every {DeviceWatch.UnannouncedMs} ms for what it does not cover";

    /// <summary>One ALC context for the questions asked when no sink is open, such as those of the
    /// settings picker.</summary>
    private static ALContext? SharedApi()
    {
        try { return _shared ??= ALContext.GetApi(soft: true); }
        catch { return null; }
    }

    private static ALContext? _shared;

    public OpenAlSink()
    {
        // This only wakes the watcher. The notification arrives on an OS thread that must not be held
        // up. Every question is asked on the watcher's own thread.
        _onDevicesChanged = () => _wake.Set();

        // The library is loaded first, on its own. If it does not load, there is no device to ask
        // about, and the fix is to reinstall the app, not to connect a device.
        try
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);
        }
        catch (Exception e)
        {
            _alc = null;
            _al = null;
            Faults.Note("audio", "loading the sound library", e);
            _trouble = new OutputTrouble.LibraryMissing();

            // No watcher is started. The watcher asks the library for everything, and there is no
            // library. No device change can bring it back. So every play is refused with this reason.
            return;
        }

        var chosen = "";
        try
        {
            FindThreadContext(_alc);

            chosen = ChosenDevice();
            _device = _alc.OpenDevice(chosen);
            if (_device != null)
                Open(chosen);
        }
        catch (Exception e)
        {
            IsAvailable = false;
            Faults.Note("audio", "opening the sound device", e);
        }

        if (IsAvailable)
            return;

        // A sink that cannot open its device may open later. The machine may be switching devices.
        // Another process may hold the endpoint for a moment. A cast may have just been shut down. The
        // listener may also connect speakers. So the watcher runs in every case. It reopens the device
        // itself while no pump is attached (LookWhileIdle). Until a pump is attached, nothing else can
        // carry a move out.
        NoteFailedOpen(chosen);
        _deviceLost = true;

        // A first open that failed partway may have left a device and its reopen entry point in place.
        // Without the entry point, the next move is a full rebuild, which tears down whatever is there.
        _reopen = null;

        // Open subscribes before the step that can throw. The subscription is added once, so the
        // listener is not registered twice.
        AudioDeviceWatch.Remove(_onDevicesChanged);
        AudioDeviceWatch.Add(_onDevicesChanged);
        new Thread(Watch) { IsBackground = true, Name = "audio-devices" }.Start();
    }

    /// <summary>The rest of the first open, after <c>OpenDevice</c> has returned a device. It throws
    /// when the library throws. The constructor records the failure and starts the watcher.</summary>
    private void Open(string chosen)
    {
        _intended = chosen;
        _context = _alc!.CreateContext(_device, null);

        // The library does not throw here. It returns null, for example when the endpoint will not
        // accept a shared client. If the code went on past this point, the sink would report itself as
        // available and play into nothing, until the stall timeout gave up ten seconds later.
        if (_context == null)
            throw new InvalidOperationException("the device opened and would not make a context");

        // The context is claimed, not only made current. The source and the buffers created next belong
        // to whichever context is current when they are created. Another sink may hold the process-wide
        // slot at this moment. A source created in another sink's context can never be used by this
        // sink. Every call about it returns AL_INVALID_NAME, and from outside that looks like a device
        // that has stopped playing.
        ClaimContext();

        _source = _al!.GenSource();
        _buffers = new uint[BufferCount];
        for (int i = 0; i < BufferCount; i++) _buffers[i] = _al.GenBuffer();

        if (_alc.IsExtensionPresent(_device, "ALC_SOFT_reopen_device"))
            _reopen = (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)
                _alc.GetProcAddress(_device, "alcReopenDeviceSOFT");

        AudioDeviceWatch.Add(_onDevicesChanged);

        IsAvailable = true;

        // Started last: it asks the driver questions, and it must not do that against a half-built
        // sink. Background, so it never holds the app open.
        new Thread(Watch) { IsBackground = true, Name = "audio-devices" }.Start();
    }

    /// <summary>Records why an open did not take, for the message the player shows and for the ring.
    /// Asks the machine what endpoints it has, because none at all is different advice from one that
    /// refused. Not called under <see cref="_gate"/>: the enumeration opens every endpoint, which with
    /// a Bluetooth device in the list takes hundreds of milliseconds.</summary>
    /// <param name="device">What was asked for; empty is the machine's default.</param>
    private void NoteFailedOpen(string device)
    {
        var machine = AvailableDevices();
        _failedOn = string.Join('\n', machine);
        _trouble = machine.Count == 0
            ? new OutputTrouble.NoDevice()
            : new OutputTrouble.Refused(device.Length > 0 ? device : machine[0]);

        // The endpoint name is not logged. The log may be attached to a crash report, and a speaker
        // name can contain a person's name.
        Said.Line("audio", machine.Count == 0
            ? "no playback device to open"
            : $"the chosen endpoint would not open ({machine.Count} listed)");
    }

    /// <summary>The reason the sink cannot play, or null when it can. See
    /// <see cref="OutputTrouble"/>.</summary>
    public OutputTrouble? Trouble => _trouble;

    /// <summary>Asks the watcher to try opening the device again, even when the machine's endpoints
    /// have not changed since the last failure. This is used for a play the sink has just refused. The
    /// listener may have closed the app that held the device, and that does not change the endpoint
    /// list.</summary>
    public void TryAgain()
    {
        _retryAsked = true;
        _wake.Set();
    }

    /// <summary>Volatile, because it is written by the constructor, by a rebuild on the pump or the
    /// watcher, and by <see cref="Dispose"/>. It is read by the engine on the UI thread. The reason is
    /// cleared before this becomes true. That order holds only if neither write can be moved past the
    /// other.</summary>
    private volatile bool _available;

    public bool IsAvailable
    {
        get => _available;
        private set => _available = value;
    }

    /// <summary>Read from the device: the frames of buffers it has finished, plus the position within
    /// the buffer it is playing now.</summary>
    public long FramesPlayed
    {
        get
        {
            lock (_gate)
            {
                if (_al is null || _stopped) return _framesConsumed;
                ClaimContext();
                int offset = 0;
                try { _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out offset); } catch { }
                return _framesConsumed + offset;
            }
        }
    }

    /// <summary>How many times the device has run out of queued audio since the app started. Each count
    /// is one audible break. While a track plays, zero is the only correct value. Any other value means
    /// the pump missed its deadline, and the queue was not deep enough to cover it.</summary>
    public long Underruns => Volatile.Read(ref _underruns);

    public void Begin(SinkTrack track, int sampleRate, int channels)
    {
        if (!IsAvailable) return;

        // The context is claimed before anything is asked about a source. Every method claims the
        // context for itself (see ClaimContext). Claiming once per track was enough only while no two
        // sinks overlapped. Two sinks can overlap. The opening sound may still be playing when a record
        // starts, and it stops at the moment this line takes the context. If the context were not
        // claimed here, the opening sound could stop, empty and delete this sink's source by its
        // number. The record would then play to nobody.
        ClaimContext();

        // A track that starts after the speakers changed plays on the new speakers. This only wakes the
        // watcher. Nothing here asks the driver anything.
        _wake.Set();

        FlushDevice();

        lock (_gate)
        {
            _rate = sampleRate;
            _channels = channels;
            _framesConsumed = 0;
            _queuedFrames.Clear();
            _free.Clear();
            foreach (var b in _buffers) _free.Push(b);
            _paused = false;
            _stopped = false;
            _pumpAttached = true;
            _started = false;
            _dry = false;
        }

        _lastProgress = Environment.TickCount64;
        _silence.Heard();
        _played.Restart();

        SetVolume(_volume);
    }

    public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
    {
        // A false return ends the pump, and the engine stops feeding it. This is the pump's last call.
        // From then on, the watcher carries out device moves (see Carrier).
        if (EnqueueCore(interleaved, frames)) return true;
        _pumpAttached = false;
        return false;
    }

    private bool EnqueueCore(ReadOnlySpan<float> interleaved, int frames)
    {
        if (_al is null) return false;

        // Not available and not lost means the sink never opened. There is nothing to wait for. Not
        // available while lost means a rebuild did not succeed. The machine may still return that
        // device. In that case the pump waits in the loop below instead of returning. A false return
        // would end the pump and the track, even though a speaker may be plugged back in a few seconds
        // later.
        if (!IsAvailable && !_deviceLost) return false;

        // The pump's own thread claims the context once for the whole block. Everything below uses this
        // sink's source: the queueing, the reclaiming, and the restart after a break. A claim made by
        // another thread says nothing about this source.
        //
        // Every call into the library from here on is timed, so the underrun log line can say whether
        // the block was late because the library held it. See _alMs.
        long alTicks = 0;
        long claiming = Stopwatch.GetTimestamp();
        ClaimContext();
        alTicks += Stopwatch.GetTimestamp() - claiming;

        int samples = frames * _channels;
        if (samples <= 0) return !_stopped;
        if (_scratch.Length < samples) _scratch = new short[samples];

        for (int i = 0; i < samples; i++)
            _scratch[i] = (short)(Math.Clamp(interleaved[i], -1f, 1f) * short.MaxValue);

        while (!_stopped)
        {
            // A speaker change decided elsewhere is carried out here. Moving this sink's device is the
            // pump's job. When there is nothing to do, the check costs only a volatile read. Nothing
            // else about devices runs on this thread. See the watcher.
            if (_moveTo is { } target)
            {
                _moveTo = null;
                long moving = Stopwatch.GetTimestamp();
                MoveTo(target);
                alTicks += Stopwatch.GetTimestamp() - moving;
            }

            // A rebuild did not succeed, so there is no source to queue on and no buffers to fill. The
            // block is held where it is, and the watcher keeps trying. This is the same wait as for a
            // lost device. It ends in one of two ways: a move that succeeds, or the give-up.
            if (!IsAvailable)
            {
                lock (_gate) Monitor.Wait(_gate, 5);
                continue;
            }

            long reclaiming = Stopwatch.GetTimestamp();
            Reclaim();
            alTicks += Stopwatch.GetTimestamp() - reclaiming;

            uint buffer;
            lock (_gate)
            {
                if (_free.Count == 0 || _queuedFrames.Count >= _inFlight)
                {
                    // Every buffer is with the device. There is nothing to do except wait for one to
                    // play. This wait sets the pace of the decoder. While paused, it does not end.
                    //
                    // The wait ends early when the device is not going to play one. A device returns a
                    // buffer every 50 ms. A silence this long means the device no longer exists. That
                    // is detected here, instead of after the five seconds the machine may take to
                    // report it. The track is held in place until there is somewhere to play it.
                    //
                    // The silence is counted only in the waits this loop actually performed. It is not
                    // counted in wall clock time. Wall clock time includes the time this thread was not
                    // running. A managed thread is paused by every garbage collection in the process.
                    // In one test, 868 ms of one second passed this way while the grid was rebuilt. A
                    // healthy device was then declared dead, the track was held, and the music stopped
                    // ten seconds later. The app blamed the speakers for its own pause. Only what this
                    // loop has watched can be trusted.
                    //
                    // The silence counts only while the device owes audio. A paused source returns
                    // nothing, so a wait during a pause is not counted. If it were, the first look
                    // after play would read the whole pause as a dead speaker.
                    if (!_deviceLost && _silence.PresumeLost(NoProgressMs, owed: !_paused && _started))
                        Presume(lost: true, $"nothing came back across {_silence.WatchedMs}ms of watching");

                    KeepPlaying();
                    Monitor.Wait(_gate, StallPollMs);
                    _silence.Waited(StallPollMs, owed: !_paused && _started);
                    continue;
                }
                buffer = _free.Pop();
            }

            AudioError refused;
            long handed = Stopwatch.GetTimestamp();
            try
            {
                _al.GetError();     // clear what was standing, so the flag below is about these two calls

                fixed (short* p = _scratch)
                    _al.BufferData(buffer, _channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16,
                                   p, samples * sizeof(short), _rate);
                _al.SourceQueueBuffers(_source, 1, &buffer);

                refused = _al.GetError();
                alTicks += Stopwatch.GetTimestamp() - handed;
            }
            catch
            {
                lock (_gate) _free.Push(buffer);
                return false;   // the device went away
            }

            // The error flag is checked because these calls do not throw on failure. They set a flag
            // and return. If a block the library never took were counted, the sink would believe half a
            // second of audio is with the device when it has none. It would then wait for audio to come
            // back, which will not happen before the ten second give-up. In every case the sound is
            // going nowhere. The track is held, the watcher tries to move it, and the track is stopped
            // if that never works.
            if (refused != AudioError.NoError)
            {
                lock (_gate) _free.Push(buffer);
                Presume(lost: true, $"the library would not take the block ({refused})");
                lock (_gate) Monitor.Wait(_gate, StallPollMs);
                continue;
            }

            // This is set before KeepPlaying, because KeepPlaying reads it. An underrun counted there
            // reports what the library cost for this block.
            _alMs = alTicks * 1000.0 / Stopwatch.Frequency;

            lock (_gate)
            {
                _queuedFrames.Enqueue(frames);
                KeepPlaying();      // reads the queue, so it belongs under the same lock

                // This is set after KeepPlaying, not before. An underrun counted there asks how long
                // the device waited for this block, which is the time since the previous one.
                _fedAt = Environment.TickCount64;
                _gcAtFeed = GC.GetTotalPauseDuration();
            }
            return true;
        }

        return false;
    }

    public void Drain(CancellationToken cancellation = default)
    {
        // This is the pump's last call for a track that played to its end. The pump releases the sink
        // when this returns. Nothing stops the sink until the next play.
        try { DrainCore(cancellation); }
        finally { _pumpAttached = false; }
    }

    private void DrainCore(CancellationToken cancellation)
    {
        if (!IsAvailable) return;
        ClaimContext();

        while (!_stopped && !cancellation.IsCancellationRequested)
        {
            Reclaim();
            lock (_gate)
            {
                if (_queuedFrames.Count == 0) return;

                // Nothing returns while the device is gone. A track that has already finished does not
                // need the queue kept open. The next track starts on whatever device the sound has
                // moved to.
                if (_deviceLost) return;
                KeepPlaying(force: true);    // an underrun here must not leave the tail unplayed
                Monitor.Wait(_gate, 5);
            }
        }
    }

    public void Pause()
    {
        if (!IsAvailable || _paused || _stopped) return;
        _paused = true;
        _played.Stop();     // paused time is not playing time. Claims are checked against this clock.
        ClaimContext();
        try { _al!.SourcePause(_source); } catch { }
    }

    public void Resume()
    {
        if (!IsAvailable || !_paused || _stopped) return;
        _paused = false;
        _played.Start();
        _lastProgress = Environment.TickCount64;
        ClaimContext();

        // Pressing play while the sound still has nowhere to go restarts the countdown to giving up.
        // Without this, the second attempt would hold the track forever, instead of stopping it again
        // after ten seconds.
        if (_deviceLost && _lostSince == 0)
        {
            _lostSince = Environment.TickCount64;
            _wake.Set();
        }

        try { _al!.SourcePlay(_source); } catch { }

        // Resuming counts as having started. This holds even when the pause happened during the
        // pre-roll, so the source has never played before. Reclaim stays off until the source has been
        // told to play. If it stayed off, the pump would wait forever for a buffer to come back.
        lock (_gate) { _started = true; Monitor.PulseAll(_gate); }
    }

    public void Stop()
    {
        _stopped = true;
        _pumpAttached = false;
        _paused = false;
        FlushDevice();
        lock (_gate)
        {
            _queuedFrames.Clear();
            Monitor.PulseAll(_gate);    // wake anything waiting in Enqueue or Drain
        }
    }

    public void SetVolume(int volume)
    {
        var v = Math.Clamp(volume, 0, 100);
        _volume = v;
        if (!IsAvailable) return;
        ClaimContext();
        try { _al!.SetSourceProperty(_source, SourceFloat.Gain, v / 100f); } catch { }
    }

    // ── Following the sound when the speakers change ───────────────────────────────────────────────
    //
    // A device is opened once, and OpenAL never moves it. If the Bluetooth speaker in use is
    // disconnected and another is connected, the audio keeps going to an endpoint that is gone. The
    // result is silence until the app is restarted. This was reported on Windows. The same happens on
    // every platform that OpenAL runs on.
    //
    // There are two parts: being told, and moving. Being told is handled by AudioDeviceWatch, which
    // receives the platform's own notification. Moving is handled here. This sink asks which device it
    // would choose now, compares that with the device it asked for last time, and gives the pump a
    // device name.
    //
    // A missing notification is never taken to mean that nothing changed. On a platform that reports
    // every change, the sink still checks every SafetyCheckMs. On a platform that reports only some
    // changes, it checks every DeviceCheckMs. Trusting a notification that never arrived would leave a
    // whole session playing on the wrong speakers. Not trusting it costs one device enumeration.

    /// <summary>
    /// The thread that watches the machine's speakers. It exists so that the pump does not.
    ///
    /// <para>Every question here calls the sound driver. Finding the preferred endpoint walks the list
    /// of endpoints. Asking for the list walks them and opens each one. With a Bluetooth device in the
    /// list, that takes hundreds of milliseconds, not tens. The pump has about half a second of
    /// buffered audio for all of its work. If the pump waited for an answer, the listener would hear a
    /// break.</para>
    ///
    /// <para>So this thread does the asking. All it gives the pump is a device name to move to. It
    /// sleeps until <see cref="AudioDeviceWatch"/> reports a change. It also wakes by itself. It wakes
    /// every <see cref="SafetyCheckMs"/> when the machine reports every change, and every
    /// <see cref="DeviceCheckMs"/> when it does not. It wakes every <see cref="LostRetryMs"/> while the
    /// sound has nowhere to go.</para>
    /// </summary>
    private void Watch()
    {
        while (!_closing)
        {
            _wake.Wait(DeviceWatch.SleepMs(Carrier, _deviceLost, AudioDeviceWatch.Complete));
            _wake.Reset();

            if (_closing) return;
            if (!IsAvailable && !_deviceLost) continue;

            // A null _device does not stop the looking. That state means a rebuild failed. It is the
            // state where looking matters most, because nothing else will notice that the machine has
            // speakers again.
            if (_alc is null) continue;

            try
            {
                // Read once per look. A pump that attaches or detaches during a look is seen at the
                // next look.
                switch (Carrier)
                {
                    case MoveCarrier.Pump:
                        Look();
                        break;
                    case MoveCarrier.Watcher:
                        LookWhileIdle();
                        break;
                }
            }
            catch { /* the device is going away; the pump will find out */ }
        }
    }

    /// <summary>
    /// A look while no pump is attached: the watcher decides and carries the move out itself.
    ///
    /// <para>During a track the pump carries out device moves (<see cref="Enqueue"/>). With no pump
    /// attached, a name that is handed over is never read. A first open that failed would then stay
    /// silent for the whole session. The same happens when a pair of headphones disconnects while
    /// nothing is playing: the device is marked lost, the lost state sets the watcher's 200 ms clock,
    /// and each look would hand the same name to a pump that does not exist and log it, again and
    /// again. So the watcher carries out the move itself when no pump is attached.</para>
    ///
    /// <para>The watcher moves only when the answer may have changed. That is when the machine's
    /// endpoints differ from the last failure, or when a refused play asked for a retry
    /// (<see cref="TryAgain"/>). Until one of those happens, a failure is a steady state, and the
    /// watcher checks at the normal interval (<see cref="DeviceWatch.SleepMs"/>).</para>
    /// </summary>
    private void LookWhileIdle()
    {
        // A name may have been handed to a pump that has since let go. Nobody will read it, so it is
        // decided again here.
        _moveTo = null;

        var machine = AvailableDevices();
        var listed = string.Join('\n', machine);
        var asked = _retryAsked;
        _retryAsked = false;

        if (machine.Count == 0 && !IsAvailable)
        {
            // There is nothing to open. The reason may still have changed. A device that refused may
            // have been unplugged since, and the advice for that case is to connect a device.
            if (_trouble is not OutputTrouble.NoDevice)
            {
                _trouble = new OutputTrouble.NoDevice();
                Said.Line("audio", "no playback device to open");
            }
            _failedOn = listed;
            return;
        }

        // Is the endpoint we hold still there? With no device at all, a rebuild did not succeed. That
        // answer is known without asking.
        int connected = 0;
        if (_device != null)
        {
            connected = 1;
            try { _alc!.GetContextProperty(_device, (GetContextInteger)Connected, 1, &connected); } catch { }
        }
        if (connected == 0 && IsAvailable)
            Presume(lost: true, "the driver says the endpoint is gone");

        var want = OutputRouting.ChooseFromEnumeration(PreferredDevice, machine);
        var step = DeviceWatch.Next(MoveCarrier.Watcher, want, _intended, connected != 0, _deviceLost,
            unchangedSinceFailure: !asked && string.Equals(listed, _failedOn, StringComparison.Ordinal));
        if (step is not WatcherStep.MoveNow move)
            return;

        // At most one attempt per LostRetryMs, however often the watcher is woken.
        var now = Environment.TickCount64;
        if (now < _nextDeviceMove) return;
        _nextDeviceMove = now + LostRetryMs;

        Said.Line("audio",
            asked ? "a play was refused, opening the device again"
            : !IsAvailable ? "the endpoints changed, opening the device again"
            : $"nothing playing, moving from '{_intended}' to '{move.Device}'" + (connected != 0 ? "" : " (disconnected)"));
        MoveTo(move.Device);

        // Kept so the next idle look can tell whether the machine has changed since this failure.
        _failedOn = !IsAvailable || _deviceLost ? listed : null;
    }

    private void Look()
    {
        // Is the endpoint in use still there? A "no" is a disconnect that the driver has now reported.
        // With no device at all, a rebuild did not succeed. That is a "no" that needs no asking.
        int connected = 0;
        if (_device != null)
        {
            connected = 1;
            try { _alc!.GetContextProperty(_device, (GetContextInteger)Connected, 1, &connected); } catch { }
        }

        NoteConnection(connected != 0);

        // On every look, the question that matters is asked: which device would be chosen now?
        //
        // The check is not skipped when nothing has announced a change. Skipping it is correct only
        // where the machine announces every change. Some changes are not announced. Moving the system
        // output while the old speakers are still connected disconnects nothing, and without a
        // notification nothing is announced. The endpoint in use stays present and healthy, so every
        // cheap test passes. The music then keeps playing on the speakers the listener has switched
        // away from. Without the check, this would last for the
        // whole session. So the answer is checked on every look, not inferred.
        var want = ChosenDevice();

        // The rule is in DeviceWatch, where it can be tested. Look runs only while a pump is attached,
        // so any move is handed to the pump.
        if (DeviceWatch.Next(MoveCarrier.Pump, want, _intended, connected != 0, _deviceLost,
                unchangedSinceFailure: false) is not WatcherStep.HandToPump)
            return;

        // Even when the devices differ, a move is not made more often than the cooldown allows. A move
        // is an audible seam, and a change that does not settle must not cause a series of moves.
        //
        // The cooldown does not apply while the sound has nowhere to go. There is no seam to protect
        // then, because the music is already stopped. The only goal is to be on a device again as soon
        // as one is available. Otherwise a speaker swapped during the cooldown would hold the track for
        // the rest of the cooldown, which adds silence to avoid a seam in silence.
        var now = Environment.TickCount64;
        if (now < _nextDeviceMove) return;
        _nextDeviceMove = now + (_deviceLost ? LostRetryMs : MoveCooldownMs);

        // Three different things arrive here, and only one of them is a device change. They are logged
        // separately. A log line that reports a re-open of the same endpoint as "devices changed" would
        // send the reader looking for a change that never happened. The important fact in that case is
        // that the endpoint in use stopped playing.
        Said.Line("audio",
            string.Equals(want, _intended, StringComparison.Ordinal)
                ? $"re-opening '{want}' — " +
                  (connected == 0 ? "the driver says it is gone" : "it stopped handing buffers back")
                : $"devices changed — asked for '{_intended}', now wants '{want}'" +
                  (connected != 0 ? "" : " (disconnected)"));

        _moveTo = want;
    }

    /// <summary>Marks the device as lost, or as working again. This can be called from any thread. The
    /// pump usually notices first, by itself, when nothing comes back or when more comes back than the
    /// time allows. The watcher notices when the machine reports the change, or when the driver reports
    /// it. That can take five seconds. A completed move ends the lost state.
    ///
    /// <para>When the endpoint is lost, the pump stops where it is. It does not play the rest of the
    /// track into a device that is not there. That is right for the few seconds a speaker takes to be
    /// swapped. It is not right indefinitely. A device not replaced after <see cref="StallGiveUpMs"/>
    /// is not coming back by itself, so playback is paused where it stopped. The track and the position
    /// are kept. Pressing play again, once speakers are present, continues from that
    /// position.</para>
    /// </summary>
    private void Presume(bool lost, string why)
    {
        if (_deviceLost == lost) return;

        _deviceLost = lost;

        if (lost)
        {
            // Only the first loss starts the clock. A move that fails puts the sink straight back here.
            // If the clock restarted on each attempt, the ten seconds would never pass, and playback
            // would be retried on a dead device forever.
            if (_lostSince == 0) _lostSince = Environment.TickCount64;

            Said.Line("audio", $"holding the track — {why}{AlTruth()}");

            // The watcher decides where the sound goes next. It sleeps until it is woken.
            _wake.Set();
        }
        else
        {
            _lastProgress = Environment.TickCount64;
            _silence.Heard();
            Said.Line("audio", $"playing on — {why}");
        }
    }

    private void NoteConnection(bool connected)
    {
        // When the driver reports the device as connected, the hold is not cleared. The pump's evidence
        // outranks that answer, because the driver keeps reporting a dead speaker as present for
        // several seconds. Only a move that succeeds ends the hold.
        if (!connected) Presume(lost: true, "the driver says the endpoint is gone");

        if (!_deviceLost || _lostSince == 0) return;
        if (Environment.TickCount64 - _lostSince < StallGiveUpMs) return;

        Said.Line("audio", $"nowhere to play for {StallGiveUpMs / 1000}s — pausing");
        _lostSince = 0;
        Stalled?.Invoke();
    }

    /// <summary>The sound has had nowhere to go for <see cref="StallGiveUpMs"/>. It is raised on the
    /// watcher's thread. The listener is expected to pause playback.</summary>
    public event Action? Stalled;

    /// <summary>The endpoint this sink should play to, as reported by the driver and chosen by
    /// <see cref="OutputRouting.Choose"/>. An empty value means the default. OpenAL is given that when
    /// it is asked to open or reopen without a name.
    ///
    /// <para>One enumeration answers two questions: whether the chosen endpoint is present, and what
    /// the machine's default is. The default is the first name in the list. See
    /// <see cref="AvailableDevices"/> for why the default is not requested directly.</para>
    /// </summary>
    private static string ChosenDevice() => OutputRouting.ChooseFromEnumeration(PreferredDevice, AvailableDevices());

    /// <summary>Asks the watcher to look now. This is used after a settings change, which the machine
    /// has no reason to report.</summary>
    public void FollowNow() => _wake.Set();

    /// <summary>The sound is now coming out of another endpoint, which is named. It is raised on the
    /// pump's thread. A listener that updates the screen must switch to the UI thread itself.</summary>
    public event Action<string>? DeviceChanged;

    /// <summary>Carries out a move, on whichever thread gets here first. The pump and the watcher can
    /// both arrive at the same moment (see <see cref="_moving"/>). The second one skips. The watcher's
    /// next look decides again, using the state at that time.</summary>
    private void MoveTo(string device)
    {
        if (Interlocked.CompareExchange(ref _moving, 1, 0) != 0)
            return;

        try { MoveToCore(device); }
        finally { Volatile.Write(ref _moving, 0); }
    }

    private void MoveToCore(string device)
    {
        _intended = device;
        _moved = true;

        // A reopen keeps everything in place: the context, the source, and the buffers queued on it. So
        // nothing is built again, and nothing is decoded again. Only playback has to resume. The source
        // was stopped because its endpoint stopped listening. A reopen needs a device to reopen. After
        // a rebuild that failed, there is no device.
        if (_reopen != null && _device != null)
        {
            bool moved;
            try
            {
                // A null name means "the default". That is not the same as the default device's name.
                // The name is what the device was called at this moment. Asking for the default keeps
                // the device tied to the role, not to whichever device holds the role today.
                if (device.Length == 0)
                {
                    moved = _reopen(_device, null, null) != 0;
                }
                else
                {
                    var utf8 = Encoding.UTF8.GetBytes(device + '\0');
                    fixed (byte* name = utf8) moved = _reopen(_device, name, null) != 0;
                }
            }
            catch { moved = false; }

            if (moved)
            {
                ClaimContext();
                lock (_gate)
                {
                    // Only start the source if it is not already playing. Calling alSourcePlay on a
                    // playing source rewinds it to the start of its queue. On a streaming source that
                    // replays the half second in flight. The reopen often leaves the source playing,
                    // and then this does nothing.
                    if (_started && !_paused && !_stopped && _queuedFrames.Count > 0)
                        try
                        {
                            _al!.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                            if ((SourceState)state != SourceState.Playing) _al.SourcePlay(_source);
                        }
                        catch { }
                }

                Presume(lost: false, "the sound moved to another endpoint");

                var landed = CurrentDeviceName();
                Said.Line("audio", $"moved to '{landed}'");
                DeviceChanged?.Invoke(landed);
                return;
            }
        }

        Rebuild(device);

        // A rebuild can fail. The endpoint may be gone again by the time it is opened, or the driver
        // may be between states. If that were treated as success, the hold would be cleared, a device
        // that does not exist would be reported to the settings page, and the watcher would stop
        // trying. So the hold stays, and the watcher tries again on its own short interval.
        if (!IsAvailable)
        {
            // This is logged here as well as in Presume. Presume logs nothing when the hold is already
            // on, and this is a retry that repeats.
            Said.Line("audio", $"could not open '{device}', still holding");
            Presume(lost: true, $"'{device}' could not be opened");
            return;
        }

        Presume(lost: false, "the device was rebuilt");
        DeviceChanged?.Invoke(CurrentDeviceName());
    }

    /// <summary>Tears down the whole device and opens it again. This is used for a driver that cannot
    /// reopen a device. It costs whatever audio is in flight. A reopen costs nothing.</summary>
    private void Rebuild(string device)
    {
        lock (_gate)
        {
            // Disposed, or being disposed. Dispose sets this flag before it takes the lock to tear
            // down. So a rebuild either finishes before the teardown, or does not start.
            if (_closing) return;

            IsAvailable = false;

            // The blocks still with the old device will never be heard. The decoder is already past
            // them. Counting them as played keeps the clock that the UI reads correct. The cost is that
            // about half a second of audio is lost at the change.
            while (_queuedFrames.Count > 0) _framesConsumed += _queuedFrames.Dequeue();
            _free.Clear();

            try
            {
                if (_al is not null)
                {
                    ClaimContext();
                    _al.DeleteSource(_source);
                    foreach (var b in _buffers) _al.DeleteBuffer(b);
                }
                if (_alc is not null)
                {
                    if (_context != null)
                    {
                        ReleaseContext();
                        _alc.DestroyContext(_context);
                    }
                    if (_device != null) _alc.CloseDevice(_device);
                }
            }
            catch { }

            _context = null;
            _device = null;

            // The reopen entry point came from the device, so it is cleared with the device. If it were
            // kept, a rebuild that failed would lead to a reopen call on a device that does not exist.
            _reopen = null;

            try
            {
                _device = _alc!.OpenDevice(device);
                if (_device == null)
                {
                    // This leaves the method, so the reason is set here as well as after the lock.
                    _trouble = new OutputTrouble.Refused(device);
                    return;
                }

                _context = _alc.CreateContext(_device, null);
                if (_context == null)
                {
                    // The device opened but would not make a context. It is treated as refused, the
                    // same as a device that did not open. It is closed again, so the next try starts
                    // from nothing.
                    _alc.CloseDevice(_device);
                    _device = null;
                    _trouble = new OutputTrouble.Refused(device);
                    return;
                }
                ClaimContext();

                _source = _al!.GenSource();

                // The buffers exist only if the first open got this far. After a first open that
                // failed, the array is still the empty array it started as. Every rebuild would then
                // fail on its first buffer.
                if (_buffers.Length != BufferCount) _buffers = new uint[BufferCount];
                for (int i = 0; i < BufferCount; i++)
                {
                    _buffers[i] = _al.GenBuffer();
                    _free.Push(_buffers[i]);
                }

                // The function address belongs to the device, so it is looked up again for the new
                // device.
                _reopen = _alc.IsExtensionPresent(_device, "ALC_SOFT_reopen_device")
                    ? (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)
                        _alc.GetProcAddress(_device, "alcReopenDeviceSOFT")
                    : null;

                _started = false;   // pre-roll again, instead of starting the new device on one block

                // The reason is cleared before the sink is marked available. So nothing that sees the
                // sink as available can read the old reason next to it.
                _trouble = null;
                _failedOn = null;
                IsAvailable = true;

                Said.Line("audio", $"rebuilt on '{CurrentDeviceName()}'");
            }
            catch (Exception e)
            {
                Faults.Note("audio", "rebuilding the sound device", e);
            }
        }

        if (IsAvailable)
        {
            SetVolume(_volume);
            return;
        }

        // A device was named but would not open. This is logged without asking the machine for its
        // device list. That call is slow, and this runs on the pump's thread. The watcher chose the
        // name from a list that contained it, so Refused is the answer as of that decision. The idle
        // path changes it to NoDevice when the list it has is empty.
        if (!_closing) _trouble = new OutputTrouble.Refused(device);
    }

    /// <summary>The endpoint the sound is actually coming out of, by name. The settings page shows this
    /// value. It is not always the endpoint that was asked for, for example when the chosen speaker is
    /// not connected.</summary>
    public string CurrentDevice => CurrentDeviceName();

    /// <summary>The endpoint the device is open on, by name.
    ///
    /// <para>The name is read by hand, not through Silk's typed getter. That getter converts the answer
    /// as ANSI. OpenAL Soft's names are UTF-8. Any non-ASCII character comes back garbled. For example,
    /// a speaker name with a dash in it is returned with the wrong characters. A garbled name matches
    /// nothing in the machine's own endpoint list. The Bluetooth volume control then never turns on.
    /// <see cref="AvailableDevices"/> reads its list the same way, so the two names agree. Each setting
    /// stored per endpoint uses one of them as its key.</para>
    /// </summary>
    private string CurrentDeviceName()
    {
        if (_alc is null || _device == null) return "";

        try
        {
            var getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)
                _alc.GetProcAddress(_device, "alcGetString");
            if (getString == null) return "";

            var p = getString(_device, AllDevicesSpecifier);
            if (p == null) return "";

            int len = 0;
            while (p[len] != 0) len++;
            return Encoding.UTF8.GetString(p, len);
        }
        catch { return ""; }
    }

    /// <summary>Makes this sink's context the current one, so that the AL calls that follow use it.
    ///
    /// <para>This applies to the calling thread, where the library allows it (see
    /// <see cref="_setThreadContext"/>). That keeps two sinks out of each other's sources. Every method
    /// here that calls AL claims the context first, on whichever thread it runs on. The calls come from
    /// the pump, the watcher, the worker that starts a track, and the UI. A claim made on one thread
    /// says nothing about the others.</para>
    /// </summary>
    private void ClaimContext()
    {
        if (_alc is null || _context == null) return;
        try
        {
            if (_setThreadContext != null && _setThreadContext(_context) != 0) return;
            _alc.MakeContextCurrent(_context);
        }
        catch { }
    }

    /// <inheritdoc/>
    public void ReleaseThread()
    {
        // This clears only the claim for the calling thread. ReleaseContext also clears the
        // process-wide slot. That is correct when the context itself is being destroyed, but not here.
        // This thread is ending, and the sink is not. The slot may hold another sink's context for a
        // thread that is between calls.
        //
        // There is nothing to give back when the library has no per-thread contexts. In that case the
        // slot is the whole meaning of "current", and it belongs to the process, not to this thread.
        if (_setThreadContext is null || _context == null) return;
        try { _setThreadContext(null); } catch { }
    }

    /// <summary>Releases the context before it is destroyed. The process-wide slot is cleared only when
    /// it holds this sink's context. Another sink may be playing from the slot. Clearing it would leave
    /// that sink calling into nothing, which is the same fault this code exists to prevent.</summary>
    private void ReleaseContext()
    {
        if (_alc is null) return;
        try
        {
            if (_setThreadContext != null) _setThreadContext(null);
            if (_alc.GetCurrentContext() == _context) _alc.MakeContextCurrent(null);
        }
        catch { }
    }

    /// <summary>Finds <c>alcSetThreadContext</c> once, the first time a sink is built.</summary>
    private static void FindThreadContext(ALContext alc)
    {
        if (_threadContextLookedUp) return;
        _threadContextLookedUp = true;
        try
        {
            if (alc.IsExtensionPresent(null, "ALC_EXT_thread_local_context"))
                _setThreadContext = (delegate* unmanaged[Cdecl]<Context*, byte>)
                    alc.GetProcAddress(null, "alcSetThreadContext");
        }
        catch { _setThreadContext = null; }
    }

    /// <summary>Takes back every buffer that the device has finished with, and counts the frames it
    /// played.</summary>
    private void Reclaim()
    {
        if (_al is null) return;

        // Nothing can have played before the source was first told to play. Asking anyway is dangerous.
        // A stopped source reports its whole queue as processed. During the pre-roll, this would return
        // the buffers that were just filled, count them as heard, and let the pump run through the file
        // in silence. Only the first track of a session avoided this, because its source was still in
        // the initial state and not the stopped state.
        if (!_started) return;

        // The device is gone, so its report that everything is processed cannot be trusted. See
        // _deviceLost. The queue stays as it is. The same blocks play on the new speakers, once there
        // are some. A stopped source reports everything as processed, whatever the truth is. So nothing
        // read here can show that the device is working again. Only a move shows that.
        if (_deviceLost) return;

        try
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            if (processed <= 0) return;

            // The device reports how much audio it has played. That is checked against how much time
            // has passed. A device cannot have played more audio than the real time that has passed.
            // When a device dies, the source stops and reports its whole queue as processed at once.
            // That would count half a second of audio as played in one block, and then another half
            // second, until the track is gone. The listener would lose that audio, and no notification
            // arrives in time to prevent it. The machine can take five seconds to report that a speaker
            // has gone.
            //
            // So the reported amount is capped at what the clock allows, plus a tenth for timing jitter
            // and one block of margin. A healthy device never gets near the cap. It returns only audio
            // it has really played. A dead device is cut off at the first block.
            var allowed = (long)(_played.Elapsed.TotalSeconds * _rate * 1.10) + _rate / 20 - _framesConsumed;

            for (int i = 0; i < processed; i++)
            {
                if (allowed <= 0)
                {
                    // The device claimed more than it can have played. Nothing more is returned. The
                    // pump runs out of buffers and waits, with the track exactly where it is.
                    Presume(lost: true, "the device claimed more than the clock allows");
                    return;
                }

                uint done = 0;
                _al.SourceUnqueueBuffers(_source, 1, &done);
                lock (_gate)
                {
                    var frames = _queuedFrames.Count > 0 ? _queuedFrames.Dequeue() : 0;
                    _framesConsumed += frames;
                    allowed -= frames;
                    _free.Push(done);
                    Monitor.PulseAll(_gate);
                }
            }

            // The device returned buffers, which is the one fact that ends the lost state. It also
            // cancels the countdown to giving up.
            _lastProgress = Environment.TickCount64;
            _silence.Heard();
            _lostSince = 0;
            if (_deviceLost) Presume(lost: false, "the device is handing buffers back again");
        }
        catch { /* device went away; Enqueue will notice */ }
    }

    /// <summary>Starts the source, or restarts it after an underrun. It never overrides a pause. Call
    /// it while holding <see cref="_gate"/>, or where a race is harmless.</summary>
    /// <param name="force">Start on whatever is queued, without waiting for the pre-roll. This is used
    /// for the drain at the end of a track, and for a track shorter than the pre-roll.</param>
    private void KeepPlaying(bool force = false)
    {
        if (_al is null || _paused || _stopped || _deviceLost) return;
        try
        {
            if (_queuedFrames.Count == 0) return;
            // The pre-roll must not be deeper than the queue it waits to fill. Otherwise the source
            // would never start, and the block that would complete the pre-roll would never be handed
            // over.
            if (!_started && !force && _queuedFrames.Count < Math.Min(PreRollBuffers, _inFlight - 1))
                return;

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state == SourceState.Playing) { _dry = false; return; }

            // The source is not playing, audio is still queued, and it was started before. So the
            // device ran out of buffers and stopped. This is a break in the sound. It is counted here,
            // so it can be measured.
            //
            // It is counted once per break, not once per check. This runs every StallPollMs while the
            // queue is full. A source that does not restart when told to would count an underrun and
            // write a log line every five milliseconds. That would give hundreds of counts for one
            // break. The count would then measure the poll, not the audio, and a debug write would be
            // made under _gate two hundred times a second.
            if (_started && !_moved && !_dry)
            {
                _dry = true;
                Volatile.Write(ref _underruns, _underruns + 1);

                // The gap is logged with the underrun. "Ran dry" on its own has been read in two ways,
                // and the two readings need opposite fixes. See _fedAt.
                long away = _fedAt == 0 ? 0 : Environment.TickCount64 - _fedAt;

                // This sink can account for two parts of the gap. "The pump was away" is where the
                // question starts, not where it ends. Whatever remains after these two parts happened
                // upstream. The pump reports that in its own log line.
                double gc = _fedAt == 0 ? 0 : (GC.GetTotalPauseDuration() - _gcAtFeed).TotalMilliseconds;

                Said.Line("audio", $"underrun #{_underruns} — device ran dry, {away}ms since the pump last fed it " +
                    $"(collector {gc:F0}ms, library {_alMs:F0}ms, {_queuedFrames.Count} blocks queued){AlTruth()}");
            }

            // A move stops the source by definition, because the endpoint it played to has gone. If
            // that were counted as a missed deadline, it would hide the real ones.
            _moved = false;

            _al.SourcePlay(_source);
            _started = true;
        }
        catch { }
    }

    /// <summary>What the sound library itself reports about the source. It is added to the two log
    /// lines that report a device that is not playing what it was given.
    ///
    /// <para>Every AL call in this file is a plain call into the library. It does not throw when it
    /// fails. It sets an error flag and does nothing else. An out parameter that was not written keeps
    /// its old value. So a source name that is not ours, a context that belongs to another sink, or a
    /// queue the library never received all read as "not playing". They look the same as a speaker that
    /// stopped. This report tells them apart. It gives this sink's count of what is with the device,
    /// the library's own count, and whether the library accepted the query.</para>
    /// </summary>
    private string AlTruth()
    {
        if (_al is null) return "";
        try
        {
            ClaimContext();                     // also called from the watcher's thread
            var standing = _al.GetError();      // clears an earlier error, so the reads below are clean

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            var asking = _al.GetError();

            var name = (SourceState)state switch
            {
                SourceState.Initial => "initial",
                SourceState.Playing => "playing",
                SourceState.Paused => "paused",
                SourceState.Stopped => "stopped",
                _ => $"0x{state:x}",
            };

            bool ours = _alc is not null && _context != null && _alc.GetCurrentContext() == _context;

            return $" [al: {name}, {queued} queued, {processed} processed, free {_free.Count}, " +
                   $"context {(ours ? "ours" : "ANOTHER")}, err {standing}/{asking}]";
        }
        catch { return " [al: unreadable]"; }
    }

    private void FlushDevice()
    {
        if (!IsAvailable || _al is null) return;

        // Every call below names a source by number, and that number means something in every context.
        // If the context were not claimed, this method would stop, empty and rewind whichever source
        // had that number in whichever context was current. For example, the player's source would be
        // affected when the opening sound stood down as a record started.
        ClaimContext();

        try
        {
            _al.SourceStop(_source);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            for (int i = 0; i < queued; i++)
            {
                uint done = 0;
                _al.SourceUnqueueBuffers(_source, 1, &done);
            }

            // Back to AL_INITIAL, the state a source has before it is ever played. A source left in the
            // STOPPED state reports everything queued on it as already processed. That would trap the
            // next track (see Reclaim).
            _al.SourceRewind(_source);
        }
        catch { }
    }

    public void Dispose()
    {
        // This is set first, before the stop and the teardown. The watcher can rebuild the device by
        // itself while no track is playing (ReopenWhileIdle), and Rebuild checks this flag under the
        // lock. If it were set later, a rebuild could open a device after the teardown below had closed
        // the old one.
        _closing = true;
        _wake.Set();

        Stop();

        AudioDeviceWatch.Remove(_onDevicesChanged);

        // This runs under the lock, which a rebuild holds for its whole length. A rebuild in progress
        // finishes first, and its device is the one closed here.
        lock (_gate)
        {
            try
            {
                if (_al is not null && IsAvailable)
                {
                    // The context is claimed on this thread before anything is deleted by number.
                    // Without the claim, the opening sound could delete the player's source and its
                    // buffers. The track would then keep decoding into a queue that OpenAL no longer
                    // had.
                    ClaimContext();

                    _al.DeleteSource(_source);
                    foreach (var b in _buffers) _al.DeleteBuffer(b);
                }
                if (_alc is not null)
                {
                    // Destroying the context while it is still current is undefined behaviour. It is
                    // unset first, so a surviving sink starts from a clean state to claim in Begin.
                    if (_context != null)
                    {
                        ReleaseContext();
                        _alc.DestroyContext(_context);
                    }
                    if (_device != null) _alc.CloseDevice(_device);
                }
            }
            catch { }
            IsAvailable = false;
        }
    }
}
