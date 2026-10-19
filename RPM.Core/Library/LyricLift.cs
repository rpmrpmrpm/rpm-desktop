namespace RPM.Core.Library;

/// <summary>What a song's words are allowed to do to where it sits in the Chill list.
///
/// <para><b>An addition to the measurements, never a decider.</b> A song that nobody has read keeps the
/// place that brightness, onsets and dynamics gave it. A song that has been read moves from that place a
/// little, in the direction its words point. Nothing is excluded for having no lyrics, and nothing is
/// placed by a number that was not measured.</para>
///
/// <para><b>Why there is no audio fallback.</b> The obvious idea is to estimate a valence from the audio
/// for songs with no lyric sheet. That does not work. On a real shelf, the 1,184 songs that have both a
/// sheet and audio show the lyric reading correlating with brightness at r = +0.06, with onsets at +0.01
/// and with dynamics at -0.05. The audio carries almost no information about this axis. An estimate built
/// from it would be noise. On a shelf where 93% of songs have no sheet, almost the whole list would be
/// arranged by that noise.</para>
///
/// <para><b>Chill only, for now.</b> This is a product choice, not a measured finding. A calm song with
/// bleak words is still calm, and whether Chill should prefer the other kind is a matter of opinion. The
/// other four lists read nothing here.</para>
/// </summary>
internal sealed class LyricLift
{
    /// <summary>How far the words may move a song, up or down.
    ///
    /// <para>A tenth, against a ranking that runs from nought to one across the whole band. So the words
    /// reorder a song against its neighbours. They can never carry a song from the edge of the band to the
    /// middle of it. The measurement decides which songs are calm and roughly how calm. The words decide
    /// between songs that the measurement has already placed close together.</para>
    /// </summary>
    internal const double Weight = 0.1;

    /// <summary>The fewest read songs before the lift is applied at all.
    ///
    /// <para>A lift is a song's place among the songs that have been read. Among three songs, a percentile
    /// is an accident of which three were played, not a position. Twenty is the same floor that
    /// <see cref="Lyrics.LyricValence.LeastWords"/> uses, for the same reason: below it the number is
    /// noise, and noise lands at the extremes.</para></summary>
    internal const int LeastRead = 20;

    /// <summary>Every believed reading in the collection, ascending. The lift is a song's place in
    /// this, so it is taken from the collection the listener has rather than from the lexicon's
    /// scale.</summary>
    private readonly double[] _sorted;

    private LyricLift(double[] sorted) => _sorted = sorted;

    /// <summary>Nothing has been read, so nothing moves. This is used instead of a null, so callers do not
    /// need a null check.</summary>
    internal static readonly LyricLift Nothing = new([]);

    /// <summary>
    /// Takes the readings out of the measured rows.
    /// </summary>
    /// <param name="measured">The same rows the bands are cut from, so the lift and the cut are
    /// answering about one collection.</param>
    internal static LyricLift Over(IReadOnlyList<MeasuredMood> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);

        var read = measured.Where(m => m.LyricValence is not null)
                           .Select(m => m.LyricValence!.Value)
                           .ToArray();

        if (read.Length < LeastRead)
            return Nothing;

        Array.Sort(read);
        return new LyricLift(read);
    }

    /// <summary>How many songs the lift is taken over. Nought where it does nothing.</summary>
    internal int Read => _sorted.Length;

    /// <summary>What this song's words are worth, from <c>-Weight</c> to <c>+Weight</c>, and nought where
    /// the song has no reading.
    ///
    /// <para><b>A place among the read songs, not a reading on the lexicon's scale. This is why the lift is
    /// neutral.</b> The readings on a real shelf average well above the middle of the lexicon's range.
    /// Adding a scaled reading would push almost every song that has one above every song that does not,
    /// and the list would split into the songs somebody has played and the rest. Taken as a place among the
    /// read songs, half of them move up and half move down. The songs with no reading stay exactly where
    /// they were.</para>
    /// </summary>
    internal double For(MeasuredMood track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (_sorted.Length == 0 || track.LyricValence is not { } valence)
            return 0;

        return Weight * ((2 * Place(valence)) - 1);
    }

    /// <summary>Where a reading falls among the others, from nought to one. This is the share of readings
    /// below it, plus half the share equal to it. So a value that every song shares sits in the middle
    /// rather than at one end.</summary>
    private double Place(double valence)
    {
        var below = 0;
        var same = 0;
        foreach (var read in _sorted)
        {
            if (read < valence) below++;
            else if (read == valence) same++;
        }

        return (below + (same / 2.0)) / _sorted.Length;
    }
}
