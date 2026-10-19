using RPM.Core.Analysis;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>The merged "one album, continuing track numbers" view derived from standard per-disc tags.</summary>
public class DiscNumberingTests
{
    private static TrackInfo T(uint disc, uint track, uint discCount = 2, uint trackCount = 0) => new()
    {
        FilePath = $"/m/{disc}-{track}.flac",
        Extension = ".flac",
        DiscNumber = disc,
        DiscCount = discCount,
        TrackNumber = track,
        TrackCount = trackCount,
    };

    [Fact]
    public void Disc_one_keeps_its_own_numbers()
    {
        var album = new[] { T(1, 1, trackCount: 3), T(1, 2, trackCount: 3), T(1, 3, trackCount: 3),
                            T(2, 1, trackCount: 2), T(2, 2, trackCount: 2) };
        Assert.Equal(1u, DiscNumbering.ContinuingNumber(album[0], album));
        Assert.Equal(3u, DiscNumbering.ContinuingNumber(album[2], album));
    }

    [Fact]
    public void Later_discs_continue_from_the_previous_disc_total()
    {
        var album = new[] { T(1, 1, trackCount: 3), T(1, 2, trackCount: 3), T(1, 3, trackCount: 3),
                            T(2, 1, trackCount: 2), T(2, 2, trackCount: 2) };
        // Disc 2 track 1 → 3 (disc 1) + 1
        Assert.Equal(4u, DiscNumbering.ContinuingNumber(album[3], album));
        Assert.Equal(5u, DiscNumbering.ContinuingNumber(album[4], album));
    }

    [Fact]
    public void A_missing_track_count_falls_back_to_the_highest_present_number()
    {
        // Disc 1 has no TrackCount tag but its tracks run 1..3; disc 2 track 1 must still land on 4.
        var album = new[] { T(1, 1), T(1, 2), T(1, 3), T(2, 1) };
        Assert.Equal(4u, DiscNumbering.ContinuingNumber(album[3], album));
    }

    [Fact]
    public void A_single_disc_album_is_unchanged()
    {
        var album = new[] { T(0, 1, discCount: 0), T(0, 2, discCount: 0) };
        Assert.Equal(2u, DiscNumbering.ContinuingNumber(album[1], album));
    }
}
