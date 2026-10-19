using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The first record of a set, and the one thread it must not be loaded on.
///
/// <para>Every record reaches a deck through the cue runner, because opening a decoder means
/// starting a process and waiting for its first bytes, and the audio thread has fifty milliseconds to
/// answer. The opening record is no exception: if it were loaded inline on the first block the device
/// asks for, the device would run out of samples for as long as ffmpeg took to start, giving a burst
/// of underruns at the start of every set.</para>
/// </summary>
public class OpeningRecordTests
{
    private const int Rate = 44100, Channels = 2, Block = 2205;

    [Fact]
    public void The_opening_record_is_not_put_on_the_deck_from_the_audio_thread()
    {
        var hand = new CueHand();
        int opens = 0, openedOn = 0;

        using var dj = new Dj(
            new DjCrate([Track("one", 128), Track("two", 128)]), Rate, Channels,
            options =>
            {
                opens++;
                openedOn = Environment.CurrentManagedThreadId;
                return new ToneSource(1f, 120 - options.StartSeconds);
            },
            cueRunner: hand.Enqueue,
            chance: () => 1);

        var buffer = new float[Block * Channels];

        // The first block the device asks for. It gets an answer (silence, but an answer, and on
        // time), and no decoder is opened during it.
        int wrote = dj.Read(buffer, Block);
        Assert.Equal(Block, wrote);
        Assert.Equal(0, opens);
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Assert.False(dj.HasFinished, "a record on its way is not a set that is over");

        // The cue thread does the work the audio thread refused to do.
        hand.RunAll();
        Assert.Equal(1, opens);
        Assert.NotEqual(Environment.CurrentManagedThreadId, openedOn);

        // From the next block the record is playing.
        wrote = dj.Read(buffer, Block);
        Assert.Equal(Block, wrote);
        Assert.Contains(buffer, sample => sample != 0f);
    }

    [Fact]
    public void The_opening_record_is_announced_when_it_is_on_the_deck_and_not_before()
    {
        var hand = new CueHand();
        var heard = new List<string>();

        using var dj = new Dj(
            new DjCrate([Track("one", 128), Track("two", 128)]), Rate, Channels,
            options => new ToneSource(1f, 120 - options.StartSeconds),
            cueRunner: hand.Enqueue,
            chance: () => 1);

        dj.TrackChanged += t => heard.Add(t.Title!);

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);
        Assert.Empty(heard);            // nothing is playing yet: the deck is still empty

        hand.RunAll();
        dj.Read(buffer, Block);
        Assert.Equal(["one"], heard);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>The cue runner, held by the test: work is queued and waits until the test lets it
    /// run, and it runs on a thread that is not the one reading audio. That is what these tests
    /// check.</summary>
    private sealed class CueHand
    {
        private readonly Queue<(Action Work, TaskCompletionSource Done)> _queue = new();

        public Task Enqueue(Action work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue((work, done));
            return done.Task;
        }

        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                var (work, done) = _queue.Dequeue();
                var thread = new Thread(() =>
                {
                    try { work(); done.SetResult(); }
                    catch (Exception ex) { done.SetException(ex); }
                });
                thread.Start();
                thread.Join();
            }
        }
    }

    private sealed class ToneSource(float value, double seconds) : IPcmSource
    {
        private long _left = (long)(seconds * Rate);

        public string Label => "tone";

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0) return 0;
            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = value;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static MixTrack Track(string path, double bpm, double duration = 120)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / bpm)
            beats.Add(at);

        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = path,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = 4,
                EnergyCurve = [0.3f, 0.3f],
                Intensity = 0.5,
                IntroEndSeconds = 8,
                OutroStartSeconds = duration - 20,
            },
        };
    }
}
