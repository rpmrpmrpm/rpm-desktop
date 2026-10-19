using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// A pause survives the speakers changing under it.
///
/// <para>Sending the music to a Cast device during a record restarts it on the new sink. A record that
/// is paused must arrive paused. The engine sets its own flag and tells the sink, and the transport reads
/// the flag back and shows a paused player. But the telling happens before the sink has started. The
/// start is queued on the audio worker, and no sink can be paused before then. Both sinks ignore a pause
/// while stopped, and beginning a track clears the flag. So the pause reached nothing, and a record paused
/// here started playing in the room while the transport still showed play.</para>
///
/// <para>The sink below is that contract and nothing else, so the test fails for the reason the
/// speakers do.</para>
/// </summary>
public class PausedHandoverTests
{
    [Fact]
    public void A_paused_record_sent_to_another_speaker_arrives_paused()
    {
        var here = new Speaker();
        using var engine = new AudioEngine(here);
        engine.PlaySource(new Silence("dj-set:whatever"), durationSeconds: 0);
        Assert.True(here.Started(), "the pump never reached the first sink");

        engine.Pause();

        var speaker = new Speaker();
        engine.SetSink(speaker, ownsSink: true);

        Assert.True(speaker.Begun(), "the record never reached the new sink");
        Assert.True(Settles(() => speaker.IsPaused), "the new speaker was never told the player is paused");
        Assert.True(engine.IsPaused, "and the engine still says so, so the transport is not lying");
    }

    /// <summary>A record that was playing keeps playing when the speakers change. This is the other half of
    /// the same rule. The fix for pausing must not break it.</summary>
    [Fact]
    public void A_playing_record_sent_to_another_speaker_carries_on()
    {
        var here = new Speaker();
        using var engine = new AudioEngine(here);
        engine.PlaySource(new Silence("dj-set:whatever"), durationSeconds: 0);
        Assert.True(here.Started(), "the pump never reached the first sink");

        var speaker = new Speaker();
        engine.SetSink(speaker, ownsSink: true);

        Assert.True(speaker.Started(), "the record never reached the new sink");
        Assert.False(speaker.IsPaused);
        Assert.False(engine.IsPaused);
    }

    /// <summary>The engine's worker does the starting, so the answer arrives a moment after the call. The
    /// test waits for it instead of sleeping for a guessed time.</summary>
    private static bool Settles(Func<bool> read)
    {
        for (int i = 0; i < 400 && !read(); i++) Thread.Sleep(5);
        return read();
    }

    /// <summary>
    /// A device that makes no sound. It holds a pause the same way the two real sinks do: it starts stopped,
    /// it ignores a pause while stopped, and starting a track clears the pause. The behaviour this test depends
    /// on is in those three rules. See OpenAlSink and CastSink.
    /// </summary>
    private sealed class Speaker : IAudioSink
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _fed = new();
        private readonly ManualResetEventSlim _begun = new();
        private bool _stopped = true;
        private bool _paused;
        private long _frames;

        public bool IsAvailable => true;
        public long FramesPlayed => Interlocked.Read(ref _frames);

        public bool IsPaused { get { lock (_gate) return _paused; } }

        /// <summary>Waits until the engine has fitted the track onto this sink.</summary>
        public bool Begun() => _begun.Wait(TimeSpan.FromSeconds(2));

        /// <summary>Waits until the pump has fed it. That is how the test knows the worker has picked up
        /// the source.</summary>
        public bool Started() => _fed.Wait(TimeSpan.FromSeconds(2));

        public void Begin(SinkTrack track, int sampleRate, int channels)
        {
            lock (_gate) { _stopped = false; _paused = false; }
            Interlocked.Exchange(ref _frames, 0);
            _begun.Set();
        }

        public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
        {
            Interlocked.Add(ref _frames, frames);
            _fed.Set();
            Thread.Sleep(5);          // Enqueue is the clock: a sink that never blocks spins the pump
            return true;
        }

        public void Drain(CancellationToken cancellation = default) { }

        public void Pause() { lock (_gate) { if (_stopped) return; _paused = true; } }

        public void Resume() { lock (_gate) { if (_stopped) return; _paused = false; } }

        public void Stop() { lock (_gate) { _stopped = true; _paused = false; } }

        public void SetVolume(int volume) { }

        public void Dispose() { _fed.Dispose(); _begun.Dispose(); }
    }

    /// <summary>A source the engine did not open, a set, a station, so no decoder is needed to get
    /// the pump running.</summary>
    private sealed class Silence(string label) : IPcmSource
    {
        public string Label => label;

        public int Read(float[] buffer, int frames)
        {
            Array.Clear(buffer, 0, frames * 2);
            return frames;
        }

        public void Dispose() { }
    }
}
