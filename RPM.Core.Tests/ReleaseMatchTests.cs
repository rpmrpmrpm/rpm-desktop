using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// A search for "Rumours" returns the 1977 UK cut, the 1984 CD and a 2013 reissue. They are the same
/// songs with different lengths, and the recording on the turntable shows which one it is. These
/// tests pin down that the right pressing wins, that a turntable running fast does not lose it, and
/// that the score is honest about what it could weigh: a right count with no listed times is a real
/// match that the lengths could not confirm, not a 45% match.
/// </summary>
public class ReleaseMatchTests
{
    /// <summary>The record as detected: five tracks, in seconds.</summary>
    private static readonly double[] Detected = [223, 278, 195, 251, 310];

    private static IReadOnlyList<double?> Lengths(params double[] seconds) =>
        seconds.Select(s => (double?)s).ToArray();

    [Fact]
    public void The_pressing_that_matches_scores_high()
    {
        var match = ReleaseMatcher.Measure(Lengths(224, 277, 196, 250, 311), Detected);

        Assert.Equal(MatchBasis.Lengths, match.Basis);
        Assert.True(match.CountMatches);
        Assert.True(match.Score > ReleaseMatcher.GoodMatch, $"score was {match.Score}");
        Assert.InRange(match.MeanErrorSeconds, 0, 2);
    }

    [Fact]
    public void A_different_pressing_of_the_same_album_scores_lower()
    {
        // Same five songs, but this edition's times differ by 8-15 s each: a different master.
        var other = ReleaseMatcher.Measure(Lengths(238, 263, 210, 236, 325), Detected);
        var right = ReleaseMatcher.Measure(Lengths(224, 277, 196, 250, 311), Detected);

        Assert.True(right.Score > other.Score);
        Assert.True(other.Score < ReleaseMatcher.GoodMatch);
    }

    /// <summary>Every track stretched by the same 2% is a deck running fast, not the wrong record.
    /// Divide the drift out and the release still fits.</summary>
    [Fact]
    public void A_turntable_running_fast_still_matches_its_release()
    {
        var slow = Detected.Select(d => d * 1.02).ToArray();

        var match = ReleaseMatcher.Measure(Lengths(Detected), slow);

        Assert.True(match.Score > ReleaseMatcher.GoodMatch, $"score was {match.Score}");
        Assert.InRange(match.DriftFactor, 1.015, 1.025);
    }

    [Fact]
    public void The_wrong_number_of_tracks_cannot_score_well_however_the_total_adds_up()
    {
        // Four tracks summing to the same total as our five: the running time agrees and the record is
        // still not this one.
        double total = Detected.Sum();
        var match = ReleaseMatcher.Measure(Lengths(total / 4, total / 4, total / 4, total / 4), Detected);

        Assert.Equal(MatchBasis.TotalOnly, match.Basis);
        Assert.False(match.CountMatches);
        Assert.True(match.Score < ReleaseMatcher.GoodMatch, $"score was {match.Score}");
    }

    /// <summary>
    /// A listing with the right count but no times does not get the flat 45% that a wrong count
    /// earns. It is a strong match that the lengths could not confirm, scored as such and labelled
    /// <see cref="MatchBasis.CountOnly"/>.
    /// </summary>
    [Fact]
    public void A_right_count_with_no_times_is_a_real_match_the_lengths_could_not_confirm()
    {
        var noTimes = new double?[] { null, null, null, null, null };

        var matching = ReleaseMatcher.Measure(noTimes, Detected);
        var wrongCount = ReleaseMatcher.Measure(new double?[] { null, null }, Detected);

        Assert.Equal(MatchBasis.CountOnly, matching.Basis);
        Assert.True(matching.CountMatches);
        Assert.Equal(ReleaseMatcher.CountOnlyScore, matching.Score, 6);
        Assert.True(matching.Score > wrongCount.Score);
        Assert.True(matching.Score < ReleaseMatcher.GoodMatch);   // a count is evidence, not proof
        Assert.True(double.IsNaN(matching.MeanErrorSeconds));

        Assert.Equal(MatchBasis.TotalOnly, wrongCount.Basis);
    }

