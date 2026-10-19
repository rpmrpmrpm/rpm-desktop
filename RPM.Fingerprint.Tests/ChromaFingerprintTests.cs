using RPM.Fingerprint.Chroma;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// The fingerprint has two jobs. It must survive being written to storage, and it must give the
/// distance between two recordings.
///
/// <para>Both are wire formats. The blobs are already stored in SQLite files on every machine that has
/// fingerprinted a library. The distances they produce are what the confidence threshold was set
/// against. A change to either one changes data that already exists. So these tests state the format
/// and the arithmetic, and do not assume that they stay the same.</para>
/// </summary>
public class ChromaFingerprintTests
{
    /// <summary>Frames that differ from their neighbour in a few bits, which is what real chroma
    /// looks like and what the delta-XOR encoding is built for.</summary>
    private static uint[] RealisticFrames(int count, int seed = 7)
    {
        var random = new Random(seed);
        var frames = new uint[count];
        frames[0] = (uint)random.Next();
        for (var i = 1; i < count; i++)
        {
            var flipped = frames[i - 1];
            for (var bit = 0; bit < 3; bit++)
                flipped ^= 1u << random.Next(32);
            frames[i] = flipped;
        }
        return frames;
    }

    [Fact]
    public void A_fingerprint_comes_back_exactly_as_it_went_in()
    {
        var frames = RealisticFrames(5000);

        var back = ChromaFingerprint.Deserialize(new ChromaFingerprint(frames).Serialize());

        Assert.Equal(frames.Length, back.Length);
        Assert.Equal(frames, back.SubFingerprints);
    }

    /// <summary>The two ends of the range. The delta loop stores the first frame as a literal and the rest
    /// as XORs, so an off-by-one error is easy to make at either end.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_shortest_fingerprints_survive_the_round_trip(int count)
    {
        uint[] frames = count == 0 ? [] : RealisticFrames(count);

        var back = ChromaFingerprint.Deserialize(new ChromaFingerprint(frames).Serialize());

        Assert.Equal(frames, back.SubFingerprints);
    }

    [Fact]
    public void Frames_at_the_extremes_of_the_range_round_trip_too()
    {
        // uint.MaxValue is the one that catches a varint written with a signed shift, and the
        // all-bits-set delta is the widest the encoder ever has to write.
        uint[] frames = [0u, uint.MaxValue, 0u, 0x8000_0000u, 0x7FFF_FFFFu, uint.MaxValue];

        var back = ChromaFingerprint.Deserialize(new ChromaFingerprint(frames).Serialize());

        Assert.Equal(frames, back.SubFingerprints);
    }

    /// <summary>
    /// The point of the format. A raw fingerprint is four bytes a frame; the delta-XOR plus Deflate
    /// is what makes a whole library's worth of them fit in a file worth shipping, and the "bytes per
    /// song" number the store reports is only meaningful while that holds.
    /// </summary>
    [Fact]
    public void Storing_a_fingerprint_costs_far_less_than_the_frames_it_holds()
    {
        var frames = RealisticFrames(5000);
        var raw = frames.Length * sizeof(uint);

        var bytes = new ChromaFingerprint(frames).Serialize().Length;

        // Frames that drift three bits at a time compress to a little over half of raw here; the
        // threshold is loose enough that a change in the deflate level is not a failure, and tight
        // enough that a delta step or the compression stage quietly dropping out is.
        Assert.True(bytes < raw * 3 / 4, $"{bytes} bytes for {raw} bytes of frames — barely compressed");
    }

    [Fact]
    public void An_empty_fingerprint_takes_no_space_at_all()
    {
        Assert.Empty(new ChromaFingerprint([]).Serialize());
        Assert.Equal(0, ChromaFingerprint.Deserialize([]).Length);
    }

    [Fact]
    public void The_distance_between_a_recording_and_itself_is_nothing()
    {
        var frames = RealisticFrames(64);

        Assert.Equal(0, ChromaFingerprint.HammingDistance(frames, frames, offset: 0));
    }

    [Fact]
    public void The_distance_counts_every_bit_that_differs_over_the_overlap()
    {
        uint[] a = [0b0000u, 0b0000u];
        uint[] b = [0b0011u, 0b1111u];

        Assert.Equal(2 + 4, ChromaFingerprint.HammingDistance(a, b, offset: 0));
    }

