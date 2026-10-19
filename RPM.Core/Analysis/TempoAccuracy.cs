namespace RPM.Core.Analysis;

/// <summary>
/// Whether one tempo reading agrees with another, using the standard terms of the field: a percentage
/// tolerance, and a separate answer for whether the two are the same tempo counted differently.
///
/// <para>Two readings of the same record can disagree in two different ways, and combining them loses
/// useful information. One is a near miss, for example 128.4 against 128.0, where the grid is
/// slightly fast and nothing in a mix would notice. The other is an octave error: 64 against 128, or
/// 85.3 against 128 where a bar of four was counted as three. This is not a large version of the
/// small error. It is a different reading of the same beats, and it decides whether a fade runs for
/// the right number of bars.</para>
///
/// <para>So the same pair of numbers is scored twice. <see cref="Matches"/> is the strict answer
/// (MIREX Accuracy1). <see cref="MatchesAtAnyOctave"/> also accepts the half, the double, the third
/// and the triple (Accuracy2). Results against a public corpus are reported as that pair, so a
/// number here can be compared with a published one.</para>
/// </summary>
public static class TempoAccuracy
{
    /// <summary>Four percent, the tolerance that published tempo tables use. It is wide enough
    /// that two correct readings of a record that drifts still agree, and much narrower than the gap
    /// to the next octave.</summary>
    public const double DefaultTolerance = 0.04;

    /// <summary>The factors by which a reading may differ and still be the same reading: counted in
    /// half-time or double-time, or a bar of three read as a bar of four and the reverse.</summary>
    public static readonly IReadOnlyList<double> OctaveFactors = [1.0, 2.0, 3.0, 1.0 / 2, 1.0 / 3];

    /// <summary>Accuracy1: the estimate is within <paramref name="tolerance"/> of the reference.</summary>
    public static bool Matches(double estimate, double reference, double tolerance = DefaultTolerance) =>
        reference > 0 && estimate > 0 && Math.Abs(estimate - reference) <= tolerance * reference;

    /// <summary>Accuracy2: the estimate matches the reference at any of <see cref="OctaveFactors"/>.</summary>
    public static bool MatchesAtAnyOctave(double estimate, double reference,
                                          double tolerance = DefaultTolerance)
    {
        foreach (double factor in OctaveFactors)
            if (Matches(estimate, reference * factor, tolerance))
                return true;

        return false;
    }

    /// <summary>The factor by which the estimate differs, or 0 when it matches at none of them.
    /// A series of these results shows whether a reader loses tracks to half-time or to triplets.
    /// The two problems need opposite fixes.</summary>
    public static double OctaveOf(double estimate, double reference,
                                  double tolerance = DefaultTolerance)
    {
        foreach (double factor in OctaveFactors)
            if (Matches(estimate, reference * factor, tolerance))
                return factor;

        return 0;
    }

    /// <summary>Accuracy0: the two agree to the whole BPM. It is reported with the other two
    /// because a reader can pass Accuracy1 with a wide tolerance while always being one BPM off.</summary>
    public static bool MatchesExactly(double estimate, double reference) =>
        reference > 0 && estimate > 0 && Math.Round(estimate) == Math.Round(reference);
}
