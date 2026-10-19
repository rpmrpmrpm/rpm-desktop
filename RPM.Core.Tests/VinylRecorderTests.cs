using RPM.Core.Audio;
using RPM.Core.Recording;

namespace RPM.Core.Tests;

public class VinylRecorderTests
{
    /// <summary>A monitor sink that counts what it is handed. <paramref name="enqueueDelayMs"/>
    /// simulates a sink that cannot keep up; like the real one it honours Stop so a parked
    /// Enqueue is released.</summary>
    private sealed class FakeSink(int enqueueDelayMs = 0) : IAudioSink
    {
        private volatile bool _stopped;
        private int _blocks;
        private float[]? _last;

        public bool IsAvailable => true;
        public long FramesPlayed => 0;
        public int Blocks => Volatile.Read(ref _blocks);
        public bool Begun { get; private set; }

        /// <summary>The most recent block handed to the sink, so a test can see what the monitor
        /// heard rather than only how many times it heard something.</summary>
        public float[]? Last => Volatile.Read(ref _last);

        /// <summary>Set as each block arrives. The meter and the monitor are fed by different
        /// threads, so waiting for a meter frame says nothing about whether the sink has been
        /// handed anything yet.</summary>
        public readonly ManualResetEventSlim Arrived = new();

        public void Begin(SinkTrack track, int sampleRate, int channels) { Begun = true; _stopped = false; }

        public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
        {
            if (_stopped) return false;
            if (enqueueDelayMs > 0)
            {
                // Wake early when stopped, exactly as the real sink's backpressure wait does.
                for (int i = 0; i < enqueueDelayMs && !_stopped; i += 5)
                    Thread.Sleep(5);
            }
            if (_stopped) return false;
            Volatile.Write(ref _last, interleaved[..(frames * 2)].ToArray());
            Interlocked.Increment(ref _blocks);
            Arrived.Set();
            return true;
        }

        public void Drain(CancellationToken cancellation = default) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() => _stopped = true;
        public void SetVolume(int volume) { }
        public void Dispose() => _stopped = true;
    }

    /// <summary>A capture source that hands back the same stereo sine block on every read: enough
    /// to exercise the meter/gain/monitoring logic without a real device or ffmpeg.</summary>
    private sealed class FakeCapture(double amplitude) : IAudioCaptureSource
    {
        public bool IsAvailable => true;
        public int SampleRate => 44100;
        public int Channels => 2;

        private readonly float[] _block = Build(amplitude);

        private static float[] Build(double amp)
        {
            int frames = 2205;
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float s = (float)(amp * Math.Sin(2 * Math.PI * 440 * f / 44100));
                b[f * 2] = s;
                b[f * 2 + 1] = s;
            }
            return b;
        }

