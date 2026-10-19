namespace RPM.Core.Analysis;

/// <summary>
/// A beat grid that another machine read, offered to the analyser in place of a model.
///
/// <para>It is used when the pool holds an analysis of the same track under the same fingerprint
/// (for example the same CD ripped on another computer). Two facts from that analysis are worth
/// taking and cannot be found cheaply here: which octave the tempo is in, and which beat of the bar
/// is the "one". Both need a transformer to read. Everything else is cheap to measure here: where
/// the beats fall on <em>this</em> copy, how loud it is, where its phrases and cues are. Measuring
/// them here is also more accurate than copying them from a rip that starts at a different
/// sample.</para>
///
/// <para>The borrowed reading enters through the same interface as a model. The analyser then runs
/// as usual, against a grid that did not need ten seconds of transformer time, and every curve in
/// the result describes the file actually being analysed.</para>
/// </summary>
/// <param name="reading">The grid, already fitted onto this copy (see <see cref="GridRealign"/>).</param>
public sealed class BorrowedBeats(BeatReading reading) : IBeatModel
{
    /// <inheritdoc />
    public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default) => reading;

    /// <summary>The beats and bar lines of an analysis, in the form a model would give them.</summary>
    public static BeatReading? Of(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.BeatTimes.Count < 8)
            return null;

        int perBar = Math.Max(1, analysis.BeatsPerBar);
        var downbeats = new List<double>(analysis.BeatTimes.Count / perBar + 1);
        for (int i = analysis.FirstDownbeat; i < analysis.BeatTimes.Count; i += perBar)
            if (i >= 0)
                downbeats.Add(analysis.BeatTimes[i]);

        return new BeatReading { BeatTimes = analysis.BeatTimes, DownbeatTimes = downbeats };
    }
}

/// <summary>The key from the same borrowed reading. A key is a property of the music and does not
/// change between rips, so it is copied unchanged.</summary>
public sealed class BorrowedKey(KeyReading reading) : IKeyModel
{
    /// <inheritdoc />
    public KeyReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default) => reading;

    /// <summary>The key of an analysis, or null when it has none worth copying.</summary>
    public static KeyReading? Of(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Key.IsKnown ? new KeyReading(analysis.Key, analysis.KeyConfidence) : null;
    }
}
