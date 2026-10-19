namespace RPM.Core.Mixing;

/// <summary>
/// One piece of the pull that the incoming record is played at. From <paramref name="AtSeconds"/> into
/// the changeover, it runs at <paramref name="Ratio"/> instead of the speed it had before.
/// </summary>
///
/// <param name="AtSeconds">How far into the changeover this piece begins, in the seconds the room
/// hears. It is always a bar line of the mix, so the speed only changes where the music does.</param>
/// <param name="CueSeconds">Where the incoming record is at that moment, in its own seconds. The piece
/// can be played without it, because the deck is already there. It is kept so the schedule can be
/// checked against the grid it was written from. The mix lab also draws it.</param>
/// <param name="Ratio">The pull from here to the next piece.</param>
///
/// <remarks>
/// <para>One pull for the whole changeover is the right answer only for two records that hold their
/// tempo. For a record that does not, the grid wanders, and one number cannot follow a moving target.
/// The two grids meet at the ends and drift apart in the middle. The room hears that as a flam.</para>
///
/// <para>So the pull follows the grid, not the tempo. Each piece is the speed that makes the incoming
/// record cover exactly the music that the record playing covers over the same stretch. It is worked
/// out from the two grids themselves. A warp marker in an arranger does the same job. Real gear cannot
/// see the future, so it measures the error and corrects it on every block. A planned changeover
/// already knows both grids, so it can play the right speed from the start.</para>
///
/// <para>Keep the pieces as few as will hold. Each boundary costs a decoder reopened at the new speed
/// (see <see cref="Deck.SetTempo"/>), and it is a step in the record's pitch. That step is small and
/// sits on a bar line. It is better than a flam, but it is not free.</para>
/// </remarks>
public readonly record struct TempoStep(double AtSeconds, double CueSeconds, double Ratio);
