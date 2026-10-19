namespace RPM.Fingerprint;

/// <summary>
/// The rates a track is served at, and which accounts may have which.
///
/// <para>
/// This is here rather than beside the encoder, because two parts need to agree on it: the desktop,
/// which makes the renditions with ffmpeg, and the Lambda, which decides what a given account may be
/// given. The Lambda must not depend on the whole of <c>RPM.Core</c>, because a cold start delays the
/// listener. It already references this assembly for <see cref="ToolPaths"/>.
/// </para>
///
/// <para>
/// The ladder matches iBroadcast's, because what is worth paying for is lossless audio, not a wider
/// choice of rates.
/// </para>
/// </summary>
public static class AudioLadder
{
    /// <summary>Smallest first.</summary>
    public static readonly int[] Rates = [96, 128, 192, 256, 320];

    /// <summary>What a free account is served, whatever it asks for.</summary>
    public const int FreeKilobits = 128;

    /// <summary>The untranscoded file. What a plan sells.</summary>
    public const int Original = 0;

    /// <summary>How much of a track a head clip holds. Three seconds covers the first moment of playback
    /// and the two network round trips behind it. At <see cref="HeadKilobits"/> that is about 36 KB. The
    /// head clips for a twenty-track album cost less than a tenth of the data in the start of one FLAC
    /// track.</summary>
    public const double HeadSeconds = 3;

    /// <summary>The head clip's rate. It is deliberately the bottom of the ladder. It plays for three
    /// seconds while the real stream opens behind it, and the join is inaudible because what follows
    /// is the same recording.</summary>
    public const int HeadKilobits = 96;

    /// <summary>Whether a rate is one that gets made. Anything else is a caller inventing numbers.</summary>
    public static bool IsOnTheLadder(int kilobits) => Array.IndexOf(Rates, kilobits) >= 0;

    /// <summary>
    /// The rate actually served for one that was asked for: the nearest rung at or below it, never
    /// above what the account is allowed.
    /// </summary>
    /// <param name="mayHaveOriginal">Whether this account is entitled to the untranscoded file.</param>
    public static int Serve(int asked, bool mayHaveOriginal)
    {
        if (asked == Original) return mayHaveOriginal ? Original : FreeKilobits;

        var capped = mayHaveOriginal ? asked : Math.Min(asked, FreeKilobits);

        // Down, never up. Rounding up would spend data nobody agreed to and, for a free account, hand
        // over a rendition they are not entitled to.
        var served = Rates[0];
        foreach (var rate in Rates)
            if (rate <= capped) served = rate;

        return served;
    }

    /// <summary>
    /// The rates one account may choose between, and whether the choice is theirs to make.
    ///
    /// <para>
    /// The cap is not a display detail. <see cref="Serve"/> already refuses to hand a free account
    /// anything above <see cref="FreeKilobits"/>, so a screen that offers the whole ladder to one is
    /// offering a setting that does nothing: the box reads 320, the stream is 128, and the only place
    /// the two are ever reconciled is a log line. This is that same rule said where a chooser can
    /// read it, so what is offered and what arrives cannot disagree.
    /// </para>
    ///
    /// <para>
    /// There is no third standing for "not read yet". An account whose plan has not come back is
    /// offered <see cref="Unread"/>, which is the whole ladder: the setting is theirs either way and
    /// it is only the serving that is capped, and telling somebody they are on the free plan because
    /// a request timed out is the worse of the two mistakes.
    /// </para>
    /// </summary>
    public sealed record RateOffer
    {
        private readonly int[] _rungs;

        private RateOffer(int[] rungs, bool capped)
        {
            _rungs = rungs;
            Capped = capped;
        }

        /// <summary>Every rung, the original first: it is what somebody who has thought about it
        /// wants, and the rest descend from there.</summary>
        public static RateOffer Whole { get; } = new([Original, 320, 256, 192, 128, 96], capped: false);

        /// <summary>The one rung a free account is served, and nothing beside it to pick from.
        /// Listing the others greyed would be a longer way of saying the same thing.</summary>
        public static RateOffer Free { get; } = new([FreeKilobits], capped: true);

        /// <summary>What is offered before the account's standing is known, and when reading it
        /// failed. See the note on the type: the whole ladder, deliberately.</summary>
        public static RateOffer Unread => Whole;

        /// <summary>Which of the two this standing gets.</summary>
        public static RateOffer For(bool isSupporter) => isSupporter ? Whole : Free;

        /// <summary>The rates on offer, in the order a chooser lists them.</summary>
        public IReadOnlyList<int> Rungs => _rungs;

        /// <summary>Whether the choice has been taken away, which is what a screen greys out.</summary>
        public bool Capped { get; }

        /// <summary>Where a remembered rate sits in this offer. A rate that is not on it, such as a
        /// supporter's 320 read back after their support has lapsed, is the first rung. For <see cref="Free"/>
        /// that is the only rung there is.</summary>
        public int IndexOf(int kilobits) => Math.Max(0, Array.IndexOf(_rungs, kilobits));

        /// <summary>What is actually served for a rate chosen under this offer.</summary>
        public int Serves(int asked) => Serve(asked, mayHaveOriginal: !Capped);
    }
}
