namespace RPM.Core.Analysis;

/// <summary>
/// Which parts of a record the CLAP model hears: three ten-second clips, centred at 20%, 50% and 80% of
/// its length.
///
/// <para>Three clips and not the whole record, because the model reads ten seconds at a time and a
/// whole record would cost twenty or more readings for little gain. Three points spread over the record
/// catch an intro, a middle and a late chorus. The feeling lists were tested with exactly these clips,
/// so a change here changes which songs land in which list.</para>
///
/// <para>Shared by the desktop and the cloud, so that a record measured on either one is heard at the
/// same points.</para>
/// </summary>
public static class SoundClips
{
    /// <summary>How long each clip is.</summary>
    public const double Seconds = ClapFeatures.ClipSamples / (double)ClapFeatures.SampleRate;

    /// <summary>Where each clip is centred, as a share of the record's length.</summary>
    public static IReadOnlyList<double> Centres { get; } = [0.2, 0.5, 0.8];

    /// <summary>The shortest clip that is used. A clip near the end of a very short record can come back
    /// shorter than asked. Below two seconds it is mostly the repetition that pads it, and is
    /// dropped.</summary>
    public const int LeastSamples = 2 * ClapFeatures.SampleRate;

    /// <summary>Where each clip starts, in seconds, for a record <paramref name="durationSeconds"/> long.
    /// A clip that would start before the record does starts at zero.</summary>
    public static IReadOnlyList<double> Starts(double durationSeconds)
    {
        double length = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
        return [.. Centres.Select(centre => Math.Max(0, length * centre - Seconds / 2))];
    }
}