        public IReadOnlyList<InputDevice> ListDevices() => new[] { new InputDevice("", "fake input") };
        public void Start(string? deviceId) { }
        public void Stop() { }
        public int Read(float[] buffer, int maxSamples)
        {
            int n = Math.Min(maxSamples, _block.Length);
            Array.Copy(_block, buffer, n);
            return n;
        }
        public void Dispose() { }
    }

    private static MeterFrame WaitForMeter(VinylRecorder rec, Action start)
    {
        MeterFrame? got = null;
        using var signal = new ManualResetEventSlim();
        rec.MeterUpdated += f => { got = f; signal.Set(); };
        start();
        Assert.True(signal.Wait(3000), "no meter update arrived");
        rec.Stop();
        return got!;
    }

    [Fact]
    public void Monitoring_meters_input_and_records_nothing()
    {
        using var rec = new VinylRecorder(new FakeCapture(0.5));
        var frame = WaitForMeter(rec, () => rec.StartMonitoring(null));

        // Half-scale sine: about -6 dBFS peak, not clipping.
        Assert.InRange(frame.Channels[0].PeakDb, -8f, -4f);
        Assert.False(frame.Channels[0].Clipped);
        Assert.Empty(rec.Sides);           // calibration writes no files
    }

    [Fact]
    public void Gain_scales_the_metered_level_and_can_clip()
    {
        using var rec = new VinylRecorder(new FakeCapture(0.5)) { Gain = 4f };
        var frame = WaitForMeter(rec, () => rec.StartMonitoring(null));

        // 0.5 × 4 clamps to full scale: about 0 dBFS, and clipping.
        Assert.InRange(frame.Channels[0].PeakDb, -0.6f, 0.1f);
        Assert.True(frame.Channels[0].Clipped);
    }

    private static bool FfmpegAvailable = ProbeFfmpeg();

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Records a side of roughly the requested length into the session folder.</summary>
    private static void RecordSide(VinylRecorder rec, string sessionDir, int milliseconds)
    {
        rec.StartSide(null, VinylImporter.SidePath(sessionDir, rec.Sides.Count + 1));
        Thread.Sleep(milliseconds);
        rec.Stop();
    }

    /// <summary>
    /// Side order is play order, and it has to survive the session being closed: <c>side1.flac</c> is
    /// first because of its NAME, so reordering has to move the files, not just a list.
    /// </summary>
    [Fact]
    public void Moving_a_side_swaps_the_files_on_disk()
    {
        if (!FfmpegAvailable) return;   // environment without ffmpeg: nothing to encode with

        string dir = Path.Combine(Path.GetTempPath(), $"vinylmove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var rec = new VinylRecorder(new FakeCapture(0.5));
            RecordSide(rec, dir, 200);    // side 1: short
            RecordSide(rec, dir, 700);    // side 2: clearly longer

            var before = rec.Sides;
            Assert.Equal(2, before.Count);
            Assert.True(before[0].DurationSeconds < before[1].DurationSeconds);

            Assert.True(rec.MoveSide(before[1], -1));   // put side 2 first

            var after = rec.Sides;
            // The records swapped places, and each still points at the file it describes.
            Assert.True(after[0].DurationSeconds > after[1].DurationSeconds);
            Assert.EndsWith("side1.flac", after[0].FilePath);
            Assert.EndsWith("side2.flac", after[1].FilePath);

            // The bytes moved too: side1.flac is now the long one, so a reopened session agrees.
            Assert.True(DurationOf(VinylImporter.SidePath(dir, 1)) > DurationOf(VinylImporter.SidePath(dir, 2)));
            Assert.Empty(Directory.GetFiles(dir, "*.swap"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Moving_a_side_past_either_end_does_nothing()
    {
        if (!FfmpegAvailable) return;

        string dir = Path.Combine(Path.GetTempPath(), $"vinylmove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var rec = new VinylRecorder(new FakeCapture(0.5));
            RecordSide(rec, dir, 200);
            RecordSide(rec, dir, 200);

            Assert.False(rec.MoveSide(rec.Sides[0], -1));   // already first
            Assert.False(rec.MoveSide(rec.Sides[1], +1));   // already last
            Assert.Equal(2, rec.Sides.Count);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static double DurationOf(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Properties?.Duration.TotalSeconds ?? 0;
    }

    [Fact]
    public void Devices_are_listed_from_the_source()
    {
        using var rec = new VinylRecorder(new FakeCapture(0.1));
        Assert.Single(rec.Devices());
        Assert.Equal("fake input", rec.Devices()[0].Name);
    }

    [Fact]
    public void Monitor_plays_captured_audio_when_enabled()
    {
        var sink = new FakeSink();
        using var rec = new VinylRecorder(new FakeCapture(0.5), sink) { MonitorEnabled = true };

        rec.StartMonitoring(null);
        SpinWait.SpinUntil(() => sink.Blocks > 0, 3000);
        rec.Stop();

        Assert.True(sink.Begun);
        Assert.True(sink.Blocks > 0, "monitor received no audio");
    }

    [Fact]
    public void Monitor_stays_silent_when_disabled()
    {
        var sink = new FakeSink();
        using var rec = new VinylRecorder(new FakeCapture(0.5), sink) { MonitorEnabled = false };

        // Wait on the meter, so we know capture ran before asserting nothing was monitored.
        WaitForMeter(rec, () => rec.StartMonitoring(null));

        Assert.Equal(0, sink.Blocks);
    }

    [Fact]
    public void Monitor_can_be_toggled_live()
    {
        var sink = new FakeSink();
        using var rec = new VinylRecorder(new FakeCapture(0.5), sink) { MonitorEnabled = false };

        rec.StartMonitoring(null);
        SpinWait.SpinUntil(() => false, 150);      // capture is running, monitor off
        Assert.Equal(0, sink.Blocks);

        rec.MonitorEnabled = true;                 // flip it on mid-stream
        bool started = SpinWait.SpinUntil(() => sink.Blocks > 0, 3000);
        rec.Stop();

        Assert.True(started, "monitor did not start when toggled on");
    }

    /// <summary>The contract that makes monitoring while recording safe: a sink that cannot keep up
    /// must lose monitor audio, never stall the capture pump (which would leave a hole in the
    /// recording).</summary>
    [Fact]
    public void Slow_monitor_never_stalls_capture()
    {
        var slow = new FakeSink(enqueueDelayMs: 250);   // 5× slower than the 50 ms blocks arrive
        using var rec = new VinylRecorder(new FakeCapture(0.5), slow) { MonitorEnabled = true };

        int meterFrames = 0;
        rec.MeterUpdated += _ => Interlocked.Increment(ref meterFrames);

        rec.StartMonitoring(null);
        SpinWait.SpinUntil(() => Volatile.Read(ref meterFrames) > 200, 3000);
        rec.Stop();

        // Capture kept running at full speed while the sink was slow: far more blocks captured than
        // monitored.
        Assert.True(Volatile.Read(ref meterFrames) > 200,
            $"capture stalled behind the monitor (only {meterFrames} frames)");
        Assert.True(slow.Blocks < Volatile.Read(ref meterFrames),
            "monitor kept up with capture — the drop path never engaged");
    }

    /// <summary>Peak magnitude of a block, as a plain linear amplitude.</summary>
    private static float Peak(IEnumerable<float> block) => block.Select(Math.Abs).DefaultIfEmpty(0).Max();

    /// <summary>A chain that lifts everything by 12 dB, which is impossible to miss in a level.</summary>
    private static RestoreChain LoudChain()
    {
        var gains = new double[Equalizer.Bands.Length];
        Array.Fill(gains, 12.0);
        return new RestoreChain(
            RestoreSettings.Off with { EqOn = true, Preamp = 6, Gains = gains }, 44100, 2);
    }

    [Fact]
    public void The_monitor_chain_does_not_reach_the_meter()
    {
        // The meter reads what is being recorded. A curve that is only being auditioned must not
        // move it, or the user sets the input gain against a level that is not the one in the file.
        using var rec = new VinylRecorder(new FakeCapture(0.25)) { MonitorChain = LoudChain() };
        var frame = WaitForMeter(rec, () => rec.StartMonitoring(null));

        Assert.InRange(frame.Channels[0].PeakDb, -14f, -10f);   // 0.25 is -12 dBFS
        Assert.False(frame.Channels[0].Clipped);
    }

    [Fact]
    public void The_monitor_chain_reaches_the_speakers()
    {
        var sink = new FakeSink();
        using var rec = new VinylRecorder(new FakeCapture(0.1), sink) { MonitorChain = LoudChain() };

        rec.StartMonitoring(null);
        // Wait for the sink, not for a meter frame: the monitor is drained by its own thread, and a
        // meter update says only that the pump ran, not that anything reached the speakers yet.
        Assert.True(sink.Arrived.Wait(3000), "no block reached the monitor");
        var heard = sink.Last;
        rec.Stop();

        Assert.NotNull(heard);
        // The capture is 0.1; a 12 dB lift with a 6 dB pre-amp puts it well above that.
        Assert.True(Peak(heard!) > 0.2f, $"monitor peaked at {Peak(heard!):F3}, expected the lift");
    }

    [Fact]
    public void Turning_the_monitor_back_on_resets_the_chain()
    {
        using var rec = new VinylRecorder(new FakeCapture(0.5), new FakeSink())
        {
            MonitorChain = LoudChain(),
        };

        rec.StartMonitoring(null);
        rec.MonitorEnabled = false;
        rec.MonitorEnabled = true;
        Assert.Equal(1, rec.MonitorResetsForTest);
        rec.Stop();
    }
}
