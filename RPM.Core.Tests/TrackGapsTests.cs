using RPM.Core.Recording;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// Silence is not the same thing as a track break. A song holds a beat, drops out before the last
/// chorus, or fades to nothing, and none of that is a new track. What marks a track break on a
/// record is that the gap is cut by a lathe: it repeats at the same length, and it is
/// <i>empty</i>, because nothing was cut into it at all.
/// </summary>
public class TrackGapsTests
{
    /// <summary>A silence of the given length sitting at the given level.</summary>
    private static SilenceCandidate Gap(double startSec, double lengthSec, double floorDb = -60) =>
        new(startSec, startSec + lengthSec, startSec + lengthSec / 2, floorDb);

    // ── length: the gap repeats, the music does not ──

    [Fact]
    public void The_repeated_length_is_the_gap()
    {
        // Four 2 s gaps between tracks, and one 1 s pause inside a song.
        var candidates = new[]
        {
            Gap(100, 2.0), Gap(300, 2.1), Gap(520, 1.9), Gap(700, 2.0),
            Gap(420, 1.0),   // a beat held mid-song
        };

        var profile = TrackGaps.Profile(candidates);

        Assert.NotNull(profile);
        Assert.InRange(profile!.LengthSeconds, 1.9, 2.1);
        Assert.Equal(4, profile.Support);
    }

    [Fact]
    public void A_pause_inside_a_song_is_dropped_and_the_gaps_survive()
    {
        var candidates = new[]
        {
            Gap(100, 2.0), Gap(300, 2.1), Gap(520, 1.9), Gap(700, 2.0),
            Gap(420, 1.0),
        };

        var kept = TrackGaps.KeepRepeatedGaps(candidates);

        Assert.Equal(4, kept.Count);
        Assert.DoesNotContain(kept, c => c.StartSec == 420);
    }

    /// <summary>A floor, not a window: a silence longer than the usual gap is still a gap, and
    /// dropping it would merge two tracks into one.</summary>
    [Fact]
    public void A_silence_longer_than_the_gap_is_still_a_gap()
    {
        var candidates = new[]
        {
            Gap(100, 2.0), Gap(300, 2.0), Gap(520, 2.0),
            Gap(700, 6.0),   // a long pause before the side's last track
        };

        var kept = TrackGaps.KeepRepeatedGaps(candidates);

        Assert.Equal(4, kept.Count);
        Assert.Contains(kept, c => c.StartSec == 700);
    }

    [Fact]
    public void Nothing_repeating_means_no_profile_and_no_filtering()
    {
        // Very different lengths: there is no lathe here, so guessing would be worse than not trying.
        var candidates = new[] { Gap(100, 1.0), Gap(300, 4.0), Gap(520, 12.0) };

        Assert.Null(TrackGaps.Profile(candidates));
        Assert.Equal(3, TrackGaps.KeepRepeatedGaps(candidates).Count);
    }

    [Fact]
    public void A_single_silence_is_not_a_pattern()
    {
        Assert.Null(TrackGaps.Profile(new[] { Gap(100, 2.0) }));
    }

    [Fact]
    public void The_gap_length_is_the_median_of_the_ones_that_agreed()
    {
        // 2.0 / 2.1 / 2.6 all agree within tolerance; the canonical length should not be dragged
        // out to the longest of them.
        var candidates = new[] { Gap(100, 2.0), Gap(300, 2.1), Gap(500, 2.6) };
        var profile = TrackGaps.Profile(candidates)!;
        Assert.InRange(profile.LengthSeconds, 2.0, 2.2);
    }

    // ── depth: a gap is empty, a pause is not ──

    [Fact]
    public void Only_silences_that_reach_the_noise_floor_are_gaps()
    {
        var candidates = new[]
        {
            Gap(100, 2.0, floorDb: -58),   // bare groove
            Gap(300, 2.0, floorDb: -60),   // bare groove
            Gap(420, 2.0, floorDb: -34),   // a song's reverb tail: quiet, but not empty
        };

        var kept = TrackGaps.KeepAtNoiseFloor(candidates, noiseFloorDb: -60);

        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, c => c.StartSec == 420);
    }

    [Fact]
    public void A_floor_that_rejects_everything_is_not_believed()
    {
        // If nothing reaches the floor that was passed in, the floor is wrong. Keep the silences rather
        // than return a record with no tracks in it.
        var candidates = new[] { Gap(100, 2.0, floorDb: -40), Gap(300, 2.0, floorDb: -42) };
        Assert.Equal(2, TrackGaps.KeepAtNoiseFloor(candidates, noiseFloorDb: -90).Count);
    }

    // ── estimating the floor from two sources that can each be wrong ──

    /// <summary>The lead-in is where the needle lands and where the record is handled, so it is
    /// often the most scratched groove on the side and reads far too loud. The gaps are more
    /// reliable.</summary>
    [Fact]
    public void A_scratched_lead_in_does_not_raise_the_floor()
    {
        var candidates = new[] { Gap(100, 2, floorDb: -60), Gap(300, 2, floorDb: -58), Gap(500, 2, floorDb: -59) };

        var floor = TrackGaps.EstimateNoiseFloor(candidates, leadInDb: -35);   // scratched lead-in

        Assert.Equal(-59, floor!.Value, 1);   // the gaps' own median, not the damage
    }

    /// <summary>The mirror case: if the silences are mostly pauses in songs, their middle is not the
    /// floor either, and then a clean lead-in is the better source.</summary>
    [Fact]
    public void A_clean_lead_in_beats_silences_that_are_not_really_gaps()
    {
        var candidates = new[] { Gap(100, 2, floorDb: -30), Gap(300, 2, floorDb: -28) };

        var floor = TrackGaps.EstimateNoiseFloor(candidates, leadInDb: -57);

        Assert.Equal(-57, floor!.Value, 1);
    }

    /// <summary>The quietest silence on a vinyl recording is usually the stretch before the needle
    /// landed: electrical silence, quieter than any groove. The median must ignore it.</summary>
    [Fact]
    public void Silence_from_before_the_needle_landed_does_not_drag_the_floor_down()
    {
        var candidates = new[]
        {
            Gap(0, 4, floorDb: -95),    // recording started, needle not down yet
            Gap(100, 2, floorDb: -58),
            Gap(300, 2, floorDb: -60),
            Gap(500, 2, floorDb: -59),
        };

        var floor = TrackGaps.EstimateNoiseFloor(candidates, leadInDb: null);

        Assert.InRange(floor!.Value, -61, -57);   // groove noise, not the dead air before it
    }

    [Fact]
    public void With_no_witnesses_there_is_no_floor()
    {
        Assert.Null(TrackGaps.EstimateNoiseFloor(Array.Empty<SilenceCandidate>(), leadInDb: null));
    }
}
