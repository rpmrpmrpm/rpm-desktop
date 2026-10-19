using RPM.Fingerprint.Chroma;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// The stage-1 index: flat arrays addressed by the key itself, plus an overlay for recordings filed
/// while it is running. What has to hold is that a lookup cannot tell the two apart, before or
/// after they are merged.
/// </summary>
public class ChromaIndexTests
{
    private static uint[] Frames(int count, int seed)
    {
        var random = new Random(seed);
        var frames = new uint[count];
        frames[0] = (uint)random.Next();
        for (int i = 1; i < count; i++)
        {
            uint flipped = frames[i - 1];
            for (int bit = 0; bit < 3; bit++) flipped ^= 1u << random.Next(32);
            frames[i] = flipped;
        }
        return frames;
    }

    private static ChromaIndex Build(params (int Ordinal, uint[] Frames)[] songs)
    {
        var builder = new ChromaIndex.Builder(ChromaKeySchemes.Default);
        foreach (var (ordinal, frames) in songs) builder.Add(ordinal, frames, anchorsPerSong: 10);
        return builder.Build();
    }

    /// <summary>Every entry a song was filed with is findable under the same keys the scheme
    /// derives from its frames. That is the only reason a query ever finds anything.</summary>
    [Fact]
    public void A_recordings_anchors_are_findable_under_the_keys_its_frames_produce()
    {
        var frames = Frames(1000, seed: 7);
        var index = Build((3, frames));

        var scheme = ChromaKeySchemes.Default;
        var keys = new uint[scheme.KeysPerAnchor];
        int found = 0;
        foreach (int pos in ChromaKeyScheme.AnchorPositions(frames, anchorsPerSong: 10))
        {
            int written = scheme.Extract(frames, pos, keys);
            for (int k = 0; k < written; k++)
            {
                var bucket = index.Flat(keys[k]);
                for (int h = 0; h < bucket.Length; h++)
                    if (bucket.Songs[h] == 3 && bucket.Positions[h] == pos) found++;
            }
        }

        Assert.Equal(10 * scheme.KeysPerAnchor, index.Entries);
        Assert.Equal(index.Entries, found);
    }

    [Fact]
    public void A_key_nothing_was_filed_under_carries_nothing()
    {
        var index = Build((1, Frames(500, seed: 3)));

        Assert.Equal(0, index.Flat(uint.MaxValue).Length);
        Assert.Null(index.Overlay(uint.MaxValue));
    }

    /// <summary>A recording filed while running is answered out of the overlay, and after a merge
    /// out of the flat side. A lookup must not be able to tell which.</summary>
    [Fact]
    public void A_recording_filed_while_running_reads_the_same_before_and_after_a_merge()
    {
        var standing = Frames(1000, seed: 11);
        var newcomer = Frames(1000, seed: 12);
        var index = Build((1, standing));
        var scheme = ChromaKeySchemes.Default;

        index.Add(2, newcomer, scheme, anchorsPerSong: 10);
        Assert.True(index.OverlayEntries > 0, "nothing was filed into the overlay");
        var beforeMerge = Everything(index, scheme, newcomer);

        index.Compact();

        Assert.Equal(0, index.OverlayEntries);
        Assert.Equal(beforeMerge, Everything(index, scheme, newcomer));
        Assert.Equal(20 * scheme.KeysPerAnchor, index.Entries);
    }

    /// <summary>Merging the overlay must not disturb what was already filed.</summary>
    [Fact]
    public void A_merge_leaves_the_recordings_already_filed_where_they_were()
    {
        var standing = Frames(1000, seed: 21);
        var index = Build((1, standing));
        var scheme = ChromaKeySchemes.Default;
        var before = Everything(index, scheme, standing);

        index.Add(2, Frames(1000, seed: 22), scheme, anchorsPerSong: 10);
        index.Compact();

        Assert.Equal(before, Everything(index, scheme, standing).Where(e => e.Song == 1).ToList());
    }

    /// <summary>
    /// A key carried by a large share of the collection costs a lot to walk and narrows nothing
    /// down. The threshold is a multiple of the mean bucket, so it says the same thing at any store
    /// size. Unlike an absolute figure, it cannot throw the whole index away.
    /// </summary>
    [Fact]
    public void A_key_every_recording_carries_is_dropped_and_the_rest_of_the_index_survives()
    {
        // A frame every recording shares, at one anchor each: its keys end up carrying the whole
        // collection while every other key carries about one recording.
        const uint Shared = 0x0F0F_0F0Fu;
        var builder = new ChromaIndex.Builder(ChromaKeySchemes.Default);
        for (int song = 0; song < 400; song++)
        {
            var frames = Frames(200, seed: song + 100);
            frames[0] = Shared;
            builder.Add(song, frames, anchorsPerSong: 10);
        }
        var index = builder.Build();

        var keys = new uint[ChromaKeySchemes.Default.KeysPerAnchor];
        ChromaKeySchemes.Default.Extract([Shared], 0, keys);

        Assert.True(index.StopWords > 0, "the shared key was not pruned");
        foreach (uint key in keys) Assert.Equal(0, index.Flat(key).Length);
        Assert.True(index.Entries > 0, "pruning emptied the index");
    }

    [Fact]
    public void An_index_with_nothing_in_it_answers_nothing_rather_than_failing()
    {
        var index = new ChromaIndex.Builder(ChromaKeySchemes.Default).Build();

        Assert.Equal(0, index.Entries);
        Assert.Equal(0, index.Buckets);
        Assert.Equal(0, index.Flat(0).Length);
    }

    /// <summary>Every (song, position) the index answers for a recording's own frames.</summary>
    private static List<(int Song, int Pos)> Everything(ChromaIndex index, ChromaKeyScheme scheme, uint[] frames)
    {
        var seen = new List<(int, int)>();
        var keys = new uint[scheme.KeysPerAnchor];
        foreach (int pos in ChromaKeyScheme.AnchorPositions(frames, anchorsPerSong: 10))
        {
            int written = scheme.Extract(frames, pos, keys);
            for (int k = 0; k < written; k++)
            {
                var bucket = index.Flat(keys[k]);
                for (int h = 0; h < bucket.Length; h++) seen.Add((bucket.Songs[h], bucket.Positions[h]));
                foreach (var entry in index.Overlay(keys[k]) ?? []) seen.Add(entry);
            }
        }
        return seen.Order().ToList();
    }
}
