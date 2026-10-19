namespace RPM.Core.Analysis;

/// <summary>Why a disc number is being proposed, which decides what the user is told.</summary>
public enum DiscProposalKind
{
    /// <summary>Nothing of this album is in the library, so it is disc one of one.</summary>
    FirstDisc,

    /// <summary>The album is already there, and this disc is not one of the ones it holds.</summary>
    NextDisc,

    /// <summary>This looks like a disc that the album already has: the same number of tracks and the
    /// same running time. It is almost always a re-rip of a disc that was ripped badly.</summary>
    ReRip,
}

/// <summary>What to stamp on the disc about to be ripped, and why.</summary>
public sealed record DiscProposal(uint Disc, uint DiscCount, DiscProposalKind Kind)
{
    public static readonly DiscProposal First = new(1, 1, DiscProposalKind.FirstDisc);
}

/// <summary>
/// Works out which disc of an album the one in the drive is, using the library and not a
/// catalogue.
///
/// <para>The catalogue is a guess and the library is the record. A release lookup can fail, or can
/// list a reissue as two separate single-disc releases. Then a rip has no disc number, and the
/// naming template still files it into the existing album's folder because the album name is the
/// same. The result is one album with two discs and nothing to say which is which. The library
/// already holds that album, so it can supply the answer.</para>
///
/// <para>This class only proposes. It cannot know that the disc ripped second is disc two (the user
/// may have put CD 2 in first), and different records often share a name ("Greatest Hits"). The
/// number it returns is shown to the user and can be edited. It is never written without the
/// user seeing it.</para>
/// </summary>
public static class DiscProposals
{
    /// <summary>
    /// How close the running times of two discs must be to count as the same disc, in seconds across
    /// the whole disc. A re-rip of the same CD has an identical length in samples. The tolerance only
    /// has to cover rounding and drives that report a partial frame differently.
    /// </summary>
    public const double SameDiscToleranceSeconds = 4.0;

    /// <summary>
    /// The disc to propose for a rip with the track lengths <paramref name="incomingSeconds"/>,
    /// given what the library already holds for that album.
    /// </summary>
    /// <param name="existing">Every track of the album already in the library: the disc it claims (0
    /// for "untagged", which counts as disc one) and its length.</param>
    /// <param name="incomingSeconds">The lengths of the tracks about to be ripped, in order.</param>
    public static DiscProposal For(
        IReadOnlyList<(uint Disc, double Seconds)> existing,
        IReadOnlyList<double> incomingSeconds)
    {
        if (existing.Count == 0)
            return DiscProposal.First;

        // Untagged counts as disc one. This is the case the code exists for: a first CD ripped with
        // no disc number, now joined by a second.
        var discs = existing
            .GroupBy(t => t.Disc == 0 ? 1u : t.Disc)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Seconds: g.Sum(t => t.Seconds)));

        var highest = discs.Keys.Max();

        // A disc that the album already holds, with this many tracks and this much music, is this
        // disc again. Proposing "disc 3" for a re-rip of disc 1 would invent a disc that does not
        // exist and leave the album with two copies of the same music under different numbers.
        var incomingCount = incomingSeconds.Count;
        var incomingTotal = incomingSeconds.Sum();
        foreach (var (number, disc) in discs.OrderBy(d => d.Key))
        {
            if (disc.Count == incomingCount
                && Math.Abs(disc.Seconds - incomingTotal) <= SameDiscToleranceSeconds)
                return new DiscProposal(number, highest, DiscProposalKind.ReRip);
        }

        return new DiscProposal(highest + 1, highest + 1, DiscProposalKind.NextDisc);
    }
}