    /// <summary>The score falls gradually, not at a cliff: an off-by-one count (a welded pair, a
    /// missed side) is a plausible vinyl detection and must outscore an off-by-many, given the same
    /// total running time.</summary>
    [Fact]
    public void A_count_that_is_off_by_one_outscores_one_off_by_many()
    {
        double total = Detected.Sum();
        var offByOne = ReleaseMatcher.Measure(Lengths(Enumerable.Repeat(total / 6, 6).ToArray()), Detected);
        var offByThree = ReleaseMatcher.Measure(Lengths(Enumerable.Repeat(total / 8, 8).ToArray()), Detected);

        Assert.Equal(MatchBasis.TotalOnly, offByOne.Basis);
        Assert.Equal(MatchBasis.TotalOnly, offByThree.Basis);
        Assert.True(offByOne.Score > offByThree.Score);
        Assert.True(offByOne.Score < ReleaseMatcher.GoodMatch);
    }

    [Fact]
    public void A_track_with_no_catalogue_time_does_not_sink_the_others()
    {
        var match = ReleaseMatcher.Measure(
            new double?[] { 224, null, 196, 250, 311 }, Detected);

        Assert.Equal(MatchBasis.Lengths, match.Basis);
        Assert.True(match.CountMatches);
        Assert.True(match.Score > ReleaseMatcher.GoodMatch, $"score was {match.Score}");
    }

    /// <summary>
    /// Real MusicBrainz data for Fleetwood Mac's "Rumours". It marks the limit of what a length can
    /// settle. The 1977 UK vinyl and the 1984 CD have <i>identical</i> track times (MusicBrainz
    /// attaches the same recordings to both), so no comparison of lengths can ever separate them. The
    /// 2013 remaster is a different master and does come apart.
    ///
    /// <para>So the match score picks the master, not the pressing. Telling the 1977 cut from the 1984
    /// CD is the job of the country, year, format and catalogue number in the search results. The two
    /// do different jobs, which is why the results need both.</para>
    /// </summary>
    [Fact]
    public void Real_pressings_the_remaster_is_told_apart_but_identical_masters_cannot_be()
    {
        var vinyl1977 = Lengths(174, 258, 135, 194, 220, 203, 271, 213, 195, 238, 302);
        var cd1984 = Lengths(174, 258, 135, 194, 220, 203, 271, 213, 195, 238, 302);
        var cd2013 = Lengths(176, 258, 134, 193, 224, 202, 270, 214, 197, 236, 296);

        // The 1977 record, from a deck running 1% fast.
        var detected = new double[] { 174, 258, 135, 194, 220, 203, 271, 213, 195, 238, 302 }
            .Select(s => s * 1.01).ToArray();

        var match1977 = ReleaseMatcher.Measure(vinyl1977, detected);
        var match1984 = ReleaseMatcher.Measure(cd1984, detected);
        var match2013 = ReleaseMatcher.Measure(cd2013, detected);

        Assert.True(match1977.Score > ReleaseMatcher.GoodMatch);
        Assert.InRange(match1977.DriftFactor, 1.005, 1.015);      // the deck, correctly read

        // Same master, same times: the score cannot prefer one, and must not pretend to.
        Assert.Equal(match1977.Score, match1984.Score, 6);

        // A different master has different lengths, and the score can see that.
        Assert.True(match2013.Score < match1977.Score);
    }

    [Fact]
    public void Nothing_to_compare_scores_nothing()
    {
        var empty = ReleaseMatcher.Measure(Array.Empty<double?>(), Detected);
        Assert.Equal(0, empty.Score);
        Assert.Equal(MatchBasis.None, empty.Basis);
        Assert.Equal(0, ReleaseMatcher.Measure(Lengths(200), Array.Empty<double>()).Score);
    }
}
