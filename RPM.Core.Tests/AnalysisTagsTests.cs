using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The rule that decides whether what the analyser measured is allowed into a file's tags.
///
/// <para>It has to be the same rule in three places (the pass that writes as tracks are analysed, the
/// manual backfill, and the box the per-file editor fills in), so it lives in one testable place
/// rather than in each of them.</para>
/// </summary>
public class AnalysisTagsTests
{
    /// <summary>An analysis good enough to be written: a confident tempo with a real grid behind it,
    /// and a key the detector was sure of.</summary>
    private static TrackAnalysis Confident(
        double bpm = 128.4, double bpmConfidence = 0.9,
        MusicalKey? key = null, double keyConfidence = 0.8, int beats = 64) => new()
    {
        FilePath = "/music/a.flac",
        Bpm = bpm,
        BpmConfidence = bpmConfidence,
        BeatTimes = [.. Enumerable.Range(0, beats).Select(i => i * 0.5)],
        Key = key ?? new MusicalKey(9, IsMinor: true),   // A minor
        KeyConfidence = keyConfidence,
    };

    [Fact]
    public void A_confident_reading_fills_a_file_that_says_nothing()
    {
        var wanted = AnalysisTags.Wanted(Confident(), fileBpm: 0, fileKey: null);

        Assert.True(wanted.Any);
        Assert.Equal(128u, wanted.Bpm);
        Assert.Equal("Am", wanted.Key);
    }

    [Fact]
    public void The_tempo_is_rounded_rather_than_truncated()
    {
        Assert.Equal(129u, AnalysisTags.Wanted(Confident(bpm: 128.6), 0, null).Bpm);
        Assert.Equal(128u, AnalysisTags.Wanted(Confident(bpm: 128.4), 0, null).Bpm);
    }

    [Fact]
    public void What_the_file_already_says_is_left_alone()
    {
        var wanted = AnalysisTags.Wanted(Confident(), fileBpm: 87, fileKey: "Fm");

        Assert.False(wanted.Any);
        Assert.Equal(0u, wanted.Bpm);
        Assert.Null(wanted.Key);
    }

    [Fact]
    public void Each_field_is_decided_on_its_own()
    {
        // A file with a tempo and no key gets the key and keeps the tempo.
        var wanted = AnalysisTags.Wanted(Confident(), fileBpm: 87, fileKey: "  ");

        Assert.Equal(0u, wanted.Bpm);
        Assert.Equal("Am", wanted.Key);
    }

    [Fact]
    public void Overwriting_replaces_what_the_file_carries()
    {
        var wanted = AnalysisTags.Wanted(Confident(), fileBpm: 87, fileKey: "Fm", overwrite: true);

        Assert.Equal(128u, wanted.Bpm);
        Assert.Equal("Am", wanted.Key);
    }

    [Fact]
    public void Overwriting_never_means_replacing_a_value_with_nothing()
    {
        // Nothing the analyser is sure of: "write ours over theirs" cannot mean "clear theirs".
        var vague = Confident(bpmConfidence: 0.05, keyConfidence: 0.1);
        var wanted = AnalysisTags.Wanted(vague, fileBpm: 87, fileKey: "Fm", overwrite: true);

        Assert.False(wanted.Any);
    }

    [Fact]
    public void A_value_that_already_matches_is_not_written_again()
    {
        // Nothing to gain, and the write would give the file a new mtime, which the analysis cache and
        // the library sync are both keyed on.
        var wanted = AnalysisTags.Wanted(Confident(), fileBpm: 128, fileKey: "am", overwrite: true);

        Assert.False(wanted.Any);
    }

    [Fact]
    public void A_tempo_the_analyser_would_not_beat_match_to_is_not_written()
    {
        Assert.Null(AnalysisTags.BpmOf(Confident(bpmConfidence: 0.05)));

        // A tempo with no grid behind it is a guess over a rubato record, whatever its confidence.
        Assert.Null(AnalysisTags.BpmOf(Confident(beats: 3)));
    }

    [Fact]
    public void A_key_the_detector_is_unsure_of_is_not_written()
    {
        // Under the threshold the model is picking between the relative major and minor, which is the
        // one mistake a DJ notices immediately.
        Assert.Null(AnalysisTags.KeyOf(Confident(keyConfidence: AnalysisTags.MinKeyConfidence - 0.01)));
        Assert.NotNull(AnalysisTags.KeyOf(Confident(keyConfidence: AnalysisTags.MinKeyConfidence)));
    }

    [Fact]
    public void An_ordinary_records_key_clears_the_bar()
    {
        // What the two readers report is how far the winner is ahead of the second choice, not a
        // probability, and over a real collection the middle of that distribution is around 0.4. A
        // threshold above it would show the key on screen and leave the box empty, which looks broken.
        Assert.NotNull(AnalysisTags.KeyOf(Confident(keyConfidence: 0.43)));
        Assert.NotNull(AnalysisTags.KeyOf(Confident(keyConfidence: 0.25)));
    }

    [Fact]
    public void What_a_person_asks_for_is_not_gated_on_confidence()
    {
        // The button beside the reading hands over what is on screen. The thresholds decide what
        // happens unasked, which is a different question.
        var coinToss = Confident(bpmConfidence: 0.01, keyConfidence: 0.02);

        Assert.False(AnalysisTags.Wanted(coinToss, 0, null).Any);

        var detected = AnalysisTags.Detected(coinToss);
        Assert.Equal(128u, detected.Bpm);
        Assert.Equal("Am", detected.Key);
    }

    [Fact]
    public void There_is_nothing_to_hand_over_for_a_track_with_no_reading_at_all()
    {
        var nothing = new TrackAnalysis { FilePath = "/music/a.flac", Key = MusicalKey.Unknown };

        Assert.False(AnalysisTags.Detected(nothing).Any);
    }

    [Fact]
    public void A_track_with_no_key_at_all_writes_no_key()
    {
        // Silence, or nothing tonal enough to call. The tempo it did find still goes in.
        var noKey = Confident(key: MusicalKey.Unknown, keyConfidence: 1);
        var wanted = AnalysisTags.Wanted(noKey, 0, null);

        Assert.Null(AnalysisTags.KeyOf(noKey));
        Assert.Null(wanted.Key);
        Assert.Equal(128u, wanted.Bpm);
    }

    [Theory]
    // ID3's TKEY is three characters at most: A to G, 'b'/'#' for the half keys, 'm' for minor. That
    // is what every other tagger and every DJ program reads a key out of.
    [InlineData(9, true, "Am")]
    [InlineData(0, false, "C")]
    [InlineData(6, true, "F#m")]
    [InlineData(10, false, "A#")]
    public void A_key_goes_into_a_tag_in_the_form_readers_expect(int tonic, bool minor, string expected)
    {
        var key = new MusicalKey(tonic, minor);

        Assert.Equal(expected, key.TagValue);
        Assert.True(key.TagValue.Length <= 3);
    }

    [Fact]
    public void An_unknown_key_has_no_tag_form()
    {
        Assert.Equal("", MusicalKey.Unknown.TagValue);
    }
}
