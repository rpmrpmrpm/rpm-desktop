using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Tools.PlaybackHarness;

/// <summary>A sink with the timing of a sound card: N buffers queued, each taking its real duration
/// to "play", and a frame count that increases only as they finish. Using it instead of the device
/// separates the engine from OpenAL, so you can tell which of the two has the fault.</summary>
internal sealed class FakeSink : IAudioSink
{
    private const int BufferCount = 10;

    private readonly object _gate = new();
    private readonly Queue<(int frames, long readyAt)> _queued = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _framesConsumed;
    private int _rate = 44100;
    private volatile bool _stopped = true;
    private volatile bool _paused;

    public string Name { get; init; } = "sink";

    public long TotalFramesAccepted;
    public int BeginCount;

    public bool IsAvailable => true;

    public long FramesPlayed { get { lock (_gate) { Reclaim(); return _framesConsumed; } } }

    public void Begin(SinkTrack track, int sampleRate, int channels)
    {
        lock (_gate)
        {
            BeginCount++;
            _rate = sampleRate;
            _queued.Clear();
            _framesConsumed = 0;
            _stopped = false;
            _paused = false;
        }
        Log($"Begin({track.Title}) rate={sampleRate} ch={channels}");
    }

    public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
    {
        while (!_stopped)
        {
            lock (_gate)
            {
                Reclaim();
                if (_queued.Count < BufferCount)
                {
                    long ms = _queued.Count == 0
                        ? _clock.ElapsedMilliseconds
                        : _queued.Last().readyAt;
                    _queued.Enqueue((frames, ms + frames * 1000L / _rate));
                    Interlocked.Add(ref TotalFramesAccepted, frames);
                    return true;
                }
            }
            Thread.Sleep(2);
        }
        return false;
    }

    public void Drain(CancellationToken cancellation = default)
    {
        while (!_stopped && !cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                Reclaim();
                if (_queued.Count == 0) return;
            }
            Thread.Sleep(2);
        }
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void Stop()
    {
        _stopped = true;
        lock (_gate) _queued.Clear();
        Log("Stop");
    }

    public void SetVolume(int volume) { }

    public void Dispose() => _stopped = true;

    private void Reclaim()
    {
        long now = _clock.ElapsedMilliseconds;
        while (_queued.Count > 0 && _queued.Peek().readyAt <= now && !_paused)
            _framesConsumed += _queued.Dequeue().frames;
    }

