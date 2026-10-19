using System.Globalization;

namespace RPM.Fingerprint;

/// <summary>
/// One thing known about one record (the levelling, the tempo and its grid, the key, the edges),
/// with everything needed to decide whether a newly arrived version of it is better.
///
/// <para>
/// One fact per field, not one row per analysis. If a whole analysis had one version number, the only
/// way to improve it would be to discard all of it for every user at once: every user would analyse
/// their library again at the same time, and the shared pool would be empty until it filled again.
/// With a version per fact, a better tempo reading replaces only the tempo and keeps the key, the
/// loudness and the cue points.
/// </para>
/// </summary>
/// <param name="Field">Which fact this is: one of the names on this type.</param>
/// <param name="Generation">The measuring code's generation for this field only. Higher wins.</param>
/// <param name="Value">The measurement, as the field's own JSON. Not read here: what makes one reading
/// better than another is the provenance next to it, not the number inside it.</param>
/// <param name="Modelled">Whether a trained model produced it, not the DSP passes. At equal
/// generations this decides, because the two disagree about the tempo octave on some records and
/// about the bar by one beat on others.</param>
/// <param name="Confidence">How sure the measurement was, 0..1. A minimum for what may be served, not
/// a ranking: this does not order a confident wrong answer and an uncertain right one.</param>
public sealed record RecordingFact(
    string Field,
    int Generation,
    string Value,
    bool Modelled = false,
    double Confidence = 1)
{
    /// <summary>How loud the record was mastered: R128 loudness and true peak.</summary>
    public const string Level = "level";

    /// <summary>Tempo, beat times, downbeat, bar length.</summary>
    public const string Grid = "grid";

    /// <summary>The musical key.</summary>
    public const string Key = "key";

    /// <summary>Energy over the record, and the one-number summary.</summary>
    public const string Energy = "energy";

    /// <summary>Where to mix in and out.</summary>
    public const string Cues = "cues";

    /// <summary>Where the audio and the beat actually begin and end. Without it, a grid continues
    /// past the end of the music and a changeover is timed to a downbeat in silence.</summary>
    public const string Edges = "edges";

    /// <summary>The record measured in three frequency ranges instead of one (low, middle and high),
    /// plus how much of it is in the vocal range. A changeover needs this to move the bass from one
    /// record to the other instead of fading both at once.</summary>
    public const string Bands = "bands";

    /// <summary>The structure of the record, from those bands: where its phrases are, how steady
    /// the tempo is, where the beat returns after a breakdown, and how loud it is at the two points
    /// where a changeover starts and ends.</summary>
    public const string Shape = "shape";

    /// <summary>Every field this build knows. Anything else is refused, not stored, so a client
    /// cannot fill the pool with names that no code reads.</summary>
    public static readonly IReadOnlySet<string> Fields =
        new HashSet<string>(StringComparer.Ordinal) { Level, Grid, Key, Energy, Cues, Edges, Bands, Shape };

    /// <summary>The maximum value length. Large enough for a beat grid (a few hundred numbers) and
    /// small enough to keep rows a manageable size.</summary>
    public const int MaxValueLength = 192 * 1024;

    /// <summary>True when this is a fact of a kind the store keeps, in a shape it can hold.</summary>
    public bool IsWellFormed =>
        Fields.Contains(Field)
        && Generation > 0
        && Value is { Length: > 0 and <= MaxValueLength }
        && Confidence is >= 0 and <= 1;
}

/// <summary>What is to be done with one arriving fact.</summary>
public enum FactVerdict
{
    /// <summary>Nothing is stored yet, or the arriving fact is better. Write it.</summary>
    Take,

    /// <summary>The same measurement, sent again by another client. Keep what is stored and count
    /// the agreement. Agreement is the only thing that makes one client's reading trusted.</summary>
    Agree,

    /// <summary>What is stored is as good or better. Nothing to do.</summary>
    Keep,

    /// <summary>The same code at the same generation produced a different answer, which should not
    /// happen. Keep what is stored and record that the two disagree.</summary>
    Dispute,

    /// <summary>Not a fact this store keeps.</summary>
    Refuse,
}

/// <summary>
/// Which of two readings of the same thing wins.
///
/// <para>Pure, and tested without the table, because this rule decides what one client's analysis
/// may change for every other user. The better reading wins: a newer generation over an older one,
/// a model's reading over the DSP's at the same generation, and the two are never merged. The mean
/// of two downbeats is a downbeat that is wrong for everyone.</para>
/// </summary>
public static class RecordingFacts
{
    /// <summary>Below this a reading is not served to other users, whatever produced it.</summary>
    public const double MinConfidence = 0.05;

    /// <summary>
    /// What to do with <paramref name="arriving"/> given what is already <paramref name="stored"/>
    /// (null when nothing is).
    /// </summary>
    public static FactVerdict Judge(RecordingFact arriving, RecordingFact? stored)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        if (!arriving.IsWellFormed || arriving.Confidence < MinConfidence)
            return FactVerdict.Refuse;

        if (stored is null)
            return FactVerdict.Take;

        if (arriving.Field != stored.Field)
            return FactVerdict.Refuse;

        if (arriving.Generation != stored.Generation)
            return arriving.Generation > stored.Generation ? FactVerdict.Take : FactVerdict.Keep;

        // Same generation. A model's reading replaces the DSP's; the DSP's never replaces a model's.
        if (arriving.Modelled != stored.Modelled)
            return arriving.Modelled ? FactVerdict.Take : FactVerdict.Keep;

        // Same generation, same kind of reading, same audio: the numbers must be identical, because
        // the analysis is deterministic. An identical value is a second confirmation of what is stored.
        if (string.Equals(arriving.Value, stored.Value, StringComparison.Ordinal))
            return FactVerdict.Agree;

        // When they are not identical, something is wrong that this code cannot see: a different
        // decode, a different build, or a client sending numbers it did not measure. What is stored
        // stays. The disagreement is counted, so it can be investigated.
        return FactVerdict.Dispute;
    }

    /// <summary>A number in the store's format: invariant, round-trippable, and the same on every
    /// machine, so that two correct clients measuring one record produce the same string and are
    /// counted as agreeing.</summary>
    public static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
