using RPM.Fingerprint.Chroma;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// How an anchor becomes index keys. The index and the query derive keys from the same instance,
/// so they agree by construction. But a scheme that changed what it produces would leave every
/// index already written unreadable, and the symptom would not be an error: lookups would find no
/// candidates, which is what a store that has never heard the recording looks like.
/// </summary>
public class ChromaKeySchemeTests
{
    [Fact]
    public void The_byte_windows_are_the_four_bytes_of_the_sub_fingerprint_lowest_first()
    {
        var keys = Extract(ChromaKeySchemes.ByteWindows, 0xDE_AD_BE_EFu);

        Assert.Equal([0xEFu, 0xBEu, 0xADu, 0xDEu], keys.Select(k => k & 0xFF));
    }

    [Fact]
    public void The_half_words_are_sixteen_bit_slices_taken_every_four_bits()
    {
        var keys = Extract(ChromaKeySchemes.HalfWords, 0xDE_AD_BE_EFu);

        Assert.Equal([0xBEEFu, 0xDBEEu, 0xADBEu, 0xEADBu, 0xDEADu], keys.Select(k => k & 0xFFFF));
    }

    /// <summary>
    /// Two keys off one anchor must not land in the same bucket, or an anchor votes for itself
    /// twice and the votes stop meaning "this many places agree".
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_keys_of_one_anchor_are_all_different(int schemeId)
    {
        var scheme = ChromaKeySchemes.ById(schemeId);

        var keys = Extract(scheme, 0x55_55_55_55u);   // the worst case: every slice the same bits

        Assert.Equal(scheme.KeysPerAnchor, keys.Distinct().Count());
    }

    [Fact]
    public void A_scheme_is_found_by_the_id_it_writes_and_an_unknown_id_is_refused()
    {
        Assert.Same(ChromaKeySchemes.ByteWindows, ChromaKeySchemes.ById(ChromaKeySchemes.ByteWindows.Id));
        Assert.Same(ChromaKeySchemes.HalfWords, ChromaKeySchemes.ById(ChromaKeySchemes.HalfWords.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChromaKeySchemes.ById(99));
    }

    /// <summary>
    /// The key space is what stops a growing collection from making the same buckets longer instead
    /// of making more of them. A key space of only 1,024 keys was too small. With 13,332 songs, a
    /// lookup walked 6.35 million index entries and ran out of memory while it counted the votes.
    /// </summary>
    [Fact]
    public void The_scheme_new_indexes_are_built_under_has_room_the_old_one_did_not()
    {
        Assert.Equal(1_024, ChromaKeySchemes.ByteWindows.KeySpace);
        Assert.True(ChromaKeySchemes.Default.KeySpace >= 256 * ChromaKeySchemes.ByteWindows.KeySpace,
                    $"{ChromaKeySchemes.Default} has {ChromaKeySchemes.Default.KeySpace} buckets");
    }

    /// <summary>Anchors are spaced through the recording, and silence is left out of the index:
    /// every recording has some, and it collides with every other recording's.</summary>
    [Fact]
    public void Anchors_are_spread_through_the_recording_and_skip_the_frames_that_say_nothing()
    {
        var frames = new uint[100];
        for (int i = 0; i < frames.Length; i++) frames[i] = (uint)(i + 1) * 2_654_435_761u;
        frames[0] = 0;                 // silence
        frames[10] = uint.MaxValue;    // clipping

        var positions = ChromaKeyScheme.AnchorPositions(frames, anchorsPerSong: 10);

        Assert.DoesNotContain(0, positions);
        Assert.DoesNotContain(10, positions);
        Assert.All(positions, p => Assert.InRange(p, 0, frames.Length - 1));
        Assert.Equal(positions.Order(), positions);
        Assert.Equal(positions.Distinct(), positions);
    }

    [Fact]
    public void A_recording_with_no_frames_has_no_anchors()
        => Assert.Empty(ChromaKeyScheme.AnchorPositions(Array.Empty<uint>(), anchorsPerSong: 10));

    private static uint[] Extract(ChromaKeyScheme scheme, uint frame)
    {
        var keys = new uint[scheme.KeysPerAnchor];
        int written = scheme.Extract(new[] { frame }, 0, keys);
        return keys[..written];
    }
}
