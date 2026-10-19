namespace RPM.Core.Analysis;

/// <summary>
/// How much levelling is applied: whether one record plays at the level of the previous one, and
/// what "the same level" is measured over.
/// </summary>
/// <remarks>Persisted BY NAME wherever it is stored. If it were stored as a number, a mode inserted
/// in the middle would silently change every saved choice to the neighbouring mode.</remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<NormalizeMode>))]
public enum NormalizeMode
{
    /// <summary>Every record plays at the level it was mastered at.</summary>
    Off,

    /// <summary>
    /// The default. Each record is levelled as a whole, so the loud song and the quiet one keep the
    /// difference in level that the person who sequenced the record intended.
    /// </summary>
    Album,

    /// <summary>Every track is levelled on its own. This suits a shuffled queue of unrelated songs,
    /// but it flattens a record that was built to rise and fall in level.</summary>
    Track,
}

/// <summary>What is known about how loud something is: a track, or a whole record.</summary>
/// <param name="Lufs">Programme loudness. Null when nothing has measured it.</param>
/// <param name="TruePeak">Peak as a linear amplitude, 1.0 being full scale. Null when unknown, in
/// which case a boost is applied without the protection it would have given.</param>
public readonly record struct Level(double? Lufs, double? TruePeak)
{
    public static readonly Level Unknown = new(null, null);

    /// <summary>False for a track that has not been measured, and for one that could not be
    /// measured. The meter returns negative infinity for silence and for a file it could not read,
    /// and a gain cannot be computed from either.</summary>
    public bool Known => Lufs is { } lufs && double.IsFinite(lufs);
}

/// <summary>
/// Everything the decision needs about the record being played, apart from the measurements.
/// </summary>
/// <param name="Mode">The global setting.</param>
/// <param name="IncludeClassical">Whether classical recordings are levelled too. Normally false.</param>
/// <param name="AlbumOverride">The listener's choice for this record: null to let the other settings
/// decide, true or false to decide it directly.</param>
/// <param name="AlbumIsClassical">Whether the listener has marked this record classical. This is never
/// detected automatically: an album is classical only if the listener marked it.</param>
/// <param name="InAlbumOrder">Whether the record is played as a whole record and not as one song
/// in a shuffled queue. See <see cref="Normalization.Resolve"/>.</param>
public readonly record struct NormalizeContext(
    NormalizeMode Mode,
    bool IncludeClassical = false,
    bool? AlbumOverride = null,
    bool AlbumIsClassical = false,
    bool InAlbumOrder = true);

/// <summary>What was decided, and the gain that came out of it.</summary>
/// <param name="GainDb">What to apply, in dB. Zero when nothing is to be done. This covers
/// levelling being off, this record being excluded, and the track not being measured yet.</param>
/// <param name="Basis">Which reading the gain came from, so that the UI can show it.</param>
public readonly record struct NormalizeDecision(double GainDb, NormalizeBasis Basis)
{
    public static readonly NormalizeDecision None = new(0, NormalizeBasis.None);

    /// <summary>The gain as a linear multiplier, ready for the audio pipeline.</summary>
    public float Linear => ReplayGain.ToLinear(GainDb);

    /// <summary>True when this changes the sound. A gain below a hundredth of a dB cannot be heard
    /// and is not worth switching the pipeline out of bypass.</summary>
    public bool Applies => Math.Abs(GainDb) >= 0.01;
}

/// <summary>Which measurement a gain was computed from.</summary>
public enum NormalizeBasis
{
    /// <summary>Nothing was applied.</summary>
    None,

    /// <summary>The record as a whole. The loud song and the quiet one keep their difference in
    /// level.</summary>
    Album,

    /// <summary>This track on its own.</summary>
    Track,
}

/// <summary>
/// Whether this record is levelled, what it is levelled against, and by how much.
///
/// <para>This logic is kept apart from the player and the settings page because it contains the
/// decisions, and a wrong decision is not visible: a record that is levelled by mistake just plays at
/// a level the listener did not choose, and nothing reports an error.</para>
/// </summary>
public static class Normalization
{
    /// <summary>
    /// The gain to play a track at.
    ///
    /// <para>The settings are applied from the most specific to the least specific. The listener's
    /// choice for this one record wins over everything, including the classical rule: a listener who
    /// switches levelling on for a boxed set has given a more precise instruction than "leave
    /// classical alone". Next comes the classical rule, and last the global mode.</para>
    ///
    /// <para>Which reading is used is a separate question from whether levelling is used. It depends
    /// on how the record is played, not on a setting: album gain in album order, track gain on
    /// shuffle. The quiet song on a record is meant to be quiet where it sits, and is just a quiet
    /// song when it appears between two others.</para>
    /// </summary>
    /// <param name="track">This track's own measurement.</param>
    /// <param name="album">The record's measurement, when it is known. Album mode uses the track's own
    /// reading when it is not, so a half-measured library still plays evenly and does not wait for
    /// the pass to reach the end of the record.</param>
    public static NormalizeDecision Resolve(
        NormalizeContext context, Level track, Level album,
        double targetLufs = ReplayGain.ReferenceLufs)
    {
        if (!IsOn(context))
            return NormalizeDecision.None;

        bool wantsAlbum = context.Mode is NormalizeMode.Album && context.InAlbumOrder;

        if (wantsAlbum && album.Known)
            return Decide(album, NormalizeBasis.Album, targetLufs);

        return track.Known
            ? Decide(track, NormalizeBasis.Track, targetLufs)
            : NormalizeDecision.None;
    }

    /// <summary>Whether this record is levelled at all, before any measurement is used.</summary>
    public static bool IsOn(NormalizeContext context)
    {
        // The listener's own choice for this record decides it, either way.
        if (context.AlbumOverride is { } told)
            return told;

        if (context.Mode is NormalizeMode.Off)
            return false;

        // A symphony's quiet movement is meant to be quiet, but a meter reads it as a record at the
        // wrong level. Album mode protects movements that share a folder. It does not help for a
        // recital disc of four unrelated works, or for a box set with one disc per movement. For this
        // reason it is a separate rule and not a mode.
        return context.IncludeClassical || !context.AlbumIsClassical;
    }

    private static NormalizeDecision Decide(Level level, NormalizeBasis basis, double targetLufs)
    {
        double gain = ReplayGain.For(level.Lufs!.Value, level.TruePeak ?? 0, targetLufs);
        return new NormalizeDecision(gain, basis);
    }
}
