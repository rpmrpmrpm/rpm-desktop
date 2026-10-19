using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the engine says is playing, to something that is going to SHOW it.
///
/// <para>A source that plays a series of tracks is handed to the engine without a path, because it is not
/// one record. The set's own label stands in for it, so "is anything playing?" is true and no grid row
/// lights up. That label is not a record. If anything treats it as one, it goes on screen. For example,
/// "dj-set:f33270..." would sit in the Track info header for the first seconds of every set, and a
/// station's stream address would sit there for as long as the station plays.</para>
///
/// <para>So the two questions are two properties. <c>PlayingPath</c> matches; <c>PlayingRecord</c>
/// names something the collection can be asked about, and answers with nothing where there is
/// nothing.</para>
/// </summary>
public class PlayingRecordTests
{
    [Fact]
    public void A_set_with_no_record_on_the_deck_yet_is_playing_but_names_none()
    {
        using var engine = new AudioEngine(new SilentSink());
        var mix = new Multi("dj-set:whatever", null);

        engine.PlaySource(mix, durationSeconds: 0);

        Assert.Equal("dj-set:whatever", engine.PlayingPath);   // something IS playing
        Assert.Null(engine.PlayingRecord);                     // and it is not a record
    }

    [Fact]
    public void A_set_names_the_record_that_is_under_the_needle()
    {
        using var engine = new AudioEngine(new SilentSink());
        var mix = new Multi("dj-set:whatever",
                            new NowPlayingTrack("/library/a.flac", IsRecord: true, "A", "Someone", null, 12, 300));

        engine.PlaySource(mix, durationSeconds: 0);

        Assert.Equal("/library/a.flac", Settles(() => engine.PlayingRecord, "/library/a.flac"));
        Assert.Equal("/library/a.flac", engine.PlayingPath);
    }

    [Fact]
    public void A_station_is_playing_and_is_never_a_record()
    {
        using var engine = new AudioEngine(new SilentSink());
        var station = new Multi("radio:station",
                                new NowPlayingTrack("http://stream.example/live.mp3", IsRecord: false,
                                                    "Some Song", "Someone", "The Station", 30, 0,
                                                    ArtUrl: "http://example/logo.png"));

        engine.PlaySource(station, durationSeconds: 0);

        // The address is what everything MATCHES on, and there is nothing to show a sheet about.
        Assert.Equal("http://stream.example/live.mp3",
                     Settles(() => engine.PlayingPath, "http://stream.example/live.mp3"));
        Assert.Null(engine.PlayingRecord);
    }

    [Fact]
    public void A_cloud_record_started_on_its_head_clip_is_a_record()
    {
        using var engine = new AudioEngine(new SilentSink());

        // Handed in as a source like a mix is, but it IS one row of the library and says so.
        engine.PlaySource(new Multi("clip", null), durationSeconds: 300, path: "/library/cloud.flac");

        Assert.Equal("/library/cloud.flac", engine.PlayingRecord);
    }

    [Fact]
    public void Sending_a_set_to_another_speaker_does_not_turn_its_label_into_a_record()
    {
        var speaker = new SilentSink();
        using var engine = new AudioEngine(speaker);
        engine.PlaySource(new Multi("dj-set:whatever", null), durationSeconds: 0);

        // With the set actually running on it: a swap made before the pump has picked the source up
        // has nothing to carry over and ends the set, which is a different question from this one.
        Assert.True(speaker.Started(), "the pump never reached the sink");

        // The swap re-declares what was playing on the new sink. Everything the engine knows about the set
        // travels as one value. Flattening it to a string would arrive on the far side as "this label is a
        // record", and then be opened as a file.
        engine.SetSink(new SilentSink(), ownsSink: true);

        Assert.Equal("dj-set:whatever", engine.PlayingPath);
        Assert.Null(engine.PlayingRecord);
    }

    /// <summary>The engine picks its source up on its own worker, so an answer from the source arrives a
    /// moment after the call. Until then the reading is the one the label gives. The test waits for the
    /// answer rather than sleeping a guessed time. It returns whatever it ended on, so a failure shows what
    /// the engine actually said.</summary>
    private static string? Settles(Func<string?> read, string expected)
    {
        string? answer = read();
        for (int i = 0; i < 400 && answer != expected; i++)
        {
            Thread.Sleep(5);
            answer = read();
        }
        return answer;
    }

    /// <summary>A source that plays a succession of tracks, a set, a station, and says which one it
    /// is on. Null while it is on none, which is where a set starts.</summary>
    private sealed class Multi(string label, NowPlayingTrack? now) : IPcmSource, INowPlayingSource
    {
        public string Label => label;
        public NowPlayingTrack? NowPlayingTrack => now;

        public int Read(float[] buffer, int frames)
        {
            Array.Clear(buffer, 0, frames * 2);
            return frames;
        }

        public void Dispose() { }
    }

    /// <summary>A device that takes everything and makes no sound, so the pump has somewhere to
    /// go.</summary>
    private sealed class SilentSink : IAudioSink
    {
        private readonly ManualResetEventSlim _fed = new();
        private long _frames;

        public bool IsAvailable => true;
        public long FramesPlayed => Interlocked.Read(ref _frames);

        /// <summary>Waits until the pump has fed this sink. That is how the test knows the engine's worker
        /// has picked up the source.</summary>
        public bool Started() => _fed.Wait(TimeSpan.FromSeconds(2));

        public void Begin(SinkTrack track, int sampleRate, int channels) => Interlocked.Exchange(ref _frames, 0);

        public bool Enqueue(ReadOnlySpan<float> interleaved, int frames)
        {
            Interlocked.Add(ref _frames, frames);
            _fed.Set();
            Thread.Sleep(5);          // Enqueue is the clock: a sink that never blocks spins the pump
            return true;
        }

        public void Drain(CancellationToken cancellation = default) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void SetVolume(int volume) { }
        public void Dispose() => _fed.Dispose();
    }
}