    /// <summary>
    /// The offset is where a[0] sits inside b, and it is signed: a clip that starts before the
    /// stored song is a negative offset, which is the case a "clip is always inside the song"
    /// reading of the parameter gets backwards.
    /// </summary>
    [Fact]
    public void A_clip_lines_up_at_the_offset_it_was_taken_from()
    {
        var song = RealisticFrames(200);
        var clip = song[50..90];

        Assert.Equal(0, ChromaFingerprint.HammingDistance(clip, song, offset: 50));
        Assert.True(ChromaFingerprint.HammingDistance(clip, song, offset: 49) > 0);

        // And the other way round: the song, placed 50 frames before the clip, overlaps the same way.
        Assert.Equal(0, ChromaFingerprint.HammingDistance(song, clip, offset: -50));
    }

    [Fact]
    public void Two_sequences_that_do_not_overlap_are_infinitely_far_apart()
    {
        var a = RealisticFrames(10);
        var b = RealisticFrames(10, seed: 99);

        // Slid so far that nothing lines up: the answer has to be "no comparison", not "zero bits
        // differed", which is what a sum over an empty range would otherwise say.
        Assert.Equal(int.MaxValue, ChromaFingerprint.HammingDistance(a, b, offset: 10));
        Assert.Equal(int.MaxValue, ChromaFingerprint.HammingDistance(a, b, offset: -10));
    }

    [Fact]
    public void The_best_alignment_finds_where_the_clip_was_taken_from()
    {
        var song = RealisticFrames(600);
        var clip = song[220..320];

        var (offset, bitsPerFrame, overlap) = ChromaFingerprint.BestAlignment(
            clip, song, searchStart: 0, searchEnd: song.Length - clip.Length);

        Assert.Equal(220, offset);
        Assert.Equal(0, bitsPerFrame);
        Assert.Equal(clip.Length, overlap);
    }

    /// <summary>A clip off a worn record is not bit-identical, which is the whole reason the score is
    /// a distance rather than an equality.</summary>
    [Fact]
    public void A_clip_with_a_few_bits_flipped_still_aligns_where_it_belongs()
    {
        var song = RealisticFrames(600);
        var clip = song[220..320];
        var noise = new Random(3);
        for (var i = 0; i < clip.Length; i++)
            clip[i] ^= 1u << noise.Next(32);

        var (offset, bitsPerFrame, _) = ChromaFingerprint.BestAlignment(
            clip, song, searchStart: 0, searchEnd: song.Length - clip.Length);

        Assert.Equal(220, offset);
        Assert.Equal(1.0, bitsPerFrame, 3);   // exactly the one bit per frame that was flipped
    }

    /// <summary>
    /// Bits per frame, not bits: a four-frame overlap that happens to agree would otherwise beat a
    /// hundred-frame overlap that is genuinely the same recording, and the matcher would return the
    /// edge of whichever song had the luckiest four frames.
    /// </summary>
    [Fact]
    public void The_score_is_per_frame_so_long_and_short_overlaps_compare_fairly()
    {
        var song = RealisticFrames(100);
        var half = song[0..50];

        var (_, wholeBpf, wholeOverlap) = ChromaFingerprint.BestAlignment(song, song, 0, 0);
        var (_, halfBpf, halfOverlap) = ChromaFingerprint.BestAlignment(half, song, 0, 50);

        Assert.Equal(wholeBpf, halfBpf);
        Assert.Equal(100, wholeOverlap);
        Assert.Equal(50, halfOverlap);
    }

    /// <summary>An overlap of a handful of frames says nothing at all, so it is not allowed to be the
    /// best answer. It is skipped rather than scored.</summary>
    [Fact]
    public void An_overlap_too_short_to_mean_anything_is_not_scored()
    {
        var a = RealisticFrames(20);
        var b = RealisticFrames(20, seed: 42);

        // Every offset in this range leaves fewer than eight frames overlapping.
        var (_, bitsPerFrame, overlap) = ChromaFingerprint.BestAlignment(a, b, searchStart: 13, searchEnd: 19);

        Assert.Equal(double.MaxValue, bitsPerFrame);
        Assert.Equal(0, overlap);
    }
}
