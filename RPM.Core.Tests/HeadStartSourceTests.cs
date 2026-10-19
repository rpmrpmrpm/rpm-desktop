using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The join between the three seconds held in memory and the real stream that follows.
///
/// <para>
/// Every test here is about the join. The same recording is on both sides of it, so a mistake does
/// not crash: a fraction of a second is played twice, or skipped, or the clip's last frames are
/// overwritten by the stream's first ones. All three are audible and none of them throw.
/// </para>
/// </summary>
public class HeadStartSourceTests
{
    /// <summary>A source that counts up one per frame, so anything repeated or skipped shows as a
    /// wrong number rather than as silence.</summary>
    private sealed class Ramp(float from, int frames, int channels = 2) : IPcmSource
    {
        private int _at;

        public string Label => "ramp";
        public bool Disposed { get; private set; }

        public int Read(float[] buffer, int frames_)
        {
            var take = Math.Min(frames_, frames - _at);
            if (take <= 0) return 0;

            for (var f = 0; f < take; f++)
                for (var c = 0; c < channels; c++)
                    buffer[f * channels + c] = from + _at + f;

            _at += take;
            return take;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Everything the source produced, block by block, at the given block size.</summary>
    private static List<float> Drain(IPcmSource source, int block, int channels = 2)
    {
        var got = new List<float>();
        var buffer = new float[block * channels];

        while (true)
        {
            var frames = source.Read(buffer, block);
            if (frames <= 0) break;

            // First channel only: the ramp writes the same value to both.
            for (var f = 0; f < frames; f++) got.Add(buffer[f * channels]);
        }

        return got;
    }

    [Fact]
    public void Plays_the_clip_then_the_stream_from_where_the_clip_stopped()
    {
        // The clip is the first 5 frames of the track; the real stream is the whole thing.
        var head = new Ramp(from: 0, frames: 5);
        var real = new Ramp(from: 0, frames: 20);

        var source = new HeadStartSource(head, () => real, channels: 2);

        // 0..19 exactly once. Not 0,1,2,3,4,0,1,2…, which is the result if the played frames are
        // not discarded from the stream.
        Assert.Equal(Enumerable.Range(0, 20).Select(n => (float)n), Drain(source, block: 4));
        Assert.True(source.Joined);
    }

    [Fact]
    public void Joins_inside_a_block_without_losing_the_clips_last_frames()
    {
        // The block size does not divide the clip, so one read spans the join. This case fails if
        // the stream is read into the caller's buffer from index zero: it overwrites the clip's
        // frames, and the listener hears the stream twice and the clip not at all.
        var head = new Ramp(from: 0, frames: 5);
        var real = new Ramp(from: 0, frames: 12);

        var source = new HeadStartSource(head, () => real, channels: 2);

        Assert.Equal(Enumerable.Range(0, 12).Select(n => (float)n), Drain(source, block: 8));
    }

    [Fact]
    public void Discards_what_was_played_and_not_what_the_clip_was_meant_to_be()
    {
        // A clip shorter than three seconds (encoder padding, a truncated object, or another cause).
        // The join is still at the right place, because the count comes from the frames that were
        // played.
        var head = new Ramp(from: 0, frames: 3);
        var real = new Ramp(from: 0, frames: 10);

        var source = new HeadStartSource(head, () => real, channels: 2);

        Assert.Equal(Enumerable.Range(0, 10).Select(n => (float)n), Drain(source, block: 4));
    }

    [Fact]
    public void Ends_where_the_clip_ended_when_the_stream_never_arrives()
    {
        var head = new Ramp(from: 0, frames: 5);

        var source = new HeadStartSource(head, () => null, channels: 2);

        // The clip plays and then the source stops. It does not output silence as if it were the
        // track.
        Assert.Equal(Enumerable.Range(0, 5).Select(n => (float)n), Drain(source, block: 4));
        Assert.False(source.Joined is false);   // it did try
    }

    [Fact]
    public void Asks_for_the_stream_once_and_only_when_the_clip_runs_out()
    {
        var asked = 0;
        var head = new Ramp(from: 0, frames: 8);
        var real = new Ramp(from: 0, frames: 20);

        var source = new HeadStartSource(head, () => { asked++; return real; }, channels: 2);

        var buffer = new float[4 * 2];

        source.Read(buffer, 4);
        Assert.Equal(0, asked);   // still inside the clip: nothing has been opened

        source.Read(buffer, 4);
        Assert.Equal(0, asked);   // exactly used up, but not yet run dry

        source.Read(buffer, 4);
        Assert.Equal(1, asked);

        source.Read(buffer, 4);
        Assert.Equal(1, asked);   // and never again
    }

    [Fact]
    public void A_stream_that_was_already_open_cost_the_seam_nothing()
    {
        var head = new Ramp(from: 0, frames: 8);
        var real = new Ramp(from: 0, frames: 20);

        var source = new HeadStartSource(head, () => real, channels: 2);

        Assert.False(source.HandedOver);   // nothing has joined yet

        Drain(source, block: 4);

        // The normal case, and the reason the head clip exists: the factory returned a stream that
        // was already open, so there is no gap.
        Assert.True(source.HandedOver);
        Assert.True(source.Waited < TimeSpan.FromMilliseconds(50), $"waited {source.Waited}");
    }

    [Fact]
    public void A_stream_that_was_not_open_yet_says_how_long_the_seam_waited()
    {
        var head = new Ramp(from: 0, frames: 8);
        var real = new Ramp(from: 0, frames: 20);

        // A factory that has not finished opening when the clip ends. Waited reports this case: the
        // audio stops for this long and then starts again, and nothing else reports it.
        var source = new HeadStartSource(
            head, () => { Thread.Sleep(120); return real; }, channels: 2);

        Drain(source, block: 4);

        Assert.True(source.HandedOver);
        Assert.True(source.Waited >= TimeSpan.FromMilliseconds(100), $"waited {source.Waited}");
    }

    [Fact]
    public void A_clip_with_nothing_behind_it_still_says_what_the_waiting_cost()
    {
        var head = new Ramp(from: 0, frames: 4);

        var source = new HeadStartSource(
            head, () => { Thread.Sleep(120); return null; }, channels: 2);

        Drain(source, block: 4);

        // The worst case: a wait, and then no stream. Both the failure and the wait are reported.
        Assert.Equal(PcmState.Failed, source.State);
        Assert.True(source.Waited >= TimeSpan.FromMilliseconds(100), $"waited {source.Waited}");
    }

    [Fact]
    public void Stops_rather_than_replaying_when_the_stream_is_shorter_than_the_clip()
    {
        // This should not happen (it means the two are not the same recording). If it does, the
        // source stops instead of playing the track again from the start.
        var head = new Ramp(from: 0, frames: 10);
        var real = new Ramp(from: 0, frames: 4);

        var source = new HeadStartSource(head, () => real, channels: 2);

        Assert.Equal(Enumerable.Range(0, 10).Select(n => (float)n), Drain(source, block: 16));
    }

    [Fact]
    public void Lets_go_of_both_and_of_a_stream_it_never_opened()
    {
        var head = new Ramp(from: 0, frames: 5);
        var real = new Ramp(from: 0, frames: 5);

        var used = new HeadStartSource(head, () => real, channels: 2);
        Drain(used, block: 4);
        used.Dispose();

        Assert.True(head.Disposed);
        Assert.True(real.Disposed);

        // A source disposed before the join must not open a stream only to close it.
        var opened = false;
        var spare = new HeadStartSource(new Ramp(0, 5), () => { opened = true; return null; }, 2);
        spare.Dispose();

        Assert.False(opened);
    }

    [Fact]
    public void Reads_nothing_once_disposed()
    {
        var source = new HeadStartSource(new Ramp(0, 5), () => null, channels: 2);
        source.Dispose();

        Assert.Equal(0, source.Read(new float[16], 8));
    }

    // ── telling a finished track from a clip that ran out ──
    //
    // To the engine these two look the same: the source stops returning audio. Only one of them is a
    // track that finished. If a failed stream is treated as an end, the player plays three seconds
    // of a track and skips to the next one. State tells the two apart.

    [Fact]
    public void A_clip_with_nothing_behind_it_reports_a_failure_and_not_an_end()
    {
        var source = new HeadStartSource(new Ramp(0, 5), () => null, channels: 2);

        Drain(source, block: 4);

        Assert.True(source.Joined);
        Assert.Equal(PcmState.Failed, source.State);
    }

    [Fact]
    public void A_track_that_handed_over_and_finished_reports_an_end()
    {
        var source = new HeadStartSource(new Ramp(0, 5), () => new Ramp(0, 40), channels: 2);

        Drain(source, block: 4);

        Assert.True(source.Joined);
        Assert.NotEqual(PcmState.Failed, source.State);
    }

    [Fact]
    public void A_clip_still_playing_answers_for_the_clip()
    {
        var source = new HeadStartSource(new Ramp(0, 100), () => null, channels: 2);

        source.Read(new float[16], 8);

        Assert.False(source.Joined);
        Assert.NotEqual(PcmState.Failed, source.State);
    }
}
