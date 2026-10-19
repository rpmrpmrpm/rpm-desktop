using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// A play the sink will not take ends as a stop. If it did not, the message about the output would show,
/// and the header, the turning record and the mini player would still show the track as playing. The
/// queue puts a record on its deck before it asks for the start, and only a stop takes it off. A refused
/// start raises nothing, so the deck would keep the record.
/// </summary>
public class RefusedPlayTests
{
    [Fact]
    public void A_refused_track_says_stopped_and_plays_nothing()
    {
        using var engine = new AudioEngine(new NoOutput());
        int stopped = 0, changed = 0;
        engine.Stopped += () => stopped++;
        engine.Changed += () => changed++;

        Assert.False(engine.Play("/library/song.flac", 200, 0));

        Assert.Equal(1, stopped);
        Assert.True(changed >= 1, "nothing was told to look again");
        Assert.Null(engine.PlayingPath);
    }

    [Fact]
    public void A_refused_source_says_stopped_and_is_let_go()
    {
        using var engine = new AudioEngine(new NoOutput());
        int stopped = 0;
        engine.Stopped += () => stopped++;
        var station = new Station();

        Assert.False(engine.PlaySource(station, durationSeconds: 0));

        Assert.Equal(1, stopped);
        Assert.True(station.Disposed, "the engine took the source over and dropped it without disposing it");
        Assert.Null(engine.PlayingSource);
    }

    /// <summary>A sink that never opened, which is what the engine sees on a PC with no output.</summary>
    private sealed class NoOutput : IAudioSink
    {
        public bool IsAvailable => false;
        public long FramesPlayed => 0;
        public void Begin(SinkTrack track, int sampleRate, int channels) { }
        public bool Enqueue(ReadOnlySpan<float> interleaved, int frames) => false;
        public void Drain(CancellationToken cancellation = default) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void SetVolume(int volume) { }
        public void Dispose() { }
    }

    private sealed class Station : IPcmSource
    {
        public bool Disposed { get; private set; }
        public string Label => "radio:test";
        public int Read(float[] buffer, int frames) => 0;
        public void Dispose() => Disposed = true;
    }
}
