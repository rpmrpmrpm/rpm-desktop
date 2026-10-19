using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The audio held between the decoder and the pump. Each of these tests covers a way of getting the
/// arithmetic wrong that is inaudible in a unit test and audible as a click on a phone: a buffer
/// that grows for the length of a track, a compaction that replays what was already handed over, a
/// read that runs past the end of what is there.
/// </summary>
public class SpillTests
{
    /// <summary>Adds <paramref name="frames"/> frames of a recognisable ramp, so that anything played
    /// twice or skipped shows up as a wrong number rather than as silence.</summary>
    private static void Add(Spill spill, int channels, float from, int frames)
    {
        var (buffer, at) = spill.Reserve(frames * channels);

        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                buffer[at + frame * channels + channel] = from + frame;

        spill.Commit(frames * channels);
    }

    [Fact]
    public void Holds_what_is_put_in_it()
    {
        var spill = new Spill(2);
        Add(spill, 2, from: 0, frames: 4);

        Assert.Equal(4, spill.Frames);

        var taken = new float[8];
        Assert.Equal(4, spill.Take(taken, 0, 4));

        Assert.Equal([0, 0, 1, 1, 2, 2, 3, 3], taken);
        Assert.Equal(0, spill.Frames);
    }

    [Fact]
    public void Accumulates_rather_than_replacing()
    {
        // The reason this exists: cueing a record reads several buffers ahead, and a spill that
        // overwrote would hold only the last of them (two seconds asked for, one buffer delivered).
        var spill = new Spill(1);

        Add(spill, 1, from: 0, frames: 3);
        Add(spill, 1, from: 10, frames: 3);
        Add(spill, 1, from: 20, frames: 3);

        Assert.Equal(9, spill.Frames);

        var taken = new float[9];
        spill.Take(taken, 0, 9);

        Assert.Equal([0, 1, 2, 10, 11, 12, 20, 21, 22], taken);
    }

    [Fact]
    public void Hands_over_a_partial_read_and_says_how_much()
    {
        var spill = new Spill(1);
        Add(spill, 1, from: 0, frames: 3);

        var taken = new float[10];

        Assert.Equal(3, spill.Take(taken, 0, 10));
        Assert.Equal(0, spill.Take(taken, 3, 10));   // and nothing at all when it is empty
    }

    [Fact]
    public void Continues_where_the_last_read_stopped()
    {
        var spill = new Spill(1);
        Add(spill, 1, from: 0, frames: 6);

        var taken = new float[6];

        spill.Take(taken, 0, 2);
        spill.Take(taken, 2, 2);
        spill.Take(taken, 4, 2);

        // Not 0,1,0,1,0,1, which is what a compaction that forgot the offset sounds like.
        Assert.Equal([0, 1, 2, 3, 4, 5], taken);
    }

    [Fact]
    public void Keeps_the_unread_part_when_more_arrives_mid_read()
    {
        var spill = new Spill(1);

        Add(spill, 1, from: 0, frames: 4);

        var first = new float[2];
        spill.Take(first, 0, 2);          // 0, 1 taken; 2, 3 still held

        Add(spill, 1, from: 10, frames: 2);   // compacts, because the offset is 2

        Assert.Equal(4, spill.Frames);

        var rest = new float[4];
        spill.Take(rest, 0, 4);

        Assert.Equal([2, 3, 10, 11], rest);
    }

    [Fact]
    public void Does_not_grow_for_the_length_of_a_track()
    {
        // A thousand buffers written and read in turn. Without the compaction this allocates a hundred
        // thousand samples and never gives any back.
        var spill = new Spill(2);
        var taken = new float[200];

        for (var n = 0; n < 1000; n++)
        {
            Add(spill, 2, from: n, frames: 100);
            spill.Take(taken, 0, 100);
        }

        Assert.Equal(0, spill.Frames);
        Assert.True(spill.Capacity <= 400, $"grew to {spill.Capacity} samples");
    }

    [Fact]
    public void Counts_in_frames_when_there_is_more_than_one_channel()
    {
        var spill = new Spill(2);
        Add(spill, 2, from: 0, frames: 5);

        // Ten samples went in; five frames came out. Mixing the two up is how a stereo track plays at
        // half speed.
        Assert.Equal(5, spill.Frames);
    }

    [Fact]
    public void Empties()
    {
        var spill = new Spill(2);
        Add(spill, 2, from: 0, frames: 4);

        spill.Clear();

        Assert.Equal(0, spill.Frames);
        Assert.Equal(0, spill.Take(new float[8], 0, 4));
    }

    [Fact]
    public void Refuses_a_negative_reservation()
    {
        var spill = new Spill(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => spill.Reserve(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => spill.Commit(-1));
        Assert.Throws<ArgumentNullException>(() => spill.Take(null!, 0, 1));
    }
}