    private void Log(string message) => Console.WriteLine($"[{_clock.ElapsedMilliseconds,6} ms] {Name}: {message}");
}

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static void Log(string message) => Console.WriteLine($"[{Clock.ElapsedMilliseconds,6} ms] {message}");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: harness <file1> <file2> [--real] [--playlist|--edges|--churn]");
            Console.WriteLine("       harness <file> --real --devices    watch the sound follow the speakers");
            return 2;
        }

        // --real uses the actual sound device, which a fake sink cannot test.
        bool real = args.Contains("--real");
        var sink = new FakeSink();
        IAudioSink target = real ? new OpenAlSink() : sink;
        if (real) Log($"real OpenAL sink, available={target.IsAvailable}");

        using var engine = new AudioEngine(target);
        engine.SetVolume(0);   // silent: this is a measurement

        engine.Changed += () => Log($"  event Changed        path={Trim(engine.PlayingPath)} pos={engine.PositionSeconds:F2}");
        engine.TrackEnded += () => Log($"  event TrackEnded     path={Trim(engine.PlayingPath)}");

        if (args.Contains("--playlist"))
            return await Playlist(engine, target, args.Where(a => !a.StartsWith("--")).ToArray());

        if (args.Contains("--edges"))
            return await Edges(engine, target, args.Where(a => !a.StartsWith("--")).ToArray());

        if (args.Contains("--churn"))
            return await Churn(engine, target, args.Where(a => !a.StartsWith("--")).ToArray());

        if (args.Contains("--devices"))
            return await Devices(engine, target, args.Where(a => !a.StartsWith("--")).ToArray());

        Log($"--- play #1: {Path.GetFileName(args[0])}");
        engine.Play(args[0], 30, 0, null, "one");
        double first = await Report(engine, target, seconds: 3);

        Log($"--- play #2: {Path.GetFileName(args[1])}  (the case that fails in the app)");
        engine.Play(args[1], 30, 0, null, "two");
        double second = await Report(engine, target, seconds: 3);

        Log($"--- play #3: {Path.GetFileName(args[0])} again");
        engine.Play(args[0], 30, 0, null, "three");
        double third = await Report(engine, target, seconds: 3);

        Console.WriteLine();
        Console.WriteLine($"seconds actually played: #1={first:F2}  #2={second:F2}  #3={third:F2}   (~2.5 expected each)");
        if (target is OpenAlSink al) Console.WriteLine($"underruns: {al.Underruns}");
        bool pass = first > 1.5 && second > 1.5 && third > 1.5;
        Console.WriteLine(pass ? "PASS: every play reached the device" : "FAIL: a play produced no sound");
        return pass ? 0 : 1;
    }

    /// <summary>Checks that the sound follows the output device when it changes. This test cannot use a
    /// fake: it needs a real machine with real endpoints and a person to unplug them.
    ///
    /// <para>Plays one track at full length and prints every move, every hold and every give-up when it
    /// happens, with a heartbeat line in between. Run it and then, one at a time: move the system
    /// output to another device, move it back, unplug the endpoint it is playing to, plug it back in,
    /// and (with a device selected in settings) unplug the selected one. Each should show a
    /// DeviceChanged within a second, and the position should keep increasing through it.</para>
    ///
    /// <para>It also prints the source of device-change notifications on this machine, because "it did
    /// not follow" and "it was never notified" are different faults with different fixes.</para></summary>
    private static async Task<int> Devices(AudioEngine engine, IAudioSink target, string[] files)
    {
        if (target is not OpenAlSink local)
        {
            Console.WriteLine("--devices needs --real: there is nothing to follow without a sound device.");
            return 2;
        }

        Log($"device news: {OpenAlSink.DeviceNews}");
        Log($"the machine lists (its default first): {string.Join("  |  ", OpenAlSink.AvailableDevices())}");

        local.DeviceChanged += device => Log($"  event DeviceChanged  now playing to '{device}'");
        local.Stalled += () => Log("  event Stalled        nowhere to play — the app pauses here");

        // Full length, not the usual clip: this is a manual test that takes minutes.
        engine.Play(files[0], 3600, 0, null, "devices");

        Console.WriteLine();
        Console.WriteLine("Switch the system output, unplug the endpoint, plug it back in. Ctrl-C to stop.");
        Console.WriteLine();

        double peak = 0;
        for (int second = 0; second < 600 && engine.PlayingPath is not null; second++)
        {
            await Task.Delay(1000);
            peak = Math.Max(peak, engine.PositionSeconds);

            if (second % 5 == 4)
                Log($"on '{local.CurrentDevice}'  pos={engine.PositionSeconds:F1}s  underruns={local.Underruns}");
        }

        Console.WriteLine();
        Console.WriteLine($"reached {peak:F1}s, underruns: {local.Underruns}");
        return 0;
    }

    /// <summary>What the amp does: play a track, and when it ends, start the next one. Also a
    /// pause/resume in the middle of the second track, because pausing during the pre-roll is a
    /// separate cause of hangs. Every track must produce sound and the gap between them must stay
    /// small.</summary>
    private static async Task<int> Playlist(AudioEngine engine, IAudioSink target, string[] files)
    {
        var played = new List<(string file, double seconds, long gapMs)>();
        var lastEnd = new System.Diagnostics.Stopwatch();
        int index = 0;
        var done = new TaskCompletionSource();
        double peakPosition = 0;   // the position resets when a track ends, so keep its highest value

        void Next()
        {
            if (index >= files.Length) { done.TrySetResult(); return; }
            var file = files[index++];
            long gap = lastEnd.IsRunning ? lastEnd.ElapsedMilliseconds : 0;
            Log($"--- track {index}: {Path.GetFileName(file)} (gap since last end: {gap} ms)");
            played.Add((file, 0, gap));
            peakPosition = 0;
            engine.Play(file, 30, 0, null, $"track{index}");
        }

        engine.TrackEnded += () =>
        {
            lastEnd.Restart();
            var last = played[^1];
            played[^1] = (last.file, peakPosition, last.gapMs);
            Log($"  TrackEnded, that track reached {peakPosition:F2}s");
            Next();
        };

        Next();

        // Pause/resume in the middle of the second track.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3500);
            Log("  >> Pause");
            engine.Pause();
            await Task.Delay(1000);
            Log("  >> Resume");
            engine.Resume();
        });

        var monitor = Task.Run(async () =>
        {
            while (!done.Task.IsCompleted)
            {
                await Task.Delay(100);
                double pos = engine.PositionSeconds;
                if (pos > peakPosition) peakPosition = pos;
            }
        });

        await Task.WhenAny(done.Task, Task.Delay(60_000));
        await monitor;

        Console.WriteLine();
        bool pass = played.Count == files.Length;
        foreach (var (file, seconds, gap) in played)
        {
            bool ok = seconds > 1.0 && gap < 1500;
            Console.WriteLine($"  {Path.GetFileName(file),-12} played={seconds:F2}s gap={gap}ms {(ok ? "ok" : "BAD")}");
            pass &= ok;
        }
        if (target is OpenAlSink al) Console.WriteLine($"underruns: {al.Underruns}");
        Console.WriteLine(pass ? "PASS: the whole list played, one track after another" : "FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>The two ways a pre-roll can stop playback: a track shorter than the pre-roll itself
    /// (it must still play, and must still report that it ended), and a pause during the pre-roll,
    /// before the source has started playing.</summary>
    private static async Task<int> Edges(AudioEngine engine, IAudioSink target, string[] files)
    {
        bool pass = true;

        Log($"--- a track shorter than the pre-roll: {Path.GetFileName(files[0])}");
        var ended = new TaskCompletionSource();
        void OnEnded() => ended.TrySetResult();
        engine.TrackEnded += OnEnded;
        engine.Play(files[0], 0.1, 0, null, "tiny");
        bool sawEnd = await Task.WhenAny(ended.Task, Task.Delay(5000)) == ended.Task;
        engine.TrackEnded -= OnEnded;
        Log(sawEnd ? "    ended, as it should" : "    NEVER ENDED (stranded)");
        pass &= sawEnd;

        Log($"--- pause inside the pre-roll, then resume: {Path.GetFileName(files[1])}");
        engine.Play(files[1], 30, 0, null, "paused-early");
        await Task.Delay(20);          // during the pre-roll
        engine.Pause();
        Log("    paused");
        await Task.Delay(700);
        engine.Resume();
        Log("    resumed");

        double before = engine.PositionSeconds;
        await Task.Delay(1500);
        double after = engine.PositionSeconds;
        Log($"    position {before:F2} -> {after:F2}");
        bool moved = after > before + 0.5;
        pass &= moved;
        if (target is OpenAlSink al) Console.WriteLine($"underruns: {al.Underruns}");

        Console.WriteLine(pass ? "PASS: both edges survive the pre-roll" : "FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>Pressing next many times quickly: a burst of plays must leave exactly one track playing.</summary>
    private static async Task<int> Churn(AudioEngine engine, IAudioSink target, string[] files)
    {
        for (int i = 0; i < 12; i++)
        {
            var file = files[i % files.Length];
            engine.Play(file, 30, 0, null, $"churn{i}");
            await Task.Delay(i < 6 ? 40 : 250);     // faster than the decoder can start, then slower
        }

        Log("  burst done, letting it settle");
        await Task.Delay(1500);
        double before = engine.PositionSeconds;
        await Task.Delay(1000);
        double after = engine.PositionSeconds;

        Log($"  position {before:F2} -> {after:F2}");
        if (target is OpenAlSink al) Console.WriteLine($"underruns: {al.Underruns}");
        bool pass = after > before + 0.5 && engine.PlayingPath is not null;
        Console.WriteLine(pass ? "PASS: still playing after the burst" : "FAIL: the burst left it stuck");
        return pass ? 0 : 1;
    }

    /// <summary>Waits a few seconds and returns how far the play position got.</summary>
    private static async Task<double> Report(AudioEngine engine, IAudioSink target, int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            await Task.Delay(500);
            Log($"  t+{(i + 1) * 500,4} path={Trim(engine.PlayingPath)} pos={engine.PositionSeconds:F2}"
                + (target is OpenAlSink al ? $" underruns={al.Underruns}" : ""));
        }
        return engine.PositionSeconds;
    }

    private static string Trim(string? path) => path is null ? "(null)" : Path.GetFileName(path);
}
