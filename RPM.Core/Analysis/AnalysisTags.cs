namespace RPM.Core.Analysis;

/// <summary>What to write into one file's tags, taken from the analyser's measurements. Both parts
/// are optional: a track can have a tempo and no key, or a key and no tempo.</summary>
/// <param name="Bpm">The rounded tempo, or zero to leave the file's own value alone.</param>
/// <param name="Key">The key in tag form (see <see cref="MusicalKey.TagValue"/>), or null to leave
/// the file's own value alone.</param>
public readonly record struct AnalysisTagWrite(uint Bpm, string? Key)
{
    /// <summary>Nothing to write: the file is not opened at all.</summary>
    public static readonly AnalysisTagWrite Nothing = new(0, null);

    public bool Any => Bpm > 0 || !string.IsNullOrEmpty(Key);
}

/// <summary>
/// Decides whether the app's own measurement of a track is good enough to put into that track's
/// tags, and in what form.
///
/// <para>RPM measures the tempo and key of every track played in the DJ view, because it needs
/// them to beat-match. The measurements are stored in a cache keyed on the file, not in the file
/// itself, so other programs (Rekordbox, Serato, a file browser) do not see them. Writing them to
/// the tags fixes that.</para>
///
/// <para>The rules live here and not in the writer because the same decision is needed in three
/// places: the pass that writes tags as tracks are analysed, the manual backfill, and the box the
/// per-file editor fills in.</para>
/// </summary>
public static class AnalysisTags
{
    /// <summary>
    /// How sure the key detector must be before its answer is written anywhere.
    ///
    /// <para>This is not the probability that the key is right. Both readers report <b>how far the
    /// winner finished ahead of the runner-up</b>: the key model as the gap between the top two of
    /// twenty-four probabilities, the chromagram as the gap between the top two correlations,
    /// scaled. With twenty-four choices, a winner at 0.35 with the runner-up at 0.15 is a clear
    /// answer, and it scores 0.2.</para>
    ///
    /// <para>On a real collection, a threshold of 0.6 let through only a third of the tracks that
    /// had a key at all. At 0.2 about three quarters pass. What is rejected is the real coin toss,
    /// such as a relative major against its minor. That is the mistake a DJ notices at once, and it
    /// is worth refusing, because a wrong key in a file is worse than no key: every other program
    /// believes it.</para>
    ///
    /// <para>The threshold applies to what is written or filled in without being asked. It does
    /// <b>not</b> apply to <see cref="Detected"/>, which is what a person gets by pressing a button
    /// while looking at the confidence.</para>
    /// </summary>
    public const double MinKeyConfidence = 0.2;

    /// <summary>The measured tempo as a tag would carry it, or null when the reading is not worth
    /// writing. Only a track that a DJ could beat-match qualifies: a tempo guessed over a rubato
    /// ballad would be believed by other programs.</summary>
    public static uint? BpmOf(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!analysis.CanBeatMatch || analysis.Bpm <= 0)
            return null;

        var rounded = (uint)Math.Round(analysis.Bpm, MidpointRounding.AwayFromZero);
        return rounded > 0 ? rounded : null;
    }

    /// <summary>The measured key as a tag would carry it, or null when the detector was not sure
    /// enough.</summary>
    public static string? KeyOf(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Key.IsKnown && analysis.KeyConfidence >= MinKeyConfidence
            ? analysis.Key.TagValue
            : null;
    }

    /// <summary>
    /// Everything the analyser found, confident or not. This is what a person may take when they
    /// ask for it themselves.
    ///
    /// <para>The thresholds above decide what happens without being asked, which is a different
    /// question. A person who presses "use measured values" can see the reading and its confidence
    /// and has decided. Refusing the number they can see on screen would be wrong.</para>
    /// </summary>
    public static AnalysisTagWrite Detected(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return new AnalysisTagWrite(
            analysis.Bpm > 0 ? (uint)Math.Round(analysis.Bpm, MidpointRounding.AwayFromZero) : 0,
            analysis.Key.IsKnown ? analysis.Key.TagValue : null);
    }

    /// <summary>
    /// What this analysis should write into a file that currently carries
    /// <paramref name="fileBpm"/> and <paramref name="fileKey"/>.
    ///
    /// <para>Only blank fields are filled, unless <paramref name="overwrite"/> is true. A value
    /// already in the file was put there by a person or a tagging program (for example Picard or
    /// Mixed In Key), and a measurement must not override it. Only the explicit overwrite option
    /// of the backfill replaces existing values.</para>
    ///
    /// <para>A field the analyser is not sure about is left alone even when overwriting. "Replace
    /// the value with our answer" cannot mean "replace it with nothing".</para>
    /// </summary>
    public static AnalysisTagWrite Wanted(
        TrackAnalysis analysis, uint fileBpm, string? fileKey, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        uint bpm = 0;
        if ((overwrite || fileBpm == 0) && BpmOf(analysis) is { } measured && measured != fileBpm)
            bpm = measured;

        string? key = null;
        if ((overwrite || string.IsNullOrWhiteSpace(fileKey)) && KeyOf(analysis) is { } tagKey
            && !string.Equals(tagKey, fileKey?.Trim(), StringComparison.OrdinalIgnoreCase))
            key = tagKey;

        return new AnalysisTagWrite(bpm, key);
    }
}
