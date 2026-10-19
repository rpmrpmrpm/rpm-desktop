using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Which disc of a multi-disc release a recording is: the automatic part of the lookup window's disc
/// picker and the disc-per-album import's "disc N of M" stamp.
/// </summary>
public class DiscMatcherTests
{
    private static ReleaseTrackInfo T(uint pos, uint disc, uint discPos, double? len) =>
        new(pos, $"t{pos}", "A", len, disc, discPos);

    private static ReleaseDetail TwoDisc() => new(
        new ReleaseRef("musicbrainz", "r"), "Set", "A", 2000, null, null,
        [
            T(1, 1, 1, 200), T(2, 1, 2, 210), T(3, 1, 3, 220),   // disc 1: three tracks
            T(4, 2, 1, 300), T(5, 2, 2, 305),                    // disc 2: two tracks
        ],
        DiscCount: 2);

    [Fact]
    public void Discs_lists_the_disc_numbers_in_order() =>
        Assert.Equal(new uint[] { 1, 2 }, DiscMatcher.Discs(TwoDisc()));

    [Fact]
    public void A_single_disc_release_has_no_disc_to_pick()
    {
        var single = new ReleaseDetail(new ReleaseRef("x", "1"), "One", "A", 2000, null, null,
            [new ReleaseTrackInfo(1, "t", "A", 100), new ReleaseTrackInfo(2, "t2", "A", 100)]);
        Assert.Equal(0u, DiscMatcher.BestDisc(single, null, 2));
    }

    [Fact]
    public void Track_count_alone_picks_the_disc_that_matches()
    {
        // Two tracks detected, no lengths known (a new CD rip): disc 2 has two tracks.
        Assert.Equal(2u, DiscMatcher.BestDisc(TwoDisc(), null, detectedCount: 2));
        // Three tracks detected: disc 1.
        Assert.Equal(1u, DiscMatcher.BestDisc(TwoDisc(), null, detectedCount: 3));
    }

    [Fact]
    public void Lengths_pick_the_disc_whose_durations_fit()
    {
        // Durations close to disc 2's (300, 305).
        Assert.Equal(2u, DiscMatcher.BestDisc(TwoDisc(), new double[] { 299, 306 }, 2));
    }

    [Fact]
    public void TracksOf_returns_one_discs_tracks_in_order()
    {
        var tracks = DiscMatcher.TracksOf(TwoDisc(), 2);
        Assert.Equal(new uint[] { 1, 2 }, tracks.Select(t => t.DiscPosition).ToArray());
    }
}
