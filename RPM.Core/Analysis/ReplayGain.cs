using System.Globalization;

namespace RPM.Core.Analysis;

/// <summary>
/// Turns a loudness reading into the gain that a player applies, using ReplayGain 2.0.
///
/// <para>The standard is followed exactly, not approximated. Many files already contain these tags,
/// written by foobar2000, beets, Picard or rsgain, and a player that reads them can level a whole
/// library without decoding anything. The same tag names also carry ReplayGain 1.0 readings, which
/// used a different filter and a simpler average. The two versions work together because the
/// reference level was chosen so that they nearly agree.</para>
/// </summary>
public static class ReplayGain
{
    /// <summary>
    /// The level that everything is adjusted to, in LUFS. This is the ReplayGain 2.0 reference. It
    /// matches the 89 dB reference of the first version, so a library that is partly tagged by an
    /// older tool does not get two different results.
    ///
    /// <para>This is not the streaming target. Streaming services level to -14, which suits a phone
    /// on a train but gives up headroom that a record collection has no reason to lose.</para>
    /// </summary>
    public const double ReferenceLufs = -18.0;

    /// <summary>
    /// How close to full scale a normalised track may come, in dBTP.
    ///
    /// <para>The value is not zero. A converter reconstructs a curve through the samples, and that
    /// curve goes higher than the samples. A margin of 1 dB is the usual allowance, and it is about
    /// the difference between a sample peak and a true peak on real music.</para>
    /// </summary>
    public const double PeakCeilingDb = -1.0;

    /// <summary>How far a track may be turned up or down. A record that needs more than this is
    /// mastered so far from the others that moving it the whole way would be a bigger surprise than
    /// leaving it. On the quiet side it is usually a file that was measured wrongly.</summary>
    public const double MaxGainDb = 15.0;

    /// <summary>The gain that brings <paramref name="lufs"/> to <paramref name="targetLufs"/>, before
    /// checking what it does to the peaks. Zero for a track with no measurement.</summary>
    public static double GainDb(double lufs, double targetLufs = ReferenceLufs) =>
        double.IsFinite(lufs) ? targetLufs - lufs : 0;

    /// <summary>
    /// The gain to apply: <paramref name="gainDb"/> reduced as far as needed so that the track's
    /// loudest moment stays under <see cref="PeakCeilingDb"/>, and limited to
    /// <see cref="MaxGainDb"/> in both directions.
    ///
    /// <para>This only ever reduces a gain. A track whose peaks are far below the ceiling is not
    /// raised to it: the ceiling is a limit, not a target, and raising a quiet recording to meet it
    /// would change the level that was measured.</para>
    /// </summary>
    /// <param name="truePeak">The track's peak as a linear amplitude, 1.0 being full scale. Zero or
    /// less means it was never measured, so there is no peak to limit against.</param>
    public static double Clamp(double gainDb, double truePeak, double ceilingDb = PeakCeilingDb)
    {
        if (!double.IsFinite(gainDb))
            return 0;

        double capped = Math.Clamp(gainDb, -MaxGainDb, MaxGainDb);

        if (truePeak <= 0 || !double.IsFinite(truePeak))
            return capped;

        // The room between the peak and the ceiling. It is negative for a track that is already over
        // the ceiling, which is normal for modern masters and is exactly when this limit matters.
        double headroom = ceilingDb - 20 * Math.Log10(truePeak);
        return Math.Min(capped, headroom);
    }

    /// <summary>The measurement converted directly to the gain to apply, with the limits applied.
    /// This is the one call that a player needs.</summary>
    public static double For(double lufs, double truePeak, double targetLufs = ReferenceLufs) =>
        Clamp(GainDb(lufs, targetLufs), truePeak);

    /// <summary>A gain in dB as a linear multiplier.</summary>
    public static float ToLinear(double gainDb) =>
        double.IsFinite(gainDb) ? (float)Math.Pow(10, gainDb / 20) : 1f;

    /// <summary>
    /// An album's loudness from its tracks', in LUFS.
    ///
    /// <para>This is the mean of the tracks' mean squares, not the mean of their dB, because loudness
    /// does not average in decibels: a record with one loud song and one quiet song is not as loud as
    /// the midpoint of the two numbers. Tracks that were never measured are not counted.</para>
    ///
    /// <para>It approximates what BS.1770 would give for the whole record played end to end. It is
    /// close enough for levelling, and it does not need every track to be decoded again in one pass.
    /// It is weighted by length, so a forty-second interlude does not count as much as a ten-minute
    /// side.</para>
    /// </summary>
    public static double AlbumLufs(IEnumerable<(double Lufs, double Seconds)> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        double sum = 0;
        double weight = 0;

        foreach (var (lufs, seconds) in tracks)
        {
            if (!double.IsFinite(lufs))
                continue;

            double length = seconds > 0 ? seconds : 1;
            sum += Math.Pow(10, lufs / 10) * length;
            weight += length;
        }

        return weight > 0 ? 10 * Math.Log10(sum / weight) : double.NegativeInfinity;
    }

    /// <summary>A gain as ReplayGain writes it into a tag: <c>"-6.54 dB"</c>.</summary>
    public static string FormatGain(double gainDb) =>
        gainDb.ToString("0.00", CultureInfo.InvariantCulture) + " dB";

    /// <summary>A peak as ReplayGain writes it into a tag: a linear amplitude, 1.0 being full
    /// scale.</summary>
    public static string FormatPeak(double peak) =>
        peak.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a ReplayGain tag value: <c>"-6.54 dB"</c>, <c>"-6.54dB"</c>, or a bare number.
    ///
    /// <para>The parser is loose on purpose. Many different tools write this tag, and in practice the
    /// unit is optional. A reader that required the exact spelling would reject good values and
    /// decode the file again for no reason.</para>
    /// </summary>
    public static bool TryParseGain(string? value, out double gainDb)
    {
        gainDb = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].TrimEnd();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gainDb)
            && double.IsFinite(gainDb);
    }

    /// <summary>Reads a ReplayGain peak tag: a linear amplitude. Negative and non-numeric values are
    /// rejected, not corrected: a peak that cannot be parsed is a peak that is not known.</summary>
    public static bool TryParsePeak(string? value, out double peak)
    {
        peak = 0;
        return !string.IsNullOrWhiteSpace(value)
            && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out peak)
            && double.IsFinite(peak)
            && peak > 0;
    }

    /// <summary>
    /// The loudness a ReplayGain tag implies, given the reference the tag was written against.
    ///
    /// <para>This lets a stored gain replace a measurement that this app never made. The tag says how
    /// far the track is from the writer's reference, which is the same information as its loudness.
    /// Storing it as loudness means playback receives one kind of number, whether it was read from a
    /// tag or measured.</para>
    /// </summary>
    public static double LufsFromGain(double gainDb, double referenceLufs = ReferenceLufs) =>
        referenceLufs - gainDb;
}
