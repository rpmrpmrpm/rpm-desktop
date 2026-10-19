using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Which disc of an album the one in the drive is, answered from the library. The main case is when
/// the catalogue has no answer: a first CD ripped with no disc number, then a second one, and the
/// naming template silently filing both into one folder.
/// </summary>
public class DiscProposalTests
{
    private static List<(uint, double)> Disc(uint number, params double[] seconds)
        => [.. seconds.Select(s => (number, s))];

    // ── nothing to go on ──

    [Fact]
    public void An_album_the_library_has_never_seen_is_disc_one_of_one()
    {
        var p = DiscProposals.For([], [210, 245, 198]);

        Assert.Equal(1u, p.Disc);
        Assert.Equal(1u, p.DiscCount);
        Assert.Equal(DiscProposalKind.FirstDisc, p.Kind);
    }

    // ── the case this is for ──

    [Fact]
    public void A_second_disc_joining_an_untagged_first_becomes_disc_two()
    {
        // A reissue the catalogue lists as two single-disc releases, so the first CD was ripped with
        // no disc number at all.
        var existing = Disc(0, 363, 260, 222, 238, 258);

        var p = DiscProposals.For(existing, [300, 271, 289, 245]);

        Assert.Equal(2u, p.Disc);
        Assert.Equal(2u, p.DiscCount);
        Assert.Equal(DiscProposalKind.NextDisc, p.Kind);
    }

    [Fact]
    public void A_third_disc_counts_from_the_highest_the_album_holds()
    {
        var existing = Disc(1, 200, 210).Concat(Disc(2, 190, 205)).ToList();

        var p = DiscProposals.For(existing, [180, 220, 240]);

        Assert.Equal(3u, p.Disc);
        Assert.Equal(3u, p.DiscCount);
    }

    /// <summary>A gap in the numbering is not filled in: discs 1 and 3 present means the next rip is 4,
    /// because the missing disc has not been seen and may not be 2.</summary>
    [Fact]
    public void A_gap_in_the_numbering_is_not_filled()
    {
        var existing = Disc(1, 200).Concat(Disc(3, 190)).ToList();

        Assert.Equal(4u, DiscProposals.For(existing, [180, 220]).Disc);
    }

    // ── the case that would corrupt the library silently ──

    [Fact]
    public void Re_ripping_a_disc_the_album_already_holds_proposes_that_disc_again()
    {
        var existing = Disc(1, 363, 260, 222).Concat(Disc(2, 300, 271)).ToList();

        // The same three tracks as disc 1, same running time. A bad rip, done again.
        var p = DiscProposals.For(existing, [363, 260, 222]);

        Assert.Equal(1u, p.Disc);
        Assert.Equal(DiscProposalKind.ReRip, p.Kind);
        Assert.Equal(2u, p.DiscCount);   // the album still has two discs; this is not a third
    }

    [Fact]
    public void A_re_rip_of_the_second_disc_is_recognised_as_the_second()
    {
        var existing = Disc(1, 363, 260, 222).Concat(Disc(2, 300, 271)).ToList();

        var p = DiscProposals.For(existing, [300, 271]);

        Assert.Equal(2u, p.Disc);
        Assert.Equal(DiscProposalKind.ReRip, p.Kind);
    }

    [Fact]
    public void A_disc_with_the_same_track_count_but_different_music_is_a_new_disc()
    {
        // Two discs of a box set often hold the same number of tracks. Length tells them apart.
        var existing = Disc(1, 363, 260, 222);

        var p = DiscProposals.For(existing, [180, 190, 200]);

        Assert.Equal(2u, p.Disc);
        Assert.Equal(DiscProposalKind.NextDisc, p.Kind);
    }

    [Fact]
    public void A_disc_with_the_same_running_time_but_a_different_track_count_is_a_new_disc()
    {
        var existing = Disc(1, 300, 300);   // 600s over two tracks

        var p = DiscProposals.For(existing, [200, 200, 200]);   // 600s over three

        Assert.Equal(2u, p.Disc);
        Assert.Equal(DiscProposalKind.NextDisc, p.Kind);
    }

    [Fact]
    public void Rounding_in_the_drives_reported_lengths_still_counts_as_the_same_disc()
    {
        var existing = Disc(1, 363.0, 260.0, 222.0);

        // Each track is about a second off, well inside the whole-disc tolerance.
        var p = DiscProposals.For(existing, [362.2, 260.4, 222.6]);

        Assert.Equal(DiscProposalKind.ReRip, p.Kind);
    }

    [Fact]
    public void A_disc_just_past_the_tolerance_is_a_new_disc()
    {
        var existing = Disc(1, 300, 300);

        var p = DiscProposals.For(existing, [300, 305]);   // 5s off, more than the 4s allowed

        Assert.Equal(DiscProposalKind.NextDisc, p.Kind);
    }
}
