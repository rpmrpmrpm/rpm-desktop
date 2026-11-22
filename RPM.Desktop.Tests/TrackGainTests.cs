using System.Collections.Concurrent;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// The levelling gain belongs to one track, and only that track's samples are multiplied by it.
///
/// <para>The engine takes the gain with the start request and applies it on its worker, after the old
/// track's pump has stopped. Each source below plays one constant value, so every block the speaker
/// receives says which track it came from and at what gain it was played.</para>
/// </summary>
public class TrackGainTests
{
    private const float A = 0.5f;
    private const float B = 0.25f;

    [Fact]
    public void A_new_records_gain_never_reaches_the_record_still_playing()
    {
        var speaker = new Speaker();
        using var engine = new AudioEngine(speaker);

        engine.PlaySource(new Constant("a", A), durationSeconds: 0, gainDb: 0, path: "/library/a.flac");
        Assert.True(speaker.Heard(A, 1f), "the first record never reached the speaker");

        engine.PlaySource(new Constant("b", B), durationSeconds: 0, gainDb: -12, path: "/library/b.flac");
        var b = B * ReplayGain.ToLinear(-12);
        Assert.True(speaker.Heard(B, ReplayGain.ToLinear(-12)), "the second record never reached the speaker");

        // Every block is one of the two records at its own gain. A block of the first record at the second
        // record's gain, or of the second at no gain, is the fault.
        foreach (var level in speaker.Levels())
            Assert.True(Near(level, A) || Near(level, b), $"a block was played at {level}, expected {A} or {b}");
    }

    [Fact]
    public void A_station_started_after_a_levelled_record_plays_as_it_comes()
    {
        var speaker = new Speaker();
        using var engine = new AudioEngine(speaker);

        engine.PlaySource(new Constant("a", A), durationSeconds: 0, gainDb: -9, path: "/library/a.flac");
        Assert.True(speaker.Heard(A, ReplayGain.ToLinear(-9)), "the record never reached the speaker");

        engine.PlaySource(new Constant("station", B), durationSeconds: 0, gainDb: 0);
        Assert.True(speaker.Heard(B, 1f), "the station was not played at its own level");
    }

    [Fact]
    public void A_record_moved_to_another_speaker_keeps_its_gain()
    {
        var here = new Speaker();
        using var engine = new AudioEngine(here);

        engine.PlaySource(new Constant("a", A), durationSeconds: 0, gainDb: -6, path: "/library/a.flac");
        Assert.True(here.Heard(A, ReplayGain.ToLinear(-6)), "the record never reached the first speaker");

        var there = new Speaker();
        engine.SetSink(there, ownsSink: true);

        Assert.True(there.Heard(A, ReplayGain.ToLinear(-6)), "the record reached the new speaker without its gain");
        Assert.Equal(-6, there.TrackGainDb, precision: 6);
        foreach (var level in there.Levels())
            Assert.True(Near(level, A * ReplayGain.ToLinear(-6)), $"a block was played at {level}");
    }

    private static bool Near(float level, float expected) => Math.Abs(level - expected) < 1e-4f;

    /// <summary>A speaker that makes no sound and writes down the level of every block it is given.</summary>
    private sealed class Speaker : IAudioSink
    {
        private readonly ConcurrentQueue<float> _levels = new();
        private long _frames;

        public bool IsAvailable => true;
        public long FramesPlayed => Interlocked.Read(ref _frames);

        /// <summary>The last gain the engine sent, in dB. Only a surround cast acts on it.</summary>
        public double TrackGainDb { get; private set; } = double.NaN;

        public IReadOnlyList<float> Levels() => [.. _levels];

        /// <summary>Waits until a block of <paramref name="value"/> played at <paramref name="gain"/> has
        /// arrived.</summary>
        public bool Heard(float value, float gain)
        {
            var want = value * gain;
            for (int i = 0; i < 400; i++)
            {
                if (_levels.Any(level => Near(level, want)))
                    return true;
                Thread.Sleep(5);
            }
            return false;
        }

        public void Begin(SinkTrack track, int sampleRate, int channels) => Interlocked.Exchange(ref _frames, 0);

        public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
        {
            // A block that changes level inside itself would be a slide between two gains. Both ends are
            // written, so such a block cannot pass as one level.
            if (frames > 0)
            {
                _levels.Enqueue(interleaved[0]);
                _levels.Enqueue(interleaved[frames * 2 - 1]);
            }
            Interlocked.Add(ref _frames, frames);
            Thread.Sleep(5);          // Enqueue is the clock: a sink that never blocks spins the pump
            return true;
        }

        public void SetTrackGain(double gainDb) => TrackGainDb = gainDb;
        public void Drain(CancellationToken cancellation = default) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void SetVolume(int volume) { }
        public void Dispose() { }
    }

    /// <summary>A source that plays one value on both channels for as long as it is read.</summary>
    private sealed class Constant(string label, float value) : IPcmSource
    {
        public string Label => label;

        public int Read(float[] buffer, int frames)
        {
            Array.Fill(buffer, value, 0, frames * 2);
            return frames;
        }

        public void Dispose() { }
    }
}
