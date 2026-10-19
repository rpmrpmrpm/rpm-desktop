using System.Collections.Concurrent;
using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Fingerprint;

namespace RPM.Core.Recording;

/// <summary>
/// Drives a single vinyl-import session. It pulls PCM off an <see cref="IAudioCaptureSource"/> on a
/// background thread, applies the calibration gain, and feeds a live peak meter. It can also monitor
/// through the speakers. When recording a side, it pipes the samples into ffmpeg, which writes one
/// lossless FLAC per side.
///
/// <para>Two modes share the same pump. <see cref="StartMonitoring"/> is the calibration path. It runs
/// the meter and the monitor, with nothing written to disk, so the user can drop the needle on a few
/// passages and set the level before committing. <see cref="StartSide"/> adds the encoder and captures
/// the side to a file. Both modes can monitor. <see cref="MonitorEnabled"/> is honoured live in either
/// mode.</para>
///
/// <para><b>Monitoring never affects the pace of the capture thread.</b> A sink's Enqueue blocks by
/// contract, because it is the playback clock. If the pump blocked, it would overrun the capture ring
/// and leave gaps in the recording. So monitor blocks go through a small bounded queue, which a
/// dedicated thread drains. If the sink falls behind, the queue drops audio. That is a monitoring
/// glitch, and the recording stays intact. Speaker sound cannot leak back in either, because a
/// turntable arrives at line level, not through a microphone.</para>
///
/// <para><see cref="MonitorChain"/> is applied to the monitor's copy of each block, and to nothing
/// else. The encoder receives the raw capture, and the meter measures it. So a curve being auditioned
/// never lands in the file, and it never changes the level that the input gain is being set against.
/// The restoration is applied to the album when it is cut, not here.</para>
///
/// <para>Sides accumulate in <see cref="Sides"/>. The wizard concatenates and splits them once the user
/// is done.</para>
/// </summary>
public sealed class VinylRecorder(IAudioCaptureSource source, IAudioSink? monitor = null) : IDisposable
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH
    /// lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    private const int ReadBlockFrames = 2205;   // About 50 ms at 44.1 kHz, like the playback engine

    private readonly object _gate = new();
    private readonly List<RecordedSide> _sides = new();

    /// <summary>The number of monitor blocks that can queue before new blocks are dropped. Eight blocks
    /// of about 50 ms is 400 ms, which is more than the sink's own buffering. So only a real stall
    /// loses audio.</summary>
    private const int MonitorQueueBlocks = 8;

    private Thread? _pump;
    private Thread? _monitorPump;
    private BlockingCollection<float[]>? _monitorQueue;
    private volatile bool _running;
    private Process? _encoder;
    private Stream? _encoderStdin;
    private Task<string>? _encoderError;
    private string? _sidePath;
    private int _sideIndex;
    private long _framesWritten;

    /// <summary>Linear input gain applied to every captured sample before metering, monitoring and
    /// recording. 1.0 = unity. Settable live so the calibration slider takes effect
    /// immediately.</summary>
    public float Gain { get; set; } = 1f;

    private volatile bool _monitorEnabled = true;
    private int _monitorResets;

    /// <summary>Plays the captured audio through <paramref name="monitor"/>. This is honoured live, in
    /// both modes. Flipping it during a recording starts or stops the speakers without touching the
    /// capture.</summary>
    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (value == _monitorEnabled) return;
            _monitorEnabled = value;
            // The chain's filters hold the audio either side of them. While the monitor is off the pump
            // stops feeding it, so what it is holding is from a different moment of the record and
            // would ring into the first block it gets back.
            if (value)
            {
                MonitorChain?.Reset();
                Interlocked.Increment(ref _monitorResets);
            }
        }
    }

    /// <summary>Restoration applied to what the monitor plays, and to nothing else. Set while running:
    /// the pump reads it per block, so a fader moved on the UI thread is heard on the next one. Null is
    /// no processing.</summary>
    public RestoreChain? MonitorChain { get; set; }

    internal int MonitorResetsForTest => Volatile.Read(ref _monitorResets);

    /// <summary>The format the capture is running at, so a caller building a chain for the monitor does
    /// not have to know it separately from the recorder that will run it.</summary>
    public int SampleRate => source.SampleRate;

    public int Channels => source.Channels;

    /// <summary>Fired about 20 times a second with the latest per-channel levels. It is raised on the
    /// capture thread, so marshal to the UI thread before touching controls.</summary>
    public event Action<MeterFrame>? MeterUpdated;

    public IReadOnlyList<RecordedSide> Sides
    {
        get { lock (_gate) return _sides.ToArray(); }
    }

    public bool IsRecording { get; private set; }

    public bool IsMonitoring { get; private set; }

    /// <summary>Seconds captured into the side currently recording (0 when not recording).</summary>
    public double CurrentElapsedSeconds =>
        IsRecording ? Interlocked.Read(ref _framesWritten) / (double)source.SampleRate : 0;

    /// <summary>Devices the backend can open, for the picker.</summary>
    public IReadOnlyList<InputDevice> Devices() => source.ListDevices();

    public bool IsAvailable => source.IsAvailable;

    /// <summary>Calibration mode: meter (and optional monitor) with nothing written to disk.</summary>
    public void StartMonitoring(string? deviceId)
    {
        StopInternal();
        source.Start(deviceId);
        IsMonitoring = true;
        IsRecording = false;
        StartPump();
    }

    /// <summary>Recording mode: capture one side to <paramref name="outputPath"/> (FLAC).</summary>
    public void StartSide(string? deviceId, string outputPath)
    {
        StopInternal();
        source.Start(deviceId);
        StartEncoder(outputPath);
        _sidePath = outputPath;
        Interlocked.Exchange(ref _framesWritten, 0);
        IsRecording = true;
        IsMonitoring = false;
        StartPump();
    }

    /// <summary>Stops the current mode. When recording, finalises the side, appends it to
    /// <see cref="Sides"/> and returns it; otherwise returns null.</summary>
    public RecordedSide? Stop()
    {
        bool wasRecording = IsRecording;
        string? path = _sidePath;

        StopInternal();   // joins the pump, so every captured frame is counted before we read it

        if (!wasRecording || path is null)
            return null;

        long frames = Interlocked.Read(ref _framesWritten);
        var side = new RecordedSide(++_sideIndex, path, frames / (double)source.SampleRate);
        lock (_gate) _sides.Add(side);
        return side;
    }

    /// <summary>Discards a side already recorded (user chose to re-do it), deleting its file.</summary>
    public void DiscardSide(RecordedSide side)
    {
        lock (_gate) _sides.Remove(side);
        try { File.Delete(side.FilePath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Moves a side one place earlier or later. Recording order is not always play order. For example,
    /// side B may go on the platter first. The order here is the order the sides are joined in, which
    /// is the order the tracks come out numbered.
    ///
    /// <para>The files are renamed to match, because the order has to survive the session being closed
    /// and reopened. <c>side1.flac</c> is first because of its name, not because of any list held in
    /// memory. The swap goes via a temporary name. That works on a case-insensitive filesystem, and a
    /// side is not destroyed if the swap fails halfway.</para>
    /// </summary>
    public bool MoveSide(RecordedSide side, int delta)
    {
        lock (_gate)
        {
            int from = _sides.IndexOf(side);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= _sides.Count)
                return false;

            var other = _sides[to];
            string tempPath = side.FilePath + ".swap";
            try
            {
                File.Move(side.FilePath, tempPath);
                File.Move(other.FilePath, side.FilePath);
                File.Move(tempPath, other.FilePath);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Move(tempPath, side.FilePath); } catch { }
                return false;
            }

            // The records now describe each other's files: swap the paths back onto them.
            _sides[from] = other with { FilePath = side.FilePath, Index = from + 1 };
            _sides[to] = side with { FilePath = other.FilePath, Index = to + 1 };
            return true;
        }
    }

    private void StartPump()
    {
        // The monitor is opened for both modes; MonitorEnabled decides per block whether anything is
        // actually fed to it, so the toggle works live without restarting capture.
        if (monitor is { IsAvailable: true })
        {
            monitor.Begin(new SinkTrack("", "input", null, null, 0, 0), source.SampleRate, source.Channels);
            _monitorQueue = new BlockingCollection<float[]>(MonitorQueueBlocks);
            _monitorPump = new Thread(MonitorPump) { IsBackground = true, Name = "vinyl-monitor" };
            _monitorPump.Start();
        }

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "vinyl-capture" };
        _pump.Start();
    }

    /// <summary>Drains monitor blocks into the sink. Waiting here on the sink's backpressure is fine.
    /// That is this thread's problem, and never the capture pump's.</summary>
    private void MonitorPump()
    {
        var queue = _monitorQueue;
        if (queue is null || monitor is null) return;
        try
        {
            foreach (var block in queue.GetConsumingEnumerable())
            {
                try { monitor.Enqueue(block, block.Length / source.Channels); }
                catch { /* sink went away; keep draining so the pump's TryAdd never wedges */ }
            }
        }
        catch { /* queue disposed underneath us on stop */ }
    }

    private void Pump()
    {
        var buffer = new float[ReadBlockFrames * source.Channels];
        var pcm16 = new byte[buffer.Length * sizeof(short)];
        int channels = source.Channels;

        while (_running)
        {
            int samples = source.Read(buffer, buffer.Length);
            if (samples <= 0)
            {
                Thread.Sleep(3);
                continue;
            }

            if (Gain != 1f)
            {
                float g = Gain;
                for (int i = 0; i < samples; i++)
                    buffer[i] = Math.Clamp(buffer[i] * g, -1f, 1f);
            }

            var span = buffer.AsSpan(0, samples);
            try { MeterUpdated?.Invoke(PeakMeter.Measure(span, channels)); } catch { }

            if (IsRecording && _encoderStdin is not null)
            {
                int bytes = WritePcm16(span, pcm16);
                try
                {
                    _encoderStdin.Write(pcm16, 0, bytes);
                    Interlocked.Add(ref _framesWritten, samples / channels);
                }
                catch { _running = false; }   // The encoder died, so stop. Stop() reports the error
            }

            // Hand the monitor its own copy and move on. TryAdd never blocks: a full queue means the
            // speakers are behind, and dropping a block there beats stalling the recording.
            //
            // The chain runs on this copy and nowhere else. The encoder above was given the raw capture
            // and the meter measured it, because what is being recorded is not the same thing as what
            // is being auditioned.
            if (_monitorEnabled && _monitorQueue is { IsAddingCompleted: false } queue)
            {
                var copy = new float[samples];
                span.CopyTo(copy);
                // Read once into a local. The UI thread can swap the chain between any two statements
                // here, and a block half processed by one chain and half by another would not be the
                // sound either of them describes.
                var chain = MonitorChain;
                chain?.Process(copy, samples / channels);
                try { queue.TryAdd(copy); } catch { /* completed underneath us */ }
            }
        }
    }

    private static int WritePcm16(ReadOnlySpan<float> samples, byte[] dest)
    {
        int b = 0;
        foreach (var s in samples)
        {
            short v = (short)(Math.Clamp(s, -1f, 1f) * short.MaxValue);
            dest[b++] = (byte)(v & 0xFF);
            dest[b++] = (byte)((v >> 8) & 0xFF);
        }
        return b;
    }

    private void StartEncoder(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(source.SampleRate.ToString());
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(source.Channels.ToString());
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
        // A lossless intermediate. The final per-track format is applied when the album is cut.
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
        psi.ArgumentList.Add("-compression_level"); psi.ArgumentList.Add("5");
        psi.ArgumentList.Add(outputPath);

        _encoder = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg to record the side");
        _encoderStdin = _encoder.StandardInput.BaseStream;
        _encoderError = _encoder.StandardError.ReadToEndAsync();
    }

    private void StopInternal()
    {
        _running = false;
        _pump?.Join();
        _pump = null;

        // Capture has stopped, so nothing more can be queued. The sink must be stopped BEFORE the join.
        // The monitor thread may be waiting in Enqueue's backpressure, and only Stop releases that
        // wait. Joining first would hang. For live monitoring, discarding the queued tail is the right
        // result anyway.
        _monitorQueue?.CompleteAdding();
        monitor?.Stop();
        _monitorPump?.Join();
        _monitorPump = null;
        _monitorQueue?.Dispose();
        _monitorQueue = null;

        try { source.Stop(); } catch { }

        if (_encoder is not null)
        {
            try { _encoderStdin?.Flush(); _encoderStdin?.Close(); } catch { }
            try
            {
                if (!_encoder.WaitForExit(15_000))
                    _encoder.Kill();
            }
            catch { }
            string err = "";
            try { err = _encoderError?.GetAwaiter().GetResult() ?? ""; } catch { }
            int exit = -1;
            try { exit = _encoder.ExitCode; } catch { }
            _encoder.Dispose();
            _encoder = null;
            _encoderStdin = null;
            _encoderError = null;

            if (exit != 0 && IsRecording)
            {
                IsRecording = false;
                throw new InvalidOperationException(
                    $"ffmpeg failed writing the side: {FfmpegSaid.Short(err)}");
            }
        }

        IsRecording = false;
        IsMonitoring = false;
        _sidePath = null;
    }

    public void Dispose()
    {
        try { StopInternal(); } catch { }
        source.Dispose();
        monitor?.Dispose();
    }
}
